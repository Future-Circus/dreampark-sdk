using System;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark {
    /// <summary>
    /// The player's kicking foot (v2, Oct 27 2026). Quest has no foot tracking, so the foot is ESTIMATED from the head
    /// and kicks are PREDICTED by <see cref="KickPredictor"/> (pure, frame-rate independent maths), tuned for feel
    /// ("like Wiimotes and Wii Sports"). Interface for titles: FEET.md next to this file (Lua: dp.feet).
    ///
    /// Every frame it:
    ///   * records a timestamped head sample (position + orientation);
    ///   * measures the floor under the player (raycast onto the Level layer, smoothed), so the foot sits ON THE FLOOR
    ///     for every height of player. v1 hung it a fixed distance below the eyes (SAL: 1.3 m), so kids and anyone on
    ///     a raised or lowered floor missed. With no floor found it falls back to v1's head + yOffset;
    ///   * gathers nearby kickables (non-kinematic physics bodies + virtual targets from SetTarget) and lets the
    ///     predictor start a standing-kick sweep into one when the player clearly means to kick;
    ///   * moves its kinematic Rigidbody there with MovePosition in FixedUpdate (v1 teleported it in LateUpdate), so
    ///     anything jointed to it — the rig's `Kicker` collider (tag Kicker, kept for SAL's filters) — follows with a
    ///     real velocity;
    ///   * fires <see cref="Kicked"/> once per touch of a kickable (physics overlap, or a swept virtual target).
    ///
    /// Kept from v1 (rigs and titles depend on them): head, rb, yOffset, kickMultiplier (scales the momentum lead),
    /// GetDirection(), GetVelocity(multiplier).
    /// </summary>
    public class FeetTracker : MonoBehaviour
    {
        public enum FootSource { Estimated = 0, Predicted = 1, Detected = 2 }

        public struct KickEvent {
            public GameObject target;     // physics kickable touched, or null
            public string targetKey;      // virtual target key, or null
            public Vector3 point;         // foot position at contact (world, floor height)
            public Vector3 velocity;      // foot velocity at contact, m/s
            public Vector3 direction;     // unit, horizontal
            public float speed;           // horizontal m/s
            public float confidence;      // 0..1
            public string source;         // "contact" | "predicted" | "depth" | a requester's label
            public double time;
            public int seq;
        }

        /// <summary>One event per kick contact (local headset only).</summary>
        public static event Action<KickEvent> Kicked;
        /// <summary>The active local foot (the most recently enabled tracker), or null.</summary>
        public static FeetTracker Local { get; private set; }
        public static KickEvent? LastKick { get; private set; }
        static readonly List<FeetTracker> s_enabled = new List<FeetTracker>();
        static int s_seq;

        //we have to fake feet tracking by using the head position and velocity
        public Transform head;
        public Rigidbody rb;
        [Tooltip("v1: foot body height relative to the head. v2 uses it only when no floor is found.")]
        public float yOffset = 0;
        [Tooltip("Scales the momentum lead (how far the foot leads while you move).")]
        public float kickMultiplier = 1f;

        [Header("v2: floor, reach, kicks")]
        [Tooltip("Height of the foot body's centre above the floor. SAL's Kicker box is 0.6 m tall: 0.3 = sole on the floor.")]
        public float footCenterHeight = 0.3f;
        [Tooltip("Measure the floor under the player (Level layer) instead of hanging the foot below the head.")]
        public bool floorRelative = true;
        [Tooltip("Foot volume for contact events: x = width, y = height, z = length (m).")]
        public Vector3 footSize = new Vector3(0.25f, 0.6f, 0.30f);
        [Tooltip("Layers that count as kickable (contacts + standing-kick targets). Player and Level are always ignored.")]
        public LayerMask kickableLayers = ~0;
        [Tooltip("Start a standing-kick sweep when the player clearly means to kick a kickable in reach.")]
        public bool predictStandingKicks = true;
        public KickPredictor.Settings tuning = KickPredictor.Settings.Default;
        [Tooltip("Log strikes and kick contacts to the console ([kick]).")]
        public bool debugLog = true;
        [Tooltip("A/B only: behave exactly like v1 (head − 1.3 m box, 0.6 s momentum, teleported). Contacts still raise Kicked.")]
        public bool useV1 = false;

        // v1 state (useV1)
        readonly List<Vector3> _v1Samples = new List<Vector3>();
        Vector3 _v1Last; bool _v1Started;

        readonly KickPredictor _pred = new KickPredictor();
        readonly List<KickPredictor.Target> _targets = new List<KickPredictor.Target>(16);
        readonly HashSet<int> _seenBodies = new HashSet<int>();
        readonly Dictionary<string, Vector4> _virtual = new Dictionary<string, Vector4>();   // xyz = pos, w = radius
        readonly Dictionary<int, float> _lastTouch = new Dictionary<int, float>();
        readonly Dictionary<string, float> _lastVirtualTouch = new Dictionary<string, float>();
        readonly List<string> _keys = new List<string>(8);
        readonly Collider[] _hits = new Collider[32];
        int _playerLayer = -1, _levelLayer = -1;
        float _floorY; bool _floorKnown; float _nextFloor, _nextTargets;
        float _frameDt = 1f / 72f;
        double _lastFrameT;
        Vector3 _prevFixedFoot; bool _havePrevFixed;
        KickPredictor.FootState _state;
        float _detectedUntil = -1f;
        Transform _rigRoot;
        Rigidbody[] _ownBodies = new Rigidbody[0];

        public Vector3 FootPosition => _state.position;
        public Vector3 FootVelocity => _state.velocity;
        public float FloorY => _state.floorY;
        public bool FloorMeasured => _state.floorMeasured;
        public bool IsStriking => _state.striking;
        public FootSource Source => Time.time < _detectedUntil ? FootSource.Detected : (_state.striking ? FootSource.Predicted : FootSource.Estimated);
        public KickPredictor Predictor => _pred;
        public KickPredictor.FootState State => _state;
        /// <summary>The kickables near the player right now (physics bodies + virtual targets), refreshed ~12 times a second.</summary>
        public IReadOnlyList<KickPredictor.Target> Targets => _targets;

        void Start()
        {
            if (!head && Camera.main) head = Camera.main.transform;
            if (!rb) rb = GetComponent<Rigidbody>();
            // device fallback without a rebuild: PlayerPrefs "dp_feet_v1" = 1 brings the old foot back
            if (PlayerPrefs.GetInt("dp_feet_v1", 0) == 1) useV1 = true;
            _playerLayer = LayerMask.NameToLayer("Player");
            _levelLayer = LayerMask.NameToLayer("Level");
            var rig = GetComponentInParent<PlayerRig>(true);
            _rigRoot = rig ? rig.transform : (transform.parent ? transform.parent : transform);
            _ownBodies = CollectOwnBodies();
        }

        void OnEnable()
        {
            s_enabled.Remove(this); s_enabled.Add(this); Local = this;
            _pred.ClearHistory(); _havePrevFixed = false;
        }

        void OnDisable()
        {
            s_enabled.Remove(this);
            if (Local == this) Local = s_enabled.Count > 0 ? s_enabled[s_enabled.Count - 1] : null;
        }

        // Every body of this rig (ours, anything jointed to us such as the Kicker, the rig's other parts): never kickable.
        Rigidbody[] CollectOwnBodies()
        {
            var list = new List<Rigidbody>();
            if (rb) list.Add(rb);
            if (_rigRoot)
                foreach (var b in _rigRoot.GetComponentsInChildren<Rigidbody>(true)) if (!list.Contains(b)) list.Add(b);
            foreach (var j in FindObjectsByType<Joint>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (rb && j.connectedBody == rb && j.TryGetComponent(out Rigidbody jb) && !list.Contains(jb)) list.Add(jb);
            return list.ToArray();
        }

        bool IsOwn(Collider c)
        {
            if (!c) return true;
            int layer = c.gameObject.layer;
            if (layer == _playerLayer || layer == _levelLayer) return true;
            var b = c.attachedRigidbody;
            if (b) for (int i = 0; i < _ownBodies.Length; i++) if (_ownBodies[i] == b) return true;
            return _rigRoot && c.transform.IsChildOf(_rigRoot);
        }

        int Mask => kickableLayers.value & ~((_playerLayer >= 0 ? 1 << _playerLayer : 0) | (_levelLayer >= 0 ? 1 << _levelLayer : 0));

        // ── Per frame: head sample, floor, targets, strike prediction ────────
        void LateUpdate()
        {
            if (!head) { if (Camera.main) head = Camera.main.transform; else return; }
            if (useV1) { V1Late(); return; }
            double t = Time.timeAsDouble;
            if (_lastFrameT > 0) _frameDt = Mathf.Lerp(_frameDt, Mathf.Clamp((float)(t - _lastFrameT), 0.004f, 0.1f), 0.2f);
            _lastFrameT = t;

            var s = tuning;
            s.fallbackDrop = footCenterHeight - yOffset;   // no floor found: body centre at head + yOffset, as in v1
            _pred.settings = s;
            _pred.kickMultiplier = kickMultiplier;
            _pred.AddHeadSample(t, head.position, head.forward, head.up);

            if (Time.time >= _nextFloor) { _nextFloor = Time.time + 0.1f; MeasureFloor(); }
            _pred.SetFloor(_floorKnown ? _floorY : (float?)null);

            if (Time.time >= _nextTargets) { _nextTargets = Time.time + 0.08f; GatherTargets(); }
            if (predictStandingKicks && _pred.Step(t) && debugLog)
                Debug.Log("[kick] strike " + _pred.LastStrikeReason);

            _state = _pred.Evaluate(t);
            if (!rb) ApplyPose(_state, false);
        }

        void MeasureFloor()
        {
            if (!floorRelative || _levelLayer < 0) { _floorKnown = false; return; }
            Vector3 o = head.position;
            if (Physics.Raycast(o, Vector3.down, out RaycastHit hit, 2.4f, 1 << _levelLayer, QueryTriggerInteraction.Ignore)
                && hit.point.y <= o.y - 0.45f)
            {
                float y = hit.point.y;
                if (!_floorKnown || Mathf.Abs(y - _floorY) > 0.6f) _floorY = y;   // first sight / a big change: snap
                else _floorY = Mathf.MoveTowards(_floorY, y, 0.15f);             // steps: ease (1.5 m/s at 10 Hz)
                _floorKnown = true;
            }
            else _floorKnown = false;
        }

        void GatherTargets()
        {
            _targets.Clear(); _seenBodies.Clear();
            float floor = _floorKnown ? _floorY : head.position.y - 1.6f;
            int n = Physics.OverlapSphereNonAlloc(new Vector3(head.position.x, floor + 0.4f, head.position.z),
                                                  1.4f, _hits, Mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var col = _hits[i];
                if (IsOwn(col)) continue;
                var b = col.attachedRigidbody;
                if (!b || b.isKinematic) continue;                  // movable things are kickable; walls are not
                int id = b.GetInstanceID();
                if (!_seenBodies.Add(id)) continue;
                Bounds bd = col.bounds;
                float r = Mathf.Max(0.08f, Mathf.Max(bd.extents.x, bd.extents.z));
                _targets.Add(new KickPredictor.Target(bd.center, r, id));
            }
            foreach (var kv in _virtual)
                _targets.Add(new KickPredictor.Target((Vector3)kv.Value, kv.Value.w, VirtualId(kv.Key)));
            _pred.SetTargets(_targets);
        }

        static int VirtualId(string key) => unchecked(0x3f000000 ^ key.GetHashCode());

        // ── Physics step: move the body, contacts ───────────────────────────
        void FixedUpdate()
        {
            if (useV1) { V1Fixed(); return; }
            if (!_pred.HasSamples || !head) return;
            // Evaluate one frame behind the newest head sample, so the physics steps that fall between two frames
            // interpolate instead of piling onto the last sample (v1: a jump, then zero velocity, at low fps).
            double t = Math.Min(Time.fixedTimeAsDouble, _pred.LastSampleTime) - _frameDt;
            var st = _pred.Evaluate(t);
            if (rb) ApplyPose(st, true);
            Contacts(st);
            _prevFixedFoot = st.position; _havePrevFixed = true;
        }

        void ApplyPose(KickPredictor.FootState st, bool physics)
        {
            Vector3 center = st.position + Vector3.up * footCenterHeight;
            Quaternion rot = Quaternion.LookRotation(st.direction.sqrMagnitude > 1e-6f ? st.direction : Vector3.forward);
            if (physics && rb.isKinematic) { rb.MovePosition(center); rb.MoveRotation(rot); }
            else if (physics) { rb.position = center; rb.rotation = rot; }
            else transform.SetPositionAndRotation(center, rot);
        }

        void Contacts(KickPredictor.FootState st)
        {
            float now = Time.time;
            Vector3 foot = st.position;
            Vector3 center = foot + Vector3.up * footCenterHeight;
            Quaternion rot = Quaternion.LookRotation(st.direction.sqrMagnitude > 1e-6f ? st.direction : Vector3.forward);
            int n = Physics.OverlapBoxNonAlloc(center, footSize * 0.5f, _hits, rot, Mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var col = _hits[i];
                if (IsOwn(col)) continue;
                if (!col.attachedRigidbody) continue;   // walls, floors, static dressing: never a kick
                GameObject go = col.attachedRigidbody.gameObject;
                int id = go.GetInstanceID();
                bool fresh = !_lastTouch.TryGetValue(id, out float last) || now - last > 0.25f;
                _lastTouch[id] = now;
                if (fresh) Fire(st, go, null, foot);
            }
            if (_virtual.Count == 0 || !_havePrevFixed) return;
            _keys.Clear(); _keys.AddRange(_virtual.Keys);
            foreach (var key in _keys)
            {
                Vector4 v = _virtual[key];
                Vector3 p = v;
                float f = KickPredictor.SweptHit(_prevFixedFoot, foot, p, footSize.z * 0.5f, v.w);
                bool inHeight = p.y - v.w <= st.floorY + footSize.y && p.y + v.w >= st.floorY - 0.05f;
                if (f < 0f || !inHeight) continue;
                bool fresh = !_lastVirtualTouch.TryGetValue(key, out float last) || now - last > 0.25f;
                _lastVirtualTouch[key] = now;
                if (fresh) Fire(st, null, key, Vector3.Lerp(_prevFixedFoot, foot, f));
            }
        }

        void Fire(KickPredictor.FootState st, GameObject target, string key, Vector3 point)
        {
            Vector3 v = st.velocity;
            float speed = new Vector2(v.x, v.z).magnitude;
            string source; float conf;
            if (Time.time < _detectedUntil) { source = "depth"; conf = 0.9f; }
            else if (st.striking) { source = st.strikeSource ?? "predicted"; conf = Mathf.Max(0.7f, st.strikeConfidence); }
            else { source = "contact"; conf = speed > 0.6f ? 0.55f : 0.4f; }
            Vector3 dir = speed > 0.05f ? new Vector3(v.x, 0, v.z) / speed : st.direction;
            var e = new KickEvent {
                target = target, targetKey = key, point = point, velocity = v, direction = dir, speed = speed,
                confidence = conf, source = source, time = Time.timeAsDouble, seq = ++s_seq,
            };
            LastKick = e;
            if (debugLog)
                Debug.Log(string.Format("[kick] contact {0} src={1} conf={2:0.00} speed={3:0.00} fps={4:0}",
                    target ? target.name : key, source, conf, speed, 1f / Mathf.Max(1e-4f, _frameDt)));
            try { Kicked?.Invoke(e); } catch (Exception ex) { Debug.LogException(ex); }
        }

        // ── v1, verbatim (A/B) ───────────────────────────────────────────────
        Vector3 V1Sum() { Vector3 a = Vector3.zero; for (int i = 0; i < _v1Samples.Count; i++) a += _v1Samples[i]; return a; }
        Vector3 V1Dir() => _v1Samples.Count > 0 ? V1Sum().normalized : Vector3.forward;
        float V1Vel(float m = 1f) => _v1Samples.Count > 0 ? m * V1Sum().magnitude / _v1Samples.Count : 0f;
        void V1Fixed()
        {
            if (!_v1Started) { _v1Last = head ? head.position : transform.position; _v1Started = true; }
            Vector3 xz = new Vector3(transform.position.x, 0, transform.position.z);
            _v1Samples.Add(xz - _v1Last); _v1Last = xz;
            if (_v1Samples.Count > 30) _v1Samples.RemoveAt(0);
            var st = _state; st.position = transform.position - Vector3.up * footCenterHeight; st.direction = V1Dir();
            st.velocity = V1Dir() * V1Vel() / Mathf.Max(1e-4f, Time.fixedDeltaTime); st.floorY = st.position.y;
            _state = st;
            Contacts(st);
        }
        void V1Late()
        {
            Vector3 p = new Vector3(head.position.x, head.position.y + yOffset, head.position.z) + V1Dir() * Mathf.Clamp(kickMultiplier * V1Vel(10f), 0f, 0.2f);
            if (rb != null) { rb.position = p; if (V1Vel() > 0.01f) rb.rotation = Quaternion.LookRotation(V1Dir()); }
            else { transform.position = p; if (V1Vel() > 0.01f) transform.rotation = Quaternion.LookRotation(V1Dir()); }
        }

        // ── Public API ───────────────────────────────────────────────────────
        /// <summary>Strike toward a world point now (titles, the depth probe). False while a strike runs.</summary>
        public bool RequestStrike(Vector3 target, float confidence, string source, float radius = 0.15f, float emphasis = 0.6f)
        {
            bool ok = _pred.RequestStrike(Time.timeAsDouble, target, radius, confidence, source, emphasis);
            if (ok && debugLog) Debug.Log("[kick] strike " + _pred.LastStrikeReason);
            return ok;
        }

        /// <summary>The depth probe saw a real foot at a kickable: contacts in the next <paramref name="seconds"/> report source "depth".</summary>
        public void MarkDetected(float seconds = 0.2f) { _detectedUntil = Time.time + seconds; }

        /// <summary>A virtual kickable (not a collider, e.g. a Lua-simulated ball). Update it every frame while it moves.</summary>
        public void SetTarget(string key, Vector3 pos, float radius)
        {
            if (string.IsNullOrEmpty(key)) return;
            _virtual[key] = new Vector4(pos.x, pos.y, pos.z, Mathf.Max(0.02f, radius));
        }
        public void ClearTarget(string key) { if (key != null) { _virtual.Remove(key); _lastVirtualTouch.Remove(key); } }

        public Vector3 GetDirection () {
            return _state.direction.sqrMagnitude > 1e-6f ? _state.direction : Vector3.forward;
        }

        /// <summary>v1 meaning kept: horizontal foot displacement per physics step × multiplier (speed × fixedDeltaTime).</summary>
        public float GetVelocity (float multiplier = 1f) {
            Vector3 v = _state.velocity;
            return multiplier * new Vector2(v.x, v.z).magnitude * Time.fixedDeltaTime;
        }

        public void OnDrawGizmos()
        {
            if (!head || !Application.isPlaying) return;
            Gizmos.color = _state.striking ? Color.red : Color.blue;
            Vector3 c = _state.position + Vector3.up * footCenterHeight;
            Gizmos.matrix = Matrix4x4.TRS(c, Quaternion.LookRotation(GetDirection()), Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, footSize);
            Gizmos.matrix = Matrix4x4.identity;
            Gizmos.DrawRay(c, _state.velocity * 0.25f);
        }
    }
}
