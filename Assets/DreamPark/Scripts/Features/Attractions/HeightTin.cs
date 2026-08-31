// ─────────────────────────────────────────────────────────────────────
//  HeightTin.cs — linear interpolation between neighbouring floors
//
//  WHY THIS REPLACES INVERSE-DISTANCE WEIGHTING. GapFiller used to blend
//  every floor's nearest-edge height by 1/d^p. Shepard's method has GLOBAL
//  SUPPORT: every contributor pulls on every point, and far from any edge
//  the surface relaxes toward the mean of the whole park. Measured, with
//  two attractions both at 5.0m twenty metres apart and one unrelated
//  floor forty metres further away at 0m, the fill midway between the two
//  came out at 4.90m (p=2) or 4.55m (p=1) instead of 5.00m. Remove the
//  distant floor and both give exactly 5.00. So the basin between two
//  attractions was caused entirely by geometry that had nothing to do
//  with that gap, and no exponent fixes it — a lower power widens the
//  contamination, a higher one turns the edges into ledges.
//
//  A TRIANGULATED IRREGULAR NETWORK has none of that. Sample every floor's
//  boundary, Delaunay-triangulate the samples in XZ, and interpolate each
//  gap vertex barycentrically inside whichever triangle contains it.
//  Delaunay adjacency is precisely "which floors face each other across
//  this gap", so the surface between two floors is a straight ramp from
//  one edge to the other. Two floors at the same height give a flat fill
//  no matter what else exists in the park, because barycentric
//  interpolation is bounded by its own three corners rather than by the
//  global set.
//
//  PURE ARITHMETIC, NO UNITY API. GapFiller computes on a worker thread,
//  so this holds only structs and does only maths — the same contract
//  FloorHeightSampler works under.
//
//  Circumcircle tests run in double precision. Nearly-cocircular boundary
//  samples are the normal case here, not a corner case: floors are
//  rectangles, so their sampled corners are exactly cocircular, and float
//  error there flips triangles and tears holes in the surface.
// ─────────────────────────────────────────────────────────────────────

namespace DreamPark
{
    using System.Collections.Generic;
    using UnityEngine;

    internal sealed class HeightTin
    {
        private struct Tri { public int a, b, c; }

        private readonly List<Vector3> _pts = new List<Vector3>();   // x, height, z
        private readonly List<Tri> _tris = new List<Tri>();
        private readonly Dictionary<long, List<int>> _buckets = new Dictionary<long, List<int>>();

        /// Boundary edges of the triangulation — those belonging to exactly one
        /// triangle. Used to extend the surface past the content instead of
        /// handing the perimeter to a different interpolator, which is what put
        /// a cliff around every attraction.
        private readonly List<int> _hull = new List<int>();   // flattened index pairs
        private float _cell = 1f;
        private bool _built;

        public bool IsUsable { get { return _built && _tris.Count > 0; } }
        public int PointCount { get { return _pts.Count; } }
        public int TriangleCount { get { return _tris.Count; } }

        /// <summary>
        /// Build from boundary samples. Duplicates are collapsed — floors that
        /// abut share sample positions, and a repeated point makes the
        /// triangulation degenerate.
        /// </summary>
        public HeightTin(List<Vector3> samples, float bucketCell)
        {
            if (samples == null || samples.Count < 3) return;

            _cell = Mathf.Max(0.25f, bucketCell);

            var seen = new HashSet<long>();
            for (int i = 0; i < samples.Count; i++)
            {
                // 1cm dedupe grid: finer than any sampling we do, coarse
                // enough to catch two floors sharing an edge sample.
                long key = ((long)Mathf.RoundToInt(samples[i].x * 100f) << 32)
                         ^ (uint)Mathf.RoundToInt(samples[i].z * 100f);
                if (!seen.Add(key)) continue;
                _pts.Add(samples[i]);
            }
            if (_pts.Count < 3) return;

            Triangulate();
            if (_tris.Count > 0) { BuildBuckets(); _built = true; }
        }

        /// <summary>
        /// Height at (x, z), interpolated inside the content and EXTENDED
        /// outward beyond it.
        ///
        /// Beyond the hull there is nothing to interpolate between, so the
        /// surface holds the height of the nearest point on the boundary. That
        /// is continuous by construction: approaching the hull from outside,
        /// the nearest boundary point converges on the query point itself, and
        /// the value it carries is the same linear interpolation along that
        /// edge that the triangle inside produces. The two agree exactly where
        /// they meet.
        ///
        /// Handing the outside to a different interpolator is what created the
        /// cliff: the padding ring was drawn by a global distance blend while
        /// the interior was barycentric, and two unrelated surfaces met at the
        /// hull with nothing forcing them to agree.
        /// </summary>
        public bool TrySampleOrExtend(Vector2 p, out float height)
        {
            if (TrySample(p, out height)) return true;
            return TryNearestHullHeight(p, out height);
        }

        /// Height at the closest point on the boundary. Linear scan — the hull
        /// is a small fraction of the triangulation and this only runs for
        /// points outside it, which is the padding ring.
        private bool TryNearestHullHeight(Vector2 p, out float height)
        {
            height = 0f;
            if (_hull.Count == 0) return false;

            float bestSqr = float.MaxValue;
            for (int e = 0; e < _hull.Count; e += 2)
            {
                Vector3 A = _pts[_hull[e]], B = _pts[_hull[e + 1]];
                Vector2 a = new Vector2(A.x, A.z), b = new Vector2(B.x, B.z);
                Vector2 ab = b - a;

                float len2 = ab.sqrMagnitude;
                float t = len2 > 1e-9f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
                Vector2 q = a + ab * t;

                float d2 = (p - q).sqrMagnitude;
                if (d2 >= bestSqr) continue;
                bestSqr = d2;
                height = Mathf.Lerp(A.y, B.y, t);
            }
            return true;
        }

        /// <summary>
        /// Height at (x, z) by barycentric interpolation, or false when the
        /// point falls outside the triangulated hull.
        /// </summary>
        public bool TrySample(Vector2 p, out float height)
        {
            height = 0f;
            if (!_built) return false;

            long key = Key(CellOf(p.x), CellOf(p.y));
            if (!_buckets.TryGetValue(key, out var list)) return false;

            for (int i = 0; i < list.Count; i++)
            {
                var t = _tris[list[i]];
                Vector3 A = _pts[t.a], B = _pts[t.b], C = _pts[t.c];

                float d = (B.z - C.z) * (A.x - C.x) + (C.x - B.x) * (A.z - C.z);
                if (Mathf.Abs(d) < 1e-9f) continue;

                float u = ((B.z - C.z) * (p.x - C.x) + (C.x - B.x) * (p.y - C.z)) / d;
                float v = ((C.z - A.z) * (p.x - C.x) + (A.x - C.x) * (p.y - C.z)) / d;
                float w = 1f - u - v;

                // Small tolerance so a point sitting exactly on a shared edge
                // resolves into one of the two triangles instead of neither.
                const float e = -1e-4f;
                if (u < e || v < e || w < e) continue;

                height = u * A.y + v * B.y + w * C.y;
                return true;
            }
            return false;
        }

        // ── Bowyer-Watson ────────────────────────────────────────────────

        private void Triangulate()
        {
            // Super-triangle large enough to contain everything, so every real
            // point is inserted into an existing triangulation rather than
            // seeding one.
            var b = new Bounds(new Vector3(_pts[0].x, 0f, _pts[0].z), Vector3.zero);
            for (int i = 1; i < _pts.Count; i++) b.Encapsulate(new Vector3(_pts[i].x, 0f, _pts[i].z));

            float m = Mathf.Max(b.size.x, b.size.z) * 10f + 100f;
            Vector3 c = new Vector3(b.center.x, 0f, b.center.z);

            int s0 = _pts.Count, s1 = s0 + 1, s2 = s0 + 2;
            _pts.Add(new Vector3(c.x - m, 0f, c.z - m));
            _pts.Add(new Vector3(c.x + m, 0f, c.z - m));
            _pts.Add(new Vector3(c.x, 0f, c.z + m));
            _tris.Add(new Tri { a = s0, b = s1, c = s2 });

            var bad = new List<int>();
            var edges = new List<int>();   // flattened pairs

            for (int i = 0; i < s0; i++)
            {
                bad.Clear(); edges.Clear();

                for (int t = 0; t < _tris.Count; t++)
                {
                    if (InCircumcircle(_pts[i], _pts[_tris[t].a], _pts[_tris[t].b], _pts[_tris[t].c]))
                        bad.Add(t);
                }
                if (bad.Count == 0) continue;

                // Cavity boundary: edges belonging to exactly one bad triangle.
                for (int k = 0; k < bad.Count; k++)
                {
                    var t = _tris[bad[k]];
                    AddEdge(edges, t.a, t.b);
                    AddEdge(edges, t.b, t.c);
                    AddEdge(edges, t.c, t.a);
                }

                for (int k = bad.Count - 1; k >= 0; k--) _tris.RemoveAt(bad[k]);

                for (int e = 0; e < edges.Count; e += 2)
                {
                    if (edges[e] < 0) continue;   // marked as shared
                    _tris.Add(new Tri { a = edges[e], b = edges[e + 1], c = i });
                }
            }

            // Drop anything still touching the super-triangle.
            for (int t = _tris.Count - 1; t >= 0; t--)
            {
                var tr = _tris[t];
                if (tr.a >= s0 || tr.b >= s0 || tr.c >= s0) _tris.RemoveAt(t);
            }
            _pts.RemoveRange(s0, 3);

            CollectHull();
        }

        /// An edge in exactly one triangle is on the outside. Counted rather
        /// than walked, because the hull of a Delaunay triangulation over
        /// several separate floors is not a single loop and a walk would only
        /// find one component.
        private void CollectHull()
        {
            var count = new Dictionary<long, int>();
            for (int t = 0; t < _tris.Count; t++)
            {
                var tr = _tris[t];
                Bump(count, tr.a, tr.b); Bump(count, tr.b, tr.c); Bump(count, tr.c, tr.a);
            }
            foreach (var kv in count)
            {
                if (kv.Value != 1) continue;
                _hull.Add((int)(kv.Key >> 32));
                _hull.Add((int)(kv.Key & 0xffffffffL));
            }
        }

        private static void Bump(Dictionary<long, int> map, int u, int v)
        {
            int lo = u < v ? u : v, hi = u < v ? v : u;
            long key = ((long)lo << 32) | (uint)hi;
            map.TryGetValue(key, out int n);
            map[key] = n + 1;
        }

        /// Push an edge, cancelling it against an identical one already
        /// present — a shared edge is interior to the cavity and must not
        /// become part of its boundary.
        private static void AddEdge(List<int> edges, int u, int v)
        {
            for (int e = 0; e < edges.Count; e += 2)
            {
                if (edges[e] < 0) continue;
                if ((edges[e] == u && edges[e + 1] == v) || (edges[e] == v && edges[e + 1] == u))
                {
                    edges[e] = -1; edges[e + 1] = -1;
                    return;
                }
            }
            edges.Add(u); edges.Add(v);
        }

        /// Double precision deliberately — floors are rectangles, so their
        /// sampled corners are exactly cocircular and float error there flips
        /// triangles and tears holes in the fill.
        private static bool InCircumcircle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            double ax = a.x - p.x, ay = a.z - p.z;
            double bx = b.x - p.x, by = b.z - p.z;
            double cx = c.x - p.x, cy = c.z - p.z;

            double det =
                (ax * ax + ay * ay) * (bx * cy - cx * by)
              - (bx * bx + by * by) * (ax * cy - cx * ay)
              + (cx * cx + cy * cy) * (ax * by - bx * ay);

            // Sign depends on winding; normalise by the triangle's orientation.
            double orient = (b.x - a.x) * (double)(c.z - a.z) - (c.x - a.x) * (double)(b.z - a.z);
            return orient > 0 ? det > 0 : det < 0;
        }

        // ── Point location ───────────────────────────────────────────────

        private void BuildBuckets()
        {
            for (int t = 0; t < _tris.Count; t++)
            {
                var tr = _tris[t];
                Vector3 A = _pts[tr.a], B = _pts[tr.b], C = _pts[tr.c];
                int minX = CellOf(Mathf.Min(A.x, B.x, C.x)), maxX = CellOf(Mathf.Max(A.x, B.x, C.x));
                int minZ = CellOf(Mathf.Min(A.z, B.z, C.z)), maxZ = CellOf(Mathf.Max(A.z, B.z, C.z));

                for (int x = minX; x <= maxX; x++)
                {
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        long key = Key(x, z);
                        if (!_buckets.TryGetValue(key, out var list))
                        {
                            list = new List<int>();
                            _buckets[key] = list;
                        }
                        list.Add(t);
                    }
                }
            }
        }

        private int CellOf(float v) { return Mathf.FloorToInt(v / _cell); }
        private static long Key(int x, int z) { return ((long)x << 32) ^ (uint)z; }
    }
}
