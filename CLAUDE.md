# DreamPark SDK — Content Creator Template

## What This Is
SDK template for third-party game developers. A complete Unity 6.3 LTS (6000.3.23f1) project. Creators fork this repo and place their game content in Assets/Content/{GameName}/.

## Unity CLI
The template uses Unity's standard `com.unity.pipeline` package, so an agent can drive the open Editor from a
shell with the `unity` CLI. Always aim it at THIS project:

```
unity command --project-path . <command> [--param value ...]
unity command --project-path . --timeout 180 <long command> ...   # the CLI default is 30 s
```

**Agents, read first:**
- **How to upload** (beta/release builds, new titles, the `/developer` website): § Shipping → "Agent access (sign in, upload, website)". You use your own human-approved agent key via `dreampark_agent_request` → `dreampark_agent_wait` (the owner clicks Approve in the email) → `dreampark_content_publish --target beta|release`. Each title is approved once and the project remembers every title's key. Never ask for, type or relay the 6-digit code. `Assets/DreamPark/RELEASE_AUTOMATION.md` is deprecated; don't follow it.

**Drive the Editor through the CLI**, never through screen control or computer use:

| Need | How |
|---|---|
| Any Unity command | `unity command --project-path . <cmd>`. While the Editor reloads (domain reload, entering Play mode) the command cannot connect: wait a few seconds and retry. A **timeout** is different: the operation may still run, so check its result before retrying. |
| Prefab/scene/asset generation, editor harnesses | A builder in `Tools/Builders/*.cs`, run with `unity command --project-path . run_script --file Tools/Builders/X.cs --entry X.Build`: compiled in memory in <1 s, no domain reload, full `UnityEditor` API. Keep builders idempotent; rebuild only what changed. |
| Screenshots | `unity command --project-path . capture_game_view --format json` (or `capture_scene_view`) returns the PNG as base64; decode it to a file outside `Assets/`. Never pass `--save_path` (it lands under `Assets/`). |
| Checking Lua | The Lua surface gate (§ The Lua surface gate) runs on every upload and player build. Play the change before calling it done. |

- **Play session:** `unity command --project-path . editor_play` → test → `editor_stop`. Only one agent may be in Play mode (or open scenes) at a time; coordinate if several agents share one Editor. Keep it under 5 min; always stop, even on failure.
- **Never add C# under `Assets/` during active work**: it reloads the Editor for every agent (~20 s). Harnesses and generators are builders in `Tools/Builders/`. Staging folders must end in `~`.
- **Never save, revert or discard a scene you did not open.** Commands that load scenes (`run_tests`, `open_scene`, builds) raise the modal "Scene(s) Have Been Modified" when a scene is dirty; use your own test scene, and run pure-logic tests offline.
- **A modal dialog blocks every command** ("Main thread operation timed out"): report it with its exact text at once; don't retry in a loop. `eval --timeout` is in milliseconds.
- **Tag your log lines** with your track and filter `get_console_logs` by it. Keep scratch files in a per-agent folder, never under `Assets/`.

Meta XR SDK 205's experimental Agent Bridge/DevAgent is separate and must remain disabled.
`DreamParkOpenXRSetup` clears its machine-local address and token during setup and immediately
before/after builds so those values cannot enter shipped content.

## Template Structure
```
Assets/
├── [StartHere].unity  ← Startup scene, auto-opened on first project open (StartupSceneOpener,
│                        configured in Assets/.dreampark-editor.json, mode FirstOpenOnly;
│                        re-point via DreamPark → Startup Scene...). Meta building blocks
│                        pre-wired + Player.prefab and Attraction.prefab instances.
├── DreamPark/         ← SDK source. SDK-owned: replaced on every SDK update, so never
│                        edit it for one title.
├── Content/
│   ├── Sample/                ← Bundled worked example (2 attractions, 5 props, Sample.unity,
│   │                            8 Lua scripts). Deliberately NOT gated for browsing/editing;
│   │                            RESERVED only in that it is never the creator's content and
│   │                            cannot be published. See ContentFolders.IsSample.
│   └── YOUR_GAME_HERE/        ← Renamed via the in-editor setup popup (ContentIdSetupPopup, auto-opened by PlaceholderContentDetector; letters+digits, starts with a letter, 2–64 chars). Folder name = content ID. Also reserved until renamed. Multiple content folders may coexist — each is an independent package; the Content Uploader's dropdown picks which to publish.
│       ├── Prefabs/            ← Player.prefab + Attraction.prefab ship in the template;
│       │                         Phone Player.prefab = the optional phone rig (built by
│       │                         Tools/Builders/PhonePlayerBuilder.cs; used only when the
│       │                         Phone Player slot is set — see § Phone players & gestures);
│       │                         A_*.prefab attractions (AttractionTemplate root),
│       │                         P_*.prefab props (PropTemplate root);
│       │                         Game Container.prefab = the Game Manager (generated once)
│       ├── DreamSequence/      ← Start / Overlay / Game Over Level prefabs (creator-owned)
│       │                         + generated Dream Sequence Package.asset (see § Packages)
│       ├── .dreampark-*.json   ← package authoring (library, arena, dream-sequence,
│       │                         sequence = ADVENTURE). Commit them.
│       ├── Previews/           ← {prefabName}.png tile art
│       ├── Scripts/            ← Game-specific C# (minimal — prefer Lua) + .lua.txt
│       ├── ThirdParty/         ← Only used assets (git-tracked, shipped in builds)
│       └── ThirdPartyLocal/    ← Imported packages land here (excluded from builds by
│                                 ContentProcessor — but NOT actually gitignored, despite
│                                 what several comments in the repo claim)
```

There are no `1. Scenes/` or `2. Features/` folders — that layout predates the current template.

## SDK files and docs in your project
`Assets/DreamPark/` is SDK-owned and is replaced on every SDK update. Customize through your own content folder,
prefab-instance overrides and Lua, never by editing SDK files. `#if DREAMPARKCORE` blocks in SDK source are
DreamPark-app-only code, compiled out of the SDK; ignore them.

**These docs ship with the SDK.** In a game project, each SDK update (once per SDK version, tracked in the committed
`ProjectSettings/DreamParkSdkDocs.json`; set `"enabled": false` there to opt out) rewrites the root copies of the SDK
docs from `Assets/DreamPark/Docs/`, except the game's own `README.md`. They carry a "managed" header: don't edit them,
your edits are overwritten. It also keeps a managed `@Assets/DreamPark/Docs/CLAUDE.md` import block at the top of the
game's own `CLAUDE.md`. That block states that the project's own notes win wherever they disagree with the SDK guide,
and everything below it belongs to the game.

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

## Layout Frames (Sept 2026)

Spec: `LAYOUT_FRAMES_SPEC.md`. Creator guide: `PACKAGES.md` § Layout frames.

**One attraction = unique content, every size (Oct 2026).** An attraction is defined by what it IS — its theme, look,
feel and gameplay — never by its size. Support every room it can play in (a 6 × 10 ft closet up to a basketball court) with
layout frames on ONE `AttractionTemplate`: a Minimum frame for the smallest viable version, custom frames for the sizes that
need their own arrangement, grow limits for the rest. **Never ship the same content at different sizes as separate attractions**
(`A_Arena_Small` / `A_Arena_Large`, `A_X_8x12` / `A_X_20x30`): they split revenue history and previews, fill the Arena with
duplicates, and every fix has to be made N times. Separate attractions must differ in content, not footprint. Check the frame
coverage chart before adding an attraction; if a size gap exists, add a frame.

A **layout frame** is a hand-authored arrangement of an attraction at one **width × length**, stored on `AttractionTemplate` as overrides (active + local TRS, by hierarchy path) on top of the prefab. **Base** (id `base`) is the prefab as authored — never a stored frame. At most one **Minimum** frame (id `minimum`) — the smallest viable version; it REPLACES `essentialProps` (hidden; legacy lists are migrated into a Minimum frame on bake, once). Custom ids are `f` + 8 hex, generated once — never derive ids from sizes.
- **Every frame gets its own shrink/grow bake** (`AttractionPackingBaker.Bake` bakes Base + all frames). With frames, bakes ignore objects inactive in that frame. Frame-less attractions bake and run exactly as before.
- **Runtime picks ONE frame and never blends**: the ladder (`GetFrameEnvelopes`, largest → smallest authored area) → the first frame whose shrink fits → scale within that frame's range (`TrySelectFrame` / `ApplyLayout(frameId, frameScale)`). Frame scale is relative to that frame's own size. Legacy `ApplyPackingVariant(scale, essentialOnly:true)` maps to the Minimum frame.
- **Keeping helper visuals out of the bake** (Oct 2026): an object whose name contains `[nopack]`, or that runs a Lua script containing a `-- @packing-ignore` comment, is ignored by the shrink/grow bake with everything under it (`PackingIgnore` in `AttractionPackingBaker.cs`; names containing `gizmo` still are too). A naming/annotation convention on purpose — no component.
- **Walking in from the gap ground: `-- @gapfiller-links`** (Oct 2026): a `-- @gapfiller-links` comment in any LuaBehaviour script inside an attraction makes `GapFiller` add bidirectional NavMeshLinks across the attraction's footprint edges, so agents on the gap ground can path in and out (`SeamLinkCount`). No marker means no links. The links are clipped to the stretches where both ends are on open floor (a carved prop at the edge splits a side), and each stretch is split into ~0.6 m lanes because a link carries one agent at a time. A side re-links only when its floor changes and then holds still (a carving obstacle settles, a rebake: `SeamReclips`), or when a link stops joining its own two ends (`SeamHeals`). Unity's auto-traverse slides an agent ~0.35 m onto the link start, then crosses at ~2.35x its speed. For a walking-speed crossing, set `autoTraverseOffMeshLink = false` and move the agent along the link yourself.
- **Author frames through the inspector's Layout Frames section** (Edit → change objects → Save Frame). Never edit a prefab's objects "into" a frame by hand while an edit session is active — the save guard restores Base before any save; don't bypass it.
- **Previews**: `Previews/Frames/{prefab}@{frameId}.png`, excluded from Addressables, uploaded with `&frame=`. The Content Uploader shows framed attractions as a stack.
- **Worked example:** the template's `Prefabs/Attraction.prefab`, also placed in `[StartHere].unity`, ships with a demo. It has a 14 × 16 ft Base, an `Example 11 × 14 ft` Custom frame, an `Example 8 × 12 ft (Minimum)` frame and Front/Left wall layers. A centred **play-area rug** sized per layout keeps each layout's shrink at its own size; without one, a sparse Base auto-shrinks below every frame and the frames are never chosen. Objects are `EXAMPLE …` and materials are in `Materials/Examples/`; creators delete them. The demo is rebuilt by `Tools/Builders/ExampleAttractionLayoutsBuilder.cs` (idempotent).
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
- Tests: `lua Tools/routes-offline/test_routes.lua`.
- Planned (in the app): a C# route service on the park graph, NavMesh links through openings, and
  Adventure placement by route.

## Packages: Arena, Sequence & Adventure (Sept 2026)

Creator guide: `PACKAGES.md` (repo root). Engineering spec: `PACKAGE_RUNTIME_SPEC.md` (predates Arena). Website: `dreampark.app/docs?s=packages`.

A **package** is how a title turns whatever floor an operator scanned into a playable experience without losing authored intent (order, Start/End, Groups, `required`). The app loads a **package occurrence**, not a random prefab. All three are authored in `DreamPark → Content Uploader` → **Packages** (tabs Arena · Sequence · Adventure) from the same **Park Assets** library, and all three share one **Game Manager** and one **Player**.

| | Arena | Sequence | Adventure |
|---|---|---|---|
| Space | any scanned rect, ≥ 1×1 ft | one room, authored 12 × 18 ft (`DreamSequenceTemplate.StandardWidthFeet/LengthFeet`), packs within the levels' bakes | whole scanned park |
| Runtime | per rect: largest bucket that fits, its first mounted entry — the bucket decides fit, an entry that doesn't fit exactly uses its smallest shrink (`SpaceMapPacker.PackArenaPackage`) | assembled root streams ONE attraction at a time between Start and Game Over (`DreamSequencePackageRuntime` + `DreamParkPackageHost` + `DreamLevelLoader`) | Start → ordered stages → End along a walk route, each faces its predecessor, all deployed (`PackAdventurePackage`) |
| On failure | fails (`no_arena_bucket_fits`) — no fallback | fails (`no_room_supports_sequence`) | **atomic** fallback: Sequence (if authored) → Arena (if it has buckets) → fail |
| Required? | optional | optional (Oct 2026) — authored only when the saved organizer has ≥1 visible attraction that fits 12 × 18 ft (`DreamSequencePackageCompiler.IsAuthored`); otherwise left out of the upload with a console warning | optional; needs BOTH Start and End Point, otherwise left out of the upload with a console warning |
| Authoring file | `.dreampark-arena.json` (schema 2) | `.dreampark-dream-sequence.json` | `.dreampark-sequence.json` ⚠ historical name — it IS Adventure |

**All three are optional; a title needs at least ONE playable package** (`dream-sequence-required` blocks only when it has none). An unfinished Sequence or Adventure is left out, never shipped broken and never blocking — better to ship just an Arena than a broken Sequence or Adventure.

Library file: `.dreampark-library.json` (Groups, hidden). `ContentSequenceStore` (schema 6) owns library/Sequence/Adventure JSON; `ArenaPackageStore` owns Arena. Commit all of them.

### Compile output (Test or upload — never hand-edit)
- `Assets/Content/{id}/DreamPark Package Manifest.asset` — `DreamParkPackageManifest` (schema 5; readers accept 3–5 — `DreamParkPackageManifest.IsSupportedSchema`), address `{id}/Assets/DreamPark Package Manifest`. One `PackageRecipe` per kind (`arena`/`adventure`/`sequence`; null = absent, which is different from empty): `playerAddress`, `phonePlayerAddress` (schema 5, empty = phones get no rig), `gameManagerAddress`, `sequenceDefinitionAddress`, ordered `occurrences` (`occurrenceId`, `resourceAddress` in ContentProcessor form, `role` start|stage|end|prop, `required`, `groupOccurrenceId`, packing envelope), `groups`, `arenaBuckets`. `packageRevision` = SHA-256 of the manifest. Built by `DreamParkPackageCompiler.Compile`. **Runtime reads THIS, never the backend package endpoint** (that one is approved-release-only).
- `Assets/Content/{id}/DreamSequence/Dream Sequence Package.asset` — `DreamSequencePackageDefinition`: refs to Start/Overlay/Game Over/Game Manager prefabs, level addresses, `minimumRoomMeters`/`maximumRoomMeters`. Built by `DreamSequencePackageCompiler`. Manifest Sequence order and definition order are asserted equal.
- Upload also bakes every attraction's packing variants and layout frames (`AttractionPackingBaker.BakeAllInContent`), and sends the backend a normalized `packages` object (schemaVersion **2**, or **3** only when an Arena entry pins a non-Base layout frame via `frameId`; Arena as `buckets[]`, smallest→largest) via `commitUpload` — `BuildPublishedPackagesJson`. The backend's `resourceName` there is the catalog asset-path STEM, not the address.
- **No per-title `Dream Sequence.prefab` exists.** Don't generate one, don't look for one.

### Arena rules
- Every Attraction + Prop (except the three Sequence special levels, and `ThirdPartyLocal/`) is included automatically in ONE bucket: baked shrink footprint → feet, **floored** to whole feet, rotation-normalized short × long (`ArenaPackageStore.CandidatesForPrefab`). A plain `LevelTemplate` is NOT a candidate. An attraction with layout frames yields one candidate PER FRAME (key `{guid}` for Base, `{guid}:{frameId}` otherwise — store schema 3), each at its own frame's shrink; two frames of one prefab in the same bucket keep only the larger.
- **The bucket is authoritative for fit**: the app places an entry in a space of its bucket's size even when its exact footprint is up to a foot larger (it uses its smallest shrink). Don't "fix" this back to exact fitting.
- Order inside a bucket = priority; the first entry wins. Authors can override a bucket size (`sizeOverrides`) or exclude (`excludedGuids`) — both keyed by candidate key.
- Arena occurrences are never `required` (the backend rejects `required:true`). Occurrence ids are `arena-{guid}` (Base) or `arena-{guid}-{frameId}`, and every Arena occurrence pins `frameId` (`base` or the frame).
- An active Arena replaced by the SAME title's Sequence/Adventure needs `promotionConsent:true` on `PACK_PARK`; without it the app refuses with `PromotionConsentRequired`.

### Sequence rules
- Slots are 1-based: `1` Start Level, `2…N+1` streamed attractions (organizer order, Groups flattened), `N+2` = `LevelCount` Game Over.
- Compatible = authored size OR the baked shrink footprint of ANY layout frame fits 12 × 18 ft either way round (`DreamSequenceCompatibility.IsCompatible`; the runtime picks the largest fitting frame via `TryChooseFrame`, and the minimum room uses each level's smallest fitting frame). Incompatible optional items are dimmed + skipped; incompatible `gameRequiresAttraction` items throw at compile. **Props never become Sequence levels.**
- Minimum room = max shrink footprint of Start/Overlay/Game Over + every included level; maximum = max grow. One unshrinkable object on the Start Level raises the whole game's minimum room. An EMPTY Game Over is deliberately baked at a 0.1 m shrink (`AllowEmptySpecialLevelShrink`).
- Each level is packed between its own shrink/grow limits (`ApplyPackingVariant`), rotated 90° when needed, and **never stretched** past its grow limit. The package root owns the one calibrated floor; its grade is transferred to each level; level cutouts stay per level.
- Level change: `transitionDelaySeconds` (2.5 s) minimum, swap at the END, after download + fit check; newest request wins (`requestVersion`); failure leaves the level unchanged and raises `LevelChangeFailed`.

### The special levels (the Sequence boilerplate)
`Assets/Content/{id}/DreamSequence/{Start Level|Overlay Level|Game Over Level}.prefab`, generated by `DreamSequenceGenerator.EnsureScaffold` when the Sequence tab opens. Creator-owned `AttractionTemplate`s, Custom 12 × 18 ft, `generateFloor = false`, each with a `Sequence Floor Border` (an `AttractionPackingFollower`, XZ-independent).
- **Start Level**: `START — Super Adventure Land Button` = instance of SDK `RuntimeAssets/DreamSequence/StartButton/Start Button.prefab` (trigger + `LuaBehaviour` running `DreamSequenceButton.lua.txt` with string injection `action=start` + `DreamSequenceButtonFeedback` squashing child `Press Visual`, ~2 s cooldown via `dp.button_press()`). Calls `dp.game_manager().start_game()`.
- **Overlay Level**: `Default 3D Level Navigation` (instance of `Overlay/Elevator Controls.prefab`, children `BACK` action=back and `FORWARD` action=forward) at local x −1.72 facing in, plus `Default Level Transition` (`Overlay/Transition/Level Transition Effect.prefab`). Auto-visible only on ordinary levels (not Start/Game Over); no floor of its own — its FloorAnchors bind to the active level's floor.
- **`Default Level Transition` is found BY NAME** (`DreamSequencePackageRuntime.FindRecursive`) and copied out to `Persistent Level Transition` with `OptimizedAFIgnore`. Renaming it silently removes every transition. It must contain a `ParticleSystem`.
- **Game Over Level**: empty by default.
- Scaffold refresh (`NeedsLevelRefresh`) resets size to Custom 12 × 18, removes `DreamSequenceSpecialLevelTemplate` markers and children named `12 ft × 18 ft Level Floor` / `LevelFloor` / `Game Over Backdrop`. Migrations are one-time, stamped in the importer's `userData`, so creator edits (including deleting the default button) survive. Deleting a special-level prefab regenerates it.
- Agents: customize with prefab-instance overrides or creator-owned objects in the content folder. Do not edit the shared control prefabs under `Assets/DreamPark/` for one title — they are SDK files (replaced on SDK update).

### Adventure rules
- Explicit Start Point + End Point required (`hasExplicitEndpoints`); stages may be Attractions, Props or Groups; repeats are distinct occurrences; `hidden` placements are skipped. A non-prop occurrence without a packing bake throws at compile.
- The app commits only after every required occurrence has a slot AND spawned; otherwise it rolls back and tries the Sequence (if authored), then the Arena (if it has buckets), then fails. Occurrences are culled independently; the manager is not.
- `DreamParkPackageHost.LoadLevel` on Adventure/Arena **never hides anything** — it moves `CurrentLevelIndex` and fires `LevelActivated` (a progress marker for the Game Manager).

### Game Manager
- File: `Assets/Content/{id}/Prefabs/Game Container.prefab` (historical name; runtime address `{id}/Game Container`, spawned as `Game Manager`), script `Scripts/game-container.lua.txt`. Created once by `DreamSequenceGenerator.EnsureContainer` with `LuaBehaviour` + `NetId` + `OptimizedAFIgnore`. **Never overwritten** by compiles (`WriteStarterTextAsset` writes only when missing). The app adds `NetId`/`OptimizedAFIgnore` at runtime to older manager prefabs.
- One per package instance, created BEFORE attraction gameplay, parented under the package owner OUTSIDE every calibrated LevelAnchor, survives level swaps, exempt from distance culling but still paused/torn down with the park. Not a spatial parent; never contributes to a footprint.
- **Exists only when the title runs as a package.** Loose placements have no manager → `dp.game_manager()` is nil. Every caller nil-checks (the shipped button falls back to `dp.next_level()`).
- Default script exports `start_game`, `next_level`, `previous_level`, `load_level(index[, groupId])` (1-based absolute; 0-based inside a Group) and syncs navigation over its NetId (`sequence_state {slot, revision, writer}` + `sequence_sync_request` on relay connect; newest revision wins, ties by writer id). **Controls call the manager's `load_level`, not `dp.load_level`** — the raw call is local and unsynced.
- Title-wide rules/state → Game Manager. Body/senses/per-guest presentation, and anything that must also work in loose mode → Player. The injected `storage` proxy is a no-op on the manager (no GameArea above it) — use `dp.storage.game(contentId)`.

### Player rig in packages
- `FindPlayerAddress`: first prefab with `PlayerRig` under the content folder, preferring the one named `Player` → `{id}/Player`, and NEVER the Phone Player slot prefab or any prefab named `Phone Player`. A recipe without one fails to compile ("package has no PlayerRig prefab"). Keep exactly one headset rig.
- **Phone Player slot** (optional, one per title, shared by all three packages like Player): `.dreampark-phone-player.json` `{schemaVersion:1, prefabGuid}` (`PhonePlayerStore`; absent file = empty). Empty (default) = phones get NO rig, only the Game Manager. Set = phones spawn that PlayerRig prefab instead of Player; headsets ignore it. Compiled to every recipe's `phonePlayerAddress` (`{id}/{prefab name}`); the slot prefab must carry `PlayerRig` and live in this content folder, else compile error. `DeliveryIndexGenerator` puts it in each recipe's bootstrap services. Uploader: Packages → PHONE PLAYER (optional) field + "Use template Phone Player".
- Created once per package owner before gameplay, outside calibrated anchors, claimed via `ContentManager.TryClaimPlayerLoad`; an Adventure↔Sequence replacement BORROWS the live rig and transfers the claim at commit. `DreamParkPackageMembership` links the Player, the manager and every Adventure/Arena occurrence to their `DreamParkPackageHost` (they are not its children).
- Template rig: `Head` (HeadTracker), `Body` (BodyTracker), `Feet` (FeetTracker), `LeftHand`/`RightHand`/`BothHands` (HandTracker, with `… Handheld Pivot` children), example effect children. Optional `RemoteRig` child = what other players see (see Multiplayer).

### Lua package API (scope-local; resolved from the CALLING object — ancestor host, else `DreamParkPackageMembership`; never a global "current package")
`dp.package()` · `dp.game_manager()` (scope) / `dp.game_manager_object()` · `dp.container()` / `dp.container_object()` (aliases) · `dp.is_arena()` / `is_sequence()` / `is_adventure()` · `dp.current_level_index()` · `dp.level_count([groupId])` · `dp.levels([groupId])` · `dp.groups()` → `{id,name,sourceId,count}` (Sequence only) · `dp.level_slot(i, groupId)` · `dp.load_level(slot[, groupId])` · `dp.preload_level` / `dp.level_ready` (Sequence stages only) · `dp.level_loading()` · `dp.level_error()` (`invalid-or-inactive-slot|missing-loader|download-or-load-failed|load-exception|room-fit-failed|spawn-exception|spawn-failed|presentation-failed`) · `dp.on_level_loaded(fn)` / `off_…` (Sequence: one drawn frame after the level appears) · `dp.on_level_presented(fn)` / `off_…` (same frame the level becomes visible, before it is drawn; `DreamParkPackageHost.LevelPresented`, Oct 2026) · `dp.on_level_failed(fn)` / `off_…` · `dp.on_occurrence_removed(fn)` / `dp.off_occurrence_removed(h|fn)` (`fn({occurrence_id, resource, anchor})` when one attraction of this package goes while the title stays, BEFORE its anchor is destroyed; `DreamParkPackageHost.OccurrenceRemoved`; scoped to this package, drops with the script; call guarded `if dp.on_occurrence_removed then … end`, Oct 11 2026) · `dp.show_overlay(b)` · `dp.overlay_auto_visibility(b)` · `dp.next_level()` / `dp.previous_level()` (delegate to the manager; false if absent) · `dp.button_press()`. Bound in `DreamParkLuaAPI.BindScope`; C# host is `DreamParkPackageHost`. Always `off_level_loaded` in `ondestroy()` — the host's C# event holds the delegate.

### Checks & tests
- `dream-sequence-required` (id kept for history; it no longer requires a Sequence): clean when no Sequence is authored and the title has an Arena or Adventure; **Blocking** "Title has no playable package" when it has none of the three; for an AUTHORED Sequence, the same validations as before (Blocking): definition compiled + current order, all four parts present, special levels baked, no oversize required attraction. Fix: *Rebuild Sequence Package*.
- `package-references` (Blocking): every placement in an authored Sequence or a complete Adventure resolves to an Attraction/Prop in this content folder (broken refs stay visible in the organizer on purpose; the compiler refuses them rather than silently dropping required content). Stale refs inside an unfinished Adventure (no Start or End) don't block — that Adventure is left out anyway.
- Uploader buttons `▶ Test Arena|Sequence|Adventure in Park Simulator`. EditMode suites: `DreamParkPackageCompilerTests`, `DreamParkPackageHostTests`, `ArenaPackageStoreTests`, `ContentSequenceStoreTests`, `DreamSequenceGeneratorTests`, `DreamSequencePackageRuntimeTests`, `DreamSequenceRequiredCheckTests`, `ParkSimArenaPreviewStateTests`, `ParkSimPackageTestTests`.

## Materials & Shaders — use the DreamPark universal shaders for EVERYTHING
DreamPark is mixed reality. Every pixel of virtual geometry has to be clipped by the guest's real room via Meta's Depth API, or it draws on top of their hands, furniture and walls. That integration lives in the shader, so **shader choice is a correctness requirement, not an art preference.**

**The three shaders. Use one of them for every material you ship:**

| Use for | Declared shader name | Source |
|---|---|---|
| Anything lit (props, environment, characters) | `Shader Graphs/DreamPark-UniversalShader` | `Assets/DreamPark/Shaders/DreamPark-UniversalShader.shadergraph` |
| Anything unlit / flat / emissive / UI-in-world | `Shader Graphs/DreamPark-Unlit` | `Assets/DreamPark/Shaders/DreamPark-Unlit.shadergraph` |
| Every ParticleSystem material | `DreamPark/Particles` | `Assets/DreamPark/Shaders/DreamPark-Particles.shader` |

These names are the canonical constants (`DreamParkShaderNames`, in `MaterialConverter/MaterialPlan.cs`); the pre-upload gate matches on them exactly.

- **Never ship Standard/URP-Lit, URP-Unlit, marketplace, Hovl/Kriptofx-style, or asset-pack shaders.** They compile fine and look fine in the Editor — the failure only appears on-headset, where the object refuses to be occluded.
- **Opaque ⇒ Alpha Clipping ON. Always. This is not a preference and not a default — it is enforced.** The occlusion subgraph drives its result into the fragment's **alpha**. A *Transparent* surface consumes that alpha through the blend, so occlusion works with no further setup. An *Opaque* surface has nothing downstream that reads alpha — the clip is the only consumer — so with Alpha Clipping off the occlusion value is computed, written and thrown away. The material renders at full opacity over the guest's hands, furniture and walls.
  - **This is the failure mode that survives every other check.** The shader is correct, so `meta-occlusion` passes. The Editor and Play mode look perfect. It is wrong only on a headset, only in passthrough, and only after upload.
  - **Two pieces of state, and they can disagree.** `_AlphaClip` (float) is what the inspector draws; `_ALPHATEST_ON` is the keyword that actually selects the clipping variant. `material.shader = x` carries over same-named floats *and* the old keyword set, so a converted material routinely lands with one on and the other off — including the case where the inspector shows the toggle ON while the shader is not clipping. **Never test just one.** `DreamParkMaterialRules.IsSatisfied(mat)` requires both; use it rather than reading either property yourself.
  - **Enforcement, four layers** (`Assets/DreamPark/Editor/Shaders/`): `DreamParkMaterialRules` is the single definition of the rule; `DreamParkShaderGUI` re-establishes it on every inspector repaint, so toggling Alpha Clipping off on an Opaque material snaps straight back; `DreamParkMaterialPostprocessor` repairs any `.mat` on import, which covers script-created and hand-edited materials; `MaterialConverter` applies it on every conversion. The pre-upload gate is the backstop.
  - **What Alpha is actually built from** — both graphs multiply the occlusion result into it, which is why the clip is the consumer:
    - `DreamPark-UniversalShader`: `Alpha = baseTex.a × (_opacity × Occlusion)`, clip threshold from the exposed `_alphaClipping` (Range 0-1, default 0.5).
    - `DreamPark-Unlit`: `Alpha = baseTex.a × Occlusion × _baseColor.a`, threshold is an unexposed constant 0.5.
    - There is **no `_Cutoff`** on either — that is URP Lit's name and a Shader Graph does not get one for free. Use `DreamParkMaterialRules.ThresholdProp`.
    - Consequence: on an Opaque material `_opacity` / `_baseColor.a` are dead weight until clipping is on, then they are live. `DreamParkMaterialRules` resets them to 1 as part of the repair (unconditionally — a threshold test is not enough, since the scalar is multiplied by the texture's alpha), so enabling clipping fixes occlusion and changes nothing else.
    - **The one thing the repair cannot fix**: an albedo whose alpha channel is empty or garbage (some asset packs ship one). Enabling clipping makes that object vanish in the Scene view. That is not a bug in the fix — it means the material could never have been occluded and the texture needs fixing. Always look at the Scene view after a bulk repair.
  - **If you need soft blending, switch Surface Type to Transparent** — don't reach for the clip toggle. On Transparent, Alpha Clipping is genuinely optional and occlusion works either way.
  - **Fixing a whole project at once:** `DreamPark → Troubleshooting → Fix Opaque Materials Missing Alpha Clipping`. Import-time repair only reaches materials that get imported; existing ones sit as-is until touched.
- **Converting imported assets**: `DreamPark → Optimization → Material Optimizer...` rewrites third-party materials onto the three shaders and ports the feature keywords (emission, camera fade, distortion, dissolve, soft particles, vertex colour/blend modes, flipbook streams). Run it on every imported pack before doing anything else. Expect `ParticleSystemRenderer` Custom Vertex Streams to be reset to the default set by the post-convert pass — that is the fix for the "whole flipbook visible at once" symptom, not a regression.
- **If a custom shader is genuinely unavoidable, it must integrate Meta occlusion itself.** The requirement is the standard Meta environment-depth keywords — `HARD_OCCLUSION` / `SOFT_OCCLUSION` (and, for transparent output, `META_DEPTH_OCCLUDE_OUTPUT_PREMULTIPLY`) — reachable in the shader's compiled keyword space, driving the occlusion value into alpha. Copy the pattern from `DreamPark-Particles.shader:19-24` for HLSL, or drop the occlusion subgraph node into a Shader Graph the way `DreamPark-UniversalShader.shadergraph` does. When neither keyword is enabled the Meta macros expand to no-ops, so there is no cost on non-passthrough platforms.
- **Occlusion is NOT a per-material property.** `HARD_OCCLUSION`/`SOFT_OCCLUSION` are *global* keywords set at runtime by Meta's `EnvironmentDepthManager`/`OcclusionToggle`. `Material.IsKeywordEnabled(...)` and `material.enabledKeywords` will never tell you whether occlusion is integrated — the question is only ever answerable at the SHADER level. Don't write material-level checks and don't believe them. The Alpha Clipping rule above is *not* a counterexample: whether occlusion is **integrated** is a shader question, whether the material **consumes the result** is a material question, and both have to be true.
- **Transparent never writes depth (Oct 2026).** Both DreamPark graphs default Depth Write to Auto (Opaque on, Transparent off). The Material Optimizer sets Auto on every material it converts and takes the surface type from the source (URP `_Surface`, Standard `_Mode` Fade/Transparent ⇒ Transparent, else RenderType tag / render queue). A material left on "Force Enabled" keeps writing depth after every inspector repaint (URP's sync honours it) — set it back to Auto unless you mean it.
- **No placeholder checker.** An untextured source converts with an EMPTY `_baseTex` (samples white, so `_baseColor` is the colour). Materials converted before Oct 2026 may still hold `Default-Checker-Gray`; clear the slot.
- **Scrolling/tiling textures from Lua.** `_baseTex` is the `[MainTexture]` with Tiling and Offset on both DreamPark-Unlit and DreamPark-UniversalShader: use `mat.mainTextureOffset` / `mainTextureScale` or `mat:SetTextureOffset('_baseTex', v)`. No need to fall back to DreamPark/Particles. Identical look at default tiling.
- **What `meta-occlusion` / `opaque-alpha-clip` scan:** MeshRenderer, SkinnedMeshRenderer and ParticleSystemRenderer materials (main + trail), materials injected into LuaBehaviours, across every content root AND the Sequence Start/Overlay/Game Over levels (Oct 2026). LineRenderer/TrailRenderer materials are not scanned yet.
- **TextMesh Pro text is occluded too (Oct 2026).** TMP's stock `TextMeshPro/Distance Field` and `TextMeshPro/Mobile/Distance Field` have no Meta occlusion, so world-space text drew over hands in every title. `DreamPark/TextMeshPro/Distance Field` and `DreamPark/TextMeshPro/Mobile/Distance Field` (`Assets/DreamPark/Shaders/DreamPark-TMP_SDF*.shader`) are the same shaders plus `HARD_OCCLUSION`/`SOFT_OCCLUSION` (premultiplied into TMP's `One OneMinusSrcAlpha` output), the same properties and TMP's inspector, and render identically off-headset.
  - The SDK's default fonts (LiberationSans SDF + its Outline/Drop Shadow/Fallback materials, OpenSans) ship on them.
  - A creator material on a stock TMP SDF shader is a `meta-occlusion` **Warning** with a one-click lossless "Use DreamPark TMP shader" fix; bulk: `DreamPark → Troubleshooting → Use Occluded TextMesh Pro Shaders`. TMP's Font Asset Creator defaults new fonts to the stock shader — re-point them.
  - At runtime (app Oct 2026+) `DreamParkTmpOcclusion` moves world-space content text (3D `TextMeshPro`, or UI on a World Space canvas, under a `GameArea` and not under a `PlayerRig`) onto the occluded shader through a cached per-material clone. HUD/rig text, screen-space UI and the Overlay/SSD/Bitmap/Surface/Masking TMP shaders are left alone. Kill switch: `DreamParkTmpOcclusion.Enabled`.
  - Reference TMP shaders by NAME, never by GUID: TMP `.meta` GUIDs differ between projects.
- **Known SDK shaders that deliberately lack occlusion** and are allowlisted (`PreUploadScopes.OcclusionExemptShaders`, used by `MetaOcclusionCheck`): `Meta/EnvironmentDepth/DepthMask` (produces depth), `Meta/MRUK/MixedReality/InvisibleOccluderCulled` and `Meta/MRUK/MixedReality/InvisibleOccluder` (z-only occluders), `DreamPark/KeepAliveObject`, `Unlit/NormalOverlay` (editor debug viz). `Shader Graphs/LavaScreen` ships without occlusion and WILL be flagged — don't build content on it.
- Related gotcha that bites shader work: never mix a `MaterialPropertyBlock` writer and a `renderer.material` writer on the same object (see Multiplayer) — the MPB silently masks material changes.


## Particle effects — always Unity's built-in ParticleSystem
**Every particle effect ships as a Unity `ParticleSystem` component.** Not VFX Graph, not
a marketplace particle framework, not a custom mesh/shader/animated-quad trick, not
runtime-spawned billboard pooling of your own. If the effect is particles, it is a
`ParticleSystem` under the attraction prefab, authored in the prefab, enabled, with its
material on `DreamPark/Particles` (see Materials & Shaders).

This is a correctness requirement, not a taste call — three SDK systems are built on the
component and only on the component:

- **The Lua surface.** `UnityEngine.ParticleSystem` and `ParticleSystemRenderer` have AOT
  XLua wrappers in `Gen/`; `UnityEngine.VFX.VisualEffect` does not. A VFX Graph driven
  from Lua works in the Editor (Mono reflects) and dies on device (IL2CPP cannot), and no
  content re-upload fixes it — it needs an app rebuild and a store release. See The Lua
  surface gate.
- **Occlusion.** `DreamPark/Particles` carries the Meta environment-depth integration. A
  VFX Graph output or a custom particle shader draws on top of the guest's hands,
  furniture and walls, and looks perfect everywhere except a headset in passthrough.
- **OptimizedAF.** `LevelObjectManager` disables and re-plays distant particle systems by
  walking `GetComponentsInChildren<ParticleSystem>(true)` and restoring per-system play
  state. Anything that isn't a `ParticleSystem` is invisible to that budget and runs at
  full cost across the whole park. A system that must always run goes on an object
  whose script carries `-- @optimizer-ignore` (see What the optimizer guarantees).

Practical notes:

- Nested systems are fine and are handled individually — the manager keeps one settings
  entry per system in a flat array.
- Drive them from Lua with the ordinary component API (`sparkle:Play()`,
  `:Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear)`), injected via
  `LuaBehaviour` — don't reimplement emission in Lua update loops.
- Author the system in the prefab rather than spawning it in `start()` (see the
  authored-state rule) — a one-shot burst can still start stopped and be played from code.
- Imported particle packs: run `DreamPark → Optimization → Material Optimizer...` first.
  Expect `ParticleSystemRenderer` Custom Vertex Streams to be reset — that's the fix, not
  a regression.

## The attraction prefab IS the attraction (authored-state rule)

**Every environmental prop, character and interactable that belongs to an attraction
must exist, enabled, inside the attraction prefab.** Do not spawn the contents of an
attraction at runtime.

This is not a style preference. The preview PNG is rendered from the prefab, and that
image is the *only* thing a guest sees in the Attractions browser and on the consumer
map before they walk in. An attraction that populates itself in `start()` renders as an
empty room, and the tile tells the guest nothing about the layout, the activity, or the
point of the attraction. A prefab that looks empty in the Project view is a bug even if
it plays correctly.

The rule, concretely:

- **Author the world, then vary it.** Runtime code may *choose among*, *reposition*,
  *retheme* or *retire* authored objects. It must not *conjure* them. `SetActive(false)`
  on a slot the player never fills is fine; an empty `Transform` that gets an
  `Instantiate` on the first frame is not.
- **Something must always be on screen.** If content genuinely arrives late — streamed,
  server-driven, or picked per session — ship a **visible authored stand-in**: the start
  screen layout, a plinth with placeholder art, a roped-off area, silhouettes. Never a
  blank floor, never a missing-asset gap, never geometry that pops in on frame one.
- **Disabled-by-default is a preview bug.** If most of an attraction ships disabled, the
  preview is a lie. Enable a representative arrangement — one creature per pen, one prop
  per station — and let the runtime swap or hide from there.
- **Space the authored set honestly.** Objects must not interpenetrate or stack at the
  origin in the authored state. If the preview shows overlapping meshes, the attraction
  reads as unfinished regardless of how it plays.
- **Regenerate previews after any layout change** (`DreamPark - Troubleshooting -
  Regenerate Level Previews`) and *look at the PNG*. Ask: could someone who has never
  seen this attraction tell what it is and what they would do in it?

The test: **a guest should never be confused about what an attraction is, how it is laid
out, or what it is for, from its preview image alone.**

## Phone players & gestures (Oct 2026)

Guests can join on a **phone**: an AR window onto the park, touch input, no hands.
Runtime: `Scripts/Features/Phone/` (`DreamParkDevice`, `DreamParkGestures`, Editor mouse source).

- **Default: spectator.** A phone gets no rig unless the title fills the optional **Phone Player**
  slot (see § Packages → Player rig in packages). It still gets the Game Manager, so `dp.player()` is
  nil on a phone without a Phone Player — **title-wide systems belong on the Game Manager**, never only
  on Player.
- **The slot.** Content Uploader → Packages → PHONE PLAYER (optional). The template ships
  `Prefabs/Phone Player.prefab` (PlayerRig; Head/Body/Feet trackers, no hands; `Phone Controls`
  running `Scripts/phone-player.lua.txt`; an authored, stopped `Tap Ripple` ParticleSystem; a
  `RemoteRig/Head` phone slab = what headset players see of a phone user) but leaves the slot EMPTY.
  "Use template Phone Player" fills it in one click. Regenerate the prefab with
  `unity command --project-path . run_script --file Tools/Builders/PhonePlayerBuilder.cs --entry PhonePlayerBuilder.Build`.
- **Device API.** `DreamParkDevice` (the app calls `Set` at boot: iOS → Phone, Android → Headset;
  default Editor / Phone on iOS / Headset). Lua: `dp.device()`, `dp.is_phone()`, `dp.is_headset()`,
  `dp.device_of(playerId)` — other players' device rides `PlayerPresence`'s state resend as optional
  `dv` (absent on older clients → nil). Phone players are otherwise treated like any player.
- **What others see.** `RemotePlayer` draws a peer with a RemoteRig registered for THEIR device
  (`RemoteRig.RegisterTemplate(gameId, "phone"|"headset", template)` — the host app's job, since a
  headset never wears the Phone Player), else the local rig's RemoteRig when the devices match (or the
  peer's device is unknown), else only the name tag.
- **Gestures.** Sources (the app's touch source; the Editor mouse source) call
  `DreamParkGestures.Submit(DreamParkGesture)`; recognition is the source's job, the SDK routes:
  raycast from the camera through the screen point (triggers included; `Ignore Raycast`, `UI` and
  the local rig skipped), then
  - object hooks: `ontap(t)` (Tap) / `ontouch(t)` (TouchDown/Up) on every enabled LuaBehaviour of the
    hit object and its ancestors, nearest first; return `true` to stop the walk;
  - global handlers: `dp.on_tap` / `dp.on_touch` (phase `down`/`up`) / `dp.on_drag`
    (`begin`/`move`/`end`) / `dp.on_pinch` (`t.scale`, multiplicative), pumped like
    `dp.on_peer_join`. Inside a LuaBehaviour the owner defaults to the script.
  - `t = {kind, phase, x, y, dx, dy, scale, fingerId, position, normal, object, distance}` — x/y are
    0..1 from bottom-left; position/normal/object/distance are nil when nothing was hit.
  - `DreamParkGestures.Enabled = false` drops everything. The app sets it whenever the phone's AR view is not on screen or not synced to the park: the map/diorama view, relocalizing or lost tracking. Gestures therefore only reach a game while the guest is looking at the real, aligned park.
- **Editor simulation.** `DreamPark → Simulate Phone Player` (checkmark, EditorPrefs, applied on
  entering Play): `dp.device() == 'phone'`, the Park Simulator spawns each package's Phone Player (or
  no rig when the slot is empty), and the Game-view mouse becomes one finger — click = tap, drag =
  drag, scroll = pinch. Scene-placed rigs outside the Park Simulator are not swapped.
- **Phones are an addition, never a replacement.** Headsets keep hands and colliders; give phone
  players a way in (taps), never the only way in. Sample: `Assets/DreamPark/Samples/Phone/phone_tap_sample.lua.txt`.

## Hand-held objects (Oct 2026)
**Read `HANDHELD.md` before putting anything in a player's hand.** On a headset the hand node is the OpenXR wrist
(+Z toward the fingers, +Y back of the hand, thumb −X on the right hand / +X on the left), so an identity mount is 90° off.
- Author the prop as `Prop/Model` + `Grip` (or `PalmGrip`) + `Muzzle`, plus `GripLeft`/`PalmGripLeft`/`Grip2` when needed.
  The Grip pose IS the hold: pistol, wand, sword/haft/torch/palm/ball per `HANDHELD.md` §5.
  `HandheldKit.Normalize` writes it from any model; `HandheldKit.Validate` and `ValidateRig` must PASS.
- Mount from Lua: `dp.hold(prop, 'right'|'left'|'active'[, hand_node])` (re-mounts when the active hand changes; copies
  under `RemoteRig` hands mount themselves; on a rig with only a `BothHands` tracker a named side mounts on THAT hand via a
  follow node under the rig, not on the active hand — Oct 2026), `dp.hold_two_handed(prop, side, off_hand)`, aim with
  `local pos, fwd = dp.muzzle(prop)`, let go with `dp.release_hold(prop)`.
- Never a −1 scale. The template `… Handheld Pivot`s are re-posed to the Grip pose since Oct 2026 (older game copies
  are not: see `HANDHELD.md` §12). The Editor's hands use the device wrist frame (`dp.editor_hand_frame() == 'device'`),
  but render with `HandheldKit.Render` (both hands) and test both hands on a Quest with a remote player watching. The
  `handheld-convention` pre-upload check (Warning) runs the same rules.

## Portals: a window into another place (Oct 2026)

**A portal is a MATERIAL on any surfaces, sampled in SCREEN space.** Use it to show a big world (a golf course, a canyon, outer space) through a wall, the floor, a doorway or several of them at once, at real scale and in true stereo 3D, inside a small real room. `Assets/DreamPark/Scripts/Features/Portals/`, shader `DreamPark/Portal` (`Assets/DreamPark/Shaders/DreamPark-Portal.shader`). The pieces:

- **`PortalScene`** goes on a GameObject that holds the far world (terrain, sky, props). Everything rendered under it is **never drawn by any play-space camera**: not the headset eyes, the phone, or the phone/Quest diorama. It is drawn only into portals and by cameras with a `PortalSceneCamera`. So the real room's depth (Meta occlusion of floors and walls) can never hide it, it can't take over a diorama, and the portal works from anywhere it is seen, inside your attraction or outside it.
  - `source` is the play-space frame the scene is mapped from (default: the parent). With `followSource` the scene copies the source's pose every frame, so author the far world in the source's local coordinates: offset 0 lines it up 1:1 with the room.
  - `offset` puts the scene that many metres away. Use it only when the scene has colliders, lights or audio that must not reach the room, and keep it ≤ 4 km: beyond that 32-bit floats step by more than 0.5 mm and the scene jitters.
  - `layer` (default 16 "Portal") is the only layer the portal camera culls.
  - `emptyIsPassthrough` (default on): wherever the scene draws nothing, the portal shows the REAL world (passthrough on a headset, the camera feed on a phone), never a flat colour. That covers a scene shrinking away in a teleport iris, one not streamed in yet, or one switched off. The scene's opaque materials must write alpha 1 (DreamPark shaders do).
  - `background` is the clear colour, used only with `emptyIsPassthrough` off.
  - `farClip` is the portal camera's far plane. The viewer's own far plane is ignored, so a phone's 50 m far plane never cuts a 900 m course.
  - `resolutionScale` / `maxResolution` / `fullResDistance` / `minDistanceScale` control quality and cost.
- **`PortalSurface`** and a `DreamPark/Portal` material go on every mesh that is a window into that scene: a wall window, the floor, both (an L), a dome. All surfaces of one scene sample ONE per-eye view, so where a wall meets a floor there is no seam.
  - The surface is ordinary opaque content at queue Geometry-4 (before the SDK's depth-only floors). It writes depth, so things in front of it draw normally and things behind it are hidden, like a real window.
  - It is Meta-occluded: real hands and legs in front of it cut it. `_MetaDepthBias` (default 0.06) keeps the real wall or floor it lies on from eating it. On a floor portal, also put a `DepthMask` mesh on the opening.
  - `PortalSurface.fade` (0..1, from Lua or C#) dissolves the surface per pixel to the real world and back (1 = there). Use it for comfortable transitions, such as letting a floor portal dissolve while the scene moves.
  - `_FallbackColor` / `_FallbackTex` (mesh UVs) show where no portal texture exists: the Scene view, catalog previews, an app older than the portal renderer.
- **Crossers** (`PortalScene.crossers` + `playVolumes`) are play-space objects that must also be seen through the portal, such as a ball hit out through the wall. The portal always draws them. Play-space cameras draw them only while their bounds centre is inside an ACTIVE play-volume box (a transform whose local unit cube is the room), so the ball flies out through the window seamlessly and never floats beyond the real wall.
  - Scene objects that stand wholly inside an active play volume (a pin on the green under your feet) are drawn in the room too.
- **`PortalSceneCamera`** on any extra camera (a shot cam, a minimap) lets it see the listed scenes (empty list = all) while it renders, including `Camera.Render()` from Lua.

**How it renders (`PortalRenderer`, automatic).** Once per frame, after tracking (`Application.onBeforeRender`), for every enabled game camera and every scene whose surfaces it can see:

- One hidden camera renders the scene from that eye's exact pose and projection. That is each XR eye (head pose this frame × the OpenXR eye offset; single-pass instanced and multiview), the phone camera, or the phone diorama camera with its depth-scaled projection.
- The pose and projection are mapped into the scene and cut to the screen rectangle the surfaces cover.
- The result goes into a pooled texture whose pixels match the eye buffer's, so it stays sharp with no resampling blur. It is capped at `maxResolution` and its resolution falls with distance.

Cost control:

- Nothing renders for a camera that sees no surface (frustum + facing).
- There is one render per eye per scene, however many surfaces share it.
- No shadows, post-processing, depth/opaque textures or render scale.
- Meta occlusion keywords are off for that render.
- Textures are freed after about 2 s unused.

Kill switch: `PortalRenderer.Enabled`. `PortalRenderer.Last` holds the last frame's renders, pixels, CPU ms and texture memory.

**Rules.**

- Do not write `Renderer.forceRenderingOff` on scene renderers or crossers; the scene owns it. Use `SetActive` / `enabled`.
- Keep scene geometry behind the wall and below the floor in mapped space. There is no single-plane clipping (it can't serve an L-shaped portal), so anything of the far world standing inside the room shows on the surfaces behind it. Cull it, or let it be a real in-room object.
- New portal C# ships in the app: content using portals needs an app build that has them.

Typical layout: the far world under one `PortalScene` object (e.g. `CourseScene`), and one `PortalSurface` mesh per window (e.g. `Surface_Wall`, `Surface_Floor`).

## Game Storage (per-user save data: high scores, progress, coins)
Sample: `Assets/DreamPark/Samples/GameStorage/storage_high_score.lua.txt`. Every LuaBehaviour gets a `storage` variable auto-bound to its attraction (lazy walk up to GameArea/PropTemplate/LevelTemplate — no ids to pass):

```lua
storage.increment("coins", 5)          -- returns new value
storage.max("high_score", score)       -- set-if-greater: race-safe across devices
storage.min("best_time", lapTime)      -- set-if-lower
storage.set("checkpoint", 3)           -- string(≤1KB)/number/bool ONLY
storage.get("checkpoint", 0)           -- synchronous, with default
storage.game.set("progress", 3)        -- game scope (shared across your attractions)
storage.onReady(function() ... end)    -- server snapshot loaded (reads work before it)
```

Rules & gotchas:
- **Backed by `GameStorageAPI`** (`Assets/DreamPark/Scripts/Core/`) → `/app/profile/storage/{contentId}`. Editor testing uses the SDK preview key (same `ProfileAPI.BindToLoggedInUser` pairing flow as inventory); production auth is the headset binding.
- **Hard caps** — this is progress/score storage, NOT a blob store: keys `[A-Za-z0-9_-]` ≤64 chars, string values ≤1 KB, 64 keys & 8 KB per scope, and at most 64 attraction scopes per game (`MaxAttractionScopes` — a scope count, not a byte cap). Oversized writes fail locally with a warning (`set` returns false).
- **Prefer `max`/`min`/`increment` over read-compare-`set`** — ops apply server-side, so the same profile playing on another headset can't be clobbered. Writes are debounced/coalesced automatically; per-frame `increment` is fine.
- **Works unbound**: guest play reads/writes locally and merges into the account if the player pairs mid-session. Nothing persists for a session that never pairs.
- Scripts outside any attraction (e.g. Player.prefab park systems) use `dp.storage.game(gameId)`; the injected `storage` there would warn-once and no-op.
- **Writes are gated on attraction entry** (`ContentGate`, `Scripts/Core/`). A guest in the park hasn't opted into every installed game, so storage flushes and all `ProfileAPI` writes (items/achievements/badges/DreamPoints) are held until the player enters a `GameArea` of that content, then flush in order and stay open for the rest of the identity. Reads and session heartbeats are never gated. Editor sessions auto-open (`ContentGate.AutoOpenInEditor`) since there's often no GameArea to walk into. It's a correctness guard against honest mistakes, NOT a security boundary — the server-side per-guest rate limits are that.

## Badges: where they're earned — auto links + manual `earnAt` (Oct 2026)
The app uses each badge's `awardedBy` to show WHERE it is earned: Today's Challenge floats the badge over that attraction in the diorama, or offers "Add <Attraction> to your park?" (and later the mini-map). The Content Uploader builds `awardedBy` for you from two sources and uploads their **union**:

- **Auto (inferred, read-only).** `BadgeAttributionScanner` walks every Attraction/Prop root's hierarchy — nested prefabs and variants included — and links a badge to that root when something inside it awards or **carries** the id: a `dp.profile.awardBadge("id")` call (literal, or an `@var` / instance value), a Lua string var whose value is the id (e.g. a reward medal with `badgeId` set per instance), an `@var` default, or a C# string field named `badge…`. Only ids the package defines count. Nothing to author.
- **Manual (`earnAt`, editable).** For badges awarded from Player-side / title-wide scripts that key off attraction names, a `groupName`, a track theme, etc. — the scan only sees "the Player", so link them by hand. In the panel's badge card: **Auto** row (read-only, with how each link was found in its tooltip) beside the **Manual** field (comma-separated prefab names, or the ▾ picker of the package's attractions and props). Or edit `Assets/Content/{id}/.badges.json` directly:

```json
{ "badgeId": "mg_dragon_slayer", "name": "Dragon Slayer", "description": "…", "iconAssetPath": "…",
  "idSource": 1, "discoveredIn": "…",
  "earnAt": ["A_DragonLair", "L_DragonLairSmall", "A_DragonLairBattle_Old"] }
```

Rules: names are the attraction/prop **prefab file names** (case-sensitive), never addresses; list the **current prefab first, then its size/variant prefabs**; **name the WRAPPER attraction, never an inner level** — a level nested inside a Sequence wrapper (`Level3_Boss` inside `A_BossBattle`, an `L_` level inside an `A_` prefab) is recognised by the app only through its wrapper, since nothing sent to the phone maps the inner level to it; **leave title-wide badges unlinked** (omit `earnAt` or `[]`); don't duplicate what Auto already shows. The uploader resolves each name to the address the upload stamps (`{gameId}/Levels/{size}/{name}` / `{gameId}/Props/{category}/{name}`, computed from the file name, never the possibly-empty serialized `resourceName`). The `badge-earn-at` check warns on a name with no attraction/prop prefab (typo, rename) — such a name is not uploaded. Every upload sends each still-awarded badge's full list, and the server REPLACES its stored links with it, so removed links never linger: placed links if any (no `player` row then); else `player` (title-wide, awarded by the Player); else `script` (awarded by package Lua not attached to any root). The automated data is authoritative: a badge with no award site and no manual link sends `[]`, which clears its old links and marks it not awarded in this version until a site or manual link exists (a severed automatic link is updated on the next push). `awardedBy` is omitted — stored data left alone — only when the scan itself failed, or when the badge's only link is a manual name that doesn't resolve (the check warns), so a broken scan or a typo can never wipe or vault good data. Changes reach the server only with the title's next badge push (every upload, or **Push Badges to Portal**).

## Items (catalog + per-instance metadata, Oct 2026)
Items work like badges: you author the CATALOG entry in Unity and it is created on the backend for you; players own
inventory rows that point at it. Sample: `Assets/DreamPark/Samples/ProfileAPI/item.lua.txt`.

```lua
dp.profile.awardItem("coin", 5)                          -- stackable (or a singleton when Stacks is off)
dp.profile.awardUniqueItem("cr_fox", { level = 1, uid = "u7" }, function(ok, item)
    -- item.instanceId = "cr_fox_1a2b3c4d": the key for updateItem/removeItem; item.metadata = your table
end)
dp.profile.updateItem(item.instanceId, { level = 2 }, cb)  -- merge (4th arg true = replace the whole table)
dp.profile.getItems("cr_fox")                              -- every instance; or { baseItemId=, type=, contentId= }
dp.profile.removeItem(item.instanceId, 1, cb)
local off = dp.profile.onInventoryChanged(fn)              -- after every load/refresh/write
```

- **Authoring:** Content Uploader → Park Assets → **Items**: Icon, Title, ID, Type, Rarity, Stacks, Description per card,
  drafted in `Assets/Content/{id}/.items.json` (commit it). Every id your Lua passes to `awardItem` / `awardUniqueItem` /
  `getItem(s)` / `hasItem` / `removeItem` is found and locked automatically (literals, `@var` defaults, Inspector
  values). For ids built at runtime (`'cr_' .. species`) add one comment per id: `-- @item cr_fox "Fox"`.
  **Push Items to Portal** saves them; every Compile & Upload pushes them too.
  Agents: `dreampark_items_scan|push|delete --content_id <id>`.
- **Item ids are global** (one `items/{id}` doc), so the server refuses to save over another title's item. Prefix them
  (`cr_fox`, `zz_medkit`).
- **Metadata:** a table of strings/numbers/booleans/nested tables, at most **4 KB of JSON per item** (award and every
  update), no array directly inside an array; keep ids as strings. The SDK stamps `contentId` (it scopes rows that have
  no catalog doc and can't be changed). Whole numbers read back as Lua integers.
- **A unique item needs Stacks ON** (the default). A non-stacking catalog item is a singleton: the server ignores award
  metadata and keeps one row (`item-award` warns).
- `updateItem` only works on rows minted with metadata (409 `ITEM_NOT_UNIQUE`), on your own title's rows (403
  `ITEM_NOT_OWNED`), and shares the per-player write budget (60/min, 600/h) with awards: update on game events, not per
  frame. Writes wait for the player to enter one of your attractions (`ContentGate`), like every profile write.
- `dp.profile.supportsItemMetadata` is true on SDKs with this API. Backend: `POST /app/profile/inventory/update`
  (Oct 2026).

## DO NOT
- Edit files under `Assets/DreamPark/` for one title. They are SDK-owned and replaced on every SDK update.
- Use keyboard controls or virtual cameras as an interaction pattern. This is VR — hands and physical presence are the input.
- **Author gameplay with EasyEvent components. EasyEvent is DEPRECATED** — see below. All code functionality goes in Lua via `LuaBehaviour`, with no exceptions worth taking.
- Build a particle effect with anything other than a Unity `ParticleSystem` component — no VFX Graph, no marketplace particle frameworks, no hand-rolled billboard systems (see Particle effects).
- Ship a material on any shader other than the three DreamPark shaders, unless that shader integrates Meta occlusion itself (see Materials & Shaders).
- Ship a material set to Surface Type = Opaque with Alpha Clipping off, or "fix" a material by turning Alpha Clipping off to get soft edges — switch to Transparent instead (see Materials & Shaders).
- Upload with an empty Name/Description, or with unresolved pre-upload findings (see Shipping).

## EasyEvent is deprecated — all code functionality goes in Lua

**Do not author new content with EasyEvent components.** Not `EasyBend`, not
`EasyShatter`, not `EasyInteraction`, `EasyAudio`, `EasySpawn`, `EasyMove`, or
any of the 58 `Easy*` behaviours under
`Assets/DreamPark/Scripts/Features/EasyEvent/`. If you are reaching for one,
the answer is a `LuaBehaviour` and the `dp` API.

That includes `EasyLua`. Running a Lua script from an inspector-wired event
chain is the half-in-half-out shape this section exists to stop — if the logic
is Lua, put the `LuaBehaviour` on the object and skip the chain.

**They are not going anywhere, and they are not broken.** Every `Easy*`
component still works, is still supported, and is not scheduled for removal —
shipped prefabs and live content depend on them, and deleting a MonoBehaviour
that live content references turns every one of those prefabs into a
missing-script reference. This is a rule about what to author NEXT, not a
migration. Present and working ≠ the pattern to copy.

**Why Lua and not inspector-wired event chains:**

- Everything the platform actually supports is on the Lua surface. Storage
  (`storage.*`), multiplayer (`net_send`/`onnet`), the whole `dp` API,
  attraction scoping — none of it has an EasyEvent equivalent, so any
  non-trivial game ends up half-wired in the inspector and half-written in Lua,
  which is worse than either.
- A `.lua.txt` file diffs, reviews and greps. A chain of serialized
  `UnityEvent` references in a prefab does none of the three, and the only way
  to find out what it does is to click through it in the inspector.
- Lua ships over the air. Content-only updates can change gameplay without an
  app release; a behaviour baked into a prefab's serialized wiring cannot be
  reasoned about the same way, and anything needing a new C# component needs a
  whole app release.
- One place to look. "Where does this object's logic live" has exactly one
  answer, instead of depending on whether the previous author preferred the
  inspector that day.

**If you are an agent generating content or tooling:** do not add `Easy*`
components to prefabs you author from scratch, and do not propose them as the
answer to a new problem.

**And do not go and fix the ones already there.** Existing C# that attaches or
subclasses `Easy*` — in the SDK, in tooling, in shipped prefabs — is working
code, and rewriting it is not an improvement, it is churn with a risk of
regression attached. That includes "while I was in here" cleanups, porting an
`Easy*` component to Lua because this section says Lua, and flagging existing
usage as a bug. It is not a bug. It is the previous convention, still running.
Leave it alone unless a human has asked you, in those words, to change it.

Start at **Lua Lifecycle Under Park Loading** and **The `dp` Creator API**
below. Both are further down this file.

## Workflow
1. Creator clones dreampark-sdk as a new project.
2. On first editor open, the setup popup renames YOUR_GAME_HERE to the game name (e.g., CoinCollector). (There is no new-park.sh — the popup is the rename mechanism.)
3. Creator adds game content, Lua scripts, and prefabs to Assets/Content/{GameName}/.
4. All gameplay logic is Lua via LuaBehaviour. Not "Lua-first" — Lua. C# in `Assets/Content/` should be interop shims and nothing else, and EasyEvent components are deprecated (see above).
5. Creator authors the three packages in the Content Uploader's **Packages** section — Arena priorities, the Sequence order plus its Start/Overlay/Game Over levels, the Adventure's Start/End route — and puts title-wide rules in the Game Manager (see § Packages). Every package is optional, but the title needs at least one playable package to upload; an unfinished Sequence or Adventure is left out with a warning.
6. Built Addressable prefabs are deployed to DreamPark servers via DreamPark → Content Uploader: fill the panel's **Name** and **Description** fields (the launch window shows them read-only, and an empty Name blocks the upload), clear the pre-upload checks (see Shipping below), hit **Compile & Upload**, then **Start · All** in the launch window. One attraction at a time or a whole park's worth; each upload publishes the attractions catalog automatically and is immediately playable in the iOS app with Experimental Mode on. The launch window's **Upload Scope** picker selects All / Patch / Code-only; the first release for a content ID is locked to All, and Patch is the default choice after that. Smart (dependency-aware) bundling is the default strategy as of Aug 2026 and is what makes the partial modes work — Legacy is deprecated, hidden behind `DreamPark → Troubleshooting → Use Legacy Bundling`, and forces a full re-upload when active.
7. Sign-in (`DreamPark → Sign In`) is passwordless as of July 2026 — email + 6-digit OTP, and `/auth/otp/verify` get-or-creates the account, so there is no separate sign-up path and no password to reset.

## AI Playtester — play the game before you call it done (Sept 2026)
A simulated guest that plays content in Editor Play mode: it walks, turns, crouches, reaches/touches/strikes with either hand and steps on pads, then reports what it caused. Source: `Assets/DreamPark/Scripts/Features/Playtest/` (runtime, `#if UNITY_EDITOR` — never in a build) and `Assets/DreamPark/Editor/Playtest/`.

- **How it drives the player**: `PlaytestAvatar` disables the Scene-view `Simulator` and writes `Camera.main` + `Left/RightHandAnchor` itself (order 32000, re-applied in LateUpdate/onBeforeRender). Everything keys off those — trackers, GameArea presence, `dp.head()/dp.hand()` — so content needs no changes. It adds kinematic probe colliders (hand spheres, floor-to-eye body capsule) parented under the active `PlayerRig`, so `dp.is_player` is true by ancestry.
- **What it sees**: `PlaytestObserver` — console + Lua errors/prints, zone changes, storage writes, probe contacts (as a per-action delta), plus snapshots: nearby interactables with short ids (`e12`), tags, live Lua scope values and function names, which hand each rig script rides on, world TMP text, head/overhead screenshots. `lua_eval` runs code in an object's script scope (read state; don't mutate unless setting up a test).
- **Game time is frozen between actions** (`PlaytestClock`), so thinking time never costs the player.
- **Built-in agent**: `DreamPark → AI Playtester` (Claude via the Messages API; key from `ANTHROPIC_API_KEY` or the window, stored in EditorPrefs only). It reads a briefing — `Assets/Content/{id}/PLAYTEST.md` (the creator's spec, optional), every prefab's wired scripts and collider tags, the Lua source, the package JSON — plays, files issues, optionally patches `.lua.txt` in the content folder and restarts Play mode to re-verify, and writes `Logs/DreamParkPlaytest/<run>/report.md` with screenshots. The run survives Play-mode domain reloads via `Library/DreamParkPlaytest/session.json`.
- **Driving it yourself from the Unity CLI** (for agents authoring content — build, play, fix, replay), one agent in Play mode at a time:
  ```
  unity command --project-path . editor_play
  unity command --project-path . dreampark_playtest_attach                 # snapshot + screenshot path
  unity command --project-path . dreampark_playtest_tools                  # action schemas
  unity command --project-path . --timeout 180 dreampark_playtest_act --tool travel_to --args '{"target":"A_MyAttraction"}'
  unity command --project-path . --timeout 180 dreampark_playtest_act --tool reach --args '{"hand":"left","target":"e12","style":"touch"}'
  unity command --project-path . dreampark_playtest_act --tool lua_eval --args '{"target":"e25","code":"get_points()"}'
  unity command --project-path . dreampark_playtest_detach                 # un-freezes time, restores the Simulator
  unity command --project-path . editor_stop
  ```
  Or hand off a whole run: `dreampark_playtest_run --goal "..." [--allow_fixes true]`, then poll `dreampark_playtest_status`. Walks are real-time — pass `--timeout` (the CLI default is 30 s) or use `travel_to` between distant attractions. Read the returned screenshot paths; the images are the ground truth.
- **Gotchas it surfaces**: a touch only counts through colliders the game put on the rig — observe's PLAYER SYSTEMS says which hand each rides (a `BothHands` tracker follows the LEFT hand in the Editor). The Editor spawns the guest wherever the Scene camera was, often nowhere near content — `travel_to` first.

## Shipping: the Content Uploader (`DreamPark → Content Uploader`)
Open it from the **`DreamPark` menu in the Unity Editor's top menu bar** — a first-party menu the SDK adds, alongside File/Edit/Assets/GameObject. `DreamPark → Content Uploader` is the first item. The same menu holds `Sign In`, `Optimization → Material Optimizer...`, `Troubleshooting → …` and `Startup Scene...`; every SDK tool referenced in this file lives under it.

The game is not done when it plays in the Editor. It is done when it goes through the uploader clean. Two things are required there and both are on you, not on the human reviewing later.

**1. Set the Name and Description.** The panel's **Name** and **Description** fields are the store-facing metadata for the content package — they're what a guest sees in the Attractions browser and the consumer app. The launch window shows them read-only, and an **empty Name blocks the upload outright**. Write a real description (what the attraction is, what the guest does), not a placeholder. Pick the right content package first if the project has more than one — the dropdown at the top of the panel selects which `Assets/Content/{GameName}/` gets published.

**2. Run the pre-upload verification and fix everything it reports.** `Pre Launch Options → Review Pre-Upload Checks...` opens the unified checks window; the Content Uploader also runs an advisory scan when the panel is engaged and shows per-attraction tile badges. The upload path is gated by `PreUploadChecksGate.Passes(...)` — Blocking findings stop the upload. The fourteen creator-facing checks (`Assets/DreamPark/Editor/PreUploadChecks/Checks/`):

| Check id | Severity | What it means |
|---|---|---|
| `dream-sequence-required` | **Blocking** | The title has **no playable package** (no Arena, no authored Sequence, no complete Adventure) — or it has an authored **Sequence** that is broken: recipe not compiled or not current, Start/Overlay/Game Over/Game Manager prefab missing, special levels not baked, or a required attraction too big to fit. Clean when no Sequence is authored and an Arena or Adventure ships. Fix: *Rebuild Sequence Package*. No Dream Sequence prefab is involved. See § Packages. |
| `package-references` | **Blocking** | A placement in an authored Sequence or a complete Adventure points at a deleted or invalid prefab. Remove or replace it in the Packages organizer. |
| `duplicate-names` | **Blocking** (case-only clashes: Warning) | Two prefabs share a name → collided Addressables address, preview PNG, `PreviewMetadataStore` key and `GameArea.resourceName` (the revenue-attribution key). Always a true positive. |
| `meta-occlusion` | **Blocking** when a material's shader definitely lacks occlusion; **Warning** when undeterminable | See the Materials & Shaders section. Converts editable materials directly; embedded model materials can be extracted and converted from the finding. |
| `opaque-alpha-clip` | **Blocking** | A DreamPark material set to Surface Type = Opaque with Alpha Clipping off. `meta-occlusion`'s blind spot — the shader has the occlusion wiring, the material discards it. Editable materials are repaired directly; embedded materials can be extracted and repaired. |
| `sun-light` | **Blocking** (inactive lights: Warning) | A directional light shipped in content lights *every* attraction in the park and no other creator can opt out. |
| `root-components` | **Blocking** (trigger colliders: Warning) | Physics, audio, particles, animation or lights placed on an attraction/prop root sit outside the optimizer. The check opens Prefab Mode and selects the exact component; moving it automatically is unsafe because it can change physics, animation paths and spatial-audio origin. |
| `scene-overrides` | Warning | Unapplied prefab overrides in scenes — what you see in the test scene isn't what ships. Apply them to the prefab, or confirm the scene tweak is intentional. Reads only scenes inside `Assets/Content/{id}`; vendor demo scenes under `ThirdParty/` / `ThirdPartyLocal/` are not opened (Oct 2026). It never prompts or saves: a scene already open is read in place (unsaved changes are reported as a Warning), others are opened additively and closed without saving, and overrides a scene makes to itself while loading are not reported. |
| `outside-content-folder` | Warning (`Assets/Plugins/`-style protected folders and `ThirdPartyLocal/`: Info) | A prefab depends on an asset outside `Assets/Content/{GameName}/` — it won't be in the bundle. The guided resolver transfers unshared assets and copies/repoints shared ones. |
| `resource-name-address` | Warning (never-stamped: Info) | A prefab's stored revenue key disagrees with its computed Addressables address, often after `Revert All` on a variant. Re-stamp it from the finding. |
| `handheld-convention` | Warning | A prop with a `Grip`/`Muzzle` under a hand node breaks the hand-held convention (wrong grip pose for its side, negative scale, missing Grip, remote copy ≠ local, a prop on an old template pivot). See `HANDHELD.md`. |
| `badge-award` | Warning | A badge is not awarded by any shipping Attraction, Prop or Player script. The finding links to the source, can copy the Lua call, and can remove an unwired manual draft. |
| `badge-earn-at` | Warning | A badge's manual `earnAt` link names a prefab that isn't an attraction or prop in this package (typo, rename, deleted) — that link would not be uploaded — or names an inner level nested inside a wrapper attraction, which the app can't match. Remove it or use the suggested name / wrapper from the finding. See § Badges. |
| `item-award` | Warning (untitled item: Info) | An item card nothing in the package awards, or a UNIQUE (metadata) item whose card has Stacks off — the server treats a non-stacking item as a singleton and drops its per-instance data (one-click "Turn Stacks on"). See § Items. |

`net-budget-invariant` is intentionally not creator-facing: it compares two SDK-owned
constants and is enforced by both SDK release preflight and the Editor SDK publish panel.

Rules for agents working the checks list:

- **Fix findings; do not ignore them.** Findings offer either a direct repair, a guided resolver, or precise navigation to the object that needs a judgment call. Guided tools report completion back to the popup and re-run their check automatically. `Ignore` writes a permanent, typed-reason entry into `Assets/Content/{contentId}/.preupload-ignores.json` that is git-tracked and visible to the whole team. Only a human decides to ignore a Blocking finding.
- **Warnings and Info still get resolved or explained.** They don't block, but `scene-overrides` and `outside-content-folder` warnings are usually real content bugs that only show up after upload, when they're expensive.
- **A clean project shows no popup at all** — the gate opens the window only when there's at least one non-ignored Blocking or Warning finding. If the popup appears, there is work to do; don't click through it.
- Re-run the checks after fixing. Results are cached per content id and refresh on `ReportChanged`.
- Then **Compile & Upload**, then **Start · All** in the launch window.

### Agent access (sign in, upload, website)

Agents sign in with their own **agent key**, never a human's session, and only after a human approves. The flow is **request → wait → publish**: `dreampark_agent_request` emails the content's owner an **Approve** link; `dreampark_agent_wait` polls until they click it and then stores a scoped, revocable key for the agent's OWN account (never the owner's), which joins the content's team. Keys live in `Library/DreamParkAgent/agent-key.json` (gitignored; never print one), **one entry per approved title**; pending requests (with their secret poll tokens) live in `Library/DreamParkAgent/pending-request.json`, one per title.

**Agents never ask for, type or relay the 6-digit code.** The code in the email exists only so the OWNER can run `dreampark_agent_verify` themselves if the link can't be used. The request result's `fallback` field has that command; show it to the human on ONE line and let them run it with `!` in Claude Code. Always give a human commands on one line, never wrapped. All through the Unity CLI:

```
unity command --project-path . dreampark_agent_status [--content_id <id>]                 # every stored key (masked) + its titles, and pending requests
unity command --project-path . dreampark_agent_request --content_id <id>                  # emails <id>'s OWNER an Approve link
unity command --project-path . dreampark_agent_request --content_id <new id> --email <human who will own it>   # new title
unity command --project-path . dreampark_agent_wait [--content_id <id>]                   # returns when they click Approve (<= 9 min; re-run if "pending"); no id = every pending title
unity command --project-path . --timeout 600 dreampark_content_preflight --content_id <id>
unity command --project-path . dreampark_content_publish --content_id <id> --release_notes "..." --target beta --wait false   # or release (default); returns a jobId at once
unity command --project-path . dreampark_content_publish_status                             # poll every few minutes until succeeded / failed
unity command --project-path . dreampark_agent_web_login --next /developer [--content_id <id>]   # single-use browser link, ~2 min, as that title's agent
unity command --project-path . dreampark_agent_signout [--content_id <id>]                # revoke + delete one title's key, or (no id) every key
```

- Tell the human, in one sentence, that an approval email went to `sentTo` and they just need to click Approve; then run `dreampark_agent_wait`. It polls every 3 s, survives Editor reloads (state is the pending file; just re-run it), and returns `status: "pending"` at its timeout (default 480 s, max 540) — run it again until the request expires (30 min). `denied` / `expired` / `consumed` come back with a `next` step (usually request again). `dreampark_agent_status` shows a pending request too.
- **One key per title (Oct 2026).** An approval covers one content ID (and its `<id>Beta` twin), so a project with several content folders keeps one key per approved title, and a new approval never overwrites another title's key. Every content-scoped command (`dreampark_content_preflight`, `_publish`, `_publish_status`, `_inspect`, the badge push, `dreampark_agent_web_login --content_id`) uses the key approved for its `--content_id`; when none was, it falls back to the newest key (`dreampark_agent_status --content_id <id>` says which). Approve each title **once**; re-approving a title replaces only that title's key. Approvals can run **in parallel**: request every title you need, then one `dreampark_agent_wait` waits on all of them and stores each key as its own entry. `dreampark_agent_signout --content_id <id>` revokes just that title's key.
- `--content_id` defaults to the only content folder besides Sample/YOUR_GAME_HERE. Server errors come back as `code` + `error` verbatim; `APPROVER_EMAIL_REQUIRED` means a new title — add `--email`.
- Publishing is still a ship decision for the human: preflight is evidence, not approval. `--target beta` goes to the separate `<id>Beta` content; release goes live.
- The publish reads the store Name/Description from the live record and re-sends them unchanged. **New titles ship beta-first:** the first upload of a title with no backend record creates it, and needs `--name` and `--description` (real store copy, not placeholders). With `--target beta --mode all`, it creates the title record WITHOUT a build and uploads only to `<id>Beta`; the live title stays empty until a later `--target release`. The first upload to any target must use `--mode all`.
- **Automation never shows a dialog (Oct 2026).** `dreampark_content_preflight` and `dreampark_content_publish` (and batch mode, or `DREAMPARK_AUTOMATION=1`) run every interactive step without a modal and put the reason in the command's result instead. `saved-state` looks only at scenes inside `Assets/Content/{id}`: with the publish's save it saves those (path-backed) in place, and reports Untitled scenes and scenes elsewhere without touching them. During the Addressables build, the unsaved flag of any open scene is set aside and put back afterwards, so Addressables' "Unsaved Scenes" prompt cannot appear; nothing is saved or discarded. A failed platform switch fails the publish with its reason. If a publish still stops at a modal, that is an SDK bug: report the dialog's exact text.
- **Follow every publish with `dreampark_content_publish_status`.** A build and upload can take far longer than one tool call (Claude Code caps a single command at 10 minutes), so start it with `--wait false` and poll. Status returns `state` (`running` / `succeeded` / `failed` / `interrupted` / `none`), the current `phase` and `detail`, `progress`, files and bytes uploaded, `elapsedSeconds`, and once finished the `versionNumber`, `verified` and `noChanges`. It answers even while a build blocks the Editor. Only one publish runs at a time. Don't reload or quit the Editor mid-publish: that ends it, and status reports `interrupted`. Even with `--wait true`, a CLI timeout doesn't stop the publish, and status still reports how it ended.
- A 403 on a content ID means no stored key is on its team: `dreampark_agent_request --content_id <id>` (the other titles' keys are kept). Results carry errors in `message` (no dialogs in automation). A Patch with nothing changed returns `noChanges: true` — success, no new version.
- Never call `/auth/logout` with an agent key (it ends all of a human's sessions); `dreampark_agent_signout` is the only sign-out.
- **What an approved agent can do** (on every title whose team it is on): upload beta and release builds; create NEW titles (up to 5/day) — they are always owned by the human who approved you, with you on the team, never by you; edit title info, logo, title card, cover/hero and screenshots on `/developer`; author **badges, items and achievements** (save, delete, icons, descriptions — the uploader's badge and item pushes included; items also from the CLI: `dreampark_items_scan`, `dreampark_items_push`, `dreampark_items_delete`, see § Items).
- **What it cannot do, by design:** own a title, add/remove collaborators, approve a release, change `contentOwner`/`contentOwners`/`versions`/`review` (refused as `CONTENT_FIELD_PROTECTED` for everyone, humans included), manage agent keys, or reach DreamPark's internal tools. A `403 AGENT_KEY_SCOPE` means the route is outside the agent surface — ask the human, don't look for another way in.
- Agents are hidden from public credits (feed, creator pages, people search); the human owner is credited and paid.
- **The website:** `dreampark_agent_web_login` prints a single-use link (~2 min). Open it in the browser you are driving (e.g. Claude in Chrome) and you land in `/developer` signed in as the agent, with the same permissions as above. Pass `--content_id <id>` (or a `--next` path naming the title) to sign in as the agent approved for that title.

## Multiplayer (Oct 2026: the platform does the netcode)
**Read `Assets/DreamPark/Samples/Multiplayer/MULTIPLAYER.md` before writing netcode.** Transport: one dumb relay per
session (a DreamBox kiosk, or an elected headset running `PeerRelayServer` in-process). It rebroadcasts every message to
every OTHER peer (never the sender), ReliableOrdered, 16 KB cap, and a peer host silently drops anything past 60 msg/s
per peer. Stack: `Assets/DreamPark/Scripts/Features/Net/`. Everything creator-facing is Lua; the only
networking component you add is one `NetId` on an attraction's manager.

The nine rules:
1. **Encounters** (bosses, enemies, rounds, timers): `dp.shared_state(name)` + `dp.is_leader()`. Players send requests
   (`s.request`); the leader applies them.
2. **Nothing gameplay-visible is rolled per device:** `dp.random(key)`, leader choices, `dp.clock()`. Never
   `math.random` for gameplay, never `math.randomseed` (shared VM).
3. **Late join is automatic** with `shared_state`; gate every start on `s.synced()`; effects never replay.
4. **Physics props:** `dp.net_body(go)`. Never write a remote prop's transform. (Every content Rigidbody also starts
   capped at 2 m/s depenetration and 14 rad/s spin; values a game sets explicitly are kept.)
5. **Positions:** typed `net_send(kind, table)` (attraction-relative); people come from `dp.peers()`; never world space.
6. **No `explicitId`** except on a `Player.prefab` bus. Ids are scoped (Arena/Adventure attractions, every Sequence level
   incl. Start/Overlay/Game Over, the Game Manager) and title-unique automatically.
7. **Audio/VO/greetings start in `onzoneenter()`**, never `start()`.
8. **One send budget per headset:** design for `dp.relay().budget` (60, presence included), never `cap`.
9. **Test with simulated peers before a headset**, then one two-headset late-join test:
   `unity command --project-path . --timeout 400 dreampark_peers_scenario --name all --wait true --params '{"target":"<manager path>",…}'`
   (window: DreamPark → Multiplayer → Simulated Peers; params and scenarios in MULTIPLAYER.md § Testing).

Under them: nothing sends at file scope or in `awake()` (`net_send` is injected after the file scope runs; register
`dp.on_connected` / `dp.on_peer_join` in `start()`), and `ondisable()` is not "the player left" (rigs hide in another
title's zone; the optimizer parks distant objects). Numbers on the wire are float32: ids, seeds and absolute times are
strings.

Debugging: `Verbose Net Logs` on `DreamBoxClient`. `[NetRegistry] Event for UNREGISTERED NetId` = the two devices
computed different ids (an older app, or `explicitId` on content); `NO subscribers` = no `onmessage`/`onnet` on that
object here (or it errored at file scope); `NetId COLLISION` = two objects in one app claim one id; `Ignoring peer beacon` =
channel/park/protocol filter. Sessions: `NetSessionArbiter` (next to `DreamBoxClient`) runs DreamBox beacon → kiosk
(always wins) → peer beacon → self-elect host after 3–5 s; host loss re-elects in ~1–3 s and nothing migrates (state
lives in the peers). Beacons carry `parkId` and channel `sdk` (SDK) / `prod` (the DreamPark app) plus a protocol version, so SDK test
sessions never meet production ones and apps with different id schemes never share a deaf session. The relay host is not
the gameplay leader. Hosting needs `CHANGE_WIFI_MULTICAST_STATE` + `WAKE_LOCK` on Android (loopback tests pass without
them, deceptively). iOS is client-only.

## Lua Lifecycle Under Park Loading (read before writing awake())

Park content is spawned by the loader, not authored into a live scene, and that
changes what is true when each hook runs.

- **`awake()` runs BEFORE the object is parented, posed or stamped.** `LevelAnchor.Spawn`
  instantiates the prefab, then writes `localPosition`/`localRotation`/`localScale` and
  parents it to the LevelAnchor *afterwards*. So in `awake()` the object is still at its
  prefab pose at the scene root. Anything that reads `self.transform.position`, walks
  `transform.parent`, or calls `GetComponentInParent` gets an answer that is about to
  become wrong. This is the same reason `storage` is a lazy proxy and `net_send` reads
  `netId.Id` at send time rather than caching it.
  **Use `awake()` only to publish globals and wire your own tables. Do anything
  positional, hierarchical, or cross-object in `start()`.**

- **`start()` is safe and is guaranteed to arrive.** Park objects spawn with their
  LuaBehaviour disabled (OptimizedAF parks everything until the level finishes loading),
  so Unity's real `Start` may never fire. `LuaBehaviour` tracks dispatch separately and
  also drives it from `Update`, so `start()` lands on the first frame the script actually
  runs — whichever path booted it.

- **`onenable()` / `ondisable()` are NOT gameplay events.** OptimizedAF enables and
  disables LuaBehaviours during load and culling, so these fire as load artifacts, and
  depending on ordering a script can see `ondisable()` before it ever sees `onenable()`.
  Do not treat them as "the player can see me now" or tear down state in them.

- **Never hand-roll edge detection on polled SDK state.** Reading `GameArea.isPlaying`
  each frame and comparing it to a cached copy puts the edge in *your* script, and if you
  latch before checking that your receiver exists, the edge is consumed and never fires
  again. Use `onzoneenter()` / `onzoneexit()` instead — the SDK owns the state and
  delivers the *current* value when your script activates, so loading in after the player
  already walked in still works.

- **`ontriggerenter(other)` also fires for overlaps that already existed** when the
  object first came up. Colliders are disabled during load, and Unity does not reliably
  raise `OnTriggerEnter` for something already inside a collider at the moment it is
  enabled — so the SDK sweeps once on the first real frame. A pickup the player spawns
  standing on top of still fires.

- **Reading `.enabled` in `start()` can see the optimizer, not your prefab.** OptimizedAF
  parks content by switching its components off: during the load, and whenever it is far
  from the player. `start()` is deliberately not delayed until the unpark (globals, net
  handlers and timers depend on it arriving early), so a manager that starts while the
  child it inspects is still parked reads `renderer.enabled == false` for a renderer you
  authored ON. One shipped boss recorded its whole rig as "should be hidden" that
  way and never rendered again. Ask for the intended state instead, or take visibility
  from your own authored data (a list of names, an injected flag), never from a live
  `.enabled` read at startup:
  ```lua
  local function intended(c)            -- the authored / game-set value, parked or not
      if dp.enabled then return dp.enabled(c) end
      return c.enabled                  -- LEGACY(app<2026.10): no dp.enabled on older apps
  end
  ```
  `dp.set_enabled(c, on)` is the matching write (see What the optimizer guarantees).

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
                             --   among players WEARING this title's rig while anyone does (Oct 2026)
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
```

`dp.player()` is **nil** whenever there is no rig — before it loads, and for the whole session on a
phone whose title has no Phone Player. Nil-check it.

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

### What the optimizer guarantees (July 2026)

The park loader registers every spawned object with `LevelObjectManager`, which parks
and restores its components as the player moves. A hand-authored scene registers
nothing, so this machinery only ever ran in a park — and until July 2026 it silently
reverted anything the game changed at runtime:

```lua
function ontriggerenter(other)
    if dp.is_player(other) then
        col.enabled  = false     -- hide the collected pickup
        rend.enabled = false
    end
end
-- walk 10 m away and back: the pickup was solid and visible again
```

**The rule now: live state is re-read on the way out.** Whatever the game has set at
the moment an object is parked is what gets restored. Covers `Collider.enabled`,
`Renderer.enabled`, runtime material assignment, any `MonoBehaviour`/`Behaviour`
`.enabled`, `Animator.enabled` and particle play state. Rigidbodies already worked this
way.

Consequences a creator can rely on:

- **A change you make while your object is live survives being culled.**
- **A component you shipped disabled stays disabled.** `Light`, `AudioSource`, `Camera`
  and `AudioListener` used to switch themselves ON at the first restore, because the
  snapshot only read `MonoBehaviour.enabled`.
- **Physics props keep their momentum** across a cull. The velocity restore tested the
  live `isKinematic`, which parking had already forced true, so it could never run.
- **An object that moves is culled against where it IS**, not where it spawned.
- **A child that is inactive at spawn is still managed** once you activate it.

Known limit, deliberate: a change made to an object **while it is parked** is not seen
by a plain write. Scripts on a parked object are themselves disabled, so this only bites if
you write to a culled object from somewhere else (a manager, the Player). Opt in to the
fix with the two calls below; content that doesn't call them behaves exactly as before.

**Reading and writing the intended state (Oct 2026): `dp.enabled` / `dp.set_enabled`.**
A parked component's live `.enabled` is the optimizer's `false`, not yours. These read and
write the snapshot the unpark restores:

| | object parked (load, distance cull) or frozen (game pause) | object live / never registered |
|---|---|---|
| `dp.enabled(c)` | the value the unpark will restore | `c.enabled` |
| `dp.set_enabled(c, on)` | updates that value; the component stays parked and gets it at the unpark | `c.enabled = on` |

- `c` is a `Renderer`, `Collider` or any `Behaviour` (`LuaBehaviour`, `Light`, `AudioSource`,
  `Animator`, `Camera`…). Anything else (a `Transform`, a `GameObject`, a `ParticleSystem`,
  nil) returns nil / false and changes nothing.
- A renderer in a simplified (mid-distance) park is still drawn, and `dp.set_enabled` hides
  or shows it at once there too. A hidden (far) park keeps it hidden until the unpark.
- They exist in apps from Oct 2026. Feature-test: `if dp.enabled then … else … end`; the
  `else` branch is the old behaviour (`c.enabled`).

**Keeping a manager running everywhere: `-- @optimizer-ignore` / `dp.optimizer_ignore`
(Oct 2026).** The Lua equivalent of an `OptimizedAFIgnore` component: the distance sweep
skips the object, so it never culls it however far the player walks. The park load,
build mode and the operator pause still park it, exactly as they do the component.
- **The annotation** (preferred): a `-- @optimizer-ignore` comment anywhere in a
  LuaBehaviour's script, like `-- @packing-ignore`. It is read when the object is
  REGISTERED, which on device can be before its `awake()` runs, so it covers the first park.
  It applies to the object holding the script and everything under it. The optimizer's unit
  is a registered object (each child of an attraction root), so a marked script nested
  inside one exempts that whole child; a marked script on the attraction root exempts the
  whole attraction.
- **At runtime:** `dp.optimizer_ignore(go, true)` restores anything parked (from its
  snapshot, renderers included) and exempts it; `dp.optimizer_ignore(go, false)` hands it
  back (the next sweep parks it if it is far). Idempotent and nil-safe; inside a script
  `dp.optimizer_ignore()` means this object. A call made before the object is registered
  (in `awake()`) is remembered. It never undoes a platform `OptimizedAFIgnore`. Returns the
  number of registered objects changed (0 = remembered for later, -1 = not an object).
- **For managers and always-on systems, not props.** Every exempt object runs and renders
  all session wherever the player stands, which costs Quest frame time; a title exempting
  more than 20 objects gets a one-time console warning. You don't need it for the
  **Game Manager** (the app adds `OptimizedAFIgnore` to it automatically) or for anything on the
  **Player rig** (a `PlayerRig` is priority and is never parked).
### The Lua surface gate

Content ships over the air; the XLua wrappers it needs are AOT code compiled into the
app. A Unity type nobody registered therefore **cannot be fixed by re-uploading
content** — it needs an app rebuild and a store release. The same call works perfectly
in the Editor, because Mono reflects where IL2CPP cannot.

`LuaSurfaceScanner` catches this, and as of July 2026 something actually runs it:

| Check | Content upload | Player build |
|---|---|---|
| **Sandbox-denied type** — throws at a venue | **blocked**, dialog | **build fails** |
| **Codegen drift** — config has a type with no wrapper in `Gen/`, or (Oct 2026) a wrapper lacks a public member / overload its type now has | console warning | **build fails** |
| **Unregistered type** — Lua names a type with no wrapper | console warning | console warning |

Only two of those interrupt anyone, and both are checks that are *always right*. The
third is deliberately demoted: it fires on "type has no wrapper", but the failure it
hints at is a call signature AOT cannot fake — a struct passed by `out`, or a
runtime-instantiated generic. Those sets barely overlap, since reflection handles
ordinary member access on device fine. Nearly every finding is "maybe nothing", which
is exactly the shape that teaches people to click through dialogs — and then the one
that mattered gets dismissed too. It also has a better replacement at runtime: the SDK
defines `NOT_GEN_WARNING`, so a creator's own headset build *names* every type that fell
back to reflection. Observed beats predicted.

**Codegen drift is about build integrity, not config coverage.** In one shipped title, codegen had
never *succeeded* (`DreamParkLuaConfig` duplicated four `GCOptimize` entries XLua's own `SysGenConfig`
already declared, `OptimizeCfg.Add` threw on the duplicate key, `GenAll()` died), so every type was
reflection-only and `NavMesh.SamplePosition` was just the first call unlucky enough to need a real
wrapper. It looked like a missing config entry; it was a build that never generated.

It blocks a *build* but not an *upload*: wrappers are AOT code inside the app, so a
creator's stale `Gen/` can never reach a guest — but an APK built against it means
testing a runtime you don't ship. Menu item: `DreamPark ▸ Troubleshooting ▸ Verify XLua
Codegen`.

Two notes on the gate:

- **It resolves fully-qualified names only.** It strips Lua comments, follows alias
  declarations (`local UE = CS.UnityEngine`, `local Vector3 = UE.Vector3`) to a fixpoint,
  and looks results up by FULL name. The first version matched *simple* names with a
  capitalised-identifier heuristic, which is the shape of a method call, not a type —
  `Vector3.Angle(a, b)` reported `UnityEngine.UIElements.Angle` as a missing wrapper. It
  trades recall for precision deliberately: a gate that cries wolf teaches people to
  click through.
- **An authoring tool opts out with `-- @editor-only`** (or by living under an `Editor/`
  folder). A baker script under `Assets/Content` that calls `CS.UnityEditor` is correctly
  sandbox-denied, and without the marker it would hard-block every upload.

The scanner also now indexes **DreamPark's own types**, not just `UnityEngine*` — it
previously could not have reported `CS.DreamPark.FloorAnchor`, which shipped content
depends on. And the SDK defines `NOT_GEN_WARNING`, so a creator's own headset build
names every type falling back to reflection at runtime. The DreamPark app does not
define it; production logs stay quiet.

### XLua `Gen/` is SDK-owned — updates replace it, never merge it (Sept 2026)

A `.unitypackage` import adds and overwrites but never deletes, and `Gen/` only compiles as
one set (wrappers call helpers that exist only in the `WrapPusher.cs` of the same run). Mixing
two runs breaks compilation with CS1061. `Editor/XLuaGenGuard.cs` enforces:

- **Generate XLua Code** runs codegen into `Temp/`, checks it, then syncs `Gen/` to exactly that
  output (stale wrappers deleted; a failed run leaves `Gen/` untouched) and writes
  `Editor/XLuaGenManifest.json`. Never call `Generator.GenAll()` into `Gen/` directly.
- **After any import** (updater or hand-imported package), if the shipped set in the manifest is
  intact, anything else in `Gen/` is removed. The updater also deletes `Gen/` before importing.
- **XLua's demos are retired.** `ThirdParty/XLua/Examples/` and `Tutorial/` are deleted on sight
  and `Generator.IsExcludedFromCodegen` skips the `XLuaTest`/`Tutorial` namespaces. Don't restore them.
- **Every SDK export is gated** (Publish, CLI publish, and the local "Export .unitypackage"):
  codegen drift, unresolved helpers or unregistered wrappers block the export.
- Before publishing, run `python3 scripts/release/check_sdk_upgrade.py upgrade <package> <real project>`
  against an existing game project — it simulates the import and fails on anything that would not compile.

### Rules that still apply

- **`awake()` runs before the object is parented or posed.** Publish globals there;
  do anything positional, hierarchical or cross-object in `start()` or `onready()`.
- **`onenable()` / `ondisable()` are not gameplay events.** The optimizer parks and
  unparks components during load and culling. The platform suppresses its own toggles from
  reaching your script, but do not treat them as "the player can see me now".
- **Reads never need `onReady`.** `storage.get` is synchronous against the local
  cache and works for unpaired guests. `storage.onReady` is for refreshing a
  display once the server snapshot lands — it fires immediately when there is
  nothing to wait for.
- **`dp.attraction()` does not work from inside a prop.** It resolves to the
  nearest `GameArea`, and `PropTemplate` gives every prop its own, so a prop asking
  for "my attraction" gets *itself* back and silently does nothing. Walk up from
  `self.transform.parent` taking the first scope that exposes what you need
  (`dp.scope()` boots each script it touches, so the walk doesn't depend on Awake
  order). For Sequence navigation, skip the walk: call `dp.game_manager()` / `dp.next_level()`.

## PlayerHead.Override
`Assets/DreamPark/Scripts/Core/PlayerHead.cs`. A host app can put the player's head somewhere other than `Camera.main`; `GameArea` (zone presence) and `dp.head()` (Lua, PlayerPresence) read it first. Null = `Camera.main`. Content never sets it.

## `dp.local_player_present` and the content camera
`PlayerHead.LocalPlayerAbsent` is set by the host app while the local player is not in the park (diorama, relocalizing, a phone showing the miniature). While absent, content sees a stand-in camera far away at (10000, −10000, 10000) that never renders and rides the real head again once present, so cached references heal. In Lua: `dp.local_player_present()`. `dp.head()` and Lua `Camera.main` return the stand-in while absent; C# `Camera.main` is unchanged. Easy* player detection/chase/target/look and StandardEntity player ranges use it. Default false = the old behaviour. Guard enemies with `if dp.local_player_present and not dp.local_player_present() then idle end`.

## Platform notes (Oct 2026)
- **Overhead depth occlusion.** `DepthMaskCeiling` builds a dome mask over the player and is the ONLY writer of the global `EnvironmentDepthManager.MaskBias`. **Content must not write `MaskBias`**: a game that does breaks overhead occlusion park-wide. Lift a floor mask a few cm instead.
- **`dp.on_occurrence_removed(fn[, owner])`** → `fn({occurrence_id, resource, anchor})` when ONE attraction of a package is removed while the title stays loaded (an Arena rect set to "No Attraction", a swap, a repack that retires a rect), BEFORE its anchor is destroyed. Returns a handle (`h.off()` / `dp.off_occurrence_removed(h|fn)`); inside a script it is scoped to that script's package and dropped with the script. Still clean up in `ondisable`/`ondestroy`, and call it guarded: `if dp.on_occurrence_removed then dp.on_occurrence_removed(fn) end`. See PACKAGES.md.
- **The Lua pump never stops.** One title's Lua error no longer disables `dp.on_tap`/`on_touch`/peer/`shared_state` dispatch for every title; it logs `[DreamParkLuaPump] <name> threw (pump keeps running)` at most once per 10 s. Fix the error it names.
- **Compiled-chunk cache.** `LuaBehaviour` compiles each TextAsset once and runs it per scope instead of per instance. A file that can't be compiled that way falls back to the old path.
- **Sequence minimum room.** The Overlay and Game Over levels no longer raise a Sequence's `minimumRoomMeters` (they still count toward the maximum). Recompile and upload a title to pick it up.
- **Sessions.** A solo device that connects to a lone, YOUNGER room keeps its game (clock, seed, shared state, physics bodies) and the room adopts it; an established room still wins. Leader order is synced → headset → seniority → id. `RemoteRig.RegisterTemplate` also re-arms peers that are already built. LAN sessions recover on their own from network changes, a paused or restarted host, and a rebinding discovery listener.
