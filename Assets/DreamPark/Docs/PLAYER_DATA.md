# Player data: storage, badges and items

Per-user save data, where badges are earned, and the items catalog.
`§ Name` points to a section of these docs; the table at the end of `Assets/DreamPark/Docs/CLAUDE.md` maps every section to its file.

## Game Storage (per-user save data: high scores, progress, coins)
Sample: `Assets/DreamPark/Samples/GameStorage/storage_high_score.lua.txt`. Every LuaBehaviour gets a `storage` variable auto-bound to its attraction (lazy walk up to GameArea/PropTemplate/LevelTemplate — no ids to pass):

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
- **Backed by `GameStorageAPI`** (`Assets/DreamPark/Scripts/Core/`) → `/app/profile/storage/{contentId}`. Editor testing uses the SDK preview key (same `ProfileAPI.BindToLoggedInUser` pairing flow as inventory); production auth is the headset binding.
- **Hard caps** — this is progress/score storage, NOT a blob store: keys `[A-Za-z0-9_-]` ≤64 chars, string values ≤1 KB, 64 keys & 8 KB per scope, and at most 64 attraction scopes per game (`MaxAttractionScopes` — a scope count, not a byte cap). Oversized writes fail locally with a warning (`set` returns false).
- **Prefer `max`/`min`/`increment` over read-compare-`set`** — ops apply server-side, so the same profile playing on another headset can't be clobbered. Writes are debounced/coalesced automatically; per-frame `increment` is fine.
- **Works unbound**: guest play reads/writes locally and merges into the account if the player pairs mid-session. Nothing persists for a session that never pairs.
- Scripts outside any attraction (e.g. Player.prefab park systems) use `dp.storage.game(gameId)`; the injected `storage` there would warn-once and no-op.
- **Writes are gated on attraction entry** (`ContentGate`, `Scripts/Core/`). A guest in the park hasn't opted into every installed game, so storage flushes and all `ProfileAPI` writes (items/achievements/badges/DreamPoints) are held until the player enters a `GameArea` of that content, then flush in order and stay open for the rest of the identity. Reads and session heartbeats are never gated. Editor sessions auto-open (`ContentGate.AutoOpenInEditor`) since there's often no GameArea to walk into. It's a correctness guard against honest mistakes, NOT a security boundary — the server-side per-guest rate limits are that.

## Badges: where they're earned — auto links + manual `earnAt` (Oct 2026)
The app uses each badge's `awardedBy` to show WHERE it is earned: Today's Challenge floats the badge over that attraction in the diorama, or offers "Add <Attraction> to your park?" (and later the mini-map). The Content Uploader builds `awardedBy` for you from two sources and uploads their **union**:

- **Auto (inferred, read-only).** `BadgeAttributionScanner` walks every Attraction/Prop root's hierarchy — nested prefabs and variants included — and links a badge to that root when something inside it awards or **carries** the id: a `dp.profile.awardBadge("id")` call (literal, or an `@var` / instance value), a Lua string var whose value is the id (e.g. a reward medal with `badgeId` set per instance), an `@var` default, or a C# string field named `badge…`. Only ids the package defines count. Nothing to author.
- **Manual (`earnAt`, editable).** For badges awarded from Player-side / title-wide scripts that key off attraction names, a `groupName`, a track theme, etc. — the scan only sees "the Player", so link them by hand. In the panel's badge card: **Auto** row (read-only, with how each link was found in its tooltip) beside the **Manual** field (comma-separated prefab names, or the ▾ picker of the package's attractions and props). Or edit `Assets/Content/{id}/.badges.json` directly:

```json
{ "badgeId": "mg_dragon_slayer", "name": "Dragon Slayer", "description": "…", "iconAssetPath": "…",
  "idSource": 1, "discoveredIn": "…",
  "earnAt": ["A_DragonLair", "L_DragonLairSmall", "A_DragonLairBattle_Old"] }
```

Rules: names are the attraction/prop **prefab file names** (case-sensitive), never addresses; list the **current prefab first, then its size/variant prefabs**; **name the WRAPPER attraction, never an inner level** — a level nested inside a Sequence wrapper (`Level3_Boss` inside `A_BossBattle`, an `L_` level inside an `A_` prefab) is recognised by the app only through its wrapper, since nothing sent to the phone maps the inner level to it; **leave title-wide badges unlinked** (omit `earnAt` or `[]`); don't duplicate what Auto already shows. The uploader resolves each name to the address the upload stamps (`{gameId}/Levels/{size}/{name}` / `{gameId}/Props/{category}/{name}`, computed from the file name, never the possibly-empty serialized `resourceName`). The `badge-earn-at` check warns on a name with no attraction/prop prefab (typo, rename) — such a name is not uploaded. Every upload sends each still-awarded badge's full list, and the server REPLACES its stored links with it, so removed links never linger: placed links if any (no `player` row then); else `player` (title-wide, awarded by the Player); else `script` (awarded by package Lua not attached to any root). The automated data is authoritative: a badge with no award site and no manual link sends `[]`, which clears its old links and marks it not awarded in this version until a site or manual link exists (a severed automatic link is updated on the next push). `awardedBy` is omitted — stored data left alone — only when the scan itself failed, or when the badge's only link is a manual name that doesn't resolve (the check warns), so a broken scan or a typo can never wipe or vault good data. Changes reach the server only with the title's next badge push (every upload, or **Push Badges to Portal**).

## Items (catalog + per-instance metadata, Oct 2026)
Items work like badges: you author the CATALOG entry in Unity and it is created on the backend for you; players own
inventory rows that point at it. Sample: `Assets/DreamPark/Samples/ProfileAPI/item.lua.txt`.

```lua
dp.profile.awardItem("coin", 5)                          -- stackable (or a singleton when Stacks is off)
dp.profile.awardUniqueItem("cr_fox", { level = 1, uid = "u7" }, function(ok, item)
    -- item.instanceId = "cr_fox_1a2b3c4d": the key for updateItem/removeItem; item.metadata = your table
end)
dp.profile.updateItem(item.instanceId, { level = 2 }, cb)  -- merge (4th arg true = replace the whole table)
dp.profile.getItems("cr_fox")                              -- every instance; or { baseItemId=, type=, contentId= }
dp.profile.removeItem(item.instanceId, 1, cb)
local off = dp.profile.onInventoryChanged(fn)              -- after every load/refresh/write
```

- **Authoring:** Content Uploader → Park Assets → **Items**: Icon, Title, ID, Type, Rarity, Stacks, Description per card,
  drafted in `Assets/Content/{id}/.items.json` (commit it). Every id your Lua passes to `awardItem` / `awardUniqueItem` /
  `getItem(s)` / `hasItem` / `removeItem` is found and locked automatically (literals, `@var` defaults, Inspector
  values). For ids built at runtime (`'cr_' .. species`) add one comment per id: `-- @item cr_fox "Fox"`.
  **Push Items to Portal** saves them; every Compile & Upload pushes them too.
  Agents: `dreampark_items_scan|push|delete --content_id <id>`.
- **Item ids are global** (one `items/{id}` doc), so the server refuses to save over another title's item. Prefix them
  (`cr_fox`, `zz_medkit`).
- **Metadata:** a table of strings/numbers/booleans/nested tables, at most **4 KB of JSON per item** (award and every
  update), no array directly inside an array; keep ids as strings. The SDK stamps `contentId` (it scopes rows that have
  no catalog doc and can't be changed). Whole numbers read back as Lua integers.
- **A unique item needs Stacks ON** (the default). A non-stacking catalog item is a singleton: the server ignores award
  metadata and keeps one row (`item-award` warns).
- `updateItem` only works on rows minted with metadata (409 `ITEM_NOT_UNIQUE`), on your own title's rows (403
  `ITEM_NOT_OWNED`), and shares the per-player write budget (60/min, 600/h) with awards: update on game events, not per
  frame. Writes wait for the player to enter one of your attractions (`ContentGate`), like every profile write.
- `dp.profile.supportsItemMetadata` is true on SDKs with this API. Backend: `POST /app/profile/inventory/update`
  (Oct 2026).
