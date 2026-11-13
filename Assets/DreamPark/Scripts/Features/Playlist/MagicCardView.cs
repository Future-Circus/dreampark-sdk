// MOVING TO SAL; NOT PLATFORM (PLAYLIST-PLAN § 6.1.5 / § 6.3.5, Oct 21 2026): games own their instruction UI. Kept until
// SuperAdventureLand ports this card code into SAL; the platform shows no objective cards once pl-sets switches
// off the PlaylistObjectiveCallout call sites. Don't add features here.
using System.Collections.Generic;
using DreamPark.MagicUI;
using TMPro;
using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// The playlist objective UI in DreamPark Magic UI (Oct 18 2026; the bookends' shared kit,
    /// `Scripts/Features/MagicUI`): the stop intro, the boss reveal, the personal "You're in — GO!" card, the
    /// 3·2·1 and the team progress are a <see cref="MagicBoard"/> — feathered glowing glass, a shimmer, the
    /// brand type resolving in light, each count bursting in (rays + glints), a sparkle dissolve to leave — and the
    /// low-key goal line over a live stop is a brand-type light label (no panel).
    ///
    /// READABILITY IS THE HARD RULE (Aidan). Every frame the card is shown, <see cref="Place"/> casts rays from the
    /// wearer's eye to the card's FULL extent (corners, edges, centre — not an inset grid) against the occluders
    /// near it (MagicSight's renderer boxes, refreshed every 0.25 s or when the card moves 0.3 m):
    ///   1. the desired body-locked spot when all of it is clear;
    ///   2. else the clearest nearby spot (closer, higher, to either side — kept until it is blocked in turn, so
    ///      the card never jitters), which moves the card clear of a boss's renderer bounds;
    ///   3. else draw over the geometry (ZTest always: glass, edges, text) — the fallback, switched the same frame.
    /// <see cref="Visible"/> is the measured share of clear rays (1 when drawn over), for automated checks.
    /// </summary>
    public sealed class MagicCardView : MonoBehaviour, IPlaylistCardView
    {
        public const float Clear = 0.999f;       // every ray clear = no draw-over
        MagicBoard _board;
        TextMeshPro _label;
        float _w, _h, _leaveAt = -1f, _visible = 1f;
        int _occFrame = -1;
        bool _xray;
        float _clearSince = -1f;
        Vector3 _occAround, _offset;
        List<Bounds> _occ = new List<Bounds>();

        public float Visible => _xray ? 1f : _visible;
        /// Drawn over the geometry right now (the fallback), and the card's size (for checks).
        public bool Xray => _xray;
        public float Width => _w;
        public float Height => _h + _below;

        public static IPlaylistCardView Create(Transform parent, PlaylistCardKind kind)
        {
            var go = new GameObject("MagicCard");
            go.transform.SetParent(parent, false);
            var v = go.AddComponent<MagicCardView>();
            if (kind == PlaylistCardKind.Marker)
            {
                v._label = MagicLook.Label(go.transform, "Goal", 46f, MagicLook.Lilac, 2.4f, display: false);
                v._w = 1.2f; v._h = 0.12f;
                return v;
            }
            v._w = kind == PlaylistCardKind.Boss ? 1.15f : kind == PlaylistCardKind.Intro ? 1.05f : 0.95f;
            v._h = kind == PlaylistCardKind.Boss ? 0.6f : kind == PlaylistCardKind.Intro ? 0.52f : 0.36f;   // tall enough that the 3·2·1 sits between title and goal
            v._board = MagicBoard.Create(go.transform, v._w, v._h);
            if (kind == PlaylistCardKind.Boss && v._board.title != null) v._board.title.color = MagicLook.Gold;
            // Readable over ANY room and any attraction (Aidan's rule; Oct 19 review): a dense pane, the goal in bright white,
            // and the card always drawn over the content — it is body-locked, 1.15 m in front of the eyes, nothing of the
            // attraction belongs in front of it (the platform's personal-card rule, BookendRoom: personal boards x-ray).
            // Dense enough behind the words to read over anything, never a slab over the view (Oct 19 recut: a fully
            // dense, 1.3×-wide pane 1.15 m from the eyes covered half the view): the rim feathers just past the text.
            v._board.SetGlassDensity(GlassDensity);
            v._board.SetGlassSpread(GlassSpreadX, GlassSpreadY);
            if (v._board.foot != null)
            {
                v._board.foot.color = Color.white;
                v._board.foot.fontSize = 64f;
                v._board.foot.alignment = TMPro.TextAlignmentOptions.Top;
                v._footTop = -v._h * 0.5f + 0.1f + FootLine * 0.5f;
                v._board.foot.transform.localPosition = new Vector3(0f, v._footTop - FootLine * 0.5f, 0f);
            }
            v._board.SetXray(true);
            v._overAlways = true;
            // Aidan, Oct 19: the card's sparkle matches the playlist UI's (bright twinkling stars), and it leaves in a
            // burst of them that fades — the panel itself gone at the pop.
            v._board.UseCardSparkles();
            v._board.PopExit = true;
            return v;
        }

        public void Show(string title, string headline, string foot)
        {
            if (_label != null) { if (_label.text != headline) _label.text = headline ?? ""; return; }
            if (_board == null) return;
            _board.SetTitle(title);
            _board.SetHeadline(headline);      // a count ("3", "2", "1", "GO!") bursts in light
            _board.SetFoot(foot);
            FitFoot();
        }

        // The goal never truncates: the card grows downward by every line past the first (SetBelow keeps the top put).
        public const float GlassDensity = 0.82f, GlassSpreadX = 1.0f, GlassSpreadY = 1.08f;
        public const float FootLine = 0.075f;   // one line of the 64-pt goal, metres
        float _footTop, _below;
        bool _overAlways;
        string _fitted;
        void FitFoot()
        {
            var f = _board?.foot;
            var hl = _board?.headline;
            string key = (f?.text ?? "") + "|" + (hl?.text ?? "");
            if (f == null || key == _fitted) return;
            _fitted = key;
            float wrapUnits = f.rectTransform.sizeDelta.x;
            float pref = string.IsNullOrEmpty(f.text) ? 0f : f.GetPreferredValues(f.text, wrapUnits, 0f).y * f.transform.localScale.y;
            // Never on the headline (Oct 19 final cut: on the short personal card "Find the 15 hidden coins!" and
            // "Your team: 1/15 coins" drew over each other): the goal starts below the headline's measured bottom.
            float top = _footTop;
            if (hl != null && !string.IsNullOrEmpty(hl.text))
            {
                float hh = hl.GetPreferredValues(hl.text, hl.rectTransform.sizeDelta.x, 0f).y * hl.transform.localScale.y;
                top = Mathf.Min(top, hl.transform.localPosition.y - hh * 0.5f - FootGap);
            }
            float bottomPad = 0.1f - FootLine * 0.5f;                    // the card's own margin under its goal
            _below = Mathf.Max(0f, -_h * 0.5f - (top - pref - bottomPad));
            _room = top - (-_h * 0.5f - _below + bottomPad);
            f.rectTransform.sizeDelta = new Vector2(wrapUnits, Mathf.Max(14f, pref / Mathf.Max(1e-4f, f.transform.localScale.y) + 1f));
            f.transform.localPosition = new Vector3(0f, top - pref * 0.5f, 0f);
            _board.SetBelow(_below);
        }
        public const float FootGap = 0.02f;
        float _room;

        /// The goal's measured height vs. the card's room for it (checks): true = it fits without truncation.
        public bool FootFits(out float need, out float room)
        {
            var f = _board?.foot;
            need = f == null || string.IsNullOrEmpty(f.text) ? 0f : f.GetPreferredValues(f.text, f.rectTransform.sizeDelta.x, 0f).y * f.transform.localScale.y;
            room = _room > 0f ? _room : FootLine + _below;
            return need <= room + 0.002f;
        }

        public void SetFade(float alpha)
        {
            if (_label != null) _label.alpha = alpha;   // the board draws itself in and dissolves on Leave
        }

        /// Rays to the card's full extent: the 4 corners, the 4 edge midpoints and the centre (+ quarter points).
        public static List<Vector3> Points(Vector3 centre, Vector3 right, float w, float h)
        {
            var pts = new List<Vector3>(15);
            for (int iy = 0; iy < 3; iy++)
                for (int ix = 0; ix < 5; ix++)
                    pts.Add(centre + right * ((ix / 4f - 0.5f) * w) + Vector3.up * ((iy / 2f - 0.5f) * h));
            return pts;
        }

        /// The share (0..1) of eye → card-point rays no occluder box blocks.
        public static float VisibleShare(List<Bounds> occ, Vector3 eye, Vector3 centre, Vector3 right, float w, float h)
        {
            int clear = 0, total = 0;
            foreach (Vector3 p in Points(centre, right, w, h))
            {
                total++;
                Vector3 d = p - eye;
                float dist = d.magnitude;
                if (dist < 1e-3f) { clear++; continue; }
                var ray = new Ray(eye, d / dist);
                bool hit = false;
                if (occ != null)
                    foreach (Bounds b in occ)
                        if (b.IntersectRay(ray, out float t) && t < dist - 0.02f) { hit = true; break; }
                if (!hit) clear++;
            }
            return total == 0 ? 1f : (float)clear / total;
        }

        /// Half-angle (degrees) the whole card must stay within around the wearer's straight-ahead (the desired spot's
        /// direction): a clear spot off the side of the view is no better than a blocked one.
        public const float ViewHalfAngle = 32f;

        public static bool InView(Vector3 eye, Vector3 desired, Vector3 centre, Vector3 right, float w, float h)
        {
            Vector3 ahead = (desired - eye).normalized;
            foreach (Vector3 p in new[] { centre + right * (w * 0.5f), centre - right * (w * 0.5f), centre + Vector3.up * (h * 0.5f) })
                if (Vector3.Angle(ahead, p - eye) > ViewHalfAngle) return false;
            return true;
        }

        /// The nearby spots tried when the desired one is blocked: closer, higher, either side, and combinations.
        public static IEnumerable<Vector3> Offsets(Vector3 eye, Vector3 desired, Vector3 right)
        {
            Vector3 toEye = eye - desired; toEye.y = 0f;
            Vector3 back = toEye.sqrMagnitude > 1e-4f ? toEye.normalized : Vector3.zero;   // towards the eye
            yield return back * 0.3f;
            yield return Vector3.up * 0.25f;
            yield return back * 0.3f + Vector3.up * 0.2f;
            yield return right * 0.4f;
            yield return -right * 0.4f;
            yield return Vector3.up * 0.45f;
            yield return back * 0.45f + Vector3.up * 0.3f;
            yield return right * 0.4f + Vector3.up * 0.3f;
            yield return -right * 0.4f + Vector3.up * 0.3f;
        }

        public Vector3 Place(Vector3 eye, Vector3 desired, Vector3 right)
        {
            if (_leaveAt >= 0) return desired + _offset;
            Vector3 c = desired + _offset;
            // Bosses animate: the occluder boxes are re-read every frame while the card shows (cards are short-lived).
            if (Time.frameCount != _occFrame)
            {
                _occFrame = Time.frameCount; _occAround = c;
                var eyes = new List<Vector3> { eye };
                _occ = MagicSight.Occluders(c, 3f, eyes, eye.y - MagicSight.EyeHeight);
            }
            // A body-locked card may move; the marker stays over its stop. A card drawn over everything (_overAlways)
            // never dodges: nothing can hide it, and the dodge brought it closer to the eyes (Oct 19: 0.89 m, a slab).
            bool body = _board != null && !_overAlways;
            float here = VisibleShare(_occ, eye, c, right, _w, _h);
            if (here < Clear && body)
            {
                // Back home first if the desired spot is clear again; else the clearest offset (earliest wins ties).
                float home = _offset == Vector3.zero ? here : VisibleShare(_occ, eye, desired, right, _w, _h);
                if (home >= Clear) { _offset = Vector3.zero; here = home; }
                else
                {
                    Vector3 best = _offset; float bestVis = here;
                    foreach (Vector3 o in Offsets(eye, desired, right))
                    {
                        if (!InView(eye, desired, desired + o, right, _w, _h)) continue;   // never move it out of sight
                        float v = VisibleShare(_occ, eye, desired + o, right, _w, _h);
                        if (v > bestVis + 1e-4f) { bestVis = v; best = o; if (v >= Clear) break; }
                    }
                    _offset = best; here = bestVis;
                }
            }
            else if (body && _offset != Vector3.zero && Time.frameCount % 15 == 0)
            {
                // Clear where it is: drift home when home is clear too.
                if (VisibleShare(_occ, eye, desired, right, _w, _h) >= Clear) _offset = Vector3.zero;
            }
            _visible = here;
            return desired + _offset;
        }

        /// The final decision at the card's ACTUAL pose this frame (after it eased toward the target): draw over now if
        /// any ray to its full extent is blocked; off only after 0.3 s clear.
        public void Seen(Vector3 eye)
        {
            float actual = VisibleShare(_occ, eye, transform.position, transform.right, _w, _h);
            _visible = Mathf.Min(_visible, actual);
            bool blocked = _visible < Clear;
            if (blocked) _clearSince = -1f; else if (_clearSince < 0f) _clearSince = Time.time;
            // On at once; off only after 0.3 s clear (no flicker at an edge).
            bool on = _overAlways || blocked || (_xray && Time.time - _clearSince < 0.3f);
            if (on != _xray)
            {
                _xray = on;
                if (_board != null) _board.SetXray(on); else MagicLook.SetXray(transform, on);
            }
        }

        public void Leave()
        {
            if (_leaveAt >= 0) return;
            _leaveAt = Time.time;
            if (_board != null) _board.Leave();
        }

        public bool Gone => _leaveAt >= 0 && (_board != null ? _board.Gone : Time.time - _leaveAt > 0.6f);
    }
}
