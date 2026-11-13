#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using System.Linq;

namespace DreamPark.Editor.PlaylistStudio
{
    /// <summary>
    /// The arc graphic's layout (pure): one dot per stop along a line, the climax boss as the big gold node,
    /// a wand between legs that swap in place, hollow dots for platform bookends, and a beat label per leg —
    /// the same reading as the app's mini arc (PickerPlaylists) and the reel's lower third.
    /// </summary>
    public static class PlaylistArcModel
    {
        public sealed class Node
        {
            public int leg, stop;
            public string label, role, beat;
            public bool gold;        // the climax's boss (or the climax's last stop when it has none)
            public bool bookend;
            public bool legStart;
            public bool wandBefore;  // the previous leg swaps in place into this one
            public float x;          // 0..1 along the line
        }

        public sealed class LegSpan { public int leg; public string name, beat; public int minutes; public float x0, x1; public bool swapAfter; }

        public static List<Node> Build(PackagePlaylist p, out List<LegSpan> legs)
        {
            var nodes = new List<Node>();
            legs = new List<LegSpan>();
            if (p?.arc == null) return nodes;
            int climax = p.arc.FindIndex(l => l.beat == "climax");
            for (int i = 0; i < p.arc.Count; i++)
            {
                PlaylistLeg leg = p.arc[i];
                int goldStop = -1;
                if (i == climax && leg.stops.Count > 0)
                {
                    goldStop = leg.stops.FindLastIndex(s => s.role == "boss");
                    if (goldStop < 0) goldStop = leg.stops.FindLastIndex(s => s.role == "miniboss");
                    if (goldStop < 0) goldStop = leg.stops.Count - 1;
                }
                for (int j = 0; j < leg.stops.Count; j++)
                {
                    PlaylistStop s = leg.stops[j];
                    bool bookend = PlaylistBookends.IsPlatformStop(s.contentId, s.attraction);
                    nodes.Add(new Node
                    {
                        leg = i, stop = j, role = s.role, beat = leg.beat,
                        label = !string.IsNullOrEmpty(s.displayName) ? s.displayName
                            : bookend ? PlaylistBookends.DisplayName(s.attraction) : PlaylistLibrary.Pretty(s.attraction),
                        gold = j == goldStop, bookend = bookend, legStart = j == 0,
                        wandBefore = j == 0 && i > 0 && p.arc[i - 1].next == "swap_in_place",
                    });
                }
            }
            int n = nodes.Count;
            for (int k = 0; k < n; k++) nodes[k].x = n == 1 ? 0.5f : k / (float)(n - 1);
            for (int i = 0; i < p.arc.Count; i++)
            {
                var mine = nodes.Where(x => x.leg == i).ToList();
                if (mine.Count == 0) continue;
                legs.Add(new LegSpan
                {
                    leg = i, name = p.arc[i].name, beat = p.arc[i].beat, minutes = p.arc[i].minutes,
                    x0 = mine.First().x, x1 = mine.Last().x, swapAfter = i < p.arc.Count - 1 && p.arc[i].next == "swap_in_place",
                });
            }
            return nodes;
        }

        /// "Coin Garden → ✦ Icy Ambush → ★ Cannonsnout" — the one-line arc for lists (✦ = swapped in place, ★ = climax).
        public static string OneLine(PackagePlaylist p)
        {
            var nodes = Build(p, out _);
            return string.Join(" → ", nodes.Select(x => (x.wandBefore ? "✦ " : "") + (x.gold ? "★ " : "") + x.label));
        }
    }
}
#endif
