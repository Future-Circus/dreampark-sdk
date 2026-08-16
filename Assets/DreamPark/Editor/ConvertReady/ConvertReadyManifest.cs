// ─────────────────────────────────────────────────────────────────────
//  ConvertReadyManifest.cs — what the converter did to each prop, kept
//  where it cannot ship.
//
//  WHY THIS IS A ScriptableObject AND NOT A MARKER COMPONENT
//
//  The obvious design is a [ConvertedByDreamPark] MonoBehaviour on the prop
//  root holding the plan. That design has already been tried in this codebase
//  and it failed in production.
//
//  InteractiveInflatableGenerator was a runtime MonoBehaviour that only ever
//  did authoring work — it walked a bone chain in OnValidate and serialized
//  colliders, rigidbodies and joints into the prefab. Nothing of it ran in a
//  build. It rode along on E_Arch, E_FlipGate and E_Pylon into the core app,
//  which has no CrashCourse assembly, and landed as a missing-script
//  reference on three live prefabs. See InflatableRigBaker.cs, which exists
//  because of that.
//
//  A converter marker component reproduces it exactly: content-authored,
//  authoring-only, riding into an app that has never heard of it. So the rule
//  is absolute — anything the converter leaves behind is either a real runtime
//  component in the SDK assembly, or it is not a component at all.
//
//  This is the "not a component at all" half:
//
//   • ScriptableObject, editor-only, under an Editor/ folder so
//     ContentProcessor's ShouldSkipAsset never sees it and no bundle can
//     contain it.
//   • Keyed by prefab GUID. A path is not an identity — it changes on the
//     first rename or folder move, and a path-keyed manifest silently
//     re-converts the same prop into a duplicate.
//   • Child references are hierarchy path strings ("Anchor/Motion/Visual").
//     A serialized Transform reference cannot point into a prefab from
//     outside it; it comes back null and the re-bake fits a collider to
//     nothing.
//
//  THE PLAN IS STORED AS JSON, AND ONE THING DOES NOT SURVIVE THE TRIP.
//  ConversionPlan is [Serializable], but a UnityEngine.Object field inside a
//  JsonUtility round-trip comes back NULL — the same caveat ConversionPlan's
//  own Clone() carries. The only such field today is
//  FractureSettings.insideMaterial, so its GUID is stored alongside the JSON
//  and re-attached in DecodePlan. Any future Object reference added to the
//  plan needs the same treatment or it will silently vanish on a re-bake.
//
//  The second-order payoff is the one that pays later: with the plan on disk,
//  changing one number and re-baking every prefab that used that preset is a
//  loop over this list. InflatableRigBaker already has that button, and it is
//  the reason its -0.08 collider spacing still exists.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace DreamPark.ConvertReady
{
    /// <summary>
    /// One converted prop. Plain serializable fields only — this is inspected
    /// by hand often enough that it has to stay readable in the inspector.
    /// </summary>
    [Serializable]
    public class ConvertReadyEntry
    {
        /// Identity. Survives rename and move; a path does not.
        public string prefabGuid;

        /// Last known path. Diagnostics only — always resolve through the GUID.
        public string prefabPath;

        /// The model / texture / audio clip the prop was built from, so a
        /// re-run can re-import the source rather than re-deriving from the
        /// prefab's own geometry.
        public string sourceGuid;
        public string sourcePath;

        public string presetName;

        /// JsonUtility.ToJson(ConversionPlan). Read it through DecodePlan().
        public string planJson;

        /// FractureSettings.insideMaterial, which does not survive the JSON
        /// round-trip. See the file header.
        public string insideMaterialGuid;

        // Hierarchy paths relative to the prop root. Empty means "that node
        // did not exist" — which is a real answer for motionPath, since the
        // Motion node is omitted entirely when nothing animates.
        public string anchorPath;
        public string motionPath;
        public string visualPath;

        /// Round-trip format version, so a future field rename can migrate
        /// instead of silently reading zeros.
        public int version = ConvertReadyManifest.CurrentVersion;

        /// Invariant-culture round-trip ("o"). DateTime is not serializable by
        /// JsonUtility or by Unity's own serializer, hence strings.
        public string firstConvertedUtc;
        public string lastConvertedUtc;

        /// <summary>
        /// The plan as recorded, with UnityEngine.Object references
        /// re-attached. Returns null when nothing was recorded, so callers can
        /// tell "converted with defaults" apart from "never converted".
        /// </summary>
        public ConversionPlan DecodePlan()
        {
            if (string.IsNullOrEmpty(planJson)) return null;

            ConversionPlan plan;
            try
            {
                plan = JsonUtility.FromJson<ConversionPlan>(planJson);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Convert Ready] Could not read the recorded plan for "
                                 + (prefabPath ?? prefabGuid) + " — " + e.Message);
                return null;
            }
            if (plan == null) return null;

            if (plan.fracture != null && !string.IsNullOrEmpty(insideMaterialGuid))
            {
                string path = AssetDatabase.GUIDToAssetPath(insideMaterialGuid);
                if (!string.IsNullOrEmpty(path))
                    plan.fracture.insideMaterial = AssetDatabase.LoadAssetAtPath<Material>(path);
            }

            return plan;
        }

        /// <summary>The prefab's path today, or empty when it has been deleted.</summary>
        public string ResolvedPath()
        {
            return string.IsNullOrEmpty(prefabGuid) ? string.Empty : AssetDatabase.GUIDToAssetPath(prefabGuid);
        }

        /// <summary>The prefab asset, or null when it no longer exists.</summary>
        public GameObject LoadPrefab()
        {
            string path = ResolvedPath();
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        public bool Exists()
        {
            return !string.IsNullOrEmpty(ResolvedPath());
        }
    }

    public class ConvertReadyManifest : ScriptableObject
    {
        public const int CurrentVersion = 1;

        /// Under Editor/ on purpose: ContentProcessor's ShouldSkipAsset
        /// excludes Editor folders, so this can never acquire an Addressables
        /// address or end up in a bundle.
        public const string FolderPath = "Assets/DreamPark/Editor/ConvertReady";
        public const string AssetPath = FolderPath + "/ConvertReadyManifest.asset";

        public List<ConvertReadyEntry> entries = new List<ConvertReadyEntry>();

        // ── Loading ─────────────────────────────────────────────────────

        /// <summary>
        /// The manifest, creating it on first use. Never returns null: if the
        /// asset cannot be written — a read-only checkout, an import still in
        /// flight — this hands back a throwaway in-memory instance so a
        /// conversion still completes. The prefab is the deliverable; the
        /// manifest is bookkeeping.
        /// </summary>
        public static ConvertReadyManifest LoadOrCreate()
        {
            var existing = AssetDatabase.LoadAssetAtPath<ConvertReadyManifest>(AssetPath);
            if (existing != null) return existing;

            // Someone may have moved it. Find it before creating a second one:
            // two manifests means half the props look unconverted, and the
            // re-run duplicates them.
            //
            // But adopt it WHERE IT IS only while it is still editor-only. The
            // whole premise of this asset is that ContentProcessor's
            // ShouldSkipAsset skips anything under an Editor/ folder, so it can
            // never acquire an address or enter a bundle. Dragged into
            // Assets/Content/MyGame it gets both — and its script is compiled
            // out of the player, because this class lives inside #if
            // UNITY_EDITOR. That is the missing-script-in-a-bundle failure in
            // the file header, reproduced by the tool that exists to prevent it.
            // So: move it back, and only give up on the move. Giving up costs a
            // fresh manifest and the recorded history with it — which is the
            // cheaper of the two failures, because the other one ships.
            string[] found = AssetDatabase.FindAssets("t:" + typeof(ConvertReadyManifest).Name);
            for (int i = 0; i < found.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(found[i]);
                if (string.IsNullOrEmpty(path)) continue;

                if (!IsEditorOnlyPath(path))
                {
                    string relocated = TryMoveBack(path);
                    if (string.IsNullOrEmpty(relocated)) continue;   // warned already; do not adopt it in place
                    path = relocated;
                }

                var moved = AssetDatabase.LoadAssetAtPath<ConvertReadyManifest>(path);
                if (moved != null) return moved;
            }

            var created = CreateInstance<ConvertReadyManifest>();
            try
            {
                if (!AssetClassifier.EnsureFolder(FolderPath) || !AssetClassifier.CommitFolder(FolderPath))
                    throw new Exception("could not create " + FolderPath);

                AssetDatabase.CreateAsset(created, AssetPath);
                AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.SaveAssets();
                return created;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Convert Ready] Could not create " + AssetPath + " — " + e.Message
                                 + ". Conversions will still run; they just will not be recorded, so a "
                                 + "re-run will look like a first run.");
                created.hideFlags = HideFlags.DontSave;
                return created;
            }
        }

        /// <summary>
        /// True when <paramref name="assetPath"/> sits under an Editor folder,
        /// which is the exact test ContentProcessor.ShouldSkipAsset applies —
        /// a "/Editor/" segment anywhere in the path. Keep the two in step.
        /// </summary>
        private static bool IsEditorOnlyPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;

            string norm = assetPath.Replace('\\', '/');
            return norm.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Put a stray manifest back under Editor/. Returns its new path, or
        /// empty when the move failed — in which case the caller must NOT adopt
        /// it where it is, because keeping it there is what bundles it.
        /// </summary>
        private static string TryMoveBack(string strayPath)
        {
            try
            {
                if (!AssetClassifier.EnsureFolder(FolderPath) || !AssetClassifier.CommitFolder(FolderPath))
                    throw new Exception("could not create " + FolderPath);

                // GenerateUniqueAssetPath in case something already occupies
                // AssetPath that is not a manifest — MoveAsset onto a taken
                // path fails outright, and the stray would then stay put.
                string target = AssetDatabase.GenerateUniqueAssetPath(AssetPath);

                string err = AssetDatabase.MoveAsset(strayPath, target);
                if (!string.IsNullOrEmpty(err)) throw new Exception(err);

                Debug.LogWarning("[Convert Ready] Moved " + strayPath + " back to " + target
                                 + " — outside an Editor folder it gets an Addressables address and ships in a "
                                 + "bundle, where its script does not exist in the player.");
                return target;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Convert Ready] " + strayPath + " is outside an Editor folder and would be "
                                 + "bundled as an asset whose script is editor-only. Move it back to "
                                 + FolderPath + " by hand — " + e.Message);
                return string.Empty;
            }
        }

        // ── Recording ───────────────────────────────────────────────────

        /// <summary>
        /// Record (or update) how <paramref name="prefabGuid"/> was converted.
        /// Updating in place rather than appending is the whole point: two
        /// entries for one GUID and a re-run cannot tell which one it did.
        ///
        /// Node paths are read off the prefab asset at
        /// <paramref name="prefabPath"/>. Use the overload taking a root when
        /// you already have the hierarchy in hand.
        /// </summary>
        public void Record(string prefabGuid, string prefabPath, ConversionPlan plan)
        {
            GameObject root = string.IsNullOrEmpty(prefabPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);

            Record(prefabGuid, prefabPath, plan, root, null);
        }

        /// <summary>
        /// As above, with the hierarchy and the source asset in hand.
        /// <paramref name="root"/> may be the saved asset or the in-memory
        /// root — only its child NAMES are read, never a reference to it.
        /// </summary>
        public void Record(string prefabGuid, string prefabPath, ConversionPlan plan,
                           GameObject root, string sourcePath)
        {
            if (string.IsNullOrEmpty(prefabGuid))
            {
                Debug.LogWarning("[Convert Ready] Refusing to record an entry with no prefab GUID"
                                 + (string.IsNullOrEmpty(prefabPath) ? "." : " (" + prefabPath + ")."));
                return;
            }

            ConvertReadyEntry entry;
            if (!TryGet(prefabGuid, out entry))
            {
                entry = new ConvertReadyEntry { prefabGuid = prefabGuid };
                entry.firstConvertedUtc = NowUtc();
                entries.Add(entry);
            }

            entry.version = CurrentVersion;
            entry.prefabPath = prefabPath;
            entry.lastConvertedUtc = NowUtc();

            if (!string.IsNullOrEmpty(sourcePath))
            {
                entry.sourcePath = sourcePath;
                entry.sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath);
            }

            if (plan != null)
            {
                entry.presetName = plan.presetName;
                entry.planJson = EncodePlan(plan);
                entry.insideMaterialGuid = GuidOf(plan.fracture != null ? plan.fracture.insideMaterial : null);
            }

            if (root != null)
            {
                // Recorded from the hierarchy rather than assumed from the
                // canonical names, because a creator is allowed to restructure
                // a prop after conversion and the re-bake has to find whatever
                // is actually there.
                entry.anchorPath = PropPrefabEmitter.RelativePath(root.transform, PropPrefabEmitter.FindAnchor(root));
                entry.motionPath = PropPrefabEmitter.RelativePath(root.transform, PropPrefabEmitter.FindMotion(root));
                entry.visualPath = PropPrefabEmitter.RelativePath(root.transform, PropPrefabEmitter.FindVisual(root));
            }

            MarkDirty();
        }

        // ── Reading ─────────────────────────────────────────────────────

        public bool TryGet(string prefabGuid, out ConvertReadyEntry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(prefabGuid)) return false;

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e == null) continue;
                if (string.Equals(e.prefabGuid, prefabGuid, StringComparison.Ordinal))
                {
                    entry = e;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Convenience for callers holding a path. Resolves through the GUID,
        /// so it still finds an entry recorded before the prefab was moved.
        /// </summary>
        public bool TryGetByPath(string prefabPath, out ConvertReadyEntry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(prefabPath)) return false;
            return TryGet(AssetDatabase.AssetPathToGUID(prefabPath), out entry);
        }

        /// <summary>True when this prefab has been converted before.</summary>
        public bool WasConverted(string prefabGuid)
        {
            ConvertReadyEntry ignored;
            return TryGet(prefabGuid, out ignored);
        }

        /// <summary>
        /// The recorded plan for a prefab, or null. The common call: a re-run
        /// wants last time's flags as its starting point so the dialog opens
        /// on what the creator already chose.
        /// </summary>
        public ConversionPlan PlanFor(string prefabGuid)
        {
            ConvertReadyEntry entry;
            return TryGet(prefabGuid, out entry) ? entry.DecodePlan() : null;
        }

        // ── Forgetting ──────────────────────────────────────────────────

        /// <summary>
        /// Drop an entry. Returns true when one was removed. Used when a
        /// creator asks for a clean re-convert, and by PruneMissing.
        /// </summary>
        public bool Forget(string prefabGuid)
        {
            if (string.IsNullOrEmpty(prefabGuid)) return false;

            int removed = entries.RemoveAll(e =>
                e != null && string.Equals(e.prefabGuid, prefabGuid, StringComparison.Ordinal));

            if (removed == 0) return false;

            MarkDirty();
            return true;
        }

        /// <summary>
        /// Drop entries whose prefab no longer exists. Deliberately NOT
        /// automatic on load: a GUID also fails to resolve while a branch
        /// switch or an import is mid-flight, and a manifest that self-pruned
        /// on load would quietly forget a whole content folder that is about to
        /// come back.
        /// </summary>
        public int PruneMissing()
        {
            int removed = entries.RemoveAll(e => e == null || !e.Exists());
            if (removed > 0) MarkDirty();
            return removed;
        }

        // ── Persistence ─────────────────────────────────────────────────

        /// <summary>Flush now. Records already schedule this; call it to be sure.</summary>
        public void Save()
        {
            EditorUtility.SetDirty(this);
            savePending = false;
            AssetDatabase.SaveAssets();
        }

        // A batch conversion records once per prop. AssetDatabase.SaveAssets
        // per record turns a 50-asset folder convert into 50 full asset-
        // database saves, so the flush is coalesced onto one delayCall — the
        // same shape DuplicateNamesCheck uses to coalesce its re-stamp.
        private static bool savePending;

        private void MarkDirty()
        {
            EditorUtility.SetDirty(this);

            if (hideFlags == HideFlags.DontSave) return;   // in-memory fallback; nothing to write
            if (savePending) return;

            savePending = true;
            EditorApplication.delayCall += () =>
            {
                if (!savePending) return;
                savePending = false;
                AssetDatabase.SaveAssets();
            };
        }

        // ── Internals ───────────────────────────────────────────────────

        private static string EncodePlan(ConversionPlan plan)
        {
            if (plan == null) return string.Empty;
            try
            {
                return JsonUtility.ToJson(plan);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Convert Ready] Could not serialize the plan — " + e.Message);
                return string.Empty;
            }
        }

        private static string GuidOf(UnityEngine.Object asset)
        {
            if (asset == null) return string.Empty;

            string path = AssetDatabase.GetAssetPath(asset);
            return string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path);
        }

        private static string NowUtc()
        {
            return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }
    }
}
#endif
