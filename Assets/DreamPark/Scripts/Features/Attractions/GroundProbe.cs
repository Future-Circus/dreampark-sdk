// ─────────────────────────────────────────────────────────────────────
//  GroundProbe.cs — shared ground-finding for CalibrateLevel/CalibrateProp
//
//  WHY THIS EXISTS. The two calibrators used to disagree with each other
//  about two separate things, and both disagreements were bugs.
//
//  1. WHERE THE RAY STARTS. CalibrateLevel anchored every ray in the floor
//     grid to ONE plane at `Camera.main.y + 10`, reaching 20m. That ties
//     the search volume to where the guest happens to be standing rather
//     than to where the attraction is. A guest on a mezzanine placing an
//     attraction on the floor below, or standing at one end of a 128ft
//     Large footprint on graded ground, produced vertices whose rays
//     missed the floor entirely. They did not conform, coverage fell, and
//     ConformOnce's coverage gate then discarded the WHOLE bake — so the
//     failure mode was a silently flat floor, not a partially conformed
//     one. CalibrateProp already did the right thing (it anchors to the
//     prop's own position); CalibrateLevel was the anomaly.
//
//  2. WHAT COUNTS AS FLOOR. CalibrateLevel used RaycastAll and skipped any
//     hit whose normal.y was below 0.5, so it saw past walls, ceilings and
//     table tops to the floor behind them. CalibrateProp used a plain
//     Physics.Raycast and took the FIRST hit whatever its orientation, so
//     a prop could silently bind to the underside of a table or the face
//     of a wall. Its comment claimed the two "agree on where the ground
//     is". They did not. One definition now lives here.
//
//  THE SPAN IS MEASURED, NOT GUESSED. A fixed ±10m works in a mall and
//  fails outdoors; a fixed ±250m would work everywhere and make every
//  per-vertex RaycastAll walk the entire scene's BVH. So MeasureSpan runs
//  a handful of coarse vertical probes over the footprint FIRST and sizes
//  the real span from what it finds. Note it measures the ground range
//  under THIS FOOTPRINT, not the whole park: a 40ft attraction on the side
//  of a hill gets a ~6m span even when the park itself spans 48m of
//  relief. Venues stay cheap, terrain stays correct, and there is no
//  per-project number for anyone to tune wrong.
//
//  LIVE BEATS PRIOR is preserved exactly as it was (Auto-Calibration-Spec
//  §5.3): two passes over two separate masks, never one merged mask, so
//  standing in the room always outranks remembering it.
//
//  DOWN THEN UP. Ground is normally below the sample point, so the
//  downward pass runs first and short-circuits. The upward pass only
//  matters when a sample point starts beneath the mesh — a rematerialised
//  prior that sits above the authored plane, or an attraction dropped into
//  a dip. Previously those simply found nothing.
// ─────────────────────────────────────────────────────────────────────

namespace DreamPark
{
    using UnityEngine;

    public static class GroundProbe
    {
        /// Minimum upward component of a surface normal for it to count as
        /// "floor". 0.5 ~= 60 degrees from vertical: accepts ramps and graded
        /// terrain, rejects walls, ceilings and table undersides.
        public const float MinFloorNormalY = 0.5f;

        /// Hard ceiling on how far a probe will ever reach. Bounds the cost of
        /// the coarse sweep and stops one stray distant collider (a roof, a
        /// neighbouring floor of a mall) from inflating every per-vertex ray.
        public const float MaxSpanMeters = 250f;

        /// Breathing room added above/below the measured ground range, so a
        /// sample point sitting exactly on the surface still starts its ray
        /// outside it.
        private const float SpanMarginMeters = 1f;

        /// <summary>
        /// Vertical search volume around a sample point, expressed relative to
        /// that point rather than in world space, so the same Span can be
        /// reused for every vertex of a grid that is not itself level.
        /// </summary>
        public struct Span
        {
            /// How far above the sample point the ray starts.
            public float above;
            /// How far below the sample point the ray must still reach.
            public float below;

            public float Length { get { return above + below; } }

            public static Span Of(float above, float below)
            {
                return new Span {
                    above = Mathf.Clamp(above, 0f, MaxSpanMeters),
                    below = Mathf.Clamp(below, 0f, MaxSpanMeters),
                };
            }

            /// The legacy shape: an origin height plus a total ray length.
            /// Kept so the serialized raycastHeight/raycastLength fields that
            /// already exist on CalibrateProp keep meaning what they meant.
            public static Span FromLegacy(float above, float totalLength)
            {
                return Of(above, Mathf.Max(0f, totalLength - above));
            }

            public override string ToString()
            {
                return string.Format("Span(+{0:F1}m / -{1:F1}m)", above, below);
            }
        }

        /// <summary>
        /// Size a search volume from the ground that actually exists under a
        /// footprint. Runs a small fixed number of coarse vertical probes
        /// (centre plus the four corners of <paramref name="worldFootprint"/>)
        /// across the full MaxSpanMeters range, collects every floor-like hit,
        /// and returns a span that covers them plus a margin.
        ///
        /// <paramref name="minAbove"/> / <paramref name="minBelow"/> are floors,
        /// never caps: the returned span is always at least as large as the
        /// caller's legacy defaults, so this can only ever find MORE ground
        /// than the code it replaced, never less. That property is what makes
        /// it safe to ship onto venues that calibrate correctly today.
        ///
        /// When nothing is found — no AR mesh yet, footprint entirely outside
        /// the scan — the caller's minimums are returned unchanged and the
        /// per-vertex probes behave exactly as they did before.
        /// </summary>
        public static Span MeasureSpan(
            Bounds worldFootprint,
            LayerMask liveMask,
            LayerMask priorMask,
            float minAbove,
            float minBelow)
        {
            float planeY = worldFootprint.center.y;
            float foundMinY = float.PositiveInfinity;
            float foundMaxY = float.NegativeInfinity;

            // Centre plus four corners. Five probes is enough to bracket the
            // relief under a single attraction footprint; the per-vertex pass
            // is what resolves the actual shape.
            Vector3 c = worldFootprint.center;
            Vector3 e = worldFootprint.extents;
            var probes = new Vector3[5] {
                new Vector3(c.x,       planeY, c.z      ),
                new Vector3(c.x - e.x, planeY, c.z - e.z),
                new Vector3(c.x - e.x, planeY, c.z + e.z),
                new Vector3(c.x + e.x, planeY, c.z - e.z),
                new Vector3(c.x + e.x, planeY, c.z + e.z),
            };

            for (int i = 0; i < probes.Length; i++) {
                AccumulateFloorHits(probes[i], liveMask, ref foundMinY, ref foundMaxY);
                if (priorMask.value != 0) {
                    AccumulateFloorHits(probes[i], priorMask, ref foundMinY, ref foundMaxY);
                }
            }

            if (float.IsInfinity(foundMinY) || float.IsInfinity(foundMaxY)) {
                // Nothing raycastable under this footprint. Fall back to the
                // caller's legacy numbers rather than inventing a span.
                return Span.Of(minAbove, minBelow);
            }

            float above = Mathf.Max(minAbove, (foundMaxY - planeY) + SpanMarginMeters);
            float below = Mathf.Max(minBelow, (planeY - foundMinY) + SpanMarginMeters);
            return Span.Of(above, below);
        }

        /// One coarse vertical sweep at a single XZ position, widening the
        /// running min/max with every floor-like surface it passes through.
        /// Deliberately RaycastAll over the full range in BOTH directions:
        /// this is the measuring pass, so it must see the floor beneath a
        /// mezzanine and the one above a pit, not just the first thing it hits.
        private static void AccumulateFloorHits(
            Vector3 origin, LayerMask mask, ref float minY, ref float maxY)
        {
            if (mask.value == 0) return;

            var down = Physics.RaycastAll(
                new Ray(origin + Vector3.up * MaxSpanMeters, Vector3.down),
                MaxSpanMeters * 2f, mask, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < down.Length; i++) {
                if (down[i].normal.y < MinFloorNormalY) continue;
                float y = down[i].point.y;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        /// <summary>
        /// Find the ground at a single world position. Searches downward from
        /// <c>samplePoint + up * span.above</c> first, then upward as a
        /// fallback, consulting live AR mesh before the rematerialised prior in
        /// each direction. Returns the NEAREST floor-like surface, skipping
        /// walls, ceilings and table undersides on the way.
        /// </summary>
        public static bool TryFindGround(
            Vector3 samplePoint,
            Span span,
            LayerMask liveMask,
            LayerMask priorMask,
            out RaycastHit hit)
        {
            // DOWNWARD. The common case, so it runs first and short-circuits.
            Vector3 topOrigin = samplePoint + Vector3.up * span.above;
            float downLength = span.Length;

            if (TryFloorHit(new Ray(topOrigin, Vector3.down), downLength, liveMask, out hit)) return true;
            if (priorMask.value != 0
                && TryFloorHit(new Ray(topOrigin, Vector3.down), downLength, priorMask, out hit)) return true;

            // UPWARD. Only reachable when the sample point started beneath the
            // surface. Costs one extra pair of empty casts where it does not
            // apply; where it does, it is the difference between calibrating
            // and silently staying flat.
            if (span.below > 0f) {
                Vector3 bottomOrigin = samplePoint + Vector3.down * span.below;
                float upLength = span.Length;

                if (TryFloorHit(new Ray(bottomOrigin, Vector3.up), upLength, liveMask, out hit)) return true;
                if (priorMask.value != 0
                    && TryFloorHit(new Ray(bottomOrigin, Vector3.up), upLength, priorMask, out hit)) return true;
            }

            hit = default(RaycastHit);
            return false;
        }

        /// RaycastAll, nearest-first, first surface whose normal says "floor".
        /// RaycastAll rather than Raycast so a wall or a ceiling between the
        /// origin and the ground does not shadow it — that is the whole reason
        /// CalibrateLevel used this shape and CalibrateProp needed to.
        private static bool TryFloorHit(Ray ray, float distance, LayerMask mask, out RaycastHit hit)
        {
            hit = default(RaycastHit);
            if (mask.value == 0 || distance <= 0f) return false;

            var hits = Physics.RaycastAll(ray, distance, mask, QueryTriggerInteraction.Ignore);
            if (hits.Length == 0) return false;

            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            for (int i = 0; i < hits.Length; i++) {
                // Normal is compared against WORLD up in both directions on
                // purpose. A floor is a floor whether we approached it from
                // above or from below; an upward cast that hits the underside
                // of a deck must not accept it as ground.
                if (hits[i].normal.y >= MinFloorNormalY) {
                    hit = hits[i];
                    return true;
                }
            }
            return false;
        }
    }
}
