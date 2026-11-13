// ─────────────────────────────────────────────────────────────────────
//  RunnerCatalog.cs — finds a title's ways to play and its levels, and builds
//  the `runners` block the Content Uploader sends with commitUpload
//  (contract ways-to-play.md §3; handoff sdk-tools.md §2; Docs/RUNNERS.md).
//
//  Shared by the uploader (SDK), the pre-upload check and the Ways to Play
//  panel so all three read the SAME headers the same way.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Defective.JSON;
using UnityEditor;
using UnityEngine;

namespace DreamPark.WaysToPlay
{
    public sealed class RunnerEntry
    {
        public string assetPath;          // Assets/Content/<Title>/Runners/pvp.lua.txt
        public string runnerId;
        public string source;
        public string sourceSha256;
        public RunnerHeaderInfo header;
        public string coverAssetPath = ""; // resolved Assets/... path ("" = none / missing)
        public bool coverMissing;
        public string coverSha256 = "";
        public string coverMime = "";
    }

    public static class RunnerCatalog
    {

        public static string ContentRoot(string gameId) => "Assets/Content/" + gameId;
        public static string RunnersFolder(string contentRoot) => contentRoot.TrimEnd('/') + "/" + RunnerIds.FolderName;

        /// Every runner script under &lt;root&gt;/Runners (*.lua.txt), sorted by runnerId, headers read,
        /// exactly one default resolved.
        public static List<RunnerEntry> Discover(string contentRoot)
        {
            var list = new List<RunnerEntry>();
            string folder = RunnersFolder(contentRoot);
            if (!Directory.Exists(folder)) return list;
            foreach (string file in Directory.GetFiles(folder, "*" + RunnerIds.FileSuffix, SearchOption.TopDirectoryOnly))
            {
                string path = file.Replace('\\', '/');
                string text = File.ReadAllText(path);
                var e = new RunnerEntry
                {
                    assetPath = path,
                    runnerId = RunnerIds.FromFileName(path),
                    source = text,
                    sourceSha256 = Sha256(Encoding.UTF8.GetBytes(text)),
                };
                e.header = RunnerHeaderReader.Read(text, e.runnerId);
                ResolveCover(contentRoot, e);
                list.Add(e);
            }
            list.Sort((a, b) => string.CompareOrdinal(a.runnerId, b.runnerId));
            RunnerHeaderReader.ResolveDefault(list.Select(x => x.header).ToList(), out _);
            return list;
        }

        /// Files in Runners/ that will NOT ship (a bare .lua is not a TextAsset).
        public static List<string> StrayFiles(string contentRoot)
        {
            var list = new List<string>();
            string folder = RunnersFolder(contentRoot);
            if (!Directory.Exists(folder)) return list;
            foreach (string file in Directory.GetFiles(folder, "*.lua", SearchOption.TopDirectoryOnly))
                list.Add(file.Replace('\\', '/'));
            return list;
        }

        static void ResolveCover(string contentRoot, RunnerEntry e)
        {
            string cover = e.header?.cover;
            if (string.IsNullOrEmpty(cover)) return;
            string path = cover.StartsWith("Assets/", StringComparison.Ordinal) ? cover : contentRoot.TrimEnd('/') + "/" + cover.TrimStart('/');
            if (!File.Exists(path)) { e.coverMissing = true; return; }
            e.coverAssetPath = path;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            e.coverMime = ext == ".png" ? "image/png" : ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" : "";
            e.coverSha256 = Sha256(File.ReadAllBytes(path));
        }

        public const int MaxRunners = 32;

        /// The `runners` ARRAY for commitUpload (ways-web handoff §1: top-level, beside `packages`):
        /// [{runnerId, name, blurb, coverUrl|null, default, scriptPath}], display order = runnerId order,
        /// at most one default (exactly one here). null = the title has no Runners folder (send nothing:
        /// the server serves its implicit default). Only runners whose header reads cleanly are exported;
        /// `problems` lists the rest (the pre-upload check blocks on them first). `coverUrls` = runnerId →
        /// the URL POST /api/content/:id/runners/cover returned (missing = null).
        public static JSONObject BuildExportJson(string contentRoot, out List<string> problems, IDictionary<string, string> coverUrls = null)
        {
            problems = new List<string>();
            List<RunnerEntry> entries = Discover(contentRoot);
            if (entries.Count == 0) return null;
            var good = entries.Where(e => e.header.Ok).Take(MaxRunners).ToList();
            foreach (var bad in entries.Where(e => !e.header.Ok)) problems.Add(bad.runnerId + ": " + bad.header.error);
            if (entries.Count(e => e.header.Ok) > MaxRunners) problems.Add($"more than {MaxRunners} ways to play; only the first {MaxRunners} are sent");
            RunnerHeaderReader.ResolveDefault(good.Select(x => x.header).ToList(), out bool twoMarked);
            if (twoMarked) problems.Add("more than one runner says default = true; the first by name is used");

            var items = new JSONObject(JSONObject.Type.Array);
            foreach (var e in good)
            {
                var it = new JSONObject(JSONObject.Type.Object);
                it.AddField("runnerId", e.runnerId);
                it.AddField("name", e.header.name);
                it.AddField("blurb", e.header.blurb ?? "");
                string url = null;
                if (coverUrls != null) coverUrls.TryGetValue(e.runnerId, out url);
                if (string.IsNullOrEmpty(url)) it.AddField("coverUrl", JSONObject.nullObject); else it.AddField("coverUrl", url);
                it.AddField("default", e.header.isDefault);
                it.AddField("scriptPath", ScriptPath(e.assetPath));
                items.Add(it);
            }
            return items;
        }

        /// "Assets/Content/LaserTag/Runners/pvp.lua.txt" → "Content/LaserTag/Runners/pvp.lua.txt".
        public static string ScriptPath(string assetPath) =>
            assetPath != null && assetPath.StartsWith("Assets/", StringComparison.Ordinal) ? assetPath.Substring(7) : assetPath ?? "";

        /// The DreamParkPackageManifest.runners list (the headset reads its directors from it, runtime handoff §1).
        public static List<RunnerRecord> BuildManifestRunners(string contentRoot)
        {
            var list = new List<RunnerRecord>();
            foreach (var e in Discover(contentRoot).Where(e => e.header.Ok).Take(MaxRunners))
                list.Add(new RunnerRecord { runnerId = e.runnerId, name = e.header.name, blurb = e.header.blurb, cover = e.header.cover, isDefault = e.header.isDefault, lua = e.source });
            return list;
        }

        /// Upload every runner cover (POST /api/content/:id/runners/cover, multipart `image` + `runnerId`) and
        /// return runnerId → coverUrl. Blocking (an editor upload step); a failed cover is skipped with a warning
        /// and the runner ships with coverUrl null — never fails the upload.
        public static Dictionary<string, string> UploadCovers(string uploadContentId, string contentRoot, string authToken, string baseUrl)
        {
            var urls = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(uploadContentId) || string.IsNullOrEmpty(authToken)) return urls;
            foreach (var e in Discover(contentRoot).Where(e => e.header.Ok && !string.IsNullOrEmpty(e.coverAssetPath) && !string.IsNullOrEmpty(e.coverMime)))
            {
                try
                {
                    var form = new List<UnityEngine.Networking.IMultipartFormSection>
                    {
                        new UnityEngine.Networking.MultipartFormDataSection("runnerId", e.runnerId),
                        new UnityEngine.Networking.MultipartFormFileSection("image", File.ReadAllBytes(e.coverAssetPath), Path.GetFileName(e.coverAssetPath), e.coverMime),
                    };
                    string url = baseUrl + "/api/content/" + Uri.EscapeDataString(uploadContentId) + "/runners/cover";
                    using (var req = UnityEngine.Networking.UnityWebRequest.Post(url, form))
                    {
                        req.SetRequestHeader("Authorization", authToken);
                        req.timeout = 60;
                        var op = req.SendWebRequest();
                        var started = DateTime.UtcNow;
                        while (!op.isDone && (DateTime.UtcNow - started).TotalSeconds < 65) System.Threading.Thread.Sleep(25);
                        string body = req.downloadHandler != null ? req.downloadHandler.text : "";
                        if (req.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                        {
                            var j = new JSONObject(body);
                            string cu = j.GetField("coverUrl")?.stringValue;
                            if (!string.IsNullOrEmpty(cu)) { urls[e.runnerId] = cu; Debug.Log($"[WaysToPlay] cover for '{e.runnerId}' → {cu}"); }
                        }
                        else Debug.LogWarning($"[WaysToPlay] cover upload for '{e.runnerId}' failed ({req.responseCode} {req.error}); it ships without a cover. {body}");
                    }
                }
                catch (Exception ex) { Debug.LogWarning($"[WaysToPlay] cover upload for '{e.runnerId}' failed: {ex.Message}"); }
            }
            return urls;
        }

        public static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(bytes ?? Array.Empty<byte>());
                var sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// Content roots the panel offers: every Assets/Content/&lt;Title&gt; plus the SDK sample.
        public static List<string> CandidateRoots()
        {
            var roots = new List<string>();
            if (Directory.Exists("Assets/Content"))
                foreach (string d in Directory.GetDirectories("Assets/Content"))
                    roots.Add(d.Replace('\\', '/'));
            roots.Sort(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(SampleRoot)) roots.Add(SampleRoot);
            return roots;
        }

        public const string SampleRoot = "Assets/DreamPark/Samples/WaysToPlay";
    }

    /// One level a director can load, as the simulator / checks see it.
    public sealed class TitleLevel
    {
        public string name;
        public string assetPath;
        public Vector2 sizeFeet;
        public int footprintTiles;          // 1, 2 or 4
        public bool fitsOneTileByFrame;     // a layout frame fits 12 × 18 ft (2/4-tile levels)
        public List<EdgeSocket> sockets = new List<EdgeSocket>();   // on the prefab asset (read only)
        public GameObject prefab;
    }

    public static class TitleLevels
    {
        public const float TileWidthFeet = 12f, TileDepthFeet = 18f;
        const float Slack = 0.75f;

        /// Prefabs under the root carrying a LevelTemplate, plus any prefab under a Levels/ folder
        /// (the SDK sample's plain rooms). Sorted by name.
        public static List<TitleLevel> Discover(string contentRoot)
        {
            var list = new List<TitleLevel>();
            if (!AssetDatabase.IsValidFolder(contentRoot)) return list;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { contentRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                // under Runners/: only runner-only LEVELS (start areas, maps, forks) — never links or other prefabs
                if (path.Contains("/Runners/"))
                {
                    var rg = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (rg == null || rg.GetComponent<LevelTemplate>() == null) continue;
                }
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) continue;
                var lt = go.GetComponent<LevelTemplate>();
                bool inLevels = path.Contains("/Levels/");
                // Titles whose levels are AttractionTemplate prefabs (most titles): the runtime reads them from the
                // manifest's occurrences (RunnerLevelsFromManifest), so the simulator lists them too (ww-proof, Oct 26).
                var atLevel = lt == null ? go.GetComponent<AttractionTemplate>() : null;
                if (lt == null && !inLevels && atLevel == null) continue;
                var lvl = new TitleLevel { name = Path.GetFileNameWithoutExtension(path), assetPath = path, prefab = go };
                lvl.sizeFeet = SizeFeet(go, lt);
                lvl.footprintTiles = FootprintTiles(lvl.sizeFeet);
                lvl.fitsOneTileByFrame = lvl.footprintTiles == 1 || FrameFitsOneTile(go);
                go.GetComponentsInChildren(true, lvl.sockets);
                list.Add(lvl);
            }
            list.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        static Vector2 SizeFeet(GameObject go, LevelTemplate lt)
        {
            if (lt != null)
            {
                Vector3 s = lt.Size;
                if (s.x > 0.01f && s.z > 0.01f) return new Vector2(s.x, s.z) / 0.3048f;
            }
            // An attraction's baked authored footprint (the number the uploader ships as the occurrence's
            // authoredFootprintMeters, which the headset's RunnerLevelsFromManifest reads).
            var at = lt == null ? go.GetComponent<AttractionTemplate>() : null;
            if (at != null && at.PackingBake != null)
            {
                Vector2 m = at.PackingBake.AuthoredFootprintMeters;
                if (m.x > 0.01f && m.y > 0.01f) return m / 0.3048f;
            }
            // Edge Socket looks straddle the edge, so they do not count toward the level's own size.
            var renderers = go.GetComponentsInChildren<Renderer>(true).Where(r => r.GetComponentInParent<EdgeSocket>(true) == null).ToArray();
            if (renderers.Length == 0) return new Vector2(TileWidthFeet, TileDepthFeet);
            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            return new Vector2(b.size.x, b.size.z) / 0.3048f;
        }

        /// 1 (≤ 12 × 18 ft, either way round), 2 (≤ 24 × 18), else 4 (24 × 36 board).
        /// The runtime's rule (RunnerBoardRules.Footprint), so the panel, the checks and the headset agree.
        public static int FootprintTiles(Vector2 feet) => RunnerBoardRules.Footprint(feet.x, feet.y);

        static bool FrameFitsOneTile(GameObject go)
        {
            var at = go.GetComponent<AttractionTemplate>();
            if (at == null) return false;
            foreach (var f in at.LayoutFrames)
            {
                if (f == null) continue;
                float a = Mathf.Min(f.sizeFeet.x, f.sizeFeet.y), b = Mathf.Max(f.sizeFeet.x, f.sizeFeet.y);
                if (a > 0f && a <= TileWidthFeet + Slack && b <= TileDepthFeet + Slack) return true;
            }
            return false;
        }
    }
}
