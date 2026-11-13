# Game Graph

A compact, versioned JSON picture of a title: **attractions → their gameplay objects → the Lua
that drives them**, plus badges, playlists and cross-links. An agent (Dreamie, the playlist
generator, insights, a developer's coding agent) reads it to understand a game **without
Unity and without downloading bundles**.

- Schema id `dreampark.game-graph`, `schemaVersion` **1**. Additive changes keep 1; a breaking
  change bumps it (readers accept the versions they know: `lib/gameGraph.js SUPPORTED_SCHEMA_VERSIONS`).
- Two files per title/release:
  - `<Title>.json` (the graph: structure + Lua *index*), ~1–1.5 MB raw, **100–220 KB gzip**.
  - `<Title>.lua.json.gz`, the **sidecar** with every script's full source (`{schema:
    "dreampark.game-graph.lua", title, sources: {scriptId: text}}`), 0.5–0.8 MB gzip.
    The graph says `lua.sources: "sidecar"` and carries `lua.sourcesSha`.
- **Deterministic**: the same project gives byte-identical output (no timestamps; stable
  order). `revision` = sha256 (16 hex) of the graph without `revision`/`generator`, so it
  changes only when the content does. Diff two releases' graphs to see what changed.

## Producers

| Producer | When | Where |
|---|---|---|
| `GameGraphExporter` (SDK, Editor/GameGraph) | **Content Uploader, after every successful release commit** (fire-and-forget, never affects the upload) | `POST /api/content/:id/game-graph` → private Storage |
| same, headless | developer / CI agents | `Unity -batchmode -quit -projectPath . -executeMethod DreamPark.Editor.GameGraph.GameGraphExporter.RunHeadless -contentId MyGame [-out dir]` → prints `GAME_GRAPH_REPORT_BEGIN … END` (exit 0 ok, 1 failed, 2 bad args); default out `Library/GameGraph/` |
| same, menu | by hand | DreamPark → Game Graph → Export All Titles |
| offline builder (no Unity) | platform agents, backfill | `dreampark-core/Tools/Dreamie/game-graphs/tool/build.js` (reads prefab YAML, `.meta`, `.lua.txt`) and `all.js` for every first-party title |

The two producers share the schema; the Lua index is the same algorithm in C#
(`GameGraphLuaIndex.cs`) and JS (`luaIndex.js`) and gives identical output (checked on the
SAL, WizardsWay, ArcadeLand and Zombiez scripts). The offline builder also joins the
objective catalog (`objectives.source: "catalog"`) and draft playlists
(`title.playlistsSource: "draft"`); the SDK exporter infers objectives from signals
(`source: "inferred"`) and ships the title's compiled playlists.

## Top level

```jsonc
{
  "schema": "dreampark.game-graph", "schemaVersion": 1, "revision": "57c81f6269ef5d66",
  "generator": "…",
  "title": {
    "id": "SuperAdventureLand", "contentId": "SuperAdventureLand",
    "packageKinds": ["arena","adventure","sequence"],          // compiled package manifest
    "playlistKinds": [{"id":"world","singular":"World","plural":"Worlds"}], "playlistsSource": "title",
    "gameManager": {"prefab":"Game Container","scripts":["game-container", …],"components":["Lua","NetId"],"nodes":6},
    "player":      {"prefab":"Player","scripts":[…],"components":[…],"nodes":41},
    "phonePlayer": {…},                                          // when the title has one
    "objectiveNotes": {…}                                        // offline builder: catalog evidence (file:line)
  },
  "counts": {"attractions":95,"props":60,"prefabs":396,"nodes":2419,"scripts":200,"luaBytes":2546024,"functions":4236,"badges":15,"playlists":7},
  "attractions": [ Template… ], "props": [ Template… ],
  "prefabs": { "<prefabId>": Prefab… },
  "lua": { "sources": "sidecar", "sourcesSha": "…", "scripts": { "<scriptId>": Script… } },
  "badges": [ Badge… ], "playlists": [ Playlist… ],
  "index": { "signals":   {"stage_won":["game-container", …]},          // event → scripts that SEND it
             "listeners": {"stage_won":["game-container","platform"]},  // event → scripts that RECEIVE it ("platform" = runner/profile)
             "unheard":   ["half","retry","team_gameover"],             // sent, received by no script and not the platform
             "dp":        {"dp.playlist.score":["game-container"]},     // dp API → scripts
             "hooks":     {"start_challenge":["easy-challenge"]},       // platform hooks → scripts
             "fall":      {"<scriptId>":[lines]} },
  "warnings": []
}
```

### Template (attraction / prop)

| field | meaning |
|---|---|
| `id` | prefab leaf (`A_CoinRush`); the content-relative path when two prefabs share a leaf. Prefabs under hidden or `~` folders (`_Retired~`) are skipped, as Unity skips them |
| `kind` / `component` | `attraction` (`AttractionTemplate`/`LevelTemplate`) or `prop` (`PropTemplate`) |
| `resourceName`, `attractionId` | Web catalog key (`Content/…/A_CoinRush`) and its slug (the `attractionsCatalog` row id) |
| `name` | `AttractionTemplate.displayName`, else humanized leaf |
| `size`, `customSizeFt`, `required` | authored size class / custom feet / `gameRequiresAttraction` |
| `footprintM` | packing bake: `authored`, `shrink`, `grow` metres `[w, l]` |
| `packingBake`, `packingBakeVersion` | whether a valid packing bake is present (attractions) |
| `layoutFrames`, `frames`, `feet` | authored frames; the compiled manifest's frame ladder and Arena feet |
| `modes`, `packageRoles` | packages it ships in (`arena`/`adventure`/`sequence`/`playlist`) and their roles |
| `preview` | content-relative preview PNG (Web serves the URL from the catalog row) |
| `badges` | badge ids whose `earnAt` names this attraction |
| `scripts` | every Lua script in its closure (own objects, nested prefabs, spawned prefabs) |
| `uses` | prefab ids in that closure |
| `objectives` | `{source: "catalog"\|"inferred", default, best, kinds: {collect:"live", boss:"hook", …}}`. Kinds and levels are the objective catalog's (`dreampark-core/Tools/Dreamie/objectives/catalog.json`: platform / live / hook / build); `inferred` = derived from signals |
| `roleHint`, `note`, `inPlaylists`, `playlistStops` | catalog role/note, catalog playlists, and `playlistId/legId` stops in this graph's playlists |
| `space` | collider geometry, below |
| `fallScripts` | scripts in the closure with fall / respawn / kill-plane handling |
| `root` | root GameObject name |

### `space`: geometry for generic detectors (reach-the-end, no-fall)

All boxes are axis-aligned, in the **attraction root's local space** (metres; the root's own
placement excluded), and include colliders inside nested prefabs.

```jsonc
"space": {
  "axis": "z",                         // long axis (from the footprint), the "start → far" direction
  "footprintM": [2.74, 4.27],
  "extent": [[-1.35,0.7,-2.12],[1.33,1.1,2.11]],   // all solid colliders
  "startEdge": -2.12, "farEdge": 2.11, // extent along `axis`
  "solids": 24, "meshColliders": 2,    // mesh colliders: bounds from the mesh (SDK) or unknown (offline)
  "meshUnknownBounds": 1,             // mesh colliders whose bounds the producer could not read (offline: FBX meshes)
  "triggers": [{"path":"A_X/Goal","min":[…],"max":[…],"trigger":true,"tag":"Exit","layer":"Triggers","scripts":["exit-pad"]}],
  "hazards":  [{"path":"A_X/Pit/LavaCollider","min":[…],"max":[…],"mesh":true,"approx":true,"tag":"Lava","why":"tag"|"name"|"script"|"cutout"}],
  "cutouts":  [{"path":"A_X/L_LAVAPIT_TINY","kind":"hazard"|"hole","min":[…],"max":[…],"y":0,"poly":[[x,z], …],"scripts":["procedural-lava-pit"]}],
  "raised":   {"n": 9, "topMax": 1.75, "along": [-0.39, 0.36]}   // solid tops ≥ 0.25 m you could fall off
}
```

Triggers are sorted along `axis` (first 40). Hazards, in priority order: a hazard **tag**
(Lava, Water, Kill, Death …); a hazard **name** unless the name says it is the safe part
(platform, stone, step, bridge, ledge …) or the tag is Ground/Level/Block; a **trigger** whose
script's fall lines are about the player; a FloorCutout **cutout**. Mesh-collider bounds come
from the mesh (built-in meshes and `.asset` meshes offline; any mesh in the SDK). A mesh
collider with no mesh of its own (a procedural pit rebuilds it at runtime) takes the nearest
ancestor MeshFilter's bounds and is marked `approx`. FloorCutout outlines (lava and ice pits)
are `cutouts`, polygons in root space (`poly` = [x, z], `y` = floor height); `kind: hazard` when
the cut-out holds a hazard-tagged collider or a lava/pit/ice/water script. Other runtime
geometry (spawned platforms) is not in a prefab: see `fallScripts` and `index.fall`.

### Prefab (pruned hierarchy)

```jsonc
"A_CoinRush": { "path": "2. Features/3. Level/Sequence/A_CoinRush.prefab", "root": "A_CoinRush",
  "variantOf": "A_Base",            // prefab variants
  "spawns": ["E_COIN"],             // prefabs referenced from fields (spawners, projectiles)
  "nodes": [
    {"path":"A_CoinRush","c":[{"t":"GameArea","f":{…}},{"t":"AttractionTemplate"},
                              {"t":"Lua","script":"physics-settle","f":{"holdSeconds":0.6,"debugSettle":false}}]},
    {"path":"A_CoinRush/L_COIN_3x2 (1)","prefab":"L_COIN_3x2","count":4,"at":[[0.74,0,-1.14], …]},
    {"path":"A_X/Gate","prefab":"P_Gate","ov":{"floatInjections[0]":2,"Door:speed":3}}
  ] }
```

A node is kept when it has a gameplay component, a tag, removed components, or is a nested
prefab instance whose prefab (or overrides) matter; plain art and pure-physics colliders without a tag are pruned,
and ancestors are implied by `path`. Fields: `pos` (local position), `tag`, `layer`,
`inactive`, `c` (components), `prefab` (nested instance → another prefab id, or `art:<leaf>`
for an art-only prefab), `ov` (instance overrides that matter: Lua injections by name, tag,
layer, active, SDK component fields, `art` = a words-carrying material/sprite swap), `removed`
(components / GameObjects the instance removes from its prefab, `Type@Sub/Path`), `chain`
(the EasyEvent component types in order: component order IS the event order),
`count`/`at`/`rat`/`atMore` (identical siblings collapsed; `at` local, `rat` root-space).

### Transforms

`pos` is the local position. Every non-root node also carries its placement in the **prefab
root's space**: `rp` (position; omitted when equal to `pos`), `rq` (rotation quaternion
`[x,y,z,w]`; omitted = identity) and `rs` (scale; omitted = 1). Parent rotations, parent
scales and prefab-instance TRS (base root values + instance overrides) are composed. To place
a nested prefab's objects in the attraction: `T(instance node) ∘ T(child rp/rq/rs)`.
`lib/gameGraph.flattenAttraction(graph, id)` does this walk and returns every object in the
attraction root's space. (Objects added under a sub-object of a nested instance are placed
relative to the instance root, an approximation; non-uniform scale under rotation is
approximated.)

Components (`c[]`): `{t:"Lua"|"EasyLua", script, f:{injection: value}}` (all injection
kinds by name), colliders `{t:"BoxCollider", trigger:true}`, `{t:"AudioSource", clip, loop,
playOnAwake}`, `{t:"Rigidbody", kinematic}`, `NavMeshAgent`, `{t:"Text", text}` (TextMeshPro,
UI Text, TextMesh: the words players read), `{t:"WordsArt", art:[names]}` (speech-bubble,
instruction-card and sign sprites/materials; for image bubbles the asset name is all there is),
and every SDK / title MonoBehaviour (`NetId`, `EasyEnemy`, `GameArea`, `FloorCutout`,
`EasyEvent` …) with up to 24 serialized fields (`f`). Value refs: `@Path/In/Prefab`
(`@Path#Type` for a component), `#prefabId`, `$scriptId`, or an asset's content-relative
path. UnityEvents become `[{to, fn, arg}]`.

### Script (Lua index)

```jsonc
"game-container": {
  "path":"Scripts/game-container.lua.txt", "bytes": 98213, "sha1":"…", "lines": 2092,
  "functions":[{"name":"pl_tick","line":1641,"params":["dt"],"scope":"local"|"global"|"member"|"field"}],
  "globals":["available_stages", …], "globalsRead":["sal_damage", …],
  "hooks":["awake","onready","update"],                         // lifecycle + platform hooks it defines
  "dp":{"dp.playlist.progress":{"n":1,"lines":[1685],"args":["…"]}},   // args = literal first arguments
  "signals":{"stage_won":{"n":3,"lines":[1700,1911,1955],"via":["literal"]},
             "sequence_state":{"n":2,"lines":[76,140],"via":["net_send"]}},
  "badges":{"sal_champion":{"n":1,"lines":[123],"how":["award"|"mention"]}},
  "listens":{"sal_damage":{"n":1,"lines":[593],"via":["dp.on_global"]}},      // registered listeners
  "compares":{"sequence_state":{"n":1,"lines":[201]}},                       // == "name" (onnet dispatch)
  "dialogue":[[1702,"Grab every coin!"]],                                    // prose passed to say/toast/bubble/hint…, or say=/text= fields
  "fall":[[1712,"if p.y < KILL_Y then respawn() end"]],
  "usedBy":["Game Container:Game Container/Manager"], "attractions":["A_…"]
}
```

- **Platform hooks** recognised: `start_challenge`, `stop_challenge`, `end_challenge`,
  `dreamie_describe`, `dreamie_select_mission`, `select_mission`, `mission_count`,
  `mission_name`, `standing_mission`, `pick_mission`; lifecycle: `awake start update
  fixedupdate lateupdate ondestroy onenable ondisable onnet onwalls on_rect_changed onlayout
  ontrigger* oncollision* onapplication* onzoneenter onzoneexit onready onpause onresume`.
- **Signals**: a call whose callee ends in `signal / emit / broadcast / send / fire / notify /
  net_send / award* / report* / …` with a string-literal first argument, plus known win-signal
  literals (`stage_won`, `boss_defeated`, `exit_reached`, `timed_out`, …) wherever they appear.
- **Receivers** (`index.listeners`): a script that registers a listener with a literal name
  (`on* / subscribe / listen / register / dp.on_global("x", fn)`), compares against it
  (`t == "x"`, the usual `onnet` dispatch), or defines a handler function named after it;
  `platform` for playlist win signals, dp-routed signals and badge ids. `index.unheard` lists
  events sent but received nowhere (a C# receiver would not show, so treat it as a lead).
- Lifecycle and physics callbacks (`ontriggerenter`, `oncollisionenter` …) are hooks, never events.
- Comments never count; string contents never count as code.
- External scripts (a Lua file outside the content folder) appear as `ext:<leaf>` with
  `external: true` and no source.

### Badge, Playlist

```jsonc
{"id":"sal_lionheart","name":"…","description":"…","earnAt":["A_Lionheart", …],
 "awardSites":[{"script":"sal-badges","lines":[121],"how":["award"]}]}
{"id":"great-sun-heist","kind":"adventure","name":"…","pitch":"…","tags":[…],"badges":[…],
 "legs":[{"id":"sun-stolen","name":"…","beat":"opening","minutes":3,"objective":{"type":"reach_exit","target":"A_BlockParty"},
          "stops":[{"attraction":"A_BlockParty","role":"start","alternates":[],"displayName":"…"}]}]}
```

A stop names a template `id`; a platform stop is `DreamParkBookends/start-lobby`; an
unresolved one is `?<authored name>`.

## Storage and privacy

Lua source is the developer's IP.

- Stored per release at `content-graphs/{contentId}/v{N}/game-graph.json.gz` and
  `…/lua.json.gz` (Firebase Storage, **never made public, never under `addressables/`**,
  which the public bundle proxy serves), metadata at Firestore
  `content/{contentId}/gameGraphs/v{N}` (revision, counts, sizes, sha256, submittedBy).
- **Write**: `POST /api/content/:contentId/game-graph {versionNumber, graph, luaSources}`
  (title owner or the title's own release token).
- **Read**: `GET /api/content/:contentId/game-graph[?version=N&lua=1]`, **owners and platform
  admins only**. Platform services (Dreamie brain, playlist generator, insights) read
  in-process with `lib/gameGraph.loadGameGraph()`. There is **no public or consumer
  endpoint**, and Web's test fails if another route or lib starts loading graphs.
- Dreamie's in-game tips get **design facts only** (`tipFacts`: how a stop is won, objective
  kinds, badges, hazards), never source text or puzzle internals.

## Reading it (Web: `lib/gameGraph.js`)

`findAttraction(graph, key)` (id, attractionId, resourceName, display or stop name),
`attractionObjects`, `scriptsBySignal(graph, "boss_defeated" | "dp.playlist.score" |
badgeId)`, `findFunction(graph, name|RegExp)`, `searchLua(sources, pattern)`,
`functionSource(sources, script, line)`, `badgeSites`, `playlistStops`, `listenersOf(graph, event)`,
`flattenAttraction(graph, id)` (every object placed in the attraction root's space),
`summarize(graph, {budgetTokens, focus, question, lua})` (a token-budgeted text view for an
LLM: title, attraction table, signal index, badges, playlists, ranked scripts and source
excerpts), `tipFacts`, `makeTipFacts()`.
