using System.Collections.Generic;
using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// A scene seen ONLY through portals (Oct 2026, golf-portal). Everything rendered under this transform is
    /// invisible to every play-space camera (headset eyes, phone AR camera, the phone/Quest diorama, third-person) and
    /// is drawn only by <see cref="PortalRenderer"/> into the portal textures that <see cref="PortalSurface"/>s show,
    /// plus any camera carrying a <see cref="PortalSceneCamera"/>. So nothing in it can be occluded by the real room,
    /// collide visually with the play space, or take over a diorama, at any distance — and a portal into it can render
    /// from anywhere it is visible.
    ///
    /// MAPPING. A play-space point q is seen at <c>X·q</c> in the scene, X = this.localToWorld · source.worldToLocal.
    /// With <see cref="followSource"/> this transform copies <see cref="source"/>'s pose every frame (plus the world
    /// <see cref="offset"/>), so content authored under it in the source's local coordinates lines up 1:1 with the play
    /// space (offset 0) or sits the same way <c>offset</c> metres away (for scenes with colliders, lights or audio that
    /// must not reach the room: keep |offset| ≤ 4 km, beyond that 32-bit floats step by more than 0.5 mm and the scene
    /// and its camera jitter).
    ///
    /// CROSSERS. Play-space objects that must also be seen through the portal (a ball flying out of the room into the
    /// course): their renderers are drawn by the portal always, and by play-space cameras only while their bounds
    /// centre lies inside one of the ACTIVE <see cref="playVolumes"/> (each a transform whose local unit cube, centred
    /// on it and scaled by it, is the box — e.g. the room of the layout in use). No active volume = always drawn.
    ///
    /// IN-ROOM SCENE OBJECTS. A scene renderer whose bounds lie ENTIRELY inside an active play volume (the pin of a green
    /// that lies under the guest's feet, a puff landing in the room) is real 3D for the play-space cameras too, so it
    /// stands in the room instead of showing only where a portal surface is behind it (not while scaled below half the
    /// scene's scale: a world shrinking into a point is not "in the room").
    ///
    /// RULES FOR CONTENT. Do not write <c>Renderer.forceRenderingOff</c> on renderers under a PortalScene or on crossers
    /// (this component owns it); use SetActive / enabled. Renderers are picked up automatically (children changes, a
    /// 3 s rescan, or <see cref="Refresh"/>).
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10000)]
    public class PortalScene : MonoBehaviour
    {
        [Tooltip("The play-space frame the scene is mapped from (default: the parent).")]
        public Transform source;
        [Tooltip("Copy the source's pose every frame (plus offset). Off: this transform stays where it is and X maps source-local to this-local.")]
        public bool followSource = true;
        [Tooltip("World-space offset of the scene from the source (metres). 0 = in place (render gating alone hides it). Keep |offset| <= 4000.")]
        public Vector3 offset = Vector3.zero;
        [Tooltip("Layer every scene renderer is put on; the portal camera culls only this layer. 16 'Portal' by default.")]
        public int layer = 16;

        [Header("Look")]
        [Tooltip("Pixels where the scene draws nothing show the REAL world (passthrough on a headset, the camera feed on a phone) instead of a colour: a scene shrinking away (a teleport iris), not yet streamed in, or switched off never flashes a flat colour. Off: they show `background`.")]
        public bool emptyIsPassthrough = true;
        [Tooltip("Clear colour behind the scene, used only with emptyIsPassthrough off.")]
        public Color background = new Color(0.72f, 0.85f, 0.98f, 1f);
        [Tooltip("Far clip of the portal camera in scene metres (the viewer's own far plane is ignored: a phone's 50 m far plane must not cut a 900 m course).")]
        public float farClip = 2000f;
        [Tooltip("Smallest near clip of the portal camera (metres).")]
        public float minNearClip = 0.02f;
        [Tooltip("Global fog on while the portal renders (unchanged = keep whatever the scene settings say).")]
        public FogMode fog = FogMode.Unchanged;

        [Header("Quality / cost")]
        [Tooltip("Portal texture pixels per eye-buffer pixel at close range (1 = pixel-exact, sharp).")]
        [Range(0.25f, 1.5f)] public float resolutionScale = 1f;
        [Tooltip("Longest side of a portal texture (pixels).")]
        public int maxResolution = 2048;
        [Tooltip("Within this distance (m) from the camera to the nearest surface the portal renders at full resolution; beyond it the resolution falls off as fullResDistance / distance.")]
        public float fullResDistance = 8f;
        [Tooltip("Lowest resolution factor the distance fall-off may reach.")]
        [Range(0.1f, 1f)] public float minDistanceScale = 0.35f;

        [Header("Crossers (play-space objects also drawn through the portal)")]
        public List<Transform> crossers = new List<Transform>();
        [Tooltip("Boxes (each transform's local unit cube): crossers are drawn by play-space cameras only while their bounds centre is inside an ACTIVE one. None active = always.")]
        public List<Transform> playVolumes = new List<Transform>();

        public enum FogMode { Unchanged, Off, On }

        // ── runtime ────────────────────────────────────────────────────────────────────────────────────────────────────
        internal readonly List<Renderer> members = new List<Renderer>();
        internal readonly List<bool> memberInside = new List<bool>();
        readonly List<int> _live = new List<int>();          // members active at the last scan: the only ones tested per frame
        internal readonly List<Renderer> crosserRenderers = new List<Renderer>();
        internal readonly List<bool> crosserInside = new List<bool>();
        readonly List<int> _savedLayers = new List<int>();
        readonly List<Renderer> _tmp = new List<Renderer>();
        readonly HashSet<Renderer> _set = new HashSet<Renderer>();
        readonly HashSet<Renderer> _memberSet = new HashSet<Renderer>();
        float _nextScan;
        bool _dirty = true;
        int _openDepth;                    // >0 while a portal / scene camera renders the scene (members visible)
        internal int lastRenderedFrame = -1;
        internal int emptyFrame = -1;          // the last frame the scene had nothing to draw (its surfaces show the real world)

        public static readonly List<PortalScene> Active = new List<PortalScene>();
        /// <summary>The scene's renderers (drawn only by portals / PortalSceneCameras).</summary>
        public IReadOnlyList<Renderer> Members { get { return members; } }
        /// <summary>The crossers' renderers.</summary>
        public IReadOnlyList<Renderer> CrosserRenderers { get { return crosserRenderers; } }

        /// <summary>Play space → scene.</summary>
        public Matrix4x4 PlayToScene
        {
            get
            {
                Transform s = source != null ? source : transform.parent;
                if (s == null) return transform.localToWorldMatrix;
                return transform.localToWorldMatrix * s.worldToLocalMatrix;
            }
        }

        void Reset() { source = transform.parent; }

        void OnEnable()
        {
            if (!Active.Contains(this)) Active.Add(this);
            _dirty = true;
            Follow();
            Refresh();
            PortalRenderer.Ensure();
        }

        void OnDisable()
        {
            Active.Remove(this);
            // hand everything back to the play space exactly as authored
            foreach (Renderer r in members) if (r != null) r.forceRenderingOff = false;
            foreach (Renderer r in crosserRenderers) if (r != null) r.forceRenderingOff = false;
            members.Clear();
            crosserRenderers.Clear();
            crosserInside.Clear();
        }

        void OnTransformChildrenChanged() { _dirty = true; }

        void LateUpdate()
        {
            Follow();
            if (_dirty || Time.unscaledTime >= _nextScan) Refresh();
        }

        /// <summary>Copy the source pose (+ offset). Called every LateUpdate and again just before portal renders.</summary>
        public void Follow()
        {
            if (!followSource) return;
            Transform s = source != null ? source : transform.parent;
            if (s == null || s == transform) return;
            Vector3 p = s.position + offset;
            Quaternion q = s.rotation;
            if (transform.position != p || transform.rotation != q) transform.SetPositionAndRotation(p, q);
            Vector3 ls = s.lossyScale, ps = transform.parent != null ? transform.parent.lossyScale : Vector3.one;
            Vector3 want = new Vector3(Div(ls.x, ps.x), Div(ls.y, ps.y), Div(ls.z, ps.z));
            if ((transform.localScale - want).sqrMagnitude > 1e-12f) transform.localScale = want;
        }
        static float Div(float a, float b) { return Mathf.Abs(b) < 1e-8f ? a : a / b; }

        /// <summary>Re-collect the scene's renderers and the crossers' renderers (cheap; also runs every second).</summary>
        public void Refresh()
        {
            _dirty = false;
            _nextScan = Time.unscaledTime + 3f;
            _tmp.Clear();
            GetComponentsInChildren(true, _tmp);
            _set.Clear();
            foreach (Renderer r in _tmp) if (r != null) _set.Add(r);
            // members that left the sub-tree go back to normal
            for (int i = 0; i < members.Count; i++)
            {
                Renderer r = members[i];
                if (r != null && !_set.Contains(r)) r.forceRenderingOff = false;
            }
            members.Clear();
            memberInside.Clear();
            _live.Clear();
            _memberSet.Clear();
            foreach (Renderer r in _tmp)
            {
                if (r == null) continue;
                members.Add(r);
                memberInside.Add(false);
                _memberSet.Add(r);
                if (r.gameObject.activeInHierarchy) _live.Add(members.Count - 1);
                if (layer >= 0 && layer < 32 && r.gameObject.layer != layer) r.gameObject.layer = layer;
                if (_openDepth == 0 && !r.forceRenderingOff) r.forceRenderingOff = true;
            }
            _set.Clear();
            foreach (Renderer r in crosserRenderers) if (r != null) _set.Add(r);      // the old crossers
            crosserRenderers.Clear();
            crosserInside.Clear();
            var now = new HashSet<Renderer>();
            foreach (Transform t in crossers)
            {
                if (t == null) continue;
                _tmp.Clear();
                t.GetComponentsInChildren(true, _tmp);
                foreach (Renderer r in _tmp)
                    if (r != null && !_memberSet.Contains(r) && now.Add(r)) { crosserRenderers.Add(r); crosserInside.Add(true); }
            }
            foreach (Renderer r in _set) if (r != null && !now.Contains(r)) r.forceRenderingOff = false;
            UpdateCrossers();
        }

        /// <summary>Play-space visibility of the crossers (inside the play volume or not).</summary>
        internal void UpdateCrossers()
        {
            _vol.Clear();
            foreach (Transform v in playVolumes) if (v != null && v.gameObject.activeInHierarchy) _vol.Add(v.worldToLocalMatrix);
            bool always = _vol.Count == 0;
            // scene objects standing wholly inside the room are drawn in the play space too (none without an active volume)
            for (int k = 0; k < _live.Count; k++)
            {
                int i = _live[k];
                Renderer r = members[i];
                bool inside = false;
                if (!always && r != null && r.enabled && r.gameObject.activeInHierarchy)
                {
                    Bounds b = r.bounds;
                    // (not while the scene is being scaled down around a point — an iris / a shrink effect would put the whole
                    // world "inside the room" for a moment)
                    float rel = Mathf.Abs(r.transform.lossyScale.x) / Mathf.Max(1e-6f, Mathf.Abs(transform.lossyScale.x));
                    if (rel >= 0.5f && b.extents.sqrMagnitude < 400f) inside = InVolume(b.min) && InVolume(b.max);
                }
                memberInside[i] = inside;
                if (_openDepth == 0 && r != null && r.forceRenderingOff == inside) r.forceRenderingOff = !inside;
            }
            for (int i = 0; i < crosserRenderers.Count; i++)
            {
                Renderer r = crosserRenderers[i];
                if (r == null || !r.gameObject.activeInHierarchy) continue;      // (an inactive one draws nothing anyway)
                bool inside = always || InVolume(r.bounds.center);
                crosserInside[i] = inside;
                if (_openDepth == 0) r.forceRenderingOff = !inside;
            }
        }

        readonly List<Matrix4x4> _vol = new List<Matrix4x4>();      // the active play volumes' world→local, this frame
        bool InVolume(Vector3 world)
        {
            for (int i = 0; i < _vol.Count; i++)
            {
                Vector3 l = _vol[i].MultiplyPoint3x4(world);
                if (Mathf.Abs(l.x) <= 0.5f && Mathf.Abs(l.y) <= 0.5f && Mathf.Abs(l.z) <= 0.5f) return true;
            }
            return false;
        }

        /// <summary>Is anything of the scene worth rendering (an active renderer)?</summary>
        internal bool HasContent()
        {
            for (int i = 0; i < members.Count; i++) { Renderer r = members[i]; if (r != null && r.enabled && r.gameObject.activeInHierarchy) return true; }
            return false;
        }

        /// <summary>Members (and crossers) visible to the camera about to render; nests.</summary>
        internal void Open()
        {
            if (_openDepth++ > 0) return;
            _savedLayers.Clear();
            for (int i = 0; i < members.Count; i++) { Renderer r = members[i]; if (r != null) r.forceRenderingOff = false; }
            for (int i = 0; i < crosserRenderers.Count; i++)
            {
                Renderer r = crosserRenderers[i];
                if (r == null) { _savedLayers.Add(0); continue; }
                _savedLayers.Add(r.gameObject.layer);
                r.forceRenderingOff = false;
                if (layer >= 0 && layer < 32) r.gameObject.layer = layer;
            }
        }

        internal void Close()
        {
            if (_openDepth == 0 || --_openDepth > 0) return;
            for (int i = 0; i < members.Count; i++) { Renderer r = members[i]; if (r != null) r.forceRenderingOff = !(i < memberInside.Count && memberInside[i]); }
            for (int i = 0; i < crosserRenderers.Count; i++)
            {
                Renderer r = crosserRenderers[i];
                if (r == null) continue;
                if (i < _savedLayers.Count) r.gameObject.layer = _savedLayers[i];
                r.forceRenderingOff = !(i < crosserInside.Count && crosserInside[i]);
            }
        }
    }
}
