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
│       │   ├── 2. DreamBand/DreamBand.prefab
│       │   └── 3. Level/Level.prefab (uses AttractionTemplate component)
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
