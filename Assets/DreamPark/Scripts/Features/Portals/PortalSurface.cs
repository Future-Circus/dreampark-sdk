using System.Collections.Generic;
using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// A surface that shows a <see cref="PortalScene"/> (Oct 2026, golf-portal). Put it on ANY mesh with a
    /// <c>DreamPark/Portal</c> material — a wall window, the floor, both together (an L), a dome, a doorway. The
    /// material samples the viewing eye's portal texture in SCREEN space, so every surface of the same scene shows one
    /// continuous view with no seam where surfaces meet: the surfaces are only a mask saying where the scene shows.
    ///
    /// The surface draws normally in the play space (opaque, writes depth, Meta-occluded like other content), so real
    /// hands and furniture in front of it hide it. Put a DepthMask mesh on it when it lies ON a real surface (a floor
    /// portal), so real-floor depth noise never eats it.
    /// When no portal texture exists for the camera drawing it (Scene view, catalog preview, an app without the portal
    /// renderer) the material shows its fallback colour / texture.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Renderer))]
    public class PortalSurface : MonoBehaviour
    {
        [Tooltip("The scene this surface looks into.")]
        public PortalScene scene;
        [Tooltip("Visible from both sides (off: only from the side its normals face; the renderer skips the portal render when every surface faces away).")]
        public bool twoSided = false;
        [Tooltip("1 = the portal is fully there; 0 = the surface has dissolved away and the real world (passthrough / the phone camera) shows. Dithered per pixel, so it can fade smoothly from Lua (e.g. a floor portal dissolving while the scene glides under the player).")]
        [Range(0f, 1f)] public float fade = 1f;

        public static readonly List<PortalSurface> Active = new List<PortalSurface>();

        internal Renderer rend;
        internal MaterialPropertyBlock mpb;
        // mean area-weighted normal of the mesh (object space) and how planar it is (|sum n·a| / sum a)
        internal Vector3 localNormal = Vector3.forward;
        internal float planarity;
        Mesh _measured;

        void OnEnable()
        {
            rend = GetComponent<Renderer>();
            if (mpb == null) mpb = new MaterialPropertyBlock();
            if (scene == null) scene = GetComponentInParent<PortalScene>();
            if (!Active.Contains(this)) Active.Add(this);
            Measure();
            PortalRenderer.Ensure();
        }

        void OnDisable() { Active.Remove(this); }

        internal void Measure()
        {
            var mf = GetComponent<MeshFilter>();
            Mesh m = mf != null ? mf.sharedMesh : null;
            if (m == null || m == _measured) return;
            _measured = m;
            planarity = 0f;
            if (!m.isReadable) return;
            Vector3[] v = m.vertices;
            int[] t = m.triangles;
            Vector3 sum = Vector3.zero;
            float area = 0f;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                Vector3 n = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                sum += n;
                area += n.magnitude;
            }
            if (area > 1e-9f && sum.sqrMagnitude > 1e-12f)
            {
                localNormal = sum.normalized;
                planarity = sum.magnitude / area;
            }
        }

        /// <summary>Can a camera at world position <paramref name="eye"/> see the front of this surface?</summary>
        internal bool Faces(Vector3 eye)
        {
            if (twoSided || planarity < 0.98f) return true;
            Vector3 n = transform.TransformDirection(localNormal);
            Vector3 c = rend.bounds.center;
            // a planar mesh: is the eye on its front side (a little slack for grazing views)?
            return Vector3.Dot(n, eye - c) > -0.01f * (eye - c).magnitude - 1e-4f || BoundsContain(eye);
        }

        bool BoundsContain(Vector3 p)
        {
            Bounds b = rend.bounds;
            b.Expand(0.02f);
            return b.Contains(p);
        }
    }
}
