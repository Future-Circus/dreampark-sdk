# Routes: walking between attractions (Phase 1, Oct 2026)

A park is many attractions in many rects (rooms, halls, a yard). Routes tells your Lua how
they connect through **real doorways**, and plans walks and tours across them. It is a Lua
library, `dp.*` only. There are no components to add, and the data stays on `AttractionTemplate`.

- **Deterministic.** Same park data gives the same answer on every headset, with no messages.
- **Works without data.** With no park data (SDK Editor, an unseated attraction) every side is
  `unknown` (passable, RECT_EDGES_SPEC.md) and routes are straight lines. With a simulated rect (Park Simulator → Arena Rect) you get
  the real thing in the Editor.
- **Phones too.** The planning is pure Lua.

## What is around me

```lua
local ports = dp.ports()        -- this attraction's Entries & Exits for the APPLIED layout frame
-- each port: { kind = 'entry'|'exit'|'both', entry, exit, position (0..1), side ('front'…),
--   local, pos, fwd (walk direction, world), outward, width, tag, t (m along the rect side),
--   gap (m from footprint edge to rect edge), blocked (true = it faces a wall),
--   opening = { type, to, a, b, width, mid, narrow } | nil }
local near = dp.neighbors()     -- one row per passable stretch that leads into another rect:
-- { rect_id, from_rect, side, type, a, b, width, mid (world), attraction (GameObject|nil),
--   attractions = {GameObjects}, game_id }
local r = dp.rect()             -- my seated rect in world metres: { id, center, width, depth, yaw,
--   has_data, source = 'park'|'preview'|'default', opens = { {ax,az,bx,bz,nx,nz,type,kind,to,side,a,b,width} },
--   links = { {ax,az,bx,bz,nx,nz,kind,to,side,a,b,width} }, spans = {...}, open_area }
```

Rect edges are spans of `wall` / `opening` / `unknown` (RECT_EDGES_SPEC.md, v3): `opens` (and
`dp.ports`' `opening`) are the PASSABLE stretches — doors, openings, adjacent-rect openings and
unknown (`type` `door`/`opening`/`adjacent`/`unknown`, `kind`); only walls block. `dp.rect()` / `dp.park_rects()` rect tables
also carry `links` (stretches a neighbouring rect shares, any `kind`, with `to`), `spans` and
`open_area` (no wall and no RoomPlan doorway on any side: the rect connects from any side, and
routes join open-area rects with a straight walk).

`dp.rect_openings()` also carries `entry_side` (the side guests arrive from:
the app's Arena entry, else your front = first Entry portal), `entry_reason`, `source`, and per side
`wall_height` and `walled` (a wall layer applies there).

Portals gained `Both` (a doorway used either way), an optional `width` (the clear width it needs;
a narrower opening reads `narrow`/`blocked`) and a `tag` ("track", "hall"…). Author them in the
inspector's *Entries & Exits* section. The catalog still receives entry/exit pairs only.

## Walks

```lua
local r = dp.route(a, b[, opts])   -- a/b: Vector3 | GameObject | port | stop
-- r = { ok, pts = {Vector3}, len, hops, doors = { {pos, width, type, from, to} }, source, why }
local pos, fwd = r.at(s)           -- the point s metres along
local s, off = r.project(somePos)  -- arc length of the nearest point, and how far off it is
local marks = r.arrows(0.8)        -- floor-arrow poses every 0.8 m
```
- `source = 'doors'`: straight lines inside rects (scan rects are free floor), joined at door
  midpoints. On a headset the app hands Routes the WHOLE park graph — every scanned rect with its
  openings, including empty halls (`dp.park_rects()`). Without it (SDK Editor) only the rects of
  placed attractions are known, and a walk through two empty rooms in a row is `ok = false`.
- `'grid'`: you passed `opts.world = { cell_blocked = fn(x, z) }` (your own sensing) and some stop
  has no rect data.
- `'straight'`: no park data, or an end outside every known rect (`why` says which).
- `ok = false`: the rects are known but no doorway joins them (an unscanned door). Check `why`.

## Tours

```lua
local t = dp.tour({ goA, goB, goC }, { loop = true, spur_penalty = 6 })
-- t = { ok, why, order = {stops}, roots = {GameObjects}, len (m walked between stops), cost,
--       ends = { [i] = { role, spur, entry = {pos, fwd, width, to, type}, exit = {…} } },
--       links = { route… (from_root, to_root, closing) }, dec, stops }
```
A tour chooses the order and, for each stop, which doorway to come in by and leave by. It minimises
the walk, including the walk back to the start when `loop`. Options:

| option | default | meaning |
|---|---|---|
| `loop` | true | close the tour (false = an open chain: Start → … → End) |
| `keep_order` | false | keep your list order and only choose the doors (an authored route, a patrol) |
| `start` | elected | a GameObject or stop id to start at (else the most central stop) |
| `spur_penalty` | 6 | metres added when a stop is entered and left through the same opening (a dead-end room) |
| `max_exact` | 5 | exact order search up to this many stops; greedy + 2-opt above |
| `world` | nil | `{cell_blocked = fn(x,z), seg_blocked = fn(ax,az,bx,bz)}` for stops without rect data |
| `links` | true | also compute each leg's walk |

Hard caps: 10 stops per tour, 64 candidate door points, a walking
grid of at most 30 m and 20k cells. Above them `ok = false` and `why` says which cap. Split big
parks first: `dp.route_clusters(stops, gap)` groups stops into venues.

For walk routes, race lines and patrol loops:
```lua
-- a guide that walks the guest to the next stop
local r = dp.route(dp.head(), nextStop)
-- a race over every piece of my title in this venue, through the doors
local t = dp.tour(myPieces, { loop = true })
-- a patrol in authored order
local p = dp.tour(rooms, { keep_order = true, loop = true })
```

## Agreeing on a plan (optional)

A tour is a pure function of park data, so headsets agree without messages. Use a published
plan when your inputs differ per headset (your own sensing, a player's choice):

```lua
local plan = dp.route_plan('race')        -- built on dp.shared_state('race')
if dp.is_leader() then plan.publish(t) end
plan.on_change(function(p) local mine = p.get(myPieces) if mine then build(mine) end end)
```
The plan is park-local and rounded to cm (≤ 1 KB for 10 stops). `get` refuses a plan whose stops
moved (`nil, why`); keep your own plan until the leader re-plans.

## The park graph

`dp.park_rects()` lists every rect the app knows: `{id, cx, cz, yaw, W, L, rect = {id, opens}}` (world).
The app fills it from the live space map (`DreamParkRoutes.ParkGraph`, refreshed when the map changes).
Editor tools and tests can set `DreamParkRoutes.ParkGraph` to a simulated park. `dp.route` / `dp.tour` use it automatically (`park_graph = false` opts out).

## The planner itself

`dp.routes` is the pure library: `search`, `links`, `plan`, `door_router`, `route`, `clusters`,
`grid`/`flood`/`trace`, `resample`/`at`/`project`/`link_arrows`, and `encode`/`decode`/`blob`. Planner
hooks (`prepare`, `elect`, `ends`) let a title keep its own rules (for example its track kinds, its start
election and its lane split).
Source: `Tools/routes-offline/dp_routes.lua`, tested by `lua Tools/routes-offline/test_routes.lua`.

## Not yet (Phase 2)
- A C# route service backed by the app's park graph (all rects, not just the seated ones).
- NavMesh links through openings, and agents that follow routes (`dp.follow_route`).
- Adventure placement by route: each stage's Entry seated on the opening toward the previous Exit.
