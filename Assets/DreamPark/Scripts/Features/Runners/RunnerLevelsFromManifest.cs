// ─────────────────────────────────────────────────────────────────────
//  RunnerLevelsFromManifest.cs — a title's LEVELS for the board (ways-to-play
//  contract §2): every AttractionTemplate occurrence its release ships, in
//  release order (Adventure → Sequence → Arena → playlist recipes, then
//  occurrence order), deduplicated by address, props / player rigs left out.
//  A level's footprint comes from its authored bounds (the packing envelope
//  the uploader baked), so nothing has to load to plan the board.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;

namespace DreamPark
{
    public static class RunnerLevelsFromManifest
    {
        const float MToFt = 1f / 0.3048f;

        public static List<RunnerLevel> Levels(DreamParkPackageManifest m)
        {
            var list = new List<RunnerLevel>();
            if (m == null) return list;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in new[] { m.adventure, m.sequence, m.arena, m.playlist })
            {
                if (DreamParkPackageManifest.IsAbsent(r) || r.occurrences == null) continue;
                var occ = new List<PackageOccurrence>(r.occurrences);
                occ.Sort((a, b) => (a?.order ?? 0).CompareTo(b?.order ?? 0));
                foreach (var o in occ)
                {
                    if (o == null || string.IsNullOrEmpty(o.resourceAddress)) continue;
                    string role = (o.role ?? "").ToLowerInvariant();
                    if (role == "prop" || role == "player" || role == "manager") continue;
                    if (!seen.Add(o.resourceAddress)) continue;
                    list.Add(FromOccurrence(o));
                }
            }
            // levels only a runner loads (start areas, maps, forks, the Sequence's Start / Game Over levels) — last
            if (m.runnerLevels != null)
                foreach (var o in m.runnerLevels)
                {
                    if (o == null || string.IsNullOrEmpty(o.resourceAddress) || !seen.Add(o.resourceAddress)) continue;
                    list.Add(FromOccurrence(o));
                }
            return list;
        }

        public static RunnerLevel FromOccurrence(PackageOccurrence o)
        {
            float w = 0, d = 0;
            if (o.hasPackingMetadata && o.authoredFootprintMeters.x > 0 && o.authoredFootprintMeters.y > 0)
            { w = o.authoredFootprintMeters.x * MToFt; d = o.authoredFootprintMeters.y * MToFt; }
            else if (o.widthFeet > 0 && o.lengthFeet > 0) { w = o.widthFeet; d = o.lengthFeet; }
            string leaf = RunnerBoardRules.Leaf(o.resourceAddress);
            return new RunnerLevel { id = leaf, name = Pretty(leaf), address = o.resourceAddress, widthFt = w, depthFt = d };
        }

        /// "A_MoleHillCannon" → "Mole Hill Cannon".
        public static string Pretty(string leaf)
        {
            if (string.IsNullOrEmpty(leaf)) return "";
            string s = leaf.StartsWith("A_", StringComparison.Ordinal) || leaf.StartsWith("L_", StringComparison.Ordinal) ? leaf.Substring(2) : leaf;
            var b = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '_' || c == '-') { b.Append(' '); continue; }
                if (i > 0 && char.IsUpper(c) && (char.IsLower(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]) && char.IsUpper(s[i - 1])))) b.Append(' ');
                b.Append(c);
            }
            return b.ToString().Trim();
        }
    }
}
