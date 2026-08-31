// ─────────────────────────────────────────────────────────────────────
//  ConvertReadyMenu.cs — the right-click that subsumes the pipeline
//
//  A creator drags an FBX into Assets/Content/MyGame/ and gets: a pink object,
//  authored in centimetres so it is 100× too big, lying on its side because it
//  came out of Blender, with no collider, no prefab, and no idea that any of
//  those four things are wrong. Every one of them already has a fix in this
//  SDK. None of them is discoverable.
//
//  That is the gap this menu closes. It is not new capability — it is the six
//  existing menu items in three places, plus the conventions written down in
//  CLAUDE.md, collapsed into one right-click that works on the first try for
//  an asset the creator did not author and does not understand.
//
//  PRIORITY 40 puts this above "Convert to DreamPark Shader" (50) and
//  "Move to ThirdPartyLocal" (51), with a separator between. That ordering is
//  deliberate: for a new creator this item subsumes the shader converter, and
//  the menu should read that way rather than burying it underneath.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DreamPark.ConvertReady
{
    public static class ConvertReadyMenu
    {
        private const string Root = "Assets/DreamPark/Convert to DreamPark-Ready/";

        // Above MaterialConverter's 50 and PackageRelocator's 51.
        private const int PriorityInteractive = 40;
        private const int PriorityBendy       = 41;
        private const int PriorityShatterable = 42;
        private const int PriorityCustom      = 43;
        // A gap of 11+ makes Unity draw a separator.
        private const int PrioritySharedProp  = 60;

        // ── Items ───────────────────────────────────────────────────────

        [MenuItem(Root + "Interactive", false, PriorityInteractive)]
        private static void Interactive() { RunPreset(ConversionPlan.Interactive()); }

        [MenuItem(Root + "Bendy", false, PriorityBendy)]
        private static void Bendy() { RunPreset(ConversionPlan.Bendy()); }

        [MenuItem(Root + "Shatterable", false, PriorityShatterable)]
        private static void Shatterable() { RunPreset(ConversionPlan.Shatterable()); }

        [MenuItem(Root + "Custom…", false, PriorityCustom)]
        private static void Custom()
        {
            var paths = SelectedConvertiblePaths();
            if (paths.Count == 0) { NothingToDo(); return; }

            // Seed from whatever the selection looks like, so "Custom…" reads
            // as "…starting from the sensible preset" rather than as a blank
            // form. A blank form is how you get thirty props with the collider
            // mode left on whatever the default was.
            ConversionPlan seed = SeedForSelection(paths);

            ConvertReadyOptionsWindow.Open(seed, paths.Count, delegate (ConversionPlan plan)
            {
                Execute(paths, plan);
            });
        }

        [MenuItem(Root + "Shared Prop…", false, PrioritySharedProp)]
        private static void SharedProp()
        {
            var paths = SelectedConvertiblePaths();
            var models = new List<string>();
            foreach (var p in paths)
                if (AssetClassifier.Classify(p) == ConvertTrack.Mesh) models.Add(p);

            if (models.Count < 2)
            {
                EditorUtility.DisplayDialog(
                    "Shared Prop",
                    "Select two or more models.\n\nA shared prop family is one base prefab "
                    + "holding everything except the model, plus one prefab variant per model — "
                    + "so changing the base changes every member of the family at once. With a "
                    + "single model there is nothing to share.",
                    "OK");
                return;
            }

            var seed = ConversionPlan.Interactive();
            seed.shared.enabled = true;
            seed.shared.familyName = SuggestFamilyName(models);

            ConvertReadyOptionsWindow.Open(seed, models.Count, delegate (ConversionPlan plan)
            {
                plan.shared.enabled = true;
                Execute(models, plan);
            });
        }

        // ── Validators ──────────────────────────────────────────────────
        //
        // Without these the items appear greyed out on unrelated selections
        // (scripts, scenes, materials), which reads as "broken" rather than
        // "not applicable".

        [MenuItem(Root + "Interactive", true)]
        private static bool ValidateInteractive() { return HasConvertibleSelection(); }

        [MenuItem(Root + "Bendy", true)]
        private static bool ValidateBendy() { return HasConvertibleSelection(); }

        [MenuItem(Root + "Shatterable", true)]
        private static bool ValidateShatterable() { return HasConvertibleSelection(); }

        [MenuItem(Root + "Custom…", true)]
        private static bool ValidateCustom() { return HasConvertibleSelection(); }

        [MenuItem(Root + "Shared Prop…", true)]
        private static bool ValidateSharedProp()
        {
            int models = 0;
            foreach (var p in SelectedConvertiblePaths())
            {
                if (AssetClassifier.Classify(p) == ConvertTrack.Mesh) models++;
                if (models >= 2) return true;
            }
            return false;
        }

        // ── Plumbing ────────────────────────────────────────────────────

        private static void RunPreset(ConversionPlan plan)
        {
            var paths = SelectedConvertiblePaths();
            if (paths.Count == 0) { NothingToDo(); return; }

            // A preset applied to a texture or a clip would silently do the
            // wrong thing — Bendy on a PNG is meaningless. Retarget rather than
            // refuse, and let the report say what happened.
            Execute(paths, plan);
        }

        private static void Execute(List<string> paths, ConversionPlan plan)
        {
            if (paths == null || paths.Count == 0 || plan == null) return;

            var report = new ConversionReport();

            // Split by track so a mixed selection does not get one preset
            // forced onto all of it. Each group runs with a plan whose track
            // matches the assets in it.
            var meshes = new List<string>();
            var textures = new List<string>();
            var clips = new List<string>();

            foreach (var p in paths)
            {
                switch (AssetClassifier.Classify(p))
                {
                    case ConvertTrack.Texture: textures.Add(p); break;
                    case ConvertTrack.Audio:   clips.Add(p);    break;
                    case ConvertTrack.Mesh:    meshes.Add(p);   break;
                }
            }

            var created = new List<string>();

            if (meshes.Count > 0)
            {
                var p = plan.Clone();
                p.track = ConvertTrack.Mesh;
                Merge(report, created, ConvertReadyExecutor.Run(meshes, p));
            }
            if (textures.Count > 0)
            {
                var p = RetargetForTrack(plan, ConvertTrack.Texture);
                Merge(report, created, ConvertReadyExecutor.Run(textures, p));
            }
            if (clips.Count > 0)
            {
                var p = RetargetForTrack(plan, ConvertTrack.Audio);
                Merge(report, created, ConvertReadyExecutor.Run(clips, p));
            }

            ConvertReadyReportWindow.Show(report, created);
        }

        private static void Merge(ConversionReport into, List<string> created, ConversionReport from)
        {
            if (from == null) return;
            into.results.AddRange(from.results);
            created.AddRange(ConvertReadyExecutor.LastRunCreatedAssets);
        }

        /// <summary>
        /// A mesh preset carries settings that mean nothing on a texture or a
        /// clip. Rather than apply them anyway, take the track's own preset and
        /// carry across only what genuinely transfers.
        /// </summary>
        private static ConversionPlan RetargetForTrack(ConversionPlan source, ConvertTrack track)
        {
            ConversionPlan p = track == ConvertTrack.Texture
                ? ConversionPlan.TexturePlane()
                : ConversionPlan.AudioEmitter();

            p.category = source.category;
            p.affectsGapFiller = source.affectsGapFiller;
            p.plane = source.plane;
            p.audio = source.audio;
            return p;
        }

        private static ConversionPlan SeedForSelection(List<string> paths)
        {
            bool anyMesh = false, anyTexture = false, anyAudio = false;
            foreach (var p in paths)
            {
                switch (AssetClassifier.Classify(p))
                {
                    case ConvertTrack.Mesh:    anyMesh = true;    break;
                    case ConvertTrack.Texture: anyTexture = true; break;
                    case ConvertTrack.Audio:   anyAudio = true;   break;
                }
            }
            if (anyMesh) return ConversionPlan.Interactive();
            if (anyTexture) return ConversionPlan.TexturePlane();
            if (anyAudio) return ConversionPlan.AudioEmitter();
            return ConversionPlan.Interactive();
        }

        /// <summary>
        /// Longest shared prefix of the model names, so selecting Coin_Gold,
        /// Coin_Silver and Coin_Ruby suggests "Coin" rather than making the
        /// creator type it. Falls back to the first name.
        /// </summary>
        private static string SuggestFamilyName(List<string> modelPaths)
        {
            if (modelPaths.Count == 0) return "Family";

            string prefix = System.IO.Path.GetFileNameWithoutExtension(modelPaths[0]);
            for (int i = 1; i < modelPaths.Count; i++)
            {
                string other = System.IO.Path.GetFileNameWithoutExtension(modelPaths[i]);
                int n = 0;
                while (n < prefix.Length && n < other.Length && prefix[n] == other[n]) n++;
                prefix = prefix.Substring(0, n);
                if (prefix.Length == 0) break;
            }

            prefix = prefix.TrimEnd('_', '-', ' ', '.');
            if (prefix.Length < 2)
                prefix = System.IO.Path.GetFileNameWithoutExtension(modelPaths[0]);

            return AssetClassifier.SanitizeAssetName(prefix);
        }

        /// <summary>
        /// Every convertible asset in the selection, with folders expanded
        /// recursively. Deduplicated, because selecting a folder and a file
        /// inside it is an easy thing to do by accident and converting the same
        /// model twice would emit two prefabs.
        /// </summary>
        private static List<string> SelectedConvertiblePaths()
        {
            var seen = new HashSet<string>();
            var result = new List<string>();

            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;

                if (AssetClassifier.IsFolder(path))
                {
                    foreach (var inner in AssetClassifier.EnumerateConvertible(path))
                        if (seen.Add(inner)) result.Add(inner);
                }
                else if (AssetClassifier.IsConvertible(path))
                {
                    if (seen.Add(path)) result.Add(path);
                }
            }

            return result;
        }

        private static bool HasConvertibleSelection()
        {
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;
                if (AssetClassifier.IsConvertible(path)) return true;
                if (AssetClassifier.IsFolder(path)) return true;
            }
            return false;
        }

        private static void NothingToDo()
        {
            EditorUtility.DisplayDialog(
                "Convert to DreamPark-Ready",
                "Nothing in the selection can be converted.\n\n"
                + "This works on models (.fbx, .glb, .obj and friends), prefabs, textures, "
                + "audio clips, and folders containing them.",
                "OK");
        }
    }
}
#endif
