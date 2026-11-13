# The foot API (FeetTracker v2, Oct 27 2026)

There is no real foot tracking on Quest. `FeetTracker` **estimates** where your kicking foot is from the
headset (position, facing, how you move) and **predicts kicks** you make while standing still. The optional
depth prototype (core only, Quest only) can **confirm** a foot that it sees in the headset's depth map.
Everything below is local to this headset. Nothing here is synced; send your own result if peers need it.

Lua titles use `dp.feet` (new headset app only). Always guard it, the live app does not have it:

```lua
if dp.feet then ... else <today's path> end
```

## What is unchanged (SAL and every existing rig)

- `FeetTracker` stays on the rig's `Feet` object with the same fields (`head`, `rb`, `yOffset`,
  `kickMultiplier`) and methods (`GetDirection()`, `GetVelocity(mult)`).
- The rig's `Kicker` collider (tag `Kicker`) still follows the foot through its FixedJoint, so every
  `tags:["Kicker"]` filter (SAL Jubbos, pears, blocks, shells) keeps working with no content change.
- What changed is where the foot goes:
  - It sits on the floor under you, not 1.3 m below your eyes. Kids and tall players now reach the same
    targets.
  - It reaches forward in front of you.
  - It leads with your movement, using time-based velocity, so it works the same at any frame rate.
  - It sweeps into a kickable when you make a standing kick.

## Lua: `dp.feet`

### `dp.feet.state()`
Returns `nil` (no rig worn) or a table:

| field | meaning |
|---|---|
| `x, y, z` | foot point in world metres. `y` is the floor under you (the sole), not head-relative. |
| `vx, vy, vz` | foot velocity, world m/s. Time-based (about 80 ms window), frame-rate independent. |
| `speed` | horizontal speed of the foot, m/s. |
| `dx, dz` | the foot's horizontal direction: the strike direction while striking, else movement, else facing. |
| `floor_y` | the floor height used. |
| `floor` | `true` when the floor was measured (a Level collider under you). `false` = head height minus 1.3 m was used. |
| `source` | `'estimated'` (from the head), `'predicted'` (a standing-kick sweep in progress) or `'detected'` (the depth probe saw a foot in the last 0.2 s). |
| `tracked` | `true` only when `source == 'detected'`. The foot is never truly tracked. |
| `striking` | `true` while a predicted or requested strike sweep is running. |
| `t` | `Time.time` of the sample. |

### `dp.feet.on_kick(fn [, owner_go])`
Calls `fn(k)` once per kick contact. The listener is dropped when `owner_go` is destroyed. `k` is:

| field | meaning |
|---|---|
| `x, y, z` | where the foot was at contact (world). |
| `vx, vy, vz`, `speed` | foot velocity at contact (world m/s), plus its horizontal speed. |
| `dx, dy, dz` | unit kick direction. |
| `confidence` | 0..1. About 0.5 for a plain contact of the estimated foot (walking into it), 0.7 for a predicted standing-kick sweep, 0.9 when the depth probe saw a foot there. |
| `source` | `'contact'`, `'predicted'` or `'depth'`. |
| `target` | the GameObject touched (physics kickables), or `nil`. |
| `target_key` | the key of a virtual target (see `dp.feet.target`), or `nil`. |
| `t`, `seq` | `Time.time`, and an increasing number. |

A target fires once per touch. It can fire again after the foot has left it for 0.25 s.

**Be honest about power.** A predicted standing kick has no measured foot speed. Its `speed` is a sweep speed
scaled by how sharply your head moved (about 2 to 5 m/s). Treat it as power, never as a measurement.

### `dp.feet.target(key, x, y, z, radius)` / `dp.feet.untarget(key)`
Registers a **virtual** kickable: something that is not a Unity collider, such as a ball simulated in Lua.
The standing-kick predictor aims at it, and contacts with it (swept, per physics step) arrive through
`on_kick` with `target_key = key`. Update it every frame while it moves; `untarget` it when it is gone.
Physics kickables (any collider with a non-kinematic Rigidbody that is not on the Player or Level layer)
are found automatically.

### `dp.feet.strike(x, y, z [, confidence])`
Asks for a strike sweep toward that point now, the same sweep a predicted standing kick uses. Use it when
your title knows better than the predictor, for example a goal-kick button. Returns `false` while a strike
is already running.

### `dp.feet.last_kick()`
The newest kick table, or `nil`. Use it to poll instead of `on_kick`.

## C# (core / SDK code)

- `FeetTracker.Local`: the active local tracker, or null.
- Read-only: `FootPosition`, `FootVelocity`, `FloorY`, `FloorMeasured`, `Source` (`FeetTracker.FootSource`),
  `IsStriking`.
- `static event Action<FeetTracker.KickEvent> Kicked`.
- `RequestStrike(Vector3 target, float confidence, string source)`.
- `SetTarget(string key, Vector3 pos, float radius)` / `ClearTarget(string key)`.
- The pure maths lives in `KickPredictor` (no Unity scene needed). Editor harness:
  `Tools/Builders/KickHarness.cs` in the SDK.

## Switches on the component (rig authors)

- `predictStandingKicks` (on): turn it off for a rig whose foot must never sweep on its own.
- `floorRelative` (on): measure the floor on the Level layer. Off = v1's head + `yOffset`.
- `footCenterHeight` (0.3): the foot body's centre above the floor (SAL's Kicker box is 0.6 m tall).
- `kickableLayers` (everything): Player and Level are always excluded.
- `useV1` (off): an A/B switch that behaves exactly like the old foot, for comparisons.
- `tuning`: the predictor's thresholds (`KickPredictor.Settings`). The defaults are tuned on modelled head motion,
  not yet on a headset.
- A head jump of more than 0.6 m in under 0.2 s (a teleport or relocalization) restarts the foot's history, so it
  never reads as a kick.

## Depth (core, Quest only, prototype)

`KickDepthProbe` (core `Assets/Scripts/Kick/`) watches the headset's environment depth around a kickable you are
looking down at. When a real leg or foot appears there, it calls `RequestStrike` and `MarkDetected`, so the kick
reports `source = 'depth'` and `confidence = 0.9`. It is off unless a kickable is within 1.2 m and you look at it.
Kill switch: PlayerPrefs `dp_kick_depth` = 0. Titles do nothing to get it.

## Soccer notes (DreamSports)

- Feed your Lua ball as a virtual target every frame. Kick off from `on_kick` (`target_key` = your ball).
- Or, to keep your own swept detector, sample `dp.feet.state()` every frame. `x, z` and `vx, vz` are the
  boot path you need.
- Foot height above the floor is not known. `y` is always the floor, so chips need your own lift rule, from
  how high the predicted speed's `vy` or the head goes.
- A hand touch is a handball. Nothing in `dp.feet` uses hands.
