using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using Defective.JSON;

namespace DreamPark
{
    /// <summary>
    /// Where everybody is. One pose stream per headset, owned by the SDK, so
    /// no game ever streams player positions again.
    ///
    /// Every headset broadcasts its head and both hands at ≤ <see cref="hz"/>
    /// as PARK-LOCAL poses (see ParkRoot.cs), dead-banded so standing still
    /// costs a keepalive and nothing more, plus a small state bag any game can
    /// fill (dp.set_state). Every other headset turns that into a
    /// <see cref="RemotePlayer"/> under its own park root — smoothed anchor
    /// transforms content reads through dp.peers() — and, when it has that
    /// player's game loaded, a clone of their RemoteRig: the subtree of
    /// Player.prefab that IS what other players see (RemoteRig.cs).
    ///
    /// Identity is derived from the headset's device id, so the same player
    /// rejoining is the same id, and two Editors on one machine differ.
    /// Peers are reaped after <see cref="peerTimeoutSeconds"/> of silence; a
    /// headset that quits cleanly says goodbye first.
    ///
    /// Wire: type "pp", no netId (a session-level event), payload
    ///   {"i":id,"g":gameId,"n":seq,"h":[x,y,z,qx,qy,qz,qw],"l":[...],"r":[...],
    ///    "a":"l"|"r","nm":"display name","s":{state}}   — l / r absent when
    ///    that hand is not tracked; nm and s ride the periodic state resend.
    ///
    /// Self-creating (RuntimeInitializeOnLoadMethod); nothing to put in a scene.
    /// Does nothing until a DreamBoxClient is connected.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class PlayerPresence : MonoBehaviour
    {
        public const string TypePose = "pp";
        public const string TypeBye  = "pp_bye";

        public static PlayerPresence Instance { get; private set; }

        [Header("Send")]
        [Tooltip("Pose messages per second while moving. The relay budget is 60/s per headset for everything, and this is one stream for every game.")]
        public float hz = 12f;
        [Tooltip("Resend even when nothing moved, so peers can tell a still player from a dead link.")]
        public float keepaliveSeconds = 0.5f;
        [Tooltip("How often the state bag rides along even when unchanged, so a late joiner gets it.")]
        public float stateResendSeconds = 2f;
        public float deadbandMeters = 0.01f;
        public float deadbandDegrees = 2f;

        [Header("Peers")]
        public float peerTimeoutSeconds = 6f;
        [Tooltip("Anchor smoothing rate (1/s). 12 ≈ what a 12 Hz stream needs to read as continuous motion.")]
        public float smoothing = 12f;

        readonly Dictionary<string, RemotePlayer> _peers = new Dictionary<string, RemotePlayer>();
        public IReadOnlyDictionary<string, RemotePlayer> Peers => _peers;
        public int PeerCount => _peers.Count;

        public event Action<RemotePlayer> OnPeerJoined;
        public event Action<RemotePlayer> OnPeerLeft;

        // Join / leave events queued for the Lua pump (dp.on_peer_join / leave).
        // Bounded: nobody may ever drain them.
        public struct PeerEvent { public string kind; public string id; public RemotePlayer peer; }
        const int MaxPendingEvents = 64;
        readonly List<PeerEvent> _pendingEvents = new List<PeerEvent>();
        public int PendingEventCount => _pendingEvents.Count;
        public List<PeerEvent> DrainEvents()
        {
            var list = new List<PeerEvent>(_pendingEvents);
            _pendingEvents.Clear();
            return list;
        }
        void Queue(PeerEvent e)
        {
            if (_pendingEvents.Count >= MaxPendingEvents) _pendingEvents.RemoveAt(0);
            _pendingEvents.Add(e);
        }

        // ── Identity ─────────────────────────────────────────────────
        static string _myId;
        public static string MyId
        {
            get
            {
                if (_myId == null)
                {
                    string seed = SystemInfo.deviceUniqueIdentifier ?? "unknown";
#if UNITY_EDITOR
                    // Two Editors on one machine share a device id.
                    seed += ":" + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
#endif
                    _myId = Fnv8(seed);
                }
                return _myId;
            }
        }

        static string Fnv8(string s)
        {
            uint h = 2166136261;
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
            return h.ToString("x8", CultureInfo.InvariantCulture);
        }

        // ── Bootstrap ────────────────────────────────────────────────
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Instance != null) return;
            var go = new GameObject("~DreamParkPresence") { hideFlags = HideFlags.DontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<PlayerPresence>();
        }

        DreamBoxClient _client;
        float _nextSend;
        float _lastSent = -999f;
        float _lastStateSent = -999f;
        int _seq;
        string _stateJson = "{}";
        bool _stateDirty;
        string _lastGame = null;
        Vector3 _lastHeadPos, _lastLeftPos, _lastRightPos;
        Quaternion _lastHeadRot, _lastLeftRot, _lastRightRot;
        bool _lastLeftTracked, _lastRightTracked;
        string _lastActive = "";
        PlayerRig _trackerRig;
        HandTracker _tracker;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        void OnDestroy()
        {
            Unsubscribe();
            if (Instance == this) Instance = null;
        }

        void OnApplicationQuit() => SendBye();

        void Unsubscribe()
        {
            if (_client != null) _client.OnGlobalEvent -= HandleGlobal;
            _client = null;
        }

        /// <summary>Publish this headset's state bag (a JSON object). Rides on the next pose.</summary>
        public void SetStateJson(string json)
        {
            if (string.IsNullOrEmpty(json)) json = "{}";
            if (json == _stateJson) return;
            if (json.IndexOf("\"netId\"", StringComparison.Ordinal) >= 0)
            {
                // DreamBoxClient routes any message containing a netId field to
                // an object; a state key by that name would hijack the stream.
                Debug.LogWarning("[PlayerPresence] state bag ignored: a key named 'netId' is reserved by the relay.");
                return;
            }
            _stateJson = json;
            _stateDirty = true;
        }
        public string StateJson => _stateJson;

        public RemotePlayer GetPeer(string id)
        {
            if (id == null) return null;
            _peers.TryGetValue(id, out var p);
            return p;
        }

        void Update()
        {
            // The client is a scene singleton that can appear or be replaced at
            // any time (host migration, late bundle) — bind late, every frame.
            var client = DreamBoxClient.Instance;
            if (client != _client)
            {
                Unsubscribe();
                _client = client;
                if (_client != null) _client.OnGlobalEvent += HandleGlobal;
            }

            float now = Time.time;
            Reap(now);

            if (_client != null && _client.ConnectionState == DreamBoxClient.State.Connected && now >= _nextSend)
            {
                _nextSend = now + 1f / Mathf.Max(1f, hz);
                TrySend(now);
            }
        }

        // ── Send ─────────────────────────────────────────────────────
        void TrySend(float now)
        {
            var head = DreamParkLuaAPI.Head();
            if (head == null) return;

            var left  = HandAnchor("left",  out bool lt);
            var right = HandAnchor("right", out bool rt);
            string active = ActiveHandSide();
            string game = CurrentGameId();

            // Park-local, because world space is per headset.
            Vector3 hp = ParkRoot.ToPark(head.position);
            Quaternion hq = ParkRoot.ToParkRot(head.rotation);
            Vector3 lp = lt ? ParkRoot.ToPark(left.position)  : Vector3.zero;
            Quaternion lq = lt ? ParkRoot.ToParkRot(left.rotation)  : Quaternion.identity;
            Vector3 rp = rt ? ParkRoot.ToPark(right.position) : Vector3.zero;
            Quaternion rq = rt ? ParkRoot.ToParkRot(right.rotation) : Quaternion.identity;

            bool keepalive = now - _lastSent >= keepaliveSeconds;
            bool stateDue  = _stateDirty || now - _lastStateSent >= stateResendSeconds;
            bool changed =
                game != _lastGame || active != _lastActive ||
                lt != _lastLeftTracked || rt != _lastRightTracked ||
                Moved(hp, _lastHeadPos, hq, _lastHeadRot) ||
                (lt && Moved(lp, _lastLeftPos, lq, _lastLeftRot)) ||
                (rt && Moved(rp, _lastRightPos, rq, _lastRightRot));

            if (!changed && !keepalive && !stateDue) return;

            _seq++;
            var sb = new StringBuilder(320);
            sb.Append("{\"i\":\"").Append(MyId).Append("\",\"g\":\"").Append(Escape(game)).Append("\",\"n\":").Append(_seq);
            AppendPose(sb, "h", hp, hq);
            if (lt) AppendPose(sb, "l", lp, lq);
            if (rt) AppendPose(sb, "r", rp, rq);
            sb.Append(",\"a\":\"").Append(active).Append('"');
            if (stateDue)
            {
                sb.Append(",\"nm\":\"").Append(Escape(LocalDisplayName())).Append('\"');
                sb.Append(",\"s\":").Append(_stateJson);
                _stateDirty = false;
                _lastStateSent = now;
            }
            sb.Append('}');

            _client.PublishRaw(TypePose, sb.ToString());

            _lastSent = now;
            _lastGame = game; _lastActive = active;
            _lastHeadPos = hp; _lastHeadRot = hq;
            _lastLeftPos = lp; _lastLeftRot = lq; _lastLeftTracked = lt;
            _lastRightPos = rp; _lastRightRot = rq; _lastRightTracked = rt;
        }

        bool Moved(Vector3 p, Vector3 lastP, Quaternion q, Quaternion lastQ)
        {
            return (p - lastP).sqrMagnitude > deadbandMeters * deadbandMeters
                || Quaternion.Angle(q, lastQ) > deadbandDegrees;
        }

        static void AppendPose(StringBuilder sb, string key, Vector3 p, Quaternion q)
        {
            var ic = CultureInfo.InvariantCulture;
            sb.Append(",\"").Append(key).Append("\":[")
              .Append(p.x.ToString("F3", ic)).Append(',').Append(p.y.ToString("F3", ic)).Append(',').Append(p.z.ToString("F3", ic)).Append(',')
              .Append(q.x.ToString("F4", ic)).Append(',').Append(q.y.ToString("F4", ic)).Append(',').Append(q.z.ToString("F4", ic)).Append(',').Append(q.w.ToString("F4", ic))
              .Append(']');
        }

        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // ── Display name ─────────────────────────────────────────────
        // ProfileAPI resolves whenever the account binding lands; polled
        // slowly rather than subscribed so this file has no coupling to its
        // lifecycle. Rides the state cadence: late joiners have it within a
        // couple of seconds, and the fallback is stable per headset.
        static string _cachedName;
        static float _nextNameCheck;
        static string LocalDisplayName()
        {
            if (_cachedName == null || Time.realtimeSinceStartup >= _nextNameCheck)
            {
                _nextNameCheck = Time.realtimeSinceStartup + 5f;
                string n = null;
                try { n = API.ProfileAPI.DisplayName; } catch (Exception) { n = null; }
                if (string.IsNullOrEmpty(n)) n = RemoteNameTag.FallbackName(MyId);
                if (n.Length > 24) n = n.Substring(0, 24);
                _cachedName = n;
            }
            return _cachedName ?? "";
        }

        /// <summary>The game whose rig this headset is wearing right now, or "" between zones.</summary>
        static string CurrentGameId()
        {
            var rig = PlayerRig.Instance;
            if (rig != null && rig.gameObject.activeInHierarchy && !string.IsNullOrEmpty(rig.gameId)) return rig.gameId;
            return "";
        }

        HandTracker RigTracker()
        {
            var rig = PlayerRig.Instance;
            if (rig == null) { _trackerRig = null; _tracker = null; return null; }
            if (rig != _trackerRig || _tracker == null)
            {
                _trackerRig = rig;
                _tracker = rig.GetComponentInChildren<HandTracker>(true);
            }
            return _tracker;
        }

        // The hand pose source: the OVRCameraRig hand anchors, resolved by
        // name exactly the way HandTracker resolves its own for the client.
        // The OVRHand's parent is NOT a pose source: in hand-tracking rigs
        // the OVRHand can sit under a static container while the anchor
        // tracks, which put remote blasters at the rig origin while name
        // tags (head = Camera.main) were right.
        static Transform _sceneAnchorL, _sceneAnchorR;
        static Transform SceneHandAnchor(string side)
        {
            // Explicit branches: a destroyed Transform is Unity fake-null,
            // caught by the == overload, so a scene reload re-resolves.
            if (side == "left")
            {
                if (_sceneAnchorL == null)
                {
                    var g = GameObject.Find("LeftHandAnchor");
                    _sceneAnchorL = g != null ? g.transform : null;
                }
                return _sceneAnchorL;
            }
            if (_sceneAnchorR == null)
            {
                var g = GameObject.Find("RightHandAnchor");
                _sceneAnchorR = g != null ? g.transform : null;
            }
            return _sceneAnchorR;
        }

        // PHYSICAL truth only, on purpose: presence is the game-independent
        // body bus. Head = Camera.main, hands = the OVRCameraRig hand
        // anchors — the transforms OVR itself drives, one set per headset,
        // alive with no game rig at all. Player rigs are per game and load
        // dynamically (there can be ten of them): sampling a rig node here
        // would couple every player's broadcast to whichever game they are
        // wearing. Rig-side presentation (hand choice, flipVisual, model
        // offsets) belongs to each game's rig locally and to its RemoteRig
        // subtree remotely — both are pure functions of this same physical
        // stream, and p.active_hand plus the tracked flags carry what a
        // RemoteRig needs to reproduce the client-side choices.
        // "tracked" comes from the worn rig's OVRHand refs when there is
        // one; without any (Simulator, SDK test scene) an active anchor
        // counts as tracked.
        Transform HandAnchor(string side, out bool tracked)
        {
            tracked = false;
            var anchor = SceneHandAnchor(side);
            if (anchor == null) anchor = DreamParkLuaAPI.Hand(side);   // Simulator / odd-rig fallback
            if (anchor == null) return null;
            var tracker = RigTracker();
            OVRHand hand = tracker != null ? (side == "left" ? tracker.leftHand : tracker.rightHand) : null;
            tracked = hand != null ? hand.IsTracked : anchor.gameObject.activeInHierarchy;
            return anchor;
        }

        string ActiveHandSide()
        {
            var tracker = RigTracker();
            if (tracker != null)
            {
                if (tracker.handPreference == HandTracker.HandPreference.Left) return "l";
                if (tracker.handPreference == HandTracker.HandPreference.Right) return "r";
                var active = tracker.ActiveHand;
                if (active != null && tracker.leftHand != null && active == tracker.leftHand) return "l";
            }
            return "r";
        }

        void SendBye()
        {
            if (_client == null || _client.ConnectionState != DreamBoxClient.State.Connected) return;
            _client.PublishRaw(TypeBye, "{\"i\":\"" + MyId + "\"}");
        }

        // ── Receive ──────────────────────────────────────────────────
        void HandleGlobal(string type, string json)
        {
            if (type != TypePose && type != TypeBye) return;
            try
            {
                var obj = new JSONObject(json);
                var p = obj.GetField("payload");
                if (p == null) return;
                string id = p.GetField("i")?.stringValue;
                if (string.IsNullOrEmpty(id) || id == MyId) return;

                if (type == TypeBye) { Remove(id); return; }

                var peer = GetOrCreate(id);
                int seq = 0;
                var nf = p.GetField("n"); if (nf != null) seq = (int)nf.floatValue;
                string game = p.GetField("g")?.stringValue ?? "";
                string active = p.GetField("a")?.stringValue;
                var sf = p.GetField("s");
                string state = sf != null && sf.type == JSONObject.Type.Object ? sf.Print() : null;
                string name = p.GetField("nm")?.stringValue;

                ReadPose(p.GetField("h"), out var hp, out var hq);
                ReadPose(p.GetField("l"), out var lp, out var lq);
                ReadPose(p.GetField("r"), out var rp, out var rq);
                peer.Receive(seq, game, hp, hq, lp, lq, rp, rq, active, state, name);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PlayerPresence] bad " + type + " message: " + e.Message);
            }
        }

        static void ReadPose(JSONObject arr, out Vector3? pos, out Quaternion? rot)
        {
            pos = null; rot = null;
            if (arr == null || arr.type != JSONObject.Type.Array || arr.list == null || arr.list.Count < 3) return;
            var l = arr.list;
            pos = new Vector3(l[0].floatValue, l[1].floatValue, l[2].floatValue);
            if (l.Count >= 7)
            {
                var q = new Quaternion(l[3].floatValue, l[4].floatValue, l[5].floatValue, l[6].floatValue);
                if (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w > 0.5f) rot = Quaternion.Normalize(q);
            }
        }

        RemotePlayer GetOrCreate(string id)
        {
            if (_peers.TryGetValue(id, out var existing) && existing != null) return existing;
            var go = new GameObject("RemotePlayer_" + id);
            go.transform.SetParent(ParkRoot.Resolve(), false);
            var peer = go.AddComponent<RemotePlayer>();
            peer.Init(id, smoothing);
            _peers[id] = peer;
            Queue(new PeerEvent { kind = "join", id = id, peer = peer });
            try { OnPeerJoined?.Invoke(peer); } catch (Exception e) { Debug.LogWarning("[PlayerPresence] OnPeerJoined handler threw: " + e.Message); }
            NetLog.V("[PlayerPresence] peer joined " + id);
            return peer;
        }

        void Remove(string id)
        {
            if (!_peers.TryGetValue(id, out var peer)) return;
            _peers.Remove(id);
            Queue(new PeerEvent { kind = "leave", id = id, peer = null });
            try { OnPeerLeft?.Invoke(peer); } catch (Exception e) { Debug.LogWarning("[PlayerPresence] OnPeerLeft handler threw: " + e.Message); }
            if (peer != null) Destroy(peer.gameObject);
            NetLog.V("[PlayerPresence] peer left " + id);
        }

        List<string> _stale;
        void Reap(float now)
        {
            if (_peers.Count == 0) return;
            if (_stale == null) _stale = new List<string>();
            _stale.Clear();
            foreach (var kv in _peers)
                if (kv.Value == null || now - kv.Value.lastSeen > peerTimeoutSeconds) _stale.Add(kv.Key);
            for (int i = 0; i < _stale.Count; i++) Remove(_stale[i]);
        }
    }
}
