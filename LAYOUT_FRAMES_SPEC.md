# Attraction Layout Frames — implementation spec

Status: implemented (Sept 2026) in the SDK, the DreamPark app, the backend and the phone app.

## 1. Why

Flexible packing today gives an attraction one authored layout plus an automatic
shrink/grow bake, and one hand-picked "essential props" variant. For games whose
layout *is* the gameplay (a Breakout-style game), shrinking is wrong: you want fewer
bricks, not smaller or squashed ones. For big rooms, you want extra content, not
stretched spacing.

A **layout frame** is a complete, hand-authored arrangement of the attraction at a
specific **width × length** (both axes at once). A creator makes as many frames as
they like. Each frame gets its own automatic shrink/grow bake, exactly like the
authored layout gets today. At runtime the system picks **one** frame for the space
it was given and shrinks/grows *within that frame's own range*. Frames never blend
into each other — players never watch an attraction resize (packing is chosen once
at placement), so a frame switch is never visible.

A special **Minimum frame** replaces the `essentialProps` list: the smallest viable
version of the attraction (it can be a single prop). The inspector encourages every
attraction to have one.

### Decisions (fixed — do not relitigate)
- Frames are 2D (a width × length pair). No per-axis keys, no blending between frames.
- Minimum frame replaces essential props. Legacy essential data is migrated, and old
  park documents that say `essentialOnly: true` map to the Minimum frame.
- **An app update is acceptable.** Old app builds are NOT a compatibility target for
  new content. But the NEW app must keep loading OLD content (bakes without frames),
  OLD park documents, OLD manifests (schema 3) and OLD published packages (schema 2).
- Attractions with **no** frames must behave byte-for-byte as today (all existing
  tests keep passing unmodified).
- Arena: the same attraction may appear in several Arena spaces of one park at
  different sizes (no new per-park dedupe). This is an open product question; keep
  current behaviour and do not add dedupe.

## 2. Glossary

| Term | Meaning |
|---|---|
| Base frame | The prefab as authored. Implicit; id `base`; its bake is the existing `AttractionTemplate.packingBake`; its size is the attraction's `Size`. Never stored in the frame list. |
| Custom frame | Creator-made frame, stored in `AttractionTemplate.layoutFrames`, kind `Custom`, id `f` + 8 lowercase hex chars, generated once at creation (`"f" + Guid.NewGuid().ToString("N").Substring(0, 8)`). |
| Minimum frame | At most one, kind `Minimum`, fixed id `minimum`. |
| Frame envelope | `{frameId, kind, name, authored, shrink, grow, shrinkScale, growScale}` — one frame's baked range. Scales are relative to that frame's own authored size. |
| Ladder | All frame envelopes with a valid bake, ordered **largest → smallest** by authored area; ties: larger min(side) first, then base > custom > minimum, then list order. |
| Frame scale | Per-axis multiplier of a frame's own authored footprint (1,1 = the frame exactly as authored). |

Frame id validation everywhere (C#, JS, Swift): `^[a-z0-9][a-z0-9-]{0,31}$`.
Reserved ids: `base`, `minimum`.

## 3. SDK data model (`Assets/DreamPark/Scripts/Features/Attractions/`)

New file `AttractionLayoutFrame.cs` (namespace `DreamPark`):

```csharp
public enum AttractionLayoutFrameKind { Custom = 0, Minimum = 1 }

[Serializable] public sealed class AttractionLayoutOverride {
    public string path;            // hierarchy path from the attraction root, same format as AttractionPackingBaker.PathFrom; authoritative
    public Transform target;       // resolved reference (fast path); re-resolved from path when copying into a prefab
    public bool setActive;   public bool active;
    public bool setPosition; public Vector3 localPosition;
    public bool setRotation; public Quaternion localRotation = Quaternion.identity;
    public bool setScale;    public Vector3 localScale = Vector3.one;
}

[Serializable] public sealed class AttractionLayoutFrame {
    public string id;
    public string name;
    public AttractionLayoutFrameKind kind;
    public Vector2 sizeFeet;                       // x = local-X width, y = local-Z length
    public List<AttractionLayoutOverride> overrides = new List<AttractionLayoutOverride>();
    [SerializeField] private AttractionPackingBake bake;   // authored footprint == SizeMeters
    public AttractionPackingBake Bake => bake;
    public Vector2 SizeMeters => sizeFeet * 0.3048f;
    public void SetBake(AttractionPackingBake value) => bake = value;
}

[Serializable] public sealed class AttractionLayoutBaseState {   // authored (Base) state of one transform any frame/bake may touch
    public string path; public Transform target;
    public bool active; public Vector3 localPosition; public Quaternion localRotation; public Vector3 localScale;
}

[Serializable] public struct AttractionFrameEnvelope {
    public string frameId; public string kind;     // "base" | "custom" | "minimum"
    public string name;
    public Vector2 authoredMeters, shrinkMeters, growMeters, shrinkScale, growScale;
}
```

Additions to `AttractionTemplate`:

```csharp
public const string BaseFrameId = "base";
public const string MinimumFrameId = "minimum";
[SerializeField, HideInInspector] private List<AttractionLayoutFrame> layoutFrames = new List<AttractionLayoutFrame>();
[SerializeField, HideInInspector] private List<AttractionLayoutBaseState> layoutBaseStates = new List<AttractionLayoutBaseState>();
[SerializeField, HideInInspector] private bool legacyEssentialMigrated;
[SerializeField, HideInInspector] private Vector2 layoutPreviewFootprintMeters;   // zero = unset (editor preview)
[SerializeField, HideInInspector] private string layoutPreviewFrameId;           // empty = auto

public IReadOnlyList<AttractionLayoutFrame> LayoutFrames { get; }
public bool HasLayoutFrames { get; }                   // layoutFrames has ≥ 1 entry
public AttractionLayoutFrame MinimumFrame { get; }     // null if none
public AttractionLayoutFrame FindFrame(string id);      // custom/minimum only; null for base/unknown
public static bool IsValidFrameId(string id);
public bool FrameHasValidBake(AttractionLayoutFrame frame); // bake valid && authored ≈ SizeMeters && growScale ≈ requested grow scale
public List<AttractionFrameEnvelope> GetFrameEnvelopes();   // the ladder; empty when there is no valid base bake; base only when no frames
public bool TryGetFrameEnvelope(string frameId, out AttractionFrameEnvelope envelope);
public Vector2 MinimumFootprintMeters { get; }         // smallest-area shrink across the ladder (legacy: min(shrink, essentialShrink when essential props))
public bool TrySelectFrame(Vector2 targetMeters, out string frameId, out Vector2 frameScale);
public bool ApplyLayout(string frameId, Vector2 frameScale);
public bool ApplyFootprint(Vector2 targetMeters);      // TrySelectFrame + ApplyLayout (applies the fallback even when selection fails; returns the selection result)
public string CurrentFrameId { get; }                  // runtime; "base" until something else is applied
public Vector2 CurrentFrameScale { get; }
public Vector2 GetFrameFootprintMeters(string frameId, Vector2 frameScale); // clamped to that frame's [shrink, grow]
public void SetLayoutPreview(Vector2 targetMeters, string frameIdOrEmpty);  // editor preview (Play Mode applies; Edit Mode is ghosts only)
// Editor/baker mutation API (public so Editor assemblies can call it):
public void SetLayoutFrames(List<AttractionLayoutFrame> frames);
public void SetLayoutBaseStates(List<AttractionLayoutBaseState> states);
public bool LegacyEssentialMigrated { get; set; }
```

`essentialProps` gets `[HideInInspector]` (data kept for migration). `AttractionPackingBake.CurrentVersion` → **10**
(versions 8–10 stay valid), `CatalogSchemaVersion` → **9**. Add a serialized `bool mirrorsMinimumFrame` to
`AttractionPackingBake` (with a constructor overload/setter); `HasEssentialProps` returns true when legacy essential
poses exist **or** `mirrorsMinimumFrame` is set.

## 4. Runtime selection & application (the app runs this)

**`TrySelectFrame(target)`**: walk the ladder; the first envelope whose `shrink.x ≤ target.x + 1e-4` and
`shrink.y ≤ target.y + 1e-4` wins. `frameScale = clamp(target / authored, shrinkScale, growScale)` per axis. If
nothing fits: choose the envelope with the smallest shrink area, `frameScale = shrinkScale`, return false. No
rotation — callers orient the target into prefab-local axes first (exactly as today).

**`ApplyLayout(frameId, frameScale)`**:
- No frames (legacy content): `base`/empty/unknown → existing `ApplyPackingVariant(frameScale, false)` path,
  unchanged. Anything else → warn once, treat as `base`.
- With frames:
  1. Restore every `layoutBaseStates` entry (active + local TRS). Resolve by `target`, falling back to `path`.
  2. Apply the chosen frame's overrides (Base has none).
  3. Apply the chosen frame's bake at `frameScale` using the SAME per-pose math as today
     (`ResolvePose`/`ResolveAxis` for props, follower position/scale logic) but **never the essential branch**;
     prop roots `SetActive(pose.AuthoredActive)`.
  4. `runtimePackingFootprintMeters = frame.authored × clamp(frameScale)`; set `CurrentFrameId/Scale`;
     `RefreshPackingDependentSystems` and FloorAnchor recache as today.
  - Unknown frame id → warn once, apply `base`. A frame without a valid bake → treat as unknown.
  - Must be idempotent and correct when switching A → B → A on a live object (build-mode map edits and
    Sequence re-apply packing to live attractions).

**Legacy mapping** — `ApplyPackingVariant(scale, essentialOnly)` and `GetPackingFootprintMeters(scale, essentialOnly)`
where `scale` is relative to the BASE authored size:
- No frames: unchanged.
- With frames: `essentialOnly && Minimum frame valid` → target = baseAuthored × scale →
  `ApplyLayout("minimum", target / minimumAuthored)` (clamped). Otherwise `ApplyLayout("base", scale)`.

**Editor preview**: keep `SetPackingPreview`/Awake/LateUpdate behaviour for no-frame attractions. With frames,
`SetLayoutPreview` drives the same editor-only Play Mode override through `ApplyLayout` (auto frame via
`TrySelectFrame`, or the forced frame at target/authored). Default/unset preview must leave real park packing
decisions untouched (same rule as today's (1,1) default).

## 5. Baking (`Assets/DreamPark/Editor/AttractionPackingBaker.cs`)

- `Bake(attraction, recordUndo)` bakes Base **and** every frame, then records `layoutBaseStates`.
- Refactor the existing algorithm into a routine that takes the authored footprint explicitly (do NOT mutate
  `size`/`customSize` to bake a frame) plus two switches: `includeLegacyEssential` and `skipInactive`.
  Safe footprint = authored × (1 − 2·safeAreaInset) as today.
- **No frames (after migration check)** → exactly today's algorithm and results.
- **With frames**:
  - Every bake (Base included) uses `skipInactive = true`: packing items / followers whose root is not
    `activeInHierarchy` in that frame are ignored (so extras that only exist in big frames never block Base).
  - No legacy essential variant is computed. The Base bake's legacy essential fields MIRROR the Minimum frame:
    `essentialShrinkFootprint = minimum.shrink`, `essentialShrinkScale = minimum.shrink / base.authored`,
    `mirrorsMinimumFrame = true`. With no Minimum frame: essential fields = shrink fields, `mirrorsMinimumFrame = false`.
    Pose-level essential positions = shrunk positions.
  - Per frame, in a try/finally: snapshot every transform under the root (active + local TRS), apply the
    frame's overrides, `Physics.SyncTransforms()`, bake with authored = `frame.SizeMeters`, store the bake on the
    frame, restore the snapshot. The bake's Transform references point at the live objects (valid for the
    prefab-contents copy used by `BakeIntoAsset` and for in-place stage/scene bakes). Never leave the hierarchy
    in a frame state, even on exception.
  - `layoutBaseStates` = union of all override targets (resolved by path) + every prop/follower transform in any
    bake (Base and frames), captured in the Base state.
- **Migration** (runs at the start of `Bake`): if `!legacyEssentialMigrated` and `essentialProps` has at least one
  non-null entry inside the attraction and there is no Minimum frame → create a Minimum frame: name "Minimum",
  `sizeFeet` = the attraction's current size in feet, overrides `setActive=false` for every packing root (same
  roots `CollectItems` returns for Base) that is active and NOT essential. Set `legacyEssentialMigrated = true`.
  Keep `essentialProps` data (hidden). Attractions with no essential props are not migrated and get no frames.
- `BakeIntoAsset`/`CopyPackingAuthoringSettings`: deep-copy `layoutFrames` (re-resolve `target` from `path` in the
  destination; unresolved → keep override, `target = null`, warn), copy `legacyEssentialMigrated`.
  `RevertPackingOverrides` also reverts the new serialized fields.
- `internal static List<string> GetFrameWarnings(AttractionTemplate)` for the inspector: override path not found;
  frame layout overflows its size (measured active-item footprint at scale 1 > size + 0.05 m); Minimum frame's
  shrink area ≥ Base shrink area; two frames with the same size (±0.01 ft); frame bake stale/missing.
- Also create `Assets/DreamPark/Editor/AttractionLayoutFramePreviewPaths.cs` (see §7) so later work can share it.

### 5.1 Honest, continuous, collision-free frame bakes (2026-09-27, fix F3)

Found on A_BlockParty: frame envelopes lied (shrink = min(declared, measured), so a frame whose content was
bigger than its size claimed to shrink by nothing), shrink poses collapsed every prop to the centre while a few edge props held
the footprint, X/Z were packed jointly although the runtime resolves them independently (props slid through each
other at mixed width/length), and the ladder left gaps between frames. Everything below applies **only when the
attraction has layout frames** (every frame bake, Base included). Frame-less bakes are unchanged. Supersedes the
§3 validity rule and the §5 "overflows" warning.

- **Separable shrink pose** (`PackSeparable`). The runtime moves each prop per axis
  (`ResolveAxis`), so any `(sx, sz)` pair must be collision-free, not only the shrunk corner. Pack one axis with
  the other at its authored coordinates (same-row props never pass through each other), then pack the second
  axis treating every pair that overlaps on the first axis *anywhere between authored and shrunk* (exact: the
  interpolation is linear) as overlapping there permanently. Both orders (X→Z, Z→X) are tried; the smaller
  footprint area wins. Growth is computed per axis (grown X depends only on the X scale) and, where growth would
  bring a pair together on one axis while the other axis can overlap, that pair's grow groups merge on that axis.
- **Relax pass** (`RelaxAxis`). After packing an axis and measuring its symmetric footprint S, move every prop
  back toward its authored coordinate, outermost first, as far as it can while staying inside S and without a
  new overlap, until stable. The shrink pose therefore moves each prop only as much as the footprint shrinks.
  The recorded shrink stays S.
- **Bake-time collision check** (`FindGridCollision`): a 5 × 5 grid of independent X/Z scales (shrink, halfway,
  authored, halfway, grow per axis) resolved with the runtime math; pairs intersecting as authored are exempt,
  pairs authored closer than the clearance may only not intersect. The first collision is stored in the bake
  (`layoutIssue`) and shown by `GetFrameWarnings` with the pair and the size.
- **The declared size is the authored footprint** (product decision, 2026-09-27).
  A frame bakes at exactly its declared size (Base: the attraction's Size). Content that overhangs it is
  allowed and intentional: nothing is pre-fitted, re-centred or moved to make it fit, and the size is never
  raised. The packing math runs against the content's own extent and the envelope is anchored to the declared
  size: per axis `shrink = packed × min(1, declared / asPlaced)` — the packed extent when the content fits (the
  original rule), `declared × packed / asPlaced` when it overhangs (both extents measured symmetrically about the
  pivot, so off-centre content cancels in the ratio). Content and reserved footprint keep their authored
  proportion across the whole range; shrink ≤ declared always.
- **Overhang note** (informational, not a problem): "‘{frame}’: content overhangs its W × L ft frame by up to
  X × Y ft (allowed …)", X × Y = the worst side's overhang per axis. Base's note is listed first. There are no
  "can't fit" warnings.
- **Bake version 11** (8–11 valid): serialized `requestedFootprintMeters` (the declared size, always equal to the
  authored footprint for frame bakes) and `requestedGrowScale` (the attraction's max multiplier).
  `HasPackingBake`/`FrameHasValidBake` compare those (legacy fields for older bakes), so chained growth is not
  "stale".
- **Chained growth** (`ChainGrowth`). Walk the ladder (largest → smallest). Frame k grows, per axis, to at least
  `authored × maxGrowthScale`, every larger frame's shrink, and the point on the **Size line** where the next
  larger frame starts to fit. The Size line runs from the smallest-area shrink (`MinimumFootprintMeters`) to the
  componentwise largest grow; it is the debug preview's Size slider and the dotted line on the coverage chart.
  Frames needing more than their multiplier are re-baked with the larger per-axis scale. Result: along the Size
  line `TrySelectFrame` always returns a footprint equal to the space, and it never shrinks as the space grows.
  Limits: spaces far off that line on one axis (much wider than every larger frame's shrink while too short for
  them) can still leave margin on that axis; the top frame's own grow is not chained, so a line end beyond it
  (differently shaped frames) shows margin; a frame whose content is inherently smaller (e.g. a single-object
  Minimum) reserves the space but its content does not fill it; at a switch between frames the raw content
  differs by the difference in how much each frame's content overhangs its declared size.
- **Inspector**: Size slider (0–100 % along the Size line) above the width/length sliders; "Chosen: {frame} at
  W × L ft (+margin …)". The coverage chart draws the runtime selection map (cells coloured by the frame
  `TrySelectFrame` picks; hatched = that frame beyond its grow; empty = nothing fits), each frame's shrink → grow
  as an outline, and a legend with authored · shrink → grow in feet. The bake summary shows Base as authored,
  the smallest achievable (smallest-area shrink) and largest (componentwise max grow) sizes across the ladder.
  With frames the reset button reads "Reset Debug Preview to Base (as authored)" (the declared Base size).

## 6. Authoring UX (Editor)

Inspector (`AttractionTemplatePackingEditor`), new **Layout Frames** section above the debug preview:
- **Base** row (read-only): size, shrink → grow in feet.
- **Minimum** slot, pinned. When missing: warning HelpBox "No Minimum frame — this attraction can never be smaller
  than {base shrink} ft. Create one: turn off everything the game can live without." + **Create Minimum Frame**
  (size = Base size, no overrides, immediately enters edit mode).
- **Custom frames** list: editable name, size (width × length ft), override count, "shrinks to A × B ft, grows to
  C × D ft", buttons **Edit**, **Duplicate**, **Delete** (confirm). **Add Frame** (size defaults to the current
  preview footprint rounded to 0.5 ft, else Base size).
- **Coverage chart** (IMGUI, ~180 px tall): width on X, length on Y, in feet, 0 → largest grow. Each frame's range
  is a translucent rectangle from its shrink corner to its grow corner, with a dot at its authored size and a
  label; a dashed reference box for the Sequence room (12 × 18 ft, both orientations). Hover highlights a frame.
- **Debug preview**: width/length sliders in **feet** (overall minimum shrink → overall maximum grow) and a frame
  popup (Auto + each frame). Shows "Chosen: {frame} at {w × l ft}". Play Mode applies via `SetLayoutPreview`;
  Edit Mode draws ghosts of the chosen frame's bake (objects inactive in that frame are not drawn).
- Frame warnings from `GetFrameWarnings`.
- Attractions without frames keep the existing inspector content working (bake button, maximum multipliers, etc.).

**Frame edit session** (new Editor file(s)):
- **Begin(attraction, frameId)**: snapshot every transform under the root (path → active, local TRS); apply the
  frame's overrides; Scene view draws that frame's rectangle (size) with a label "Editing frame X (W × L ft)".
  Inspector shows a sticky panel with the frame size fields, **Save Frame**, **Cancel**.
- **Save/Commit**: diff hierarchy vs snapshot → overrides containing only the changed properties
  (epsilon: 1e-4 m position/scale, 0.01° rotation, any active change). Objects **created during the session** are
  kept in the prefab, set inactive in Base, and get `setActive=true` in this frame (this is how creators add extras
  for big frames). Objects **deleted** during the session cannot be restored: show an error telling the user to
  Undo, and do not commit. Then restore the snapshot, mark dirty, and rebake (the save processor / BakeIntoAsset
  path as appropriate).
- **Cancel**: restore the snapshot.
- **Guards** — never let a frame state be saved as Base: in `AttractionPackingPrefabSaveProcessor.OnWillSaveAssets`,
  if a session is active for that prefab, commit it first (restore Base + bake), then re-enter the session after
  the save via `EditorApplication.delayCall`. Also commit on `AssemblyReloadEvents.beforeAssemblyReload`,
  `PrefabStage.prefabStageClosing`, and `playModeStateChanged(ExitingEditMode)`.

## 7. Previews & Content Uploader

**Paths** (`AttractionLayoutFramePreviewPaths`, Editor):
- `PathFor(contentId, prefabName, frameId)` → `Assets/Content/{contentId}/Previews/Frames/{prefabName}@{frameId}.png`
- `FolderFor(contentId)` → `Assets/Content/{contentId}/Previews/Frames`
- `MetadataKey(prefabName, frameId)` → `{prefabName}@{frameId}`
- `TryParse(fileName, out prefabName, out frameId)`
- Base keeps `Previews/{prefabName}.png`. The Frames subfolder must not be mistaken for base previews and must not
  create duplicate Addressables addresses or ContentProcessor errors (check how ContentProcessor treats
  `Previews/`; exclude `Previews/Frames/` from address assignment if needed — preview groups are build-excluded).

**Rendering**: `PrefabPreviewRenderer.RenderPreview` gains an optional frame id; when set and the clone has an
`AttractionTemplate` with that frame, call `ApplyLayout(frameId, Vector2.one)` on the clone before bounds are
measured. Anything that frames the shot from the attraction size must use `RuntimeFootprintMeters`.
Generation (`GenerateAllLevelPreviews`, `RegeneratePreviewForPrefab`) renders every frame (Minimum included) at the
base resolution, skipping existing files unless forced, and deletes `{prefab}@*.png` for frames that no longer
exist. `PreviewMetadataStore` stores per-frame settings under `MetadataKey`; missing → fall back to the prefab's
base settings. `PreviewEditorWindow` can edit a specific frame (frame selector or `Open(..., frameId)`).

**Stack tile** (`ContentUploaderPanel.DrawCard` and entry model): an attraction with ≥ 1 frame draws as a stack
(two offset card outlines behind it), a count badge `×N` (N includes Base), and an expand chevron. Expanding shows
an inline row of frame cards — Base first, then custom by area (largest first), Minimum last — each with its frame
preview, the frame name and "W × L ft". Expanded state persists in EditorPrefs keyed by content id + prefab GUID.
Clicking a frame card opens the preview editor for that frame. Main-card click behaviour is unchanged. Applies to the
library/top-level attraction grids; the Arena organizer shows frames as separate candidates instead (§10).

**Upload**: after the base preview upload, upload each existing frame PNG to
`POST /api/content/{id}/attractions/preview?resourceName={rn}&frame={frameId}` (multipart `image`, same as base).
Failures are logged and non-fatal.

**Backend**: an optional `frame` (query or body) stores a per-frame preview beside the base preview and records it on
the catalog row (`framePreviews.{frame} = {url, url64?, url256?, url512?, updatedAt}`). Absent or `base` behaves exactly
as before.

## 8. Catalog packing profile v9 (per catalog row `packing`)

SDK `BuildPackingUpload` (take the `AttractionTemplate`, not just the bake):
- Top-level legacy fields = Base bake (with mirrored essential fields per §5). `schemaVersion: 9`.
- `essentialProps`: with frames → the Minimum frame's active packing roots (same item shape as today) or `[]`;
  without frames → unchanged.
- `frames`: present only when the attraction has frames — every ladder envelope in ladder order:
  `{ "id", "name", "kind": "base"|"custom"|"minimum", "authored": {widthFt,lengthFt}, "shrink": {…}, "grow": {…},
  "shrinkScale": {x,z}, "growScale": {x,z} }`.

The backend normalizes the profile:
- When `frames` is present it must be an array of ≤ 64 valid entries (id regex, kind, dims, scales, name ≤ 200 chars),
  exactly one `base`, at most one `minimum`, unique ids; any violation → the whole profile is `null`. Output carries
  `frames` (received order) when present.
- Minimum dimensions with frames come from the smallest shrink-area frame
  (`{widthFt, lengthFt, packingScale: shrinkScale, frameId, essentialOnly: false}`).
- Packing variants with frames: for each frame in order, authored then shrink (with `frameId`), no essential variant.

The app parses `frames` (feet → metres) into its packing profile (§12).

## 9. Package manifest schema 4 (`DreamParkPackageManifest.cs`)

- `CurrentSchemaVersion = 4`. Readers accept 3 and 4.
- New `[Serializable] PackageFrameEnvelope { string frameId; string kind; string name; Vector2 authoredFootprintMeters,
  shrinkFootprintMeters, growFootprintMeters; Vector2 shrinkScale = one, growScale = one; }`.
- `PackageOccurrence` gains `string frameId` (empty = runtime ladder may choose any frame; set for Arena) and
  `List<PackageFrameEnvelope> frames` (full ladder when the prefab has frames; empty otherwise). Legacy envelope
  fields stay = Base (+ mirrored essential).
- `DreamParkPackageCompiler.CopyPackingMetadata` fills `frames` for every occurrence whose prefab has frames.

## 10. Arena

- `ArenaPackageStore` → store schema **3**. A *candidate key* is the prefab GUID (Base) or `{guid}:{frameId}`.
  `Bucket.guids`, `excludedGuids`, `SizeOverride.guid` hold candidate keys (field names unchanged; document it).
  Schema 2 files load as Base keys; newer-than-3 still throws.
- Candidates: an AttractionTemplate with frames yields one candidate per ladder envelope — Base keyed by GUID at its
  shrink footprint floored (as today), each frame keyed `{guid}:{frameId}` at *its* shrink footprint floored and
  rotation-normalized. If two candidates of one prefab land in the same bucket size, keep the one with the larger
  authored area. Props and frame-less attractions are unchanged. Reconcile drops keys whose frame disappeared and
  appends new frame candidates to their natural bucket (same as new prefabs).
- UI: Arena cards are per candidate (no stack) with the frame's preview (§7 paths; Base uses the normal preview),
  title "{prefab} · {frame name}" for non-Base, and size label. Drag, size override, exclude and context menu work
  per candidate key.
- Compile: one occurrence per bucket entry. `occurrenceId` = `arena-{guid}` (Base, unchanged) or
  `arena-{guid}-{frameId}`; `frameId` = `base` or the frame id; bucket width/length; envelope + `frames`.
- Published `packages` JSON (`BuildPublishedPackagesJson`): `PublishedPackageSchemaVersion = 3`; Arena entries add
  `"frameId"` only when not Base.
- The backend accepts packages schemaVersion 2 and 3; Arena entries take an optional `frameId` (`base` normalized to
  omitted) and are de-duplicated on attraction + frame. The normalized Arena `schemaVersion` is 3 only when some entry
  has a `frameId`, else 2 (existing revisions stay identical).
- **The bucket is authoritative for fit** (decision 2026-09-27): buckets stay *floored*, so a
  candidate's exact footprint can exceed its bucket by < 1 ft (more with a creator size override). The app
  places any Arena entry in a space of its bucket's size: a rung that fits exactly still wins; otherwise
  the entry uses its smallest rung (pinned frame's shrink; legacy: essential/shrink). The post-spawn
  reservation check compares against the chosen variant, and an overflow ≥ 1 ft logs a warning.
  Adventure, loose hubs and Sequence keep exact fitting.
- Park Simulator (`ParkSimPackageTest.cs`, Arena preview state): apply the occurrence's pinned frame via
  `ApplyLayout`; replace direct `ApplyPackingVariant(..., false)` calls with frame-aware equivalents.

## 11. Sequence

- `DreamSequenceCompatibility.IsCompatible`: authored fits 12 × 18 ft either way, OR any ladder envelope's shrink
  fits either way.
- New `DreamSequenceCompatibility.TryChooseFrame(AttractionTemplate, Vector2 roomMeters, bool allowRotation,
  out string frameId, out Vector2 frameScale, out bool rotate)`: walk the ladder; per envelope try native then (if
  allowed) rotated; first envelope with any fitting orientation wins. Within that envelope prefer: native if
  authored fits natively → rotated if authored fits rotated → native if shrink fits natively → rotated.
  `frameScale = clamp(orientedRoom / authored, shrinkScale, growScale)` in prefab axes. Frame-less attractions
  reproduce today's `ShouldRotate`/`SequenceScale` results exactly.
- `DreamSequencePackageCompiler`: minimum room uses each level's **smallest-area** envelope whose shrink fits the
  standard room (rotation only for stage levels, as today) in the orientation that fits; maximum room uses the
  largest envelope's grow in its chosen orientation. Special levels use the same helpers without rotation.
- `DreamParkPackageHost` (`SetRoomFootprintMeters`, `ApplyRoomFit`, `CanFit`, `ScaleFor`): levels with frames
  choose via `TryChooseFrame` (stage levels may rotate; Start/Overlay/Game Over do not) and apply with
  `ApplyLayout`; keep the "cannot fit calibrated room" failure when nothing fits; log the chosen frame. Frame-less
  levels: unchanged.
- `DreamSequenceRequiredCheck` and uploader compatibility badges keep using `IsCompatible` (now frame-aware).

## 12. App packer & park documents

- The app's packer walks one ladder everywhere (Arena, Adventure, hub placement, map editing): with frames → the
  pinned frame only when set, else each frame largest → smallest: authored fits (either rotation) → `(frameId, 1)`;
  shrink fits → `(frameId, shrinkScale)`. Without frames → exactly the legacy authored → shrink → essential ladder.
  Sequence planning is unchanged (it uses the Sequence root envelope).
- Park document: `packing.frameId` (string, optional). Written when non-empty and not `base`, and never together with
  `essentialOnly`. When `frameId` is set, `packingScale` is relative to THAT frame's authored size. Legacy
  `essentialOnly:true` without `frameId` → `AttractionTemplate.ApplyPackingVariant(scale, true)` (maps to Minimum when
  the content has frames). Spawn applies `template.ApplyLayout(frameId, scale)`; the backend and the phone app
  round-trip `frameId`.

## 13. Compatibility matrix (new app)

| Input | Behaviour |
|---|---|
| Content baked before frames | Legacy code paths, identical results |
| Park doc `{packingScale}` | Base frame at that scale |
| Park doc `{packingScale, essentialOnly:true}` | Minimum frame if the content has one, else legacy essential variant |
| Park doc `{packingScale, frameId}` | That frame at that scale; unknown id → Base (warn) |
| Manifest schema 3 | No frames; legacy ladder |
| Published packages schema 2 | Unchanged |
| Catalog packing v8 | No frames; legacy ladder |

## 14. Tests (minimum)

- SDK EditMode (`AttractionPackingTests` + a new `AttractionLayoutFrameTests`): frame bake honours overrides and
  restores the hierarchy (including on exception); inactive items ignored when frames exist; migration from
  essentialProps; selection picks the largest fitting frame, scale clamping, fallback; ApplyLayout A→B→A restores
  correctly; legacy essentialOnly → Minimum; frame-less attractions unchanged (existing tests untouched);
  BakeIntoAsset copies frames with path remapping.
- Arena store migration 2→3, per-frame candidates and same-bucket dedupe; compiler occurrence ids/frameIds;
  published JSON v3. Sequence TryChooseFrame, IsCompatible with frames, min-room shrinkage. Host picks frames.

## 16. Out of scope

Consumer-app (iOS/website) display of per-frame previews; per-park Arena dedupe; per-frame growth multipliers;
blending between frames.
