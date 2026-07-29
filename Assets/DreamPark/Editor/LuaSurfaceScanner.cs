// ─────────────────────────────────────────────────────────────────────
//  LuaSurfaceScanner.cs — SDK-synced editor tooling
//
//  Catches, at authoring time, the two ways creator Lua can look perfectly
//  healthy in the Editor and then do nothing on a headset.
//
//  1. UNREGISTERED  — the type has no XLua wrapper (it is not in
//     DreamParkLuaConfig.LuaCallCSharp). In the Editor, Mono reflection
//     covers for it and everything works. On Quest and iOS the player is
//     IL2CPP with managedStrippingLevel:High, so the call goes down a
//     reflection path that is fragile for AOT-hostile shapes — notably a
//     struct passed by `out`. That is the Zombiez bug in one line:
//     AI.NavMesh.SamplePosition(pos, out NavMeshHit, ...) silently failed on
//     device, inside a pcall, so zombies rotated but never moved and nothing
//     reached the log.
//
//  2. BLOCKED — the type is denied by LuaSecuritySandbox, which is compiled
//     ONLY into the core app (#if DREAMPARKCORE). Creators develop entirely
//     unsandboxed, so System.IO or Resources works on their machine and then
//     throws the moment the content runs at a venue.
//
//  Both failures share a shape: the SDK Editor is a strictly more permissive
//  environment than the app. Nothing you can run locally will reproduce
//  either one — which is exactly why this scan exists.
//
//  WHY IT MATTERS MORE THAN IT LOOKS: content ships OVER THE AIR, but the
//  XLua wrappers are AOT code compiled into the app. A type you need that
//  nobody registered cannot be fixed by publishing content — it needs an app
//  rebuild and a store release. Finding it here costs a minute. Finding it
//  after launch costs a release cycle.
//
//  July 2026 — two gaps closed:
//
//    • It only ever ran from a menu item. Nothing called it: not the build,
//      not the content uploader, not the docs. A creator who never opened
//      DreamPark ▸ Troubleshooting shipped blind. Analyze() now exposes the
//      result programmatically and LuaSurfaceGate runs it at upload and build.
//
//    • BuildUnityTypeIndex only indexed assemblies named UnityEngine*, so
//      DREAMPARK'S OWN types were structurally invisible to it. The live
//      example: zombie_ai.lua.txt does
//      GetComponent(typeof(CS.DreamPark.FloorAnchor)) — load-bearing, since
//      FloorAnchor rewrites localPosition every frame — and FloorAnchor was
//      missing from the config. The scan could never have told anyone.
//      Project types are now indexed by FULL name and matched only against
//      fully-qualified CS.* references, so precision does not depend on the
//      capitalised-identifier heuristic used for Unity types.
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
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using XLua;

public static class LuaSurfaceScanner
{
    /// <summary>Outcome of one scan. `report` is the human-readable block.</summary>
    public class ScanResult
    {
        public int fileCount;
        public int refCount;
        public SortedDictionary<string, SortedSet<string>> blocked =
            new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        public SortedDictionary<string, SortedSet<string>> unregistered =
            new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        public string report = "";

        public bool HasBlocked => blocked.Count > 0;
        public bool HasUnregistered => unregistered.Count > 0;
        public bool IsClean => !HasBlocked && !HasUnregistered;

        public string BlockedSummary => string.Join(", ", blocked.Keys);
        public string UnregisteredSummary => string.Join(", ", unregistered.Keys);
    }

    [MenuItem("DreamPark/Troubleshooting/Scan Lua API Surface", false, 210)]
    public static void Scan()
    {
        var result = Analyze();
        if (result == null) return;

        if (result.HasBlocked) Debug.LogError(result.report);
        else if (result.HasUnregistered) Debug.LogWarning(result.report);
        else Debug.Log(result.report);
    }

    /// <summary>
    /// Runs the scan and returns the result instead of logging it. Returns null
    /// when there is nothing to scan (no Assets/Content, no scripts) — that is
    /// "not applicable", not "clean", and callers should treat it as a pass.
    /// </summary>
    public static ScanResult Analyze()
    {
        string contentRoot = "Assets/Content";
        if (!AssetDatabase.IsValidFolder(contentRoot))
            return null;

        string[] files;
        try
        {
            files = Directory.GetFiles(contentRoot, "*.lua.txt", SearchOption.AllDirectories)
                             .Where(p => p.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) < 0
                                      && p.IndexOf("\\ThirdPartyLocal\\", StringComparison.OrdinalIgnoreCase) < 0)
                             .ToArray();
        }
        catch (Exception e) { Debug.LogError("[LuaScan] " + e.Message); return null; }

        if (files.Length == 0) return null;

        HashSet<Type> registered = BuildRegisteredSet();
        Dictionary<string, Type> unityTypes = BuildUnityTypeIndex();
        Dictionary<string, Type> projectTypes = BuildProjectTypeIndex();

        var result = new ScanResult { fileCount = files.Length };

        foreach (string file in files)
        {
            string src;
            try { src = File.ReadAllText(file); } catch { continue; }
            string shortName = Path.GetFileName(file);

            foreach (string fqn in ExtractQualified(src))
            {
                result.refCount++;
                if (IsSandboxBlocked(fqn))
                {
                    Add(result.blocked, fqn, shortName);
                    continue;
                }
                // Project/DreamPark types: matched on the FULLY-QUALIFIED name only.
                // The bare-identifier heuristic below is fine for Unity types, whose
                // names are distinctive, but would produce constant false positives
                // against a project assembly full of ordinary words.
                if (projectTypes.TryGetValue(fqn, out Type pt) && !registered.Contains(pt))
                    Add(result.unregistered, pt.FullName ?? fqn, shortName);
            }

            foreach (string simple in ExtractCandidates(src))
            {
                if (!unityTypes.TryGetValue(simple, out Type t)) continue;   // not a real Unity type
                result.refCount++;
                if (registered.Contains(t)) continue;
                Add(result.unregistered, t.FullName ?? simple, shortName);
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("═══ Lua API surface scan ═══");
        sb.AppendLine($"{result.fileCount} script(s), {result.refCount} type reference(s), " +
                      $"{registered.Count} registered type(s).");
        sb.AppendLine();

        if (result.HasBlocked)
        {
            sb.AppendLine($"⛔ BLOCKED BY THE PRODUCTION SANDBOX ({result.blocked.Count}) — these THROW in the app:");
            foreach (var kv in result.blocked)
                sb.AppendLine($"    {kv.Key}\n        {string.Join(", ", kv.Value)}");
            sb.AppendLine();
        }

        if (result.HasUnregistered)
        {
            sb.AppendLine($"⚠ NOT REGISTERED FOR CODEGEN ({result.unregistered.Count}) — reflection-only on device:");
            foreach (var kv in result.unregistered)
                sb.AppendLine($"    {kv.Key}\n        {string.Join(", ", kv.Value)}");
            sb.AppendLine();
            sb.AppendLine("    Add these to DreamParkLuaConfig.LuaCallCSharp, run");
            sb.AppendLine("    DreamPark ▸ Troubleshooting ▸ Generate XLua Code, and rebuild the app.");
            sb.AppendLine("    Most will still work via reflection — but anything passing a struct");
            sb.AppendLine("    by `out` (NavMesh.SamplePosition is the known one) will fail silently.");
            sb.AppendLine();
        }

        if (result.IsClean)
            sb.AppendLine("✓ Every type referenced from Lua is registered and permitted.");

        result.report = sb.ToString();
        return result;
    }

    private static void Add(SortedDictionary<string, SortedSet<string>> map, string key, string file)
    {
        if (!map.TryGetValue(key, out var set)) map[key] = set = new SortedSet<string>(StringComparer.Ordinal);
        set.Add(file);
    }

    /// Types XLua will generate wrappers for: the explicit config list plus
    /// anything carrying [LuaCallCSharp] directly (LuaBehaviour, EasyLua,
    /// GameStorageAPI, ProfileAPI, DreamParkLuaAPI, …). The attribute half used
    /// to be described in a comment but never actually collected, so every
    /// self-registering type read as unregistered.
    private static HashSet<Type> BuildRegisteredSet()
    {
        var set = new HashSet<Type>();
        try
        {
            if (DreamParkLuaConfig.LuaCallCSharp != null)
                foreach (var t in DreamParkLuaConfig.LuaCallCSharp) if (t != null) set.Add(t);
        }
        catch (Exception e) { Debug.LogWarning("[LuaScan] Couldn't read DreamParkLuaConfig: " + e.Message); }

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
                try
                {
                    if (t.IsDefined(typeof(LuaCallCSharpAttribute), false)) set.Add(t);
                }
                catch { /* type failed to load — nothing to register */ }
            }
        }
        return set;
    }

    /// Simple-name → Type index over the assemblies creator Lua actually reaches.
    private static Dictionary<string, Type> BuildUnityTypeIndex()
    {
        var index = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name = asm.GetName().Name;
            bool relevant = name.StartsWith("UnityEngine", StringComparison.Ordinal)
                            || name == "Unity.TextMeshPro" || name == "UnityEngine.UI";
            if (!relevant) continue;
            if (name.IndexOf("Editor", StringComparison.Ordinal) >= 0) continue;

            Type[] types;
            try { types = asm.GetExportedTypes(); } catch { continue; }
            foreach (var t in types)
            {
                if (t == null || t.IsNested || string.IsNullOrEmpty(t.Name)) continue;
                string ns = t.Namespace ?? "";
                if (ns.StartsWith("UnityEditor", StringComparison.Ordinal)) continue;
                if (!index.ContainsKey(t.Name)) index[t.Name] = t;
            }
        }
        return index;
    }

    /// FULL-name → Type index over this project's own runtime assemblies —
    /// DreamPark's SDK types and anything the creator compiled themselves.
    ///
    /// Identified by location rather than by name: a compiled project assembly
    /// lives in Library/ScriptAssemblies, which is exact, whereas a name-prefix
    /// allowlist would have to be updated for every asmdef anyone ever adds.
    private static Dictionary<string, Type> BuildProjectTypeIndex()
    {
        var index = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name = asm.GetName().Name;
            if (name.IndexOf("Editor", StringComparison.Ordinal) >= 0) continue;

            string loc;
            try { loc = asm.Location; } catch { continue; }
            if (string.IsNullOrEmpty(loc)) continue;
            if (loc.Replace('\\', '/').IndexOf("/Library/ScriptAssemblies/", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            Type[] types;
            try { types = asm.GetExportedTypes(); } catch { continue; }
            foreach (var t in types)
            {
                if (t == null || t.IsNested || string.IsNullOrEmpty(t.FullName)) continue;
                string ns = t.Namespace ?? "";
                if (ns.StartsWith("UnityEditor", StringComparison.Ordinal)) continue;
                if (!index.ContainsKey(t.FullName)) index[t.FullName] = t;
            }
        }
        return index;
    }

    // CS.Foo.Bar / typeof(CS.Foo.Bar) — fully-qualified, high confidence.
    private static readonly Regex QualifiedRx =
        new Regex(@"CS\.([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)", RegexOptions.Compiled);

    // UE.X / AI.X aliases, and bare identifiers used as X.member or X(...).
    private static readonly Regex AliasRx =
        new Regex(@"\b(?:UE|AI)\.([A-Z][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly Regex BareRx =
        new Regex(@"\b([A-Z][A-Za-z0-9_]{2,})\s*[.(:]", RegexOptions.Compiled);

    private static IEnumerable<string> ExtractQualified(string src)
    {
        foreach (Match m in QualifiedRx.Matches(src)) yield return m.Groups[1].Value;
    }

    private static IEnumerable<string> ExtractCandidates(string src)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in AliasRx.Matches(src)) if (seen.Add(m.Groups[1].Value)) yield return m.Groups[1].Value;
        foreach (Match m in BareRx.Matches(src))  if (seen.Add(m.Groups[1].Value)) yield return m.Groups[1].Value;
        foreach (Match m in QualifiedRx.Matches(src))
        {
            string fqn = m.Groups[1].Value;
            string leaf = fqn.Substring(fqn.LastIndexOf('.') + 1);
            if (leaf.Length > 0 && char.IsUpper(leaf[0]) && seen.Add(leaf)) yield return leaf;
        }
    }

    // ── Sandbox mirror ───────────────────────────────────────────────
    // Kept in sync BY HAND with LuaSecuritySandbox.GUARD. That file lives in
    // core's Assets/Scripts (not the SDK) and keeps its denylist inside a Lua
    // string, so it cannot be referenced from here — the SDK does not even ship
    // it. If you change the sandbox, change this list too.
    private static readonly string[] DeniedPrefixes = {
        "System.IO", "System.Reflection", "System.Diagnostics", "System.Threading",
        "System.Net", "System.Activator", "System.AppDomain", "System.Environment",
        "System.Runtime", "System.GC", "System.Security", "System.Type",
        "System.CodeDom", "System.Configuration", "System.Xml",
        "UnityEngine.Resources", "UnityEngine.PlayerPrefs",
        "Microsoft", "Mono", "UnityEditor",
    };

    private static bool IsSandboxBlocked(string fqn)
    {
        foreach (string p in DeniedPrefixes)
            if (fqn == p || fqn.StartsWith(p + ".", StringComparison.Ordinal)) return true;
        return false;
    }
}
#endif
