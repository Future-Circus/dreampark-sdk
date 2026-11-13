using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

namespace DreamPark
{
    /// <summary>
    /// The camera depth texture on demand (Nov 11 2026, perf round). The URP asset no longer makes a depth texture
    /// (_CameraDepthTexture) for every camera: on Quest that copy also forced an intermediate colour target and a final
    /// full-screen blit every frame, and nothing in core needs it (director: "the extra full screen copy every frame -
    /// what is this used for?" / "it could be used in the game content that is dynamically loaded in yeah? could it be
    /// added for the portals?"). Content that samples scene depth turns it on for ONE camera, only while it is loaded:
    ///
    /// • MAIN CAMERA: while any active <see cref="LevelTemplate"/> (a loaded title / attraction) has a renderer whose
    ///   material needs depth, Camera.main gets requiresDepthOption = On; when none does it goes back to what it was
    ///   (the pipeline setting = off). Rescanned when a template enables / disables
    ///   (<see cref="LevelTemplate.OnAnyLevelTemplateChanged"/>) and every <see cref="RescanSeconds"/> while a title is
    ///   up and depth is off (props / FX spawned later). The scan walks the titles' transforms sliced across frames
    ///   (<see cref="SliceBudgetMs"/> per frame), so a 20k-renderer title never costs one long frame. URP then adds the intermediate texture by itself (renderer
    ///   Intermediate Texture = Auto) only for that camera and only while depth is on.
    /// • PORTALS: <see cref="PortalRenderer"/> asks <see cref="NeedsDepth(PortalScene)"/> per scene render, so only the
    ///   portal camera gets depth, only for a scene whose own renderers need it. The eyes stay off.
    ///
    /// "Needs depth" (decided once per material, cached): a keyword in <see cref="DepthKeywords"/> (soft particles: URP
    /// Particles, DreamPark/Particles, Piloto UberFX), a shader name in <see cref="DepthShaders"/>, or the shader tag
    /// "DreamParkNeedsDepth" = "True". A ShaderGraph Scene Depth node is not visible at runtime: add that shader's name
    /// to DepthShaders (or the tag to a hand-written shader). Traces: [SceneDepth] on every flip, with the reason.
    ///
    /// FAR-PLANE FALLBACK: a camera that makes no depth texture leaves _CameraDepthTexture unbound, and Unity's default
    /// for an unbound texture is grey (measured: reads 0.5 = a surface right at the near plane), which fades soft
    /// particles out completely. So for every camera without a depth texture we bind a 1×1 far-plane texture (black on
    /// reversed-Z, a 2-slice array for single-pass stereo): a missed soft-particle material then just loses its soft
    /// edge instead of vanishing, which is what it looked like before against passthrough anyway.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class SceneDepthDemand : MonoBehaviour
    {
        /// <summary>Kill switch: false = never turn the main camera's depth texture on.</summary>
        public static bool Enabled = true;
        /// <summary>Rescan period while a title is loaded and depth is off (catches FX spawned after load).</summary>
        public static float RescanSeconds = 4f;
        public static readonly HashSet<string> DepthKeywords = new HashSet<string>
        {
            "_SOFTPARTICLES_ON",      // URP Particles Unlit/Lit/SimpleLit, DreamPark/Particles
            "SOFTPARTICLES_ON",       // built-in particle shaders
            "_USE_SOFT_PARTICLES",    // Piloto Studio UberFXSG (Soft Particle subgraph = Scene Depth)
        };
        /// <summary>Shader names that always sample scene depth (ShaderGraphs with a Scene Depth node, …).</summary>
        public static readonly HashSet<string> DepthShaders = new HashSet<string>();
        public const string DepthTag = "DreamParkNeedsDepth";

        /// <summary>Whether the main camera's depth texture is on right now, and why (for overlays / traces).</summary>
        public static bool MainCameraDepthOn { get; private set; }
        public static string Reason { get; private set; } = "";

        static SceneDepthDemand s_inst;
        static readonly Dictionary<int, bool> s_verdict = new Dictionary<int, bool>();
        static readonly List<Material> s_mats = new List<Material>();
        static readonly List<Renderer> s_rends = new List<Renderer>();
        sealed class PortalCheck { public int cursor, frame = -1; public bool on, any, logged; public float nextCycle; public string why; }
        static readonly Dictionary<PortalScene, PortalCheck> s_portal = new Dictionary<PortalScene, PortalCheck>();

        bool _dirty = true;
        float _next = float.PositiveInfinity;
        Camera _cam;
        UniversalAdditionalCameraData _camData;
        CameraOverrideOption _restore = CameraOverrideOption.UsePipelineSettings;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (s_inst != null || !Application.isPlaying) return;
            // statics survive play sessions when domain reload is off
            MainCameraDepthOn = false; Reason = "";
            s_verdict.Clear(); s_portal.Clear();
            var go = new GameObject("DreamPark.SceneDepthDemand") { hideFlags = HideFlags.DontSave };
            DontDestroyOnLoad(go);
            s_inst = go.AddComponent<SceneDepthDemand>();
        }

        /// <summary>Ask for a rescan next frame (e.g. after spawning content outside a LevelTemplate).</summary>
        public static void Rescan() { if (s_inst != null) s_inst._dirty = true; }

        static readonly int IdDepth = Shader.PropertyToID("_CameraDepthTexture");
        Texture2D _far2D;
        Texture2DArray _farArray;

        void OnEnable()
        {
            LevelTemplate.OnAnyLevelTemplateChanged += MarkDirty;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            _dirty = true;
        }

        void OnDisable()
        {
            LevelTemplate.OnAnyLevelTemplateChanged -= MarkDirty;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            if (MainCameraDepthOn) Apply(false, "SceneDepthDemand disabled");
        }

        void OnDestroy()
        {
            if (_far2D != null) Destroy(_far2D);
            if (_farArray != null) Destroy(_farArray);
            if (s_inst == this) s_inst = null;
        }

        // A camera without a depth texture: bind the far plane (URP binds the real one itself when it makes it).
        void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (cam == null) return;
            var d = cam.GetComponent<UniversalAdditionalCameraData>();
            if (d != null && d.requiresDepthTexture) return;
            bool array = cam.stereoEnabled && XRSettings.enabled &&
                         (XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassInstanced ||
                          XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassMultiview);
            Shader.SetGlobalTexture(IdDepth, array ? FarArray() : Far2D());
        }

        static Color FarColor => SystemInfo.usesReversedZBuffer ? Color.black : Color.white;

        Texture2D Far2D()
        {
            if (_far2D != null) return _far2D;
            _far2D = new Texture2D(1, 1, TextureFormat.RGBA32, false, true) { name = "DreamParkFarDepth", hideFlags = HideFlags.DontSave };
            _far2D.SetPixel(0, 0, FarColor);
            _far2D.Apply(false, true);
            return _far2D;
        }

        Texture2DArray FarArray()
        {
            if (_farArray != null) return _farArray;
            _farArray = new Texture2DArray(1, 1, 2, TextureFormat.RGBA32, false, true) { name = "DreamParkFarDepthXR", hideFlags = HideFlags.DontSave };
            var px = new[] { (Color32)FarColor };
            _farArray.SetPixels32(px, 0); _farArray.SetPixels32(px, 1);
            _farArray.Apply(false, true);
            return _farArray;
        }

        void MarkDirty() { _dirty = true; }

        void Update()
        {
            if (!Enabled)
            {
                if (MainCameraDepthOn) Apply(false, "SceneDepthDemand.Enabled = false");
                _scanning = false;
                _dirty = true;   // rescan when switched back on
                return;
            }
            if (_cam != null && Camera.main != _cam) Apply(MainCameraDepthOn, Reason);   // hand the state to the new camera
            if (_dirty) BeginPass(true);
            else if (!_scanning && Time.unscaledTime >= _next) BeginPass(false);
            if (_scanning) StepPass();
        }

        // ── the title scan: a transform walk sliced across frames (SliceBudgetMs per frame), never one big spike ────

        /// <summary>Per-frame time budget of the title scan; the walk continues next frame.</summary>
        public static float SliceBudgetMs = 0.2f;
        public struct PassStats { public int titles, nodes, renderers, frames; public double maxSliceMs, totalMs, findMs; public bool found; }
        /// <summary>The last finished scan (for the perf check and overlays).</summary>
        public static PassStats LastPass;

        readonly Stack<Transform> _walk = new Stack<Transform>();
        readonly List<LevelTemplate> _titles = new List<LevelTemplate>();
        readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
        bool _scanning;
        string _found;
        PassStats _cur;

        // refind = the set of titles changed (template enable / disable); the periodic rescan reuses the list
        void BeginPass(bool refind)
        {
            _dirty = false;
            _walk.Clear();
            _found = null;
            _cur = default;
            _sw.Restart();
            if (refind)
            {
                _titles.Clear();
                foreach (var lt in FindObjectsByType<LevelTemplate>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    if (lt != null) _titles.Add(lt);
            }
            for (int i = _titles.Count - 1; i >= 0; i--)
            {
                var lt = _titles[i];
                if (lt == null || !lt.isActiveAndEnabled) { _titles.RemoveAt(i); continue; }
                _walk.Push(lt.transform);
            }
            _cur.titles = _titles.Count;
            _cur.findMs = _sw.Elapsed.TotalMilliseconds;
            _scanning = true;
        }

        void StepPass()
        {
            _sw.Restart();
            long budget = (long)(SliceBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            int n = 0;
            while (_walk.Count > 0 && _found == null)
            {
                if ((++n & 15) == 0 && _sw.ElapsedTicks >= budget) break;
                Transform t = _walk.Pop();
                if (t == null) continue;   // destroyed mid-pass
                _cur.nodes++;
                t.GetComponents(s_rends);
                for (int i = 0; i < s_rends.Count; i++)
                {
                    Renderer r = s_rends[i];
                    _cur.renderers++;
                    // portal-scene members are hidden from the eyes (the portal camera asks for itself)
                    if (r == null || r.forceRenderingOff) continue;
                    if (NeedsDepth(r, out Material m))
                    {
                        var lt = r.GetComponentInParent<LevelTemplate>(true);
                        _found = $"{(lt != null ? TitleName(lt) : "title")}: {r.name} uses {m.name} ({m.shader.name})";
                        break;
                    }
                }
                s_rends.Clear();
                for (int c = t.childCount - 1; c >= 0; c--) _walk.Push(t.GetChild(c));
            }
            double ms = _sw.Elapsed.TotalMilliseconds;
            _cur.frames++;
            _cur.totalMs += ms;
            if (ms > _cur.maxSliceMs) _cur.maxSliceMs = ms;
            if (_walk.Count > 0 && _found == null) return;
            _walk.Clear();
            _scanning = false;
            _cur.found = _found != null;
            LastPass = _cur;
            int titles = _cur.titles;
            // periodic rescan only while a title is up and depth is off (turning ON is the direction that can be late)
            _next = titles > 0 && _found == null ? Time.unscaledTime + RescanSeconds : float.PositiveInfinity;
            Apply(_found != null, _found ?? (titles == 0 ? "no title loaded" : $"{titles} title(s) loaded, none samples scene depth"));
        }

        static string TitleName(LevelTemplate lt) => string.IsNullOrEmpty(lt.gameId) ? lt.name : lt.gameId + "/" + lt.name;

        void Apply(bool on, string why)
        {
            Camera cam = Camera.main;
            if (cam != _cam)
            {
                // hand the previous camera back exactly as we found it
                if (_camData != null && MainCameraDepthOn) _camData.requiresDepthOption = _restore;
                _cam = cam;
                _camData = cam != null ? cam.GetUniversalAdditionalCameraData() : null;
                _restore = _camData != null ? _camData.requiresDepthOption : CameraOverrideOption.UsePipelineSettings;
                MainCameraDepthOn = false;
            }
            if (_camData == null) return;
            if (on == MainCameraDepthOn && why == Reason) return;
            bool flip = on != MainCameraDepthOn;
            _camData.requiresDepthOption = on ? CameraOverrideOption.On : _restore;
            MainCameraDepthOn = on;
            Reason = why;
            if (flip) Debug.Log($"[SceneDepth] main camera ({_cam.name}) depth texture {(on ? "ON" : "OFF")}: {why}");
        }

        // ── detection ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Whether this material samples the camera depth texture (cached per material).</summary>
        public static bool NeedsDepth(Material m)
        {
            if (m == null) return false;
            int id = m.GetInstanceID();
            if (s_verdict.TryGetValue(id, out bool v)) return v;
            v = false;
            Shader sh = m.shader;
            if (sh != null && DepthShaders.Contains(sh.name)) v = true;
            if (!v)
            {
                string tag = m.GetTag(DepthTag, true, "");
                v = tag == "True" || tag == "true" || tag == "On" || tag == "1";
            }
            if (!v)
                foreach (string kw in m.shaderKeywords)
                    if (DepthKeywords.Contains(kw)) { v = true; break; }
            if (s_verdict.Count > 8192) s_verdict.Clear();   // runtime material instances: keep the cache bounded
            s_verdict[id] = v;
            return v;
        }

        /// <summary>Whether any material on this renderer needs depth.</summary>
        public static bool NeedsDepth(Renderer r, out Material which)
        {
            which = null;
            if (r == null) return false;
            r.GetSharedMaterials(s_mats);
            foreach (var m in s_mats)
                if (NeedsDepth(m)) { which = m; s_mats.Clear(); return true; }
            s_mats.Clear();
            return false;
        }

        /// <summary>Members checked per portal scene per frame (the check is sliced: a 20k-member scene never spikes).</summary>
        public static int PortalChunk = 256;

        /// <summary>
        /// Whether a portal scene's own renderers need depth. Sliced: each frame checks at most <see cref="PortalChunk"/>
        /// members and keeps the last verdict until a full cycle ends (early as soon as one needs depth); a new cycle
        /// starts at most once a second.
        /// </summary>
        public static bool NeedsDepth(PortalScene sc)
        {
            if (sc == null) return false;
            if (!s_portal.TryGetValue(sc, out var c))
            {
                if (s_portal.Count > 64) s_portal.Clear();
                s_portal[sc] = c = new PortalCheck();
            }
            int frame = Time.frameCount;
            float now = Time.unscaledTime;
            if (c.frame == frame || (c.cursor == 0 && now < c.nextCycle)) return c.on;
            c.frame = frame;
            var members = sc.Members;
            int end = Mathf.Min(members.Count, c.cursor + PortalChunk);
            for (; c.cursor < end && !c.any; c.cursor++)
                if (NeedsDepth(members[c.cursor], out Material m)) { c.any = true; c.why = $"{members[c.cursor].name} uses {m.name}"; }
            if (!c.any && c.cursor < members.Count) return c.on;   // cycle continues next frame
            bool was = c.on;
            c.on = c.any;
            c.any = false;
            c.cursor = 0;
            c.nextCycle = now + 1f;
            if (was != c.on || !c.logged)
                Debug.Log($"[SceneDepth] portal scene {sc.name} depth texture {(c.on ? "ON: " + c.why : "OFF")}");
            c.logged = true;
            return c.on;
        }
    }
}
