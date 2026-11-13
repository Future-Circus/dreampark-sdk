# Multiplayer rules

The platform does the netcode. The full creator guide, with testing scenarios, is `Assets/DreamPark/Samples/Multiplayer/MULTIPLAYER.md`.
`§ Name` points to a section of these docs; the table at the end of `Assets/DreamPark/Docs/CLAUDE.md` maps every section to its file.

## Multiplayer (Oct 2026: the platform does the netcode)
**Read `Assets/DreamPark/Samples/Multiplayer/MULTIPLAYER.md` before writing netcode.** Transport: one dumb relay per
session (a DreamBox kiosk, or an elected headset running `PeerRelayServer` in-process). It rebroadcasts every message to
every OTHER peer (never the sender), ReliableOrdered, 16 KB cap, and a peer host silently drops anything past 60 msg/s
per peer. Stack: `Assets/DreamPark/Scripts/Features/Net/`. Everything creator-facing is Lua; the only
networking component you add is one `NetId` on an attraction's manager.

The nine rules:
1. **Encounters** (bosses, enemies, rounds, timers): `dp.shared_state(name)` + `dp.is_leader()`. Players send requests
   (`s.request`); the leader applies them.
2. **Nothing gameplay-visible is rolled per device:** `dp.random(key)`, leader choices, `dp.clock()`. Never
   `math.random` for gameplay, never `math.randomseed` (shared VM).
3. **Late join is automatic** with `shared_state`; gate every start on `s.synced()`; effects never replay.
4. **Physics props:** `dp.net_body(go)`. Never write a remote prop's transform. (Every content Rigidbody also starts
   capped at 2 m/s depenetration and 14 rad/s spin; values a game sets explicitly are kept.)
5. **Positions:** typed `net_send(kind, table)` (attraction-relative); people come from `dp.peers()`; never world space.
6. **No `explicitId`** except on a `Player.prefab` bus. Ids are scoped (Arena/Adventure attractions, every Sequence level
   incl. Start/Overlay/Game Over, the Game Manager) and title-unique automatically.
7. **Audio/VO/greetings start in `onzoneenter()`**, never `start()`.
8. **One send budget per headset:** design for `dp.relay().budget` (60, presence included), never `cap`.
9. **Test with simulated peers before a headset**, then one two-headset late-join test:
   `unity command --project-path . --timeout 400 dreampark_peers_scenario --name all --wait true --params '{"target":"<manager path>",…}'`
   (window: DreamPark → Multiplayer → Simulated Peers; params and scenarios in MULTIPLAYER.md § Testing).

Under them: nothing sends at file scope or in `awake()` (`net_send` is injected after the file scope runs; register
`dp.on_connected` / `dp.on_peer_join` in `start()`), and `ondisable()` is not "the player left" (rigs hide in another
title's zone; the optimizer parks distant objects). Numbers on the wire are float32: ids, seeds and absolute times are
strings.

Debugging: `Verbose Net Logs` on `DreamBoxClient`. `[NetRegistry] Event for UNREGISTERED NetId` = the two devices
computed different ids (an older app, or `explicitId` on content); `NO subscribers` = no `onmessage`/`onnet` on that
object here (or it errored at file scope); `NetId COLLISION` = two objects in one app claim one id; `Ignoring peer beacon` =
channel/park/protocol filter. Sessions: `NetSessionArbiter` (next to `DreamBoxClient`) runs DreamBox beacon → kiosk
(always wins) → peer beacon → self-elect host after 3–5 s; host loss re-elects in ~1–3 s and nothing migrates (state
lives in the peers). Beacons carry `parkId` and channel `sdk` (SDK) / `prod` (the DreamPark app) plus a protocol version, so SDK test
sessions never meet production ones and apps with different id schemes never share a deaf session. The relay host is not
the gameplay leader. Hosting needs `CHANGE_WIFI_MULTICAST_STATE` + `WAKE_LOCK` on Android (loopback tests pass without
them, deceptively). iOS is client-only.

## Sessions

- **Sessions.** A solo device that connects to a lone, YOUNGER room keeps its game (clock, seed, shared state, physics bodies) and the room adopts it; an established room still wins. Leader order is synced → headset → seniority → id. `RemoteRig.RegisterTemplate` also re-arms peers that are already built. LAN sessions recover on their own from network changes, a paused or restarted host, and a rebinding discovery listener.
