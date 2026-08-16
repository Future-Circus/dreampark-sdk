// ─────────────────────────────────────────────────────────────────────
//  ResourceNameAddressCheck.cs — the stored resourceName does not match the
//  address this asset actually resolves to.
//
//  WHY THIS EXISTS
//
//  ContentProcessor stamps GameArea.resourceName and PropTemplate.resourceName
//  with the asset's computed Addressables address —
//  "{gameId}/Props/{category}/{filename}" for a prop, "{gameId}/Levels/{size}/
//  {filename}" for an attraction — and its own comment says what that value is
//  for: "This is what lets the headset attribute revenue to the individual
//  attraction, not just the title."
//
//  Nothing verifies it afterwards. The stamp is an ordinary serialized string
//  on a MonoBehaviour, and there are at least three ways it goes stale without
//  a single warning anywhere:
//
//   1. PREFAB VARIANTS AND "REVERT ALL". This is the one that motivated the
//      check. On a variant, the stamp is a property OVERRIDE — every member of
//      a shared prop family legitimately shows resourceName as modified
//      relative to its base. So "Revert All" on a variant removes the override
//      and the BASE's resource key shows through: the variant now reports its
//      revenue against a different asset. Nothing errors, nothing logs, and
//      the action that caused it looks like housekeeping. It is a billing bug
//      wearing a tidy-up's clothes.
//
//   2. A prefab renamed or moved between categories after it was last stamped.
//      The address is built from PropTemplate.category and the bare file stem,
//      so changing either invalidates the stored value.
//
//   3. A prefab copy-pasted from another content package, which arrives
//      carrying the ORIGINAL package's gameId in its resourceName.
//
//  WHY WARNING AND NOT BLOCKING
//
//  Per the suite's own policy, inherited from LuaSurfaceGate: "A modal with an
//  'Upload anyway' button is a scarce resource… A check that cannot tell must
//  not block." This check cannot tell the difference between a stamp that is
//  wrong and a stamp ContentProcessor is about to rewrite on its next pass —
//  and a brand-new prefab is legitimately unstamped until then, so blocking
//  would put a modal in front of every first upload of every prop anyone ever
//  makes. Hence: a never-stamped prefab is Info (visible, never interrupts),
//  and only a stamp that disagrees with its own address is a Warning.
//
//  What a Warning here means concretely: the value on disk right now — the one
//  in git, and the one in whatever bundle was last built and uploaded — names a
//  different asset's revenue key.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DreamPark.PreUploadChecks.Checks
{
    public sealed class ResourceNameAddressCheck : IPreUploadCheck
    {
        public const string CheckId = "resource-name-address";

        // Sub-key modes. They are SEPARATE so an ignored Info can never
        // suppress a Warning for the same asset: DuplicateNamesCheck already
        // shipped that bug once — an exact-collision Blocking finding and a
        // case-only Warning shared checkId + guid + subKey, ignoring the
        // harmless one silenced the severe one, and two prefabs went out
        // sharing a revenue key.
        private const string ModeMismatch = "mismatch";
        private const string ModeUnstamped = "unstamped";

        public string Id { get { return CheckId; } }
        public string DisplayName { get { return "resourceName vs. address"; } }
        public CheckSeverity DefaultSeverity { get { return CheckSeverity.Warning; } }

        // Cheap ENOUGH, and it is worth being precise about why, because the
        // next person to add work here will read this line first.
        //
        // This is not DuplicateNamesCheck, which reads ContentRootInfo.name and
        // loads nothing. This one calls LoadAssetAtPath on every content root —
        // one prefab deserialise per root, plus the base prefab on any variant
        // for the VariantNote — so its cost is O(roots) prefab loads. That is
        // acceptable here only because the AssetDatabase caches loaded prefabs
        // and the runner caches the report, so the panel pays for it once per
        // open rather than once per repaint. No scene is loaded and no
        // dependency walk is done, and NOTHING THAT WALKS DEPENDENCIES MAY BE
        // ADDED without dropping this flag: an advisory scan runs on every
        // Content Uploader panel open, and a dependency walk over a full package
        // there is seconds of stall with no progress bar in front of it.
        public bool RunsInAdvisoryScan { get { return true; } }

        public string Rationale
        {
            get
            {
                return "ContentProcessor stamps resourceName with the asset's own Addressables address, "
                     + "and that value is what the headset attributes revenue with. On a prefab variant "
                     + "the stamp is a property override, so 'Revert All' silently repoints that "
                     + "variant's revenue at its base — and renaming, re-categorising or copying a "
                     + "prefab between packages leaves the same value stale.";
            }
        }

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            if (ctx == null || ctx.roots == null || ctx.roots.Count == 0)
                return CheckResult.Skipped(CheckId, "no content roots to check");

            var findings = new List<Finding>();

            for (int i = 0; i < ctx.roots.Count; i++)
            {
                ContentRootInfo root = ctx.roots[i];
                if (root == null) continue;

                ctx.Progress((float)i / ctx.roots.Count, "resourceName: " + root.name);

                try
                {
                    Inspect(root, ctx, findings);
                }
                catch (Exception e)
                {
                    // One unreadable prefab must not take the whole check with
                    // it — an Errored check contributes nothing at all, and the
                    // other roots are still worth reporting on.
                    Debug.LogWarning("[DreamPark] " + CheckId + " could not read "
                                     + root.assetPath + ": " + e.Message);
                }
            }

            return CheckResult.From(CheckId, findings);
        }

        // ── One root ────────────────────────────────────────────────────

        private static void Inspect(ContentRootInfo root, PreUploadCheckContext ctx,
                                    List<Finding> findings)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(root.assetPath);
            if (prefab == null) return;

            string gameId = GameIdFor(root.assetPath, ctx);
            if (string.IsNullOrEmpty(gameId)) return;

            string expected = ExpectedAddress(prefab, root.assetPath, gameId);
            if (string.IsNullOrEmpty(expected)) return;      // a PlayerRig — nothing is stamped on it

            // Read the same two components ContentProcessor writes, and only on
            // the ROOT: that is where it stamps, and a GameArea on a child is a
            // nested prop with its own address.
            var stamps = new List<Stamp>();
            var prop = prefab.GetComponent<PropTemplate>();
            if (prop != null) stamps.Add(new Stamp("PropTemplate", prop.resourceName));
            var area = prefab.GetComponent<GameArea>();
            if (area != null) stamps.Add(new Stamp("GameArea", area.resourceName));

            if (stamps.Count == 0) return;

            var wrong = new List<Stamp>();
            int empty = 0;
            foreach (Stamp s in stamps)
            {
                if (string.IsNullOrEmpty(s.value)) { empty++; continue; }
                if (!string.Equals(s.value, expected, StringComparison.Ordinal)) wrong.Add(s);
            }

            if (wrong.Count > 0)
            {
                findings.Add(BuildMismatch(root, prefab, expected, wrong, gameId));
                return;
            }

            // Every stamp that exists is either correct or blank. A blank one
            // beside a correct one is not worth a row: PropTemplate.EnsureGameArea
            // copies its resourceName onto the GameArea at runtime, and
            // ContentProcessor fills both on its next pass.
            if (empty == stamps.Count)
                findings.Add(BuildUnstamped(root, expected));
        }

        private struct Stamp
        {
            public string component;
            public string value;

            public Stamp(string component, string value)
            {
                this.component = component;
                this.value = value;
            }
        }

        // ── Findings ────────────────────────────────────────────────────

        private static Finding BuildMismatch(ContentRootInfo root, GameObject prefab, string expected,
                                             List<Stamp> wrong, string gameId)
        {
            var parts = new List<string>();
            foreach (Stamp s in wrong) parts.Add(s.component + ".resourceName = '" + s.value + "'");

            string detail =
                string.Join("; ", parts.ToArray())
                + ", but this asset resolves to '" + expected + "'. resourceName is the key the headset "
                + "attributes revenue with, so while it disagrees with the address, this prop's earnings "
                + "are recorded against whatever asset owns the stored key.";

            // The variant case is worth naming explicitly, because the action
            // that causes it does not look destructive.
            string variantNote = VariantNote(prefab, wrong[0].value, gameId);
            if (!string.IsNullOrEmpty(variantNote)) detail += "\n\n" + variantNote;

            var finding = new Finding
            {
                checkId = CheckId,
                severity = CheckSeverity.Warning,
                assetGuid = root.guid,
                assetPath = root.assetPath,
                // Keyed on the stored value: ignoring THIS wrong address should
                // not also pre-ignore a different wrong address later.
                subKey = ModeMismatch + ":" + wrong[0].value,
                title = root.KindLabel + " '" + root.name + "' — resourceName points at a different asset",
                detail = detail,
            };

            AddFixes(finding, root, expected, wrong[0].value);
            return finding;
        }

        private static Finding BuildUnstamped(ContentRootInfo root, string expected)
        {
            var finding = new Finding
            {
                checkId = CheckId,
                // Info, deliberately. Info findings never interrupt the upload
                // gate (PreUploadReport.HasActionableFindings counts Warning and
                // Blocking only), and a prefab created five minutes ago is
                // legitimately unstamped until ContentProcessor's next pass.
                severity = CheckSeverity.Info,
                assetGuid = root.guid,
                assetPath = root.assetPath,
                subKey = ModeUnstamped,
                title = root.KindLabel + " '" + root.name + "' — resourceName not stamped yet",
                detail = "This prefab carries no resourceName. ContentProcessor stamps '" + expected
                       + "' onto it on its next pass over the package, so this normally resolves itself "
                       + "at upload. It is listed here because an asset that stays unstamped through a "
                       + "build has no revenue key of its own.",
            };

            AddFixes(finding, root, expected, null);
            return finding;
        }

        private static void AddFixes(Finding finding, ContentRootInfo root, string expected, string stored)
        {
            string path = root.assetPath;

            finding.fixes.Add(new FixAction("Re-stamp resourceName", () => Restamp(path, expected))
            {
                tooltip = "Writes '" + expected + "' onto this prefab's PropTemplate and GameArea — the "
                        + "same value ContentProcessor computes. On a variant this re-creates the "
                        + "property override that 'Revert All' removed.",
                confirmTitle = "Re-stamp resourceName",
                confirmMessage =
                    path + "\n\n"
                    + (string.IsNullOrEmpty(stored) ? "(not stamped)" : "'" + stored + "'")
                    + "\n  →  '" + expected + "'\n\n"
                    + "This edits the prefab asset. Asset writes are not covered by Ctrl-Z — use version "
                    + "control if you need to get back. Addressables entries are not touched; "
                    + "ContentProcessor still owns those.",
            });

            finding.fixes.Add(FixAction.Navigate("Select", () =>
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null) return;
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
            }));
        }

        // ── The address ─────────────────────────────────────────────────

        /// <summary>
        /// Exactly what ContentProcessor.ResolveAttractionAddress computes, and
        /// it has to stay exactly that: a check that recomputes the address its
        /// own way reports a mismatch on every correctly stamped prefab in the
        /// project, which is how a check gets ignored permanently.
        ///
        /// LevelTemplate is tested FIRST because AttractionTemplate derives from
        /// it and an attraction root also carries a GameArea — same ordering as
        /// ContentRootScanner's classification.
        ///
        /// Returns null for a root that is neither, which is the PlayerRig case:
        /// ContentProcessor stamps nothing on it, so there is nothing to check.
        /// </summary>
        public static string ExpectedAddress(GameObject root, string assetPath, string gameId)
        {
            if (root == null || string.IsNullOrEmpty(assetPath)) return null;

            string name = Path.GetFileNameWithoutExtension(assetPath);

            var level = root.GetComponent<LevelTemplate>();
            if (level != null) return gameId + "/Levels/" + level.size + "/" + name;

            var prop = root.GetComponent<PropTemplate>();
            if (prop != null) return gameId + "/Props/" + prop.category + "/" + name;

            return null;
        }

        /// <summary>
        /// The gameId ContentProcessor would use: the folder name straight after
        /// Assets/Content/. Read from the asset's own path rather than from the
        /// panel's selection, so a root that somehow sits outside the selected
        /// package is still measured against the package it actually lives in.
        /// </summary>
        private static string GameIdFor(string assetPath, PreUploadCheckContext ctx)
        {
            string fromPath = ContentFolders.FolderOfAsset(assetPath);
            if (!string.IsNullOrEmpty(fromPath)) return fromPath;
            return ctx != null ? ctx.contentId : null;
        }

        /// <summary>
        /// When the stored value is exactly the BASE prefab's own address, this
        /// is not a stale stamp — it is the base's value showing through a
        /// removed override, which is what "Revert All" on a variant does.
        /// Naming that explicitly is the difference between a finding someone
        /// fixes and a finding someone dismisses.
        /// </summary>
        private static string VariantNote(GameObject prefab, string stored, string gameId)
        {
            try
            {
                if (PrefabUtility.GetPrefabAssetType(prefab) != PrefabAssetType.Variant) return null;

                var baseRoot = PrefabUtility.GetCorrespondingObjectFromSource(prefab) as GameObject;
                if (baseRoot == null) return null;

                string basePath = AssetDatabase.GetAssetPath(baseRoot);
                if (string.IsNullOrEmpty(basePath)) return null;

                string baseName = Path.GetFileNameWithoutExtension(basePath);
                string baseAddress = ExpectedAddress(baseRoot, basePath, gameId);

                if (!string.Equals(stored, baseAddress, StringComparison.Ordinal))
                {
                    return "This is a prefab variant of '" + baseName + "'. resourceName is stamped per "
                         + "asset, so on a variant it is a property override — reverting that override "
                         + "hands this variant the base's revenue key.";
                }

                return "This is a prefab variant of '" + baseName + "', and the stored value IS that "
                     + "base's own address. That is the signature of 'Revert All' on the variant: the "
                     + "ContentProcessor stamp is a property override, reverting it removed the "
                     + "override, and the base's key now shows through. Every sale of this variant is "
                     + "being attributed to '" + baseName + "'. Revert individual overrides rather than "
                     + "all of them on a content prefab.";
            }
            catch (Exception)
            {
                // A broken variant chain is somebody else's finding.
                return null;
            }
        }

        // ── The fix ─────────────────────────────────────────────────────

        /// <summary>
        /// Write the computed address onto the prefab's own PropTemplate and
        /// GameArea. Deliberately narrower than ContentProcessor.ForceUpdateContent,
        /// which re-stamps and re-addresses the entire package: this is one
        /// asset, one field, and clicking it on five findings must not run five
        /// full passes.
        ///
        /// LoadPrefabContents on a VARIANT hands back the merged contents;
        /// writing the field and saving records it as a property override on the
        /// variant, which is exactly the state ContentProcessor produces.
        /// </summary>
        private static bool Restamp(string assetPath, string expected)
        {
            if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(expected)) return false;

            GameObject contents = null;
            try
            {
                contents = PrefabUtility.LoadPrefabContents(assetPath);
                if (contents == null)
                {
                    Debug.LogWarning("[DreamPark] Could not open " + assetPath + " to re-stamp resourceName.");
                    return false;
                }

                bool changed = false;

                var prop = contents.GetComponent<PropTemplate>();
                if (prop != null && !string.Equals(prop.resourceName, expected, StringComparison.Ordinal))
                {
                    prop.resourceName = expected;
                    EditorUtility.SetDirty(prop);
                    changed = true;
                }

                var area = contents.GetComponent<GameArea>();
                if (area != null && !string.Equals(area.resourceName, expected, StringComparison.Ordinal))
                {
                    area.resourceName = expected;
                    EditorUtility.SetDirty(area);
                    changed = true;
                }

                if (!changed) return true;      // already correct; the finding is stale

                PrefabUtility.SaveAsPrefabAsset(contents, assetPath);
                AssetDatabase.SaveAssets();

                Debug.Log("[DreamPark] resourceName on " + Path.GetFileNameWithoutExtension(assetPath)
                          + " re-stamped to '" + expected + "'.");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DreamPark] Could not re-stamp " + assetPath + ": " + e);
                return false;
            }
            finally
            {
                // ALWAYS unload. A leaked prefab-contents scene stays open for
                // the rest of the editor session and quietly breaks the next
                // LoadPrefabContents on the same asset.
                if (contents != null) PrefabUtility.UnloadPrefabContents(contents);
            }
        }
    }
}
#endif
