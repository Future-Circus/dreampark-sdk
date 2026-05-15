# Animation Optimizer

Batch-optimizes animation clips under `Assets/Content/{Game}/` — both
FBX sub-clips and standalone `.anim` files — by routing every clip
through Unity's own keyframe reducer on the `ModelImporter`. Companion
to the Texture Optimizer.

## Why this exists

A single bloated `.anim` can be the difference between a 200 MB
attraction and a 400 MB one. The canonical case: a rigger exports a
short idle animation, the FBX importer bakes every channel of every
bone at 24 FPS without keyframe reduction, and the clip ends up as
60 MB of YAML where every key is the identity quaternion repeated
hundreds of times.

The optimizer scans for these, proposes a strategy, and lets you
review each row before committing.

## What it does

The optimizer enumerates two kinds of animation clips:

- **FBX sub-clips** — AnimationClips embedded in `.fbx` / `.ma` / `.mb`
  model files. Optimization is a one-call `ModelImporter` settings flip
  on the host model.
- **Standalone `.anim` files** — separate YAML assets. For each one
  the optimizer searches for the source FBX by matching name + length +
  framerate + bone-path Jaccard similarity. When a confident match is
  found, the clip is "routed through" that FBX — the FBX gets the
  compression settings, then the compressed sub-clip's data is copied
  back to the standalone path with GUID preservation.

Standalone clips with no detectable FBX source are flagged as **orphans**
and skipped in v1 (a future custom reducer with proper quaternion handling
would handle these).

## Setup

Nothing to install. Open `DreamPark → Animation Optimizer...` (or the
**Pre Launch Options** section in the Content Uploader). The first scan
takes ~10-30 seconds depending on how many clips and FBXs are in the
folder — the bone-path similarity search is the slow step.

## Architecture

| File | Role |
| ---- | ---- |
| `AnimationPlan.cs` | Shared data types (row kinds, strategy enum, result types). |
| `AnimationUsageGraph.cs` | Scans clips, builds usage graph, runs FBX source detection via bone-path Jaccard similarity. |
| `AnimationOptimizationPlanner.cs` | Picks per-row strategy + tolerances, estimates savings. |
| `AnimationOptimizationExecutor.cs` | Routes each row through `ModelImporter` (sub-clips in-place; standalones via round-trip with GUID preservation). |
| `AnimationOptimizerWindow.cs` | The `DreamPark/Animation Optimizer...` EditorWindow. |

Pipeline:

    AnimationUsageGraph.Build(root)
        → List<AnimationUsage>           ← both row kinds, source FBX resolved
        → AnimationOptimizationPlanner.Plan(usages)
        → List<AnimationPlanRow>         ← review UI binds to this
        → AnimationOptimizationExecutor.Apply(approved rows)
        → ExecuteResult

## Execution paths

- **FBX sub-clip** — set the host FBX's `animationCompression` +
  `animationRotationError` / `animationPositionError` / `animationScaleError`,
  then `SaveAndReimport()`. Unity's keyframe reducer rewrites the
  embedded clips. No GUID work needed — the sub-clip stays as a
  sub-asset of the FBX.
- **Standalone with source** — three-step dance:
  1. Stash the standalone's `.meta` file content (the GUID lives here).
  2. Set the source FBX's importer settings and re-import. The FBX's
     embedded sub-clip now carries reduced curves.
  3. `EditorUtility.CopySerialized` the sub-clip into a fresh
     `AnimationClip`, `AssetDatabase.DeleteAsset` the original
     standalone, `CreateAsset` the new one at the same path, then
     overwrite the freshly-generated `.meta` with the stashed content.
     Unity's next `ImportAsset` picks up the carried-over GUID,
     preserving every reference.
- **Standalone orphan** — hard-skipped. No FBX source detected; v1 won't
  touch these.

## Strategies

- **Optimal** *(default)* — `Anim. Compression = Optimal`. Unity picks
  the best of keyframe reduction or dense compression per clip.
- **KeyframeReduction** — `Anim. Compression = KeyframeReduction`. Just
  removes redundant keys; keeps full-fidelity float storage.
- **KeepAsIs** — no-op. Sentinel for hard skips and user opt-outs.

## Error tolerances

Three global error fields in the window header — the same fields Unity
exposes on the `ModelImporter` inspector:

- **Rotation error** (degrees, default 0.5) — angular slerp distance
  below which a keyframe can be dropped.
- **Position error** (units, default 0.5) — linear distance threshold.
- **Scale error** (factor, default 0.5) — scale-factor delta threshold.

Tighter values preserve more fidelity, less compression. Looser values
compress harder and risk visible drift on fine motion. Persist across
sessions via EditorPrefs.

## Source detection

When the planner finds a standalone `.anim` whose bone-path set
overlaps with an FBX sub-clip by ≥ 50% (Jaccard similarity), it claims
the FBX as the source. Scoring breakdown:

- Identical clip name: +100
- Length matches within 0.01s: +50
- Same framerate: +10
- Bone-path Jaccard similarity × 1000 *(the dominant signal)*

A standalone is flagged as **diverged from source** if path similarity
is high but length or binding counts differ substantially — implying
someone hand-edited the standalone after extraction. Round-tripping
would clobber those edits, so the planner force-skips it for manual
review.

## Notes

- Lightmaps / legacy clips / read-only assets (in immutable packages)
  are hard-skipped.
- Standalone clips suppress their FBX sub-clip "twin" in the list when
  matched — otherwise the user would see two rows for the same
  animation.
- The optimizer never deletes anything. Orphan or unused clips show
  as soft-skipped rows; deletion is always a separate, explicit action.
- Unity strips `m_EditorCurves` automatically when it re-serializes a
  clip with `Optimal` compression — so the bloat-amplifier block we
  worried about in v1 just disappears for free.

## Recovery

If a re-import produces visibly wrong playback, the fix is `git checkout
HEAD --` on the affected files (both the `.anim` and the source FBX's
`.meta` — the FBX itself is unchanged, but its `.meta` carries the
import settings we flipped). Commit your working state before running
the optimizer.
