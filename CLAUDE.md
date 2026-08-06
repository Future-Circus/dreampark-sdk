# DreamPark SDK — Content Creator Template

## What This Is
SDK template for third-party game developers. A complete Unity 6 project cloned from dreampark-core. Creators fork this repo and place their game content in Assets/Content/{GameName}/.

## Template Structure
```
Assets/
├── DreamPark/         ← SDK source (~540 C# files, must match dreampark-core exactly)
├── Content/
│   └── YOUR_GAME_HERE/        ← Renamed via the in-editor setup popup (ContentIdSetupPopup, auto-opened by PlaceholderContentDetector; letters+digits, starts with a letter). Folder name = content ID. Multiple content folders may coexist — each is an independent package; the Content Uploader's dropdown picks which to publish.
│       ├── 1. Scenes/Template.unity
│       ├── 2. Features/1. Player/Player.prefab
│       ├── Prefabs/            ← A_*.prefab attractions (AttractionTemplate root), P_*.prefab props (PropTemplate root)
│       ├── Previews/           ← {prefabName}.png tile art
│       ├── Scripts/            ← Game-specific C# (minimal — prefer Lua)
│       └── ThirdParty/         ← Only used assets (git-tracked, shipped in builds)
└── ThirdPartyLocal/            ← Imported packages land here (gitignored, not in builds)
```

## SDK Sync
The ~540 files in Assets/DreamPark/ must match dreampark-core exactly. `#if DREAMPARKCORE` blocks (core-only code) are conditionally compiled out of this SDK distribution — the source remains visible but doesn't compile in SDK builds. Use conditional compilation to mark what's core-specific. Anything entirely core-only (no SDK reason to exist at all, e.g. consumer-app pairing flows, internal admin tooling) should live in dreampark-core's own `Assets/Scripts/` outside `Assets/DreamPark/` rather than as an empty SDK file.

## The Three Primitives
- **Player.prefab** (`2. Features/1. Player/`): Root player object (persists across attractions). Global systems (audio, score, park state) live here as LuaBehaviours.
- **AttractionTemplate** (: LevelTemplate): root component of an attraction prefab — a self-contained experience (arcade game, boss battle, challenge course). LevelTemplate's `[RequireComponent]`s auto-add GameArea (presence detection — drives PlayerRig show/hide AND playtime-based revenue attribution) and MusicArea. Defines the physical space (size/customSize, floor generation, calibration).
- **PropTemplate**: root component of a prop prefab — the individual interactive elements that make up an attraction (coin, hammer, enemy). Auto-adds its own GameArea at priority -1 (self-suppressed when nested inside an attraction). Props are also placeable standalone in parks.

There is no creator-facing DreamBand or Level prefab — the DreamBand wrist UI ships with the SDK's hand tracking, and attractions are authored as `Prefabs/A_*.prefab`, not a canonical Level.prefab.

## Creating Attractions & Props (content pipeline)
An attraction is a prefab under `Assets/Content/{GameName}/` with an `AttractionTemplate` (: `LevelTemplate`) on its root; a prop has `PropTemplate`. `ContentProcessor` (SDK-synced, `Assets/DreamPark/Editor/`) watches `Assets/Content/` and automates everything else — do NOT hand-edit Addressables addresses or labels:
- **Naming**: prefixing attraction prefabs `A_` and props `P_` is still the convention, but no longer required for backend discovery (July 2026): the attractions catalog classifies by the stamped ADDRESS NAMESPACE — anything with a `{gameId}/Levels/…` address (i.e. any prefab whose root has AttractionTemplate/LevelTemplate) is an attraction, `{gameId}/Props/…` is a prop. **Exception: an `L_` prefix marks a pre-attraction Legacy Level** (hidden from the Attractions browser, entry-fee-priced only) — never name a new attraction `L_*`. If an attraction is missing from the browser, check that the prefab root has the right template component (that's what produces the address).
- **Runtime address** (assigned automatically): `{gameId}/Levels/{size}/{name}` for attractions, `{gameId}/Props/{category}/{name}` for props, `{gameId}/{TypeFolder}/{name}` for typed assets (Models/Audio/Textures/…). Label = `{gameId}`. Preview PNGs and the content logo still get addresses (`{gameId}/Previews/{name}`, `{gameId}/Logos/{name}`) but their groups are EXCLUDED FROM THE BUILD since July 2026 — those addresses resolve in the editor and nowhere else.
- **TWO identifier namespaces — never conflate them**: the runtime Addressables ADDRESS above vs the backend catalog `resourceName`, which is the internal ASSET-PATH stem (`Content/{GameName}/Attractions/A_X` — asset path with `Assets/` + extension stripped). They only coincided for legacy folder layouts. Core's `LevelAnchor.ResolveSpawnAddress` translates stem→address at spawn; treat `resourceName` as an opaque join key, never a loadable address.
- **Stamping pass** (`ContentProcessor`, EditPrefabContentsScope + SaveAsPrefabAsset): injects `gameId` into any component with a `gameId` field and stamps the per-attraction address onto `GameArea`/`PropTemplate.resourceName` — this is the revenue-attribution key, so it must match what the backend catalog derives. Skips `ThirdPartyLocal/`. Prefabs get edited + saved dirty by this pass; that's expected — commit the churn.
- **Previews**: `Assets/Content/{GameName}/Previews/{prefabName}.png` (or sibling `{name}_preview.png`) — powers Attractions-browser/level-picker tiles and the consumer map. Auto-generated for every attraction/prop; regenerate via `DreamPark → Troubleshooting → Regenerate Level Previews` after visual changes. **They do NOT ship in a bundle (July 2026)**: the uploader pushes each PNG to the backend (`POST /api/content/:id/attractions/preview`) after a successful commit, and every client reads the backend image. Same story for the content logo (`POST /api/content/:id/logo` → `content.logoImageUrl`). The `{gameId}-Previews` / `{gameId}-Logos` Addressables groups still exist but are build-excluded (`SmartBundleGrouper.ExcludeRetiredArtGroupsFromBuild`), so preview churn can no longer abort a Code-only upload — which is why `UploadMode.PreviewsOnly` is gone.
- **Catalog population is automated**: uploading a build publishes the attractions catalog server-side (discovered from the catalog's `m_InternalIds`). There is no manual registration step — if an attraction is missing from the browser, check the `A_`/`P_` prefix and that the prefab root has the right template component.

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
- **Hard caps** — this is progress/score storage, NOT a blob store: keys `[A-Za-z0-9_-]` ≤64 chars, 64 keys & 8 KB per scope, 64 KB per game. Oversized writes fail locally with a warning (`set` returns false).
- **Prefer `max`/`min`/`increment` over read-compare-`set`** — ops apply server-side, so the same profile playing on another headset can't be clobbered. Writes are debounced/coalesced automatically; per-frame `increment` is fine.
- **Works unbound**: guest play reads/writes locally and merges into the account if the player pairs mid-session. Nothing persists for a session that never pairs.
- Scripts outside any attraction (e.g. Player.prefab park systems) use `dp.storage.game(gameId)`; the injected `storage` there would warn-once and no-op.
- **Writes are gated on attraction entry** (`ContentGate`, `Scripts/Core/`). A guest in the park hasn't opted into every installed game, so storage flushes and all `ProfileAPI` writes (items/achievements/badges/DreamPoints) are held until the player enters a `GameArea` of that content, then flush in order and stay open for the rest of the identity. Reads and session heartbeats are never gated. Editor sessions auto-open (`ContentGate.AutoOpenInEditor`) since there's often no GameArea to walk into. It's a correctness guard against honest mistakes, NOT a security boundary — the server-side per-guest rate limits are that.

## DO NOT
- Add core-specific code (e.g., backend debug toggles, internal versioning systems).
- Modify Assets/DreamPark/ files without syncing back to dreampark-core.
- Use keyboard controls, virtual cameras, or EasyEvent chains as primary interaction pattern.

## Workflow
1. Creator clones dreampark-sdk as a new project.
2. On first editor open, the setup popup renames YOUR_GAME_HERE to the game name (e.g., CoinCollector). (There is no new-park.sh — the popup is the rename mechanism.)
3. Creator adds game content, Lua scripts, and prefabs to Assets/Content/{GameName}/.
4. All gameplay logic is Lua-first via LuaBehaviour.
5. Built Addressable prefabs are deployed to DreamPark servers via DreamPark → Content Uploader — one attraction at a time or a whole park's worth; each upload publishes the attractions catalog automatically and is immediately playable in the iOS app with Experimental Mode on.

## Multiplayer (LAN peer-host + DreamBox relay)
Full spec: dreampark-core `Docs/LAN-PeerHost-Spec.md`. Stack lives in `Assets/DreamPark/Scripts/Features/Net/` (SDK-synced).

**Model**: one relay per session, two interchangeable host types — DreamBox kiosk (external) or an elected headset (`PeerRelayServer` in-process). Identical wire protocol; the host headset connects to its own relay via 127.0.0.1, so gameplay/Lua can never tell which host type it's on. The relay is a dumb pipe: rebroadcasts every message verbatim to all OTHER peers (never echoes the sender), ReliableOrdered, 16 KB cap, 60 msg/s per-peer rate cap, MaxPeers 16 (soft, tunable).

**Enabling**: add `NetSessionArbiter` next to `DreamBoxClient` — presence is the on-switch (DreamBoxClient defers discovery to it). The arbiter owns the ladder: DreamBox beacon → join kiosk (always outranks, preempts peer sessions) → peer beacon → join → 3–5 s silence → self-elect host. Host loss (doff/battery) → coordinator-free re-election in ~1–3 s (sorted hostIds, staggered timers, lowest wins ties). Session state is ephemeral by design — nothing migrates on host change; design content as last-write-wins cosmetics.

**Scoping**: beacons carry `parkId` (sessions never merge across parks; set automatically in core via ParkAnchor.LoadPark, or on the arbiter Inspector) and `ch` — `"sdk"` in SDK builds, `"prod"` in core builds (from the `DREAMPARKCORE` define). SDK test sessions can NEVER collide with production sessions on shared Wi-Fi; set `channelOverride` on the arbiter to cross intentionally. Kiosk/dev-relay beacons are channel-exempt.

**NetId identity (deterministic by design — no explicitId needed)**: ids finalize in `Start` (not Awake — the park spawner parents/renames/stamps AFTER Instantiate) and hash with three rules:
1. **Park-spawned attraction content is scope-anchored**: core's `LevelAnchor.Spawn` stamps a `NetScope` on every spawned attraction root with `{levelId}|{objectIndex}|{resourceName}` — all park-doc data, identical on every client. `NetId.ComputeId` walks up, STOPS at the NetScope, and mixes its key. Levels and objects spawn concurrently (`Task.Run` / `Task.WhenAll`), so sibling order ABOVE an attraction root reflects download completion order and differs per device — it never enters the hash. Below the scope, hierarchy comes from the prefab asset — identical everywhere.
2. **Scene roots hash by name only** (no sibling index) — device builds order scene roots differently than the Editor. Keep scene-placed networked props uniquely named at root, or a `[NetRegistry] NetId COLLISION` warning fires.
3. **Deterministic string hashing** (FNV over chars, `(Clone)` stripped) — never `string.GetHashCode()`, which is not stable across Mono (Editor) and IL2CPP (device).
`explicitId` still exists as a manual override, but generated ids are stable without it. Mismatch symptom: `[NetRegistry] Event for UNREGISTERED NetId` on the receiver; with Verbose Net Logs, compare `Registered NetId` lines between devices. Note for anyone touching LuaBehaviour: `net_send` must read `netId.Id` at send time, never capture it at Awake (id isn't final until Start).

**Writing Lua multiplayer scripts** (reference sample: `Assets/DreamPark/Samples/Multiplayer/lua_touch_color_switch.lua.txt`):
- `onnet(payload)` at file scope is auto-wired to the sibling NetId's events; `net_send(eventType, payloadJson)` is injected — both require a `NetId` on the SAME GameObject as the LuaBehaviour, and net_send additionally requires DreamBoxClient to exist at Awake. Always nil-guard: `if net_send then net_send(...) end` (solo play must work).
- `onnet` receives the FULL wire JSON `{"type":"...","payload":{"netId":N,...}}` — use the global `json_parse(payload)` and read `t.payload.<field>`.
- The relay never echoes your own message back: apply changes locally when sending (optimistic apply).
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
dp.head()                    -- the head/camera Transform, or nil
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
    ├── Level1   ACTIVE            ← 1. THE START TRIGGER (a "splash")
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

1. **A start trigger.** The only level active on load. The guest is standing in
   front of it when they arrive, and pressing the button is the intentional act
   that begins the run. Nothing is timed until they do.
2. **A sequence of levels.** Each holds whatever the game is about and a way
   out — usually a portal. One is active at a time; the controller owns that.
3. **A final area.** The last level, with **no portal**, so the sequence ends
   there and the guest is left standing in it. Score, a scorecard, a prize, a
   quiet room — whatever the ending is.

Vary it freely: no button (`autoStart`), two levels or twenty, a final area that
loops back (`loopSequence`), objectives instead of portals. The controller cares
about exactly one thing — children of `levelParent` named `Level*`.

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
