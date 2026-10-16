# DreamPark Multiplayer — the guide

Read this before you write a line of netcode. Since October 2026 the platform does the hard parts:
who leads, a shared clock, shared seeded randomness, replicated encounter state with late join, physics
props with one owner, attraction-relative positions, and NetIds that agree on every headset, including inside
a Dream Sequence. Your job is to **use those APIs and follow the short rule list**, then **prove it with
simulated peers** before a headset ever sees it.

Everything here is pure Lua. You add no networking components except one `NetId` on the attraction's manager.

---

## 0. The nine rules (each one broke a shipped game)

1. **Encounters have one writer.** Bosses, enemies, rounds, timers, spawns: keep them in `dp.shared_state` and let
   the leader (`dp.is_leader()`, asked every time, never cached) apply changes. Everyone else sends **requests**.
2. **Nothing gameplay-visible is rolled per device.** Targets, attack choice, AI wander, gaze, spawn order come from
   the leader, `dp.random(key)` or `dp.clock()`. "The boss looks at me on my headset and at you on yours" is this
   bug. Never `math.random` for gameplay, never `math.randomseed` (the Lua VM is shared). Cosmetic jitter may stay local.
3. **Late join is automatic with `shared_state`; starts are gated.** A late joiner is unsynced until it hears the
   room (`s.synced()`); it may not start or reset anything. Starting is a request, so two players pressing Start in
   the same frame get one fight. Effects (hits, sparks) are events and never replay to late joiners.
4. **Physics props go through `dp.net_body(...)`.** One owner while it moves; never write a remote prop's transform
   and never simulate a prop you do not own.
5. **No world coordinates on the wire.** Typed `net_send(kind, table)` encodes positions relative to the attraction
   for you; people come from `dp.peers()`; nobody streams player positions.
6. **Don't set `explicitId`** except on a `Player.prefab` bus. Ids are scoped and title-unique automatically.
7. **Audio, VO and greetings start in `onzoneenter()`** (sticky; in a Sequence the active level is the attraction),
   never in `start()`.
8. **One send budget per headset.** Design for `dp.relay().budget` (60 msg/s, presence included), never for `cap`.
9. **Test before a headset does.** The simulated-peer scenarios (§7: late join mid-encounter, simultaneous start,
   leader leaves, two players on one prop, misaligned frames, a phone peer, another title in the park, the budget)
   must pass; then one two-headset late-join test.

Two lifecycle facts sit under all of them (§5): **nothing sends at file scope or in `awake()`** (`net_send` is not
injected until after the file scope runs, and in `awake()` the object's NetId is not final until `Start`), and **`ondisable()` is not "the player left"** (rigs hide in other titles' zones; the optimizer
parks distant objects).

---

## 1. Use these

| You need | Call | Notes |
|---|---|---|
| Where the other players are | `dp.peers()`, `dp.peer(id)`, `dp.on_peer_join/leave(fn, owner)` | Heads and hands as Transforms in THIS headset's world, name tags by default, `p.state` from `dp.set_state{…}`. The SDK streams them (12 Hz, dead-banded); a game never does. Presence is park-wide: players of other titles are in the list too (`p.game`). |
| Who I am | `dp.me()` | Stable per headset; an 8-hex **string** (never a number: §4 float32). |
| Who decides | `dp.leader()`, `dp.is_leader()`, `dp.on_leader_changed(fn(old, new))`, `dp.rank([id])` | Scoped to **your title** (only devices running it count) and identical on every device. While anyone wears your title's `Player.prefab`, only players **wearing it** count: one standing in another title's zone (or between zones, rig hidden) never leads your game and has rank 0. The most senior live, synced headset leads, and **the incumbent keeps the lead until it leaves**, so a newcomer never takes over mid-round; equal seniority breaks on id, the same on every device. Phones lead only a room with no headset. `dp.on_leader_changed` fires once immediately with `(nil, current)`, then on each change. `dp.rank` = 1-based join order (colours, queues). |
| One timeline | `dp.clock()`, `dp.clock_synced()` | Session seconds, beaten by the leader and slewed; once synced it never runs backwards and keeps counting through pauses. `Time.time` is per device: never compare it across headsets. |
| Same randomness everywhere | `dp.random(key)` → [0,1), `dp.random(key, n)` → 1..n, `dp.random(key, a, b)` | Same key → same number on every device **once `dp.clock_synced()` is true** (the session seed arrives with the leader's beat; before that a device rolls on its own seed). `dp.random('wave:'..n..':spawn:'..i, 4)`. Never `math.random` for gameplay, never `math.randomseed` (the VM is shared). |
| Encounter state + late join | `local s = dp.shared_state('boss')` | §2. One writer, revisioned, snapshot to late joiners automatically, plus per-player merge cells. |
| A physics prop | `local body = dp.net_body(go, opts)` | §3. Toucher owns while it moves, leader owns loose motion, followers glide. |
| A prop in the hand | `dp.hold(prop, 'right'\|'left'\|'active'[, hand_node])`, `dp.muzzle(prop)`, `dp.release_hold(prop)` | See **HANDHELD.md**. Remote copies under `RemoteRig` hands mount themselves. |
| A custom message | `net_send(kind, table)` / `function onmessage(kind, p)` | Typed: Vector3 / Quaternion / Transform / Color convert for you, relative to the attraction (and park-local beside it). Needs a `NetId` on the same object. Hand-packed numbers: `dp.to_attraction(v)` / `dp.from_attraction(v)`. |
| What other players see of you | `Player.prefab/RemoteRig` | Children named `Head`, `LeftHand`, `RightHand`, `Hand` follow that player. Scripts inside get `peer_id`. |
| Link state | `dp.relay()`, `dp.on_connected(fn)` | `budget` (design against it), `cap` (diagnostic), `send_rate`, `queued`. |

Inside a script, `dp.leader()`, `dp.on_leader_changed`, `dp.shared_state` handlers and `dp.to_attraction` are bound
to that script (its title, its attraction) and handlers die with it; you never pass `self`.

Where the bus lives: **one `NetId` on the attraction's manager object**, for your own `net_send` messages.
`dp.shared_state` needs none (it addresses the attraction itself) and `dp.net_body` attaches what it needs. Props,
buttons and pads have no NetId; they call the manager (script injection or `dp.scope`). Never set `explicitId` on
attraction content (§4).

---

## 2. Shared encounter state: `dp.shared_state`

The working sample is `mp_encounter_sample.lua.txt` (next to this file); this is its core:

```lua
local s                                               -- the whole encounter

function start()                                      -- never at file scope or in awake()
    s = dp.shared_state('encounter')                  -- bus = this script's attraction
    s.on_request(function(kind, p, from)              -- runs on the WRITER only
        if kind == 'start' and s.get('phase', 'idle') ~= 'fight' then
            s.set('epoch', s.get('epoch', 0) + 1)
            s.set('t0', dp.clock())
            s.set('target', pick_target())            -- the writer picks; everyone sees it
            s.set('phase', 'fight')
        elseif kind == 'hit' and s.get('phase') == 'fight' then
            s.set('hp', math.max(0, s.get('hp', 50) - (p.n or 1)))
        end
    end)
    s.on_change(function(key, value) render(key, value) end)   -- fires for what is already there, then on change
end

function begin()                                      -- a touch, a button
    if not s.synced() then return end                 -- an unsynced late joiner may not start a fight
    s.request('start', {})                            -- idempotent: two at once = one fight
end

function pick_target()
    local ids = { dp.me() }
    local game = dp.game_id(self.gameObject)
    for _, p in ipairs(dp.peers()) do                 -- presence is park-wide: keep this title's players
        if p.game == nil or p.game == '' or p.game == game then ids[#ids + 1] = p.id end
    end
    table.sort(ids)                                   -- the same list on every device
    return ids[dp.random('target:' .. s.get('epoch', 0), #ids)]
end

function update()
    if s == nil or not s.synced() then return end
    pose_boss(s.get('phase'), dp.clock() - s.get('t0', dp.clock()), s.get('target'))
end
```

- **Registers:** `s.get(k[, default])`, `s.set(k, v)` (nil deletes), `s.keys()`, `s.rev()`, `s.synced()`,
  `s.leader()` / `s.is_leader()` (this state's writer), `s.on_change(fn(key, value))`,
  `s.request(kind, payload)` + `s.on_request(fn(kind, payload, from))`.
- **The writer** is chosen among the devices holding the state: headsets before phones, synced before unsynced, then
  **seniority** (the incumbent keeps writing until it leaves), then id as the tie-break — the session leader when it
  holds the state. `s.set` on
  anyone else becomes a request the writer applies; prefer `s.request` + `on_request` when the change needs a rule
  ("only if idle"). On the writer, `s.request` runs its own handler at once.
- **Late join:** a new holder asks for the snapshot (also on every reconnect) and `s.synced()` stays false until one
  of these: the snapshot arrives; every live player of the title has announced itself on this state and none holds
  it (then the first of them starts the room); or 10 s pass with no holder answering. With no relay client at all a
  state syncs alone after 0.25 s, and with a client that never connects after 6 s. When the session **knows** the
  room is empty — this headset hosts and nobody else is on its relay, or it listened a full discovery window and heard
  no host — the session and every state sync **at once** (a lone headset no longer waits 1.5–7 s). A state that
  synced alone and then connects **adopts the room's snapshot** (unless the room is its own empty one). Writes made while unsynced are held: applied if the room was empty,
  **dropped** if it already had state, so a late joiner's start can never reset a fight in progress.
- **Hand-off:** every holder keeps the whole table, so the next writer continues from the current revision.
- **Per-player merge cells** (no writer round trip; every device writes only its own part, everyone merges):
  `s.add(k, n)` / `s.total(k)` (grow-only counters: damage per player, scores), `s.max(k, n)`,
  `s.union(k, items)` / `s.has(k, item)` / `s.items(k)` (grow-only sets: claims, collected), `s.mine(k, v)` /
  `s.of(k, id)` / `s.all(k)` (one value per player). `s.clear(k)` (writer only) starts a new epoch.
- Values: numbers, strings, booleans, Vector3 / rotations (attraction-relative on the wire), small tables. The whole
  table is the late-join snapshot: keep it under a few KB (hard cap 14 KB).

---

## 3. Physics props: `dp.net_body`

```lua
local body
function start()
    body = dp.net_body(self.gameObject, { max_speed = 2.6, max_spin = 9, max_push = 1.2, rate = 10, home = true })
    body.on_owner_changed(function(old, new) tint(new == dp.me()) end)
end
```

(`mp_body_sample.lua.txt`.) A Rigidbody plus that call; no NetId or component to add.

- **One owner while it moves.** Touch it (any collider under your PlayerRig) and it is yours while it moves; a
  settled pose releases the lease. Loose motion nobody owns (knocked by another prop) is claimed by the leader. You
  cannot steal a prop someone else is streaming. Ties resolve the same way on every device (higher revision, then id).
- The owner streams its pose at ≤ `rate` Hz (≤ 10), dead-banded. Bodies, shared states and the session draw from
  **one 24 msg/s SDK pool** per headset (`dp.budget()`); must-arrive messages (claims, settled poses, snapshots) may
  borrow a little, live streams leave headroom for them. Followers glide (`MovePosition`/`MoveRotation`) at a capped speed and teleport only for a gross error
  (> 3 m); they never simulate the prop themselves.
- Speed, spin and push-out are clamped (`max_speed` m/s, `max_spin` rad/s, `max_push` m/s depenetration). Every
  content Rigidbody also starts with process-wide caps (depenetration 2 m/s, angular 14 rad/s); values a game sets
  explicitly are kept.
- Poses are attraction-relative; a late joiner asks once and gets every moved body's pose; the leader re-broadcasts
  after joins. `home = true`: a body that falls 1.5 m below or strays 6 m from where it rested goes back.
  `body.home()` sends it home now, `body.home(pos)` moves home.
- `body.owner()`, `body.is_mine()`, `body.claim()` (takes it and **pins** the lease until `body.release()`, e.g. while
  it sits in a hand), `body.release()`. Idempotent per object.
- Optimizer-safe: nothing is written while the object is parked; a parked owner's stream stops and hands back.
- **Recipes beat streaming** for anything that can be simulated identically everywhere: a thrown ball, a bolt, a knock
  on an idle prop. Send the recipe once (origin, direction, speed, `t = dp.clock()`) and let every headset run it
  (§6 Spawns). Use `net_body` for things people push around.

---

## 4. The bus and the wire (what the platform guarantees)

**A NetId is an address, not an owner.** Every player's copy of the manager has the **same** id, and sending on it
reaches that object's twin on every other headset. *Which player* a message is about rides in the payload, never in
the id. You do not author ids: NetId hashes the object's path below the nearest `NetScope`, and the platform stamps a
`NetScope` on every attraction it spawns — Arena and Adventure attractions, the Dream Sequence root, its Start /
Overlay / Game Over levels, every streamed level, and the Game Manager. Every id also mixes in the title (the content
id of the nearest GameArea / template / PlayerRig), so two titles in one park cannot collide. The session protocol
is version 2: apps with the old id scheme never join a session with new ones.

`explicitId` has exactly one use: a bus on **`Player.prefab`** (a cross-attraction bus), which no scope covers. The
title is mixed into it too. Never set `explicitId` on attraction content.

The facts that shape everything else:

| Fact | Consequence |
|---|---|
| The relay never echoes to the sender | Apply locally first, then send. Your own `onmessage` never fires for your own `net_send`. |
| 60 msg/s per headset, then **silent** drops (the kiosk relay does not limit, a peer host does) | Budget against `dp.relay().budget`, never `cap`. The 60 is split: presence ≤ 12, the SDK pool (session, shared states, bodies) 24, and 24 for your own `net_send` — a promise the SDK keeps by capping itself, **not** a limit it enforces on you. A kiosk session can hand the room to a peer host mid-play. |
| ReliableOrdered, one channel | Send absolute values, not deltas: a lost packet blocks the ones behind it, and a dropped delta is wrong forever. |
| Numbers arrive as **float32** | Integers are exact only below 2^24. Ids, seeds and absolute times are **strings** (`dp.me()` is). `dp.clock()` values and revisions are fine. |
| 16 KB per message | Keep snapshots small. |
| World space is per headset | Typed `net_send` converts for you; anything hand-packed goes through `dp.to_attraction` / `dp.from_attraction` (or `dp.to_park` on a rig bus) — never a raw `transform.position`. |

Typed wire: `net_send('shot', { from = muzzle_pos, dir = dp.dir(muzzle_fwd), t = dp.clock() })` →
`function onmessage(kind, p) … p.from is a Vector3 in THIS headset's world … end`. A Vector3 is a point, `dp.dir(v)` a
direction. Inside an attraction points travel attraction-relative (`$a`) beside park-local (`$p`): receivers in the
attraction use the first, so two headsets whose park alignment differs a little still agree on where things are in
the attraction; older apps read the second. A string body passes through untouched; `onnet(raw)` still gets the raw
envelope `{"type":kind,"payload":{"netId":N,…}}` — read `t.payload.x`, not `t.x`.

---

## 5. Lifecycle under park loading

- File scope: no `net_send` yet (it is injected after the file runs). `awake()`: publish globals only — the object is
  not parented or posed yet, and its NetId is not final until `Start`, so a send would go out on the wrong id.
- `start()` / `onready()`: everything that reads the hierarchy, positions, or sends. Register `dp.on_connected`,
  `dp.on_peer_join`, `s.on_change`, `dp.on_leader_changed` here, with `self.gameObject` as the owner when the call
  takes one, so the handler dies with the object (a streamed Sequence level is destroyed when the level changes).
- `onmessage` can arrive before `start()` (a backlog flushed when you subscribe): guard it (`if s == nil then return end`).
- `ondisable()` is not "the player left". The rig hides when its player walks into another title's zone, and the
  optimizer parks distant objects. Never leave the room or drop a lease in `ondisable()`; the platform's
  leader/state/body logic does not depend on it.

---

## 6. Patterns the platform does not do for you

**Per-player counters outside a shared state.** `s.add` / `s.total` cover most games. If you roll your own: every
player owns one cell and only raises its own, broadcast the cell **absolute**, merge with `max()`, sum for the total;
clearing needs an epoch, also merged with `max()` (`mp_kit.lua.txt` `new_counter()`).

**Events (effects).** Play locally first; key `(uid, seq)` and drop repeats; never replay to late joiners.

**Spawns.** Send the recipe, not the object: origin, direction, speed, `t = dp.clock()`. Every headset runs the
same kinematics and agrees on the path and on what it hits. Advance a fresh projectile by `dp.clock() - p.t`
(clamped) so remote shots are not a round trip behind.

**Derive, don't broadcast.** With one clock, `round = math.floor(dp.clock() / roundLength)` agrees everywhere at
zero messages. Reach for this before optimising anything.

---

## 7. Testing: simulated peers in the Editor

You do not need two headsets to find the bugs above. **DreamPark → Multiplayer → Simulated Peers** (or the
`dreampark_peers_*` CLI) adds simulated players to your Play session:

- They join over the **real relay** (the shipping `PeerRelayServer`, hosted on loopback with no beacon: no echo to the
  sender, 60 msg/s per peer, ReliableOrdered) and stream **real presence**, so `dp.peers()`, name tags and RemoteRig
  clones react to them exactly as to a headset.
- **Each peer runs the platform's own engines** — its own session (beats, sync, leader election, clock, seed) and a
  replica of every `dp.shared_state` your game opened — the same C# a headset runs. So peers agree (or not) on the
  leader, answer a late joiner with the snapshot, and can become the state's writer when you leave. What a peer does
  **not** run is your game's Lua: the Editor runs the one real copy of the game (one Unity process has one NetRegistry,
  one `dp.me()`, one rig). A peer's `s.request` reaches your `on_request`, but a peer that writes the state cannot
  run *your* `on_request` handlers.
- You can move them, walk them, put their hands on things, push a `dp.net_body` prop, make them `s.request` / `s.set`
  through their replica or `net_send` anything to any NetId, drop their Wi-Fi, quit them cleanly or crash them, make
  them a phone or another title's player, and make the Editor itself leave and rejoin as the newest member.
- **The Editor's world is turned away from the park frame** for every scenario (`misalign`, 35° by default), as it is
  on every headset. At a desk the park frame is usually identity, which hides every world-space number on the wire;
  misaligned, those land metres off and fail the position checks.
- The Park Simulator gives a Sequence root the NetScope a device gives it (device parity), so a Sequence that only works
  in the Editor fails in the Editor too.

**Scenarios** (JSON → pass/fail log in `Logs/DreamParkPeers/<time>_<name>/`), six built in:

| Scenario | What it proves |
|---|---|
| `late_join` | B plays, the encounter starts, C joins mid-fight: C sees everyone, C's replica holds the same state (revision + values), positions on the wire are park-correct, the encounter stays healthy, the leader does not change and every peer agrees on it. |
| `simultaneous_start` | You and B start in the same frame (B through its replica's `s.request`): exactly one encounter; B holds the same state. |
| `leader_leaves` | L is the most senior player and leads (the Editor rejoins after it and must resync its state from the room); L crashes: leadership comes back to the Editor, everyone agrees, the encounter carries on from the state it had. |
| `shared_prop` | You and B touch the same `dp.net_body` prop at once: one owner, no fling, settled and consistent. |
| `budget` | Three peers (one a phone) walk around while your game runs: peak ≤ 60 msg/s, zero relay drops, a by-kind breakdown, everyone holds the same state. |
| `other_title` | A player of another title (senior, lower id) and one in another attraction: your game counts only its own players and keeps its own leader. |

Each scenario becomes a test of **your** game through params (`mp_encounter_sample.peers.json` is a complete example):

```sh
unity command --project-path . editor_play
unity command --project-path . --timeout 400 dreampark_peers_scenario --name all --wait true --params '{
  "target": "A_MyGame/Boss", "start": "begin()", "healthy": "healthy()", "one_encounter": "one_encounter()",
  "state": "encounter", "peer_request": "start",
  "body": "A_MyGame/Gift", "body_touch": "claim()",
  "activity": "hit(1)", "roster": "#players()", "roster_count": 2 }'
unity command --project-path . editor_stop
```

`target` is the object path of your manager; Lua params run in that script's scope (`body_touch` in the body's).
`bus` is where your own `net_send` messages travel when that is not the manager (e.g. a `Player.prefab` bus; default
`target`); `warmup` waits before the start for games with their own join gate. A `recv` step on `target`/`bus` also
counts your `dp.shared_state` traffic there (snapshots, merges, writes and requests on that object's state bus), so a
game fully on `dp.shared_state` that never `net_send`s passes it. In a peer's `peer_send` body, `"@me"`
becomes that peer's id. Add `--reset_park true` so the Park Simulator rebuilds the park before each scenario and no game
state carries over (an Arena preview's size auto-cycle is paused for the run). After the rebuild it wakes the Game
Manager (the optimizer never un-parks it on its own) and refuses to run a scenario whose `target` script is not
running, with what parks it, instead of failing every step with "has not booted". An unset param skips its steps and says
so (`skip`), never a silent pass. Write your own scenarios as JSON (the step list
is at the top of `Assets/DreamPark/Editor/ParkSimulator/ParkSimPeerScenario.cs`) and pass a file path or inline JSON as
`--name`.

Interactive: `dreampark_peers_add --name B [--rank low] [--device phone]`, `dreampark_peers_act --step
'{"do":"peer_request","name":"B","state":"encounter","kind":"hit","body":{"n":1}}'`, `dreampark_peers_inbox --name B`,
`dreampark_peers_remove --name B --mode crash`, `dreampark_peers_status` (each peer's leader, sync and replica
revisions). **Peers on Play** in the window adds N peers every time you press Play.

**What simulated peers cannot show** (so do these on hardware once): two copies of your game's *Lua* disagreeing with
each other (for that, a headless Lua harness that runs your scripts N times over a simulated wire, kept in
your content folder's `Tests~/`), real Wi-Fi loss and latency, tracking and park alignment between two real headsets.

**Then one two-headset test:** headset A starts the encounter, headset B joins late and walks to the same spot. Both
must see the same phase, HP and target, and B's name tag must stand on B.

---

## 8. Diagnosing

| Symptom | First thing to check |
|---|---|
| Name tags work, nothing your game sends arrives | Both headsets must log the same `[NetRegistry] Registered NetId N` for your manager. Mismatch = an older app, or `explicitId` on attraction content. |
| `Event for UNREGISTERED NetId N` | The sender's id does not exist here. Same cause. (Traffic for an object this headset just left — a level torn down — is dropped for 5 s instead, so a quick re-entry never replays the last visit's messages.) |
| `Event delivered but NO subscribers` | No script with `onmessage`/`onnet` on that object here, or it errored at file scope. |
| `NetId COLLISION … waits until it is released` | Two objects in one app claim one id (duplicate scene-root names, a duplicated rig). The first keeps it; the newcomer takes over only when the first is destroyed. |
| `attempt to call a nil value (global 'net_send')` | You sent from file scope or `awake()`. Move it to `start()`. |
| Two fights, two targets, two of anything | Something written outside the writer, or rolled with `math.random`. Run `simultaneous_start`. |
| Late joiner sees a fresh encounter | It started one while unsynced. Gate starts on `s.synced()`. |
| Works solo, breaks with 3+ | The budget. `dp.relay().send_rate`; the host logs `[PeerRelay] Rate limit: dropped N`. |
| Positions right in the Editor, metres off on a headset | World coordinates on the wire. Run a scenario (they misalign the Editor's frame) or use typed `net_send`. |

Turn on **Verbose Net Logs** on `DreamBoxClient` for beacons, inbound previews, relay fan-out and NetId registrations.

---

## 9. Sessions and host migration

`NetSessionArbiter` finds or becomes the relay: a DreamBox kiosk always wins; otherwise it joins a peer host; after
3–5 s of silence it hosts itself; a headset hosting with nobody else on its relay knows it is alone, so its session
and shared states sync the moment it connects. Host loss re-elects in 1–3 s. **The relay host is not the leader**: the arbiter
picks who owns the socket, `dp.leader()` picks who drives gameplay, and they change independently. Nothing
migrates with the host: state lives in the peers (`shared_state` snapshots, `max()`-merged cells), which is why
it survives. Channels (`prod` in the app, `sdk` in SDK projects), `parkId` and the protocol version (2 since October
2026) keep sessions apart.

---

## 10. Checklist for a networked attraction

- [ ] One `NetId` on the manager (hashed — no `explicitId`); props have none and call the manager
- [ ] Encounter state in `dp.shared_state`; starts and hits are requests; starts gated on `s.synced()`
- [ ] Every gameplay choice from the leader or `dp.random(key)`; timing from `dp.clock()`
- [ ] Per-player scores as grow-only cells; effects keyed `(uid, seq)` and not replayed
- [ ] Physics props through `dp.net_body`; hand-held props through `dp.hold` (HANDHELD.md)
- [ ] Typed `net_send`; no `transform.position` in a hand-packed message; players from `dp.peers()`
- [ ] Nothing sends at file scope or in `awake()`; handlers registered in `start()` with an owner
- [ ] Music/VO in `onzoneenter()`
- [ ] Under 60 msg/s with presence included (`budget` scenario)
- [ ] All six simulated-peer scenarios pass with your params; then one two-headset late-join test

---

## 11. Upgrading a game with hand-rolled netcode

The October 2026 release changes NetId keys (Sequence levels and the Game Manager get scopes; ids mix in the title)
and bumps the session protocol, so **old and new apps do not join one session**. A title cannot require an app
version: the new APIs exist only in the DreamPark app release that carries them (headsets are told to update when
DreamPark marks that build required). Publish a title that calls `dp.shared_state` / `dp.net_body` / `dp.hold` after
that app release is out, keep a fallback for `nil` APIs if older apps matter to you, and re-run the scenarios and the
two-headset late-join test.

What you can usually delete: your own roster/join/leader/heartbeat code (→ `dp.leader()`), your clock beat
(→ `dp.clock()`), seed broadcasts (→ `dp.random`), encounter/phase snapshot-and-sync code (→ `dp.shared_state`),
position-streaming for pushed props (→ `dp.net_body`), hand-mount maths (→ `dp.hold`), explicit-id rig buses that
only existed to dodge the Sequence id bug (→ the manager's hashed NetId). Keep: grow-only per-player cells, recipes
and deterministic sims, victim-authoritative hits. Migrate leadership all at once — a game that mixes its own
leader with `dp.leader()` has two leaders.

---

## Where to look

| | |
|---|---|
| Simulated peers + scenarios | `Assets/DreamPark/Editor/ParkSimulator/ParkSimPeers*.cs` |
| A networked encounter + a pushable prop | `mp_encounter_sample.lua.txt`, `mp_body_sample.lua.txt`, `mp_encounter_sample.peers.json` |
| The platform services | `Scripts/Features/Net/DreamParkSession.cs`, `SharedState.cs`, `NetBodies.cs`; Lua face `Scripts/Core/DreamParkLuaNet.cs` |
| The transport | `Assets/DreamPark/Scripts/Features/Net/` |
| Hand-held props | `HANDHELD.md` |
| Reusable Lua (counters, budget, dedupe) | `mp_kit.lua.txt` (its room/leader/clock predate `DreamParkSession`: use the platform's) |
| Touch-to-recolour sample | `lua_touch_color_switch.lua.txt` |
| Running a dev relay | `Tools/DreamBoxServer/README.md` |
