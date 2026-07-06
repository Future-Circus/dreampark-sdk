# DreamPark SDK — Content Creator Template

## What This Is
SDK template for third-party game developers. A complete Unity 6 project cloned from dreampark-core. Creators fork this repo and place their game content in Assets/Content/{GameName}/.

## Template Structure
```
Assets/
├── DreamPark/         ← SDK source (~540 C# files, must match dreampark-core exactly)
├── Content/
│   └── YOUR_GAME_HERE/        ← Renamed to PascalCase park name by new-park.sh
│       ├── 1. Scenes/Template.unity
│       ├── 2. Features/
│       │   ├── 1. Player/Player.prefab
│       │   └── 2. Level/Level.prefab (uses AttractionTemplate component)
│       ├── Scripts/            ← Game-specific C# (minimal — prefer Lua)
│       └── ThirdParty/         ← Only used assets (git-tracked, shipped in builds)
└── ThirdPartyLocal/            ← Imported packages land here (gitignored, not in builds)
```

## SDK Sync
The ~540 files in Assets/DreamPark/ must match dreampark-core exactly. `#if DREAMPARKCORE` blocks (core-only code) are conditionally compiled out of this SDK distribution — the source remains visible but doesn't compile in SDK builds. Use conditional compilation to mark what's core-specific. Anything entirely core-only (no SDK reason to exist at all, e.g. consumer-app pairing flows, internal admin tooling) should live in dreampark-core's own `Assets/Scripts/` outside `Assets/DreamPark/` rather than as an empty SDK file.

## Key Prefabs
- **Player.prefab**: Root player object (persists across attractions). Global systems (audio, score, park state) live here as LuaBehaviours.
- **DreamBand.prefab**: Wrist band UI integration.
- **Level.prefab**: Physical space definition using AttractionTemplate (extends LevelTemplate with auto-added GameArea and MusicArea).

## Creating Attractions & Props (content pipeline)
An attraction is a prefab under `Assets/Content/{GameName}/` with an `AttractionTemplate` (: `LevelTemplate`) on its root; a prop has `PropTemplate`. `ContentProcessor` (SDK-synced, `Assets/DreamPark/Editor/`) watches `Assets/Content/` and automates everything else — do NOT hand-edit Addressables addresses or labels:
- **Naming**: prefixing attraction prefabs `A_` and props `P_` is still the convention, but no longer required for backend discovery (July 2026): the attractions catalog classifies by the stamped ADDRESS NAMESPACE — anything with a `{gameId}/Levels/…` address (i.e. any prefab whose root has AttractionTemplate/LevelTemplate) is an attraction, `{gameId}/Props/…` is a prop. **Exception: an `L_` prefix marks a pre-attraction Legacy Level** (hidden from the Attractions browser, entry-fee-priced only) — never name a new attraction `L_*`. If an attraction is missing from the browser, check that the prefab root has the right template component (that's what produces the address).
- **Runtime address** (assigned automatically): `{gameId}/Levels/{size}/{name}` for attractions, `{gameId}/Props/{category}/{name}` for props, `{gameId}/{TypeFolder}/{name}` for typed assets (Models/Audio/Textures/…), `{gameId}/Previews/{name}` for preview PNGs. Label = `{gameId}`.
- **TWO identifier namespaces — never conflate them**: the runtime Addressables ADDRESS above vs the backend catalog `resourceName`, which is the internal ASSET-PATH stem (`Content/{GameName}/Attractions/A_X` — asset path with `Assets/` + extension stripped). They only coincided for legacy folder layouts. Core's `LevelAnchor.ResolveSpawnAddress` translates stem→address at spawn; treat `resourceName` as an opaque join key, never a loadable address.
- **Stamping pass** (`ContentProcessor`, EditPrefabContentsScope + SaveAsPrefabAsset): injects `gameId` into any component with a `gameId` field and stamps the per-attraction address onto `GameArea`/`PropTemplate.resourceName` — this is the revenue-attribution key, so it must match what the backend catalog derives. Skips `ThirdPartyLocal/`. Prefabs get edited + saved dirty by this pass; that's expected — commit the churn.
- **Previews**: `Assets/Content/{GameName}/Previews/{prefabName}.png` (or sibling `{name}_preview.png`) — powers Attractions-browser/level-picker tiles and the consumer map. No preview = blank tile.
- **Catalog population is automated**: uploading a build publishes the attractions catalog server-side (discovered from the catalog's `m_InternalIds`). There is no manual registration step — if an attraction is missing from the browser, check the `A_`/`P_` prefix and that the prefab root has the right template component.

## DO NOT
- Add core-specific code (e.g., backend debug toggles, internal versioning systems).
- Modify Assets/DreamPark/ files without syncing back to dreampark-core.
- Use keyboard controls, virtual cameras, or EasyEvent chains as primary interaction pattern.

## Workflow
1. Creator clones dreampark-sdk as a new project.
2. new-park.sh renames YOUR_GAME_HERE to the park name (e.g., CoinCollector).
3. Creator adds game content, Lua scripts, and prefabs to Assets/Content/{GameName}/.
4. All gameplay logic is Lua-first via LuaBehaviour.
5. Built Addressable prefabs are deployed to DreamPark servers.

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

**Writing Lua multiplayer scripts** (reference sample: `Assets/Content/YOUR_GAME_HERE/Scripts/lua_touch_color_switch.lua.txt`):
- `onnet(payload)` at file scope is auto-wired to the sibling NetId's events; `net_send(eventType, payloadJson)` is injected — both require a `NetId` on the SAME GameObject as the LuaBehaviour, and net_send additionally requires DreamBoxClient to exist at Awake. Always nil-guard: `if net_send then net_send(...) end` (solo play must work).
- `onnet` receives the FULL wire JSON `{"type":"...","payload":{"netId":N,...}}` — use the global `json_parse(payload)` and read `t.payload.<field>`.
- The relay never echoes your own message back: apply changes locally when sending (optimistic apply).
- One owner per networked visual property: never mix a MaterialPropertyBlock writer (e.g. TestNetObject) and a `renderer.material` writer (Lua) on the same object — the MPB silently masks material changes.

**Debugging**: tick `Verbose Net Logs` on DreamBoxClient (or set `NetLog.Verbose = true`) → per-beacon discovery, `RECV` previews, relay fan-out, NetId registrations. Always-on warnings and their meanings: `UNREGISTERED NetId` = id mismatch between builds; `NO subscribers` = receiving script missing on that client's object; `Ignoring peer beacon` = channel/park/protocol-version filter (reason included). Healthy session signature: one side `→ Hosting`, other `→ ClientPeer`, host shows `Peer connected … (2/16)` — a host stuck at 1/16 is broadcasting to nobody.

**Platform**: hosting compiles on Android (Quest) + Editor; iOS is client-only in v1. On-device discovery REQUIRES `CHANGE_WIFI_MULTICAST_STATE` (+ `WAKE_LOCK` for the host's Wi-Fi lock) — provided in `Assets/Plugins/Android/AndroidManifest.xml`; loopback bypasses the Wi-Fi broadcast filter, so localhost tests pass without it (deceptively). UDP broadcast is lossy on phone hotspots — the arbiter treats the connection, not beacons, as liveness ground truth; never re-add beacon-silence-kills-connected-session logic.

**Namespace gotcha**: dreampark-core declares `class DreamPark` INSIDE `namespace DreamPark`. In SDK-synced files, `DreamPark.X` inside a `namespace DreamPark` scope resolves to that class in core and fails to compile. Use unqualified sibling references, or `global::DreamPark.X` from global-namespace files.
