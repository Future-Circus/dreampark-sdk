# Audio Optimizer

Batch-optimizes audio under `Assets/Content/{Game}/` to keep source files
lean, AudioImporter settings sensible, and built bundles small. Pairs with
the Texture Optimizer and Animation Optimizer — same review workflow, same
GUID-preservation guarantees, designed for DreamPark's OTA content
pipeline where every megabyte costs the player a few hundred milliseconds
of download time.

## What it does

For every audio clip under the content folder, the optimizer:

1. **Classifies usage** — UI / SFX / Voice / Music / Ambient / Orphan,
   driven by path heuristic (`/Voice/`, `/Music/`, `/UI/`, …) and clip
   duration. References are gathered from both prefab AudioSources and
   `.lua.txt` `playSFXByName("…")` mentions so by-name clips don't get
   flagged as orphans.
2. **Picks a target encoding** — Vorbis at game-friendly quality presets
   (SFX/Voice/Ambient = q70, Music = q80, UI = q50). Sample rate snaps to
   the smallest of `{22050, 32000, 44100, 48000}` that's appropriate for
   the usage. Mono is forced on for SFX/Voice/UI; stereo stays for music.
3. **Picks a load type** — `DecompressOnLoad` for sub-5s SFX (zero
   playback latency), `CompressedInMemory` for voice and medium SFX,
   `Streaming` for music and long ambient loops.
4. **Shows the diff** — every row has play button, current
   format/duration/size, proposed encoding, estimated savings %. You
   approve per-row or in bulk before anything is committed.
5. **Re-encodes safely** — for `.wav` rows, the source is replaced with
   `.ogg` on disk and the Unity GUID is carried across via the `.meta`
   file. Every prefab AudioSource reference and Lua name-based lookup
   stays intact.

## Setup

Nothing to install. Open `DreamPark → Audio Optimizer...`. The first time
you open it, the tool auto-downloads OggVorbisEncoder + its transitive
NuGet dependencies (`System.Memory`, `System.Buffers`,
`System.Runtime.CompilerServices.Unsafe`) for your editor host (~1 MB
total, one-time) and configures them as editor-only plugins. Subsequent
opens are instant.

Why the deps are bundled alongside: Unity ships its own copies of some
of these for other packages (burst, Sentis, etc.) but at different
assembly versions, so OggVorbisEncoder's strong-name reference can't
bind to Unity's copies. Shipping the exact versions from NuGet is the
only reliable fix — but they're marked editor-only so they don't fight
Unity's built-ins at runtime in player builds.

Pure managed C# — no native binaries to package per platform, no
FFmpeg-sized install footprint. The DLL lands in
`Assets/DreamPark/ThirdParty/OggVorbisEncoder/Editor/` and is gitignored
so it never bloats the repo.

If the download fails (no internet, NuGet unreachable), the window shows
a clear error and offers a retry on next open.

## Architecture

| File | Role |
| ---- | ---- |
| `AudioPlan.cs` | Shared data structures + the per-usage policy (quality presets, sample rates, bytes estimator). |
| `AudioUsageGraph.cs` | Builds the clip → AudioSource → prefab + Lua-script reference graph. |
| `AudioOptimizationPlanner.cs` | Decides target encoding + load type + sample rate + estimates savings. |
| `WavReader.cs` | Minimal PCM WAV reader (8/16/24/32-bit + 32-bit float, mono/stereo) + linear resampler + stereo→mono mixer. |
| `OggVorbisEncoderInstaller.cs` | One-time auto-install of OggVorbisEncoder from NuGet on first use. |
| `OggVorbisEncoderBootstrap.cs` | Reflection-based wrapper around the auto-installed encoder. |
| `AudioOptimizationExecutor.cs` | Applies the plan: WAV → OGG re-encode (GUID preserved) + AudioImporter settings. |
| `AudioOptimizerWindow.cs` | The `DreamPark/Audio Optimizer...` EditorWindow. |

Pipeline:

    AudioUsageGraph.Build(root)
        → List<AudioUsage>
        → AudioOptimizationPlanner.Plan(usages)
        → List<AudioPlanRow>          ← review UI binds to this
        → AudioOptimizationExecutor.Apply(approved rows)
        → AudioExecuteResult

## Bumping the OggVorbisEncoder version

Version is pinned in `OggVorbisEncoderInstaller.PinnedVersion`. To bump:

1. Edit the constant.
2. Delete `Assets/DreamPark/ThirdParty/OggVorbisEncoder/Editor/` locally.
3. Open the Audio Optimizer — it'll redownload the new version.
4. Run a Scan + Apply on a test park, listen to a few clips.
5. If the reflection-based bootstrap can't find a method (OggVorbisEncoder
   broke its public API), unbump or update the reflection in
   `OggVorbisEncoderBootstrap.cs`.
6. Commit the constant change.

## Notes

- The optimizer never upsamples — target sample rate is clamped to source.
- `.aif` / `.aiff` sources are hard-skipped in v1 (the WAV reader doesn't
  handle the AIFF chunk layout). Convert to `.wav` first if you want
  on-disk savings; importer settings still get tuned.
- `.ogg` and `.mp3` sources are settings-only — we never transcode lossy
  → lossy, that just compounds artifacts. Only the AudioImporter settings
  change for those rows.
- Mono mixing is a simple L+R average. For stereo SFX where the channels
  are intentionally different (e.g. a panned stereo image of an
  explosion), the user should uncheck the "mono" toggle on that row.
- Sample-rate downsampling uses linear interpolation. Fine for the
  44.1 → 22.05 case (clean 2× ratio); audible artifacts above 4× ratios,
  which we never hit.
- Vorbis is editor-friendly: Unity decodes it natively on Quest, Windows,
  macOS, iOS, and Android. No platform-specific reimport needed.

## Recovery

If a re-encode produces a visibly wrong result, the fix is `git checkout
HEAD -- Assets/Content/Foo/Audio/Bar.wav` (and its `.meta`). The
optimizer's commit policy is: review before approve, commit your working
state before applying — that's your undo.

If OggVorbisEncoder failed to install or got stuck half-loaded, the
window shows a recovery card with a single "Reinstall" button that wipes
`ThirdParty/OggVorbisEncoder/Editor/` and re-downloads from scratch.
