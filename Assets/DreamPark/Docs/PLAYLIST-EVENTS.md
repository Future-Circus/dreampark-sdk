# Playlist events: what a game tells a playlist (Oct 2026)

A playlist is a list of attractions plus Lua that decides what happens (PLAYLISTS in the portal / Studio).
The playlist never looks inside your game. Your game and the playlist talk only through the calls on this page:

1. **Announce** the objects a challenge is about: coins, bad guys, things to break (`dp.announce`).
2. **Handle challenge events** the runtime sends to your attraction, and reset in place when asked (`onevent`).
3. **Broadcast** progression events: something was defeated, a puzzle step was solved (`dp.event`).
4. **Show the map** and mark it when your game or the playlist wants to (`dp.map`, `run.map`).

Everything players read about how to play (instructions, objective popups, progress panels, countdowns) is your
game's own UI. The platform draws only the map (when Lua asks for it), Dreamie and the end screens.

Older apps have none of these calls. Guard them: `if dp.announce then ... end`.

---

## 1. Announce what an object is

An object tells the playlist what it is. The playlist decides whether to count it (`stop.count{ kind = ... }` in a
goal block).

```lua
dp.announce(kind, { item = 'coin' })              -- I'm here, and I'm a <kind> thing
dp.announce(kind, { holds = { collect = 1 } })     -- I hold 1 collectible (counted from the start)
dp.announce(kind, { from = source })               -- I came out of `source`: its count moves to me
dp.announce_done({ by = player })                  -- I was collected / defeated / broken (by = a player id)
```

| kind | means |
|---|---|
| `collect` | pick it up (coins, keys, gems) |
| `defeat` | beat it (Jubbos, pengos, moles) |
| `break` | break it (bricks, crates, ice) |
| `target` | hit it (a snowman, a gong) |
| `reach` | get to it (a pad, a door) |
| `find` | find it (a hidden medal) |
| `protect` | keep it safe |
| `catch` | catch it (the scarab thief) |
| `waypoint` | a place on the map (`label = '…'`) |

- Call it from the object's own script: `dp` in a script already knows its object.
- `item` is optional: what it is in your game (`'coin'`, `'jubbo'`).
- **Done is automatic** when the object is **destroyed** or **switched off** (`SetActive(false)`) while its attraction
  is still up. Call `dp.announce_done({ by = player })` when you **hide** it instead (renderer and collider off, the
  restart-in-place pattern), or to credit a player. Packing the attraction away never counts as done.
- Unknown kinds are refused (a warning in the console).

### Holders and hand-offs: the total never changes

A `!` block holding a coin, a Jubbo that drops a coin, a crate with loot: announce what it **holds**. The held
coin counts in the total from the start. When the coin comes out, announce it
**from** the holder. The count moves to the new coin: the total stays the same and nothing counts twice.

```lua
-- block.lua: a ! block holding one coin
function start() dp.announce('break', { item = 'block', holds = { collect = 1 } }) end
function hit(by) dp.announce_done({ by = by }) end         -- the block is spent
function reset() dp.announce('break', { item = 'block', holds = { collect = 1 } }) end
```

```lua
-- coin.lua: a coin, placed in the level or spawned out of a block (`source` = that block's GameObject)
function start() dp.announce('collect', { item = 'coin', from = source }) end   -- from = nil for placed coins
function collect(by)
    dp.announce_done({ by = by })
    self.gameObject:GetComponent(typeof(CS.UnityEngine.Renderer)).enabled = false
    self.gameObject:GetComponent(typeof(CS.UnityEngine.Collider)).enabled = false
end
function reset()
    self.gameObject:GetComponent(typeof(CS.UnityEngine.Renderer)).enabled = true
    self.gameObject:GetComponent(typeof(CS.UnityEngine.Collider)).enabled = true
    dp.announce('collect', { item = 'coin' })
end
```

- `from` takes the source's GameObject (or a component on it, or its script's `self`). The source must have
  announced, in the same attraction.
- A holder that releases more than it declared adds only the extra.
- Release everything a holder holds. A held coin that never comes out stays in "remaining".

### After a reset

When your game resets an attraction in place, every object that was collected, defeated, broken or that released
what it held **announces again**. That starts a new round of counting for it; objects that never changed may
announce again too (nothing happens). Spawned extras (coins that came out of blocks) are cleaned up by the reset;
their old hand-offs stop counting. Headsets that reset at the same moment count once.

### Waypoints: the places on the map

Every placed attraction is a waypoint already: the platform announces `waypoint` for it (labelled with its display name)
with no game code, and again after your game's reset (`reset_done`). The playlist decides which waypoints count
(`run.waypoint(...)`); the map marks those done / not done (§ 5).

```lua
-- an extra place inside your attraction (a secret ledge, a gate): announce it, and mark it done when it is reached
function start() dp.announce('waypoint', { label = 'Secret ledge' }) end
function reached(by) dp.announce_done({ by = by }) end
```

- With your own waypoints announced, the attraction counts as done when all of them are done.
- Without any, the attraction's own waypoint is done when the challenge there is won, or when a script on the attraction's
  root calls `dp.announce_done()`.

### No Lua: the Announce component

Add **DreamPark/Announce** to a prefab: `kind`, `item`, `holds` (kind × count). It announces when the object first
starts and again whenever it is switched back on. Switched off or destroyed = done.

### Multiplayer and late joiners

Each attraction keeps one live list, shared by every headset (`dp.shared_state` underneath, on the attraction's bus).
Every headset's copy of an object announces the same thing; a late joiner gets the current list and counts. Objects
are keyed by their **path under the attraction** (`Coins/Coin~3` = the 4th child named Coin), the same on every
headset and after a reset, so:
- never rename or reorder objects at runtime before they announce;
- spawned objects that count must announce `from` their source (their own path is not stable).

When two players collect the same coin at once, it counts once, credited to one of them on every headset.

---

## 2. What a goal block does with it

Goal blocks (the playlist's Lua per attraction) read the list:

```lua
-- a coins challenge: report progress, won when none are left
return function(stop)
    stop.every(0.5, function()
        local c = stop.count{ kind = 'collect' } -- { total, remaining, done, by = { [player] = n } }, held coins included
        stop.progress(c.total - c.remaining, c.total, 'coins')
        if c.total > 0 and c.remaining == 0 then
            local best, n = nil, -1
            for p, k in pairs(c.by) do if k > n then best, n = p, k end end
            stop.won{ by = best }
        end
    end)
end
```

`stop.census{ kind = 'collect' }` returns just the number left (`field = 'total'` / `'done'` for the others).

**Objective markers** (`stop.mark{ kind = … }`: white pins over objects in AR) are available, but titles don't use them;
they are reserved for Dreamie and help features. The platform never draws them on its own: show progress in your HUD.

---

## 3. Challenge events: what the runtime sends your attraction

Each attraction in a playlist set runs one challenge. The runtime talks to the attraction's own objects with events.
Every script under the attraction that defines `onevent(name, e)` gets them (only **active** objects: hide things,
don't switch off the script that listens).

| Event | Meaning | Your game |
|---|---|---|
| `challenge {occurrence, kind, options, epoch}` | this attraction now runs challenge `kind` (`coins`, `bad_guys`, `break`, or its own id) with `options` (e.g. `time = 30`) | set itself up: its own instructions, UI, countdown. A late joiner gets it once with `late = true` (and `won = true` if already won) |
| `challenge_won {occurrence, by}` | the goal block called `stop.won` | celebrate (your own UI) |
| `challenge_lost {occurrence, why, player}` | `stop.lost` (`why`: `hit`, `time`, …; `player` = one player out, none = the whole attempt) | show your fail beat |
| `challenge_retry {occurrence, epoch}` | reset in place now | reset within ~2–3 s, then answer `reset_done` |
| `challenge_skip {occurrence}` | the skip was used | treat it as won |

```lua
-- on the attraction's coordinator script
function onevent(name, e)
    if name == 'challenge_retry' then
        reset_attraction()   -- hide/show + reset() every object, clean up spawned extras; objects announce again
        dp.event('reset_done', { occurrence = e.occurrence, epoch = e.epoch })
    end
end
```

- No `reset_done` within 6 s: the next attempt starts anyway.
- A reset never destroys what a challenge counts: hide it (renderer and collider off) and bring it back whole. The objects
  keep their paths (§ 1), so they announce again under the same keys.
- `dp.playlist.on(name, fn)` is the same as `onevent`, as a handler.

### Jump challenges: pads and hazards

- **Pads.** Add a `PlaylistJumpPadsAuthoring` component to **any object under the attraction**. The runtime finds every
  one under the stop. Put it on the object the pads belong to, such as the lava pit, not on the AttractionTemplate root:
  the layout packer moves pits, and the pads move with them. `chain` names a set of pads (`stop.jump{ chain = … }`);
  pads a goal block passes itself (`stop.jump{ pads = … }`) win over the authored ones. With no chain named, the runtime
  runs the **longest** authored chain that passes the as-placed check below (e.g. a 3-hop `large` chain in a large room,
  else the default). A named chain is exactly that chain.
- **Checked as placed.** When the jump starts, the runtime checks the authored chain where the layout put it:
  - the pit is switched on;
  - each pad circle is on floor and clear of the hazard;
  - each pad stands on the **real floor**: the surface under it is within 2 cm of the attraction's floor (a flush painted
    island is fine). A pad on a block, rock, raised platform or island above the floor drops the chain, because a person
    can't stand on a virtual block;
  - each hop crosses one 0.5–0.9 m gap, with both pads within 0.2 m of its edges;
  - nothing solid is in a 0.9 m wide flight path from the knees to 3 m up. Creatures, coins and pickups never count,
    announced or not: `E_*` roots, `*coin*` / `*pickup*` (not `*block*`), the Enemy / creature / pickup layers.

  If any of this fails, the whole chain is dropped and the stop runs as a crossing. `stop.jump{}` then returns
  `{ pads = 0, fell_back = true, why = … }`, so show the crossing, not "0/0 jumps". The pads follow their pit if it
  moves.
- **Hazards.** The runtime judges the collider under the player's feet **by itself, not its parents**. That collider is
  a hazard when it is tagged `Lava`, carries a `FloorCutout`, or is named `*lava*` / `*hazard*` (not `*platform*`).
  Islands, walkways and moving platforms inside a `L_LAVAPIT_*` object are ground, so tag the lava surface itself.
- Lava drawn on the floor is fine: a hazard up to 5 cm under the attraction's level floor (the platform's `LevelFloor`)
  wins over that floor. Only the level floor: anything else solid above the lava, even by 1 mm, is ground.

#### Drawing the jump yourself: `stop.jump{ draw = false }`

By default the platform draws the jump:
- purple footprints on the start pad;
- when the player steps on them, the arch and the white landing footprints, plus a chime;
- a burst on landing.

With `draw = false` it draws nothing and your game draws its own look from cues. Everything else still runs:
- the checked chain, hop detection and hazard fails;
- the hop count `stop.jump{}` returns;
- the chime and Dreamie's "Check to make sure it's safe, then jump!" when the player hesitates.

The cues go to the attraction's own objects on each headset, for that headset's player (`onevent` / `dp.on`):

| Cue | When |
|---|---|
| `jump_ready {hop, from, to, arc_height, radius}` | hop `hop` (pad `hop` → `hop + 1`) is the one to make: at the start, after a landing, after a fail (back to its start pad), and again if the pit moved |
| `jump_armed {hop, by}` | the player stepped on its start footprints (the platform shows the arch here and hides the start prints) |
| `jump_landed {hop, by}` | landed on the far pad |
| `jump_failed {hop, by, why}` | the attempt failed (`why`: `hazard`, `window`, `hit`, …); a `jump_ready` for the pad to go back to follows |
| `jump_done` | the last hop landed |

- `from` and `to` are `{ pos = {x, y, z}, rot = {x, y, z} }`.
  - `pos` is the pad's live world position, so it follows a moved or rotated pit.
  - `rot` is Euler degrees, facing along the hop.
- `arc_height` is the arch's height at its middle, in metres. `radius` is the pad's radius.

```lua
-- goal block: the game draws it
return function(stop) stop.jump{ fail_on = { 'hazard' }, draw = false } end

-- on the attraction: SAL-style pads + arc
dp.on('jump_ready', function(e)
    place_pads(e.hop, e.from.pos, e.to.pos, e.from.rot.y)   -- your start / landing pads
    hide_arc()
end)
dp.on('jump_armed',  function(e) show_arc(e.hop) end)
dp.on('jump_landed', function(e) burst_at_landing(e.hop) end)
dp.on('jump_failed', function(e) flash_fail(e.why) end)
dp.on('jump_done',   function() clear_pads() end)
```

---

## 4. Progression events

Broadcast what happens in your game with `dp.event(name, data)`. It does nothing by itself; goal blocks listen
(`stop.on('event', { name = 'defeated' }, fn)`). Data stays under 1 KB.

```lua
dp.event('defeated', { kind = 'jubbo', how = 'stomp', by = player })
dp.event('boss_defeated', { boss = 'king_jubbo' })
dp.event('found', { item = 'medal' })
dp.event('puzzle_step', { id = 'chorus', n = 2, of = 5 })
```

Common names: `defeated {kind, how, by}`, `boss_defeated {boss}`, `hit {cause, player}`, `stage_won`, `failed`,
`found {item}`, `puzzle_step {id, n, of}`, `score`, `game_end {summary}`.

---

## 5. The map: your game and the playlist decide

The map is a miniature of the park that pops up over AR. **The platform never shows it or marks it on its own.** Your
game and the playlist do, so either can choose when (or never). Marks: not done = an empty circle with a down arrow; done =
a white filled circle with a check. Marks and show / hide reach every headset in the run, late joiners included. A
late joiner sees the marks but does not get a pop that is already over.

**Game Lua** (any headset):

```lua
dp.map.mark(target, { done = true })      -- add or update a mark
dp.map.unmark(target)
dp.map.clear()                             -- this attraction's marks
dp.map.show{ seconds = 3 }                 -- pop the map over AR
dp.map.show{ seconds = 3, mark = { done = true } }   -- mark (this attraction) and show, together
dp.map.hide()
```

`target`: nothing = this script's attraction; `'A_CoinGarden'` / a pool id = an attraction; an object (a GameObject,
component or `self`); a position `{ x, y, z }` (world). Outside a playlist run the calls do nothing. Guard them:
`if dp.map then … end`.

```lua
-- SAL: a challenge won → mark this attraction done and show the map for 3 s
function onevent(name, e)
    if name == 'challenge_won' and dp.map then
        dp.map.show{ seconds = 3, mark = { done = true } }
    end
end
```

**Playlist Lua** (`run.map`, attractions by name or pool id):

```lua
run.map.mark('lava-pits', { done = false })
run.map.unmark('lava-pits')
run.map.clear()
run.map.show{ seconds = 4, mark = { ref = 'coin-garden', done = true } }
run.map.hide()
run.placed()            --> { rooms = 3, attractions = 3 }: what the field that is out now spans
```

**The usual rule, one line:** show the map after each field change only when the field spans more than one room. The
same playlist then pops the map in a 3-room house and never in a 1-room apartment:

```lua
return run.batches{ challenges = { 'coin-garden', 'block-course', 'brick-stacks', 'jubbo-crossing', 'lava-pits', 'wet-floor' },
    boss = 'king-jubbo', map = 'multi_room' }
-- or per field: run.show{ attractions = { … }, map = 'multi_room' }   (map = true: always; map = 4: always, 4 s)
```

`'multi_room'` waits until the new field is placed, counts the rooms it spans (`run.placed().rooms`, not the space's
total) and pops the map only when that is more than one. Tested: the offline sim plays this exact Lua in 3 rooms (the map
pops on both headsets, again for set 2) and in 1 room (it never pops).
