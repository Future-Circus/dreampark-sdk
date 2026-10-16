# DreamPark package runtime — design record

> **Status note:** Arena was added after this spec as a third package kind. The current, complete description of all
> three kinds lives in `CLAUDE.md` § Packages and `PACKAGES.md`; this document remains the design record for how the
> DreamPark app loads a Sequence or an Adventure.

The staged Adventure manager is active while placement completes so newly instantiated attraction scripts can resolve
it in `Awake`; rollback destroys that manager and restores the previous one, but cannot undo arbitrary creator-authored
side effects that ran before a failed placement.

## Goal and invariants

A content title can offer an Adventure (spatial attractions distributed through a park) and a Sequence (one calibrated,
packable room that streams levels). The app loads a **package occurrence**, not a random prefab from a title. The old
loose level/prop path keeps working for parks without a package selection.

* `contentId` selects a versioned catalog; `packageKind` (`adventure` or `sequence`) selects a recipe. The park document
  or launch request carries both explicitly. URL/path segments are asset addresses, not the authority for mode.
* One active package per park owns one Player rig and one creator-owned Game Manager. A second distinct package request
  fails explicitly; it never silently replaces the first. Both are persistent across level changes. They are never
  children of a calibrated attraction/floor and never define a packing footprint. Shared `contentId/Player` and
  `contentId/Game Container` are the defaults; the recipe has explicit address fields to permit distinct
  package-specific assets later.
* The Game Manager is exempt from **distance** optimization, but remains subject to the park's build/play pause and
  teardown. The package root and the attractions are never exempt. Player priority keeps its existing behavior.
* A package has a stable instance identity. Repeating an attraction or Group creates distinct occurrences; no
  name/address de-duplication applies within a package. Groups are organizational, not spatial parents.
* A Sequence is one placed/calibrated `DreamSequenceTemplate`. It keeps its one floor-data source, shrink/grow bake
  limits, per-level floor cutouts, persistent overlay and transition, and lazy attraction loading. Start/Overlay/Game
  Over remain editable spatial AttractionTemplates. Invalid/incompatible attractions are excluded from compilation and
  block release where required.
* An Adventure places Start, ordered stage occurrences, and End as separate attractions along the park path. All
  required occurrences must fit, or the package plan fails atomically and the app falls back: Adventure → Sequence (if
  authored) → Arena (if it has buckets) → fail. Every package is optional. Hidden and Unused assets are not auto-placed.
  Optional props are placed only when explicitly present in the Adventure recipe.

## Release/runtime contract

The SDK compiles one Addressable `DreamParkPackageManifest` under `Assets/Content/{contentId}/` with the stable address
`{contentId}/Assets/DreamPark Package Manifest`. It is a runtime recipe, not a generated Sequence prefab. It contains the
schema version, content ID, the optional package recipes, and for each recipe: Player address, Game Manager address,
Sequence-definition address (Sequence only), ordered occurrences, occurrence IDs, asset addresses, roles (`start`,
`stage`, `end`, `prop`), required flags, and Group occurrence IDs/names. Compiled addresses use the same scheme as
ContentProcessor (`{id}/Levels/{size}/{name}`, `{id}/Props/{category}/{name}`). Unity editor GUIDs are never runtime
lookup keys. An absent package is distinguished from an empty one. The compiler validates addresses, duplicate
occurrence IDs, required Start/End, Sequence compatibility, and bake data.

The `DreamSequencePackageDefinition` remains the Sequence's lazy-load recipe and is referenced by the manifest; it is
**not** replaced by a prefab. The manifest's Sequence occurrence order and the definition's level order are generated
from the same flattened layout and checked for equality. Repeated source prefabs are legal.

The upload also publishes a separate, normalized `packages` description to the backend (`groups`/`attractions`/
`resourceName`/`role`/`required`) for discovery and auto-layout. The SDK's richer organizer layout is not sent as
`packages`; the backend rejects that shape. Runtime loading always uses the Addressable manifest tied to the mounted
catalog, never the public package endpoint, which serves approved releases only and cannot serve beta, offline or
pinned-version loads.

## Runtime lifecycle

1. Resolve the requested `contentId`, catalog version and `packageKind`. Mount the catalog and its dependencies, then
   load and validate the package manifest by explicit address. A missing manifest or package is an error for an
   explicit package request; legacy park objects stay on the old path. A saved `packageVersion` is checked against the
   catalog actually mounted; on a mismatch the app asks for a fresh plan rather than applying positional data from a
   different release.
2. Create a package owner beneath the park/portal lifetime root. Instantiate its Player and Game Manager once
   **before** attraction gameplay begins, through the player-claim mechanism with a package-instance-aware key. Claims,
   objects and asset handles are released on cancel, failure, rebuild or unload, and are attributed to the content ID.
3. The manager gets an `OptimizedAFIgnore` marker before optimizer registration and stays registered, so build-mode
   pausing still works. Every Adventure occurrence (and the Player) carries a package membership link, so
   `dp.package()`, `dp.game_manager()`, `dp.is_sequence()` and `dp.is_adventure()` resolve the correct owner even when
   Adventure attractions are spatially separate. Nothing depends on a singleton or a scene-wide name search.
4. For a Sequence, `DreamSequencePackageRuntime` builds the transient root, the spatial anchor gives it its pose,
   packing and floor data, and then it activates. Its children register with the optimizer only after the manager
   exemption is present. The Player and manager survive `DreamParkPackageHost.LoadLevel` swaps.
5. For an Adventure, the authored ordered occurrences become placement candidates that keep their occurrence ID,
   order, role, Group and required flag (also in the saved park document), with no attraction de-duplication. Placement
   favors a traversable path, faces each stage toward its predecessor, and respects explicit Start and End. Placements
   commit only after all required items fit; otherwise they are removed and the Sequence (if authored), then the Arena
   (if it has buckets), is tried as one unit.
6. A Sequence fallback needs a calibrated rectangle at or above its compiled minimum footprint. If no fallback package
   fits, the app reports a blocking placement failure rather than silently omitting required content.
7. Package builds are single-flight per active park. An overlapping request gets an explicit busy response; it never
   replaces an in-progress staged owner or leaves uncommitted attraction anchors behind.

Saved parks persist package kind, version/content ID, package instance ID, occurrence ID, resource address and
floor/packing data, so a reload is deterministic within the mounted catalog version. Old park documents with only loose
`resourceName` entries remain valid.

## Acceptance behaviour

* SDK compilation emits both a valid backend `packages` payload and an Addressable runtime manifest; beta ID
  retargeting and repeated attraction/Group occurrences work.
* A Sequence loads Start, Overlay, manager and Player once; Start navigates through levels to Game Over and back;
  transitions and floor anchors work after shrink and park reload. No generated Sequence prefab is required.
* An Adventure deploys Start → ordered stages → End, with distinct repeated occurrences, independently culled
  attractions, and a manager that keeps running after the player walks beyond the optimizer's far band.
* Missing required Adventure content triggers the atomic fallback (Sequence, then Arena); when nothing fits, the app
  reports failure. Hidden/Unused never enter auto-layout. Legacy loose-content parks still load unchanged.
* Cancellation, the build/play toggle, a content update and park teardown leave no duplicate Player, manager, package
  host or claims; package asset handles are released with the mounted content.
