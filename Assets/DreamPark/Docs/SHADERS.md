# Materials, shaders and particles

Why every material is on a DreamPark shader, how occlusion reaches alpha, and how particles must be built.
`§ Name` points to a section of these docs; the table at the end of `Assets/DreamPark/Docs/CLAUDE.md` maps every section to its file.

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
`ParticleSystem` in a prefab, one prefab per effect type, with its material on
`DreamPark/Particles` (see Materials & Shaders).

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
- Instantiate an effect at the spot from its prefab and destroy it when it finishes; never
  pre-place copies of it in the attraction (§ The attraction prefab).
- Imported particle packs: run `DreamPark → Optimization → Material Optimizer...` first.
  Expect `ParticleSystemRenderer` Custom Vertex Streams to be reset — that's the fix, not
  a regression.

## Depth masks

- **Overhead depth occlusion.** `DepthMaskCeiling` builds a dome mask over the player and is the ONLY writer of the global `EnvironmentDepthManager.MaskBias`. **Content must not write `MaskBias`**: a game that does breaks overhead occlusion park-wide. Lift a floor mask a few cm instead.
