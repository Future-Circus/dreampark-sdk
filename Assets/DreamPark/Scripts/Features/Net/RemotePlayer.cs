using System;
using UnityEngine;
using XLua;

namespace DreamPark
{
    /// <summary>
    /// One other player in the park, as this headset sees them. Created and fed
    /// by <see cref="PlayerPresence"/>; never authored.
    ///
    /// Lives under the ParkRoot so its anchors are park-local by construction:
    /// the wire carries park-local poses, they land here as localPosition /
    /// localRotation, and Unity does the per-headset world conversion. If the
    /// park re-syncs, every remote player moves with it — exactly like the
    /// props do.
    ///
    /// Head / LeftHand / RightHand are plain child Transforms, smoothed toward
    /// the last received pose. Content reads them through dp.peers(); the
    /// player's visible shape is a clone of their game's RemoteRig subtree
    /// (RemoteRig.cs), which follows these anchors.
    ///
    /// Runs before default-order scripts so a game's update() reads this
    /// frame's anchors.
    /// </summary>
    [DefaultExecutionOrder(-90)]
    public class RemotePlayer : MonoBehaviour
    {
        public string id;
        public string gameId = "";
        public int seq;
        public float lastSeen;
        public float joinedAt;

        public Transform head;
        public Transform leftHand;
        public Transform rightHand;
        public bool leftTracked;
        public bool rightTracked;
        /// <summary>"l" or "r": the hand the remote player's rig is currently using.</summary>
        public string activeHand = "r";

        public Transform ActiveHandTransform => activeHand == "l" ? leftHand : rightHand;

        /// <summary>Their display name (ProfileAPI on their headset), or "" until it arrives — the name tag falls back to Guest + id.</summary>
        public string displayName = "";

        /// <summary>Their avatar URL (ProfileAPI.AvatarUrl on their headset), or "" until it arrives, or forever if they have none — content decides what "no avatar" looks like.</summary>
        public string avatarUrl = "";

        /// <summary>"phone" / "headset" from their presence stream, or "" until known (older clients never send it).</summary>
        public string device = "";

        /// <summary>The clone of this player's RemoteRig, or null while no local rig exists for their game (or their prefab has no RemoteRig).</summary>
        public GameObject rig;
        string _builtForGame;      // game the last build was for (null = never built)
        float _nextBuildTry;

        /// <summary>Last state bag published by this player (JSON object text).</summary>
        public string stateJson = "{}";
        LuaTable _stateTable;
        string _stateTableJson;
        LuaTable _view;

        float _smoothing = 12f;

        // ── Frame (Oct 8 2026, NetPose) ──────────────────────────────
        /// <summary>Their claim: true = localized, false = not (no pose sent), null = an older client that does not say.</summary>
        public bool? senderLocalized;
        /// <summary>Their shared frame (Saved Location id), "" until known / for older clients.</summary>
        public string frameId = "";
        /// <summary>Their frame epoch: bumps when their frame jumps (relocalization).</summary>
        public int frameEpoch;
        /// <summary>This frame's verdict (NetPoseRules.Agree against this device's frame).</summary>
        public FrameAgreement agreement = FrameAgreement.Legacy;
        /// <summary>
        /// Their pose is a real place in this device's frame. When false the rig
        /// and name tag are hidden and head / hands sit in limbo (10 km below
        /// the park, like LocalPlayerGate's local head), so aim assist,
        /// proximity and AI targeting written against dp.peers() never chase a
        /// ghost. Exposed to Lua as p.synced.
        /// </summary>
        public bool Synced => NetPoseRules.Drawable(agreement) && _hasPose;
        /// <summary>Synced, and past the diorama reveal hold: the rig and name tag may draw.</summary>
        public bool VisualsShown { get; private set; }
        bool _hasPose;   // a plausible head pose arrived (NetPoseRules.Plausible): never draw a limbo head
        float _drawableSince = -1f;

        // NetPoseTrace bookkeeping.
        internal string traceKey;
        internal float traceNextBeat;

        /// <summary>The last received head pose, park-local (what the wire carried).</summary>
        public Vector3 HeadTargetPark => _hp;
        /// <summary>The last received head pose in this device's world.</summary>
        public Vector3 HeadTargetWorld => transform.TransformPoint(_hp);

        public static readonly Vector3 LimboLocal = new Vector3(0f, -10000f, 0f);

        // Targets are park-local (= this object's local space).
        Vector3 _hp, _lp, _rp;
        Quaternion _hq = Quaternion.identity, _lq = Quaternion.identity, _rq = Quaternion.identity;
        bool _snap = true;

        /// <summary>Record their frame claim (before <see cref="Receive"/>). A jump of their frame, or a change of claim, snaps rather than glides.</summary>
        public void SetFrame(bool? localized, string frame, int? epoch)
        {
            if (localized != senderLocalized) { senderLocalized = localized; _snap = true; }
            if (frame != null && frame != frameId) { frameId = frame; _snap = true; }
            if (epoch.HasValue && epoch.Value != frameEpoch) { frameEpoch = epoch.Value; _snap = true; }
        }

        public void Init(string peerId, float smoothing)
        {
            id = peerId;
            _smoothing = smoothing;
            joinedAt = lastSeen = Time.time;
            head = MakeAnchor("Head");
            leftHand = MakeAnchor("LeftHand");
            rightHand = MakeAnchor("RightHand");
            RemoteNameTag.Attach(this);
        }

        Transform MakeAnchor(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            return go.transform;
        }

        /// <summary>Feed a received pose (park-local). Null pose = that hand is not tracked right now.</summary>
        public void Receive(int sequence, string game, Vector3? headPos, Quaternion? headRot,
                            Vector3? leftPos, Quaternion? leftRot, Vector3? rightPos, Quaternion? rightRot,
                            string active, string state, string name = null, string avatar = null)
        {
            // ReliableOrdered cannot reorder, but a peer that restarted resets
            // its counter; accept anything that is not a duplicate of what we
            // already have.
            if (sequence != 0 && sequence == seq) return;
            seq = sequence;
            lastSeen = Time.time;

            if (game != null && game != gameId)
            {
                gameId = game;
                _nextBuildTry = 0f;   // rebuild for the new game on the next frame
            }
            if (headPos.HasValue) { _hp = headPos.Value; _hq = headRot ?? _hq; _hasPose = NetPoseRules.Plausible(_hp); }
            if (leftPos.HasValue)  { _lp = leftPos.Value;  _lq = leftRot ?? _lq;  leftTracked = true; }  else leftTracked = false;
            if (rightPos.HasValue) { _rp = rightPos.Value; _rq = rightRot ?? _rq; rightTracked = true; } else rightTracked = false;
            if (!string.IsNullOrEmpty(active)) activeHand = active;
            if (!string.IsNullOrEmpty(name)) displayName = name;
            if (!string.IsNullOrEmpty(avatar)) avatarUrl = avatar;
            if (state != null && state != stateJson) stateJson = state;
        }

        /// <summary>Record their device. A change rebuilds the RemoteRig (a phone and a headset may look different).</summary>
        public void SetDevice(string value)
        {
            if (string.IsNullOrEmpty(value) || value == device) return;
            device = value;
            if (_builtForGame != null) { _builtForGame = null; _nextBuildTry = 0f; }
        }

        /// <summary>
        /// A RemoteRig template for their game and device was registered or
        /// removed (RemoteRig.RegisterTemplate): build again on the next frame,
        /// even if a build already happened (Oct 11 2026 — the phone guest
        /// that showed on one headset only).
        /// </summary>
        public void TemplateChanged()
        {
            _builtForGame = null;
            _nextBuildTry = 0f;
        }

        /// <summary>The state bag as a Lua table (wire form; dp.peers() unpacks it). Rebuilt only when the JSON changes.</summary>
        public LuaTable StateTable(LuaEnv env)
        {
            if (_stateTable == null || _stateTableJson != stateJson)
            {
                _stateTable?.Dispose();
                _stateTable = null;
                try { _stateTable = LuaBehaviour.JsonParseToLuaTable(string.IsNullOrEmpty(stateJson) ? "{}" : stateJson); }
                catch (Exception) { _stateTable = env.NewTable(); }
                _stateTableJson = stateJson;
            }
            return _stateTable;
        }

        /// <summary>
        /// The table dp.peers() / dp.peer() hand to Lua. One per peer, kept and
        /// refreshed rather than rebuilt, so polling it every frame is cheap.
        /// </summary>
        public LuaTable LuaView(LuaEnv env)
        {
            if (_view == null)
            {
                _view = env.NewTable();
                _view.Set("id",    id);
                _view.Set("head",  head);
                _view.Set("left",  leftHand);
                _view.Set("right", rightHand);
            }
            _view.Set("game",          gameId ?? "");
            _view.Set("name",          displayName ?? "");
            _view.Set("avatar",        avatarUrl ?? "");
            _view.Set("device",        device ?? "");
            _view.Set("hand",          ActiveHandTransform);
            _view.Set("active_hand",   activeHand);
            _view.Set("left_tracked",  leftTracked);
            _view.Set("right_tracked", rightTracked);
            _view.Set("seen",          lastSeen);
            _view.Set("age",           Time.time - joinedAt);
            _view.Set("state",         StateTable(env));
            _view.Set("rig",           rig);
            // false = their pose is not a place here (they or we are not
            // localized, or different locations): head / hands sit in limbo.
            _view.Set("synced",        Synced);
            _view.Set("frame",         frameId ?? "");
            return _view;
        }

        void Update()
        {
            // Stay under the park root, wherever that is now.
            var park = ParkRoot.Resolve();
            if (transform.parent != park) transform.SetParent(park, false);
            if (transform.localPosition != Vector3.zero || transform.localRotation != Quaternion.identity)
            {
                transform.localPosition = Vector3.zero;
                transform.localRotation = Quaternion.identity;
                transform.localScale = Vector3.one;
            }

            // Is their pose a place in THIS frame? (NetPoseRules; FrameNow is
            // sampled once a frame by PlayerPresence, which runs first.)
            PlayerPresence.SampleFrame();
            var mine = PlayerPresence.FrameNow;
            var verdict = NetPoseRules.Agree(senderLocalized, frameId, mine);
            if (verdict != agreement)
            {
                agreement = verdict;
                _snap = true;   // out of limbo (or into it) in one step, never a 10 km glide
                NetPoseTrace.Receiver(this, mine);
            }
            bool drawable = Synced;
            if (!drawable) _drawableSince = -1f;
            else if (_drawableSince < 0f) _drawableSince = Time.time;
            // In this device's diorama a newly drawable peer waits one diorama
            // remote-avatar scan, so only the miniature camera ever draws it.
            VisualsShown = drawable &&
                (!mine.miniature || Time.time - _drawableSince >= NetPoseRules.MiniatureRevealSeconds);

            float k = _snap ? 1f : Mathf.Clamp01(Time.deltaTime * _smoothing);
            _snap = false;
            if (drawable)
            {
                Smooth(head, _hp, _hq, k);
                Smooth(leftHand, _lp, _lq, k);
                Smooth(rightHand, _rp, _rq, k);
            }
            else
            {
                Smooth(head, LimboLocal, _hq, 1f);
                Smooth(leftHand, LimboLocal, _lq, 1f);
                Smooth(rightHand, LimboLocal, _rq, 1f);
                _snap = true;   // the first real pose lands, it does not glide up from limbo
            }
            // The visible shape is a clone of this player's own RemoteRig. It
            // needs a local rig for their game to clone from, which may load
            // after they do — so keep trying, slowly, until a build has
            // happened for the game they are in.
            if (string.IsNullOrEmpty(gameId))
            {
                if (rig != null || _builtForGame != null) DestroyRig();
            }
            else if (_builtForGame != gameId && Time.time >= _nextBuildTry)
            {
                _nextBuildTry = Time.time + 2f;
                RebuildRig();
            }

            // After the build, so a clone made this frame never draws one
            // frame where it should not (Build activates it).
            if (rig != null && rig.activeSelf != VisualsShown) rig.SetActive(VisualsShown);
        }

        static void Smooth(Transform t, Vector3 p, Quaternion q, float k)
        {
            t.localPosition = Vector3.Lerp(t.localPosition, p, k);
            t.localRotation = Quaternion.Slerp(t.localRotation, q, k);
        }

        void RebuildRig()
        {
            DestroyRig();
            // A template registered for THEIR device wins (a host app can load
            // a title's Phone Player so headsets see phone users as the title
            // intends — RemoteRig.RegisterTemplate). Otherwise clone the local
            // rig for their game, as always — unless their device is known and
            // differs from ours: a phone user wearing a headset's blaster rig
            // (or the reverse) is worse than just their name tag.
            Transform registered = RemoteRig.TemplateFor(gameId, device);
            PlayerRig source = null;
            if (registered == null)
            {
                if (PlayerRig.instances != null) PlayerRig.instances.TryGetValue(gameId, out source);
                if (source == null) return;   // not loaded here (yet) — try again later
                if (!string.IsNullOrEmpty(device) && device != DreamParkDevice.PeerWire)
                {
                    _builtForGame = gameId;   // nothing to show for this device: done
                    return;
                }
            }
            try
            {
                rig = registered != null
                    ? RemoteRig.Build(registered, gameId, this)
                    : RemoteRig.Build(source, this);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemotePlayer] RemoteRig build for '{gameId}' failed: {e.Message}");
            }
            _builtForGame = gameId;    // built (possibly to nothing): done for this game
        }

        public void DestroyRig()
        {
            if (rig != null) Destroy(rig);
            rig = null;
            _builtForGame = null;
        }

        void OnDestroy()
        {
            _stateTable?.Dispose();
            _stateTable = null;
            _view?.Dispose();
            _view = null;
        }
    }
}
