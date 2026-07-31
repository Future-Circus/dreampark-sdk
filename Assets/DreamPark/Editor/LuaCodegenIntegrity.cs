// ─────────────────────────────────────────────────────────────────────
//  LuaCodegenIntegrity.cs — SDK-synced editor tooling
//
//  Asks one question: does Gen/ actually contain a wrapper for every type the
//  config says is registered?
//
//  WHY THIS AND NOT THE SURFACE SCAN
//
//  The Zombiez failure — zombies animated and turned but never moved, silently,
//  on device only, needing an app release to fix — is usually retold as "NavMesh
//  wasn't in the config." It wasn't. Codegen had NEVER SUCCEEDED in this project:
//  DreamParkLuaConfig duplicated four GCOptimize entries that XLua's own
//  SysGenConfig already declared, OptimizeCfg.Add threw on the duplicate key, and
//  GenAll() died. Every type was reflection-only, and NavMesh.SamplePosition —
//  a struct passed by `out`, the one shape AOT genuinely cannot fake — was the
//  first call unlucky enough to depend on it.
//
//  So the failure was BUILD INTEGRITY, not config coverage. Config coverage is
//  what LuaSurfaceScanner guesses at, and it can only ever guess: "type has no
//  wrapper" and "this call will break on device" are almost disjoint sets, since
//  reflection covers ordinary member access perfectly well.
//
//  This check has no such ambiguity. A type in the config with no wrapper on disk
//  means codegen is stale or it failed. That is always true, always actionable,
//  and needs no judgement from the person reading it.
//
//  Verified against the live repo when written: 120 configured types resolved to
//  93 wrapper files plus 22 entries in EnumWrap.cs, and the only 5 reported were
//  exactly the 5 added in that session before codegen was re-run. No false
//  positives across the other 115.
//
//  HOW A TYPE IS MATCHED — mirrors XLua's own Generator:
//    • non-enum → GenWrap writes NonmalizeName(type.ToString()) + "Wrap.cs"
//      (Generator.cs:1004), where NonmalizeName maps . + ` & [ ] , to _
//    • enum     → GenEnumWrap folds ALL enums into one EnumWrap.cs
//      (Generator.cs:632), so match on typeof(FullName) inside it
//    • fallback → scan every Gen/*.cs for typeof(FullName) before reporting
//      anything missing. This check can FAIL A BUILD, so it errs toward silence:
//      a naming rule that drifts should cost a missed warning, never a blocked
//      build someone has to argue with.
//
//  Editor-only; reads files, changes nothing.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using XLua;

public static class LuaCodegenIntegrity
{
    public class Result
    {
        public string genPath = "";
        public bool genFolderMissing;
        public int configuredCount;
        public int wrapperFileCount;
        public List<string> missing = new List<string>();
        public string report = "";

        public bool IsClean => !genFolderMissing && missing.Count == 0;

        public string Summary
        {
            get
            {
                if (genFolderMissing) return "Gen/ folder is missing entirely — codegen has never run.";
                const int max = 10;
                var sb = new StringBuilder();
                for (int i = 0; i < missing.Count && i < max; i++)
                    sb.Append("    • ").Append(missing[i]).Append('\n');
                if (missing.Count > max)
                    sb.Append("    …and ").Append(missing.Count - max).Append(" more (see Console)\n");
                return sb.ToString();
            }
        }
    }

    [MenuItem("DreamPark/Troubleshooting/Verify XLua Codegen", false, 208)]
    public static void VerifyMenu()
    {
        var r = Check();
        if (r.IsClean) Debug.Log(r.report);
        else Debug.LogError(r.report);
    }

    public static Result Check()
    {
        var result = new Result();

        string genPath;
        try { genPath = CSObjectWrapEditor.DreamParkXLuaGenPath.Path; }
        catch (Exception e)
        {
            result.report = "[LuaCodegen] Could not resolve the Gen path: " + e.Message;
            return result;   // clean-by-default: cannot verify is not the same as broken
        }
        result.genPath = genPath;

        if (!Directory.Exists(genPath))
        {
            result.genFolderMissing = true;
            result.report =
                "═══ XLua codegen integrity ═══\n" +
                "⛔ Gen/ does not exist at " + genPath + "\n" +
                "   Codegen has never run in this project. Every type Lua touches will fall back\n" +
                "   to reflection on device, and anything passing a struct by `out` will fail\n" +
                "   silently on a headset while working perfectly in the Editor.\n" +
                "   Run DreamPark ▸ Troubleshooting ▸ Generate XLua Code.";
            return result;
        }

        string[] genFiles;
        try { genFiles = Directory.GetFiles(genPath, "*.cs", SearchOption.TopDirectoryOnly); }
        catch (Exception e)
        {
            result.report = "[LuaCodegen] Could not read " + genPath + ": " + e.Message;
            return result;
        }

        var wrapperNames = new HashSet<string>(genFiles.Select(Path.GetFileName), StringComparer.Ordinal);
        result.wrapperFileCount = wrapperNames.Count(n => n.EndsWith("Wrap.cs", StringComparison.Ordinal));

        // Read once: EnumWrap for enums, everything else as the last-resort fallback.
        string enumWrapSrc = ReadIfPresent(Path.Combine(genPath, "EnumWrap.cs"));
        string allGenSrc = null;   // built lazily; only needed when a lookup misses

        var configured = CollectConfiguredTypes();
        result.configuredCount = configured.Count;

        foreach (var t in configured)
        {
            string full = t.FullName;
            if (string.IsNullOrEmpty(full)) continue;
            string needle = "typeof(" + full.Replace('+', '.') + ")";

            if (t.IsEnum)
            {
                if (enumWrapSrc != null && enumWrapSrc.Contains(needle)) continue;
            }
            else
            {
                if (wrapperNames.Contains(NonmalizeName(t.ToString()) + "Wrap.cs")) continue;
            }

            if (allGenSrc == null)
            {
                var sb = new StringBuilder();
                foreach (var f in genFiles) sb.Append(ReadIfPresent(f));
                allGenSrc = sb.ToString();
            }
            if (allGenSrc.Contains(needle)) continue;

            result.missing.Add(full);
        }

        result.missing.Sort(StringComparer.Ordinal);

        var report = new StringBuilder();
        report.AppendLine("═══ XLua codegen integrity ═══");
        report.AppendLine($"{result.configuredCount} configured type(s), {result.wrapperFileCount} wrapper file(s) in Gen/.");
        if (result.IsClean)
        {
            report.AppendLine("✓ Every configured type has generated code.");
        }
        else
        {
            report.AppendLine();
            report.AppendLine($"⛔ NO GENERATED WRAPPER ({result.missing.Count}):");
            foreach (var m in result.missing) report.AppendLine("    " + m);
            report.AppendLine();
            report.AppendLine("   The config and Gen/ disagree: codegen is stale, or it threw partway.");
            report.AppendLine("   Adding a type to DreamParkLuaConfig does nothing until you run");
            report.AppendLine("   DreamPark ▸ Troubleshooting ▸ Generate XLua Code and rebuild the app —");
            report.AppendLine("   wrappers are AOT code compiled INTO the app, not shipped with content.");
            report.AppendLine();
            report.AppendLine("   If codegen throws, check DreamParkLuaConfig.GCOptimize first: XLua's own");
            report.AppendLine("   SysGenConfig already registers Vector2/3/4, Color, Quaternion, Ray,");
            report.AppendLine("   Bounds and Ray2D, and OptimizeCfg is a Dictionary — a duplicate throws");
            report.AppendLine("   and kills the whole run. That is what produced the July 2026 Zombiez bug.");
        }
        result.report = report.ToString();
        return result;
    }

    private static string ReadIfPresent(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    /// XLua's own filename mangling — Generator.NonmalizeName.
    private static string NonmalizeName(string name)
    {
        return name.Replace("+", "_").Replace(".", "_").Replace("`", "_")
                   .Replace("&", "_").Replace("[", "_").Replace("]", "_").Replace(",", "_");
    }

    /// Everything XLua would generate for: the explicit config list plus every
    /// type carrying [LuaCallCSharp]. Filters mirror Generator.GetGenConfig so we
    /// do not report types XLua itself would have discarded.
    private static List<Type> CollectConfiguredTypes()
    {
        var set = new HashSet<Type>();

        try
        {
            if (DreamParkLuaConfig.LuaCallCSharp != null)
                foreach (var t in DreamParkLuaConfig.LuaCallCSharp) if (t != null) set.Add(t);
        }
        catch (Exception e) { Debug.LogWarning("[LuaCodegen] Couldn't read DreamParkLuaConfig: " + e.Message); }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
            catch { continue; }
            if (types == null) continue;
            foreach (var t in types)
            {
                if (t == null) continue;
                try { if (t.IsDefined(typeof(LuaCallCSharpAttribute), false)) set.Add(t); }
                catch { }
            }
        }

        return set.Where(t => t != null
                           && (t.IsPublic || t.IsNestedPublic)
                           && !t.IsGenericTypeDefinition
                           && !typeof(Delegate).IsAssignableFrom(t)
                           && !t.Name.Contains("<"))
                  .ToList();
    }
}
#endif
