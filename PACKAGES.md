# Packages: Arena, Sequence & Adventure

> The creator guide to DreamPark's three package formats, the Game Manager, and the Player rig.
> Short version for agents: `CLAUDE.md` § Packages. Runtime/engineering spec: `PACKAGE_RUNTIME_SPEC.md`.

Every venue is a different shape. A package is how your title turns *whatever floor an operator
scanned* into a playable experience, automatically, without losing what you authored — the order
things happen in, what's required, where the game starts and where it ends.

You author all three packages from the same library of Attractions and Props, in one panel:
**`DreamPark → Content Uploader` → Packages**. The same **Game Manager** (your rules and state)
and the same **Player** (the guest's body) run under all three.

| | **Arena** | **Sequence** | **Adventure** |
|---|---|---|---|
| **In one line** | Instant: the best-fitting piece of your content for every free space | Your whole game in one room | Your game spread across the venue |
| **Space** | Any scanned rectangle, down to 1 × 1 ft | One room, authored at 12 × 18 ft, shrinks/grows within its bake | The whole scanned park |
| **What the guest sees** | One representative Attraction or Prop per free space | Start Level → your attractions one at a time → Game Over Level | Start Point → attractions laid out along a walking route → End Point |
| **You author** | Priority within each size (everything is included by default) | The order, plus editable Start / Overlay / Game Over levels | Start Point, End Point, ordered attractions & Groups |
| **Loads** | Each chosen piece at its place | One streamed level at a time | Every occurrence, all deployed at once |
| **Required?** | Optional | Optional | Optional |

Every package is optional, but your title needs **at least one** to upload. A Sequence or Adventure
you haven't finished is simply left out of the upload (with a console warning) — never shipped broken,
and never blocking. It's better to ship just an Arena than a broken Sequence or Adventure.

---

## Why packages matter

- **Your title plays everywhere.** An operator picks your title, the app measures their space and
  builds the right package. You never hand-place anything for a specific venue.
- **Your intent survives.** A loose pile of attractions has no order, no start, no finish, no idea
  which pieces the game can't live without. A package carries all of that: occurrence order, Groups,
  Start/End, and `required` flags.
- **There is always a playable answer.** If an Adventure's required pieces don't fit, the app falls
  back to your Sequence — the real, compiled game in one room — and, without one, to your Arena,
  instead of dropping random props.
- **One codebase for your rules.** The Game Manager and Player are shared by all three formats. The
  same Lua runs whether the guest is in one room or walking a whole venue.
- **Sequence streams.** Only the level on screen is *instantiated*; each stage is downloaded when it
  is first needed (the first stage is warmed while the guest stands in the Start Level, and each
  next stage while the current one plays). A stage's loaded prefab stays cached until the package
  is torn down, so going back is instant — the cost is that bundles you have visited stay in memory.
- **Arena makes you discoverable in small spaces.** Even a 3 × 3 ft corner gets *something* of
  yours, if you've authored something that small.

---

## The Packages panel

`DreamPark → Content Uploader` has two relevant sections:

1. **Park Assets** — your reusable library: every Attraction and Prop in the content folder. Create
   **Groups** here to organize reusable sets. Nothing in the library is moved when you place it in a
   package; a placement is a *reference*, so the same asset (or Group) can appear more than once, and
   each placement keeps its own stable identity (its *occurrence*).
2. **Packages** — three tabs: **Arena · Sequence · Adventure**. Each tab has:
   - a description of the format,
   - a **▶ Test … in Park Simulator** button,
   - a **GAME MANAGER** card and a **PLAYER** card (click to select/ping the prefab),
   - the organizer for that format.

The panel saves every change immediately to small JSON files next to your content (see
[Files on disk](#files-on-disk)). Commit them — they *are* your package authoring.

**Test / upload compiles.** Pressing a Test button or uploading compiles the packages into
`DreamPark Package Manifest.asset` (the runtime recipe) and, for Sequence, `Dream Sequence Package.asset`.
Attractions without a packing bake are baked on the way. You never edit those two assets.

---

## Layout frames: one attraction, many sizes

> **Rule: one attraction per piece of content, never one per size.** Two attractions should differ in what they *are* —
> theme, look, feel, gameplay — not in how big they are. If your arena needs to work from a 6 × 10 ft closet to a
> basketball court, that is ONE attraction with a Minimum frame, a few custom frames and grow limits; not
> `Arena_Small` + `Arena_Medium` + `Arena_Large`. Size variants as separate attractions split revenue history and
> previews, put duplicates in the Arena, and multiply every fix.

Shrinking isn't right for every game. Breakout with squashed bricks is a worse game; Breakout with
**fewer** bricks is the same game in a smaller room. A **layout frame** is a hand-authored arrangement
of your attraction at a specific **width × length**: turn objects off, move or rescale them, or switch on
extras that only make sense in a big room. Each frame gets its own automatic shrink/grow range, and the
app picks the **largest frame that fits** the space it was given, then shrinks or grows *within that
frame*. Frames never blend into each other — players never watch an attraction resize.

- **Where:** the Attraction's inspector → **Layout Frames**. *Add Frame*, set its size in feet, click
  **Edit**, change the objects in the Scene or Prefab Mode, **Save Frame**. Only what you changed is
  stored; the prefab itself (the **Base** frame) is never modified. Objects you *add* while editing a
  frame exist only in that frame.
- **The Minimum frame** is the smallest version of the attraction that still works — it can be a single
  prop. Make one for every attraction: without it, your attraction can never be smaller than its Base
  shrink. It replaces the old *Essential Props* list (existing lists are converted automatically).
- **The coverage chart** in the inspector shows every frame's range against the standard 12 × 18 ft
  Sequence room, so gaps are obvious.
- **Previews:** every frame gets its own HD preview. In the Content Uploader an attraction with frames
  shows as a **stack** — expand it to see each size.

## Wall layers: optional extras for real walls

**"No walls" is always the baseline.** Your attraction has to be complete and playable in the middle of
an open floor. When the park knows a side of the space is a real wall, a **wall layer** can make that
side *better* — a target board on the wall, a scoreboard hung where players look, decorations that lean
on it. Nothing the game needs may live in a wall layer.

- **How the park decides:** every space the app scans knows which of its edges are real walls (measured
  from the room scan, doors and windows excluded). When your attraction is placed flush against such an
  edge, the side of your attraction on that edge is *walled*, and its wall layer switches on. You never
  measure anything: the edge of the space IS the wall.
- **Where:** the Attraction's inspector → **Wall Layers**. *Add wall layer* → pick a side
  (Front = +Z, Back = −Z, Right = +X, Left = −X). Click **Edit**: a blue ghost wall stands on that side of
  the footprint. Move, rotate, scale or turn objects on and off, or add new ones (they appear only when
  that side is walled), then **Save Wall Layer**. Only what you change is stored.
- **Order is priority.** Use ▲/▼ to reorder. If two walled layers both move the same object (a target
  that can hang on the front *or* the left wall), it goes to the higher one; with only one of those
  walls, it goes there; with neither, it stays where Base puts it.
- **Works with every layout frame.** A wall layer applies on top of whichever frame the park picks.
  Objects you place against the ghost wall keep the same distance from that edge — and the same relative
  place along it — at any size the attraction is shrunk or grown to.
- **Preview:** under the list, toggle *Front / Back / Right / Left* to see any combination of walls. In
  Play Mode the layers are applied to the live attraction. Baking checks every combination and warns
  when a wall layer puts something on top of something else.
- The park prefers seating an attraction so that more of its wall layers face real walls, but only
  when it costs nothing — never a smaller layout, never a worse fit.

---

## Arena

**What it is.** An instant package that works in any space. Every Attraction and Prop is
automatically sorted into a **size bucket** — its smallest (baked shrink) footprint, rounded down to
whole feet and rotation-normalized to *short side × long side*. An attraction with
[layout frames](#layout-frames-one-attraction-many-sizes) appears **once per frame**, each in the size
its frame shrinks to, with that frame's preview — so one attraction can cover many sizes. The size
category is authoritative: an entry in the 5 × 10 ft size is placed in a 5 × 10 ft space even if its
exact footprint is 5.4 × 10.1 ft. When the app builds an Arena, it walks
the scanned floor rectangle by rectangle; for each one it takes the **largest bucket that fits** and
places that bucket's **first** entry (your priority winner). Big spaces get your attractions; the gaps
left over get your small props.

**Authoring it.**

- Everything is **in by default**. New assets enter Arena automatically.
- **Drag cards within a size** to set priority — the first card is that size's pick.
- **Drag a card onto another size header** to override the measured size (e.g. you want an 8 × 9 ft
  attraction offered only in rooms that are at least 10 × 10). *Right-click → Reset Arena Size* undoes it.
- **Right-click → Exclude from Arena** for anything that shouldn't stand alone (a door that only
  makes sense inside its attraction). Excluded assets stay in Park Assets and in your other packages.
- The panel shows **Recommended square-size gaps** — square sizes from 1 × 1 up to your largest bucket
  that nothing covers. A small prop at each gap means no scanned space is ever left empty.

**Rules.**

- Arena candidates are never `required` — they are alternatives, not a set.
- If an entry's content isn't mounted, the app skips to the next entry in that bucket. An entry is
  never skipped for being slightly larger than its size — the size you see it in is the size it's
  placed at (it uses its smallest shrink when its exact footprint is bigger).
- If no bucket fits any scanned rectangle, the Arena fails ("no Arena size bucket fits"). There is no
  fallback from Arena.
- A guest who is in your Arena and is offered your Sequence or Adventure has to accept that switch in
  the app before the Arena is replaced.

**Design tips.** Make your Arena winners *self-explanatory at a glance* — they are often the first
thing anyone sees of your title. Put your best small attraction first in its bucket, not your
biggest set piece squeezed down.

---

## Sequence

**What it is.** Your whole game, playable in one flexible room. The room is authored at
**12 × 18 ft** and can shrink or grow within the limits of the levels it contains. The guest starts
on your **Start Level**, and your attractions stream in *one at a time*, in the room, with a particle
transition covering each swap, until the **Game Over Level**. A persistent **Overlay Level** sits on
top of every ordinary level for navigation and HUD.

The Sequence is **optional**. It counts as authored once its organizer has at least one visible
attraction that fits the 12 × 18 ft room; until then it's left out of the upload with a console
warning. Once authored, it's the whole game in one room and the first fallback when an Adventure
doesn't fit — and it must be valid (`dream-sequence-required` blocks a broken one).

### Anatomy

The Sequence tab shows, top to bottom:

```
GAME MANAGER   PLAYER                 ← shared by all three packages
START LEVEL    OVERLAY LEVEL          ← editable 12 × 18 ft special levels
  your attractions and Groups, in order (drag to reorder, drop from Park Assets to add)
GAME OVER LEVEL                       ← editable 12 × 18 ft special level
```

At runtime the app assembles this hierarchy — **no per-title "Dream Sequence" prefab exists or is
needed**:

```
Dream Sequence            transient AttractionTemplate: owns the calibrated floor & room size
├── Levels
│   ├── Level00_Start Level
│   ├── <streamed attraction>          only the current one is resident
│   └── Level99_Game Over Level
├── Persistent Overlay Level           active over ordinary levels only
└── Persistent Level Transition        copied out of the Overlay's "Default Level Transition"

Game Manager, Player                   live OUTSIDE the room (see below)
```

### Slots

Sequence levels are addressed by **1-based slot**:

| Slot | What |
|---|---|
| `1` | Start Level |
| `2 … N+1` | Your streamed attractions, in organizer order (Groups flattened in place) |
| `N+2` = `dp.level_count()` | Game Over Level |

### Which attractions can play

An Attraction plays in the Sequence if **its authored size, or the baked shrink footprint of any of its
layout frames, fits 12 × 18 ft in either orientation**. In a given room the largest frame that fits is
used. Incompatible items stay visible (dimmed) in the organizer and are
skipped at compile — unless the Attraction has **Game Requires Attraction** ticked, in which case the
upload is blocked until you shrink/rebake it or clear the flag. **Props never play as Sequence levels**
(a Group containing props is fine; its props are skipped).

### How the room is sized

- The Sequence's **minimum room** is the largest *shrink* footprint among everything it contains —
  Start, Overlay and Game Over included — using each level's smallest fitting layout frame, so a
  Minimum frame lets the whole game fit a smaller room. Its **maximum** is the largest *grow* footprint. One big,
  unshrinkable object on your Start Level pins the whole game to a bigger room.
- In a given room each level is packed between its own shrink and grow limits and **never stretched**
  past its grow limit — spare floor is left empty rather than distorting meshes and colliders.
- A level is rotated 90° if that's the only way (or the better way) it fits.
- The Sequence owns **one** calibrated floor. Its grade is transferred to each level as it appears;
  each level keeps its own floor cutouts (pits, lava…).

### Transitions & loading

- A level change plays the transition for at least `transitionDelaySeconds` (2.5 s) and swaps the level
  at the **end** of it, after the next level has downloaded and been checked for fit. That window is
  the guest's cue that the world is about to change.
- Requests supersede each other: the last one wins.
- If the next level can't load or can't fit, the current level stays and `on_level_failed` fires with
  `dp.level_error()` set (see [Lua API](#lua-api)).

---

## Editing the Sequence boilerplate: Start, Overlay, Game Over

The Content Uploader generates three editable prefabs the first time you open the Sequence tab:

```
Assets/Content/{YourGame}/DreamSequence/Start Level.prefab
Assets/Content/{YourGame}/DreamSequence/Overlay Level.prefab
Assets/Content/{YourGame}/DreamSequence/Game Over Level.prefab
```

They are **yours**. Click a card in the Sequence tab to ping it, open it in Prefab Mode, and edit it
like any attraction. Each is an ordinary `AttractionTemplate` set to **Custom 12 × 18 ft**, with a
**Sequence Floor Border** that follows the packed room edge.

### Start Level — your title screen

What ships:

- **`START — Super Adventure Land Button`** — an instance of the SDK's *Start Button* prefab, sitting
  on the floor at the room's centre. It's a trigger collider running the shared control script with a
  string injection `action = start`, plus a `DreamSequenceButtonFeedback` that squashes its
  *Press Visual* child, plays a click and ignores re-presses for ~2 s.
- **It never starts by itself** (`dp.button_press()`, Oct 2026): it is stepped on (any collider of the
  local player presses it; set the feedback's *Pressed By* to *Hands* for a hand-only control), but it
  arms only after it has been live ~0.75 s with nobody inside it. A guest who is standing on it when the
  level comes up, or who is still on it after a press, has to step off first.
- Pressing it calls `dp.game_manager().start_game()`, which by default is `load_level(2)` — the first
  attraction.

Make it yours:

- **Brand it.** Title, logo, mascot, set dressing. This is the first thing a guest sees of your
  Sequence; a Start Level that is just a button in an empty room makes a finished game look broken.
  Author decorations as **Props** (children with `PropTemplate`) so the packing bake can pull them in
  when the room is small — see *How the room is sized* above.
- **Move or restyle the button** with prefab-instance overrides (position, scale, materials). Keep the
  `LuaBehaviour` and its `action` injection.
- **Replace the button** with anything — a portal, a lever, a proximity trigger. Either reuse the
  shared control script (`Assets/DreamPark/RuntimeAssets/DreamSequence/DreamSequenceButton.lua.txt`,
  inject `action = start`) or write your own:

  ```lua
  -- my-start-portal.lua.txt
  local armed = true
  function ontriggerenter(other)
      if not armed or not dp.is_player(other) then return end
      armed = false
      local gm = dp.game_manager()
      if gm ~= nil and gm.start_game ~= nil then gm.start_game() end
  end
  ```

- **Change what "start" means** in the Game Manager, not in the button — e.g. `start_game` jumps to a
  hub Group or picks a random first level.

### Overlay Level — persistent navigation & HUD

What ships:

- **`Default 3D Level Navigation`** — an instance of the SDK's *Elevator Controls* prefab on the room's
  left edge, facing in: **BACK** (`action = back` → `previous_level()`) and **FORWARD**
  (`action = forward` → `next_level()`), same feedback/cooldown as the start button.
- **`Default Level Transition`** — the particle-and-sound effect that covers every level swap.

Behaviour:

- By default the Overlay is **visible over ordinary levels and hidden on Start and Game Over**. Take
  control with `dp.overlay_auto_visibility(false)` and `dp.show_overlay(true|false)`.
- The Overlay has **no floor of its own** — its floor-anchored objects sit on the current level's floor.
- It persists across level swaps, so it's the right home for per-run HUD: a score board, a timer, a
  level counter reading `dp.current_level_index()` / `dp.levels()`.

Make it yours:

- Restyle or move the navigation, or delete it entirely if your Game Manager advances levels itself
  (objectives, portals at the end of each level, a timer).
- Replace the transition with your own `ParticleSystem`-based effect (on `DreamPark/Particles`), but
  **keep the object named exactly `Default Level Transition`** — the runtime finds it by that name and
  copies it out so it can also cover the Start and Game Over swaps. It must contain at least one
  `ParticleSystem`; it is stopped-and-cleared then replayed on every change, so an authored *Play On
  Awake* can't leave it running.

### Game Over Level — your ending

What ships: an empty room (just the border). An empty Game Over is baked with a near-zero shrink
footprint on purpose, so it never forces a bigger room.

Make it yours: a scorecard, a thank-you, credits, a **play-again** control:

```lua
-- replay-button.lua.txt  (trigger on the Game Over Level)
function ontriggerenter(other)
    if dp.is_player(other) and dp.button_press() then
        local gm = dp.game_manager()
        if gm ~= nil and gm.load_level ~= nil then gm.load_level(1) end   -- back to Start
    end
end
```

Once you add content, its shrink footprint counts toward the Sequence's minimum room like any other
level.

### Rules for all three

- **Leave the size at Custom 12 × 18 ft.** The scaffold refresh resets any other size.
- **Don't use these child names** — the refresh deletes them: `12 ft × 18 ft Level Floor`,
  `LevelFloor`, `Game Over Backdrop`.
- **Your edits are safe.** Scaffold upgrades are one-time, stamped per prefab; removing the default
  button or border won't make it come back. **Deleting** a prefab regenerates the default.
- **They need a packing bake.** Test and upload bake them for you; you can also press
  *Bake Packing Variants* in the AttractionTemplate inspector.
- **Prefer instance overrides to editing SDK files.** The shared *Start Button*, *Elevator Controls*,
  *Level Transition Effect* and *Sequence Floor Border* prefabs live under `Assets/DreamPark/` and are
  replaced when you update the SDK. Override them on the instance, or build your own control in your
  content folder.
- Use `FloorAnchor` for anything that must sit on the calibrated floor.

---

## Adventure

**What it is.** A spatial package that expands across the venue. You set a clear **Start Point**, an
ordered set of Attractions and Groups, and a required **End Point**. The app lays them out along a
walkable route through the scanned park — Start first, End last, each attraction facing the one
before it — and **every occurrence stays deployed**; the guest walks between them.

**Authoring it.**

- Drag an Attraction onto **START POINT** and one onto **END POINT** — both are required. An
  Adventure missing either one is unfinished: it's left out of the upload with a console warning, and
  stale placements inside it don't block.
- Drop Attractions, Props and Groups between them in the order you want them met. Repeats are allowed;
  each placement is its own occurrence.
- Hidden placements are not deployed.
- Tick **Game Requires Attraction** on attractions the game cannot function without.

**At runtime.**

- Placement is **atomic**: the existing layout isn't touched until every required occurrence has a
  slot and has spawned. If a required piece doesn't fit, or fails to load, the app falls back to your
  **Sequence** if you authored one, otherwise to your **Arena** if it has any sizes, otherwise it fails.
- Attractions are culled independently by distance like any park content. The Game Manager is not —
  it keeps running however far the guest walks.
- `dp.load_level(slot)` does **not** hide anything in an Adventure. It moves the package's "current
  stop" marker and fires `on_level_loaded`, so your Game Manager can track progress through the route.

---

## How the app chooses

The operator (or Dreamie) asks for a package kind; the app builds it against the scanned floor.

| Requested | Tries | If it can't |
|---|---|---|
| Arena | Largest-fitting bucket per scanned rectangle | Fails — no fallback |
| Adventure | Start → stages → End, all required pieces | Falls back to **Sequence** (if authored), then **Arena** (if it has sizes), then fails |
| Sequence | A free rectangle at least as big as the Sequence's minimum room | Fails with a blocking "doesn't fit" |

One package is active per park. The Player and Game Manager are created **before** any attraction
gameplay starts, and a failed replacement leaves the previous package playable.

---

## The Game Manager

The Game Manager is **your game's brain**: one persistent, developer-owned prefab holding the title's
rules, progression and state. It is shared by Arena, Sequence and Adventure. It is **not** a spatial
container — nothing is parented under it and it never affects room size.

### Where it lives

```
Assets/Content/{YourGame}/Prefabs/Game Container.prefab     ← the Game Manager (historical file name)
Assets/Content/{YourGame}/Scripts/game-container.lua.txt    ← its default script
```

The uploader creates both once, the first time you open the Packages section. **It never overwrites
them** — recompiling a package doesn't touch your manager or its Lua. (Delete the script and it's
regenerated with the default.) The prefab ships with three components:

| Component | Why |
|---|---|
| `LuaBehaviour` → `game-container.lua.txt` | Your rules |
| `NetId` | One shared network bus, so navigation is shared by every player in the session |
| `OptimizedAFIgnore` | Never culled by distance (it still pauses in build mode and tears down with the park) |

Keep the file name — the runtime loads it by address `{YourGame}/Game Container`.

### Lifetime

- Instantiated **once per package**, named `Game Manager`, outside every calibrated attraction.
- Survives every level change; destroyed when the package is replaced or the park unloads.
- **Only exists when your title runs as a package.** Attractions placed loose (by hand, or by an older
  park) have no Game Manager, so `dp.game_manager()` returns `nil`. Always nil-check — the shipped
  control script does, and falls back to `dp.next_level()` / `dp.previous_level()`.

### The default script, briefly

| Function | Default behaviour |
|---|---|
| `start_game()` | `load_level(2)` — the first attraction after Start |
| `next_level()` | `load_level(current + 1)` |
| `previous_level()` | `load_level(current - 1)` |
| `load_level(index[, groupId])` | The shared, networked navigation request. Absolute slots are 1-based; with a `groupId`, `index` is 0-based inside that Group. Refuses a new request while one is pending. |

Under the hood it listens to `dp.on_level_loaded` / `dp.on_level_failed`, and keeps players in sync
with `net_send('sequence_state', {slot, revision, writer})` plus a `sequence_sync_request` whenever the
relay (re)connects — newest revision wins, ties broken by player id. That's why buttons call the
**manager's** `load_level`, not the raw `dp.load_level`: the raw call is local and unsynced.

### Customizing it

Replace or extend those functions — the buttons and portals don't care how you decide.

**Random order, no repeats:**

```lua
local played = {}
function next_level()
    local pool = {}
    for slot = 2, dp.level_count() - 1 do        -- attractions only
        if not played[slot] then pool[#pool + 1] = slot end
    end
    if #pool == 0 then return load_level(dp.level_count()) end   -- Game Over
    local pick = pool[math.random(#pool)]
    played[pick] = true
    return load_level(pick)
end
```

**A hub with Groups:**

```lua
-- dp.groups() → { {id=, name=, sourceId=, count=}, ... } in package order
function enter_world(name)
    for _, g in ipairs(dp.groups()) do
        if g.name == name then return load_level(0, g.id) end   -- first level of that Group
    end
    return false
end
```

**Score and progress.** Keep run state here, where it survives level changes. The manager isn't inside
an attraction, so the injected `storage` variable is a no-op there — use game scope explicitly:

```lua
local save = dp.storage.game('YourGame')
local score = 0
function add_score(n) score = score + n end
function on_game_over() save.max('high_score', score) end
```

Attractions reach it with `dp.game_manager().add_score(10)`. Remember that local variables like
`score` are per-headset unless you network them — see `Samples/Multiplayer/MULTIPLAYER.md`.

### Game Manager vs Player vs Attraction

| Put it on… | When it is… |
|---|---|
| **Game Manager** | Title-wide rules and shared state: progression, routing, score for the run, randomizers, hubs, quest flags. Needs to be networked or needs to outlive every attraction. |
| **Player** | About *this guest's body and senses*: hand-held tools, damage screens, wrist-adjacent effects, per-guest audio, what other players see of you. Also: anything that must work when your attractions are placed loose (no package → no Game Manager). |
| **Attraction** | Everything that belongs to one experience. It reaches up with `dp.game_manager()`. |

---

## The Player rig

`Assets/Content/{YourGame}/Prefabs/Player.prefab` is the guest's body in your game. The template's
rig looks like this:

```
Player                      PlayerRig (gameId stamped by the SDK)
├── Head                    HeadTracker — follows the headset; carries a collider
├── Body                    BodyTracker — a torso collider below the head (yOffset 0.75 m)
├── Feet                    FeetTracker — foot collider that can kick things
├── LeftHand / RightHand    HandTracker — hand-tracked colliders
│   └── … Handheld Pivot    attach blasters, wands, tools here
├── BothHands               HandTracker — whichever hand is in use
└── DamageEffect, LavaScreen, CrackedScreen, GlassShatter …   example effects (safe to delete)
```

**How it's loaded.**

- One rig per title. `PlayerRig` registers under its `gameId`; a duplicate destroys itself.
- **In a package** the rig is created once, **before** any attraction gameplay, outside every
  calibrated attraction, and survives every level swap. Switching between your Sequence and Adventure
  re-uses the live rig.
- **Placed loose**, the rig is spawned by the first of your attractions to load.
- Only the rig of the game whose zone the guest is standing in is shown; walking into another
  developer's attraction swaps to theirs.

**Rules.**

- Keep it named **`Player.prefab`** with a `PlayerRig` on the root, and keep one headset rig per content
  folder — the compiler picks the first `PlayerRig` prefab, preferring the one named *Player* and never
  the Phone Player (below). The uploader's
  PLAYER card says **Player Required** until one exists.
- Never put the rig inside an attraction or a special level.
- Don't find parts by name or tag. Use `dp.player()`, `dp.head()`, `dp.hand(side)`, `dp.hands()`,
  `dp.is_player(collider)`, and `dp.head_park()` for anything sent over the network.
- **What other players see:** add a child named **`RemoteRig`**. Its contents are hidden on your own
  headset and cloned once per remote player; children named `Head`, `LeftHand`, `RightHand` and `Hand`
  follow that player. Lua inside runs only on the clones, with `peer_id` injected.
- `BodyTracker.anchorOverride` exists for the iOS body-tracking path; leave it empty.

### Phone players: the optional Phone Player slot

Guests can also join on a **phone** (an AR window onto the park, touch input, no hands). Every package
— Arena, Sequence and Adventure — shares **one optional Phone Player slot**, exactly like Player:

- **Empty (the default):** phone users get **no rig**. They watch, they still get the Game Manager, and
  `dp.player()` is `nil` for them. This is why title-wide systems belong on the **Game Manager**, not
  on Player.
- **Set:** phones spawn that `PlayerRig` prefab **instead of** Player. Headsets always use Player.

The template ships a ready-made rig, **`Prefabs/Phone Player.prefab`**, but leaves the slot empty:

```
Phone Player                PlayerRig (gameId stamped by the SDK)
├── Head / Body / Feet      HeadTracker (follows the phone) / BodyTracker / FeetTracker — no hands
├── Phone Controls          LuaBehaviour → Scripts/phone-player.lua.txt (dp.is_phone, dp.on_tap)
├── Tap Ripple              authored ParticleSystem, stopped; moved to the tap point and played
└── RemoteRig/Head          a phone slab: what headset players see of a phone user
```

To opt in, open **Content Uploader → Packages → PHONE PLAYER (optional)** and drop
`Prefabs/Phone Player.prefab` into the field, or press **Use template Phone Player**. The choice is
saved in `.dreampark-phone-player.json` (commit it). The slot must hold a prefab with a `PlayerRig` on
its root inside this content folder, or the compile fails with a message saying so. A prefab in the
slot, or any prefab named *Phone Player*, is never picked as the headset Player.

On the phone, input is touch, not hands. Scripts ask `dp.device()` (`"phone"`, `"headset"` or
`"editor"`), `dp.is_phone()` and `dp.is_headset()`, and receive gestures through `ontap(t)` /
`ontouch(t)` on the tapped object or `dp.on_tap` / `dp.on_touch` / `dp.on_drag` / `dp.on_pinch`
anywhere (see the SDK's `CLAUDE.md` → *Phone players & gestures*). To try it at a desk, turn on
**DreamPark → Simulate Phone Player** and press Play: the Editor reports `"phone"`, the Park
Simulator spawns the Phone Player (or no rig when the slot is empty), and the mouse taps, drags and
scroll-pinches in the Game view.

---

## Lua API

Every function resolves **the package this script belongs to** — walking up from the calling object
(Sequence) or through its package membership (Adventure / Arena occurrences, the Player and the Game
Manager). There is no global "current package"; a park can hold several titles.

| Call | Returns / does |
|---|---|
| `dp.package()` | The package root GameObject, or `nil` outside a package |
| `dp.game_manager()` | The Game Manager's Lua scope (call its functions), or `nil` |
| `dp.game_manager_object()` | The Game Manager GameObject |
| `dp.container()`, `dp.container_object()` | Older aliases of the two above |
| `dp.is_arena()`, `dp.is_sequence()`, `dp.is_adventure()` | Which format is running |
| `dp.level_count([groupId])` | Number of slots (Sequence: attractions + 2), or levels in a Group |
| `dp.current_level_index()` | Current 1-based slot |
| `dp.levels([groupId])` | 1-based display names in package order |
| `dp.groups()` | Sequence Groups: `{ id, name, sourceId, count }` in package order |
| `dp.level_slot(index, groupId)` | 0-based index inside a Group → absolute slot (0 if invalid) |
| `dp.load_level(slot[, groupId])` | **Raw, local** level request (async). Prefer `dp.game_manager().load_level`. |
| `dp.preload_level(slot[, groupId])` | Sequence only: download a stage before it's needed |
| `dp.level_ready(slot[, groupId])` | Sequence only: is that stage resident? |
| `dp.level_loading()` | A change is in progress |
| `dp.level_error()` | Why the last change failed, or `nil` |
| `dp.on_level_loaded(fn)` / `dp.off_level_loaded(fn)` | `fn(slot)` when a slot finishes activating (in a Sequence, one drawn frame after the level appears) |
| `dp.on_level_presented(fn)` / `dp.off_level_presented(fn)` | `fn(slot)` in the same frame the level becomes visible, before it is drawn — for changes the guest must never see happen (Oct 2026). Arena/Adventure: just before `on_level_loaded`. |
| `dp.on_level_failed(fn)` / `dp.off_level_failed(fn)` | `fn(slot)` when it can't |
| `dp.on_occurrence_removed(fn)` / `dp.off_occurrence_removed(h or fn)` | `fn({occurrence_id, resource, anchor})` when ONE attraction of this package is removed while the title stays (Arena "No Attraction", a swap, a repack retiring a rect) — fired BEFORE the anchor is destroyed; stop its bots/sounds, drop props it handed out. Drops with the script. Still clean up in `ondisable`/`ondestroy`, and call it guarded: `if dp.on_occurrence_removed then dp.on_occurrence_removed(fn) end` (Oct 11 2026). |
| `dp.show_overlay(bool)` | Show/hide the Sequence Overlay Level |
| `dp.overlay_auto_visibility(bool)` | `false` = you own Overlay visibility |
| `dp.next_level()`, `dp.previous_level()` | Ask the Game Manager to advance / go back (returns `false` if it can't) |
| `dp.button_press()` | For control scripts: plays the press feedback and returns `false` during its cooldown |

`dp.level_error()` values: `invalid-or-inactive-slot`, `missing-loader`, `download-or-load-failed`,
`load-exception`, `room-fit-failed`, `spawn-exception`, `spawn-failed`, `presentation-failed`.

**Always unsubscribe.** A handler registered with `on_level_loaded` is held by the package; remove it
in `ondestroy()`:

```lua
local function on_loaded(slot) print('now on ' .. tostring(dp.levels()[slot])) end
function onready()   dp.on_level_loaded(on_loaded) end
function ondestroy() dp.off_level_loaded(on_loaded) end
```

**Layout (any script inside an attraction).** Define `onlayout(frame_id, scale, footprint)`; it is
called once after `start()` and again whenever the app re-fits the attraction: `frame_id` is `'base'`,
`'minimum'` or a custom frame id, `scale` the frame scale and `footprint` the runtime footprint in metres
(both `Vector2`, x/z). Debug overlays that draw the layout stay out of the bake if their object's name
contains `[nopack]` or their script has a `-- @packing-ignore` comment.

**Walls (any script inside an attraction).** Define `onwalls(walls)`; it is called once after `start()`
and again whenever the walled sides change. `walls` is a comma list like `"front,left"` (`""` = no
walls). The attraction also answers `attraction:HasWallNamed("front")` and `attraction.WallsWire`.
Wall layers already switch your objects on and off for you — use this only for behaviour, and never
make progress depend on a wall:

```lua
function onwalls(walls)
  bonus_targets_enabled = walls:find("front") ~= nil
end
```

**Rect edges (any script inside an attraction).** Every side of the seated rect
(`front`/`back`/`right`/`left`, the attraction's own frame) is a list of spans of `wall` (a real
wall), `opening` (a RoomPlan door/opening — a sure place for a track — or `adjacent`, where a
neighbouring rect touches with no confirmed wall) or `unknown` (neither; the default, passable).
`dp.rect_openings()` returns per side `spans`, `openings` (door / opening / adjacent),
`passages` (everything not a wall), `links` (stretches a neighbouring rect shares, with `to`),
`walled`, `full_wall`, `wall_height`; `dp.rect_spans(side)` the raw spans, `dp.rect_span_at(side, t)` one;
`dp.rect_is_wall / rect_is_open / rect_is_opening / rect_is_unknown(side, t)` (`t` = metres from the
side's left corner seen from inside); `dp.rect_is_open_area()` (no wall and no RoomPlan doorway on any side —
enter from any side); `dp.rect_point(side, t)`; define `on_rect_changed(o)` to rebuild when the park changes.
With no park data every side is unknown — your attraction must work that way. See
`RECT_EDGES_SPEC.md`.

The C# counterpart is `DreamParkPackageHost` (`LoadLevel`, `PreloadLevel`, `IsLevelReady`,
`ShowOverlay`, `SetOverlayAutoVisibility`, events `LevelActivated` / `LevelChangeFailed`). Gameplay
belongs in Lua.

---

## Files on disk

```
Assets/Content/{YourGame}/
├── .dreampark-library.json          Park Assets library: Groups, hidden items
├── .dreampark-arena.json            Arena: size buckets (priority order), exclusions, size overrides
├── .dreampark-dream-sequence.json   Sequence: order
├── .dreampark-sequence.json         Adventure: Start/End + order   (historical name — it IS Adventure)
├── DreamPark Package Manifest.asset GENERATED runtime recipe for all three (don't edit)
├── DreamSequence/
│   ├── Start Level.prefab           yours
│   ├── Overlay Level.prefab         yours
│   ├── Game Over Level.prefab       yours
│   └── Dream Sequence Package.asset GENERATED Sequence recipe (don't edit)
├── Prefabs/
│   ├── Game Container.prefab        the Game Manager — yours
│   ├── Player.prefab                the Player rig — yours
│   └── Phone Player.prefab          the optional phone rig — yours (used only when the slot is set)
├── .dreampark-phone-player.json     Phone Player slot (absent = empty = phones watch)
└── Scripts/game-container.lua.txt   the Game Manager's Lua — yours
```

Commit all of it. The JSON files are the authoring; the generated assets are rebuilt on Test/upload.

---

## Checks & testing

- **`dream-sequence-required`** (blocking) — the title has at least one playable package ("Title has
  no playable package" otherwise). If you authored a Sequence, it must also be valid: the recipe is
  compiled and current; Start, Overlay, Game Over and Game Manager exist and are baked; no required
  attraction is too big. Fix: *Rebuild Sequence Package*. With no Sequence authored and an Arena or
  Adventure present, the check is clean.
- **`package-references`** (blocking) — every placement in an authored Sequence or a complete
  Adventure still resolves to an Attraction or Prop in this content folder. Deleted prefabs are kept visible in the organizer so you
  can replace or remove them.
- **Test before upload.** Each tab's **▶ Test … in Park Simulator** runs that package in the editor:
  Arena previews the first-priority entry of your largest occupied size; Sequence is assembled from
  its editable parts on the basketball-court blacktop; Adventure plays as an ordered walking route
  through the simulated park.
