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
    /// OPENING CROSSERS (Nov 2026, sdk-crossers). A crosser registered against ONE opening (<see cref="AddCrosser(Transform, Transform)"/>,
    /// <see cref="openingCrossers"/>, Lua <c>dp.portal_cross</c>) is split at that opening's plane, per pixel: play-space cameras
    /// draw only the part on the room side, the portal only the part on the scene side, mapped through <see cref="PlayToScene"/>
    /// (an <see cref="offset"/> scene too). Half through a door it is one continuous body: no pop, no double. Each crosser clips
    /// against its own opening, so an L (wall + floor) works. Wholly on one side, nothing is clipped: the room draws it as
    /// usual, or only the portal does. Needs the SDK shaders (DreamPark-UniversalShader / -Unlit / DreamPark/Particles), which
    /// read the clip from the renderer's MaterialPropertyBlock (written only while a renderer straddles or is mapped).
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

        [Header("Opening crossers (split at their opening's plane, per pixel)")]
        [Tooltip("Play-space objects passing through ONE opening of this scene: the room's cameras draw the part on the room side of the opening's plane, the portal the part beyond it (mapped through the scene's offset). Add them at runtime with AddCrosser / dp.portal_cross.")]
        public List<OpeningCrosser> openingCrossers = new List<OpeningCrosser>();

        public enum FogMode { Unchanged, Off, On }

        /// <summary>A play-space object and the one opening it passes through.</summary>
        [System.Serializable]
        public struct OpeningCrosser
        {
            [Tooltip("The play-space object (all renderers under it).")]
            public Transform crosser;
            [Tooltip("The opening: a PortalSurface (a planar mesh: its front faces the room), or any transform on the opening's plane whose +Z (blue) points into the room.")]
            public Transform opening;
        }

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

        // opening crossers: one entry per renderer (state kept across rescans, keyed by the renderer)
        internal enum Side : byte { Room, Straddle, Scene }
        internal sealed class XRend
        {
            public Renderer r;
            public int opening;              // index into _xo
            public Side side;
            public bool hidden;              // forceRenderingOff written by us for the play-space cameras
            public bool touched, hadBlock;   // our values are in its property block / it had one of its own before
            public bool boundsSet;           // bounds overridden for the portal render (mapped)
            public int savedLayer;
        }
        sealed class XOpening { public Transform crosser, opening; public PortalSurface surface; public Vector4 plane; public bool ok; }
        readonly List<XOpening> _xo = new List<XOpening>();
        internal readonly List<XRend> openingRenderers = new List<XRend>();
        readonly Dictionary<Renderer, XRend> _xByRend = new Dictionary<Renderer, XRend>();
        readonly HashSet<Renderer> _xSet = new HashSet<Renderer>();
        Matrix4x4 _xMap = Matrix4x4.identity;
        bool _xMapped;
        static MaterialPropertyBlock s_xBlock;
        const float PortalOverlap = 0.01f;     // metres the portal's part reaches back over the room's (hidden behind it)
        // (in their own class: a PortalScene's statics initialise wherever a prefab with one deserialises, maybe off the main
        // thread, where Shader.PropertyToID throws; these are first touched from SetBlock, on the main thread)
        static class XIds
        {
            public static readonly int Cross = Shader.PropertyToID("_DPCross");
            public static readonly int Plane = Shader.PropertyToID("_DPCrossPlane");
            public static readonly int Map = Shader.PropertyToID("_DPCrossMap");
        }

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
            foreach (XRend x in openingRenderers) Release(x);
            openingRenderers.Clear();
            _xByRend.Clear();
            _xo.Clear();
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
            RefreshOpenings();
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
                    if (r != null && !_memberSet.Contains(r) && !_xSet.Contains(r) && now.Add(r)) { crosserRenderers.Add(r); crosserInside.Add(true); }
            }
            foreach (Renderer r in _set) if (r != null && !now.Contains(r) && !_xSet.Contains(r)) r.forceRenderingOff = false;
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
            UpdateOpenings();
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

        /// <summary>Members (and crossers) visible to the camera about to render; nests. <paramref name="portal"/>: a portal render
        /// (opening crossers: only their scene side, mapped); false: a PortalSceneCamera (opening crossers whole, unmapped).</summary>
        internal void Open(bool portal = true)
        {
            if (_openDepth++ > 0) return;
            OpenOpenings(portal);
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
            CloseOpenings();
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
        // OPENING CROSSERS (Nov 2026, sdk-crossers): split at the plane of the one opening each passes through
        // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Draw <paramref name="crosser"/> (a play-space object: all renderers under it) through <paramref name="opening"/> of
        /// this scene, split per pixel at the opening's plane: the room's cameras draw the part on the room side, the portal the
        /// part beyond (mapped through the scene's offset). The opening is a <see cref="PortalSurface"/> (planar: its front faces
        /// the room) or any transform on the opening's plane whose +Z points into the room. Calling it again moves the crosser
        /// to another opening; a crosser is in one opening at a time.
        /// </summary>
        public void AddCrosser(Transform crosser, Transform opening)
        {
            if (crosser == null || opening == null) return;
            for (int i = 0; i < openingCrossers.Count; i++)
            {
                if (openingCrossers[i].crosser != crosser) continue;
                if (openingCrossers[i].opening == opening) return;
                openingCrossers[i] = new OpeningCrosser { crosser = crosser, opening = opening };
                if (isActiveAndEnabled) Refresh();
                return;
            }
            openingCrossers.Add(new OpeningCrosser { crosser = crosser, opening = opening });
            if (isActiveAndEnabled) Refresh();
        }

        /// <summary><see cref="AddCrosser(Transform, Transform)"/> through a portal surface of this scene.</summary>
        public void AddCrosser(Transform crosser, PortalSurface opening)
        {
            if (opening != null) AddCrosser(crosser, opening.transform);
        }

        /// <summary>Stop drawing <paramref name="crosser"/> through this scene (an opening crosser, or one in
        /// <see cref="crossers"/>): it is drawn by the room's cameras as authored again. True when it was one.</summary>
        public bool RemoveCrosser(Transform crosser)
        {
            if (crosser == null) return false;
            bool was = RemoveOpeningCrosser(crosser);
            while (crossers.Remove(crosser)) was = true;
            if (was && isActiveAndEnabled) Refresh();
            return was;
        }

        bool RemoveOpeningCrosser(Transform crosser)
        {
            bool was = false;
            for (int i = openingCrossers.Count - 1; i >= 0; i--)
                if (openingCrossers[i].crosser == crosser) { openingCrossers.RemoveAt(i); was = true; }
            return was;
        }

        /// <summary>Is <paramref name="crosser"/> registered against an opening of this scene?</summary>
        public bool IsCrossing(Transform crosser)
        {
            for (int i = 0; i < openingCrossers.Count; i++) if (openingCrossers[i].crosser == crosser) return true;
            return false;
        }

        /// <summary>
        /// One call for scripts (Lua <c>dp.portal_cross</c>). <paramref name="on"/>: <paramref name="crosser"/> goes through
        /// <paramref name="opening"/> (a PortalSurface's object, an object with one under it, or a transform on the opening's
        /// plane facing the room, then with <paramref name="scene"/>) and leaves any other scene's opening. Off: it leaves every
        /// scene's openings. Returns whether it is now (on) / was (off) an opening crosser.
        /// </summary>
        public static bool Cross(GameObject crosser, GameObject opening, bool on, PortalScene scene = null)
        {
            if (crosser == null) return false;
            Transform t = crosser.transform;
            if (!on)
            {
                bool was = false;
                foreach (PortalScene s in Active) if (s != null && s.RemoveOpeningCrosser(t)) { was = true; s.Refresh(); }
                return was;
            }
            if (opening == null) return false;
            PortalSurface surf = opening.GetComponent<PortalSurface>();
            if (surf == null) surf = opening.GetComponentInChildren<PortalSurface>(true);
            if (scene == null && surf != null) scene = surf.scene != null ? surf.scene : surf.GetComponentInParent<PortalScene>();
            if (scene == null) scene = opening.GetComponentInParent<PortalScene>();
            if (scene == null && Active.Count == 1) scene = Active[0];
            if (scene == null) return false;
            foreach (PortalScene s in Active) if (s != null && s != scene && s.RemoveOpeningCrosser(t)) s.Refresh();
            scene.AddCrosser(t, surf != null ? surf.transform : opening.transform);
            return true;
        }

        void RefreshOpenings()
        {
            _xo.Clear();
            _xSet.Clear();
            var seen = new HashSet<Renderer>();
            for (int i = openingCrossers.Count - 1; i >= 0; i--)
                if (openingCrossers[i].crosser == null) openingCrossers.RemoveAt(i);
            var keep = new List<XRend>(openingRenderers.Count);
            foreach (OpeningCrosser oc in openingCrossers)
            {
                if (oc.crosser == null || oc.crosser.IsChildOf(transform)) continue;    // (scene content is a member, never a crosser)
                var xo = new XOpening { crosser = oc.crosser, opening = oc.opening, surface = oc.opening != null ? oc.opening.GetComponent<PortalSurface>() : null };
                _xo.Add(xo);
                _tmp.Clear();
                oc.crosser.GetComponentsInChildren(true, _tmp);
                foreach (Renderer r in _tmp)
                {
                    if (r == null || _memberSet.Contains(r) || !seen.Add(r)) continue;
                    if (!_xByRend.TryGetValue(r, out XRend x)) { x = new XRend { r = r }; _xByRend[r] = x; }
                    x.opening = _xo.Count - 1;
                    keep.Add(x);
                    _xSet.Add(r);
                }
            }
            // renderers no longer crossing go back to the room exactly as authored
            foreach (XRend x in openingRenderers) if (!seen.Contains(x.r)) { Release(x); if (x.r != null) _xByRend.Remove(x.r); }
            openingRenderers.Clear();
            openingRenderers.AddRange(keep);
        }

        // the opening's plane in world space, positive on the room side
        bool OpeningPlane(XOpening o, out Vector4 plane)
        {
            plane = default;
            Transform t = o.opening;
            if (t == null || o.crosser == null) return false;
            Vector3 n, c;
            PortalSurface s = o.surface;
            Renderer sr = s != null ? s.GetComponent<Renderer>() : null;
            if (s != null && s.rend != null && s.planarity >= 0.98f)
            {
                n = t.TransformDirection(s.localNormal);
                c = s.rend.bounds.center;
            }
            else if (s != null && sr != null && sr is MeshRenderer && t.GetComponent<MeshFilter>() is MeshFilter mf && mf.sharedMesh != null)
            {
                // a mesh we can't measure (not readable): its thinnest local axis, turned toward the room's viewer
                Bounds lb = mf.sharedMesh.bounds;
                Vector3 e = lb.extents;
                Vector3 ax = e.x <= e.y && e.x <= e.z ? Vector3.right : (e.y <= e.z ? Vector3.up : Vector3.forward);
                n = t.TransformDirection(ax);
                c = sr.bounds.center;
                Vector3 toward = RoomPoint(c - t.forward);          // (no volume, no camera: a Unity quad's front, -Z)
                if (Vector3.Dot(n, toward - c) < 0f) n = -n;
            }
            else
            {
                n = t.forward;
                c = t.position;
            }
            if (n.sqrMagnitude < 1e-12f) return false;
            n.Normalize();
            plane = new Vector4(n.x, n.y, n.z, -Vector3.Dot(n, c));
            return true;
        }

        // somewhere on the room side: the active play volume's centre, else the main camera, else the fallback
        Vector3 RoomPoint(Vector3 fallback)
        {
            foreach (Transform v in playVolumes) if (v != null && v.gameObject.activeInHierarchy) return v.position;
            Camera m = Camera.main;
            return m != null ? m.transform.position : fallback;
        }

        static bool IsIdentity(Matrix4x4 m)
        {
            for (int i = 0; i < 16; i++) if (Mathf.Abs(m[i] - Matrix4x4.identity[i]) > 1e-5f) return false;
            return true;
        }

        static Bounds MapBounds(Matrix4x4 m, Bounds b)
        {
            Vector3 c = b.center, e = b.extents;
            Vector3 mn = Vector3.positiveInfinity, mx = Vector3.negativeInfinity;
            for (int i = -1; i <= 1; i += 2) for (int j = -1; j <= 1; j += 2) for (int k = -1; k <= 1; k += 2)
            {
                Vector3 p = m.MultiplyPoint3x4(c + new Vector3(e.x * i, e.y * j, e.z * k));
                mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
            }
            var r = new Bounds(); r.SetMinMax(mn, mx);
            return r;
        }

        // where each opening crosser's renderer is (room / straddling / beyond), and its play-space look
        void UpdateOpenings()
        {
            if (openingRenderers.Count == 0) return;
            _xMap = PlayToScene;
            _xMapped = !IsIdentity(_xMap);
            foreach (XOpening o in _xo) o.ok = OpeningPlane(o, out o.plane);
            for (int i = 0; i < openingRenderers.Count; i++)
            {
                XRend x = openingRenderers[i];
                if (x.r == null || !x.r.gameObject.activeInHierarchy) continue;
                XOpening o = _xo[x.opening];
                if (!o.ok) x.side = Side.Room;
                else
                {
                    Bounds b = x.r.bounds;
                    // slack for bounds that lag the pose (a skinned mesh's), so a limb never pokes through unclipped
                    b.Expand(0.04f + 0.1f * b.size.magnitude);
                    Vector3 n = o.plane, c = b.center, e = b.extents;
                    float d = Vector3.Dot(n, c) + o.plane.w;
                    float r = Mathf.Abs(n.x) * e.x + Mathf.Abs(n.y) * e.y + Mathf.Abs(n.z) * e.z;
                    x.side = d - r >= 0f ? Side.Room : (d + r <= 0f ? Side.Scene : Side.Straddle);
                }
                if (_openDepth == 0) ApplyRoom(x);
            }
        }

        // the play-space cameras' view of one crosser renderer
        void ApplyRoom(XRend x)
        {
            bool hide = x.side == Side.Scene;
            if (x.hidden != hide || x.r.forceRenderingOff != hide) { x.r.forceRenderingOff = hide; x.hidden = hide; }
            if (x.side == Side.Straddle) SetBlock(x, new Vector4(1f, 0f, 0f, 0f), _xo[x.opening].plane, Matrix4x4.identity);
            else ClearBlock(x);
        }

        void OpenOpenings(bool portal)
        {
            Matrix4x4 inv = _xMapped ? _xMap.inverse.transpose : Matrix4x4.identity;
            for (int i = 0; i < openingRenderers.Count; i++)
            {
                XRend x = openingRenderers[i];
                if (x.r == null) continue;
                x.savedLayer = x.r.gameObject.layer;
                if (!portal)
                {
                    // a PortalSceneCamera sees the scene AND the play space unmapped: the whole crosser where it is
                    ClearBlock(x);
                    x.r.forceRenderingOff = false;
                    if (layer >= 0 && layer < 32) x.r.gameObject.layer = layer;
                    continue;
                }
                if (x.side == Side.Room) { x.r.forceRenderingOff = true; continue; }   // wholly in the room: not in the portal
                x.r.forceRenderingOff = false;
                if (layer >= 0 && layer < 32) x.r.gameObject.layer = layer;
                if (x.side == Side.Straddle)
                {
                    // the scene side, in the render's space, overlapping the room side by PortalOverlap: a face lying in the
                    // opening's plane (a box top level with a floor opening) z-fights the surface in the room render, and the
                    // portal image under it must hold that face, not the crosser's inside
                    Vector4 beyond = -_xo[x.opening].plane;
                    beyond.w += PortalOverlap;
                    beyond = inv * beyond;
                    SetBlock(x, new Vector4(1f, _xMapped ? 1f : 0f, 0f, 0f), beyond, _xMap);
                }
                else if (_xMapped) SetBlock(x, new Vector4(0f, 1f, 0f, 0f), Vector4.zero, _xMap);
                else ClearBlock(x);
                if (_xMapped)
                {
                    // culling sees the mapped place (the shader moves the vertices there)
                    x.r.bounds = MapBounds(_xMap, x.r.bounds);
                    x.boundsSet = true;
                }
            }
        }

        void CloseOpenings()
        {
            for (int i = 0; i < openingRenderers.Count; i++)
            {
                XRend x = openingRenderers[i];
                if (x.r == null) continue;
                if (x.boundsSet) { x.r.ResetBounds(); x.boundsSet = false; }
                if (x.r.gameObject.layer != x.savedLayer) x.r.gameObject.layer = x.savedLayer;
                ApplyRoom(x);
            }
        }

        static void SetBlock(XRend x, Vector4 cross, Vector4 plane, Matrix4x4 map)
        {
            if (s_xBlock == null) s_xBlock = new MaterialPropertyBlock();
            if (!x.touched) { x.hadBlock = x.r.HasPropertyBlock(); x.touched = true; }
            x.r.GetPropertyBlock(s_xBlock);
            s_xBlock.SetVector(XIds.Cross, cross);
            s_xBlock.SetVector(XIds.Plane, plane);
            s_xBlock.SetMatrix(XIds.Map, map);
            x.r.SetPropertyBlock(s_xBlock);
        }

        static void ClearBlock(XRend x)
        {
            if (!x.touched) return;
            x.touched = false;
            if (!x.hadBlock) { x.r.SetPropertyBlock(null); return; }
            if (s_xBlock == null) s_xBlock = new MaterialPropertyBlock();
            x.r.GetPropertyBlock(s_xBlock);
            s_xBlock.SetVector(XIds.Cross, Vector4.zero);
            x.r.SetPropertyBlock(s_xBlock);
        }

        // back to the play space exactly as authored
        static void Release(XRend x)
        {
            if (x == null || x.r == null) return;
            if (x.boundsSet) { x.r.ResetBounds(); x.boundsSet = false; }
            ClearBlock(x);
            if (x.hidden || x.r.forceRenderingOff) x.r.forceRenderingOff = false;
            x.hidden = false;
        }
    }
}
