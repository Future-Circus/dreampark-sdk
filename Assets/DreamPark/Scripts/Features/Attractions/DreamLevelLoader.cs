// ─────────────────────────────────────────────────────────────────────
//  DreamLevelLoader.cs — per-level streaming for the Dream Sequence format.
//
//  A Dream Sequence plays a SEQUENCE of small levels back to back in one
//  physical space. Today every level ships inside ONE prefab as pre-instantiated
//  children and the controller just toggles SetActive
//  (Assets/Content/Sample/Scripts/dreamsequence-controller.lua.txt:211-224,
//  :315-336, :338-368). That means the whole Dream is one bundle, and the
//  guest downloads every level before playing the first.
//
//  This loads each level from its OWN addressable bundle, by ADDRESS STRING,
//  on demand. Spec: DREAMTEMPLATE_RUNTIME_SPEC.md rev 4 §1.
//
//  ── WHY AN ADDRESS STRING AND NOT AssetReferenceGameObject ───────────
//
//  Both keep levels out of the DreamTemplate's build graph — that property
//  comes from holding NO serialized UnityEngine.Object reference, not from
//  AssetReference specifically (SmartBundleGrouper.cs:1036 walks
//  AssetDatabase.GetDependencies, which only sees object references).
//
//  The string additionally routes through CoreExtensions.GetAsset<T>, which
//  carries hardening a raw AssetReference load does not:
//    - corrupt-bundle classification (CoreExtensions.cs:41-51) — the signal
//      that a cached bundle is poison and must not simply be retried;
//    - newest-locator preference (:147-163) for the beta/release catalog case;
//    - failure containment (:200-206), so a bad bundle returns null instead of
//      throwing into the caller's async chain and killing the load;
//    - the AssetLoadFailed event (:34) that project code surfaces to the user.
//  AssetReferenceGameObject also has ZERO usages anywhere in Assets/ — it would
//  be a new pattern with no precedent here. Hence: string.
//
//  ── WHAT THIS DELIBERATELY DOES NOT DO: RELEASE ──────────────────────
//
//  There is no unload/release call in this file, ON PURPOSE. See §6.4 below
//  and the header of ReleaseOwnership at the bottom of this file. Short
//  version: whether anything needs releasing, and who owns doing it, is not
//  determinable from this repository, and inventing an SDK-side release
//  against an assumed gap in dreampark-core risks double-releasing a handle
//  core already owns.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using DreamPark.ParkBuilder;

namespace DreamPark
{
    /// <summary>
    /// Loads and instantiates Dream Sequence levels one at a time, by addressable
    /// address string. Decoupled from any particular component on purpose: it takes
    /// an ordered list of addresses and a parent, so it can be driven by the
    /// DreamTemplate component, by the Lua sequence controller, or by the published
    /// level list in content data (which per the publish payload spec is authoritative
    /// alongside the prefab, so the runtime need not be the only source of ordering).
    /// </summary>
    public class DreamLevelLoader : MonoBehaviour
    {
        // ── Authored input ───────────────────────────────────────────────
        [Tooltip("Ordered addressable ADDRESS STRINGS, one per level. Same key form " +
                 "ContentProcessor stamps into GameArea.resourceName — e.g. " +
                 "\"{gameId}/Levels/{size}/{filename}\".")]
        public List<string> levelAddresses = new List<string>();

        [Tooltip("Where level instances are parented. Falls back to this object's transform.")]
        public Transform levelParent;

        [Tooltip("Start fetching level N+1 as soon as level N begins playing, rather than " +
                 "waiting for the transition. See PreloadNext.")]
        public bool preloadNextOnBegin = true;

        /// <summary>
        /// Optional source override for simulator/integration tests. Production
        /// leaves this null and resolves each address through Addressables.
        /// </summary>
        [NonSerialized] public Func<int, UniTask<GameObject>> prefabProvider;

        /// <summary>
        /// Backward-compatible source override for integrations that resolve by
        /// address rather than sequence index. The index-based provider wins when
        /// both are assigned.
        /// </summary>
        [NonSerialized] public Func<string, UniTask<GameObject>> AssetResolver;

        // ── Runtime state ────────────────────────────────────────────────

        /// Loaded PREFAB ASSETS, keyed by address. Cached so re-entering a level in a
        /// looping sequence does not re-resolve through Addressables every cycle.
        /// These are assets, not instances — see ReleaseOwnership for why nothing here
        /// is ever released by this class.
        private readonly Dictionary<string, GameObject> _prefabs =
            new Dictionary<string, GameObject>(StringComparer.Ordinal);

        /// Live INSTANCES by level index. Destroying one of these frees the scene
        /// objects and nothing else — it does not decrement any bundle refcount.
        private readonly Dictionary<int, GameObject> _instances = new Dictionary<int, GameObject>();

        /// In-flight loads by address, so two callers asking for the same level
        /// (a preload racing an advance) await ONE resolve instead of issuing two.
        private readonly Dictionary<string, UniTask<GameObject>> _inFlight =
            new Dictionary<string, UniTask<GameObject>>(StringComparer.Ordinal);

        /// <summary>
        /// Inactive staging parent. Levels are instantiated in here so their Awake /
        /// OnEnable / Start do NOT run until this loader has configured them — see
        /// ConfigureBeforeActivation for why that ordering is load-bearing.
        /// </summary>
        private Transform _staging;

        /// <summary>Raised after a level instance is live and configured. (index, instance)</summary>
        public event Action<int, GameObject> LevelInstantiated;

        /// <summary>Raised when a level fails to load. (index, address)</summary>
        public event Action<int, string> LevelLoadFailed;

        /// <summary>
        /// The ordered occurrence currently playing. A negative value means the
        /// configured sequence has not started or has completed.
        /// </summary>
        public int CurrentSequenceIndex { get; private set; } = -1;

        public event Action SequenceCompleted;

        public int LevelCount => levelAddresses != null ? levelAddresses.Count : 0;

        public bool IsValidIndex(int index) => index >= 0 && index < LevelCount;

        /// <summary>
        /// Replace the ordered sequence. Duplicate addresses are deliberately kept:
        /// each list index is a distinct occurrence in the package.
        /// </summary>
        public void ConfigureSequence(IEnumerable<string> orderedResourceNames)
        {
            foreach (int index in new List<int>(_instances.Keys)) DespawnLevel(index);
            levelAddresses = orderedResourceNames != null
                ? new List<string>(orderedResourceNames)
                : new List<string>();
            CurrentSequenceIndex = -1;
        }

        /// <summary>Spawn and begin the first configured sequence occurrence.</summary>
        public async UniTask<bool> StartSequenceAsync()
        {
            if (LevelCount == 0) return false;

            GameObject first = await SpawnLevelAsync(0);
            if (first == null) return false;

            CurrentSequenceIndex = 0;
            if (preloadNextOnBegin) PreloadNext(0);
            return true;
        }

        /// <summary>
        /// Advance one ordered occurrence. The current level remains live if the
        /// next asset cannot be loaded. Advancing past the final occurrence completes
        /// the sequence and restores <see cref="CurrentSequenceIndex"/> to -1.
        /// </summary>
        public async UniTask<bool> AdvanceSequenceAsync()
        {
            if (CurrentSequenceIndex < 0) return false;

            int next = CurrentSequenceIndex + 1;
            if (next >= LevelCount)
            {
                DespawnLevel(CurrentSequenceIndex);
                CurrentSequenceIndex = -1;
                SequenceCompleted?.Invoke();
                return true;
            }

            // Resolve first so a failed download never removes the playable level.
            if (await LoadPrefabAsync(next) == null) return false;

            int previous = CurrentSequenceIndex;
            DespawnLevel(previous);
            GameObject instance = await SpawnLevelAsync(next);
            if (instance == null)
            {
                await SpawnLevelAsync(previous);
                return false;
            }

            CurrentSequenceIndex = next;
            if (preloadNextOnBegin) PreloadNext(next);
            return true;
        }

        public GameObject GetInstance(int index)
        {
            return _instances.TryGetValue(index, out var go) ? go : null;
        }

        /// <summary>True when this level's PREFAB is already resident, so activating it costs an Instantiate and nothing else.</summary>
        public bool IsPrefabResident(int index)
        {
            return IsValidIndex(index) && _prefabs.ContainsKey(levelAddresses[index]);
        }

        private Transform ResolveLevelParent()
        {
            return levelParent != null ? levelParent : transform;
        }

        private Transform Staging
        {
            get
            {
                if (_staging == null)
                {
                    var go = new GameObject("~DreamLevelStaging");
                    go.transform.SetParent(transform, false);
                    // Inactive for its whole life. Anything instantiated inside it is
                    // inactive-in-hierarchy and therefore not yet running.
                    go.SetActive(false);
                    _staging = go.transform;
                }
                return _staging;
            }
        }

        // ── Loading ──────────────────────────────────────────────────────

        /// <summary>
        /// Fetch a level's prefab without instantiating it. Safe to call repeatedly and
        /// safe to fire-and-forget: concurrent calls for the same address share one
        /// resolve. This is the preload entry point.
        /// </summary>
        public async UniTask<GameObject> LoadPrefabAsync(int index)
        {
            if (!IsValidIndex(index)) return null;

            string address = levelAddresses[index];
            if (string.IsNullOrEmpty(address))
            {
                Debug.LogWarning($"[DreamLevelLoader] Level {index} has an empty address; nothing to load.");
                LevelLoadFailed?.Invoke(index, address);
                return null;
            }

            if (_prefabs.TryGetValue(address, out var cached) && cached != null) return cached;
            if (_inFlight.TryGetValue(address, out var pending)) return await pending;

            // UniTask's ordinary async source is single-consumer. Preloading and
            // advancing can await this same address simultaneously, so preserve
            // the in-flight result for multiple awaiters.
            var task = (prefabProvider != null
                ? prefabProvider(index)
                : ResolveAsync(index, address)).Preserve();
            _inFlight[address] = task;
            try
            {
                GameObject loaded = await task;
                if (loaded != null) _prefabs[address] = loaded;
                return loaded;
            }
            finally
            {
                _inFlight.Remove(address);
            }
        }

        private async UniTask<GameObject> ResolveAsync(int index, string address)
        {
            // GetAsset<T> returns the ASSET, not the handle — by design, and it is why
            // nothing here can (or tries to) release anything. It contains its own
            // failures: a corrupt or missing bundle comes back as null rather than an
            // exception, having already raised CoreExtensions.AssetLoadFailed.
            GameObject prefab = AssetResolver != null
                ? await AssetResolver(address)
                : await address.GetAsset<GameObject>();

            if (prefab == null)
            {
                Debug.LogWarning($"[DreamLevelLoader] Level {index} failed to load from address '{address}'. " +
                                 "GetAsset has already classified and reported the failure; the sequence " +
                                 "cannot advance into this level.");
                LevelLoadFailed?.Invoke(index, address);
                return null;
            }

            _prefabs[address] = prefab;
            return prefab;
        }

        /// <summary>
        /// Start fetching the NEXT level while the current one plays. Fire-and-forget.
        ///
        /// Called at level START rather than at transition start, deliberately: the
        /// Lua controller computes the next index inside begin_transition
        /// (dreamsequence-controller.lua.txt:395) and then waits only
        /// get_transition_delay() — 2.5s by default (:132-135) — before swapping.
        /// Preloading from the moment level N begins gives the fetch the whole
        /// PLAY DURATION of level N instead of just that 2.5s window, which is what
        /// makes a swap cost an Instantiate rather than a download.
        /// </summary>
        public void PreloadNext(int currentIndex, bool loop = false)
        {
            int next = currentIndex + 1;
            if (next >= LevelCount) next = loop ? 0 : -1;
            if (next < 0 || next == currentIndex) return;
            if (IsPrefabResident(next)) return;

            LoadPrefabAsync(next).Forget();
        }

        // ── Instantiation ────────────────────────────────────────────────

        /// <summary>
        /// Make a level live: load its prefab if needed, instantiate it, configure it
        /// while it is still inert, then activate it. Returns the live instance, or null
        /// if the level could not be loaded.
        /// </summary>
        public async UniTask<GameObject> SpawnLevelAsync(int index, bool activate = true)
        {
            var existing = GetInstance(index);
            if (existing != null)
            {
                if (activate && !existing.activeSelf) existing.SetActive(true);
                return existing;
            }

            GameObject prefab = await LoadPrefabAsync(index);
            if (prefab == null) return null;

            // Two callers can reach this point after the same in-flight fetch.
            // Unity resumes them serially; the first creates the instance and the
            // second must reuse it instead of cloning a duplicate level.
            existing = GetInstance(index);
            if (existing != null)
            {
                if (activate && !existing.activeSelf) existing.SetActive(true);
                return existing;
            }

            // ── THE ORDERING THAT MAKES THE FLOOR HANDOFF WORK ───────────
            //
            // Instantiated into the INACTIVE staging parent, so the level's own
            // Awake/OnEnable/Start have not run yet. LevelTemplate.Start() reads
            // generateFloor (LevelTemplate.cs:194) and builds a floor immediately if
            // it is true — so the flag has to be written BEFORE the level's lifecycle
            // begins, not after. Instantiating live and then setting the field is a
            // race against Unity's Start ordering; staging is not a race at all.
            GameObject instance = Instantiate(prefab, Staging);
            instance.name = prefab.name;

            ConfigureBeforeActivation(instance);

            // Keep the instance inert while moving it out of staging. The package
            // host can then make the old level inactive before the new one boots.
            instance.SetActive(false);

            // Reparenting out of inactive staging into the live parent is what runs
            // the level's Awake/OnEnable — with its configuration already applied.
            instance.transform.SetParent(ResolveLevelParent(), false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;

            if (activate) instance.SetActive(true);

            _instances[index] = instance;

#if DREAMPARKCORE
            // Streamed children do not pass through CoreExtensions' spawn path.
            // Registration must happen here for build/play parking and culling.
            LevelObjectManager.Instance?.RegisterLevelObject(instance);
#endif

            // NOTE for whoever wires this to the park loader: a streamed-in level is
            // NOT registered with LevelObjectManager by this call.
            // CoreExtensions.InstantiateAssetAsync does that registration, but only
            // under #if DREAMPARKCORE (CoreExtensions.cs:234-237), and this SDK repo
            // does not define that symbol. Registration is core-side work; see
            // DREAMTEMPLATE_RUNTIME_SPEC.md §3.2. An unregistered level is never
            // parked or culled by OptimizedAF and does not observe the park-content
            // physics lock.

            LevelInstantiated?.Invoke(index, instance);
            return instance;
        }

        /// <summary>
        /// Everything that must be true of a level instance BEFORE its first frame.
        /// Runs while the instance is parented under inactive staging.
        /// </summary>
        private void ConfigureBeforeActivation(GameObject instance)
        {
            // The attraction owns its cutouts and NavMesh. Enable its floor
            // before first activation so LevelTemplate.Start builds the packed
            // geometry. The package root retains a separate, non-colliding
            // calibration reference; never share its mesh with streamed levels.
            LevelTemplate template = instance.GetComponent<LevelTemplate>();
            if (template != null) template.generateFloor = true;
        }

        /// <summary>
        /// Destroy a level's scene instance.
        ///
        /// THIS FREES SCENE OBJECTS ONLY. It does not decrement any AssetBundle
        /// refcount, because this class never held a handle to decrement — see
        /// ReleaseOwnership. The loaded prefab asset stays in <see cref="_prefabs"/>
        /// so a looping sequence can re-enter the level without re-resolving.
        /// </summary>
        public void DespawnLevel(int index)
        {
            if (!_instances.TryGetValue(index, out var go)) return;
            _instances.Remove(index);
            if (go == null) return;

#if DREAMPARKCORE
            UnregisterInstance(go);
#endif

            if (Application.isPlaying) Destroy(go);
            else DestroyImmediate(go);
        }

        /// <summary>
        /// Regenerate the floor of the template that actually owns it, guarded.
        /// </summary>
        public void RegenerateOwningFloor(LevelTemplate owner)
        {
            if (owner == null) return;
            if (!owner.generateFloor) return;
            owner.RegenerateFloor();
        }

        private void OnDestroy()
        {
            // Instances are children and Unity destroys them with us. The prefab ASSETS
            // in _prefabs are deliberately not touched — see ReleaseOwnership.
#if DREAMPARKCORE
            foreach (GameObject instance in _instances.Values)
                UnregisterInstance(instance);
#endif
            _instances.Clear();
            _prefabs.Clear();
            _inFlight.Clear();
        }

#if DREAMPARKCORE
        private static void UnregisterInstance(GameObject instance)
        {
            LevelObjectManager manager = LevelObjectManager.Instance;
            if (manager == null || instance == null) return;
            // RegisterLevelObject recurses through a LevelTemplate's descendants;
            // unregistering only its root would leave stale culling entries.
            foreach (Transform child in instance.GetComponentsInChildren<Transform>(true))
                manager.UnregisterLevelObject(child.gameObject);
        }
#endif

        // ─────────────────────────────────────────────────────────────────
        //  ReleaseOwnership — why there is no unload path in this file, and
        //  exactly what has to be answered before one is written.
        //
        //  WHAT IS KNOWN, from this repository:
        //
        //   - CoreExtensions.GetAsset<T> returns handle.Result — the ASSET
        //     (CoreExtensions.cs:185). The AsyncOperationHandle is a local
        //     (:165) and is the only AsyncOperationHandle in all of Assets/.
        //     On success the handle is passed to
        //     ContentManager.TrackLoadedAssetHandle, but ONLY under
        //     #if DREAMPARKCORE (:181-182).
        //   - The only Addressables.Release in the tree is on the FAILURE
        //     branch (:196). There are zero Addressables.InstantiateAsync,
        //     zero ReleaseInstance, and zero Resources.UnloadUnusedAssets.
        //   - DREAMPARKCORE is NOT among this project's scripting defines
        //     (ProjectSettings/ProjectSettings.asset:790-802), so in THIS
        //     project that tracking call compiles out entirely.
        //   - In the Editor this project resolves addressables through
        //     BuildScriptFastMode ("Use Asset Database"), because the play-mode
        //     builder index is not serialized in AddressableAssetSettings.asset
        //     and so sits at the package default of 0. No bundles are built or
        //     mounted in Editor play mode, so locally there is nothing to leak
        //     and nothing for a release call to reclaim.
        //
        //  WHAT IS NOT KNOWN, and cannot be determined from this repository:
        //
        //   - In a build where DREAMPARKCORE *is* defined, the handle reaches
        //     ContentManager. Whether ContentManager OWNS it (and releases at
        //     park teardown or content unmount) or merely OBSERVES it is
        //     invisible from here — ContentManager lives in dreampark-core.
        //   - Whether park teardown unmounts bundles wholesale regardless of
        //     handle refcounts.
        //   - Whether core already exposes a per-content or per-level unload
        //     API that a DreamTemplate should be calling rather than duplicating.
        //
        //  WHY NOTHING IS BUILT HERE:
        //
        //  If core already owns these handles, an SDK-side release would either
        //  double-release or release out from under core's own bookkeeping.
        //  Building a release mechanism against an ASSUMED gap is how you turn a
        //  question into a bug. The correct move is to answer the three questions
        //  above — they are a short read for anyone with dreampark-core checked
        //  out — and only then decide whether the SDK needs a handle-returning
        //  load API at all. DREAMTEMPLATE_RUNTIME_SPEC.md §6.3 carries that
        //  contingency design, explicitly gated on those answers.
        //
        //  ONE RULE THAT APPLIES WHOEVER BUILDS IT — it is a CORRECTNESS rule,
        //  not a tuning choice, so it must not be "optimized" away later:
        //  a level's handle must not be released until its INSTANCE is destroyed.
        //  A live instance references its prefab's meshes and materials through
        //  the bundle; releasing while it lives can drop the refcount to zero and
        //  unload the bundle out from under the level the guest is standing in,
        //  which renders as pink or invisible geometry. Peak residency across a
        //  transition is therefore two levels, and that is the number a memory
        //  budget must be sized against.
        // ─────────────────────────────────────────────────────────────────
    }
}
