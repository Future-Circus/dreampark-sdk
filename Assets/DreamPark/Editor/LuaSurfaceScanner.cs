// ─────────────────────────────────────────────────────────────────────
//  LuaSurfaceScanner.cs — SDK-synced editor tooling
//
//  Catches, at authoring time, the two ways creator Lua can look perfectly
//  healthy in the Editor and then do nothing on a headset.
//
//  1. UNREGISTERED  — the type has no XLua wrapper (it is not in
//     DreamParkLuaConfig.LuaCallCSharp and does not carry [LuaCallCSharp]).
//     In the Editor, Mono reflection covers for it. On Quest and iOS the
//     player is IL2CPP with managedStrippingLevel:High, so the call goes down
//     a reflection path that is fragile for AOT-hostile shapes — notably a
//     struct passed by `out`. That is the Zombiez bug in one line:
//     AI.NavMesh.SamplePosition(pos, out NavMeshHit, ...) silently failed on
//     device, inside a pcall, so zombies rotated but never moved.
//
//  2. BLOCKED — the type is denied by LuaSecuritySandbox, which is compiled
//     ONLY into the core app (#if DREAMPARKCORE). Creators develop entirely
//     unsandboxed, so System.IO or Resources works on their machine and then
//     throws the moment the content runs at a venue.
//
//  Content ships OVER THE AIR, but the wrappers are AOT code compiled INTO
//  the app. A type nobody registered cannot be fixed by publishing content —
//  it needs an app rebuild and a store release.
//
// ─────────────────────────────────────────────────────────────────────
//  HOW IT RESOLVES NAMES, and why the first version cried wolf
//
//  The original matched SIMPLE names with a heuristic — any capitalised
//  identifier followed by `.`, `(` or `:` — and looked them up in a
//  simple-name index over every UnityEngine assembly. That shape is not a
//  type reference, it is a METHOD CALL, so it reported nonsense:
//
//      Vector3.Angle(a, b)   -> "Angle"  -> UnityEngine.UIElements.Angle
//      animator:Update(0)    -> "Update" -> UnityEngine.PlayerLoop.Update
//      Physics.RaycastAll(…) -> "RaycastAll", "Raycast", …
//
//  In one content pack, 89 of the names it produced existed ONLY as method
//  names. It also scanned COMMENTS, so a doc block listing components a
//  script can drive reported every one of them as a missing wrapper.
//
//  It now resolves FULLY-QUALIFIED names only:
//
//    1. Strip Lua comments. A comment is documentation, not a call site.
//    2. Collect alias declarations — `local UE = CS.UnityEngine`,
//       `local AI = CS.UnityEngine.AI`, `local Vector3 = UE.Vector3` — and
//       chase chains to a fixpoint. The right-hand side must be a bare member
//       path: `local ang = Vector3.Angle(...)` is a CALL, not an alias.
//    3. Expand every `alias.Member` use through that map, and take every
//       literal `CS.Foo.Bar`, producing candidate FULL names.
//    4. Look those up in one index keyed by FULL name, over the Unity
//       assemblies AND this project's own (so DreamPark's types are visible —
//       CS.DreamPark.FloorAnchor was structurally unreportable before).
//
//  `UnityEngine.Vector3.Angle` is still generated as a candidate; it simply
//  is not a type, so the lookup fails and it is ignored. Precision now comes
//  from the index instead of from guessing at capitalisation.
//
//  This trades recall for precision on purpose. A gate that blocks uploads
//  must not cry wolf: people learn to click through, and then it is worth
//  less than nothing. A type only reachable through an alias chain this does
//  not model will be missed — and that is the right direction to fail.
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
    /// <summary>
    /// A file may opt out of the scan with this marker — for an authoring or
    /// baking tool that lives under Assets/Content but never runs at a venue.
    /// Files under a folder named Editor/ are skipped for the same reason.
    /// Without this, an editor-only script calling UnityEditor.AssetDatabase
    /// would hard-block every upload.
    /// </summary>
    public const string EditorOnlyMarker = "@editor-only";

    public class Finding
    {
        public string type;
        public SortedSet<string> files = new SortedSet<string>(StringComparer.Ordinal);
        public override string ToString() => type + "  (" + string.Join(", ", files) + ")";
    }

    public class ScanResult
    {
        public int fileCount;
        public int skippedCount;
        public int candidateCount;
        public List<Finding> blocked = new List<Finding>();
        public List<Finding> unregistered = new List<Finding>();
        public string report = "";

        public bool HasBlocked => blocked.Count > 0;
        public bool HasUnregistered => unregistered.Count > 0;
        public bool IsClean => !HasBlocked && !HasUnregistered;

        /// <summary>Short list for a dialog. Unity truncates long message bodies.</summary>
        public static string Summarize(List<Finding> findings, int max = 8)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < findings.Count && i < max; i++)
                sb.Append("    • ").Append(findings[i]).Append('\n');
            if (findings.Count > max)
                sb.Append("    …and ").Append(findings.Count - max).Append(" more (see Console)\n");
            return sb.ToString();
        }
    }

    [MenuItem("DreamPark/Troubleshooting/Scan Lua API Surface", false, 210)]
    public static void Scan()
    {
        var result = Analyze();
        if (result == null) { Debug.Log("[LuaScan] Nothing to scan (no .lua.txt under Assets/Content)."); return; }

        if (result.HasBlocked) Debug.LogError(result.report);
        else if (result.HasUnregistered) Debug.LogWarning(result.report);
        else Debug.Log(result.report);
    }

    /// <summary>
    /// Runs the scan and returns the result instead of logging it. Null means
    /// "nothing to scan" — treat as a pass, not as clean.
    /// </summary>
    public static ScanResult Analyze()
    {
        string contentRoot = "Assets/Content";
        if (!AssetDatabase.IsValidFolder(contentRoot)) return null;

        string[] files;
        try
        {
            files = Directory.GetFiles(contentRoot, "*.lua.txt", SearchOption.AllDirectories)
                             .Where(p => !PathHasSegment(p, "ThirdPartyLocal"))
                             .ToArray();
        }
        catch (Exception e) { Debug.LogError("[LuaScan] " + e.Message); return null; }

        if (files.Length == 0) return null;

        HashSet<Type> registered = BuildRegisteredSet();
        Dictionary<string, Type> index = BuildTypeIndex();

        var blocked = new SortedDictionary<string, Finding>(StringComparer.Ordinal);
        var unregistered = new SortedDictionary<string, Finding>(StringComparer.Ordinal);
        var result = new ScanResult();

        foreach (string file in files)
        {
            string src;
            try { src = File.ReadAllText(file); } catch { continue; }

            if (PathHasSegment(file, "Editor") || src.IndexOf(EditorOnlyMarker, StringComparison.Ordinal) >= 0)
            {
                result.skippedCount++;
                continue;
            }

            result.fileCount++;
            string shortName = Path.GetFileName(file);

            foreach (string fqn in ResolveCandidates(src))
            {
                result.candidateCount++;

                // Sandbox check runs on the RESOLVED full name, so a denied type
                // reached through an alias (`UE.Resources`) is reported at the same
                // severity as a literal one (`CS.UnityEngine.Resources`). It used to
                // apply only to literals, so the alias form was downgraded from
                // "this throws at a venue" to a warning nobody had to act on.
                if (IsSandboxBlocked(fqn)) { Record(blocked, fqn, shortName); continue; }

                if (!index.TryGetValue(fqn, out Type t)) continue;   // not a type — a member, or nothing
                if (registered.Contains(t)) continue;
                Record(unregistered, t.FullName ?? fqn, shortName);
            }
        }

        result.blocked = blocked.Values.ToList();
        result.unregistered = unregistered.Values.ToList();

        var sb = new StringBuilder();
        sb.AppendLine("═══ Lua API surface scan ═══");
        sb.Append($"{result.fileCount} script(s) scanned");
        if (result.skippedCount > 0) sb.Append($", {result.skippedCount} skipped (editor-only)");
        sb.AppendLine($", {result.candidateCount} qualified reference(s), {registered.Count} registered type(s).");
        sb.AppendLine();

        if (result.HasBlocked)
        {
            sb.AppendLine($"⛔ BLOCKED BY THE PRODUCTION SANDBOX ({result.blocked.Count}) — these THROW in the app:");
            foreach (var f in result.blocked) sb.AppendLine("    " + f);
            sb.AppendLine();
            sb.AppendLine($"    If a file is an authoring tool that never runs at a venue, put");
            sb.AppendLine($"    `-- {EditorOnlyMarker}` in it (or move it under an Editor/ folder) to exclude it.");
            sb.AppendLine();
        }

        if (result.HasUnregistered)
        {
            sb.AppendLine($"⚠ NOT REGISTERED FOR CODEGEN ({result.unregistered.Count}) — reflection-only on device:");
            foreach (var f in result.unregistered) sb.AppendLine("    " + f);
            sb.AppendLine();
            sb.AppendLine("    Add these to DreamParkLuaConfig.LuaCallCSharp, run");
            sb.AppendLine("    DreamPark ▸ Troubleshooting ▸ Generate XLua Code, and rebuild the app.");
            sb.AppendLine("    Most still work via reflection — but anything passing a struct by `out`");
            sb.AppendLine("    (NavMesh.SamplePosition is the known one) fails silently on a headset.");
            sb.AppendLine();
        }

        if (result.IsClean)
            sb.AppendLine("✓ Every type referenced from Lua is registered and permitted.");

        result.report = sb.ToString();
        return result;
    }

    private static bool PathHasSegment(string path, string segment)
    {
        string p = path.Replace('\\', '/');
        return p.IndexOf("/" + segment + "/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void Record(SortedDictionary<string, Finding> map, string key, string file)
    {
        if (!map.TryGetValue(key, out var f)) map[key] = f = new Finding { type = key };
        f.files.Add(file);
    }

    // ── Name resolution ──────────────────────────────────────────────

    // Lua comments: long form first (--[[ … ]] / --[==[ … ]==]), then to-EOL.
    private static readonly Regex LongCommentRx =
        new Regex(@"--\[(=*)\[.*?\]\1\]", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LineCommentRx =
        new Regex(@"--[^\n]*", RegexOptions.Compiled);

    // `local UE = CS.UnityEngine`  — RHS must be a bare path, not a call or index.
    private static readonly Regex CsAliasRx =
        new Regex(@"local\s+([A-Za-z_]\w*)\s*=\s*CS\.([A-Za-z_][\w.]*)\s*(?![\w.(\[])", RegexOptions.Compiled);
    // `local Vector3 = UE.Vector3` — chained off an alias already known.
    private static readonly Regex ChainAliasRx =
        new Regex(@"local\s+([A-Za-z_]\w*)\s*=\s*([A-Za-z_]\w*)\.([A-Za-z_]\w*)\s*(?![\w.(\[])", RegexOptions.Compiled);
    // Literal CS.Foo.Bar anywhere.
    private static readonly Regex QualifiedRx =
        new Regex(@"CS\.([A-Za-z_][\w.]*)", RegexOptions.Compiled);
    // Any `root.member` use, expanded only when `root` is a known alias.
    private static readonly Regex MemberUseRx =
        new Regex(@"\b([A-Za-z_]\w*)\.([A-Za-z_]\w*)", RegexOptions.Compiled);

    /// <summary>Fully-qualified candidate names referenced by this script.</summary>
    private static HashSet<string> ResolveCandidates(string src)
    {
        src = LongCommentRx.Replace(src, " ");
        src = LineCommentRx.Replace(src, " ");

        var alias = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in CsAliasRx.Matches(src))
            alias[m.Groups[1].Value] = m.Groups[2].Value;

        // Chains are usually one deep (CS.UnityEngine -> UE -> UE.Vector3); three
        // passes is a cheap fixpoint that also covers declaration order.
        for (int pass = 0; pass < 3; pass++)
        {
            foreach (Match m in ChainAliasRx.Matches(src))
            {
                string name = m.Groups[1].Value, root = m.Groups[2].Value, member = m.Groups[3].Value;
                if (alias.ContainsKey(name)) continue;
                if (alias.TryGetValue(root, out string rootPath)) alias[name] = rootPath + "." + member;
            }
        }

        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in QualifiedRx.Matches(src)) candidates.Add(m.Groups[1].Value);
        foreach (var kv in alias) candidates.Add(kv.Value);
        foreach (Match m in MemberUseRx.Matches(src))
        {
            if (alias.TryGetValue(m.Groups[1].Value, out string path))
                candidates.Add(path + "." + m.Groups[2].Value);
        }
        return candidates;
    }

    // ── Indexes ──────────────────────────────────────────────────────

    /// Types XLua generates wrappers for: the explicit config list plus anything
    /// carrying [LuaCallCSharp] itself (LuaBehaviour, EasyLua, GameStorageAPI,
    /// ProfileAPI, DreamParkLuaAPI, …). The attribute half was described in a
    /// comment but never collected, so every self-registering type read as missing.
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
                try { if (t.IsDefined(typeof(LuaCallCSharpAttribute), false)) set.Add(t); }
                catch { /* type failed to load */ }
            }
        }
        return set;
    }

    /// FULL-name → Type, over the Unity assemblies and this project's own.
    ///
    /// Project assemblies are identified by location (Library/ScriptAssemblies)
    /// rather than a name allowlist, so an asmdef anyone adds later is covered
    /// without touching this file.
    private static Dictionary<string, Type> BuildTypeIndex()
    {
        var index = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name = asm.GetName().Name;
            if (name.IndexOf("Editor", StringComparison.Ordinal) >= 0) continue;

            bool isUnity = name.StartsWith("UnityEngine", StringComparison.Ordinal)
                           || name == "Unity.TextMeshPro" || name == "UnityEngine.UI";
            bool isProject = false;
            if (!isUnity)
            {
                string loc;
                try { loc = asm.Location; } catch { continue; }
                isProject = !string.IsNullOrEmpty(loc) &&
                            loc.Replace('\\', '/').IndexOf("/Library/ScriptAssemblies/",
                                StringComparison.OrdinalIgnoreCase) >= 0;
            }
            if (!isUnity && !isProject) continue;

            Type[] types;
            try { types = asm.GetExportedTypes(); } catch { continue; }
            foreach (var t in types)
            {
                if (t == null || t.IsNested || string.IsNullOrEmpty(t.FullName)) continue;
                if ((t.Namespace ?? "").StartsWith("UnityEditor", StringComparison.Ordinal)) continue;
                if (!index.ContainsKey(t.FullName)) index[t.FullName] = t;
            }
        }
        return index;
    }

    // ── Sandbox mirror ───────────────────────────────────────────────
    // Kept in sync BY HAND with LuaSecuritySandbox.GUARD. That file lives in
    // core's Assets/Scripts (not the SDK) and keeps its denylist inside a Lua
    // string, so it cannot be referenced from here. If you change the sandbox,
    // change this list too.
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
