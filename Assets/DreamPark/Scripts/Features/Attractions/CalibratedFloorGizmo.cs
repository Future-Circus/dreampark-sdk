// ─────────────────────────────────────────────────────────────────────
//  CalibratedFloorGizmo.cs — draw the floor that EXISTS, not the one
//  that was authored
//
//  LevelTemplate.OnDrawGizmos draws the attraction's footprint from its
//  authored dimensions: a wire rectangle, a translucent slab and a grid,
//  all at local y = 0. That is the right picture right up until the floor
//  calibrates, at which point every vertex has its own height and the
//  purple plane is left hanging in space describing a floor that is no
//  longer there — visually indistinguishable from a floor that FAILED to
//  calibrate, which is the worst possible failure for a debugging aid.
//
//  So once a conformed mesh exists this draws from the mesh itself. The
//  floor mesh IS the grid — LevelTemplate generates it as a row-major
//  (gridX+1) x (gridY+1) lattice — so the same lines the gizmo always drew
//  are recoverable directly from the vertex array, now at their real
//  heights. What you see is literally the mesh points.
//
//  EDGES COME FROM THE TRIANGLES, NOT FROM THE INDEX MATH. Drawing every
//  grid-adjacent pair would rule lines straight across FloorCutouts, whose
//  triangles CalibrateLevel.RecutHolesAndUpdateMesh has removed. Counting
//  how many triangles each edge belongs to solves both problems at once:
//  an edge in two triangles is interior (faint grid), an edge in one is a
//  rim — the outside of the floor or the lip of a cutout — and gets the
//  solid boundary colour. Holes outline themselves, for free.
//
//  Diagonals are skipped. They exist in the triangulation but they were
//  never part of the grid the creator authored, and drawing them turns a
//  readable lattice into noise.
//
//  BEFORE CALIBRATION THIS DECLINES. TryDraw returns false when there is
//  no runtime mesh, when the mesh is not the expected lattice, or when
//  every vertex is still flat — and LevelTemplate falls back to exactly
//  the gizmo it drew before. Edit mode is untouched.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
namespace DreamPark
{
    using System.Collections.Generic;
    using UnityEngine;

    internal static class CalibratedFloorGizmo
    {
        /// A vertex is "conformed" once it carries any height at all. Matches
        /// the tolerance CalibrateLevel.hasFloorData uses so the two agree
        /// about whether a floor has been calibrated.
        private const float FlatTolerance = 0.001f;

        // Reused across every template and every frame: OnDrawGizmos runs
        // continuously for every attraction in the scene, and mesh.vertices
        // would allocate a fresh array each time.
        private static readonly List<Vector3> _verts = new List<Vector3>();
        private static readonly List<int> _tris = new List<int>();

        // Edge -> how many triangles contain it. Topology only changes when
        // holes are re-cut (which changes the triangle count), so that plus
        // the mesh identity is a sound cache key. Vertex POSITIONS are re-read
        // every frame, so a re-conform is picked up immediately.
        private static readonly Dictionary<long, int> _edgeUse = new Dictionary<long, int>();
        private static int _cachedMeshId;
        private static int _cachedTriCount;

        public static bool TryDraw(
            LevelTemplate template, Color outline, Color fill, Color gridColor, bool showGrid)
        {
            if (template == null || template.runtimePlane == null) return false;

            var filter = template.runtimePlane.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null) return false;

            int vx = template.gridX + 1;
            int vy = template.gridY + 1;
            if (template.gridX <= 0 || template.gridY <= 0) return false;
            if (mesh.vertexCount != vx * vy) return false;

            mesh.GetVertices(_verts);
            if (!IsConformed(_verts)) return false;

            // runtimePlane is parented at identity local position/rotation/
            // scale, so the mesh's local space and the template's local space
            // are the same — the caller's Gizmos.matrix already applies.
            Gizmos.color = fill;
            Gizmos.DrawMesh(mesh, Vector3.zero);

            EnsureEdgeCache(mesh);

            foreach (var pair in _edgeUse)
            {
                int a = (int)(pair.Key >> 32);
                int b = (int)(pair.Key & 0xffffffffL);
                if (!IsGridAdjacent(a, b, vx)) continue;   // skip triangulation diagonals

                bool rim = pair.Value == 1;
                if (!rim && !showGrid) continue;

                Gizmos.color = rim ? outline : gridColor;
                Gizmos.DrawLine(_verts[a], _verts[b]);
            }

            return true;
        }

        private static bool IsConformed(List<Vector3> verts)
        {
            for (int i = 0; i < verts.Count; i++)
            {
                if (Mathf.Abs(verts[i].y) > FlatTolerance) return true;
            }
            return false;
        }

        /// Two vertices are grid-adjacent when they are neighbours in the
        /// row-major lattice — one step along a row, or one row apart in the
        /// same column. Everything else a triangle contains is a diagonal.
        private static bool IsGridAdjacent(int a, int b, int vx)
        {
            int ax = a % vx, ay = a / vx;
            int bx = b % vx, by = b / vx;
            if (ay == by) return Mathf.Abs(ax - bx) == 1;
            if (ax == bx) return Mathf.Abs(ay - by) == 1;
            return false;
        }

        private static void EnsureEdgeCache(Mesh mesh)
        {
            mesh.GetTriangles(_tris, 0);
            int id = mesh.GetInstanceID();
            if (id == _cachedMeshId && _tris.Count == _cachedTriCount) return;

            _edgeUse.Clear();
            for (int t = 0; t + 2 < _tris.Count; t += 3)
            {
                Bump(_tris[t], _tris[t + 1]);
                Bump(_tris[t + 1], _tris[t + 2]);
                Bump(_tris[t + 2], _tris[t]);
            }
            _cachedMeshId = id;
            _cachedTriCount = _tris.Count;
        }

        /// Undirected: the smaller index always goes in the high word, so the
        /// two windings of a shared edge collapse onto one key and the count
        /// reaches 2 rather than staying at 1 twice.
        private static void Bump(int a, int b)
        {
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            long key = ((long)lo << 32) | ((long)hi & 0xffffffffL);
            _edgeUse.TryGetValue(key, out int n);
            _edgeUse[key] = n + 1;
        }
    }
}
#endif
