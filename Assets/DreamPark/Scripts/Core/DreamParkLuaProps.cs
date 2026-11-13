// ─────────────────────────────────────────────────────────────────────
//  DreamParkLuaProps.cs — load props at runtime from Lua (Nov 2026):
//
//    local h = dp.spawn_prop('P_FrostWindow', { parent = slot, key = 'slot:north' })
//    h.go / h.key / h.address / h.state() / h.loaded() / h.error() / h.despawn()
//    dp.preload_prop('P_FrostWindow')      -- the bundle closure only; h.ready() / h.error()
//    dp.despawn_prop(h or go)
//    dp.on_prop_loaded(fn(h)[, owner]) / dp.on_prop_failed(fn(h, err)[, owner]) (+ off_)
//
//  The prop gets what the park loader gives a prop authored inside the attraction:
//    • it is instantiated INERT (inactive staging), then stamped and posed, and only
//      then parented under the calling attraction, so Awake runs with its final
//      hierarchy: PropTemplate suppresses its own PropTemplate/GameArea under a
//      LevelTemplate, and onready/onzoneenter/onlayout resolve the attraction exactly
//      as for authored content;
//    • a NetScope "prop|{owner hash}|{key}" — the owner's NetId.HashPath (itself
//      NetScope-stable) plus the creator's key, identical on every headset, so every
//      NetId inside the prop computes the same id everywhere and on every respawn;
//    • LevelObjectManager registration (distance culling, build parking, the game
//      master pause) the way a nested prop is registered, and unregistration on despawn;
//    • optional floor seating (a FloorAnchor on the prop root, seat = true);
//    • despawn with its owner (the calling attraction, or the calling object outside
//      one): destroying the owner despawns every prop it spawned, wherever parented.
//
//  Names: a bare prefab name resolves within the CALLING object's title (its Beta twin
//  included) to that prop's address ({gameId}/Props/{category}/{name}), the same
//  leaf-match rule core's park loader uses; a full address is taken as is (a
//  "Content/…" stem and a Beta/release twin prefix also resolve). In the Editor, a name
//  Addressables cannot locate falls back to the prefab asset (the Park Simulator loads
//  prefabs straight from the project).
//
//  Bridge shape: flat delegates of primitives + GameObjects only (no Lua function ever
//  crosses into C#, so no new delegate bridge in Gen/), events delivered by calling one
//  Lua global (__dp_prop_event) like DispatchOccurrenceRemoved. Not [LuaCallCSharp]:
//  nothing here needs a wrapper, so no codegen drift.
//
//  Older apps have none of this: feature-test `if dp.spawn_prop then`.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Globalization;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using XLua;
using DreamPark.ParkBuilder;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace DreamPark
{
    public static class DreamParkLuaProps
    {
        // ── Errors (h.error()) ───────────────────────────────────────────
        public const string ErrNotFound = "not-found";           // no prop by that name/address in this title
        public const string ErrDownloadFailed = "download-failed"; // the closure or the prefab did not load
        public const string ErrNotAProp = "not-a-prop";          // it is an attraction / has no PropTemplate
        public const string ErrSpawnFailed = "spawn-failed";     // instantiate threw / no caller

        public const int Loading = 0, Loaded = 1, Failed = 2, Despawned = 3;

        // ── Test / tooling hooks (null in production) ────────────────────
        /// <summary>(request, gameId) → address or null. Replaces the resolver.</summary>
        public static Func<string, string, string> AddressResolver;
        /// <summary>address → prefab. Replaces Addressables/AssetDatabase loading.</summary>
        public static Func<string, UniTask<GameObject>> AssetLoader;
        /// <summary>address → closure downloaded. Replaces the dependency download.</summary>
        public static Func<string, UniTask<bool>> DependencyDownloader;

        internal sealed class Entry
        {
            public int id;
            public bool preload;
            public string request, key, address, title, error;
            public GameObject owner, parentGo, go;
            public bool parentGiven, hasPos, hasRot, world, seat;
            public Vector3 pos;
            public Quaternion rot = Quaternion.identity;
            public int state = Loading;
            public string mapKey;
        }

        static readonly Dictionary<int, Entry> s_byId = new Dictionary<int, Entry>();
        static readonly Dictionary<string, int> s_byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        static int s_next;
        static Transform s_staging;
#if UNITY_EDITOR
        static readonly Dictionary<string, string> s_editorPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Ids keep counting (a Lua env that survives a play session must never see an
            // old id reused); only the registry is cleared.
            s_byId.Clear();
            s_byKey.Clear();
            s_staging = null;
        }

        // ── Queries ──────────────────────────────────────────────────────
        internal static Entry Get(int id) => s_byId.TryGetValue(id, out var e) ? e : null;
        public static int Count => s_byId.Count;

        static string StateName(int s) =>
            s == Loaded ? "loaded" : s == Failed ? "failed" : s == Despawned ? "despawned" : "loading";

        /// <summary>A title id with its Beta suffix removed: a title and its Beta twin are one title.</summary>
        public static string BaseTitle(string id)
        {
            if (string.IsNullOrEmpty(id)) return id ?? "";
            return id.Length > 4 && id.EndsWith("Beta", StringComparison.OrdinalIgnoreCase)
                ? id.Substring(0, id.Length - 4) : id;
        }

        static GameObject OwnerOf(GameObject caller)
        {
            if (caller == null) return null;
            var root = DreamParkLuaAPI.AttractionRoot(caller);
            return root != null ? root : caller;
        }

        static string TitleOf(GameObject caller)
        {
            if (caller == null) return null;
            string id = DreamParkLuaAPI.GameId(caller);
            if (string.IsNullOrEmpty(id)) id = NetId.ContentIdOf(caller.transform);
            return id;
        }

        static bool Alive(UnityEngine.Object o) => o != null;

        // ── Address resolution ───────────────────────────────────────────

        static string Normalize(string raw) => raw == null ? null : raw.Replace("\\/", "/").Trim();

        static List<string> Twins(string gameId)
        {
            var l = new List<string>();
            if (string.IsNullOrEmpty(gameId)) return l;
            string b = BaseTitle(gameId);
            l.Add(gameId);
            if (!string.Equals(b, gameId, StringComparison.Ordinal)) l.Add(b);
            else l.Add(gameId + "Beta");
            return l;
        }

        static bool Locatable(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            try
            {
                foreach (var locator in Addressables.ResourceLocators)
                    if (locator.Locate(key, typeof(GameObject), out var locs) && locs != null && locs.Count > 0) return true;
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// The runtime address for <paramref name="request"/> in title <paramref name="gameId"/>
        /// (Beta twin included), or null. Exact / "Content/" stem / twin-prefix first, then a
        /// leaf-name match over the mounted locators (newest first) preferring "/Props/" keys —
        /// LevelAnchor.ResolveSpawnAddress's rule, scoped to the caller's title. In the Editor,
        /// a prefab asset of that name is the last resort.
        /// </summary>
        public static string ResolveAddress(string request, string gameId)
        {
            if (AddressResolver != null) return AddressResolver(request, gameId);
            string s = Normalize(request);
            if (string.IsNullOrEmpty(s)) return null;
            var twins = Twins(gameId);

            if (s.IndexOf('/') >= 0)
            {
                if (Locatable(s)) return s;
                string body = s.StartsWith("Content/", StringComparison.OrdinalIgnoreCase) ? s.Substring(8) : s;
                if (body != s && Locatable(body)) return body;
                int slash = body.IndexOf('/');
                if (slash > 0)
                {
                    string head = body.Substring(0, slash), rest = body.Substring(slash);
                    foreach (string t in twins)
                        if (!string.Equals(t, head, StringComparison.OrdinalIgnoreCase) && Locatable(t + rest)) return t + rest;
                    foreach (string t in Twins(head))
                        if (!string.Equals(t, head, StringComparison.OrdinalIgnoreCase) && Locatable(t + rest)) return t + rest;
                    if (twins.Count == 0) twins = Twins(head);
                }
            }

            string leaf = s.Substring(s.LastIndexOf('/') + 1);
            if (string.IsNullOrEmpty(leaf)) return null;
            string suffix = "/" + leaf;
            string best = null;
            int bestRank = int.MaxValue;
            try
            {
                var locators = new List<UnityEngine.AddressableAssets.ResourceLocators.IResourceLocator>(Addressables.ResourceLocators);
                for (int li = locators.Count - 1; li >= 0; li--)
                {
                    var locator = locators[li];
                    if (locator?.Keys == null) continue;
                    foreach (object k in locator.Keys)
                    {
                        if (!(k is string key) || !key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                        if (key.IndexOf("/Previews/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (twins.Count > 0)
                        {
                            bool mine = false;
                            foreach (string t in twins)
                                if (key.StartsWith(t + "/", StringComparison.OrdinalIgnoreCase)) { mine = true; break; }
                            if (!mine) continue;
                        }
                        int rank = key.IndexOf("/Props/", StringComparison.OrdinalIgnoreCase) >= 0 ? 0
                                 : key.IndexOf("/Levels/", StringComparison.OrdinalIgnoreCase) >= 0 ? 2 : 1;
                        if (rank >= bestRank) continue;
                        if (!locator.Locate(key, typeof(GameObject), out var locs) || locs == null || locs.Count == 0) continue;
                        best = key;
                        bestRank = rank;
                        if (rank == 0) break;
                    }
                    if (bestRank == 0) break;
                }
            }
            catch (Exception e) { Debug.LogWarning("[dp.spawn_prop] locator scan failed: " + e.Message); }
            if (best != null) return best;

#if UNITY_EDITOR
            return EditorResolve(leaf, twins);
#else
            return null;
#endif
        }

#if UNITY_EDITOR
        // The Park Simulator instantiates prefab assets straight from the project, so a
        // prop that Addressables cannot locate (no build, "Use Existing Build" mode) is
        // still found here: a prefab named exactly `leaf`, the caller's content folder first.
        static string EditorResolve(string leaf, List<string> twins)
        {
            string bestPath = null;
            int bestRank = int.MaxValue;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab " + leaf))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.Equals(System.IO.Path.GetFileNameWithoutExtension(path), leaf, StringComparison.OrdinalIgnoreCase)) continue;
                int rank = 2;
                foreach (string t in twins)
                    if (path.StartsWith("Assets/Content/" + t + "/", StringComparison.OrdinalIgnoreCase)) { rank = 0; break; }
                if (rank != 0 && twins.Count > 0 && path.StartsWith("Assets/Content/", StringComparison.OrdinalIgnoreCase)) continue; // another title
                if (rank != 0 && path.StartsWith("Assets/DreamPark/", StringComparison.OrdinalIgnoreCase)) rank = 1;   // SDK samples
                if (rank < bestRank) { bestRank = rank; bestPath = path; }
            }
            if (bestPath == null) return null;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(bestPath);
            string address = null;
            if (prefab != null && prefab.TryGetComponent<PropTemplate>(out var pt) && !string.IsNullOrEmpty(pt.resourceName)) address = pt.resourceName;
            if (string.IsNullOrEmpty(address))
            {
                string gid = twins.Count > 0 ? twins[0] : "Editor";
                bool level = prefab != null && prefab.GetComponent<LevelTemplate>() != null;
                address = gid + (level ? "/Levels/Custom/" : "/Props/Generic/") + leaf;
            }
            s_editorPaths[address] = bestPath;
            return address;
        }
#endif

        // ── Loading ──────────────────────────────────────────────────────

        static async UniTask<bool> DownloadClosure(string address)
        {
            if (DependencyDownloader != null) return await DependencyDownloader(address);
#if UNITY_EDITOR
            if (s_editorPaths.ContainsKey(address)) return true;
#endif
#if DREAMPARKCORE
            // The device path: the same closure fetch the park loader runs before a spawn
            // (FAST PLAY — exactly this address's bundles, newest mounted locator).
            if (!Application.isEditor)
            {
                try
                {
                    return await global::ContentManager.DownloadAddressDependenciesAsync(
                        global::ContentManager.ContentIdFromAddress(address), new[] { address }) > 0;
                }
                catch (Exception e) { Debug.LogWarning($"[dp.spawn_prop] closure for {address} failed: {e.Message}"); return false; }
            }
#endif
            try
            {
                var h = Addressables.DownloadDependenciesAsync((object)address, false);
                await h.Task;
                bool ok = h.Status == AsyncOperationStatus.Succeeded;
                if (h.IsValid()) Addressables.Release(h);
                return ok;
            }
            catch (Exception e) { Debug.LogWarning($"[dp.spawn_prop] closure for {address} failed: {e.Message}"); return false; }
        }

        static async UniTask<GameObject> LoadPrefab(string address)
        {
            if (AssetLoader != null) return await AssetLoader(address);
#if UNITY_EDITOR
            if (s_editorPaths.TryGetValue(address, out var path)) return AssetDatabase.LoadAssetAtPath<GameObject>(path);
#endif
            // GetAsset: newest-locator preference, corrupt-bundle classification, failure
            // containment (null, never a throw) — the park loader's own load path.
            return await address.GetAsset<GameObject>();
        }

        static bool IsLevelAddress(string a) => a != null && a.IndexOf("/Levels/", StringComparison.OrdinalIgnoreCase) >= 0;

        // ── Spawn / preload / despawn ────────────────────────────────────

        static Entry NewEntry(GameObject caller, string request)
        {
            var e = new Entry { id = ++s_next, request = request, owner = OwnerOf(caller), title = TitleOf(caller) };
            s_byId[e.id] = e;
            return e;
        }

        /// <summary>
        /// Spawn (or return the live spawn of) prop <paramref name="request"/> under the caller's
        /// attraction. <paramref name="spec"/> = "flags|px|py|pz|rx|ry|rz|rw" — flags: P pos given,
        /// R rot given, W world space, S seat, G parent given. Returns the handle id (≥ 1), 0 = no caller.
        /// </summary>
        public static int Spawn(GameObject caller, string request, GameObject parent, string key, string spec)
        {
            if (caller == null) { Debug.LogWarning("[dp.spawn_prop] no calling object"); return 0; }
            GameObject owner = OwnerOf(caller);
            string norm = Normalize(request) ?? "";

            // Idempotent by key: the live (or still loading) spawn of this key is the answer.
            string k = string.IsNullOrEmpty(key) ? null : key;
            string mapKeyProbe = null;
            if (k != null)
            {
                mapKeyProbe = owner.GetInstanceID() + "\n" + k;
                if (s_byKey.TryGetValue(mapKeyProbe, out int existing) && s_byId.TryGetValue(existing, out var ex)
                    && (ex.state == Loading || ex.state == Loaded) && (ex.state == Loading || Alive(ex.go)))
                    return existing;
            }

            var e = NewEntry(caller, norm);
            ParseSpec(e, spec);
            e.parentGo = parent;
            e.address = ResolveAddress(norm, e.title);
            // No key: the address (else the request) is the key, so spawning the same prop
            // twice without one returns the same handle. Distinct keys for several copies.
            e.key = k ?? (e.address ?? norm);
            e.mapKey = owner.GetInstanceID() + "\n" + e.key;
            if (k == null && s_byKey.TryGetValue(e.mapKey, out int same) && s_byId.TryGetValue(same, out var sx)
                && (sx.state == Loading || (sx.state == Loaded && Alive(sx.go))))
            {
                s_byId.Remove(e.id);
                return same;
            }
            s_byKey[e.mapKey] = e.id;

            if (e.address == null) { FailSoon(e, ErrNotFound); return e.id; }
            if (IsLevelAddress(e.address)) { FailSoon(e, ErrNotAProp); return e.id; }
            RunSpawn(e).Forget();
            return e.id;
        }

        /// <summary>Download a prop's bundle closure without instantiating it. Idempotent per address.</summary>
        public static int Preload(GameObject caller, string request)
        {
            string norm = Normalize(request) ?? "";
            string title = TitleOf(caller);
            string address = ResolveAddress(norm, title);
            string mapKey = "preload\n" + (address ?? (BaseTitle(title) + "\n" + norm));
            if (s_byKey.TryGetValue(mapKey, out int existing) && s_byId.TryGetValue(existing, out var ex)
                && (ex.state == Loading || ex.state == Loaded))
                return existing;

            var e = NewEntry(caller, norm);
            e.preload = true;
            e.address = address;
            e.key = address ?? norm;
            e.mapKey = mapKey;
            s_byKey[mapKey] = e.id;
            if (address == null) { FailSoon(e, ErrNotFound); return e.id; }
            if (IsLevelAddress(address)) { FailSoon(e, ErrNotAProp); return e.id; }
            RunPreload(e).Forget();
            return e.id;
        }

        static void ParseSpec(Entry e, string spec)
        {
            var p = (spec ?? "").Split('|');
            string flags = p.Length > 0 ? p[0] : "";
            float F(int i) => i < p.Length && float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
            e.hasPos = flags.IndexOf('P') >= 0;
            e.hasRot = flags.IndexOf('R') >= 0;
            e.world = flags.IndexOf('W') >= 0;
            e.seat = flags.IndexOf('S') >= 0;
            e.parentGiven = flags.IndexOf('G') >= 0;
            e.pos = new Vector3(F(1), F(2), F(3));
            if (e.hasRot)
            {
                var q = new Quaternion(F(4), F(5), F(6), F(7));
                float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                e.rot = n > 1e-6f ? new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n) : Quaternion.identity;
            }
        }

        // Events never arrive before the call that started them has returned its handle:
        // a play-mode spawn always yields once first.
        static async UniTask YieldOnce()
        {
            if (Application.isPlaying) await UniTask.Yield();
        }

        static void FailSoon(Entry e, string err) => FailLater(e, err).Forget();

        static async UniTaskVoid FailLater(Entry e, string err)
        {
            await YieldOnce();
            Fail(e, err);
        }

        static void Fail(Entry e, string err)
        {
            if (e.state == Despawned) return;
            e.state = Failed;
            e.error = err;
            Debug.LogWarning($"[dp.spawn_prop] '{e.request}' ({e.address ?? "unresolved"}) failed: {err}");
            Dispatch(e, "failed");
        }

        static async UniTaskVoid RunPreload(Entry e)
        {
            await YieldOnce();
            bool ok;
            try { ok = await DownloadClosure(e.address); }
            catch (Exception ex) { Debug.LogWarning("[dp.preload_prop] " + ex.Message); ok = false; }
            if (e.state == Despawned) return;
            if (!ok) { Fail(e, ErrDownloadFailed); return; }
            e.state = Loaded;
            Dispatch(e, "loaded");
        }

        static async UniTaskVoid RunSpawn(Entry e)
        {
            await YieldOnce();
            GameObject prefab = null;
            try
            {
                // The closure first (device: ContentManager's FAST PLAY fetch, the park
                // loader's own step); the load below is the authority, so a failed
                // prefetch is retried by it rather than failing the spawn.
                await DownloadClosure(e.address);
                if (e.state == Despawned) return;
                prefab = await LoadPrefab(e.address);
            }
            catch (Exception ex) { Debug.LogWarning($"[dp.spawn_prop] load {e.address} threw: {ex.Message}"); }
            if (e.state == Despawned) return;
            if (!Alive(e.owner)) { Drop(e); return; }   // the attraction left while it downloaded
            if (prefab == null) { Fail(e, ErrDownloadFailed); return; }
            if (prefab.GetComponent<PropTemplate>() == null || prefab.GetComponent<LevelTemplate>() != null)
            { Fail(e, ErrNotAProp); return; }

            GameObject inst = null;
            try { inst = Materialize(e, prefab); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[dp.spawn_prop] spawn {e.address} threw: {ex}");
                if (inst != null) DestroyObject(inst);
                inst = null;
            }
            if (inst == null) { Fail(e, ErrSpawnFailed); return; }
            e.go = inst;
            e.state = Loaded;
            Dispatch(e, "loaded");
        }

        static Transform Staging()
        {
            if (s_staging != null) return s_staging;
            var go = new GameObject("~DreamParkPropStaging") { hideFlags = HideFlags.HideAndDontSave };
            go.SetActive(false);   // anything instantiated in here is inert: no Awake/OnEnable yet
            if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(go);
            s_staging = go.transform;
            return s_staging;
        }

        static GameObject Materialize(Entry e, GameObject prefab)
        {
            Transform parent = e.parentGiven && Alive(e.parentGo) ? e.parentGo.transform : e.owner.transform;
            Transform staging = Staging();
            GameObject inst = UnityEngine.Object.Instantiate(prefab, staging, false);
            inst.name = prefab.name;

            // Pose relative to the FINAL parent, written while inert (staging sits at the
            // origin), so Awake/OnEnable and FloorAnchor's one-time capture see the real pose.
            Vector3 lp = Vector3.zero;
            Quaternion lr = Quaternion.identity;
            if (e.world && parent != null)
            {
                lp = e.hasPos ? parent.InverseTransformPoint(e.pos) : Vector3.zero;
                lr = e.hasRot ? Quaternion.Inverse(parent.rotation) * e.rot : Quaternion.identity;
            }
            else
            {
                if (e.hasPos) lp = e.pos;
                if (e.hasRot) lr = e.rot;
            }
            inst.transform.localPosition = lp;
            inst.transform.localRotation = lr;

            // Deterministic multiplayer identity, before any NetId inside hashes (Start).
            if (!inst.TryGetComponent(out NetScope scope)) scope = inst.AddComponent<NetScope>();
            scope.scopeKey = ScopeKeyFor(e.owner, e.key);

            var link = inst.AddComponent<DreamParkPropLink>();
            link.propId = e.id;

            if (e.seat && inst.GetComponent<FloorAnchor>() == null) inst.AddComponent<FloorAnchor>();

            // Owner tracking: a prop parented outside its owner still despawns with it.
            if (!e.owner.TryGetComponent(out DreamParkPropLink ownerLink)) ownerLink = e.owner.AddComponent<DreamParkPropLink>();
            ownerLink.isOwner = true;

            // Out of staging: Awake/OnEnable run here, with hierarchy, pose and scope final.
            inst.transform.SetParent(parent, false);

            Register(inst);
            return inst;
        }

        /// <summary>The NetScope key of a spawned prop: the owner's NetScope-stable hash plus the creator's key.</summary>
        public static string ScopeKeyFor(GameObject owner, string key)
        {
            uint ownerHash = owner != null ? NetId.HashPath(owner.transform) : 0u;
            return "prop|" + ownerHash.ToString("x8", CultureInfo.InvariantCulture) + "|" + (key ?? "");
        }

        static void Register(GameObject inst)
        {
            var lom = LevelObjectManager.Instance;
            if (lom == null) return;
            // Under a LevelTemplate this registers ONE LevelObject for the prop root — the
            // nested-prop rule authored props follow. Parked on arrival while the park is.
            bool parked = !LevelObjectManager.objectsEnabled || LevelObjectManager.ParkContentLocked;
            lom.RegisterLevelObject(inst, parked);
        }

        static void Unregister(GameObject inst)
        {
            var lom = LevelObjectManager.Instance;
            if (lom == null || inst == null) return;
            foreach (Transform t in inst.GetComponentsInChildren<Transform>(true))
                lom.UnregisterLevelObject(t.gameObject);
        }

        static void DestroyObject(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(go);
            else UnityEngine.Object.DestroyImmediate(go);
        }

        static void Drop(Entry e)
        {
            e.state = Despawned;
            if (e.mapKey != null && s_byKey.TryGetValue(e.mapKey, out int id) && id == e.id) s_byKey.Remove(e.mapKey);
            s_byId.Remove(e.id);
        }

        /// <summary>Despawn by handle id. True when something was despawned or cancelled.</summary>
        public static bool Despawn(int id)
        {
            if (!s_byId.TryGetValue(id, out var e) || e.state == Despawned) return false;
            GameObject go = e.go;
            Drop(e);
            e.go = null;
            if (go != null)
            {
                Unregister(go);
                var link = go.GetComponent<DreamParkPropLink>();
                if (link != null) link.propId = 0;   // OnDestroy must not re-enter
                DestroyObject(go);
            }
            return true;
        }

        /// <summary>The handle id of a spawned prop's object (or an object inside one), 0 if none.</summary>
        public static int IdOf(GameObject go)
        {
            if (go == null) return 0;
            for (Transform t = go.transform; t != null; t = t.parent)
                if (t.TryGetComponent(out DreamParkPropLink l) && l.propId != 0) return l.propId;
            return 0;
        }

        internal static void NoteDestroyed(int id)
        {
            if (id == 0 || !s_byId.TryGetValue(id, out var e)) return;
            try { if (e.go != null) Unregister(e.go); } catch (Exception) { }
            e.go = null;
            Drop(e);
        }

        /// <summary>The owner (attraction, or the calling object) is going away: so is everything it spawned.</summary>
        internal static void NoteOwnerDestroyed(GameObject owner)
        {
            var mine = new List<Entry>();
            foreach (var kv in s_byId)
                if (ReferenceEquals(kv.Value.owner, owner) || !Alive(kv.Value.owner)) mine.Add(kv.Value);
            foreach (var e in mine)
            {
                GameObject go = e.go;
                e.go = null;
                Drop(e);
                if (go == null) continue;
                try { Unregister(go); } catch (Exception) { }
                var link = go.GetComponent<DreamParkPropLink>();
                if (link != null) link.propId = 0;
                // Children of the owner are already being destroyed with it.
                bool underOwner = owner != null && go.transform.IsChildOf(owner.transform);
                if (!underOwner) DestroyObject(go);
            }
        }

        // ── Lua bridge ───────────────────────────────────────────────────

        static void Dispatch(Entry e, string kind)
        {
            LuaFunction fn = null;
            try
            {
                LuaEnv env = LuaBehaviour.GetLuaEnv();
                fn = env?.Global.Get<LuaFunction>("__dp_prop_event");
                fn?.Call(e.id, kind, e.error ?? "");
            }
            catch (Exception ex) { Debug.LogWarning("[dp.spawn_prop] event dispatch failed: " + ex.Message); }
            finally { fn?.Dispose(); }
        }

        static string Info(int id, string field)
        {
            var e = Get(id);
            if (e == null) return field == "state" ? "despawned" : "";
            switch (field)
            {
                case "state": return StateName(e.state);
                case "address": return e.address ?? "";
                case "key": return e.key ?? "";
                case "error": return e.error ?? "";
                case "title": return BaseTitle(e.title);
                case "request": return e.request ?? "";
                case "kind": return e.preload ? "preload" : "spawn";
                default: return "";
            }
        }

        static GameObject Go(int id)
        {
            var e = Get(id);
            return e != null && e.state == Loaded && Alive(e.go) ? e.go : null;
        }

        static string TitleOfCaller(GameObject go) => BaseTitle(TitleOf(go));

        internal static void Register(LuaEnv env)
        {
            env.Global.Set("dp_prop_spawn",   new Func<GameObject, string, GameObject, string, string, int>(Spawn));
            env.Global.Set("dp_prop_preload", new Func<GameObject, string, int>(Preload));
            env.Global.Set("dp_prop_despawn", new Func<int, bool>(Despawn));
            env.Global.Set("dp_prop_of",      new Func<GameObject, int>(IdOf));
            env.Global.Set("dp_prop_go",      new Func<int, GameObject>(Go));
            env.Global.Set("dp_prop_info",    new Func<int, string, string>(Info));
            env.Global.Set("dp_prop_title_of", new Func<GameObject, string>(TitleOfCaller));
            env.DoString(Bootstrap, "dp.props.bootstrap");
        }

        // No double quotes inside this verbatim block. Pure Lua 5.3.
        internal const string Bootstrap = @"
dp = dp or {}
__dp_props = __dp_props or {}
__dp_prop_fns = __dp_prop_fns or { loaded = {}, failed = {} }

local function __prop_go(x)
    if x == nil then return nil end
    local ok, g = pcall(function() return x.gameObject end)
    if ok and g ~= nil then return g end
    return nil
end

local function __prop_handle(id)
    if id == nil or id == 0 then return nil end
    local h = __dp_props[id]
    if h ~= nil then return h end
    h = { id = id }
    h.state = function() return dp_prop_info(id, 'state') end
    h.loaded = function() return dp_prop_info(id, 'state') == 'loaded' end
    h.ready = h.loaded
    h.error = function()
        local e = dp_prop_info(id, 'error')
        if e == nil or e == '' then return nil end
        return e
    end
    h.despawn = function() return dp_prop_despawn(id) end
    setmetatable(h, { __index = function(t, k)
        if k == 'go' then return dp_prop_go(id) end
        if k == 'key' or k == 'address' or k == 'request' or k == 'kind' then
            local v = dp_prop_info(id, k)
            if v == '' then return nil end
            return v
        end
        return nil
    end })
    __dp_props[id] = h
    return h
end
__dp_prop_handle = __prop_handle

local function __prop_num(v) return string.format('%.9g', v or 0) end

-- opts: parent (Transform/GameObject), pos (Vector3), rot (Quaternion), key (string),
-- seat (bool), world (bool; also ['local'] = false). Local to the parent by default.
dp.spawn_prop = function(name, opts, caller)
    opts = opts or {}
    if caller == nil then caller = __prop_go(opts.caller) end
    local flags = ''
    local parent = __prop_go(opts.parent)
    if opts.parent ~= nil then flags = flags .. 'G' end
    local p, r = opts.pos, opts.rot
    local px, py, pz, rx, ry, rz, rw = 0, 0, 0, 0, 0, 0, 1
    if p ~= nil then flags = flags .. 'P'; px, py, pz = p.x, p.y, p.z end
    if r ~= nil then flags = flags .. 'R'; rx, ry, rz, rw = r.x, r.y, r.z, r.w end
    if opts.world == true or opts['local'] == false or opts.space == 'world' then flags = flags .. 'W' end
    if opts.seat == true then flags = flags .. 'S' end
    local spec = table.concat({ flags, __prop_num(px), __prop_num(py), __prop_num(pz),
        __prop_num(rx), __prop_num(ry), __prop_num(rz), __prop_num(rw) }, '|')
    local key = opts.key
    if key ~= nil then key = tostring(key) else key = '' end
    local id = dp_prop_spawn(caller, tostring(name or ''), parent, key, spec)
    return __prop_handle(id)
end

dp.preload_prop = function(name, caller)
    return __prop_handle(dp_prop_preload(caller, tostring(name or '')))
end

dp.despawn_prop = function(x)
    if x == nil then return false end
    if type(x) == 'table' and rawget(x, 'id') ~= nil then return dp_prop_despawn(rawget(x, 'id')) end
    local go = __prop_go(x)
    if go == nil then return false end
    local id = dp_prop_of(go)
    if id == 0 then return false end
    return dp_prop_despawn(id)
end

dp.prop_of = function(x)
    local go = __prop_go(x)
    if go == nil then return nil end
    return __prop_handle(dp_prop_of(go))
end

local function __prop_owner_gone(owner)
    if owner == nil then return false end
    local ok, gone = pcall(function() return owner:Equals(nil) end)
    return ok and gone == true
end

local function __prop_on(kind, fn, owner, go)
    if fn == nil then return nil end
    if go == nil and owner ~= nil then pcall(function() go = owner.gameObject end) end
    local list = __dp_prop_fns[kind]
    local title = nil
    if go ~= nil then title = dp_prop_title_of(go) end
    local h = { fn = fn, owner = owner, title = title }
    h.off = function()
        for i = #list, 1, -1 do if list[i] == h then table.remove(list, i) end end
    end
    list[#list + 1] = h
    return h
end
local function __prop_off(kind, x)
    if x == nil then return end
    local list = __dp_prop_fns[kind]
    for i = #list, 1, -1 do
        if list[i] == x or list[i].fn == x then table.remove(list, i) end
    end
end

-- From a script, handlers hear this title's props (a Beta twin is the same title) and
-- drop with the script (owner defaults to self). From the console: every prop.
dp.on_prop_loaded = function(fn, owner, go) return __prop_on('loaded', fn, owner, go) end
dp.on_prop_failed = function(fn, owner, go) return __prop_on('failed', fn, owner, go) end
dp.off_prop_loaded = function(x) __prop_off('loaded', x) end
dp.off_prop_failed = function(x) __prop_off('failed', x) end

-- Called from C# (DreamParkLuaProps.Dispatch).
function __dp_prop_event(id, kind, err)
    local list = __dp_prop_fns[kind]
    if list == nil then return end
    for i = #list, 1, -1 do
        if __prop_owner_gone(list[i].owner) then table.remove(list, i) end
    end
    local h = __prop_handle(id)
    if h == nil then return end
    local title = dp_prop_info(id, 'title')
    local snap = {}
    for j = 1, #list do snap[j] = list[j] end
    for j = 1, #snap do
        local w = snap[j]
        if w.title == nil or w.title == '' or title == '' or w.title == title then
            local ok, e
            if kind == 'failed' then ok, e = pcall(w.fn, h, err) else ok, e = pcall(w.fn, h) end
            if not ok then print('[dp.on_prop_' .. kind .. '] handler threw: ' .. tostring(e)) end
        end
    end
end
";
    }
}
