// ─────────────────────────────────────────────────────────────────────
//  PlaylistLockField.cs — the LOCKED force field over a future round's
//  stop (whole-arc playlists, Docs/PLAYLISTS.md § 7.10), in the Magic UI
//  light language (MagicLook: HoloParticle light, flourish glints, brand
//  violet / lilac). Oct 19 redesign (Aidan): no dome — the field IS the
//  attraction's own square footprint, so in a house it lines up with the
//  room it was seated in:
//    • four walls of light standing on the footprint's edges, energy rising
//      off the floor and fading upward (vertex alpha: the fade never moves);
//    • a padlock pattern scrolling DIAGONALLY across the walls — reads as
//      "locked" from across the room;
//    • a bright seam on the floor along the edges, motes rising off it;
//    • ONE big sign floating high over the attraction's centre (above its own
//      name / health HUD), billboarded to the viewer: a large padlock,
//      "Locked", how to unlock it ("Finish Lava Crossing and Mole Trench to
//      unlock" / "Just Lava Crossing left!") and the requirements checked off
//      live ("✓ Lava Crossing  |  Mole Trench"), on glowing glass with motes;
//    • Dissolve(): the walls sink into the floor in a glint burst, the sign
//      bursts open and fades.
//  Dims as the viewer comes close, faint inside (PlaylistRoundRules.FieldAlpha).
//  One shader, DreamPark/HoloParticle (the Quest-proven additive sprite shader):
//  no new shader, no keywords, two draws per field.
//  The cleared mark (done rounds) is a small, faint "Cleared" label.
// ─────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using System.Linq;
using DreamPark.MagicUI;
using TMPro;
using UnityEngine;

namespace DreamPark
{
    public sealed class PlaylistLockField : MonoBehaviour
    {
        public const float Pad = 0.03f;           // the walls stand on the footprint edge (a hair outside, never in it)
        public const float DissolveSeconds = 1.4f;
        public const float LockTileM = 0.62f;     // one padlock pair per tile (staggered → a diagonal lattice)
        public const float StreakTileM = 0.35f;
        public const float LockScroll = 0.10f;    // tiles / s, along both axes: the diagonal drift
        public const float StreakRise = 0.45f;    // the rising energy, tiles / s

        Vector2 _size;                              // footprint x / z (metres)
        float _height, _wallH;
        Material _streaks, _locks, _seam;
        Color _streakColor, _lockColor, _seamColor;
        Transform _walls, _badge;
        TextMeshPro _title, _how;
        Transform _rowRoot;
        string _rowText = "";
        readonly List<TextMeshPro> _rowLabels = new List<TextMeshPro>();
        readonly List<(Material m, float a)> _signMats = new List<(Material, float)>();
        ParticleSystem _signMotes;
        string _needsKey;

        // ── One voice for the requirements (Oct 19 dry 11: the sign AND Dreamie's caption both said "Just Mole Ville left!") ──
        /// Dreamie's live caption (set by the headset's Dreamie, null when none): while it names a requirement, the sign
        /// shows only "Locked" and lets her say it.
        public static System.Func<string> CaptionNow;
        IReadOnlyList<(string name, bool done)> _needs;
        float _wordsA = 1f;
        static readonly List<PlaylistLockField> s_locked = new List<PlaylistLockField>();
        /// True when the eye is within <paramref name="metres"/> of a standing locked field (not cleared, not dissolving):
        /// other stops' goal markers step back there.
        public static bool NearLocked(Vector3 eye, float metres)
        {
            foreach (var f in s_locked)
                if (f != null && !f._cleared && f._dissolveAt < 0f
                    && PlaylistRoundRules.DistanceToEdge(f.transform.position, f.transform.rotation, f._size, eye) < metres) return true;
            return false;
        }
        bool CaptionNames()
        {
            string c = null;
            try { c = CaptionNow?.Invoke(); } catch (System.Exception) { c = null; }
            if (string.IsNullOrEmpty(c) || _needs == null) return false;
            foreach (var n in _needs) if (!n.done && !string.IsNullOrEmpty(n.name) && c.IndexOf(n.name, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
        /// Goal markers step back where a SHOWN sign covers them (UI never stacks): true when <paramref name="point"/> is
        /// seen from <paramref name="eye"/> within a shown sign's angular extent (+ a margin).
        public static bool SignCovers(Vector3 eye, Vector3 point)
        {
            foreach (var f in s_locked)
            {
                if (f == null || !f._signed || f._badge == null || f._cleared || f._signShown < 0.3f) continue;
                Vector3 toSign = f._badge.position - eye, toPoint = point - eye;
                float d = toSign.magnitude;
                if (d < 0.1f || toPoint.magnitude < d * 0.5f) continue;   // the point is well in front of the sign
                float half = Mathf.Rad2Deg * Mathf.Atan(SignW * 0.6f * f._signScale / d) + 3f;
                if (Vector3.Angle(toSign, toPoint) < half) return true;
            }
            return false;
        }

        /// The SIGN shows only where the player can see the stop (Oct 20, Aidan: the lock panel showed through a wall): a
        /// clear line (PlaylistGoalSight: scanned walls / the room graph) to the sign or to any of the field's sides, and
        /// within SignShowM. Eased over FadeSightS. The field's walls stay whenever the stop stands (real walls occlude
        /// them on the device).
        public const float SignShowM = 9f, FadeSightS = 0.35f;
        float _signShown = 1f, _nextSight;
        bool _sightClear = true;
        public bool InSight => _sightClear;
        public float SignShown => _signShown;

        bool SightCheck(Vector3 eye)
        {
            if (_badge != null && PlaylistGoalSight.CanSee(eye, _badge.position)) return true;
            float hx = _size.x * 0.5f, hz = _size.y * 0.5f;
            foreach (var l in new[] { new Vector3(0, 1f, 0), new Vector3(hx, 1f, 0), new Vector3(-hx, 1f, 0), new Vector3(0, 1f, hz), new Vector3(0, 1f, -hz) })
                if (PlaylistGoalSight.CanSee(eye, transform.TransformPoint(l))) return true;
            return false;
        }

        void OnEnable() { if (!s_locked.Contains(this)) s_locked.Add(this); }
        void OnDisable() => s_locked.Remove(this);
        Material _signEnergy;
        int _rowLines;
        /// A bright, clear gold for checks (brand Gold reads tan over the violet light).
        static readonly Color CheckGold = new Color(1f, 0.86f, 0.32f, 1f);
        float _signScale = 1f;
        /// The sign's distance scale: 1x within 3 m, growing linearly to 1.8x at 5.4 m and beyond.
        public static float SignScaleFor(float dist) => Mathf.Clamp(dist / 3f, 1f, 1.8f);
        public int RowLines => _rowLines;
        bool _signed;
        public const float SignW = 2.4f, SignH = 1.3f;
        /// The sign's centre: above the attraction's top (its renderers incl. its own name / health HUD) — never on it.
        /// Clamped 2.6–3.3 m: tall props (hanging blocks) must not push it out of sight; a boss HUD tops out ~2.7 m.
        float SignY => Mathf.Clamp(_height + 0.18f + SignH * 0.5f, SignMinY, SignMaxY);
        public const float SignMinY = 2.6f, SignMaxY = 3.3f;
        TextMeshPro _label;
        MeshRenderer _lock;
        ParticleSystem _motes, _glints;
        float _born, _dissolveAt = -1f, _alpha = 1f;
        bool _cleared;

        public bool Dissolving => _dissolveAt >= 0f;
        public bool Cleared => _cleared;
        public Vector2 Size => _size;
        public float WallHeight => _wallH;
        public string Text => _label != null ? _label.text : "";
        /// The instruction line the sign shows now (lockSign / "Finish … to unlock") — for checks.
        public string Sign { get; private set; } = "";
        /// The walls' current brightness (1 = full; dims near / inside the field) — for checks.
        public float Alpha => _alpha;

        /// A locked field on a footprint: centre on the floor, yaw rotation, size (x, z), height of the attraction.
        public static PlaylistLockField Locked(Transform parent, Vector3 floorCentre, Quaternion rot, Vector2 size, float height,
            IReadOnlyList<(string name, bool done)> needs = null, string sign = null)
        {
            var f = Make(parent, "LockedField", floorCentre, rot, size, height);
            f.BuildWalls();
            f.BuildEdgeLight();
            f.BuildSign();
            f.SetNeeds(needs, sign);
            return f;
        }

        /// The authored requirements (lockedUntil) with their live state: the sign's instructions + check row follow.
        /// <paramref name="sign"/>: the playlist's own words for it (record v2 `lockSign`), in place of "Finish … to unlock".
        public void SetNeeds(IReadOnlyList<(string name, bool done)> needs, string sign = null)
        {
            _needs = needs;
            if (_how == null) return;
            string key = (sign ?? "") + "#" + (needs == null ? "" : string.Join("|", needs.Select(n => (n.done ? "1" : "0") + n.name)));
            if (key == _needsKey) return;
            _needsKey = key;
            var remaining = needs == null ? new List<string>() : needs.Where(n => !n.done).Select(n => n.name).ToList();
            string how = !string.IsNullOrEmpty(sign) && remaining.Count > 0 ? sign
                : needs == null || needs.Count == 0 ? (sign ?? "") : PlaylistRoundRules.UnlockLine(remaining);
            // Never dropped (Oct 20, schema-device: a 37-char lockSign blanked the sign): one line at full size when it
            // fits, else wrapped onto two lines, shrinking (down to HowMinSize) until two lines hold it.
            int lines = FitHow(how);
            Sign = _how.text;
            _rowText = needs == null || needs.Count <= 1 ? "" : PlaylistRoundRules.RequirementsRow(needs);
            LayoutRow(needs == null || needs.Count <= 1 ? null : needs, null);
            float howY = lines >= 2 ? -0.2f : -0.14f;
            _how.transform.localPosition = new Vector3(0f, howY, -0.002f);
            if (_rowRoot != null) _rowRoot.localPosition = new Vector3(0f, string.IsNullOrEmpty(_how.text) ? -0.16f : lines >= 2 ? -0.52f : -0.38f, -0.002f);
        }

        public const float HowSize = 140f, HowMinSize = 92f, HowWidthM = SignW - 0.12f;
        /// The instruction line's lines on the sign now (0 = none) — for checks.
        public int HowLines { get; private set; }

        /// Fit the instruction: one line at HowSize, else two lines, shrinking toward HowMinSize until two hold it (a
        /// longer text keeps HowMinSize and wraps on). Returns the line count.
        int FitHow(string text)
        {
            _how.text = text ?? "";
            _how.fontSize = HowSize;
            _how.textWrappingMode = TextWrappingModes.Normal;
            if (string.IsNullOrEmpty(text)) return HowLines = 0;
            float widthUnits = HowWidthM / _how.transform.localScale.x;
            _how.rectTransform.sizeDelta = new Vector2(widthUnits, _how.rectTransform.sizeDelta.y);
            int Lines() { _how.ForceMeshUpdate(); return Mathf.Max(1, _how.textInfo.lineCount); }
            if (_how.GetPreferredValues(text, 99999f, 0f).x <= widthUnits) return HowLines = 1;
            for (float size = HowSize; size >= HowMinSize; size -= 6f)
            {
                _how.fontSize = size;
                if (Lines() <= 2) return HowLines = Lines();
            }
            _how.fontSize = HowMinSize;
            return HowLines = Lines();
        }

        /// The check row as icon + text items with " | " between: a drawn check (the brand fonts have no ✓) before the done ones.
        void LayoutRow(IReadOnlyList<(string name, bool done)> needs, string prefix = null)
        {
            foreach (Transform c in _rowRoot) Destroy(c.gameObject);
            _rowLabels.Clear();
            if (needs == null) return;
            // Readable from the doorway (Oct 19 review): as big as the unlock line, wrapped onto as many lines as it needs
            // rather than shrunk; an item ("✓ Lava Crossing") never splits, " | " sits only between items on one line.
            const float size = 140f, gap = 0.05f, icon = 0.15f, lineH = 0.25f, maxW = SignW - 0.08f;
            var groups = new List<List<(Transform t, float w)>>();
            if (prefix != null) groups.Add(new List<(Transform, float)> { RowLabel(prefix, MagicLook.Lilac, size) });
            for (int i = 0; i < needs.Count; i++)
            {
                var g = new List<(Transform t, float w)>();
                var (name, done) = needs[i];
                if (done && MagicLook.Holo != null)
                {
                    var m = MagicLook.HoloMat(CheckTexture(), CheckGold, additive: false, queue: MagicLook.QSticker);
                    _signMats.Add((m, 1f));
                    var r = MagicLook.Plate(_rowRoot, "Check", MagicLook.Quad(), m, Vector3.zero, Quaternion.identity, new Vector3(icon, icon, 1f));
                    var gm = MagicLook.HoloMat(MagicLook.GlowDiscTexture(), new Color(MagicLook.Gold.r, MagicLook.Gold.g, MagicLook.Gold.b, 0.55f), true, MagicLook.QGlow);
                    _signMats.Add((gm, 0.55f));
                    MagicLook.Plate(r.transform, "CheckGlow", MagicLook.Quad(), gm, new Vector3(0f, 0f, 0.002f), Quaternion.identity, new Vector3(2.1f, 2.1f, 1f));
                    g.Add((r.transform, icon + 0.012f));
                }
                g.Add(RowLabel(name, Color.white, size));   // bright white; the gold check says it is done
                groups.Add(g);
            }
            float W(List<(Transform t, float w)> g) => g.Sum(x => x.w) + gap * (g.Count - 1);
            var lines = new List<List<List<(Transform t, float w)>>>(); float lineW = 0f; bool prevIsItem = false;
            var sepW = 0.07f;   // until the first separator is measured
            foreach (var g in groups)
            {
                bool isItem = !(prefix != null && g == groups[0]);
                float add = W(g) + (lines.Count > 0 && lines[lines.Count - 1].Count > 0 ? gap + (prevIsItem && isItem ? sepW + gap : 0f) : 0f);
                if (lines.Count == 0 || (lineW + add > maxW && lines[lines.Count - 1].Count > 0)) { lines.Add(new List<List<(Transform, float)>>()); lineW = 0f; add = W(g); prevIsItem = false; }
                if (prevIsItem && isItem)
                {
                    var sep = RowLabel("|", new Color(1f, 1f, 1f, 0.6f), size); sepW = sep.w;
                    lines[lines.Count - 1].Add(new List<(Transform, float)> { sep });
                    add = W(g) + 2f * gap + sep.w;
                }
                lines[lines.Count - 1].Add(g); lineW += add; prevIsItem = isItem;
            }
            for (int li = 0; li < lines.Count; li++)
            {
                var flat = lines[li].SelectMany(x => x).ToList();
                float total = W(flat), xx = -total * 0.5f;
                foreach (var (t, w) in flat) { t.localPosition = new Vector3(xx + w * 0.5f, -li * lineH, 0f); xx += w + gap; }
            }
            _rowLines = lines.Count;
        }

        (Transform t, float w) RowLabel(string text, Color c, float size)
        {
            var l = MagicLook.Label(_rowRoot, "Item", size, c, 1.4f, display: false);
            l.text = text; l.alignment = TextAlignmentOptions.Center;
            l.fontStyle |= FontStyles.Bold;   // reads bright white over the field's light, not lavender
            Vector2 pv = l.GetPreferredValues(text);
            float w = pv.x * l.transform.localScale.x;
            l.rectTransform.sizeDelta = new Vector2(pv.x + 1f, l.rectTransform.sizeDelta.y);
            _rowLabels.Add(l);
            return (l.transform, w);
        }

        /// A check mark: two soft strokes.
        public static Texture2D CheckTexture() => MagicLook.Tex("LockFieldCheck2", 64, 64, (u, v) =>
        {
            float d1 = DistSeg(new Vector2(u, v), new Vector2(0.16f, 0.52f), new Vector2(0.4f, 0.26f));
            float d2 = DistSeg(new Vector2(u, v), new Vector2(0.4f, 0.26f), new Vector2(0.86f, 0.78f));
            return Mathf.Clamp01((0.11f - Mathf.Min(d1, d2)) / 0.03f);
        });

        static float DistSeg(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a; float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }

        static string Hex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);
        public string HowText => _how != null ? _how.text : "";
        public string RowText => _rowText;

        /// The subtle mark over a finished round's stop (held, no field).
        public static PlaylistLockField Done(Transform parent, Vector3 floorCentre, Quaternion rot, Vector2 size, float height)
        {
            var f = Make(parent, "ClearedMark", floorCentre, rot, size, height);
            f._cleared = true;
            f.BuildCleared();
            return f;
        }

        static PlaylistLockField Make(Transform parent, string name, Vector3 floorCentre, Quaternion rot, Vector2 size, float height)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, true);
            go.transform.SetPositionAndRotation(floorCentre, rot);
            var f = go.AddComponent<PlaylistLockField>();
            f._size = new Vector2(Mathf.Max(0.6f, size.x), Mathf.Max(0.6f, size.y));
            f._height = Mathf.Clamp(height + 0.3f, 1.4f, 4.5f);
            f._wallH = WallHeightFor(height);
            f._born = Time.time;
            return f;
        }

        /// Tall enough to read as a wall from across a room, low enough to stay light: 1.3–1.9 m.
        public static float WallHeightFor(float attractionHeight) => Mathf.Clamp(attractionHeight * 0.8f + 0.6f, 1.3f, 1.9f);

        // ── Build ───────────────────────────────────────────────────

        void BuildWalls()
        {
            if (MagicLook.Holo == null) return;
            _walls = new GameObject("Walls").transform;
            _walls.SetParent(transform, false);
            float hx = _size.x * 0.5f + Pad, hz = _size.y * 0.5f + Pad;
            // Energy: soft vertical streaks rising off the floor.
            _streakColor = new Color(MagicLook.VioletBright.r, MagicLook.VioletBright.g, MagicLook.VioletBright.b, 0.75f);
            _streaks = Additive(StreakTexture(), _streakColor, MagicLook.QGlass);
            Wall("Energy", WallMesh(hx, hz, _wallH, StreakTileM, _wallH, new[] { 1f, 0.55f, 0.18f, 0f }), _streaks);
            // The lock lattice, a hair in front of the energy so they never z-fight.
            _lockColor = Color.Lerp(MagicLook.Lilac, MagicLook.VioletBright, 0.35f); _lockColor.a = 0.75f;
            _locks = Additive(LockLatticeTexture(), _lockColor, MagicLook.QGlass + 1);
            Wall("Locks", WallMesh(hx + 0.004f, hz + 0.004f, _wallH, LockTileM, LockTileM, new[] { 0.95f, 0.85f, 0.45f, 0f }), _locks);
        }

        void Wall(string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_walls, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        static Material Additive(Texture tex, Color c, int queue)
        {
            var m = MagicLook.HoloMat(tex, c, additive: true, queue: queue);
            m.SetFloat("_VertexColor", 1f);   // the upward fade rides in the vertex alpha (our own mesh carries a colour stream)
            return m;
        }

        void BuildEdgeLight()
        {
            if (MagicLook.Holo == null) return;
            float hx = _size.x * 0.5f + Pad, hz = _size.y * 0.5f + Pad;
            // The seam: a bright line of light where the walls meet the floor (a thin, soft strip either side of the edge).
            _seamColor = new Color(MagicLook.VioletBright.r, MagicLook.VioletBright.g, MagicLook.VioletBright.b, 0.85f);
            _seam = MagicLook.HoloMat(MagicLook.GlowDiscTexture(), _seamColor, additive: true, queue: MagicLook.QGlow);
            var seam = new GameObject("Seam");
            seam.transform.SetParent(transform, false);
            seam.transform.localPosition = new Vector3(0f, 0.012f, 0f);
            seam.AddComponent<MeshFilter>().sharedMesh = SeamMesh(hx, hz, 0.09f);
            var sr = seam.AddComponent<MeshRenderer>();
            sr.sharedMaterial = _seam;
            sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            sr.receiveShadows = false;
            // Motes rising off the edges + glints running along them (the box's edges, flattened onto the floor).
            float perim = 2f * (hx + hz) * 2f;
            _motes = MagicLook.Sparkles(transform, "FieldMotes", MagicLook.FlourishOrb, 1f, rate: 1.2f * perim, rise: 0.35f,
                life: 2.8f, size: new Vector2(0.03f, 0.06f), edgeOnly: true);
            _glints = MagicLook.Sparkles(transform, "FieldGlints", MagicLook.FlourishGlint, 1f, rate: 0.8f * perim, rise: 0.06f,
                life: 2.2f, size: new Vector2(0.07f, 0.13f), edgeOnly: true);
            foreach (var ps in new[] { _motes, _glints })
            {
                if (ps == null) continue;
                var sh = ps.shape;
                sh.shapeType = ParticleSystemShapeType.BoxEdge;
                sh.scale = new Vector3(hx * 2f, hz * 2f, 0f);   // the emitter lies on the floor (−90° about X): its XY = the footprint
            }
        }

        void BuildSign()
        {
            var b = new GameObject("Sign");
            b.transform.SetParent(transform, false);
            b.transform.localPosition = new Vector3(0f, SignY, 0f);
            _badge = b.transform;
            _signed = true;
            if (MagicLook.Holo != null)
            {
                // Glowing glass (the Magic UI board language, never a flat slab): halo, feathered glass, inner light, top +
                // bottom edge light, a shimmer — and motes.
                SignPlate("Halo", MagicLook.InnerLightTexture(), new Color(MagicLook.Violet.r, MagicLook.Violet.g, MagicLook.Violet.b, 0.32f), true, MagicLook.QGlass - 3, 0.03f, SignW * 1.55f, SignH * 1.7f);
                // The field's own light (Oct 19 review: not a flat dark panel): a light veil for contrast, then the walls'
                // rising energy across the sign, scrolling up like the walls do.
                SignPlate("Glass", MagicLook.FeatherTexture(), new Color(0.05f, 0.02f, 0.11f, 0.5f), false, MagicLook.QGlass, 0.012f, SignW * 1.04f, SignH * 1.1f);   // just behind the words
                // Feathered on every side (vertex alpha), so it glows out of the air instead of reading as a screen.
                var ec = new Color(MagicLook.VioletBright.r, MagicLook.VioletBright.g, MagicLook.VioletBright.b, 0.6f);
                _signEnergy = Additive(StreakTexture(), ec, MagicLook.QGlass + 1);
                MagicLook.Plate(_badge, "Energy", FeatherQuad(SignW * 1.15f / StreakTileM * 0.5f, SignH * 1.2f / (StreakTileM * 4f), 0.22f, 0.26f),
                    _signEnergy, new Vector3(0f, 0f, 0.01f), Quaternion.identity, new Vector3(SignW * 1.15f, SignH * 1.2f, 1f));
                _signMats.Add((_signEnergy, ec.a));
                var top = SignPlate("EdgeTop", MagicLook.LineTexture(), new Color(MagicLook.VioletBright.r, MagicLook.VioletBright.g, MagicLook.VioletBright.b, 0.6f), true, MagicLook.QGlow, 0.006f, SignW * 0.8f, 0.018f);
                if (top != null) top.localPosition = new Vector3(0f, SignH * 0.5f, 0.006f);
                var bot = SignPlate("EdgeBottom", MagicLook.LineTexture(), new Color(MagicLook.VioletBright.r, MagicLook.VioletBright.g, MagicLook.VioletBright.b, 0.6f), true, MagicLook.QGlow, 0.006f, SignW * 0.8f, 0.018f);
                if (bot != null) bot.localPosition = new Vector3(0f, -SignH * 0.5f, 0.006f);
                // The padlock, big, glowing.
                var glow = SignPlate("PadlockGlow", MagicLook.GlowDiscTexture(), new Color(MagicLook.Violet.r, MagicLook.Violet.g, MagicLook.Violet.b, 0.7f), true, MagicLook.QGlow, 0.004f, 0.62f, 0.62f);
                if (glow != null) { glow.localPosition = new Vector3(0f, 0.4f, 0.004f); glow.localScale = new Vector3(0.75f, 0.75f, 1f); }
                var lockMat = MagicLook.HoloMat(MagicLook.LockTexture(), MagicLook.Lilac, additive: false, queue: MagicLook.QSticker);
                _lock = MagicLook.Plate(b.transform, "Padlock", MagicLook.Quad(), lockMat, new Vector3(0f, 0.4f, 0f), Quaternion.identity, new Vector3(0.42f, 0.42f, 1f));
                _signMats.Add((lockMat, 1f));
            }
            _signMotes = MagicLook.Sparkles(b.transform, "SignMotes", MagicLook.FlourishOrb, 0.5f, rate: 3f, rise: 0.04f, life: 3f,
                size: new Vector2(0.014f, 0.03f), edgeOnly: false);
            if (_signMotes != null)
            {
                _signMotes.transform.localRotation = Quaternion.identity;
                var sh = _signMotes.shape; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(SignW + 0.2f, SignH + 0.15f, 0.05f);
            }
            _title = SignText("Title", 175f, MagicLook.Lilac, true, 0.12f);
            _title.text = "Locked";
            _how = SignText("How", HowSize, Color.white, false, -0.14f, HowWidthM);
            _rowRoot = new GameObject("Row").transform;
            _rowRoot.SetParent(b.transform, false);
            _rowRoot.localPosition = new Vector3(0f, -0.38f, -0.002f);
            MagicLook.SetXray(b.transform, true);   // the attraction's own geometry never cuts the sign
            MagicOnTop.Of(b.transform);   // when shown, over walls / occluders / 3D objects — its requirement row too (Aidan, Oct 21)
        }

        /// A unit quad (−0.5..0.5) whose vertex alpha feathers to 0 over `bx`/`by` of each edge; UVs tile uTiles × vTiles.
        public static Mesh FeatherQuad(float uTiles, float vTiles, float bx, float by)
        {
            float[] xs = { 0f, bx, 1f - bx, 1f }, ys = { 0f, by, 1f - by, 1f };
            var v = new Vector3[16]; var uv = new Vector2[16]; var col = new Color[16]; var tri = new List<int>();
            for (int j = 0; j < 4; j++)
                for (int i = 0; i < 4; i++)
                {
                    int k = j * 4 + i;
                    v[k] = new Vector3(xs[i] - 0.5f, ys[j] - 0.5f, 0f);
                    uv[k] = new Vector2(xs[i] * uTiles, ys[j] * vTiles);
                    col[k] = new Color(1f, 1f, 1f, (i == 0 || i == 3 || j == 0 || j == 3) ? 0f : 1f);
                    if (i < 3 && j < 3) tri.AddRange(new[] { k, k + 4, k + 5, k, k + 5, k + 1 });
                }
            var m = new Mesh { name = "LockSignFeather", vertices = v, uv = uv, colors = col };
            m.SetTriangles(tri, 0); m.RecalculateBounds();
            return m;
        }

        Transform SignPlate(string name, Texture tex, Color c, bool additive, int queue, float z, float w, float h)
        {
            var m = MagicLook.HoloMat(tex, c, additive, queue);
            if (m == null) return null;
            var r = MagicLook.Plate(_badge, name, MagicLook.Quad(), m, new Vector3(0f, 0f, z), Quaternion.identity, new Vector3(w, h, 1f));
            _signMats.Add((m, c.a));
            return r.transform;
        }

        TextMeshPro SignText(string name, float size, Color c, bool display, float y, float widthM = SignW - 0.12f)
        {
            var t = MagicLook.Label(_badge, name, size, c, widthM, display: display);
            t.alignment = TextAlignmentOptions.Center;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.transform.localPosition = new Vector3(0f, y, -0.002f);
            return t;
        }

        void BuildCleared()
        {
            var b = new GameObject("Badge");
            b.transform.SetParent(transform, false);
            b.transform.localPosition = new Vector3(0f, _height * 0.75f, 0f);
            _badge = b.transform;
            _label = MagicLook.Label(b.transform, "Marker", 70f, MagicLook.TextSecondary, 2f, display: false);
            _label.text = "Cleared";
            _label.alignment = TextAlignmentOptions.Center;
            _label.alpha = 0.55f;
        }

        // ── Live ────────────────────────────────────────────────────

        /// Sinks into the floor with a glint burst; the GameObject goes when done.
        public void Dissolve()
        {
            if (_dissolveAt >= 0f) return;
            _dissolveAt = Time.time;
            if (_motes != null) _motes.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            if (_glints != null) _glints.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            if (!_cleared)
            {
                var burst = MagicLook.Burst(transform, Mathf.Max(_size.x, _size.y) * 0.5f + Pad);
                if (burst != null) { burst.transform.localPosition = new Vector3(0f, 0.3f, 0f); burst.Play(); }
                if (_signed && _badge != null)
                {
                    // The sign bursts open: a ring of glints at the padlock, the sign swells and fades.
                    var pop = MagicLook.Burst(_badge, 0.5f);
                    if (pop != null) { pop.transform.localPosition = new Vector3(0f, 0.4f, 0f); pop.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); pop.Play(); }
                    if (_title != null) _title.text = "Unlocked!";
                }
                if (_signMotes != null) _signMotes.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }

        void LateUpdate()
        {
            float t = Time.time;
            Transform head = PlaylistObjectiveCallout.Head();
            float near = 1f;
            if (head != null)
            {
                near = PlaylistRoundRules.FieldAlpha(PlaylistRoundRules.DistanceToEdge(transform.position, transform.rotation,
                    _size + Vector2.one * (2f * Pad), head.position));
                if (_badge != null && _signed)
                {
                    // Over the centre; a close viewer gets it a little lower and turned down toward them (no craned neck),
                    // never lower than the attraction's own top.
                    Vector3 lh = transform.InverseTransformPoint(head.position);
                    float d = new Vector2(lh.x, lh.z).magnitude;
                    // Readable from the doorway (Oct 19 review): grows with distance, 1x within 3 m, up to 1.8x, about
                    // its bottom edge, so it only ever grows upward, away from the boss HUD below it.
                    _signScale = Mathf.Lerp(_signScale, SignScaleFor(Vector3.Distance(head.position, _badge.position)), 1f - Mathf.Exp(-4f * Time.deltaTime));
                    float y = SignY + (_signScale - 1f) * SignH * 0.5f;
                    if (_dissolveAt < 0f) _badge.localScale = Vector3.one * _signScale;
                    _badge.localPosition = Vector3.Lerp(_badge.localPosition, new Vector3(0f, y, 0f), 1f - Mathf.Exp(-4f * Time.deltaTime));
                    Vector3 to3 = _badge.position - head.position;
                    if (to3.sqrMagnitude > 1e-4f) _badge.rotation = Quaternion.LookRotation(to3.normalized, Vector3.up);
                }
                if (_badge != null && !_signed)
                {
                    Vector3 to = _badge.position - head.position; to.y = 0f;
                    if (to.sqrMagnitude > 1e-4f) _badge.rotation = Quaternion.LookRotation(to.normalized, Vector3.up);
                }
            }
            if (head != null && Time.unscaledTime >= _nextSight)
            {
                _nextSight = Time.unscaledTime + 0.15f;
                _sightClear = SightCheck(head.position);
            }
            float signDist = head != null && _badge != null ? Vector3.Distance(head.position, _badge.position) : 0f;
            _signShown = Mathf.MoveTowards(_signShown, _sightClear && signDist <= SignShowM ? 1f : 0f, Time.deltaTime / FadeSightS);
            float fadeIn = Mathf.Clamp01((t - _born) / 0.8f);
            float dissolve = 1f, sink = 1f;
            if (_dissolveAt >= 0f)
            {
                float k = Mathf.Clamp01((t - _dissolveAt) / DissolveSeconds);
                dissolve = 1f - k * k;
                sink = 1f - k;                                   // the walls slide down into the floor
                if (_badge != null) _badge.localScale = Vector3.one * (_signScale * (1f + 0.5f * k));
                if (k >= 1f) { Destroy(gameObject); return; }
            }
            if (_walls != null) _walls.localScale = new Vector3(1f, Mathf.Max(0.001f, sink), 1f);
            // A slow breath (4 s, ±8 %): shimmer without flicker.
            float breath = 0.92f + 0.08f * Mathf.Sin(t * Mathf.PI * 0.5f);
            _alpha = near * fadeIn * dissolve;
            if (_streaks != null)
            {
                var c = _streakColor; c.a = _streakColor.a * _alpha * breath;
                _streaks.SetColor("_BaseColor", c);
                _streaks.SetTextureOffset("_BaseMap", new Vector2(0f, -t * StreakRise));
            }
            if (_locks != null)
            {
                var c = _lockColor; c.a = _lockColor.a * _alpha;
                _locks.SetColor("_BaseColor", c);
                _locks.SetTextureOffset("_BaseMap", new Vector2(t * LockScroll, -t * LockScroll));   // diagonal: along the wall and up
            }
            if (_seam != null) { var c = _seamColor; c.a = _seamColor.a * _alpha * breath; _seam.SetColor("_BaseColor", c); }
            if (_label != null) _label.alpha = (_cleared ? 0.55f : 1f) * fadeIn * dissolve;
            if (_signEnergy != null) _signEnergy.SetTextureOffset("_BaseMap", new Vector2(0f, -t * StreakRise));
            // The sign's motes go with the sign (never through a wall). The field itself always shows while the stop does.
            SetParticles(_signMotes, _signShown > 0.05f);
            if (_signed)
            {
                float sa = fadeIn * dissolve * _signShown;
                foreach (var (m, a0) in _signMats) { if (m == null) continue; var c = m.GetColor("_BaseColor"); c.a = a0 * sa; m.SetColor("_BaseColor", c); }
                // While Dreamie says what is left, the sign says only "Locked" (one voice); back after her line.
                _wordsA = Mathf.MoveTowards(_wordsA, CaptionNames() ? 0f : 1f, Time.deltaTime * 4f);
                if (_title != null) _title.alpha = sa; if (_how != null) _how.alpha = sa * _wordsA;
                foreach (var l in _rowLabels) if (l != null) l.alpha = sa * _wordsA;
                if (_rowRoot != null) _rowRoot.gameObject.SetActive(_wordsA > 0.02f);   // the gold checks go with the words
            }
        }

        static void SetParticles(ParticleSystem ps, bool on)
        {
            if (ps == null) return;
            var r = ps.GetComponent<ParticleSystemRenderer>();
            if (r != null && r.enabled != on) r.enabled = on;
        }

        // ── Geometry + texture ──────────────────────────────────────

        /// Four walls on the rectangle ±hx × ±hz, height h. u runs along the perimeter in tiles of <paramref name="uTileM"/>
        /// (continuous round the corners, so the lattice wraps the box), v up the wall in tiles of <paramref name="vTileM"/>.
        /// Vertex alpha fades upward through <paramref name="rowAlpha"/> (rows at 0, ⅓, ⅔ and the top).
        public static Mesh WallMesh(float hx, float hz, float h, float uTileM, float vTileM, float[] rowAlpha)
        {
            Vector2[] c = { new Vector2(-hx, -hz), new Vector2(hx, -hz), new Vector2(hx, hz), new Vector2(-hx, hz) };
            int rows = rowAlpha.Length;
            var v = new System.Collections.Generic.List<Vector3>();
            var uv = new System.Collections.Generic.List<Vector2>();
            var col = new System.Collections.Generic.List<Color>();
            var tri = new System.Collections.Generic.List<int>();
            float s = 0f;
            for (int side = 0; side < 4; side++)
            {
                Vector2 a = c[side], b = c[(side + 1) % 4];
                float len = Vector2.Distance(a, b);
                int baseIndex = v.Count;
                for (int r = 0; r < rows; r++)
                {
                    float y = h * r / (rows - 1);
                    var cc = new Color(1f, 1f, 1f, rowAlpha[r]);
                    v.Add(new Vector3(a.x, y, a.y)); uv.Add(new Vector2(s / uTileM, y / vTileM)); col.Add(cc);
                    v.Add(new Vector3(b.x, y, b.y)); uv.Add(new Vector2((s + len) / uTileM, y / vTileM)); col.Add(cc);
                }
                for (int r = 0; r < rows - 1; r++)
                {
                    int i0 = baseIndex + r * 2, i1 = i0 + 1, i2 = i0 + 2, i3 = i0 + 3;
                    tri.Add(i0); tri.Add(i2); tri.Add(i1);
                    tri.Add(i1); tri.Add(i2); tri.Add(i3);
                }
                s += len;
            }
            var m = new Mesh { name = "PlaylistLockWalls" };
            m.SetVertices(v); m.SetUVs(0, uv); m.SetColors(col); m.SetTriangles(tri, 0);
            m.RecalculateBounds();
            return m;
        }

        /// A thin glowing strip on the floor along the rectangle's edges (width w, centred on the edge).
        public static Mesh SeamMesh(float hx, float hz, float w)
        {
            Vector2[] c = { new Vector2(-hx, -hz), new Vector2(hx, -hz), new Vector2(hx, hz), new Vector2(-hx, hz) };
            var v = new System.Collections.Generic.List<Vector3>();
            var uv = new System.Collections.Generic.List<Vector2>();
            var tri = new System.Collections.Generic.List<int>();
            for (int side = 0; side < 4; side++)
            {
                Vector2 a = c[side], b = c[(side + 1) % 4];
                Vector2 d = (b - a).normalized, n = new Vector2(-d.y, d.x) * (w * 0.5f);
                a -= d * (w * 0.5f); b += d * (w * 0.5f);   // overlap the corners
                int i = v.Count;
                v.Add(new Vector3(a.x - n.x, 0f, a.y - n.y)); uv.Add(new Vector2(0.5f, 0f));
                v.Add(new Vector3(b.x - n.x, 0f, b.y - n.y)); uv.Add(new Vector2(0.5f, 0f));
                v.Add(new Vector3(a.x + n.x, 0f, a.y + n.y)); uv.Add(new Vector2(0.5f, 1f));
                v.Add(new Vector3(b.x + n.x, 0f, b.y + n.y)); uv.Add(new Vector2(0.5f, 1f));
                tri.Add(i); tri.Add(i + 2); tri.Add(i + 1); tri.Add(i + 1); tri.Add(i + 2); tri.Add(i + 3);
            }
            var m = new Mesh { name = "PlaylistLockSeam" };
            m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(tri, 0);
            m.RecalculateBounds();
            return m;
        }

        static Texture2D Repeat(Texture2D t) { if (t != null) t.wrapMode = TextureWrapMode.Repeat; return t; }

        /// Rising energy: soft vertical streaks of uneven width and strength over a faint veil (tiles in u and v).
        public static Texture2D StreakTexture() => Repeat(MagicLook.Tex("LockFieldStreaks", 64, 128, (u, v) =>
        {
            float a = Mathf.Pow(0.5f + 0.5f * Mathf.Cos((u * 3f + 0.15f * Mathf.Sin(v * Mathf.PI * 2f)) * Mathf.PI * 2f), 6f);
            float b = Mathf.Pow(0.5f + 0.5f * Mathf.Cos((u * 5f + 0.4f) * Mathf.PI * 2f), 10f) * 0.6f;
            float pulse = 0.55f + 0.45f * Mathf.Pow(0.5f + 0.5f * Mathf.Sin(v * Mathf.PI * 4f), 2f);
            return 0.16f + (a * 0.55f + b * 0.35f) * pulse;
        }));

        /// The lock lattice: two padlocks per tile (centre of each half, staggered) → a diagonal lattice; each glyph with a
        /// soft halo so it glows rather than stamps.
        public static Texture2D LockLatticeTexture() => Repeat(MagicLook.Tex("LockFieldLattice", 128, 128, (u, v) =>
        {
            float g = Mathf.Max(Glyph(u, v, 0.25f, 0.25f), Glyph(u, v, 0.75f, 0.75f));
            return g;
        }));

        // One padlock centred at (cx, cy) in tile space, 0.3 tile tall, plus its halo.
        static float Glyph(float u, float v, float cx, float cy)
        {
            const float s = 0.34f;
            float x = (u - cx) / s + 0.5f, y = (v - cy) / s + 0.5f;
            bool body = x > 0.2f && x < 0.8f && y > 0.08f && y < 0.56f;
            float sr = Vector2.Distance(new Vector2(x, y), new Vector2(0.5f, 0.56f));
            bool shackle = y >= 0.56f && sr > 0.14f && sr < 0.26f;
            bool hole = Vector2.Distance(new Vector2(x, y), new Vector2(0.5f, 0.34f)) < 0.07f;
            float solid = (body && !hole) || shackle ? 1f : 0f;
            float d = Vector2.Distance(new Vector2(x, y), new Vector2(0.5f, 0.42f));
            float halo = Mathf.Clamp01(1f - d / 0.75f); halo *= halo * 0.28f;
            return Mathf.Max(solid, halo);
        }
    }
}
