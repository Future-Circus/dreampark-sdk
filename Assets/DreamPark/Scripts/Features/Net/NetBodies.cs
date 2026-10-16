using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Defective.JSON;
using UnityEngine;

namespace DreamPark
{
    // ─────────────────────────────────────────────────────────────────────
    //  NetBodies — the engine side of Lua `dp.net_body(go_or_rigidbody[, opts])`.
    //
    //  NOT a component. A creator never adds or configures anything: a Lua call makes a
    //  Rigidbody a shared, correctly-synced physics object. Behind it the engine attaches
    //  only internal helpers at runtime (a NetId for the object's bus, a hidden contact relay)
    //  and ticks everything from one hidden runner (DreamParkBodyRunner).
    //
    //  The rules (MULTIPLAYER.md rule 7, learned the hard way in Birthday Bash):
    //   • ONE OWNER WHILE IT MOVES. Touch it (any collider under the local PlayerRig) and it
    //     is yours: your physics is the truth and you stream its pose. While someone else is
    //     streaming it, it follows them kinematically and your touches do not steal it.
    //     Unowned motion (knocked by another prop) is claimed by the session LEADER, so every
    //     device converges on one copy. Conflicts: higher revision wins, ties → higher uid —
    //     the same comparison everywhere, no arbiter, no round trip.
    //   • ≤ rate Hz (default 10, never more), dead-banded, inside ONE shared budget for every
    //     SDK service on this headset (NetBodies.Budget → DreamParkSession.Budget, 24 msg/s shared
    //     with SharedState and the session; see NetSendBudget for the split of the 60/s
    //     relay design budget), plus a final "settled" pose that releases the lease.
    //   • GENTLE. Owner-side (and any locally simulated body) speed / spin are clamped and the
    //     depenetration push is capped, so a fast hand or two overlapping props can never
    //     launch a 150 g party hat across the room.
    //   • FOLLOWERS GLIDE: Rigidbody.MovePosition / MoveRotation toward the velocity-
    //     extrapolated stream at a capped speed; a teleport only for a gross error (> 3 m).
    //   • ATTRACTION-RELATIVE poses on the wire (the content root every player is aligned to),
    //     so props stay put relative to the attraction even if two park frames disagree.
    //   • LATE JOIN: a new peer asks once ("nb_rq"); the leader (or the owner) answers every
    //     body that has ever moved with a settled snapshot; the leader also re-broadcasts
    //     moved bodies 1.5 s and 6 s after any peer joins. NetRegistry buffers what arrives
    //     before the body exists.
    //   • HOME: a body that falls 1.5 m below or strays 6 m from where it rested is put back.
    //   • OPTIMIZER-SAFE: while LevelObjectManager parks the object (detectCollisions off,
    //     forced kinematic) nothing here writes Rigidbody flags; the follower's forced
    //     kinematic is reconciled on unpark. A body the GAME made kinematic is never made
    //     dynamic by us; its scripted motion is streamed when this device owns it.
    //
    //  Wire (on the object's own NetId — hashed, identical on every device inside a NetScope):
    //    "nb"    {"u":owner,"w":writer,"r":rev,"p":[x,y,z],"q":[x,y,z,w],"v":[x,y,z],"s":0|1,"h":0|1}
    //            p/q/v are local to the attraction frame; s = settled; h = home reset.
    //    "nb_rq" {"w":writer}   — "send me what you have"
    //  rev stays far below 2^24 (json_parse float32), uids are strings.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Per-body tuning (Lua opts: max_speed, max_spin, max_push, rate, home).</summary>
    [Serializable]
    public sealed class NetBodyOptions
    {
        /// <summary>Linear speed cap while simulated here, m/s (0 = none).</summary>
        public float maxSpeed = 2.6f;
        /// <summary>Angular speed cap, rad/s (0 = Unity default).</summary>
        public float maxSpin = 9f;
        /// <summary>Rigidbody.maxDepenetrationVelocity, m/s (0 = leave as is).</summary>
        public float maxPush = 1.2f;
        /// <summary>Owner stream rate, Hz. Clamped to 1..10.</summary>
        public float rate = 10f;
        /// <summary>Return home when it falls out of the room.</summary>
        public bool home = true;
        /// <summary>Drop below home (m) that counts as "fell out".</summary>
        public float homeFall = 1.5f;
        /// <summary>Horizontal distance from home (m) that counts as "left the room" (0 = never).</summary>
        public float homeRadius = 6f;

        public NetBodyOptions Clone() => (NetBodyOptions)MemberwiseClone();
    }

    /// <summary>Send priority inside the shared body budget.</summary>
    public enum NetBodyPriority { Low, Normal, High }

    /// <summary>
    /// The transport + session view a body needs. The default goes to DreamBoxClient and
    /// DreamParkSession; tests plug in simulated peers.
    /// </summary>
    public interface INetBodyLink
    {
        string LocalId { get; }
        bool IsLeader { get; }
        bool Connected { get; }
        int PeerCount { get; }
        /// <summary>Is there room in the headset-wide relay budget (all scripts) for one more message?</summary>
        bool HasHeadroom(NetBodyPriority priority);
        /// <summary>Send on the body's bus. False = not sent (no id yet / no client).</summary>
        bool Send(NetBody body, string type, string json);
    }

    /// <summary>
    /// The shared token bucket every NetBody on one headset draws from. Normal stream traffic
    /// needs a whole token; Low (snapshots, requests) only spends the top half so live streams
    /// keep their headroom; High (claims, settled poses, resets) may borrow a quarter second.
    /// </summary>
    public sealed class NetBodyBudget
    {
        public float perSecond;
        float _tokens;
        double _last = double.NaN;
        public int Sent { get; private set; }

        public NetBodyBudget(float perSecond = 20f) { this.perSecond = perSecond; _tokens = perSecond; }

        // Set → draw from a pool shared with other services instead of this bucket.
        Func<NetBodyPriority, bool> _shared;

        /// <summary>
        /// The runtime budget: every body draws from the headset-wide SDK bucket
        /// (DreamParkSession.Budget) that SharedState and the session use too, so
        /// SDK traffic as a whole stays inside its share of the 60/s relay floor
        /// and content net_send keeps a guaranteed share (NetSendBudget).
        /// </summary>
        public static NetBodyBudget Shared(Func<NetBodyPriority, bool> take)
            => new NetBodyBudget(NetSendBudget.SdkShare) { _shared = take };

        public bool Take(double now, NetBodyPriority priority)
        {
            if (_shared != null)
            {
                if (!_shared(priority)) return false;
                Sent++;
                return true;
            }
            if (double.IsNaN(_last)) _last = now;
            double dt = now - _last;
            if (dt > 0) { _tokens = Mathf.Min(perSecond, _tokens + (float)(dt * perSecond)); _last = now; }
            float floor = priority == NetBodyPriority.High ? -perSecond * 0.25f + 1f
                        : priority == NetBodyPriority.Low ? perSecond * 0.5f
                        : 1f;
            if (_tokens < floor) return false;
            _tokens -= 1f;
            Sent++;
            return true;
        }
    }

    /// <summary>
    /// The default link: DreamBoxClient for the wire, DreamParkSession for identity and the
    /// leader (with a PlayerPresence fallback — lowest uid among synced headsets incl. self —
    /// used only when the session service is absent).
    /// </summary>
    public sealed class DefaultNetBodyLink : INetBodyLink
    {
        public string LocalId => SessionLocalId() ?? PlayerPresence.MyId;

        public bool IsLeader
        {
            get
            {
                var s = SessionIsLeader();
                if (s.HasValue) return s.Value;
                return FallbackLeader() == LocalId;
            }
        }

        public bool Connected
        {
            get
            {
                var c = DreamBoxClient.Instance;
                return c != null && c.ConnectionState == DreamBoxClient.State.Connected;
            }
        }

        public int PeerCount => PlayerPresence.Instance != null ? PlayerPresence.Instance.PeerCount : 0;

        public bool HasHeadroom(NetBodyPriority priority)
        {
            var c = DreamBoxClient.Instance;
            if (c == null) return false;
            int reserve = priority == NetBodyPriority.High ? 2 : priority == NetBodyPriority.Normal ? 12 : 24;
            return c.SendRate < DreamBoxClient.DesignBudget - reserve;
        }

        public bool Send(NetBody body, string type, string json)
        {
            var c = DreamBoxClient.Instance;
            var id = body != null ? body.Net : null;
            if (c == null || id == null || id.Id == 0) return false;
            c.SendToNetId(id.Id, type, json);
            return true;
        }

        // ── DreamParkSession (NET) ──
        // Direct references (NET, Oct 2026): reflection on these static properties
        // could be stripped from an IL2CPP player, silently dropping to the fallback.
        static string SessionLocalId() => DreamParkSession.LocalId;
        static bool? SessionIsLeader() => DreamParkSession.IsLeader;

        /// <summary>Lowest uid among live headset peers incl. self (phones never lead).</summary>
        public static string FallbackLeader()
        {
            string best = DreamParkDevice.IsPhone ? null : PlayerPresence.MyId;
            var pres = PlayerPresence.Instance;
            if (pres != null)
            {
                foreach (var kv in pres.Peers)
                {
                    if (kv.Value == null || kv.Value.device == "phone") continue;
                    if (best == null || string.CompareOrdinal(kv.Key, best) < 0) best = kv.Key;
                }
            }
            return best;
        }
    }

    /// <summary>
    /// One synced physics body (the handle behind <c>dp.net_body</c>). Created by
    /// <see cref="NetBodies.Attach(GameObject, NetBodyOptions)"/>; content never constructs one.
    /// The public constructor and tick methods exist for the engine and its tests.
    /// </summary>
    public sealed class NetBody
    {
        public const string TypePose = "nb";
        public const string TypeRequest = "nb_rq";

        public const float LiveWindow = 0.5f;        // a non-settled pose keeps the stream "live" this long
        public const float KeepaliveSeconds = 0.3f;  // owner re-sends at least this often while it moves / is held
        public const float SettleDelay = 0.15f;      // at rest this long → settled pose, lease released
        public const float SilentOwnerSeconds = 2f;  // a stream that stops without "settled" hands back to physics
        public const float SpawnGrace = 2f;          // no leader claims in the first seconds (load settling)
        public const float MaxFollowSpeed = 6f;      // m/s cap on the follower glide
        public const float MinFollowSpeed = 0.5f;
        public const float TeleportDistance = 3f;
        public const float DeadbandMeters = 0.005f;
        public const float DeadbandDegrees = 1f;

        public readonly GameObject gameObject;
        public readonly Rigidbody Body;
        public NetBodyOptions Options { get; private set; }
        public NetId Net { get; internal set; }
        public INetBodyLink Link;
        public NetBodyBudget Budget;
        public Func<double> Clock = () => Time.timeAsDouble;
        /// <summary>
        /// Oct 11 2026: the session is holding this device's solo timeline while
        /// the room answers (SessionEngine merge). Room poses are ignored until
        /// it settles, so a younger room can never yank the bodies of the game
        /// that seeds it. Swappable for tests.
        /// </summary>
        public static Func<bool> SessionMerging = () => DreamParkSession.Merging;

        /// <summary>Owner uid (the last player to own it), or null if nobody ever did.</summary>
        public string Owner { get; private set; }
        public int Revision { get; private set; }
        public bool IsMine => Owner != null && Link != null && Owner == Link.LocalId;
        /// <summary>This device holds the live lease (it is moving under our physics, or claimed from Lua).</summary>
        public bool HoldsLease => IsMine && (_pinned || Clock() < _leaseUntil);
        public bool IsFollowing => _following;
        /// <summary>Where a follower is gliding to (world), or null when not following. Diagnostics/tests.</summary>
        public Vector3? FollowTargetWorld => _following ? ToWorld(_tp) : (Vector3?)null;
        public bool IsPinned => _pinned;
        public int Sent { get; private set; }

        /// <summary>(oldOwner, newOwner) — fired on every device when ownership changes.</summary>
        public event Action<string, string> OwnerChanged;

        Transform _frame;
        bool _frameResolved;

        double _leaseUntil;
        bool _pinned;

        bool _following;
        Vector3 _tp, _tv;
        Quaternion _tq = Quaternion.identity;
        bool _tSettled;
        double _tAt;
        double _remoteLiveUntil;

        bool _forcedKinematic;
        bool _kinematicBefore;

        double _nextSend, _lastSendAt = -999, _lastMoveAt;
        Vector3 _lastSentP;
        Quaternion _lastSentQ = Quaternion.identity;
        bool _settledSent = true;

        bool _hasHome, _homeExplicit;
        Vector3 _homeP;
        Quaternion _homeQ = Quaternion.identity;
        double _nextHomeSample;

        double _attachedAt;
        bool _requested, _wasConnected;
        internal bool WantSnapshot;

        Vector3 _prevPos;
        Quaternion _prevRot = Quaternion.identity;
        bool _havePrev;

        public NetBody(Rigidbody body, NetBodyOptions options, INetBodyLink link, NetBodyBudget budget, Func<double> clock = null)
        {
            Body = body;
            gameObject = body != null ? body.gameObject : null;
            Link = link;
            Budget = budget;
            if (clock != null) Clock = clock;
            _attachedAt = Clock();
            SetOptions(options ?? new NetBodyOptions());
            CaptureHome(explicitHome: false);
        }

        // ── Public API (dp.net_body handle) ─────────────────────────────

        public void SetOptions(NetBodyOptions options)
        {
            Options = (options ?? new NetBodyOptions()).Clone();
            Options.rate = Mathf.Clamp(Options.rate, 1f, 10f);
            if (Body == null) return;
            if (Options.maxPush > 0f) Body.maxDepenetrationVelocity = Options.maxPush;
            if (Options.maxSpin > 0f) Body.maxAngularVelocity = Options.maxSpin;
            ClampVelocity();   // a body attached mid-flight is capped before its next physics step
        }

        void ClampVelocity()
        {
            if (Body == null || Body.isKinematic) return;
            if (Options.maxSpeed > 0f)
            {
                var v = Body.linearVelocity;
                if (v.sqrMagnitude > Options.maxSpeed * Options.maxSpeed) Body.linearVelocity = v.normalized * Options.maxSpeed;
            }
            if (Options.maxSpin > 0f)
            {
                var w = Body.angularVelocity;
                if (w.sqrMagnitude > Options.maxSpin * Options.maxSpin) Body.angularVelocity = w.normalized * Options.maxSpin;
            }
        }

        /// <summary>
        /// Take it. From Lua (body.claim()) it is pinned — held until <see cref="Release"/> — so a
        /// held, scripted body keeps streaming. Fails while someone else is streaming it.
        /// </summary>
        public bool Claim() => Claim(force: false, pin: true);

        public bool Claim(bool force, bool pin)
        {
            double now = Clock();
            if (!force && _following && now < _remoteLiveUntil) return false;
            string me = Link.LocalId;
            string prev = Owner;
            if (Owner != me || (!_pinned && now >= _leaseUntil)) Revision++;
            Owner = me;
            _leaseUntil = now + 3.0;
            if (pin) _pinned = true;
            if (_following) EndFollow(snap: false);
            if (!Parked() && !Body.isKinematic) Body.WakeUp();
            _settledSent = false;
            _lastMoveAt = now;
            _nextSend = 0;
            SendPose(settled: false, NetBodyPriority.High);   // tell everyone to start following
            if (prev != Owner) RaiseOwnerChanged(prev, Owner);
            return true;
        }

        /// <summary>Let go (body.release()). A body still in flight keeps its lease until it rests.</summary>
        public void Release()
        {
            if (!IsMine) { _pinned = false; return; }
            double now = Clock();
            _pinned = false;
            if (IsMovingNow())
            {
                _leaseUntil = Math.Max(_leaseUntil, now + 1.0);
                return;
            }
            _leaseUntil = now;
            if (SendPose(settled: true, NetBodyPriority.High)) _settledSent = true;
        }

        /// <summary>Where home is (world), or null before it is known.</summary>
        public Vector3? HomePosition => _hasHome ? ToWorld(_homeP) : (Vector3?)null;

        /// <summary>Set home to a world position (and rotation); stops tracking the resting pose.</summary>
        public void SetHome(Vector3 worldPosition, Quaternion? worldRotation = null)
        {
            _homeP = ToFrame(worldPosition);
            _homeQ = ToFrameRot(worldRotation ?? Body.transform.rotation);
            _hasHome = true;
            _homeExplicit = true;
        }

        /// <summary>Put it back home now (claims it, then announces the reset to everyone).</summary>
        public bool ResetHome()
        {
            if (!_hasHome || Body == null) return false;
            Claim(force: true, pin: false);
            Vector3 p = ToWorld(_homeP);
            Quaternion q = ToWorldRot(_homeQ);
            Body.transform.SetPositionAndRotation(p, q);
            if (!Parked())
            {
                Body.position = p;
                Body.rotation = q;
                if (!Body.isKinematic)
                {
                    Body.linearVelocity = Vector3.zero;
                    Body.angularVelocity = Vector3.zero;
                }
            }
            _havePrev = false;
            _leaseUntil = Clock();
            _settledSent = SendPose(settled: true, NetBodyPriority.High, home: true);
            return true;
        }

        // ── Engine ticks ────────────────────────────────────────────────

        /// <summary>Is LevelObjectManager parking it (or is it inactive)? Then we touch nothing.</summary>
        public bool Parked()
        {
            if (Body == null || gameObject == null) return true;
            return !gameObject.activeInHierarchy || !Body.detectCollisions;
        }

        bool _wasParked;

        /// <summary>
        /// Track LevelObjectManager parking. Parked = culled: the optimizer saved the Rigidbody
        /// state and will RESTORE it on unpark, so any write now would be reverted. On park we
        /// release our lease (a culled owner must not hold a body nobody can see it push) and tell
        /// everyone where it settled; on unpark a follower snaps to the latest pose it received.
        /// Returns true while parked.
        /// </summary>
        bool ParkTransition()
        {
            bool parked = Parked();
            if (parked == _wasParked) return parked;
            _wasParked = parked;
            if (Body == null) return parked;
            double now = Clock();
            if (parked)
            {
                if (Owner == Link.LocalId && (_pinned || now < _leaseUntil))
                {
                    _pinned = false;
                    _leaseUntil = now;
                    _settledSent = SendPose(settled: true, NetBodyPriority.High);
                }
            }
            else if (_following)
            {
                if (!_forcedKinematic)
                {
                    _kinematicBefore = Body.isKinematic;
                    Body.isKinematic = true;
                    _forcedKinematic = true;
                }
                Vector3 lp = _tp;
                if (!_tSettled) lp += _tv * (float)Math.Min(now - _tAt, 0.25);
                Vector3 p = ToWorld(lp);
                Quaternion q = ToWorldRot(_tq);
                Body.transform.SetPositionAndRotation(p, q);
                Body.position = p;
                Body.rotation = q;
                if (_tSettled) EndFollow(snap: true);
            }
            return parked;
        }

        bool IsMovingNow()
        {
            if (Body == null) return false;
            if (!Body.isKinematic)
            {
                if (Body.IsSleeping()) return false;
                return Body.linearVelocity.sqrMagnitude > 0.0025f || Body.angularVelocity.sqrMagnitude > 0.01f;
            }
            if (!_havePrev) return false;
            var t = Body.transform;
            return (t.position - _prevPos).sqrMagnitude > 0.002f * 0.002f
                   || Quaternion.Angle(t.rotation, _prevRot) > 0.5f;
        }

        /// <summary>Per frame (Update): ownership, the owner stream, leader claims, home, late join.</summary>
        public void Tick()
        {
            if (Body == null) return;
            double now = Clock();
            bool connected = Link.Connected;
            if (connected && !_wasConnected) _requested = false;
            _wasConnected = connected;

            if (ParkTransition()) { _havePrev = false; return; }
            ResolveFrame();

            bool moving = IsMovingNow();
            var t = Body.transform;
            _prevPos = t.position;
            _prevRot = t.rotation;
            _havePrev = true;

            string me = Link.LocalId;

            // Home = where it rested before anyone moved it (layouts settle after spawn).
            if (Options.home && !_homeExplicit && Revision == 0 && !_following && !moving && now >= _nextHomeSample)
            {
                _nextHomeSample = now + 0.5;
                CaptureHome(explicitHome: false);
            }

            // Late join: ask once per connection, once the room has someone in it
            // (not while the session holds our solo timeline: the merge decides first).
            if (!_requested && connected && Link.PeerCount > 0 && now - _attachedAt > 1.0 && !SessionMerging())
            {
                if (Send(TypeRequest, "{\"w\":\"" + Esc(me) + "\"}", NetBodyPriority.Low)) _requested = true;
            }

            if (_following)
            {
                // The owner stopped streaming mid-push (headset off): back to physics where it is.
                if (!_tSettled && now - _tAt > SilentOwnerSeconds) EndFollow(snap: false);
                if (_following) return;
            }

            bool mine = Owner == me && (_pinned || now < _leaseUntil);

            // Knocked by something that is not a player and nobody owns it: the leader takes it.
            if (!mine && moving && !Body.isKinematic && now - _attachedAt > SpawnGrace && now >= _remoteLiveUntil && Link.IsLeader)
                mine = Claim(force: false, pin: false);

            // Never lose it: fell through the floor / flew out of the room → home.
            if (Options.home && _hasHome && (mine || (Link.IsLeader && now >= _remoteLiveUntil)) && OutOfRoom())
            {
                ResetHome();
                return;
            }

            // Answer a snapshot request / peer join with what we know (never mid-stream).
            if (WantSnapshot && Revision == 0) WantSnapshot = false;
            if (WantSnapshot && !(mine && moving) && SendPose(settled: true, NetBodyPriority.Low)) WantSnapshot = false;

            if (!mine) return;

            if (moving)
            {
                _lastMoveAt = now;
                if (!_pinned) _leaseUntil = Math.Max(_leaseUntil, now + 1.0);
                _settledSent = false;
                if (now >= _nextSend)
                {
                    bool changed = (t.position - _lastSentP).sqrMagnitude > DeadbandMeters * DeadbandMeters
                                   || Quaternion.Angle(t.rotation, _lastSentQ) > DeadbandDegrees
                                   || now - _lastSendAt > KeepaliveSeconds;
                    if (changed && SendPose(settled: false, NetBodyPriority.Normal)) _nextSend = now + 1.0 / Options.rate;
                }
            }
            else if (_pinned)
            {
                // Held but still: a slow keepalive so nobody else's touch steals it.
                if (now - _lastSendAt > KeepaliveSeconds && now >= _nextSend)
                    if (SendPose(settled: false, NetBodyPriority.Normal)) _nextSend = now + 1.0 / Options.rate;
            }
            else if (!_settledSent && now - _lastMoveAt > SettleDelay)
            {
                if (SendPose(settled: true, NetBodyPriority.High))
                {
                    _settledSent = true;
                    _leaseUntil = now;   // released at rest
                }
            }
        }

        /// <summary>Per physics step: follower glide, or caps on a locally simulated body.</summary>
        public void FixedTick(float dt)
        {
            if (Body == null || ParkTransition()) return;
            double now = Clock();

            if (_following)
            {
                if (!_forcedKinematic)
                {
                    _kinematicBefore = Body.isKinematic;
                    if (!Body.isKinematic) { Body.linearVelocity = Vector3.zero; Body.angularVelocity = Vector3.zero; }
                    Body.isKinematic = true;
                    _forcedKinematic = true;
                }
                Vector3 lp = _tp;
                if (!_tSettled) lp += _tv * (float)Math.Min(now - _tAt, 0.25);
                Vector3 target = ToWorld(lp);
                Quaternion tq = ToWorldRot(_tq);
                Vector3 next = GlideStep(Body.position, target, dt, out bool teleport);
                if (teleport) { Body.position = target; Body.transform.position = target; }
                else Body.MovePosition(next);
                Body.MoveRotation(RotateStep(Body.rotation, tq, dt));
                if (_tSettled && (((Body.position - target).sqrMagnitude < 0.005f * 0.005f && Quaternion.Angle(Body.rotation, tq) < 1f) || now - _tAt > 1.5))
                    EndFollow(snap: true);
                return;
            }

            // A follow that ended while parked: give the body back now.
            if (_forcedKinematic) RestoreKinematic(sleep: false);

            ClampVelocity();
        }

        /// <summary>A contact on this body (from the hidden relay). Local-player touches claim it.</summary>
        public void OnContact(Collider other)
        {
            if (other == null || _pinned) return;
            if (!DreamParkLuaAPI.IsPlayer(other)) return;
            double now = Clock();
            if (IsMine && now < _leaseUntil - 2.5) return;   // already ours, lease fresh
            Claim(force: false, pin: false);
        }

        /// <summary>A wire message for this body ({"type":…,"payload":{…}}).</summary>
        public void Receive(string json)
        {
            if (string.IsNullOrEmpty(json) || Link == null) return;
            JSONObject obj;
            try { obj = new JSONObject(json); } catch { return; }
            string type = obj.GetField("type")?.stringValue;
            var p = obj.GetField("payload") ?? obj;
            if (type == TypeRequest)
            {
                if (Revision > 0 && (Link.IsLeader || IsMine)) WantSnapshot = true;
                return;
            }
            if (type != TypePose) return;
            if (SessionMerging()) return;   // our solo game until the merge settles (Oct 11 2026)

            string me = Link.LocalId;
            string w = p.GetField("w")?.stringValue;
            if (w == me) return;
            string u = p.GetField("u")?.stringValue ?? "";
            int r = p.GetField("r") != null ? (int)p.GetField("r").floatValue : 0;
            bool settled = p.GetField("s") != null && p.GetField("s").floatValue > 0.5f;
            bool home = p.GetField("h") != null && p.GetField("h").floatValue > 0.5f;
            if (!ReadVec(p.GetField("p"), out Vector3 pos)) return;
            ReadQuat(p.GetField("q"), out Quaternion rot);
            ReadVec(p.GetField("v"), out Vector3 vel);

            double now = Clock();
            bool newer = r > Revision || (r == Revision && string.CompareOrdinal(u, Owner ?? "") > 0);
            bool same = r == Revision && u == (Owner ?? "");
            if (!newer && !same)
            {
                // The sender is behind (a late joiner's stale view): bring them up to date.
                if (Revision > 0 && (IsMine || Link.IsLeader)) WantSnapshot = true;
                return;
            }
            if (same)
            {
                if (u == me) return;                                   // our own state echoed in a snapshot
                if (w != u && now < _remoteLiveUntil) return;          // a bystander's snapshot during the live stream
            }

            string prev = Owner;
            Revision = r;
            Owner = u;
            _pinned = false;
            _leaseUntil = 0;
            WantSnapshot = false;
            _tp = pos;
            _tq = rot;
            _tv = settled ? Vector3.zero : vel;
            _tSettled = settled;
            _tAt = now;
            _remoteLiveUntil = settled ? 0 : now + LiveWindow;
            BeginFollow();
            if (home && !Parked())
            {
                Vector3 wp = ToWorld(pos);
                Quaternion wq = ToWorldRot(rot);
                Body.transform.SetPositionAndRotation(wp, wq);
                Body.position = wp;
                Body.rotation = wq;
            }
            if (prev != Owner) RaiseOwnerChanged(prev, Owner);
        }

        /// <summary>
        /// This device gave its timeline up in a session merge (Oct 11 2026): its
        /// revisions belong to a game that no longer exists, so they must never
        /// outrank (yank) the seeding side's bodies. Forget them and ask again;
        /// the body stays where it is until the room says otherwise. A body
        /// held from Lua right now, or following a live stream, is left alone.
        /// </summary>
        public void ForgetRevisionForMerge()
        {
            if (_pinned || _following) return;
            Revision = 0;
            _leaseUntil = 0;
            WantSnapshot = false;
            _requested = false;
        }

        // ── Follow ──────────────────────────────────────────────────────

        void BeginFollow()
        {
            _following = true;
            if (Parked() || _forcedKinematic) return;
            _kinematicBefore = Body.isKinematic;
            if (!Body.isKinematic) { Body.linearVelocity = Vector3.zero; Body.angularVelocity = Vector3.zero; }
            Body.isKinematic = true;
            _forcedKinematic = true;
        }

        void EndFollow(bool snap)
        {
            _following = false;
            if (Parked()) return;   // reconciled in FixedTick after unpark
            if (snap)
            {
                Vector3 p = ToWorld(_tp);
                Quaternion q = ToWorldRot(_tq);
                Body.transform.SetPositionAndRotation(p, q);
                Body.position = p;
                Body.rotation = q;
            }
            RestoreKinematic(sleep: snap);
        }

        void RestoreKinematic(bool sleep)
        {
            if (!_forcedKinematic || Parked()) return;
            Body.isKinematic = _kinematicBefore;
            _forcedKinematic = false;
            if (!Body.isKinematic)
            {
                Body.linearVelocity = Vector3.zero;
                Body.angularVelocity = Vector3.zero;
                if (sleep) Body.Sleep();
            }
        }

        // ── Send ────────────────────────────────────────────────────────

        bool Send(string type, string json, NetBodyPriority priority)
        {
            if (!Link.Connected) return true;          // nobody to tell; state machine moves on
            if (!Link.HasHeadroom(priority)) return false;
            if (Budget != null && !Budget.Take(Clock(), priority)) return false;
            if (!Link.Send(this, type, json)) return false;
            Sent++;
            return true;
        }

        bool SendPose(bool settled, NetBodyPriority priority, bool home = false)
        {
            var t = Body.transform;
            Vector3 p = ToFrame(t.position);
            Quaternion q = ToFrameRot(t.rotation);
            Vector3 v = settled || Body.isKinematic ? Vector3.zero : ToFrameVec(Body.linearVelocity);
            var sb = new StringBuilder(160);
            sb.Append("{\"u\":\"").Append(Esc(Owner ?? "")).Append("\",\"w\":\"").Append(Esc(Link.LocalId))
              .Append("\",\"r\":").Append(Revision.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"p\":"); Arr(sb, p.x, p.y, p.z, "F3");
            sb.Append(",\"q\":"); Arr4(sb, q);
            if (!settled) { sb.Append(",\"v\":"); Arr(sb, v.x, v.y, v.z, "F2"); }
            sb.Append(",\"s\":").Append(settled ? '1' : '0');
            if (home) sb.Append(",\"h\":1");
            sb.Append('}');
            if (!Send(TypePose, sb.ToString(), priority)) return false;
            _lastSentP = t.position;
            _lastSentQ = t.rotation;
            _lastSendAt = Clock();
            return true;
        }

        static void Arr(StringBuilder sb, float x, float y, float z, string f)
        {
            sb.Append('[').Append(x.ToString(f, CultureInfo.InvariantCulture)).Append(',')
              .Append(y.ToString(f, CultureInfo.InvariantCulture)).Append(',')
              .Append(z.ToString(f, CultureInfo.InvariantCulture)).Append(']');
        }

        static void Arr4(StringBuilder sb, Quaternion q)
        {
            sb.Append('[').Append(q.x.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
              .Append(q.y.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
              .Append(q.z.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
              .Append(q.w.ToString("F4", CultureInfo.InvariantCulture)).Append(']');
        }

        static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        static bool ReadVec(JSONObject a, out Vector3 v)
        {
            v = Vector3.zero;
            if (a == null || a.type != JSONObject.Type.Array || a.list == null || a.list.Count < 3) return false;
            v = new Vector3(a.list[0].floatValue, a.list[1].floatValue, a.list[2].floatValue);
            return true;
        }

        static void ReadQuat(JSONObject a, out Quaternion q)
        {
            q = Quaternion.identity;
            if (a == null || a.type != JSONObject.Type.Array || a.list == null || a.list.Count < 4) return;
            var r = new Quaternion(a.list[0].floatValue, a.list[1].floatValue, a.list[2].floatValue, a.list[3].floatValue);
            float m = r.x * r.x + r.y * r.y + r.z * r.z + r.w * r.w;
            if (m > 1e-6f) q = Quaternion.Normalize(r);
        }

        // ── Frame (attraction-relative) ─────────────────────────────────

        /// <summary>The frame wire poses are local to (null = world). Resolved lazily, after spawn parenting.</summary>
        public Transform Frame { get { ResolveFrame(); return _frame; } }

        /// <summary>Override the frame (tests, or content with its own shared root).</summary>
        public void SetFrame(Transform frame) { _frame = frame; _frameResolved = true; }

        double _nextFrameTry;

        void ResolveFrame()
        {
            if (_frameResolved && _frame != null) return;
            if (_frameResolved && (!Application.isPlaying || Clock() < _nextFrameTry)) return;
            _frameResolved = true;
            _nextFrameTry = Clock() + 1.0;   // no content root yet (still parenting): look again later
            _frame = FrameFor(Body != null ? Body.transform : null);
        }

        /// <summary>
        /// The attraction root above a body (never the body itself or anything it moves):
        /// GameArea / PropTemplate / LevelTemplate ancestor; a body that IS a prop root uses
        /// its parent; with no content root, the park frame; else world (null).
        /// </summary>
        public static Transform FrameFor(Transform body)
        {
            if (body == null) return null;
            var root = DreamParkLuaAPI.AttractionRoot(body.gameObject);
            Transform t = root != null ? root.transform : null;
            if (t == body) t = body.parent;
            if (t != null) return t;
            return ParkRoot.Resolve();
        }

        Vector3 ToFrame(Vector3 w) => _frame != null ? _frame.InverseTransformPoint(w) : w;
        Vector3 ToWorld(Vector3 l) => _frame != null ? _frame.TransformPoint(l) : l;
        Vector3 ToFrameVec(Vector3 w) => _frame != null ? _frame.InverseTransformVector(w) : w;
        Quaternion ToFrameRot(Quaternion w) => _frame != null ? Quaternion.Inverse(_frame.rotation) * w : w;
        Quaternion ToWorldRot(Quaternion l) => _frame != null ? _frame.rotation * l : l;

        void CaptureHome(bool explicitHome)
        {
            if (Body == null) return;
            ResolveFrame();
            var t = Body.transform;
            _homeP = ToFrame(t.position);
            _homeQ = ToFrameRot(t.rotation);
            _hasHome = true;
            if (explicitHome) _homeExplicit = true;
        }

        bool OutOfRoom()
        {
            Vector3 l = ToFrame(Body.transform.position);
            if (Options.homeFall > 0f && l.y < _homeP.y - Options.homeFall) return true;
            if (Options.homeRadius > 0f)
            {
                float dx = l.x - _homeP.x, dz = l.z - _homeP.z;
                if (dx * dx + dz * dz > Options.homeRadius * Options.homeRadius) return true;
            }
            return false;
        }

        void RaiseOwnerChanged(string prev, string now)
        {
            try { OwnerChanged?.Invoke(prev, now); }
            catch (Exception e) { Debug.LogWarning("[NetBody] on_owner_changed handler threw: " + e.Message); }
        }

        // ── Pure helpers (tested) ───────────────────────────────────────

        /// <summary>
        /// One follower step toward <paramref name="target"/>: speed proportional to the error
        /// (critically-damped feel), clamped to [MinFollowSpeed, MaxFollowSpeed], never
        /// overshooting; a teleport only past <see cref="TeleportDistance"/>.
        /// </summary>
        public static Vector3 GlideStep(Vector3 current, Vector3 target, float dt, out bool teleport)
        {
            Vector3 d = target - current;
            float dist = d.magnitude;
            teleport = dist > TeleportDistance;
            if (teleport) return target;
            if (dist < 1e-5f) return target;
            float speed = Mathf.Clamp(dist * 12f, MinFollowSpeed, MaxFollowSpeed);
            float step = Mathf.Min(dist, speed * dt);
            return current + d * (step / dist);
        }

        /// <summary>Rotation counterpart of <see cref="GlideStep"/> (90–1080 °/s).</summary>
        public static Quaternion RotateStep(Quaternion current, Quaternion target, float dt)
        {
            float angle = Quaternion.Angle(current, target);
            if (angle < 0.01f) return target;
            float rate = Mathf.Clamp(angle * 12f, 90f, 1080f);
            return Quaternion.RotateTowards(current, target, rate * dt);
        }
    }

    /// <summary>
    /// Registry + entry points for <c>dp.net_body</c>. Everything is keyed by GameObject so the
    /// Lua bindings can stay primitive (no custom types across the XLua boundary).
    /// </summary>
    public static class NetBodies
    {
        static readonly Dictionary<int, NetBody> _bodies = new Dictionary<int, NetBody>();
        static readonly List<int> _dead = new List<int>();
        static readonly List<double> _waves = new List<double>();
        static PlayerPresence _presence;
        /// <summary>Session merge losses (Oct 11 2026; swappable for tests).</summary>
        public static Func<int> SessionMergeLosses = () => DreamParkSession.MergeLosses;
        static int _mergeLossSeen;

        /// <summary>Messages per second ALL bodies on this headset may send together.</summary>
        public static readonly NetBodyBudget Budget = NetBodyBudget.Shared(p => DreamParkSession.Budget.TryTake(p));

        /// <summary>The transport/session used for new bodies (tests may swap it).</summary>
        public static INetBodyLink Link = new DefaultNetBodyLink();

        public static int Count => _bodies.Count;

        /// <summary>dp.net_body(go[, opts]). Idempotent per object: a second call returns the same body (options updated when given).</summary>
        public static NetBody Attach(GameObject go, NetBodyOptions options = null)
        {
            if (go == null) return null;
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null)
            {
                Debug.LogWarning($"[NetBody] dp.net_body('{go.name}'): no Rigidbody on the object.");
                return null;
            }
            return Attach(rb, options);
        }

        public static NetBody Attach(Rigidbody rb, NetBodyOptions options = null)
        {
            if (rb == null) return null;
            var go = rb.gameObject;
            int key = go.GetInstanceID();
            if (_bodies.TryGetValue(key, out var existing) && existing != null && existing.Body == rb)
            {
                if (options != null) existing.SetOptions(options);
                return existing;
            }
            var body = new NetBody(rb, options, Link, Budget);
            // Internal helpers, attached by the engine (never authored).
            var net = go.GetComponent<NetId>();
            if (net == null) net = go.AddComponent<NetId>();
            body.Net = net;
            net.OnNetEvent += body.Receive;
            var contacts = go.GetComponent<NetBodyContacts>();
            if (contacts == null) contacts = go.AddComponent<NetBodyContacts>();
            contacts.hideFlags = HideFlags.HideInInspector | HideFlags.DontSave;
            contacts.body = body;
            _bodies[key] = body;
            DreamParkBodyRunner.Ensure();
            return body;
        }

        /// <summary>Lua-friendly overload (opts flattened to primitives).</summary>
        public static NetBody Attach(GameObject go, float maxSpeed, float maxSpin, float maxPush, float rate, bool home)
            => Attach(go, new NetBodyOptions { maxSpeed = maxSpeed, maxSpin = maxSpin, maxPush = maxPush, rate = rate, home = home });

        public static NetBody Get(GameObject go)
            => go != null && _bodies.TryGetValue(go.GetInstanceID(), out var b) ? b : null;

        /// <summary>Stop syncing (the internal NetId stays; the contact relay goes quiet).</summary>
        public static bool Detach(GameObject go)
        {
            var b = Get(go);
            if (b == null) return false;
            if (b.Net != null) b.Net.OnNetEvent -= b.Receive;
            var c = go.GetComponent<NetBodyContacts>();
            if (c != null) c.body = null;
            _bodies.Remove(go.GetInstanceID());
            return true;
        }

        // GameObject-keyed façade for the Lua handle (body.owner() etc.)
        public static string Owner(GameObject go) => Get(go)?.Owner;
        public static bool IsMine(GameObject go) => Get(go)?.IsMine ?? false;
        public static bool Claim(GameObject go) => Get(go)?.Claim() ?? false;
        public static void Release(GameObject go) => Get(go)?.Release();
        public static bool ResetHome(GameObject go) => Get(go)?.ResetHome() ?? false;
        public static void SetHome(GameObject go, Vector3 worldPosition) => Get(go)?.SetHome(worldPosition);

        /// <summary>Update tick (runner): every body's ownership/stream, plus late-join waves.</summary>
        public static void Tick(double now)
        {
            HookPresence();
            bool wave = false;
            for (int i = _waves.Count - 1; i >= 0; i--)
                if (now >= _waves[i]) { wave = true; _waves.RemoveAt(i); }

            // This device adopted another timeline: our revisions are void (Oct 11 2026).
            int losses = SessionMergeLosses();
            bool forget = losses != _mergeLossSeen;
            _mergeLossSeen = losses;

            _dead.Clear();
            foreach (var kv in _bodies)
            {
                var b = kv.Value;
                if (b == null || b.Body == null) { _dead.Add(kv.Key); continue; }
                if (forget) b.ForgetRevisionForMerge();
                if (wave && b.Revision > 0 && b.Link.IsLeader) b.WantSnapshot = true;
                try { b.Tick(); }
                catch (Exception e) { Debug.LogWarning("[NetBody] tick failed on " + b.gameObject.name + ": " + e.Message); }
            }
            for (int i = 0; i < _dead.Count; i++) _bodies.Remove(_dead[i]);
        }

        /// <summary>FixedUpdate tick (runner): follower glide and caps.</summary>
        public static void FixedTick(double now, float dt)
        {
            foreach (var kv in _bodies)
            {
                var b = kv.Value;
                if (b == null || b.Body == null) continue;
                try { b.FixedTick(dt); }
                catch (Exception e) { Debug.LogWarning("[NetBody] fixed tick failed on " + b.gameObject.name + ": " + e.Message); }
            }
        }

        static void HookPresence()
        {
            var p = PlayerPresence.Instance;
            if (p == _presence) return;
            if (_presence != null) _presence.OnPeerJoined -= OnPeerJoined;
            _presence = p;
            if (_presence != null) _presence.OnPeerJoined += OnPeerJoined;
        }

        static void OnPeerJoined(RemotePlayer peer)
        {
            // The joiner may still be loading: re-broadcast moved bodies twice. NetRegistry
            // buffers anything that lands before their copy exists.
            double now = Time.timeAsDouble;
            _waves.Add(now + 1.5);
            _waves.Add(now + 6.0);
        }
    }
}
