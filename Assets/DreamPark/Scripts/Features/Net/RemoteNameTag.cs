using UnityEngine;
using TMPro;

namespace DreamPark
{
    /// <summary>
    /// The name floating over every remote player's head. Instant presence:
    /// the park feels multiplayer the moment two headsets share a relay, even
    /// when no game has a single multiplayer feature — you can see who
    /// somebody is before any game does anything.
    ///
    /// Created by <see cref="RemotePlayer"/> for every peer; never authored.
    /// The name is whatever the peer's ProfileAPI reports, or a stable
    /// "Guest XXXX" derived from their presence id (PlayerPresence sends it
    /// on the state cadence, so late joiners get it within a couple of
    /// seconds). Yaw-billboarded to this headset's camera, hidden while the
    /// peer has produced no pose yet.
    ///
    /// A game that wants its own treatment can switch the default off with
    /// <see cref="show"/> (e.g. from core settings) — its RemoteRig can then
    /// carry whatever nameplate it likes.
    /// </summary>
    public class RemoteNameTag : MonoBehaviour
    {
        /// <summary>Park-wide switch for the default tags.</summary>
        public static bool show = true;

        public RemotePlayer peer;
        [Tooltip("Metres above the head anchor.")]
        public float height = 0.28f;

        TextMeshPro _text;
        string _shown;

        public static RemoteNameTag Attach(RemotePlayer peer)
        {
            var go = new GameObject("NameTag");
            // Under the peer root (the park origin), not under the head:
            // the tag follows the head's position but must never inherit its
            // pitch and roll.
            go.transform.SetParent(peer.transform, false);
            var tag = go.AddComponent<RemoteNameTag>();
            tag.peer = peer;
            return tag;
        }

        void Awake()
        {
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(transform, false);
            _text = textGo.AddComponent<TextMeshPro>();
            _text.text = "";
            _text.fontSize = 1.1f;
            _text.alignment = TextAlignmentOptions.Center;
            _text.textWrappingMode = TextWrappingModes.NoWrap;
            _text.rectTransform.sizeDelta = new Vector2(2f, 0.3f);
            // Default font asset comes from TMP settings; colour reads on any
            // background at park lighting.
            _text.color = Color.white;
            _text.outlineWidth = 0.2f;
        }

        void LateUpdate()
        {
            if (peer == null || peer.head == null || _text == null) { return; }

            bool visible = show && peer.seq != 0;
            if (_text.gameObject.activeSelf != visible) _text.gameObject.SetActive(visible);
            if (!visible) return;

            transform.position = peer.head.position + new Vector3(0f, height, 0f);

            // Yaw-only billboard: readable from anywhere, never tilted.
            var cam = Camera.main;
            if (cam != null)
            {
                var to = transform.position - cam.transform.position;
                to.y = 0f;
                if (to.sqrMagnitude > 0.0001f)
                    transform.rotation = Quaternion.LookRotation(to.normalized, Vector3.up);
            }

            string name = peer.displayName;
            if (string.IsNullOrEmpty(name)) name = FallbackName(peer.id);
            if (name != _shown)
            {
                _shown = name;
                _text.text = name;
            }
        }

        /// <summary>"Guest 3F2A" — stable per headset, derived from the presence id.</summary>
        public static string FallbackName(string peerId)
        {
            if (string.IsNullOrEmpty(peerId)) return "Guest";
            var tail = peerId.Length <= 4 ? peerId : peerId.Substring(peerId.Length - 4);
            return "Guest " + tail.ToUpperInvariant();
        }
    }
}
