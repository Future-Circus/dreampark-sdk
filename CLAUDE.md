# DreamPark game agent guide

DreamPark is a mixed-reality theme park. Guests walk real, scanned rooms wearing a Quest headset (passthrough)
or holding a phone (AR), and each room hosts an **attraction** from a game (a **title**). A title is a Unity 6
project built on the DreamPark SDK: content ships over the air as Addressables, and all gameplay is Lua.

**How to work** (Editor setup, play sessions, captures, hand-off): `Tools/agent/PLAYBOOK.md`.
**Details:** the reference docs in `Assets/DreamPark/Docs/` (table at the end). Read the one for your task before you
start it. Where this project's own notes (below the import block in the root `CLAUDE.md`) disagree with this guide,
the project notes win.

## Project layout

```
Assets/
├── DreamPark/                SDK-owned: replaced on every SDK update. Customize through your content folder,
│   ├── Docs/                 prefab-instance overrides and Lua. `#if DREAMPARKCORE` blocks are app-only code.
│   └── Samples/              Worked Lua samples (GameStorage, ProfileAPI, Phone, Multiplayer, PropStreaming …)
└── Content/{GameName}/       This title. Folder name = content ID.
    ├── Prefabs/              Player.prefab (headset rig), optional Phone Player.prefab,
    │                         A_*.prefab attractions (AttractionTemplate root), P_*.prefab props (PropTemplate root),
    │                         Game Container.prefab (the Game Manager)
    ├── DreamSequence/        Sequence Start / Overlay / Game Over levels (creator-owned)
    ├── Scripts/              *.lua.txt (all gameplay) + minimal C# interop shims
    ├── Previews/             {prefab}.png tiles, generated
    ├── ThirdParty/           assets the game uses (shipped)
    ├── ThirdPartyLocal/      imported packs (excluded from builds)
    └── .dreampark-*.json, .badges.json, .items.json, .preupload-ignores.json   (authoring data: commit them)
Tools/Builders/*.cs           Editor builders run through the Unity CLI (no domain reload)
*.md at the project root      SDK-managed copies of the docs (header says "Managed"): edit the SDK, not the copy
```

Drive the Editor with the `unity` CLI (`unity command --project-path . <cmd>`; pass `--timeout` for long commands,
the default is 30 s). Builders, screenshots, play sessions and modal dialogs: `Assets/DreamPark/Docs/UNITY_CLI.md`.

## The model

- **Player.prefab**: the guest's body (`PlayerRig`: Head/Body/Feet/Hand trackers). It persists across attractions.
  Per-guest systems (hand-held tools, damage screens, per-player audio) live here, and so does anything that must
  work when attractions are placed loose. Keep exactly one headset rig.
- **Game Manager** (`Prefabs/Game Container.prefab`, script `Scripts/game-container.lua.txt`): title-wide rules,
  progression and shared state. Networked, never distance-culled, not a spatial parent. It exists only when the title
  runs as a package, so `dp.game_manager()` can be nil: nil-check every call.
- **Attraction** = a prefab whose root has `AttractionTemplate`: a self-contained experience that defines its physical
  space. It auto-adds `GameArea` (presence: drives the rig and revenue attribution) and `MusicArea`.
- **Prop** = a prefab whose root has `PropTemplate`: an interactive element (coin, hammer, enemy), placeable inside an
  attraction or standalone.
- **Phones** join as an AR window with touch input. A phone takes part in gameplay only while it wears the title's
  Phone Player; otherwise it is a spectator. Title-wide systems belong on the Game Manager, which phones also get.

**The content pipeline is automatic.** `ContentProcessor` assigns Addressables addresses
(`{gameId}/Levels/{size}/{name}`, `{gameId}/Props/{category}/{name}`), labels, `gameId` and the revenue key
`resourceName`; commit the prefab churn it causes. Treat `resourceName` as an opaque join key. Keep
`PropTemplate.category` as it is (it is baked into every shipped address; new props use `Generic`). Name attractions
`A_*` and props `P_*`; `L_*` marks legacy levels only. Previews are generated per prefab and uploaded separately.
Details: `Assets/DreamPark/Docs/ATTRACTIONS.md`.

**Author the stage and one of every actor; instantiate the crowd.** The prefab's preview is all a guest sees, so it
holds, enabled and honestly spaced, the stage, one exemplar of every actor posed mid-action, and the fixed content the
game counts. Whatever comes and goes (waves, projectiles, traffic, pickups, every particle effect) is instantiated and
destroyed when done; hidden copies waiting to be used are a pool. Regenerate previews and look at them.

**One attraction, one size: 12 × 18 ft.** Author every attraction as a single layout for a 12 × 18 ft room: the
prefab's Base, with no layout frames. The shrink/grow bake (`AttractionPackingBaker`) covers the rooms around that
size automatically: it fits the same layout into somewhat smaller rooms and spreads it out in bigger ones, within its
baked range. Don't author frames, a Minimum frame, or hidden content for other room sizes. Rooms larger than the grow
range play the 12 × 18 layout at its largest; rooms smaller than the shrink range don't get the attraction. Separate
attractions still differ in content, never in size. The frame system stays in the SDK so older content keeps loading,
but new and reworked attractions don't use it.

Optional **wall layers** add extras for a side seated on a real wall; "no walls" is the baseline. **Entries & Exits**
mark the footprint edge; **Routes** (`dp.route`, `dp.tour`) walk between attractions through real doorways.

**Packages** turn whatever floor an operator scanned into a playable title. Author them in
`DreamPark → Content Uploader → Packages`; a title needs at least one playable package:

| | Arena | Sequence | Adventure |
|---|---|---|---|
| Space | any scanned rect: its size bucket's first entry | one 12 × 18 ft room, levels streamed one at a time between Start and Game Over | the whole park: Start → stages → End along a route |
| Authoring | `.dreampark-arena.json` | `.dreampark-dream-sequence.json` | `.dreampark-sequence.json` (historical name) |

Level controls call the Game Manager's `load_level` (synced); `dp.load_level` alone is local. The compiled
`DreamPark Package Manifest.asset` and `Dream Sequence Package.asset` are build output: rebuild them, don't edit them.
Details: `PACKAGES.md` (creator guide) and `Assets/DreamPark/Docs/PACKAGE_RULES.md`.

## Lua

All gameplay is Lua (`*.lua.txt`) on a `LuaBehaviour`. C# in `Assets/Content/` is interop shims only. EasyEvent
(`Easy*`) components are deprecated for new work: author new logic in Lua, and leave existing `Easy*` usage as it is
unless a human asks for a change.

**Lifecycle under park loading.** Park content is spawned by the loader and parked by the optimizer, so:

- `awake()` runs before the object is parented or posed: use it only to publish globals and wire your own tables.
  Do positional, hierarchical and cross-object work in `start()` or `onready()`.
- `start()` always arrives. `onready()` fires once the world is real (spawned, placed, floors built, enabled).
- `onzoneenter()` / `onzoneexit()` are the player entering and leaving this script's attraction. They are sticky (late
  arrivals are told the current state), so use them instead of polling `GameArea.isPlaying`. Start audio, VO and
  greetings in `onzoneenter()`.
- `onenable()` / `ondisable()` are load and culling artifacts, not gameplay events.
- `ontriggerenter(other)` also fires for overlaps that already existed when the object came up.
- `onlayout(frame_id, scale, footprint)`, `onwalls(walls)`, `onpause()` / `onresume()` (game-master pause; base timers
  on `dp.game_time()`), `ontap(t)` / `ontouch(t)` on phones.
- A parked component reads `.enabled == false`. Read and write the intended state with `dp.enabled(c)` /
  `dp.set_enabled(c, on)`. Changes made while an object is live survive culling.
- `dp.attraction()` inside a prop answers the prop itself. Walk up from `self.transform.parent` with `dp.scope()`,
  or use `dp.game_manager()` for package navigation.

**Core `dp` API** (full list with options: `Assets/DreamPark/Docs/LUA_API.md`):

```lua
dp.is_player(other)                  -- is this collider the player? (rig-aware)
dp.player() / dp.head()              -- rig GameObject / head Transform; nil when there is no rig: nil-check
dp.scope(go) / dp.attraction(go) / dp.attraction_root(go) / dp.game_id(go)
dp.on_global(name, fn)               -- fn(value) now if bound, else when it appears (instead of Find* scans)
dp.park() / dp.to_park(pos) / dp.from_park(pos)       -- park-local coordinates
dp.game_manager() / dp.package() / dp.load_level(slot) / dp.next_level()   -- packages (PACKAGE_RULES.md)
dp.device() / dp.is_phone() / dp.is_headset() / dp.participating()         -- devices (PLAYERS.md)
dp.on_tap(fn) / dp.on_touch / dp.on_drag / dp.on_pinch                     -- phone gestures
dp.me() / dp.peers() / dp.leader() / dp.is_leader() / dp.runs_world()      -- multiplayer
dp.shared_state(name) / dp.net_body(go) / dp.clock() / dp.random(key[, a, b])
dp.hold(prop, side) / dp.muzzle(prop) / dp.release_hold(prop)              -- hand-held props (HANDHELD.md)
dp.spawn_prop(name, opts) / dp.preload_prop(name) / dp.despawn_prop(h)     -- stream a prop in by name
dp.time.now() / dp.time.hour()       -- the device's wall clock (cosmetic, not synced)
dp.light.ambient() / dp.light.direction()   -- the real room's light (per device)
dp.ports() / dp.neighbors() / dp.route(a, b) / dp.tour(stops)               -- routes (ROUTES.md)
dp.optimizer_ignore(go)              -- keep a manager out of distance culling (prefer `-- @optimizer-ignore`)
storage.max('high_score', s) / storage.increment('coins', 5) / storage.get(k, default)   -- per-user saves
dp.profile.awardBadge(id) / dp.profile.awardItem(id, n) / dp.profile.awardUniqueItem(id, meta, cb)
dp.results.report{go, round, kind, entrants}   -- end of every round: session results + leaderboards (RESULTS.md)
dp.lobby.left() / dp.lobby.onEnd(fn) / dp.flow.* / dp.board.*   -- ways to play: runner directors (RUNNERS.md)
```

Newer calls are missing on older apps: feature-test them (`if dp.spawn_prop then … end`, `if dp.enabled then`).

**Script annotations** (comments in a Lua script, or a name tag): `-- @optimizer-ignore` (never distance-culled),
`-- @packing-ignore` / `[nopack]` in an object name (left out of the shrink/grow bake), `-- @gapfiller-links`
(NavMesh links in from the gap ground), `-- @editor-only` (authoring tools that call `CS.UnityEditor`),
`-- @item id "Title"` (an item id built at runtime), `-- @var name type` (an injected variable).

**The Lua surface.** Lua reaches Unity through AOT XLua wrappers compiled into the app, so a type without a wrapper
works in the Editor and fails on device, and only an app release fixes it. Use the types the SDK already wraps
(including `ParticleSystem`), use fully-qualified names, and treat the Lua surface gate's findings on upload and
player builds as real. Details: `Assets/DreamPark/Docs/LUA_RUNTIME.md`.

**Saves** (`storage.*`): progress and scores, not blobs (keys ≤ 64 chars, strings ≤ 1 KB, 64 keys / 8 KB per scope).
Prefer `max` / `min` / `increment` (applied server-side). Scripts outside an attraction use `dp.storage.game(gameId)`.
Writes are held until the player enters one of the title's attractions. Details:
`Assets/DreamPark/Docs/PLAYER_DATA.md`.

## Multiplayer

The platform does the netcode: one relay per session rebroadcasts to every other peer (16 KB per message, 60 msg/s
per peer). The only networking component you add is one `NetId` on an attraction's manager. Read
`Assets/DreamPark/Samples/Multiplayer/MULTIPLAYER.md` before writing netcode. The nine rules:

1. Encounters (bosses, enemies, rounds, timers): `dp.shared_state(name)` + `dp.is_leader()`. Players send requests
   (`s.request`); the leader applies them.
2. Everything gameplay-visible is shared: `dp.random(key)`, leader choices, `dp.clock()`. Gameplay randomness never
   comes from `math.random`, and `math.randomseed` is off-limits (the VM is shared).
3. Late join is automatic with `shared_state`: gate every start on `s.synced()`; effects never replay.
4. Physics props: `dp.net_body(go)`. Only the owner writes a networked prop's transform.
5. Positions: typed `net_send(kind, table)`, attraction-relative; people come from `dp.peers()`.
6. Ids are scoped and title-unique automatically; `explicitId` belongs only on a `Player.prefab` bus.
7. Audio, VO and greetings start in `onzoneenter()`.
8. One send budget per headset: design for `dp.relay().budget` (60, presence included).
9. Test with simulated peers before a headset (`dreampark_peers_scenario`), then one two-headset late-join test.

Register `dp.on_connected` / `dp.on_peer_join` in `start()`; nothing sends at file scope or in `awake()`. Numbers on the
wire are float32, so send ids, seeds and absolute times as strings. On a phone without its rig, gate gameplay work with
`dp.participating()`. Details and debugging: `Assets/DreamPark/Docs/MULTIPLAYER_RULES.md`.

## Rendering

Every pixel of virtual geometry must be clipped by the guest's real room (Meta Depth API occlusion), and that lives in
the shader, so shader choice is a correctness rule:

| Use for | Shader |
|---|---|
| Anything lit | `Shader Graphs/DreamPark-UniversalShader` |
| Unlit / flat / emissive / UI-in-world | `Shader Graphs/DreamPark-Unlit` |
| Every ParticleSystem material | `DreamPark/Particles` |
| TextMesh Pro text | `DreamPark/TextMeshPro/Distance Field` (or `/Mobile/Distance Field`) |

- **Opaque materials have Alpha Clipping ON** (`_AlphaClip` and `_ALPHATEST_ON` both; `DreamParkMaterialRules.IsSatisfied`
  checks both). For soft edges, use Surface Type = Transparent.
- Run `DreamPark → Optimization → Material Optimizer...` on every imported pack first. A custom shader is acceptable only
  when it integrates Meta occlusion (`HARD_OCCLUSION` / `SOFT_OCCLUSION` driving alpha).
- **Particles are Unity `ParticleSystem` components** authored in the prefab, on `DreamPark/Particles`. VFX Graph and
  particle frameworks have no Lua wrappers, no occlusion and no optimizer budget.
- Content ships no directional light (it would light every attraction in the park), and never writes
  `EnvironmentDepthManager.MaskBias` (to lift a floor mask, raise it a few cm).
- Portals (a big world seen through a wall or floor): `Assets/DreamPark/Docs/PORTALS.md`.

Details: `Assets/DreamPark/Docs/SHADERS.md`.

## Input

This is mixed reality: hands and physical presence are the input, never keyboard controls or virtual cameras. Phone
taps are an addition, never the only way in. Before putting anything in a player's hand, read `HANDHELD.md`: the hand
node is the OpenXR wrist frame, props carry `Grip` / `Muzzle` children, mount with `dp.hold`, and scales stay positive.

## Done means played and shipped clean

1. **Play it.** Enter Play mode and exercise the change, or run the AI Playtester
   (`Assets/DreamPark/Docs/PLAYTESTER.md`). Read the screenshots: the images are the ground truth.
2. **Clear the pre-upload checks.** Fix every Blocking and Warning finding; only a human decides to ignore a Blocking
   one. Fill the Content Uploader's Name and Description with real store copy.
3. **Upload as yourself**, with a human-approved agent key: `dreampark_agent_request` (emails the title's owner an
   Approve link) → `dreampark_agent_wait` → `dreampark_content_publish --target beta|release --wait false`, then poll
   `dreampark_content_publish_status` until it finishes. Each title is approved once; the project keeps one key per
   title. Publishing is a ship decision for the human; new titles ship beta-first.

Exact commands, flags, check ids and what an agent key can and cannot do: `Assets/DreamPark/Docs/SHIPPING.md`.

## Hard rules

- **Secrets stay secret.** Never print, paste or commit an agent key (`Library/DreamParkAgent/`) or an API key.
- **The 6-digit approval code belongs to the owner.** Never ask for it, type it or relay it. If the Approve link can't
  be used, show the human the request's `fallback` command on one line and let them run it.
- **Sign out only with `dreampark_agent_signout`.** Never call `/auth/logout` with an agent key (it ends a human's
  sessions). A `403 AGENT_KEY_SCOPE` means the route is outside the agent surface: ask the human.
- **SDK files are read-only for a title.** `Assets/DreamPark/` and the managed root `*.md` copies are replaced on SDK
  updates; customize in `Assets/Content/{GameName}/`.
- **Drive Unity through the `unity` CLI**, never screen control. A modal dialog blocks every command: report its exact
  text at once instead of retrying.
- **Save, revert or discard only scenes you opened.** Add C# under `Assets/` only when needed (each file reloads the
  Editor); tools and harnesses are builders in `Tools/Builders/`, scratch files live outside `Assets/`.
- **Every shipped material is on an occluding shader** (see Rendering).

## Reference: read when you need it

All paths are in `Assets/DreamPark/Docs/` unless they name the project root. `§ Name` in any doc points to a section
listed here.

| Task | Read | Sections |
|---|---|---|
| Unity CLI, builders, screenshots, play sessions | `UNITY_CLI.md` | Unity CLI |
| Attractions, props, addresses, previews, layout frames, walls, entries/exits, routes | `ATTRACTIONS.md` | The Three Primitives · Creating Attractions & Props · The attraction prefab · One attraction, one size · Layout Frames (legacy) · Wall Layers · Entries & Exits · Routes |
| Arena / Sequence / Adventure internals, Game Manager, package Lua API | `PACKAGE_RULES.md` | Packages · Compile output · Arena / Sequence / Adventure rules · The special levels · Game Manager · Player rig in packages · Lua package API · Checks & tests |
| Packages, creator view | `PACKAGES.md` (root) | The Packages panel · Arena · Sequence · Adventure · The Game Manager · The Player rig · Lua API |
| Full `dp` API, lifecycle hooks, pause, props at runtime, device time, room light | `LUA_API.md` | The `dp` Creator API · Loading props at runtime · Lifecycle hooks · Device time · Room light · What these replace |
| Park loading, optimizer, Lua surface gate, XLua `Gen/`, EasyEvent | `LUA_RUNTIME.md` | Lua Lifecycle Under Park Loading · What the optimizer guarantees · Rules that still apply · The Lua surface gate · XLua `Gen/` · EasyEvent is deprecated |
| Multiplayer rules and debugging | `MULTIPLAYER_RULES.md`, then `Assets/DreamPark/Samples/Multiplayer/MULTIPLAYER.md` | Multiplayer |
| Phones, gestures, hand-held objects, player head | `PLAYERS.md`, `HANDHELD.md` (root) | Phone players & gestures · Hand-held objects · PlayerHead.Override · `dp.local_player_present` |
| Shaders, materials, occlusion, particles, TMP text | `SHADERS.md` | Materials & Shaders · Particle effects |
| Portals | `PORTALS.md` | Portals |
| Saves, badges, items | `PLAYER_DATA.md` | Game Storage · Badges · Items |
| AI Playtester | `PLAYTESTER.md` | AI Playtester |
| Uploading, pre-upload checks, agent keys, `/developer` website | `SHIPPING.md`, `RELEASE.md` / `PIPELINE.md` (root) | Shipping · Agent access |
| Routes in depth | `ROUTES.md` (root) | |
| Specs: layout frames (legacy), wall layers, rect edges, package runtime | `LAYOUT_FRAMES_SPEC.md`, `WALL_LAYERS_SPEC.md`, `RECT_EDGES_SPEC.md`, `PACKAGE_RUNTIME_SPEC.md` (root) | |
| Game results, leaderboards, `dp.score` boards per way to play | `RESULTS.md` | |
| Ways to play (runner directors), lobby wait, the board | `RUNNERS.md` | |
| CPU players, game graph, playlist events | `CPU_PLAYERS.md`, `GAME-GRAPH.md`, `PLAYLIST-EVENTS.md` | |
