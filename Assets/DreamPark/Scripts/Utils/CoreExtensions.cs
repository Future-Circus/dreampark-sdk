using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
#if DREAMPARKCORE
using DreamPark.ParkBuilder;
#endif

public static class CoreExtensions
{
    private static Task _addressablesInitTask;
    private static bool _skipDefaultInit = false; // Skip InitializeAsync after beta toggle

    /// <summary>
    /// Describes an Addressables asset load failure. Kept dependency-free so the
    /// SDK stays standalone — project code (e.g. ContentErrorReporter) subscribes
    /// to <see cref="AssetLoadFailed"/> and decides how to surface it (UI, native bridge, etc.).
    /// </summary>
    public struct AssetLoadFailure
    {
        public string assetKey;        // e.g. "SuperAdventureLand/Levels/Custom/A_Example"
        public string message;         // human-readable reason
        public bool isCorruption;      // true for corrupt/truncated AssetBundle faults
    }

    /// <summary>
    /// Raised when an Addressables load fails. Subscribers run on Unity's main thread.
    /// Handlers MUST NOT throw — failures here are already error paths.
    /// </summary>
    public static event Action<AssetLoadFailure> AssetLoadFailed;

    /// <summary>
    /// Heuristic: does this message look like a corrupt/incompatible/truncated AssetBundle?
    /// These are the native deserialization faults ("archive is corrupted", "Position out
    /// of bounds") that can hard-crash on device if the same bad cached bundle is reloaded.
    /// </summary>
    private static bool LooksLikeCorruption(string message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        string m = message.ToLowerInvariant();
        return m.Contains("corrupt")
            || m.Contains("position out of bounds")
            || m.Contains("is not a valid asset bundle")
            || m.Contains("failed to decompress")
            || m.Contains("failed to read")
            || m.Contains("data integrity");
    }

    private static void RaiseAssetLoadFailed(string assetKey, string message)
    {
        var handler = AssetLoadFailed;
        if (handler == null) return;
        try
        {
            handler.Invoke(new AssetLoadFailure
            {
                assetKey = assetKey,
                message = message,
                isCorruption = LooksLikeCorruption(message)
            });
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[CoreExtensions] AssetLoadFailed handler threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears the cached Addressables initialization task.
    /// MUST be called when Addressables is reinitialized (e.g., during beta mode toggle)
    /// to ensure GetAsset uses the fresh Addressables state.
    /// </summary>
    public static void ResetAddressablesInit()
    {
        _addressablesInitTask = null;
        _skipDefaultInit = true; // Don't call InitializeAsync - it loads default catalog that conflicts
        Debug.Log("[CoreExtensions] Addressables init cache cleared, skipping future default init");
    }

    private static async Task EnsureAddressablesInitialized()
    {
        // After a beta toggle, skip InitializeAsync since DownloadContents loads remote catalogs directly
        // Calling InitializeAsync would load the default/built-in catalog with LOCAL paths that conflict
        if (_skipDefaultInit)
        {
            return;
        }

        // already started or done? just await the same one
        if (_addressablesInitTask != null)
        {
            await _addressablesInitTask;
            return;
        }

        // create and store the shared initialization task
        _addressablesInitTask = Addressables.InitializeAsync().Task;
        await _addressablesInitTask;
    }
    public static void GetAsset<T>(this string resourceName, Action<T> onSuccess = null, Action<string> onError = null) where T : UnityEngine.Object
    {
        GetAssetAsync(resourceName, onSuccess, onError).Forget();
    }

    public static async UniTaskVoid GetAssetAsync<T>(string resourceName, Action<T> onSuccess = null, Action<string> onError = null) where T : UnityEngine.Object
    {
        T obj = await resourceName.GetAsset<T>();
        if (obj != null)
        {
            onSuccess?.Invoke(obj);
        }
        else
        {
            onError?.Invoke(resourceName);
        }
    }

    public static async Task<T> GetAsset<T>(this string resourceName) where T : UnityEngine.Object
    {
        await EnsureAddressablesInitialized();

        // Try common Resources paths
        T asset = Resources.Load<T>(resourceName)
                ?? Resources.Load<T>($"Prefabs/{resourceName}")
                ?? Resources.Load<T>($"Levels/{resourceName}")
                ?? Resources.Load<T>($"Audio/{resourceName}")
                ?? Resources.Load<T>($"Music/{resourceName}");

        if (asset != null)
            return asset;

        // Try Addressables - use explicit location from the LAST (newest) locator to avoid
        // conflicts between release/beta bundles with same internal asset paths
        Debug.Log($"[GetAsset] Loading '{resourceName}' of type {typeof(T).Name} via Addressables");
        Debug.Log($"[GetAsset] Total locators: {Addressables.ResourceLocators.Count()}");

        try
        {
            // Find the location from the LAST locator that has this key
            // (newest/most recently loaded catalog should take precedence)
            // NOTE: Use null for type to avoid type mismatch issues - LoadAssetAsync will handle type
            IResourceLocation explicitLocation = null;
            foreach (var locator in Addressables.ResourceLocators.Reverse())
            {
                // Try with specific type first
                if (locator.Locate(resourceName, typeof(T), out var locations) && locations != null && locations.Count > 0)
                {
                    explicitLocation = locations[0];
                    Debug.Log($"[GetAsset] Found '{resourceName}' in locator '{locator.LocatorId}' (typed)");
                    break;
                }
                // Fallback: try without type constraint (null matches any type)
                if (locator.Locate(resourceName, null, out var anyLocations) && anyLocations != null && anyLocations.Count > 0)
                {
                    explicitLocation = anyLocations[0];
                    Debug.Log($"[GetAsset] Found '{resourceName}' in locator '{locator.LocatorId}' (untyped)");
                    break;
                }
            }

            AsyncOperationHandle<T> handle;
            if (explicitLocation != null)
            {
                // Load using explicit location to avoid wrong bundle resolution
                handle = Addressables.LoadAssetAsync<T>(explicitLocation);
            }
            else
            {
                // Fallback to key-based loading
                handle = Addressables.LoadAssetAsync<T>(resourceName);
            }

            await handle.Task;

            if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                #if DREAMPARKCORE
                // Package manifests/definitions/manager prefabs are loaded by
                // explicit content-prefixed keys. Attribute their handles to
                // that pack so a per-content unload releases them too.
                ContentManager.TrackLoadedAssetHandle(
                    ContentManager.ContentIdFromAddress(resourceName), handle);
                #endif
                Debug.Log($"[GetAsset] Successfully loaded '{resourceName}'");
                return handle.Result;
            }
            else
            {
                string failMessage = handle.OperationException?.Message
                    ?? $"LoadAssetAsync returned status {handle.Status}";
                Debug.LogWarning($"[GetAsset] LoadAssetAsync status: {handle.Status} for '{resourceName}'");
                if (handle.OperationException != null)
                {
                    Debug.LogWarning($"[GetAsset] Exception: {handle.OperationException.Message}");
                }
                Addressables.Release(handle);
                RaiseAssetLoadFailed(resourceName, failMessage);
            }
        }
        catch (Exception e)
        {
            // Contain the failure here so a bad/corrupt bundle never propagates as an
            // unhandled exception up the async chain (which is what kills the boot/load).
            Debug.LogWarning($"[GetAsset] Exception loading '{resourceName}': {e.Message}");
            RaiseAssetLoadFailed(resourceName, e.Message);
        }

        // Debug: Check if key exists in any locator (for debugging purposes)
        foreach (var locator in Addressables.ResourceLocators)
        {
            bool canLocate = locator.Locate(resourceName, typeof(T), out var locations);
            if (canLocate && locations != null && locations.Count > 0)
            {
                Debug.LogWarning($"[GetAsset] Key '{resourceName}' found in locator '{locator.LocatorId}' but load failed");
            }
            // Also check if key exists without type constraint
            foreach (var key in locator.Keys)
            {
                if (key is string keyStr && keyStr == resourceName)
                {
                    Debug.LogWarning($"[GetAsset] Key '{resourceName}' EXISTS in '{locator.LocatorId}' (type mismatch possible)");
                    break;
                }
            }
        }

        Debug.LogWarning($"❌ Asset '{resourceName}' of type {typeof(T).Name} not found in Resources or Addressables.");
        return null;
    }

    public static async UniTask<GameObject> InstantiateAssetAsync(GameObject prefab, Vector3 position = default, Quaternion rotation = default, Transform parent = null, Action<GameObject> onSuccess = null, Action<string> onError = null)
    {
       GameObject instance = GameObject.Instantiate(prefab, position, rotation, parent);
       #if DREAMPARKCORE
        if (LevelObjectManager.Instance != null) {
            LevelObjectManager.Instance.RegisterLevelObject(instance);
        }
        #endif
        if (onSuccess != null) {
            onSuccess(instance);
        }
        return instance;
    }

    public static void PrioritizeAsset(this GameObject gameObject) {    
        #if DREAMPARKCORE
        if (LevelObjectManager.Instance != null) {
            LevelObjectManager.Instance.PrioritizeLevelObject(gameObject);
        }
        #else
        Debug.Log("PrioritizeLevelObject makes our Automated Optimization ignore this asset keeping it alive forever");
        #endif
    }

    // ── PlaySFX voices (Nov 11 2026, pt-perf; Arcadeland playtest M43) ──────
    // Every call used to make a "TempAudio" GameObject at the scene ROOT + an AudioSource + a RealisticRolloff (whose Awake
    // built a new AnimationCurve), destroyed after the clip: ArcadeLand made 1817 of them in 15 min (~2/s), and each new
    // root is another pass of the core's runtime-object scan. Now the voice GameObjects are pooled under one holder and the
    // rolloff curve is shared per distance range (RealisticRolloff.Apply). The holder has hideFlags (no title's runtime
    // object: a title retire never takes the pool with it) and lives in the active scene (a scene load ends its sounds, as
    // it ended the TempAudio roots). The AudioSource itself is still one per sound and is destroyed when the clip ends,
    // exactly when the TempAudio was: a caller that kept the returned source (EasyAudio's Stop + Destroy of its gameObject,
    // Lua) finds it null afterwards, never a later sound's source, and no clip stays referenced. Audibly identical:
    // position, volume, pitch, spatial blend 1, maxDistance from volume, the same custom rolloff / spread / doppler, no
    // mixer group, same lifetime. Looping sounds on a parent stay caller-owned GameObjects as before.
    // dreampark-sdk carries the same file: keep the two copies identical.
    const int MaxPlaySfxVoices = 64;
    static readonly List<GameObject> s_sfxVoices = new List<GameObject>();
    static Transform s_sfxHolder;

    /// Diagnostics / play checks: pooled voice GameObjects (busy or free).
    public static int PlaySfxVoiceCount => s_sfxVoices.Count;

    /// A free pooled voice (its last sound's AudioSource is gone), else a new one; pooled = it stays for the next sound.
    static GameObject TakeSfxVoice(out bool pooled)
    {
        if (s_sfxHolder == null)
        {
            s_sfxVoices.Clear();
            var holder = new GameObject("PlaySFX voices") { hideFlags = HideFlags.NotEditable };
            s_sfxHolder = holder.transform;
        }
        for (int i = s_sfxVoices.Count - 1; i >= 0; i--)
        {
            GameObject v = s_sfxVoices[i];
            if (v == null) { s_sfxVoices.RemoveAt(i); continue; }   // a caller destroyed it (EasyAudio): forget it
            if (v.TryGetComponent(out AudioSource _)) continue;   // still playing its sound
            if (v.transform.parent != s_sfxHolder) v.transform.SetParent(s_sfxHolder, false);
            if (!v.activeSelf) v.SetActive(true);
            pooled = true;
            return v;
        }
        var voice = new GameObject("TempAudio");
        voice.transform.SetParent(s_sfxHolder, false);
        pooled = s_sfxVoices.Count < MaxPlaySfxVoices;
        if (pooled) s_sfxVoices.Add(voice);
        return voice;
    }

    public static AudioSource PlaySFX(this AudioClip clip, Vector3 position, float volume = 1f, float pitch = 1f, Transform parent = null)
    {
        GameObject voice;
        bool pooled = false;
        if (parent != null) voice = new GameObject("TempAudio");   // caller-owned loop (EasyAudio / EasyVoice destroy it)
        else
        {
            _ = clip.length;   // a null clip throws here, as it always did (now before anything is made)
            voice = TakeSfxVoice(out pooled);
        }
        voice.transform.SetPositionAndRotation(position, Quaternion.identity);
        AudioSource audioSource = voice.AddComponent<AudioSource>();
        audioSource.clip = clip;
        audioSource.volume = Mathf.Clamp01(volume);
        audioSource.spatialBlend = 1;
        audioSource.maxDistance = 10 + ((volume-1f) * 10f);
        audioSource.pitch = pitch;
        RealisticRolloff.Apply(audioSource);
        audioSource.Play();

        if (parent != null)
        {
            voice.transform.SetParent(parent,true);
            voice.transform.localPosition = Vector3.zero;
            voice.transform.localRotation = Quaternion.identity;
            audioSource.loop = true;
        }
        else if (pooled)
        {
            UnityEngine.Object.Destroy(audioSource, clip.length);   // the voice stays for the next sound
        }
        else
        {
            UnityEngine.Object.Destroy(voice, clip.length);   // over the pool cap: gone with its sound, as before
        }

        return audioSource;
    }

    public static void PlaySFX(this string clipName, Vector3 position, float volume = 1f, float pitch = 1f, Transform parent = null)
    {
         GetAssetAsync<AudioClip>(clipName, (clip) => {
            if (clip)
            {
            clip.PlaySFX(position, volume, pitch, parent);
            }
         }, null).Forget();
    }

    public static async UniTask<GameObject> InstantiateAssetAsync(this string resourceName, Vector3 position = default, Quaternion rotation = default, Transform parent = null, Action<GameObject> onSuccess = null, Action<string> onError = null)
    {
        GameObject prefab = await resourceName.GetAsset<GameObject>();
        if (prefab != null) {
            return await InstantiateAssetAsync(prefab, position, rotation, parent, onSuccess, onError);
        } else if (onError != null) {
            onError(resourceName);
        }
        return null;
    }
    public static void SpawnAsset(this string resourceName, Action<GameObject> onSuccess, Action<string> onError)
    {
        InstantiateAssetAsync(resourceName, onSuccess: onSuccess, onError: onError).Forget();
    }

    public static void SpawnAsset(this string resourceName, Vector3 position, Quaternion rotation, Transform parent = null, Action<GameObject> onSuccess = null, Action<string> onError = null)
    {
        InstantiateAssetAsync(resourceName, position, rotation, parent, onSuccess, onError).Forget();
    }

    public static void SpawnAsset(this GameObject prefab, Action<GameObject> onSuccess, Action<string> onError)
    {
        InstantiateAssetAsync(prefab, onSuccess: onSuccess, onError: onError).Forget();
    }

    public static void SpawnAsset(this GameObject prefab, Vector3 position, Quaternion rotation, Transform parent = null, Action<GameObject> onSuccess = null, Action<string> onError = null)
    {
        InstantiateAssetAsync(prefab, position, rotation, parent, onSuccess, onError).Forget();
    }
}
