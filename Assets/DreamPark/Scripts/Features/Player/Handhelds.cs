using System.Collections.Generic;
using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// The engine side of Lua <c>dp.hold(prop, side[, hand_node])</c>, <c>dp.release_hold(prop)</c>
    /// and <c>dp.muzzle(prop)</c>. Not a component: a static service ticked by
    /// <see cref="DreamParkBodyRunner"/> (LateUpdate). The maths is <see cref="HandheldMath"/>.
    ///
    /// Hold puts the prop in a hand per HANDHELD.md: its Grip on the fist centre, its +Z along
    /// the fingers, its +Y on the thumb side, for the side in use, never with a −1 scale.
    /// <list type="bullet">
    /// <item>side "left" / "right" is fixed; "active" (the default) follows the hand in use: a
    ///   BothHands HandTracker's driving anchor, or RemoteRig/Hand's peer active hand, and
    ///   re-mounts the moment it changes.</item>
    /// <item>hand node: the given one, else the nearest hand node above the prop (HandTracker,
    ///   RemoteRig LeftHand/RightHand/Hand), else the LOCAL rig's tracker for that side — the
    ///   prop is then reparented under it ("pick it up"). Inside a RemoteRig clone it is always
    ///   that clone's hand (Hand for "active"; without one, LeftHand/RightHand, hopping sides).</item>
    /// <item>HandTracker.flipVisual is honoured (the node frame is raw * flip while the left
    ///   anchor drives it); RemoteRig hand nodes are raw.</item>
    /// <item>Remote copies: any object with a <c>Grip</c> child directly under a RemoteRig clone's
    ///   LeftHand / RightHand / Hand is mounted automatically for that node's side (Hand =
    ///   live active hand), so what other players see matches without any script.</item>
    /// </list>
    /// Only the prop ROOT transform is written; recoil and spin belong on its Model child.
    /// </summary>
    public static class Handhelds
    {
        public enum SideMode { Active, Left, Right }

        sealed class HoldState
        {
            public Transform prop;
            public Transform node;
            public HandheldMath.HandNodeKind kind;
            public HandTracker tracker;
            public RemoteRig remote;
            public SideMode mode;
            public bool auto;
            public bool remoteSwap;                // 'active' on a RemoteRig with no Hand node: hop LeftHand <-> RightHand
            public Vector3? targetRight;           // per-prop calibration (right-hand value)
            public Transform offHand;              // two-handed: the off hand node (Grip2)
            public HandTracker offTracker;
            public bool offLeft;

            public bool mounted;
            public bool mountedLeft;
            public bool mountedFlip;
            public Transform mountedParent;
        }

        static readonly Dictionary<int, HoldState> _holds = new Dictionary<int, HoldState>();

        // Rigs with only a BothHands tracker (ArcadeLand, Zombiez) have no per-side hand node.
        // dp.hold(prop,'left'|'right') used to mount on that tracker — i.e. on whichever hand
        // was ACTIVE, with the named side's maths. A named side now mounts on a follow node
        // under the rig that copies that hand's anchor (dp.hand(side)) every LateUpdate: the
        // raw OpenXR wrist, exactly the frame of a per-side HandTracker without flipVisual.
        // The node lives under the PlayerRig so the prop still hides with the rig.
        sealed class SideFollow { public Transform node; public Transform anchor; }
        static readonly List<SideFollow> _follows = new List<SideFollow>();
        internal const string FollowLeftName = "Handheld Follow (left)";
        internal const string FollowRightName = "Handheld Follow (right)";
        static readonly List<int> _dead = new List<int>();
        static float _nextRemoteScan;

        /// <summary>Mount Grip props under RemoteRig clone hands automatically (see class doc).</summary>
        public static bool AutoMountRemote = true;

        /// <summary>Scan RemoteRig clones on the next tick (a clone was just enabled).</summary>
        public static void ScanRemoteSoon() => _nextRemoteScan = 0f;

        /// <summary>Number of props currently managed (tests / diagnostics).</summary>
        public static int Count => _holds.Count;

        public static SideMode ParseSide(string side)
        {
            if (string.IsNullOrEmpty(side)) return SideMode.Active;
            switch (char.ToLowerInvariant(side[0]))
            {
                case 'l': return SideMode.Left;
                case 'r': return SideMode.Right;
                default: return SideMode.Active;     // "active", "a", "both", "hand"
            }
        }

        /// <summary>
        /// dp.hold(prop, side[, hand_node]). Returns false (and logs why) when no hand node can
        /// be found. Idempotent: holding again updates side/node and re-mounts.
        /// </summary>
        public static bool Hold(GameObject prop, string side = "active", Transform handNode = null)
            => Hold(prop, ParseSide(side), handNode, false);

        public static bool Hold(GameObject prop, SideMode mode, Transform handNode, bool auto)
        {
            if (prop == null) return false;
            Transform p = prop.transform;
            HandheldMath.HandNodeKind kind;
            HandTracker tracker;
            Transform node = handNode;
            bool remoteSwap = false;

            if (node != null)
            {
                // An explicit node that is not itself a hand (a jointed HandTouch child) is
                // resolved to the hand above it below; it stays the prop's parent.
                kind = HandheldMath.KindOf(node, out tracker);
            }
            else
            {
                node = HandheldMath.FindHandNode(p, out kind, out tracker);
                var rr = p.GetComponentInParent<RemoteRig>(true);
                if (rr != null && (node == null || (mode != SideMode.Active && kind == HandheldMath.HandNodeKind.Active)))
                {
                    // Inside a RemoteRig clone: that player's hands, never this headset's rig. A named
                    // side under their Hand (active) node uses their LeftHand / RightHand when present.
                    var named = RemoteHand(rr, mode, out var nk, out remoteSwap);
                    if (named != null || node == null) { node = named; kind = nk; tracker = null; }
                }
                else if (node == null) node = LocalHandFor(mode, out kind, out tracker);
                else if (mode != SideMode.Active && kind == HandheldMath.HandNodeKind.Tracker && tracker != null
                         && tracker.handPreference != HandTracker.HandPreference.Left
                         && tracker.handPreference != HandTracker.HandPreference.Right
                         && tracker.GetComponentInParent<RemoteRig>(true) == null)
                {
                    // Authored under the local rig's BothHands tracker but asked for a named side:
                    // that tracker follows the ACTIVE hand. Use the named hand (see LocalHandFor).
                    var rig = DreamParkLuaAPI.Player();
                    if (rig != null && tracker.transform.IsChildOf(rig.transform))
                    {
                        var named = LocalHandFor(mode, out var nk, out var nt);
                        if (named != null) { node = named; kind = nk; tracker = nt; }
                    }
                }
            }

            if (node == null)
            {
                Debug.LogWarning($"[Handhelds] dp.hold('{prop.name}'): no hand to hold it in (no HandTracker / RemoteRig hand above it and no local Player rig).");
                return false;
            }

            // Frame node: the hand node itself, or (for an explicit non-hand parent) the hand above it.
            Transform frameNode = node;
            if (kind == HandheldMath.HandNodeKind.None)
            {
                frameNode = HandheldMath.FindHandNode(node, out kind, out tracker);
                if (frameNode == null)
                {
                    // Not under any hand: treat the given node as a raw wrist frame (builders, tests).
                    frameNode = node;
                    kind = mode == SideMode.Left ? HandheldMath.HandNodeKind.Left
                         : mode == SideMode.Right ? HandheldMath.HandNodeKind.Right
                         : HandheldMath.HandNodeKind.Active;
                }
            }

            if (!p.IsChildOf(node)) p.SetParent(node, true);

            var h = new HoldState
            {
                prop = p,
                node = frameNode,
                kind = kind,
                tracker = tracker,
                remote = frameNode.GetComponentInParent<RemoteRig>(true),
                mode = mode,
                auto = auto,
                remoteSwap = remoteSwap,
            };
            _holds[prop.GetInstanceID()] = h;
            Apply(h, force: true);
            DreamParkBodyRunner.Ensure();
            return true;
        }

        /// <summary>
        /// Two-handed hold (two_hand_aim.lua.txt): mount in the main hand as <see cref="Hold(GameObject,string,Transform)"/>,
        /// and while <paramref name="offHand"/> (the other hand node) is tracked, pivot about the
        /// main fist so Grip → Grip2 points at the off fist. Use per-side nodes, not BothHands.
        /// </summary>
        public static bool HoldTwoHanded(GameObject prop, string side, Transform offHand, Transform handNode = null)
        {
            if (!Hold(prop, side, handNode)) return false;
            var h = _holds[prop.GetInstanceID()];
            h.offHand = offHand;
            h.offTracker = null;
            h.offLeft = true;
            if (offHand != null)
            {
                var k = HandheldMath.KindOf(offHand, out var ot);
                h.offTracker = ot;
                h.offLeft = k == HandheldMath.HandNodeKind.Tracker && ot != null
                    ? HandheldMath.TrackerIsLeft(ot)
                    : HandheldMath.Normalize(offHand.name).Contains("left");
            }
            Apply(h, force: true);
            return true;
        }

        /// <summary>
        /// Per-prop calibration: land the Grip on <paramref name="targetRight"/> (right-hand value,
        /// raw wrist frame, x mirrored on the left) instead of Meta's fist centre — for rigs tuned on
        /// device to another point (DreamSports' pivot). Pass null to go back to the convention.
        /// </summary>
        public static bool SetTarget(GameObject prop, Vector3? targetRight)
        {
            if (prop == null || !_holds.TryGetValue(prop.GetInstanceID(), out var h)) return false;
            h.targetRight = targetRight;
            Apply(h, force: true);
            return true;
        }

        /// <summary>dp.release_hold(prop): stop managing it. Leaves the prop where it is (parent and pose).</summary>
        public static bool Release(GameObject prop)
        {
            if (prop == null) return false;
            return _holds.Remove(prop.GetInstanceID());
        }

        public static bool IsHeld(GameObject prop) => prop != null && _holds.ContainsKey(prop.GetInstanceID());

        /// <summary>"left" / "right" for a held prop (the side it is mounted for now), else null.</summary>
        public static string Side(GameObject prop)
        {
            if (prop == null || !_holds.TryGetValue(prop.GetInstanceID(), out var h)) return null;
            return ResolveLeft(h) ? "left" : "right";
        }

        /// <summary>dp.muzzle(prop) → position, forward of its Muzzle (the root when it has none).</summary>
        public static bool Muzzle(GameObject prop, out Vector3 position, out Vector3 forward)
        {
            HandheldMath.Muzzle(prop != null ? prop.transform : null, out position, out forward);
            return prop != null;
        }

        public static Vector3 MuzzlePosition(GameObject prop) { Muzzle(prop, out var p, out _); return p; }
        public static Vector3 MuzzleForward(GameObject prop) { Muzzle(prop, out _, out var f); return f; }

        /// <summary>Re-mount everything whose side/frame changed. Called every LateUpdate by the runner.</summary>
        public static void Tick()
        {
            if (AutoMountRemote && Application.isPlaying && Time.unscaledTime >= _nextRemoteScan)
            {
                _nextRemoteScan = Time.unscaledTime + 0.5f;
                ScanRemoteRigs();
            }
            UpdateFollows();
            if (_holds.Count == 0) return;
            _dead.Clear();
            foreach (var kv in _holds)
            {
                var h = kv.Value;
                if (h.prop == null || h.node == null) { _dead.Add(kv.Key); continue; }
                if (!h.node.gameObject.activeInHierarchy && !h.remoteSwap) continue;
                Apply(h, force: false);
            }
            for (int i = 0; i < _dead.Count; i++) _holds.Remove(_dead[i]);
        }

        /// <summary>Forget every hold (tests / domain teardown).</summary>
        public static void ClearAll() { _holds.Clear(); _follows.Clear(); }

        static void UpdateFollows()
        {
            for (int i = _follows.Count - 1; i >= 0; i--)
            {
                var f = _follows[i];
                if (f.node == null) { _follows.RemoveAt(i); continue; }
                if (f.anchor == null) continue;
                f.node.SetPositionAndRotation(f.anchor.position, f.anchor.rotation);
            }
        }

        /// <summary>The per-side follow node under <paramref name="rig"/> for a BothHands-only rig.</summary>
        static Transform SideFollowNode(GameObject rig, bool left, Transform anchor)
        {
            string name = left ? FollowLeftName : FollowRightName;
            Transform node = rig.transform.Find(name);
            if (node == null)
            {
                node = new GameObject(name).transform;
                node.SetParent(rig.transform, false);
            }
            SideFollow f = null;
            foreach (var e in _follows) if (e.node == node) { f = e; break; }
            if (f == null) _follows.Add(f = new SideFollow { node = node });
            f.anchor = anchor;
            node.SetPositionAndRotation(anchor.position, anchor.rotation);
            return node;
        }

        // ── internals ─────────────────────────────────────────────────

        static bool ResolveLeft(HoldState h)
        {
            switch (h.mode)
            {
                case SideMode.Left: return true;
                case SideMode.Right: return false;
            }
            switch (h.kind)
            {
                case HandheldMath.HandNodeKind.Tracker: return HandheldMath.TrackerIsLeft(h.tracker);
                case HandheldMath.HandNodeKind.Left: return true;
                case HandheldMath.HandNodeKind.Right: return false;
                case HandheldMath.HandNodeKind.Active:
                    if (h.remote == null) h.remote = h.node.GetComponentInParent<RemoteRig>(true);
                    return PeerLeft(h.remote);
            }
            return false;
        }

        static bool PeerLeft(RemoteRig rr)
        {
            var peer = rr != null ? rr.peer : null;
            return peer != null && (peer.activeHand == "l" || peer.activeHand == "left");
        }

        /// <summary>A RemoteRig clone's hand node for <paramref name="mode"/>: Left → LeftHand, Right → RightHand,
        /// Active → Hand, else (no Hand node) the side the player is using now, and <paramref name="swap"/> = follow it.</summary>
        static Transform RemoteHand(RemoteRig rr, SideMode mode, out HandheldMath.HandNodeKind kind, out bool swap)
        {
            Transform l = null, r = null, a = null;
            var q = new Queue<Transform>();   // breadth-first, the shallowest wins (as RemoteRig drives them)
            q.Enqueue(rr.transform);
            while (q.Count > 0)
            {
                var t = q.Dequeue();
                switch (HandheldMath.KindOf(t, out _))
                {
                    case HandheldMath.HandNodeKind.Left: if (l == null) l = t; break;
                    case HandheldMath.HandNodeKind.Right: if (r == null) r = t; break;
                    case HandheldMath.HandNodeKind.Active: if (a == null) a = t; break;
                }
                for (int i = 0; i < t.childCount; i++) q.Enqueue(t.GetChild(i));
            }
            swap = false;
            bool left = mode == SideMode.Left || (mode == SideMode.Active && a == null && PeerLeft(rr));
            Transform node = mode == SideMode.Active && a != null ? a : left ? l : r;
            if (node == null) { kind = HandheldMath.HandNodeKind.None; return null; }
            swap = mode == SideMode.Active && a == null;
            kind = node == a ? HandheldMath.HandNodeKind.Active : left ? HandheldMath.HandNodeKind.Left : HandheldMath.HandNodeKind.Right;
            return node;
        }

        // 'active' hold on a RemoteRig without a Hand node: move the prop to the hand the player now uses.
        static void SwapRemoteSide(HoldState h)
        {
            bool left = PeerLeft(h.remote);
            if (left == (h.kind == HandheldMath.HandNodeKind.Left)) return;
            var n = RemoteHand(h.remote, left ? SideMode.Left : SideMode.Right, out var k, out _);
            if (n == null) return;
            h.node = n;
            h.kind = k;
            h.prop.SetParent(n, true);
        }

        static void Apply(HoldState h, bool force)
        {
            if (h.remoteSwap) SwapRemoteSide(h);
            bool left = ResolveLeft(h);
            // handheld_mount.lua: the tracker frame is raw * flip while the LEFT anchor drives it.
            bool flip = left && h.kind == HandheldMath.HandNodeKind.Tracker && h.tracker != null && h.tracker.flipVisual;

            // Two-handed (two_hand_aim.lua): aim Grip → Grip2 at the off fist every frame while
            // the off hand is tracked; otherwise the one-hand mount below.
            if (h.offHand != null && OffTracked(h))
            {
                if (HandheldMath.TwoHandAim(h.prop, h.node, left, h.offHand, h.offLeft))
                {
                    h.mounted = false;   // the one-hand pose is re-applied the moment the off hand is lost
                    return;
                }
            }

            if (!force && h.mounted && left == h.mountedLeft && flip == h.mountedFlip && h.prop.parent == h.mountedParent) return;
            HandheldMath.Place(h.node, h.prop, left, flip,
                               h.tracker != null ? h.tracker.flipVisualRotation : (Vector3?)null,
                               h.tracker != null ? h.tracker.flipVisualPosition : (Vector3?)null,
                               h.targetRight);
            h.mounted = true;
            h.mountedLeft = left;
            h.mountedFlip = flip;
            h.mountedParent = h.prop.parent;
        }

        static bool OffTracked(HoldState h)
        {
            var t = h.offHand;
            if (t == null || !t.gameObject.activeInHierarchy) return false;
            if (h.offTracker != null) return h.offTracker.isActiveAndTracking;
            return true;
        }

        static Transform LocalHandFor(SideMode mode, out HandheldMath.HandNodeKind kind, out HandTracker tracker)
        {
            kind = HandheldMath.HandNodeKind.None;
            tracker = null;
            var rigGo = DreamParkLuaAPI.Player();
            if (rigGo == null) return null;
            HandTracker left = null, right = null, both = null;
            foreach (var t in rigGo.GetComponentsInChildren<HandTracker>(true))
            {
                if (t.GetComponentInParent<RemoteRig>(true) != null) continue;
                switch (t.handPreference)
                {
                    case HandTracker.HandPreference.Left: if (left == null) left = t; break;
                    case HandTracker.HandPreference.Right: if (right == null) right = t; break;
                    default: if (both == null) both = t; break;
                }
            }
            switch (mode)
            {
                case SideMode.Left: tracker = left; break;
                case SideMode.Right: tracker = right; break;
                default: tracker = both ?? right ?? left; break;
            }
            if (tracker == null && mode != SideMode.Active && both != null)
            {
                // Named side on a BothHands-only rig: follow THAT hand, not the active one.
                bool wantLeft = mode == SideMode.Left;
                Transform anchor = DreamParkLuaAPI.Hand(wantLeft ? "left" : "right");
                if (anchor != null && anchor != both.transform && !anchor.IsChildOf(both.transform))
                {
                    kind = wantLeft ? HandheldMath.HandNodeKind.Left : HandheldMath.HandNodeKind.Right;
                    return SideFollowNode(rigGo, wantLeft, anchor);
                }
                tracker = both;   // no anchor for that side (yet): the old behaviour
            }
            if (tracker == null) return null;
            kind = HandheldMath.HandNodeKind.Tracker;
            return tracker.transform;
        }

        static readonly Queue<Transform> _scan = new Queue<Transform>();

        static void ScanRemoteRigs()
        {
            var rigs = Object.FindObjectsByType<RemoteRig>(FindObjectsSortMode.None);
            for (int r = 0; r < rigs.Length; r++)
            {
                var rr = rigs[r];
                if (rr == null) continue;
                _scan.Clear();
                for (int i = 0; i < rr.transform.childCount; i++) _scan.Enqueue(rr.transform.GetChild(i));
                while (_scan.Count > 0)
                {
                    var t = _scan.Dequeue();
                    var kind = HandheldMath.KindOf(t, out _);
                    if (kind == HandheldMath.HandNodeKind.Left || kind == HandheldMath.HandNodeKind.Right || kind == HandheldMath.HandNodeKind.Active)
                    {
                        for (int c = 0; c < t.childCount; c++)
                        {
                            var prop = t.GetChild(c);
                            // A grip marker as a DIRECT child (the documented convention). A deep search here
                            // mounted whole containers by a nested weapon's Grip (Zombiez Hand/Grip/Arsenal/W_*/Grip).
                            if (!HasGripChild(prop)) continue;
                            if (prop.name == HandheldMath.ModelName) continue;
                            if (_holds.ContainsKey(prop.gameObject.GetInstanceID())) continue;
                            var mode = kind == HandheldMath.HandNodeKind.Left ? SideMode.Left
                                     : kind == HandheldMath.HandNodeKind.Right ? SideMode.Right : SideMode.Active;
                            Hold(prop.gameObject, mode, t, true);
                        }
                        continue;
                    }
                    for (int i = 0; i < t.childCount; i++) _scan.Enqueue(t.GetChild(i));
                }
            }
        }

        static bool HasGripChild(Transform prop)
        {
            for (int i = 0; i < 4; i++) if (prop.Find(HandheldMath.GripNames[i]) != null) return true;   // Grip, PalmGrip, GripLeft, PalmGripLeft
            return false;
        }
    }
}
