// ─────────────────────────────────────────────────────────────────────
//  ParkSimContent.cs — what the simulator is going to spawn
//
//  Three jobs, and the second one is the subtle one.
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
//  ADOPT. Content that a scan can never find because it does not exist on
//  disk — an attraction resolved out of a downloaded Addressables catalog,
//  handed over by a host tool through ParkSimExternalContent. Those come
//  in as PINNED entries, so tapping an attraction in a content browser and
//  pressing Regenerate shows you the same attraction somewhere new rather
//  than losing it to the shuffle.
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
        ///
        /// Also true for externally injected content, which is pinned for the
        /// same reason: somebody asked for it by name.
        public bool fromScene;
        /// Sourced from the bundled Sample project rather than the creator's
        /// own content. Surfaced so a spawned attraction can never be mistaken
        /// for something they wrote.
        public bool fromSample;
        public string assetPath;

        /// Handed over by a host tool rather than found on disk — see
        /// ParkSimExternalContent. Carries the ticket id so the overlay can
        /// take it back out again.
        public bool external;
        public string externalId;
        public string externalOrigin;

        /// The content folder this came from — Sample, YOUR_GAME_HERE, or
        /// whatever the creator renamed theirs to. For a PLAYER entry this is
        /// the identity that matters: rigs are keyed by gameId and there is
        /// one per content package, not one per park.
        public string contentFolder;

        public GameObject Source { get { return sceneTemplate != null ? sceneTemplate : prefabAsset; } }
    }

    public class ScanResult
    {
        public readonly List<ContentEntry> placeables = new List<ContentEntry>();

        /// ONE RIG PER CONTENT PACKAGE, not one per park.
        ///
        /// PlayerRig.instances is a Dictionary&lt;gameId, PlayerRig&gt; and
        /// GameArea.Enter() looks the rig up by the zone's gameId to decide
        /// which one to Show(). A park holding Sample attractions AND the
        /// creator's own therefore needs BOTH rigs present — the globals that
        /// live on each Player.prefab only exist if its rig was spawned, and a
        /// zone whose gameId has no rig never gets claimed at all.
        ///
        /// Spawning a single rig looked fine right up until you walked into an
        /// attraction from the other package, which is exactly the case the
        /// simulator exists to reproduce.
        public readonly List<ContentEntry> players = new List<ContentEntry>();

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
                // A PlayerRig nested inside an attraction belongs to that
                // attraction and is not the park's player. The OVR/XR camera
                // rig never carries PlayerRig at all, so it is out of scope
                // here by construction — which matters, because moving or
                // deleting it would break the simulation outright.
                if (rig.transform.parent != null &&
                    rig.transform.parent.GetComponentInParent<LevelTemplate>(true) != null) continue;

                // No early break: a scene can legitimately hold one rig per
                // content package and every one of them has to come along.
                AddPlayer(result, BuildEntry(rig.gameObject, ContentKind.Player, claimedAssets, sceneOriginAssets));
            }

            // ── Project scan ─────────────────────────────────────────────
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (claimedAssets.Contains(path)) continue;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                ContentKind kind;
                if (prefab.GetComponent<LevelTemplate>() != null) kind = ContentKind.Attraction;
                else if (includeProps && prefab.GetComponent<PropTemplate>() != null) kind = ContentKind.Prop;
                else if (prefab.GetComponent<PlayerRig>() != null) kind = ContentKind.Player;
                else continue;

                // Filter applied AFTER the kind is known: excluding Sample is a
                // choice about what gets PLACED, and dropping the Sample rig
                // with it stranded every Sample zone in the park with no rig to
                // claim it. A rig for content that never got placed costs one
                // hidden GameObject; a missing one costs the zone swap.
                if (!ParkSimSettings.IncludeSample && ContentFolders.IsUnderSample(path)
                    && kind != ContentKind.Player) {
                    sampleSkipped++;
                    continue;
                }

                var entry = new ContentEntry {
                    displayName = prefab.name,
                    kind = kind,
                    prefabAsset = prefab,
                    assetPath = path,
                    hasUnappliedOverrides = false,
                };

                entry.fromSample = ContentFolders.IsUnderSample(path);
                entry.fromScene = sceneOriginAssets.Contains(path);
                entry.contentFolder = ContentFolders.FolderOfAsset(path);

                if (kind == ContentKind.Player) {
                    AddPlayer(result, entry);
                } else {
                    result.placeables.Add(entry);
                }
            }

            // ── Injected content ─────────────────────────────────────────
            AppendExternal(result, includeProps);

            if (result.players.Count == 0) {
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

        /// <summary>
        /// Fold in whatever a host tool handed us. Resolution happens HERE,
        /// once per generation, rather than at the moment the ticket was
        /// created: a catalog can be remounted or a version swapped in
        /// between, and a ticket that re-asks survives that where a stored
        /// GameObject would be pointing at a released asset.
        ///
        /// THE PREFAB DECIDES THE KIND, not the caller. A host tool
        /// classifying from a catalog key path ("…/Levels/…", "…/Props/…") is
        /// reading a naming convention; the components on the resolved prefab
        /// are the actual answer, and getting it wrong would place an
        /// attraction as a prop — clustered beside a host and feeding
        /// GapFiller a height it has no business contributing.
        /// </summary>
        private static void AppendExternal(ScanResult result, bool includeProps)
        {
            foreach (var ticket in ParkSimExternalContent.Tickets)
            {
                if (ticket == null || ticket.resolve == null) continue;

                GameObject prefab = null;
                try {
                    prefab = ticket.resolve();
                } catch (System.Exception e) {
                    result.notes.Add(ticket.displayName + " could not be resolved: " + e.Message);
                    continue;
                }

                if (prefab == null) {
                    result.notes.Add(
                        ticket.displayName + " is still listed but its prefab did not resolve this " +
                        "generation — its catalog is probably no longer mounted. Tap it again, or " +
                        "remove it from the Park Sim panel.");
                    continue;
                }

                ContentKind kind;
                if (prefab.GetComponent<LevelTemplate>() != null) kind = ContentKind.Attraction;
                else if (prefab.GetComponent<PropTemplate>() != null) kind = ContentKind.Prop;
                else if (prefab.GetComponent<PlayerRig>() != null) kind = ContentKind.Player;
                else {
                    kind = ticket.declaredKind;
                    result.notes.Add(
                        ticket.displayName + " carries no LevelTemplate, PropTemplate or PlayerRig, so " +
                        "it will be placed as plain geometry — no floor, no navmesh, no calibration. " +
                        "That is what it would do in a real park too.");
                }

                if (kind == ContentKind.Prop && !includeProps) continue;

                var entry = new ContentEntry {
                    displayName = ticket.displayName,
                    kind = kind,
                    prefabAsset = prefab,
                    // No asset path: this prefab lives in a bundle, not on
                    // disk. The floor cache and the viewpoint restore both fall
                    // back to the display name, which is stable for a ticket.
                    assetPath = null,
                    hasUnappliedOverrides = false,
                    fromSample = false,
                    fromScene = true,
                    external = true,
                    externalId = ticket.id,
                    externalOrigin = ticket.origin,
                };

                if (kind == ContentKind.Player) {
                    // An injected player is an explicit request and outranks
                    // whatever the scan found for the same content package.
                    AddPlayer(result, entry, replaceExisting: true);
                } else {
                    result.placeables.Add(entry);
                }
            }
        }

        /// <summary>
        /// Record a rig for its content package. First one in wins — the scene
        /// sweep runs before the project sweep, so a rig the creator has in
        /// front of them beats the asset it came from, exactly like an
        /// attraction. `replaceExisting` is for injected content, which is an
        /// explicit request rather than something the scan stumbled on.
        /// </summary>
        private static void AddPlayer(ScanResult result, ContentEntry entry, bool replaceExisting = false)
        {
            if (entry == null) return;

            string key = entry.contentFolder ?? string.Empty;
            for (int i = 0; i < result.players.Count; i++) {
                if ((result.players[i].contentFolder ?? string.Empty) != key) continue;
                if (replaceExisting) result.players[i] = entry;
                return;
            }
            result.players.Add(entry);
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

            // Nor anything a park source built. Those objects loaded through
            // the shipping path, are already registered with the optimizer, and
            // in a real park carry floor data baked at the venue — respawning
            // them here would place a second copy on a marker and calibrate it
            // against ground that is not theirs.
            if (go.GetComponentInParent<ParkSimRoot>(true) != null) return null;

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
            string contentFolder = ContentFolders.FolderOfAsset(assetPath);
            if (string.IsNullOrEmpty(contentFolder) && kind == ContentKind.Player) {
                // A bare scene rig with no asset behind it still has an
                // identity: the gameId it registers itself under.
                var rig = go.GetComponent<PlayerRig>();
                if (rig != null) contentFolder = rig.gameId ?? string.Empty;
            }

            if (fromSample && !ParkSimSettings.IncludeSample && kind != ContentKind.Player) {
                // Returned BEFORE the clean-instance branch below, so a Sample
                // instance the creator has excluded is left in their scene
                // rather than deleted out of it.
                //
                // The PLAYER is exempt. "Include Sample Content" is a choice
                // about what gets PLACED in the park; the player is not placed
                // content, it is the thing doing the looking. Excluding it took
                // the rig out of the run entirely — every global that lives on
                // Player.prefab silently went missing — which is never what
                // unticking that box was meant to mean.
                return null;
            }

            bool dirty = isInstance && HasUnappliedOverrides(go);

            // The player rig in the scene is ALWAYS the spawn source, clean or
            // not — same treatment a scene attraction gets: suspended in place,
            // duplicated into the park. Deferring it to the project scan (what
            // the clean-instance branch below does) had two failure modes that
            // both ended with no rig in the park at all: the asset could be
            // filtered out of that scan, and a project with the player only in
            // the scene has no asset for it to find. Core spawns the rig first
            // and everything binding to a Player.prefab global depends on it,
            // so "usually resolves" is not good enough here.
            if (kind == ContentKind.Player) {
                if (!string.IsNullOrEmpty(assetPath)) claimedAssets.Add(assetPath);

                return new ContentEntry {
                    displayName = go.name,
                    kind = kind,
                    sceneTemplate = go,
                    prefabAsset = asset,
                    assetPath = assetPath,
                    hasUnappliedOverrides = dirty,
                    fromSample = fromSample,
                    fromScene = true,
                    contentFolder = contentFolder,
                };
            }

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
                    contentFolder = contentFolder,
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
