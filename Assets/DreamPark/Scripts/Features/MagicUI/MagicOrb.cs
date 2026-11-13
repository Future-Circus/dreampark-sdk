using TMPro;
using UnityEngine;

namespace DreamPark.MagicUI
{
    /// <summary>Marks a Magic UI root: sight checks never count it (or anything under it) as an occluder.</summary>
    public sealed class MagicUIRoot : MonoBehaviour { }

    /// <summary>
    /// A choice as a glowing ORB of light (no button chrome): it brightens and swells under the viewer's gaze, a
    /// line of light fills under its label as the dwell builds, and it confirms with a burst on its board.
    /// Pressed by GAZE-DWELL or a fingertip POKE (<see cref="MagicPress"/>: the one shared button behaviour; no
    /// controllers, never the DreamBand). Call <see cref="Tick"/> every frame.
    /// </summary>
    public sealed class MagicOrb
    {
        public const float DefaultDwell = MagicPress.DefaultDwell, ConeDeg = MagicPress.ConeDeg;
        public readonly Transform transform;
        readonly Material _orb, _fillMat;
        readonly Transform _fill;
        readonly bool _primary;
        readonly MagicPress _press;
        readonly TextMeshPro _label;
        readonly float _w, _h;
        /// A TAG, not a button (Aidan, Oct 19 2026: "STOP USING THIS DESIGN UI ELEMENT … just use the icon + text"):
        /// no orb, no heart, no capsule of any kind behind it — the icon and the words alone. It stays a gaze / poke
        /// control; picked = bold, bright violet, with the dwell line held full under it (an underline).
        readonly bool _bare;
        Material _iconMat;
        float _contentW, _iconPart, _lineLeft;

        public MagicOrb(Transform parent, string text, Vector3 pos, bool primary, float w = 0.7f, float h = 0.17f, float font = 70f,
            float dwell = DefaultDwell, bool bare = false)
        {
            _w = w; _h = h; _primary = primary; _bare = bare; _press = new MagicPress(dwell);
            transform = new GameObject(text).transform;
            transform.SetParent(parent, false);
            transform.localPosition = pos;
            Mesh q = MagicLook.Quad();
            if (MagicLook.Holo != null && bare)
            {
                _fillMat = MagicLook.HoloMat(MagicLook.LineTexture(), MagicLook.VioletBright, true, MagicLook.QGlow + 5);
                _fill = MagicLook.Plate(transform, "Dwell", q, _fillMat, new Vector3(0, -font * 0.00062f, 0.001f), Quaternion.identity, new Vector3(0.001f, 0.014f, 1)).transform;   // just under the words
            }
            else if (MagicLook.Holo != null)
            {
                _orb = MagicLook.HoloMat(MagicLook.OrbPillTexture(), new Color(MagicLook.Violet.r, MagicLook.Violet.g, MagicLook.Violet.b, primary ? 0.75f : 0.45f), true, MagicLook.QGlow + 4);
                MagicLook.Plate(transform, "Orb", q, _orb, new Vector3(0, 0, 0.004f), Quaternion.identity, new Vector3(w * 1.25f, h * 1.6f, 1));
                MagicLook.Plate(transform, "Heart", q, MagicLook.HoloMat(MagicLook.OrbPillTexture(), new Color(0.07f, 0.03f, 0.15f, 0.7f), false, MagicLook.QGlass + 4),
                    new Vector3(0, 0, 0.005f), Quaternion.identity, new Vector3(w, h, 1));
                _fillMat = MagicLook.HoloMat(MagicLook.LineTexture(), MagicLook.Lilac, true, MagicLook.QGlow + 5);
                _fill = MagicLook.Plate(transform, "Dwell", q, _fillMat, new Vector3(0, -h * 0.5f + 0.012f, 0.001f), Quaternion.identity, new Vector3(0.001f, 0.02f, 1)).transform;
            }
            TextMeshPro label = MagicLook.Label(transform, "Label", font, Color.white, w, display: primary);
            _label = label;
            label.text = text;
            label.transform.localPosition = new Vector3(0, 0.005f, 0);
            label.gameObject.AddComponent<TextResolve>();
        }

        /// An icon left of the label (e.g. the thumb glyph; <paramref name="flip"/> turns it upside down).
        public void AddIcon(Texture2D icon, bool flip, float x, float size, Color color)
        {
            if (MagicLook.Holo == null || icon == null) return;
            _iconMat = MagicLook.HoloMat(icon, color, false, MagicLook.QGlow + 6);
            MagicLook.Plate(transform, "Icon", MagicLook.Quad(), _iconMat,
                new Vector3(x, 0, -0.001f), Quaternion.Euler(0, 0, flip ? 180f : 0f), Vector3.one * size);
        }

        /// The app's button layout: the icon BESIDE the label, the pair centred in the button (`thumb("Fun!", …)`).
        public void AddIconBeside(Texture2D icon, bool flip, float size, Color color, float gap = 0.04f)
        {
            if (_label == null) return;
            float tw = _label.GetPreferredValues(_label.text).x * MagicLook.TextUnitScale;
            float group = size + gap + tw;
            _contentW = group; _iconPart = size + gap;
            float iconX = -group * 0.5f + size * 0.5f;
            _lineLeft = -group * 0.5f;
            AddIcon(icon, flip, iconX, size, color);
            if (_bare)
            {
                // A tag's words start right after its icon (left-aligned), so going bold when picked grows them to the
                // right, never back over the icon.
                _label.alignment = TextAlignmentOptions.Left;
                float left = iconX + size * 0.5f + gap, rectW = _label.rectTransform.sizeDelta.x * MagicLook.TextUnitScale;
                _label.transform.localPosition = new Vector3(left + rectW * 0.5f, _label.transform.localPosition.y, _label.transform.localPosition.z);
                return;
            }
            _label.transform.localPosition = new Vector3(iconX + size * 0.5f + gap + tw * 0.5f, _label.transform.localPosition.y, _label.transform.localPosition.z);
        }

        /// True once, when the viewer's gaze has dwelt on it or a fingertip poked it. <paramref name="chosen"/> = it is
        /// this viewer's pick.
        public bool Tick(bool chosen)
        {
            bool fired = _press.Tick(transform, new Vector2(_w * 0.5f, _h * 0.5f), Time.deltaTime);
            float k = chosen ? 1f : _press.DwellK;
            float confirm = _press.Confirm;
            if (_fill != null)
            {
                // A tag's line runs under its icon + words only (the underline), a button's under most of its width.
                if (_bare && _contentW <= 0f && _label != null) _contentW = _label.GetPreferredValues(_label.text).x * MagicLook.TextUnitScale;
                float full = _bare ? Mathf.Min(_w, _contentW) : _w * 0.8f;
                _fill.localScale = new Vector3(Mathf.Max(0.001f, full * k), _bare ? 0.014f : 0.02f, 1);
                // A tag's line starts at its icon's left edge (the words grow to the right when picked).
                float x0 = _bare && _iconPart > 0f ? _lineLeft : -full * 0.5f;
                _fill.localPosition = new Vector3(x0 + full * 0.5f * k, _fill.localPosition.y, 0.001f);
            }
            if (_bare)
            {
                // No capsule: the state is in the words. Picked = bold + bright violet; a dwell brightens toward it.
                Color c = chosen ? MagicLook.VioletBright : Color.Lerp(MagicLook.TextSecondary, Color.white, Mathf.Max(k, _press.PokeK));
                if (_label != null)
                {
                    if (_label.color != c) _label.color = c;
                    FontStyles fs = chosen ? FontStyles.Bold : FontStyles.Normal;
                    if (_label.fontStyle != fs)
                    {
                        _label.fontStyle = fs;
                        // The underline spans the icon + the words at their new weight.
                        if (_iconPart > 0f) _contentW = _iconPart + _label.GetPreferredValues(_label.text).x * MagicLook.TextUnitScale;
                    }
                }
                if (_iconMat != null) _iconMat.SetColor("_BaseColor", c);
            }
            if (_orb != null)
            {
                Color c = Color.Lerp(MagicLook.Violet, MagicLook.Lilac, Mathf.Max(k * 0.5f, Mathf.Max(confirm, _press.PokeK)));
                c.a = Mathf.Clamp01((_primary ? 0.75f : 0.45f) + 0.45f * Mathf.Max(k, _press.PokeK) + (chosen ? 0.25f : 0f) + confirm * 0.6f);
                _orb.SetColor("_BaseColor", c);
            }
            // A dwell swells it; a poke presses it in (then the same confirm pop).
            float scale = 1f + 0.07f * k + (chosen ? 0.05f : 0f) + 0.12f * confirm * confirm - 0.08f * _press.PokeK;
            transform.localScale = Vector3.one * scale;
            if (fired)
            {
                var board = transform.GetComponentInParent<MagicBoard>();
                if (board != null) board.Pop(board.transform.InverseTransformPoint(transform.position));
            }
            return fired;
        }
    }
}
