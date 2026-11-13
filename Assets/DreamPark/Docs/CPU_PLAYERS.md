# CPU players: a platform primitive (Oct 18 2026)

A CPU player (a sports opponent, a computer racer) is declared once by its title. After that it
shows up everywhere players do, clearly marked as a CPU.

Code: SDK `Scripts/Features/Playlist/CpuRoster.cs` (the cast) and `PlaylistScoreboard.cs` (the rows), plus
`Scripts/Core/DreamParkLuaPlaylist.cs` (Lua `dp.cpu`). This file extends PLAYLISTS.md § 7.8 (CPU entrants and
`placings`) and § 11.10 (CPU rows); nothing there changes.

## 1. Declare: `dp.cpu`

Every device runs the title's Lua, so every device declares the same cast. Do it at load, in the Game
Manager or a library. Guard every call with `if dp.cpu then`, because old apps have no `dp.cpu`.

```lua
if dp.cpu then
    dp.cpu.register{
        id = 'pip',                         -- → 'cpu:pip' (lower-case; stable for the whole run)
        name = 'PIP',
        portrait = hudMaterial,             -- a Texture, Sprite or Material from the title's package …
        uv = { 0.703, 0.844, 0.781, 1.0 },  -- … and its cell (u0, v0, u1, v1; Unity's bottom-left origin)
        avatar = 'https://…/pip.png',       -- and/or an absolute https PNG (≤ 256 px)
        color = '#FFB000',                  -- livery: the disc / orb colour
        difficulty = 'normal',              -- the title's own word
        title = 'Tennis Champion',          -- a short subtitle (optional)
        tag = 'DreamPal',                   -- the small tag beside the name (default 'CPU')
        champion = false,                   -- the title's champion: "DreamPal | Champion" (gold)
    }
end
dp.cpu.roster{ {...}, {...} }   -- many at once → count
dp.cpu.id('PIP')                -- 'cpu:pip'
dp.cpu.get('cpu:pip')           -- {id, name, kind='cpu', tag, avatar?, color?, difficulty?, title?, champion} or nil
dp.cpu.is_cpu(id)  /  dp.cpu.list()
```
- **Portraits:**
  - A title texture never crosses the network. Each device bakes the cell into a 160 px PNG on first use
    (GPU blit, so a compressed or non-readable atlas works) under `temporaryCachePath/dp-cpu/`. That
    device's surfaces use it.
  - `avatar` (https) is the portable face for Web, reels and the server copy.
  - With neither, a CPU gets its livery disc.
- Re-registering an id updates it (the last call wins). At most 64 CPUs per device.

## 2. Report: CPUs are entrants like players

Use the same calls as for players (PLAYLISTS.md § 7.2 / § 7.8), with the CPU's id:
```lua
dp.playlist.standings{                                   -- a race / match: every entrant, once, from the title's leader
    { racer = dp.me(), name = 'Leo', kind = 'player', place = 2 },
    { racer = 'cpu:pip', kind = 'cpu', place = 1 },     -- name / face come from the cast
}
dp.playlist.standings{ { racer = 'cpu:kenji', kind = 'cpu', medal = 'gold' } }   -- a medal-scored title
dp.playlist.report_result{ racer = 'cpu:rosa', kind = 'cpu', place = 3 }
dp.playlist.score('cpu:rosa', 120)                       -- an absolute score this leg (max-merged)
```
- **Id:** a `cpu:` id, `kind = 'cpu'` or a declared id is a CPU. Ids are normalized to `cpu:<lower-case id>`.
  The same id every leg means the same row, so its totals accumulate across the run.
- **Face:** the reporting device's cast entry rides each report (`k, av, co, tg, tt, df, ch`). The leader's
  row has the face even if the leader never declared that CPU.
- **Who reports:**
  - Report a race once, from the title's race leader (§ 7.8 delta 5).
  - A medal or score report may come from several devices: a placing is overwritten with the same values,
    and a score is max-merged, so a repeat is harmless.

## 3. Score: the same rule as players
- `placings`: points per place (the title's `points`, else 15·12·10·8·6·4·2·1); `sum` / `best`: the leg points.
- **Medals → points:** an entrant report may carry `medal = 'gold'|'silver'|'bronze'|'none'` (or 3/2/1/0).
  - Unless `points` is given, it is worth 3/2/1/0 points.
  - It is kept per leg (`legs[].medal` on the board).
  - With a medal or points and no `place`, no finishing order is recorded (the cell shows the medal / points,
    not "DNF").
  - **A title that scores in medals gives its CPUs medals too.** Without that, the CPUs never reach the
    scoreboard.
- A CPU can win. Ranking is `PlaylistScoreRules.Ranked` for everyone, so a DreamPal that beats you stands on the
  top step.

## 4. The board row and the wire
`PLAYLIST_STATE.scoreboard[]` (and `dp.playlist.state().scoreboard`) gains these fields for CPU rows:
```jsonc
{ "rank", "id": "cpu:pip", "name": "PIP", "kind": "cpu", "tag": "DreamPal", "title"?, "difficulty"?, "champion"?: true,
  "avatar"?: "https://…",        // portable
  "portrait"?: "file:///…/dp-cpu/cpu_pip.png",   // THIS device's baked portrait (never sent to the server)
  "color"?, "score", "legs": [{ "leg", "score", "place"?, "dnf"?, "medal"?: "gold" }] }
```
- A player row may carry `legs[].medal` too.
- `tag` is always present on a CPU row (default `CPU`).
- The durable copy (§ 5a.1 `complete {result:{players}}`) copies `kind`, `tag` and `champion`, but **never
  `portrait`**.

## 5. Display (every surface that lists players)
| Surface | CPU row |
|---|---|
| Headset Results Room / standings flash (SDK `MagicBoard`) | portrait (else livery disc) + the tag as plain text after the name (`DREAMPAL`, champion `DREAMPAL \| CHAMPION` in gold) |
| Headset podium + spread sign | the CPU's portrait on its step; it can take the top step |
| iPhone Results: hero / podium, standings, score per round, live bar, share card | `portrait ?? avatar` + icon + text, " \| " between items (`DreamPal \| Champion`) |

**Tags are plain text, never a pill** on the headset and iPhone surfaces above: no capsule, chip, background or
border around a tag. Write an icon (the app's own icon set, never emoji) and the text, with " | " between items.
**Exception:** the web portal's game-result rows (session Results, Highscores, `/docs`) show the CPU tag as
a small pill after the name, beside a smiling-computer CPU face.

## 6. Rules
- **CPUs are never "you".** They never earn badges, ratings, play facts or profile stats:
  - the badge montage reads `e.{playerId}` from real devices only;
  - ratings post per device;
  - profile stats skip `kind: "cpu"` rows;
  - the phone app never marks a CPU row as you.
- **Existing CPU reporters keep working.** A title that already reports `cpu:<id>` with `kind = 'cpu'` and an
  explicit `place` keeps working unchanged.
- Players' ids are never normalized; only CPU ids are.
