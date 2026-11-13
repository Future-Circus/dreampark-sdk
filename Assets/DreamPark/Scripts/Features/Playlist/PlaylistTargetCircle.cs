// ─────────────────────────────────────────────────────────────────────
//  PlaylistTargetCircle.cs — the shared step-in circle ("get to this spot": Reach's destination, every jump pad),
//  and PlaylistGoalLight: the one light language of the movement challenges (crossing lines, the circle, the arch).
//  goal-visuals, Oct 21 2026 — Aidan on the previous version: "terrible little lines", "why isn't it one clean sheet",
//  "scale of the ring is giant", "the glow up effect has a terrible texture". So:
//    • a crisp, bright PURPLE line on the floor (brand VioletBright, a lifted core), ~2.5 cm on the ring;
//    • ONE smooth sheet of soft purple light rising off it, fading to nothing at ~0.28 m — geometry + vertex colour
//      only, untextured (no streaks, no noise); a slow, broad brightness breath, never stripes;
//    • the circle is one person's step-in spot: 1.0 m across (radius 0.5); Reach and the jump pads test the same ring;
//    • no dark disc: inside the ring the floor stays as it is (a breath of purple at most);
//    • one mesh + one material per element (DreamPark/HoloParticle, premultiplied: the line stays purple on a bright
//      floor, the sheet adds light) — one draw each on Quest, no per-frame allocations.
//  Used by stop.mark{at=pos, icon='target'} (PlaylistGoalMarkers: the circle + the white pin over it), stop.reach,
//  stop.jump pads. Fades out of sight behind walls with the markers (PlaylistGoalSight).
// ─────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using DreamPark.MagicUI;
using UnityEngine;
using UnityEngine.Rendering;

namespace DreamPark
{
    [AddComponentMenu("")]
    public sealed class PlaylistTargetCircle : MonoBehaviour
    {
        /// One person's step-in circle (m). The drawn ring IS the test: Reach and the jump pads use this radius.
        public const float DefaultRadius = 0.5f, ReachSeconds = 0.8f, FadeInSeconds = 0.5f;

        float _radius, _born, _reachedAt = -1f, _alpha = 1f, _sight = 1f;
        Material _mat;
        MeshRenderer _renderer;

        public float Radius => _radius;
        public bool Reached => _reachedAt >= 0f;
        /// Current brightness (0..1) — for checks.
        public float Alpha => _alpha;
        /// 0..1 set by the owner each frame (line of sight); the circle eases toward it.
        public float SightTarget { get; set; } = 1f;

        /// A circle standing on the floor at <paramref name="floorPoint"/> (world), radius in metres.
        public static PlaylistTargetCircle Create(Transform parent, Vector3 floorPoint, float radius = DefaultRadius)
        {
            var go = new GameObject("TargetCircle") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(parent, true);
            go.transform.SetPositionAndRotation(floorPoint, Quaternion.identity);
            var c = go.AddComponent<PlaylistTargetCircle>();
            c._radius = Mathf.Clamp(radius, 0.25f, 3f);
            c._born = Time.time;
            c.Build();
            return c;
        }

        void Build()
        {
            _mat = PlaylistGoalLight.Material(MagicLook.QFloor + 1);
            if (_mat == null) return;
            gameObject.AddComponent<MeshFilter>().sharedMesh = PlaylistGoalLight.RingMesh(_radius);
            _renderer = gameObject.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = _mat;
            PlaylistGoalLight.Plain(_renderer);
        }

        /// The player stood in it: a short brighten + a glint burst, then the circle fades and goes.
        public void Reach()
        {
            if (_reachedAt >= 0f) return;
            _reachedAt = Time.time;
            var b = MagicLook.Burst(transform, _radius);
            if (b != null) { b.transform.localPosition = new Vector3(0f, 0.05f, 0f); b.Play(); }
        }

        /// True when a floor point (world) is inside the circle (horizontal).
        public bool Contains(Vector3 world, float margin = 0f)
        {
            Vector3 d = world - transform.position; d.y = 0f;
            return d.magnitude <= _radius + margin;
        }

        void LateUpdate()
        {
            if (_mat == null) return;
            float t = Time.time;
            float fadeIn = Mathf.Clamp01((t - _born) / FadeInSeconds);
            float reach = 1f, flash = 1f;
            if (_reachedAt >= 0f)
            {
                float k = Mathf.Clamp01((t - _reachedAt) / ReachSeconds);
                if (k >= 1f) { Destroy(gameObject); return; }
                flash = 1f + 0.8f * Mathf.Sin(Mathf.PI * Mathf.Min(1f, k * 2.5f)) * (1f - k);   // a short brighten
                reach = 1f - k * k;
            }
            _sight = Mathf.MoveTowards(_sight, Mathf.Clamp01(SightTarget), Time.deltaTime / 0.25f);
            _alpha = fadeIn * reach * _sight;
            PlaylistGoalLight.Set(_mat, _alpha, flash);
            if (_renderer != null) _renderer.enabled = _alpha > 0.004f;
        }

        void OnDestroy() { if (_mat != null) Destroy(_mat); }
    }

    /// The movement challenges' light: a crisp purple line on the floor + one smooth sheet of light rising off it
    /// (a straight line, a ring), and the jump arch (one bright curved line). Geometry + vertex colour only.
    public static class PlaylistGoalLight
    {
        /// The ring line's core width and its soft edge (m); the straight line's; the arch's.
        public const float RingCoreW = 0.03f, RingSoftW = 0.02f;
        public const float LineCoreW = 0.035f, LineSoftW = 0.022f;
        public const float ArchCoreW = 0.022f, ArchSoftW = 0.02f;
        /// The rising sheet: height (m), its brightness and coverage at the floor (fading as (1−k)² to 0 at the top).
        public const float RingSheetH = 0.28f, LineSheetH = 0.25f, SheetLight = 0.95f, SheetCover = 0.25f;
        /// The breath: a slow, broad brightness pulse (never stripes).
        public const float BreathMin = 0.88f, BreathHz = 0.22f;

        /// The line's core colour (brand VioletBright), its edge, and the sheet's light (brand Violet: deeper, saturated).
        public static Color Core => MagicLook.VioletBright;
        public static Color Edge => Color.Lerp(MagicLook.VioletBright, MagicLook.Violet, 0.5f);
        public static Color SheetC => MagicLook.Violet;

        /// A goal-light material: untextured, vertex colour (premultiplied), one per element (its own fade).
        public static Material Material(int queue)
        {
            var m = MagicLook.HoloMat(Texture2D.whiteTexture, Color.white, true, queue);
            if (m == null) return null;
            m.name = "GoalLight";
            m.SetFloat("_VertexColor", 1f);
            m.SetFloat("_SrcBlend", (float)BlendMode.One);                 // premultiplied: rgb = light, a = cover
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            return m;
        }

        /// Fade (0..1) and brighten (≥ 1) a goal-light material, with the shared breath.
        public static void Set(Material m, float fade, float bright = 1f)
        {
            if (m == null) return;
            float breath = BreathMin + (1f - BreathMin) * (0.5f + 0.5f * Mathf.Sin(Time.time * Mathf.PI * 2f * BreathHz));
            float l = Mathf.Clamp01(fade) * breath * bright;
            m.SetColor("_BaseColor", new Color(l, l, l, Mathf.Clamp01(fade)));
        }

        public static void Plain(Renderer r)
        {
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        static Color Pre(Color c, float light, float cover) => new Color(c.r * light, c.g * light, c.b * light, cover);

        /// The line's cross-section: offset across it (× half the full width) → colour (premultiplied).
        static void Profile(float coreW, float softW, List<float> off, List<Color> col)
        {
            off.Clear(); col.Clear();
            float h = coreW * 0.5f;
            off.Add(-h - softW); col.Add(Pre(Edge, 0f, 0f));
            off.Add(-h); col.Add(Pre(Edge, 1f, 0.92f));
            off.Add(0f); col.Add(Pre(Core, 1f, 1f));
            off.Add(h); col.Add(Pre(Edge, 1f, 0.92f));
            off.Add(h + softW); col.Add(Pre(Edge, 0f, 0f));
        }

        static float Sheet(float k) => (1f - k) * (1f - k);
        const int SheetRows = 9;

        static readonly Dictionary<int, Mesh> s_rings = new Dictionary<int, Mesh>();

        /// The ring (radius r, cached per cm): a faint inside, the line, and the sheet as an open cylinder at r.
        public static Mesh RingMesh(float r)
        {
            int key = Mathf.RoundToInt(r * 100f);
            if (s_rings.TryGetValue(key, out var cached) && cached != null) return cached;
            const int N = 128;
            const float y0 = 0.012f;
            var v = new List<Vector3>(); var c = new List<Color>(); var tri = new List<int>();
            int Ring(float radius, float y, Color col)
            {
                int start = v.Count;
                for (int i = 0; i <= N; i++)
                {
                    float a = i * Mathf.PI * 2f / N;
                    v.Add(new Vector3(Mathf.Cos(a) * radius, y, Mathf.Sin(a) * radius)); c.Add(col);
                }
                return start;
            }
            void Band(int a0, int a1) { for (int i = 0; i < N; i++) { int p = a0 + i, q = a1 + i; tri.Add(p); tri.Add(q); tri.Add(p + 1); tri.Add(p + 1); tri.Add(q); tri.Add(q + 1); } }
            var off = new List<float>(); var pc = new List<Color>();
            Profile(RingCoreW, RingSoftW, off, pc);
            // the inside: a breath of purple (no dark disc)
            int centre = v.Count; v.Add(new Vector3(0f, y0 - 0.001f, 0f)); c.Add(Pre(Edge, 0.025f, 0.01f));
            int inner = Ring(r + off[0], y0 - 0.001f, Pre(Edge, 0.035f, 0.015f));
            for (int i = 0; i < N; i++) { tri.Add(centre); tri.Add(inner + i + 1); tri.Add(inner + i); }
            // the line
            int prev = -1;
            for (int j = 0; j < off.Count; j++) { int row = Ring(r + off[j], y0, pc[j]); if (prev >= 0) Band(prev, row); prev = row; }
            // the sheet
            prev = -1;
            for (int j = 0; j <= SheetRows; j++)
            {
                float k = j / (float)SheetRows, s = Sheet(k);
                int row = Ring(r, y0 + k * RingSheetH, Pre(SheetC, SheetLight * s, SheetCover * s));
                if (prev >= 0) Band(prev, row);
                prev = row;
            }
            var m = new Mesh { name = "GoalLight Ring r" + key };
            m.SetVertices(v); m.SetColors(c); m.SetTriangles(tri, 0); m.RecalculateBounds();
            s_rings[key] = m;
            return m;
        }

        /// A straight line `len` m long along local X (centred), on the floor (y = 0), with its sheet rising in the
        /// XY plane. Its ends taper softly (the line over 0.1 m, the sheet over 0.3 m).
        public static Mesh LineMesh(float len)
        {
            len = Mathf.Max(0.3f, len);
            int cols = Mathf.Max(4, Mathf.CeilToInt(len / 0.08f)) + 1;
            var v = new List<Vector3>(); var c = new List<Color>(); var tri = new List<int>();
            var off = new List<float>(); var pc = new List<Color>();
            Profile(LineCoreW, LineSoftW, off, pc);
            float SS(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
            void Grid(int rows, System.Func<int, int, Vector3> pos, System.Func<int, float, Color> col)
            {
                int start = v.Count;
                for (int j = 0; j < rows; j++)
                    for (int i = 0; i < cols; i++)
                    {
                        float x = -len * 0.5f + len * i / (cols - 1);
                        float end = Mathf.Min(x + len * 0.5f, len * 0.5f - x);
                        v.Add(pos(j, i)); c.Add(col(j, end));
                    }
                for (int j = 0; j < rows - 1; j++)
                    for (int i = 0; i < cols - 1; i++)
                    {
                        int p = start + j * cols + i, q = p + cols;
                        tri.Add(p); tri.Add(q); tri.Add(p + 1); tri.Add(p + 1); tri.Add(q); tri.Add(q + 1);
                    }
            }
            float X(int i) => -len * 0.5f + len * i / (cols - 1);
            Grid(off.Count, (j, i) => new Vector3(X(i), 0.004f, off[j]), (j, end) => pc[j] * SS(end / 0.1f));
            Grid(SheetRows + 1, (j, i) => new Vector3(X(i), 0.004f + LineSheetH * j / SheetRows, 0f), (j, end) =>
            {
                float s = Sheet(j / (float)SheetRows) * SS(end / 0.3f);
                return Pre(SheetC, SheetLight * s, SheetCover * s);
            });
            var m = new Mesh { name = "GoalLight Line" };
            m.SetVertices(v); m.SetColors(c); m.SetTriangles(tri, 0); m.RecalculateBounds();
            return m;
        }

        /// Fill a ribbon mesh's colours / triangles for the arch (positions come from PlaylistGoalArch, facing the
        /// camera). Samples × the 5-point cross-section; the ends fade over ~15 % of the curve.
        public static void ArchTopology(Mesh m, int samples, Color[] colors)
        {
            var off = new List<float>(); var pc = new List<Color>();
            Profile(ArchCoreW, ArchSoftW, off, pc);
            int w = off.Count;
            var tri = new List<int>();
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)(samples - 1);
                float e = Mathf.Clamp01(Mathf.Min(t, 1f - t) / 0.15f); e = e * e * (3f - 2f * e);
                for (int j = 0; j < w; j++) colors[i * w + j] = pc[j] * e;
                if (i < samples - 1)
                    for (int j = 0; j < w - 1; j++)
                    {
                        int p = i * w + j, q = p + w;
                        tri.Add(p); tri.Add(q); tri.Add(p + 1); tri.Add(p + 1); tri.Add(q); tri.Add(q + 1);
                    }
            }
            m.SetVertices(new Vector3[samples * w]);
            m.SetColors(colors);
            m.SetTriangles(tri, 0);
        }

        /// The arch's cross-section offsets (m) — PlaylistGoalArch places these along the facing side vector.
        public static float[] ArchOffsets()
        {
            var off = new List<float>(); var pc = new List<Color>();
            Profile(ArchCoreW, ArchSoftW, off, pc);
            return off.ToArray();
        }
    }

    /// The jump arch: ONE bright purple curved line from pad to pad (a parabola), a ribbon that always faces the camera
    /// rendering it (so it reads the same from every side), with soft ends. Re-faced per camera before it renders
    /// (no allocations); the breath + fade like every goal light.
    [AddComponentMenu("")]
    public sealed class PlaylistGoalArch : MonoBehaviour
    {
        const int Samples = 40;
        static readonly HashSet<PlaylistGoalArch> s_live = new HashSet<PlaylistGoalArch>();
        static bool s_hooked;

        Vector3[] _curve, _verts;
        float[] _off;
        Mesh _mesh;
        Material _mat;
        float _born;

        /// An arch a → b (local to `parent`), `rise` m high at its middle.
        public static GameObject Create(Transform parent, Vector3 a, Vector3 b, float rise, string name)
        {
            var mat = PlaylistGoalLight.Material(MagicLook.QGlow);
            if (mat == null) return null;
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(parent, false);
            var arch = go.AddComponent<PlaylistGoalArch>();
            arch._mat = mat;
            arch._born = Time.time;
            arch._off = PlaylistGoalLight.ArchOffsets();
            arch._curve = new Vector3[Samples];
            for (int i = 0; i < Samples; i++)
            {
                float t = i / (float)(Samples - 1);
                arch._curve[i] = Vector3.Lerp(a, b, t) + Vector3.up * (4f * rise * t * (1f - t) + 0.015f);
            }
            arch._verts = new Vector3[Samples * arch._off.Length];
            arch._mesh = new Mesh { name = "GoalLight Arch" };
            arch._mesh.MarkDynamic();
            PlaylistGoalLight.ArchTopology(arch._mesh, Samples, new Color[Samples * arch._off.Length]);
            var bounds = new Bounds((a + b) * 0.5f + Vector3.up * rise * 0.5f, new Vector3(Mathf.Abs(b.x - a.x) + 0.4f, rise + 0.4f, Mathf.Abs(b.z - a.z) + 0.4f));
            arch._mesh.bounds = bounds;
            go.AddComponent<MeshFilter>().sharedMesh = arch._mesh;
            var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
            PlaylistGoalLight.Plain(r);
            arch.Face(Camera.main != null ? Camera.main.transform.position : go.transform.position + Vector3.back * 3f);
            return go;
        }

        void OnEnable()
        {
            s_live.Add(this);
            if (!s_hooked) { RenderPipelineManager.beginCameraRendering += OnCamera; s_hooked = true; }
        }

        void OnDisable() => s_live.Remove(this);

        static void OnCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (cam == null) return;
            Vector3 eye = cam.transform.position;
            foreach (var a in s_live) if (a != null) a.Face(eye);
        }

        /// Turn the ribbon's width across the curve, perpendicular to the view (world eye position).
        void Face(Vector3 eyeWorld)
        {
            if (_mesh == null) return;
            Vector3 eye = transform.InverseTransformPoint(eyeWorld);
            int w = _off.Length;
            for (int i = 0; i < Samples; i++)
            {
                Vector3 p = _curve[i];
                Vector3 tan = _curve[Mathf.Min(i + 1, Samples - 1)] - _curve[Mathf.Max(i - 1, 0)];
                Vector3 side = Vector3.Cross(tan, eye - p);
                side = side.sqrMagnitude > 1e-8f ? side.normalized : Vector3.up;
                for (int j = 0; j < w; j++) _verts[i * w + j] = p + side * _off[j];
            }
            _mesh.SetVertices(_verts);
        }

        void LateUpdate() => PlaylistGoalLight.Set(_mat, Mathf.Clamp01((Time.time - _born) / 0.35f));

        void OnDestroy()
        {
            s_live.Remove(this);
            if (_mat != null) Destroy(_mat);
            if (_mesh != null) Destroy(_mesh);
        }
    }

    /// A goal light that only breathes (the crossing lines): its own material's fade-in + breath.
    [AddComponentMenu("")]
    public sealed class PlaylistGoalGlow : MonoBehaviour
    {
        public Material mat;
        public Mesh mesh;
        float _born = -1f;
        void OnEnable() => _born = Time.time;
        void LateUpdate() => PlaylistGoalLight.Set(mat, Mathf.Clamp01((Time.time - _born) / 0.35f));
        void OnDestroy() { if (mat != null) Destroy(mat); if (mesh != null) Destroy(mesh); }
    }
}
