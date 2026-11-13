# Lua at runtime

What is true when your hooks run in a park, what the optimizer does to your objects, and how Lua reaches Unity on device.
`§ Name` points to a section of these docs; the table at the end of `Assets/DreamPark/Docs/CLAUDE.md` maps every section to its file.

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

## Rules that still apply

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

## What the optimizer guarantees (July 2026)

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

## The Lua surface gate

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

## XLua `Gen/` is SDK-owned — updates replace it, never merge it (Sept 2026)

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

## The Lua pump

- **The Lua pump never stops.** One title's Lua error no longer disables `dp.on_tap`/`on_touch`/peer/`shared_state` dispatch for every title; it logs `[DreamParkLuaPump] <name> threw (pump keeps running)` at most once per 10 s. Fix the error it names.

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

Start at **Lua Lifecycle Under Park Loading** (below) and **The `dp` Creator API**
(`LUA_API.md`).
