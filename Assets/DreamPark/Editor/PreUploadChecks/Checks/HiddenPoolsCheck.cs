#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DreamPark.PreUploadChecks.Checks
{
    // HIDDEN POOLS: an attraction prefab holding many inactive-at-load copies of the same thing.
    //
    // THE RULE (director, Nov 11 2026; ATTRACTIONS.md § The attraction prefab): "Author the stage and one of every
    // actor. Instantiate the crowd." The prefab holds the stage, ONE exemplar of every kind of actor and the fixed
    // content the game counts; anything that comes and goes (waves, projectiles, traffic, pickups, debris, every
    // particle effect) is instantiated at runtime and destroyed when done. "If the prefab holds several hidden copies
    // of the same thing waiting to be used, that's a pool. Delete the copies and instantiate."
    //
    // WHY: the old rule ("do not spawn the contents of an attraction at runtime") made authors pre-place big hidden
    // pools (ArcadeLand's Toad Road held ~750 nested copies: 77 trucks, 66 cars, 47 wagons...). Every copy is
    // deserialized, registered with the optimizer and parked when the attraction loads, so the attraction loads slowly
    // while the guest sees none of it.
    //
    // WHAT COUNTS AS A COPY. Walk the prefab from the root. The first inactive GameObject on a branch is hidden at
    // load, and so is everything under it. Inside a hidden branch:
    //   - a nested prefab instance root is one copy of its source prefab (variants count as their own source);
    //   - a bare container (Transform only, not a prefab instance: a "Pool" object) is not a copy: its children are;
    //   - any other object is one copy, keyed by a "near-identical" signature: its name with digits, separators and
    //     " (n)" stripped, its component types and its mesh;
    //   - a lone static mesh (no children, only MeshFilter/MeshRenderer) or an empty is NOT a copy: a hidden pip on a
    //     lives display or an alternate animation frame costs nothing worth reporting.
    // Copies are grouped per PARENT: more than MaxHiddenCopies copies of the same thing under one parent is a pool.
    // Grouping per parent keeps per-actor state children (each alien's hidden second frame, each brick's hidden
    // damage layers: one per parent) out of it. EXCEPT effects: a hidden copy carrying a ParticleSystem is never part
    // of an actor's look ("every particle effect: one prefab per effect type, created at the spot ... never
    // pre-placed copies"), so its copies are counted across the whole attraction (each zombie carrying its own hidden
    // loot drop with a sparkle is N copies of that drop).
    //
    // NOT A POOL: objects a layout frame switches on (AttractionLayoutOverride setActive + active). Those are authored
    // content for another room size, which is exactly what layout frames are for.
    //
    // Warning, never Blocking: a hidden pool plays correctly; it only loads slowly. There is deliberately no
    // ParticleSystem-count threshold (director: "this doesnt have to be a rule"); pre-placed copies of an effect
    // count as copies under the same rule as everything else.
    //
    // NO AUTOMATIC FIX: replacing a pool means changing the game's Lua to instantiate from the exemplar. The finding
    // opens the prefab on the biggest pool.
    public sealed class HiddenPoolsCheck : IPreUploadCheck
    {
        public const string CheckId = "hidden-pools";

        // "More than 8 inactive-at-load copies of the same prefab or near-identical object" (director).
        public const int MaxHiddenCopies = 8;

        public string Id { get { return CheckId; } }
        public string DisplayName { get { return "Hidden pools in attractions"; } }
        public CheckSeverity DefaultSeverity { get { return CheckSeverity.Warning; } }

        // Loads each attraction asset and walks its transforms. No prefab contents scene, no dependency walk.
        public bool RunsInAdvisoryScan { get { return true; } }

        public string Rationale
        {
            get
            {
                return "An attraction prefab holds the stage and one exemplar of every actor; whatever comes and goes "
                     + "(waves, projectiles, traffic, pickups, effects) is instantiated at runtime and destroyed when "
                     + "done. Hidden copies waiting to be used are a pool: every one is loaded with the attraction and "
                     + "slows it down. Delete the copies and instantiate from the exemplar.";
            }
        }

        public sealed class Pool
        {
            public string parentPath;   // under the attraction root ("" = the root itself); effects: the first copy's
            public bool acrossAttraction; // effect copies, counted across the whole attraction
            public string what;         // source prefab name, or the object's name for plain objects
            public string key;
            public int count;
        }

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            var findings = new List<Finding>();
            int i = 0;
            foreach (var root in ctx.roots)
            {
                ctx.Progress((float)i / Mathf.Max(1, ctx.roots.Count), $"Looking for hidden pools in {root.name}…");
                i++;
                if (root.kind != ContentRootKindPublic.Attraction) continue;

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(root.assetPath);
                if (prefab == null || prefab.GetComponent<AttractionTemplate>() == null) continue;

                List<Pool> pools = FindPools(prefab, out int hiddenCopies);
                if (pools.Count == 0) continue;

                int pooled = pools.Sum(p => p.count);
                Pool biggest = pools[0];
                var finding = new Finding
                {
                    checkId = CheckId,
                    severity = CheckSeverity.Warning,
                    assetGuid = root.guid,
                    assetPath = root.assetPath,
                    title = $"{root.name}: {pooled} hidden copies in {pools.Count} pool{(pools.Count == 1 ? "" : "s")} "
                          + $"(biggest: {biggest.count} × {biggest.what})",
                    detail = Detail(root.name, pools, hiddenCopies),
                };
                string path = root.assetPath;
                string select = biggest.parentPath;
                finding.fixes.Add(FixAction.Navigate("Open prefab at the biggest pool", () => OpenAndSelect(path, select)));
                findings.Add(finding);
            }
            return CheckResult.From(CheckId, findings);
        }

        /// <summary>Pools (more than MaxHiddenCopies hidden copies of one thing under one parent), biggest first.
        /// hiddenCopies = every hidden copy counted, pooled or not.</summary>
        public static List<Pool> FindPools(GameObject prefabRoot, out int hiddenCopies)
        {
            Transform root = prefabRoot.transform;
            var frameOn = new HashSet<Transform>();
            AttractionTemplate attraction = prefabRoot.GetComponent<AttractionTemplate>();
            if (attraction != null)
                foreach (AttractionLayoutFrame frame in attraction.LayoutFrames)
                {
                    if (frame?.overrides == null) continue;
                    foreach (AttractionLayoutOverride o in frame.overrides)
                    {
                        if (o == null || !o.setActive || !o.active) continue;
                        Transform t = o.Resolve(root);
                        if (t != null) frameOn.Add(t);
                    }
                }

            var groups = new Dictionary<(Transform, string), Pool>();
            int counted = 0;

            void Hidden(Transform n, Transform parent)
            {
                if (frameOn.Contains(n)) return;
                GameObject go = n.gameObject;
                bool isInstance = PrefabUtility.IsAnyPrefabInstanceRoot(go);
                if (!isInstance && n.childCount > 0 && IsBare(go))
                {
                    foreach (Transform c in n) Hidden(c, n);
                    return;
                }
                if (!isInstance && n.childCount == 0 && IsLoneMesh(go)) return;

                string what, key;
                if (isInstance)
                {
                    string src = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                    what = string.IsNullOrEmpty(src) ? go.name : System.IO.Path.GetFileNameWithoutExtension(src);
                    key = "prefab:" + (string.IsNullOrEmpty(src) ? go.name : src);
                }
                else
                {
                    what = go.name;
                    key = "object:" + Signature(go);
                }
                counted++;
                bool effect = n.GetComponentInChildren<ParticleSystem>(true) != null;
                Transform groupParent = effect ? null : parent;
                if (!groups.TryGetValue((groupParent, key), out Pool pool))
                    groups[(groupParent, key)] = pool = new Pool
                    {
                        parentPath = PathUnder(parent, root), what = what, key = key, acrossAttraction = effect,
                    };
                pool.count++;
            }

            void Active(Transform n)
            {
                foreach (Transform c in n)
                {
                    if (c.gameObject.activeSelf) Active(c);
                    else Hidden(c, n);
                }
            }

            Active(root);
            hiddenCopies = counted;
            return groups.Values.Where(p => p.count > MaxHiddenCopies).OrderByDescending(p => p.count).ToList();
        }

        private static string Detail(string name, List<Pool> pools, int hiddenCopies)
        {
            var sb = new StringBuilder();
            sb.Append($"'{name}' holds hidden copies of the same thing waiting to be used, more than {MaxHiddenCopies} "
                    + "under one parent (effects: anywhere in the attraction):\n");
            foreach (Pool p in pools.Take(12))
                sb.Append(p.acrossAttraction
                    ? $"  • {p.count} × {p.what} (an effect, counted across the attraction; first under {(p.parentPath.Length == 0 ? "the root" : p.parentPath)})\n"
                    : $"  • {p.count} × {p.what}  under {(p.parentPath.Length == 0 ? "the root" : p.parentPath)}\n");
            if (pools.Count > 12) sb.Append($"  • … and {pools.Count - 12} more pools\n");
            sb.Append($"({hiddenCopies} hidden copies in all.)\n\n");
            sb.Append("Every copy is loaded, registered and parked when the attraction loads, so a pool slows the "
                    + "attraction down while the guest sees none of it.\n\n"
                    + "FIX (ATTRACTIONS.md § The attraction prefab: author the stage and one of every actor, instantiate "
                    + "the crowd): keep ONE visible exemplar of each kind, posed mid-action, as the runtime template; "
                    + "delete the hidden copies; instantiate from the exemplar (or a prefab reference) when one is "
                    + "needed and destroy it when it is done. Effects too: one prefab per effect type, created at the "
                    + "spot. In multiplayer, key each spawned object by its event's id (a string such as \"p2-bolt-14\"), "
                    + "never by a pool slot. Content a layout frame switches on is not counted.");
            return sb.ToString();
        }

        // Transform/RectTransform only: a grouping object.
        private static bool IsBare(GameObject go)
        {
            foreach (Component c in go.GetComponents<Component>())
                if (c != null && !(c is Transform)) return false;
            return true;
        }

        // A single static mesh (or an empty): MeshFilter/MeshRenderer and nothing else.
        private static bool IsLoneMesh(GameObject go)
        {
            foreach (Component c in go.GetComponents<Component>())
                if (c != null && !(c is Transform) && !(c is MeshFilter) && !(c is MeshRenderer)) return false;
            return true;
        }

        private static readonly Regex Numbering = new Regex(@"\(\s*\d+\s*\)|[\d\s_\-\.]+", RegexOptions.Compiled);

        private static string Signature(GameObject go)
        {
            string name = Numbering.Replace(go.name, "").ToLowerInvariant();
            var types = go.GetComponents<Component>()
                .Where(c => c != null && !(c is Transform))
                .Select(c => c.GetType().Name)
                .OrderBy(s => s, StringComparer.Ordinal);
            MeshFilter mf = go.GetComponent<MeshFilter>();
            string mesh = mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "";
            return name + "|" + string.Join(",", types) + "|" + mesh;
        }

        private static string PathUnder(Transform t, Transform root)
        {
            if (t == null || t == root) return "";
            string s = t.name;
            for (Transform c = t.parent; c != null && c != root; c = c.parent) s = c.name + "/" + s;
            return s;
        }

        private static void OpenAndSelect(string prefabPath, string pathUnderRoot)
        {
            try
            {
                var stage = PrefabStageUtility.OpenPrefab(prefabPath);
                GameObject root = stage != null ? stage.prefabContentsRoot : null;
                if (root == null) return;
                Transform t = string.IsNullOrEmpty(pathUnderRoot) ? root.transform : root.transform.Find(pathUnderRoot);
                GameObject target = t != null ? t.gameObject : root;
                Selection.activeGameObject = target;
                EditorGUIUtility.PingObject(target);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DreamPark] Could not open '{prefabPath}' in Prefab Mode: {e.Message}");
            }
        }
    }
}
#endif
