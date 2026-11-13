// MOVING TO SAL; NOT PLATFORM (PLAYLIST-PLAN § 6.1.5 / § 6.3.5, Oct 21 2026): games own their instruction UI. Kept until
// SuperAdventureLand ports this card code into SAL; the platform shows no objective cards once pl-sets switches
// off the PlaylistObjectiveCallout call sites. Don't add features here.
using System;
using TMPro;
using UnityEngine;

namespace DreamPark
{
    /// The SDK's default look: glowing violet-outlined text, no panel (the creators' simulator; core skins it).
    public sealed class GlowTextView : MonoBehaviour, IPlaylistCardView
    {
        TextMeshPro _title, _headline, _foot;
        float _alpha = 1f, _leaveAt = -1f, _swellAt = -10f;
        string _lastHeadline;

        public static GlowTextView Create(Transform parent, PlaylistCardKind kind)
        {
            var go = new GameObject("CardText");
            go.transform.SetParent(parent, false);
            var v = go.AddComponent<GlowTextView>();
            bool marker = kind == PlaylistCardKind.Marker, boss = kind == PlaylistCardKind.Boss;
            v._title = marker ? null : Text(go.transform, "Title", boss ? 1.5f : 1.0f,
                boss ? new Color(1f, 0.8f, 0.45f) : PlaylistObjectiveCallout.Violet, 0.13f);
            v._headline = Text(go.transform, "Goal", marker ? 0.75f : boss ? 1.05f : 0.95f, PlaylistObjectiveCallout.Face, marker ? 0f : -0.02f);
            v._foot = marker ? null : Text(go.transform, "Foot", 0.8f, PlaylistObjectiveCallout.Face, -0.19f);
            return v;
        }

        static TextMeshPro Text(Transform parent, string name, float size, Color c, float y)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0, y, 0);
            var t = go.AddComponent<TextMeshPro>();
            t.alignment = TextAlignmentOptions.Center;
            t.fontSize = size;
            t.fontStyle = FontStyles.Bold;
            t.enableWordWrapping = false;
            t.color = c;
            t.outlineWidth = 0.16f;
            t.outlineColor = PlaylistObjectiveCallout.Violet;
            try
            {
                var m = t.fontMaterial;
                m.EnableKeyword("UNDERLAY_ON");
                m.SetColor("_UnderlayColor", new Color(0.62f, 0.42f, 1f, 0.6f));
                m.SetFloat("_UnderlaySoftness", 0.9f);
                m.SetFloat("_UnderlayDilate", 0.65f);
            }
            catch (Exception) { }
            t.alpha = 0f;
            return t;
        }

        public void Show(string title, string headline, string foot)
        {
            if (_title != null && _title.text != title) _title.text = title ?? "";
            if (_headline != null && _headline.text != headline)
            {
                _headline.text = headline ?? "";
                if (headline != _lastHeadline && headline != null && headline.Length <= 3) _swellAt = Time.time;
                _lastHeadline = headline;
            }
            if (_foot != null && _foot.text != foot) _foot.text = foot ?? "";
        }

        public void SetFade(float alpha) { _alpha = alpha; }
        public Vector3 Place(Vector3 eye, Vector3 desired, Vector3 right) => desired;
        public float Visible => 1f;
        public void Seen(Vector3 eye) { }
        public void Leave() { if (_leaveAt < 0) _leaveAt = Time.time; }
        public bool Gone => _leaveAt >= 0 && Time.time - _leaveAt > 0.6f;

        void LateUpdate()
        {
            float a = _alpha;
            if (_title != null) _title.alpha = a;
            if (_foot != null) _foot.alpha = a;
            if (_headline != null)
            {
                _headline.alpha = a;
                float k = Mathf.Clamp01((Time.time - _swellAt) / 0.35f);
                _headline.transform.localScale = Vector3.one * (k < 1f ? 0.8f + 0.2f * k * k * (3f - 2f * k) : 1f);
            }
        }
    }
}
