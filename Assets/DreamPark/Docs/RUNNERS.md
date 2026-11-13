# Ways to play (runner scripts)

A title ships one or more **ways to play**. Each one is a small Lua **director** script. The phone picks the title and, when there is more than one, the way to play. Everything after that happens in the headset, decided by your director: which level comes next, forks, votes, loops, the boss.

The platform is the stage crew. It runs the session (timer, results, Thanks), places your levels on the board, dresses the edges between tiles, syncs every headset, and keeps going if the leader's headset leaves. The director runs on the session **leader** only.

Sample: `Assets/DreamPark/Samples/WaysToPlay/` (5 rooms, two ways to play). Try it in **DreamPark ▸ Park Simulator ▸ Ways to Play…**.

## 1. Where they live

```
Assets/Content/<Title>/Runners/<id>.lua.txt      one file per way to play
```

- `<id>` is the file name, lower-cased (`PvP Only.lua.txt` → `pvp-only`). It is the id the phone and the server use.
- Use `.lua.txt`. A bare `.lua` file is not imported, so it never ships; the pre-upload check warns about it.
- A title with no `Runners` folder still plays. The built-in default runs its levels in order, one per tile.

## 2. The header

The header comes first in the file:

```lua
DreamPark.runner {
  name    = "PvP only",                    -- what the phone shows (1–60 characters)
  blurb   = "Team battles, round after round.",  -- one line (≤ 200 characters)
  cover   = "Art/Runners/pvp.png",         -- relative to Assets/Content/<Title>/ (png or jpg, optional)
  default = true,                          -- exactly one way to play is the default
}
```

At upload the SDK runs each file in a throwaway sandbox and reads the header. File-scope calls such as `dp.flow.onLevelComplete(...)` are harmless there, and nothing else runs. It then uploads the covers and sends the headers with the release. You never write JSON. If no runner says `default = true`, the first by id is the default. If two do, the upload is blocked.

## 3. A director

```lua
DreamPark.runner { name = "Adventure", blurb = "Three rooms, a vote, then the boss.", default = true }

local WORLD = { "Garden", "Workshop", "Library" }

function onSessionStart(board)                -- board = {tiles, widthFt, depthFt, minutes, cols, rows}
  dp.flow.state.next = 2
  dp.flow.load(WORLD[1], { tile = 1 })
end

function onResume(state)                      -- a NEW leader took over: state = board + tiles + dp.flow.state
end

dp.flow.onLevelComplete(function(level, info) -- info = {tile, tiles, result, scores, load}
  local nxt = WORLD[dp.flow.state.next]
  if nxt then
    dp.flow.state.next = dp.flow.state.next + 1
    dp.flow.load(nxt, { tile = info.tile })   -- seamless: no screen between levels
  else
    dp.flow.transform("GreatHall")            -- whole board, always gets the Next-up reveal
  end
end)
```

**Keep what matters in `dp.flow.state`, never in locals.** When the leader's headset leaves, another headset becomes leader. It starts your script fresh and calls `onResume(state)`. Only `dp.flow.state` (synced as JSON, ≤ 3 KB) survives the handover. Callbacks passed to `choose` do not survive either: use `dp.flow.onChoice` with a `tag`.

Levels are named by their prefab name, slug or display name (`"GreatHall"`, `"great-hall"`, `"Great Hall"`).

## 4. The board and edges

A park is a board of 12 × 18 ft **tiles**: 12 × 18 = 1 tile, 24 × 18 = 2, 24 × 36 = 4. Tiles are numbered from 1. The front row is at the portal, and tiles count left to right, then the back row:

```
  back   [3][4]
  front  [1][2]      ▼ portal
```

A level's footprint (1, 2 or 4 tiles) comes from its size. A bigger level shrinks into a smaller board through its layout frames, so give 2- and 4-tile levels a layout frame that fits 12 × 18 ft.

**Edge Sockets** (`DreamPark/Ways to Play/Edge Socket`) dress a level's sides:

- Put one per side. The GameObject sits on the edge midpoint with its +Z pointing out of the level. Sides are level-local: **front = −Z** (toward the portal), back = +Z, right = +X, left = −X.
- **Closed** look: shown when the side stands alone (a wall, a hedge). Every level must play fully closed; that is how it spawns.
- **Open** look: the doorway. **Named looks** (`gate`, `path`, …) are what `dp.edges.open(tile, side, "gate")` asks for. **Locked** look (optional): the gate held shut; otherwise Closed is shown.
- `sockets` are the doorway centres in feet from the edge midpoint (default one, centred). Keep the standard 4 ft width so two neighbours' doorways line up.
- GameObject ▸ DreamPark ▸ **Add Edge Sockets (4 sides)** creates all four sockets with placeholder looks. The inspector previews each look.
- **Edge Link**: a prefab that straddles the seam between two specific levels (an arch, a bridge). Set `levelA`, `levelB` and `sideOfA`. Put it under `Content/<Title>/` (its address is `<Title>/<prefab name>`). The director spawns it with `dp.edges.link{a = "Garden", b = "Workshop", link = "GardenWorkshopArch"}` (or `dp.edges.link(tile, side, link)`): it stands on the seam on every headset, its +Z pointing out of level A, and both sockets of the seam open. It leaves when either level leaves the board, or with `dp.edges.unlink(tile, side)`.

The platform decides what each edge shows. Outside the board = closed. Inside one level that spans both tiles = open. Otherwise the director's look from `dp.edges.open` / `dp.edges.lock`, and closed by default. A seam is one edge: both levels' sockets on it always show the same look. A look you set **stays** when either side gets a new level (close it with `dp.edges.close(tile, side)`); it goes only when one level comes to span both tiles. The open look an Edge Link gave the seam leaves with the link.

**Turned levels.** A level authored the other way round from its tile (18 × 12 ft in a 12 × 18 ft tile) is turned a quarter so it fits. Its front (portal side) then faces the board's outside wall: left in the left column, right in the right column. Its Edge Sockets turn with it, so `dp.edges` still names the BOARD's sides.

### Levels only a runner loads (start areas, maps, forks)

Put them under `Assets/Content/<Title>/Runners/` (for example `Runners/Levels/A_FloorZones.prefab`, an AttractionTemplate like any level). The uploader:

- gives them the address `<Title>/Runners/Levels/<prefab name>`, never `<Title>/Levels/…`, so today's app and the live attraction catalog never offer them as placeable attractions (the catalog may file one under the developer portal's legacy levels, hidden from the app);
- keeps them out of the Arena, Sequence, Adventure and playlists;
- ships them in the manifest's `runnerLevels` list, with the Sequence's **Start Level** and **Game Over Level**.

So `dp.flow.load("A_FloorZones")`, `dp.flow.load("Start Level")` and `dp.flow.load("Game Over Level")` all work, and `dp.flow.levels()` lists them after the package levels. `dp.flow.load("<Title>/Runners/Levels/A_Map")` (any full address) loads that address even when no list names it (1 tile).

## 5. API (director only, unless noted)

| Call / event | What it does |
| --- | --- |
| `onSessionStart(board)` | The session starts. `board = {tiles, widthFt, depthFt, minutes, cols, rows, startsInSec}` (`startsInSec`: seconds of pile-in left, 0 = the clock runs, -1 = unknown). |
| `dp.session.onClockStart(fn)` / `dp.session.startsIn()` | The shared clock starts when the 30 s pile-in ends. Load a lobby / start area in `onSessionStart` and the first real level here. Fires once per session (never again after a leader handover), and at once when there is no pile-in (an older server, the editor). |
| `dp.lobby.total()` / `left()` / `over()` / `onEnd(fn)` | The Location's "Waiting for players" wait, in director and gameplay Lua: seconds set (0 = none), seconds left (on the server's clock, so headsets agree within a few seconds), whether it is over, and a trigger when it ends (once; at once when already over; never again after a leader handover; also runs if the session ends during the wait). Want a lobby? Load it in `onSessionStart` when `left() > 0`, and register `onEnd` at the top level of the script, not inside `onSessionStart`: a new leader runs `onResume`, never `onSessionStart`, so a handler registered there is lost if the leader leaves mid-wait (`Samples/WaysToPlay/Runners/lobby.lua.txt`). Older apps have no `dp.lobby`: guard directors and gameplay Lua with `if dp.lobby then`, or check `dp.flow.features.lobby`. `left()` can rise once if a provisional offline session is replaced by the server's. |
| `onResume(state)` | A new leader took over. `state` = the board, its tiles, and `state` = `dp.flow.state`. |
| `dp.flow.state` | Your table that survives a leader handover (JSON, ≤ 3 KB). |
| `dp.flow.levels()` / `dp.flow.board()` | The title's levels `{id, name, tiles}` in release order / the board now. |
| `dp.flow.load(level, {tile=n \| span="all", nextUp={title, subtitle} \| nextUp=true})` | Put a level in a tile or across the board. Seamless unless you ask for `nextUp` (`true` = "Next up: <the level's name>"). `level` = id, name or full address. |
| `dp.flow.swapWhenClear(tile, level)` | Swap once the tile's level is done **and** nobody stands in it (out of sight). |
| `dp.flow.transform(level, {nextUp=…})` | Whole board, once every tile's level is done. Always gets the Next-up reveal. |
| `dp.flow.endLevel(levelOrTile, result)` | The director ends a level. Fires `onLevelComplete`. |
| `dp.level.complete{result, scores, data, go}` | **Any title Lua** (gameplay): this level is done (goal met, boss down, round timer out). `result` any string ≤ 200 chars, `data` a small table (≤ 1 KB) → `info.result` / `info.data`. |
| `dp.flow.onLevelComplete(fn(level, info))` | `info = {tile, tiles, result, scores, data, load, zone}`. Load nothing and the group stays in free play. |
| `dp.flow.onTileEmpty(fn(tile))`, `dp.flow.onPlayerEnterTile(fn(tile, player))` | Presence events. |
| `dp.flow.closeVote([option])` | Close the open vote now: on `option` (e.g. "the first one in picks"), or on the zones as they stand. |
| `dp.flow.onPlayerJoin(fn(player))` / `dp.flow.onPlayerLeave(fn(player))` | A headset joined / left the session. |
| `dp.flow.onRequest(fn(player, kind, data))` | Gameplay on any headset called `dp.board.request(kind, data)` (≤ 1 KB): e.g. "the group unlocked these worlds", "go to this place". |
| `dp.flow.load(level, {options = {...}})` | Per-load options (≤ 1 KB): the level's gameplay reads them with `dp.level.options(self.gameObject)` (Bowling 5 frames, Golf from hole 10). |
| `dp.flow.features` | `{dwell, tiebreak, options, nextUpTrue, zones, clock, addresses, override, closeVote, labels, panel, outline, prompt, descriptions, grid}` = true on this runtime. Check it in a director that may run on an older build. |
| `dp.flow.choose{options, labels, descriptions, prompt, zones, seconds, default, tag, tiebreak, dwell, panel, outline}` | **Say what the vote is for** with `prompt` (≤ 60 characters, e.g. `"Pick the next game"`; else "Pick one"). **Options have an id, a label and a description:** `options = { {id = "pitrun", label = "Pit Run", description = "Jump the pits before the floor drops"}, … }` (or plain ids plus `labels = {pitrun = "Pit Run"}` / `descriptions = {…}`; a description ≤ 120 characters). The floor zones, the panel and the phone show the label (the id when there is none); `onChoice`, the callback, `default`, `closeVote` and `dp.board.pick` use the id. An id-looking option with no label (lower case, no spaces) is a pre-upload warning and a console warning. **The platform draws the vote on the play area** (in the content-loading effect's light): the board's levels bounce out and wait hidden (their Lua sleeps), each zone is a band of violet light on the floor with its label and description lying flat inside it, and a header above the grid shows the prompt and the countdown. The zone a player stands in lights up white-hot with glints and rising motes and the others gray out (on that player's headset); the winner turns gold with the attraction flourish burst and the rest go. When the vote is gone the levels (the one `onChoice` loaded, or the old ones if it loaded nothing) come in with the spawn effect. **No `zones`:** the platform splits the board into a grid, one rectangular cell per option (1 to 20+; as many options as tiles = the tiles). `panel = false`: nothing drawn and your level stays, because your title draws its own vote from `dp.board.vote()`; the sync, dwell, tie-break, leader handover and phone voting all still run. `outline = false`: you draw the zones, and the platform shows a floating panel (prompt, labels, countdown) instead. `dwell = true` (1 s) or seconds: a floor-zone pick. The countdown starts only once someone has stood in a zone that long, waits again when everyone steps out, and the vote closes early once every player stands in a zone. A phone pick (or `dp.board.pick(option)` from gameplay) closes the open vote with that option (`info.override = true`). The synced vote. Players stand in zones, a countdown runs, the most votes win. Nobody voted → `default`. A tie → `tiebreak`: a function `fn(tiedOptions) → option` (runs on the leader; lost on a handover, then `'default'`), or `'random'` (one of the tied, the same on every headset), `'first'`, `'last'`, `'default'` (the default option; also when unset). `zones`: tile numbers, `{tile=n}`, circles `{x, z, r}` or rectangles `{x, z, w, d}` (metres, board frame, +X right, +Z into the park). |
| `dp.flow.onChoice(fn(winner, tag, info))` | The vote result, `info = {counts = {[option] = n}, votes, tied}`. Also reaches a new leader. Prefer it over a callback (which gets the same arguments). |

**One vote at a time.** A `choose` while another vote is still open does not replace it: it waits, and opens with its own full countdown once the open vote has closed and its winner has been shown. Every waiting vote's callback and `onChoice` fire, in order. Up to 4 may wait; a 5th waiting `choose` returns `nil` and logs an error. A `choose` made inside `onChoice` waits until the winner has been shown (2.5 s), then opens.
| `dp.edges.open(tile, side, look)` / `dp.edges.close(tile, side)` / `dp.edges.lock(tile, side, on)` | Dress a tile edge (`"open"`, `"doorway"`, `"gate"` …) / close it again / hold a gate shut. Both sides of the seam follow. |
| `dp.edges.link{a, b, link}` / `dp.edges.link(tile, side, link)` / `dp.edges.unlink(tile, side)` | Spawn an Edge Link prefab on the seam between two neighbouring levels (both sockets open) / remove it. |
| `dp.session.timeLeft()` / `dp.session.onEnding(fn [, seconds=60])` | Land the finale in time. |

### Zones: players in different levels at once

A **zone** is a group of players who see their own level on the same physical tiles while everyone else stays on the main board (for example: a hub with the region map on tile 1, a cave for up to 4 behind a door on tile 2, a 1-v-CPU trainer arena). Each headset renders only its own zone's levels, plus the main board's levels in tiles its zone has nothing in (so the hub / map stays in view) unless the zone was opened `alone = true`.

| Call / event (director) | What it does |
| --- | --- |
| `dp.zones.open(id, {level, tile \| span="all", capacity, name, locked, alone})` | Open (or update) a zone and load its level. `capacity` 0 = no limit. A full zone (members ≥ capacity) is locked. |
| `dp.zones.load(id, level, {tile \| span})` | Change the zone's level; its players stay in it. |
| `dp.zones.close(id)` | Everyone in it goes back to the main board; its levels and doors go. |
| `dp.zones.send(player or {players}, id)` → n | Move players (`"main"` = back to the main board). A full / locked / unknown zone refuses them (`onRefused`). |
| `dp.zones.follow(player, friend)` → n | Send a player to the friend's zone. |
| `dp.zones.door(doorId, {zone, from="main", tile=n \| x, z, r, dwell=2})` / `dp.zones.removeDoor(doorId)` | A walk-in door: a player of zone `from` who stands in it `dwell` seconds goes to `zone` (`"main"` for an exit). |
| `dp.zones.lock(id, on)` | Lock / unlock a zone by hand. |
| `dp.zones.of(player)`, `dp.zones.members(id)`, `dp.zones.list()`, `dp.zones.players()` | Who is where. `list()` = `{id, name, level, capacity, count, full, locked}`; `players()` = `{id, name, zone, x, z, tile}`. |
| `dp.zones.onEnter(fn(zone, player))`, `onLeave(fn(zone, player, why))`, `onRefused(fn(zone, player, why))`, `onEmpty(fn(zone))` | `why`: `"sent"`, `"door <id>"`, `"closed"`, `"left"` (the headset left the session) / `"full"`, `"locked"`, `"unknown"`. |

- `dp.level.complete` inside a zone's level completes that level only: `onLevelComplete(level, info)` with `info.zone`.
- Every load is its own network scope: the same level open in two zones never shares objects or state. **Title-wide** state (your Game Manager, `dp.shared_state` opened outside a level) is still shared by everyone: prefix its names with `dp.zones.room(self.gameObject)`.
- `dp.is_leader()` is the session's leader, who may be in another zone and not have your zone's level at all. For things only a zone's own players run (its wild critters, its CPU trainer), drive them from the zone's own leader: `if dp.zones.isLeader(self.gameObject) then … end` (`dp.zones.leader(zone)` names it; the lowest live player id in the zone, the same on every headset).
- Gameplay Lua (any title, any headset) can read: `dp.zones.mine()` (`"main"` or the zone id), `dp.zones.of(player)`, `dp.zones.players()` (for the map's markers), `dp.zones.list()`, `dp.zones.peers([zone or go])`, `dp.zones.leader([zone or go])` / `dp.zones.isLeader([zone or go])`, `dp.zones.room(go)` (`"main"` or `"zone:<id>"` for the level `go` stands in).
- Feature check: `dp.zones ~= nil` (gameplay and director). Old apps have none of this.

### What gameplay Lua can read (any title, any headset)

| Call | What it gives |
| --- | --- |
| `dp.level.runner([go])` (= `dp.level.info`) | `nil` when no way to play runs on this headset; else `{id, name, content, tiles, level, load, span, zone, done, options, state}` for the load `go` stands in (`go` = a level object), or this headset's standing load (`go` = nil, your Game Manager or Player rig). `state` is a copy of the director's `dp.flow.state`: the director's way to pass small facts to gameplay ("stage 2 of 3"). |
| `dp.level.options([go])` | That load's options (`{}` when none). |
| `dp.level.board()` (= `dp.board.get()`) | The board as the director sees it: `{tiles, widthFt, depthFt, cols, rows, minutes, startsInSec, players, list = {{tile, level, done, players, load}}, zones?, state}` (state = the director's `dp.flow.state`) — e.g. to draw progress on a map. |
| `dp.board.vote()` | The open vote: `{id, tag, prompt, options, labels, descriptions, panel, zones = {{tile, x, z, w, d} or {x, z, w, d} or {x, z, r}}, secondsLeft, winner, winnerLabel}` (board metres; `options` / `winner` are ids, `labels` / `winnerLabel` what to show). The platform draws the zones on the floor unless the vote said `panel = false` or `outline = false`. `nil` when none. |
| `dp.board.toWorld(x, z)` | A board-frame point (metres) → a world `Vector3`. |
| `dp.board.pick(option)` / `dp.board.request(kind, data)` | Close the open vote on `option` / ask the director (`dp.flow.onRequest`). |

Feature check from gameplay: `if dp.level and dp.level.runner and dp.level.runner(self.gameObject) then … end` (nil on today's app and on a package level).

### The title's Player rig and Game Manager

On a runner board the headset stands the title's **Player** rig and **Game Manager** (`<Title>/Game Container`) exactly as a package does, once per session, outside every tile, so they survive every level change. The Game Manager gets a package host that never navigates (`dp.is_sequence()` is false), and every level the runner places belongs to that package, so level Lua finds its Game Manager as it does in an Arena.

**Screens.** Next up appears only when you ask for it (`nextUp`, usually at the end of a world) and on every `transform`. A vote is drawn on the floor: the zones with their labels and descriptions, the prompt and the countdown above them, the winner in gold (see `dp.flow.choose`). A floating panel shows it instead only when the vote has no zones or said `outline = false`; `panel = false` shows nothing, for a title that draws its own vote. The session end is always the platform's: final results, badges, then "Thanks for playing".

## 6. Try it without a headset

**DreamPark ▸ Park Simulator ▸ Ways to Play…** runs your way to play on the real director runtime with simulated headsets:

- Pick a title, a way to play, a board (1, 2 or 4 tiles) and how many players.
- The board, edges, players, panels and event log update live.
- **Show in scene** places your real level prefabs and switches their Edge Sockets.
- **Late join**, **Leader leaves** (mid-level) and **Finish** (`dp.level.complete`) buttons test the hard cases.
- The **Checks** tab shows the pre-upload findings. **Upload export (dry run)** shows the exact `runners` block the uploader will send.

## 7. Pre-upload checks

These run with the uploader's other checks (`WaysToPlayCheck`):

- **Blocking:** a runner whose header is missing or does not run; a header with no name; two defaults; more than 32 runners; two files that become the same id; an Edge Socket with no Open look; an edge a runner opens with a look the level does not have.
- **Warning:** a bare `.lua` file; a missing or unsupported cover; a socket with no Closed look; a socket facing the wrong way; a 2/4-tile level with no layout frame that fits 12 × 18 ft; a level name the title does not have.

## 8. Today's app (compatibility)

Your bundle must keep working on the headset app players have now, which knows nothing of runners:

- It never loads `Runners/*.lua.txt`, ignores `DreamParkPackageManifest.runners` / `runnerLevels`, and never places a runner-only level. Your packages (Sequence / Arena / Adventure / playlists) play exactly as before.
- Gameplay Lua: guard every new call, e.g. `if dp.level and dp.level.complete then dp.level.complete{...} end`, `if dp.zones then … end`. On today's app `dp.level` is nil.
- An **Edge Socket** on today's app is a missing script: it does nothing and every look stays exactly as saved in the prefab. Save prefabs with the looks the level shows today (new doorway / wall pieces inactive) — never add a wall to a finished level.
- Details and the checks behind this: the release round's `COMPAT.md`.
