// ─────────────────────────────────────────────────────────────────────
//  ParkSimContent.cs — what the simulator is going to spawn
//
//  Two jobs, and the second one is the subtle one.
//
//  SCAN. Every prefab in the project whose ROOT carries LevelTemplate
//  (which catches AttractionTemplate through inheritance), PropTemplate,
//  or PlayerRig. Root-only on purpose, matching ContentProcessor's own
//  discovery: a LevelTemplate nested somewhere inside another prefab is a
//  composition detail, not a separately placeable attraction.
//
//  TRIAGE. The scene the developer is working in usually already contains
//  the attraction they are building, and it usually has edits they have not
//  pressed Apply on yet. Deleting it and respawning from the asset would
//  silently test a DIFFERENT build than the one on their screen — they
//  would fix a bug, watch it not go away, and have no way to see why. So:
//
//    instance with unapplied overrides -> keep it as the spawn source,
//                                         disabled in place, duplicated into
//                                         the park, and FLAGGED in-world so
//                                         the difference is visible
//    instance with none                -> delete; the prefab asset is
//                                         identical to it by definition, and
//                                         spawning from the asset keeps one
//                                         code path
//
//  THE ROOT TRANSFORM IS NOT AN OVERRIDE. Every placed prefab instance
//  carries m_LocalPosition/m_LocalRotation/m_LocalScale/m_Name/m_RootOrder
//  modifications against its asset — that is simply what "I dragged this
//  into a scene" looks like. Counting those would flag literally every
//  instance as dirty and the warning would mean nothing. They are filtered
//  against the ASSET's transform, because PropertyModification.target
//  points at the source object, not the instance.
// ─────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DreamPark.ParkSim
{
    public enum ContentKind { Attraction, Prop, Player }

    public class ContentEntry
    {
        public string displayName;
        public ContentKind kind;

        /// The prefab asset to instantiate. Null when this entry is sourced
        /// from a scene instance that carries unapplied overrides.
        public GameObject prefabAsset;

        /// A disabled scene instance to duplicate instead of the asset,
        /// preserving the developer's un-applied edits. Null otherwise.
        public GameObject sceneTemplate;

        public bool hasUnappliedOverrides;

        /// This content was present in the creator's scene when they pressed
        /// Play. Those are PINNED — placed in every generation, never rotated
        /// out — because the attraction you are working on has to be in the
        /// park you are looking at, every single time.
        ///
        /// True whether the scene object was the spawn source (unapplied
        /// overrides, or a bare scene object) or was a clean instance that the
        /// park respawns from its asset. Both were on screen when Play was
        /// pressed, which is the only thing that matters here.
        public bool fromScene;
        /// Sourced from the bundled Sample project rather than the creator's
        /// own content. Surfaced so a spawned attraction can never be mistaken
        /// for something they wrote.
        public bool fromSample;
        public string assetPath;

        public GameObject Source { get { return sceneTemplate != null ? sceneTemplate : prefabAsset; } }
    }

    public class ScanResult
    {
        public readonly List<ContentEntry> placeables = new List<ContentEntry>();
        public ContentEntry player;
        public readonly List<string> notes = new List<string>();

        public int AttractionCount
        {
            get { int n = 0; foreach (var e in placeables) if (e.kind == ContentKind.Attraction) n++; return n; }
        }
        public int PropCount
        {
            get { int n = 0; foreach (var e in placeables) if (e.kind == ContentKind.Prop) n++; return n; }
        }
        public int DirtyCount
        {
            get { int n = 0; foreach (var e in placeables) if (e.hasUnappliedOverrides) n++; return n; }
        }
    }

    public static class ParkSimContent
    {
        /// <summary>
        /// Scan the project and triage the open scene. Runs in play mode:
        /// PrefabUtility operates on the loaded scene graph, and entering play
        /// mode does not sever instance links, so override queries are valid
        /// here. Anything that comes back null is treated as "clean" rather
        /// than guessed at — a false clean costs a respawn from the asset, a
        /// false dirty would hide the asset from the run entirely.
        /// </summary>
        public static ScanResult Scan(bool includeProps)
        {
            var result = new ScanResult();
            int sampleSkipped = 0;

            // ── Triage the scene first ───────────────────────────────────
            // Whatever it claims takes precedence, so the project scan below
            // can skip assets the scene is already speaking for.
            var claimedAssets = new HashSet<string>();

            // Assets whose CLEAN scene instance we suspended. The project scan
            // below spawns these from the asset, and this is the only way that
            // entry can learn it was on screen when Play was pressed.
            var sceneOriginAssets = new HashSet<string>();

            foreach (var lt in Object.FindObjectsByType<LevelTemplate>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                TriageSceneInstance(lt.gameObject, ContentKind.Attraction, result, claimedAssets, sceneOriginAssets);
            }

            if (includeProps)
            {
                foreach (var pt in Object.FindObjectsByType<PropTemplate>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    // A prop nested under an attraction belongs to that
                    // attraction and must not be placed independently — same
                    // rule LevelObjectManager.RegisterLevelObject uses via
                    // PropTemplate.IsNestedUnderTemplate.
                    if (pt.transform.parent != null &&
                        pt.transform.parent.GetComponentInParent<LevelTemplate>(true) != null) continue;

                    TriageSceneInstance(pt.gameObject, ContentKind.Prop, result, claimedAssets, sceneOriginAssets);
                }
            }

            foreach (var rig in Object.FindObjectsByType<PlayerRig>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (result.player != null) break;

                // A PlayerRig nested inside an attraction belongs to that
                // attraction and is not the park's player. The OVR/XR camera
                // rig never carries PlayerRig at all, so it is out of scope
                // here by construction — which matters, because moving or
                // deleting it would break the simulation outright.
                if (rig.transform.parent != null &&
                    rig.transform.parent.GetComponentInParent<LevelTemplate>(true) != null) continue;

                result.player = BuildEntry(rig.gameObject, ContentKind.Player, claimedAssets, sceneOriginAssets);
            }

            // ── Project scan ─────────────────────────────────────────────
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (claimedAssets.Contains(path)) continue;

                if (!ParkSimSettings.IncludeSample && ContentFolders.IsUnderSample(path)) {
                    sampleSkipped++;
                    continue;
                }

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                ContentKind kind;
                if (prefab.GetComponent<LevelTemplate>() != null) kind = ContentKind.Attraction;
                else if (includeProps && prefab.GetComponent<PropTemplate>() != null) kind = ContentKind.Prop;
                else if (prefab.GetComponent<PlayerRig>() != null) kind = ContentKind.Player;
                else continue;

                var entry = new ContentEntry {
                    displayName = prefab.name,
                    kind = kind,
                    prefabAsset = prefab,
                    assetPath = path,
                    hasUnappliedOverrides = false,
                };

                entry.fromSample = ContentFolders.IsUnderSample(path);
                entry.fromScene = sceneOriginAssets.Contains(path);

                if (kind == ContentKind.Player) {
                    if (result.player == null) result.player = entry;
                } else {
                    result.placeables.Add(entry);
                }
            }

            if (result.player == null) {
                result.notes.Add(
                    "No Player prefab found (nothing in the project or scene carries PlayerRig). " +
                    "Global systems that live on Player.prefab will be absent, so score, audio and " +
                    "anything else bound through them will silently no-op.");
            }
            if (sampleSkipped > 0) {
                result.notes.Add(
                    sampleSkipped + " Sample prefab(s) left out — turn on " +
                    "Park Simulator > Include Sample Content to place them.");
            }
            if (result.placeables.Count == 0) {
                result.notes.Add("No attractions or props found — nothing to place in the park.");
            }

            return result;
        }

        private static void TriageSceneInstance(
            GameObject go, ContentKind kind, ScanResult result,
            HashSet<string> claimedAssets, HashSet<string> sceneOriginAssets)
        {
            // Only the outermost instance root. A LevelTemplate sitting on a
            // nested child is part of a bigger thing and is not placed alone.
            if (go.transform.parent != null &&
                go.transform.parent.GetComponentInParent<LevelTemplate>(true) != null) return;

            var entry = BuildEntry(go, kind, claimedAssets, sceneOriginAssets);
            if (entry != null) result.placeables.Add(entry);
        }

        private static ContentEntry BuildEntry(
            GameObject go, ContentKind kind,
            HashSet<string> claimedAssets, HashSet<string> sceneOriginAssets)
        {
            // NEVER touch the Simulator. It owns Camera.main — it copies the
            // Scene view's pose onto it every frame — and GlobalSceneKeyHandler
            // calls FindFirstObjectByType<Simulator>().ReceiveInput() with no
            // null check, so disabling or deleting the object carrying it would
            // both freeze the guest and make pressing Space in the Scene view
            // throw. Resources/Prefabs/Simulator carries nothing else today, so
            // this cannot currently fire; it is here so that adding a
            // PropTemplate to it later fails safe instead of silently killing
            // the camera.
            if (go.GetComponentInParent<Simulator>(true) != null) return null;

            bool isInstance = PrefabUtility.IsPartOfPrefabInstance(go);
            string assetPath = null;
            GameObject asset = null;

            if (isInstance) {
                var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
                if (root != null) {
                    assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
                    if (!string.IsNullOrEmpty(assetPath))
                        asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                }
            }

            bool fromSample = ContentFolders.IsUnderSample(assetPath);
            if (fromSample && !ParkSimSettings.IncludeSample) {
                // Returned BEFORE the clean-instance branch below, so a Sample
                // instance the creator has excluded is left in their scene
                // rather than deleted out of it.
                return null;
            }

            bool dirty = isInstance && HasUnappliedOverrides(go);

            if (dirty || !isInstance) {
                // Either the developer has un-applied edits, or this is a bare
                // scene object with no asset behind it at all. Both cases mean
                // the SCENE is the only place this content exists in the form
                // being tested, so the scene object is the spawn source.
                if (!string.IsNullOrEmpty(assetPath)) claimedAssets.Add(assetPath);

                return new ContentEntry {
                    displayName = go.name,
                    kind = kind,
                    sceneTemplate = go,
                    prefabAsset = asset,
                    assetPath = assetPath,
                    // A bare scene object is not "unapplied overrides" — there
                    // is no prefab for it to differ from. Do not warn about it.
                    hasUnappliedOverrides = dirty,
                    fromSample = fromSample,
                    fromScene = true,
                };
            }

            // Clean instance: identical to its asset by definition. Let the
            // project scan pick the asset up and SUSPEND the instance, so there
            // is exactly one spawn code path.
            //
            // Suspended, never destroyed — Stop has to be able to hand the
            // creator's scene back exactly as they left it, and a destroyed
            // object cannot be handed back.
            if (!string.IsNullOrEmpty(assetPath)) sceneOriginAssets.Add(assetPath);
            ParkSimulator.MarkForSuspension(go);
            return null;
        }

        /// <summary>
        /// True when the instance differs from its asset in any way a developer
        /// would need to press Apply for. Placement of the instance root — its
        /// position, rotation, scale, name and sibling order — is explicitly
        /// not such a difference.
        /// </summary>
        public static bool HasUnappliedOverrides(GameObject go)
        {
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (root == null) return false;

            if (PrefabUtility.GetAddedComponents(root).Count > 0) return true;
            if (PrefabUtility.GetAddedGameObjects(root).Count > 0) return true;
            if (PrefabUtility.GetRemovedComponents(root).Count > 0) return true;
#if UNITY_2022_2_OR_NEWER
            if (PrefabUtility.GetRemovedGameObjects(root).Count > 0) return true;
#endif

            var mods = PrefabUtility.GetPropertyModifications(root);
            if (mods == null) return false;

            // PropertyModification.target points at the ASSET object, so the
            // instance's own transform has to be resolved back to its source
            // to be recognised.
            var assetTransform = PrefabUtility.GetCorrespondingObjectFromSource(root.transform) as Object;
            var assetGameObject = PrefabUtility.GetCorrespondingObjectFromSource(root) as Object;

            foreach (var m in mods) {
                if (m == null || m.target == null) continue;
                if (IsRootPlacement(m, assetTransform, assetGameObject)) continue;
                return true;
            }
            return false;
        }

        private static bool IsRootPlacement(
            PropertyModification m, Object assetTransform, Object assetGameObject)
        {
            string p = m.propertyPath;

            if (assetTransform != null && m.target == assetTransform) {
                if (p.StartsWith("m_LocalPosition")) return true;
                if (p.StartsWith("m_LocalRotation")) return true;
                if (p.StartsWith("m_LocalScale")) return true;
                if (p.StartsWith("m_LocalEulerAnglesHint")) return true;
                if (p == "m_RootOrder") return true;
            }

            if (assetGameObject != null && m.target == assetGameObject) {
                // Unity records the instance name against the asset's
                // GameObject. Renaming a dragged-in instance is housekeeping,
                // not an un-applied edit to the attraction.
                if (p == "m_Name") return true;
            }

            return false;
        }
    }
}
