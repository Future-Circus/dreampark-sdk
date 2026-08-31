#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Linq;
using Defective.JSON;
using UnityEditor;
using UnityEngine;

namespace DreamPark.EditorTools.AssetBrowser
{
    /// <summary>
    /// What a downloaded model remembers about where it came from.
    /// </summary>
    [Serializable]
    public class DreamieProvenanceRecord
    {
        public int v = 1;
        public string assetId;
        public string jobId;
        public string name;
        public string creatorId;
        public string creatorName;
        /// <summary>
        /// The GLB companion's URL. THE REASON THIS WHOLE CLASS EXISTS: Make
        /// Item needs a .glb to fill an item's modelUri, Unity ships no glTF
        /// exporter, and the library already has one sitting next to the FBX
        /// we imported. Recording the URL lets the backend copy it
        /// server-side, so the geometry never has to make a round trip
        /// through the editor at all.
        /// </summary>
        public string glbUrl;
        public string fbxUrl;
        public string downloadedUtc;
        public string sdkVersion;
        public string importProfile;
        public string[] tags;
    }

    /// <summary>
    /// Where a Dreamie-sourced model records its origin, and how anything
    /// downstream asks about it.
    ///
    /// ── WHY AssetImporter.userData AND NOT A SIDECAR FILE ──────────────────
    ///
    /// The obvious design is a JSON file next to the .fbx. It is wrong here,
    /// and specifically wrong because of ThirdPartySyncTool: on Compile &amp;
    /// Upload it walks ThirdPartyLocal/, finds every file referenced from a
    /// shipping prefab, and MoveAsset()s it to ThirdParty/. The .fbx moves.
    /// An unreferenced sidecar does not. So the first time a creator actually
    /// USES a downloaded mesh — the exact moment provenance starts to matter —
    /// the mesh and its record part company, silently.
    ///
    /// userData lives in the .meta, so it moves and renames with the asset for
    /// free, through MoveAsset and through a rename in the Project window. It
    /// is importer metadata, so it is editor-only and can never end up in an
    /// AssetBundle or affect a build. And it is reachable from a prefab in
    /// three hops (mesh → source path → importer), which is exactly the query
    /// Make Item needs to run.
    ///
    /// A ScriptableObject was the other candidate and is a trap: an .asset
    /// under Assets/Content/ gets an Addressables address and a group from
    /// ContentProcessor, AND it would hold a reference to the model, dragging
    /// the FBX into a bundle as a dependency even when nothing else uses it.
    ///
    /// ── THE ONE RISK, AND THE MITIGATION ───────────────────────────────────
    ///
    /// userData is a single unnamespaced string shared by every tool in the
    /// project. Nothing in the DreamPark SDK writes it today, but a
    /// third-party importer could. So it is always treated as a JSON OBJECT
    /// and merged under a "dreamie" key, preserving whatever else is in there.
    /// Never assign the raw string.
    /// </summary>
    public static class DreamieProvenance
    {
        /// <summary>
        /// Asset label, so "everything from the library" is one FindAssets
        /// call rather than opening every .meta in the project. Also lives in
        /// the .meta, so it survives the same moves userData does.
        /// </summary>
        public const string Label = "Dreamie";

        private const string RootKey = "dreamie";

        /* ── read / write ──────────────────────────────────────────────── */

        public static void Write(string modelAssetPath, DreamieProvenanceRecord rec)
        {
            if (string.IsNullOrEmpty(modelAssetPath) || rec == null) return;
            var importer = AssetImporter.GetAtPath(modelAssetPath);
            if (importer == null)
            {
                Debug.LogWarning($"[Dreamie] No importer at {modelAssetPath} — provenance not written.");
                return;
            }

            /* ── merge, never overwrite ────────────────────────────────────
             *
             * The obvious guard here is a try/catch around the parse, and it
             * would be useless: Defective.JSON's string constructor does NOT
             * throw on malformed input — it logs and hands back a Type.Null
             * object. So foreign, non-JSON userData has to be detected by
             * CHECKING THE TYPE, or it sails through and gets replaced
             * wholesale, which is precisely the outcome this whole scheme
             * exists to prevent.
             */
            string prior = importer.userData;
            JSONObject root = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(prior)) root = new JSONObject(prior);
            }
            catch
            {
                root = null;   // belt and braces; the ctor is not supposed to throw
            }

            if (root == null || root.type != JSONObject.Type.Object)
            {
                root = new JSONObject(JSONObject.Type.Object);
                // Somebody else's string. Keep it rather than discarding it —
                // losing another tool's state silently is not recoverable.
                if (!string.IsNullOrWhiteSpace(prior)) root.AddField("_preexisting", prior);
            }

            root.SetField(RootKey, new JSONObject(JsonUtility.ToJson(rec)));
            importer.userData = root.ToString();

            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();

            // AFTER the reimport, deliberately. Writing a label flushes the
            // .meta too, and doing it first can race the userData that has not
            // been saved yet.
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(modelAssetPath);
            if (asset != null)
            {
                var labels = AssetDatabase.GetLabels(asset).ToList();
                if (!labels.Contains(Label))
                {
                    labels.Add(Label);
                    AssetDatabase.SetLabels(asset, labels.ToArray());
                }
            }
        }

        public static bool TryRead(string modelAssetPath, out DreamieProvenanceRecord rec)
        {
            rec = null;
            if (string.IsNullOrEmpty(modelAssetPath)) return false;
            var importer = AssetImporter.GetAtPath(modelAssetPath);
            if (importer == null || string.IsNullOrWhiteSpace(importer.userData)) return false;

            try
            {
                var root = new JSONObject(importer.userData);
                if (root.type != JSONObject.Type.Object) return false;
                var node = root.GetField(RootKey);
                if (node == null) return false;
                rec = JsonUtility.FromJson<DreamieProvenanceRecord>(node.ToString());
                return rec != null && !string.IsNullOrEmpty(rec.assetId);
            }
            catch
            {
                // Malformed userData is a "this asset has no provenance",
                // never an exception into a caller's loop.
                return false;
            }
        }

        public static bool Has(string modelAssetPath) => TryRead(modelAssetPath, out _);

        /* ── prefab → record ───────────────────────────────────────────────
         *
         * The Make Item entry point. Walks every renderer in the prefab,
         * resolves each mesh back to the asset that owns it, and reads the
         * record off that.
         *
         * Returns ALL distinct records rather than the first one. A prefab
         * built from three Dreamie meshes has three sources and there is no
         * honest way to pick one — so the caller is handed the ambiguity to
         * resolve (or to refuse) rather than having it papered over here.
         * ─────────────────────────────────────────────────────────────── */

        public static List<DreamieProvenanceRecord> ReadFromPrefab(GameObject prefab)
        {
            var found = new List<DreamieProvenanceRecord>();
            if (prefab == null) return found;

            var seenPaths = new HashSet<string>();
            foreach (var path in SourceModelPaths(prefab))
            {
                if (!seenPaths.Add(path)) continue;
                if (TryRead(path, out var rec)) found.Add(rec);
            }
            return found;
        }

        public static List<DreamieProvenanceRecord> ReadFromPrefab(string prefabAssetPath)
        {
            return ReadFromPrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath));
        }

        /// <summary>
        /// The single source, when a prefab has exactly one. False for a
        /// prefab with none AND for a prefab with several — the caller wants
        /// to say something different in each case, so use ReadFromPrefab when
        /// you need to tell them apart.
        /// </summary>
        public static bool TryReadFromPrefab(GameObject prefab, out DreamieProvenanceRecord rec)
        {
            var all = ReadFromPrefab(prefab);
            rec = all.Count == 1 ? all[0] : null;
            return rec != null;
        }

        /// <summary>
        /// Every asset path this prefab's meshes come from, Dreamie or not.
        /// Includes inactive children — a pickup that spawns disabled is still
        /// made of something.
        /// </summary>
        public static IEnumerable<string> SourceModelPaths(GameObject prefab)
        {
            if (prefab == null) yield break;

            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                string p = PathOf(mf != null ? mf.sharedMesh : null);
                if (p != null) yield return p;
            }
            foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                string p = PathOf(smr != null ? smr.sharedMesh : null);
                if (p != null) yield return p;
            }
        }

        private static string PathOf(Mesh mesh)
        {
            if (mesh == null) return null;
            string path = AssetDatabase.GetAssetPath(mesh);
            // A mesh built at runtime, or one of Unity's built-in primitives
            // (which report a path inside the editor resources bundle), owns
            // no importer. Neither is a source.
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/")) return null;
            return path;
        }

        /* ── discovery ─────────────────────────────────────────────────── */

        /// <summary>
        /// Every Dreamie-sourced model in the project, via the label. Used for
        /// the "already imported" state and to rebuild the folder index when
        /// it goes missing.
        /// </summary>
        public static IEnumerable<KeyValuePair<string, DreamieProvenanceRecord>> All()
        {
            foreach (var guid in AssetDatabase.FindAssets("l:" + Label))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (TryRead(path, out var rec))
                    yield return new KeyValuePair<string, DreamieProvenanceRecord>(path, rec);
            }
        }
    }
}
#endif
