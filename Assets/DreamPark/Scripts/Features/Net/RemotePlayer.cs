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

        // Targets are park-local (= this object's local space).
        Vector3 _hp, _lp, _rp;
        Quaternion _hq = Quaternion.identity, _lq = Quaternion.identity, _rq = Quaternion.identity;
        bool _snap = true;

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
            if (headPos.HasValue) { _hp = headPos.Value; _hq = headRot ?? _hq; }
            if (leftPos.HasValue)  { _lp = leftPos.Value;  _lq = leftRot ?? _lq;  leftTracked = true; }  else leftTracked = false;
            if (rightPos.HasValue) { _rp = rightPos.Value; _rq = rightRot ?? _rq; rightTracked = true; } else rightTracked = false;
            if (!string.IsNullOrEmpty(active)) activeHand = active;
            if (!string.IsNullOrEmpty(name)) displayName = name;
            if (!string.IsNullOrEmpty(avatar)) avatarUrl = avatar;
            if (state != null && state != stateJson) stateJson = state;
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
            _view.Set("hand",          ActiveHandTransform);
            _view.Set("active_hand",   activeHand);
            _view.Set("left_tracked",  leftTracked);
            _view.Set("right_tracked", rightTracked);
            _view.Set("seen",          lastSeen);
            _view.Set("age",           Time.time - joinedAt);
            _view.Set("state",         StateTable(env));
            _view.Set("rig",           rig);
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

            float k = _snap ? 1f : Mathf.Clamp01(Time.deltaTime * _smoothing);
            _snap = false;
            Smooth(head, _hp, _hq, k);
            Smooth(leftHand, _lp, _lq, k);
            Smooth(rightHand, _rp, _rq, k);

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
        }

        static void Smooth(Transform t, Vector3 p, Quaternion q, float k)
        {
            t.localPosition = Vector3.Lerp(t.localPosition, p, k);
            t.localRotation = Quaternion.Slerp(t.localRotation, q, k);
        }

        void RebuildRig()
        {
            DestroyRig();
            PlayerRig source = null;
            if (PlayerRig.instances != null) PlayerRig.instances.TryGetValue(gameId, out source);
            if (source == null) return;   // not loaded here (yet) — try again later
            try
            {
                rig = RemoteRig.Build(source, this);
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
