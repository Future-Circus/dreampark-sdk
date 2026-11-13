using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace DreamPark.MagicUI
{
    /// <summary>
    /// DREAMPARK MAGIC UI (Oct 18 2026; first built for the playlist bookends, now the shared platform kit for
    /// every in-world information UI: bookends, objective cards, Dreamie's bubbles — PLAYLISTS.md § 11.11).
    /// The brand's magic, not a new style: the approved attraction flourish's additive light (the
    /// Hidden/DreamPark/AttractionFlourish Resources materials — glint stars, the soft ring, orb motes; a
    /// HoloParticle spark stands in where a project has no flourish), the DreamBand / Dreamie violet, the brand
    /// type (Zalando Sans Expanded + DM Sans, TMP SDF with a glow underlay), sized for Quest reading distance.
    /// Pieces: <see cref="MagicLook"/> (palette, fonts, materials, textures, ring, sparkles, confetti, burst,
    /// x-ray), <see cref="MagicBoard"/> (the conjured board), <see cref="TextResolve"/>, <see cref="MagicOrb"/>
    /// (gaze-dwell choices), <see cref="MagicSight"/> (readability), <see cref="MagicUIRoot"/> (marker).
    /// </summary>
    public static class MagicLook
    {
        // Brand palette (the DreamBand HoloTheme defaults + Dreamie's DreamieFx; iOS DesignSystem dpViolet).
        public static readonly Color Violet = new Color(0.545f, 0.243f, 0.965f);        // #8B3EF6
        public static readonly Color VioletBright = new Color(0.686f, 0.408f, 1f);      // #AF68FF
        public static readonly Color Lilac = new Color(0.86f, 0.78f, 1f);
        public static readonly Color Spark = new Color(0.92f, 0.85f, 1f);
        public static readonly Color Gold = new Color(0.941f, 0.706f, 0.161f);          // #F0B429
        public static readonly Color Glass = new Color(0.055f, 0.031f, 0.110f, 0.72f);
        public static readonly Color TextSecondary = new Color(0.725f, 0.659f, 0.831f); // #B9A8D4
        /// The transform scale every 3D TMP label is authored at (TMP keeps sane font sizes; the transform shrinks).
        public const float TextUnitScale = 0.01f;

        public const string FlourishGlint = "AttractionTransitions/AttractionFlourishGlint";
        public const string FlourishOrb = "AttractionTransitions/AttractionFlourishOrb";
        public const string FlourishRing = "AttractionTransitions/AttractionFlourishRing";

        // Render order: floor light, glass, glow, text (TMP 3000), stickers, sparkles.
        public const int QFloor = 2980, QGlass = 2990, QGlow = 2992, QSticker = 3010, QSpark = 3020;

        static Shader s_holo;
        public static Shader Holo => s_holo != null ? s_holo : (s_holo = Shader.Find("DreamPark/HoloParticle"));

        /// A per-use copy of a flourish material (null when the build has no flourish).
        public static Material Flourish(string resource, float intensity = -1f)
        {
            var src = Resources.Load<Material>(resource);
            if (src == null) return null;
            var m = new Material(src) { name = src.name + " (bookend)" };
            if (intensity > 0f && m.HasProperty("_Intensity")) m.SetFloat("_Intensity", intensity);
            return m;
        }

        public static Material HoloMat(Texture tex, Color color, bool additive, int queue)
        {
            if (Holo == null) return null;
            var m = new Material(Holo) { name = "Bookend" };
            m.SetTexture("_BaseMap", tex);
            m.SetColor("_BaseColor", color);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)(additive ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
            m.SetFloat("_Exposure", 1f);
            m.SetFloat("_VertexColor", 0f);
            m.renderQueue = queue;
            return m;
        }

        public static MeshRenderer Plate(Transform parent, string name, Mesh mesh, Material mat, Vector3 pos, Quaternion rot, Vector3 scale)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return r;
        }

        // ── Brand type (PLAYLISTS.md § 11; SDK Assets/DreamPark/Fonts, SIL OFL 1.1) ──
        // Display: Zalando Sans Expanded ExtraBold (the Web landing page's --lp-display). Body: DM Sans SemiBold
        // (--lp-body). TMP SDF atlases in SDK Resources/DreamParkFonts; TMP's default font is the fallback.
        public const string DisplayFontResource = "DreamParkFonts/DreamPark Display SDF";
        public const string BodyFontResource = "DreamParkFonts/DreamPark Body SDF";
        static TMP_FontAsset s_display, s_body;
        static bool s_fontsLoaded;
        static readonly Dictionary<TMP_FontAsset, Material> s_glowMats = new Dictionary<TMP_FontAsset, Material>();

        public static TMP_FontAsset Font(bool display)
        {
            if (!s_fontsLoaded)
            {
                s_fontsLoaded = true;
                s_display = Resources.Load<TMP_FontAsset>(DisplayFontResource);
                s_body = Resources.Load<TMP_FontAsset>(BodyFontResource);
                // A project whose TMP lives elsewhere (Unity 6 ugui-TMP vs an imported copy) resolves the SDF shader by
                // name, so the shared atlases never render pink in a title, the SDK or Dreamie.
                foreach (TMP_FontAsset fa in new[] { s_display, s_body })
                    if (fa != null && fa.material != null && (fa.material.shader == null || !fa.material.shader.isSupported || fa.material.shader.name == "Hidden/InternalErrorShader"))
                    {
                        Shader sdf = Shader.Find("TextMeshPro/Distance Field") ?? Shader.Find("TextMeshPro/Mobile/Distance Field");
                        if (sdf != null) fa.material.shader = sdf;
                    }
            }
            TMP_FontAsset f = display ? s_display : s_body;
            return f;   // null → TMP's default font
        }

        /// The font's material with a soft violet inner glow behind every glyph (TMP underlay): text floats in
        /// light AND keeps its contrast over any background.
        public static Material GlowMaterial(TMP_FontAsset font)
        {
            if (font == null || font.material == null) return null;
            if (s_glowMats.TryGetValue(font, out Material m) && m != null) return m;
            m = new Material(font.material) { name = font.name + " (bookend glow)" };
            m.EnableKeyword("UNDERLAY_ON");
            if (m.HasProperty("_UnderlayColor")) m.SetColor("_UnderlayColor", new Color(0.24f, 0.06f, 0.52f, 0.78f));
            if (m.HasProperty("_UnderlayDilate")) m.SetFloat("_UnderlayDilate", 0.55f);
            if (m.HasProperty("_UnderlaySoftness")) m.SetFloat("_UnderlaySoftness", 0.75f);
            if (m.HasProperty("_FaceDilate")) m.SetFloat("_FaceDilate", 0.05f);
            s_glowMats[font] = m;
            return m;
        }

        public static TextMeshPro Label(Transform parent, string name, float size, Color color, float widthM = 1.5f, bool display = false)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshPro>();
            TMP_FontAsset f = Font(display);
            if (f != null)
            {
                t.font = f;
                Material glow = GlowMaterial(f);
                if (glow != null) t.fontSharedMaterial = glow;
            }
            t.fontSize = size;
            t.alignment = TextAlignmentOptions.Center;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Overflow;
            t.richText = true;
            t.color = color;
            t.rectTransform.sizeDelta = new Vector2(widthM / TextUnitScale, 20f);
            t.rectTransform.localScale = Vector3.one * TextUnitScale;
            var r = go.GetComponent<MeshRenderer>();
            if (r != null) { r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false; }
            return t;
        }

        // ── Readability fallback (§ 11.7): geometry that still cuts through a board ──

        /// Draw these renderers over everything (ZTest Always, after the scene). Used only when the sight check
        /// could not find a clear spot: the board stays legible; it is a fixed, flat plane, so there is no
        /// flicker or depth fighting, only the occluder appearing behind it.
        static readonly System.Collections.Generic.Dictionary<int, int> s_xrayBaseQueue = new System.Collections.Generic.Dictionary<int, int>();
        /// X-ray panes rise into the overlay range (QGlass 2990 → 4090 … QSpark 3020 → 4120), their text above them all:
        /// no title content (transparent props, lines, particles up to the overlay queue) draws over a readable board.
        public const int XrayLift = 1100, XrayTextQueue = 4125;

        public static void SetXray(Transform root, bool on)
        {
            if (root == null) return;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                var tmp = r.GetComponent<TMP_Text>();
                if (tmp != null)
                {
                    Material m = tmp.fontMaterial;   // a per-label instance
                    // TMP's SDF shaders read ZTest [unity_GUIZTestMode] but do NOT declare it in their Properties
                    // (the Mobile SDF the brand fonts use), so HasProperty is false: set it regardless, or the TEXT
                    // stays depth-tested while the glass draws over (playlist-runner, Oct 18: the boss card's words
                    // were cut by King Jubbo's pillar while the board reported x-ray on).
                    m.SetFloat("unity_GUIZTestMode", (float)(on ? UnityEngine.Rendering.CompareFunction.Always : UnityEngine.Rendering.CompareFunction.LessEqual));
                    // Over-content = the OVERLAY range (Oct 19 review: SAL props/wires at transparent queues ≥ 3110 still drew
                    // over a 3110 card): text above every x-ray pane.
                    m.renderQueue = on ? XrayTextQueue : 3000;
                    continue;
                }
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null || !m.HasProperty("_ZTest")) continue;
                    m.SetFloat("_ZTest", (float)(on ? UnityEngine.Rendering.CompareFunction.Always : UnityEngine.Rendering.CompareFunction.LessEqual));
                    // Idempotent: x-ray = the material's OWN queue + 100, however often it is applied (a board
                    // re-applies it for content added later; a relative += pushed the glass over its words, Oct 18).
                    int id = m.GetInstanceID();
                    if (!s_xrayBaseQueue.TryGetValue(id, out int baseQueue)) s_xrayBaseQueue[id] = baseQueue = m.renderQueue;
                    m.renderQueue = on ? baseQueue + XrayLift : baseQueue;
                }
            }
        }

        // ── Meshes ──────────────────────────────────────────────────

        static Mesh s_quad, s_cube;
        public static Mesh Quad()
        {
            if (s_quad != null) return s_quad;
            s_quad = new Mesh { name = "BookendQuad" };
            s_quad.vertices = new[] { new Vector3(-.5f, -.5f, 0), new Vector3(.5f, -.5f, 0), new Vector3(-.5f, .5f, 0), new Vector3(.5f, .5f, 0) };
            s_quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            s_quad.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            s_quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            s_quad.RecalculateBounds();
            return s_quad;
        }

        public static Mesh Cube()
        {
            if (s_cube != null) return s_cube;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            s_cube = tmp.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.DestroyImmediate(tmp);
            return s_cube;
        }

        /// A floor quad for the flourish's RING mode: TEXCOORD0 = (u, v, random, age), colour = tint.
        public static Mesh RingQuad(float random, float age, Color color)
        {
            var m = new Mesh { name = "BookendRingQuad" };
            m.vertices = new[] { new Vector3(-.5f, 0, -.5f), new Vector3(.5f, 0, -.5f), new Vector3(-.5f, 0, .5f), new Vector3(.5f, 0, .5f) };
            m.SetUVs(0, new List<Vector4> { new Vector4(0, 0, random, age), new Vector4(1, 0, random, age), new Vector4(0, 1, random, age), new Vector4(1, 1, random, age) });
            m.colors = new[] { color, color, color, color };
            m.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            m.RecalculateBounds();
            return m;
        }

        // ── The floor ring of light (start ring, lobby, results) ─────

        /// A soft ring of light on the floor, `radius` m, with glints running round it and motes rising
        /// (the flourish's ring + sparkle, looping). Falls back to a HoloParticle ring texture.
        public static GameObject FloorRing(Transform parent, float radius, out Material ringMat, out ParticleSystem glints)
        {
            var root = new GameObject("LightRing");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = new Vector3(0f, 0.015f, 0f);
            glints = null;
            ringMat = Flourish(FlourishRing, 1.0f);
            if (ringMat != null)
            {
                // A thin band of light, not a lit disc: the flourish's ring is tuned for a 1 m footprint, so
                // its band narrows as the room-sized ring grows.
                if (ringMat.HasProperty("_RingWidth")) ringMat.SetFloat("_RingWidth", Mathf.Clamp(0.075f / Mathf.Max(1f, radius), 0.025f, 0.075f));
                // The band sits at uv 0.78 of the quad's half-size: size the quad so the band IS the radius.
                float size = 2f * radius / 0.78f;
                foreach (var (rnd, age, a, s, lilac) in new[] { (0.31f, 0.35f, 0.85f, 1f, false), (0.77f, 0.7f, 0.35f, 0.97f, true) })
                {
                    var go = new GameObject("Band");
                    go.transform.SetParent(root.transform, false);
                    go.transform.localScale = new Vector3(size * s, 1f, size * s);
                    Color tint = lilac ? Lilac : Violet;
                    go.AddComponent<MeshFilter>().sharedMesh = RingQuad(rnd, age, new Color(tint.r, tint.g, tint.b, a));
                    var r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = ringMat;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    r.receiveShadows = false;
                }
            }
            else if (Holo != null)
            {
                ringMat = HoloMat(RingTexture(), VioletBright, true, QFloor);
                Plate(root.transform, "Band", Quad(), ringMat, Vector3.zero, Quaternion.Euler(90, 0, 0), new Vector3(2 * radius, 2 * radius, 1));
            }
            // Glints running round the ring (a slow shimmer that travels the circumference) + motes rising off it.
            glints = Sparkles(root.transform, "RingGlints", FlourishGlint, radius, rate: 10f + 4f * radius, rise: 0.06f, life: 2.4f,
                size: new Vector2(0.10f, 0.22f), edgeOnly: true);
            var orbit = glints.velocityOverLifetime;
            orbit.enabled = true;
            orbit.space = ParticleSystemSimulationSpace.Local;
            orbit.orbitalZ = new ParticleSystem.MinMaxCurve(0.35f / Mathf.Max(1f, radius));   // the shape's local Z = world up
            Sparkles(root.transform, "RingMotes", FlourishOrb, radius, rate: 4f + 2f * radius, rise: 0.35f, life: 2.6f,
                size: new Vector2(0.04f, 0.08f), edgeOnly: true);
            return root;
        }

        /// A looping particle shimmer on a circle (edge) or disc, in brand colours, the flourish's materials.
        public static ParticleSystem Sparkles(Transform parent, string name, string material, float radius, float rate, float rise,
            float life, Vector2 size, bool edgeOnly)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);   // shape circle lies on the floor
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.maxParticles = 300;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.7f, life);
            main.startSpeed = new ParticleSystem.MinMaxCurve(rise * 0.5f, rise);
            main.startSize = new ParticleSystem.MinMaxCurve(size.x, size.y);
            main.startColor = BrandGradient(false);
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            var em = ps.emission; em.rateOverTime = rate;
            var sh = ps.shape;
            sh.shapeType = ParticleSystemShapeType.Circle;
            sh.radius = radius;
            sh.radiusThickness = edgeOnly ? 0.02f : 1f;
            sh.arcMode = ParticleSystemShapeMultiModeValue.Random;
            var col = ps.colorOverLifetime; col.enabled = true;
            col.color = FadeInOut();
            var noise = ps.noise; noise.enabled = true; noise.strength = 0.08f; noise.frequency = 0.5f;
            Rendering(ps, material, stretched: false);
            ps.Play();
            return ps;
        }

        /// MAGIC LIGHT-CONFETTI (the Results Room): twinkling star glints and light streaks in violet, lilac,
        /// white and a little gold, bursting up and drifting down. Never paper.
        public static ParticleSystem LightConfetti(Transform parent, out ParticleSystem streaks)
        {
            var go = new GameObject("LightConfetti");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0.4f, 0f);
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = false; main.playOnAwake = false; main.duration = 1f; main.maxParticles = 600;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.4f, 3.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.8f, 3.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.2f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.gravityModifier = 0.22f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor = BrandGradient(true);
            var em = ps.emission; em.enabled = false;
            var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 32f; sh.radius = 0.35f;
            var vel = ps.limitVelocityOverLifetime; vel.enabled = true; vel.limit = 2.6f; vel.dampen = 0.08f;
            var col = ps.colorOverLifetime; col.enabled = true; col.color = FadeInOut(0.75f);
            var noise = ps.noise; noise.enabled = true; noise.strength = 0.45f; noise.frequency = 0.5f;
            Rendering(ps, FlourishGlint, stretched: false);

            var sgo = new GameObject("LightStreaks");
            sgo.transform.SetParent(go.transform, false);
            streaks = sgo.AddComponent<ParticleSystem>();
            streaks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var sm = streaks.main;
            sm.loop = false; sm.playOnAwake = false; sm.duration = 1f; sm.maxParticles = 200;
            sm.startLifetime = new ParticleSystem.MinMaxCurve(0.9f, 1.6f);
            sm.startSpeed = new ParticleSystem.MinMaxCurve(3.0f, 4.6f);
            sm.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.09f);
            sm.gravityModifier = 0.35f;
            sm.simulationSpace = ParticleSystemSimulationSpace.World;
            sm.startColor = BrandGradient(true);
            var sem = streaks.emission; sem.enabled = false;
            var ssh = streaks.shape; ssh.shapeType = ParticleSystemShapeType.Cone; ssh.angle = 24f; ssh.radius = 0.2f;
            var scol = streaks.colorOverLifetime; scol.enabled = true; scol.color = FadeInOut(0.5f);
            Rendering(streaks, FlourishOrb, stretched: true);
            return ps;
        }

        /// A short glint fountain (a badge popping in, the start's kick-off).
        public static ParticleSystem Burst(Transform parent, float radius)
        {
            var go = new GameObject("GlintBurst");
            go.transform.SetParent(parent, false);
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = false; main.playOnAwake = false; main.duration = 0.5f; main.maxParticles = 200;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.2f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 1.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.16f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor = BrandGradient(true);
            var em = ps.emission; em.enabled = false;
            var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Circle; sh.radius = radius; sh.radiusThickness = 0.3f;
            var col = ps.colorOverLifetime; col.enabled = true; col.color = FadeInOut(0.3f);
            Rendering(ps, FlourishGlint, stretched: false);
            return ps;
        }

        static void Rendering(ParticleSystem ps, string material, bool stretched)
        {
            var r = ps.GetComponent<ParticleSystemRenderer>();
            Material m = Flourish(material);
            if (m != null)
            {
                r.sharedMaterial = m;
                r.SetActiveVertexStreams(new List<ParticleSystemVertexStream>
                {
                    ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV,
                    ParticleSystemVertexStream.StableRandomX, ParticleSystemVertexStream.AgePercent,
                });
            }
            else if (Holo != null)
            {
                m = HoloMat(SparkTexture(), Color.white, true, QSpark);
                m.SetFloat("_VertexColor", 1f);
                r.sharedMaterial = m;
            }
            r.renderMode = stretched ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
            if (stretched) { r.velocityScale = 0.09f; r.lengthScale = 2.0f; }
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            if (m != null) m.renderQueue = QSpark;
        }

        public static ParticleSystem.MinMaxGradient BrandGradient(bool withGold)
        {
            var g = new Gradient();
            g.SetKeys(new[]
                {
                    new GradientColorKey(VioletBright, 0f), new GradientColorKey(Lilac, 0.35f), new GradientColorKey(Spark, 0.65f),
                    new GradientColorKey(withGold ? new Color(1f, 0.84f, 0.5f) : Lilac, 0.85f), new GradientColorKey(Violet, 1f),
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return new ParticleSystem.MinMaxGradient(g) { mode = ParticleSystemGradientMode.RandomColor };
        }

        static Gradient FadeInOut(float holdTo = 0.7f)
        {
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.12f), new GradientAlphaKey(1f, holdTo), new GradientAlphaKey(0f, 1f) });
            return g;
        }

        // ── Textures (procedural, cached) ───────────────────────────

        static readonly Dictionary<string, Texture2D> s_tex = new Dictionary<string, Texture2D>();
        static readonly Dictionary<string, Texture2D> s_glyphs = new Dictionary<string, Texture2D>();

        /// A shipped glyph (Resources/MagicUI/<name>: white on transparent, tinted by the material). Licences:
        /// Resources/MagicUI/GLYPHS-LICENSE.md (Material Symbols, Apache 2.0). Null when it isn't there.
        public static Texture2D Glyph(string name)
        {
            if (s_glyphs.TryGetValue(name, out Texture2D t)) return t;
            try { t = Resources.Load<Texture2D>("MagicUI/" + name); } catch (Exception) { t = null; }
            if (t != null) t.wrapMode = TextureWrapMode.Clamp;
            s_glyphs[name] = t;
            return t;
        }

        public static Texture2D Tex(string key, int w, int h, Func<float, float, float> alpha)
        {
            if (s_tex.TryGetValue(key, out Texture2D t) && t != null) return t;
            t = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "Bookend" + key, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float a = Mathf.Clamp01(alpha((x + .5f) / w, (y + .5f) / h));
                    px[y * w + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            s_tex[key] = t;
            return t;
        }

        static float RoundBox(float u, float v, float corner)
        {
            float dx = Mathf.Max(0f, Mathf.Abs(u - .5f) - (0.5f - corner)), dy = Mathf.Max(0f, Mathf.Abs(v - .5f) - (0.5f - corner));
            return Mathf.Sqrt(dx * dx + dy * dy) - corner;   // < 0 inside
        }

        public static Texture2D SoftRoundTexture(float softness) => Tex("Round" + softness, 64, 64, (u, v) =>
            1f - Mathf.SmoothStep(-softness, 0f, RoundBox(u, v, 0.12f) + softness * 0.2f));

        public static Texture2D RimTexture() => Tex("Rim", 128, 128, (u, v) =>
        {
            float d = Mathf.Abs(RoundBox(u, v, 0.08f) + 0.01f);
            return Mathf.Exp(-d * d / (0.012f * 0.012f)) * 0.9f + Mathf.Exp(-d / 0.05f) * 0.15f;
        });

        public static Texture2D LineTexture() => Tex("Line", 64, 8, (u, v) =>
            Mathf.Sin(Mathf.PI * u) * Mathf.Exp(-Mathf.Pow((v - .5f) / 0.3f, 2f)));

        public static Texture2D RingTexture() => Tex("Ring", 128, 128, (u, v) =>
        {
            float r = Vector2.Distance(new Vector2(u, v), new Vector2(.5f, .5f)) * 2f;
            return r > 1f ? 0f : Mathf.Exp(-Mathf.Pow((r - 0.9f) / 0.035f, 2f)) + 0.05f;
        });

        public static Texture2D BeamTexture() => Tex("Beam", 32, 128, (u, v) =>
            Mathf.Pow(1f - v, 1.6f) * Mathf.Exp(-Mathf.Pow((u - .5f) / 0.22f, 2f)) * 0.8f);

        public static Texture2D EdgeTexture() => Tex("Edge", 64, 64, (u, v) =>
            Mathf.Exp(-Mathf.Min(Mathf.Min(u, 1f - u), Mathf.Min(v, 1f - v)) / 0.025f) * 0.9f);

        public static Texture2D SparkTexture() => Tex("Spark", 32, 32, (u, v) =>
        {
            float r = Vector2.Distance(new Vector2(u, v), new Vector2(.5f, .5f)) * 2f;
            return Mathf.Exp(-r * r * 4f);
        });

        public static Texture2D DiscTexture() => Tex("Disc", 128, 128, (u, v) =>
        {
            float r = Vector2.Distance(new Vector2(u, v), new Vector2(.5f, .5f)) * 2f;
            return 1f - Mathf.SmoothStep(0.96f, 1f, r);
        });

        public static Texture2D GlowDiscTexture() => Tex("GlowDisc", 64, 64, (u, v) =>
        {
            float r = Vector2.Distance(new Vector2(u, v), new Vector2(.5f, .5f)) * 2f;
            return r > 1f ? 0f : Mathf.Pow(1f - r, 1.8f);
        });

        /// A feathered glass panel: dense in the middle, dissolving to nothing at the edge (no hard slab).
        public static Texture2D FeatherTexture() => Tex("Feather2", 128, 128, (u, v) =>
        {
            // No silhouette: the glass thins out over the outer quarter of the board on every side.
            float d = RoundBox(u, v, 0.3f);
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(-d / 0.24f));
        });

        /// The inner light: a soft lilac bloom in the panel's heart.
        public static Texture2D InnerLightTexture() => Tex("Inner2", 64, 64, (u, v) =>
        {
            // A soft bloom that reaches exactly zero at the quad's edge (never a visible rectangle).
            float dx = (u - .5f) * 2f, dy = (v - .5f) * 2f;
            float r2 = dx * dx + dy * dy;
            float window = Mathf.Clamp01(1f - Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)));
            return Mathf.Exp(-r2 * 2.2f) * Mathf.SmoothStep(0f, 1f, window * 2.5f);
        });

        /// A soft diagonal band (the shimmer sweep across a board).
        public static Texture2D SheenTexture() => Tex("Sheen", 128, 32, (u, v) =>
        {
            float d = u - 0.5f + (v - 0.5f) * 0.25f;
            return Mathf.Exp(-d * d / 0.0025f) * Mathf.Sin(Mathf.PI * v);
        });

        /// Light rays (an earned badge, a countdown burst).
        public static Texture2D RaysTexture() => Tex("Rays", 128, 128, (u, v) =>
        {
            Vector2 p = new Vector2(u - .5f, v - .5f) * 2f;
            float r = p.magnitude, a = Mathf.Atan2(p.y, p.x);
            float rays = Mathf.Pow(Mathf.Abs(Mathf.Cos(a * 6f)), 18f) + 0.6f * Mathf.Pow(Mathf.Abs(Mathf.Cos(a * 6f + 0.26f)), 30f);
            return r > 1f ? 0f : rays * Mathf.Pow(1f - r, 1.4f) + Mathf.Exp(-r * r * 30f);
        });

        /// A soft glowing orb-pill (a choice): brightest in the middle, no hard border.
        public static Texture2D OrbPillTexture() => Tex("OrbPill", 128, 48, (u, v) =>
        {
            float dx = Mathf.Max(0f, Mathf.Abs(u - .5f) * 2f - (1f - 48f / 128f)) * (128f / 48f), dy = Mathf.Abs(v - .5f) * 2f;
            float r = Mathf.Sqrt(dx * dx + dy * dy);
            return Mathf.Clamp01(1f - r) * Mathf.Clamp01(1f - r) * 1.2f;
        });

        /// A padlock glyph (the locked badge mark).
        public static Texture2D LockTexture() => Tex("Lock", 64, 64, (u, v) =>
        {
            bool body = u > 0.24f && u < 0.76f && v > 0.12f && v < 0.56f;
            float sr = Vector2.Distance(new Vector2(u, v), new Vector2(0.5f, 0.56f));
            bool shackle = v >= 0.56f && sr > 0.15f && sr < 0.24f;
            bool hole = Vector2.Distance(new Vector2(u, v), new Vector2(0.5f, 0.36f)) < 0.06f;
            return (body && !hole) || shackle ? 1f : 0f;
        });

        /// The fallback badge art: a rosette (a badge with no icon still reads as a badge).
        public static Texture2D RosetteTexture() => Tex("Rosette", 128, 128, (u, v) =>
        {
            Vector2 p = new Vector2(u - .5f, v - .5f) * 2f;
            float r = p.magnitude, a = Mathf.Atan2(p.y, p.x);
            float petals = 0.82f + 0.08f * Mathf.Cos(a * 12f);
            float star = r < petals ? 1f : 0f;
            float inner = r < 0.5f ? (r > 0.42f ? 0.55f : 0.85f) : 1f;
            return star * inner;
        });
    }
}
