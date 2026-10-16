#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace DreamPark.Playtest
{
    public enum PlaytestHand { Left, Right }

    /// How a hand approaches its target. Touch presses gently into the surface and
    /// retracts; Strike swings through it fast (whack/punch/hit games care about
    /// contact velocity); Hold leaves the hand there and carries it with the body.
    public enum PlaytestReachStyle { Touch, Strike, Hold }

    public class PlaytestActionResult
    {
        public bool ok = true;
        public string message = "";
        public readonly List<string> notes = new List<string>();

        public void Fail(string why) { ok = false; message = why; }
        public void Note(string note) { notes.Add(note); }
    }

    /// <summary>
    /// The body the AI playtester wears. While it exists it owns the guest's pose:
    /// it switches off the Scene-view <see cref="Simulator"/> (which would otherwise
    /// copy the Scene camera onto Camera.main every frame) and writes the head
    /// (Camera.main) and both hand anchors itself, after everything else has run.
    ///
    /// Everything the rig reads its pose from — HeadTracker, BodyTracker,
    /// FeetTracker, HandTracker, GameArea presence, dp.head()/dp.hand() — keys off
    /// Camera.main and the Left/RightHandAnchor transforms, so driving those two is
    /// driving the player. Nothing in content has to know a bot is playing.
    ///
    /// It also carries physics probes, because the template rig has no colliders:
    /// a sphere on each hand and an optional body capsule that reaches the floor.
    /// They are parented under the active PlayerRig, so dp.is_player(other) is true
    /// for them by ancestry, and they carry kinematic Rigidbodies so trigger and
    /// collision callbacks fire on content that has none of its own.
    ///
    /// Editor-only. It never exists in a player build.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    [AddComponentMenu("")]
    public class PlaytestAvatar : MonoBehaviour
    {
        public static PlaytestAvatar Instance { get; private set; }

        public const float WalkSpeed = 1.3f;
        public const float TurnSpeed = 240f;
        public const float ArmReach = 0.65f;
        const float HandRadius = 0.05f;
        const float BodyRadius = 0.18f;
        const int IgnoreRaycastLayer = 2;

        public Vector3 HeadPosition;
        public float Yaw;
        public float Pitch;
        public float EyeHeight = 1.6f;
        public float FloorY;
        public bool FloorKnown;

        public bool Busy => _running != null;
        public string CurrentAction { get; private set; }

        Simulator _simulator;
        bool _simulatorWasEnabled;
        Transform _leftAnchor, _rightAnchor;
        PlaytestProbe _leftProbe, _rightProbe, _bodyProbe;
        readonly HandState _left = new HandState(-1f), _right = new HandState(1f);

        TaskCompletionSource<PlaytestActionResult> _running;
        PlaytestActionResult _runningResult;

        class HandState
        {
            public readonly float side;
            public Vector3 world;              // where the hand is this frame
            public Vector3? holdLocal;          // carried offset in the yaw frame
            public HandState(float side) { this.side = side; }
        }

        public Quaternion YawRotation => Quaternion.Euler(0f, Yaw, 0f);
        public Quaternion HeadRotation => Quaternion.Euler(Pitch, Yaw, 0f);
        public Vector3 Forward => YawRotation * Vector3.forward;

        // ── Lifecycle ─────────────────────────────────────────────────────

        public static PlaytestAvatar Spawn(bool bodyProbe)
        {
            if (Instance != null) return Instance;
            var go = new GameObject("[Playtest Avatar]");
            go.hideFlags = HideFlags.DontSave;
            var avatar = go.AddComponent<PlaytestAvatar>();
            avatar.Init(bodyProbe);
            return avatar;
        }

        public static void Despawn()
        {
            if (Instance != null) Destroy(Instance.gameObject);
        }

        void Init(bool bodyProbe)
        {
            Instance = this;

            _simulator = FindFirstObjectByType<Simulator>();
            if (_simulator != null)
            {
                _simulatorWasEnabled = _simulator.enabled;
                _simulator.enabled = false;
            }

            var cam = Camera.main;
            if (cam != null)
            {
                HeadPosition = cam.transform.position;
                var e = cam.transform.eulerAngles;
                Yaw = e.y;
                Pitch = 0f;
            }
            else
            {
                HeadPosition = new Vector3(0f, 1.6f, 0f);
            }

            if (ProbeFloor(HeadPosition, out float floor))
            {
                FloorY = floor;
                FloorKnown = true;
                EyeHeight = Mathf.Clamp(HeadPosition.y - floor, 1.0f, 2.0f);
            }
            else
            {
                FloorY = HeadPosition.y - EyeHeight;
            }
            HeadPosition.y = FloorY + EyeHeight;

            _leftAnchor = FindOrCreateAnchor("LeftHandAnchor");
            _rightAnchor = FindOrCreateAnchor("RightHandAnchor");
            foreach (var h in new[] { _left, _right })
            {
                h.world = RestPosition(h);
                h.holdLocal = Quaternion.Inverse(YawRotation) * (h.world - HeadPosition);
            }

            _leftProbe = PlaytestProbe.Create("Playtest Left Hand", PlaytestProbe.Part.LeftHand, HandRadius, 0f);
            _rightProbe = PlaytestProbe.Create("Playtest Right Hand", PlaytestProbe.Part.RightHand, HandRadius, 0f);
            if (bodyProbe)
                _bodyProbe = PlaytestProbe.Create("Playtest Body", PlaytestProbe.Part.Body, BodyRadius, EyeHeight);

            Application.onBeforeRender += ApplyPose;
            ApplyPose();
            SyncProbes(teleport: true);
        }

        void OnDestroy()
        {
            Application.onBeforeRender -= ApplyPose;
            if (_leftProbe) Destroy(_leftProbe.gameObject);
            if (_rightProbe) Destroy(_rightProbe.gameObject);
            if (_bodyProbe) Destroy(_bodyProbe.gameObject);
            if (_simulator != null) _simulator.enabled = _simulatorWasEnabled;
            if (_running != null)
            {
                var r = _runningResult ?? new PlaytestActionResult();
                r.Fail("Play mode ended before the action finished.");
                _running.TrySetResult(r);
                _running = null;
            }
            if (Instance == this) Instance = null;
        }

        static Transform FindOrCreateAnchor(string name)
        {
            var go = GameObject.Find(name);
            if (go == null) go = new GameObject(name);
            return go.transform;
        }

        // ── Pose output ───────────────────────────────────────────────────
        // OVRCameraRig writes CenterEyeAnchor from its own Update when no HMD is
        // attached; order 32000 plus a re-apply in LateUpdate and onBeforeRender
        // makes sure the frame that renders, and the trackers that read the pose in
        // LateUpdate, see ours.

        void Update()
        {
            UpdateHeldHands();
            ApplyPose();
        }

        void LateUpdate() => ApplyPose();

        void FixedUpdate() => SyncProbes(teleport: false);

        void ApplyPose()
        {
            if (this == null) return;
            var cam = Camera.main;
            if (cam != null) cam.transform.SetPositionAndRotation(HeadPosition, HeadRotation);
            // Hands in the device wrist frame (HandheldMath.EditorAnchorRotation): fingers along
            // the look direction, thumb up, palm inward — what a headset reports, so hand-held
            // props sit as they do on device. Only rotation changed; probes follow positions.
            if (_leftAnchor != null) _leftAnchor.SetPositionAndRotation(_left.world, HandheldMath.EditorAnchorRotation(HeadRotation, true));
            if (_rightAnchor != null) _rightAnchor.SetPositionAndRotation(_right.world, HandheldMath.EditorAnchorRotation(HeadRotation, false));
        }

        void SyncProbes(bool teleport)
        {
            _leftProbe?.Follow(_left.world, teleport);
            _rightProbe?.Follow(_right.world, teleport);
            if (_bodyProbe != null)
            {
                _bodyProbe.SetBodyHeight(EyeHeight);
                _bodyProbe.Follow(new Vector3(HeadPosition.x, FloorY + EyeHeight * 0.5f, HeadPosition.z), teleport);
            }
        }

        void UpdateHeldHands()
        {
            foreach (var h in new[] { _left, _right })
            {
                if (h.holdLocal.HasValue) h.world = HeadPosition + YawRotation * h.holdLocal.Value;
            }
        }

        Vector3 RestPosition(HandState h)
        {
            return HeadPosition + YawRotation * new Vector3(0.2f * h.side, -0.45f, 0.25f);
        }

        Vector3 Shoulder(HandState h)
        {
            return HeadPosition + YawRotation * new Vector3(0.18f * h.side, -0.25f, 0f);
        }

        HandState HandOf(PlaytestHand hand) => hand == PlaytestHand.Left ? _left : _right;

        public Vector3 HandPosition(PlaytestHand hand) => HandOf(hand).world;
        public bool IsHolding(PlaytestHand hand) => HandOf(hand).holdLocal.HasValue;

        // ── Floor ─────────────────────────────────────────────────────────

        /// Nearest solid surface under a point, ignoring triggers, the player rig and
        /// our own probes. Virtual floors in a park are generated per attraction, so
        /// this is also how a bot notices it has walked off one.
        public static bool ProbeFloor(Vector3 from, out float floorY, float maxDistance = 4f)
        {
            floorY = 0f;
            var hits = Physics.RaycastAll(from + Vector3.up * 0.1f, Vector3.down, maxDistance, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            foreach (var hit in hits)
            {
                if (IsPlayerCollider(hit.collider)) continue;
                // Something you can stand on: faces up, doesn't move, and is wider
                // than a prop. Loose cubes and enemies are not the floor.
                if (hit.normal.y < 0.7f) continue;
                var body = hit.collider.attachedRigidbody;
                if (body != null && !body.isKinematic) continue;
                Vector3 size = hit.collider.bounds.size;
                if (Mathf.Max(size.x, size.z) < 1.0f) continue;
                if (hit.distance < best) { best = hit.distance; floorY = hit.point.y; }
            }
            return best < float.MaxValue;
        }

        public static bool IsPlayerCollider(Collider c)
        {
            if (c == null) return false;
            if (c.GetComponentInParent<PlaytestProbe>() != null) return true;
            return c.GetComponentInParent<PlayerRig>(true) != null;
        }

        void FollowFloor()
        {
            // Once on a floor, only follow gentle changes (steps, ramps) so walking
            // over a tall prop doesn't lift the guest onto it. With no floor yet,
            // take the first one found.
            if (ProbeFloor(HeadPosition, out float floor) && (!FloorKnown || Mathf.Abs(floor - FloorY) < 0.5f))
            {
                FloorY = floor;
                FloorKnown = true;
            }
            HeadPosition.y = FloorY + EyeHeight;
        }

        // ── Action runner ─────────────────────────────────────────────────

        /// Runs one action to completion. Actions are coroutines; exceptions inside
        /// them (including nested ones) become a failed result rather than a hung
        /// task, so the agent loop can never wait forever on a crashed step.
        public Task<PlaytestActionResult> Run(string name, Func<PlaytestActionResult, IEnumerator> body)
        {
            var result = new PlaytestActionResult();
            if (_running != null)
            {
                result.Fail($"Busy with '{CurrentAction}'.");
                return Task.FromResult(result);
            }
            _running = new TaskCompletionSource<PlaytestActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _runningResult = result;
            CurrentAction = name;
            var task = _running.Task;
            StartCoroutine(Drive(body(result), result));
            return task;
        }

        IEnumerator Drive(IEnumerator root, PlaytestActionResult result)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var top = stack.Peek();
                bool more;
                try { more = top.MoveNext(); }
                catch (Exception e)
                {
                    result.Fail("Action threw: " + e.Message);
                    Debug.LogException(e);
                    break;
                }
                if (!more) { stack.Pop(); continue; }
                if (top.Current is IEnumerator nested) { stack.Push(nested); continue; }
                yield return top.Current;
            }
            var tcs = _running;
            _running = null;
            _runningResult = null;
            CurrentAction = null;
            tcs?.TrySetResult(result);
        }

        static float Dt => Mathf.Min(Time.deltaTime, 0.05f);

        // ── Primitive motions (yield these from actions) ──────────────────

        public IEnumerator TurnTo(float targetYaw, float targetPitch)
        {
            targetPitch = Mathf.Clamp(targetPitch, -70f, 80f);
            for (float t = 0; t < 5f; t += Dt)
            {
                Yaw = Mathf.MoveTowardsAngle(Yaw, targetYaw, TurnSpeed * Dt);
                Pitch = Mathf.MoveTowardsAngle(Pitch, targetPitch, TurnSpeed * Dt);
                if (Mathf.Abs(Mathf.DeltaAngle(Yaw, targetYaw)) < 0.5f && Mathf.Abs(Mathf.DeltaAngle(Pitch, targetPitch)) < 0.5f) break;
                yield return null;
            }
            Yaw = targetYaw;
            Pitch = targetPitch;
        }

        public IEnumerator LookAt(Func<Vector3> point)
        {
            Vector3 to = point() - HeadPosition;
            if (to.sqrMagnitude < 1e-4f) yield break;
            float yaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Atan2(to.y, new Vector2(to.x, to.z).magnitude) * Mathf.Rad2Deg;
            yield return TurnTo(yaw, pitch);
        }

        /// Walks in a straight line until within stopDistance (horizontal) of the
        /// target. There is no pathfinding on purpose: in mixed reality the guest
        /// walks through virtual geometry, and the only real obstacles are in the
        /// physical room, which the bot cannot know about.
        public IEnumerator WalkTo(Func<Vector3> target, float stopDistance, PlaytestActionResult result)
        {
            float walked = 0f;
            float limit = 150f;
            Vector3 first = target() - HeadPosition;
            first.y = 0f;
            if (first.magnitude > stopDistance + 0.02f)
                yield return TurnTo(Mathf.Atan2(first.x, first.z) * Mathf.Rad2Deg, 0f);

            while (walked < limit)
            {
                Vector3 to = target() - HeadPosition;
                to.y = 0f;
                float remaining = to.magnitude - stopDistance;
                if (remaining <= 0.02f) break;
                Vector3 dir = to.normalized;
                Yaw = Mathf.MoveTowardsAngle(Yaw, Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg, TurnSpeed * Dt);
                float step = Mathf.Min(WalkSpeed * Dt, remaining);
                HeadPosition += dir * step;
                walked += step;
                FollowFloor();
                yield return null;
            }
            if (walked >= limit) result.Note($"Gave up after walking {limit:0} m without arriving.");
            result.Note($"Walked {walked:0.0} m.");
        }

        /// Harness shortcut, not a guest motion: jump straight to a spot facing a
        /// point. Used to reach a distant attraction without spending a minute of
        /// game time walking there.
        /// <paramref name="groundHint"/> is where the destination's ground probably
        /// is (the bottom of the target); parks can sit at any height, so the floor
        /// search starts above the destination rather than at the current head.
        public IEnumerator Teleport(Vector3 position, Vector3 face, float groundHint)
        {
            HeadPosition = new Vector3(position.x, groundHint + EyeHeight, position.z);
            FloorKnown = ProbeFloor(new Vector3(position.x, groundHint + 3f, position.z), out float floor, 8f);
            FloorY = FloorKnown ? floor : groundHint;
            HeadPosition.y = FloorY + EyeHeight;
            Vector3 to = face - HeadPosition;
            Yaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
            Pitch = 0f;
            UpdateHeldHands();
            ApplyPose();
            SyncProbes(teleport: true);
            yield return null;
            yield return null;
        }

        public IEnumerator SetEyeHeight(float height)
        {
            height = Mathf.Clamp(height, 0.6f, 2.1f);
            while (Mathf.Abs(EyeHeight - height) > 0.005f)
            {
                EyeHeight = Mathf.MoveTowards(EyeHeight, height, 1.2f * Dt);
                HeadPosition.y = FloorY + EyeHeight;
                yield return null;
            }
            EyeHeight = height;
            HeadPosition.y = FloorY + EyeHeight;
        }

        IEnumerator MoveHand(HandState h, Func<Vector3> target, float speed, float timeout = 4f)
        {
            h.holdLocal = null;
            for (float t = 0; t < timeout; t += Dt)
            {
                Vector3 goal = target();
                h.world = Vector3.MoveTowards(h.world, goal, speed * Dt);
                if ((h.world - goal).sqrMagnitude < 1e-5f) yield break;
                yield return null;
            }
        }

        public IEnumerator Retract(PlaytestHand hand)
        {
            var h = HandOf(hand);
            yield return MoveHand(h, () => RestPosition(h), 1.5f);
            h.holdLocal = Quaternion.Inverse(YawRotation) * (h.world - HeadPosition);
            // Resting hands ride along with the body rather than hanging in space.
        }

        /// Reaches a hand to a point that may move (a mole popping up, a thrown
        /// ball). Steps closer and crouches automatically when the point is out of
        /// arm's reach, the way a guest would, and reports when even that fails.
        public IEnumerator Reach(PlaytestHand hand, Func<Vector3> target, PlaytestReachStyle style, float holdSeconds, PlaytestActionResult result)
        {
            var h = HandOf(hand);
            float restoreEye = EyeHeight;

            Vector3 flat = target() - HeadPosition;
            flat.y = 0f;
            if (flat.magnitude > ArmReach)
                yield return WalkTo(target, 0.4f, result);

            float below = HeadPosition.y - target().y;
            if (below > 1.0f)
            {
                yield return SetEyeHeight(target().y - FloorY + 0.75f);
                result.Note($"Crouched to eye height {EyeHeight:0.00} m.");
            }
            float above = target().y - HeadPosition.y;
            if (above > 0.55f)
                result.Note($"Target is {above:0.00} m above eye level; a guest could barely reach it.");

            yield return LookAt(target);

            float speed = style == PlaytestReachStyle.Strike ? 4f : 1.2f;
            float overshoot = style == PlaytestReachStyle.Strike ? 0.15f : 0.03f;
            Func<Vector3> through = () =>
            {
                Vector3 goal = target();
                Vector3 dir = goal - Shoulder(h);
                return dir.sqrMagnitude > 1e-6f ? goal + dir.normalized * overshoot : goal;
            };
            yield return MoveHand(h, through, speed);

            float reachError = Vector3.Distance(h.world, through());
            if (reachError > 0.1f) result.Note($"Hand stopped {reachError:0.00} m short of the target (it may have moved).");

            if (style == PlaytestReachStyle.Hold)
            {
                h.holdLocal = Quaternion.Inverse(YawRotation) * (h.world - HeadPosition);
                yield break;
            }

            float hold = style == PlaytestReachStyle.Strike ? 0.05f : Mathf.Max(0.1f, holdSeconds);
            for (float t = 0; t < hold; t += Dt)
            {
                h.world = through();
                yield return null;
            }
            yield return Retract(hand);
            if (!Mathf.Approximately(restoreEye, EyeHeight)) yield return SetEyeHeight(restoreEye);
        }

        public IEnumerator Wait(float seconds)
        {
            for (float t = 0; t < seconds; t += Time.deltaTime) yield return null;
        }
    }
}
#endif
