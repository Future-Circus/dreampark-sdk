using System.Collections.Generic;
using UnityEngine;

namespace DreamPark.MagicUI
{
    /// <summary>
    /// READABILITY IS THE HARD RULE (Oct 18 2026, Aidan: "pretty essential the UI is always readable regardless of
    /// the objects clipping through it"). The board must be visible from where the players stand, whatever the
    /// attraction around it is (pillars, Cinder's wings, braziers).
    ///
    /// The check uses the attraction's real RENDERER bounds, not its footprint: every visible mesh near the board
    /// is an occluder box (floors, the sky/shell enclosing everyone, particles, text and our own overlay are
    /// skipped). It samples viewpoints (each player's head, plus a ring round the group at eye height) and rays to
    /// points across the board (corners, edges, the text block), and reports the share that arrive unblocked.
    /// <see cref="Best"/> tries candidate spots and heights and keeps the clearest one near the group; when even
    /// that one is partly hidden, the board draws over the geometry (<see cref="MagicLook.SetXray"/>).
    /// Pure over its inputs (offline tests: Tools/playlist-bookends-offline).
    /// </summary>
    public static class MagicSight
    {
        public const float EyeHeight = 1.6f;
        /// Below this share of clear sightlines, the board draws over the geometry.
        public const float XrayBelow = 0.95f;

        /// Occluder boxes: the visible meshes within <paramref name="range"/> of <paramref name="around"/>.
        public static List<Bounds> Occluders(Vector3 around, float range, IList<Vector3> eyes, float floorY)
        {
            var list = new List<Bounds>();
            foreach (Renderer r in Candidates(around, range))
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r.forceRenderingOff) continue;
                Bounds b = r.bounds;
                if (r is SkinnedMeshRenderer smr && !smr.updateWhenOffscreen) b = Grow(smr);                 // posed bounds
                if (b.size.y < 0.15f && b.max.y < floorY + 0.25f) continue;                 // floor, rugs, decals
                if ((b.ClosestPoint(around) - around).magnitude > range) continue;
                if (b.size.magnitude > 40f) continue;                                        // a sky / backdrop
                // The room / level SHELL around everyone (walls, dome) is not an occluder; anything smaller that an
                // eye happens to stand in (a dragon's wing) still is.
                bool enclosesEye = false;
                if (eyes != null) foreach (Vector3 e in eyes) if (b.Contains(e)) { enclosesEye = true; break; }
                if (enclosesEye && b.size.x > 6f && b.size.z > 6f) continue;
                list.Add(b);
            }
            return list;
        }

        // PERF (Nov 11 2026, pf-find): MagicCardView re-reads the occluders EVERY frame while a card shows, and
        // this used to be a scene-wide FindObjectsByType<Renderer> plus four component / parent walks per
        // renderer — ~5 ms a frame at 3,000 renderers (editor), on the Quest main thread for the card's whole
        // life. The renderer set and those type tests change only when content spawns or is re-parented, so
        // they are cached once a second, keeping only renderers within CandidateMargin of the card (nothing
        // crosses 10 m in a second; a card that moves 5 m re-gathers at once). Bounds, enabled and the exact
        // range test are still read on every call, so the boxes are the same as before.
        static readonly List<Renderer> s_candidates = new List<Renderer>();
        static float s_candidatesAt = float.NegativeInfinity;
        static Vector3 s_candidatesAround;
        static float s_candidatesRange;
        const float CandidateRefreshSeconds = 1f, CandidateMargin = 10f, CandidateRecentre = 5f;

        static List<Renderer> Candidates(Vector3 around, float range)
        {
            float now = Time.unscaledTime;
            if (Application.isPlaying && now >= s_candidatesAt && now - s_candidatesAt < CandidateRefreshSeconds
                && range <= s_candidatesRange && (around - s_candidatesAround).sqrMagnitude < CandidateRecentre * CandidateRecentre)
                return s_candidates;
            s_candidatesAt = now; s_candidatesAround = around; s_candidatesRange = range;
            s_candidates.Clear();
            float keep = range + CandidateMargin;
            foreach (Renderer r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (r == null) continue;
                if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer) continue;
                if ((r.bounds.ClosestPoint(around) - around).sqrMagnitude > keep * keep) continue;
                if (r.GetComponent<TMPro.TMP_Text>() != null) continue;
                if (r.GetComponentInParent<MagicUIRoot>(true) != null) continue;
                if (r.GetComponentInParent<RemotePlayer>(true) != null || r.GetComponentInParent<PlayerRig>(true) != null) continue;
                s_candidates.Add(r);
            }
            return s_candidates;
        }

        /// A skinned mesh's bounds in its CURRENT pose (its cached bounds are the bind pose: a dragon's spread wings
        /// would be missed).
        static Bounds Grow(SkinnedMeshRenderer smr)
        {
            var mesh = new Mesh();
            // BakeMesh(…, useScale: true) bakes the transform's scale INTO the vertices, so only position + rotation
            // remain (TransformPoint would scale twice: a 100×-scaled FBX came out hundreds of metres wide, Oct 18).
            try { smr.BakeMesh(mesh, true); Bounds lb = mesh.bounds;
                  Matrix4x4 m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                  Bounds w = new Bounds(m.MultiplyPoint3x4(lb.center), Vector3.zero);
                  for (int i = 0; i < 8; i++) w.Encapsulate(m.MultiplyPoint3x4(lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
                  // Never trust a bake bigger than the renderer's own bounds by a lot (a broken rig): keep the cached one.
                  if (w.size.magnitude > 3f * Mathf.Max(1f, smr.bounds.size.magnitude)) return smr.bounds;
                  return w; }
            catch { return smr.bounds; }
            finally { Object.Destroy(mesh); }
        }

        /// Points across a board of w × h centred at <paramref name="centre"/>, facing <paramref name="eye"/>.
        public static List<Vector3> Targets(Vector3 centre, float w, float h, Vector3 eye)
        {
            Vector3 to = eye - centre; to.y = 0f;
            Vector3 fwd = to.sqrMagnitude > 1e-4f ? to.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            var pts = new List<Vector3>();
            for (int iy = 0; iy < 3; iy++)
                for (int ix = 0; ix < 5; ix++)
                {
                    float x = (ix / 4f - 0.5f) * w * 0.92f, y = (iy / 2f - 0.5f) * h * 0.9f;
                    pts.Add(centre + right * x + Vector3.up * y + fwd * 0.01f);
                }
            return pts;
        }

        /// The share (0..1) of eye → board rays that no occluder box blocks.
        public static float Visibility(List<Bounds> occ, IList<Vector3> eyes, Vector3 centre, float w, float h)
        {
            if (eyes == null || eyes.Count == 0) return 1f;
            int clear = 0, total = 0;
            foreach (Vector3 eye in eyes)
                foreach (Vector3 p in Targets(centre, w, h, eye))
                {
                    total++;
                    Vector3 d = p - eye;
                    float dist = d.magnitude;
                    if (dist < 1e-3f) { clear++; continue; }
                    var ray = new Ray(eye, d / dist);
                    bool hit = false;
                    if (occ != null)
                        foreach (Bounds b in occ)
                            if (b.IntersectRay(ray, out float t) && t < dist - 0.05f) { hit = true; break; }
                    if (!hit) clear++;
                }
            return total == 0 ? 1f : (float)clear / total;
        }

        /// Viewpoints: each player's head (given), plus a ring of 6 round the group centre at eye height.
        public static List<Vector3> Eyes(IList<Vector3> heads, Vector3 groupFloor, float ring = 0.9f)
        {
            var eyes = new List<Vector3>();
            if (heads != null) eyes.AddRange(heads);
            Vector3 c = new Vector3(groupFloor.x, groupFloor.y + EyeHeight, groupFloor.z);
            eyes.Add(c);
            for (int i = 0; i < 6; i++)
            {
                float a = i * Mathf.PI / 3f;
                eyes.Add(c + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * ring);
            }
            return eyes;
        }

        /// The clearest board centre among <paramref name="candidates"/> (each tried at its own height plus the
        /// given lifts); ties go to the nearest the group and the lowest. Returns the centre and its visibility.
        public static (Vector3 centre, float visible) Best(List<Bounds> occ, IList<Vector3> eyes, IList<Vector3> candidates,
            float w, float h, Vector3 group, float[] lifts, float idealDistance = 0f)
        {
            Vector3 best = candidates != null && candidates.Count > 0 ? candidates[0] : group;
            float bestVis = -1f, bestScore = float.NegativeInfinity;
            if (candidates == null) return (best, Visibility(occ, eyes, best, w, h));
            foreach (Vector3 c0 in candidates)
                foreach (float lift in lifts ?? new[] { 0f })
                {
                    Vector3 c = c0 + Vector3.up * lift;
                    float vis = Visibility(occ, eyes, c, w, h);
                    Vector3 flat = c - group; flat.y = 0f;
                    // Clear first; then a comfortable reading distance (or simply near, with no ideal), then low.
                    float dist = idealDistance > 0f ? Mathf.Abs(flat.magnitude - idealDistance) * 4f : flat.magnitude * 2f;
                    float score = vis * 100f - dist - lift * 4f;
                    if (score > bestScore + 1e-4f) { bestScore = score; best = c; bestVis = vis; }
                }
            return (best, bestVis);
        }
    }
}
