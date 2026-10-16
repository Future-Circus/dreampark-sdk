# Rect Edges: walls, openings and unknown on every side of a rect

Status: v3, Oct 15 2026. Implemented in the DreamPark app and the SDK. This document replaces the v1/v2
rect-openings format, and §9 explains how to migrate.
Companion docs: `WALL_LAYERS_SPEC.md` (extras for walled sides) and `ROUTES.md` (walking between attractions).

Design rules: there are three kinds, `unknown`, `opening` and `wall`. The default is `unknown` (no wall activity,
no opening), so rects in the middle of an open area never get walls. Openings are where RoomPlan finds a door or
opening. Every side starts as one `unknown` span across its whole length and accumulates `opening`/`wall` sub-spans
as data comes in. Two rects touching at any point without a confirmed wall are an opening, and touching rects share
what they see: a wall one rect sees along a shared side is on both.

---

## 1. The algorithm: painting spans

Each of a rect's four sides starts as one `unknown` span. Evidence is then painted on top of it.
The app repaints the sides from all current evidence on every bake, so a later RoomPlan refinement can
correct an earlier guess.

| step | what is painted | rule |
|---|---|---|
| 1. init | `[0, length]` `unknown` | A side with no evidence keeps exactly this span. |
| 2. walls | `wall` | A RoomPlan wall within 3° of parallel whose line is on the side (±6 cm), or up to **40 cm beyond** it with no other wall or window in between. Nearest wall first. The span records `offset` (how far beyond) and `height`. A window paints as `wall` with `window: true`. |
| 3. doorways | `opening` (type `door` / `opening`) | A RoomPlan door or opening on the same line or up to 40 cm beyond, with no wall or window in between (its own wall is collinear, so it never counts as "between"), within 10° of parallel, and overlapping the side by **≥ 0.3 m**. It is painted over a wall. There is no minimum wall coverage, so a lone door at a corner counts. A smaller graze only clears the wall there. A doorway with no touching rect looks up to 0.6 m beyond it for the rect a step through it lands in, and that rect becomes its `touches`. |
| 4. adjacent | `opening` (type `adjacent`) + `touches` | Where a neighbouring rect (same storey) touches the side, every stretch that is still `unknown` becomes an opening of type `adjacent` touching that rect. A neighbour touches when either (a) it covers the line 5 cm beyond, or 20 cm for rects of the same room; or (b) one of its sides faces this one within **5°** of parallel, with the gap at **both** ends of the overlap between −10 cm (rects may overlap a little) and **40 cm**, and no wall or window standing in the gap. A confirmed wall is never overridden; a wall or doorway there only gets `touches`. After every rect is baked, each passable stretch is mirrored onto the facing side (with the same type), so both rects agree. |
| 5. reconcile | both rects of a touching pair | Once every rect is painted, each touching pair gets the **same** kind on every sub-stretch of the stretch they share, from what **either** rect saw, by precedence: RoomPlan door/opening > wall > adjacent opening > unknown. A pair touches when the sides face each other within 5° of parallel with the gap ≤ 40 cm, or when one already names the other via `touches`. So a wall one rect matched is a wall on both. Its `offset` is re-measured from the other edge (gap − offset). A door either rect saw is a door on both. Only where neither sees a wall or a doorway does the stretch become `adjacent` on both. `touches` names the partner. Repeated until nothing changes (chains of rects). Last, an `adjacent` opening that the partner side cannot hold, for example a rect touching only by a skewed corner, is dropped on both sides. |
| 6. build | — | Adjacent spans with equal attributes merge. An `opening` narrower than **0.3 m**, or any other span narrower than **0.15 m**, joins its wider neighbour (narrowest first). A RoomPlan door or opening never grows past what RoomPlan measured. A sliver next to one joins the wall or unknown neighbour, or stays as its own short span when a doorway or the side's end is all it touches. Boundaries that moved less than **3 cm** since the previous bake stay put, so the edges do not flicker while you scan. |

**Precedence (painting and reconciling): RoomPlan opening (door / opening) > wall > adjacent opening > unknown.**
Touching rects never disagree about the stretch they share. Nothing else
makes a wall or an opening. The capture walk and open floor beyond the edge do not count. Rects
never move.

### Worked example

A 4.0 m side. RoomPlan has:
- a wall 3 cm beyond it from −0.5 to 3.2 m (2.6 m tall);
- a door in that wall from 1.5 to 2.4 m (2.05 m tall);
- a window from 0.2 to 1.2 m.

Beyond the door, a hall rect lies 15 cm away. Past the wall's end, a neighbouring yard rect faces
this side 8 cm away from 3.2 to 4.0 m, 1° off parallel.

```
1. init        [0.00 ──────────────────────────────── 4.00] unknown
2. wall        [0.00 ─────────────────────────── 3.20] wall  offset 0.03  height 2.6     | 3.20–4.00 unknown
   window      [0.20 ── 1.20] wall window                  (keeps the wall's height and offset)
3. door        [1.50 ── 2.40] opening door  height 2.05 → touches "hall" (the 0.6 m look-up finds it)
4. adjacent    yard faces 3.20–4.00, nothing in the gap: unknown → opening adjacent ~yard
               (had the wall run on past 3.2 m, it would stay wall: a touch never overrides a wall)
5. reconcile   the hall's facing side gets the door; the yard's gets the adjacent opening
6. build       wall | wall window | wall | door ~hall | wall | adjacent ~yard
```

Stored (§2):

```json
{ "length": 4.0, "spans": [
  { "a": 0.0, "b": 0.2, "kind": "wall", "height": 2.6, "offset": 0.03 },
  { "a": 0.2, "b": 1.2, "kind": "wall", "height": 2.6, "offset": 0.03, "window": true },
  { "a": 1.2, "b": 1.5, "kind": "wall", "height": 2.6, "offset": 0.03 },
  { "a": 1.5, "b": 2.4, "kind": "opening", "type": "door", "height": 2.05, "touches": "hall" },
  { "a": 2.4, "b": 3.2, "kind": "wall", "height": 2.6, "offset": 0.03 },
  { "a": 3.2, "b": 4.0, "kind": "opening", "type": "adjacent", "touches": "yard" } ] }
```

The yard's facing side gets the matching `adjacent` opening touching this rect. The hall's facing
side gets a `door` opening touching it.
The same side with no RoomPlan data and no neighbours: `{ "length": 4.0, "spans": [ { "a": 0, "b": 4.0, "kind": "unknown" } ] }`.

---

## 2. Stored format (v3)

A rect's sides are stored as spans only. Nothing else about a side is stored.
Rect rows (`spacemap.json` `rooms[].rects[]` and the v2 location rects) carry them under `edges`.
For readers that predate v3, writers also emit `borders`: the same data in the v1/v2 shape,
derived from the spans. Readers prefer `edges`, and a row that only has old `borders` is converted (§9).

```json
"edges": {
  "v": 3,
  "minX": { "length": 4.68, "spans": [ { "a": 0.00, "b": 1.79, "kind": "unknown" }, … ] },
  "maxX": { … }, "minZ": { … }, "maxZ": { … }
}
```

| key | meaning |
|---|---|
| `v` | `3`. |
| `minX` `maxX` `minZ` `maxZ` | The rect's four sides in its own frame, before `yawDeg` (`minX` is the line x = rect.x, and so on). |
| `length` | Side length in metres. `minX`/`maxX` sides are `d` long, `minZ`/`maxZ` sides are `w` long. |
| `spans[]` | Ordered spans tiling `[0, length]` with no gaps and no overlaps. |
| `a`, `b` | Metres along the side from its min-tangent corner (`minX`/`maxX` from `z`, `minZ`/`maxZ` from `x`), with `b > a`. |
| `kind` | `wall`, `opening` or `unknown`. |
| `type` | Openings only: `door` or `opening` (RoomPlan), or `adjacent` (a neighbouring rect touches it with no confirmed wall). |
| `height` | Walls: the wall's height. Openings: the door or opening height when RoomPlan gave one. Omitted when not measured. |
| `offset` | Walls only: the real wall plane's distance beyond the edge, in metres (+ means beyond). Between −0.06 and 0.40. |
| `window` | Walls only, and only when true: a window stands in this stretch. It is still a wall, because it blocks walking. |
| `touches` | Optional, on any span: the id of the neighbouring rect that shares this stretch. On an opening it is the rect beyond it. An `adjacent` opening always has it. On a wall it is only adjacency, and nobody walks through. |

Optional keys are omitted when unset. Writers emit 3-decimal metres. Readers re-tile what they get:
gaps read as `unknown`, overlaps resolve by precedence, and unknown keys are ignored.

**Real rect: Russell Home 1, `room_3_r0`** (4.57 × 4.68 m, re-baked from the saved scan):

```json
{ "v": 3,
  "minX": { "length": 4.678, "spans": [
    { "a": 0, "b": 1.787, "kind": "wall", "height": 2.795, "offset": 0.033 },
    { "a": 1.787, "b": 2.824, "kind": "opening", "type": "door", "height": 2.368 },
    { "a": 2.824, "b": 3.243, "kind": "wall", "height": 2.795, "offset": 0.033 },
    { "a": 3.243, "b": 4.222, "kind": "wall", "height": 2.795, "offset": 0.033, "window": true },
    { "a": 4.222, "b": 4.678, "kind": "wall", "height": 2.795, "offset": 0.033 } ] },
  "maxX": { "length": 4.678, "spans": [
    { "a": 0, "b": 0.97, "kind": "opening", "type": "opening", "height": 2.401, "touches": "room_1_r0" },
    { "a": 0.97, "b": 1.256, "kind": "wall", "height": 2.594, "offset": 0.047, "touches": "room_1_r0" },
    { "a": 1.256, "b": 4.678, "kind": "wall", "height": 2.594, "offset": 0.015 } ] },
  "minZ": { "length": 4.574, "spans": [
    { "a": 0, "b": 3.296, "kind": "opening", "type": "adjacent", "touches": "room_19_r0" },
    { "a": 3.296, "b": 3.485, "kind": "wall", "height": 2.824, "offset": 0.033 },
    { "a": 3.485, "b": 4.574, "kind": "opening", "type": "door", "height": 2.213 } ] },
  "maxZ": { "length": 4.574, "spans": [
    { "a": 0, "b": 1.99, "kind": "wall", "height": 2.824, "offset": -0.021, "touches": "room_16_r0" },
    { "a": 1.99, "b": 3.498, "kind": "unknown" },
    { "a": 3.498, "b": 4.574, "kind": "opening", "type": "adjacent", "touches": "room_14_r0" } ] } }
```

**A rect in open floor with no neighbours: colour-blank (c8aaeb10), `room_28_r0`** (4.75 × 4.15 m). This is an open area (§5):

```json
{ "v": 3,
  "minX": { "length": 4.15, "spans": [ { "a": 0, "b": 4.15, "kind": "unknown" } ] },
  "maxX": { "length": 4.15, "spans": [ { "a": 0, "b": 4.15, "kind": "unknown" } ] },
  "minZ": { "length": 4.75, "spans": [ { "a": 0, "b": 4.75, "kind": "unknown" } ] },
  "maxZ": { "length": 4.75, "spans": [ { "a": 0, "b": 4.75, "kind": "unknown" } ] } }
```

Old data (v1/v2 `kind` / `wallSpans` / `openings`) is converted when it is read (§9).

---

## 3. What the three kinds mean

| kind | means | walk through? | wall layers / nav barrier | an "opening"? |
|---|---|---|---|---|
| `wall` | A real wall (RoomPlan), on the edge or up to 40 cm beyond it. Windows are included. | no | yes | no |
| `opening` `door` / `opening` | A RoomPlan door or opening. | yes | no | **yes** (a doorway) |
| `opening` `adjacent` | A neighbouring rect touches here with no confirmed wall. | yes | no | **yes** |
| `unknown` | Everything else, and the default. This is no wall activity, not a claim that the space is open. | yes | no | no |

- **Openings are a sure place for tracks to go.** RoomPlan doorways are physical doors.
  Adjacent openings join two rects, for example two halves of one room or a room and the hall
  next to it.
- **Unknown is passable but promises nothing.** It never gets walls, wall layers or navmesh barriers.
- **`touches` on a wall is adjacency only.** The rects touch, but nobody walks through.

---

## 4. Computed helpers (not stored)

Everything below is derived from the spans. In C# it lives on
`AttractionRectOpenings` in the SDK. In Lua it comes from `dp.*` (§6).

| helper | rule |
|---|---|
| walled side (`IsWalled`) | Wall spans cover **≥ 50%** of the side. This alone lights wall layers (`onwalls`) when the attraction is flush within 10 cm. |
| side kind (`Kind`, Lua `kind`) | The kind covering the most length. Ties go opening > wall > unknown. It is a summary only, so behaviour should read the spans. |
| mountable wall | Wall spans without a window, contiguous ones joined, none shorter than 0.15 m. |
| openings | `opening` spans (type `door` / `opening` / `adjacent`, `to` = touches). Doorways (`HasDoorway`, `IsDoorway`) are the RoomPlan ones only. |
| passages | Every non-wall stretch (door / opening / unknown), `to` = touches. Routes use these. |
| links | Every stretch with `touches` (any kind; `kind` says what is under it). |
| full wall | The whole side is wall. |
| open area | No side has a wall or a RoomPlan doorway; every span is unknown or adjacent (§5). No data at all also reads as an open area. |
| wall offset / height | Length-weighted `offset`, and the lowest `height`, of the wall spans. |

---

## 5. Open areas: connect from any side

Openings are a sure place for routes to go, but a rect with 100% unknown edges is considered connected from any
side: it is probably out in a big open floor like a gymnasium or a mall courtyard.

- `dp.rect_is_open_area()` / `o.open_area` / `AttractionRectOpenings.IsOpenArea` is true when no side
  has a wall or a RoomPlan doorway. Every span is `unknown` or an `adjacent` opening, so
  neighbours touching the rect do not make it less open.
- Routes: an open-area rect offers entries on all four sides, and any two open-area rects are joined
  by a straight walk even when they do not touch.
- Content: on an open-area rect, enter and leave from whichever side suits the layout. On any other
  rect, prefer doorways (`door` / `opening`), then `adjacent` openings, then unknown stretches.
  Never use a wall.

---

## 6. Lua API

`side` is `'front'`, `'back'`, `'right'` or `'left'`. These are the attraction's own sides (+Z, −Z,
+X, −X). Each one is the RECT's edge that side faces. `t` is metres from that side's **left corner,
seen from inside looking out**. Inside a script the attraction is implicit; global forms take a
GameObject first (`dp.rect_is_wall(go, side, t)`).

| function | returns |
|---|---|
| `dp.rect_openings()` | The whole table (below), or `nil` outside an attraction. |
| `dp.rect_spans(side)` | That side's raw spans: `{ {a, b, width, center, kind, type?, height?, offset?, window?, touches?, p0, p1, mid}, … }`. |
| `dp.rect_is_wall(side, t)` | `true` only on a `wall` span. |
| `dp.rect_is_open(side, t)` | `not rect_is_wall`: an opening **or** unknown (passable). |
| `dp.rect_is_opening(side, t)` | `true` on any opening: a RoomPlan door/opening or an `adjacent` one (read `dp.rect_span_at(side, t).type`). |
| `dp.rect_span_at(side, t)` | The span at `t` (`{a, b, kind, type?, touches?, …}`), or `nil`. |
| `dp.rect_is_unknown(side, t)` | `true` only on an `unknown` span. With no data, `true`. |
| `dp.rect_is_open_area()` | No wall and no RoomPlan doorway on any side. Also `true` with no data or outside an attraction. |
| `dp.rect_point(side, t)` | An attraction-local Vector3 on the rect edge. |
| `on_rect_changed(o)` | A hook. It fires after `start()`, then whenever the seat or the map changes. `o` is the `dp.rect_openings()` table. |
| `onwalls(walls)` | A hook. `"front,left"` is the list of walled sides (≥ 50% wall and flush). |

A point on a boundary reads as the higher one (RoomPlan opening > wall > adjacent opening > unknown).

Outside an attraction, or with no park data, the functions answer as "no data". That means no walls and nothing known, which is intended so that content works without a park:

| function | answer |
|---|---|
| `rect_is_wall` | `false` |
| `rect_is_open` | `true` |
| `rect_is_opening` | `false` |
| `rect_is_unknown` | `true` |
| `rect_is_open_area()` | `true` |
| `rect_openings`, `rect_spans`, `rect_span_at` (no attraction) | `nil` |
| `rect_openings` (an attraction with no park data) | four all-unknown sides, with `has_data = false` |

`dp.rect_openings()` table:

```lua
o.version      -- 3 (feature-detect the model: o.version == 3, or dp.rect_is_opening ~= nil)
o.rect_id, o.has_data, o.width, o.depth, o.center (Vector3, local), o.sides = {'front','back','right','left'}
o.source       -- 'park' | 'preview' | 'default'
o.entry_side, o.entry_reason
o.open_area    -- no wall and no RoomPlan doorway on any side
o.front = {
  side, length, outward (Vector3), kind ('wall'|'opening'|'unknown', most length),
  walled (a wall layer applies here), full_wall, all_unknown,
  wall_height, wall_offset,            -- lowest wall height; length-weighted offset beyond the edge
  spans    = { {a, b, width, center, kind, type?, height?, offset?, window?, touches?, p0, p1, mid}, … },
  openings = { {a, b, width, center, type ('door'|'opening'|'adjacent'), kind = 'opening', to?, p0, p1, mid}, … },
  passages = { {a, b, width, center, type ('door'|'opening'|'adjacent'|'unknown'), kind, to?, p0, p1, mid}, … },
  links    = { {a, b, width, center, kind ('wall'|'opening'|'unknown'), to, p0, p1, mid}, … },
}
```

`p0`, `p1` and `mid` are attraction-local Vector3s on the rect edge. `to` is the neighbouring rect id.

Examples:

```lua
-- a track through the widest doorway, else any opening, else any passable stretch
local function pick_entry(o)
    local best, side = nil, nil
    for _, s in ipairs(o.sides) do
        for _, op in ipairs(o[s].openings) do                -- doorways first, then adjacent openings
            local door, bdoor = op.type ~= 'adjacent', best ~= nil and best.type ~= 'adjacent'
            if best == nil or (door and not bdoor) or (door == bdoor and op.width > best.width) then best, side = op, s end
        end
    end
    if best ~= nil then return best, side end
    for _, s in ipairs(o.sides) do                           -- no door: passable stretches
        for _, p in ipairs(o[s].passages) do
            if p.width >= 0.9 and (best == nil or p.width > best.width) then best, side = p, s end
        end
    end
    return best, side
end

-- doorway pairs with neighbouring rects: every opening that leads into a rect
for _, s in ipairs(o.sides) do
    for _, l in ipairs(o[s].links) do
        if l.kind == 'opening' then place_doorway(l.mid, o[s].outward, l.to) end
    end
end

-- spawn zombies only from sides that are not walls (sample the side, in METRES)
local function side_is_walled(side, len)
    for k = 1, 9 do if not dp.rect_is_wall(side, len * k / 10) then return false end end
    return true
end

if dp.rect_is_open_area() then enter_from_any_side() end
```

Routes (`ROUTES.md`): `dp.ports()` resolves each portal against the **passages**. A portal is
`blocked` only when it faces a wall, and `opening = {type, kind, to?, a, b, width, mid, narrow}`.
`dp.neighbors()` returns one row per passage that has a `to`. `dp.rect()` and `dp.park_rects()`
entries carry `rect = { id, opens, links, spans, open_area }` in world metres. `opens` holds the
passages (`type` door / opening / adjacent / unknown, `kind`, `to`), `links` holds the touches (any `kind`, `to`) and `spans` holds the raw
spans. `dp.route` and `dp.tour` walk through passages that have a `to`, and join open areas directly.

---

## 7. C# API and Editor injection

`AttractionTemplate.RectOpenings` returns an `AttractionRectOpenings`, which is never null:
- `Spans(side)` returns `IReadOnlyList<AttractionRectSpan>`, with fields `a`, `b`, `kind`, `type`, `height`, `offset`, `window`, `touches`. Spans also have `IsDoorway` and `IsAdjacent`.
- `SpanAt(side, t)` returns the span at `t`. `IsOpenFloor(side)` is true when the side has no wall and no doorway.
- `KindAt`, `IsWall`, `IsOpen`, `IsOpening`, `IsUnknown` take `(side, t)`.
- `IsFullWall`, `IsAllUnknown`, `IsWalled`, `Share(side, kind)`, `Kind`, `WallHeight` and `WallOffset` take `side`. `IsOpenArea` is a property.
- `Openings`, `Passages` and `Links` take `side`. `Nearest(side, t)` and `Widest(out side, type)` search over passages.
- `LocalPoint(side, t)` and `Outward(side)` are unchanged.
- The `RectChanged` event and `HasParkRectOpenings` are unchanged.

**Inject test data in the Editor or Park Simulator, before an app build:**

```csharp
var o = AttractionRectOpenings.AllUnknown(attraction.RuntimeFootprintMeters);
o.rectId = "test_rect"; o.hasData = true;
var front = o.Side(AttractionWallSide.Front).spans;
front.Clear();
front.Add(new AttractionRectSpan(0f, 1.0f, "wall", height: 2.4f, offset: 0.2f));
front.Add(new AttractionRectSpan(1.0f, 1.9f, "opening", "door", 2.0f, touches: "hall"));
front.Add(new AttractionRectSpan(1.9f, 3.0f, "opening", "adjacent", touches: "yard"));
front.Add(new AttractionRectSpan(3.0f, o.Length(AttractionWallSide.Front), "unknown"));
attraction.SetRectOpenings(o);              // as the app does at seat time (wins over the preview)
attraction.SetRectOpeningsPreview(o, true); // or: authored preview data, used only without park data
```

Spans may be given in any order and may leave gaps: they are painted over an all-unknown side.
Inspector: AttractionTemplate → **Rect Openings Preview** (*Reset: all unknown*, *Sample: walls + front
door*, Scene gizmo with walls grey, openings green and unknown yellow). Park Simulator → **Arena Rect**
presets seat the Arena test on simulated rects. Their edges use the v2 vocabulary and are converted.

---

## 8. Tunable constants (the app's bake)

| constant | value |
|---|---|
| beyond-edge wall band | 0.40 m |
| wall plane tolerance | 0.06 m |
| cut plane tolerance | 0.12 m (inside, for doors and windows) |
| wall parallel max | 3° |
| opening parallel max | 10° |
| min door overlap | 0.3 m |
| adjacency probe | 0.05 m |
| same-room gap probe | 0.2 m |
| adjacency parallel max | 5° |
| adjacency overlap | 0.10 m |
| min opening | 0.3 m |
| min span | 0.15 m |
| snap | 0.03 m |
| walled min share | 0.5 |

---

## 9. Migrating from v1/v2

| v2 | v3 |
|---|---|
| side `kind`: `wall`, `window` | spans of `kind: wall` (`window: true` on window stretches) |
| side `kind`: `door`, `opening` | `opening` spans (`type`) |
| side `kind`: `open`, `unresolved` | `unknown` |
| `openings[]` of type `door` / `opening` | `opening` spans; `to` → `touches` |
| `openings[]` of type `rect` | `opening` span of type `adjacent` with `touches` |
| `openings[]` of type `open` | `unknown` span |
| "everything not an opening is blocked" | Only `wall` blocks. `unknown` is passable. |
| `wallSpans`, `coverage`, `mountableCoverage`, `planeOffsetM`, `wallHeightM`, `matchedWallId`, `acrossRoomId`, `parallelDeg` | Not stored. Mountable wall, offset and height are computed from the spans; the rest is gone. |
| walked (capture-walk) openings | Gone. Openings are RoomPlan doorways and adjacent rects only. Openings are mirrored onto the facing rect. |

Lua changes:
- `side.openings` now lists RoomPlan doors/openings and `adjacent` openings, but not unknown stretches.
  - Code that used it as "every walkable stretch" should read `side.passages`.
  - Code that read neighbour `rect` entries should read the `adjacent` openings or `side.links`.
- Opening `type` is `door`, `opening` or `adjacent`. Passages may also be `unknown`. `open` and `rect` no longer appear.
- `side.full_wall` now means the whole side is wall. It used to mean "no openings", so a side with no data is no longer a full wall.
- `side.walled` and `onwalls` now light up at ≥ 50% wall, so a mostly-wall side with a door is walled.
- `rect_is_open(side, t)` is true on unknown stretches too, as before for "open". It is false only on walls.
- `rect.opens` from `dp.rect()` and `dp.park_rects()` holds passages. Their `type` is `adjacent` instead of `rect`, and `unknown` instead of `open`.
- Feature detection: `if dp.rect_is_opening ~= nil then … v3 … end` (or `o.version == 3`).

Old saved borders (v1/v2) are converted when they are read. Blocked stretches on an edge that had a
matched wall become `wall`, and the rest follows the table above.

---

## 10. Tests

- SDK EditMode `AttractionRectOpeningsTests` (spans, queries, links, old-data conversion, gaps and overlaps).
- SDK EditMode `OpeningsRoutesTests`:
  - The Lua 3-kind semantics, `dp.rect_spans`, links and open area.
  - `dp.ports`, `dp.route`, `dp.tour` and `dp.neighbors`.
  - Two open-area rects routing to each other.
- `Tools/routes-offline/test_routes.lua`, including the open-area routing cases.
