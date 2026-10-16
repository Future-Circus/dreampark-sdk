using System.Collections.Generic;
using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// The whole remote-player story, in one convention:
    ///
    /// Put a child named "RemoteRig" in your Player.prefab. Whatever you put
    /// inside it is what OTHER players see of you. Children named "Head",
    /// "LeftHand", "RightHand" and "Hand" (= whichever hand is in use) follow
    /// that player's tracked body from the SDK presence stream — hang your
    /// marker under Head, your blaster under Hand, done. Everything else in
    /// the subtree stays where you put it, relative to the rig. Lua scripts
    /// inside it run on the clones (they boot with `peer_id` = that
    /// player's id — feed it to dp.peer(peer_id) for their state), never on
    /// your own headset, so they need no local/remote branching at all.
    ///
    /// No components to add, no roles to tag. PlayerRig hides the template on
    /// its owner's rig; PlayerPresence clones it here once per remote player
    /// and this component drives the anchors, after the presence smoothing.
    ///
    /// The SDK strips its own machinery from clones (NetId, trackers, …) so a
    /// copied rig can never fight the stream or register duplicate net ids.
    /// Colliders and physics you author are kept as-is: a hitbox inside
    /// RemoteRig is how local projectiles get to hit remote players.
    /// </summary>
    public class RemoteRig : MonoBehaviour
    {
        public const string TemplateName = "RemoteRig";

        public string peerId;
        public string gameId;
        public RemotePlayer peer;

        Transform _head, _left, _right, _hand;

        void Awake()
        {
            _head = FindAnchor(transform, "head");
            _left = FindAnchor(transform, "lefthand");
            _right = FindAnchor(transform, "righthand");
            _hand = FindAnchor(transform, "hand");
        }

        // Oct 11 2026: the remote hand-held auto-mount (Handhelds, ticked by the body runner) used
        // to start only once something called dp.hold or dp.net_body, so a title that did neither
        // (LaserTag) never re-mounted a peer's gun when they switched hands. A clone starts it.
        void OnEnable()
        {
            DreamParkBodyRunner.Ensure();
            Handhelds.ScanRemoteSoon();
        }

        void LateUpdate()
        {
            if (peer == null) return;
            Drive(_head, peer.head, true);
            Drive(_left, peer.leftHand, peer.leftTracked);
            Drive(_right, peer.rightHand, peer.rightTracked);
            Drive(_hand, peer.ActiveHandTransform, peer.activeHand == "l" ? peer.leftTracked : peer.rightTracked);
        }

        static void Drive(Transform node, Transform src, bool shown)
        {
            if (node == null || src == null) return;
            // The local HandTracker hides what is in an untracked hand; the
            // clone reads the same.
            if (node.gameObject.activeSelf != shown) node.gameObject.SetActive(shown);
            if (shown) node.SetPositionAndRotation(src.position, src.rotation);
        }

        // ── Naming ──────────────────────────────────────────────────
        // Forgiving on purpose: "RemoteRig", "Remote Rig", "remote_rig" and
        // "LeftHand" / "Left Hand" / "left_hand" all match.
        static string Normalize(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '_' || c == '-') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        static Transform FindNamed(Transform root, string normalized, bool skipTemplates)
        {
            // Breadth-first: the shallowest match wins, which is always the
            // one the creator can see at a glance in the hierarchy.
            var queue = new Queue<Transform>();
            for (int i = 0; i < root.childCount; i++) queue.Enqueue(root.GetChild(i));
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                if (Normalize(t.name) == normalized) return t;
                if (skipTemplates && Normalize(t.name) == "remoterig") continue;   // never reach into a nested template
                for (int i = 0; i < t.childCount; i++) queue.Enqueue(t.GetChild(i));
            }
            return null;
        }

        static Transform FindAnchor(Transform root, string normalized) => FindNamed(root, normalized, false);

        /// <summary>The RemoteRig template inside a rig hierarchy, or null.</summary>
        public static Transform FindTemplate(Transform rigRoot) => FindNamed(rigRoot, "remoterig", false);

        // ── Building ────────────────────────────────────────────────

        /// <summary>
        /// Clone <paramref name="rig"/>'s RemoteRig template for
        /// <paramref name="peer"/>. Returns null when the rig has no template.
        /// </summary>
        public static GameObject Build(PlayerRig rig, RemotePlayer peer)
        {
            var template = rig.remoteRigTemplate != null ? rig.remoteRigTemplate : FindTemplate(rig.transform);
            if (template == null) return null;
            return Build(template, rig.gameId, peer);
        }

        // ── Per-device templates ────────────────────────────────────
        //
        // By default a remote player is drawn with the RemoteRig of the rig
        // THIS device wears for their game. A title with a Phone Player has a
        // second rig (and a second RemoteRig — "what headset players see of a
        // phone user"), which a headset never wears. A host app that loads it
        // registers its RemoteRig here, keyed by the device it represents, and
        // RemotePlayer prefers it for peers on that device.
        //
        // Register an INSTANTIATED rig's template (kept inactive, e.g. under
        // an inactive holder), never a prefab asset: Build deactivates the
        // template before cloning.
        static readonly Dictionary<string, Transform> _templates = new Dictionary<string, Transform>();

        static string TemplateKey(string gameId, string device) => (gameId ?? "") + "|" + (device ?? "");

        /// <summary>Use <paramref name="template"/> (a RemoteRig subtree) for players of <paramref name="gameId"/> on <paramref name="device"/> ("phone" / "headset").</summary>
        public static void RegisterTemplate(string gameId, string device, Transform template)
        {
            if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(device)) return;
            if (template == null) { if (_templates.Remove(TemplateKey(gameId, device))) TemplateChanged(gameId, device); return; }
            _templates.TryGetValue(TemplateKey(gameId, device), out var old);
            _templates[TemplateKey(gameId, device)] = template;
            if (old != template) TemplateChanged(gameId, device);
        }

        public static void UnregisterTemplate(string gameId, string device)
        {
            if (_templates.Remove(TemplateKey(gameId, device))) TemplateChanged(gameId, device);
        }

        /// <summary>
        /// Oct 11 2026: the Phone Player template loads fire-and-forget, so a
        /// phone's presence can land first — its RemotePlayer then built with
        /// what was there (nothing, for a phone on a headset) and marked the
        /// game done, forever. Every live player of that game on that device
        /// rebuilds against the new template on its next frame.
        /// </summary>
        static void TemplateChanged(string gameId, string device)
        {
            var presence = PlayerPresence.Instance;
            if (presence == null) return;
            foreach (var p in presence.Peers.Values)
                if (p != null && TemplateAffects(p.gameId, p.device, gameId, device)) p.TemplateChanged();
        }

        /// <summary>Does a template registered for (<paramref name="gameId"/>, <paramref name="device"/>) change how a peer in <paramref name="peerGame"/> on <paramref name="peerDevice"/> is drawn?</summary>
        public static bool TemplateAffects(string peerGame, string peerDevice, string gameId, string device)
            => !string.IsNullOrEmpty(peerGame) && peerGame == gameId && peerDevice == device;

        /// <summary>The registered template for this game and device, or null.</summary>
        public static Transform TemplateFor(string gameId, string device)
        {
            if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(device)) return null;
            if (!_templates.TryGetValue(TemplateKey(gameId, device), out var t)) return null;
            if (t == null) { _templates.Remove(TemplateKey(gameId, device)); return null; }
            return t;
        }

        /// <summary>Clone an explicit RemoteRig <paramref name="template"/> for <paramref name="peer"/>.</summary>
        public static GameObject Build(Transform template, string gameId, RemotePlayer peer)
        {
            if (template == null || peer == null) return null;

            // The template is inactive (PlayerRig hides it in Awake; this is
            // the belt to that suspender), so the clone comes out inactive
            // too: nothing on it runs until it is placed and cleaned, and Lua
            // scripts inside boot on the clone only.
            if (template.gameObject.activeSelf) template.gameObject.SetActive(false);
            var clone = Instantiate(template.gameObject, peer.transform, false);
            clone.name = TemplateName + "_" + gameId;

            var rr = clone.AddComponent<RemoteRig>();
            rr.peerId = peer.id;
            rr.gameId = gameId;
            rr.peer = peer;

            StripSdkMachinery(clone);

            // Sit where the template sits on the local copy of that game's
            // rig — levels are park-synced, so that is the same physical spot
            // on every headset. The peer object is the park origin, so a park
            // re-sync moves the clone with the props.
            clone.transform.SetPositionAndRotation(template.position, template.rotation);

            clone.SetActive(true);
            return clone;
        }

        /// <summary>
        /// SDK components that must never run on a clone: identity would
        /// collide (NetId/NetScope), trackers would drag the clone to THIS
        /// headset's head and hands, a nested PlayerRig would register itself.
        /// Creator-authored content is untouched.
        /// </summary>
        static void StripSdkMachinery(GameObject clone)
        {
            foreach (var c in clone.GetComponentsInChildren<Component>(true))
            {
                switch (c)
                {
                    case NetId _:
                    case NetScope _:
                    case PlayerRig _:
                    case HeadTracker _:
                    case HandTracker _:
                    case BodyTracker _:
                    case FeetTracker _:
                        DestroyImmediate(c);
                        break;
                }
            }
        }
    }
}
