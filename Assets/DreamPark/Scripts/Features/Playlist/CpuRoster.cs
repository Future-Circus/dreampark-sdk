// ─────────────────────────────────────────────────────────────────────
//  CpuRoster.cs — CPU players as a platform primitive (Docs/CPU_PLAYERS.md).
//
//  A title declares its CPU cast ONCE (Lua `dp.cpu.register{…}`): a stable
//  id ("cpu:<id>"), a name, a portrait (an https URL and/or a title texture,
//  e.g. a HUD atlas cell), a livery colour, a difficulty, a subtitle, a tag
//  ("CPU" by default; DreamSports says "DreamPal") and a champion flag.
//  Every surface that lists players reads it: the run's scoreboard rows take
//  their face and tag from here when a CPU reports (dp.playlist.standings /
//  report_result / score with a "cpu:" id), so a title reports a CPU by id
//  and nothing else.
//
//  Device-local on purpose: every device runs the same title Lua, so every
//  device registers the same cast. A title texture never crosses the wire:
//  each device bakes its own PNG (PortraitUrl, a file:// URL in the app's
//  cache) for its own surfaces (the headset podium, the phone's Results).
//  The pure parts (Normalize, Fill, Medal*) are tested offline.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace DreamPark
{
    /// One declared CPU player.
    public sealed class CpuEntrant
    {
        /// "cpu:<stable id>", stable for the whole run so its totals accumulate across legs.
        public string id;
        public string name;
        /// An absolute https image (≤ 256 px) — the face Web, reels and other devices can load.
        public string avatar;
        /// Livery "#RRGGBB" (the orb / disc colour when it has no portrait).
        public string color;
        /// The title's own word: "easy" | "normal" | "hard" | anything short.
        public string difficulty;
        /// A short subtitle under the name ("Tennis Champion").
        public string title;
        /// The small tag every surface shows beside a CPU ("CPU" when empty).
        public string tag;
        /// The title's champion (the hardest opponent): surfaces add a crown / "CHAMPION".
        public bool champion;
        /// A title texture (a Texture, Sprite or Material from the title's own package) and the cell of it that is
        /// the portrait (uv: x = u0, y = v0, w, h; bottom-left origin, Unity's). Baked to a PNG on first use.
        public UnityEngine.Object portrait;
        public Rect uv = new Rect(0, 0, 1, 1);
    }

    public static class CpuRoster
    {
        public const string Prefix = "cpu:";
        public const string DefaultTag = "CPU";
        public const int MaxEntrants = 64;

        static readonly Dictionary<string, CpuEntrant> s_cast = new Dictionary<string, CpuEntrant>(StringComparer.Ordinal);
        static readonly Dictionary<string, string> s_baked = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly Dictionary<string, Texture2D> s_tex = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        public static int Version { get; private set; }

        // ── Pure ──────────────────────────────────────────────

        /// "PIP" / "pip" / "cpu:Pip" → "cpu:pip". Lower-case, trimmed, spaces → '-', ≤ 60 chars. "" when nothing left.
        public static string Normalize(string raw)
        {
            string s = (raw ?? "").Trim();
            if (s.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) s = s.Substring(Prefix.Length);
            s = s.Trim().ToLowerInvariant().Replace(' ', '-');
            if (s.Length == 0) return "";
            if (s.Length > 56) s = s.Substring(0, 56);
            return Prefix + s;
        }

        /// A "cpu:" id (the fallback test; a row's `kind` wins).
        public static bool IsCpuId(string id) => !string.IsNullOrEmpty(id) && id.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

        /// Medal → points (gold 3 · silver 2 · bronze 1 · none 0): "gold" | "silver" | "bronze" | 3 | 2 | 1.
        public static int MedalRank(string medal)
        {
            switch ((medal ?? "").Trim().ToLowerInvariant())
            {
                case "gold": case "3": return 3;
                case "silver": case "2": return 2;
                case "bronze": case "1": return 1;
                default: return 0;
            }
        }

        public static string MedalName(int rank) => rank == 3 ? "gold" : rank == 2 ? "silver" : rank == 1 ? "bronze" : "";

        /// Copy a declared CPU's face onto a scoreboard row (the row's own values win; kind becomes cpu). True = changed.
        public static bool Fill(PlaylistScoreRow row, CpuEntrant e)
        {
            if (row == null || e == null) return false;
            bool changed = false;
            if (row.kind != PlaylistScoreRow.KindCpu) { row.kind = PlaylistScoreRow.KindCpu; changed = true; }
            if (string.IsNullOrEmpty(row.name) && !string.IsNullOrEmpty(e.name)) { row.name = e.name; changed = true; }
            if (string.IsNullOrEmpty(row.avatar) && !string.IsNullOrEmpty(e.avatar)) { row.avatar = e.avatar; changed = true; }
            if (string.IsNullOrEmpty(row.color) && !string.IsNullOrEmpty(e.color)) { row.color = e.color; changed = true; }
            if (string.IsNullOrEmpty(row.tag) && !string.IsNullOrEmpty(e.tag)) { row.tag = e.tag; changed = true; }
            if (string.IsNullOrEmpty(row.title) && !string.IsNullOrEmpty(e.title)) { row.title = e.title; changed = true; }
            if (string.IsNullOrEmpty(row.difficulty) && !string.IsNullOrEmpty(e.difficulty)) { row.difficulty = e.difficulty; changed = true; }
            if (e.champion && !row.champion) { row.champion = true; changed = true; }
            return changed;
        }

        // ── The cast (device-local) ───────────────────────────

        /// Declare (or update) one CPU. Returns its normalized id ("" = refused: no id and no name, or the cast is full).
        public static string Register(CpuEntrant e)
        {
            if (e == null) return "";
            string id = Normalize(!string.IsNullOrEmpty(e.id) ? e.id : e.name);
            if (id.Length == 0) return "";
            if (!s_cast.ContainsKey(id) && s_cast.Count >= MaxEntrants) { Debug.LogWarning("[CPU] roster full (" + MaxEntrants + "): " + id); return ""; }
            e.id = id;
            if (string.IsNullOrEmpty(e.name)) e.name = id.Substring(Prefix.Length).ToUpperInvariant();
            if (s_cast.TryGetValue(id, out var old) && (old.portrait != e.portrait || old.uv != e.uv)) { s_baked.Remove(id); s_tex.Remove(id); }
            s_cast[id] = e;
            Version++;
            return id;
        }

        public static CpuEntrant Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (s_cast.TryGetValue(id, out var e)) return e;
            return s_cast.TryGetValue(Normalize(id), out e) ? e : null;
        }

        /// A CPU by id: a declared one, or any "cpu:" id.
        public static bool IsCpu(string id) => IsCpuId(id) || (!string.IsNullOrEmpty(id) && s_cast.ContainsKey(id));

        public static IEnumerable<CpuEntrant> All => s_cast.Values;
        public static int Count => s_cast.Count;

        public static void Clear() { s_cast.Clear(); s_baked.Clear(); s_tex.Clear(); Version++; }

        /// Fill a row from the cast when it is a declared CPU. True = changed.
        public static bool Resolve(PlaylistScoreRow row) => row != null && Fill(row, Get(row.id));

        // ── Portraits ─────────────────────────────────────────

        /// THIS device's portrait for a CPU: the baked title texture (file:// PNG in the app cache), else its https
        /// avatar, else null. Main thread (it may bake once).
        public static string PortraitUrl(string id)
        {
            var e = Get(id);
            if (e == null) return null;
            if (e.portrait != null)
            {
                if (s_baked.TryGetValue(e.id, out string url)) return url;
                url = Bake(e);
                if (url != null) { s_baked[e.id] = url; return url; }
            }
            return string.IsNullOrEmpty(e.avatar) ? null : e.avatar;
        }

        /// The baked portrait texture, by CPU id or by its file:// URL (the headset's avatar loader asks first).
        public static bool TryTexture(string idOrUrl, out Texture2D tex)
        {
            tex = null;
            if (string.IsNullOrEmpty(idOrUrl)) return false;
            if (s_tex.TryGetValue(idOrUrl, out tex) && tex != null) return true;
            foreach (var kv in s_baked)
                if (kv.Value == idOrUrl && s_tex.TryGetValue(kv.Key, out tex) && tex != null) return true;
            var e = Get(idOrUrl);
            if (e?.portrait == null) return false;
            PortraitUrl(e.id);
            return s_tex.TryGetValue(e.id, out tex) && tex != null;
        }

        /// The portrait's source texture and its uv cell (a Sprite brings its own rect; a Material its main map).
        static Texture Source(CpuEntrant e, out Rect uv)
        {
            uv = e.uv;
            switch (e.portrait)
            {
                case Sprite sp when sp != null && sp.texture != null:
                {
                    Rect r = sp.textureRect;
                    float w = sp.texture.width, h = sp.texture.height;
                    uv = new Rect(r.x / w, r.y / h, r.width / w, r.height / h);
                    return sp.texture;
                }
                case Material m when m != null:
                    if (m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") != null) return m.GetTexture("_BaseMap");
                    return m.mainTexture;
                case Texture t when t != null:
                    return t;
                default:
                    return null;
            }
        }

        /// GPU-blit the cell to a 160 px square (works for non-readable / compressed atlases), keep the texture, and
        /// write a PNG to <c>temporaryCachePath/dp-cpu/</c>. null when there is nothing to bake or no GPU.
        static string Bake(CpuEntrant e, int size = 160)
        {
            RenderTexture prev = RenderTexture.active, rt = null;
            try
            {
                Texture src = Source(e, out Rect uv);
                if (src == null || uv.width <= 0f || uv.height <= 0f) return null;
                rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(src, rt, new Vector2(uv.width, uv.height), new Vector2(uv.x, uv.y));
                RenderTexture.active = rt;
                var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "CpuPortrait_" + e.id, wrapMode = TextureWrapMode.Clamp };
                t.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                t.Apply(false, false);
                s_tex[e.id] = t;
                string dir = Path.Combine(Application.temporaryCachePath, "dp-cpu");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, SafeFile(e.id) + ".png");
                File.WriteAllBytes(file, t.EncodeToPNG());
                return new Uri(file).AbsoluteUri;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CPU] portrait bake failed for " + e.id + ": " + ex.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }

        static string SafeFile(string id)
        {
            var sb = new StringBuilder();
            foreach (char c in id ?? "") sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }
    }
}
