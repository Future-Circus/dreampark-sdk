using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace DreamPark.MagicUI
{
    /// <summary>
    /// THE MAGIC BOARD — the conjured information surface of DreamPark Magic UI (Oct 18 2026, Aidan: "the actual important UI isn't magical"). The information
    /// itself is light, in the attraction flourish's and Dreamie's visual language:
    ///   • the surface: feathered glass (no flat fill, no hard edge), an inner lilac light, a shimmer sweep,
    ///     edges that dissolve into sparkles, motes drifting round it;
    ///   • the draw-in: light traces the outline, the glass fills, then the text resolves character by
    ///     character behind a bright leading edge (<see cref="TextResolve"/>); the leave is a sparkle dissolve;
    ///   • the text: brand type (Zalando Sans Expanded / DM Sans) with a soft violet inner glow (TMP underlay)
    ///     that also keeps it readable over any background;
    ///   • list rows: a glowing tick that SPARKLES ON when a player is READY, and score bars that FILL WITH LIGHT;
    ///   • the countdown: each number bursts in light (a punch, rays, a glint burst).
    /// It faces its viewer (yaw only). <see cref="SetXray"/> is the readability fallback (§ 11.7).
    /// </summary>
    public sealed class MagicBoard : MonoBehaviour
    {
        public float width, height;
        public TextMeshPro title, headline, foot;
        public Transform content;          // stickers / rating live here (the rows area)
        public float contentTop;           // local y of the rows area's top

        readonly List<Material> _glass = new List<Material>();
        readonly List<float> _glassA = new List<float>();
        int _mainGlass = -1;

        float _glassSX = 1.08f, _glassSY = 1.1f;
        /// Spread the pane wider than the board (its feathered rim falls outside the words): a card read over a busy level.
        public void SetGlassSpread(float sx, float sy)
        {
            _glassSX = sx; _glassSY = sy;
            Transform g = transform.Find("Glass");
            if (g != null) g.localScale = new Vector3(width * sx, (height + _below) * sy, 1f);
        }

        /// A denser pane for a board that sits close over a busy world (the personal card): 0.7 by default.
        public void SetGlassDensity(float alpha)
        {
            if (_mainGlass < 0 || _mainGlass >= _glassA.Count) return;
            _glassA[_mainGlass] = Mathf.Clamp01(alpha);
        }
        Transform[] _edges = new Transform[4];
        Material _edgeMat, _sheenMat, _flashMat, _raysMat;
        Transform _sheen, _flash, _rays;
        ParticleSystem _edgeSparks, _motes, _burst;
        readonly List<Row> _rows = new List<Row>();
        float _bornAt, _leaveAt = -1f, _popAt = -10f, _nextSheen;
        string _lastHeadline;
        bool _xray;

        public bool Leaving => _leaveAt >= 0f;
        public bool Gone => _leaveAt >= 0f && Time.time - _leaveAt > 0.9f;

        // ── Build ───────────────────────────────────────────────────

        public static MagicBoard Create(Transform parent, float w, float h)
        {
            var go = new GameObject("Board");
            go.transform.SetParent(parent, false);
            go.AddComponent<MagicUIRoot>();
            var b = go.AddComponent<MagicBoard>();
            b.width = w; b.height = h;
            b.Build();
            // Every board is a panel drawn over walls, occluders and 3D objects (Aidan, Oct 21; MagicOnTop).
            b._onTop = MagicOnTop.Of(go.transform);
            if (b._onTop != null) b.SetXray(true);
            return b;
        }

        void Build()
        {
            Mesh q = MagicLook.Quad();
            Transform t = transform;
            float w = width, h = height;
            if (MagicLook.Holo != null)
            {
                // Halo: a wide soft violet bloom behind (drawn first), the light the glass floats in.
                Glass(MagicLook.Plate(t, "Halo", q, MagicLook.HoloMat(MagicLook.InnerLightTexture(),
                    new Color(MagicLook.Violet.r, MagicLook.Violet.g, MagicLook.Violet.b, 0.32f), true, MagicLook.QGlass - 3),
                    new Vector3(0, 0, 0.02f), Quaternion.identity, new Vector3(w * 1.5f, h * 1.6f, 1)));
                // Feathered glass: dark violet, dense in the middle, dissolving at the edge.
                _mainGlass = _glass.Count;
                Glass(MagicLook.Plate(t, "Glass", q, MagicLook.HoloMat(MagicLook.FeatherTexture(),
                    new Color(0.05f, 0.02f, 0.11f, 0.7f), false, MagicLook.QGlass),
                    new Vector3(0, 0, 0.008f), Quaternion.identity, new Vector3(w * 1.08f, h * 1.1f, 1)));
                // Inner light: a lilac glow in its heart.
                Glass(MagicLook.Plate(t, "InnerLight", q, MagicLook.HoloMat(MagicLook.InnerLightTexture(),
                    new Color(MagicLook.Lilac.r, MagicLook.Lilac.g, MagicLook.Lilac.b, 0.10f), true, MagicLook.QGlass + 1),
                    new Vector3(0, 0, 0.006f), Quaternion.identity, new Vector3(w * 0.95f, h * 0.9f, 1)));
                // The outline of light (four strips; the draw-in traces them, then they stay as a soft edge).
                _edgeMat = MagicLook.HoloMat(MagicLook.LineTexture(), new Color(MagicLook.VioletBright.r, MagicLook.VioletBright.g, MagicLook.VioletBright.b, 0.6f), true, MagicLook.QGlow);
                var edges = new[] { (new Vector3(0, h * 0.5f, 0), 0f, w), (new Vector3(w * 0.5f, 0, 0), 90f, h), (new Vector3(0, -h * 0.5f, 0), 180f, w), (new Vector3(-w * 0.5f, 0, 0), 270f, h) };
                for (int i = 0; i < 4; i++)
                {
                    var (pos, rot, len) = edges[i];
                    var e = new GameObject("Edge" + i).transform;
                    e.SetParent(t, false);
                    e.localPosition = pos;
                    e.localRotation = Quaternion.Euler(0, 0, rot);
                    // Each trace fades toward the corners (LineTexture), so the outline never closes into a frame.
                    MagicLook.Plate(e, "Light", q, _edgeMat, new Vector3(0, 0, 0.004f), Quaternion.identity, new Vector3(len * 0.8f, 0.022f, 1));
                    _edges[i] = e;
                }
                // The shimmer sweep.
                _sheenMat = MagicLook.HoloMat(MagicLook.SheenTexture(), new Color(1f, 0.96f, 1f, 0.22f), true, MagicLook.QGlass + 2);
                _sheen = MagicLook.Plate(t, "Sheen", q, _sheenMat, new Vector3(0, 0, 0.005f), Quaternion.identity, new Vector3(w, h, 1)).transform;
                // The countdown's burst of light (flash + rays behind the headline).
                _flashMat = MagicLook.HoloMat(MagicLook.GlowDiscTexture(), MagicLook.VioletBright, true, MagicLook.QGlow + 1);
                _flash = MagicLook.Plate(t, "Flash", q, _flashMat, new Vector3(0, 0, 0.003f), Quaternion.identity, Vector3.zero).transform;
                _raysMat = MagicLook.HoloMat(MagicLook.RaysTexture(), MagicLook.Lilac, true, MagicLook.QGlow + 1);
                _rays = MagicLook.Plate(t, "Rays", q, _raysMat, new Vector3(0, 0, 0.003f), Quaternion.identity, Vector3.zero).transform;
            }
            // Edges that dissolve into sparkles, and motes drifting round the board.
            _edgeSparks = EdgeSparkles(t, w, h);
            _motes = MagicLook.Sparkles(t, "Motes", MagicLook.FlourishOrb, 0.5f * Mathf.Max(w, h), rate: 6f, rise: 0.05f, life: 4f,
                size: new Vector2(0.025f, 0.05f), edgeOnly: false);
            _motes.transform.localRotation = Quaternion.identity;
            var sh = _motes.shape; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(w + 0.5f, h + 0.5f, 0.1f);
            // Motes drift BEHIND the board (never over its text or its cards' art): behind its plane, and drawn before its
            // glass (their own material instance, so other sparkles keep their layer).
            sh.position = new Vector3(0f, 0f, 0.09f);
            var mr = _motes.GetComponent<ParticleSystemRenderer>();
            if (mr != null && mr.sharedMaterial != null) mr.sharedMaterial = new Material(mr.sharedMaterial) { renderQueue = MagicLook.QGlass - 4 };
            _burst = MagicLook.Burst(t, 0.1f);
            _burst.transform.localRotation = Quaternion.identity;

            title = MagicLook.Label(t, "Title", 60f, MagicLook.Lilac, w - 0.16f, display: false);
            title.characterSpacing = 6f;
            title.transform.localPosition = new Vector3(0, h * 0.5f - 0.1f, 0);
            headline = MagicLook.Label(t, "Headline", 100f, Color.white, w - 0.12f, display: true);
            headline.enableAutoSizing = true; headline.fontSizeMin = 56f; headline.fontSizeMax = 100f;
            headline.rectTransform.sizeDelta = new Vector2((w - 0.14f) / MagicLook.TextUnitScale, 16f);
            headline.transform.localPosition = new Vector3(0, h * 0.5f - 0.26f, 0);
            foot = MagicLook.Label(t, "Foot", 58f, MagicLook.TextSecondary, w - 0.16f);
            foot.textWrappingMode = TextWrappingModes.Normal;
            foot.rectTransform.sizeDelta = new Vector2((w - 0.18f) / MagicLook.TextUnitScale, 14f);
            foot.transform.localPosition = new Vector3(0, -h * 0.5f + 0.1f, 0);
            foreach (var l in new[] { title, headline, foot }) l.gameObject.AddComponent<TextResolve>();
            contentTop = h * 0.5f - 0.4f;
            content = new GameObject("Content").transform;
            content.SetParent(t, false);
            content.localPosition = new Vector3(0, contentTop, -0.01f);
            _bornAt = Time.time;
            Tick(0f);
        }

        void Glass(Renderer r)
        {
            if (r?.sharedMaterial == null) return;
            _glass.Add(r.sharedMaterial);
            _glassA.Add(r.sharedMaterial.GetColor("_BaseColor").a);
        }

        float _below;
        /// How far the board currently reaches below its built height (metres).
        public float Below => _below;

        /// Grow the board DOWNWARD by <paramref name="extra"/> metres (0 = back to its built size): the glass, halo,
        /// inner light, sheen, the outline of light and its sparkles all follow; the top (title, headline) stays put.
        public void SetBelow(float extra)
        {
            extra = Mathf.Max(0f, extra);
            if (Mathf.Abs(extra - _below) < 0.001f) return;
            _below = extra;
            float w = width, h = height + extra, cy = -extra * 0.5f;
            void Fit(string name, float sx, float sy)
            {
                Transform p = transform.Find(name);
                if (p == null) return;
                p.localPosition = new Vector3(0f, cy, p.localPosition.z);
                p.localScale = new Vector3(w * sx, h * sy, 1f);
            }
            Fit("Halo", 1.5f, 1.6f); Fit("Glass", _glassSX, _glassSY); Fit("InnerLight", 0.95f, 0.9f); Fit("Sheen", 1f, 1f);
            var edges = new[] { (new Vector3(0, height * 0.5f, 0), w), (new Vector3(w * 0.5f, cy, 0), h), (new Vector3(0, -height * 0.5f - extra, 0), w), (new Vector3(-w * 0.5f, cy, 0), h) };
            for (int i = 0; i < 4; i++)
            {
                if (_edges[i] == null) continue;
                _edges[i].localPosition = edges[i].Item1;
                Transform light = _edges[i].Find("Light");
                if (light != null) light.localScale = new Vector3(edges[i].Item2 * 0.8f, light.localScale.y, 1f);
            }
            if (_edgeSparks != null)
            {
                _edgeSparks.transform.localPosition = new Vector3(0f, cy, 0f);
                var sh = _edgeSparks.shape; sh.scale = new Vector3(w * 1.02f, h * 1.02f, 0f);
            }
            if (_motes != null) { var sh = _motes.shape; sh.scale = new Vector3(w + 0.5f, h + 0.5f, 0.1f); sh.position = new Vector3(0f, cy, 0.09f); }
        }

        static ParticleSystem EdgeSparkles(Transform parent, float w, float h)
        {
            ParticleSystem ps = MagicLook.Sparkles(parent, "EdgeSparkles", MagicLook.FlourishGlint, 0.5f, rate: 14f, rise: 0.04f, life: 1.4f,
                size: new Vector2(0.03f, 0.075f), edgeOnly: true);
            ps.transform.localRotation = Quaternion.identity;
            var sh = ps.shape;
            sh.shapeType = ParticleSystemShapeType.BoxEdge;
            sh.scale = new Vector3(w * 1.02f, h * 1.02f, 0f);
            return ps;
        }

        // ── Content ─────────────────────────────────────────────────

        public void SetTitle(string s) => Set(title, s);
        public void SetFoot(string s) => Set(foot, s);

        /// The headline; a countdown number ("3", "2", "1", "GO!") bursts in light.
        public void SetHeadline(string s)
        {
            s = s ?? "";
            if (s == _lastHeadline) return;
            _lastHeadline = s;
            Set(headline, s);
            bool count = s.Length > 0 && s.Length <= 3 && (char.IsDigit(s[0]) || s == "GO!");
            if (count) Pop(headline.transform.localPosition);
        }

        /// A burst of light at a local point: punch, flash, rays, glints.
        public void Pop(Vector3 at, bool small = false)
        {
            if (small)
            {
                if (_burst != null) _burst.Emit(new ParticleSystem.EmitParams { position = transform.TransformPoint(at), applyShapeToPosition = true }, 14);
                return;
            }
            _popAt = Time.time;
            if (_flash != null) _flash.localPosition = new Vector3(at.x, at.y, 0.003f);
            if (_rays != null) _rays.localPosition = new Vector3(at.x, at.y, 0.0035f);
            if (_burst != null)
            {
                var ep = new ParticleSystem.EmitParams { position = transform.TransformPoint(at), applyShapeToPosition = true };
                _burst.Emit(ep, 40);
            }
        }

        static void Set(TextMeshPro t, string s)
        {
            if (t == null) return;
            s = s ?? "";
            if (t.text == s) return;
            // A ticking number ("starts in 43s" → "42s") just updates; new words resolve in again.
            bool tick = TextResolve.OnlyDigitsDiffer(t.text, s);
            t.text = s;
            if (!tick) t.GetComponent<TextResolve>()?.Restart();
        }

        /// The list (WHO'S PLAYING / the scores / standings / the team result). `on` = the tick lit (READY,
        /// cleared); `fill` 0..1 = a bar of light (a score against the best). Up to 6 rows in one column; 7–12 in
        /// two; more → the top 11 + this device's own row. The viewer's own row (<paramref name="myId"/>) is
        /// always highlighted, whatever its place. CPU entrants carry a CPU tag and their colour.
        public void SetRows(IList<MagicRow> rows, bool bars, string myId = null)
        {
            var list = new List<MagicRow>(rows ?? new List<MagicRow>());
            if (list.Count > 12)
            {
                int mine = list.FindIndex(r => !string.IsNullOrEmpty(myId) && r.id == myId);
                var top = list.GetRange(0, mine >= 11 ? 11 : 12);
                if (mine >= 11) top.Add(list[mine]);
                list = top;
            }
            int n = list.Count;
            int cols = n > 6 ? 2 : 1;
            int perCol = cols == 2 ? Mathf.CeilToInt(n / 2f) : n;
            float colW = cols == 2 ? (width - 0.12f) * 0.5f : width - 0.1f;
            float rowH = cols == 2 || n > 5 ? 0.1f : 0.12f;
            while (_rows.Count < n) _rows.Add(new Row(this, _rows.Count));
            for (int i = 0; i < _rows.Count; i++)
            {
                bool show = i < n;
                _rows[i].root.gameObject.SetActive(show);
                if (!show) continue;
                int c = i / Mathf.Max(1, perCol), r = i % Mathf.Max(1, perCol);
                float x = cols == 2 ? (c == 0 ? -1f : 1f) * (colW * 0.5f + 0.02f) : 0f;
                _rows[i].Layout(new Vector3(x, -r * rowH, 0), colW, rowH, cols == 2);
                _rows[i].Set(list[i], bars, !string.IsNullOrEmpty(myId) && list[i].id == myId);
            }
        }

        public void ShowRows(bool on) { foreach (Row r in _rows) if (!on) r.root.gameObject.SetActive(false); }

        // ── Life ────────────────────────────────────────────────────

        // ── The objective cards' sparkle (Oct 19, Aidan: "more like sparkles … match the nice particles in the other
        // playlist UI", and "a particle burst that fades out") — opt-in, so Results / What's Next boards keep theirs. ──

        /// Pop exit: the panel is gone in ~0.08 s and bursts outward into star glints (radial, slowing with drag,
        /// shrinking to nothing over 0.6–0.9 s). Capped at <see cref="PopCount"/> particles.
        public bool PopExit;
        public const int PopCount = 90;
        public const float PopFadeS = 0.08f;

        /// The card's ambient sparkle in the platform's flourish look (the lock field's / bounce-in glints): crisp
        /// twinkling stars, brighter, still drawn behind the words.
        public void UseCardSparkles()
        {
            foreach (var ps in new[] { _motes, _edgeSparks })
            {
                if (ps == null) continue;
                var r = ps.GetComponent<ParticleSystemRenderer>();
                Material glint = MagicLook.Flourish(MagicLook.FlourishGlint);
                if (r != null && glint != null)
                    r.sharedMaterial = ps == _motes ? new Material(glint) { renderQueue = MagicLook.QGlass - 4 } : glint;
                var main = ps.main;
                main.startColor = MagicLook.BrandGradient(true);
                main.startSize = ps == _motes ? new ParticleSystem.MinMaxCurve(0.035f, 0.075f) : new ParticleSystem.MinMaxCurve(0.04f, 0.09f);
                var em = ps.emission; em.rateOverTime = ps == _motes ? 7f : 12f;
                var sz = ps.sizeOverLifetime; sz.enabled = true;   // the twinkle: up, a flicker, out
                sz.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.15f, 1f),
                    new Keyframe(0.35f, 0.55f), new Keyframe(0.5f, 1f), new Keyframe(1f, 0f)));
            }
        }

        void PopBurst()
        {
            if (_burst == null) return;
            var main = _burst.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 0.9f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.12f);
            main.startColor = MagicLook.BrandGradient(true);
            main.maxParticles = Mathf.Max(main.maxParticles, PopCount);
            var lv = _burst.limitVelocityOverLifetime; lv.enabled = true; lv.drag = 3.5f; lv.multiplyDragByParticleSize = false;
            var sz = _burst.sizeOverLifetime; sz.enabled = true;
            sz.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.25f, 0.85f), new Keyframe(1f, 0f)));
            Vector3 c = transform.position, right = transform.right, up = transform.up, fwd = -transform.forward;
            float w = width * transform.lossyScale.x, h = (height + _below) * transform.lossyScale.y;
            for (int i = 0; i < PopCount; i++)
            {
                // From across the card's face, flying out from its middle (a little toward the viewer).
                float x = UnityEngine.Random.Range(-0.5f, 0.5f), y = UnityEngine.Random.Range(-0.5f, 0.5f);
                Vector3 p = c + right * (x * w) + up * (y * h - _below * transform.lossyScale.y * 0.5f);
                Vector3 dir = (right * x * w + up * y * h).normalized + fwd * UnityEngine.Random.Range(0f, 0.35f) + UnityEngine.Random.insideUnitSphere * 0.25f;
                _burst.Emit(new ParticleSystem.EmitParams { position = p, velocity = dir.normalized * UnityEngine.Random.Range(1.1f, 2.6f), applyShapeToPosition = false }, 1);
            }
        }

        public void Leave()
        {
            if (_leaveAt >= 0f) return;
            _leaveAt = Time.time;
            if (PopExit) { PopBurst(); if (_motes != null) _motes.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); if (_edgeSparks != null) _edgeSparks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); return; }
            // The sparkle dissolve: the whole board breaks into glints.
            if (_burst != null)
            {
                var sh = _burst.shape; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(width, height, 0.05f);
                var ep = new ParticleSystem.EmitParams { applyShapeToPosition = true };
                _burst.Emit(ep, 140);
            }
        }

        MagicOnTop _onTop;

        public void SetXray(bool on)
        {
            // On top whenever it stands in the viewer's room (a sight check finding it clear never turns that off); in another
            // room it keeps normal depth, whatever the sight check says (Aidan, Oct 22: no panels visible between rooms).
            if (_onTop != null) on = _onTop.Active;
            if (on == _xray) return;
            _xray = on;
            MagicLook.SetXray(transform, on);
        }

        float _xrayAgain;
        int _xrayCount = -1;

        void Update()
        {
            Tick(Time.time - _bornAt);
            // X-ray covers what was added AFTER it was switched on (rows, stickers, choices, a countdown): without
            // this, new rows stayed depth-tested and hid behind the very geometry the x-ray was for (Oct 18).
            if (_xray && Time.time >= _xrayAgain)
            {
                _xrayAgain = Time.time + 0.25f;
                int n = transform.hierarchyCount;
                if (n != _xrayCount) { _xrayCount = n; MagicLook.SetXray(transform, true); }
            }
        }

        void Tick(float t)
        {
            // Draw-in: 0–0.45 s light traces the outline; 0.3–0.7 s the glass fills; text resolves from 0.5 s.
            float trace = Mathf.Clamp01(t / 0.45f), fill = Mathf.Clamp01((t - 0.3f) / 0.4f);
            float leave = _leaveAt >= 0f ? Mathf.Clamp01((Time.time - _leaveAt) / (PopExit ? PopFadeS : 0.6f)) : 0f;
            for (int i = 0; i < 4; i++)
            {
                if (_edges[i] == null) continue;
                float k = Mathf.Clamp01(trace * 4f - i);   // one edge after another
                _edges[i].localScale = new Vector3(Mathf.Max(0.001f, k), 1f, 1f);
            }
            float vis = Mathf.SmoothStep(0f, 1f, fill) * (1f - leave);
            for (int i = 0; i < _glass.Count; i++)
            {
                Color c = _glass[i].GetColor("_BaseColor"); c.a = _glassA[i] * vis; _glass[i].SetColor("_BaseColor", c);
            }
            if (_edgeMat != null)
            {
                Color c = _edgeMat.GetColor("_BaseColor");
                c.a = (0.38f + 0.2f * Mathf.Sin(Time.time * 1.7f)) * (1f - leave) * (trace > 0f ? 1f : 0f) + (trace < 1f ? 0.5f * (1f - trace) : 0f);
                _edgeMat.SetColor("_BaseColor", c);
            }
            foreach (var l in new[] { title, headline, foot })
                if (l != null) { var tr = l.GetComponent<TextResolve>(); if (tr != null) tr.Gate = t >= 0.5f && leave <= 0f; if (leave > 0f) l.alpha = 1f - leave; }
            // The shimmer sweep, every ~5 s, gentle.
            if (_sheenMat != null)
            {
                if (Time.time >= _nextSheen) _nextSheen = Time.time + 5f;
                float u = 1f - (_nextSheen - Time.time) / 1.6f;   // the sweep plays in the last 1.6 s of the cycle
                _sheenMat.mainTextureOffset = new Vector2(Mathf.Lerp(0.7f, -0.7f, Mathf.Clamp01(u)), 0f);
                Color c = _sheenMat.GetColor("_BaseColor"); c.a = u >= 0f && u <= 1f ? 0.22f * vis : 0f; _sheenMat.SetColor("_BaseColor", c);
            }
            // The countdown burst: a punch on the headline, a flash and rays that bloom and fade.
            float p = Time.time - _popAt;
            if (headline != null) headline.transform.localScale = Vector3.one * MagicLook.TextUnitScale * (p < 0.35f ? 1f + 0.35f * (1f - p / 0.35f) * (1f - p / 0.35f) : 1f);
            if (_flash != null)
            {
                float k = p < 0.6f ? 1f - p / 0.6f : 0f;
                _flash.localScale = Vector3.one * (0.3f + 0.9f * (1f - k)) * (k > 0f ? 1f : 0f);
                Color c = _flashMat.GetColor("_BaseColor"); c.a = 0.7f * k; _flashMat.SetColor("_BaseColor", c);
                _rays.localScale = Vector3.one * (0.4f + 1.1f * (1f - k)) * (k > 0f ? 1f : 0f);
                _rays.localRotation = Quaternion.Euler(0, 0, p * 40f);
                Color r = _raysMat.GetColor("_BaseColor"); r.a = 0.8f * k; _raysMat.SetColor("_BaseColor", r);
            }
            foreach (Row r in _rows) r.Tick();
            if (_leaveAt >= 0f)
            {
                if (_edgeSparks != null) { var em = _edgeSparks.emission; em.enabled = false; }
                if (_motes != null) { var em = _motes.emission; em.enabled = false; }
            }
        }

        // ── A list row ──────────────────────────────────────────────

        sealed class Row
        {
            public readonly Transform root;
            readonly MagicBoard _b;
            readonly TextMeshPro _rank, _left, _right, _tag;
            readonly Transform _bar, _tick, _me;
            readonly Material _barMat, _tickMat, _meMat;
            readonly MagicAvatar _face;
            bool _hasFace;
            int _rankNow;
            bool _barsNow;
            float _fill, _target, _w = 1f, _h = 0.12f, _nameX, _nameW;
            bool _on, _started, _isMe, _compact;
            Color _dot = Color.clear;
            string _key;

            public Row(MagicBoard b, int index)
            {
                _b = b;
                root = new GameObject("Row" + index).transform;
                root.SetParent(b.content, false);
                Mesh q = MagicLook.Quad();
                if (MagicLook.Holo != null)
                {
                    _meMat = MagicLook.HoloMat(MagicLook.OrbPillTexture(), new Color(MagicLook.VioletBright.r, MagicLook.VioletBright.g, MagicLook.VioletBright.b, 0f), true, MagicLook.QGlass + 2);
                    _me = MagicLook.Plate(root, "Me", q, _meMat, new Vector3(0, 0, 0.003f), Quaternion.identity, Vector3.one).transform;
                    _barMat = MagicLook.HoloMat(MagicLook.LineTexture(), new Color(MagicLook.Violet.r, MagicLook.Violet.g, MagicLook.Violet.b, 0.55f), true, MagicLook.QGlass + 3);
                    _bar = MagicLook.Plate(root, "Bar", q, _barMat, new Vector3(0, 0, 0.002f), Quaternion.identity, new Vector3(0.001f, 0.09f, 1)).transform;
                    _tickMat = MagicLook.HoloMat(MagicLook.GlowDiscTexture(), MagicLook.VioletBright, true, MagicLook.QGlow + 2);
                    _tick = MagicLook.Plate(root, "Tick", q, _tickMat, Vector3.zero, Quaternion.identity, Vector3.one * 0.07f).transform;
                }
                // A player's (or CPU's) face left of the name (Oct 18): rows with an entrant id.
                _face = new MagicAvatar(root, "Face", Vector3.zero, 0.085f);
                _face.Active = false;
                _rank = MagicLook.Label(root, "Rank", 60f, MagicLook.Lilac, 0.14f, display: true);
                _left = MagicLook.Label(root, "Left", 62f, Color.white, 0.8f);
                _left.alignment = TextAlignmentOptions.Left;
                _left.overflowMode = TextOverflowModes.Ellipsis;
                _tag = MagicLook.Label(root, "Tag", 42f, MagicLook.Lilac, 0.3f);
                _tag.alignment = TextAlignmentOptions.Left;
                _right = MagicLook.Label(root, "Right", 62f, MagicLook.Lilac, 0.4f, display: true);
                _right.alignment = TextAlignmentOptions.Right;
                _right.enableAutoSizing = true; _right.fontSizeMin = 34f; _right.fontSizeMax = 62f;   // per-race places fit
                foreach (var l in new[] { _rank, _left, _right }) l.gameObject.AddComponent<TextResolve>();
            }

            public void Layout(Vector3 at, float w, float h, bool compact)
            {
                root.localPosition = at;
                _w = w; _h = h; _compact = compact;
                float fs = compact ? 50f : 62f;
                _left.fontSize = fs; _right.fontSize = fs; _rank.fontSize = fs;
                float x0 = -w * 0.5f;
                _rank.rectTransform.sizeDelta = new Vector2(0.12f / MagicLook.TextUnitScale, 12f);
                _rank.transform.localPosition = new Vector3(x0 + 0.07f, 0, 0);
                if (_tick != null) _tick.localPosition = new Vector3(x0 + 0.17f, 0, 0);
                float face = Mathf.Min(h * 0.82f, compact ? 0.078f : 0.095f);
                _face.root.localPosition = new Vector3(x0 + 0.18f, 0, -0.001f);
                _face.root.localScale = Vector3.one * face;
                float nameX = x0 + 0.245f;
                float nameW = w * (compact ? 0.5f : 0.56f);
                _left.rectTransform.sizeDelta = new Vector2(nameW / MagicLook.TextUnitScale, 12f);
                _left.transform.localPosition = new Vector3(nameX + nameW * 0.5f, 0, 0);
                _nameX = nameX; _nameW = nameW;
                PlaceTag();
                float rw = w * 0.28f;
                _right.rectTransform.sizeDelta = new Vector2(rw / MagicLook.TextUnitScale, 12f);
                _right.transform.localPosition = new Vector3(w * 0.5f - 0.04f - rw * 0.5f, 0, 0);
                if (_me != null) _me.localScale = new Vector3(w + 0.04f, h * 0.95f, 1f);
            }

            public void Set(MagicRow r, bool bars, bool isMe)
            {
                string key = r.rank + "|" + r.left + "|" + r.right;
                if (key != _key)
                {
                    _key = key;
                    Set2(_rank, r.rank > 0 ? r.rank.ToString() : "");
                    Set2(_left, r.left);
                    Set2(_right, r.right);
                    PlaceTag();
                }
                string tagText = MagicRow.TagText(r.kind, r.tag, r.champion);
                if (_tag.text != tagText) { Set2(_tag, tagText); PlaceTag(); }
                _tag.color = r.champion ? MagicLook.Gold : MagicLook.Lilac;
                // The face: a row for an entrant (player / CPU) shows their avatar where the tick was.
                _hasFace = !string.IsNullOrEmpty(r.id);
                _face.Active = _hasFace;
                if (_tick != null) _tick.gameObject.SetActive(!_hasFace);
                if (_hasFace) _face.Set(r.avatar, r.left, r.kind, r.color, r.id);
                _rankNow = r.rank; _barsNow = bars;
                _target = bars ? Mathf.Clamp01(r.fill) : 0f;
                if (r.on && !_on && _started) Sparkle();
                if (isMe && !_isMe && _started) Sparkle();
                _on = r.on; _isMe = isMe;
                _started = true;
                _dot = ColorUtility.TryParseHtmlString(r.color ?? "", out Color c) ? c : Color.clear;
                Color medal = r.rank == 1 ? MagicLook.Gold : r.rank == 2 ? new Color(0.85f, 0.87f, 0.93f) : r.rank == 3 ? new Color(0.82f, 0.54f, 0.31f) : MagicLook.TextSecondary;
                _rank.color = medal;
                _left.color = isMe ? Color.white : r.on || bars ? new Color(0.94f, 0.92f, 1f) : MagicLook.TextSecondary;
                _left.fontStyle = isMe ? FontStyles.Bold : FontStyles.Normal;
                _right.color = r.on ? (bars ? Color.white : MagicLook.VioletBright) : MagicLook.TextSecondary;
            }

            /// The CPU tag sits right after the name (a chip), never under the score column: a long name gives way to it.
            void PlaceTag()
            {
                if (_nameW <= 0f) return;
                float tagW = string.IsNullOrEmpty(_tag.text) ? 0f : _tag.GetPreferredValues(_tag.text).x * MagicLook.TextUnitScale;
                float room = Mathf.Max(0.1f, _nameW - (tagW > 0f ? tagW + 0.03f : 0f));
                _left.rectTransform.sizeDelta = new Vector2(room / MagicLook.TextUnitScale, 12f);
                _left.transform.localPosition = new Vector3(_nameX + room * 0.5f, 0, 0);
                float nameEnd = string.IsNullOrEmpty(_left.text) ? 0f : Mathf.Min(room, _left.GetPreferredValues(_left.text).x * MagicLook.TextUnitScale);
                _tag.rectTransform.sizeDelta = new Vector2(Mathf.Max(0.05f, tagW + 0.01f) / MagicLook.TextUnitScale, 12f);
                _tag.transform.localPosition = new Vector3(_nameX + nameEnd + 0.03f + tagW * 0.5f, -0.004f, 0);
            }

            static void Set2(TextMeshPro t, string s)
            {
                s = s ?? "";
                if (t.text == s) return;
                t.text = s;
                t.GetComponent<TextResolve>()?.Restart();
            }

            void Sparkle()
            {
                if (_tick == null) return;
                _b.Pop(root.localPosition + new Vector3(_tick.localPosition.x, _b.contentTop, 0), small: true);
            }

            public void Tick()
            {
                // The bar fills with light (eased), brighter at its leading edge while it fills.
                _fill = Mathf.MoveTowards(_fill, _target, Time.deltaTime * 0.8f);
                if (_bar != null)
                {
                    float x0 = -_w * 0.5f + 0.23f, full = _w - 0.27f;
                    _bar.localScale = new Vector3(Mathf.Max(0.001f, full * _fill), _h * 0.75f, 1f);
                    _bar.localPosition = new Vector3(x0 + full * _fill * 0.5f, 0, 0.002f);
                    Color c = _barMat.GetColor("_BaseColor"); c.a = _fill > 0.001f ? 0.42f + (Mathf.Abs(_fill - _target) > 0.01f ? 0.3f : 0f) : 0f; _barMat.SetColor("_BaseColor", c);
                }
                if (_hasFace)
                {
                    // The ring: READY lights it (breathing); scores give the top 3 their medal; the viewer's own row
                    // is brightest; otherwise a quiet lilac.
                    Color medal = _rankNow == 1 ? MagicLook.Gold : _rankNow == 2 ? new Color(0.85f, 0.87f, 0.93f) : _rankNow == 3 ? new Color(0.82f, 0.54f, 0.31f) : MagicLook.Lilac;
                    bool ranked = _barsNow && _rankNow >= 1 && _rankNow <= 3;
                    Color c = ranked ? medal : _on && !_barsNow ? MagicLook.VioletBright : _isMe ? MagicLook.VioletBright : MagicLook.Lilac;
                    float k = ranked ? 0.95f : _on && !_barsNow ? 1f : _isMe ? 0.9f : 0.35f;
                    _face.SetRing(c, k);
                    _face.Tick(_on && !_barsNow ? 0.85f + 0.15f * Mathf.Sin(Time.time * 3f + root.localPosition.y * 9f) : 1f);
                }
                else if (_tick != null)
                {
                    bool lit = _on || _dot.a > 0f;
                    float pulse = _on ? 0.85f + 0.15f * Mathf.Sin(Time.time * 3f + root.localPosition.y * 9f) : 1f;
                    _tick.localScale = Vector3.one * (lit ? 0.08f : 0.05f) * pulse * (_compact ? 0.85f : 1f);
                    Color c = _dot.a > 0f ? _dot : _on ? MagicLook.VioletBright : MagicLook.TextSecondary;
                    c.a = lit ? 0.95f : 0.35f;
                    _tickMat.SetColor("_BaseColor", c);
                }
                if (_meMat != null)
                {
                    // The viewer's own row: a soft orb of light behind it, breathing.
                    Color c = _meMat.GetColor("_BaseColor");
                    c.a = _isMe ? 0.32f + 0.1f * Mathf.Sin(Time.time * 2.2f) : 0f;
                    _meMat.SetColor("_BaseColor", c);
                }
            }
        }
    }

    /// <summary>One row of a board list (WHO'S PLAYING / scores / the team result).</summary>
    public struct MagicRow
    {
        /// The entrant (player id, or "cpu:<name>" for a CPU racer), for the viewer's own-row highlight.
        public string id;
        public int rank;            // 0 = no rank shown
        public string left, right;  // name, value (score / time / READY)
        public float fill;          // 0..1: a bar of light
        public bool on;             // the tick lit (READY / cleared)
        public string kind;         // "player" (default) | "cpu"
        public string color;        // "#RRGGBB" entrant colour (a CPU's livery / a player's avatar ring), optional
        /// The entrant's profile photo URL (a player's public avatar, a CPU's own art); empty → the initial
        /// (guest) or the livery disc (CPU). A row with an <see cref="id"/> shows a <see cref="MagicAvatar"/>.
        public string avatar;
        /// A CPU's small tag ("CPU" when empty; DreamSports "DreamPal") and its champion flag (a gold CHAMPION tag),
        /// from the title's declared cast (Docs/CPU_PLAYERS.md).
        public string tag;
        public bool champion;
        public MagicRow(string left, string right, float fill, bool on)
        { id = null; rank = 0; this.left = left; this.right = right; this.fill = fill; this.on = on; kind = null; color = null; avatar = null; tag = null; champion = false; }

        /// The tag a CPU row shows ("" for a player): plain text after the name, items split by " | " (Aidan, Oct 19:
        /// never a pill / capsule / chip), e.g. "DREAMPAL" or "DREAMPAL | CHAMPION".
        public static string TagText(string kind, string tag, bool champion)
        {
            if (kind != "cpu") return "";
            string t = string.IsNullOrEmpty(tag) ? "CPU" : tag.ToUpperInvariant();
            return champion ? t + " | CHAMPION" : t;
        }
    }

    /// <summary>
    /// Characters resolve in behind a bright leading edge of light (left to right, ~110 characters a second),
    /// then settle to their colour. Works on any TMP label; restarts when its text changes.
    /// </summary>
    public sealed class TextResolve : MonoBehaviour
    {
        // Fast enough that a goal line is whole within ~0.3 s (Oct 19 recut: a half-revealed "Find the 12 hi…" read as
        // truncated): the light still sweeps, the words are never caught half-there.
        public float charsPerSecond = 110f;
        public bool Gate = true;
        TMP_Text _t;
        float _startAt = -1f;
        bool _done;

        void Awake() { _t = GetComponent<TMP_Text>(); Restart(); }

        public void Restart() { _startAt = -1f; _done = false; }

        public static bool OnlyDigitsDiffer(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            string Strip(string x) { var sb = new System.Text.StringBuilder(); foreach (char c in x) if (!char.IsDigit(c)) sb.Append(c); return sb.ToString(); }
            return Strip(a) == Strip(b);
        }

        void LateUpdate()
        {
            if (_t == null || _done) return;
            if (!Gate) { _t.alpha = 0f; return; }
            // Unscaled: a paused or slowed game (timeScale 0) must never freeze a label mid-sweep ("How" of "How was it?").
            if (_startAt < 0f) { _startAt = Time.unscaledTime; _t.alpha = 1f; }
            _t.ForceMeshUpdate();
            TMP_TextInfo info = _t.textInfo;
            float head = (Time.unscaledTime - _startAt) * charsPerSecond;
            bool all = true;
            for (int i = 0; i < info.characterCount; i++)
            {
                TMP_CharacterInfo ch = info.characterInfo[i];
                if (!ch.isVisible) continue;
                float k = Mathf.Clamp01(head - i);             // 0 → 1 as the edge passes
                float glow = Mathf.Clamp01(1f - Mathf.Abs(head - i - 0.5f) / 2f);   // the bright leading edge
                if (k < 1f || glow > 0f) all = false;
                Color32[] cols = info.meshInfo[ch.materialReferenceIndex].colors32;
                for (int v = 0; v < 4; v++)
                {
                    Color32 c = cols[ch.vertexIndex + v];
                    Color lit = Color.Lerp((Color)c, new Color(1f, 0.95f, 1f, c.a / 255f), glow);
                    lit.a = (c.a / 255f) * k;
                    cols[ch.vertexIndex + v] = lit;
                }
            }
            _t.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
            if (all && head > info.characterCount + 2) _done = true;
        }
    }
}
