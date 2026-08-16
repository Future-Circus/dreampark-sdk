// ─────────────────────────────────────────────────────────────────────
//  VoronoiFracture.cs — the Shatterable bake, without DinoFracture
//
//  WHY THIS EXISTS
//
//  EasyShatter drives DinoFracture, which is a separately licensed Asset
//  Store package the SDK does not bundle. Without it EasyShatter compiles
//  as a no-op stub and firing it logs a warning. That is a fine path for
//  a creator who owns the licence and an impossible one to put behind a
//  right-click menu item for someone who does not. So the pieces are
//  baked here, at edit time, out of geometry we already have.
//
//  WHY VORONOI, AND NOT A PRETTIER FRACTURE
//
//  Not because it looks best. Because every Voronoi cell is CONVEX, and
//  that one property collapses three hard sub-problems into easy ones:
//
//   1. The cut faces are convex polygons, so a triangle fan is a correct
//      triangulation. No ear clipping, no self-intersection handling.
//   2. Every piece takes a convex MeshCollider for free — which is
//      exactly what Stage 4 says a moving Rigidbody requires. Unity
//      SILENTLY disables a non-convex MeshCollider on a non-kinematic
//      body, and the symptom ("my shards fall through the floor") points
//      at nothing. Note "for free", not "for exact": the CELL is convex,
//      but a shard is the cell INTERSECTED WITH THE SOURCE, so a shard
//      cut out of a bowl, a ring or a doorway inherits that concavity and
//      its hull bridges it.
//   3. No Voronoi library is needed. A cell is the intersection of the
//      half-spaces bisecting its site against every other site, so the
//      whole algorithm is "clip the triangle soup by one plane at a
//      time", Sutherland–Hodgman per triangle.
//
//  WHAT BREAKS WITHOUT THE CARE TAKEN BELOW
//
//   • Loose .asset meshes. Twelve pieces per prop, re-baked four times,
//     is how you end up with a content folder holding 300 orphaned mesh
//     files nobody dares delete and an Addressables group that churns on
//     every bake. Every generated mesh here is a SUB-ASSET of the prefab
//     (AssetDatabase.AddObjectToAsset), and a re-bake purges the ones the
//     previous bake left behind before it writes new ones.
//   • Silent holes. An open, non-watertight source cannot be capped: the
//     cut-edge loop does not close. Emitting the piece anyway and saying
//     nothing means the hole is first seen on a headset. Detected here,
//     reported by name, and the affected pieces are emitted UNCAPPED so
//     at least the failure is the shape of the source.
//   • Piece count. Every piece is a draw call AND a rigidbody. The report
//     states both in absolute numbers, not as a ratio, because "3× the
//     triangles" means nothing at a venue and "36 draw calls" does.
//   • A cancelled bake shipping as a successful one. Five of twelve cells
//     is not a coarser fracture, it is five shards' worth of the prop and
//     nothing else — the rest VANISHES when it breaks. Cancel is a flag
//     on FractureNotes, not a phrase inside a warning string, and it ends
//     the bake with a Failed line and no pieces written.
//   • A throw across the module boundary. The caller is between
//     LoadPrefabContents and SaveAsPrefabAsset; an exception escaping here
//     skips its save and leaves the creator a stack trace with no Failed
//     line. BakeInto catches, cleans the partial node out and reports.
//
//  WHERE THE PIECES LIVE: Anchor/Shattered, inactive, with every piece
//  collider baked DISABLED. Under Anchor because Anchor never rotates and
//  never carries a per-frame writer (hazard 1).
//
//  The disabled colliders are hazard 2, and the reason is not the one it
//  looks like. Deactivating the node does NOT hide the pieces from
//  PropTemplate: TryGetColliderFootprint (PropTemplate.cs:354) calls
//  GetComponentsInChildren<Collider>(true) — includeInactive is TRUE — and
//  its only filter is `if (!collider.enabled) continue`. Component.enabled
//  is independent of GameObject.activeInHierarchy, so twelve enabled shard
//  colliders on an inactive node all reach the footprint, and GapFiller
//  (GapFiller.cs:929) consumes that footprint at runtime. Worse than an
//  inflated footprint: a Collider on an inactive GameObject has no PhysX
//  shape behind it, so Collider.bounds reads back zeroed at the world
//  origin, which drags the footprint's min corner to (0,0,0) and has
//  GapFiller cut a floor hole stretching from the prop to the origin.
//  Baked disabled, the pieces contribute nothing; the prop's generated
//  them one at a time as it fires them, after it has detached the node.
//
//  Lua script also detaches the node on fire so the shards never drag
//  the footprint around the floor with them.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DreamPark.ConvertReady
{
    public static class VoronoiFracture
    {
        // ── Names. Constants because a re-bake has to FIND what the last
        //    bake wrote in order to replace it rather than duplicate it. ──

        public const string ShatteredNodeName = "Shattered";
        public const string PieceNodePrefix = "Piece_";
        /// Every generated mesh carries this prefix so a re-bake can identify
        /// its own sub-assets in the prefab and remove exactly those.
        public const string PieceMeshPrefix = "ShatterPiece_";

        // ── Budget ──────────────────────────────────────────────────────

        /// Two cells is the smallest thing that is still a fracture. The
        /// ceiling is not a taste call: the clip is O(cells² × triangles),
        /// so 256 cells on a 20k-triangle model is minutes, not seconds.
        public const int MinCells = 2;
        public const int MaxCells = 256;

        /// Best-candidate sampling: draw this many random points and keep the
        /// one furthest from every site placed so far. Uniform random sites
        /// clump, and a clump makes two enormous shards and ten slivers.
        public const int SiteCandidates = 8;

        /// A shard can legitimately weigh 20 g — BehaviorPackBuilder's 0.1 kg
        /// floor is right for a whole prop and wrong for a piece of one.
        public const float MinPieceMassKg = 0.02f;

        /// Unity's convex hull cooker caps at 255 faces and silently
        /// simplifies past it. Harmless where the source was already convex;
        /// where the shard inherited a concavity from the source, the hull
        /// bridges it and the shard collides bigger than it looks. Feeds the
        /// report either way — the bake does not refuse over it.
        public const int ConvexHullTriangleCap = 255;

        // ── Result ──────────────────────────────────────────────────────

        /// <summary>
        /// What the bake actually produced. Every number here is also stated
        /// in the ConversionResult; the struct exists so a caller can branch
        /// on the outcome without re-deriving it from the hierarchy.
        /// </summary>
        public struct BakeResult
        {
            public bool ok;
            public GameObject shatteredRoot;
            public int pieceCount;
            /// Triangles in the intact visual, before the bake.
            public int intactTriangles;
            /// Triangles across every piece, including cut caps.
            public int pieceTriangles;
            /// Non-empty submeshes across every piece — i.e. added draw calls.
            public int pieceDrawCalls;
            public float totalMassKg;
            /// Non-fatal geometry trouble (open mesh, concave cross-sections).
            public string warning;
            public string error;
            /// The creator hit Cancel on the progress bar. Distinct from a
            /// plain failure: nothing is wrong with the asset, and NOTHING was
            /// baked — a partial set of shards covers a fraction of the prop's
            /// volume, so the rest of the prop would simply vanish when it
            /// broke. Never reported as a warning next to the ADDED lines.
            public bool cancelled;
        }

        /// <summary>
        /// What Fracture() ran into, as flags rather than as prose. The prose
        /// is in <see cref="FractureNotes.warning"/> for the report; callers
        /// that need to BRANCH read the flags, because sniffing substrings out
        /// of a human-readable message is how a cancelled bake gets shipped as
        /// a successful one.
        /// </summary>
        public struct FractureNotes
        {
            /// onProgress asked to stop. The returned list is a partial set and
            /// must not be baked.
            public bool cancelled;
            /// A cut-edge loop did not close: the source is not watertight and
            /// some cut faces were left open.
            public bool openMesh;
            /// At least one cut face was a concave polygon. The fan is an
            /// approximation there, and — the part that matters for physics —
            /// the shard is not convex, so its collision hull will bridge the
            /// concavity.
            public bool concaveCaps;
            /// Cells that landed outside the geometry and produced nothing.
            public int emptyCells;
            /// Everything above, joined into one line for the report. Null when
            /// there is nothing to say.
            public string warning;
        }

        // ═════════════════════════════════════════════════════════════════
        //  GEOMETRY — Fracture()
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Split <paramref name="source"/> into <paramref name="cellCount"/>
        /// convex Voronoi cells, in the source mesh's own local space.
        ///
        /// Each returned mesh has source.subMeshCount + 1 submeshes: the
        /// source submeshes carry the original surface, and the LAST one
        /// carries the cut caps, so the caller can append one inside
        /// material to the source material list and be done.
        ///
        /// Cells that come out empty are dropped, so the returned list can be
        /// shorter than <paramref name="cellCount"/> — that is the honest
        /// answer for a model that does not fill its own bounding box.
        ///
        /// <paramref name="warning"/> is null when nothing went wrong. It is
        /// set — and the pieces are still returned — when the source is not
        /// watertight (cut faces omitted) or has concave cross-sections (fan
        /// triangulation is approximate there). It is set and the list comes
        /// back EMPTY when the source could not be read at all.
        /// </summary>
        public static List<Mesh> Fracture(Mesh source, int cellCount, int seed, float capUvScale,
                                          out string warning)
        {
            return Fracture(source, cellCount, seed, capUvScale, null, out warning);
        }

        /// <summary>
        /// As above, with a progress hook. <paramref name="onProgress"/> is
        /// called once per cell with 0..1 and returns true to cancel; a
        /// cancelled bake returns the cells finished so far and sets
        /// <paramref name="warning"/>.
        ///
        /// A caller that can act on a cancel — and every caller that BAKES
        /// should — wants the FractureNotes overload instead: "cancelled" is a
        /// flag there, not a phrase inside the warning text.
        /// </summary>
        public static List<Mesh> Fracture(Mesh source, int cellCount, int seed, float capUvScale,
                                          Func<float, bool> onProgress, out string warning)
        {
            FractureNotes notes;
            List<Mesh> pieces = Fracture(source, cellCount, seed, capUvScale, onProgress, out notes);
            warning = notes.warning;
            return pieces;
        }

        /// <summary>
        /// As above, reporting what happened as flags as well as prose. See
        /// <see cref="FractureNotes"/>.
        /// </summary>
        public static List<Mesh> Fracture(Mesh source, int cellCount, int seed, float capUvScale,
                                          Func<float, bool> onProgress, out FractureNotes notes)
        {
            List<Mesh> pieces = new List<Mesh>();
            List<string> messages = new List<string>();
            notes = new FractureNotes();

            if (source == null)
            {
                notes.warning = "no source mesh to fracture";
                return pieces;
            }
            if (!source.isReadable)
            {
                // Read/Write is OFF by default on imported models, so this is
                // the common case rather than the exotic one. BakeInto flips
                // it on the importer and back again; a direct caller has to.
                notes.warning = "'" + source.name + "' is not readable — enable Read/Write on its "
                              + "import settings before fracturing it";
                return pieces;
            }

            cellCount = Mathf.Clamp(cellCount, MinCells, MaxCells);
            if (capUvScale <= 0.0001f) capUvScale = 1f;

            List<FTri> soup = ReadSoup(source, messages);
            if (soup.Count == 0)
            {
                notes.warning = "'" + source.name + "' has no triangles to fracture";
                return pieces;
            }

            int capSub = Mathf.Max(1, source.subMeshCount);
            Bounds bounds = source.bounds;

            // Every tolerance is relative to the model, not absolute: the same
            // code has to be right on a 5 cm coin and a 6 m statue.
            float diag = Mathf.Max(1e-4f, bounds.size.magnitude);
            float planeEps = diag * 1e-5f;
            float weld = diag * 1e-4f;

            Vector3[] sites = SampleSites(bounds, cellCount, seed);

            List<FTri> cur = new List<FTri>(soup.Count);
            List<FTri> next = new List<FTri>(soup.Count);
            List<Seg> segs = new List<Seg>();
            List<List<Vector3>> loops = new List<List<Vector3>>();

            bool openMesh = false;
            bool concaveCap = false;
            int emptyCells = 0;

            for (int i = 0; i < cellCount; i++)
            {
                if (onProgress != null && onProgress(i / (float)cellCount))
                {
                    // The pieces built so far come back so a caller that wants
                    // them can have them, but `cancelled` says plainly that
                    // they are a fragment of the prop and not a fracture of it.
                    notes.cancelled = true;
                    messages.Add("cancelled after " + pieces.Count + " of " + cellCount + " cell(s)");
                    notes.warning = Join(messages);
                    return pieces;
                }

                cur.Clear();
                cur.AddRange(soup);

                for (int j = 0; j < cellCount && cur.Count > 0; j++)
                {
                    if (i == j) continue;

                    Vector3 delta = sites[j] - sites[i];
                    // Two sites in the same place have no bisector. Skipping
                    // is correct: the cells are identical, and one of them
                    // will lose every triangle to the other's planes anyway.
                    if (delta.sqrMagnitude < 1e-12f) continue;

                    Vector3 n = delta.normalized;
                    Vector3 p = (sites[i] + sites[j]) * 0.5f;

                    next.Clear();
                    segs.Clear();
                    ClipSoup(cur, n, p, planeEps, next, segs);

                    // Caps are emitted as ordinary triangles into `next`, not
                    // held aside: a later plane has to be able to clip them
                    // too, or the cell ends up with a cut face sticking out
                    // past the next cut.
                    if (segs.Count > 0)
                    {
                        loops.Clear();
                        int status = BuildLoops(segs, weld, loops);
                        if (status == LoopsOk)
                        {
                            for (int k = 0; k < loops.Count; k++)
                            {
                                if (EmitCap(loops[k], n, capSub, capUvScale, next)) concaveCap = true;
                            }
                        }
                        else if (status == LoopsOpen)
                        {
                            // The cut-edge chain did not close, which means
                            // the solid was not closed. Emit uncapped rather
                            // than fanning a broken loop into garbage.
                            openMesh = true;
                        }
                        // LoopsDegenerate is silent on purpose: a plane that
                        // merely grazes an edge produces one or two segments
                        // and no area to cap. Reporting that as "your mesh has
                        // holes" would cry wolf on perfectly closed models.
                    }

                    List<FTri> swap = cur;
                    cur = next;
                    next = swap;
                }

                if (cur.Count == 0)
                {
                    emptyCells++;
                    continue;
                }

                Mesh piece = BuildMesh(cur, capSub + 1, source.name + " piece " + pieces.Count);
                if (piece != null) pieces.Add(piece);
            }

            notes.openMesh = openMesh;
            notes.concaveCaps = concaveCap;
            notes.emptyCells = emptyCells;

            if (openMesh)
            {
                messages.Add("'" + source.name + "' is not watertight — the cut faces on some pieces "
                           + "could not be closed and were left open");
            }
            if (concaveCap)
            {
                messages.Add("'" + source.name + "' has concave cross-sections — those cut faces are "
                           + "fan-triangulated and may show shading artifacts, and the shards they "
                           + "belong to are not convex, so their collision hulls bridge the concavity");
            }
            if (emptyCells > 0)
            {
                messages.Add(emptyCells + " of " + cellCount + " cells landed outside the geometry and "
                           + "produced nothing");
            }

            notes.warning = Join(messages);
            return pieces;
        }

        // ── Reading the source ──────────────────────────────────────────

        static List<FTri> ReadSoup(Mesh source, List<string> notes)
        {
            List<FTri> soup = new List<FTri>();
            // GetTriangles(0) throws on a mesh with no submeshes, and a mesh
            // with no submeshes has nothing to fracture anyway.
            if (source.subMeshCount == 0) return soup;

            Vector3[] pos = source.vertices;
            Vector3[] nrm = source.normals;
            Vector2[] uv = source.uv;
            bool hasNormals = nrm != null && nrm.Length == pos.Length;
            bool hasUv = uv != null && uv.Length == pos.Length;

            int subCount = Mathf.Max(1, source.subMeshCount);
            int skippedTopology = 0;

            for (int s = 0; s < subCount; s++)
            {
                if (s < source.subMeshCount && source.GetTopology(s) != MeshTopology.Triangles)
                {
                    // Quads/lines/points cannot be clipped by this code and a
                    // silently dropped submesh is a missing chunk of prop.
                    skippedTopology++;
                    continue;
                }

                int[] idx = source.GetTriangles(s);
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    int i0 = idx[i], i1 = idx[i + 1], i2 = idx[i + 2];
                    if (i0 >= pos.Length || i1 >= pos.Length || i2 >= pos.Length) continue;

                    FVert a = new FVert();
                    FVert b = new FVert();
                    FVert c = new FVert();
                    a.pos = pos[i0]; b.pos = pos[i1]; c.pos = pos[i2];

                    if (hasNormals)
                    {
                        a.nrm = nrm[i0]; b.nrm = nrm[i1]; c.nrm = nrm[i2];
                    }

                    // No authored normals, or a zero normal left behind by a
                    // source that had none: fall back to the face normal.
                    // Unity's winding convention makes cross(b-a, c-a) the
                    // facing direction — the same formula the caps use.
                    if (!hasNormals
                        || a.nrm.sqrMagnitude < 1e-8f
                        || b.nrm.sqrMagnitude < 1e-8f
                        || c.nrm.sqrMagnitude < 1e-8f)
                    {
                        Vector3 fn = Vector3.Cross(b.pos - a.pos, c.pos - a.pos).normalized;
                        if (a.nrm.sqrMagnitude < 1e-8f) a.nrm = fn;
                        if (b.nrm.sqrMagnitude < 1e-8f) b.nrm = fn;
                        if (c.nrm.sqrMagnitude < 1e-8f) c.nrm = fn;
                    }

                    if (hasUv)
                    {
                        a.uv = uv[i0]; b.uv = uv[i1]; c.uv = uv[i2];
                    }

                    FTri t = new FTri();
                    t.a = a; t.b = b; t.c = c; t.sub = s;
                    soup.Add(t);
                }
            }

            if (skippedTopology > 0 && notes != null)
            {
                notes.Add(skippedTopology + " submesh(es) skipped — only triangle topology can be fractured");
            }

            return soup;
        }

        // ── Sites ───────────────────────────────────────────────────────

        /// <summary>
        /// Sites inside the source bounds, from a System.Random seeded with
        /// <paramref name="seed"/>.
        ///
        /// System.Random and not UnityEngine.Random on purpose: the global
        /// Unity RNG is shared editor state, and an editor tool that advances
        /// it makes somebody else's "reproducible" bake stop reproducing.
        /// </summary>
        static Vector3[] SampleSites(Bounds bounds, int count, int seed)
        {
            System.Random rng = new System.Random(seed);
            Vector3[] sites = new Vector3[count];
            Vector3 min = bounds.min;
            Vector3 size = bounds.size;

            for (int i = 0; i < count; i++)
            {
                Vector3 best = Vector3.zero;
                float bestDistance = -1f;
                int candidates = i == 0 ? 1 : SiteCandidates;

                for (int c = 0; c < candidates; c++)
                {
                    Vector3 p = new Vector3(
                        min.x + size.x * (float)rng.NextDouble(),
                        min.y + size.y * (float)rng.NextDouble(),
                        min.z + size.z * (float)rng.NextDouble());

                    if (i == 0) { best = p; break; }

                    float nearest = float.MaxValue;
                    for (int j = 0; j < i; j++)
                    {
                        float d = (p - sites[j]).sqrMagnitude;
                        if (d < nearest) nearest = d;
                    }
                    if (nearest > bestDistance) { bestDistance = nearest; best = p; }
                }

                sites[i] = best;
            }

            return sites;
        }

        // ── Sutherland–Hodgman ──────────────────────────────────────────

        /// <summary>
        /// Clip every triangle in <paramref name="src"/> by the half-space
        /// BELOW the plane (dot(v - point, normal) &lt;= 0, i.e. the side the
        /// cell's own site is on) and fan-retriangulate what survives.
        ///
        /// Every polygon edge that ends up lying ON the plane is recorded in
        /// <paramref name="segs"/>; those are the cut boundary, and they are
        /// what the cap is built from.
        /// </summary>
        static void ClipSoup(List<FTri> src, Vector3 normal, Vector3 point, float eps,
                             List<FTri> dst, List<Seg> segs)
        {
            FVert[] v = new FVert[3];
            float[] d = new float[3];
            List<FVert> poly = new List<FVert>(4);
            List<float> pd = new List<float>(4);

            for (int i = 0; i < src.Count; i++)
            {
                FTri t = src[i];
                v[0] = t.a; v[1] = t.b; v[2] = t.c;

                int outside = 0, inside = 0, on = 0;
                for (int k = 0; k < 3; k++)
                {
                    float dist = Vector3.Dot(v[k].pos - point, normal);
                    // Snapping to exactly zero here is what makes the "is this
                    // edge on the cut plane" test below reliable instead of a
                    // tolerance comparison repeated in three places.
                    if (dist > -eps && dist < eps) dist = 0f;
                    d[k] = dist;
                    if (dist > 0f) outside++;
                    else if (dist < 0f) inside++;
                    else on++;
                }

                // Wholly inside and touching nothing: keep it verbatim. The
                // fast path matters — most triangles take it.
                if (outside == 0 && on == 0) { dst.Add(t); continue; }
                // Nothing kept. A triangle merely grazing the plane from the
                // far side contributes no cut edge, so it just goes away.
                if (inside == 0) continue;

                poly.Clear();
                pd.Clear();
                for (int k = 0; k < 3; k++)
                {
                    int m = (k + 1) % 3;
                    if (d[k] <= 0f) { poly.Add(v[k]); pd.Add(d[k]); }
                    if ((d[k] < 0f && d[m] > 0f) || (d[k] > 0f && d[m] < 0f))
                    {
                        float s = d[k] / (d[k] - d[m]);
                        poly.Add(Lerp(v[k], v[m], s));
                        pd.Add(0f);
                    }
                }
                if (poly.Count < 3) continue;

                for (int k = 1; k + 1 < poly.Count; k++)
                {
                    FTri nt = new FTri();
                    nt.a = poly[0]; nt.b = poly[k]; nt.c = poly[k + 1]; nt.sub = t.sub;
                    dst.Add(nt);
                }

                // A triangle lying entirely in the plane is not a boundary of
                // anything; recording its three edges would inject a phantom
                // loop into the cap.
                if (on == 3) continue;

                for (int k = 0; k < poly.Count; k++)
                {
                    int m = (k + 1) % poly.Count;
                    if (pd[k] == 0f && pd[m] == 0f)
                    {
                        Seg seg = new Seg();
                        seg.a = poly[k].pos;
                        seg.b = poly[m].pos;
                        segs.Add(seg);
                    }
                }
            }
        }

        // ── Cut-edge loops ──────────────────────────────────────────────

        const int LoopsOk = 0;
        /// Fewer edges than a polygon needs — a graze, not a cut. Nothing to cap.
        const int LoopsDegenerate = 1;
        /// A chain ran out of edges without closing. The source has a hole.
        const int LoopsOpen = 2;

        /// <summary>
        /// Chain the recorded cut edges into closed loops.
        ///
        /// Returns LoopsOpen the moment a chain runs out of edges without
        /// returning to its start — which is exactly the signature of a
        /// source mesh with a hole in it, and the only reliable
        /// watertightness test we get for free.
        /// </summary>
        static int BuildLoops(List<Seg> segs, float weld, List<List<Vector3>> loops)
        {
            Welder welder = new Welder(weld);
            List<int> from = new List<int>(segs.Count);
            List<int> to = new List<int>(segs.Count);

            for (int i = 0; i < segs.Count; i++)
            {
                int a = welder.Add(segs[i].a);
                int b = welder.Add(segs[i].b);
                if (a == b) continue;              // collapsed by welding
                from.Add(a);
                to.Add(b);
            }
            if (from.Count < 3) return LoopsDegenerate;

            Dictionary<int, List<int>> outgoing = new Dictionary<int, List<int>>();
            for (int i = 0; i < from.Count; i++)
            {
                List<int> list;
                if (!outgoing.TryGetValue(from[i], out list))
                {
                    list = new List<int>(2);
                    outgoing[from[i]] = list;
                }
                list.Add(i);
            }

            bool[] used = new bool[from.Count];

            for (int start = 0; start < from.Count; start++)
            {
                if (used[start]) continue;

                List<Vector3> loop = new List<Vector3>();
                int first = from[start];
                int cursor = to[start];
                used[start] = true;
                loop.Add(welder.Point(first));

                int guard = 0;
                while (cursor != first)
                {
                    if (++guard > from.Count + 2) return LoopsOpen;

                    loop.Add(welder.Point(cursor));

                    List<int> candidates;
                    if (!outgoing.TryGetValue(cursor, out candidates)) return LoopsOpen;

                    int chosen = -1;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        if (!used[candidates[i]]) { chosen = candidates[i]; break; }
                    }
                    if (chosen < 0) return LoopsOpen;   // open chain → not watertight

                    used[chosen] = true;
                    cursor = to[chosen];
                }

                if (loop.Count >= 3) loops.Add(loop);
            }

            return loops.Count > 0 ? LoopsOk : LoopsDegenerate;
        }

        /// <summary>
        /// Fan-triangulate one closed cut loop into <paramref name="dst"/> as
        /// cap geometry. Returns true if the loop was concave, in which case
        /// the fan is an approximation and the caller should say so.
        ///
        /// THE SIGN OF THE CAP NORMAL. The plane normal is
        /// normalize(otherSite - thisSite) and the cell keeps the half-space
        /// on THIS site's side, so the cut face's outward direction — out of
        /// the piece, towards the neighbouring cell — is exactly that plane
        /// normal. Flipping it inward would leave every cut face
        /// back-to-front: lit from behind, culled from the outside, and only
        /// visible once the prop has already broken.
        /// </summary>
        static bool EmitCap(List<Vector3> loop, Vector3 normal, int capSub, float capUvScale,
                            List<FTri> dst)
        {
            if (loop.Count < 3) return false;

            // Wind the loop so the fan faces along +normal. Deriving this from
            // the accumulated area vector rather than from the edge ordering
            // means it stays correct no matter which way the source mesh was
            // wound.
            Vector3 area = Vector3.zero;
            for (int k = 1; k + 1 < loop.Count; k++)
            {
                area += Vector3.Cross(loop[k] - loop[0], loop[k + 1] - loop[0]);
            }
            if (Vector3.Dot(area, normal) < 0f) loop.Reverse();

            // A stable in-plane basis. The origin is the mesh origin, not the
            // plane's own point, so neighbouring pieces share one continuous
            // interior texture instead of each starting its UVs from scratch.
            Vector3 reference = Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right;
            Vector3 u = Vector3.Normalize(Vector3.Cross(normal, reference));
            Vector3 w = Vector3.Cross(normal, u);
            float inv = 1f / capUvScale;

            bool concave = false;

            for (int k = 1; k + 1 < loop.Count; k++)
            {
                Vector3 p0 = loop[0], p1 = loop[k], p2 = loop[k + 1];
                Vector3 fn = Vector3.Cross(p1 - p0, p2 - p0);
                if (fn.sqrMagnitude <= 1e-16f) continue;              // sliver
                if (Vector3.Dot(fn, normal) < 0f) concave = true;     // fan left the polygon

                FTri t = new FTri();
                t.a = CapVert(p0, normal, u, w, inv);
                t.b = CapVert(p1, normal, u, w, inv);
                t.c = CapVert(p2, normal, u, w, inv);
                t.sub = capSub;
                dst.Add(t);
            }

            return concave;
        }

        static FVert CapVert(Vector3 p, Vector3 normal, Vector3 u, Vector3 w, float invScale)
        {
            FVert v = new FVert();
            v.pos = p;
            v.nrm = normal;
            v.uv = new Vector2(Vector3.Dot(p, u) * invScale, Vector3.Dot(p, w) * invScale);
            return v;
        }

        // ── Mesh assembly ───────────────────────────────────────────────

        /// <summary>
        /// Build one piece mesh. Bounds are recalculated; normals are NOT —
        /// RecalculateNormals would average the authored hard edges of the
        /// outer surface into mush and re-smooth them across the cut faces.
        /// The normals here are the interpolated source normals on the
        /// surface and the plane normal on the caps, which is already right.
        /// </summary>
        static Mesh BuildMesh(List<FTri> tris, int subMeshCount, string name)
        {
            if (tris.Count == 0) return null;

            List<Vector3> positions = new List<Vector3>();
            List<Vector3> normals = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            Dictionary<VKey, int> lookup = new Dictionary<VKey, int>();

            List<int>[] indices = new List<int>[subMeshCount];
            for (int i = 0; i < subMeshCount; i++) indices[i] = new List<int>();

            // Quantised welding: the same cut point reached from two adjacent
            // triangles is computed by two different interpolations and comes
            // out a few ULPs apart, so exact-match welding would leave the
            // seam split. The key carries the normal and the UV as well, so
            // hard edges and UV seams survive.
            float q = 1e-5f;
            for (int i = 0; i < tris.Count; i++)
            {
                FTri t = tris[i];
                if (t.sub < 0 || t.sub >= subMeshCount) continue;
                int ia = AddVertex(t.a, q, lookup, positions, normals, uvs);
                int ib = AddVertex(t.b, q, lookup, positions, normals, uvs);
                int ic = AddVertex(t.c, q, lookup, positions, normals, uvs);
                if (ia == ib || ib == ic || ia == ic) continue;      // degenerate
                indices[t.sub].Add(ia);
                indices[t.sub].Add(ib);
                indices[t.sub].Add(ic);
            }

            int total = 0;
            for (int i = 0; i < subMeshCount; i++) total += indices[i].Count;
            if (total == 0) return null;

            Mesh mesh = new Mesh();
            mesh.name = name;
            if (positions.Count > 65000)
            {
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = subMeshCount;
            for (int i = 0; i < subMeshCount; i++)
            {
                // An empty cap submesh is deliberate: keeping subMeshCount
                // constant across every piece means one shared material array
                // works for all of them.
                mesh.SetTriangles(indices[i], i, false);
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        static int AddVertex(FVert v, float q, Dictionary<VKey, int> lookup,
                             List<Vector3> positions, List<Vector3> normals, List<Vector2> uvs)
        {
            VKey key = VKey.From(v, q);
            int index;
            if (lookup.TryGetValue(key, out index)) return index;

            index = positions.Count;
            positions.Add(v.pos);
            normals.Add(v.nrm);
            uvs.Add(v.uv);
            lookup[key] = index;
            return index;
        }

        // ═════════════════════════════════════════════════════════════════
        //  BAKE — BakeInto()
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Bake the fracture into a prop prefab: an inactive "Shattered" node
        /// under Anchor, one Piece_NN per cell with a MeshRenderer, a convex
        /// MeshCollider and a parked Rigidbody, plus the generated Lua script that
        /// fires them.
        ///
        /// <paramref name="root"/> must be prefab CONTENTS
        /// (PrefabUtility.LoadPrefabContents) — the generated meshes are
        /// written as sub-assets of that prefab, so there has to be a prefab
        /// asset behind it. The caller saves; this does not.
        /// </summary>
        public static BakeResult BakeInto(GameObject root, GameObject visual,
                                          ConversionPlan plan, ConversionResult r)
        {
            return BakeInto(root, visual, plan, r, null);
        }

        /// <summary>
        /// As above, with the destination prefab path supplied explicitly.
        /// Use this overload whenever the caller already knows the path —
        /// resolving it from a preview-scene object is a best-effort ladder.
        ///
        /// This never throws. Everything it touches — a ModelImporter reimport
        /// on a file that may be read-only in Perforce, AddObjectToAsset on an
        /// object a half-failed purge may have left persisted — can throw, and
        /// the caller is mid-way through LoadPrefabContents/SaveAsPrefabAsset
        /// when it does. An escaping exception there skips the save and leaves
        /// the creator with a stack trace and no Failed line in the report.
        /// </summary>
        public static BakeResult BakeInto(GameObject root, GameObject visual,
                                          ConversionPlan plan, ConversionResult r,
                                          string prefabAssetPath)
        {
            try
            {
                return BakeCore(root, visual, plan, r, prefabAssetPath);
            }
            catch (Exception e)
            {
                BakeResult failed = new BakeResult();
                failed.error = "fracture: " + e.Message;

                // The caller saves the prefab whether we succeeded or not, so a
                // throw half-way through the loop would otherwise ship a prop
                // with four shards inside it. Take the partial bake back out;
                // the saved prop is then just the intact one.
                try
                {
                    CleanupPartialBake(root, prefabAssetPath);
                }
                catch (Exception cleanupError)
                {
                    // A cleanup that throws would re-open exactly the hole this
                    // catch exists to close, so it is logged and dropped.
                    Debug.LogWarning("[Convert to DreamPark-Ready] fracture cleanup after the error "
                        + "below also failed: " + cleanupError.Message, root);
                }

                Report(r, DecisionKind.Failed, failed.error);
                Debug.LogException(e, root);
                return failed;
            }
        }

        static BakeResult BakeCore(GameObject root, GameObject visual,
                                   ConversionPlan plan, ConversionResult r,
                                   string prefabAssetPath)
        {
            BakeResult result = new BakeResult();

            if (root == null)
            {
                result.error = "fracture: no prop root";
                Report(r, DecisionKind.Failed, result.error);
                return result;
            }
            if (visual == null)
            {
                result.error = "fracture: no Visual object — nothing to break";
                Report(r, DecisionKind.Failed, result.error);
                return result;
            }
            if (plan == null || plan.fracture == null)
            {
                result.error = "fracture: no FractureSettings in the plan";
                Report(r, DecisionKind.Failed, result.error);
                return result;
            }

            string assetPath = string.IsNullOrEmpty(prefabAssetPath)
                ? ResolvePrefabPath(root)
                : prefabAssetPath;

            if (string.IsNullOrEmpty(assetPath))
            {
                // Without a prefab to hang the meshes off, the only options
                // are loose .asset files (which is the orphan problem this
                // file exists to avoid) or references that serialize to null.
                result.error = "fracture: could not find the prefab asset behind '" + root.name
                             + "' — the piece meshes have to be saved as sub-assets of it. "
                             + "Load it with PrefabUtility.LoadPrefabContents, or pass the path to BakeInto.";
                Report(r, DecisionKind.Failed, result.error);
                return result;
            }

            Transform anchorTransform = PropPrefabEmitter.FindAnchor(root);
            GameObject anchor = anchorTransform != null ? anchorTransform.gameObject : root;

            RegisterUndo(root, "Convert to DreamPark-Ready (fracture)");

            // A re-bake replaces; it never appends. Both halves matter: the
            // node, and the meshes the previous bake left inside the prefab.
            // Both the anchor and the root: an older bake, or a hand-built
            // prop, may have put the node one level up, and leaving it there
            // would ship two sets of shards.
            int purgedNodes = ClearShatteredNode(anchor);
            if (anchor != root) purgedNodes += ClearShatteredNode(root);
            int purgedMeshes = PurgePieceSubAssets(assetPath);
            if (purgedNodes > 0 || purgedMeshes > 0)
            {
                Report(r, DecisionKind.Skipped, "fracture: replaced a previous bake — removed "
                    + purgedNodes + " node(s) and " + purgedMeshes + " orphaned piece mesh(es)");
            }

            List<SourcePart> parts = new List<SourcePart>();
            List<Material> materials = new List<Material>();
            string gatherError;
            if (!GatherSource(visual, anchor.transform, parts, materials, r, out gatherError))
            {
                result.error = gatherError;
                Report(r, DecisionKind.Failed, result.error);
                return result;
            }

            Mesh combined = CombineParts(parts, materials.Count, out result.intactTriangles);
            if (combined == null)
            {
                result.error = "fracture: the geometry under '" + visual.name + "' produced no triangles";
                Report(r, DecisionKind.Failed, result.error);
                return result;
            }

            int cells = Mathf.Clamp(plan.fracture.pieces, MinCells, MaxCells);
            if (plan.fracture.pieces != cells)
            {
                Report(r, DecisionKind.Measured, "fracture: piece count clamped from "
                    + plan.fracture.pieces + " to " + cells + " (legal range " + MinCells + "–" + MaxCells + ")");
            }

            // The seed has to be stable across machines and across re-bakes —
            // a creator who re-runs the converter should get the same shards,
            // not a different-looking prop. string.GetHashCode is not a
            // contract, so the seed comes from an explicit hash of the asset
            // path.
            int seed = StableSeed(assetPath);

            List<Mesh> pieces;
            FractureNotes notes;
            try
            {
                EditorUtility.DisplayProgressBar("Convert to DreamPark-Ready",
                    "Fracturing " + System.IO.Path.GetFileNameWithoutExtension(assetPath) + "…", 0f);

                pieces = Fracture(combined, cells, seed, plan.fracture.capUvScale,
                    delegate (float t)
                    {
                        return EditorUtility.DisplayCancelableProgressBar("Convert to DreamPark-Ready",
                            "Fracturing " + System.IO.Path.GetFileNameWithoutExtension(assetPath)
                            + " — cell " + Mathf.RoundToInt(t * cells) + " of " + cells, t);
                    },
                    out notes);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                DestroyTemp(combined);
            }

            string warning = notes.warning;
            result.warning = warning;

            if (notes.cancelled)
            {
                // A partial fracture is not a smaller fracture. Five cells of a
                // twelve-cell plan cover roughly the volume of five cells, so
                // the prop would mostly VANISH when it broke rather than fall
                // apart — and it would ship looking like a success, because the
                // cancel note reads as an informational line next to the ADDED
                // ones. So: nothing is baked, and this is a Failed line.
                for (int i = 0; i < pieces.Count; i++) DestroyTemp(pieces[i]);
                pieces.Clear();

                result.cancelled = true;
                result.error = "fracture: cancelled — nothing was baked"
                             + (string.IsNullOrEmpty(warning) ? "" : " (" + warning + ")");
                Report(r, DecisionKind.Failed, result.error);
                return result;
            }

            if (pieces.Count == 0)
            {
                result.error = "fracture: produced no pieces"
                             + (string.IsNullOrEmpty(warning) ? "" : " — " + warning);
                Report(r, DecisionKind.Failed, result.error);
                return result;
            }

            if (!string.IsNullOrEmpty(warning))
            {
                // Not fatal, and deliberately not silent: an open cut face is
                // invisible in the editor from the outside and obvious on a
                // headset the first time the prop breaks.
                Report(r, DecisionKind.Skipped, "fracture: " + warning);
            }

            // ── Materials: source list, inside material appended last ──
            Material inside = plan.fracture.insideMaterial;
            if (inside == null)
            {
                // A null material on a submesh renders with Unity's default
                // shader — a bright pink surface that only shows up once the
                // prop breaks. Reusing the outer material is wrong-looking but
                // not alarming, and the report says which happened.
                inside = materials.Count > 0 ? materials[0] : null;
                Report(r, DecisionKind.Skipped, inside != null
                    ? "fracture: no inside material set — the cut faces reuse '" + inside.name
                      + "'. Assign FractureSettings.insideMaterial to make a broken interior read as broken."
                    : "fracture: no inside material set and the source has no material either — the cut "
                      + "faces will render with Unity's default material");
            }
            Material[] pieceMaterials = new Material[materials.Count + 1];
            for (int i = 0; i < materials.Count; i++) pieceMaterials[i] = materials[i];
            pieceMaterials[materials.Count] = inside;

            // ── Build the node ──
            GameObject shattered = new GameObject(ShatteredNodeName);
            shattered.transform.SetParent(anchor.transform, false);
            shattered.transform.localPosition = Vector3.zero;
            shattered.transform.localRotation = Quaternion.identity;
            shattered.transform.localScale = Vector3.one;

            int overCapPieces = 0;
            float totalMass = 0f;
            int pieceTriangles = 0;
            int drawCalls = 0;

            for (int i = 0; i < pieces.Count; i++)
            {
                Mesh mesh = pieces[i];
                string suffix = i.ToString("00");
                mesh.name = PieceMeshPrefix + suffix;

                // The pivot goes to the piece's own centre before anything
                // else touches it: a Rigidbody whose pivot sits at the prop
                // origin tumbles around a point outside itself, which reads as
                // the shard being on an invisible string.
                Vector3 centre = mesh.bounds.center;
                RecentreMesh(mesh, centre);

                // Persist BEFORE the mesh is referenced by a component. A
                // reference to a non-persistent mesh serializes as null when
                // the caller saves the prefab, and the piece comes back
                // invisible with no error anywhere.
                AssetDatabase.AddObjectToAsset(mesh, assetPath);

                GameObject piece = new GameObject(PieceNodePrefix + suffix);
                piece.transform.SetParent(shattered.transform, false);
                piece.transform.localPosition = centre;
                piece.transform.localRotation = Quaternion.identity;
                piece.transform.localScale = Vector3.one;
                piece.layer = anchor.layer;

                MeshFilter filter = Componentizer.DoComponent<MeshFilter>(piece, true);
                filter.sharedMesh = mesh;

                MeshRenderer renderer = Componentizer.DoComponent<MeshRenderer>(piece, true);
                renderer.sharedMaterials = pieceMaterials;

                MeshCollider collider = Componentizer.DoComponent<MeshCollider>(piece, true);
                // Convex is not a preference here. A non-convex MeshCollider
                // on a non-kinematic Rigidbody is silently disabled by Unity,
                // and every piece gets a Rigidbody two lines down.
                collider.sharedMesh = mesh;
                collider.convex = true;
                // DISABLED until the prop breaks — a piece has no collision job
                // before that, and an enabled one is visible to PropTemplate.
                // TryGetColliderFootprint uses GetComponentsInChildren<Collider>
                // (true) and filters on Collider.enabled only, so deactivating
                // the Shattered node does NOT keep these out of the footprint.
                // An enabled collider on an inactive object has no PhysX shape
                // behind it either, so its .bounds reads back zeroed at the
                // world origin and GapFiller cuts the floor from the prop all
                // the way to (0,0,0). The generated script enables them as it fires.
                collider.enabled = false;

                Rigidbody body = Componentizer.DoComponent<Rigidbody>(piece, true);
                float volume = MeshVolume(mesh);
                float mass = Mathf.Clamp(volume * BehaviorPackBuilder.DensityKgPerCubicMetre,
                                         MinPieceMassKg, BehaviorPackBuilder.MaxMassKg);
                body.mass = mass;
                // Parked, not merely inactive. The Shattered node is inactive
                // so nothing simulates yet, but a body that wakes up the
                // instant the node is enabled would drop the shards on the
                // floor before the generated script has aimed them.
                body.isKinematic = true;
                body.useGravity = true;
                totalMass += mass;

                int tris = TriangleCount(mesh);
                pieceTriangles += tris;
                drawCalls += NonEmptySubMeshes(mesh);
                if (tris > ConvexHullTriangleCap) overCapPieces++;
            }

            shattered.SetActive(false);

            result.shatteredRoot = shattered;
            result.pieceCount = pieces.Count;
            result.pieceTriangles = pieceTriangles;
            result.pieceDrawCalls = drawCalls;
            result.totalMassKg = totalMass;

            // ── The script that fires it ──
            //
            // A Lua script in the prop's own kit folder, under the prop's own
            // name, not a C# component. The previous shape ended here with a
            // configured C# component and nothing else: a creator who wanted
            // the prop to score, play a sound or tell the attraction when it
            // broke had nowhere to put that, and the only trigger route was an
            // Interactable that nothing in the SDK adds any more — so a
            // Shatterable convert produced a prop that could not break at all.
            //
            // The generated script owns both halves: it detects the impact
            // (closing speed + optional tag) and it runs the sequence, with
            // every load-bearing rule from the bake written out beside the line
            // that depends on it.
            string luaPath = LuaScriptEmitter.EmitShatter(
                anchor, visual, shattered, root != null ? root.name : anchor.name, plan, r);

            Report(r, DecisionKind.Added, "fracture: " + pieces.Count + " Voronoi piece(s) under "
                + anchor.name + "/" + ShatteredNodeName + " (inactive), each with a parked Rigidbody and "
                + "a convex MeshCollider that is baked DISABLED — an enabled one would join "
                + "PropTemplate's footprint even on an inactive node, and the prop's script turns them "
                + "on as it fires; meshes saved as sub-assets of the prefab");

            // The script lives on the ANCHOR, not on the root. The anchor owns
            // this prop's colliders and never rotates — and, decisively,
            // LuaMessageRelays.Bind adds its collision relay to the
            // LuaBehaviour's OWN GameObject with no ancestor walk. Unity
            // delivers OnCollisionEnter to the collider's GameObject and to the
            // GameObject of that collider's attached Rigidbody, so the anchor
            // (which always has the collider) receives whether or not the plan
            // asked for a body. On the root, a prop converted without a
            // Rigidbody would never hear a single hit.
            if (!string.IsNullOrEmpty(luaPath))
            {
                Report(r, DecisionKind.Added, "shatter: fired by '" + Path.GetFileName(luaPath)
                    + "' on '" + anchor.name + "' — burst "
                    + plan.fracture.burstImpulse.ToString("0.##") + " N·s above "
                    + plan.fracture.minImpactSpeed.ToString("0.##") + " m/s closing speed, "
                    + (plan.fracture.pieceLifetime > 0f
                        ? "pieces despawn after " + plan.fracture.pieceLifetime.ToString("0.#") + " s"
                        : "pieces never despawn (lifetime 0)")
                    + ". Open that file to change what breaking it does.");
            }

            // Absolute numbers, not ratios. "3× the triangles" means nothing
            // standing in a venue; "36 draw calls and 12 rigidbodies" does.
            Report(r, DecisionKind.Measured, "fracture cost: intact " + result.intactTriangles
                + " tris → shattered " + pieceTriangles + " tris, + " + drawCalls
                + " draw calls and " + pieces.Count + " rigidbodies while the pieces are live ("
                + totalMass.ToString("0.##") + " kg total)");

            if (plan.fracture.pieces > FractureSettings.WarnAbovePieces)
            {
                string budget = "fracture budget: " + pieces.Count + " pieces is above the "
                    + FractureSettings.WarnAbovePieces + "-piece guidance — that is " + drawCalls
                    + " extra draw calls and " + pieces.Count
                    + " simultaneous rigidbodies on a Quest the moment this prop breaks. "
                    + "The default is " + new FractureSettings().pieces + ".";
                Report(r, DecisionKind.Measured, budget);
                Debug.LogWarning("[Convert to DreamPark-Ready] " + budget, root);
            }

            if (overCapPieces > 0)
            {
                // Deliberately not "harmless". A Voronoi CELL is convex; a
                // shard is that cell intersected with the source, so a shard
                // off a bowl or a ring is not, and the hull bridges the
                // concavity — the "the player's hand stops in mid-air" failure
                // Stage 4 exists to prevent.
                Report(r, DecisionKind.Measured, "fracture: " + overCapPieces + " piece(s) exceed "
                    + ConvexHullTriangleCap + " triangles, so Unity will simplify their convex hulls. "
                    + "Where the source was already convex that is harmless; where a shard inherited a "
                    + "concavity from the source, the collision surface bridges it and the shard feels "
                    + "larger than it looks."
                    + (notes.concaveCaps
                        ? " This source HAS concave cross-sections (reported above), so expect the second case."
                        : ""));
            }

            if (plan.addRigidbody
                && root.GetComponentInChildren<Rigidbody>(true) == null)
            {
                // Stage 6 owns the intact prop's body, and it puts it on the
                // ANCHOR — nothing the converter emits owns the prop root's
                // transform, because the park loader places that. Searching
                // children rather than the root is what makes this check
                // survive that move; on the root it would fire on every
                // Shatterable convert and say the opposite of the truth.
                Report(r, DecisionKind.Skipped, "fracture: the plan asks for a Rigidbody on the intact "
                    + "prop and there is none — run BehaviorPackBuilder.Apply before the fracture bake");
            }

            result.ok = true;
            return result;
        }

        // ── Source gathering ────────────────────────────────────────────

        /// <summary>
        /// A source mesh's geometry, COPIED OUT rather than referenced.
        ///
        /// This is not premature defensiveness. Restoring the Read/Write flag
        /// reimports the model, and a reimport replaces the Mesh objects the
        /// MeshFilters were pointing at — so anything still holding the old
        /// reference is holding a destroyed object. Reading the arrays while
        /// the mesh is readable and combining afterwards is the only ordering
        /// that survives the restore.
        /// </summary>
        struct SourcePart
        {
            public string name;
            public Vector3[] positions;
            public Vector3[] normals;
            public Vector2[] uvs;
            /// Indices per SOURCE submesh; a non-triangle submesh comes through null.
            public int[][] subTriangles;
            /// Index into the combined material list, per source submesh.
            public int[] subMap;
            public Matrix4x4 toAnchor;
        }

        /// <summary>
        /// Collect every renderable mesh under the Visual, expressed in ANCHOR
        /// space, together with one deduplicated material list.
        ///
        /// Anchor space, not Visual space, because the pieces are parented to
        /// Anchor: the Visual carries the Stage 2 fit scale and the Stage 3a
        /// ground offset, and baking those into the piece vertices is what
        /// makes the shards land exactly where the intact prop was standing.
        /// </summary>
        static bool GatherSource(GameObject visual, Transform anchor, List<SourcePart> parts,
                                 List<Material> materials, ConversionResult r, out string error)
        {
            error = null;

            SkinnedMeshRenderer[] skins = visual.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (skins.Length > 0)
            {
                // A skinned mesh's vertices are in bind pose and are moved by
                // bones every frame; fracturing the bind pose would produce
                // shards of a character standing in a T-pose.
                error = "fracture: '" + visual.name + "' is a skinned mesh — the Shatterable preset "
                      + "only handles static geometry";
                return false;
            }

            MeshFilter[] filters = visual.GetComponentsInChildren<MeshFilter>(true);
            HashSet<Renderer> lodExcluded = CollectLodExcluded(visual);

            List<string> reimported = new List<string>();
            try
            {
                // Read/Write is OFF by default on imported models, so without
                // this the common case is "fracture failed, and here is a wall
                // of text about import settings". Flipped for the bake and put
                // straight back, so the shipped model does not carry a
                // permanent CPU copy it does not need.
                MakeReadable(filters, lodExcluded, reimported);

                // Re-fetch after the reimport: the Mesh objects the filters
                // pointed at before it are not the ones they point at now.
                filters = visual.GetComponentsInChildren<MeshFilter>(true);
                lodExcluded = CollectLodExcluded(visual);

                for (int i = 0; i < filters.Length; i++)
                {
                    MeshFilter mf = filters[i];
                    if (mf == null || mf.sharedMesh == null) continue;

                    MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                    if (mr == null) continue;                       // invisible geometry
                    if (lodExcluded.Contains(mr)) continue;         // LOD1+ would double the shards

                    Mesh mesh = mf.sharedMesh;
                    if (!mesh.isReadable)
                    {
                        Report(r, DecisionKind.Skipped, "fracture: '" + mesh.name
                            + "' could not be made readable and was left out of the fracture");
                        continue;
                    }
                    // GetTriangles(0) throws on a mesh with no submeshes, and
                    // there is nothing in one to break anyway.
                    if (mesh.subMeshCount == 0) continue;

                    Material[] mats = mr.sharedMaterials;
                    int subCount = Mathf.Max(1, mesh.subMeshCount);
                    int[] map = new int[subCount];
                    int[][] triangles = new int[subCount][];
                    int skippedTopology = 0;

                    for (int s = 0; s < subCount; s++)
                    {
                        Material mat = mats != null && mats.Length > 0
                            ? mats[Mathf.Min(s, mats.Length - 1)]
                            : null;
                        int index = materials.IndexOf(mat);
                        if (index < 0) { index = materials.Count; materials.Add(mat); }
                        map[s] = index;

                        if (s < mesh.subMeshCount && mesh.GetTopology(s) != MeshTopology.Triangles)
                        {
                            // Quads, lines and points cannot be clipped by this
                            // code, and a chunk of prop that silently fails to
                            // appear in the shards is worse than a report line.
                            skippedTopology++;
                            continue;
                        }
                        triangles[s] = mesh.GetTriangles(s);
                    }

                    if (skippedTopology > 0)
                    {
                        Report(r, DecisionKind.Skipped, "fracture: " + skippedTopology
                            + " submesh(es) of '" + mesh.name
                            + "' are not triangles and were left out of the fracture");
                    }

                    SourcePart part = new SourcePart();
                    part.name = mesh.name;
                    part.positions = mesh.vertices;
                    part.normals = mesh.normals;
                    part.uvs = mesh.uv;
                    part.subTriangles = triangles;
                    part.subMap = map;
                    part.toAnchor = anchor.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                    parts.Add(part);
                }
            }
            finally
            {
                RestoreReadable(reimported);
            }

            // Reported BEFORE the failure check, not after it. Two full model
            // reimports change import settings, invalidate the library and can
            // take minutes on a large FBX — and the run where the creator most
            // needs to know what the tool touched is the run that then failed.
            if (reimported.Count > 0)
            {
                Report(r, DecisionKind.Measured, "fracture: temporarily enabled Read/Write on "
                    + reimported.Count + " model(s) to read their geometry, then restored the setting");
            }

            if (parts.Count == 0)
            {
                error = "fracture: no readable mesh under '" + visual.name + "'";
                return false;
            }
            if (materials.Count == 0) materials.Add(null);

            return true;
        }

        /// <summary>
        /// One mesh in anchor space, with each source submesh remapped onto
        /// the shared material list. Fracture() works on a single Mesh, so the
        /// combine happens here rather than being smeared through the
        /// clipper.
        /// </summary>
        static Mesh CombineParts(List<SourcePart> parts, int subMeshCount, out int triangleCount)
        {
            triangleCount = 0;

            List<Vector3> positions = new List<Vector3>();
            List<Vector3> normals = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<int>[] indices = new List<int>[subMeshCount];
            for (int i = 0; i < subMeshCount; i++) indices[i] = new List<int>();

            for (int p = 0; p < parts.Count; p++)
            {
                SourcePart part = parts[p];
                Vector3[] pos = part.positions;
                Vector3[] nrm = part.normals;
                Vector2[] uv = part.uvs;
                if (pos == null || pos.Length == 0) continue;
                bool hasNormals = nrm != null && nrm.Length == pos.Length;
                bool hasUv = uv != null && uv.Length == pos.Length;

                // Normals transform by the inverse transpose. With the uniform
                // scale the canonical hierarchy guarantees this is the
                // rotation alone, but a creator who scaled a child by hand
                // would otherwise get normals pointing off the surface.
                Matrix4x4 m = part.toAnchor;
                Matrix4x4 normalMatrix = m.inverse.transpose;

                int baseIndex = positions.Count;
                for (int i = 0; i < pos.Length; i++)
                {
                    positions.Add(m.MultiplyPoint3x4(pos[i]));
                    // Zero, not up, when the source has no normals: ReadSoup
                    // reads a zero normal as "use the face normal", which is
                    // the right answer. A blanket (0,1,0) would light every
                    // shard as if it were the ground.
                    normals.Add(hasNormals
                        ? Vector3.Normalize(normalMatrix.MultiplyVector(nrm[i]))
                        : Vector3.zero);
                    uvs.Add(hasUv ? uv[i] : Vector2.zero);
                }

                bool mirrored = m.determinant < 0f;
                for (int s = 0; s < part.subTriangles.Length; s++)
                {
                    int[] tris = part.subTriangles[s];
                    if (tris == null) continue;

                    int target = part.subMap[Mathf.Min(s, part.subMap.Length - 1)];
                    for (int i = 0; i + 2 < tris.Length; i += 3)
                    {
                        // A negative-determinant transform (a mirrored child)
                        // flips the winding; leaving it alone turns the shard
                        // inside out.
                        indices[target].Add(baseIndex + tris[i]);
                        indices[target].Add(baseIndex + tris[mirrored ? i + 2 : i + 1]);
                        indices[target].Add(baseIndex + tris[mirrored ? i + 1 : i + 2]);
                        triangleCount++;
                    }
                }
            }

            if (positions.Count == 0 || triangleCount == 0) return null;

            Mesh combined = new Mesh();
            combined.name = "ConvertReady_FractureSource";
            // Not saved anywhere — DestroyTemp takes it back down after the
            // bake — but it must not be picked up by a scene save in between.
            combined.hideFlags = HideFlags.HideAndDontSave;
            if (positions.Count > 65000)
            {
                combined.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }
            combined.SetVertices(positions);
            combined.SetNormals(normals);
            combined.SetUVs(0, uvs);
            combined.subMeshCount = subMeshCount;
            for (int i = 0; i < subMeshCount; i++) combined.SetTriangles(indices[i], i, false);
            combined.RecalculateBounds();
            return combined;
        }

        // ── Read/Write juggling ─────────────────────────────────────────

        static void MakeReadable(MeshFilter[] filters, HashSet<Renderer> lodExcluded, List<string> touched)
        {
            HashSet<string> paths = new HashSet<string>();
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                if (mf == null || mf.sharedMesh == null || mf.sharedMesh.isReadable) continue;

                MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || lodExcluded.Contains(mr)) continue;

                string path = AssetDatabase.GetAssetPath(mf.sharedMesh);
                if (!string.IsNullOrEmpty(path)) paths.Add(path);
            }

            foreach (string path in paths)
            {
                ModelImporter importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null || importer.isReadable) continue;
                importer.isReadable = true;
                importer.SaveAndReimport();
                touched.Add(path);
            }
        }

        static void RestoreReadable(List<string> touched)
        {
            for (int i = 0; i < touched.Count; i++)
            {
                ModelImporter importer = AssetImporter.GetAtPath(touched[i]) as ModelImporter;
                if (importer == null || !importer.isReadable) continue;
                importer.isReadable = false;
                importer.SaveAndReimport();
            }
        }

        static HashSet<Renderer> CollectLodExcluded(GameObject visual)
        {
            HashSet<Renderer> excluded = new HashSet<Renderer>();
            LODGroup[] groups = visual.GetComponentsInChildren<LODGroup>(true);
            if (groups.Length == 0) return excluded;

            for (int g = 0; g < groups.Length; g++)
            {
                LOD[] lods = groups[g].GetLODs();
                for (int l = 1; l < lods.Length; l++)
                {
                    Renderer[] rends = lods[l].renderers;
                    if (rends == null) continue;
                    for (int i = 0; i < rends.Length; i++)
                        if (rends[i] != null) excluded.Add(rends[i]);
                }
            }
            // A renderer shared between LOD0 and a lower level must survive.
            for (int g = 0; g < groups.Length; g++)
            {
                LOD[] lods = groups[g].GetLODs();
                if (lods.Length == 0) continue;
                Renderer[] rends = lods[0].renderers;
                if (rends == null) continue;
                for (int i = 0; i < rends.Length; i++)
                    if (rends[i] != null) excluded.Remove(rends[i]);
            }
            return excluded;
        }

        // ── Prefab plumbing ─────────────────────────────────────────────

        /// <summary>
        /// Undo a bake that threw half-way. The caller of BakeInto saves the
        /// prefab whether the bake succeeded or not, so a partial Shattered
        /// node left behind would ship — a prop with four of its twelve shards
        /// inside it, which is worse than a prop with none.
        /// </summary>
        static void CleanupPartialBake(GameObject root, string prefabAssetPath)
        {
            if (root == null) return;

            // The whole hierarchy, not the two levels the normal purge walks: a
            // throw can land anywhere, and this is the last chance to find the
            // node before the caller writes the prefab out.
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            List<GameObject> doomed = new List<GameObject>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null || all[i] == root.transform) continue;
                if (all[i].name == ShatteredNodeName) doomed.Add(all[i].gameObject);
            }
            for (int i = 0; i < doomed.Count; i++) Componentizer.DoDestroy(doomed[i]);

            // And the meshes that made it into the prefab file before the throw.
            // They are unreferenced now and invisible in the inspector, which is
            // the exact orphan this file exists to avoid.
            string path = string.IsNullOrEmpty(prefabAssetPath) ? ResolvePrefabPath(root) : prefabAssetPath;
            if (!string.IsNullOrEmpty(path)) PurgePieceSubAssets(path);
        }

        /// <summary>
        /// The prefab asset behind a set of prefab contents. LoadPrefabContents
        /// puts the hierarchy in a preview scene whose path is the prefab's,
        /// which is the only handle we get back from the returned root.
        /// </summary>
        static string ResolvePrefabPath(GameObject root)
        {
            string direct = AssetDatabase.GetAssetPath(root);
            if (!string.IsNullOrEmpty(direct) && direct.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                return direct;

            PrefabStage stage = PrefabStageUtility.GetPrefabStage(root);
            if (stage != null && !string.IsNullOrEmpty(stage.assetPath))
                return stage.assetPath;

            string scenePath = root.scene.path;
            if (!string.IsNullOrEmpty(scenePath)
                && scenePath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                && AssetDatabase.LoadMainAssetAtPath(scenePath) != null)
                return scenePath;

            return null;
        }

        static int ClearShatteredNode(GameObject anchor)
        {
            int removed = 0;
            Transform t = anchor.transform;
            for (int i = t.childCount - 1; i >= 0; i--)
            {
                Transform child = t.GetChild(i);
                if (child.name != ShatteredNodeName) continue;
                Componentizer.DoDestroy(child.gameObject);
                removed++;
            }
            return removed;
        }

        /// <summary>
        /// Drop the piece meshes a previous bake wrote into this prefab.
        /// Without this a re-bake leaves the old shards in the file forever:
        /// unreferenced, invisible in the inspector, and still in the bundle.
        /// </summary>
        static int PurgePieceSubAssets(string assetPath)
        {
            UnityEngine.Object[] subs = AssetDatabase.LoadAllAssetRepresentationsAtPath(assetPath);
            if (subs == null) return 0;

            int removed = 0;
            for (int i = 0; i < subs.Length; i++)
            {
                Mesh mesh = subs[i] as Mesh;
                if (mesh == null) continue;
                if (!mesh.name.StartsWith(PieceMeshPrefix, StringComparison.Ordinal)) continue;

                AssetDatabase.RemoveObjectFromAsset(mesh);
                UnityEngine.Object.DestroyImmediate(mesh, true);
                removed++;
            }
            return removed;
        }

        // ── Small helpers ───────────────────────────────────────────────

        static void RecentreMesh(Mesh mesh, Vector3 centre)
        {
            if (centre == Vector3.zero) return;
            Vector3[] verts = mesh.vertices;
            for (int i = 0; i < verts.Length; i++) verts[i] -= centre;
            mesh.vertices = verts;
            mesh.RecalculateBounds();
        }

        /// <summary>Signed volume by the divergence theorem, absolute.</summary>
        static float MeshVolume(Mesh mesh)
        {
            Vector3[] verts = mesh.vertices;
            double volume = 0.0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                int[] tris = mesh.GetTriangles(s);
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    Vector3 a = verts[tris[i]], b = verts[tris[i + 1]], c = verts[tris[i + 2]];
                    volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
                }
            }
            return Mathf.Abs((float)volume);
        }

        static int TriangleCount(Mesh mesh)
        {
            int n = 0;
            for (int s = 0; s < mesh.subMeshCount; s++) n += (int)(mesh.GetIndexCount(s) / 3);
            return n;
        }

        static int NonEmptySubMeshes(Mesh mesh)
        {
            int n = 0;
            for (int s = 0; s < mesh.subMeshCount; s++) if (mesh.GetIndexCount(s) > 0) n++;
            return n;
        }

        static void DestroyTemp(UnityEngine.Object o)
        {
            if (o != null) UnityEngine.Object.DestroyImmediate(o, true);
        }

        static FVert Lerp(FVert a, FVert b, float t)
        {
            FVert v = new FVert();
            v.pos = Vector3.Lerp(a.pos, b.pos, t);
            // Normalized rather than raw-lerped: an un-normalized normal on a
            // cut edge shows up as a dark band along every fracture line.
            v.nrm = Vector3.Normalize(Vector3.Lerp(a.nrm, b.nrm, t));
            v.uv = Vector2.Lerp(a.uv, b.uv, t);
            return v;
        }

        /// <summary>
        /// FNV-1a. string.GetHashCode is explicitly not stable across runtimes
        /// or Unity versions, and a fracture that reshuffles itself between
        /// two machines is not reproducible in any sense a creator cares about.
        /// </summary>
        static int StableSeed(string s)
        {
            unchecked
            {
                uint hash = 2166136261u;
                for (int i = 0; i < s.Length; i++)
                {
                    hash ^= s[i];
                    hash *= 16777619u;
                }
                return (int)(hash & 0x7FFFFFFF);
            }
        }

        static string Join(List<string> notes)
        {
            if (notes == null || notes.Count == 0) return null;
            return string.Join("; ", notes.ToArray());
        }

        // Prefab-contents objects live in a preview scene where Undo is a
        // no-op. Real scene objects need it so the whole conversion collapses
        // into one Cmd-Z.
        static bool IsUndoable(GameObject go)
        {
            return go != null
                && !EditorUtility.IsPersistent(go)
                && go.scene.IsValid()
                && !EditorSceneManager.IsPreviewSceneObject(go);
        }

        static void RegisterUndo(GameObject go, string label)
        {
            if (IsUndoable(go)) Undo.RegisterFullObjectHierarchyUndo(go, label);
        }

        static void Report(ConversionResult r, DecisionKind kind, string message)
        {
            if (r == null) return;
            switch (kind)
            {
                case DecisionKind.Measured:  r.Measured(message);  break;
                case DecisionKind.Guessed:   r.Guessed(message);   break;
                case DecisionKind.Added:     r.Added(message);     break;
                case DecisionKind.Extracted: r.Extracted(message); break;
                case DecisionKind.Skipped:   r.Skipped(message);   break;
                default:                     r.Failed(message);    break;
            }
        }

        // ── Value types ─────────────────────────────────────────────────

        struct FVert
        {
            public Vector3 pos;
            public Vector3 nrm;
            public Vector2 uv;
        }

        struct FTri
        {
            public FVert a, b, c;
            public int sub;
        }

        struct Seg
        {
            public Vector3 a, b;
        }

        struct VKey : IEquatable<VKey>
        {
            int px, py, pz, nx, ny, nz, u, v;

            public static VKey From(FVert vert, float q)
            {
                VKey k = new VKey();
                float inv = 1f / q;
                k.px = Mathf.RoundToInt(vert.pos.x * inv);
                k.py = Mathf.RoundToInt(vert.pos.y * inv);
                k.pz = Mathf.RoundToInt(vert.pos.z * inv);
                k.nx = Mathf.RoundToInt(vert.nrm.x * 1000f);
                k.ny = Mathf.RoundToInt(vert.nrm.y * 1000f);
                k.nz = Mathf.RoundToInt(vert.nrm.z * 1000f);
                k.u = Mathf.RoundToInt(vert.uv.x * 10000f);
                k.v = Mathf.RoundToInt(vert.uv.y * 10000f);
                return k;
            }

            public bool Equals(VKey o)
            {
                return px == o.px && py == o.py && pz == o.pz
                    && nx == o.nx && ny == o.ny && nz == o.nz
                    && u == o.u && v == o.v;
            }

            public override bool Equals(object o) { return o is VKey && Equals((VKey)o); }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = px;
                    h = h * 397 ^ py;
                    h = h * 397 ^ pz;
                    h = h * 397 ^ nx;
                    h = h * 397 ^ ny;
                    h = h * 397 ^ nz;
                    h = h * 397 ^ u;
                    h = h * 397 ^ v;
                    return h;
                }
            }
        }

        /// <summary>
        /// Point welder on a uniform grid. The 27-cell probe is not paranoia:
        /// two copies of the same cut point can land either side of a cell
        /// boundary, and a bucket-exact lookup would then declare the cut-edge
        /// loop open and throw away a perfectly good cap.
        /// </summary>
        sealed class Welder
        {
            readonly Dictionary<Vector3Int, List<int>> cells = new Dictionary<Vector3Int, List<int>>();
            readonly List<Vector3> points = new List<Vector3>();
            readonly float cell;
            readonly float weldSqr;

            public Welder(float weld)
            {
                cell = Mathf.Max(weld, 1e-7f);
                weldSqr = cell * cell;
            }

            public Vector3 Point(int index) { return points[index]; }

            public int Add(Vector3 p)
            {
                Vector3Int home = Cell(p);
                for (int x = -1; x <= 1; x++)
                    for (int y = -1; y <= 1; y++)
                        for (int z = -1; z <= 1; z++)
                        {
                            List<int> bucket;
                            Vector3Int key = new Vector3Int(home.x + x, home.y + y, home.z + z);
                            if (!cells.TryGetValue(key, out bucket)) continue;
                            for (int i = 0; i < bucket.Count; i++)
                            {
                                if ((points[bucket[i]] - p).sqrMagnitude <= weldSqr) return bucket[i];
                            }
                        }

                int index = points.Count;
                points.Add(p);
                List<int> list;
                if (!cells.TryGetValue(home, out list))
                {
                    list = new List<int>(2);
                    cells[home] = list;
                }
                list.Add(index);
                return index;
            }

            Vector3Int Cell(Vector3 p)
            {
                return new Vector3Int(
                    Mathf.FloorToInt(p.x / cell),
                    Mathf.FloorToInt(p.y / cell),
                    Mathf.FloorToInt(p.z / cell));
            }
        }
    }
}
#endif
