# Texture Optimizer

Batch-optimizes textures under `Assets/Content/{Game}/` to keep source files
lean and built bundles small. Designed for DreamPark's OTA content pipeline
where every megabyte of texture debt costs the player a few hundred
milliseconds of download time.

## What it does

For every texture under the content folder, the optimizer:

1. **Classifies usage** — walks materials and prefabs to find each
   texture's role (Albedo / Normal / Mask / Emission) and the largest
   world-space renderer that uses it.
2. **Picks a format** — PNG for transparent textures, normals, and masks;
   JPG (quality 90) for opaque color textures. If a texture has an alpha
   channel but no pixel actually uses transparency, it gets downgraded to
   JPG automatically.
3. **Picks a resolution** — 256 for sub-20cm props, 512 for hand-held
   objects (sword, book), 1024 for characters/statues/walls. Sizes are
   driven by the bounds of the largest authored prefab using the texture.
4. **Shows the diff** — every row has thumbnail, current size/format,
   proposed size/format, estimated savings %. You approve per-row or in
   bulk before anything is committed.
5. **Mutates safely** — preserves the asset's Unity GUID across the
   extension change (.tga → .png) by carrying the .meta file across, so
   every material and prefab reference stays intact.

## Setup

Nothing to install. Open `DreamPark → Texture Optimizer...` (or the
**Pre Launch Options** section in the Content Uploader). The first time
you open it, the tool auto-downloads Magick.NET for your editor host
(~30 MB, one-time) and configures it as an editor-only plugin. Subsequent
opens are instant.

The download targets only your platform: macOS arm64 / macOS x64 /
Windows x64 / Linux x64 — whatever the Unity Editor is running on. The
binaries land in `Assets/DreamPark/ThirdParty/MagickNet/Editor/` and are
gitignored so they never bloat the repo.

If the download fails (no internet, NuGet unreachable), the window shows
a clear error and offers a retry on next open.

## Architecture

| File | Role |
| ---- | ---- |
| `TexturePlan.cs` | Shared data structures + the sizing policy. |
| `TextureUsageGraph.cs` | Builds the texture → material → prefab → renderer.bounds graph. |
| `TextureOptimizationPlanner.cs` | Decides target format + resolution + estimates savings. |
| `MagickNetInstaller.cs` | One-time auto-install of Magick.NET from NuGet on first use. |
| `MagickNetBootstrap.cs` | Reflection-based wrapper around the auto-installed Magick.NET. |
| `TextureOptimizationExecutor.cs` | Applies the plan: re-encode source + tighten TextureImporter, preserving GUIDs. |
| `TextureOptimizerWindow.cs` | The `DreamPark/Texture Optimizer...` EditorWindow. |

Pipeline:

    TextureUsageGraph.Build(root)
        → List<TextureUsage>
        → TextureOptimizationPlanner.Plan(usages)
        → List<TexturePlanRow>           ← review UI binds to this
        → TextureOptimizationExecutor.Apply(approved rows)
        → ExecuteResult

## Bumping the Magick.NET version

Magick.NET version is pinned in `MagickNetInstaller.PinnedVersion`. To
bump:

1. Edit the constant.
2. Delete `Assets/DreamPark/ThirdParty/MagickNet/Editor/` locally.
3. Open the Texture Optimizer — it'll redownload the new version.
4. Run a Scan + Apply on a test park, eyeball a few textures.
5. If the reflection-based bootstrap can't find a method (Magick.NET
   broke its public API), unbump or update the reflection in
   `MagickNetBootstrap.cs`.
6. Commit the constant change.

## Notes

- The optimizer never upsamples — target resolution is clamped to source
  dimensions.
- Lightmaps, cubemaps, light cookies, and directional lightmaps are
  hard-skipped (preserved exactly).
- UI sprites, particle textures, orphans, and unused-material textures
  are soft-skipped — they show in the table with a skip reason, but the
  reviewer can manually approve them and pick a target size.
- Normal maps stay PNG (lossless, linear) regardless of alpha channel.
  Their Max Size can still be tightened.
- Crunch compression is enabled by default on the new TextureImporter
  except for normal maps (crunched normals visibly degrade lighting).

## Recovery

If a re-encode produces a visibly wrong result, the fix is `git checkout
HEAD -- Assets/Content/Foo/Textures/Bar.tga` (and its .meta). Commit
your working state before running the optimizer — that's your undo.
