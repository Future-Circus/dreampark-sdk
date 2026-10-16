using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

namespace DreamPark
{
    /// <summary>
    /// Renders every visible portal (Oct 2026, golf-portal). PORTAL = A MATERIAL ON ANY SURFACES, SCREEN-SPACE:
    ///
    /// Once per frame, after tracking has updated (Application.onBeforeRender), for every enabled game camera (each XR
    /// eye, the phone AR camera, the phone diorama overlay camera, the Editor game camera, the third-person camera…)
    /// and every <see cref="PortalScene"/> with a <see cref="PortalSurface"/> that camera can see, ONE hidden camera
    /// renders the scene from that eye's exact pose and projection mapped through the scene's play→scene transform,
    /// restricted to the screen rectangle the visible surfaces cover, into a pooled texture whose pixels match the eye
    /// buffer's (capped, and lowered with distance). Every surface of that scene then samples the eye's texture at its own
    /// screen position, so the wall, the floor and the fold between them are one continuous view.
    ///
    /// COST CONTROL: nothing renders for a camera that sees no surface (frustum + facing); one render per (camera, eye,
    /// scene) however many surfaces share it; the hidden camera culls only the scene's layer with no shadows, no
    /// post-processing, no depth/opaque textures and no render scale; textures are pooled by size and freed after
    /// ~2 s unused; resolution falls off with distance (<see cref="PortalScene.fullResDistance"/>).
    /// Meta occlusion keywords are switched off around each render (the scene is not in the room; the SURFACE is
    /// what real hands occlude).
    /// </summary>
    [DefaultExecutionOrder(10001)]
    public sealed class PortalRenderer : MonoBehaviour
    {
        /// <summary>Kill switch: false = no portal renders (surfaces show their fallback).</summary>
        public static bool Enabled = true;
        /// <summary>Editor verification aid: ≥0 forces the material to sample that eye's texture (mock stereo).</summary>
        public static int DebugForceEye = -1;

        public struct FrameStats
        {
            public int frame, renders, cameras, culledHidden, texturesLive, texturesPooled;
            public long pixels, textureBytes;
            public double cpuMs;
        }
        /// <summary>The last frame's work (renders, pixels, CPU time on the main thread).</summary>
        public static FrameStats Last;

        static PortalRenderer s_inst;
        Camera _cam;
        UniversalAdditionalCameraData _camData;

        class Eye { public RenderTexture rt; public Vector4 rect; public bool ok; }
        class View { public Camera viewer; public PortalScene scene; public readonly Eye[] eyes = { new Eye(), new Eye() }; public int frame = -1; public bool stereo; }
        readonly Dictionary<long, View> _views = new Dictionary<long, View>();
        readonly List<long> _drop = new List<long>();
        class Pooled { public RenderTexture rt; public int lastFrame; }
        readonly List<Pooled> _free = new List<Pooled>();
        readonly HashSet<RenderTexture> _ours = new HashSet<RenderTexture>();
        Camera[] _cams = new Camera[16];
        readonly List<PortalSurface> _surf = new List<PortalSurface>();
        readonly List<PortalScene> _opened = new List<PortalScene>();
        readonly Vector3[] _corners = new Vector3[8];
        readonly Plane[] _planes = new Plane[6];
        System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();

        static readonly int IdTexL = Shader.PropertyToID("_PortalTexL");
        static readonly int IdTexR = Shader.PropertyToID("_PortalTexR");
        static readonly int IdRectL = Shader.PropertyToID("_PortalRectL");
        static readonly int IdRectR = Shader.PropertyToID("_PortalRectR");
        static readonly int IdOn = Shader.PropertyToID("_PortalOn");
        static readonly int IdEye = Shader.PropertyToID("_PortalEyeOverride");
        static readonly int IdEmptyClip = Shader.PropertyToID("_PortalEmptyClip");
        static readonly int IdHide = Shader.PropertyToID("_PortalHide");
        static readonly int IdFade = Shader.PropertyToID("_PortalFade");
        static readonly string[] OcclusionKeywords = { "HARD_OCCLUSION", "SOFT_OCCLUSION" };
        const float EyeHalfIpd = 0.032f;

        public static void Ensure()
        {
            if (s_inst != null || !Application.isPlaying) return;
            var go = new GameObject("DreamPark.PortalRenderer");
            go.hideFlags = HideFlags.DontSave;
            DontDestroyOnLoad(go);
            s_inst = go.AddComponent<PortalRenderer>();
        }

        void Awake()
        {
            var cgo = new GameObject("PortalCamera");
            cgo.hideFlags = HideFlags.DontSave;
            cgo.transform.SetParent(transform, false);
            _cam = cgo.AddComponent<Camera>();
            _cam.enabled = false;
            _cam.cameraType = CameraType.Reflection;      // URP: no render scale, no scene-view/game-only extras
            _cam.stereoTargetEye = StereoTargetEyeMask.None;
            _cam.allowHDR = false;
            _cam.useOcclusionCulling = false;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.depthTextureMode = DepthTextureMode.None;
            _camData = cgo.AddComponent<UniversalAdditionalCameraData>();
            _camData.renderType = CameraRenderType.Base;
            _camData.renderShadows = false;
            _camData.renderPostProcessing = false;
            _camData.antialiasing = AntialiasingMode.None;
            _camData.requiresDepthOption = CameraOverrideOption.Off;
            _camData.requiresColorOption = CameraOverrideOption.Off;
            _camData.allowXRRendering = false;
            _camData.stopNaN = false;
            _camData.dithering = false;
        }

        void OnEnable()
        {
            Application.onBeforeRender += OnBeforeRender;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            RenderPipelineManager.endCameraRendering += OnEndCamera;
        }

        void OnDisable()
        {
            Application.onBeforeRender -= OnBeforeRender;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            RenderPipelineManager.endCameraRendering -= OnEndCamera;
        }

        void OnDestroy()
        {
            foreach (var v in _views.Values) foreach (var e in v.eyes) Free(e);
            _views.Clear();
            foreach (var p in _free) Kill(p.rt);
            _free.Clear();
            if (s_inst == this) s_inst = null;
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
        // once per frame, after tracking: render every visible portal for every camera
        // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
        [BeforeRenderOrder(2000)]
        void OnBeforeRender()
        {
            _sw.Restart();
            int frame = Time.frameCount;
            var st = new FrameStats { frame = frame };
            if (Enabled && PortalSurface.Active.Count > 0 && PortalScene.Active.Count > 0)
            {
                foreach (PortalScene sc in PortalScene.Active) { if (sc != null) { sc.Follow(); sc.UpdateCrossers(); } }
                int n = Camera.allCamerasCount;
                if (_cams.Length < n) _cams = new Camera[n + 8];
                n = Camera.GetAllCameras(_cams);
                for (int ci = 0; ci < n; ci++)
                {
                    Camera viewer = _cams[ci];
                    if (!IsViewer(viewer)) continue;
                    st.cameras++;
                    foreach (PortalScene sc in PortalScene.Active)
                    {
                        if (sc == null || !sc.isActiveAndEnabled) continue;
                        if (!sc.HasContent()) { sc.emptyFrame = frame; continue; }
                        Collect(sc, viewer);
                        if (_surf.Count == 0) continue;
                        RenderView(sc, viewer, frame, ref st);
                    }
                }
            }
            // views not rendered this frame give their textures back; old pooled textures are freed
            _drop.Clear();
            foreach (var kv in _views)
            {
                View v = kv.Value;
                if (v.frame >= frame - 1 && v.viewer != null && v.scene != null) continue;
                foreach (var e in v.eyes) { Free(e); e.ok = false; }
                if (v.viewer == null || v.scene == null || frame - v.frame > 120) _drop.Add(kv.Key);
            }
            foreach (long k in _drop) _views.Remove(k);
            for (int i = _free.Count - 1; i >= 0; i--)
                if (frame - _free[i].lastFrame > 120) { Kill(_free[i].rt); _free.RemoveAt(i); }
            foreach (var v in _views.Values) foreach (var e in v.eyes) if (e.rt != null) { st.texturesLive++; st.textureBytes += Bytes(e.rt); }
            foreach (var p in _free) { st.texturesPooled++; st.textureBytes += Bytes(p.rt); }
            _sw.Stop();
            st.cpuMs = _sw.Elapsed.TotalMilliseconds;
            Last = st;
        }

        bool IsViewer(Camera c)
        {
            if (c == null || c == _cam || !c.enabled || !c.gameObject.activeInHierarchy) return false;
            if (c.cameraType != CameraType.Game) return false;
            if (c.targetTexture != null && _ours.Contains(c.targetTexture)) return false;
            return true;
        }

        void Collect(PortalScene sc, Camera viewer)
        {
            _surf.Clear();
            foreach (PortalSurface s in PortalSurface.Active)
            {
                if (s == null || s.scene != sc || s.rend == null || !s.rend.enabled || !s.isActiveAndEnabled) continue;
                if ((viewer.cullingMask & (1 << s.gameObject.layer)) == 0) continue;
                _surf.Add(s);
            }
        }

        void RenderView(PortalScene sc, Camera viewer, int frame, ref FrameStats st)
        {
            long key = ((long)viewer.GetInstanceID() << 32) ^ (uint)sc.GetInstanceID();
            if (!_views.TryGetValue(key, out View v)) { v = new View { viewer = viewer, scene = sc }; _views[key] = v; }
            v.frame = frame;
            v.stereo = viewer.stereoEnabled;
            int eyes = v.stereo ? 2 : 1;
            Matrix4x4 sceneFromPlay = sc.PlayToScene;
            Matrix4x4 playFromScene = sceneFromPlay.inverse;
            for (int e = 0; e < 2; e++)
            {
                Eye eye = v.eyes[e];
                if (e >= eyes) { Free(eye); eye.ok = false; continue; }
                EyeMatrices(viewer, v.stereo, e, out Matrix4x4 V, out Matrix4x4 P, out int W, out int H, out Vector3 eyePos);
                if (!ScreenRect(V, P, eyePos, out Vector2 nmin, out Vector2 nmax, out float nearest))
                {
                    Free(eye); eye.ok = false; st.culledHidden++;
                    continue;
                }
                // pixel rect on the eye buffer
                int x0 = Mathf.Clamp(Mathf.FloorToInt((nmin.x * 0.5f + 0.5f) * W), 0, W - 1);
                int y0 = Mathf.Clamp(Mathf.FloorToInt((nmin.y * 0.5f + 0.5f) * H), 0, H - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt((nmax.x * 0.5f + 0.5f) * W), x0 + 1, W);
                int y1 = Mathf.Clamp(Mathf.CeilToInt((nmax.y * 0.5f + 0.5f) * H), y0 + 1, H);
                int rw = x1 - x0, rh = y1 - y0;
                float dist = Mathf.Max(nearest, 1e-3f);
                float lod = sc.resolutionScale * Mathf.Clamp(sc.fullResDistance / dist, sc.minDistanceScale, 1f);
                int tw = Mathf.CeilToInt(rw * lod), th = Mathf.CeilToInt(rh * lod);
                int cap = Mathf.Max(64, sc.maxResolution);
                bool capped = false;
                if (tw > cap || th > cap) { float k = cap / (float)Mathf.Max(tw, th); tw = Mathf.CeilToInt(tw * k); th = Mathf.CeilToInt(th * k); capped = true; }
                tw = Bucket(tw); th = Bucket(th);
                if (!capped && Mathf.Abs(lod - 1f) < 1e-3f)
                {
                    // pixel-exact: grow the rect to the bucketed texture so one texel = one eye-buffer pixel
                    rw = tw; rh = th;
                }
                float u0 = x0 / (float)W, v0 = y0 / (float)H, uw = rw / (float)W, vh = rh / (float)H;
                eye.rect = new Vector4(u0, v0, 1f / uw, 1f / vh);
                // the scene camera: the eye mapped into the scene, its projection cut to the rect
                Matrix4x4 Vs = V * playFromScene;
                Matrix4x4 Ps = SubRect(WithDepthRange(P, sc), u0 * 2f - 1f, v0 * 2f - 1f, (u0 + uw) * 2f - 1f, (v0 + vh) * 2f - 1f);
                if (eye.rt == null || eye.rt.width != tw || eye.rt.height != th) { Free(eye); eye.rt = Take(tw, th); }
                Draw(sc, Vs, Ps, eye.rt);
                eye.ok = true;
                st.renders++;
                st.pixels += (long)tw * th;
            }
        }

        static int Bucket(int x) { return Mathf.Max(32, (x + 31) / 32 * 32); }

        // the eye's view (world → eye, OpenGL convention), projection, buffer size and position — THIS frame's head pose
        void EyeMatrices(Camera viewer, bool stereo, int eye, out Matrix4x4 V, out Matrix4x4 P, out int W, out int H, out Vector3 pos)
        {
            if (!stereo)
            {
                V = viewer.worldToCameraMatrix;
                P = viewer.projectionMatrix;
                W = Mathf.Max(1, viewer.pixelWidth);
                H = Mathf.Max(1, viewer.pixelHeight);
                pos = viewer.transform.position;
                return;
            }
            Transform t = viewer.transform;
            Vector3 lp; Quaternion lr;
            if (!EyeOffset(eye, out lp, out lr)) { lp = new Vector3(eye == 0 ? -EyeHalfIpd : EyeHalfIpd, 0f, 0f); lr = Quaternion.identity; }
            pos = t.position + t.rotation * lp;
            Quaternion rot = t.rotation * lr;
            V = Matrix4x4.TRS(pos, rot, new Vector3(1f, 1f, -1f)).inverse;
            P = viewer.GetStereoProjectionMatrix(eye == 0 ? Camera.StereoscopicEye.Left : Camera.StereoscopicEye.Right);
            if (Mathf.Abs(P.m00) < 1e-6f || Mathf.Abs(P.m11) < 1e-6f) P = viewer.projectionMatrix;
            W = XRSettings.eyeTextureWidth > 0 ? XRSettings.eyeTextureWidth : Mathf.Max(1, viewer.pixelWidth / 2);
            H = XRSettings.eyeTextureHeight > 0 ? XRSettings.eyeTextureHeight : Mathf.Max(1, viewer.pixelHeight);
        }

        // the eye's pose relative to the centre eye (the XR camera's transform), from the head device
        static readonly List<InputDevice> s_heads = new List<InputDevice>();
        static bool EyeOffset(int eye, out Vector3 p, out Quaternion r)
        {
            p = Vector3.zero; r = Quaternion.identity;
            InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (!head.isValid) return false;
            if (!head.TryGetFeatureValue(CommonUsages.centerEyePosition, out Vector3 cp) || !head.TryGetFeatureValue(CommonUsages.centerEyeRotation, out Quaternion cr)) return false;
            Vector3 ep; Quaternion er;
            bool ok = eye == 0
                ? head.TryGetFeatureValue(CommonUsages.leftEyePosition, out ep) & head.TryGetFeatureValue(CommonUsages.leftEyeRotation, out er)
                : head.TryGetFeatureValue(CommonUsages.rightEyePosition, out ep) & head.TryGetFeatureValue(CommonUsages.rightEyeRotation, out er);
            if (!ok) return false;
            Quaternion inv = Quaternion.Inverse(cr);
            p = inv * (ep - cp);
            r = inv * er;
            if (p.sqrMagnitude < 1e-8f) return false;       // some runtimes report no per-eye offset: use the IPD fallback
            return true;
        }

        // union of the visible surfaces' screen rects (NDC); false when none is in the frustum and facing the eye
        bool ScreenRect(Matrix4x4 V, Matrix4x4 P, Vector3 eyePos, out Vector2 nmin, out Vector2 nmax, out float nearest)
        {
            nmin = new Vector2(1f, 1f); nmax = new Vector2(-1f, -1f); nearest = float.MaxValue;
            Matrix4x4 VP = P * V;
            GeometryUtility.CalculateFrustumPlanes(VP, _planes);
            bool any = false;
            foreach (PortalSurface s in _surf)
            {
                Bounds b = s.rend.bounds;
                if (!GeometryUtility.TestPlanesAABB(_planes, b)) continue;
                if (!s.Faces(eyePos)) continue;
                any = true;
                nearest = Mathf.Min(nearest, Vector3.Distance(eyePos, b.ClosestPoint(eyePos)));
                Vector3 c = b.center, x = b.extents;
                int k = 0;
                for (int i = -1; i <= 1; i += 2) for (int j = -1; j <= 1; j += 2) for (int l = -1; l <= 1; l += 2)
                    _corners[k++] = c + new Vector3(x.x * i, x.y * j, x.z * l);
                bool behind = false;
                for (int i = 0; i < 8; i++)
                {
                    Vector4 h = VP * new Vector4(_corners[i].x, _corners[i].y, _corners[i].z, 1f);
                    if (h.w <= 1e-4f) { behind = true; break; }
                    float px = h.x / h.w, py = h.y / h.w;
                    nmin.x = Mathf.Min(nmin.x, px); nmin.y = Mathf.Min(nmin.y, py);
                    nmax.x = Mathf.Max(nmax.x, px); nmax.y = Mathf.Max(nmax.y, py);
                }
                if (behind) { nmin = new Vector2(-1f, -1f); nmax = new Vector2(1f, 1f); }
            }
            if (!any) return false;
            nmin.x = Mathf.Max(nmin.x, -1f); nmin.y = Mathf.Max(nmin.y, -1f);
            nmax.x = Mathf.Min(nmax.x, 1f); nmax.y = Mathf.Min(nmax.y, 1f);
            return nmax.x > nmin.x && nmax.y > nmin.y;
        }

        // perspective projection with the scene's own depth range (OpenGL convention)
        static Matrix4x4 WithDepthRange(Matrix4x4 P, PortalScene sc)
        {
            if (Mathf.Abs(P.m32 + 1f) > 1e-3f) return P;                // not a standard perspective: leave it
            float A = P.m22, B = P.m23;
            float n = B / (A - 1f), f = B / (A + 1f);
            if (!(n > 0f) || float.IsInfinity(n)) n = 0.01f;
            float nn = Mathf.Max(n, sc.minNearClip), ff = Mathf.Max(sc.farClip, nn * 10f);
            P.m22 = -(ff + nn) / (ff - nn);
            P.m23 = -2f * ff * nn / (ff - nn);
            return P;
        }

        // the part of NDC [x0,x1]×[y0,y1] stretched to the whole target
        static Matrix4x4 SubRect(Matrix4x4 P, float x0, float y0, float x1, float y1)
        {
            float sx = 2f / (x1 - x0), sy = 2f / (y1 - y0);
            var S = Matrix4x4.identity;
            S.m00 = sx; S.m03 = -(x1 + x0) / (x1 - x0);
            S.m11 = sy; S.m13 = -(y1 + y0) / (y1 - y0);
            return S * P;
        }

        void Draw(PortalScene sc, Matrix4x4 V, Matrix4x4 P, RenderTexture rt)
        {
            Matrix4x4 C = V.inverse;
            Vector3 pos = C.MultiplyPoint(Vector3.zero);
            Vector3 fwd = -(Vector3)C.GetColumn(2), up = C.GetColumn(1);
            if (fwd.sqrMagnitude > 1e-12f && up.sqrMagnitude > 1e-12f) _cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(fwd, up));
            else _cam.transform.position = pos;
            _cam.worldToCameraMatrix = V;
            _cam.projectionMatrix = P;
            _cam.cullingMask = sc.layer >= 0 && sc.layer < 32 ? 1 << sc.layer : 0;
            // emptyIsPassthrough: clear to alpha 0; the surface discards those pixels (the scene's own opaques write alpha 1)
            _cam.backgroundColor = sc.emptyIsPassthrough ? new Color(sc.background.r, sc.background.g, sc.background.b, 0f) : sc.background;
            _cam.targetTexture = rt;
            _cam.allowMSAA = rt.antiAliasing > 1;
            bool fogWas = RenderSettings.fog;
            if (sc.fog == PortalScene.FogMode.Off) RenderSettings.fog = false; else if (sc.fog == PortalScene.FogMode.On) RenderSettings.fog = true;
            bool k0 = Shader.IsKeywordEnabled(OcclusionKeywords[0]), k1 = Shader.IsKeywordEnabled(OcclusionKeywords[1]);
            if (k0) Shader.DisableKeyword(OcclusionKeywords[0]);
            if (k1) Shader.DisableKeyword(OcclusionKeywords[1]);
            sc.Open();
            try { _cam.Render(); }
            catch (System.Exception ex) { Debug.LogWarning("[Portal] render failed: " + ex.Message); }
            finally
            {
                sc.Close();
                if (k0) Shader.EnableKeyword(OcclusionKeywords[0]);
                if (k1) Shader.EnableKeyword(OcclusionKeywords[1]);
                RenderSettings.fog = fogWas;
                _cam.targetTexture = null;
                _cam.ResetWorldToCameraMatrix();
                _cam.ResetProjectionMatrix();
            }
            sc.lastRenderedFrame = Time.frameCount;
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
        // per camera: hand each surface the textures rendered for THIS camera; let PortalSceneCameras see their scenes
        // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
        void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (cam == _cam) return;
            foreach (PortalSceneCamera pc in PortalSceneCamera.Active)
            {
                if (pc == null || pc.cam != cam) continue;
                foreach (PortalScene sc in PortalScene.Active)
                    if (sc != null && pc.Sees(sc)) { sc.Open(); _opened.Add(sc); }
            }
            if (PortalSurface.Active.Count == 0) return;
            int frame = Time.frameCount;
            foreach (PortalSurface s in PortalSurface.Active)
            {
                if (s == null || s.rend == null) continue;
                View v = null;
                if (s.scene != null)
                {
                    long key = ((long)cam.GetInstanceID() << 32) ^ (uint)s.scene.GetInstanceID();
                    // this frame's view; or last frame's for a camera that renders before onBeforeRender (the Editor renders
                    // off-screen cameras and the Scene view first; on device every camera renders after it)
                    if (_views.TryGetValue(key, out View vv) && vv.frame >= frame - 1) v = vv;
                }
                MaterialPropertyBlock m = s.mpb;
                m.Clear();
                Eye l = v != null ? v.eyes[0] : null;
                Eye r = v != null ? (v.stereo ? v.eyes[1] : v.eyes[0]) : null;
                bool on = l != null && l.ok && l.rt != null && r != null && r.ok && r.rt != null;
                if (on)
                {
                    m.SetTexture(IdTexL, l.rt); m.SetVector(IdRectL, l.rect);
                    m.SetTexture(IdTexR, r.rt); m.SetVector(IdRectR, r.rect);
                }
                else
                {
                    m.SetTexture(IdTexL, Texture2D.blackTexture); m.SetTexture(IdTexR, Texture2D.blackTexture);
                }
                m.SetFloat(IdOn, on ? 1f : 0f);
                m.SetFloat(IdEye, DebugForceEye);
                bool pass = s.scene != null && s.scene.emptyIsPassthrough;
                m.SetFloat(IdEmptyClip, on && pass ? 1f : 0f);
                // a passthrough scene with nothing to draw (this frame or the last): the surface shows the real world, not its fallback
                m.SetFloat(IdHide, !on && pass && s.scene.isActiveAndEnabled && s.scene.emptyFrame >= frame - 1 ? 1f : 0f);
                m.SetFloat(IdFade, Mathf.Clamp01(s.fade));
                s.rend.SetPropertyBlock(m);
            }
        }

        void OnEndCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (_opened.Count == 0) return;
            foreach (PortalScene sc in _opened) if (sc != null) sc.Close();
            _opened.Clear();
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
        // texture pool
        // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
        RenderTexture Take(int w, int h)
        {
            int msaa = Msaa();
            for (int i = 0; i < _free.Count; i++)
            {
                RenderTexture f = _free[i].rt;
                if (f != null && f.width == w && f.height == h && f.antiAliasing == msaa) { _free.RemoveAt(i); return f; }
            }
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            var d = new RenderTextureDescriptor(w, h, linear ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm, 0)
            {
                msaaSamples = msaa,
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = linear,
                memoryless = RenderTextureMemoryless.Depth | (msaa > 1 ? RenderTextureMemoryless.MSAA : RenderTextureMemoryless.None),
            };
            d.depthStencilFormat = SystemInfo.IsFormatSupported(GraphicsFormat.D32_SFloat, GraphicsFormatUsage.Render) ? GraphicsFormat.D32_SFloat : GraphicsFormat.D24_UNorm_S8_UInt;
            var rt = new RenderTexture(d) { name = "PortalRT_" + w + "x" + h, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            rt.Create();
            _ours.Add(rt);
            return rt;
        }

        void Free(Eye e)
        {
            if (e.rt == null) return;
            _free.Add(new Pooled { rt = e.rt, lastFrame = Time.frameCount });
            e.rt = null;
        }

        void Kill(RenderTexture rt)
        {
            if (rt == null) return;
            _ours.Remove(rt);
            rt.Release();
            Destroy(rt);
        }

        static int Msaa()
        {
            var a = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            int m = a != null ? a.msaaSampleCount : Mathf.Max(1, QualitySettings.antiAliasing);
            return Mathf.Clamp(m, 1, 4);
        }

        static long Bytes(RenderTexture rt) { return rt == null ? 0 : (long)rt.width * rt.height * 4 * Mathf.Max(1, rt.antiAliasing); }
    }
}
