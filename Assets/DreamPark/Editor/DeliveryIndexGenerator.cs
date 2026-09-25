#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Defective.JSON;
using UnityEditor.AddressableAssets.Build.Layout;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace DreamPark.Editor
{
    /// <summary>
    /// Compiles a small, platform-specific streaming index from Addressables'
    /// actual post-build layout. Unlike group-name guesses at runtime, this records
    /// the exact bundle dependency graph emitted by Scriptable Build Pipeline.
    /// </summary>
    internal static class DeliveryIndexGenerator
    {
        internal const int SchemaVersion = 2;
        internal const string FileName = "delivery-index.json";

        internal static string RoleForGroup(string contentId, string groupName)
        {
            string name = groupName ?? "";
            if (name.Equals(SmartBundleGrouper.BootstrapGroupName(contentId), StringComparison.OrdinalIgnoreCase)) return "bootstrap";
            if (name.IndexOf("Shared-Foundation", StringComparison.OrdinalIgnoreCase) >= 0) return "foundation";
            if (name.EndsWith("-Shared", StringComparison.OrdinalIgnoreCase)) return "shared";
            if (name.IndexOf("-Code", StringComparison.OrdinalIgnoreCase) >= 0) return "code";
            if (name.IndexOf("-Runtime", StringComparison.OrdinalIgnoreCase) >= 0) return "runtime";
            if (name.IndexOf("-Bundle-", StringComparison.OrdinalIgnoreCase) >= 0
                && name.IndexOf("-Content", StringComparison.OrdinalIgnoreCase) >= 0) return "rootContent";
            if (name.IndexOf("-Bundle-", StringComparison.OrdinalIgnoreCase) >= 0) return "rootLogic";
            return "content";
        }

        internal static IReadOnlyList<string> TransitiveClosure(string root,
            IReadOnlyDictionary<string, IReadOnlyList<string>> dependencies)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            if (!string.IsNullOrEmpty(root)) pending.Push(root);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                if (!seen.Add(current)) continue;
                if (!dependencies.TryGetValue(current, out IReadOnlyList<string> direct) || direct == null) continue;
                for (int i = direct.Count - 1; i >= 0; i--)
                    if (!string.IsNullOrEmpty(direct[i])) pending.Push(direct[i]);
            }
            return seen.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        internal static void Generate(string contentId, string platform)
        {
            if (string.IsNullOrWhiteSpace(contentId)) throw new ArgumentException("contentId is required");
            if (string.IsNullOrWhiteSpace(platform)) throw new ArgumentException("platform is required");

            string layoutPath = Path.Combine(Addressables.LibraryPath, "aa", "buildlayout.json");
            if (!File.Exists(layoutPath))
                throw new InvalidOperationException("Addressables did not emit buildlayout.json; delivery index cannot be generated.");

            BuildLayout layout = BuildLayout.Open(layoutPath, readHeader: true, readFullFile: true);
            if (layout == null) throw new InvalidOperationException("Addressables build layout could not be read.");
            try
            {
                Write(contentId, platform, layout);
            }
            finally
            {
                layout.Close();
            }
        }

        private static void Write(string contentId, string platform, BuildLayout layout)
        {
            string platformDirectory = Path.Combine(BuildManifestStore.ServerDataRoot, platform);
            if (!Directory.Exists(platformDirectory))
                throw new DirectoryNotFoundException("Addressables output directory is missing: " + platformDirectory);
            var physicalNames = new HashSet<string>(Directory.GetFiles(platformDirectory, "*.bundle", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(platformDirectory, path).Replace('\\', '/')),
                StringComparer.Ordinal);

            var allBundles = new Dictionary<string, BuildLayout.Bundle>(StringComparer.Ordinal);
            var allRoles = new Dictionary<string, string>(StringComparer.Ordinal);
            var contentRoots = new HashSet<string>(StringComparer.Ordinal);
            foreach (BuildLayout.Group group in layout.Groups ?? new List<BuildLayout.Group>())
            {
                if (group == null) continue;
                bool belongsToContent = group.Name.StartsWith(contentId + "-", StringComparison.OrdinalIgnoreCase);
                foreach (BuildLayout.Bundle bundle in group.Bundles ?? new List<BuildLayout.Bundle>())
                {
                    string fileName = BundleFileName(bundle, physicalNames);
                    if (string.IsNullOrEmpty(fileName)) continue;
                    allBundles[fileName] = bundle;
                    allRoles[fileName] = belongsToContent
                        ? RoleForGroup(contentId, group.Name) : "shared";
                    if (belongsToContent) contentRoots.Add(fileName);
                }
            }

            // Content groups can depend on Addressables-owned/shared bundles
            // whose group names do not start with the title id. Include that
            // entire reachable graph: a closure entry with no corresponding
            // bundle row would leave clients unable to resolve its storage path.
            var requiredNames = new HashSet<string>(contentRoots, StringComparer.Ordinal);
            var pendingBundles = new Stack<string>(contentRoots);
            while (pendingBundles.Count > 0)
            {
                string current = pendingBundles.Pop();
                if (!allBundles.TryGetValue(current, out BuildLayout.Bundle bundle)) continue;
                foreach (BuildLayout.Bundle dependency in bundle.Dependencies ?? new List<BuildLayout.Bundle>())
                {
                    string dependencyName = BundleFileName(dependency, physicalNames);
                    if (!string.IsNullOrEmpty(dependencyName)
                        && requiredNames.Add(dependencyName)) pendingBundles.Push(dependencyName);
                }
            }
            var bundles = allBundles.Where(pair => requiredNames.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var roles = allRoles.Where(pair => requiredNames.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

            var dependencies = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var pair in bundles)
            {
                dependencies[pair.Key] = (pair.Value.Dependencies ?? new List<BuildLayout.Bundle>())
                    .Select(bundle => BundleFileName(bundle, physicalNames))
                    .Where(name => name != null && requiredNames.Contains(name))
                    .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            }

            var resourcePrimary = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in bundles.Where(pair => contentRoots.Contains(pair.Key)))
            {
                foreach (BuildLayout.File file in pair.Value.Files ?? new List<BuildLayout.File>())
                foreach (BuildLayout.ExplicitAsset asset in file.Assets ?? new List<BuildLayout.ExplicitAsset>())
                {
                    string address = asset?.AddressableName;
                    if (string.IsNullOrWhiteSpace(address)) continue;
                    resourcePrimary[address] = pair.Key;
                }
            }

            var root = new JSONObject(JSONObject.Type.Object);
            root.AddField("schemaVersion", SchemaVersion);
            root.AddField("contentId", contentId);
            root.AddField("platform", platform);
            root.AddField("generatedAt", DateTime.UtcNow.ToString("o"));

            var bundleObject = new JSONObject(JSONObject.Type.Object);
            foreach (string fileName in bundles.Keys.OrderBy(value => value, StringComparer.Ordinal))
            {
                var row = new JSONObject(JSONObject.Type.Object);
                row.AddField("role", roles[fileName]);
                row.AddField("dependencies", StringArray(dependencies[fileName]));
                row.AddField("resourceAddresses", StringArray(resourcePrimary
                    .Where(pair => pair.Value == fileName).Select(pair => pair.Key).OrderBy(value => value, StringComparer.Ordinal)));
                bundleObject.AddField(fileName, row);
            }
            root.AddField("bundles", bundleObject);

            var resources = new JSONObject(JSONObject.Type.Object);
            foreach (var pair in resourcePrimary.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var row = new JSONObject(JSONObject.Type.Object);
                row.AddField("primaryBundle", pair.Value);
                row.AddField("bundleClosure", StringArray(TransitiveClosure(pair.Value, dependencies)));
                resources.AddField(pair.Key, row);
            }
            root.AddField("resources", resources);

            DreamParkPackageManifest manifest = UnityEditor.AssetDatabase.LoadAssetAtPath<DreamParkPackageManifest>(
                DreamParkPackageCompiler.ManifestPath(contentId));
            root.AddField("packages", BuildPackageHints(manifest, resourcePrimary, dependencies));

            string outputPath = Path.Combine(platformDirectory, FileName);
            File.WriteAllText(outputPath, root.Print(pretty: true));
            Debug.Log($"[DeliveryIndex] {contentId}/{platform}: {bundles.Count} bundles, {resourcePrimary.Count} addresses → {outputPath}");
        }

        private static JSONObject BuildPackageHints(DreamParkPackageManifest manifest,
            IReadOnlyDictionary<string, string> resourcePrimary,
            IReadOnlyDictionary<string, IReadOnlyList<string>> dependencies)
        {
            var packages = new JSONObject(JSONObject.Type.Object);
            if (manifest == null) return packages;
            string manifestAddress = DreamParkPackageManifest.AddressFor(manifest.contentId);
            packages.AddField("manifestAddress", manifestAddress);
            packages.AddField("packageRevision", manifest.packageRevision ?? "");
            packages.AddField("bootstrapAddresses", StringArray(new[] { manifestAddress }));
            packages.AddField("bootstrapBundles", StringArray(ClosureForAddresses(
                new[] { manifestAddress }, resourcePrimary, dependencies)));
            AddRecipe(packages, "arena", manifest.arena, manifestAddress, resourcePrimary, dependencies);
            AddRecipe(packages, "sequence", manifest.sequence, manifestAddress, resourcePrimary, dependencies);
            AddRecipe(packages, "adventure", manifest.adventure, manifestAddress, resourcePrimary, dependencies);
            return packages;
        }

        private static void AddRecipe(JSONObject packages, string kind, PackageRecipe recipe,
            string manifestAddress, IReadOnlyDictionary<string, string> resourcePrimary,
            IReadOnlyDictionary<string, IReadOnlyList<string>> dependencies)
        {
            if (recipe == null) return;
            var services = new[] { recipe.playerAddress, recipe.gameManagerAddress,
                recipe.sequenceDefinitionAddress }.Where(value => !string.IsNullOrWhiteSpace(value));
            var occurrenceAddresses = recipe.occurrences.Select(value => value.resourceAddress)
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            IEnumerable<string> starts;
            if (kind == "adventure")
                starts = recipe.occurrences.Where(value => value.role == "start").OrderBy(value => value.order)
                    .Select(value => value.resourceAddress);
            else if (kind == "sequence")
                starts = new[] { recipe.sequenceDefinitionAddress }.Concat(occurrenceAddresses.Take(1));
            else
            {
                PackageOccurrence first = recipe.arenaBuckets
                    .OrderBy(bucket => bucket.widthFeet * bucket.lengthFeet)
                    .SelectMany(bucket => bucket.occurrenceIds)
                    .Select(id => recipe.occurrences.FirstOrDefault(value => value.occurrenceId == id))
                    .FirstOrDefault(value => value != null);
                starts = first == null ? Array.Empty<string>() : new[] { first.resourceAddress };
            }

            string[] bootstrap = new[] { manifestAddress }.Concat(services).Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal).ToArray();
            string[] all = bootstrap.Concat(occurrenceAddresses).Distinct(StringComparer.Ordinal).ToArray();
            var row = new JSONObject(JSONObject.Type.Object);
            row.AddField("services", ServiceObject(recipe));
            row.AddField("bootstrapAddresses", StringArray(bootstrap));
            row.AddField("startAddresses", StringArray(starts.Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)));
            row.AddField("allAddresses", StringArray(all));
            row.AddField("bootstrapBundles", StringArray(ClosureForAddresses(bootstrap, resourcePrimary, dependencies)));
            row.AddField("allBundles", StringArray(ClosureForAddresses(all, resourcePrimary, dependencies)));
            packages.AddField(kind, row);
        }

        private static JSONObject ServiceObject(PackageRecipe recipe)
        {
            var row = new JSONObject(JSONObject.Type.Object);
            if (!string.IsNullOrWhiteSpace(recipe.playerAddress)) row.AddField("playerAddress", recipe.playerAddress);
            if (!string.IsNullOrWhiteSpace(recipe.gameManagerAddress)) row.AddField("gameManagerAddress", recipe.gameManagerAddress);
            if (!string.IsNullOrWhiteSpace(recipe.sequenceDefinitionAddress)) row.AddField("sequenceDefinitionAddress", recipe.sequenceDefinitionAddress);
            return row;
        }

        private static IEnumerable<string> ClosureForAddresses(IEnumerable<string> addresses,
            IReadOnlyDictionary<string, string> resourcePrimary,
            IReadOnlyDictionary<string, IReadOnlyList<string>> dependencies)
        {
            var files = new HashSet<string>(StringComparer.Ordinal);
            foreach (string address in addresses ?? Array.Empty<string>())
                if (address != null && resourcePrimary.TryGetValue(address, out string primary))
                    files.UnionWith(TransitiveClosure(primary, dependencies));
            return files.OrderBy(value => value, StringComparer.Ordinal);
        }

        private static string BundleFileName(BuildLayout.Bundle bundle,
            IReadOnlyCollection<string> physicalNames)
        {
            if (bundle == null) return null;
            return ResolvePhysicalBundlePath(new[] {
                bundle.LoadPath, bundle.Name, bundle.InternalName,
            }, physicalNames);
        }

        /// <summary>
        /// Maps SBP's URL/load-path representation back to the exact relative
        /// path uploaded by ContentAPI. Leaf-only keys are ambiguous for
        /// PackSeparately groups and do not match bundle-manifest `name` rows.
        /// </summary>
        internal static string ResolvePhysicalBundlePath(IEnumerable<string> candidates,
            IReadOnlyCollection<string> physicalNames)
        {
            if (physicalNames == null || physicalNames.Count == 0) return null;
            string[] paths = physicalNames.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Replace('\\', '/').TrimStart('/'))
                .Distinct(StringComparer.Ordinal).ToArray();
            foreach (string raw in candidates ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string candidate = raw.Replace('\\', '/');
                int query = candidate.IndexOf('?');
                if (query >= 0) candidate = candidate.Substring(0, query);
                int fragment = candidate.IndexOf('#');
                if (fragment >= 0) candidate = candidate.Substring(0, fragment);
                try { candidate = Uri.UnescapeDataString(candidate); }
                catch (UriFormatException) { }
                foreach (string path in paths.OrderByDescending(value => value.Length))
                    if (candidate.Equals(path, StringComparison.Ordinal)
                        || candidate.EndsWith("/" + path, StringComparison.Ordinal)) return path;
            }

            // Older layouts sometimes expose only the leaf. It is safe only
            // when that leaf uniquely identifies one uploaded file.
            foreach (string raw in candidates ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string candidate = raw.Replace('\\', '/');
                int query = candidate.IndexOf('?');
                if (query >= 0) candidate = candidate.Substring(0, query);
                string leaf = candidate.Substring(candidate.LastIndexOf('/') + 1);
                string[] matches = paths.Where(path => path.EndsWith("/" + leaf,
                        StringComparison.Ordinal) || path.Equals(leaf, StringComparison.Ordinal))
                    .Take(2).ToArray();
                if (matches.Length == 1) return matches[0];
            }
            return null;
        }

        private static JSONObject StringArray(IEnumerable<string> values)
        {
            var array = new JSONObject(JSONObject.Type.Array);
            foreach (string value in values ?? Array.Empty<string>()) array.Add(value);
            return array;
        }
    }
}
#endif
