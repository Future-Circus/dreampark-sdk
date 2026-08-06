// ─────────────────────────────────────────────────────────────────────
//  ParkSimSelection.cs — which content goes in THIS generation
//
//  A project can hold far more attractions and props than the park has
//  places to put them, and the fill between them is not free: GapFiller
//  tessellates the combined bounds of everything placed, so "spawn it all"
//  is both impossible past the marker count and expensive well before it.
//  So each generation places a subset, and Regenerate is how you see the
//  rest.
//
//  THREE RULES, in priority order.
//
//  1. WHAT WAS IN YOUR SCENE IS ALWAYS PLACED. Anything on screen when you
//     pressed Play is pinned and appears in every single generation. The
//     attraction you are working on must be in the park you are looking
//     at — a Regenerate that rotated it out would make the button useless
//     for the one thing it is most used for. Content injected by a host
//     tool is pinned for the same reason: somebody asked for it by name.
//
//  2. THE REST FILLS TO 80% ATTRACTIONS / 20% PROPS. Attractions are what
//     the park is made of and what actually exercises calibration,
//     GapFiller seams and culling; props are dressing. If one pool cannot
//     meet its share the slack goes to the other, so capacity is never
//     wasted on a project that is all attractions or all props.
//
//  3. CAPACITY IS THE MARKER COUNT, never a constant. park.fbx decides how
//     many places exist; adding markers raises the ceiling with no code
//     change, and content beyond it rotates.
//
//  A REAL PARK TAKES RULE 1 AND STOPS. When the simulator is dropping
//  content into a park somebody else built, that park is already full of
//  its own attractions — the free pool is not filling empty space, it is
//  crowding a venue. So pinnedOnly turns rules 2 and 3 off entirely and
//  places exactly what was asked for: your scene, plus your injections.
//  Rotating strangers through a customer's park would bury the one thing
//  you opened it to look at.
//
//  NOT TRUE RANDOM — A SHUFFLE BAG. Independent random draws would show
//  you the same attraction three generations running and leave another
//  unseen for a dozen. Instead each pool is shuffled ONCE into a fixed
//  order and a cursor walks it across generations, reshuffling only when
//  it wraps. Every item is therefore placed exactly once per cycle, so a
//  pool of 90 with 32 slots is fully seen in three Regenerates rather than
//  "probably most of it, eventually". The order changes between cycles, so
//  it does not become a fixed rotation either.
//
//  The cursor is static and survives Regenerate, which is the whole point;
//  it resets with the domain reload on leaving Play, which is the right
//  lifetime for "work through the project this session".
// ─────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using UnityEngine;

namespace DreamPark.ParkSim
{
    internal static class ParkSimSelection
    {
        /// Share of the non-pinned slots given to props.
        private const float PropShare = 0.20f;

        private static readonly ShuffleBag Attractions = new ShuffleBag();
        private static readonly ShuffleBag Props = new ShuffleBag();

        /// <summary>
        /// Pick the content for one generation. <paramref name="capacity"/> is
        /// the number of spawn markers the park actually has.
        ///
        /// <paramref name="pinnedOnly"/> places what was asked for and nothing
        /// else — see the header. Used when the park came from a source rather
        /// than from park.fbx.
        /// </summary>
        public static List<ContentEntry> Choose(
            List<ContentEntry> all, int capacity, int seed, List<string> notes,
            bool pinnedOnly = false)
        {
            var chosen = new List<ContentEntry>();
            if (all == null || all.Count == 0) return chosen;

            capacity = Mathf.Max(1, capacity);

            var pinned = new List<ContentEntry>();
            var freeAttractions = new List<ContentEntry>();
            var freeProps = new List<ContentEntry>();

            foreach (var e in all)
            {
                if (e == null || e.Source == null) continue;
                if (e.fromScene) { pinned.Add(e); continue; }
                if (e.kind == ContentKind.Attraction) freeAttractions.Add(e);
                else if (e.kind == ContentKind.Prop) freeProps.Add(e);
            }

            // Pinned content outranks capacity — but the park physically
            // cannot hold more than it has markers for, so say so rather than
            // quietly dropping the tail.
            if (pinned.Count >= capacity)
            {
                for (int i = 0; i < capacity && i < pinned.Count; i++) chosen.Add(pinned[i]);
                if (pinned.Count > capacity)
                {
                    notes.Add(string.Format(
                        "{0} object(s) are pinned but the park only has {1} place(s) to put them — " +
                        "{2} were left out. Regenerate cannot help here; the request itself is over " +
                        "capacity.",
                        pinned.Count, capacity, pinned.Count - capacity));
                }
                return chosen;
            }

            chosen.AddRange(pinned);

            if (pinnedOnly)
            {
                int skipped = freeAttractions.Count + freeProps.Count;
                if (skipped > 0)
                {
                    notes.Add(string.Format(
                        "Placed {0} pinned object(s). This park already has its own content, so the " +
                        "{1} other prefab(s) in your project were not rotated into it.",
                        pinned.Count, skipped));
                }
                return chosen;
            }

            int remaining = capacity - pinned.Count;

            Attractions.Sync(freeAttractions, seed);
            Props.Sync(freeProps, seed * 31 + 7);

            int propTarget = Mathf.RoundToInt(remaining * PropShare);
            int attrTarget = remaining - propTarget;

            // Never ask a pool for more than it holds — a bag that wrapped
            // mid-request would otherwise hand back the same entry twice.
            int attrTake = Mathf.Min(attrTarget, freeAttractions.Count);
            int propTake = Mathf.Min(propTarget, freeProps.Count);

            // Give unused capacity from a short pool to the other one, so an
            // all-attractions project still fills the park.
            int slack = remaining - attrTake - propTake;
            if (slack > 0)
            {
                int more = Mathf.Min(slack, freeAttractions.Count - attrTake);
                attrTake += more; slack -= more;
                more = Mathf.Min(slack, freeProps.Count - propTake);
                propTake += more;
            }

            Attractions.Take(attrTake, chosen);
            Props.Take(propTake, chosen);

            int rotating = freeAttractions.Count + freeProps.Count;
            int placedFree = attrTake + propTake;
            if (rotating > placedFree)
            {
                notes.Add(string.Format(
                    "Placed {0} of {1} rotating items ({2} attractions, {3} props) plus {4} pinned from " +
                    "your scene. Regenerate to see the rest — every item appears once per cycle, so " +
                    "{5} more generation(s) covers all of them.",
                    placedFree, rotating, attrTake, propTake, pinned.Count,
                    Mathf.CeilToInt((float)rotating / Mathf.Max(1, placedFree)) - 1));
            }

            return chosen;
        }

        /// Drop the cycling state. Called when the pools become meaningless —
        /// a fresh park build from a different scene.
        public static void Reset()
        {
            Attractions.Clear();
            Props.Clear();
        }

        /// <summary>
        /// A fixed shuffled order plus a cursor that survives between
        /// generations. Reshuffles only on wrap, so one full pass places every
        /// item exactly once and the next pass uses a different order.
        /// </summary>
        private class ShuffleBag
        {
            private readonly List<ContentEntry> _order = new List<ContentEntry>();
            private string _signature;
            private int _cursor;
            private int _cycle;
            private int _seed;

            public void Clear()
            {
                _order.Clear(); _signature = null; _cursor = 0; _cycle = 0;
            }

            /// Rebuild ONLY when the pool's membership actually changed —
            /// rebuilding every generation would reset the cursor and the
            /// cycling would never make progress.
            public void Sync(List<ContentEntry> pool, int seed)
            {
                string sig = Signature(pool);
                if (sig == _signature) return;

                _order.Clear();
                _order.AddRange(pool);
                _signature = sig;
                _seed = seed;
                _cycle = 0;
                _cursor = 0;
                Shuffle(_order, _seed);
            }

            public void Take(int count, List<ContentEntry> into)
            {
                if (_order.Count == 0 || count <= 0) return;
                count = Mathf.Min(count, _order.Count);

                // A wrap mid-request reshuffles, which could otherwise hand
                // back something already added in this same generation.
                var taken = new HashSet<ContentEntry>();

                int guard = _order.Count * 2 + count;
                while (taken.Count < count && guard-- > 0)
                {
                    if (_cursor >= _order.Count)
                    {
                        _cycle++;
                        _cursor = 0;
                        Shuffle(_order, _seed ^ (_cycle * 397));
                    }

                    var entry = _order[_cursor++];
                    if (taken.Add(entry)) into.Add(entry);
                }
            }

            private static string Signature(List<ContentEntry> pool)
            {
                if (pool.Count == 0) return "";
                var parts = new List<string>(pool.Count);
                foreach (var e in pool)
                {
                    parts.Add(string.IsNullOrEmpty(e.assetPath) ? e.displayName : e.assetPath);
                }
                parts.Sort(System.StringComparer.Ordinal);
                return string.Join("\n", parts);
            }

            private static void Shuffle(List<ContentEntry> list, int seed)
            {
                var rng = new System.Random(seed);
                for (int i = list.Count - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    var tmp = list[i]; list[i] = list[j]; list[j] = tmp;
                }
            }
        }
    }
}
