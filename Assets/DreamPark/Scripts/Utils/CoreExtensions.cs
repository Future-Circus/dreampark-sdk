using System;
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
                ContentManager.TrackLoadedAssetHandle(handle);
                #endif
                Debug.Log($"[GetAsset] Successfully loaded '{resourceName}'");
                return handle.Result;
            }
            else
            {
                Debug.LogWarning($"[GetAsset] LoadAssetAsync status: {handle.Status} for '{resourceName}'");
                if (handle.OperationException != null)
                {
                    Debug.LogWarning($"[GetAsset] Exception: {handle.OperationException.Message}");
                }
                Addressables.Release(handle);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[GetAsset] Exception loading '{resourceName}': {e.Message}");
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

    public static AudioSource PlaySFX(this AudioClip clip, Vector3 position, float volume = 1f, float pitch = 1f, Transform parent = null)
    {
        GameObject tempGO = new GameObject("TempAudio");
        tempGO.transform.position = position;
        AudioSource audioSource = tempGO.AddComponent<AudioSource>();
        audioSource.clip = clip;
        audioSource.volume = Mathf.Clamp01(volume);
        audioSource.spatialBlend = 1;
        audioSource.maxDistance = 10 + ((volume-1f) * 10f);
        audioSource.pitch = pitch;
        tempGO.AddComponent<RealisticRolloff>();
        audioSource.Play();

        if (parent != null)
        {
            tempGO.transform.SetParent(parent,true);
            tempGO.transform.localPosition = Vector3.zero;
            tempGO.transform.localRotation = Quaternion.identity;
            audioSource.loop = true;
        }
        else
        {
            UnityEngine.Object.Destroy(tempGO, clip.length);
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
