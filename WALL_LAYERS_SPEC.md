# Attraction Wall Layers — implementation spec

Status: implemented (Oct 2026) in the SDK, the DreamPark app, the backend and the phone app. §11 lists the SDK
tests that cover it.
Companion to `LAYOUT_FRAMES_SPEC.md` — wall layers sit on top of frames and use the same
override machinery.

## 1. Why

Game teams asked for wall awareness: *"No walls" stays the baseline; wall-mounted extras
would only ever be optional bonuses on sides where the app reports a real wall, with a
freestanding fallback and no gameplay that depends on them.* They want, per side of the
play area, whether there is a wall and where its plane is.

The old model was wrong: attractions and props were made *for* walls (`LevelTemplate.walls`,
`PropTemplate.wallSide`, `SpaceMapWallSolver`), so they needed a wall to exist at all. The new
model: every attraction is complete with no walls; a wall that is really there makes it
**better**. Same elements, re-arranged per available wall, exactly like frames re-arrange
them per available size.

### Decisions (fixed — do not relitigate)
- **Base (no walls) always works.** Wall layers are optional bonuses; no gameplay may depend
  on them. An attraction with no wall layers behaves byte-for-byte as today.
- **The rect edge IS the wall.** The rect is baked with which of its edges are walls; a wall
  layer simply switches on when its side is on a wall edge. No snapping, no wall-plane math.
- **Priority = order of the wall layers.** The inspector list is reorderable; an element used
  by several layers goes to the first one (top of the list) whose wall is available.
  No separate priority field.
- **The old wall-only content path is deleted**, not migrated (nothing shipped on it).
- Teams get normalized wall data (the same on phone and Quest), not raw Meta scene data.

## 2. Glossary
- **Side** — one of the attraction's four footprint edges in prefab-local space:
  Front (+Z), Back (−Z), Right (+X), Left (−X).
- **Wall layer** — a set of overrides (same as a frame's) that switches on when that side is a wall.
- **Walled side** — a side of the attraction that sits on a rect edge that is **walled**: wall
  spans cover at least half of it (v3, `RECT_EDGES_SPEC.md`). One boolean per side. That is the
  whole contract.

## 3. Rect wall truth (the app)

**v3 (Oct 15 2026): the rect side is spans** — see `RECT_EDGES_SPEC.md` for the stored format and
the bake. Each side of a rect is an ordered list of `wall` / `opening` / `unknown` spans (unknown is
the default). A `wall` span is a RoomPlan wall parallel within 3° whose line is on the edge (±6 cm)
or up to 40 cm beyond it with nothing in between; it carries the wall's `height` and its `offset`
(the real wall plane's distance beyond the edge — the edge is still treated as the wall; the offset
lets content sit on the real plane). Windows are wall spans with `window: true`; doors and
openings are `opening` spans painted on top.

Computed from the spans: walled (wall share ≥ 0.5 — the one thing wall layers read), mountable wall
spans (wall spans without a window, ≥ 0.15 m), wall height and plane offset.

Matching rules:
- **Normal sign is ignored.** Real scans store roughly a third of rect-bounding walls with the
  normal facing into the room.
- Candidates are **every wall on the rect's story**, not only its room's, plus RoomPlan's whole
  boundary surface list.
- The nearest wall paints first; a farther wall fills only stretches nothing nearer covers.
- Borders are baked onto every published map and serialized as
  `rects[].borders = { v: 3, minX: { length, spans }, … }`.

## 4. SDK data model (`Assets/DreamPark/Scripts/Features/Attractions/AttractionLayoutFrame.cs`)

```csharp
public enum AttractionWallSide { Front = 0, Back = 1, Right = 2, Left = 3 }        // +Z, −Z, +X, −X
[Flags] public enum AttractionWallMask { None = 0, Front = 1, Back = 2, Right = 4, Left = 8, All = 15 }

[Serializable] public sealed class AttractionWallLayer {
    public string id;                                   // "w" + 8 hex, generated once
    public AttractionWallSide side;                     // at most one layer per side
    public List<AttractionLayoutOverride> overrides;    // reused from frames
    public Vector2 authoredFootprintMeters;             // Base footprint when last saved
}
public static class AttractionWallSides { All, Mask, Has, Name, TryParse, ToWire, FromWire, TryFromLocalDirection }
```
On `AttractionTemplate`: `[SerializeField, HideInInspector] List<AttractionWallLayer> wallLayers` —
**list order is priority** — plus the editor-only `layoutPreviewWalls` mask.

## 5. What the app hands the attraction

One `AttractionWallMask`, resolved at seat time: for every side of
the attraction (rotated through the template's park yaw into the rect's frame; a side more than 1°
off the rect's axes never counts) whose rect edge is walled (`RectEdgeBorder.IsWalled`: wall spans
≥ 50% of the edge) **and** whose footprint edge is within 0.10 m of that rect
edge. An attraction seated in the middle of a bigger rect is not against the wall. `unknown` never
counts. A walled side may still have a door in it: content that must not block it reads
`dp.rect_spans(side)` / `dp.rect_is_open(side, t)`.

## 6. Runtime (`AttractionTemplate`)

- `ApplyLayout(frameId, scale, AttractionWallMask walls)` applies a layout with walls;
  `SetWalls(mask)` changes the walls and re-applies the current frame/scale. The plain
  `ApplyLayout(frameId, scale)` keeps the current walls.
- An attraction with wall layers always runs on the layout pipeline (`UsesLayoutPipeline =
  HasLayoutFrames || HasWallLayers`), so a frame-less attraction restores Base states too.
- `ResolveWallLayers` runs inside `ApplyLayoutInternal` after the frame's overrides and bake poses,
  before the single write pass: **per transform**, the first layer in list order whose side is
  walled and that overrides it wins; transforms no walled layer touches keep the frame/Base pose.
- Positions of root-level objects are re-anchored to the applied footprint (`AnchorToSide`): same
  distance from the layer's edge, same relative place along it, height unchanged. Nested objects
  are applied as authored and ride with their parent.
- Content API: `Walls`, `WallsWire` ("front,left"), `HasWall(side)`, `HasWallNamed("front")`,
  `WallLayers`, `HasWallLayers`, `WallLayerSides`, event `WallsApplied` (fires when the walls change
  or are first set). Lua: `onwalls(walls)` on any `LuaBehaviour` inside the attraction (after
  `start()`, then on change); `attraction:HasWallNamed(...)` and `attraction.WallsWire` via the XLua
  wrap. An attraction with no wall layers still reports its walls, so scripts can react.
- Editor: `SetLayoutPreviewWalls(mask)`; in Play Mode a walls-only preview layers onto whatever
  layout the park applied.

## 7. Authoring UX (Editor)

- Inspector section **Wall Layers** (`Editor/AttractionWallLayersInspector.cs`) under Layout
  Frames: rows `1. Front wall … ▲ ▼ Edit Delete`, "Add wall layer: Front (+Z) …" for free sides
  (adding opens the edit session), warnings, and the **Walled sides preview** toggles (ghost walls in
  the Scene; Play Mode applies the layers).
- **Edit** reuses `AttractionLayoutFrameEditSession` (`BeginWallLayer`, `BeginWallLayerInteractive`,
  state `wallLayer = true`): snapshot Base, apply the layer, draw a 2.4 m ghost wall on that side's
  Base footprint edge with a scene panel (Save Wall Layer / Cancel). Commit stores the diff as the
  layer's overrides and stamps `authoredFootprintMeters` with today's Base footprint. Same rules as
  frames: new objects stay in the prefab, inactive in Base; deleting/re-parenting blocks the save.
- Bake (`AttractionPackingBaker.Bake`): a frame-less attraction with wall layers bakes with
  `skipInactive` (wall-only extras never size Base) and records Base states; every bake with wall
  layers runs `FindWallCombinationIssues` — every combination of the layers' sides × Base and every
  baked frame at authored size — and warns (console + inspector) when a walled combination puts a
  top-level object on another it did not already overlap. Floors/followers and anything ≥ 60% of
  the footprint are ignored. Warnings never block a save.
- `GetWallLayerWarnings`: duplicate side, missing id, empty layer, unresolved target, a move of a
  packing follower (followers are placed by the bake, so the move would be ignored).
- `CopyPackingAuthoringSettings` / `RevertPackingOverrides` / `HasFrameInstanceOverrides` carry
  `wallLayers` like `layoutFrames`.

## 8. App packer & park documents

- When seating an attraction with wall layers on a rect with borders, after the frame and fit are settled the app:
  1. **Slides** it, inside the seat's own free room, flush against the walled edges its layers face, highest
     priority first, one per axis.
  2. **Turns** it 180° (identical footprint box) when that lights up a higher-priority set of layers and no
     Adventure facing preference owns the choice.
  Never a different frame, scale, reservation size or rect.
- The resolved mask is applied with `template.SetWalls` at seat time and re-resolved after a rescan (which never
  turns the attraction).
- Persistence: `"packing": { …, "walls": "front,left" }` (omitted for None; lenient parse, non-string rejected).
  Spawn applies it via `ApplyLayout(frame, scale, walls)` / `SetWalls`. A rect that comes back from the live map turned
  90° keeps its wall edges.
- Catalog: the uploader publishes `wallSides` (priority order) in the packing profile; the backend keeps it. The app
  does not need it: the seat reads the live template.

## 9. Legacy flush (done)

Removed: `LevelTemplate.walls`/`WallSide`/legacy bool migration/`WallsWireValue`/wall gizmos and
`GetWallHeightMeters`; `PropTemplate.wallSide`/`PropWallSide`/`WallsWireValue`/
`PublishedWallSideToken`/wall gizmos/`GetWallHeightMeters`; `WallHeightMeasurement.cs`; the
uploader's `walls`/`wallHeightFt`; their XLua wrap members and the `DreamParkLuaConfig` exclusion;
the app's old wall solver and wall routing. The backend may still hold old `walls`/`wallHeightFt`
values on dimension rows; nothing reads them.

## 10. Phases

All four phases are implemented (rect truth; SDK data/runtime/API; authoring; packer +
persistence + backend/phone-app passthrough).

## 11. Tests

- SDK EditMode `AttractionWallLayerTests`: no layers = nothing moves; a layer shows only on its
  side; shared object follows list order and reordering; all 16 masks order-independent;
  `AnchorToSide`; layer follows a frame's footprint; `WallsApplied` fires on change only; frame-less
  bake skips wall-only extras and records Base states; combination check reports only new overlaps;
  wire round-trip.

## 12. Out of scope (v1)
- Non-rectangular or angled walls (a wall more than 3° off the edge is not painted, so it never lights up a layer).
- Explicit per-corner-pair overrides (list order covers it; revisit if a team needs it).
- Using the stored `wallSpans` per element (a mounted piece over a doorway): the contract is one
  boolean per side; spans stay diagnostic.
- Carrying RoomPlan's per-surface confidence into the wall data.
- Using Quest's own MRUK scene walls (iOS capture is the cross-platform truth).
- Ceiling or floor "walls".
