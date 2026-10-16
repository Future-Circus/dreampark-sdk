// ─────────────────────────────────────────────────────────────────────
//  XLuaGenGuard.cs — SDK-synced editor tooling
//
//  Keeps Assets/DreamPark/ThirdParty/XLua/Gen/ compileable across SDK updates.
//
//  THE PROBLEM
//  Gen/ is ONE generated unit. Wrappers call Push/Update/Get helpers that exist only
//  in the WrapPusher.cs written by the same codegen run, and XLuaGenAutoRegister.cs
//  names every wrapper class. A .unitypackage import adds and overwrites files but
//  never deletes them, so after an update Gen/ could hold the new SDK's WrapPusher.cs
//  next to wrappers from an older local run whose helpers no longer exist.
//  September 2026, superadventureland: wrappers for XLua's demo types (generated
//  locally because the demo sources were never deleted from the project) survived an
//  SDK import and failed with CS1061.
//
//  THE RULES
//  1. Gen/ is SDK-owned. XLuaGenManifest.json lists every Gen/ file with a SHA-256.
//     After ANY import (in-editor updater or a hand-imported .unitypackage), if every
//     manifest file is on disk with the shipped content, anything else in Gen/ is
//     foreign and is removed; Unity's "Name 1.cs" copies of shipped files replace the
//     stale original. If the shipped set is NOT intact (a local regen, or files
//     unchecked in the import dialog) nothing is touched.
//  2. XLua's demo code is retired. Examples/ and Tutorial/ are deleted wherever they
//     appear, and codegen never generates for them (Generator.IsExcludedFromCodegen).
//  3. Codegen is all-or-nothing. Generate XLua Code writes into Temp/ first, checks
//     the output, then syncs Gen/ to exactly that output: stale files deleted,
//     unchanged files untouched, existing .meta GUIDs kept, new files given a GUID
//     derived from their name so every project agrees on it. A failed run leaves Gen/
//     alone.
//  4. The SDK cannot ship an inconsistent Gen/. Export refuses on codegen drift,
//     unresolved helpers, unregistered wrappers or demo references.
//
//  scripts/release/check_sdk_upgrade.py applies the same consistency rules outside
//  Unity and simulates an import over a real project. Keep the two in step.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    public static class XLuaGenGuard
    {
        public const string GenFolder = "Assets/DreamPark/ThirdParty/XLua/Gen";
        public const string XLuaSrcFolder = "Assets/DreamPark/ThirdParty/XLua/Src";
        public const string ManifestPath = "Assets/DreamPark/Editor/XLuaGenManifest.json";

        // SDK-owned folders the SDK no longer ships (XLua's demos, removed May 2026).
        public static readonly string[] RetiredFolders =
        {
            "Assets/DreamPark/ThirdParty/XLua/Examples",
            "Assets/DreamPark/ThirdParty/XLua/Tutorial",
        };

        [Serializable] private class ManifestEntry { public string name; public string sha256; }
        [Serializable] private class Manifest { public int schema = 1; public List<ManifestEntry> files = new List<ManifestEntry>(); }

        private static readonly Regex HelperUse = new Regex(@"\btranslator\.((?:Push|Update|Get|Is)[A-Z]\w*)", RegexOptions.Compiled);
        private static readonly Regex MethodDef = new Regex(@"(?:public|internal|private|protected)[^;{}=()\n]*?\b(\w+)\s*(?:<[^()\n]*?>)?\s*\(", RegexOptions.Compiled);
        private static readonly Regex ClassDecl = new Regex(@"^[ \t]*(?:(?:public|internal|private|protected|static|sealed|abstract|unsafe)\s+)*(partial\s+)?class\s+(\w+)", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex RegisterRef = new Regex(@"\b(\w+)\.__(?:Register|Create)\b", RegexOptions.Compiled);
        private static readonly Regex RetiredNamespaceRef = new Regex(@"\b(?:global::)?(XLuaTest|Tutorial)\.[A-Za-z_]\w*", RegexOptions.Compiled);
        private static readonly Regex UnityCopySuffix = new Regex(@"^(?<stem>.+) (?<n>\d+)(?<ext>\.[^.]+)$", RegexOptions.Compiled);

        private static bool reconciling;

        // ── paths & IO ────────────────────────────────────────────────────

        private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

        private static string Abs(string assetPath) =>
            Path.Combine(ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));

        /// Top-level files of a Gen-shaped folder, name → bytes (no .meta, no dot-files).
        private static Dictionary<string, byte[]> ReadFolder(string absDir)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (!Directory.Exists(absDir)) return result;
            foreach (var f in Directory.GetFiles(absDir, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(f);
                if (name.StartsWith(".", StringComparison.Ordinal) ||
                    name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                result[name] = File.ReadAllBytes(f);
            }
            return result;
        }

        private static HashSet<string> ListGenFiles()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            string genAbs = Abs(GenFolder);
            if (!Directory.Exists(genAbs)) return names;
            foreach (var f in Directory.GetFiles(genAbs, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(f);
                if (name.StartsWith(".", StringComparison.Ordinal) ||
                    name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                names.Add(name);
            }
            return names;
        }

        /// SHA-256 with CR removed, so a CRLF checkout of the same file still matches.
        private static string Hash(byte[] data)
        {
            byte[] normalized = Array.IndexOf(data, (byte)'\r') >= 0 ? data.Where(b => b != (byte)'\r').ToArray() : data;
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder(64);
                foreach (var b in sha.ComputeHash(normalized)) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// Asset paths of every file currently in Gen/. SDKUpgradeCleanup deletes these
        /// before the in-editor updater imports, and publishes them so an older installed
        /// SDK does the same: Gen/ is replaced wholesale, never merged.
        public static List<string> GenAssetPaths()
        {
            return ListGenFiles().OrderBy(n => n, StringComparer.Ordinal)
                                 .Select(n => GenFolder + "/" + n).ToList();
        }

        /// Asset paths of every file inside the retired folders (for the updater's
        /// file-by-file cleanup, which deliberately refuses directories).
        public static List<string> RetiredFolderFileAssetPaths()
        {
            var result = new List<string>();
            string root = ProjectRoot + Path.DirectorySeparatorChar;
            foreach (var folder in RetiredFolders)
            {
                string abs = Abs(folder);
                if (!Directory.Exists(abs)) continue;
                foreach (var f in Directory.GetFiles(abs, "*", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(f);
                    if (name.StartsWith(".", StringComparison.Ordinal) ||
                        name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(f.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/'));
                }
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        // ── manifest ──────────────────────────────────────────────────────

        public static void WriteManifest()
        {
            var m = new Manifest();
            foreach (var kv in ReadFolder(Abs(GenFolder)).OrderBy(k => k.Key, StringComparer.Ordinal))
                m.files.Add(new ManifestEntry { name = kv.Key, sha256 = Hash(kv.Value) });
            string json = JsonUtility.ToJson(m, true) + "\n";
            string abs = Abs(ManifestPath);
            if (!File.Exists(abs) || File.ReadAllText(abs) != json)
                File.WriteAllText(abs, json);
            AssetDatabase.ImportAsset(ManifestPath,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        }

        private static Dictionary<string, string> ReadManifest()
        {
            string abs = Abs(ManifestPath);
            if (!File.Exists(abs)) return null;
            try
            {
                var m = JsonUtility.FromJson<Manifest>(File.ReadAllText(abs));
                if (m == null || m.files == null || m.files.Count == 0) return null;
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var e in m.files)
                    if (e != null && !string.IsNullOrEmpty(e.name) && !string.IsNullOrEmpty(e.sha256))
                        d[e.name] = e.sha256.ToLowerInvariant();
                return d.Count > 0 ? d : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DreamPark] Could not read " + ManifestPath + ": " + e.Message);
                return null;
            }
        }

        // ── retired example folders ───────────────────────────────────────

        private static void RetireExampleFolders(List<string> removed)
        {
            foreach (var folder in RetiredFolders)
            {
                string abs = Abs(folder);
                if (!Directory.Exists(abs) && !File.Exists(abs + ".meta")) continue;
                bool ok = AssetDatabase.IsValidFolder(folder) && AssetDatabase.DeleteAsset(folder);
                if (!ok)
                {
                    FileUtil.DeleteFileOrDirectory(folder);
                    FileUtil.DeleteFileOrDirectory(folder + ".meta");
                    ok = !Directory.Exists(abs);
                }
                if (ok) removed.Add(folder + "/");
                else Debug.LogWarning("[DreamPark] Could not delete the retired XLua folder " + folder + ". Delete it by hand.");
            }
        }

        // ── post-import reconcile ─────────────────────────────────────────

        internal static bool Touches(string[] assetPaths)
        {
            if (assetPaths == null) return false;
            foreach (var p in assetPaths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (p == ManifestPath || p.StartsWith(GenFolder + "/", StringComparison.Ordinal)) return true;
                foreach (var f in RetiredFolders)
                    if (p == f || p.StartsWith(f + "/", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// Returns Gen/ to the SDK's shipped set after an import. Idempotent and cheap
        /// when there is nothing to do; safe to call at any time.
        public static void Reconcile(string trigger)
        {
            if (reconciling || EditorApplication.isPlayingOrWillChangePlaymode) return;
            reconciling = true;
            var removed = new List<string>();
            try
            {
                RetireExampleFolders(removed);

                var manifest = ReadManifest();
                string genAbs = Abs(GenFolder);
                if (manifest == null || !Directory.Exists(genAbs)) return;

                var onDisk = ListGenFiles();
                if (onDisk.All(manifest.ContainsKey)) return;   // nothing foreign: done

                // Unity gives a package asset a " 1" name when a DIFFERENT asset (another
                // GUID) already owns the path — typically a wrapper this project once
                // generated itself. If the copy is exactly what the SDK shipped, it wins.
                foreach (var name in onDisk.ToList())
                {
                    var m = UnityCopySuffix.Match(name);
                    if (!m.Success) continue;
                    string original = m.Groups["stem"].Value + m.Groups["ext"].Value;
                    if (!manifest.TryGetValue(original, out var shipped)) continue;
                    if (Hash(File.ReadAllBytes(Path.Combine(genAbs, name))) != shipped) continue;
                    string originalAsset = GenFolder + "/" + original;
                    if (File.Exists(Path.Combine(genAbs, original)) && !AssetDatabase.DeleteAsset(originalAsset)) continue;
                    string err = AssetDatabase.MoveAsset(GenFolder + "/" + name, originalAsset);
                    if (string.IsNullOrEmpty(err)) removed.Add("Gen/" + original + " (replaced by the SDK's copy, " + name + ")");
                    else Debug.LogWarning("[DreamPark] Could not rename " + name + " to " + original + ": " + err);
                }

                onDisk = ListGenFiles();
                bool intact = manifest.All(kv =>
                    onDisk.Contains(kv.Key) &&
                    Hash(File.ReadAllBytes(Path.Combine(genAbs, kv.Key))) == kv.Value);
                if (!intact) return;   // a local regen or a partial import: leave it alone

                foreach (var name in onDisk.Where(n => !manifest.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal).ToList())
                {
                    string asset = GenFolder + "/" + name;
                    if (AssetDatabase.DeleteAsset(asset)) { removed.Add("Gen/" + name); continue; }
                    FileUtil.DeleteFileOrDirectory(asset);
                    FileUtil.DeleteFileOrDirectory(asset + ".meta");
                    if (!File.Exists(Path.Combine(genAbs, name))) removed.Add("Gen/" + name);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DreamPark] XLua generated-code cleanup failed (" + trigger + "): " + e.Message);
            }
            finally
            {
                reconciling = false;
                if (removed.Count > 0)
                    Debug.Log($"[DreamPark] Removed {removed.Count} stale XLua item(s) after {trigger} — " +
                              "files the installed SDK no longer ships:\n  " + string.Join("\n  ", removed));
            }
        }

        // ── consistency rules (mirrored by scripts/release/check_sdk_upgrade.py) ──

        public sealed class ConsistencyReport
        {
            public readonly List<string> errors = new List<string>();
            public readonly List<string> stale = new List<string>();
            public bool IsClean => errors.Count == 0 && stale.Count == 0;

            public string Format()
            {
                var sb = new StringBuilder();
                foreach (var e in errors) sb.Append("    ✗ ").AppendLine(e);
                foreach (var s in stale) sb.Append("    ✗ ").AppendLine(s);
                return sb.ToString();
            }
        }

        private static IEnumerable<string> TranslatorSources()
        {
            string src = Abs(XLuaSrcFolder);
            if (!Directory.Exists(src)) yield break;
            foreach (var f in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                if (f.Replace('\\', '/').Contains("/Editor/")) continue;
                string text = File.ReadAllText(f);
                if (text.Contains("class ObjectTranslator")) yield return text;
            }
        }

        /// Internal consistency of one Gen/ folder's files. Errors break the compile;
        /// stale entries compile but are leftovers (unregistered wrappers).
        public static ConsistencyReport CheckConsistency(Dictionary<string, byte[]> gen)
        {
            var r = new ConsistencyReport();
            var texts = gen.Where(kv => kv.Key.EndsWith(".cs", StringComparison.Ordinal))
                           .ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetString(kv.Value), StringComparer.Ordinal);

            foreach (var name in gen.Keys.OrderBy(n => n, StringComparer.Ordinal))
            {
                var m = UnityCopySuffix.Match(name);
                if (m.Success && gen.ContainsKey(m.Groups["stem"].Value + m.Groups["ext"].Value))
                    r.errors.Add($"duplicate import copy: {name}");
            }

            var defined = new HashSet<string>(StringComparer.Ordinal);
            var translatorTexts = TranslatorSources().ToList();
            if (texts.TryGetValue("WrapPusher.cs", out var pusher)) translatorTexts.Add(pusher);
            foreach (var t in translatorTexts)
                if (t.Contains("class ObjectTranslator"))
                    foreach (Match m in MethodDef.Matches(t)) defined.Add(m.Groups[1].Value);
            foreach (var kv in texts.OrderBy(k => k.Key, StringComparer.Ordinal))
                foreach (var helper in HelperUse.Matches(kv.Value).Cast<Match>().Select(m => m.Groups[1].Value)
                                                .Where(h => !defined.Contains(h)).Distinct().OrderBy(h => h, StringComparer.Ordinal))
                    r.errors.Add($"{kv.Key}: calls ObjectTranslator.{helper}, which no generated or runtime file declares");

            var declared = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var kv in texts)
                foreach (Match m in ClassDecl.Matches(kv.Value))
                {
                    if (m.Groups[1].Success && m.Groups[1].Length > 0) continue;   // partial
                    string cls = m.Groups[2].Value;
                    if (!declared.TryGetValue(cls, out var files)) declared[cls] = files = new List<string>();
                    files.Add(kv.Key);
                }
            foreach (var kv in declared.Where(d => d.Value.Count > 1).OrderBy(d => d.Key, StringComparer.Ordinal))
                r.errors.Add($"class {kv.Key} is declared in {kv.Value.Count} files: {string.Join(", ", kv.Value.OrderBy(f => f, StringComparer.Ordinal))}");

            HashSet<string> registered = null;
            if (texts.TryGetValue("XLuaGenAutoRegister.cs", out var reg))
            {
                registered = new HashSet<string>(RegisterRef.Matches(reg).Cast<Match>().Select(m => m.Groups[1].Value), StringComparer.Ordinal);
                foreach (var cls in registered.Where(c => !declared.ContainsKey(c)).OrderBy(c => c, StringComparer.Ordinal))
                    r.errors.Add($"XLuaGenAutoRegister.cs names {cls}, which no Gen file declares");
            }
            else if (texts.Count > 0) r.errors.Add("XLuaGenAutoRegister.cs is missing");

            foreach (var kv in texts.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var hits = RetiredNamespaceRef.Matches(kv.Value).Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
                if (hits.Count > 0)
                    r.errors.Add($"{kv.Key}: references retired XLua example namespace(s) {string.Join(", ", hits.OrderBy(h => h))}");
            }

            if (registered != null)
                foreach (var kv in declared.OrderBy(d => d.Key, StringComparer.Ordinal))
                    if ((kv.Key.EndsWith("Wrap", StringComparison.Ordinal) || kv.Key.EndsWith("Bridge", StringComparison.Ordinal))
                        && !registered.Contains(kv.Key))
                        r.stale.Add($"{kv.Value[0]}: {kv.Key} is not registered by XLuaGenAutoRegister.cs (orphaned wrapper)");
            return r;
        }

        // ── clean regeneration ────────────────────────────────────────────

        private static string DeterministicGuid(string fileName)
        {
            using (var md5 = MD5.Create())
            {
                var sb = new StringBuilder(32);
                foreach (var b in md5.ComputeHash(Encoding.UTF8.GetBytes("DreamPark.XLuaGen/" + fileName))) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// Runs XLua codegen into Temp/, verifies the output, then makes Gen/ exactly
        /// that output. Returns false (Gen/ untouched) if codegen throws or its output
        /// is inconsistent.
        public static bool RegenerateClean(out string report)
        {
            var removed = new List<string>();
            RetireExampleFolders(removed);

            string staging = Path.Combine(ProjectRoot, "Temp", "DreamParkXLuaGen");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);

            string previousPath = CSObjectWrapEditor.GeneratorConfig.common_path;
            try
            {
                CSObjectWrapEditor.GeneratorConfig.common_path = staging.Replace('\\', '/') + "/";
                CSObjectWrapEditor.Generator.GenAll();
            }
            catch (Exception e)
            {
                report = "XLua codegen threw, so Gen/ was left exactly as it was.\n" + e;
                return false;
            }
            finally
            {
                CSObjectWrapEditor.GeneratorConfig.common_path = previousPath;
            }

            var staged = ReadFolder(staging);
            if (!staged.ContainsKey("XLuaGenAutoRegister.cs") || !staged.ContainsKey("WrapPusher.cs"))
            {
                report = "XLua codegen produced no XLuaGenAutoRegister.cs / WrapPusher.cs (see the Console), so Gen/ was left exactly as it was.";
                return false;
            }
            var check = CheckConsistency(staged);
            if (check.errors.Count > 0)
            {
                report = "XLua codegen output is inconsistent, so Gen/ was left exactly as it was:\n" + check.Format();
                return false;
            }

            string genAbs = Abs(GenFolder);
            Directory.CreateDirectory(genAbs);
            var current = ReadFolder(genAbs);
            int written = 0, unchanged = 0;
            var deleted = new List<string>();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var kv in staged)
                {
                    string dest = Path.Combine(genAbs, kv.Key);
                    if (current.TryGetValue(kv.Key, out var old) && old.SequenceEqual(kv.Value)) { unchanged++; continue; }
                    File.WriteAllBytes(dest, kv.Value);
                    if (!File.Exists(dest + ".meta"))
                    {
                        string guid = DeterministicGuid(kv.Key);
                        if (string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)))
                            File.WriteAllText(dest + ".meta", "fileFormatVersion: 2\nguid: " + guid + "\n");
                    }
                    written++;
                }
                foreach (var name in current.Keys.Where(n => !staged.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal))
                {
                    string asset = GenFolder + "/" + name;
                    if (!AssetDatabase.DeleteAsset(asset))
                    {
                        FileUtil.DeleteFileOrDirectory(asset);
                        FileUtil.DeleteFileOrDirectory(asset + ".meta");
                    }
                    deleted.Add(name);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            AssetDatabase.Refresh();
            WriteManifest();

            var sb = new StringBuilder();
            sb.AppendLine($"✅ XLua wrappers regenerated: {written} written, {unchanged} unchanged, {deleted.Count} stale removed ({staged.Count} files in Gen/).");
            foreach (var d in deleted) sb.AppendLine("    removed Gen/" + d);
            foreach (var d in removed) sb.AppendLine("    removed retired " + d);
            foreach (var s in check.stale) sb.AppendLine("    note: " + s);
            report = sb.ToString();
            return true;
        }

        // ── SDK export gate ───────────────────────────────────────────────

        /// Read-only: would the current Gen/ be safe to ship?
        public static bool CheckExportReadiness(out string report)
        {
            var sb = new StringBuilder();
            bool ok = true;

            var integrity = LuaCodegenIntegrity.Check();
            if (!integrity.IsClean) { ok = false; sb.AppendLine(integrity.report); }

            var gen = ReadFolder(Abs(GenFolder));
            var consistency = CheckConsistency(gen);
            if (!consistency.IsClean)
            {
                ok = false;
                sb.AppendLine("Gen/ is not internally consistent:");
                sb.Append(consistency.Format());
            }
            foreach (var f in RetiredFolders)
                if (Directory.Exists(Abs(f))) sb.AppendLine("note: " + f + " exists and is removed before export.");

            if (ok) sb.Insert(0, $"✓ XLua generated code is complete and consistent ({gen.Count} files).\n");
            else sb.AppendLine("Fix: DreamPark ▸ Troubleshooting ▸ Generate XLua Code, let Unity recompile, then export again.");
            report = sb.ToString();
            return ok;
        }

        /// Called by every SDK export path immediately before ExportPackage.
        public static bool PrepareSdkExport(out string report)
        {
            var removed = new List<string>();
            RetireExampleFolders(removed);
            if (!CheckExportReadiness(out string readiness))
            {
                report = "SDK export blocked — this Gen/ would break projects that update to it.\n\n" + readiness;
                return false;
            }
            WriteManifest();
            report = readiness + (removed.Count > 0 ? "Removed " + string.Join(", ", removed) + "\n" : "") +
                     "Wrote " + ManifestPath;
            return true;
        }
    }

#if !DREAMPARKCORE
    // Runs inside the INSTALLED SDK while a new one is being imported, so it also
    // protects hand-imported .unitypackages, which never go through the updater.
    internal sealed class XLuaGenGuardPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (XLuaGenGuard.Touches(importedAssets) || XLuaGenGuard.Touches(movedAssets))
                XLuaGenGuard.Reconcile("an import");
        }
    }

    [InitializeOnLoad]
    internal static class XLuaGenGuardBoot
    {
        static XLuaGenGuardBoot()
        {
            EditorApplication.delayCall += () => XLuaGenGuard.Reconcile("editor load");
        }
    }
#endif
}
#endif
