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

            // The template is inactive (PlayerRig hides it in Awake; this is
            // the belt to that suspender), so the clone comes out inactive
            // too: nothing on it runs until it is placed and cleaned, and Lua
            // scripts inside boot on the clone only.
            if (template.gameObject.activeSelf) template.gameObject.SetActive(false);
            var clone = Instantiate(template.gameObject, peer.transform, false);
            clone.name = TemplateName + "_" + rig.gameId;

            var rr = clone.AddComponent<RemoteRig>();
            rr.peerId = peer.id;
            rr.gameId = rig.gameId;
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
