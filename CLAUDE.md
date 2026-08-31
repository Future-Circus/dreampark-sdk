# DreamPark SDK — Content Creator Template

## What This Is
SDK template for third-party game developers. A complete Unity 6 project cloned from dreampark-core. Creators fork this repo and place their game content in Assets/Content/{GameName}/.

## Template Structure
```
Assets/
├── [StartHere].unity  ← Startup scene, auto-opened on first project open (StartupSceneOpener,
│                        configured in Assets/.dreampark-editor.json, mode FirstOpenOnly;
│                        re-point via DreamPark → Startup Scene...). Meta building blocks
│                        pre-wired + Player.prefab and Attraction.prefab instances.
├── DreamPark/         ← SDK source (~240 first-party C# files, 661 counting vendored
│                        ThirdParty/; must match dreampark-core exactly)
├── Content/
│   ├── Sample/                ← Bundled worked example (2 attractions, 5 props, Sample.unity,
│   │                            8 Lua scripts). Deliberately NOT gated for browsing/editing;
│   │                            RESERVED only in that it is never the creator's content and
│   │                            cannot be published. See ContentFolders.IsSample.
│   └── YOUR_GAME_HERE/        ← Renamed via the in-editor setup popup (ContentIdSetupPopup, auto-opened by PlaceholderContentDetector; letters+digits, starts with a letter, 2–64 chars). Folder name = content ID. Also reserved until renamed. Multiple content folders may coexist — each is an independent package; the Content Uploader's dropdown picks which to publish.
│       ├── Prefabs/            ← Player.prefab + Attraction.prefab ship in the template;
│       │                         A_*.prefab attractions (AttractionTemplate root),
│       │                         P_*.prefab props (PropTemplate root)
│       ├── Previews/           ← {prefabName}.png tile art
│       ├── Scripts/            ← Game-specific C# (minimal — prefer Lua) + .lua.txt
│       ├── ThirdParty/         ← Only used assets (git-tracked, shipped in builds)
│       └── ThirdPartyLocal/    ← Imported packages land here (excluded from builds by
│                                 ContentProcessor — but NOT actually gitignored, despite
│                                 what several comments in the repo claim)
```

There are no `1. Scenes/` or `2. Features/` folders — that layout predates the current template.

## SDK Sync
The files in Assets/DreamPark/ must match dreampark-core exactly (~240 first-party C# files; 661 including vendored `ThirdParty/`). `#if DREAMPARKCORE` blocks (core-only code) are conditionally compiled out of this SDK distribution — the source remains visible but doesn't compile in SDK builds. Use conditional compilation to mark what's core-specific. Anything entirely core-only (no SDK reason to exist at all, e.g. consumer-app pairing flows, internal admin tooling) should live in dreampark-core's own `Assets/Scripts/` outside `Assets/DreamPark/` rather than as an empty SDK file.

## The Three Primitives
- **Player.prefab** (`Assets/Content/{GameName}/Prefabs/`): Root player object (persists across attractions). Global systems (audio, score, park state) live here as LuaBehaviours.
- **AttractionTemplate** (: LevelTemplate): root component of an attraction prefab — a self-contained experience (arcade game, boss battle, challenge course). LevelTemplate's `[RequireComponent]`s auto-add GameArea (presence detection — drives PlayerRig show/hide AND playtime-based revenue attribution) and MusicArea. Defines the physical space (size/customSize, floor generation, calibration).
- **PropTemplate**: root component of a prop prefab — the individual interactive elements that make up an attraction (coin, hammer, enemy). Auto-adds its own GameArea at priority -1 (self-suppressed when nested inside an attraction). Props are also placeable standalone in parks.

There is no creator-facing DreamBand or Level prefab — the DreamBand wrist UI ships with the SDK's hand tracking, and attractions are authored as `Prefabs/A_*.prefab`, not a canonical Level.prefab.

## Creating Attractions & Props (content pipeline)
An attraction is a prefab under `Assets/Content/{GameName}/` with an `AttractionTemplate` (: `LevelTemplate`) on its root; a prop has `PropTemplate`. `ContentProcessor` (SDK-synced, `Assets/DreamPark/Editor/`) watches `Assets/Content/` and automates everything else — do NOT hand-edit Addressables addresses or labels:
- **Naming**: prefixing attraction prefabs `A_` and props `P_` is still the convention, but no longer required for backend discovery (July 2026): the attractions catalog classifies by the stamped ADDRESS NAMESPACE — anything with a `{gameId}/Levels/…` address (i.e. any prefab whose root has AttractionTemplate/LevelTemplate) is an attraction, `{gameId}/Props/…` is a prop. **Exception: an `L_` prefix marks a pre-attraction Legacy Level** (hidden from the Attractions browser, entry-fee-priced only) — never name a new attraction `L_*`. If an attraction is missing from the browser, check that the prefab root has the right template component (that's what produces the address).
- **Runtime address** (assigned automatically): `{gameId}/Levels/{size}/{name}` for attractions, `{gameId}/Props/{category}/{name}` for props, `{gameId}/{TypeFolder}/{name}` for typed assets (Models/Audio/Textures/…). Label = `{gameId}`. Preview PNGs and the content logo still get addresses (`{gameId}/Previews/{name}`, `{gameId}/Logos/{name}`) but their groups are EXCLUDED FROM THE BUILD since July 2026 — those addresses resolve in the editor and nowhere else. **`{category}` in the prop address is `PropTemplate.category`, and that field is FROZEN (Aug 2026)**: it is greyed out with `[ReadOnly]` (the same treatment as `gameId` and `resourceName` beside it) and kept only because it is already baked into every shipped prop's address, and therefore into the `resourceName` the backend joins on. Changing a prop's category does not re-tag it — it RENAMES the asset and orphans its downloads/views/revenue history — so the enum and its member ORDER must never change (Unity serializes enums by integer value, so a reorder silently renames every existing prop) and new props stay on the `Generic` default, `{gameId}/Props/Generic/{name}`. The category a player actually sees is assigned per attraction in the developer portal's Attractions tab, after upload, against the server-owned taxonomy in `DreamPark-Web/lib/attractionCategories.js` (~130 slugs, derived groupings). Do not add a category field to `LevelTemplate`/`AttractionTemplate`.
- **TWO identifier namespaces — never conflate them**, and note the trap: the SAME NAME `resourceName` is used for both. (Settled against core + backend, Aug 2026 — this bullet used to flag a contradiction. There wasn't one: the two names refer to different fields.)
  1. **The Unity COMPONENT field** — `GameArea.resourceName` / `PropTemplate.resourceName` — holds the **ADDRESS form** `{gameId}/Levels/{size}/{name}`, stamped by `ContentProcessor.ResolveAttractionAddress` (`ContentProcessor.cs:616`, assigned at `:580`/`:587`). This is the revenue-attribution key.
  2. **The backend catalog ROW** `resourceName` is normally the asset-path **STEM** (`Content/{GameName}/Attractions/A_X` — from `m_InternalIds`, `Assets/` + extension stripped; DreamPark-Web `lib/addressablesCatalog.js:332` and `:502`). **The catalog can hold the ADDRESS form too**: when the Unity catalog carries no asset paths (dynamic/hashed internal ids), the row is materialized from the address instead and flagged `viaAddress: true` (`addressablesCatalog.js:505-523`).

  The two are NOT reconciled by being equal — they are reconciled AT SPAWN, in core, by `LevelAnchor.ResolveSpawnAddress` (core `Assets/Scripts/LevelAnchor.cs:1234`; that file does not exist in this repo): raw string first — which is exactly what makes a `viaAddress` row loadable — then the string minus the leading `Content/` segment, then a leaf-name match against mounted locators scoped to this game's keys. So `ContentProcessor`'s "matches the backend attractions catalog exactly" comment is only true for `viaAddress` rows: the stamp is right, the comment overstates it. **Treat `resourceName` as an opaque join key, never something you hand to Addressables yourself.**
- **Stamping pass** (`ContentProcessor`, EditPrefabContentsScope + SaveAsPrefabAsset): injects `gameId` into any component with a `gameId` field and stamps the per-attraction ADDRESS onto `GameArea`/`PropTemplate.resourceName` — the revenue-attribution key. It is the address form, NOT the catalog's stem form (see the two-namespaces bullet above); the join happens at spawn, not by the two strings matching. Skips `ThirdPartyLocal/`. Prefabs get edited + saved dirty by this pass; that's expected — commit the churn.
- **Previews**: `Assets/Content/{GameName}/Previews/{prefabName}.png` (or sibling `{name}_preview.png`) — powers Attractions-browser/level-picker tiles and the consumer map. Auto-generated for every attraction/prop; regenerate via `DreamPark → Troubleshooting → Regenerate Level Previews` after visual changes. **They do NOT ship in a bundle (July 2026)**: the uploader pushes each PNG to the backend (`POST /api/content/:id/attractions/preview`) after a successful commit, and every client reads the backend image. Same story for the content logo (`POST /api/content/:id/logo` → `content.logoImageUrl`). The `{gameId}-Previews` / `{gameId}-Logos` Addressables groups still exist but are build-excluded (`SmartBundleGrouper.ExcludeRetiredArtGroupsFromBuild`), so preview churn can no longer abort a Code-only upload — which is why `UploadMode.PreviewsOnly` is gone.
- **Catalog population is automated**: uploading a build publishes the attractions catalog server-side (discovered from the catalog's `m_InternalIds`). There is no manual registration step — if an attraction is missing from the browser, check that the prefab root has the right template component (the prefix is not what's checked; see Naming above).

## Materials & Shaders — use the DreamPark universal shaders for EVERYTHING
DreamPark is mixed reality. Every pixel of virtual geometry has to be clipped by the guest's real room via Meta's Depth API, or it draws on top of their hands, furniture and walls. That integration lives in the shader, so **shader choice is a correctness requirement, not an art preference.**

**The three shaders. Use one of them for every material you ship:**

| Use for | Declared shader name | Source |
|---|---|---|
| Anything lit (props, environment, characters) | `Shader Graphs/DreamPark-UniversalShader` | `Assets/DreamPark/Shaders/DreamPark-UniversalShader.shadergraph` |
| Anything unlit / flat / emissive / UI-in-world | `Shader Graphs/DreamPark-Unlit` | `Assets/DreamPark/Shaders/DreamPark-Unlit.shadergraph` |
| Every ParticleSystem material | `DreamPark/Particles` | `Assets/DreamPark/Shaders/DreamPark-Particles.shader` |

These names are the canonical constants (`MaterialConverter/MaterialPlan.cs:182-184`, `DreamParkShaderNames`); the pre-upload gate matches on them exactly.

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
- **Known SDK shaders that deliberately lack occlusion** and are allowlisted (`MetaOcclusionCheck.IntentionallyExempt`): `Meta/EnvironmentDepth/DepthMask` (produces depth), `Meta/MRUK/MixedReality/InvisibleOccluderCulled` (z-only occluder), `DreamPark/KeepAliveObject`, `Unlit/NormalOverlay` (editor debug viz). `Shader Graphs/LavaScreen` ships without occlusion and WILL be flagged — don't build content on it.
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
  full cost across the whole park. Mark systems that must always run with
  `OptimizedAFIgnore`.

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

## Game Storage (per-user save data: high scores, progress, coins)
Spec: dreampark-core `Docs/Game-Storage-Spec.md`. Sample: `Assets/DreamPark/Samples/GameStorage/storage_high_score.lua.txt`. Every LuaBehaviour gets a `storage` variable auto-bound to its attraction (lazy walk up to GameArea/PropTemplate/LevelTemplate — no ids to pass):

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
- **Backed by `GameStorageAPI`** (SDK-synced, `Scripts/Core/`) → `/app/profile/storage/{contentId}`. Editor testing uses the SDK preview key (same `ProfileAPI.BindToLoggedInUser` pairing flow as inventory); production auth is the headset binding.
- **Hard caps** — this is progress/score storage, NOT a blob store: keys `[A-Za-z0-9_-]` ≤64 chars, string values ≤1 KB, 64 keys & 8 KB per scope, and at most 64 attraction scopes per game (`MaxAttractionScopes` — a scope count, not a byte cap). Oversized writes fail locally with a warning (`set` returns false).
- **Prefer `max`/`min`/`increment` over read-compare-`set`** — ops apply server-side, so the same profile playing on another headset can't be clobbered. Writes are debounced/coalesced automatically; per-frame `increment` is fine.
- **Works unbound**: guest play reads/writes locally and merges into the account if the player pairs mid-session. Nothing persists for a session that never pairs.
- Scripts outside any attraction (e.g. Player.prefab park systems) use `dp.storage.game(gameId)`; the injected `storage` there would warn-once and no-op.
- **Writes are gated on attraction entry** (`ContentGate`, `Scripts/Core/`). A guest in the park hasn't opted into every installed game, so storage flushes and all `ProfileAPI` writes (items/achievements/badges/DreamPoints) are held until the player enters a `GameArea` of that content, then flush in order and stay open for the rest of the identity. Reads and session heartbeats are never gated. Editor sessions auto-open (`ContentGate.AutoOpenInEditor`) since there's often no GameArea to walk into. It's a correctness guard against honest mistakes, NOT a security boundary — the server-side per-guest rate limits are that.

## DO NOT
- Add core-specific code (e.g., backend debug toggles, internal versioning systems).
- Modify Assets/DreamPark/ files without syncing back to dreampark-core.
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
5. Built Addressable prefabs are deployed to DreamPark servers via DreamPark → Content Uploader: fill the panel's **Name** and **Description** fields (the launch window shows them read-only, and an empty Name blocks the upload), clear the pre-upload checks (see Shipping below), hit **Compile & Upload**, then **Start · All** in the launch window. One attraction at a time or a whole park's worth; each upload publishes the attractions catalog automatically and is immediately playable in the iOS app with Experimental Mode on. The launch window's **Upload Scope** picker selects All / Patch / Code-only; the first release for a content ID is locked to All, and Patch is the default choice after that. Smart (dependency-aware) bundling is the default strategy as of Aug 2026 and is what makes the partial modes work — Legacy is deprecated, hidden behind `DreamPark → Troubleshooting → Use Legacy Bundling`, and forces a full re-upload when active.
6. Sign-in (`DreamPark → Sign In`) is passwordless as of July 2026 — email + 6-digit OTP, and `/auth/otp/verify` get-or-creates the account, so there is no separate sign-up path and no password to reset.

## Shipping: the Content Uploader (`DreamPark → Content Uploader`)
Open it from the **`DreamPark` menu in the Unity Editor's top menu bar** — a first-party menu the SDK adds, alongside File/Edit/Assets/GameObject. `DreamPark → Content Uploader` is the first item. The same menu holds `Sign In`, `Optimization → Material Optimizer...`, `Troubleshooting → …` and `Startup Scene...`; every SDK tool referenced in this file lives under it.

The game is not done when it plays in the Editor. It is done when it goes through the uploader clean. Two things are required there and both are on you, not on the human reviewing later.

**1. Set the Name and Description.** The panel's **Name** and **Description** fields are the store-facing metadata for the content package — they're what a guest sees in the Attractions browser and the consumer app. The launch window shows them read-only, and an **empty Name blocks the upload outright**. Write a real description (what the attraction is, what the guest does), not a placeholder. Pick the right content package first if the project has more than one — the dropdown at the top of the panel selects which `Assets/Content/{GameName}/` gets published.

**2. Run the pre-upload verification and fix everything it reports.** `Pre Launch Options → Review Pre-Upload Checks...` opens the unified checks window; the Content Uploader also runs an advisory scan when the panel is engaged and shows per-attraction tile badges. The upload path is gated by `PreUploadChecksGate.Passes(...)` — Blocking findings stop the upload. The six checks (`Assets/DreamPark/Editor/PreUploadChecks/Checks/`):

| Check id | Severity | What it means |
|---|---|---|
| `duplicate-names` | **Blocking** (case-only clashes: Warning) | Two prefabs share a name → collided Addressables address, preview PNG, `PreviewMetadataStore` key and `GameArea.resourceName` (the revenue-attribution key). Always a true positive. |
| `meta-occlusion` | **Blocking** when a material's shader definitely lacks occlusion; **Warning** when undeterminable | See the Materials & Shaders section. Fix action: *Convert selected materials to DreamPark shaders*. |
| `opaque-alpha-clip` | **Blocking** | A DreamPark material set to Surface Type = Opaque with Alpha Clipping off. `meta-occlusion`'s blind spot — the shader has the occlusion wiring, the material discards it. No heuristic and no third state, which is why it blocks. Fix action: *Enable Alpha Clipping*. |
| `sun-light` | **Blocking** (inactive lights: Warning) | A directional light shipped in content lights *every* attraction in the park and no other creator can opt out. |
| `scene-overrides` | Warning | Unapplied prefab overrides in scenes — what you see in the test scene isn't what ships. Apply them to the prefab, or confirm the scene tweak is intentional. |
| `outside-content-folder` | Warning (`Assets/Plugins/`-style protected folders and `ThirdPartyLocal/`: Info) | A prefab depends on an asset outside `Assets/Content/{GameName}/` — it won't be in the bundle. Move it in. |

Rules for agents working the checks list:

- **Fix findings; do not ignore them.** Every finding has a fix action in the popup — use it. `Ignore` writes a permanent, typed-reason entry into `Assets/Content/{contentId}/.preupload-ignores.json` that is git-tracked and visible to the whole team. Only a human decides to ignore a Blocking finding.
- **Warnings and Info still get resolved or explained.** They don't block, but `scene-overrides` and `outside-content-folder` warnings are usually real content bugs that only show up after upload, when they're expensive.
- **A clean project shows no popup at all** — the gate opens the window only when there's at least one non-ignored Blocking or Warning finding. If the popup appears, there is work to do; don't click through it.
- Re-run the checks after fixing. Results are cached per content id and refresh on `ReportChanged`.
- Then **Compile & Upload**, then **Start · All** in the launch window.

## Multiplayer (LAN peer-host + DreamBox relay)
Full spec: dreampark-core `Docs/LAN-PeerHost-Spec.md`. Stack lives in `Assets/DreamPark/Scripts/Features/Net/` (SDK-synced).

**Writing a networked game: read `Assets/DreamPark/Samples/Multiplayer/MULTIPLAYER.md`** — the long-form guide (wire shape, identity without a server, which data structures cannot desync, the send budget, diagnosis). Reusable primitives sit next to it in `mp_kit.lua.txt`: roster/join-order/leader, shared clock, ownership leases, grow-only counters, send budget, replay dedupe.

**Model**: one relay per session, two interchangeable host types — DreamBox kiosk (external) or an elected headset (`PeerRelayServer` in-process). Identical wire protocol; the host headset connects to its own relay via 127.0.0.1, so gameplay/Lua can never tell which host type it's on. The relay is a dumb pipe: rebroadcasts every message verbatim to all OTHER peers (never echoes the sender), ReliableOrdered, 16 KB cap, MaxPeers 16 (soft, tunable).

**Send budget**: `PeerRelayServer` drops anything past **60 msg/s per peer** — silently, with no error to the sender. The kiosk relay does not rate limit at all, so the cap that applies depends on which host you landed on. Two numbers, and only one belongs in your head: **`dp.relay().budget` is what you design against** (always the floor, never moves); `dp.relay().cap` is what today's host enforces (0 = none advertised) and is diagnostic only. Budget for the floor regardless — a kiosk session can hand the room to a peer host mid-play (`Reelection`), and content tuned to kiosk headroom falls over at exactly that moment. The client warns on both thresholds; the host logs actual drops as `[PeerRelay] Rate limit`.

**Enabling**: add `NetSessionArbiter` next to `DreamBoxClient` — presence is the on-switch (DreamBoxClient defers discovery to it). The arbiter owns the ladder: DreamBox beacon → join kiosk (always outranks, preempts peer sessions) → peer beacon → join → 3–5 s silence → self-elect host. Host loss (doff/battery) → coordinator-free re-election in ~1–3 s (sorted hostIds, staggered timers, lowest wins ties). **Nothing migrates on host change** — the relay carries no state, so anything that must survive has to live in the peers. That is achievable and not hard: a value merged with `max()`, rebroadcast at low rate by every peer rather than just the leader, survives arbitrary host churn as long as one player remains (see `MULTIPLAYER.md` §5). Design cosmetics as last-write-wins; design anything you care about as a merge.

**Scoping**: beacons carry `parkId` (sessions never merge across parks; set automatically in core via ParkAnchor.LoadPark, or on the arbiter Inspector) and `ch` — `"sdk"` in SDK builds, `"prod"` in core builds (from the `DREAMPARKCORE` define). SDK test sessions can NEVER collide with production sessions on shared Wi-Fi; set `channelOverride` on the arbiter to cross intentionally. Kiosk/dev-relay beacons are channel-exempt.

**NetId identity — an ADDRESS, not an owner.** Every player's copy of the same object has the SAME id, and that is the transport: the relay has no concept of "a player", so the only way one headset reaches another is that the same object everywhere computes the same id. Sending on it lands on that object's twin in every other session. *Which player* rides in the payload (a `u` field), never in the id — receivers key their tables by that, which is how one shared address carries per-player state. `[NetRegistry] NetId COLLISION` means two objects **inside one running app** claim one id (ambiguous routing); the same id across ten headsets is the point, not a fault.

**You do not author ids** — none of the SDK's sample content sets one. Ids finalize in `Start` (not Awake — the park spawner parents/renames/stamps AFTER Instantiate) and hash with three rules:
1. **Park-spawned attraction content is scope-anchored**: core's `LevelAnchor.Spawn` stamps a `NetScope` on every spawned attraction root with `{levelId}|{objectIndex}|{resourceName}` — all park-doc data, identical on every client. `NetId.ComputeId` walks up, STOPS at the NetScope, and mixes its key. Levels and objects spawn concurrently (`Task.Run` / `Task.WhenAll`), so sibling order ABOVE an attraction root reflects download completion order and differs per device — it never enters the hash. Below the scope, hierarchy comes from the prefab asset — identical everywhere.
2. **Scene roots hash by name only** (no sibling index) — device builds order scene roots differently than the Editor. Keep scene-placed networked props uniquely named at root, or a `[NetRegistry] NetId COLLISION` warning fires.
3. **Deterministic string hashing** (FNV over chars, `(Clone)` stripped) — never `string.GetHashCode()`, which is not stable across Mono (Editor) and IL2CPP (device).
`explicitId` remains a manual override, and it has one real use: an object sitting **outside any `NetScope`** whose path you cannot rely on — in practice a cross-attraction bus on the **player rig**, which nothing stamps a scope onto. (That is why LaserTag pins its Session object.) It is scoped now too — `NetId.ScopeExplicit` mixes the `scopeKey` when there is a `NetScope` above the object and returns the id verbatim when there is not — so an authored id no longer opts out of per-instance discrimination, and two live copies of one attraction no longer collide. Scene-placed props are unchanged byte-for-byte. If you set one, it must be unique within the park.

Mismatch symptom: `[NetRegistry] Event for UNREGISTERED NetId` on the receiver; with Verbose Net Logs, compare `Registered NetId` lines between devices. Note for anyone touching LuaBehaviour: `net_send` must read `netId.Id` at send time, never capture it at Awake (id isn't final until Start).

**Writing Lua multiplayer scripts** (reference sample: `Assets/DreamPark/Samples/Multiplayer/lua_touch_color_switch.lua.txt`):
- `onnet(payload)` at file scope is auto-wired to the sibling NetId's events; `net_send(eventType, payloadJson)` is injected — both require a `NetId` on the SAME GameObject as the LuaBehaviour. `net_send` now resolves `DreamBoxClient.Instance` at SEND time (same reason `netId.Id` is read at send time), so a client that appears late starts working instead of leaving the object permanently single-player. It is therefore always injected when a NetId is present: **`if net_send then` now means "is this object networkable" — a stable property of the prefab — not "did a client exist at boot".** Whether a message actually went out is `dp.relay().connected`.
- Messages sent before the link comes up are **queued** (64 deep, 5 s TTL, drained inside the budget) rather than dropped, so a join handshake survives the second or two discovery takes. That fixes delivery, not timing: if your handshake opens a listen window at `start()`, use `dp.on_connected(fn)` — one-shot, fires immediately if already connected — or the window closes against an empty roster.
- `onnet` receives the FULL wire JSON `{"type":"...","payload":{"netId":N,...}}` — use the global `json_parse(payload)` and read `t.payload.<field>`.
- The relay never echoes your own message back: apply changes locally when sending (optimistic apply).

**Coordinates are park-local, never world.** Each headset has its own XR origin. Parks and attractions are QR-anchored in an arbitrary park-local space that *does* agree across devices. `dp.head().position`, `self.transform.position`, and `Camera.main` are **world**. If you put those numbers on the wire, two players will see an aligned park and offset people — the arena was synced, the poses were not.

- After `onready()`, `LevelAnchor` parents the Player to **itself** (`localPosition` zero). The shared frame is `ParkAnchor` (park document), then `PortalAnchor` (QR), then `LevelAnchor`. `dp.park()` walks to that. Do not use `player.parent` — that is the LevelAnchor, and two levels disagree.
- Send: `dp.head_park()` (pos, fwd) or `dp.to_park(worldPos)` / `dp.to_park_dir(worldFwd)`. Receive: parent the remote visual to `dp.park()` and set `localPosition`, or `dp.from_park(localPos)` back to this headset's world.
- Same rule for hands, projectiles, AI proxies, markers. Hits that are victim-authoritative on a local body can stay local; anything *drawn* on another headset must be park-local.
- Do this in `onready()`, never `awake()` — the rig is not parented yet in `awake()`.
- Full write-up: `MULTIPLAYER.md` §2 "Coordinates are park-local".

- One owner per networked visual property: never mix a MaterialPropertyBlock writer (e.g. TestNetObject) and a `renderer.material` writer (Lua) on the same object — the MPB silently masks material changes.

**Debugging**: tick `Verbose Net Logs` on DreamBoxClient (or set `NetLog.Verbose = true`) → per-beacon discovery, `RECV` previews, relay fan-out, NetId registrations. Always-on warnings and their meanings: `UNREGISTERED NetId` = id mismatch between builds; `NO subscribers` = receiving script missing on that client's object; `Ignoring peer beacon` = channel/park/protocol-version filter (reason included). Healthy session signature: one side `→ Hosting`, other `→ ClientPeer`, host shows `Peer connected … (2/16)` — a host stuck at 1/16 is broadcasting to nobody.

**Platform**: hosting compiles on Android (Quest) + Editor; iOS is client-only in v1. On-device discovery REQUIRES `CHANGE_WIFI_MULTICAST_STATE` (+ `WAKE_LOCK` for the host's Wi-Fi lock) — provided in `Assets/Plugins/Android/AndroidManifest.xml`; loopback bypasses the Wi-Fi broadcast filter, so localhost tests pass without it (deceptively). UDP broadcast is lossy on phone hotspots — the arbiter treats the connection, not beacons, as liveness ground truth; never re-add beacon-silence-kills-connected-session logic.

**Namespace gotcha**: dreampark-core declares `class DreamPark` INSIDE `namespace DreamPark`. In SDK-synced files, `DreamPark.X` inside a `namespace DreamPark` scope resolves to that class in core and fails to compile. Use unqualified sibling references, or `global::DreamPark.X` from global-namespace files.

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

## The `dp` Creator API — what Core does so you don't have to

The rule for anything added here: **does it let a developer delete code that isn't
about their game?** If a creator is writing plumbing to survive our loader, that is
a gap in Core, not a skill issue.

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
```

### Lifecycle hooks

```lua
function onready()      end   -- the world is real: spawned, placed, floors built, enabled
function onzoneenter()  end   -- the player entered THIS script's attraction
function onzoneexit()   end   -- ...and left
```

All three are **sticky**. If the thing already happened before your script existed,
you are told on arrival. Late is normal here — content loads over the air, the park
spawner parents after Awake, and the optimizer parks components mid-load. Missing an
event must be impossible; arriving late must be fine.

`onready()` fires immediately in a hand-authored scene, because the world is already
real there. That is the contract: **your script behaves the same whether it was
placed in a scene or spawned by the park loader.**

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

Known limit, deliberate: a change made to an object **while it is parked** is not seen.
Scripts on a parked object are themselves disabled, so this only bites if you write to a
culled object from somewhere else.
### The Lua surface gate

Content ships over the air; the XLua wrappers it needs are AOT code compiled into the
app. A Unity type nobody registered therefore **cannot be fixed by re-uploading
content** — it needs an app rebuild and a store release. The same call works perfectly
in the Editor, because Mono reflects where IL2CPP cannot.

`LuaSurfaceScanner` catches this, and as of July 2026 something actually runs it:

| Check | Content upload | Player build |
|---|---|---|
| **Sandbox-denied type** — throws at a venue | **blocked**, dialog | **build fails** |
| **Codegen drift** — config has a type with no wrapper in `Gen/` | console warning | **build fails** |
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

**Codegen drift is the check that would have caught Zombiez.** That bug is usually
retold as "NavMesh wasn't in the config." It wasn't — codegen had never *succeeded*
(`DreamParkLuaConfig` duplicated four `GCOptimize` entries XLua's own `SysGenConfig`
already declared, `OptimizeCfg.Add` threw on the duplicate key, `GenAll()` died), so
every type was reflection-only and `NavMesh.SamplePosition` was just the first call
unlucky enough to need a real wrapper. Build integrity, not config coverage.

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
names every type falling back to reflection at runtime. Core does not define it;
production logs stay quiet.

### Rules that still apply

- **`awake()` runs before the object is parented or posed.** Publish globals there;
  do anything positional, hierarchical or cross-object in `start()` or `onready()`.
- **`onenable()` / `ondisable()` are not gameplay events.** The optimizer parks and
  unparks components during load and culling. Core suppresses its own toggles from
  reaching your script, but do not treat them as "the player can see me now".
- **Reads never need `onReady`.** `storage.get` is synchronous against the local
  cache and works for unpaired guests. `storage.onReady` is for refreshing a
  display once the server snapshot lands — it fires immediately when there is
  nothing to wait for.

## The Dream Sequence attraction format

A **Dream Sequence** is one attraction that plays a *sequence of small levels*
back to back in a single physical play space, with a particle transition
covering each swap. It started as `A_DreamSequence` in Super Adventure Land and
is now a standard format. The reference implementation ships in the SDK:
`Assets/Content/Sample/Prefabs/A_DreamSequence.prefab`.

The point of the format: a Micro attraction is 14 × 16 ft — one small room. A
sequence gets a long experience out of it by swapping what is IN the room
instead of asking for a bigger room. The guest never walks further than a few
metres; the world arrives around them.

### The canonical shape

Three parts. Everything else is decoration, and all of it is negotiable.

```
A_DreamSequence                    ← AttractionTemplate + GameArea + MusicArea
│                                    + LuaBehaviour: dreamsequence-controller
├── TransitionEffect               ← FX played between levels (VFX/FX_DreamTransition)
└── LevelParent
    ├── Level1   ACTIVE            ← 1. THE MAIN MENU (splash + start action)
    │   ├── StartPodium/StartButton     dreamsequence-start-button
    │   └── set dressing
    ├── Level2   inactive          ← 2. THE SEQUENCE
    │   ├── Portal                      P_DreamPortal — walk in to advance
    │   └── things to collect / avoid
    ├── Level3   inactive
    │   ├── Portal
    │   └── …
    └── Level4   inactive          ← 3. THE FINAL AREA — no portal
        └── score display               dreamsequence-scorecard on a TMP label
```

1. **A main menu.** The only level active on load, so it is what the preview PNG
   shows and the first thing the guest sees. It carries the branding and one
   definitive start action — a button, a portal, or a visual that responds to
   the guest arriving — and taking it is the intentional act that begins the
   run. Nothing is timed until they do. **It is never empty** (see below).
2. **A sequence of levels.** Each holds whatever the game is about and a way
   out — usually a portal. One is active at a time; the controller owns that.
3. **A final area.** The last level, with **no portal**, so the sequence ends
   there and the guest is left standing in it. Score, a scorecard, a prize, a
   quiet room — whatever the ending is.

Vary it freely: no button (`autoStart`), two levels or twenty, a final area that
loops back (`loopSequence`), objectives instead of portals. The controller cares
about exactly one thing — children of `levelParent` named `Level*`.

### Level1 is a main menu. Empty is a failure mode.

`Level1` is the *only* thing in the attraction a guest sees before they act — it
is also what the preview PNG renders, because it is the only level active on
load. Treat it as a **main menu / splash screen**, not as an empty room with a
button floating in it.

Every Dream Sequence's `Level1` must have, at minimum:

- **A definitive start action.** One unmistakable thing to do. Pick one:
  a **start button** (`dreamsequence-start-button` on a podium), a **portal**
  the guest walks into, or a **responsive visual** that reacts to the guest
  entering the space and begins the run. Whichever it is, it must read as
  "press/enter me" from across the room, with nothing else competing for the
  same read.
- **Branded visuals.** The title, the mascot, the logo — enough that a guest who
  has never heard of this attraction knows what it is called and what it is
  about while they are deciding whether to press the button.
- **Something on screen in every direction the guest is likely to face.** The
  splash is a room, not a wall.

**A `Level1` with nothing in it is a bug, not a placeholder.** It renders an
empty preview tile, it gives the guest no reason to start, and it makes a
finished game look broken. If the real art is not ready, ship the placeholder
treatment below — an unfinished-but-deliberate splash beats a blank one every
time.

#### The inflatable placeholder treatment

The look that has worked across our Dream Sequences, and the default to reach
for when final art is not ready:

- Take the **branded prefabs** — title text, logo, mascot, arch, podium — as
  simple geometry.
- Apply a **transparent purple "inflatable" material**: URP/Lit, Surface Type
  *Transparent*, alpha ≈ 0.55–0.7, a purple base colour, smoothness high enough
  to catch a highlight, and Render Face *Both* so the inside of the balloon
  reads.
- Put an **`EasyBend` component on each element** so it sways and settles
  independently. The motion is what sells it as a stylised bouncy-castle prop
  rather than as missing art.

The result reads as intentional theming — a soft, inflatable lobby — rather than
as a grey-box placeholder, so it is shippable while the final assets land, and
it photographs well in the preview.

### Borrow from the Sample

The fastest way to a new Dream Sequence is to copy `A_DreamSequence.prefab` into
your own content folder and replace its contents. You inherit the controller
wiring, the transition hookup, and the level scaffolding, and you keep the four
Lua scripts — they are generic and have no Sample dependency.

`Assets/Content/Sample/` also has the pieces individually: `P_DreamPortal`
(a prop, so it drops straight into any level), `VFX/FX_DreamTransition`, and the
scripts under `Scripts/dreamsequence-*.lua.txt`.

### The contract

- The controller goes on the attraction ROOT — the GameObject carrying
  AttractionTemplate / GameArea / MusicArea.
- Wire two GameObject injections: **`levelParent`** and **`transitionEffect`**.
  Everything else has a working default.
- Levels are children of `levelParent` **named `Level*`**. That prefix is the
  entire discovery rule. Anything else under `levelParent` is ignored.
- Exactly one level is active at a time. **The controller owns `SetActive`** —
  do not toggle levels yourself.
- **Whatever is active when `start_game()` runs is the splash.** It transitions
  AWAY from it into the next level. It is not "go to level 1"; it is "leave
  here". Put the start button INSIDE that first level so it disappears with it.
- A level's contents belong INSIDE its `Level*` object. Anything parented beside
  the levels stays on screen for the whole run — right for set dressing, wrong
  for anything belonging to one level.
- The attraction's floor is rebuilt on every level change (levels may cut holes
  in it), so leave `AttractionTemplate.generateFloor` on.
- One `MusicArea` for the whole attraction; each level optionally crossfades it
  to its own clip with `dreamsequence-level-music.lua.txt`. The guest hears a
  continuous score that changes with the world rather than a cut per level.

### Reaching the controller from your own scripts

Everything is public API on the attraction's script scope:

```lua
local ds = dp.attraction(self.gameObject)
```

```lua
ds.start_game()            -- leave the splash, begin the run
ds.advance()               -- this level is done; transition to the next
ds.finish()                -- end the run here and save the score
ds.is_running()            -- bool
ds.is_transitioning()      -- bool
ds.get_level_index()       -- 0 before the first level lands
ds.get_level_count()
ds.get_level_name()
ds.get_level()             -- the level GameObject

ds.register_objective(go)  -- level ends when every registered objective is done
ds.complete_objective(go)
ds.get_objectives_remaining()

ds.add_score(10)           -- persisted as storage.max("high_score", …) on finish
ds.set_score(n)
ds.get_score() / ds.get_high_score()

ds.on_level_start(function(index, go)    end)
ds.on_level_complete(function(index, go) end)
ds.on_sequence_complete(function(score)  end)
```

Register handlers in `onready()` — it is sticky and always arrives. Handlers
added after an event has passed are NOT replayed.

`next_game()` / `end_game()` are aliases of `advance()`, kept so Dream Sequences
authored against the original Super Adventure Land scripts keep working.

**`dp.attraction()` does not work from inside a prop.** It resolves to the
nearest `GameArea`, and `PropTemplate` gives every prop its own — so a prop
asking for "my attraction" is handed back *itself*, finds no `advance()`, and
does nothing at all. Walk up from `self.transform.parent` taking the first scope
that exposes what you need; `dp.scope()` boots each script it touches, so the
walk does not depend on Awake ordering. `dreamsequence-portal.lua.txt` has the
helper, `find_controller()`. This fails silently — the prop simply never
responds — so reach for the helper in anything that lives on a prop.

### Two ways a level ends — pick one, or mix

**Tell it.** The simplest rule and the one the sample uses. `P_DreamPortal` is a
trigger volume at the end of the level; the part that matters is:

```lua
function ontriggerenter(other)
    if dp.is_player(other) then find_controller().advance() end
end
```

Once used, the portal stops its emitters and disables its collider — it goes
quiet in place. It does not shrink away or deactivate itself: a prop sinking
into the floor reads as a glitch, and deactivating the GameObject kills the
particle tail mid-air.

The portal is a **looping** emitter, unlike the transition effect the controller
starts and stops. Particles are born on the rim of a circle and pulled inward,
so it is continuously swallowing something. That motion is doing a job — a
static disc reads as scenery and the guest walks past it.

**Let it count.** For levels that end when N things are done rather than on one
event, register objectives in `onready()` and complete them as they happen. The
controller advances when the last one lands. Objectives are cleared on every
level change, so a level only ever waits on its own.

This is the generic replacement for Super Adventure Land's "count the remaining
`Coin` components" rule — nothing in the controller knows what your game is
about, and a third-party dev never has to match our component types.

### The final area, and score

The last level has no portal, so `advance()` from the level before it lands
there and the sequence ends: the run is saved and `on_sequence_complete` fires.

`dreamsequence-scorecard.lua.txt` drives a TextMeshPro label from the score the
guest can already see. Put it on the object carrying the TMP (it finds
`TextMeshPro` or `TextMeshProUGUI` on itself, or point `scoreText` at one).

It **mirrors, it never counts.** The number floating at the DreamBand position
belongs to `lua_example_manager.lua.txt` on Player.prefab, which publishes
itself as the global `example_manager` and exposes `get_points()`. Two counters
that both try to be "the score" agree right up until one misses an event, and
then you are debugging which one is lying — so there is one owner and everything
else reads it.

Resolution order: `scoreSource` → `managerGlobal` (default `example_manager`) →
the containing Dream Sequence's `get_score()` → `storage.get("high_score")`.
The manager lives on Player.prefab, a DIFFERENT addressable from the attraction,
so it binds with `dp.on_global` — "not there yet" is an ordinary state on
arrival, not an error.

The script absorbs a vocabulary difference: the sample's point collector says
`get_points()`, the controller says `get_score()`. It asks for whichever the
scope actually has.

**The run is persisted once, when the sequence ends:**

```lua
storage.max("high_score", score)   -- set-if-greater, race-safe
storage.increment("runs_played", 1)
```

`max` rather than read-compare-set, so a better score this profile wrote on
another headset can never be clobbered — the ops apply server-side. Writes queue
offline and flush when the guest scans in. Adventure Log treats keys containing
score/best/record/streak as score-like, so this emits a timeline row only when
the guest actually beats their record — a personal best is worth a notification,
an ordinary run is not.

Note that `ds.add_score()` and the `example_manager` points are separate numbers.
If the run's points should become the persisted high score, feed the controller
from wherever the points are counted.

### Authoring a new one

1. Copy `A_DreamSequence.prefab` into your content folder, or start a fresh
   attraction and put `dreamsequence-controller.lua.txt` on its root.
2. Wire `levelParent` and `transitionEffect`.
3. Build `Level1…LevelN` under `levelParent`. Level 1 holds the start button;
   the last one holds the ending and has no portal.
4. Give every middle level a way out: drop in a `P_DreamPortal`, or call
   `ds.advance()` / register objectives from your own script.
5. Leave `autoStart` off if a button starts the run; turn it on for an
   attraction that begins the moment the guest walks in.
6. Set `loopSequence` if the run should wrap back round instead of ending.

Controller knobs: `transitionDelay` (how long the transition covers the swap —
the level changes at the END of it), `completionSfx`, `autoStart`,
`loopSequence`, `globalName` (publishes the scope as a Lua global for scripts
outside the attraction; usually leave empty and use `dp.attraction`).

### Things that will bite you

These all fail silently. Every one of them cost real debugging time.

- **`start_game()` leaves the splash, it does not jump to a level.** Put the
  start button beside the levels instead of inside the first one and the first
  level is already on screen when the guest presses it: the transition fires and
  nothing appears to change. A particle burst, and the same room.
- **Restart a looping effect, never resume it.** `Stop(StopEmitting)` leaves a
  looping system reporting `isPlaying == true` until its last particles expire,
  and `Play()` on a system that thinks it is playing is a no-op. The first
  transition plays and every one after it silently emits nothing. Stop with
  `StopEmittingAndClear` before `Play()` — which also means an authored
  **Play On Awake** cannot leave the effect running behind the controller's back.
- **`dp.attraction()` from inside a prop returns the prop.** See above. The
  portal, the scorecard, and anything else on a `P_*` prefab must walk up.
- **`transitionDelay` is the emit window, not a fade.** The effect plays for the
  whole window and the level swaps at the END. A couple of seconds of sustained
  particles is the guest's cue that the world is about to change and their
  chance to get ready; a single burst is over before they react.
- **The transition only advances while the attraction is live.** It is driven by
  `update()`, so calling the API on a park-suspended copy — the scene instance
  the Park Simulator disabled, say — leaves the transition hanging forever.
- **`ontriggerenter` also fires for overlaps that already existed** when the
  object came up. That is what the portal's `armDelay` is for: a moment for the
  guest to step off the previous level's exit before the next one can trigger.
- **`onenable()` is not normally a gameplay event** — but it IS the right hook in
  `dreamsequence-level-music.lua.txt`, because the controller drives level
  activation with `SetActive`, so this level going active *is* the event. The
  swap is idempotent, so a spurious load-time enable is harmless.
- **The transition VFX is SDK-native art.** Super Adventure Land's original used
  a Super Confetti FX prefab, which is licensed and cannot ship in the SDK.
  `FX_DreamTransition` reproduces the role with textures generated for this repo
  and the `DreamPark/Particles` shader. Don't swap in a marketplace prefab in
  shipped SDK content.
