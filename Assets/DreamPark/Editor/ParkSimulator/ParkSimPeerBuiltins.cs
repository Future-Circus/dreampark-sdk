// ─────────────────────────────────────────────────────────────────────
//  ParkSimPeerBuiltins.cs — the standard multiplayer scenarios (six game ones + two live-camera ones)
//
//  Every one of these broke a shipped game. Each is game-agnostic by default
//  (presence, leadership, budget, errors) and becomes a real test of YOUR game
//  through params — the object whose script runs the encounter, the Lua that
//  starts it locally, the message a remote player sends to ask for a start,
//  and a Lua expression that must hold afterwards. Unset params skip their
//  steps (reported as "skip", never as a pass you did not earn).
//
//  Common params
//    target        object path of the networked manager (LuaBehaviour; Lua params run in its scope)
//    warmup        seconds to wait after the peers joined, before starting (games with their own join/sync gate)
//    bus           object path (or NetId) the game's own net_send messages travel on (default: target)
//    script        script name on it, when it has several
//    start         Lua run in target's scope to start the encounter locally   e.g. "wake()"
//    peer_kind     message kind a remote player sends to request the start     e.g. "wake_rq"
//    peer_body     its JSON body (default {})
//    one_encounter Lua expression on target, true when exactly ONE encounter is running   e.g. "S.epoch == 1"
//    healthy       Lua expression on target, true while the game is consistent           e.g. "S.phase ~= 'idle'"
//    snapshot_kind a net_send kind the late joiner must also receive on target (games with their own sync messages)
//    state / peer_request   simultaneous_start: the dp.shared_state name and the request kind a remote player sends
//                  through its replica (s.request) to ask for the start, e.g. "wake"
//    writes_on_bus leader_leaves: true when the game also net_sends on target (then B must hear the new leader)
//    settle        seconds to let state build before the late join (default 3)
//    prop          object path of a shared physics prop (shared_prop)
//    prop_touch    Lua run in prop's scope as the LOCAL player's touch
//    peer_touch_kind / peer_touch_body   what the remote player sends when it touches the prop
//    prop_check    Lua expression on prop, true when it ended consistent (one owner, no fling)
//    body          object path of a dp.net_body prop: B pushes it by push_by while you touch it (the "nb" wire)
//    body_touch    Lua run in the body's script scope as the LOCAL player's touch, e.g. "claim()"
//    activity      Lua run on target every second during the budget run (e.g. "fire()")
//    seconds       budget run length (default 10)
//    ignore        array of error substrings to ignore
//    misalign      degrees the Editor's world is turned away from the park frame first (default 35; 0 = off).
//                  On a headset world ≠ park ALWAYS; at a desk it is usually identity, which hides every
//                  world-space number on the wire. Keep it on.
//    pos_kind / pos_near / pos_radius   late_join: a message kind whose position must be park-local and near
//                  pos_near ("local", a peer, or an object path)
//    roster / roster_count / elsewhere  other_title: Lua expression counting the players the game thinks are
//                  in it; how many that should be; an object path where an extra same-title peer stands
//    camera / camera_local (Nov 13 2026, live camera): a live-camera headset is a non-player — never the leader (it joins
//                  first with the lowest id, and still never leads), never ranked, its shared-state requests and writes
//                  dropped (the game's state is untouched by them; camera_local: nothing it sends reaches B), it still sees
//                  everyone, players draw its camera tag (LIVE on/off) and leave it out of dp.peers(). camera_local makes
//                  the EDITOR the camera (DreamParkDevice.IsCamera) next to a player B, and turns it back at the end.
// ─────────────────────────────────────────────────────────────────────

using System.Collections.Generic;

namespace DreamPark.ParkSim
{
    public static class SimPeerBuiltins
    {
        public static readonly Dictionary<string, string> All = new Dictionary<string, string>
        {
            ["late_join"] = LateJoin,
            ["simultaneous_start"] = SimultaneousStart,
            ["leader_leaves"] = LeaderLeaves,
            ["shared_prop"] = SharedProp,
            ["budget"] = Budget,
            ["other_title"] = OtherTitle,
            ["camera"] = Camera,
            ["camera_local"] = CameraLocal,
        };

        public static readonly string[] Order = { "late_join", "simultaneous_start", "leader_leaves", "shared_prop", "budget", "other_title", "camera", "camera_local" };

        const string Camera = @"{
  ""name"": ""camera"",
  ""description"": ""A live-camera headset C joins first, with the lowest id, then player B. C must never lead or rank (title or session), its shared-state requests and writes must be dropped, it must still see everyone, and the Editor must draw its camera tag with LIVE on and off and leave it out of dp.peers()."",
  ""params"": { ""misalign"": 35, ""target"": """", ""script"": """", ""start"": """", ""healthy"": """", ""state"": """", ""peer_request"": """", ""peer_body"": {}, ""camera_request"": ""hit"", ""camera_body"": { ""n"": 5 }, ""ignore"": [] },
  ""steps"": [
    { ""do"": ""misalign"", ""yaw"": ""${misalign}"", ""if"": ""${misalign}"" },
    { ""do"": ""peer_add"", ""name"": ""C"", ""rank"": ""low"", ""device"": ""camera"", ""leader_capable"": true },
    { ""do"": ""wait"", ""seconds"": 2 },
    { ""do"": ""peer_add"", ""name"": ""B"", ""rank"": ""high"" },
    { ""expect"": ""local_peers"", ""count"": 2, ""within"": 8, ""label"": ""the Editor sees B and the camera"" },
    { ""expect"": ""sees"", ""peer"": ""C"", ""who"": ""local"", ""within"": 5, ""label"": ""the camera sees the Editor"" },
    { ""expect"": ""sees"", ""peer"": ""C"", ""who"": ""B"", ""within"": 5, ""label"": ""the camera sees B"" },
    { ""expect"": ""leader"", ""is"": ""local"", ""target"": ""${target}"", ""script"": ""${script}"", ""within"": 6, ""label"": ""a headset player leads, not the camera (lowest id, first in), and every peer agrees"" },
    { ""expect"": ""leader"", ""is"": ""local"", ""within"": 6, ""label"": ""the session-wide leader is a headset player too"" },
    { ""expect"": ""spectator"", ""who"": ""C"", ""target"": ""${target}"", ""script"": ""${script}"", ""within"": 4, ""label"": ""everyone sees the camera as a spectator: never leader, never ranked"" },
    { ""do"": ""wait"", ""seconds"": ""${warmup}"", ""if"": ""${warmup}"" },
    { ""do"": ""local_lua"", ""target"": ""${target}"", ""script"": ""${script}"", ""code"": ""${start}"", ""if"": ""${start}"" },
    { ""expect"": ""lua"", ""target"": ""${target}"", ""script"": ""${script}"", ""code"": ""${healthy}"", ""within"": 4, ""hold"": 1, ""if"": ""${healthy}"", ""label"": ""a player starts the game with the camera there"" },
    { ""do"": ""wait"", ""seconds"": 1 },
    { ""do"": ""snap_state"", ""state"": ""${state}"", ""as"": ""before"", ""if"": ""${state}"" },
    { ""do"": ""together"", ""steps"": [
        { ""do"": ""peer_request"", ""name"": ""C"", ""state"": ""${state}"", ""kind"": ""${camera_request}"", ""body"": ""${camera_body}"", ""if"": ""${state}"" },
        { ""do"": ""peer_request"", ""name"": ""C"", ""state"": ""${state}"", ""kind"": ""${peer_request}"", ""body"": ""${peer_body}"", ""if"": ""${peer_request}"" },
        { ""do"": ""peer_set"", ""name"": ""C"", ""state"": ""${state}"", ""key"": ""cam_probe"", ""value"": 1, ""if"": ""${state}"" } ] },
    { ""do"": ""wait"", ""seconds"": 2 },
    { ""expect"": ""state_same"", ""as"": ""before"", ""hold"": 1, ""if"": ""${state}"", ""label"": ""the camera's requests and writes were dropped: the game's state is untouched"" },
    { ""expect"": ""state_agrees"", ""peer"": ""C"", ""within"": 4, ""label"": ""the camera still mirrors the game's state (it sees everything)"" },
    { ""expect"": ""camera_tag"", ""who"": ""C"", ""live"": false, ""within"": 4, ""label"": ""the Editor draws C as a camera (not LIVE), not in dp.peers()"" },
    { ""do"": ""peer_camera"", ""name"": ""C"", ""live"": true },
    { ""expect"": ""camera_tag"", ""who"": ""C"", ""live"": true, ""within"": 4, ""label"": ""the camera tag says LIVE once it streams"" },
    { ""do"": ""peer_camera"", ""name"": ""C"", ""live"": false },
    { ""expect"": ""camera_tag"", ""who"": ""C"", ""live"": false, ""within"": 4, ""label"": ""and stops saying LIVE when it stops"" },
    { ""expect"": ""leader"", ""is"": ""local"", ""target"": ""${target}"", ""script"": ""${script}"", ""label"": ""still a player leading at the end"" },
    { ""expect"": ""no_errors"", ""ignore"": ""${ignore}"" }
  ]
}";

        const string CameraLocal = @"{
  ""name"": ""camera_local"",
  ""description"": ""The Editor is the live camera, next to player B. B leads; the Editor is a spectator everywhere; its own start request is dropped (B's copy never starts); a write by B (the writer) still reaches it."",
  ""params"": { ""misalign"": 35, ""target"": """", ""script"": """", ""start"": """", ""activity"": """", ""state"": """", ""ignore"": [] },
  ""steps"": [
    { ""do"": ""misalign"", ""yaw"": ""${misalign}"", ""if"": ""${misalign}"" },
    { ""do"": ""local_camera"", ""on"": true },
    { ""do"": ""peer_add"", ""name"": ""B"", ""rank"": ""high"" },
    { ""expect"": ""local_peers"", ""count"": 1, ""within"": 8 },
    { ""expect"": ""sees"", ""peer"": ""B"", ""who"": ""local"", ""within"": 5, ""label"": ""B still sees the camera (it sends presence)"" },
    { ""expect"": ""leader"", ""is"": ""B"", ""target"": ""${target}"", ""script"": ""${script}"", ""within"": 8, ""label"": ""B leads; the camera never does"" },
    { ""expect"": ""leader"", ""is"": ""B"", ""within"": 8, ""label"": ""B leads the session too"" },
    { ""expect"": ""spectator"", ""who"": ""local"", ""target"": ""${target}"", ""script"": ""${script}"", ""within"": 4, ""label"": ""the camera is a spectator on the Editor and on B"" },
    { ""do"": ""wait"", ""seconds"": ""${warmup}"", ""if"": ""${warmup}"" },
    { ""do"": ""wait"", ""seconds"": 1 },
    { ""do"": ""mark"", ""name"": ""cam"" },
    { ""do"": ""local_lua"", ""target"": ""${target}"", ""script"": ""${script}"", ""code"": ""${start}"", ""if"": ""${start}"" },
    { ""do"": ""local_lua"", ""target"": ""${target}"", ""script"": ""${script}"", ""code"": ""${activity}"", ""if"": ""${activity}"" },
    { ""do"": ""wait"", ""seconds"": 2 },
    { ""expect"": ""no_recv"", ""peer"": ""B"", ""kind"": ""__ss"", ""target"": ""${target}"", ""since"": ""cam"", ""if"": ""${state}"", ""label"": ""the camera's own start / hit requests were dropped at the source (nothing reached B)"" },
    { ""do"": ""peer_set"", ""name"": ""B"", ""state"": ""${state}"", ""key"": ""cam_probe"", ""value"": 7, ""if"": ""${state}"" },
    { ""expect"": ""state_agrees"", ""peer"": ""B"", ""within"": 4, ""if"": ""${state}"", ""label"": ""the camera mirrors B's write (it still receives everything)"" },
    { ""expect"": ""no_errors"", ""ignore"": ""${ignore}"" },
    { ""do"": ""local_camera"", ""on"": false }
  ]
}";

        const string LateJoin = @"{
  ""name"": ""late_join"",
  ""description"": ""B plays with the Editor; the encounter starts; C joins mid-encounter. C must see everyone, receive the game's state on the manager's bus, and the Editor must stay consistent."",
  ""params"": { ""misalign"": 35, ""target"": """", ""script"": """", ""start"": """", ""healthy"": """", ""snapshot_kind"": """", ""settle"": 3, ""snapshot_within"": 3, ""pos_kind"": """", ""pos_near"": ""local"", ""pos_radius"": 1, ""ignore"": [] },
  ""steps"": [
    { ""do"": ""misalign"", ""yaw"": ""${misalign}"", ""if"": ""${misalign}"" },
    { ""do"": ""peer_add"", ""name"": ""B"", ""rank"": ""high"" },
    { ""expect"": ""local_peers"", ""count"": 1, ""within"": 8, ""label"": ""the Editor sees B"" },
    { ""expect"": ""sees"", ""peer"": ""B"", ""who"": ""local"", ""within"": 5, ""label"": ""B sees the Editor"" },
    { ""expect"": ""leader"", ""is"": ""local"", ""target"": ""${target}"", ""script"": ""${script}"", ""within"": 6, ""label"": ""the Editor leads, and every peer agrees"" },
    { ""do"": ""wait"", ""seconds"": ""${warmup}"", ""if"": ""${warmup}"" },
    { ""do"": ""local_lua"", ""target"": ""${target}"", ""script"": ""${script}"", ""code"": ""${start}"", ""if"": ""${start}"" },
    { ""do"": ""wait"", ""seconds"": ""${settle}"" },
    { ""do"": ""mark"", ""name"": ""join"" },
    { ""do"": ""peer_add"", ""name"": ""C"", ""rank"": ""high"" },
    { ""expect"": ""sees"", ""peer"": ""C"", ""who"": ""local"", ""since"": ""join"", ""within"": 5, ""label"": ""late joiner C sees the Editor"" },
    { ""expect"": ""sees"", ""peer"": ""C"", ""who"": ""B"", ""since"": ""join"", ""within"": 5, ""label"": ""late joiner C sees B"" },
    { ""expect"": ""local_peers"", ""count"": 2, ""within"": 8, ""label"": ""the Editor sees B and C"" },
    { ""expect"": ""state_agrees"", ""peer"": ""C"", ""within"": ""${snapshot_within}"", ""label"": ""late joiner C holds the same shared state (rev + values) as the Editor"" },
    { ""expect"": ""recv"", ""peer"": ""C"", ""kind"": ""${snapshot_kind}"", ""target"": ""${bus}"", ""since"": ""join"", ""within"": ""${snapshot_within}"", ""if"": ""${snapshot_kind}"", ""label"": ""late joiner C received the game's own state message on the manager's bus"" },
    { ""expect"": ""recv"", ""peer"": ""C"", ""kind"": ""${pos_kind}"", ""target"": ""${bus}"", ""near"": ""${pos_near}"", ""radius"": ""${pos_radius}"", ""within"": 3, ""if"": ""${pos_kind}"", ""label"": ""positions on the wire are park-local (no world-space leak)"" },
    { ""expect"": ""lua"", ""target"": ""${target}"", ""script"": ""${script}"", ""code"": ""${healthy}"", ""hold"": 1, ""if"": ""${healthy}"", ""label"": ""the encounter is still healthy after the join"" },
    { ""expect"": ""leader"", ""is"": ""local"", ""target"": ""${target}"", ""script"": ""${script}"", ""label"": ""a late joiner did not take the lead"" },
    { ""expect"": ""no_errors"", ""ignore"": ""${ignore}"" }
  ]
}";

        const string SimultaneousStart = @"{
  ""name"": ""simultaneous_start"",
  ""description"": ""The Editor and remote player B start the encounter in the same frame. The room must end up with ONE encounter, not two."",
  ""params"": { ""misalign"": 35, ""target"": """", ""script"": """", ""start"": """", ""peer_kind"": """", ""peer_request"": """", ""state"": """", ""peer_body"": {}, ""one_encounter"": """", ""ignore"": [] },
  ""steps"": [
    { ""do"": ""misalign"", ""yaw"": ""${misalign}"", ""if"": ""${misalign}"" },
    { ""do"": ""peer_add"", ""name"": ""B"", ""rank"": ""high"", ""near"": ""${target}"" },
    { ""expect"": ""local_peers"", ""count"": 1, ""within"": 8 },
    { ""expect"": ""sees"", ""peer"": ""B"", ""who"": ""local"", ""within"": 5 },
    { ""do"": ""wait"", ""seconds"": 1.5 },
    { ""do"": ""wait"", ""seconds"": ""${warmup}"", ""if"": ""${warmup}"" },
    { ""do"": ""mark"", ""name"": ""go"" },
    { ""do"": ""together"", ""steps"": [
        { ""do"": ""local_lua"", ""target"": ""${target}"", ""script"": ""${script}"", ""code"": ""${start}"" },
        { ""do"": ""peer_send"", ""name"": ""B"", ""target"": ""${bus}"", ""kind"": ""${peer_kind}"", ""body"": ""${peer_body}"", ""if"": ""${peer_kind}"" },
        { ""do"": ""peer_request"", ""name"": ""B"", ""state"": ""${state}"", ""kind"": ""${peer_request}"", ""body"": ""${peer_body}"", ""if"": ""${peer_request}"" } ] },
    { ""do"": ""wait"", ""seconds"": 2.5 },
    { ""expect"": ""state_agrees"", ""peer"": ""B"", ""within"": 3, ""label"": ""B holds the same state as the Editor"" },
    { ""expect"": ""lua"", ""target"": ""${target}"", ""script"": ""${script}"", ""code"": ""${one_encounter}"", ""hold"": 2, ""if"": ""${one_encounter}"", ""label"": ""exactly one encounter"" },
    { ""expect"": ""recv"", ""peer"": ""B"", ""kind"": ""*"", ""target"": ""${bus}"", ""since"": ""go"", ""within"": 2, ""if"": ""${target}"", ""label"": ""B was told the result"" },
    { ""expect"": ""no_errors"", ""ignore"": ""${ignore}"" }
  ]
}";

        const string LeaderLeaves = @"{
  ""name"": ""leader_leaves"",
  ""description"": ""L is the room's most senior player (the Editor rejoins after it, as a doffed headset does) and leads; B joins last. L crashes mid-encounter: leadership must come back to the Editor, every peer must agree, and the encounter must carry on from the state it had (not reset, not frozen)."",
  ""params"": { ""misalign"": 35, ""target"": """", ""script"": """", ""start"": """", ""healthy"": """", ""failover"": 10, ""ignore"": [] },
  ""steps"": [
    { ""do"": ""misalign"", ""yaw"": ""${misalign}"", ""if"": ""${misalign}"" },
    { ""do"": ""wait"", ""seconds"": ""${warmup}"", ""if"": ""${warmup}"" },
    { ""do"": ""local_lua"", ""target"": ""${target}"", ""script"": ""${script}"", ""code"": ""${start}"", ""if"": ""${start}"" },
    { ""do"": ""peer_add"", ""name"": ""L"", ""rank"": ""low"" },
    { ""expect"": ""local_peers"", ""count"": 1, ""within"": 8 },
    { ""do"": ""wait"", ""seconds"": 2.5 },
    { ""do"": ""local_rejoin"", ""seconds"": 0.5 },
    { ""do"": ""wait"", ""seconds"": 2 },
    { ""do"": ""peer_add"", ""name"": ""B"", ""rank"": ""high"" },
    { ""expect"": ""local_peers"", ""count"": 2, ""within"": 8 },
    { ""expect"": ""leader"", ""is"": ""L"", ""target"": ""${target}"", ""script"": ""${script}"", ""within"": 8, ""label"": ""the most senior player (L) leads, and everyone agrees"" },
    { ""expect"": ""state_agrees"", ""within"": 4, ""label"": ""the Editor resynced its state from the room after rejoining"" },
    { ""do"": ""wait"", ""seconds"": 1.5 },
    { ""do"": ""mark"", ""name"": ""crash"" },
    { ""do"": ""peer_remove"", ""name"": ""L"", ""mode"": ""crash"" },
    { ""expect"": ""leader"", ""is"": ""local"", ""target"": ""${target}"", ""script"": ""${script}"", ""within"": ""${failover}"", ""label"": ""the Editor takes over after L goes silent, and B agrees"" },
    { ""expect"": ""local_peers"", ""count"": 1, ""within"": ""${failover}"", ""label"": ""L was reaped"" },
    { ""expect"": ""lua"", ""target"": ""${target}"", ""script"": ""${script}"", ""code"": ""${healthy}"", ""hold"": 2, ""if"": ""${healthy}"", ""label"": ""the encounter carried on (not reset, not frozen)"" },
    { ""expect"": ""state_agrees"", ""peer"": ""B"", ""within"": 4, ""label"": ""B and the Editor still hold the same state"" },
    { ""expect"": ""recv"", ""peer"": ""B"", ""kind"": ""*"", ""target"": ""${bus}"", ""since"": ""crash"", ""within"": 6, ""if"": ""${writes_on_bus}"", ""label"": ""the new leader writes to the room"" },
    { ""expect"": ""no_errors"", ""ignore"": ""${ignore}"" }
  ]
}";

        const string SharedProp = @"{
  ""name"": ""shared_prop"",
  ""description"": ""The Editor and B touch the same prop at the same moment. One owner, no fling, and B is told where it ended up."",
  ""params"": { ""misalign"": 35, ""prop"": """", ""prop_script"": """", ""prop_touch"": """", ""body"": """", ""body_touch"": """", ""push_by"": [0.3, 0, 0], ""peer_touch_kind"": """", ""peer_touch_body"": {}, ""prop_check"": """", ""ignore"": [] },
  ""steps"": [
    { ""do"": ""misalign"", ""yaw"": ""${misalign}"", ""if"": ""${misalign}"" },
    { ""do"": ""peer_add"", ""name"": ""B"", ""rank"": ""high"", ""near"": ""${prop}"" },
    { ""expect"": ""local_peers"", ""count"": 1, ""within"": 8 },
    { ""do"": ""wait"", ""seconds"": 1.5 },
    { ""do"": ""peer_hand"", ""name"": ""B"", ""side"": ""right"", ""near"": ""${prop}"", ""if"": ""${prop}"" },
    { ""do"": ""mark"", ""name"": ""touch"" },
    { ""do"": ""together"", ""steps"": [
        { ""do"": ""local_lua"", ""target"": ""${prop}"", ""script"": ""${prop_script}"", ""code"": ""${prop_touch}"" },
        { ""do"": ""peer_send"", ""name"": ""B"", ""target"": ""${prop}"", ""kind"": ""${peer_touch_kind}"", ""body"": ""${peer_touch_body}"", ""if"": ""${peer_touch_kind}"" },
        { ""do"": ""local_lua"", ""target"": ""${body}"", ""code"": ""${body_touch}"", ""if"": ""${body_touch}"" },
        { ""do"": ""peer_push"", ""name"": ""B"", ""target"": ""${body}"", ""by"": ""${push_by}"", ""seconds"": 1, ""if"": ""${body}"" } ] },
    { ""expect"": ""net_body"", ""target"": ""${body}"", ""owner"": ""any"", ""max_speed"": 3, ""within"": 1.5, ""if"": ""${body}"", ""label"": ""while B pushes: one owner, no fling"" },
    { ""do"": ""wait"", ""seconds"": 3 },
    { ""expect"": ""net_body"", ""target"": ""${body}"", ""owner"": ""any"", ""max_speed"": 0.5, ""within"": 3, ""if"": ""${body}"", ""label"": ""after the push: settled, one owner"" },
    { ""do"": ""peer_hand"", ""name"": ""B"", ""side"": ""right"" },
    { ""expect"": ""lua"", ""target"": ""${prop}"", ""script"": ""${prop_script}"", ""code"": ""${prop_check}"", ""hold"": 1, ""if"": ""${prop_check}"", ""label"": ""the prop ended consistent"" },
    { ""expect"": ""recv"", ""peer"": ""B"", ""kind"": ""*"", ""target"": ""${prop}"", ""since"": ""touch"", ""within"": 3, ""if"": ""${prop_touch}"", ""label"": ""B received the prop's state"" },
    { ""expect"": ""no_errors"", ""ignore"": ""${ignore}"" }
  ]
}";

        const string OtherTitle = @"{
  ""name"": ""other_title"",
  ""description"": ""A player of ANOTHER title (lower id, so it would win a park-wide election) and a player standing in another attraction share the park. The game must count only its own players, keep its own leader, and stay healthy."",
  ""params"": { ""misalign"": 35, ""target"": """", ""script"": """", ""start"": """", ""healthy"": """", ""roster"": """", ""roster_count"": 2, ""elsewhere"": """", ""ignore"": [] },
  ""steps"": [
    { ""do"": ""misalign"", ""yaw"": ""${misalign}"", ""if"": ""${misalign}"" },
    { ""do"": ""peer_add"", ""name"": ""B"", ""rank"": ""high"" },
    { ""do"": ""peer_add"", ""name"": ""O"", ""rank"": ""low"", ""game"": ""SimOtherTitle"", ""label"": ""a player of another title"" },
    { ""do"": ""peer_add"", ""name"": ""E"", ""rank"": ""high"", ""near"": ""${elsewhere}"", ""if"": ""${elsewhere}"" },
    { ""do"": ""wait"", ""seconds"": 2 },
    { ""expect"": ""leader"", ""is"": ""local"", ""target"": ""${target}"", ""script"": ""${script}"", ""within"": 4, ""label"": ""another title's player does not lead this game"" },
    { ""do"": ""wait"", ""seconds"": ""${warmup}"", ""if"": ""${warmup}"" },
    { ""do"": ""local_lua"", ""target"": ""${target}"", ""script"": ""${script}"", ""code"": ""${start}"", ""if"": ""${start}"" },
    { ""do"": ""wait"", ""seconds"": 2 },
    { ""expect"": ""lua"", ""target"": ""${target}"", ""script"": ""${script}"", ""code"": ""${roster}"", ""equals"": ""${roster_count}"", ""if"": ""${roster}"", ""label"": ""the game counts only its own players"" },
    { ""expect"": ""lua"", ""target"": ""${target}"", ""script"": ""${script}"", ""code"": ""${healthy}"", ""hold"": 2, ""if"": ""${healthy}"" },
    { ""expect"": ""recv"", ""peer"": ""B"", ""kind"": ""*"", ""target"": ""${bus}"", ""within"": 4, ""if"": ""${start}"", ""label"": ""the game still talks to its own players"" },
    { ""expect"": ""no_errors"", ""ignore"": ""${ignore}"" }
  ]
}";

        const string Budget = @"{
  ""name"": ""budget"",
  ""description"": ""Three peers walk around the Editor while the game runs. The Editor must stay under the 60 msg/s design budget with zero relay drops, and every peer must keep hearing it."",
  ""params"": { ""misalign"": 35, ""target"": """", ""script"": """", ""start"": """", ""activity"": """", ""seconds"": 10, ""max_rate"": 60, ""ignore"": [] },
  ""steps"": [
    { ""do"": ""misalign"", ""yaw"": ""${misalign}"", ""if"": ""${misalign}"" },
    { ""do"": ""peer_add"", ""name"": ""B"" },
    { ""do"": ""peer_add"", ""name"": ""C"" },
    { ""do"": ""peer_add"", ""name"": ""D"", ""device"": ""phone"" },
    { ""expect"": ""local_peers"", ""count"": 3, ""within"": 8 },
    { ""do"": ""wait"", ""seconds"": ""${warmup}"", ""if"": ""${warmup}"" },
    { ""do"": ""local_lua"", ""target"": ""${target}"", ""script"": ""${script}"", ""code"": ""${start}"", ""if"": ""${start}"" },
    { ""do"": ""mark"", ""name"": ""run"" },
    { ""do"": ""peer_walk"", ""name"": ""B"", ""at"": null, ""near"": ""local"", ""speed"": 0.8 },
    { ""do"": ""repeat"", ""times"": ""${seconds}"", ""every"": 1, ""steps"": [
        { ""do"": ""local_lua"", ""target"": ""${target}"", ""script"": ""${script}"", ""code"": ""${activity}"" } ], ""if"": ""${activity}"" },
    { ""do"": ""wait"", ""seconds"": ""${seconds}"" },
    { ""expect"": ""budget"", ""since"": ""run"", ""max_rate"": ""${max_rate}"", ""max_drops"": 0, ""label"": ""Editor under budget, no relay drops"" },
    { ""expect"": ""sees"", ""peer"": ""B"", ""who"": ""local"", ""since"": ""run"" },
    { ""expect"": ""sees"", ""peer"": ""D"", ""who"": ""C"", ""since"": ""run"", ""label"": ""peers hear each other too"" },
    { ""expect"": ""state_agrees"", ""within"": 3, ""label"": ""every peer holds the same shared state"" },
    { ""expect"": ""no_errors"", ""ignore"": ""${ignore}"" }
  ]
}";
    }
}
