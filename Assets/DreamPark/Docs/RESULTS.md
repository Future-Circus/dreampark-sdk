# Game results & leaderboards (Nov 2026)

A **result** is what happened in one round of one game: a high-score list, a race's finishing order, a match's
final score, or a co-op group's score. CPUs are included. You report it once, at the end of the round, with
`dp.results.report{…}`. The platform then shows it in two places:

- **The session** (the session page, the session email and the live portal) shows every result of every game in
  play order: "Race 1 / Race 2" with finishing places, "Match 1: Sam vs CPU 11 – 8", score lists. CPUs appear
  there, tagged CPU.
- **The Leaderboard** (the QR portal's Leaderboard and each game's own page, this portal or Everywhere) shows a
  result only where a board makes sense. You choose the board with `board`. Games whose rounds make no leaderboard
  (a race, Pong) show **Recent games** there instead. CPUs never land on a leaderboard and never earn anything.

**Whose board is it?** By default the **title's**: a zombie shooter is one game with one Leaderboard entry, whichever of its
levels a round was played in. A title made of separate games (an arcade: Pong, RC racing, Snake…) gives each its own
entry with `game` (§ 2a). A title's board splits by **way to play** (Waves is not Campaign; the
player picks the way on the Leaderboard); a game inside a title is one board whatever way it was played in. The game
never passes the way to play. The platform stamps it.

Code: SDK `Scripts/Core/ResultsAPI.cs` (Lua `dp.results`). Server: `POST /app/profile/results`.

## 1. Report a round

```lua
if dp.results then                     -- old apps have no dp.results
    dp.results.report{
        go       = self.gameObject,    -- REQUIRED: tells the platform which game, attraction and way to play
        round    = R.e,                -- a value every headset in this round agrees on (see § 5)
        kind     = 'score',            -- 'score' | 'placement' | 'match' | 'coop'
        entrants = {
            { player = 'me', score = 1520 },
            { player = otherPid, score = 900 },
            { cpu = 'cpu:blue', score = 1100 },
        },
    }
end
```

`report` returns the result id (a string), or `nil` when the platform rejected the report. The console then says
why, on a line tagged `[Results]`. It never throws.

### The four kinds

| Game shape | kind | default board |
|---|---|---|
| High score (Snake, an asteroid shooter, a brick breaker, an endless runner) | `score` | `best` |
| Race / pole position / elimination order | `placement` | `none` (Recent games), or a `best_lap` stat board |
| Head-to-head or team final score (Pong 11–8, tennis sets, 2v2) | `match` | `none`, or `wins` |
| Group score (a party wall, waves survived together) | `coop` | `best` (one group row, every name) |
| Fastest time / golf strokes | `score`, `order = 'low'`, `unit = 'time'` / `'# stroke(s)'` | `best` |
| Survival time / wave / floor reached | `score`, `unit = 'time'` / `'wave'` / `'floor'` | `best` |

### Every field

| Field | Meaning |
|---|---|
| `go` | Required. Any object of your game (usually `self.gameObject`). It can be the Game Manager or the Player rig. |
| `round` | The round's merge key (§ 5). A string or a number, at most 64 characters. |
| `kind` | `score` (default), `placement`, `match`, `coop`. |
| `label` | The round's noun. The portal numbers rounds per session and game: "Race 1", "Race 2". Default `Game` (score, coop), `Race` (placement), `Match` (match). At most 24 characters. |
| `title` | An explicit heading ("Grand Final") that replaces label + number. At most 48 characters. |
| `variant` | A board split inside one game: the track, difficulty or level ("Moon", "hard"). At most 40 characters. Each variant gets its own board. |
| `order` | Which value wins for score / coop: `high` (default) or `low` (strokes, times). |
| `unit` | How values read: `points` (default), `time` (milliseconds), `distance` (metres), `percent` (0–100). Any other word (≤ 24 letters) reads before the value: `wave` → "Wave 12", `floor` → "Floor 7". A `#` puts the value where it stands and `(s)` marks the plural: `'# stroke(s)'` → "72 strokes" / "1 stroke", `'# lap(s)'`, `'# kills'`. Values always sort as numbers (Wave 100 above Wave 11). |
| `board` | `best`, `none`, `wins`, or `{ stat = 'best_lap', order = 'low', unit = 'time' }`. See § 2. |
| `entrants` | Every player AND every CPU in the round, once each, 1 to 16. |
| `sides` | Teams. Required for a match (2 or more). Optional for co-op (one side = the group). |

An entrant:

| Field | Meaning |
|---|---|
| `player` | A player: `'me'`, or a player id from `dp.me()` / `dp.peers()` (the same ids your net code uses). |
| `cpu` | A CPU: its `dp.cpu` id (`'cpu:blue'`). Its name, colour, face and tag come from the cast (CPU_PLAYERS.md). |
| `name` | Optional. The platform fills players' names from presence and CPUs' from the cast. |
| `score` | score / coop: the value, in `unit`. placement / match: optional per-player points. |
| `place` | placement: the finishing position (ties share a place). Optional elsewhere. |
| `dnf` | placement: did not finish (no place). |
| `side` | match / coop: which side (1 = the first entry of `sides`). |
| `medal` | Optional: `gold`, `silver`, `bronze` or `none`. |
| `stars` | Optional: 0 to 5. |
| `stats` | Optional: up to 6 numbers, keys `[a-z0-9_]`, at most 24 characters (`{ best_lap = 31200, kills = 12 }`). |

A side: `{ score = 11, name = 'Red', sets = { 6, 3, 7 } }`. All three fields are optional. `sets` holds per-period
scores, up to 9.

### Places you leave out

- **placement** with no `place` on any entrant: places come from the entrant order (DNF entrants get none).
- **score** and **coop** with no `place` on any entrant: places come from the scores, by `order`. Ties share a
  place: 90, 90, 40 → 1st, 1st, 3rd.
- If any entrant has a `place`, nothing is computed.

### What gets rejected

An unknown `kind`, a `unit` or `board` that is not one of the forms above, no entrants or more than 16, the same
player or CPU twice, a number that is not finite, a match with fewer than 2 sides or an entrant without a valid
`side`, `stars` outside 0–5, a `place` outside 1–64, a bad stat key. Strings longer than their cap are cut, never
rejected.

## 2. Boards

| `board` | What the leaderboard shows for this game + way to play + variant |
|---|---|
| `best` | Each real player's best value, sorted by `order`, shown in `unit`. |
| `wins` | Who won the most rounds (match and placement records). |
| `{ stat, order, unit }` | Each real player's best `stats[stat]`, sorted by its own `order`, shown in its own `unit`. |
| `none` | No leaderboard: **Recent games**, the latest results as compact cards. |

Players are matched by account, then guest, then name. CPUs are left out of every board.

## 2a. Games inside a title: `game`

| `game` | Whose Leaderboard entry this round counts for |
|---|---|
| left out | **The title** (the default). Its name and title card head the entry. |
| `true` | **The attraction this round was played in.** An arcade title's games each pass this. The entry takes the attraction's authored name and the preview the Content Uploader made for it. |
| `'A_Pong'` | **That attraction, by its prefab name**, when the round is reported from somewhere else (a hub, a lobby). `game = true` inside `A_Pong` and `game = 'A_Pong'` name the same board. |
| `'boss-rush'` + `gameName = 'Boss Rush'` | **A game that is not one attraction.** A plain id (lower-cased, at most 64 characters) with the name to show; it takes the title card. |

- A game inside a title has one board, never split by way to play, and no way-to-play picker. `variant` still splits
  it (a track, a difficulty, a level).
- A title that reports results is ranked by its results alone; its storage high scores only stand in for titles
  that never report results. So a title whose games all pass `game` has no entry of its own.
- Keep ids stable: renaming the prefab (or the plain id) starts a new board.

## 3. Ways to play, packages, zones (stamped for you)

From `go` the platform stamps: the content id, the attraction (the same slug storage and `dp.score` use), the way to
play running for that load (`runnerId`, `runnerName`; empty when none), the package kind (`arena`, `sequence`,
`adventure`, `playlist`, or empty when the game is placed loose), the zone ("main" or a zone id), the park, the
place, the session and the time. On each headset it also marks which entrant is that headset's player (`me`), so
the server can attach that player's account or guest identity to that entrant.

`dp.score.submit` / `fetchMine` / `fetchLeaderboard` send the way to play too, so an existing high-score board
splits per way to play. Pass `{ go = self.gameObject }` as the options (for `submit`, the 7th argument) to resolve
it from your script's own load, or `runnerId = 'x'` / `''` to override it.

## 4. CPUs

Declare the cast once with `dp.cpu.register` (CPU_PLAYERS.md), then list a CPU by id: `{ cpu = 'cpu:blue', place = 2 }`.
The result carries its name, tag ("CPU" unless you set one), colour and an https face. The session shows CPUs with
their tag. Leaderboards never show them, and they are never "you".

## 5. One round, many headsets (the merge)

Every headset that reports the same round with the same `round` value produces the same result id. The server then
merges the reports into one result: entrants are matched by id, each headset attaches its own identity to its own
`me` entrant, and nobody is ever removed.

- **Preferred: report on every headset**, with the same `round` and the same entrant list. Use a value all
  headsets agree on: the round epoch in your shared state, a `dp.shared_state` revision, or a `dp.clock()` start
  the leader rounded and shared. Never `os.time()` or a per-device counter.
- **Make `round` unique on its own.** The result id is the park, the title, the attraction, the zone and `round`
  (not the session, which one headset can learn before another). A bare counter that restarts at 1 every session
  would merge a new round into an old one: combine it with the shared start time, e.g.
  `round = state.epoch .. '-' .. state.start_ms` (or `"<epoch>-<seed>-<start>"`).
- **Or report on the leader only.** Then only the leader's player gets an identity; the others are listed by name.
- Without `round` nothing merges: each report becomes its own result.

`'me'` on headset A and A's player id on headset B are the same entrant, so the lists match even though each headset
wrote `'me'` for itself.

## 6. Examples

**A high-score game** (one player, CPUs optional):

```lua
local function game_over(score)
    if dp.results then
        dp.results.report{ go = self.gameObject, round = state.round_id, kind = 'score',
            entrants = { { player = 'me', score = score } } }
    end
end
```

**A race** with a lap-time board and two CPUs:

```lua
dp.results.report{
    go = self.gameObject, round = race.epoch, kind = 'placement', label = 'Race', variant = track.name,
    board = { stat = 'best_lap', order = 'low', unit = 'time' },
    entrants = {
        { player = 'me', place = 1, stats = { best_lap = 31200 } },
        { cpu = 'cpu:blue', place = 2, stats = { best_lap = 32950 } },
        { player = otherPid, dnf = true },
        { cpu = 'cpu:red', place = 3 },
    },
}
```

**A match** (Pong, first to 11, two sets):

```lua
dp.results.report{
    go = self.gameObject, round = match.id, kind = 'match',
    entrants = { { player = 'me', side = 1 }, { cpu = 'cpu:pong', side = 2 } },
    sides = { { score = 11, sets = { 11, 11 } }, { score = 8, sets = { 9, 8 } } },
}
```

**A co-op game** (waves survived together):

```lua
local list = {}
for _, p in ipairs(players) do list[#list + 1] = { player = p.pid, score = p.kills, side = 1 } end
dp.results.report{
    go = self.gameObject, round = run.epoch, kind = 'coop', unit = 'wave',
    entrants = list, sides = { { score = wave_reached } },
}
```

## 7. Old apps and feature checks

Guard every call with `if dp.results then`. `dp.results.features` says what this app understands:
`{ kinds = { 'score', 'placement', 'match', 'coop' }, units = true, variants = true, merge = true, games = true }`.

Reporting is fire and forget. A report made before the headset has an identity, or while it is offline, is held
(at most 16, each for 90 seconds) and sent when it can be. A report the server refuses is logged and dropped.
