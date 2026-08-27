# DreamPark Multiplayer — the guide

The reference for writing networked DreamPark content. Read this before
writing a line of `net_send`. Every claim here is checked against the SDK
source in `Assets/DreamPark/Scripts/Features/Net/`.

The reusable primitives are in **`mp_kit.lua.txt`**, next to this file — copy
it into your park and use what you need.

Attraction names like `A_MP_ButtonMash` refer to the MultiplayerLand test park,
an internal bench where each pattern is a room you can walk into. It does not
ship with the SDK; the patterns below stand on their own, and `mp_kit` is the
part you actually reuse.

---

## 1. What the transport actually is

There is no game server. There is a **dumb relay** — either a DreamBox kiosk
(`Tools/DreamBoxServer`) or another headset that elected itself host
(`PeerRelayServer`) — and it does exactly one thing: rebroadcast every message
it receives to all *other* connected peers, verbatim, ReliableOrdered.

No parsing. No state. No authority. No concept of "a player".

Everything else — identity, ownership, scores, rounds — is something your Lua
agrees on with the other headsets. That is not a limitation to work around;
it is the design. Consensus without a coordinator is cheap if you pick data
structures that converge, and expensive if you pick ones that need ordering.
Most of this document is about that choice.

### The five facts everything follows from

| # | Fact | Where | What it forces |
|---|------|-------|----------------|
| 1 | **The relay never echoes to the sender** | `PeerRelayServer.NetworkReceiveEvent` skips the origin peer | Apply locally first, then broadcast. Your own `onnet` never fires for your own `net_send`. |
| 2 | **One NetId = one broadcast bus, shared by every player's copy** | `NetId.ComputeId`, `NetRegistry.Dispatch` | An address, not an owner. Sending reaches that object's twin on every headset; *which player* rides in the payload. You never author an id — see §3. |
| 3 | **60 messages / peer / second, then silent drops** — enforced by `PeerRelayServer`; the DreamBox kiosk does not rate limit at all | `PeerRelayServer.MaxMessagesPerPeerPerSecond`; `Tools/DreamBoxServer/` | Budget across the *whole headset*, not per script, and always against `dp.relay().budget` (the floor) — never `cap`, which changes with whoever is hosting. |
| 4 | **ReliableOrdered only, one channel** | `DreamBoxClient.Send` | A lost packet head-of-line blocks everything behind it. Prefer absolute values over deltas. |
| 5 | **Numbers arrive as float32** | `LuaBehaviour.JsonObjectToLuaTable` uses `JSONObject.floatValue` | Integers are exact only below 2²⁴ (16,777,216). Identity belongs in a string. |

---

## 2. The wire

`net_send(kind, body)` is injected into a script's Lua scope by `LuaBehaviour`
when the same GameObject has a `NetId` **and** a `DreamBoxClient` exists in
the scene. What goes out is:

```json
{"type":"<kind>","payload":{"netId":<id>, <your body fields>}}
```

`onnet(raw)` receives **the whole envelope**, not just your body:

```lua
function onnet(raw)
    local ok, t = pcall(function() return json_parse(raw) end)
    if not ok or t == nil then return end
    local kind, p = t.type, t.payload      -- NOT t.r, NOT t.x
    if kind == nil or p == nil then return end
    ...
end
```

> **This is the single most common mistake.** Reading `t.r` instead of
> `t.payload.r` gives you `nil` with no error, and the symptom is "the other
> headset receives nothing" — which sends people looking at the network. An
> older version of `PIPELINE.md` documented the wrong shape; it has been
> corrected.

### Wire hygiene

- **Quantize.** `%.2f` is 1 cm — under tracking noise, and half the bytes.
- **One message, all the fields.** Position and orientation together. Two
  messages are two permits against the same 60/sec budget.
- **Absolute, not delta.** A dropped absolute value is repaired by the next
  one. A dropped delta is wrong forever.
- **No unbounded strings.** 16 KB hard cap (`MaxIncomingMessageBytes`);
  anything larger is dropped at the client.
- **Ids are strings.** A random 24-bit integer collides sooner than you think,
  and anything larger loses precision crossing float32 (fact 5).

### Coordinates are park-local, never world

Each Quest has its own XR tracking origin. A park is QR-calibrated into an
arbitrary **park-local** space that is the same on every headset. Attractions
and props live in that space. `dp.head().position`, `self.transform.position`,
and `Camera.main.transform` are **world** — they include that headset's XR
origin. Streaming those numbers is the classic "the room lines up, the other
player is two metres off" bug.

**Send park-local. Apply park-local.** Use the SDK helpers — do not
hand-roll `InverseTransformPoint` against `Player.parent`.

```lua
-- after onready(): dp.park() is ParkAnchor (shared park frame),
-- not player.parent (that is the LevelAnchor)
local pos, fwd = dp.head_park()          -- nil until the head exists
-- or: local pos = dp.to_park(someWorldPos)
-- net_send those x,y,z / fx,fy,fz
```

On receive, parent the remote visual to `dp.park()` and set `localPosition`
(plus a hover offset if you want). Or `dp.from_park(localPos)` if you must
write a world pose on this headset. Same rule for hands, bolts, AI proxies,
nametags. Victim-authoritative hits against *your own* body can stay in
world; anything you *draw for a peer* cannot.

`awake()` is too early — the rig is not parented or stamped yet. Do this in
`onready()`. Editor Play with no park parent: `to_park` / `from_park` are
identity (world), so desk Play still works.

---

## 3. NetId: how objects find each other

**Read this section before anything else.** It is the piece people get wrong,
and getting it wrong makes nothing else make sense.

### A NetId is an address, not an owner

**Every player's copy of the same object has the SAME id. That is the
transport, not a bug.**

The relay has no concept of "a player" — it just rebroadcasts. So the only way
one headset reaches another is that the *same object* on every headset computes
the *same* id. Send on it and it lands on that object's twin in every other
session:

```
        headset A                    headset B                headset C
   GameManager id=1234          GameManager id=1234      GameManager id=1234
        │                              ▲                        ▲
        └── net_send ──▶ relay ────────┴────────────────────────┘
```

If each player's rig had a *different* id, nobody could address anyone — you
would be shouting at an address that exists only on your own machine.

### So where does "which player" live? In the payload.

Every message carries a `u` field. That is the identity; the id is only the
channel it arrived on:

```
{"type":"tr_pose","payload":{"netId":9100201,"u":"0002-7f3a91c4","x":1.2,...}}
                    same on every headset ─┘    └─ who it is about
```

Receivers key their tables by `uid`, never by NetId. That is why the tracker
spawns one cube **per uid** and the score counter holds one cell **per uid**,
while both ride on a single shared address.

### "Collision" means something narrower than it sounds

`NetRegistry` is a per-process dictionary. `[NetRegistry] NetId COLLISION`
fires when **two objects inside one running app** claim the same id — that is
ambiguous routing, and it is load-bearing, do not ignore it. The same id
existing on ten different headsets is not a collision; it is the point.

(The Park Simulator triggers the real kind: it spawns its own copy of every
attraction alongside the ones a test scene already placed.)

### You do not set the id

`NetId.Id` is an FNV-1a hash of the object's path through the prefab, stopping
at the first `NetScope` ancestor. The park spawner stamps a `NetScope` on every
spawned attraction root keyed `{levelId}|{objectIndex}|{resourceName}` — all
park-doc data, identical on every headset — and everything below that boundary
comes from the prefab asset, which is the same file everywhere.

**For an attraction, that is automatic and correct, and you should never touch
it.** None of the ten MultiplayerLand attractions sets an id, and neither does
any of the SDK's shipped sample content. Two instances of the same attraction
in one park get different `objectIndex` values, so they hash apart on their
own.

### `explicitId` is an escape hatch, and it has exactly one real use

Setting `explicitId` skips the hash. Reach for it only when an object sits
**outside any `NetScope`** and you cannot rely on its path being stable — in
practice, a cross-attraction bus living on the **player rig**, which nothing
stamps a scope onto. That is why LaserTag pins its Session object at
`8210001`: every headset runs the same `Player.prefab`, every headset's Session
object must share one address, and there is no scope anchoring it.

If you do use it, it must be unique **within** a park. Core now scopes authored
ids the same way it scopes hashed ones (`ScopeExplicit` mixes the `scopeKey`
when there is a `NetScope` above the object, and returns the id verbatim when
there is not), so an authored id no longer opts out of per-instance
discrimination. Scene-placed props are unchanged byte-for-byte.

### One bus per attraction

Give the **attraction manager** a NetId and route everything through it.
Props (buttons, pads, rings) get no NetId at all; they call a function on the
manager through a Script Injection.

This is not tidiness. Every prop with its own NetId is a separate id that has
to stay aligned across builds, and it makes the wire protocol depend on how
many buttons you happened to build. One bus, N message kinds.

### Late joiners: the replay buffer

`NetRegistry` buffers messages for ids that have not registered yet (256 ids ×
32 messages) and flushes them **when something subscribes**, not when the
NetId registers. That matters because `LuaBehaviour` refuses to boot while
park content is parked, so a script can register its NetId long before it
wires `onnet`.

The flush is conditional on `_registered`, and that condition is why this bites
only where you cannot see it:

- **Plain scene:** the script boots in `Awake`, before `NetId.Start`, so
  nothing flushes at subscription and the backlog arrives later via
  `Register` — after `awake()`. No bug, and none reproducible.
- **In a park:** booting is deferred past `NetId.Start`, so subscribing flushes
  **synchronously**. Before the core fix this delivered to `onnet` before
  `awake()` had run — deterministically, for all shipped content.

Two consequences for content:

- A message that arrives before you exist is not lost.
- Guard `onnet` anyway. The core fix orders subscription after `awake()`, but
  the guard costs one line and covers every other path into an unbuilt script:

```lua
function onnet(raw)
    if room == nil then return end     -- awake() has not run yet
    ...
end
```

---

## 4. Identity, join order, and colour without a server

Full implementation: `mp_kit.lua.txt`, `new_room()`.

```
uid = "%04d-%s"  =  zero-padded join sequence  +  8 random hex
```

On arrival a peer broadcasts `mp_hello`, listens for ~1 second, then takes
`seq = 1 + max(seq of everyone heard)` and announces itself. Because the
sequence is the *prefix*, sorting uids lexicographically sorts them into join
order. Index = position in that sorted list.

Every headset sorts the same set the same way, so every headset computes the
same index for the same player — no assignment, no coordinator, no
renumbering of people who were already playing when a late joiner arrives.

Everything identity-shaped falls out of the index: colour, team, turn order,
spawn point.

**The provisional-uid trap.** During the listen window you need *some* uid,
and it goes out on the wire in your `mp_hello`. If you then adopt your real
uid and abandon the provisional one silently, it sits in everyone else's
roster until the reaper times it out — long enough to shift a real player's
index, which changes their colour mid-game. `new_room` sends an explicit
`mp_bye` for the provisional id. LaserTag learned this one the hard way.

**Leaving.** Send `mp_bye` on `ondisable`, *and* reap on silence. A headset
that crashes or walks out of Wi-Fi never says goodbye.

**Leader** = index 0 = earliest joiner still present. Every client computes it
independently, so it re-elects itself the instant that uid ages out. Use it
for "exactly one peer should do this": clock beats, snapshot replies, driving
a shared counter.

> The app leader is **not** the relay host. `NetSessionArbiter` elects the
> host (who owns the socket); the room elects the leader (who drives
> gameplay). They change independently. `A_MP_HostMigration` shows both.

---

## 5. Pick a data structure that cannot desync

This is the part that matters. Most multiplayer bugs are not network bugs;
they are ordering assumptions that held in testing.

### Grow-only counters (scores) — `A_MP_ButtonMash`

Every peer owns one cell per slot and only ever raises its own. The score is
the sum of all cells. Broadcast **absolute** cell values.

```lua
counter.bump(my_uid, slot, 1)      -- local, instant
send("bm_cell", {u = my_uid, a = counter.mine(my_uid, 1), ...})   -- absolute
-- receiver: counter.set(uid, slot, n)  where set() is max()
```

| Failure | What happens |
|---|---|
| Two players score in the same frame | Different cells. There is no race. |
| A message is dropped | The next broadcast carries the same absolute value. Heals itself. |
| A message arrives twice | `max()` is idempotent. Nothing double-counts. |
| A player leaves | Their cell stays. The score holds. |
| They rejoin | New uid, new cell, keeps adding. |

No peer is authoritative and no ordering is required. Clearing needs an
explicit **epoch**, also merged with `max()`, so a reset converges the same
way the scores do.

### Monotonic value + custody (data that outlives its author) — `A_MP_StateRelay`

The leader advances a number; everyone merges with `max()`; every peer
rebroadcasts what it holds every few seconds. Custody transfers with **zero
messages** — when the old leader ages out, everyone's `leader()` returns the
same new uid and that peer starts driving. The redundant rebroadcast is what
makes the hand-off lossless if the leader dies between beats.

Watch the `seed` in that attraction, not the number: eight random characters
minted once, which the only way a player five hand-offs later can have is that
it was passed along.

### Leases (single writer) — `A_MP_Ownership`

Two clients streaming a position for one object is unfixable downstream. Fix
it upstream: one writer, everyone else follows.

Claims carry a shared-clock timestamp. Every client runs the identical
comparison — *earlier claim wins; within 1 ms, lower uid wins* — so everyone
picks the same winner with no round trip. Claims are optimistic: you take the
object immediately and may lose it inside the grace window, because waiting
for confirmation puts a round trip inside a hand gesture.

**Leases must expire.** The owner heartbeats; silence releases. Without that,
one player taking their headset off locks the object for everyone for the rest
of the session.

### Events (effects) — `A_MP_ParticleBurst`, `A_MP_LaserSpawn`

An effect is a moment, not state.

1. **Play locally first.** The relay does not echo (fact 1).
2. **Key it `(uid, seq)`** and drop repeats. Delivery is reliable, but *your
   own code* re-offers events: snapshot replays, retries, an attraction
   re-entered.
3. **Do not replay history to late joiners.** The correct late-join state for
   an effect is "nothing is happening". Contrast a score, which *is* state.

### Spawns — `A_MP_LaserSpawn`

Send the **recipe**, not the object:

```
origin, direction, speed, colour, sequence, fire time
```

One message per shot regardless of how long the projectile lives — versus ~25
for a 2.5 s bolt streamed at 10 Hz. Every client runs the same kinematics, so
they independently agree on the flight path *and on what it hits*. Hits cost
zero messages.

Include the fire time on the **shared clock** so the receiver can advance the
fresh object by `clock.now() - t` before its first frame. Without it, every
remote projectile is permanently one round trip behind the shooter's. Clamp
the age so a stale message cannot teleport something across the room.

### Derive, don't broadcast — `A_MP_SharedClock`

Once a clock agrees, `round = floor(now / roundLength)` agrees too. There is
no `round_start` event to miss, drop, or double-apply. Rounds cost **zero
messages**; the only traffic is one clock beat per second from the leader, and
adding players does not add round traffic.

This inversion is the biggest message-count saving available to a DreamPark
game. Reach for it before optimising anything else.

**The clock itself:** `Time.time` counts from when *your* app launched, so it
is meaningless across headsets. The leader publishes its already-synced value
(not raw `Time.time`), which is what lets a leader change continue the same
timeline instead of restarting it. Corrections are slewed so a countdown never
visibly jumps; large ones snap, because one visible jump on join beats ten
seconds of a wrong number. Accuracy is one-way, so it carries LAN latency as
bias — single-digit milliseconds. Fine for rounds and spawn compensation.
**Do not** use it to arbitrate who pressed a button first; use a token compare.

---

## 6. The budget

**60 messages per second, per sending peer — on a peer-hosted session.**
Message 61 is dropped by `PeerRelayServer`. Not queued, not rejected, not
logged on the sender. The only symptom is that state quietly stops matching.

**The DreamBox kiosk relay does not rate limit at all** — there was no such
code in `Tools/DreamBoxServer/`, and that turned out to be accretion rather than
a decision (see SDK-CHANGES §7).

So there are two numbers, and only one of them belongs in your head:

| | Meaning |
|---|---|
| `dp.relay().budget` | **what you design against.** Always the floor — 60 — whoever is hosting. |
| `dp.relay().cap` | what *this* host enforces right now. 0 = none advertised. Diagnostic only. |

**Always budget against `budget`.** A kiosk session can lose the kiosk and fall
back to a peer host mid-play (`NetSessionArbiter` has a `Reelection` state), and
content tuned to kiosk headroom falls over at exactly that moment — which is
also the moment everything else is going wrong. `cap` is for readouts and
diagnosis; it moves, and you should not design against something that moves.

Three things are easy to get wrong:

- **It is per headset, not per script.** Four systems each politely sending
  "only 20 a second" is 80, and a third of everything is gone. Draw from one
  budget (`kit.new_budget`).
- **It is not the real ceiling.** The relay fans every message to every other
  peer, so air traffic is O(N²). Eight headsets at 30/s each is 240 sends and
  1,680 deliveries per second over one Wi-Fi radio. The relay will allow it.
  The radio will not enjoy it.
- **The sender cannot tell you it happened.** The client's meter and the
  relay's window are different windows, so an over-cap warning means "you are
  sending at a rate that may be truncated", not "you were truncated". The host
  logs the real drops: `[PeerRelay] Rate limit: dropped N message(s)`.

`A_MP_Bandwidth` lets you walk the rate up and watch the cliff, with per-sender
loss measured from sequence gaps.

### A budget that works

| Stream | Rate | Technique |
|---|---:|---|
| Head / hand pose | 10 Hz | dead-band + quantize + coalesce |
| Held-object pose | 12 Hz | only the lease owner sends |
| Score / counters | ≤10 Hz | coalesced absolute values |
| Clock beat | 1 Hz | leader only |
| Roster heartbeat | 0.5 Hz | |
| Effects / spawns | on demand | budgeted, keyed |

**Dead-banding is the rule people skip and it is the one that decides whether
eight players fit.** A room of people standing still costs one keyframe per
second each, not ten messages per second each.

---

## 7. Diagnosing it

| Symptom | First thing to check |
|---|---|
| Nothing arrives, both sides look fine | `NetId` mismatch. Compare `[NetRegistry] Registered NetId N` lines side by side. Use `explicitId`. |
| `Event for UNREGISTERED NetId N — buffering` | The sender's id does not exist on this client. Same cause. |
| `Event delivered but NO subscribers` | No `onnet` (or `TestNetObject`) on that object on this client. |
| `NetId COLLISION` | Duplicate `explicitId`, or duplicate scene-root names. |
| Works solo, breaks with 3+ | The budget. Turn on `Verbose Net Logs`, watch `SendRate`. |
| `Sending N msg/s — over the relay's 60/s cap` | You are being silently truncated. Coalesce. |
| `[PeerRelay] Rate limit: dropped N message(s)` | Same, seen from the host. |
| State drifts and never recovers | A delta or a non-idempotent handler. See `A_MP_Ordering`. |
| Desync only after someone leaves | Something keyed on roster position without re-deriving it. |
| Nothing networks at all, no errors | No `DreamBoxClient` in the scene — `net_send` is a warning stub. |

**Turn on `Verbose Net Logs`** (checkbox on `DreamBoxClient`, live in Play
Mode). It shows beacons, inbound previews, relay fan-out, and NetId
registrations. Warnings that indicate real problems always log regardless.

Useful from Lua:

```lua
local r = dp.relay()
-- present   is there a DreamBoxClient at all      r.sent       outbound this session
-- state     connection state as a string          r.send_rate  outbound msg/s, last full second
-- connected state == Connected                    r.cap        what THIS host enforces (0 = none)
-- ping      ms, -1 when unknown                   r.budget     what to DESIGN against (never moves)
-- received  inbound this session                  r.queued     waiting for the link to come up

local s = dp.session()
-- present, state, host, is_host, peers, park

dp.on_connected(function() ... end)   -- one-shot; fires now if already up
```

`dp.relay().cap` is 0 when the relay advertises no cap — treat that as
*unknown*, not as *zero allowed*. If you are budgeting, use `budget`; `cap` is
for readouts.

**Sending before the link is up is handled, but only halfway.** The client
queues pre-connection messages (64 deep, 5 s TTL) and flushes them on connect,
so a hello is delivered rather than dropped. It does **not** fix timing: if
your handshake opens a listen window at `start()`, that window can open and
close while the client is still connecting, and it will close against an empty
roster. Anything whose *timing* depends on the link — a join window, a
first-frame state request — belongs in `dp.on_connected`.

---

## 8. Sessions and host migration

`NetSessionArbiter` owns the fallback ladder:

```
Searching ── DreamBox beacon ──▶ ClientDreamBox   (the kiosk always wins)
    │
    ├── peer beacon ───────────▶ ClientPeer
    │
    └── silence for T_listen ──▶ Hosting  (PeerRelayServer + beacon + self-connect)
```

Host loss triggers coordinator-free re-election: every client sorts the live
hostIds identically, rank 0 hosts immediately, rank n waits n × 750 ms. Two
simultaneous hosts resolve by lowest hostId.

Things worth knowing:

- **A live connection is ground truth.** Beacon silence alone never kills a
  healthy session — UDP broadcast is lossy on phone hotspots.
- **Channels.** Core builds beacon on `prod`, SDK projects on `sdk`, so a
  creator testing in a studio cannot attract production headsets on the same
  Wi-Fi. Set `channelOverride` to cross deliberately.
- **`parkId`** scopes peer sessions so two groups in different parks on one LAN
  do not merge.
- **iOS is client-only** (no multicast entitlement in v1).

`A_MP_HostMigration` shows the ladder live. The number to measure is how long
the shared clock stalls during a migration — that gap is your worst-case
gameplay hitch.

---

## 9. Checklist for a new networked attraction

- [ ] One `NetId` on the manager, `explicitId` from your park's block
- [ ] `LuaBehaviour` with `onnet` on the **same** GameObject
- [ ] `onnet` guards against running before `awake()`
- [ ] Props have no NetId; they call the manager through a Script Injection
- [ ] Every message identifies its sender (`u`)
- [ ] Non-idempotent effects carry `(uid, seq)` and are deduped
- [ ] State is absolute, and merges with `max()` or last-writer-per-cell
- [ ] Continuous streams are rate-limited, dead-banded, quantized, coalesced
- [ ] Everything draws from one send budget under 60/s
- [ ] Late joiners get a snapshot — and effects do **not** replay
- [ ] Peers are reaped on silence, not only on `mp_bye`
- [ ] Anything derivable from the shared clock is derived, not broadcast
- [ ] Tested with the host walking out mid-round
- [ ] Poses, hands, projectiles, markers are park-local — never world (`dp.head().position` is world)

---

## 10. Where to look

| You want... | File |
|---|---|
| The reusable primitives | `Scripts/mp_kit.lua.txt` |
| Concurrent scoring | `Scripts/mp_buttonmash.lua.txt` |
| Cheap pose streaming | `Scripts/mp_tracker.lua.txt` |
| A shared timeline | `Scripts/mp_clock.lua.txt` |
| Effects | `Scripts/mp_particles.lua.txt` |
| Spawns + lag compensation | `Scripts/mp_laser.lua.txt` |
| Data custody | `Scripts/mp_staterelay.lua.txt` |
| Ownership | `Scripts/mp_ownership.lua.txt` |
| The transport itself | `Assets/DreamPark/Scripts/Features/Net/` |
| Running a dev relay | `Tools/DreamBoxServer/README.md` |
