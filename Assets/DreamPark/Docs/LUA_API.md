# The `dp` Lua API

Everything the platform exposes to Lua scripts. Runtime behaviour under park loading is in `LUA_RUNTIME.md`.
`§ Name` points to a section of these docs; the table at the end of `Assets/DreamPark/Docs/CLAUDE.md` maps every section to its file.

## The `dp` Creator API — what the platform does so you don't have to

The rule for anything added here: **does it let a developer delete code that isn't
about their game?** If a creator is writing plumbing to survive our loader, that is
a gap in the platform, not a skill issue.

```lua
dp.is_player(other)          -- is this collider the player? (rig-aware)
dp.player()                  -- the player rig GameObject, or nil
dp.head()                    -- the head/camera Transform, or nil (WORLD)
dp.park()                    -- ParkAnchor (then PortalAnchor / LevelAnchor), or nil
dp.to_park(pos) / dp.from_park(pos)   -- world <-> park-local
dp.to_park_dir(dir) / dp.from_park_dir(dir)  -- facing only (normalized)
dp.head_park()               -- pos, fwd in park-local, or nil
dp.scope(go)                 -- that object's script scope (any of its scripts)
dp.attraction(self.gameObject)       -- the containing attraction's ScriptScope
dp.attraction_root(self.gameObject)  -- its root GameObject
dp.game_id(self.gameObject)          -- the containing gameId
dp.on_global(name, fn)       -- fn(value) now if bound, else the moment it appears
dp.storage.game(gameId)      -- game-scope storage outside an attraction
dp.profile.*                 -- profile reads
dp.game_manager()            -- this package's Game Manager scope, or nil (see § Packages
                             --   for the full package/level API: load_level, levels, groups…)
dp.device()                  -- 'phone' | 'headset' | 'editor' (this device)
dp.is_phone() / dp.is_headset()      -- the Editor counts as a headset unless simulating a phone
dp.device_of(playerId)       -- 'phone' | 'headset' for another player, nil if unknown
dp.on_tap(fn[, owner])       -- phone gestures; also on_touch / on_drag / on_pinch.
                             --   Each returns a handle (h.off()); dp.off_tap(h or fn).
-- Multiplayer (§ Multiplayer, MULTIPLAYER.md) and hand-held props (HANDHELD.md):
dp.me() / dp.peers() / dp.peer(id)   -- me, and everyone else's head/hands (THIS headset's world)
dp.leader() / dp.is_leader() / dp.on_leader_changed(fn, owner)  -- the one writer; identical on every headset;
                             --   among players WEARING this title's rig while anyone does (Oct 2026);
                             --   never a phone without its rig ('' when only spectators are here)
dp.participating([go|title]) -- true on a headset; on a phone only while it wears the title's rig (full-screen AR).
                             --   false = spectator: skip gameplay work (§ Phone players). Guard: dp.participating == nil
dp.spectator_action(fn, ...) -- run fn as a deliberate action of this device (a person chose it); taps already are
dp.runs_world([go|title])    -- does this device simulate the title's WORLD (patrols, boss idles, platforms, ambient)?
                             --   The leader while anyone plays; with nobody playing (a phone alone) a spectator runs it
                             --   locally (its sends stay blocked). Gameplay asks dp.is_leader(); the world asks this.
dp.clock() / dp.clock_synced() / dp.random(key[, a, b])         -- shared time and shared randomness
dp.time.now() / dp.time.utc() / dp.time.unix() / dp.time.hour() / dp.time.format(fmt[, utc])
                             -- the DEVICE's wall clock (§ Device time). Per device, NOT synced: fine for
                             --   cosmetics (day/night, seasons, daily resets); never for gameplay every headset
                             --   must agree on (use dp.clock()). Prefer it over os.date: same fields, plus an
                             --   Editor override (DreamPark → Simulated Local Time).
dp.light.ambient() / dp.light.direction() / dp.light.confidence() / dp.light.live()
                             -- the REAL room's light from this device's camera (§ Room light): 0..1 brightness,
                             --   a unit Vector3 toward the light. Per device, NOT synced; reading starts the estimate.
dp.shared_state(name)        -- replicated encounter table: get/set/request/on_request/on_change/synced/rev
dp.net_body(go[, opts])      -- networked physics prop: owner/is_mine/claim/release/home/on_owner_changed
dp.hold(prop, side) / dp.muzzle(prop) / dp.release_hold(prop)  -- hand-held props from their Grip/Muzzle
-- The optimizer (§ What the optimizer guarantees). Older apps lack these: `if dp.enabled then`.
dp.enabled(c)                -- intended .enabled of a Renderer/Collider/Behaviour: the value the unpark
                             --   restores while parked, else the live flag; nil for anything else
dp.set_enabled(c, on)        -- write it: while parked it lands at the unpark, else it is set live
dp.optimizer_ignore(go[, on])  -- keep a manager out of distance culling (on ~= false) / hand it back;
                             --   prefer the static `-- @optimizer-ignore` script comment
-- Routes (§ Routes, ROUTES.md): through real doorways, identical on every headset
dp.ports() / dp.neighbors() / dp.rect()   -- my connectors, the rooms next door, my rect (world)
dp.route(a, b[, opts]) / dp.tour(stops[, opts]) / dp.route_plan(name)
-- Props at runtime (§ Loading props at runtime). Older apps lack these: `if dp.spawn_prop then`.
dp.spawn_prop(name, {parent, pos, rot, world, key, seat}) -> h   -- stream a prop into this attraction
                             --   h.go (nil until loaded) / h.key / h.address / h.state() / h.loaded() /
                             --   h.error() ('not-found' | 'download-failed' | 'not-a-prop' | 'spawn-failed') / h.despawn()
dp.preload_prop(name) -> h   -- download its bundle closure only: h.ready() / h.error()
dp.despawn_prop(h or go)
dp.on_prop_loaded(fn(h)[, owner]) / dp.on_prop_failed(fn(h, err)[, owner])   -- + off_prop_loaded / off_prop_failed
```

`dp.player()` is **nil** whenever there is no rig — before it loads, and for the whole session on a
phone whose title has no Phone Player. Nil-check it.

### Loading props at runtime (Nov 2026)

`dp.spawn_prop` streams a **prop** (a prefab with `PropTemplate`) into an attraction by name, only when the
game needs it: a hub whose portal slots each load their portal prop when that portal opens. Under Smart
bundling every prop is its own bundle closure, so only that prop downloads. It is the prop counterpart of
`dp.load_level`.

```lua
-- @var slotNorth gameobject
local portal
function onready()
    if not dp.spawn_prop then return end                      -- older app
    dp.preload_prop('P_FrostWindow')                          -- optional: fetch the bundles early
    dp.on_prop_failed(function(h, err) print('portal ' .. tostring(h.key) .. ' failed: ' .. err) end)
end
function open_north()                                         -- call it on EVERY headset (from shared state)
    portal = dp.spawn_prop('P_FrostWindow', { parent = slotNorth, key = 'slot:north', seat = true })
end
function close_north() if portal then portal.despawn() end end
```

- **Names.** A bare prefab name resolves within the calling script's title (its Beta twin is the same
  title) to that prop's address, `{gameId}/Props/{category}/{name}`; a full address is taken as is. In the
  Editor a name Addressables cannot locate falls back to the prefab asset, so it works in the Park Simulator
  without a build.
- **It behaves as if it had been authored in the attraction.** It is parented (default: the calling
  attraction's root) and posed BEFORE its `Awake`, so `onready`, `onzoneenter` and `onlayout` see the
  attraction, and its own `PropTemplate`/`GameArea` are suppressed like any nested prop (and, like any
  prop, `dp.attraction()` inside it answers the prop: see § Rules that still apply). The optimizer culls, parks and freezes it (game-master pause) with the rest of the park.
  `pos`/`rot` are local to `parent` unless `world = true` (or `['local'] = false`: `local` is a Lua keyword).
  `seat = true` adds a `FloorAnchor`, so it sits on the attraction's floor.
- **Multiplayer.** Spawning is local, like authored content: every headset calls `spawn_prop` with the same
  name and `key` (decide WHICH prop from `dp.shared_state` or the leader, never per device). The prop's
  NetIds are scoped by the attraction plus the `key`, so they are identical on every headset and on every
  respawn. Use stable keys (`'slot:north'`), never a counter or a time.
- **Idempotent by key** (the key defaults to the address): spawning a live key returns the same handle;
  use distinct keys for several copies. A failed or despawned key spawns afresh.
- **Lifetime.** Despawn with `h.despawn()` / `dp.despawn_prop(h or any object inside it)`. Everything a
  script spawned is despawned with its attraction (or, outside one, with the calling object), and with
  its package.
- **Events.** `dp.on_prop_loaded(fn(h))` / `dp.on_prop_failed(fn(h, err))` hear this title's props and
  drop with the script. They never fire before the call that started the load has returned its handle.
- Keep the attraction's authored look: a slot shows an authored stand-in (a closed frame) until its prop
  arrives (§ The attraction prefab).
- C#: `DreamParkLuaProps` (`Scripts/Core/DreamParkLuaProps.cs`). Sample: `Samples/PropStreaming/`.
  The app must include it (Nov 2026): content feature-tests `if dp.spawn_prop then`.

### Lifecycle hooks

```lua
function onready()      end   -- the world is real: spawned, placed, floors built, enabled
function onzoneenter()  end   -- the player entered THIS script's attraction
function onzoneexit()   end   -- ...and left
function onlayout(frame_id, scale, footprint) end  -- the attraction's applied layout (Oct 2026)
```

`onlayout` is sticky too: once after `start()` with the owning attraction's applied frame (`'base'`,
`'minimum'` or a custom id), its frame scale and its runtime footprint in metres (both `Vector2`, x/z),
then whenever `ApplyLayout` changes any of them (`AttractionTemplate.LayoutApplied`). `onwalls` fires
only when the walled sides change, so a script that re-lays itself out per size wants this one.

All three are **sticky**. If the thing already happened before your script existed,
you are told on arrival. Late is normal here — content loads over the air, the park
spawner parents after Awake, and the optimizer parks components mid-load. Missing an
event must be impossible; arriving late must be fine.

`onready()` fires immediately in a hand-authored scene, because the world is already
real there. That is the contract: **your script behaves the same whether it was
placed in a scene or spawned by the park loader.**

**Game master pause (Oct 2026, `GamePause.cs`).** An operator or parent can hold the game from
the phone. The platform freezes your park content (bodies keep their velocity, animators, particles,
audio, agents, timelines pause, scripts stop, colliders off) and restores it exactly; the player rig,
DreamBand and tracking stay live. `Time.timeScale` is NOT touched, so `Time.time` keeps moving.
```lua
function onpause()  end   -- the game is held: fires BEFORE the freeze (sticky: arrives at start() if already held)
function onresume() end   -- released: fires AFTER the thaw (only after an onpause this script saw)
dp.paused()               -- is the game held right now?
dp.pause_info()           -- {by, role, reason, source} or nil
dp.game_time()            -- Time.time minus every paused stretch: base timers on this
```
Player.prefab managers keep running while held, so stop your round clock / music in `onpause()`.
C# coroutines are not stopped by the freeze. Try it: DreamPark → Multiplayer → Game Master Pause (test).

### Device time: `dp.time` (Oct 2026, `DreamParkLuaTime.cs`)

The device's own clock and time zone, for things like day/night ambience, seasonal dressing and
"new daily challenge" resets.
```lua
local t = dp.time.now()      -- LOCAL: {year, month, day, hour, min, sec, wday (1 = Sunday), yday, isdst,
                             --   ms, utc_offset (minutes), unix (float)}: os.date('*t')'s fields plus three
dp.time.utc()                -- the same table in UTC (utc_offset 0, isdst false)
dp.time.unix()               -- seconds since 1970-01-01 UTC, float with ms precision
dp.time.hour()               -- local hour as a float: 19.5 = 7:30 pm. The day/night helper
dp.time.utc_offset()         -- local offset from UTC, whole minutes (-420 = UTC-7)
dp.time.format('HH:mm')      -- .NET pattern, InvariantCulture ('ddd d MMM' -> 'Mon 12 Oct'); [utc] 2nd arg.
                             --   Allowed: y M d H h m s f F t z K g, a literal T, space : - / . , _ ( ) and
                             --   'quoted text'; anything else (or > 64 chars) returns nil
dp.time.simulated()          -- the Editor's simulated hour, or nil
dp.time.debug_set(22)        -- Editor only: the clock reads 22:00 now and runs on; nil = real clock.
                             --   Returns false on a device (it never changes a real clock)

local night = dp.time and dp.time.hour() or 12        -- feature-test: older apps have no dp.time
night = night < 6.5 or night >= 19.5
```
- **Not synced.** Two headsets can be in different time zones or have drifting clocks. Anything every
  headset must agree on (round timers, spawn schedules, who acts first) uses `dp.clock()`, or the
  leader decides and shares it.
- **Testing night in the afternoon:** DreamPark → Simulated Local Time → Dawn / Noon / Dusk / Night /
  Midnight / Custom Hour… (a slider). This is an EditorPref, works with the Park Simulator and is applied
  again each time Play starts. A player build ignores it.
- **`os` is still there:** the Lua sandbox keeps `os.time`, `os.date`, `os.clock` and `os.difftime` (the rest
  of `os` is removed; `io` and `debug` are gone). Prefer `dp.time`: the fields are the same, and the Editor
  override reaches it. `os.date` never sees the override.

### Room light: `dp.light` (Oct 2026, `DreamParkLuaLight.cs`)

The REAL room's light as this device's camera sees it, for games built on it: a hidden message that only
appears when the lights go off, a sundial puzzle that works from the real light direction.
```lua
dp.light.ambient()       -- 0..1 how bright the real room is: 0 = pitch dark, 1 = a normal bright room or brighter
dp.light.direction()     -- Vector3, unit, world space, pointing TOWARD where the light comes from
                         --   (a shadow falls along -direction)
dp.light.confidence()    -- 0..1 for the direction: low = no clear main light (flat or overcast room)
dp.light.live()          -- true while a real estimate is flowing; false = fallback values
                         --   (ambient 1, direction = the scene sun's, confidence 0)
dp.light.simulated()     -- Editor: DreamPark → Simulated Room Light is on
dp.light.debug_set(0.05[, dir])  -- Editor only: read this ambient (and dir: Vector3 or {x, y, z}; omitted = keep);
                         --   nil = the real estimate. Returns false on a device
```
- **Reading turns it on.** The first read starts the camera estimate on this device (even when the scene's own
  real-light lighting is off); it stops ~10 s after the last read. So `live()` is false for the first fraction of a
  second and whenever there is no estimate (no camera permission, an older app, the SDK alone): keep your current
  state then. Read a few times a second, not more: the estimate updates ~10×/s.
- **Each device reads its OWN room** (not synced, like `dp.time`). For a multiplayer puzzle decide on one device and
  share the result: `if dp.is_leader() and dark then s.set('lights_out', true) end` (`dp.shared_state`).
- **Thresholds want hysteresis**: the value moves as the player looks around. Switch on below 0.15, off above 0.25.
- **Testing without a headset:** DreamPark → Simulated Room Light → Bright Room / Dim Lamp / Lights Off,
  Light Overhead / Low From +X / Low From +Z, or Custom… (sliders). An EditorPref, applied again at each Play; a player
  build ignores it. In Lua: `dp.light.debug_set(0.05)` / `dp.light.debug_set(nil)`.
- Older apps have no `dp.light`: guard with `if dp.light then … end`.

Hidden object that appears when the lights go off:
```lua
-- inject: secret (GameObject), shipped inactive
local shown, next_read = false, 0
function update()
    if not dp.light or CS.UnityEngine.Time.time < next_read then return end
    next_read = CS.UnityEngine.Time.time + 0.2
    if not dp.light.live() then return end          -- no estimate: keep the current state
    local a = dp.light.ambient()
    if not shown and a < 0.15 then shown = true;  secret:SetActive(true)
    elseif shown and a > 0.25 then shown = false; secret:SetActive(false) end
end
```
Sundial: the gnomon's shadow follows the real light.
```lua
-- inject: gnomon (Transform, pivot at its base, `height` metres tall), shadow (Transform: a flat strip,
--         pivot at one end, 1 m long along +Z), mark (Transform: where the shadow must point to solve it)
local V3, height, solved = CS.UnityEngine.Vector3, 0.25, false
function update()
    if not dp.light then return end
    local l = dp.light.direction()
    if dp.light.confidence() < 0.3 or l.y < 0.1 then shadow.gameObject:SetActive(false) return end
    local tip = V3(-l.x, 0, -l.z) * (height / l.y)  -- where the gnomon tip's shadow lands, from its base
    if tip.magnitude < 0.01 then shadow.gameObject:SetActive(false) return end  -- light straight overhead
    shadow.gameObject:SetActive(true)
    shadow.position = gnomon.position
    shadow.rotation = CS.UnityEngine.Quaternion.LookRotation(tip.normalized, V3.up)
    shadow.localScale = V3(0.02, 0.002, math.min(tip.magnitude, 1.5))
    local to_mark = mark.position - gnomon.position
    to_mark.y = 0
    if not solved and V3.Angle(tip, to_mark) < 8 then solved = true end  -- multiplayer: tell the leader
end
```

### What these replace

| Instead of | Write |
|---|---|
| `other.tag == 'Player' or other.gameObject.layer == LayerMask.NameToLayer('Player')` | `dp.is_player(other)` |
| walking parents calling `GetComponents(typeof(CS.LuaBehaviour))` looking for a marker field | `dp.attraction(self.gameObject)` |
| `pcall(function() local lb = go:GetComponent(typeof(CS.LuaBehaviour)); sc = lb.ScriptScope end)` | `dp.scope(go)` |
| `FindObjectsOfType(typeof(CS.LuaBehaviour))` scanning for a manager by script name | `dp.on_global('mygame', fn)` |
| `if not registered then try_register() end` every frame, giving up after 300 tries | `function onready()` |
| caching `GameArea.isPlaying` and diffing it each frame | `function onzoneenter()` |
| `if manager then pcall(...) end` on every call | `dp.on_global` once, then just call it |
| `pcall(os.date, '*t')` / `pcall(os.time)` for the local hour or date | `dp.time.now()` / `dp.time.hour()` / `dp.time.unix()` |
| guessing day/night from the clock to decide whether the real room is dark | `dp.light.ambient()` |
