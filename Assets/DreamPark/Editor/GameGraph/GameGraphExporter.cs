#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DreamPark.API;
using DreamPark.Badges;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DreamPark.Editor.GameGraph
{
    /// <summary>
    /// Exports a title's Game Graph (Docs/GAME-GRAPH.md): every attraction / prop template with its
    /// gameplay-relevant child objects (pruned), each LuaBehaviour linked to its script and exposed
    /// fields, the Lua index, badges, playlists and cross-links. Editor-only, no runtime cost.
    ///
    /// Run by the Content Uploader after a successful release commit (UploadRoutine, fire-and-forget)
    /// and headless for agents:
    ///   Unity -batchmode -quit -projectPath . -executeMethod DreamPark.Editor.GameGraph.GameGraphExporter.RunHeadless
    ///         -contentId MyGame [-out Library/GameGraph]
    /// Prints GAME_GRAPH_REPORT_BEGIN … END (one JSON document); exit 0 = written, 1 = failed, 2 = bad args.
    /// Menu: DreamPark → Game Graph → Export (Library/GameGraph/&lt;id&gt;.json + .lua.json).
    /// Deterministic: the same project exports byte-identical files.
    /// </summary>
    public static class GameGraphExporter
    {
        public const string Schema = "dreampark.game-graph";
        public const int SchemaVersion = 1;
        public static string OutDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "GameGraph"));

        static readonly string[] LevelSizes = { "Micro", "Boutique", "Small", "Square", "Medium", "Large", "Jumbo", "MallCorridor", "Custom" };
        static readonly HashSet<string> SdkDeny = new HashSet<string>(StringComparer.Ordinal)
        {
            "OptimizedAFIgnore", "MeshGizmoRenderer", "TransformGizmoRenderer", "DreamParkTmpOcclusion", "DepthMask",
            "DepthMaskCeiling", "DepthCeiling", "AttractionPackingFollower", "CalibrateProp", "CalibrateLevel",
            "GapFiller", "HeightTin", "RealisticRolloff", "AttractionSizeReference", "PassthroughCameraRouter", "FPSDisplay",
            "EasyUV", "EasyBend", "CalibratedFloorGizmo", "HandheldGizmos", "AttractionRectOpenings", "AttractionLayoutFrame",
            "OptimizedAF", "FloorAnchor",
        };
        static readonly HashSet<string> TemplateTypes = new HashSet<string>(StringComparer.Ordinal) { "AttractionTemplate", "LevelTemplate", "PropTemplate" };
        static readonly string[] WinSignals = { "stage_won", "boss_defeated", "exit_reached", "exit", "timed_out", "stage_failed", "wave_cleared", "collected" };
        static readonly System.Text.RegularExpressions.Regex HazardRx = new System.Text.RegularExpressions.Regex(
            @"lava|magma|kill|death|dead|void|water|acid|spike|hazard|respawn|fall|pit\b|abyss|ocean|drown|out.?of.?bounds|oob|deadzone|danger",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static readonly System.Text.RegularExpressions.Regex HazardTagRx = new System.Text.RegularExpressions.Regex(
            @"lava|magma|kill|death|dead|void|water|acid|spike|hazard|abyss|drown|deadzone|danger", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Things standing IN a hazard that you can stand on (a lava platform is the safe part).
        static readonly System.Text.RegularExpressions.Regex SafeRx = new System.Text.RegularExpressions.Regex(
            @"platform|bridge|stone|step|safe|island|raft|ledge|pad|floor|ground|wall|light|fx|sfx|vfx|particle|sound|audio", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static readonly System.Text.RegularExpressions.Regex SafeTagRx = new System.Text.RegularExpressions.Regex(@"^(ground|level|block|platform)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Speech bubbles, instruction cards, signs: art that carries words.
        static readonly System.Text.RegularExpressions.Regex WordsArtRx = new System.Text.RegularExpressions.Regex(
            @"bubble|speech|instruction|dialog|sign\b|signpost|tutorial|hint|caption|label|card", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static readonly System.Text.RegularExpressions.Regex NoiseFieldRx = new System.Text.RegularExpressions.Regex(
            "gizmo|preview|editor|debug|^show", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public sealed class Result
        {
            public string contentId;
            public string json;          // the graph
            public string luaJson;       // the sidecar { schema, title, sources }
            public Dictionary<string, string> luaSources;
            public string revision;
            public JObj counts;
            public List<string> warnings = new List<string>();
        }

        // ── per-export state ───────────────────────────────────────────────
        sealed class Ctx
        {
            public string contentId, contentRoot;
            public readonly Dictionary<string, string> luaIdByPath = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> externalLua = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, PrefabInfo> prefabs = new Dictionary<string, PrefabInfo>(StringComparer.Ordinal); // by asset path
            public readonly List<string> warnings = new List<string>();
        }

        sealed class PrefabInfo
        {
            public string path;
            public GameObject root;
            public List<JObj> nodes;
            public bool? relevant;
            public bool building;
            public readonly SortedSet<string> spawns = new SortedSet<string>(StringComparer.Ordinal); // prefab asset paths
            public string variantOf;
        }

        // ── entry points ───────────────────────────────────────────────────

        [MenuItem("DreamPark/Game Graph/Export All Titles", false, 400)]
        static void ExportMenu()
        {
            foreach (string id in Titles())
            {
                Result r = Export(id);
                string written = Write(r, OutDir);
                Debug.Log($"[GameGraph] {id}: {written} ({r.json.Length / 1024} KB graph, {r.luaJson.Length / 1024} KB Lua, rev {r.revision})");
            }
            EditorUtility.RevealInFinder(OutDir);
        }

        public static List<string> Titles()
            => AssetDatabase.GetSubFolders("Assets/Content").Select(Path.GetFileName)
                .Where(id => !ContentFolders.IsReserved(id)).OrderBy(x => x, StringComparer.Ordinal).ToList();

        public static void RunHeadless()
        {
            string[] args = Environment.GetCommandLineArgs();
            string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            string contentId = Arg("-contentId") ?? Titles().FirstOrDefault();
            string outDir = Arg("-out") ?? OutDir;
            var report = new JObj().Add("command", "game-graph");
            int code;
            if (string.IsNullOrEmpty(contentId)) { report.Add("ok", false).Add("error", "no -contentId and no title under Assets/Content"); code = 2; }
            else
            {
                try
                {
                    Result r = Export(contentId);
                    string file = Write(r, outDir);
                    report.Add("ok", true).Add("contentId", contentId).Add("file", file).Add("revision", r.revision)
                        .Add("bytes", new JObj().Add("graph", Encoding.UTF8.GetByteCount(r.json)).Add("lua", Encoding.UTF8.GetByteCount(r.luaJson)))
                        .Add("counts", r.counts).Add("warnings", r.warnings);
                    code = 0;
                }
                catch (Exception e) { report.Add("ok", false).Add("error", e.Message); code = 1; }
            }
            Console.WriteLine("GAME_GRAPH_REPORT_BEGIN\n" + GameGraphJson.Write(report) + "\nGAME_GRAPH_REPORT_END");
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        /// Writes &lt;dir&gt;/&lt;id&gt;.json, .json.gz and .lua.json.gz; returns the graph path.
        public static string Write(Result r, string dir)
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, r.contentId + ".json");
            File.WriteAllText(file, r.json, new UTF8Encoding(false));
            File.WriteAllBytes(file + ".gz", Gzip(r.json));
            File.WriteAllBytes(Path.Combine(dir, r.contentId + ".lua.json.gz"), Gzip(r.luaJson));
            return file;
        }

        public static byte[] Gzip(string s)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
            {
                byte[] b = new UTF8Encoding(false).GetBytes(s);
                gz.Write(b, 0, b.Length);
            }
            return ms.ToArray();
        }

        /// <summary>
        /// After a successful commit: export the SOURCE title and store it PRIVATELY with the release
        /// (POST /api/content/:uploadId/game-graph). Fire-and-forget; never affects the upload result.
        /// </summary>
        public static IEnumerator UploadRoutine(string uploadContentId, string sourceContentId, int versionNumber)
        {
            Result r;
            try { r = Export(string.IsNullOrEmpty(sourceContentId) ? uploadContentId : sourceContentId); }
            catch (Exception e) { Debug.LogWarning("[GameGraph] export failed (upload unaffected): " + e.Message); yield break; }
            string auth = AuthAPI.GetUserAuthFor(uploadContentId);
            if (string.IsNullOrEmpty(auth)) { Debug.LogWarning("[GameGraph] not signed in; graph not uploaded."); yield break; }
            string body = "{\"versionNumber\":" + versionNumber + ",\"graph\":" + r.json + ",\"luaSources\":" + GameGraphJson.Write(r.luaSources) + "}";
            bool done = false;
            DreamParkAPI.POST("/api/content/" + Uri.EscapeDataString(uploadContentId) + "/game-graph", auth, new UTF8Encoding(false).GetBytes(body),
                (ok, resp) =>
                {
                    done = true;
                    if (ok) Debug.Log($"[GameGraph] stored v{versionNumber} for {uploadContentId} (rev {r.revision}, private).");
                    else Debug.LogWarning("[GameGraph] upload failed (upload unaffected): " + (resp != null ? resp.error : "no response"));
                });
            double deadline = EditorApplication.timeSinceStartup + 120;
            while (!done && EditorApplication.timeSinceStartup < deadline) yield return null;
        }

        // ── export ─────────────────────────────────────────────────────────

        public static Result Export(string contentId)
        {
            var ctx = new Ctx { contentId = contentId, contentRoot = "Assets/Content/" + contentId };
            if (!AssetDatabase.IsValidFolder(ctx.contentRoot)) throw new ArgumentException("No content folder " + ctx.contentRoot);

            // Lua files
            var luaPaths = Directory.GetFiles(ctx.contentRoot, "*", SearchOption.AllDirectories)
                .Select(p => p.Replace('\\', '/'))
                .Where(p => p.EndsWith(".lua.txt", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string p in luaPaths)
            {
                string id = LuaLeaf(p);
                if (!usedIds.Add(id)) { id = StripLuaExt(ContentRel(ctx, p)); usedIds.Add(id); }
                ctx.luaIdByPath[p] = id;
            }

            // badges (.badges.json)
            List<BadgeStore.Entry> badgeEntries = SafeBadges(contentId, ctx);
            var badgeIds = new HashSet<string>(badgeEntries.Select(b => b.badgeId).Where(x => !string.IsNullOrEmpty(x)), StringComparer.Ordinal);
            var vocab = new HashSet<string>(WinSignals.Concat(new[] { "player_down", "boss" }), StringComparer.Ordinal);

            // Lua index
            var scripts = new SortedDictionary<string, JObj>(StringComparer.Ordinal);
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string p in luaPaths)
            {
                string id = ctx.luaIdByPath[p];
                string src = File.ReadAllText(p);
                JObj ix = GameGraphLuaIndex.Index(src, badgeIds, vocab);
                var o = new JObj().Add("path", ContentRel(ctx, p)).Add("bytes", Encoding.UTF8.GetByteCount(src)).Add("sha1", Sha1(src).Substring(0, 12));
                foreach (var kv in ix) o.Add(kv.Key, kv.Value);
                scripts[id] = o;
                sources[id] = src;
            }

            // templates
            var templates = new List<(string path, GameObject go, string type)>();
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { ctx.contentRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || path.IndexOf("/ThirdPartyLocal/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) continue;
                string type = go.GetComponent<AttractionTemplate>() != null ? "AttractionTemplate"
                    : go.GetComponent<LevelTemplate>() != null ? "LevelTemplate"
                    : go.GetComponent<PropTemplate>() != null ? "PropTemplate" : null;
                if (type != null) templates.Add((path, go, type));
            }
            templates.Sort((a, b) => string.CompareOrdinal(a.path, b.path));

            // manifest (modes, roles, frames; manager + player addresses)
            DreamParkPackageManifest man = AssetDatabase.LoadAssetAtPath<DreamParkPackageManifest>(DreamParkPackageCompiler.ManifestPath(contentId));
            var occ = new Dictionary<string, (SortedSet<string> modes, SortedSet<string> roles, PackageOccurrence o)>(StringComparer.Ordinal);
            string gmAddr = null, playerAddr = null, phoneAddr = null;
            var packageKinds = new List<string>();
            if (man != null)
            {
                foreach (string kind in new[] { "arena", "adventure", "sequence", "playlist" })
                {
                    PackageRecipe r = man.RecipeFor(kind);
                    if (DreamParkPackageManifest.IsAbsent(r)) continue;
                    packageKinds.Add(kind);
                    gmAddr ??= NullIfEmpty(r.gameManagerAddress); playerAddr ??= NullIfEmpty(r.playerAddress); phoneAddr ??= NullIfEmpty(r.phonePlayerAddress);
                    foreach (PackageOccurrence o in r.occurrences ?? new List<PackageOccurrence>())
                    {
                        string leaf = (o?.resourceAddress ?? "").Split('/').Last();
                        if (string.IsNullOrEmpty(leaf)) continue;
                        if (!occ.TryGetValue(leaf, out var e)) occ[leaf] = e = (new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal), o);
                        e.modes.Add(kind); if (!string.IsNullOrEmpty(o.role)) e.roles.Add(o.role);
                    }
                }
            }

            // prefabs reachable from templates + manager/player
            var byLeaf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { ctx.contentRoot }))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                string leaf = Path.GetFileNameWithoutExtension(p);
                if (!byLeaf.ContainsKey(leaf) || string.CompareOrdinal(p, byLeaf[leaf]) < 0) byLeaf[leaf] = p;
            }
            var special = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, addr) in new[] { ("gameManager", gmAddr), ("player", playerAddr), ("phonePlayer", phoneAddr) })
                if (addr != null && byLeaf.TryGetValue(addr.Split('/').Last(), out string sp)) special[k] = sp;

            var queue = new Queue<string>(templates.Select(t => t.path).Concat(special.Values));
            var emitted = new SortedSet<string>(StringComparer.Ordinal);
            var templatePaths = new HashSet<string>(templates.Select(t => t.path), StringComparer.Ordinal);
            while (queue.Count > 0)
            {
                string p = queue.Dequeue();
                if (emitted.Contains(p)) continue;
                PrefabInfo pi = Info(ctx, p);
                if (pi == null) continue;
                if (!templatePaths.Contains(p) && !special.ContainsValue(p) && !Relevant(ctx, p)) continue;
                emitted.Add(p);
                foreach (JObj n in pi.nodes) if (n["prefab"] is string np && Relevant(ctx, np)) queue.Enqueue(np);
                foreach (string s in pi.spawns) if (Relevant(ctx, s)) queue.Enqueue(s);
                if (pi.variantOf != null) queue.Enqueue(pi.variantOf);
            }

            // stable ids: leaf, or content-relative path when two emitted prefabs share a leaf
            var leafCount = emitted.GroupBy(p => Path.GetFileNameWithoutExtension(p)).ToDictionary(g => g.Key, g => g.Count());
            var pid = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string p in emitted)
            {
                string leaf = Path.GetFileNameWithoutExtension(p);
                pid[p] = leafCount[leaf] > 1 ? StripExt(ContentRel(ctx, p)) : leaf;
            }
            string Pid(string p) => p != null && pid.TryGetValue(p, out string id) ? id : (p == null ? "missing" : "art:" + Path.GetFileNameWithoutExtension(p));

            var prefabsOut = new SortedDictionary<string, JObj>(StringComparer.Ordinal);
            var scriptUse = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            var closureDirect = new Dictionary<string, (HashSet<string> scripts, HashSet<string> prefabs)>(StringComparer.Ordinal);
            foreach (string p in emitted)
            {
                PrefabInfo pi = ctx.prefabs[p];
                string id = Pid(p);
                var nodes = new List<object>();
                var direct = (scripts: new HashSet<string>(StringComparer.Ordinal), prefabs: new HashSet<string>(StringComparer.Ordinal));
                foreach (JObj n in pi.nodes)
                {
                    JObj m = RewriteRefs(n, Pid);
                    if (n["prefab"] is string np) { m.Add("prefab", Pid(np)); direct.prefabs.Add(Pid(np)); }
                    foreach (string sid in ScriptsOf(m))
                    {
                        direct.scripts.Add(sid);
                        if (!scriptUse.TryGetValue(sid, out var set)) scriptUse[sid] = set = new SortedSet<string>(StringComparer.Ordinal);
                        set.Add(id + ":" + (string)m["path"]);
                    }
                    nodes.Add(m);
                }
                var e = new JObj().Add("path", ContentRel(ctx, p)).Add("root", pi.root.name);
                if (pi.variantOf != null) { e.Add("variantOf", Pid(pi.variantOf)); direct.prefabs.Add(Pid(pi.variantOf)); }
                var spawns = pi.spawns.Where(emitted.Contains).Select(Pid).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (spawns.Count > 0) { e.Add("spawns", spawns); foreach (string s in spawns) direct.prefabs.Add(s); }
                e.Add("nodes", nodes);
                prefabsOut[id] = e;
                closureDirect[id] = direct;
            }

            var closureMemo = new Dictionary<string, (SortedSet<string> scripts, SortedSet<string> prefabs)>(StringComparer.Ordinal);
            (SortedSet<string> scripts, SortedSet<string> prefabs) Closure(string id, HashSet<string> stack)
            {
                if (closureMemo.TryGetValue(id, out var hit)) return hit;
                var res = (scripts: new SortedSet<string>(StringComparer.Ordinal), prefabs: new SortedSet<string>(StringComparer.Ordinal));
                if (!closureDirect.TryGetValue(id, out var d) || !stack.Add(id)) return res;
                res.scripts.UnionWith(d.scripts);
                foreach (string o in d.prefabs)
                {
                    if (!prefabsOut.ContainsKey(o)) continue;
                    res.prefabs.Add(o);
                    var c = Closure(o, stack);
                    res.scripts.UnionWith(c.scripts); res.prefabs.UnionWith(c.prefabs);
                }
                stack.Remove(id);
                closureMemo[id] = res;
                return res;
            }

            // badges with award sites
            var badgesOut = new List<object>();
            foreach (BadgeStore.Entry b in badgeEntries.OrderBy(b => b.badgeId, StringComparer.Ordinal))
            {
                var sites = new List<object>();
                foreach (var kv in scripts)
                    if (kv.Value["badges"] is JObj bj && bj[b.badgeId] is JObj hit)
                        sites.Add(new JObj().Add("script", kv.Key).Add("lines", hit["lines"]).Add("how", hit["how"]));
                badgesOut.Add(new JObj().Add("id", b.badgeId).Add("name", b.name).Add("description", b.description)
                    .Add("earnAt", (b.earnAt ?? new List<string>()).OrderBy(x => x, StringComparer.Ordinal).ToList()).Add("awardSites", sites));
            }

            var fallScripts = new HashSet<string>(scripts.Where(kv => kv.Value["fall"] is List<object> f && f.Count > 0).Select(kv => kv.Key), StringComparer.Ordinal);
            var playerFall = new HashSet<string>(fallScripts.Where(k => ((List<object>)scripts[k]["fall"]).Any(x =>
                System.Text.RegularExpressions.Regex.IsMatch((string)((List<object>)x)[1], @"respawn|kill|player|damage|hurt|die\b|death|lava|drown|checkpoint|safe", System.Text.RegularExpressions.RegexOptions.IgnoreCase))), StringComparer.Ordinal);

            // template entries
            var attractions = new List<object>();
            var props = new List<object>();
            var tplById = new Dictionary<string, JObj>(StringComparer.Ordinal);
            foreach (var t in templates)
            {
                string id = Pid(t.path);
                string leaf = Path.GetFileNameWithoutExtension(t.path);
                string resourceName = StripExt(t.path.Substring("Assets/".Length));
                bool isProp = t.type == "PropTemplate";
                var o = new JObj().Add("id", id).Add("kind", isProp ? "prop" : "attraction").Add("component", t.type)
                    .Add("resourceName", resourceName).Add("attractionId", Slug(resourceName));
                if (!isProp)
                {
                    var lt = t.go.GetComponent<LevelTemplate>();
                    var at = lt as AttractionTemplate;
                    string dn = at != null && !string.IsNullOrWhiteSpace(at.displayName) ? at.displayName.Trim() : Humanize(leaf);
                    o.Add("name", dn);
                    string size = (int)lt.size >= 0 && (int)lt.size < LevelSizes.Length ? LevelSizes[(int)lt.size] : lt.size.ToString();
                    o.Add("size", size);
                    if (size == "Custom") o.Add("customSizeFt", V2(lt.customSize, 2));
                    if (at != null)
                    {
                        if (at.gameRequiresAttraction) o.Add("required", true);
                        o.Add("packingBake", at.PackingBake != null && at.PackingBake.IsValid && at.PackingBake.AuthoredFootprintMeters.x > 0);
                        if (at.PackingBake != null && at.PackingBake.Version > 0) o.Add("packingBakeVersion", at.PackingBake.Version);
                        if (at.PackingBake != null && at.PackingBake.IsValid)
                            o.Add("footprintM", new JObj().Add("authored", V2(at.PackingBake.AuthoredFootprintMeters, 2))
                                .Add("shrink", V2(at.PackingBake.ShrinkFootprintMeters, 2)).Add("grow", V2(at.PackingBake.GrowFootprintMeters, 2)));
                        var frames = at.LayoutFrames;
                        if (frames != null && frames.Count > 0)
                            o.Add("layoutFrames", frames.Where(f => f != null).Select(f => (object)new JObj().Add("id", f.id).Add("name", f.name)).ToList());
                    }
                }
                else
                {
                    var pt = t.go.GetComponent<PropTemplate>();
                    o.Add("name", Humanize(leaf)).Add("category", pt.category.ToString());
                }
                if (occ.TryGetValue(leaf, out var oc))
                {
                    o.Add("modes", oc.modes.ToList());
                    if (oc.roles.Count > 0) o.Add("packageRoles", oc.roles.ToList());
                    if (oc.o.widthFeet > 0) o.Add("feet", new List<object> { oc.o.widthFeet, oc.o.lengthFeet });
                    if (oc.o.frames != null && oc.o.frames.Count > 0)
                        o.Add("frames", oc.o.frames.Select(f => (object)new JObj().Add("id", f.frameId).Add("kind", f.kind).Add("name", f.name)
                            .Add("m", V2(f.authoredFootprintMeters, 2)).Add("shrink", V2(f.shrinkFootprintMeters, 2)).Add("grow", V2(f.growFootprintMeters, 2))).ToList());
                }
                string preview = ctx.contentRoot + "/Previews/" + leaf + ".png";
                if (File.Exists(preview)) o.Add("preview", ContentRel(ctx, preview));
                o.Add("badges", badgeEntries.Where(b => b.earnAt != null && b.earnAt.Contains(leaf)).Select(b => b.badgeId).OrderBy(x => x, StringComparer.Ordinal).ToList());
                var cl = Closure(id, new HashSet<string>(StringComparer.Ordinal));
                o.Add("scripts", cl.scripts.ToList());
                o.Add("uses", cl.prefabs.Where(x => x != id).ToList());
                // announcements: what the attraction's objects SAY they are (its scripts' dp.announce + Announce components)
                JObj ann = AnnouncesOf(new[] { id }.Concat(cl.prefabs.Where(x => x != id)), cl.scripts, scripts, prefabsOut);
                if (ann.Count > 0) o.Add("announces", ann);
                if (!isProp)
                {
                    JObj inferred = InferKinds(cl.scripts.Select(s => scripts.TryGetValue(s, out var j) ? j : null).Where(j => j != null));
                    if (ann.Count > 0)
                    {
                        var kinds = new JObj();
                        foreach (var kv in ann) kinds.Add(kv.Key, "announced");
                        foreach (var kv in inferred) if (!kinds.Has(kv.Key)) kinds.Add(kv.Key, kv.Value);
                        o.Add("objectives", new JObj().Add("source", "announced").Add("kinds", kinds));
                    }
                    else o.Add("objectives", new JObj().Add("source", "inferred").Add("kinds", inferred));
                    o.Add("roleHint", RoleHint(leaf));
                }
                else o.Add("roleHint", "prop");
                o.Add("root", t.go.name);
                if (!isProp)
                {
                    JObj sp = Space(t.go, o["footprintM"] is JObj fm ? fm["authored"] as List<object> : null, playerFall, ctx);
                    if (sp != null) o.Add("space", sp);
                    var fh = cl.scripts.Where(fallScripts.Contains).ToList();
                    if (fh.Count > 0) o.Add("fallScripts", fh);
                }
                (isProp ? props : attractions).Add(o);
                tplById[id] = o;
            }

            // scripts ↔ objects ↔ attractions
            var tplOfPrefab = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (JObj t in tplById.Values)
                foreach (string p in new[] { (string)t["id"] }.Concat(((List<string>)t["uses"])))
                {
                    if (!tplOfPrefab.TryGetValue(p, out var s)) tplOfPrefab[p] = s = new SortedSet<string>(StringComparer.Ordinal);
                    s.Add((string)t["id"]);
                }
            foreach (var kv in scripts)
            {
                var uses = scriptUse.TryGetValue(kv.Key, out var u) ? u.ToList() : new List<string>();
                kv.Value.Add("usedBy", uses.Take(40).ToList());
                if (uses.Count > 40) kv.Value.Add("usedByMore", uses.Count - 40);
                var atts = new SortedSet<string>(StringComparer.Ordinal);
                foreach (string use in uses) if (tplOfPrefab.TryGetValue(use.Split(':')[0], out var ts)) atts.UnionWith(ts);
                kv.Value.Add("attractions", atts.Take(80).ToList());
                if (atts.Count > 80) kv.Value.Add("attractionsMore", atts.Count - 80);
            }
            foreach (var kv in ctx.externalLua)
                scripts[kv.Key] = new JObj().Add("path", kv.Value).Add("external", true)
                    .Add("usedBy", scriptUse.TryGetValue(kv.Key, out var u2) ? u2.Take(40).ToList() : new List<string>());

            // playlists (compiled manifest)
            var playlistsOut = new List<object>();
            var tplByRes = tplById.Values.ToDictionary(t => (string)t["resourceName"], t => (string)t["id"], StringComparer.Ordinal);
            string ResolveStop(PlaylistStop s, string resourceName, string authored)
            {
                if (!string.IsNullOrEmpty(s.contentId) && s.contentId != contentId) return s.contentId + "/" + authored;
                if (!string.IsNullOrEmpty(resourceName) && tplByRes.TryGetValue(resourceName, out string a)) return a;
                string leaf = (authored ?? "").Split('/').Last().Replace(".prefab", "");
                return tplById.ContainsKey(leaf) ? leaf : "?" + authored;
            }
            string ResolveAuthored(string authored, string cid)
            {
                if (!string.IsNullOrEmpty(cid) && cid != contentId) return cid + "/" + authored;
                if (tplByRes.TryGetValue(authored, out string a)) return a;
                string leaf = authored.Split('/').Last().Replace(".prefab", "");
                return tplById.ContainsKey(leaf) ? leaf : "?" + authored;
            }
            if (man != null && man.HasPlaylists)
            {
                foreach (PackagePlaylist p in man.playlists)
                {
                    var legs = new List<object>();
                    foreach (PlaylistLeg l in p.arc ?? new List<PlaylistLeg>())
                    {
                        var stops = new List<object>();
                        foreach (PlaylistStop s in l.stops ?? new List<PlaylistStop>())
                        {
                            var alts = new List<object>();
                            for (int i = 0; i < (s.alternates?.Count ?? 0); i++)
                                alts.Add(ResolveStop(s, s.alternateResourceNames != null && i < s.alternateResourceNames.Count ? s.alternateResourceNames[i] : null, s.alternates[i]));
                            var so = new JObj().Add("attraction", ResolveStop(s, s.resourceName, s.attraction)).Add("role", s.role).Add("alternates", alts);
                            if (!string.IsNullOrEmpty(s.displayName)) so.Add("displayName", s.displayName);
                            if (!string.IsNullOrEmpty(s.objectiveText)) so.Add("objectiveText", s.objectiveText);
                            stops.Add(so);
                            foreach (object a in new object[] { so["attraction"] }.Concat(alts))
                                if (a is string aid && tplById.TryGetValue(aid, out JObj t))
                                {
                                    if (!(t["playlistStops"] is List<string> ps)) t.Add("playlistStops", ps = new List<string>());
                                    string r = p.id + "/" + l.id; if (!ps.Contains(r)) ps.Add(r);
                                }
                        }
                        var lo = new JObj().Add("id", l.id).Add("name", l.name).Add("beat", l.beat).Add("minutes", l.minutes)
                            .Add("objective", new JObj().Add("type", l.objective?.type).Add("target", NullIfEmpty(l.objective?.target))
                                .Add("count", l.objective != null && l.objective.count > 0 ? (object)l.objective.count : null)
                                .Add("seconds", l.objective != null && l.objective.seconds > 0 ? (object)l.objective.seconds : null));
                        if (!string.IsNullOrEmpty(l.objectiveText)) lo.Add("objectiveText", l.objectiveText);
                        lo.Add("stops", stops);
                        legs.Add(lo);
                    }
                    var plo = new JObj().Add("id", p.id).Add("kind", p.kind).Add("name", p.name).Add("pitch", p.pitch)
                        .Add("tags", p.tags ?? new List<string>()).Add("badges", p.badges ?? new List<string>()).Add("legs", legs);
                    // SETS playlists (PLAYLIST-PLAN § 3): pool + requires.badges + finishBadge, from the authored record
                    // (byte-identical with core build.js playlistSets). arc[] playlists carry none of these.
                    JObj rec = GameGraphJsonReader.Read(p.sourceJson) as JObj ?? RecordFromStruct(p);
                    JObj sets = GameGraphPlaylistSets.Fields(rec, rec["lua"] as string ?? p.lua, ResolveAuthored);
                    foreach (var kv in sets) plo.Add(kv.Key, kv.Value);
                    if (sets["pool"] is List<object> pl)
                        foreach (JObj e in pl.OfType<JObj>())
                            if (e["attraction"] is string aid && tplById.TryGetValue(aid, out JObj t))
                            {
                                if (!(t["playlistStops"] is List<string> ps)) t.Add("playlistStops", ps = new List<string>());
                                string r = p.id + "/" + ((e["id"] as string) ?? aid); if (!ps.Contains(r)) ps.Add(r);
                            }
                    playlistsOut.Add(plo);
                }
                foreach (JObj t in tplById.Values) if (t["playlistStops"] is List<string> ps) ps.Sort(StringComparer.Ordinal);
            }

            // reverse indexes: events (senders), receivers, dp API use, platform hooks, fall handling
            var signalIndex = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            var dpIndex = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            var listenerIndex = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            var hookIndex = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            var announceIndex = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);   // kind → attractions announcing it
            foreach (JObj t in tplById.Values)
                if (t["announces"] is JObj ta)
                    foreach (var kv in ta)
                    {
                        if (!announceIndex.TryGetValue(kv.Key, out var set)) announceIndex[kv.Key] = set = new SortedSet<string>(StringComparer.Ordinal);
                        set.Add((string)t["id"]);
                    }
            var platformSignals = new HashSet<string>(badgeIds.Concat(new[] { "stage_won", "boss_defeated", "exit_reached", "exit", "timed_out", "stage_failed" }), StringComparer.Ordinal);
            var fallIndex = new JObj();
            void AddTo(SortedDictionary<string, SortedSet<string>> map, string key, string sid)
            {
                if (!map.TryGetValue(key, out var set)) map[key] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(sid);
            }
            var fnNames = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (var kv in scripts)
            {
                if (kv.Value["signals"] is JObj sg)
                    foreach (var e in sg)
                    {
                        AddTo(signalIndex, e.Key, kv.Key);
                        if (((JObj)e.Value)["via"] is List<string> via && via.Any(v => v.StartsWith("dp.", StringComparison.Ordinal) || v.IndexOf("award", StringComparison.OrdinalIgnoreCase) >= 0)) platformSignals.Add(e.Key);
                    }
                if (kv.Value["dp"] is JObj dpo) foreach (var e in dpo) AddTo(dpIndex, e.Key, kv.Key);
                if (kv.Value["functions"] is List<object> fns)
                    foreach (object f in fns)
                    {
                        string leaf = ((string)((JObj)f)["name"]).Split('.', ':').Last();
                        if (!fnNames.TryGetValue(leaf, out var set)) fnNames[leaf] = set = new SortedSet<string>(StringComparer.Ordinal);
                        set.Add(kv.Key);
                    }
                if (kv.Value["hooks"] is List<string> hs)
                    foreach (string h in hs.Where(h => GameGraphLuaIndex.PlatformHooks.Contains(h))) AddTo(hookIndex, h, kv.Key);
                if (fallScripts.Contains(kv.Key)) fallIndex.Add(kv.Key, ((List<object>)kv.Value["fall"]).Select(x => ((List<object>)x)[0]).ToList());
            }
            foreach (string sig in signalIndex.Keys)
            {
                var recv = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var kv in scripts)
                    if ((kv.Value["listens"] is JObj l && l.Has(sig)) || (kv.Value["compares"] is JObj c && c.Has(sig))) recv.Add(kv.Key);
                if (fnNames.TryGetValue(sig, out var fs)) recv.UnionWith(fs);
                if (platformSignals.Contains(sig)) recv.Add("platform");
                listenerIndex[sig] = recv;
            }
            // names only listened for (sent by the platform or by C#)
            foreach (var kv in scripts)
                if (kv.Value["listens"] is JObj l)
                    foreach (var e in l) if (!signalIndex.ContainsKey(e.Key)) AddTo(listenerIndex, e.Key, kv.Key);
            var unheard = signalIndex.Keys.Where(k => !listenerIndex.TryGetValue(k, out var r) || r.Count == 0).ToList();

            // title
            var title = new JObj().Add("id", contentId).Add("contentId", contentId).Add("packageKinds", packageKinds);
            if (man != null && man.HasPlaylists) title.Add("playlistKinds", man.PlaylistKindsOrDefault().Select(k => (object)new JObj().Add("id", k.id).Add("singular", k.singular).Add("plural", k.plural)).ToList()).Add("playlistsSource", "title");
            foreach (var kv in special)
            {
                if (!emitted.Contains(kv.Value)) continue;
                string id = Pid(kv.Value);
                var cl = Closure(id, new HashSet<string>(StringComparer.Ordinal));
                var comps = new SortedSet<string>(StringComparer.Ordinal);
                foreach (object n in (List<object>)prefabsOut[id]["nodes"]) if (((JObj)n)["c"] is List<object> cs) foreach (object c in cs) comps.Add((string)((JObj)c)["t"]);
                title.Add(kv.Key, new JObj().Add("prefab", id).Add("scripts", cl.scripts.ToList()).Add("components", comps.ToList()).Add("nodes", ((List<object>)prefabsOut[id]["nodes"]).Count));
            }

            var counts = new JObj()
                .Add("attractions", attractions.Count).Add("props", props.Count).Add("prefabs", prefabsOut.Count)
                .Add("nodes", prefabsOut.Values.Sum(p => ((List<object>)p["nodes"]).Count))
                .Add("scripts", scripts.Count(kv => kv.Value["external"] == null))
                .Add("luaBytes", sources.Values.Sum(s => Encoding.UTF8.GetByteCount(s)))
                .Add("functions", scripts.Values.Sum(s => s["functions"] is List<object> f ? f.Count : 0))
                .Add("badges", badgesOut.Count).Add("playlists", playlistsOut.Count);

            var luaSide = new JObj().Add("schema", Schema + ".lua").Add("schemaVersion", SchemaVersion).Add("title", contentId)
                .Add("sources", new SortedDictionary<string, string>(sources, StringComparer.Ordinal));
            string luaJson = GameGraphJson.Write(luaSide);

            var graph = new JObj()
                .Add("schema", Schema).Add("schemaVersion", SchemaVersion)
                .Add("title", title).Add("counts", counts)
                .Add("attractions", attractions).Add("props", props)
                .Add("prefabs", ToJObj(prefabsOut))
                .Add("lua", new JObj().Add("sources", "sidecar").Add("scripts", ToJObj(scripts)).Add("sourcesSha", Sha256(luaJson).Substring(0, 16)))
                .Add("badges", badgesOut).Add("playlists", playlistsOut)
                .Add("index", new JObj().Add("signals", ToJObj(signalIndex.ToDictionary(k => k.Key, k => (object)k.Value.ToList())))
                    .Add("listeners", ToJObj(listenerIndex.ToDictionary(k => k.Key, k => (object)k.Value.ToList())))
                    .Add("unheard", unheard)
                    .Add("dp", ToJObj(dpIndex.ToDictionary(k => k.Key, k => (object)k.Value.ToList())))
                    .Add("hooks", ToJObj(hookIndex.ToDictionary(k => k.Key, k => (object)k.Value.ToList()))).Add("fall", fallIndex)
                    .Add("announces", ToJObj(announceIndex.ToDictionary(k => k.Key, k => (object)k.Value.ToList()))))
                .Add("warnings", ctx.warnings.Take(50).ToList());
            string revision = Sha256(GameGraphJson.Write(graph)).Substring(0, 16);
            // After the hash: the revision covers the CONTENT, so an SDK bump alone does not change it.
            graph.Add("revision", revision).Add("generator", "dreampark-sdk GameGraphExporter " + SDKVersion.Current);

            return new Result
            {
                contentId = contentId, json = GameGraphJson.Write(graph), luaJson = luaJson, luaSources = sources,
                revision = revision, counts = counts, warnings = ctx.warnings,
            };
        }

        // ── prefab hierarchy ───────────────────────────────────────────────

        static PrefabInfo Info(Ctx ctx, string path)
        {
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return null;
            if (ctx.prefabs.TryGetValue(path, out PrefabInfo hit)) return hit;
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null) { ctx.prefabs[path] = null; return null; }
            var pi = new PrefabInfo { path = path, root = root, building = true, nodes = new List<JObj>() };
            ctx.prefabs[path] = pi;
            var flat = new List<JObj>();
            if (PrefabUtility.IsAnyPrefabInstanceRoot(root))
            {
                Object src = PrefabUtility.GetCorrespondingObjectFromSource(root);
                pi.variantOf = src != null ? AssetDatabase.GetAssetPath(src) : null;
            }
            VisitTree(ctx, pi, root.transform, "", true, flat);
            // prune: keep relevant nodes and the root; structure is carried by paths
            var kept = new List<JObj>();
            var keepPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (JObj n in flat) if (NodeRelevant(ctx, n)) keepPaths.Add((string)n["path"]);
            for (int i = 0; i < flat.Count; i++)
                if (i == 0 || keepPaths.Contains((string)flat[i]["path"])) kept.Add(flat[i]);
            kept[0].Remove("pos");
            pi.nodes = Collapse(kept);
            pi.building = false;
            return pi;
        }

        static void VisitTree(Ctx ctx, PrefabInfo pi, Transform t, string prefix, bool isRoot, List<JObj> flat)
        {
            GameObject go = t.gameObject;
            string p = prefix.Length > 0 ? prefix + "/" + go.name : go.name;
            bool nestedInstance = !isRoot && PrefabUtility.IsAnyPrefabInstanceRoot(go);
            bool variantRoot = isRoot && pi.variantOf != null;
            if (nestedInstance || variantRoot)
            {
                Object src = PrefabUtility.GetCorrespondingObjectFromSource(go);
                string srcPath = src != null ? AssetDatabase.GetAssetPath(src) : null;
                var node = MakeNode(p, isRoot ? null : Pos(t), null, -1, false, new List<object>());
                node.Add("prefab", srcPath ?? "missing");
                JObj ov = Overrides(ctx, pi, go);
                if (ov != null && ov.Count > 0) node.Add("ov", ov);
                if (!isRoot) Place(node, pi.root.transform, t);
                // components / objects the instance REMOVED from its prefab
                var removed = new List<string>();
                foreach (var rc in PrefabUtility.GetRemovedComponents(go))
                {
                    Component ac = rc.assetComponent;
                    if (ac == null) continue;
                    string rp = RelPath(ac.transform.root, ac.transform);
                    removed.Add(ac.GetType().Name + (rp.Length > 0 ? "@" + rp : ""));
                }
                foreach (var rg in PrefabUtility.GetRemovedGameObjects(go))
                {
                    GameObject ag = rg.assetGameObject;
                    if (ag != null) removed.Add("GameObject@" + RelPath(ag.transform.root, ag.transform));
                }
                if (removed.Count > 0) { removed.Sort(StringComparer.Ordinal); node.Add("removed", removed); }
                // components added on top of the instance (this prefab's own work)
                var added = new List<object>();
                foreach (Component c in go.GetComponents<Component>())
                    if (c != null && PrefabUtility.IsAddedComponentOverride(c)) { JObj co = Component(ctx, pi, c); if (co != null) { co.Add("added", true); added.Add(co); } }
                if (added.Count > 0) node.Add("c", added);
                flat.Add(node);
                // objects ADDED inside the instance
                foreach (Transform d in t.GetComponentsInChildren<Transform>(true))
                {
                    if (d == t) continue;
                    if (PrefabUtility.IsAddedGameObjectOverride(d.gameObject))
                    {
                        string rel = RelPath(t, d.parent);
                        VisitTree(ctx, pi, d, rel.Length > 0 ? p + "/" + rel : p, false, flat);
                    }
                    else if (!IsUnderAdded(d, t))
                    {
                        var addedHere = new List<object>();
                        foreach (Component c in d.GetComponents<Component>())
                            if (c != null && PrefabUtility.IsAddedComponentOverride(c)) { JObj co = Component(ctx, pi, c); if (co != null) addedHere.Add(co); }
                        if (addedHere.Count > 0) flat.Add(MakeNode(p + "/" + RelPath(t, d), null, null, -1, false, addedHere).Add("addedTo", true));
                    }
                }
                return;
            }
            var comps = new List<object>();
            foreach (Component c in go.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                JObj co = Component(ctx, pi, c);
                if (co != null) comps.Add(co);
            }
            JObj n = MakeNode(p, isRoot ? null : Pos(t), go.tag, go.layer, !go.activeSelf, comps);
            if (!isRoot) Place(n, pi.root.transform, t);
            var easy = comps.Select(c => (string)((JObj)c)["t"]).Where(x => x.StartsWith("Easy", StringComparison.Ordinal)).ToList();
            if (easy.Count >= 2) n.Add("chain", easy); // EasyEvent chain: component order IS the event order
            flat.Add(n);
            foreach (Transform ch in t) VisitTree(ctx, pi, ch, p, false, flat);
        }

        /// Root-space placement (GAME-GRAPH.md § transforms): rp only when it differs from the local pos.
        static void Place(JObj node, Transform root, Transform t)
        {
            Vector3 p = root.InverseTransformPoint(t.position);
            Quaternion q = Quaternion.Inverse(root.rotation) * t.rotation;
            if (q.w < 0) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            Vector3 rs = t.lossyScale, ps = root.lossyScale;
            Vector3 sc = new Vector3(ps.x != 0 ? rs.x / ps.x : 0, ps.y != 0 ? rs.y / ps.y : 0, ps.z != 0 ? rs.z / ps.z : 0);
            var rp = V3(p, 2);
            var local = node["pos"] as List<object> ?? Zero();
            bool differs = false;
            for (int i = 0; i < 3; i++) if (Math.Abs(Convert.ToDouble(rp[i]) - Convert.ToDouble(local[i])) > 0.005) differs = true;
            if (differs) node.Add("rp", rp);
            if (Math.Abs(q.w) < 0.99995f) node.Add("rq", new List<object> { GameGraphJson.Round(q.x, 4), GameGraphJson.Round(q.y, 4), GameGraphJson.Round(q.z, 4), GameGraphJson.Round(q.w, 4) });
            if (Math.Abs(sc.x - 1) > 0.005f || Math.Abs(sc.y - 1) > 0.005f || Math.Abs(sc.z - 1) > 0.005f) node.Add("rs", V3(sc, 3));
        }

        static bool IsUnderAdded(Transform d, Transform instanceRoot)
        {
            for (Transform x = d.parent; x != null && x != instanceRoot; x = x.parent)
                if (PrefabUtility.IsAddedGameObjectOverride(x.gameObject)) return true;
            return PrefabUtility.IsAddedGameObjectOverride(d.gameObject);
        }

        static string RelPath(Transform root, Transform t)
        {
            var parts = new List<string>();
            for (Transform x = t; x != null && x != root; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static JObj MakeNode(string path, List<object> pos, string tag, int layer, bool inactive, List<object> comps)
        {
            var n = new JObj().Add("path", path);
            if (pos != null && pos.Any(v => Convert.ToDouble(v) != 0)) n.Add("pos", pos);
            if (!string.IsNullOrEmpty(tag) && tag != "Untagged") n.Add("tag", tag);
            if (layer > 0) n.Add("layer", LayerMask.LayerToName(layer) is string ln && ln.Length > 0 ? ln : layer.ToString());
            if (inactive) n.Add("inactive", true);
            bool strong = comps.Any(c => { var o = (JObj)c; string t = (string)o["t"]; return !(t == "BoxCollider" || t == "SphereCollider" || t == "CapsuleCollider" || t == "MeshCollider" || t == "Rigidbody") || o["trigger"] != null; });
            bool keepPhysics = strong || (!string.IsNullOrEmpty(tag) && tag != "Untagged");
            if (keepPhysics && comps.Count > 0) n.Add("c", comps);
            return n;
        }

        static bool NodeRelevant(Ctx ctx, JObj n)
        {
            if (n["c"] is List<object> cs && cs.Count > 0) return true;
            if (n["tag"] != null || n["removed"] != null) return true;
            if (n["prefab"] is string p) return n["ov"] != null || Relevant(ctx, p);
            return false;
        }

        static bool Relevant(Ctx ctx, string path)
        {
            PrefabInfo pi = Info(ctx, path);
            if (pi == null) return false;
            if (pi.relevant.HasValue) return pi.relevant.Value;
            if (pi.building) return false;
            pi.relevant = false; // cycle guard
            bool r = pi.nodes.Any(n =>
                (n["c"] is List<object> cs && cs.Any(c => !TemplateTypes.Contains((string)((JObj)c)["t"])))
                || n["tag"] != null
                || (n["prefab"] is string np && (n["ov"] != null || Relevant(ctx, np))));
            pi.relevant = r;
            return r;
        }

        static List<JObj> Collapse(List<JObj> nodes)
        {
            var outList = new List<JObj>();
            var groups = new Dictionary<string, JObj>(StringComparer.Ordinal);
            var hasKids = new HashSet<string>(StringComparer.Ordinal);
            foreach (JObj n in nodes) { string p = (string)n["path"]; int k = p.LastIndexOf('/'); if (k > 0) hasKids.Add(p.Substring(0, k)); }
            for (int i = 0; i < nodes.Count; i++)
            {
                JObj n = nodes[i];
                string path = (string)n["path"];
                if (i == 0 || hasKids.Contains(path)) { outList.Add(n); continue; }
                int k = path.LastIndexOf('/');
                string par = k > 0 ? path.Substring(0, k) : "";
                var sigObj = new JObj().Add("p", n["prefab"]).Add("c", n["c"]).Add("ov", n["ov"]).Add("tag", n["tag"]).Add("layer", n["layer"]).Add("inactive", n["inactive"])
                    .Add("rq", n["rq"]).Add("rs", n["rs"]).Add("removed", n["removed"]);
                string sig = par + "|" + GameGraphJson.Write(sigObj);
                if (groups.TryGetValue(sig, out JObj g))
                {
                    int count = g["count"] is int c ? c + 1 : 2;
                    g.Add("count", count);
                    if (!(g["at"] is List<object> at)) { at = new List<object> { g["pos"] ?? Zero() }; g.Add("at", at); g.Add("rat", new List<object> { g["rp"] ?? g["pos"] ?? Zero() }); }
                    if (at.Count < 12) { at.Add(n["pos"] ?? Zero()); ((List<object>)g["rat"]).Add(n["rp"] ?? n["pos"] ?? Zero()); }
                    continue;
                }
                groups[sig] = n; outList.Add(n);
            }
            foreach (JObj n in outList)
                if (n["at"] is List<object> at)
                {
                    n.Remove("pos"); n.Remove("rp");
                    if (GameGraphJson.Write(n["rat"]) == GameGraphJson.Write(at)) n.Remove("rat");
                    int c = (int)n["count"]; if (c > at.Count) n.Add("atMore", c - at.Count);
                }
            return outList;
        }

        static List<object> Zero() => new List<object> { 0.0, 0.0, 0.0 };

        // ── components ─────────────────────────────────────────────────────

        static JObj Component(Ctx ctx, PrefabInfo pi, Component c)
        {
            switch (c)
            {
                case Collider col:
                {
                    string t = col is BoxCollider ? "BoxCollider" : col is SphereCollider ? "SphereCollider" : col is CapsuleCollider ? "CapsuleCollider" : col is MeshCollider ? "MeshCollider" : col.GetType().Name;
                    var o = new JObj().Add("t", t);
                    if (col.isTrigger) o.Add("trigger", true);
                    if (!col.enabled) o.Add("off", true);
                    return o;
                }
                case AudioSource a:
                {
                    if (a.clip == null) return null;
                    var o = new JObj().Add("t", "AudioSource").Add("clip", Path.GetFileName(AssetDatabase.GetAssetPath(a.clip)));
                    if (a.playOnAwake) o.Add("playOnAwake", true);
                    if (a.loop) o.Add("loop", true);
                    return o;
                }
                case Rigidbody rb: { var o = new JObj().Add("t", "Rigidbody"); if (rb.isKinematic) o.Add("kinematic", true); return o; }
                case SpriteRenderer sr:
                    return sr.sprite != null && WordsArtRx.IsMatch(sr.sprite.name) ? new JObj().Add("t", "WordsArt").Add("art", new List<string> { sr.sprite.name }) : null;
                case Renderer rr:
                {
                    var words = rr.sharedMaterials.Where(m => m != null && WordsArtRx.IsMatch(m.name)).Select(m => m.name).ToList();
                    return words.Count > 0 ? new JObj().Add("t", "WordsArt").Add("art", words) : null;
                }
                case UnityEngine.AI.NavMeshAgent _: return new JObj().Add("t", "NavMeshAgent");
                case LuaBehaviour lb: return Lua(ctx, pi, c, "Lua", lb.luaScript, lb.enabled);
                case Announce an:
                {
                    // DreamPark/Announce (PLAYLIST-PLAN § 4.2): what a prefab with no Lua says it is.
                    var a = new JObj().Add("t", "Announce").Add("kind", AnnounceKinds.Name(an.kind));
                    if (!string.IsNullOrWhiteSpace(an.item)) a.Add("item", an.item.Trim());
                    var hs = (an.holds ?? new List<AnnounceHold>()).Where(h => h.count > 0)
                        .Select(h => (object)new JObj().Add("kind", AnnounceKinds.Name(h.kind)).Add("count", h.count)).ToList();
                    if (hs.Count > 0) a.Add("holds", hs);
                    if (!an.enabled) a.Add("off", true);
                    return a;
                }
                case EasyLua el: return Lua(ctx, pi, c, "EasyLua", el.luaScript, el.enabled);
                case MonoBehaviour mb:
                {
                    MonoScript ms = MonoScript.FromMonoBehaviour(mb);
                    string sp = ms != null ? AssetDatabase.GetAssetPath(ms) : "";
                    bool sdk = sp.StartsWith("Assets/DreamPark/", StringComparison.Ordinal);
                    bool own = sp.StartsWith(ctx.contentRoot + "/", StringComparison.Ordinal);
                    if (!sdk && !own)
                    {
                        // text components (TextMeshPro m_text, UI Text m_Text) carry the words players read
                        var so = new SerializedObject(mb);
                        SerializedProperty tp = so.FindProperty("m_text") ?? (so.FindProperty("m_FontData") != null ? so.FindProperty("m_Text") : null);
                        if (tp != null && tp.propertyType == SerializedPropertyType.String && !string.IsNullOrWhiteSpace(tp.stringValue))
                            return new JObj().Add("t", "Text").Add("text", Clip(tp.stringValue, 300));
                        return null;
                    }
                    string name = mb.GetType().Name;
                    if (TemplateTypes.Contains(name)) return new JObj().Add("t", name);
                    if (SdkDeny.Contains(name)) return null;
                    var o = new JObj().Add("t", name);
                    JObj f = Fields(ctx, pi, mb);
                    if (f.Count > 0) o.Add("f", f);
                    if (!mb.enabled) o.Add("off", true);
                    return o;
                }
                default:
                {
                    // TextMesh (legacy 3D text)
                    var so = new SerializedObject(c);
                    SerializedProperty tp = c.GetType().Name == "TextMesh" ? so.FindProperty("m_Text") : null;
                    return tp != null && !string.IsNullOrWhiteSpace(tp.stringValue) ? new JObj().Add("t", "Text").Add("text", Clip(tp.stringValue, 300)) : null;
                }
            }
        }

        static JObj Lua(Ctx ctx, PrefabInfo pi, Component c, string type, TextAsset script, bool enabled)
        {
            var o = new JObj().Add("t", type);
            o.Add("script", script != null ? LuaId(ctx, AssetDatabase.GetAssetPath(script)) : null);
            var f = new JObj();
            var so = new SerializedObject(c);
            SerializedProperty it = so.GetIterator();
            bool enter = true;
            while (it.Next(enter))
            {
                enter = false;
                if (!it.name.EndsWith("njections", StringComparison.Ordinal) || !it.isArray) continue;
                for (int i = 0; i < it.arraySize; i++)
                {
                    SerializedProperty e = it.GetArrayElementAtIndex(i);
                    SerializedProperty n = e.FindPropertyRelative("name"), v = e.FindPropertyRelative("value");
                    if (n == null || string.IsNullOrEmpty(n.stringValue) || v == null) continue;
                    f.Add(n.stringValue, Value(ctx, pi, v, 0) ?? (object)"null");
                }
            }
            if (f.Count > 0) o.Add("f", f);
            if (!enabled) o.Add("off", true);
            return o;
        }

        static JObj Fields(Ctx ctx, PrefabInfo pi, Object target)
        {
            var f = new JObj();
            var so = new SerializedObject(target);
            SerializedProperty it = so.GetIterator();
            bool enter = true; int n = 0;
            while (it.Next(enter))
            {
                enter = false;
                if (it.name.StartsWith("m_", StringComparison.Ordinal) || it.name == "serializedVersion" || NoiseFieldRx.IsMatch(it.name)) continue;
                object v = Value(ctx, pi, it, 0);
                if (v == null || (v is string s && s.Length == 0) || (v is List<object> l && l.Count == 0)) continue;
                if (++n > 24) { f.Add("…", "more"); break; }
                f.Add(it.name, v);
            }
            return f;
        }

        static object Value(Ctx ctx, PrefabInfo pi, SerializedProperty p, int depth)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.longValue;
                case SerializedPropertyType.Boolean: return p.boolValue;
                case SerializedPropertyType.Float: return GameGraphJson.Round(p.doubleValue);
                case SerializedPropertyType.String: return Clip(p.stringValue, 200);
                case SerializedPropertyType.Enum: return p.enumValueIndex >= 0 && p.enumValueIndex < p.enumNames.Length ? p.enumNames[p.enumValueIndex] : (object)p.intValue;
                case SerializedPropertyType.Vector2: return V2(p.vector2Value, 3);
                case SerializedPropertyType.Vector3: return V3(p.vector3Value, 3);
                case SerializedPropertyType.Vector4: return new List<object> { R(p.vector4Value.x), R(p.vector4Value.y), R(p.vector4Value.z), R(p.vector4Value.w) };
                case SerializedPropertyType.Quaternion: { Quaternion q = p.quaternionValue; return new List<object> { R(q.x), R(q.y), R(q.z), R(q.w) }; }
                case SerializedPropertyType.Color: { Color c = p.colorValue; return new List<object> { R(c.r), R(c.g), R(c.b), R(c.a) }; }
                case SerializedPropertyType.ObjectReference: return Ref(ctx, pi, p.objectReferenceValue);
                case SerializedPropertyType.LayerMask: return p.intValue;
                case SerializedPropertyType.Generic:
                {
                    if (p.isArray)
                    {
                        var list = new List<object>();
                        int count = p.arraySize;
                        for (int i = 0; i < Math.Min(count, 16); i++) list.Add(Value(ctx, pi, p.GetArrayElementAtIndex(i), depth + 1));
                        if (count > 16) list.Add("…+" + (count - 16));
                        return list;
                    }
                    SerializedProperty calls = p.FindPropertyRelative("m_PersistentCalls.m_Calls");
                    if (calls != null && calls.isArray)
                    {
                        var list = new List<object>();
                        for (int i = 0; i < calls.arraySize; i++)
                        {
                            SerializedProperty call = calls.GetArrayElementAtIndex(i);
                            var co = new JObj().Add("to", Ref(ctx, pi, call.FindPropertyRelative("m_Target")?.objectReferenceValue))
                                .Add("fn", call.FindPropertyRelative("m_MethodName")?.stringValue);
                            string sa = call.FindPropertyRelative("m_Arguments.m_StringArgument")?.stringValue;
                            if (!string.IsNullOrEmpty(sa)) co.Add("arg", sa);
                            list.Add(co);
                        }
                        return list.Count > 0 ? list : null;
                    }
                    if (depth > 2) return "{…}";
                    var o = new JObj();
                    SerializedProperty it = p.Copy(), end = p.GetEndProperty();
                    bool enter = true;
                    while (it.Next(enter) && !SerializedProperty.EqualContents(it, end))
                    {
                        enter = false;
                        if (it.name.StartsWith("m_", StringComparison.Ordinal)) continue;
                        object v = Value(ctx, pi, it, depth + 1);
                        if (v != null && !(v is string s && s.Length == 0)) o.Add(it.name, v);
                    }
                    return o.Count > 0 ? o : null;
                }
                default: return null;
            }
        }

        /// '@path' inside this prefab, '#prefab' (another prefab; recorded as a spawn), '$script' (Lua), else the asset's content-relative path.
        static object Ref(Ctx ctx, PrefabInfo pi, Object o)
        {
            if (o == null) return null;
            string ap = AssetDatabase.GetAssetPath(o);
            if (string.IsNullOrEmpty(ap)) return "builtin";
            if (ap == pi.path)
            {
                GameObject go = o is Component c ? c.gameObject : o as GameObject;
                if (go == null) return "@?";
                string at = "@" + (go.transform == pi.root.transform ? pi.root.name : pi.root.name + "/" + RelPath(pi.root.transform, go.transform));
                return o is Component cc && !(o is Transform) ? at + "#" + cc.GetType().Name : at;
            }
            if (ap.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) { pi.spawns.Add(ap); return new JObj().Add("prefabRef", ap); }
            if (o is TextAsset && (ap.EndsWith(".lua.txt", StringComparison.OrdinalIgnoreCase) || ap.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))) return "$" + LuaId(ctx, ap);
            if (ap.StartsWith("Resources/unity_builtin", StringComparison.Ordinal) || ap.StartsWith("Library/", StringComparison.Ordinal)) return "builtin";
            return ContentRel(ctx, ap);
        }

        static JObj RewriteRefs(JObj n, Func<string, string> pid)
        {
            var o = new JObj();
            foreach (var kv in n) { if (kv.Key == "prefab") continue; o.Add(kv.Key, RewriteValue(kv.Value, pid)); }
            return o;
        }

        static object RewriteValue(object v, Func<string, string> pid)
        {
            if (v is JObj j)
            {
                if (j.Count == 1 && j["prefabRef"] is string pr) return "#" + pid(pr);
                var o = new JObj(); foreach (var kv in j) o.Add(kv.Key, RewriteValue(kv.Value, pid)); return o;
            }
            if (v is List<object> l) return l.Select(x => RewriteValue(x, pid)).ToList();
            return v;
        }

        static IEnumerable<string> ScriptsOf(JObj node)
        {
            if (node["c"] is List<object> cs) foreach (object c in cs) if (((JObj)c)["script"] is string s) yield return s;
            if (node["ov"] is JObj ov && ov["luaScript"] is string ls) yield return ls.TrimStart('$');
        }

        static JObj Overrides(Ctx ctx, PrefabInfo pi, GameObject instanceRoot)
        {
            PropertyModification[] mods = PrefabUtility.GetPropertyModifications(instanceRoot);
            if (mods == null) return null;
            var rows = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (PropertyModification m in mods)
            {
                string pp = m.propertyPath ?? "";
                if (pp.StartsWith("m_Local", StringComparison.Ordinal) || pp == "m_RootOrder" || pp.StartsWith("m_Anchor", StringComparison.Ordinal)
                    || pp.StartsWith("m_SizeDelta", StringComparison.Ordinal) || pp.StartsWith("m_Pivot", StringComparison.Ordinal) || pp == "m_Name") continue;
                string label = null;
                var im = System.Text.RegularExpressions.Regex.Match(pp, @"^(\w*[Ii]njections)\.Array\.data\[(\d+)\]\.(value|name)$");
                if (im.Success)
                {
                    if (im.Groups[3].Value == "name") continue;
                    string name = null;
                    if (m.target != null)
                    {
                        var so = new SerializedObject(m.target);
                        name = so.FindProperty(im.Groups[1].Value + ".Array.data[" + im.Groups[2].Value + "].name")?.stringValue;
                    }
                    label = !string.IsNullOrEmpty(name) ? name : im.Groups[1].Value + "[" + im.Groups[2].Value + "]";
                }
                else if (pp == "luaScript" || pp == "m_TagString" || pp == "m_IsActive" || pp == "m_Layer" || pp == "m_IsTrigger" || pp == "m_Enabled") label = pp;
                else if ((pp == "m_Sprite" || System.Text.RegularExpressions.Regex.IsMatch(pp, @"^m_Materials\.Array\.data\[\d+\]$"))
                    && m.objectReference != null && WordsArtRx.IsMatch(m.objectReference.name)) { rows["art"] = m.objectReference.name; continue; }
                else if (m.target is MonoBehaviour tmb)
                {
                    MonoScript ms = MonoScript.FromMonoBehaviour(tmb);
                    string sp = ms != null ? AssetDatabase.GetAssetPath(ms) : "";
                    string tn = tmb.GetType().Name;
                    bool ok = (sp.StartsWith("Assets/DreamPark/", StringComparison.Ordinal) || sp.StartsWith(ctx.contentRoot + "/", StringComparison.Ordinal))
                        && !SdkDeny.Contains(tn) && !pp.StartsWith("m_", StringComparison.Ordinal)
                        && !System.Text.RegularExpressions.Regex.IsMatch(pp, "gizmo|preview|editor|debug|packing|layout|^show", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (ok) label = tn + "." + pp;
                }
                if (label == null) continue;
                object v;
                if (m.objectReference != null) v = Ref(ctx, pi, m.objectReference);
                else v = double.TryParse(m.value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ? (object)GameGraphJson.Round(d) : Clip(m.value, 120);
                if (pp == "luaScript" && m.objectReference is TextAsset ta) v = LuaId(ctx, AssetDatabase.GetAssetPath(ta));
                string where = "";
                if (m.target is Component tc && tc.transform.parent != null)
                {
                    Transform srcRoot = tc.transform.root;
                    string rel = RelPath(srcRoot, tc.transform);
                    if (rel.Length > 0) where = rel + ":";
                }
                rows[where + label] = v;
                if (rows.Count >= 40) { rows["…"] = "more"; break; }
            }
            if (rows.Count == 0) return null;
            var o = new JObj();
            foreach (var kv in rows) o.Add(kv.Key, kv.Value);
            return o;
        }

        // ── space (collider geometry in the attraction root's space) ─────────

        sealed class Box
        {
            public Vector3 min, max; public bool known, trigger, mesh, approx;
            public string path, tag; public int layer; public List<string> scripts;
        }

        static JObj Space(GameObject root, List<object> footprint, HashSet<string> fallScripts, Ctx ctx)
        {
            Matrix4x4 toRoot = root.transform.worldToLocalMatrix;
            var boxes = new List<Box>();
            string PathOf(Transform t) => t == root.transform ? root.name : root.name + "/" + RelPath(root.transform, t);
            foreach (Collider col in root.GetComponentsInChildren<Collider>(true))
            {
                if (col == null || !col.enabled || !ActiveChain(col.transform, root.transform)) continue;
                Bounds? local = null;
                Transform frame = col.transform;
                bool approx = false;
                switch (col)
                {
                    case BoxCollider b: local = new Bounds(b.center, b.size); break;
                    case SphereCollider sph: local = new Bounds(sph.center, Vector3.one * sph.radius * 2); break;
                    case CapsuleCollider cc:
                    {
                        var size = Vector3.one * cc.radius * 2; size[cc.direction] = Mathf.Max(cc.height, cc.radius * 2);
                        local = new Bounds(cc.center, size); break;
                    }
                    case MeshCollider mc:
                        if (mc.sharedMesh != null) local = mc.sharedMesh.bounds;
                        else
                        {
                            // generated at runtime from the object's or an ancestor's mesh (procedural pits)
                            for (Transform x = col.transform; x != null && local == null; x = x == root.transform ? null : x.parent)
                            {
                                var mf = x.GetComponent<MeshFilter>();
                                if (mf != null && mf.sharedMesh != null) { local = mf.sharedMesh.bounds; frame = x; approx = x != col.transform; }
                            }
                        }
                        break;
                }
                var box = new Box
                {
                    trigger = col.isTrigger, mesh = col is MeshCollider, approx = approx, path = PathOf(col.transform), tag = col.tag, layer = col.gameObject.layer,
                    scripts = col.GetComponents<LuaBehaviour>().Where(l => l.luaScript != null).Select(l => LuaId(ctx, AssetDatabase.GetAssetPath(l.luaScript))).ToList(),
                };
                if (local.HasValue)
                {
                    Matrix4x4 m = toRoot * frame.localToWorldMatrix;
                    Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
                    Bounds bl = local.Value;
                    for (int i = 0; i < 8; i++)
                    {
                        Vector3 c = new Vector3((i & 1) != 0 ? bl.max.x : bl.min.x, (i & 2) != 0 ? bl.max.y : bl.min.y, (i & 4) != 0 ? bl.max.z : bl.min.z);
                        Vector3 w = m.MultiplyPoint3x4(c);
                        lo = Vector3.Min(lo, w); hi = Vector3.Max(hi, w);
                    }
                    box.min = lo; box.max = hi; box.known = true;
                }
                boxes.Add(box);
            }
            // floor cut-outs (FloorCutout.points): lava / ice pits and holes whose meshes are generated at runtime
            var cutouts = new List<(string path, List<Vector3> poly, List<string> scripts)>();
            foreach (FloorCutout fc in root.GetComponentsInChildren<FloorCutout>(true))
            {
                if (fc == null || fc.points == null || fc.points.Count < 3 || !ActiveChain(fc.transform, root.transform)) continue;
                Matrix4x4 m = toRoot * fc.transform.localToWorldMatrix;
                cutouts.Add((PathOf(fc.transform), fc.points.Select(q => m.MultiplyPoint3x4(q)).ToList(),
                    fc.GetComponents<LuaBehaviour>().Where(l => l.luaScript != null).Select(l => LuaId(ctx, AssetDatabase.GetAssetPath(l.luaScript))).ToList()));
            }
            if (boxes.Count == 0 && cutouts.Count == 0) return null;
            var solid = boxes.Where(b => !b.trigger && b.known).ToList();
            var trig = boxes.Where(b => b.trigger && b.known).ToList();
            (Vector3 lo, Vector3 hi)? Ext(List<Box> list)
            {
                if (list.Count == 0) return null;
                Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
                foreach (var b in list) { lo = Vector3.Min(lo, b.min); hi = Vector3.Max(hi, b.max); }
                return (lo, hi);
            }
            var se = Ext(solid) ?? Ext(trig);
            string axis = "z";
            if (footprint != null && Convert.ToDouble(footprint[0]) > Convert.ToDouble(footprint[1])) axis = "x";
            else if (footprint == null && se.HasValue && (se.Value.hi.x - se.Value.lo.x) > (se.Value.hi.z - se.Value.lo.z)) axis = "x";
            int ai = axis == "x" ? 0 : 2;
            var s = new JObj().Add("axis", axis);
            if (footprint != null) s.Add("footprintM", footprint);
            if (se.HasValue)
            {
                s.Add("extent", new List<object> { V3(se.Value.lo, 2), V3(se.Value.hi, 2) });
                s.Add("startEdge", GameGraphJson.Round(se.Value.lo[ai], 2)).Add("farEdge", GameGraphJson.Round(se.Value.hi[ai], 2));
            }
            s.Add("solids", solid.Count);
            int meshes = boxes.Count(b => b.mesh);
            if (meshes > 0) { s.Add("meshColliders", meshes); int unk = boxes.Count(b => b.mesh && !b.known); if (unk > 0) s.Add("meshUnknownBounds", unk); }
            JObj Item(Box b, string why)
            {
                var o = new JObj().Add("path", b.path).Add("min", V3(b.min, 2)).Add("max", V3(b.max, 2));
                if (b.trigger) o.Add("trigger", true);
                if (b.mesh) o.Add("mesh", true);
                if (b.approx) o.Add("approx", true);
                if (!string.IsNullOrEmpty(b.tag) && b.tag != "Untagged") o.Add("tag", b.tag);
                if (b.layer > 0) o.Add("layer", LayerMask.LayerToName(b.layer));
                if (b.scripts.Count > 0) o.Add("scripts", b.scripts.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList());
                if (why != null) o.Add("why", why);
                return o;
            }
            var trigSorted = trig.OrderBy(b => b.min[ai]).ThenBy(b => b.path, StringComparer.Ordinal).ToList();
            s.Add("triggers", trigSorted.Take(40).Select(b => (object)Item(b, null)).ToList());
            if (trig.Count > 40) s.Add("triggersMore", trig.Count - 40);
            var hz = new List<(double key, string path, JObj o)>();
            foreach (var b in boxes.Where(b => b.known))
            {
                string leaf = b.path.Split('/').Last();
                bool hasTag = !string.IsNullOrEmpty(b.tag) && b.tag != "Untagged";
                string why = hasTag && HazardTagRx.IsMatch(b.tag) ? "tag"
                    : HazardRx.IsMatch(leaf) && !SafeRx.IsMatch(leaf) && !(hasTag && SafeTagRx.IsMatch(b.tag)) ? "name"
                    : b.trigger && b.scripts.Any(fallScripts.Contains) ? "script" : null;
                if (why != null) hz.Add((b.min[ai], b.path, Item(b, why)));
            }
            var cutOut = new List<(double key, string path, JObj o)>();
            foreach (var cu in cutouts)
            {
                Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
                foreach (Vector3 q in cu.poly) { lo = Vector3.Min(lo, q); hi = Vector3.Max(hi, q); }
                bool lava = boxes.Any(b => b.path.StartsWith(cu.path, StringComparison.Ordinal) && !string.IsNullOrEmpty(b.tag) && HazardTagRx.IsMatch(b.tag))
                    || cu.scripts.Any(x => System.Text.RegularExpressions.Regex.IsMatch(x, "lava|pit|ice|water", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                var co = new JObj().Add("path", cu.path).Add("kind", lava ? "hazard" : "hole").Add("min", V3(lo, 2)).Add("max", V3(hi, 2))
                    .Add("poly", cu.poly.Select(q => (object)new List<object> { GameGraphJson.Round(q.x, 2), GameGraphJson.Round(q.z, 2) }).ToList())
                    .Add("y", GameGraphJson.Round(lo.y, 2));
                if (cu.scripts.Count > 0) co.Add("scripts", cu.scripts.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList());
                cutOut.Add((lo[ai], cu.path, co));
                var h = new JObj().Add("path", cu.path).Add("min", V3(lo, 2)).Add("max", V3(hi, 2)).Add("why", "cutout");
                if (cu.scripts.Count > 0) h.Add("scripts", cu.scripts.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList());
                hz.Add((lo[ai], cu.path, h));
            }
            var hzSorted = hz.OrderBy(x => x.key).ThenBy(x => x.path, StringComparer.Ordinal).Select(x => (object)x.o).ToList();
            if (hzSorted.Count > 0) { s.Add("hazards", hzSorted.Take(32).ToList()); if (hzSorted.Count > 32) s.Add("hazardsMore", hzSorted.Count - 32); }
            var cutSorted = cutOut.OrderBy(x => x.key).ThenBy(x => x.path, StringComparer.Ordinal).Select(x => (object)x.o).ToList();
            if (cutSorted.Count > 0) { s.Add("cutouts", cutSorted.Take(24).ToList()); if (cutSorted.Count > 24) s.Add("cutoutsMore", cutSorted.Count - 24); }
            var raised = solid.Where(b => b.max.y >= 0.25f && (b.max.x - b.min.x) * (b.max.z - b.min.z) >= 0.2f).ToList();
            if (raised.Count > 0)
                s.Add("raised", new JObj().Add("n", raised.Count).Add("topMax", GameGraphJson.Round(raised.Max(b => b.max.y), 2))
                    .Add("along", new List<object> { GameGraphJson.Round(raised.Min(b => b.min[ai]), 2), GameGraphJson.Round(raised.Max(b => b.max[ai]), 2) }));
            return s;
        }

        static bool ActiveChain(Transform t, Transform root)
        {
            for (Transform x = t; x != null; x = x.parent) { if (!x.gameObject.activeSelf) return false; if (x == root) break; }
            return true;
        }

        // ── helpers ────────────────────────────────────────────────────────

        /// An attraction's announcements: kind → { by: scripts / "Announce:&lt;prefab&gt;" that announce it, items, heldBy: what holds it }.
        /// From its scripts' Lua index, the kits those scripts run (one level), and its prefabs' Announce components.
        static JObj AnnouncesOf(IEnumerable<string> prefabIds, IEnumerable<string> scriptIds, IDictionary<string, JObj> scripts, IDictionary<string, JObj> prefabs)
        {
            var by = new SortedDictionary<string, (SortedSet<string> by, SortedSet<string> items, SortedSet<string> heldBy)>(StringComparer.Ordinal);
            (SortedSet<string> by, SortedSet<string> items, SortedSet<string> heldBy) At(string k)
            {
                if (!by.TryGetValue(k, out var e)) by[k] = e = (new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));
                return e;
            }
            var own = scriptIds.ToList();
            var all = new SortedSet<string>(own, StringComparer.Ordinal);
            var publisher = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in scripts)
                if (kv.Value["kits"] is JObj ks && ks["publishes"] is List<string> pubs)
                    foreach (string k in pubs) if (!publisher.ContainsKey(k)) publisher[k] = kv.Key;
            foreach (string sid in own)   // one level: never kits of kits (a shared `sal` kit would pull in everything)
                if (scripts.TryGetValue(sid, out var sj) && sj["kits"] is JObj ks && ks["uses"] is List<string> uses)
                    foreach (string k in uses) if (publisher.TryGetValue(k, out var p)) all.Add(p);
            foreach (string sid in all)
            {
                if (!scripts.TryGetValue(sid, out var sj) || !(sj["announces"] is JObj an)) continue;
                foreach (var kv in an)
                {
                    var e = (JObj)kv.Value;
                    At(kv.Key).by.Add(sid);
                    if (e["items"] is List<string> its) At(kv.Key).items.UnionWith(its);
                    if (e["holds"] is List<string> hs) foreach (string h in hs) At(h).heldBy.Add(sid);
                }
            }
            foreach (string pid in prefabIds)
            {
                if (!prefabs.TryGetValue(pid, out var pe) || !(pe["nodes"] is List<object> nodes)) continue;
                foreach (object n in nodes)
                {
                    if (!(((JObj)n)["c"] is List<object> cs)) continue;
                    foreach (object co in cs)
                    {
                        var c = (JObj)co;
                        if ((string)c["t"] != "Announce" || c.Has("off")) continue;
                        string kind = (string)c["kind"];
                        At(kind).by.Add("Announce:" + pid);
                        if (c["item"] is string it) At(kind).items.Add(it);
                        if (c["holds"] is List<object> hl) foreach (object h in hl) At((string)((JObj)h)["kind"]).heldBy.Add("Announce:" + pid);
                    }
                }
            }
            var res = new JObj();
            foreach (var kv in by)
            {
                var o = new JObj().Add("by", kv.Value.by.ToList());
                if (kv.Value.items.Count > 0) o.Add("items", kv.Value.items.ToList());
                if (kv.Value.heldBy.Count > 0) o.Add("heldBy", kv.Value.heldBy.ToList());
                res.Add(kv.Key, o);
            }
            return res;
        }

        /// The authored playlist object when the manifest carries no sourceJson: its pool, badge gate and Lua from the struct.
        static JObj RecordFromStruct(PackagePlaylist p)
        {
            var rec = new JObj();
            if (p.pool != null && p.pool.Count > 0)
                rec.Add("pool", p.pool.Where(x => x != null).Select(x => (object)new JObj().Add("id", NullIfEmpty(x.id)).Add("attraction", NullIfEmpty(x.attraction))
                    .Add("contentId", NullIfEmpty(x.contentId)).Add("challenge", NullIfEmpty(x.challenge)).Add("options", GameGraphJsonReader.Read(x.options) as JObj)
                    .Add("role", NullIfEmpty(x.role)).Add("displayName", NullIfEmpty(x.displayName))).ToList());
            if (GameGraphJsonReader.Read(p.requiresJson) is List<object> gates)
            {
                var badges = gates.OfType<JObj>().Select(g => g["badge"] as string).Where(b => !string.IsNullOrEmpty(b)).Cast<object>().ToList();
                if (badges.Count > 0) rec.Add("requires", new JObj().Add("badges", badges));
            }
            if (!string.IsNullOrEmpty(p.lua)) rec.Add("lua", p.lua);
            return rec;
        }

        static JObj InferKinds(IEnumerable<JObj> scripts)
        {
            var list = scripts.ToList();
            bool Has(System.Text.RegularExpressions.Regex rx) => list.Any(s =>
                (s["signals"] is JObj sg && sg.Any(kv => rx.IsMatch(kv.Key))) || (s["dp"] is JObj dp && dp.Any(kv => rx.IsMatch(kv.Key))));
            System.Text.RegularExpressions.Regex Rx(string p) => new System.Text.RegularExpressions.Regex(p, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var k = new JObj();
            if (Has(Rx(@"^dp\.playlist\.progress$|collect|coin"))) k.Add("collect", "inferred");
            if (Has(Rx(@"boss_defeated|^boss$"))) k.Add("boss", "inferred");
            if (Has(Rx(@"stage_won|exit_reached|^exit$"))) k.Add("reach_end", "inferred");
            if (Has(Rx(@"^dp\.playlist\.score$|^dp\.score\.submit$|score"))) k.Add("score", "inferred");
            if (Has(Rx(@"wave|survive"))) k.Add("survive", "inferred");
            k.Add("moment", "platform");
            return k;
        }

        static string RoleHint(string leaf)
        {
            string n = leaf.ToLowerInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(n, "boss|king|throne|battle")) return "boss?";
            if (System.Text.RegularExpressions.Regex.IsMatch(n, "start|intro|lobby|entrance")) return "start?";
            if (System.Text.RegularExpressions.Regex.IsMatch(n, "end|finale|victory")) return "end?";
            return "stage?";
        }

        static List<BadgeStore.Entry> SafeBadges(string contentId, Ctx ctx)
        {
            try { return BadgeStore.Load(contentId) ?? new List<BadgeStore.Entry>(); }
            catch (Exception e) { ctx.warnings.Add("badges: " + e.Message); return new List<BadgeStore.Entry>(); }
        }

        static string LuaId(Ctx ctx, string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            if (ctx.luaIdByPath.TryGetValue(assetPath, out string id)) return id;
            string eid = "ext:" + LuaLeaf(assetPath);
            ctx.luaIdByPath[assetPath] = eid;
            ctx.externalLua[eid] = assetPath;
            return eid;
        }

        static string LuaLeaf(string p) => StripLuaExt(Path.GetFileName(p));
        static string StripLuaExt(string p) => p.EndsWith(".lua.txt", StringComparison.OrdinalIgnoreCase) ? p.Substring(0, p.Length - 8)
            : p.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ? p.Substring(0, p.Length - 4) : p;
        static string StripExt(string p) { int i = p.LastIndexOf('.'); return i > p.LastIndexOf('/') && i >= 0 ? p.Substring(0, i) : p; }
        static string ContentRel(Ctx ctx, string p) => p.StartsWith(ctx.contentRoot + "/", StringComparison.Ordinal) ? p.Substring(ctx.contentRoot.Length + 1) : p;
        static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
        static string Clip(string s, int n) => s != null && s.Length > n ? s.Substring(0, n) + "…" : s;
        static double R(float v) => GameGraphJson.Round(v);
        static List<object> V2(Vector2 v, int d) => new List<object> { GameGraphJson.Round(v.x, d), GameGraphJson.Round(v.y, d) };
        static List<object> V3(Vector3 v, int d) => new List<object> { GameGraphJson.Round(v.x, d), GameGraphJson.Round(v.y, d), GameGraphJson.Round(v.z, d) };
        static List<object> Pos(Transform t) => V3(t.localPosition, 2);

        public static string Slug(string resourceName)
        {
            string s = System.Text.RegularExpressions.Regex.Replace(resourceName.Trim().ToLowerInvariant(), "[^a-z0-9]+", "__");
            s = s.Trim('_');
            return s.Length > 200 ? s.Substring(0, 200) : s;
        }

        public static string Humanize(string leaf)
        {
            string s = System.Text.RegularExpressions.Regex.Replace(leaf, "^[APL]_", "").Replace('_', ' ');
            s = System.Text.RegularExpressions.Regex.Replace(s, "([a-z0-9])([A-Z])", "$1 $2");
            return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        }

        static JObj ToJObj<T>(IEnumerable<KeyValuePair<string, T>> kvs)
        {
            var o = new JObj();
            foreach (var kv in kvs.OrderBy(k => k.Key, StringComparer.Ordinal)) o.Add(kv.Key, kv.Value);
            return o;
        }

        static string Sha1(string s) { using var h = SHA1.Create(); return Hex(h.ComputeHash(Encoding.UTF8.GetBytes(s))); }
        static string Sha256(string s) { using var h = SHA256.Create(); return Hex(h.ComputeHash(Encoding.UTF8.GetBytes(s))); }
        static string Hex(byte[] b) { var sb = new StringBuilder(b.Length * 2); foreach (byte x in b) sb.Append(x.ToString("x2")); return sb.ToString(); }
    }
}
#endif
