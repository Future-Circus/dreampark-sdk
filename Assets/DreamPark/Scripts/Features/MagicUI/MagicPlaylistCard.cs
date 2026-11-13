using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace DreamPark.MagicUI
{
    /// <summary>What a playlist card shows (the app's 2:3 card).</summary>
    public struct MagicCardData
    {
        public string choice;    // what picking it votes ("next" | "play:<key>" | "replay")
        public string title;     // "Lava Caves"
        public string climax;    // "Cannonsnout" / "The Final: …" (the boss line), optional
        public string kinds;     // "Adventure · Super Adventure Land" / "Party · 3 games"
        public string cover;     // the cover: a whole 2:3 card of art ("" → the brand gradient)
        public string pitch;     // the 2-line description under the card
        public string facts;     // the results line under the card ("9 challenges + King Jubbo · ~12 min"; never rounds / legs / sets)
        public int minutes, badges;
        public bool lead;
    }

    /// <summary>
    /// "Next up" as THE APP'S PLAYLIST CARD (Oct 18 2026, Aidan: "they must look EXACTLY like the mobile app's 2:3
    /// playlist card"). It ports `PLPosterArt` / `PLPosterCard` (DreamPark-iOS `Creator/PlaylistViews.swift`) in the
    /// app's own proportions, measured in its points (a card is 148 pt wide):
    ///   • the cover fills the 2:3 card edge to edge, with a 12 pt corner radius and a 1 pt white 10 % stroke;
    ///   • a scrim runs from clear at 42 % to dpVoid 92 % at 95 % of the height;
    ///   • the title block sits at the bottom-left, inset 10 pt: the name (17 pt heavy, ≤ 2 lines), the climax line
    ///     (12 pt), and the kinds line (12 pt, in a 22 pt slot);
    ///   • the badge row sits at the top-right, padding 7 pt: two 26 pt marks + "+N";
    ///   • under the card come the 2-line description and the results line (12 pt, white 55 %).
    /// Only the framing is Magic UI's: a halo, and a rim of light when the card is chosen or leading. Under it all
    /// go the squad's votes (each voter's face and the count) and YOUR PICK. A gaze-dwell or a poke presses it
    /// (<see cref="MagicPress"/>). <see cref="Replay"/> builds "Play again" the same way: ↻ in a glowing ring with the
    /// label under it.
    /// </summary>
    public sealed class MagicPlaylistCard
    {
        /// The app's card width in points; every size here is in those points × (width / 148).
        public const float AppWidthPt = 148f;
        /// dpVoid (#0C0220), the app's scrim colour.
        static readonly Color Void = new Color(0x0C / 255f, 0x02 / 255f, 0x20 / 255f, 1f);

        public readonly Transform root;
        public readonly float width, height;
        public string Choice { get; private set; }
        /// Play again: its ring's diameter (metres); 0 for a card.
        public float RingDiameter { get; private set; }
        public bool IsReplay => _replay;
        readonly MagicPress _press = new MagicPress();
        readonly Material _halo, _rim, _art, _fillMat;
        readonly Transform _fill, _faces;
        readonly TextMeshPro _name, _climax, _kinds, _pitch, _facts, _more, _tally, _mine, _label;
        readonly bool _replay;
        readonly float _pt;
        readonly float _climaxY, _nameBase;
        string _coverUrl, _facesKey;
        bool _isMine, _leading;
        float _lift, _baseZ;
        readonly List<MagicAvatar> _faceList = new List<MagicAvatar>();
        readonly List<GameObject> _badgeMarks = new List<GameObject>();

        static readonly Dictionary<string, Texture2D> s_covers = new Dictionary<string, Texture2D>();

        float Pt(float pt) => pt * _pt;
        static float Font(float pt, float ptM) => pt * ptM * 1000f;   // TMP size: ≈ 1 em per (size / 1000) m

        public MagicPlaylistCard(Transform parent, Vector3 at, float w, bool lead, int pitchLines = 2) : this(parent, at, w, lead, false, pitchLines, 0f) { }

        /// "Play again": a votable option (↻ in a ring of light of diameter <paramref name="ringD"/>, "Play again" under it,
        /// its votes under that). <paramref name="at"/> = the ring's centre; the text is at the cards' scale (w).
        public static MagicPlaylistCard Replay(Transform parent, Vector3 at, float w, float ringD) => new MagicPlaylistCard(parent, at, w, false, true, 2, ringD);

        // ── Measured layout (pt at the card's scale): nothing below a card or the ring may overlap ──
        // Oct 19 (dry run: "tiny and far"): the lines under a card read from where the player stands: the description and
        // results 14–15 pt, the vote count 18 pt, YOUR PICK 16 pt, the voters' faces 26 pt (were 12 / 13 / 11 / 22).
        const float GapTopPt = 6f, LinePt = 18f, GapPt = 4f, FactsPt = 20f, GapVotesPt = 6f, VotesPt = 28f, GapMinePt = 3f, MinePt = 20f;
        const float LabelGapPt = 4f, LabelPt = 24f;

        /// The height under a card (its bottom → the bottom of YOUR PICK): the description, the results line, the votes.
        public static float StackBelowM(float w, int pitchLines)
            => (GapTopPt + LinePt * pitchLines + GapPt + FactsPt + GapVotesPt + VotesPt + GapMinePt + MinePt) * w / AppWidthPt;

        /// The Play again stack's height: the ring, its label, its votes.
        public static float ReplayStackM(float w, float ringD)
            => ringD + (LabelGapPt + LabelPt + GapVotesPt + VotesPt) * w / AppWidthPt;

        MagicPlaylistCard(Transform parent, Vector3 at, float w, bool lead, bool replay, int pitchLines, float ringD)
        {
            width = w; height = w * 1.5f; _replay = replay; _pt = w / AppWidthPt;
            root = new GameObject(replay ? "PlayAgain" : lead ? "NextUpCard" : "Card").transform;
            root.SetParent(parent, false);
            root.localPosition = at;
            _baseZ = at.z;
            Mesh q = MagicLook.Quad();
            float h = height;
            if (MagicLook.Holo != null)
            {
                _halo = MagicLook.HoloMat(MagicLook.InnerLightTexture(), new Color(MagicLook.Violet.r, MagicLook.Violet.g, MagicLook.Violet.b, lead ? 0.5f : 0.18f), true, MagicLook.QGlass - 2);
                MagicLook.Plate(root, "Halo", q, _halo, new Vector3(0, 0, 0.012f), Quaternion.identity, new Vector3(w * 1.55f, h * 1.3f, 1));
            }
            float votesY;
            if (replay)
            {
                // ↻ in a glowing ring (as big as a card's art), "Play again" under it.
                float d = ringD > 0f ? ringD : w * 0.46f, cy = 0f;   // the root is the ring's centre
                RingDiameter = d;
                if (MagicLook.Holo != null)
                {
                    // The board's own light: a lilac glass disc with an inner glow (not a dark well).
                    MagicLook.Plate(root, "Disc", q, MagicLook.HoloMat(CircleTexture(), new Color(0.74f, 0.62f, 0.98f, 0.36f), false, MagicLook.QGlass + 2),
                        new Vector3(0, cy, 0.008f), Quaternion.identity, Vector3.one * d);
                    MagicLook.Plate(root, "Glow", q, MagicLook.HoloMat(MagicLook.InnerLightTexture(), new Color(MagicLook.Lilac.r, MagicLook.Lilac.g, MagicLook.Lilac.b, 0.35f), true, MagicLook.QGlass + 3),
                        new Vector3(0, cy, 0.007f), Quaternion.identity, Vector3.one * d * 0.9f);
                    _rim = MagicLook.HoloMat(RingTexture(), MagicLook.Lilac, true, MagicLook.QGlow + 3);
                    MagicLook.Plate(root, "Ring", q, _rim, new Vector3(0, cy, 0.004f), Quaternion.identity, Vector3.one * d * 1.12f);
                    Texture2D glyph = MagicLook.Glyph("MagicRestart");
                    if (glyph != null)
                        MagicLook.Plate(root, "Restart", q, MagicLook.HoloMat(glyph, Color.white, false, MagicLook.QGlass + 4),
                            new Vector3(0, cy, 0.002f), Quaternion.identity, Vector3.one * d * 0.6f);
                }
                _label = MagicLook.Label(root, "Label", Font(22f, _pt), Color.white, w * 1.6f);
                _label.fontStyle = FontStyles.Bold;
                _label.text = "Play again";
                float labelY = cy - d * 0.5f - Pt(LabelGapPt) - Pt(LabelPt) * 0.5f;
                _label.transform.localPosition = new Vector3(0, labelY, -0.002f);
                votesY = labelY - Pt(LabelPt) * 0.5f - Pt(GapVotesPt) - Pt(VotesPt) * 0.5f;
            }
            else
            {
                // THE CARD (PLPosterArt): the art edge to edge, the scrim, the 1 pt stroke.
                if (MagicLook.Holo != null)
                {
                    _art = MagicLook.HoloMat(BrandGradient(), Color.white, false, MagicLook.QGlass + 2);
                    MagicLook.Plate(root, "Art", q, _art, new Vector3(0, 0, 0.008f), Quaternion.identity, new Vector3(w, h, 1));
                    MagicLook.Plate(root, "Scrim", q, MagicLook.HoloMat(ScrimTexture(), Void, false, MagicLook.QGlass + 3),
                        new Vector3(0, 0, 0.007f), Quaternion.identity, new Vector3(w, h, 1));
                    MagicLook.Plate(root, "Stroke", q, MagicLook.HoloMat(StrokeTexture(), new Color(1f, 1f, 1f, 0.1f), false, MagicLook.QGlass + 4),
                        new Vector3(0, 0, 0.006f), Quaternion.identity, new Vector3(w, h, 1));
                    _rim = MagicLook.HoloMat(StrokeTexture(), MagicLook.VioletBright, true, MagicLook.QGlow + 3);
                    MagicLook.Plate(root, "Rim", q, _rim, new Vector3(0, 0, 0.005f), Quaternion.identity, new Vector3(w * 1.025f, h * 1.017f, 1));
                }
                // The title block, bottom-up from the 10 pt inset: the kinds line (a 22 pt slot), the climax line
                // (12 pt), and the name (≤ 2 lines).
                float inner = w - Pt(20f);
                float x0 = -w * 0.5f + Pt(10f);
                float y = -h * 0.5f + Pt(10f);
                _kinds = Left(root, "Kinds", Font(14f, _pt), new Color(1f, 1f, 1f, 0.88f), inner, Pt(22f), x0, y, TextAlignmentOptions.MidlineLeft);
                y += Pt(22f) + Pt(4f);
                _climaxY = y;
                _climax = Left(root, "Climax", Font(12f, _pt), new Color(1f, 1f, 1f, 0.85f), inner, Pt(16f), x0, y, TextAlignmentOptions.BottomLeft);
                _nameBase = y + Pt(16f) + Pt(4f);
                _name = Left(root, "Name", Font(17f, _pt), Color.white, inner, Pt(44f), x0, _nameBase, TextAlignmentOptions.BottomLeft);
                _name.fontStyle = FontStyles.Bold;
                _name.textWrappingMode = TextWrappingModes.Normal;
                _name.maxVisibleLines = 2;
                _name.enableAutoSizing = true; _name.fontSizeMax = Font(17f, _pt); _name.fontSizeMin = Font(17f * 0.8f, _pt);
                // The badge row, top-right (padding 7 pt): two 26 pt marks + "+N".
                _more = MagicLook.Label(root, "More", Font(12f, _pt), new Color(1f, 1f, 1f, 0.7f), Pt(30f));
                _more.alignment = TextAlignmentOptions.MidlineRight;
                _more.transform.localPosition = new Vector3(w * 0.5f - Pt(7f) - Pt(15f), h * 0.5f - Pt(7f) - Pt(13f), -0.003f);
                // Under the card (the bands): the 2-line description, the results line.
                float under = -h * 0.5f - Pt(GapTopPt);
                float pitchH = Pt(LinePt * pitchLines);
                // The rect reaches 6 pt into the gap below (the text is top-aligned, nothing moves): two 14 pt lines need a
                // little more than 2 × 18 pt, else TMP ellipsised after line 1 and left a dead gap (Aidan, Oct 19).
                _pitch = Left(root, "Pitch", Font(14f, _pt), new Color(1f, 1f, 1f, 0.7f), w, pitchH + Pt(6f), -w * 0.5f, under - pitchH - Pt(6f), TextAlignmentOptions.TopLeft);
                _pitch.textWrappingMode = TextWrappingModes.Normal;
                _pitch.maxVisibleLines = pitchLines;
                if (pitchLines < 2) _pitch.textWrappingMode = TextWrappingModes.NoWrap;
                float factsBottom = under - pitchH - Pt(GapPt) - Pt(FactsPt);
                _facts = Left(root, "Facts", Font(15f, _pt), new Color(1f, 1f, 1f, 0.75f), w, Pt(FactsPt), -w * 0.5f, factsBottom, TextAlignmentOptions.MidlineLeft);
                votesY = factsBottom - Pt(GapVotesPt) - Pt(VotesPt) * 0.5f;
            }
            // The votes, under it all: faces + the count; YOUR PICK.
            _faces = new GameObject("Votes").transform;
            _faces.SetParent(root, false);
            _faces.localPosition = new Vector3(replay ? -Pt(30f) : -w * 0.5f + Pt(11f), votesY, -0.004f);
            _tally = MagicLook.Label(root, "Tally", Font(18f, _pt), MagicLook.VioletBright, Pt(90f), display: true);
            _tally.alignment = replay ? TextAlignmentOptions.MidlineLeft : TextAlignmentOptions.MidlineRight;
            _tally.transform.localPosition = new Vector3(replay ? Pt(40f) : w * 0.5f - Pt(40f), votesY, -0.004f);
            _mine = MagicLook.Label(root, "Mine", Font(16f, _pt), Color.white, w * 1.4f, display: true);
            _mine.transform.localPosition = new Vector3(0, votesY - Pt(VotesPt) * 0.5f - Pt(GapMinePt) - Pt(MinePt) * 0.5f, -0.002f);
            _mine.text = "";
            if (MagicLook.Holo != null)
            {
                _fillMat = MagicLook.HoloMat(MagicLook.LineTexture(), MagicLook.Lilac, true, MagicLook.QGlow + 4);
                _fill = MagicLook.Plate(root, "Dwell", q, _fillMat, new Vector3(0, votesY + Pt(14f), 0.003f), Quaternion.identity, new Vector3(0.001f, Pt(2.5f), 1)).transform;
            }
        }

        static TextMeshPro Left(Transform parent, string name, float size, Color c, float w, float h, float x0, float yBottom, TextAlignmentOptions align)
        {
            TextMeshPro t = MagicLook.Label(parent, name, size, c, w);
            t.alignment = align;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.rectTransform.pivot = new Vector2(0f, 0f);
            t.rectTransform.sizeDelta = new Vector2(w / MagicLook.TextUnitScale, h / MagicLook.TextUnitScale);
            t.transform.localPosition = new Vector3(x0, yBottom, -0.003f);
            return t;
        }

        /// The card's meta line, compact: "3 rounds + boss  ·  ~14 min" → "3 rounds + boss · 14 min".
        public static string MetaLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            s = s.Replace("~", "").Replace("  ·  ", " · ");
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Trim();
        }

        public void Set(MagicCardData d)
        {
            Choice = d.choice;
            if (_replay) return;
            SetText(_name, d.title);
            bool climax = !string.IsNullOrEmpty(d.climax);
            SetText(_climax, climax ? d.climax : "");
            // No climax line: the name sits right on the kinds line (the block keeps its bottom anchor).
            _name.transform.localPosition = new Vector3(_name.transform.localPosition.x, climax ? _nameBase : _climaxY, -0.003f);
            SetText(_kinds, FitKinds(_kinds, d.kinds));
            SetText(_pitch, d.pitch);
            // The meta line is never cut (Oct 19): "3 rounds + boss · 14 min" (no "~", single spaces), shrunk to fit.
            SetText(_facts, MetaLine(string.IsNullOrEmpty(d.facts) ? (d.minutes > 0 ? d.minutes + " min" : "") : d.facts));
            if (_facts != null)
            {
                _facts.overflowMode = TextOverflowModes.Overflow;
                _facts.enableAutoSizing = true;
                _facts.fontSizeMax = Font(15f, _pt); _facts.fontSizeMin = Font(15f * 0.72f, _pt);
            }
            BadgeRow(d.badges);
            if (d.cover == _coverUrl) return;
            _coverUrl = d.cover;
            if (_art == null) return;
            _art.SetTexture("_BaseMap", BrandGradient());
            if (string.IsNullOrEmpty(d.cover) || MagicAvatar.Loader == null) return;
            if (s_covers.TryGetValue(d.cover, out Texture2D ready) && ready != null) { _art.SetTexture("_BaseMap", ready); return; }
            string want = d.cover;
            try
            {
                MagicAvatar.Loader(want, tex =>
                {
                    if (tex == null || _art == null || _coverUrl != want) return;
                    if (!s_covers.TryGetValue(want, out Texture2D r) || r == null) s_covers[want] = r = Fill23(tex);
                    if (r != null) _art.SetTexture("_BaseMap", r);
                });
            }
            catch (Exception) { }
        }

        int _badgeCount = -1;
        void BadgeRow(int count)
        {
            if (count == _badgeCount) return;
            _badgeCount = count;
            foreach (GameObject g in _badgeMarks) UnityEngine.Object.Destroy(g);
            _badgeMarks.Clear();
            Texture2D mark = MagicLook.Glyph("MagicBadgeMark");
            int shown = Mathf.Min(2, count);
            float size = Pt(26f);
            float right = width * 0.5f - Pt(7f) - (count > 2 ? Pt(28f) : 0f);
            for (int i = 0; i < shown && mark != null && MagicLook.Holo != null; i++)
            {
                float x = right - size * 0.5f - (shown - 1 - i) * (size + Pt(2f));
                _badgeMarks.Add(MagicLook.Plate(root, "BadgeMark" + i, MagicLook.Quad(), MagicLook.HoloMat(mark, new Color(1f, 1f, 1f, 0.55f), false, MagicLook.QGlass + 5),
                    new Vector3(x, height * 0.5f - Pt(7f) - size * 0.5f, 0.002f), Quaternion.identity, Vector3.one * size).gameObject);
            }
            SetText(_more, count > 2 ? "+" + (count - 2) : "");
        }

        /// The squad's votes on this option: their faces (up to 5); the leader glows. No count and no YOUR PICK text (Aidan,
        /// Oct 19): the faces say who, THIS viewer's own face is lit (<paramref name="meIndex"/>), and the card they chose
        /// shows the selected rim + lift.
        public void SetVotes(IList<(string url, string name, string kind, string color)> voters, bool leading, bool mine, int meIndex = -1)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var v in voters) sb.Append(v.url).Append('|').Append(v.name).Append(';');
            sb.Append('#').Append(meIndex);
            string key = sb.ToString();
            if (key != _facesKey)
            {
                _facesKey = key;
                foreach (MagicAvatar f in _faceList) UnityEngine.Object.Destroy(f.root.gameObject);
                _faceList.Clear();
                int n = Mathf.Min(5, voters.Count);
                float size = Pt(26f);
                for (int i = 0; i < n; i++)
                {
                    var f = new MagicAvatar(_faces, "Voter" + i, new Vector3(i * size * 0.72f, 0, -0.001f * i), size, MagicLook.QGlass + 6);
                    f.Set(voters[i].url, voters[i].name, voters[i].kind, voters[i].color);
                    // Mine: a bright white ring, a touch bigger, in front of the others; the rest a quiet lilac.
                    bool me = i == meIndex;
                    f.SetRing(me ? Color.white : MagicLook.Lilac, me ? 1f : 0.45f);
                    if (me) { f.root.localScale *= 1.15f; f.root.localPosition += new Vector3(0, 0, -0.004f); }
                    f.Tick();
                    _faceList.Add(f);
                }
                SetText(_tally, "");
            }
            _leading = leading; _isMine = mine;
            SetText(_mine, "");
        }

        /// Per frame: the press (gaze-dwell / poke), the rim and halo. True once per press.
        public bool Tick()
        {
            bool fired = _press.Tick(root, new Vector2(width * 0.5f, height * 0.5f), Time.deltaTime);
            float k = _press.DwellK, confirm = _press.Confirm;
            if (_fill != null)
            {
                float full = width * 0.86f;
                _fill.localScale = new Vector3(Mathf.Max(0.001f, full * k), _fill.localScale.y, 1);
                _fill.localPosition = new Vector3(-full * 0.5f + full * 0.5f * k, _fill.localPosition.y, 0.003f);
            }
            if (_rim != null)
            {
                // Purple is selection (the app's rule): bright for my pick, softer for the leader, quiet otherwise.
                Color c = _isMine ? MagicLook.VioletBright : _leading ? MagicLook.Lilac : MagicLook.Violet;
                c.a = Mathf.Clamp01((_isMine ? 0.95f : _leading ? 0.55f : _replay ? 0.5f : 0.0f) + 0.6f * k + confirm * 0.5f);
                _rim.SetColor("_BaseColor", c);
            }
            if (_halo != null)
            {
                Color g = MagicLook.Violet;
                g.a = 0.15f + (_isMine ? 0.3f : 0f) + (_leading ? 0.12f : 0f) + 0.25f * k + confirm * 0.4f;
                _halo.SetColor("_BaseColor", g);
            }
            // The card this viewer voted for: the selected rim (above) and a lift (slightly bigger, toward the viewer).
            _lift = Mathf.MoveTowards(_lift, _isMine && !_replay ? 1f : 0f, Time.unscaledDeltaTime * 4f);
            root.localScale = Vector3.one * (1f + 0.045f * _lift + 0.03f * k + 0.06f * confirm * confirm - 0.04f * _press.PokeK);
            root.localPosition = new Vector3(root.localPosition.x, root.localPosition.y, _baseZ - 0.02f * _lift);
            if (fired)
            {
                var board = root.GetComponentInParent<MagicBoard>();
                if (board != null) board.Pop(board.transform.InverseTransformPoint(root.position));
            }
            return fired;
        }

        static void SetText(TextMeshPro t, string s) { s = s ?? ""; if (t != null && t.text != s) t.text = s; }

        /// The kinds line, fitted like the app's (`PLKindsLine` + `ViewThatFits`): the whole line when it fits, else just
        /// the kind ("Adventure · Super Adventure Land" → "Adventure"); never a cut-off title.
        public static string FitKinds(TextMeshPro label, string kinds)
        {
            if (string.IsNullOrEmpty(kinds) || label == null) return kinds ?? "";
            float room = label.rectTransform.sizeDelta.x;
            if (label.GetPreferredValues(kinds).x <= room) return kinds;
            int dot = kinds.IndexOf('\u00b7');
            return dot > 0 ? kinds.Substring(0, dot).Trim() : kinds;
        }

        // ── The card's shapes (the app's 12 pt corner on a 148 × 222 pt card) ──

        const float CornerU = 12f / 148f, CornerV = 12f / 222f;

        static float CardAlpha(float u, float v, float inset = 0f)
        {
            float dx = Mathf.Max(0f, Mathf.Abs(u - 0.5f) - (0.5f - CornerU - inset)) / CornerU;
            float dy = Mathf.Max(0f, Mathf.Abs(v - 0.5f) - (0.5f - CornerV - inset * 148f / 222f)) / CornerV;
            return Mathf.Clamp01((1f - Mathf.Sqrt(dx * dx + dy * dy)) / 0.06f);
        }

        /// The scrim: clear at 42 % from the top → dpVoid 92 % at 95 % (the app's regular card).
        static Texture2D ScrimTexture() => MagicLook.Tex("AppCardScrim", 148, 222, (u, v) =>
        {
            float t = 1f - v;   // from the top
            float a = t <= 0.42f ? 0f : t >= 0.95f ? 0.92f : 0.92f * (t - 0.42f) / (0.95f - 0.42f);
            return a * CardAlpha(u, v);
        });

        /// The 1 pt stroke on the rounded edge.
        static Texture2D StrokeTexture() => MagicLook.Tex("AppCardStroke", 296, 444, (u, v) =>
        {
            float outer = CardAlpha(u, v), inner = CardAlpha(u, v, 1.5f / 148f);
            return Mathf.Clamp01((outer - inner) * 1.4f);
        });

        static Texture2D CircleTexture() => MagicLook.Tex("AppCircle", 128, 128, (u, v) => Mathf.Clamp01((1f - new Vector2(u - 0.5f, v - 0.5f).magnitude * 2f) / 0.03f));
        static Texture2D RingTexture() => MagicLook.Tex("AppRing", 128, 128, (u, v) => { float d = new Vector2(u - 0.5f, v - 0.5f).magnitude * 2f * 1.12f; return Mathf.Clamp01(1f - Mathf.Abs(d - 1f) / 0.05f); });

        static Texture2D s_gradient;
        /// No cover (yet): the brand gradient (violet → deep), cut to the card.
        static Texture2D BrandGradient()
        {
            if (s_gradient != null) return s_gradient;
            const int W = 148, H = 222;
            s_gradient = new Texture2D(W, H, TextureFormat.RGBA32, false) { name = "AppCardGradient", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[W * H];
            Color top = new Color(0.55f, 0.24f, 0.96f), bottom = new Color(0.16f, 0.05f, 0.36f);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    Color c = Color.Lerp(bottom, top, (y + 0.5f) / H);
                    c.a = CardAlpha((x + 0.5f) / W, (y + 0.5f) / H);
                    px[y * W + x] = c;
                }
            s_gradient.SetPixels32(px);
            s_gradient.Apply();
            return s_gradient;
        }

        /// The cover, scaled to FILL the 2:3 card (a centre crop, like `scaledToFill().clipped()`), its corners rounded
        /// (any texture: a GPU blit).
        static Texture2D Fill23(Texture2D src, int w = 296)
        {
            if (src == null) return null;
            int h = Mathf.RoundToInt(w * 1.5f);
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            try
            {
                float sa = src.width / (float)src.height, aspect = 2f / 3f;
                Vector2 scale = sa > aspect ? new Vector2(aspect / sa, 1f) : new Vector2(1f, sa / aspect);
                Graphics.Blit(src, rt, scale, new Vector2((1f - scale.x) * 0.5f, (1f - scale.y) * 0.5f));
                RenderTexture.active = rt;
                var t = new Texture2D(w, h, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "AppCardCover" };
                t.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                Color32[] px = t.GetPixels32();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        px[y * w + x].a = (byte)(CardAlpha((x + 0.5f) / w, (y + 0.5f) / h) * 255f);
                t.SetPixels32(px);
                t.Apply(true, false);
                return t;
            }
            catch (Exception) { return null; }
            finally { RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt); }
        }
    }
}
