// ─────────────────────────────────────────────────────────────────────
//  ParkSimPeers.cs — simulated multiplayer peers for an Editor Play session
//
//  THE PROBLEM. Every multiplayer bug Birthday Bash shipped to its first
//  two-headset playtest (a private boss per headset, a late joiner that never
//  synced, a leader nobody agreed on) was invisible in the Editor, because the
//  Editor is one player. Finding out needed two headsets, a QR code and
//  twenty minutes per try.
//
//  WHAT THIS IS. N simulated players in the SAME Play session, talking to
//  the Editor's game over the REAL wire:
//
//    • the relay is the shipping PeerRelayServer (no echo to the sender,
//      60 msg/s per peer with silent drops, ReliableOrdered, 16 KB cap),
//      hosted in-process on loopback — no beacon, so it can never be found
//      by, or merge with, a real headset on the same Wi-Fi;
//    • the Editor's own DreamBoxClient connects to it exactly as a headset
//      that won the election connects to its own relay, so gameplay and Lua
//      cannot tell the difference (dp.relay().connected is true);
//    • every simulated peer is its own LiteNetLib connection that speaks the
//      real PlayerPresence wire ("pp"/"pp_bye": head and hands, park-local,
//      dead-banded at 12 Hz, state bag every 2 s), so the Editor sees real
//      RemotePlayers — dp.peers(), name tags, RemoteRig clones, on_peer_join,
//      and the session leader election that counts them;
//    • peers can send any message to any NetId (the same id the Editor's own
//      object registered, because NetIds are identical on every device by
//      design), can drop off the Wi-Fi, quit cleanly or crash.
//
//  WHERE THE PEERS' GAME LIVES — a deliberate choice. A simulated peer runs the
//  platform's own engines (its SessionEngine and a SharedState replica of every
//  state the Editor's game opened), but it does not run a second copy of the
//  attraction's Lua.
//  One Unity process has one NetRegistry, one DreamBoxClient.Instance, one
//  Lua global table, one Camera.main, one PlayerRig and one session identity
//  (dp.me, dp.is_leader); a second in-scene copy of the attraction would
//  share all of them, collide on every NetId and answer as the local player —
//  a configuration that never exists on a device, so a pass there proves
//  nothing. Instead the Editor runs the ONE real copy of the game, and each
//  peer does what a remote headset does ON THE WIRE: it is present, it moves,
//  it beats, syncs and holds the room's shared state, it sends the game's
//  requests ("peer_send" / "peer_request"), and it is checked by what it
//  RECEIVED and holds ("recv", "state_agrees", "leader"). That covers the bug class
//  that matters — does the real game answer a late joiner, stay one fight
//  when two players start it, survive its leader leaving, keep one owner for
//  a shared prop, stay under budget. To test N full copies of the game's
//  logic against each other, use a headless Lua harness (see MULTIPLAYER.md
//  § Testing) — complementary, not a replacement: it cannot run Unity.
//
//  Driving it: DreamPark → Multiplayer → Simulated Peers (window), the
//  dreampark_peers_* CLI commands (ParkSimPeerCommands.cs), or this static
//  API from a builder (unity command --project-path . run_script). Scenarios (JSON, six
//  built in: ParkSimPeerBuiltins.cs) run through ParkSimPeerScenario.cs.
//
//  Everything here is Play-mode-only and torn down when Play stops. Nothing
//  is written to the scene. The NetSessionArbiter, if the scene has one, is
//  disabled for the session (so it cannot elect itself onto the LAN) and
//  re-enabled when the session stops.
// ─────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DreamPark.ParkSim
{
    /// One message a simulated peer received from the relay.
    public sealed class SimPeerMessage
    {
        public double t;          // SimPeers.Now when it arrived
        public string type;       // envelope "type"
        public bool hasNetId;
        public uint netId;
        public string presenceId; // "i" of a pp / pp_bye
        public string json;       // the full wire JSON
    }

    public enum SimPeerRank { High, Low }

    /// How to create a peer. Positions are PARK-LOCAL (the frame on the wire).
    public sealed class SimPeerOptions
    {
        public string name;
        public string id;                       // 8 hex; null = derived from rank + name
        public SimPeerRank rank = SimPeerRank.High;
        public string device = "headset";       // "headset" | "phone"
        public string gameId;                   // null = the local rig's gameId
        public Vector3? at;                     // park-local head position
        public string near;                     // "local" or an object path; overrides at
        public float? yaw;
        public bool sway = true;                // idle head sway so pose dead bands trip like a real person
        /// True (default): the peer runs the platform's own session + shared-state engines
        /// (DreamParkSession's SessionEngine, a SharedState replica of every state the
        /// Editor's game opened), so it beats, syncs, answers late joiners and can take
        /// over as writer exactly like a headset. False: wire-level only (presence + sends).
        public bool platform = true;
        /// False = this peer must never become the session leader. A simulated peer
        /// is wire-only and cannot do a leader's job, so unless a scenario wants a
        /// leader that goes silent, keep this false. Forces an id that sorts above
        /// any real device id (and rides presence once the session protocol carries it).
        public bool leaderCapable = true;
    }

    /// <summary>One simulated player: a real relay connection plus a scripted body.</summary>
    public sealed class SimPeer
    {
        public string Name { get; }
        public string Id { get; }
        public string Device { get; set; }
        public string GameId { get; set; }
        public string DisplayName { get; set; }
        public bool LeaderCapable { get; set; } = true;
        /// True when this peer plays the Editor's own title (no explicit "game" given): it then
        /// holds every shared state the Editor's game opened, title-less ones included.
        public bool SameTitle { get; set; } = true;

        // Body, park-local.
        public Vector3 HeadPos;
        public float Yaw, Pitch;
        public Vector3? LeftHand, RightHand;      // null = follows the head
        public bool LeftTracked = true, RightTracked = true;
        public string ActiveHand = "r";
        public bool Sway;
        public string StateJson = "{}";
        bool _stateDirty = true;

        // Walking.
        Vector3? _walkTo;
        float _walkSpeed = 1.2f;
        public bool Walking => _walkTo.HasValue;

        // Link.
        public bool Connected => _server != null && _server.ConnectionState == ConnectionState.Connected;
        public bool LinkUp = true;
        public bool Gone { get; private set; }
        public string GoneHow { get; private set; }

        // Traffic.
        public readonly List<SimPeerMessage> Inbox = new List<SimPeerMessage>();
        public readonly Dictionary<string, int> SentByKind = new Dictionary<string, int>();
        public int SentCount { get; private set; }
        public int ReceivedCount { get; private set; }
        const int InboxCap = 20000;

        NetManager _net;
        NetPeer _server;
        EventBasedNetListener _listener;
        int _seq;
        double _nextSend, _lastSent = -999, _lastStateSent = -999;
        Vector3 _lastHead, _lastL, _lastR;
        Quaternion _lastHeadQ = Quaternion.identity;
        readonly double _swayPhase;

        public SimPeer(string name, string id, string device, string gameId, bool platform = true)
        {
            Name = name;
            Id = id;
            Device = device;
            GameId = gameId ?? "";
            DisplayName = "Sim " + name;
            _swayPhase = (Fnv(id) % 1000) / 159.0;
            if (platform)
                Engine = new SessionEngine(Id, new SimSessionLink(this), () => Time.realtimeSinceStartupAsDouble,
                    () => Device == "phone", Titles, Worn);
        }

        /// The rig this peer wears, as a headset's session beat says ("w"): its game's
        /// Player — the same title its presence ("g") names. dp.leader() is scoped by it.
        IEnumerable<string> Worn() => string.IsNullOrEmpty(GameId) ? Array.Empty<string>() : new[] { GameId };

        /// The titles this peer announces: the Editor's own (it plays the same game) or its explicit one.
        IEnumerable<string> Titles()
        {
            if (!SameTitle) return string.IsNullOrEmpty(GameId) ? Array.Empty<string>() : new[] { GameId };
            return SimPeers.LocalTitles();
        }

        // ── Platform brain: the SDK's own engines, run for this peer ─────────
        /// The peer's own session (leader / clock / seed, from its point of view). null = wire-only.
        public SessionEngine Engine { get; }
        readonly Dictionary<string, SharedState> _replicas = new Dictionary<string, SharedState>(StringComparer.Ordinal);
        double _nextMirror;
        public IEnumerable<SharedState> Replicas => _replicas.Values;
        public SharedState Replica(string busKey, string name) => _replicas.TryGetValue(busKey + "|" + name, out var r) ? r : null;

        sealed class SimSessionLink : ISessionLink
        {
            readonly SimPeer _p;
            public SimSessionLink(SimPeer p) { _p = p; }
            public bool Present => true;
            public bool Connected => _p.Connected && _p.LinkUp && !_p.Gone;
            public int PingMs => 1;
            public void Publish(string type, string payloadJson) => _p.RawSend(type, payloadJson);
        }

        /// Hold a replica of every shared state the Editor's game opened for a title this
        /// peer runs — what that player's own copy of the game would have opened.
        void MirrorStates(double now)
        {
            if (Engine == null || now < _nextMirror) return;
            _nextMirror = now + 0.5;
            foreach (var st in DreamParkSession.Engine.SharedStates)
            {
                if (st == null || st.Disposed || string.IsNullOrEmpty(st.BusKey)) continue;
                // A player of ANOTHER title never holds this title's states.
                if (!string.IsNullOrEmpty(st.Title) ? st.Title != GameId && !SameTitle : !SameTitle) continue;
                string key = st.BusKey + "|" + st.Name;
                if (_replicas.ContainsKey(key)) continue;
                _replicas[key] = SharedState.CreateDetached(Engine, st.BusKey, st.Name, st.Frame, st.Title);
            }
        }

        // ── Connection ───────────────────────────────────────────────
        public void Connect(int port, string key)
        {
            _listener = new EventBasedNetListener();
            _net = new NetManager(_listener) { DisconnectTimeout = 5000 };
            _listener.PeerConnectedEvent += p => { _server = p; SimPeers.Log($"{Name} ({Id}) connected to the relay"); };
            _listener.PeerDisconnectedEvent += (p, info) => { if (_server == p) _server = null; };
            _listener.NetworkReceiveEvent += (p, reader, ch, method) =>
            {
                try
                {
                    string json = reader.GetString();
                    if (LinkUp && !Gone && !string.IsNullOrEmpty(json)) Receive(json);
                }
                catch (Exception e) { Debug.LogWarning("[SimPeers] " + Name + " bad message: " + e.Message); }
                finally { reader.Recycle(); }
            };
            _net.Start();
            _net.Connect("127.0.0.1", port, key);
        }

        void Receive(string json)
        {
            ReceivedCount++;
            var m = new SimPeerMessage { t = SimPeers.Now, json = json, type = ExtractString(json, "type") };
            m.hasNetId = TryExtractNetId(json, out m.netId);
            if (m.type == "pp" || m.type == "pp_bye") m.presenceId = ExtractString(json, "i");
            if (Engine != null && m.type != null && (m.type.StartsWith("_") || m.type == "pp_bye"))
            {
                try { Engine.HandleGlobal(m.type, json); }
                catch (Exception e) { Debug.LogWarning("[SimPeers] " + Name + " session: " + e.Message); }
            }
            if (Inbox.Count >= InboxCap) Inbox.RemoveRange(0, InboxCap / 4);
            Inbox.Add(m);
        }

        /// Quit = what a headset does on a clean exit: say goodbye, then close.
        public void Quit()
        {
            if (Gone) return;
            if (Connected)
            {
                try { Engine?.Shutdown(); } catch { }
                RawSend("pp_bye", "{\"i\":\"" + Id + "\"}");
                // Stop() right after Send() can drop the goodbye; give the socket thread a moment,
                // as NetSessionArbiter does for host_leaving.
                for (int i = 0; i < 3; i++) { _net?.TriggerUpdate(); _net?.PollEvents(); System.Threading.Thread.Sleep(20); }
            }
            _net?.Stop(true);
            Gone = true; GoneHow = "quit";
        }

        /// Crash = battery died / doffed: no goodbye, no disconnect packet. The
        /// relay and everyone else find out from silence (relay ~5 s, presence 6 s).
        public void Crash()
        {
            if (Gone) return;
            _net?.Stop(false);
            Gone = true; GoneHow = "crash";
        }

        // ── Sending ──────────────────────────────────────────────────
        /// Same envelope DreamBoxClient.Send builds.
        public bool RawSend(string type, string payloadJson)
        {
            if (Gone || !LinkUp || !Connected) return false;
            string json = "{\"type\":\"" + type + "\",\"payload\":" + (string.IsNullOrEmpty(payloadJson) ? "{}" : payloadJson) + "}";
            var w = new NetDataWriter();
            w.Put(json);
            _server.Send(w, DeliveryMethod.ReliableOrdered);
            SentCount++;
            SentByKind.TryGetValue(type, out int n);
            SentByKind[type] = n + 1;
            SimPeers.NoteSimSend(type);
            return true;
        }

        /// Same body DreamBoxClient.SendToNetId builds — what net_send does on a device.
        public bool SendToNetId(uint netId, string type, string bodyJson)
        {
            string body = (bodyJson ?? "").Trim();
            if (body.Length >= 2 && body[0] == '{' && body[body.Length - 1] == '}') body = body.Substring(1, body.Length - 2).Trim();
            string payload = "{\"netId\":" + netId.ToString(CultureInfo.InvariantCulture) + (body.Length > 0 ? "," + body : "") + "}";
            return RawSend(type, payload);
        }

        public void SetState(string json)
        {
            StateJson = string.IsNullOrEmpty(json) ? "{}" : json;
            _stateDirty = true;
        }

        public void WalkTo(Vector3 parkPos, float speed)
        {
            _walkTo = parkPos;
            _walkSpeed = Mathf.Max(0.1f, speed);
        }

        public void StopWalking() => _walkTo = null;

        // ── Pushing a dp.net_body prop (the "nb" wire, NetBodies.cs) ─────────────
        sealed class Push { public uint netId; public int rev; public Vector3 from, by; public Quaternion rot; public double t0, dur, next; }
        readonly List<Push> _pushes = new List<Push>();

        /// Push a networked physics prop the way a remote player's hand does: claim it with
        /// a newer revision and stream its frame-local pose at 10 Hz for `seconds`, then a
        /// settled pose that releases the lease. `fromLocal`/`rot` are attraction-frame values.
        public void PushBody(uint netId, int currentRev, Vector3 fromLocal, Quaternion rot, Vector3 by, float seconds)
        {
            _pushes.Add(new Push { netId = netId, rev = currentRev + 1, from = fromLocal, by = by, rot = rot,
                                   t0 = SimPeers.Now, dur = Math.Max(0.1, seconds), next = 0 });
        }

        void TickPushes(double now)
        {
            for (int i = _pushes.Count - 1; i >= 0; i--)
            {
                var k = _pushes[i];
                if (now < k.next) continue;
                k.next = now + 0.1;
                double f = Math.Min(1.0, (now - k.t0) / k.dur);
                bool done = f >= 1.0;
                Vector3 pos = k.from + k.by * (float)f;
                Vector3 vel = done ? Vector3.zero : k.by / (float)k.dur;
                var ic = CultureInfo.InvariantCulture;
                string V(Vector3 v) => "[" + v.x.ToString("F3", ic) + "," + v.y.ToString("F3", ic) + "," + v.z.ToString("F3", ic) + "]";
                string body = "{\"u\":\"" + Id + "\",\"w\":\"" + Id + "\",\"r\":" + k.rev + ",\"p\":" + V(pos)
                    + ",\"q\":[" + k.rot.x.ToString("F4", ic) + "," + k.rot.y.ToString("F4", ic) + "," + k.rot.z.ToString("F4", ic) + "," + k.rot.w.ToString("F4", ic) + "]"
                    + ",\"v\":" + V(vel) + ",\"s\":" + (done ? 1 : 0) + ",\"h\":0}";
                SendToNetId(k.netId, "nb", body);
                if (done) _pushes.RemoveAt(i);
            }
        }

        // ── Tick ─────────────────────────────────────────────────────
        public void Tick(double now, float dt)
        {
            if (_net == null) return;
            if (!Gone) _net.PollEvents();
            if (Gone) return;

            if (_walkTo.HasValue)
            {
                Vector3 to = _walkTo.Value;
                Vector3 flat = new Vector3(to.x - HeadPos.x, 0, to.z - HeadPos.z);
                float step = _walkSpeed * dt;
                if (flat.magnitude <= step) { HeadPos = new Vector3(to.x, HeadPos.y, to.z); _walkTo = null; }
                else
                {
                    HeadPos += flat.normalized * step;
                    Yaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
                }
            }

            TickPushes(now);
            if (Engine != null && LinkUp)
            {
                MirrorStates(now);
                try { Engine.Tick(); } catch (Exception e) { Debug.LogWarning("[SimPeers] " + Name + " session tick: " + e.Message); }
            }
            if (now < _nextSend) return;
            _nextSend = now + 1.0 / PresenceHz;
            SendPresence(now);
        }

        public const float PresenceHz = 12f;

        Quaternion HeadRot(double now)
        {
            float yaw = Yaw, pitch = Pitch;
            if (Sway)
            {
                // A standing person is never still: ±6° look-around every few seconds.
                yaw += 6f * (float)Math.Sin(now * 0.7 + _swayPhase);
                pitch += 3f * (float)Math.Sin(now * 0.45 + _swayPhase * 2);
            }
            return Quaternion.Euler(pitch, yaw, 0);
        }

        public Vector3 HandPos(string side, double now)
        {
            var pinned = side == "left" ? LeftHand : RightHand;
            if (pinned.HasValue) return pinned.Value;
            var q = Quaternion.Euler(0, Yaw, 0);
            return HeadPos + q * new Vector3(side == "left" ? -0.22f : 0.22f, -0.45f, 0.28f);
        }

        void SendPresence(double now)
        {
            var hq = HeadRot(now);
            var lp = HandPos("left", now);
            var rp = HandPos("right", now);
            bool keepalive = now - _lastSent >= 0.5;
            bool stateDue = _stateDirty || now - _lastStateSent >= 2.0;
            bool moved = (HeadPos - _lastHead).sqrMagnitude > 1e-4f || Quaternion.Angle(hq, _lastHeadQ) > 2f
                         || (lp - _lastL).sqrMagnitude > 1e-4f || (rp - _lastR).sqrMagnitude > 1e-4f;
            if (!moved && !keepalive && !stateDue) return;

            _seq++;
            var sb = new StringBuilder(320);
            sb.Append("{\"i\":\"").Append(Id).Append("\",\"g\":\"").Append(Esc(GameId)).Append("\",\"n\":").Append(_seq);
            Pose(sb, "h", HeadPos, hq);
            var handQ = Quaternion.Euler(0, Yaw, 0);
            if (LeftTracked) Pose(sb, "l", lp, handQ);
            if (RightTracked) Pose(sb, "r", rp, handQ);
            sb.Append(",\"a\":\"").Append(ActiveHand).Append('"');
            if (stateDue)
            {
                sb.Append(",\"nm\":\"").Append(Esc(DisplayName)).Append('"');
                sb.Append(",\"av\":\"\"");
                sb.Append(",\"dv\":\"").Append(Device).Append('"');
                sb.Append(",\"s\":").Append(StateJson);
                _stateDirty = false;
                _lastStateSent = now;
            }
            sb.Append('}');
            if (!RawSend("pp", sb.ToString())) return;
            _lastSent = now;
            _lastHead = HeadPos; _lastHeadQ = hq; _lastL = lp; _lastR = rp;
        }

        static void Pose(StringBuilder sb, string key, Vector3 p, Quaternion q)
        {
            var ic = CultureInfo.InvariantCulture;
            sb.Append(",\"").Append(key).Append("\":[")
              .Append(p.x.ToString("F3", ic)).Append(',').Append(p.y.ToString("F3", ic)).Append(',').Append(p.z.ToString("F3", ic)).Append(',')
              .Append(q.x.ToString("F4", ic)).Append(',').Append(q.y.ToString("F4", ic)).Append(',').Append(q.z.ToString("F4", ic)).Append(',').Append(q.w.ToString("F4", ic))
              .Append(']');
        }

        // ── Inbox queries ────────────────────────────────────────────
        public IEnumerable<SimPeerMessage> Received(string type = null, uint? netId = null, double since = 0)
        {
            foreach (var m in Inbox)
            {
                if (m.t < since) continue;
                if (type != null && m.type != type) continue;
                if (netId.HasValue && (!m.hasNetId || m.netId != netId.Value)) continue;
                yield return m;
            }
        }

        public bool Sees(string presenceId, double since = 0) =>
            Inbox.Any(m => m.t >= since && m.type == "pp" && m.presenceId == presenceId);

        // ── Helpers ──────────────────────────────────────────────────
        internal static string Esc(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        internal static uint Fnv(string s)
        {
            uint h = 2166136261;
            foreach (char c in s ?? "") { h ^= c; h *= 16777619; }
            return h;
        }

        internal static string ExtractString(string json, string key)
        {
            int idx = json.IndexOf("\"" + key + "\":\"", StringComparison.Ordinal);
            if (idx < 0) return null;
            int start = idx + key.Length + 4;
            int end = json.IndexOf('"', start);
            if (end < start || end - start > 128) return null;
            return json.Substring(start, end - start);
        }

        /// Same scan DreamBoxClient.HandleEvent routes by.
        internal static bool TryExtractNetId(string json, out uint id)
        {
            id = 0;
            int idx = json.IndexOf("\"netId\":", StringComparison.Ordinal);
            if (idx < 0) return false;
            int s = idx + 8;
            while (s < json.Length && json[s] == ' ') s++;
            int e = s;
            while (e < json.Length && char.IsDigit(json[e])) e++;
            return e > s && uint.TryParse(json.Substring(s, e - s), out id);
        }
    }

    /// <summary>
    /// The simulated-peer session: one loopback relay, the Editor's client on
    /// it, and the peers. Static, Play-mode-only, pumped from EditorApplication.update.
    /// </summary>
    [InitializeOnLoad]
    public static class SimPeers
    {
        public const int PreferredPort = 7790;   // never 7777: a local DreamBox dev server may own it
        public const string Tag = "[SimPeers]";

        public static bool Active { get; private set; }
        public static PeerRelayServer Relay { get; private set; }
        public static string Key { get; private set; }
        public static IReadOnlyList<SimPeer> All => _peers;
        public static IEnumerable<SimPeer> Live => _peers.Where(p => !p.Gone);
        public static double Now => EditorApplication.timeSinceStartup;
        public static readonly List<string> SessionLog = new List<string>();

        static readonly List<SimPeer> _peers = new List<SimPeer>();
        static DreamBoxClient _client;
        static bool _createdClient;
        static NetSessionArbiter _disabledArbiter;
        static double _connectAt = -1;
        static double _lastTick;

        // Local send meter: DreamBoxClient.SentCount samples, for the budget check.
        static readonly List<KeyValuePair<double, int>> _localSends = new List<KeyValuePair<double, int>>();
        static readonly List<KeyValuePair<double, string>> _simSends = new List<KeyValuePair<double, string>>();

        static SimPeers()
        {
            EditorApplication.update += Pump;
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.ExitingPlayMode) Stop();
                if (s == PlayModeStateChange.EnteredPlayMode && SimPeersSettings.AutoPeers > 0)
                    _autoStartAt = Now + 1.0;
            };
        }
        static double _autoStartAt = -1;

        public static void Log(string msg)
        {
            string line = $"{Now:0.00} {msg}";
            SessionLog.Add(line);
            if (SessionLog.Count > 2000) SessionLog.RemoveRange(0, 500);
            Debug.Log(Tag + " " + msg);
        }

        // ── Session ──────────────────────────────────────────────────
        /// Start the loopback relay and connect the Editor to it. Returns null on success, else an error.
        public static string Start()
        {
            if (!EditorApplication.isPlaying) return "Not in Play mode. Enter Play first (editor_play).";
            if (Active) return null;

            Key = "sim" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Relay = new PeerRelayServer();
            if (!Relay.Start(Key, PreferredPort)) { Relay = null; return "Could not bind a loopback relay port near " + PreferredPort + "."; }

            // The arbiter would elect itself onto the LAN, or join a real headset's
            // session. Park it for the duration; OnDisable stops its relay/discovery.
            var arbiter = NetSessionArbiter.Instance;
            if (arbiter != null && arbiter.enabled)
            {
                arbiter.enabled = false;
                _disabledArbiter = arbiter;
                Log("NetSessionArbiter disabled for the simulated session (re-enabled on stop).");
            }

            _client = DreamBoxClient.Instance;
            _createdClient = false;
            if (_client == null)
            {
                var go = new GameObject("DreamBoxClient (Simulated Peers)");
                _client = go.AddComponent<DreamBoxClient>();
                _createdClient = true;
            }
            // DreamBoxClient.Start() (re)creates its NetManager, so connect after it ran.
            _connectAt = Now + (_createdClient ? 0.25 : 0.0);
            Active = true;
            _localSends.Clear();
            _simSends.Clear();
            Log($"Loopback relay up on 127.0.0.1:{Relay.Port} (real PeerRelayServer: no echo, {PeerRelayServer.MaxMessagesPerPeerPerSecond}/s per peer, ReliableOrdered). Local id {PlayerPresence.MyId}.");
            return null;
        }

        public static void Stop()
        {
            if (!Active && _peers.Count == 0) return;
            foreach (var p in _peers) { try { p.Quit(); } catch { } }
            _peers.Clear();
            if (_client != null)
            {
                try { if (_client.ServerAddress.EndsWith(":" + (Relay?.Port ?? -1))) _client.Disconnect(); } catch { }
                if (_createdClient && _client != null) UnityEngine.Object.Destroy(_client.gameObject);
            }
            _client = null;
            try { Relay?.Stop(); } catch { }
            Relay = null;
            Misaligned = false;
            _frame = null;
            if (_disabledArbiter != null) { try { _disabledArbiter.enabled = true; } catch { } }
            _disabledArbiter = null;
            Active = false;
            _connectAt = -1;
            SimPeerScenario.AbortRunning("session stopped");
            Log("Session stopped.");
        }

        public static bool LocalConnected =>
            Active && DreamBoxClient.Instance != null && DreamBoxClient.Instance.ConnectionState == DreamBoxClient.State.Connected;

        // ── Peers ────────────────────────────────────────────────────
        public static SimPeer Add(SimPeerOptions o, out string error)
        {
            error = null;
            if (!Active) { error = Start(); if (error != null) return null; }
            o = o ?? new SimPeerOptions();
            string name = string.IsNullOrEmpty(o.name) ? NextName() : o.name;
            if (Find(name) != null) { error = $"A live peer named '{name}' already exists."; return null; }
            if (!o.leaderCapable) o.rank = SimPeerRank.High;
            string id = !string.IsNullOrEmpty(o.id) && o.leaderCapable ? o.id.ToLowerInvariant() : MakeId(name, o.rank);
            string gameId = o.gameId;
            if (gameId == null) gameId = PlayerRig.Instance != null ? PlayerRig.Instance.gameId : "";

            var p = new SimPeer(name, id, o.device ?? "headset", gameId, o.platform) { Sway = o.sway, LeaderCapable = o.leaderCapable, SameTitle = o.gameId == null };
            int index = _peers.Count(x => !x.Gone);
            p.HeadPos = ResolveSpot(o.near, o.at, index, out float faceYaw);
            p.Yaw = o.yaw ?? faceYaw;
            _peers.RemoveAll(x => x.Gone && x.Name == name);
            _peers.Add(p);
            p.Connect(Relay.Port, Key);
            Log($"+ peer {name} id {id} ({p.Device}{(string.Equals(id, PlayerPresence.MyId, StringComparison.Ordinal) ? ", SAME AS LOCAL" : "")}, ranks {(string.CompareOrdinal(id, PlayerPresence.MyId) < 0 ? "BELOW" : "above")} local {PlayerPresence.MyId}) at park {Fmt(p.HeadPos)}");
            return p;
        }

        public static bool Remove(string name, string mode, out string error)
        {
            error = null;
            var p = Find(name);
            if (p == null) { error = $"No live peer named '{name}'."; return false; }
            if (mode == "crash") p.Crash(); else p.Quit();
            Log($"- peer {name} {p.GoneHow}");
            return true;
        }

        /// Every live peer quits cleanly (pp_bye), and the dead are forgotten.
        public static void RemoveAll()
        {
            foreach (var p in _peers) if (!p.Gone) p.Quit();
            _peers.Clear();
        }

        public static SimPeer Find(string name) =>
            _peers.LastOrDefault(p => !p.Gone && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        public static SimPeer FindAny(string name) =>
            _peers.LastOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        static string NextName()
        {
            for (char c = 'B'; c <= 'Z'; c++)
                if (FindAny(c.ToString()) == null) return c.ToString();
            return "P" + _peers.Count;
        }

        /// Rank decides leadership: the session leader is the LOWEST id. "low" peers
        /// sort below any real device id, "high" above, so a scenario chooses who leads.
        static string MakeId(string name, SimPeerRank rank)
        {
            uint h = SimPeer.Fnv("simpeer:" + name) & 0xFFFF;
            return (rank == SimPeerRank.Low ? "0000" : "ffff") + h.ToString("x4", CultureInfo.InvariantCulture);
        }

        /// Titles with content live in the Editor (what DreamParkSession announces for it).
        public static IEnumerable<string> LocalTitles()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in GameArea.allGameAreas) if (a != null && !string.IsNullOrEmpty(a.gameId)) set.Add(a.gameId);
            if (PlayerRig.instances != null)
                foreach (var kv in PlayerRig.instances) if (kv.Value != null && !string.IsNullOrEmpty(kv.Key)) set.Add(kv.Key);
            return set;
        }

        // ── Places (park-local) ──────────────────────────────────────
        public static Vector3 LocalHeadPark()
        {
            var head = DreamParkLuaAPI.Head();
            if (head == null && Camera.main != null) head = Camera.main.transform;
            return head != null ? ParkRoot.ToPark(head.position) : new Vector3(0, 1.6f, 0);
        }

        /// "local", an object path/name, or an explicit point; peers fan out on a ring.
        public static Vector3 ResolveSpot(string near, Vector3? at, int index, out float faceYaw)
        {
            Vector3 center;
            if (at.HasValue && string.IsNullOrEmpty(near)) { faceYaw = 180f; return at.Value; }
            if (string.IsNullOrEmpty(near) || near == "local") center = LocalHeadPark();
            else
            {
                var go = FindObject(near);
                center = go != null ? ParkRoot.ToPark(go.transform.position) : LocalHeadPark();
                center.y = LocalHeadPark().y;
            }
            float ang = (index * 72f + 40f) * Mathf.Deg2Rad;
            Vector3 spot = center + new Vector3(Mathf.Sin(ang), 0, Mathf.Cos(ang)) * 1.6f;
            Vector3 look = center - spot;
            faceYaw = Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg;
            return spot;
        }

        /// A scene object by hierarchy path ("A_Party/Cake Boss") or by name,
        /// active objects first. Paths may start below any root.
        public static GameObject FindObject(string pathOrName)
        {
            if (string.IsNullOrEmpty(pathOrName)) return null;
            var go = GameObject.Find(pathOrName);
            if (go != null) return go;
            string[] parts = pathOrName.Split('/');
            string leaf = parts[parts.Length - 1];
            GameObject best = null;
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t.name != leaf && t.name != leaf + "(Clone)") continue;
                if (!PathEndsWith(t, parts)) continue;
                if (best == null || (t.gameObject.activeInHierarchy && !best.activeInHierarchy)) best = t.gameObject;
            }
            return best;
        }

        static bool PathEndsWith(Transform t, string[] parts)
        {
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                if (t == null) return false;
                string n = t.name.EndsWith("(Clone)") ? t.name.Substring(0, t.name.Length - 7) : t.name;
                if (n != parts[i]) return false;
                t = t.parent;
            }
            return true;
        }

        /// The NetId an object registered — identical on every device by design,
        /// so it is the address a remote player's net_send would use.
        public static bool ResolveNetId(string target, out uint id, out string error)
        {
            id = 0; error = null;
            if (string.IsNullOrEmpty(target)) { error = "no target"; return false; }
            if (uint.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out id)) return true;
            var go = FindObject(target);
            if (go == null) { error = $"no object '{target}' in the scene"; return false; }
            var nid = go.GetComponent<NetId>();
            if (nid == null) { error = $"'{target}' has no NetId (net_send needs one on the same object)"; return false; }
            if (nid.Id == 0) { error = $"'{target}' NetId has not registered yet (it registers in Start)"; return false; }
            id = nid.Id;
            return true;
        }

        // ── Misaligned park frame ────────────────────────────────────
        /// <summary>
        /// Make the Editor's world frame DIFFERENT from the park frame, as it is on
        /// every headset (each Quest's world origin is wherever it booted). At a
        /// desk the park frame is usually identity, so world-space numbers on the
        /// wire look right and a peer "two metres off" never shows up until a
        /// venue. After this, the park root is rotated about the player's head by
        /// `yawDegrees` (and shifted by `offset`): content keeps its place relative
        /// to the park, park-local numbers stay right, and every world-space leak —
        /// a raw transform.position on the wire, a home cached in world space —
        /// lands visibly wrong and fails position checks.
        ///
        /// With no park frame in the scene, one is created ("SimPeers Park Frame",
        /// a ParkRoot) and the scene's content roots move under it (camera rig and
        /// network singletons excluded). NetIds that already registered keep their
        /// ids; do this right after the game has started, as the scenarios do.
        /// </summary>
        /// <summary>
        /// After a park rebuild (dreampark_peers_scenario --reset_park): make sure the
        /// package services run. A Game Manager carries OptimizedAFIgnore, and
        /// OptimizedAF's per-frame distance pass SKIPS ignored LevelObjects — it never
        /// calls Enable() on them. So once one is parked (registered startDisabled
        /// with the rest of the regenerated park, or parked by a Disable that landed
        /// after the loader's ReleaseAllLevelObjects) the only thing that ever wakes it
        /// is the next park release; ordinary content near the player self-heals on
        /// the next physics tick, the manager never does, and its LuaBehaviour never
        /// boots (ArcadeLand: every Lua step failed with "has not booted"). Wakes every
        /// parked ignored LevelObject and every package host's container, then boots
        /// their enabled scripts. Returns a one-line summary.
        /// </summary>
        public static string WakeParkServices()
        {
            if (!ParkBuilder.LevelObjectManager.objectsEnabled || ParkBuilder.LevelObjectManager.ParkContentLocked)
                return $"park still parked (objectsEnabled {ParkBuilder.LevelObjectManager.objectsEnabled}, load lock {ParkBuilder.LevelObjectManager.ParkContentLocked}) — nothing woken";
            var services = new List<GameObject>();
            foreach (var host in UnityEngine.Object.FindObjectsByType<DreamParkPackageHost>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (host.container != null && !services.Contains(host.container)) services.Add(host.container);
            int woken = 0;
            var lom = ParkBuilder.LevelObjectManager.Instance;
            if (lom != null)
                foreach (var lo in lom.levelObjects)
                {
                    if (lo == null || lo.gameObject == null) continue;
                    bool service = lo.ignoreOptimization || services.Any(g => g.transform.IsChildOf(lo.gameObject.transform));
                    if (!service) continue;
                    if (lo.ignoreOptimization && !services.Contains(lo.gameObject)) services.Add(lo.gameObject);
                    if (lo.enabled && lo.forceDisabled != true) continue;
                    lo.forceDisabled = null;
                    lo.Enable(true);
                    woken++;
                }
            int booted = 0;
            foreach (var g in services)
            {
                if (g == null || !g.activeInHierarchy) continue;
                foreach (var lb in g.GetComponentsInChildren<LuaBehaviour>(false))
                {
                    if (!lb.isActiveAndEnabled || ScopeBooted(lb)) continue;
                    lb.EnsureBooted();
                    if (ScopeBooted(lb)) booted++;
                }
            }
            return $"{services.Count} park service(s); woke {woken} parked, booted {booted} script(s)";
        }

        static System.Reflection.FieldInfo _scopeField;
        static bool ScopeBooted(LuaBehaviour lb)
        {
            _scopeField = _scopeField ?? typeof(LuaBehaviour).GetField("scriptScopeTable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            return _scopeField?.GetValue(lb) != null;
        }

        /// <summary>Why an object's scripts are not running: what parks it, one line.</summary>
        public static string DescribeParking(GameObject go)
        {
            if (go == null) return "object not found";
            var sb = new StringBuilder();
            sb.Append($"activeInHierarchy {go.activeInHierarchy}, objectsEnabled {ParkBuilder.LevelObjectManager.objectsEnabled}, load lock {ParkBuilder.LevelObjectManager.ParkContentLocked}");
            var lbs = go.GetComponentsInChildren<LuaBehaviour>(true);
            sb.Append($", {lbs.Length} LuaBehaviour(s): ").Append(string.Join(", ", lbs.Select(l => $"{l.name} enabled {l.enabled} booted {ScopeBooted(l)}")));
            var lom = ParkBuilder.LevelObjectManager.Instance;
            var mine = lom == null ? null : lom.levelObjects.FirstOrDefault(lo => lo != null && lo.gameObject != null && go.transform.IsChildOf(lo.gameObject.transform));
            sb.Append(mine == null ? ", no LevelObject" : $", LevelObject '{mine.gameObject.name}' enabled {mine.enabled} forceDisabled {mine.forceDisabled?.ToString() ?? "-"} ignoreOptimization {mine.ignoreOptimization} priority {mine.isPriority}");
            return sb.ToString();
        }

        public static string Misalign(float yawDegrees, Vector3 offset)
        {
            if (!EditorApplication.isPlaying) return "Not in Play mode.";
            if (Misaligned)
            {
                // Already turned: content that appeared since (a regenerated park) joins the frame
                // at its frame-local pose, so it is turned exactly like what was there before.
                if (_frame != null)
                {
                    int late = SweepRoots(_frame, keepWorld: false);
                    if (late > 0) Log($"{late} new content root(s) moved into the misaligned park frame.");
                }
                return null;
            }
            var root = ParkRoot.Resolve();
            if (root == null)
            {
                var frame = new GameObject("SimPeers Park Frame");
                frame.AddComponent<ParkRoot>();
                _frame = frame;
                int moved = SweepRoots(frame, keepWorld: true);
                root = frame.transform;
                Log($"No park frame in this scene: created 'SimPeers Park Frame' (ParkRoot) over {moved} content root(s).");
            }
            var head = DreamParkLuaAPI.Head();
            Vector3 pivot = head != null ? head.position : (Camera.main != null ? Camera.main.transform.position : Vector3.zero);
            root.RotateAround(new Vector3(pivot.x, root.position.y, pivot.z), Vector3.up, yawDegrees);
            root.position += offset;
            Misaligned = true;
            Log($"Park frame misaligned from the Editor's world: '{root.name}' now at {Fmt(root.position)} yaw {root.eulerAngles.y:0}° (world ≠ park, as on a headset).");
            return null;
        }

        static GameObject _frame;

        static int SweepRoots(GameObject frame, bool keepWorld)
        {
            {
                // Everything that is park content moves under the frame: the scene's roots
                // (the Park Simulator's park, the Player rig, test content). What stays in
                // world space is what is world space on a headset: the XR camera rig, and the
                // SDK's own hidden services (relay client, presence, session).
                var camRoot = Camera.main != null ? Camera.main.transform.root : null;
                int moved = 0;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded) continue;
                    foreach (var r in scene.GetRootGameObjects())
                    {
                        if (r == frame || r.transform == camRoot) continue;
                        if (r.hideFlags != HideFlags.None) continue;
                        if (r.GetComponentInChildren<DreamBoxClient>(true) != null) continue;
                        if (r.GetComponentInChildren<NetSessionArbiter>(true) != null) continue;
                        if (r.GetComponentInChildren<PlayerPresence>(true) != null) continue;
                        if (Camera.main != null && Camera.main.transform.IsChildOf(r.transform)) continue;
                        r.transform.SetParent(frame.transform, keepWorld);
                        moved++;
                    }
                }
                return moved;
            }
        }

        public static bool Misaligned { get; private set; }

        /// The first position a wire message carries, park-local: a typed {"$p":[..]} /
        /// {"$t":[..]} value, else "x","y","z" fields, else a "pos"/"p"/"at" array.
        public static bool TryExtractPosition(string json, out Vector3 pos)
        {
            pos = default;
            var m = System.Text.RegularExpressions.Regex.Match(json, @"""\$[pt]"":\[\s*(-?[\d.eE+-]+)\s*,\s*(-?[\d.eE+-]+)\s*,\s*(-?[\d.eE+-]+)");
            if (!m.Success)
            {
                var mx = System.Text.RegularExpressions.Regex.Match(json, @"""x"":\s*(-?[\d.eE+-]+)");
                var my = System.Text.RegularExpressions.Regex.Match(json, @"""y"":\s*(-?[\d.eE+-]+)");
                var mz = System.Text.RegularExpressions.Regex.Match(json, @"""z"":\s*(-?[\d.eE+-]+)");
                if (mx.Success && my.Success && mz.Success)
                    return Parse3(mx.Groups[1].Value, my.Groups[1].Value, mz.Groups[1].Value, out pos);
                m = System.Text.RegularExpressions.Regex.Match(json, @"""(?:pos|p|at|position)"":\[\s*(-?[\d.eE+-]+)\s*,\s*(-?[\d.eE+-]+)\s*,\s*(-?[\d.eE+-]+)");
                if (!m.Success) return false;
            }
            return Parse3(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, out pos);
        }

        static bool Parse3(string a, string b, string c, out Vector3 v)
        {
            v = default;
            var ic = CultureInfo.InvariantCulture;
            if (!float.TryParse(a, NumberStyles.Float, ic, out float x) || !float.TryParse(b, NumberStyles.Float, ic, out float y)
                || !float.TryParse(c, NumberStyles.Float, ic, out float z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }

        // ── dp.net_body bridge (reflection: NetBodies may not exist in an older SDK) ──
        static Type _netBodies, _netBody;
        static bool ResolveNetBodyTypes()
        {
            if (_netBodies != null) return true;
            _netBodies = Type.GetType("DreamPark.NetBodies, Assembly-CSharp");
            _netBody = Type.GetType("DreamPark.NetBody, Assembly-CSharp");
            return _netBodies != null && _netBody != null;
        }

        /// The networked-physics state of a prop on the Editor: owner, revision, its NetId,
        /// attraction-frame pose, and whether it is following someone. null = not a dp.net_body.
        public sealed class BodyView { public string owner; public int rev; public uint netId; public Vector3 local; public Quaternion localRot; public bool following; public float speed; public Rigidbody rb; }

        public static BodyView NetBodyOf(GameObject go)
        {
            if (go == null || !ResolveNetBodyTypes()) return null;
            object b = _netBodies.GetMethod("Get", new[] { typeof(GameObject) })?.Invoke(null, new object[] { go });
            if (b == null) return null;
            var v = new BodyView
            {
                owner = _netBody.GetProperty("Owner")?.GetValue(b) as string,
                rev = (int)(_netBody.GetProperty("Revision")?.GetValue(b) ?? 0),
                following = (bool)(_netBody.GetProperty("IsFollowing")?.GetValue(b) ?? false),
                rb = _netBody.GetField("Body")?.GetValue(b) as Rigidbody,
            };
            var nid = _netBody.GetProperty("Net")?.GetValue(b) as NetId;
            v.netId = nid != null ? nid.Id : 0;
            var frame = _netBody.GetProperty("Frame")?.GetValue(b) as Transform;
            var t = go.transform;
            v.local = frame != null ? frame.InverseTransformPoint(t.position) : t.position;
            v.localRot = frame != null ? Quaternion.Inverse(frame.rotation) * t.rotation : t.rotation;
            v.speed = v.rb != null && !v.rb.isKinematic ? v.rb.linearVelocity.magnitude : 0f;
            return v;
        }

        // ── Meters ───────────────────────────────────────────────────
        internal static void NoteSimSend(string type)
        {
            _simSends.Add(new KeyValuePair<double, string>(Now, type));
            if (_simSends.Count > 50000) _simSends.RemoveRange(0, 10000);
        }

        /// Peak messages the Editor's device sent in any 1-second window since `since`.
        public static int LocalPeakRate(double since)
        {
            int peak = 0, j = 0;
            var s = _localSends;
            for (int i = 0; i < s.Count; i++)
            {
                if (s[i].Key < since) { j = i + 1; continue; }
                while (j < i && s[i].Key - s[j].Key > 1.0) j++;
                if (j <= i)
                {
                    int baseCount = j > 0 ? s[j - 1].Value : (i > 0 ? s[0].Value : s[i].Value);
                    peak = Math.Max(peak, s[i].Value - baseCount);
                }
            }
            return peak;
        }

        public static int LocalSentSince(double since)
        {
            var s = _localSends;
            if (s.Count == 0) return 0;
            int before = s[0].Value;
            foreach (var kv in s) { if (kv.Key >= since) break; before = kv.Value; }
            return s[s.Count - 1].Value - before;
        }

        /// What the Editor's device sent since `since`, by message kind — read from a
        /// peer's inbox minus what the other simulated peers sent.
        public static Dictionary<string, int> LocalSentByKind(double since)
        {
            var result = new Dictionary<string, int>();
            var observer = Live.FirstOrDefault();
            if (observer == null) return result;
            foreach (var m in observer.Received(null, null, since))
            {
                if (m.type == "pp" && m.presenceId != PlayerPresence.MyId) continue;
                string k = m.type ?? "?";
                result.TryGetValue(k, out int n);
                result[k] = n + 1;
            }
            int others = Live.Count() - 1;
            if (others > 0)
                foreach (var kv in _simSends)
                {
                    if (kv.Key < since || kv.Value == "pp") continue;
                    if (result.TryGetValue(kv.Value, out int n)) result[kv.Value] = Math.Max(0, n - 1);
                }
            return result;
        }

        // ── Pump ─────────────────────────────────────────────────────
        static void Pump()
        {
            if (!EditorApplication.isPlaying)
            {
                if (Active) Stop();
                return;
            }
            double now = Now;
            if (_autoStartAt > 0 && now >= _autoStartAt)
            {
                _autoStartAt = -1;
                for (int i = 0; i < SimPeersSettings.AutoPeers; i++)
                    Add(new SimPeerOptions(), out _);
            }
            if (!Active) return;

            float dt = (float)Math.Min(0.25, Math.Max(0, now - _lastTick));
            _lastTick = now;

            if (_connectAt > 0 && now >= _connectAt && _client != null && Relay != null)
            {
                _connectAt = -1;
                _client.Connect("127.0.0.1", Relay.Port, Key);
                Log("Editor's DreamBoxClient → loopback relay.");
            }

            try { Relay?.Poll(); } catch (Exception e) { Debug.LogWarning(Tag + " relay poll: " + e.Message); }
            foreach (var p in _peers.ToArray())
            {
                try { p.Tick(now, dt); } catch (Exception e) { Debug.LogWarning($"{Tag} {p.Name}: {e.Message}"); }
            }
            try { Relay?.Poll(); } catch { }

            var c = DreamBoxClient.Instance;
            if (c != null)
            {
                if (_localSends.Count == 0 || _localSends[_localSends.Count - 1].Value != c.SentCount)
                    _localSends.Add(new KeyValuePair<double, int>(now, c.SentCount));
                if (_localSends.Count > 100000) _localSends.RemoveRange(0, 20000);
            }

            SimPeerScenario.Tick();
        }

        public static string Fmt(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "({0:0.00}, {1:0.00}, {2:0.00})", v.x, v.y, v.z);

        /// Snapshot for the window / CLI.
        public static object Status()
        {
            var pp = PlayerPresence.Instance;
            var c = DreamBoxClient.Instance;
            return new
            {
                active = Active,
                relayPort = Relay?.Port ?? 0,
                relayPeers = Relay?.PeerCount ?? 0,
                relayDrops = Relay?.RateLimitedMessageCount ?? 0,
                localId = PlayerPresence.MyId,
                localConnected = LocalConnected,
                localSendRate = c != null ? c.SendRate : 0,
                localSeesPeers = pp != null ? pp.PeerCount : 0,
                misaligned = Misaligned,
                leader = SimPeerScenario.LeaderId(),
                peers = _peers.Select(p => new
                {
                    name = p.Name, id = p.Id, device = p.Device, game = p.GameId, leaderCapable = p.LeaderCapable, connected = p.Connected, link = p.LinkUp,
                    gone = p.GoneHow, head = Fmt(p.HeadPos), yaw = Math.Round(p.Yaw), walking = p.Walking,
                    sent = p.SentCount, received = p.ReceivedCount,
                    seesLocal = p.Sees(PlayerPresence.MyId),
                    brain = p.Engine != null ? "platform" : "wire",
                    sessionLeader = p.Engine?.LeaderId,
                    synced = p.Engine?.Synced ?? false,
                    states = p.Replicas.Select(r => $"{r.Name} rev {r.Rev} {(r.Synced ? "synced" : "unsynced")} writer {r.LeaderId}").ToArray(),
                }).ToArray(),
                scenario = SimPeerScenario.Current?.Summary(),
            };
        }
    }

    /// EditorPrefs (per developer, never committed), keyed by project like ParkSimSettings.
    public static class SimPeersSettings
    {
        static string Key(string n) => "DreamPark.SimPeers." + n + "." + Application.dataPath.GetHashCode();

        /// Peers added automatically every time Play starts (0 = off).
        public static int AutoPeers
        {
            get => EditorPrefs.GetInt(Key("AutoPeers"), 0);
            set => EditorPrefs.SetInt(Key("AutoPeers"), Mathf.Clamp(value, 0, 15));
        }
    }
}
