# Attractions and props

The building blocks of a title, how the content pipeline addresses them, and how one attraction fits every room.
`§ Name` points to a section of these docs; the table at the end of `Assets/DreamPark/Docs/CLAUDE.md` maps every section to its file.

## The Three Primitives (plus the Game Manager)
- **Player.prefab** (`Assets/Content/{GameName}/Prefabs/`): the guest's body in your game — `PlayerRig` root with Head/Body/Feet/Hand trackers. Persists across attractions. Per-guest systems (hand-held tools, damage screens, per-player audio) live here as LuaBehaviours, and so does anything that must work when attractions are placed loose.
- **Game Manager** (`Prefabs/Game Container.prefab`, generated once by the Content Uploader): title-wide rules, progression and shared state for the Arena/Sequence/Adventure packages. Networked, never distance-culled, not a spatial parent — and absent when the title runs as loose placements. See § Packages.
- **AttractionTemplate** (: LevelTemplate): root component of an attraction prefab — a self-contained experience (arcade game, boss battle, challenge course). LevelTemplate's `[RequireComponent]`s auto-add GameArea (presence detection — drives PlayerRig show/hide AND playtime-based revenue attribution) and MusicArea. Defines the physical space (size/customSize, floor generation, calibration).
- **PropTemplate**: root component of a prop prefab — the individual interactive elements that make up an attraction (coin, hammer, enemy). Auto-adds its own GameArea at priority -1 (self-suppressed when nested inside an attraction). Props are also placeable standalone in parks.

There is no creator-facing DreamBand or Level prefab — the DreamBand wrist UI ships with the SDK's hand tracking, and attractions are authored as `Prefabs/A_*.prefab`, not a canonical Level.prefab.

## Creating Attractions & Props (content pipeline)
An attraction is a prefab under `Assets/Content/{GameName}/` with an `AttractionTemplate` (: `LevelTemplate`) on its root; a prop has `PropTemplate`. `ContentProcessor` (`Assets/DreamPark/Editor/`) watches `Assets/Content/` and automates everything else — do NOT hand-edit Addressables addresses or labels:
- **Naming**: prefixing attraction prefabs `A_` and props `P_` is still the convention, but no longer required for backend discovery (July 2026): the attractions catalog classifies by the stamped ADDRESS NAMESPACE — anything with a `{gameId}/Levels/…` address (i.e. any prefab whose root has AttractionTemplate/LevelTemplate) is an attraction, `{gameId}/Props/…` is a prop. **Exception: an `L_` prefix marks a pre-attraction Legacy Level** (hidden from the Attractions browser, entry-fee-priced only) — never name a new attraction `L_*`. If an attraction is missing from the browser, check that the prefab root has the right template component (that's what produces the address).
- **Runtime address** (assigned automatically): `{gameId}/Levels/{size}/{name}` for attractions, `{gameId}/Props/{category}/{name}` for props, `{gameId}/{TypeFolder}/{name}` for typed assets (Models/Audio/Textures/…). Label = `{gameId}`. Preview PNGs and the content logo still get addresses (`{gameId}/Previews/{name}`, `{gameId}/Logos/{name}`) but their groups are EXCLUDED FROM THE BUILD since July 2026 — those addresses resolve in the editor and nowhere else. **`{category}` in the prop address is `PropTemplate.category`, and that field is FROZEN (Aug 2026)**: it is greyed out with `[ReadOnly]` (the same treatment as `gameId` and `resourceName` beside it) and kept only because it is already baked into every shipped prop's address, and therefore into the `resourceName` the backend joins on. Changing a prop's category does not re-tag it — it RENAMES the asset and orphans its downloads/views/revenue history — so the enum and its member ORDER must never change (Unity serializes enums by integer value, so a reorder silently renames every existing prop) and new props stay on the `Generic` default, `{gameId}/Props/Generic/{name}`. The category a player actually sees is assigned per attraction in the developer portal's Attractions tab, after upload, against the server-owned category taxonomy (~130 slugs, derived groupings). Do not add a category field to `LevelTemplate`/`AttractionTemplate`.
- **TWO identifier namespaces — never conflate them**, and note the trap: the SAME NAME `resourceName` is used for both.
  1. **The Unity COMPONENT field** — `GameArea.resourceName` / `PropTemplate.resourceName` — holds the **ADDRESS form** `{gameId}/Levels/{size}/{name}`, stamped by `ContentProcessor.ResolveAttractionAddress`. This is the revenue-attribution key.
  2. **The backend catalog ROW** `resourceName` is normally the asset-path **STEM** (`Content/{GameName}/Attractions/A_X` — from the Addressables catalog's `m_InternalIds`, `Assets/` + extension stripped). **The catalog can hold the ADDRESS form too**: when the Unity catalog carries no asset paths (dynamic/hashed internal ids), the row is materialized from the address instead and flagged `viaAddress: true`.

  The two are NOT reconciled by being equal — the app reconciles them AT SPAWN: raw string first — which is exactly what makes a `viaAddress` row loadable — then the string minus the leading `Content/` segment, then a leaf-name match against mounted locators scoped to this game's keys. So `ContentProcessor`'s "matches the backend attractions catalog exactly" comment is only true for `viaAddress` rows: the stamp is right, the comment overstates it. **Treat `resourceName` as an opaque join key, never something you hand to Addressables yourself.**
- **Stamping pass** (`ContentProcessor`, EditPrefabContentsScope + SaveAsPrefabAsset): injects `gameId` into any component with a `gameId` field and stamps the per-attraction ADDRESS onto `GameArea`/`PropTemplate.resourceName` — the revenue-attribution key. It is the address form, NOT the catalog's stem form (see the two-namespaces bullet above); the join happens at spawn, not by the two strings matching. Skips `ThirdPartyLocal/`. Prefabs get edited + saved dirty by this pass; that's expected — commit the churn.
- **Previews**: `Assets/Content/{GameName}/Previews/{prefabName}.png` (or sibling `{name}_preview.png`) — powers Attractions-browser/level-picker tiles and the consumer map. Auto-generated for every attraction/prop; regenerate via `DreamPark → Troubleshooting → Regenerate Level Previews` after visual changes. **They do NOT ship in a bundle (July 2026)**: the uploader pushes each PNG to the backend (`POST /api/content/:id/attractions/preview`) after a successful commit, and every client reads the backend image. Same story for the content logo (`POST /api/content/:id/logo` → `content.logoImageUrl`). The `{gameId}-Previews` / `{gameId}-Logos` Addressables groups still exist but are build-excluded (`SmartBundleGrouper.ExcludeRetiredArtGroupsFromBuild`), so preview churn can no longer abort a Code-only upload — which is why `UploadMode.PreviewsOnly` is gone.
- **Catalog population is automated**: uploading a build publishes the attractions catalog server-side (discovered from the catalog's `m_InternalIds`). There is no manual registration step — if an attraction is missing from the browser, check that the prefab root has the right template component (the prefix is not what's checked; see Naming above).

## The attraction prefab: author the stage, instantiate the crowd

**Author the stage and one of every actor. Instantiate the crowd.**

The preview PNG is rendered from the prefab and is all a guest sees before walking in (in the
Attractions browser and on the consumer map), so the prefab must show what the attraction *is*:
the space, the activity, and an example of everything that appears in it. It does not need every
copy.

**Authored in the prefab, enabled:**

- **The stage:** floor, walls, track, board, scenery, screens, stations: anything that exists for
  the whole round and defines where things happen.
- **One exemplar of every kind of actor:** one of each enemy, vehicle, projectile, pickup and
  hazard, posed mid-action where it would really be (a meteor mid-fall, a car on the road, a grunt
  in the room, a bolt in flight). The exemplar doubles as the runtime template: hide or reuse it
  when the round starts.
- **Fixed content the game counts:** bricks, shields, buildings, anything the player destroys or
  uses that starts the round in place.

**Instantiated at runtime from the exemplar (or a prefab reference), destroyed when done:**

- Anything that comes and goes: waves, spawns, projectiles, traffic, pickups, debris.
- **Every particle effect:** one prefab per effect type, created at the spot, destroyed when it
  finishes. Never pre-placed copies.

**The test:** if the prefab holds several hidden copies of the same thing waiting to be used,
that's a pool. Delete the copies and instantiate. Hidden-at-load duplicates are the smell; one
visible exemplar is the goal. The `hidden-pools` pre-upload check (Warning) flags an attraction
with more than 8 inactive-at-load copies of the same prefab or object under one parent (of an
effect: anywhere in the attraction).

**Multiplayer:** a spawned object is the local picture of a shared event. Spawn it when the event
arrives and key it by the event's id (a string, e.g. `"p2-bolt-14"`), never by a pool slot
number.

**Load cost:** keep what an attraction creates on load small, and measure the attraction's load
time after big changes. Cloning is cheap for plain objects; keep spawned things script-light (the
manager drives them); spread big bursts over a few frames, and keep a small warm set (2–3) only
for the one or two things that spawn fastest, and only after measuring a stutter.

The preview rules that keep the authored set honest:

- **The authored set is enabled.** A prefab whose stage or exemplars ship disabled renders as an
  empty room, and the preview is a lie. A prefab that looks empty in the Project view is a bug
  even if it plays correctly.
- **Space the authored set honestly.** Objects must not interpenetrate or stack at the origin in
  the authored state. If the preview shows overlapping meshes, the attraction reads as unfinished
  regardless of how it plays.
- **Something must always be on screen.** If content genuinely arrives late (streamed,
  server-driven, or picked per session), ship a **visible authored stand-in**: the start screen
  layout, a plinth with placeholder art, a roped-off area, silhouettes. Never a blank floor, never
  a missing-asset gap.
- **Regenerate previews after any layout change** (`DreamPark → Troubleshooting → Regenerate
  Level Previews`) and *look at the PNG*. Ask: could someone who has never seen this attraction
  tell what it is and what they would do in it?

## One attraction, one size: 12 × 18 ft (Nov 2026)

**One attraction, one size: 12 × 18 ft.** Author every attraction as a single layout for a 12 × 18 ft room: the
prefab's Base, with no layout frames. The shrink/grow bake (`AttractionPackingBaker`) covers the rooms around that size
automatically: it fits the same layout into somewhat smaller rooms and spreads it out in bigger ones, within its baked
range. Don't author frames, a Minimum frame, or hidden content for other room sizes. Rooms larger than the grow range play
the 12 × 18 layout at its largest; rooms smaller than the shrink range don't get the attraction. Separate attractions
still differ in content, never in size. The frame system stays in the SDK so older content keeps loading, but new and
reworked attractions don't use it.

**Never ship the same content at different sizes as separate attractions** (`A_Arena_Small` / `A_Arena_Large`,
`A_X_8x12` / `A_X_20x30`): they split revenue history and previews, fill the Arena with duplicates, and every fix has to
be made N times.

## Layout Frames (legacy: existing content only)

**Legacy.** New and reworked attractions don't use layout frames (§ One attraction, one size). This section documents the
system so attractions that already carry frames keep loading and can be maintained; don't add frames, a Minimum frame or
new frame content. Spec: `LAYOUT_FRAMES_SPEC.md`. Creator guide: `PACKAGES.md` § Layout frames (legacy). The shrink/grow
bake, `[nopack]` / `-- @packing-ignore` and `-- @gapfiller-links` below apply to every attraction, framed or not.

A **layout frame** is a hand-authored arrangement of an attraction at one **width × length**, stored on `AttractionTemplate` as overrides (active + local TRS, by hierarchy path) on top of the prefab. **Base** (id `base`) is the prefab as authored — never a stored frame. At most one **Minimum** frame (id `minimum`) — the smallest viable version; it REPLACES `essentialProps` (hidden; legacy lists are migrated into a Minimum frame on bake, once). Custom ids are `f` + 8 hex, generated once — never derive ids from sizes.
- **Every frame gets its own shrink/grow bake** (`AttractionPackingBaker.Bake` bakes Base + all frames). With frames, bakes ignore objects inactive in that frame. Frame-less attractions bake and run exactly as before.
- **Runtime picks ONE frame and never blends**: the ladder (`GetFrameEnvelopes`, largest → smallest authored area) → the first frame whose shrink fits → scale within that frame's range (`TrySelectFrame` / `ApplyLayout(frameId, frameScale)`). Frame scale is relative to that frame's own size. Legacy `ApplyPackingVariant(scale, essentialOnly:true)` maps to the Minimum frame.
- **Keeping helper visuals out of the bake** (Oct 2026): an object whose name contains `[nopack]`, or that runs a Lua script containing a `-- @packing-ignore` comment, is ignored by the shrink/grow bake with everything under it (`PackingIgnore` in `AttractionPackingBaker.cs`; names containing `gizmo` still are too). A naming/annotation convention on purpose — no component.
- **Walking in from the gap ground: `-- @gapfiller-links`** (Oct 2026): a `-- @gapfiller-links` comment in any LuaBehaviour script inside an attraction makes `GapFiller` add bidirectional NavMeshLinks across the attraction's footprint edges, so agents on the gap ground can path in and out (`SeamLinkCount`). No marker means no links. The links are clipped to the stretches where both ends are on open floor (a carved prop at the edge splits a side), and each stretch is split into ~0.6 m lanes because a link carries one agent at a time. A side re-links only when its floor changes and then holds still (a carving obstacle settles, a rebake: `SeamReclips`), or when a link stops joining its own two ends (`SeamHeals`). Unity's auto-traverse slides an agent ~0.35 m onto the link start, then crosses at ~2.35x its speed. For a walking-speed crossing, set `autoTraverseOffMeshLink = false` and move the agent along the link yourself.
- **Editing an existing frame** goes through the inspector's Layout Frames section (Edit → change objects → Save Frame). Never edit a prefab's objects "into" a frame by hand while an edit session is active — the save guard restores Base before any save; don't bypass it.
- **Previews**: `Previews/Frames/{prefab}@{frameId}.png`, excluded from Addressables, uploaded with `&frame=`. The Content Uploader shows framed attractions as a stack.
- **Legacy demo (not a model for new work):** the template's `Prefabs/Attraction.prefab`, also placed in `[StartHere].unity`, ships with a frames demo. It has a 14 × 16 ft Base, an `Example 11 × 14 ft` Custom frame, an `Example 8 × 12 ft (Minimum)` frame and Front/Left wall layers. A centred **play-area rug** sized per layout keeps each layout's shrink at its own size; without one, a sparse Base auto-shrinks below every frame and the frames are never chosen. Objects are `EXAMPLE …` and materials are in `Materials/Examples/`; creators delete them. The demo is rebuilt by `Tools/Builders/ExampleAttractionLayoutsBuilder.cs` (idempotent; in the SDK template repo).
- **Wire formats**: catalog packing `schemaVersion 9` adds `frames[]`; manifest schema 4 adds `PackageOccurrence.frameId`/`frames`; park docs carry `packing.frameId` (omitted for Base, never with `essentialOnly`). The DreamPark app, backend and phone app all understand these.

## Wall Layers (Oct 2026)

Spec: `WALL_LAYERS_SPEC.md`. Creator guide: `PACKAGES.md` § Wall layers.

"No walls" is the baseline; a **wall layer** is optional extras for one side of the footprint (Front +Z, Back −Z, Right +X, Left −X), used only when that side is seated on a rect edge the app found walled (≥ 50% `wall` spans, RECT_EDGES_SPEC.md). Stored on `AttractionTemplate.wallLayers` (`AttractionWallLayer`: `id` = `w` + 8 hex, `side`, frame-style `overrides`, `authoredFootprintMeters`). **List order is priority.**
- **Runtime:** `ApplyLayout(frameId, scale, AttractionWallMask)` / `SetWalls(mask)`. Wall layers are resolved last in `ApplyLayoutInternal` (after the frame and its bake), per transform: the first walled layer in list order that overrides it wins; unwalled → frame/Base pose. Root-level positions keep their distance from their side's edge and their relative place along it at the applied footprint (`AnchorToSide`); nested objects ride with their parent. An attraction with wall layers always runs on the layout pipeline (`UsesLayoutPipeline`), frames or not, and its bake skips objects inactive in Base and records Base states for every wall-layer target.
- **Content API:** `Walls`, `WallsWire`, `HasWall(side)`, `HasWallNamed("front")`, event `WallsApplied`; Lua `onwalls(walls)` in `LuaBehaviour` (fired after `start()` and on change).
- **Authoring:** inspector section "Wall Layers" (`Editor/AttractionWallLayersInspector.cs`); Edit reuses `AttractionLayoutFrameEditSession` (`BeginWallLayer*`, state `wallLayer = true`) with a ghost wall. Bake runs `FindWallCombinationIssues` (every side combination × Base and every frame) and warns on new overlaps.
- **Wire:** catalog packing gets additive `wallSides: ["front", …]` (priority order, omitted when none); park docs carry `packing.walls: "front,left"` (omitted when none). The app resolves walls at seat time from the scanned rect's borders.
- **Rect edges v3** (`RECT_EDGES_SPEC.md`, Oct 15 2026; replaces the v1/v2 rect-openings format): every rect side is spans of `wall` / `opening` (RoomPlan door/opening, or `adjacent` where two rects touch with no confirmed wall) / `unknown` (default, passable, never walled), stored `{v:3, minX:{length, spans:[{a,b,kind,type?,height?,offset?,window?,touches?}]}}`. `AttractionTemplate.RectOpenings` (`AttractionRectOpenings`: `Spans(side)` of `AttractionRectSpan`, `KindAt`/`IsWall`/`IsOpen` (= not wall)/`IsOpening`/`IsUnknown(side, t)`, `Openings` (door/opening/adjacent), `Passages` (not wall), `SpanAt`, `Links` (touches), `IsWalled` (≥ 50% wall), `IsOpenArea`, `LocalPoint`, `Widest`), event `RectChanged`, Editor preview / `SetRectOpenings` injection with spans (old v2 data converts on read). Lua `dp.rect_openings()` (per side `spans`/`openings`/`passages`/`links`, top `version` 3, `open_area`), `dp.rect_spans(side)`, `dp.rect_span_at(side, t)`, `dp.rect_is_wall/is_open/is_opening/is_unknown/point(side, t)`, `dp.rect_is_open_area()`, hook `on_rect_changed(o)`. No data = every side unknown. The Lua table carries `source`, `entry_side`/`entry_reason` and per side `wall_height`, `wall_offset` and `walled`. Routes walk passages with a `to` and join open-area rects directly. Park Simulator → Arena Rect seats the Arena test on a simulated rect, so all of this plays in the Editor.
- The old wall-only path (`LevelTemplate.walls`, `PropTemplate.wallSide`, `WallHeightMeasurement`, the uploader's `walls`/`wallHeightFt`, the app's old wall solver) is gone. Don't reintroduce content that requires a wall.

## Entries & Exits (Sept 2026)

`AttractionTemplate` carries **portals** (`AttractionPortal`: `kind` Entry|Exit + `position`) along its footprint edge — placement metadata only, nothing spawns. Intended use: orient attractions in a space, plan entry → exit flow, and line entries up with real detected doorways.
- **`position` is a 0–1 perimeter coordinate** (`AttractionPortalGeometry`): 0 = middle of **-Z**, clockwise seen from above (same sense as Unity yaw): 0.25 = -X, 0.5 = +Z, 0.75 = +X, corners at the eighths. Linear along each edge of a square stretched to the footprint, so a portal keeps its relative place on its edge when a layout shrinks/grows X and Z independently. ×360 reads as degrees.
- **Default: one Entry at 0** (middle of -Z — most attractions face that way). It is the field initializer, so prefabs saved before portals existed load with it too. Creators may add many entries/exits or remove them (the inspector warns when a layout has no entry).
- **Per layout frame**: Base owns `portals`; each `AttractionLayoutFrame` follows Base unless `overridePortals` (then its own `portals`). Runtime: `GetPortals(frameId)`, `CurrentPortals`, `GetPortalWorldPose`. `CopyLayoutFrames` and `DuplicateFrame` carry both fields — keep it that way if frames gain another copy path.
- **Authoring**: inspector section "Entries & Exits" (layout picker drives the debug preview), always-on arrow gizmos (blue entry, red exit, pointing the way guests walk), drag handles on the selected attraction (Ctrl/Cmd snaps to 1/16). `Assets/DreamPark/Editor/AttractionPortalsEditor.cs`.
- **Wire format**: catalog packing profile gets `portals: [{kind:"entry"|"exit", position}]` at top level (Base) and on every `frames[]` entry (always the frame's EFFECTIVE list). Additive, `schemaVersion` stays **9**; absent = the default single entry. The backend normalizes it leniently (bad entries dropped, never the profile; ≤ 32 per layout). The app does not consume it yet.

## Routes: walking between attractions (Oct 2026, Phase 1)

Creator guide: `ROUTES.md`. Rect openings with a `to` are the doors of a park graph, and Routes plans
over them, in Lua only (`dp.*`, no components):
- `dp.ports()`: this attraction's Entries & Exits for the applied frame, each resolved against the
  rect openings (`opening`, `blocked`, `t`, world `pos`/`fwd`). Portals can be `Both` and carry an
  optional `width`/`tag`.
- `dp.neighbors()`: the rects and attractions through my openings. `dp.rect()`: my rect in world metres.
- `dp.route(a, b)`: a walk through real doorways. It returns `ok`, `pts`, `len`, `hops`, `doors`, and
  `at(s)`/`project(pos)`. `source` is 'doors', 'grid' or 'straight'.
- `dp.tour(stops, {loop, keep_order, start, spur_penalty})`: the order plus each stop's entry and
  exit door, minimising the walk (closing leg included).
- `dp.route_plan(name)`: an optional leader-published plan on `dp.shared_state`.
- **Deterministic from park data**: every headset computes the same tour with no messages. With no data,
  routes are straight lines.
- **Built on a pure planning library** (`dp.routes`, with hooks for a
  title's own rules). Hard caps: 10 stops per tour, 64 door points, and a 30 m / 20k-cell grid.
- Tests (SDK repo): `lua Tools/routes-offline/test_routes.lua`.
- Planned (in the app): a C# route service on the park graph, NavMesh links through openings, and
  Adventure placement by route.
