# AI Playtester

A simulated guest that plays your content in the Editor.
`§ Name` points to a section of these docs; the table at the end of `Assets/DreamPark/Docs/CLAUDE.md` maps every section to its file.

## AI Playtester — play the game before you call it done (Sept 2026)
A simulated guest that plays content in Editor Play mode: it walks, turns, crouches, reaches/touches/strikes with either hand and steps on pads, then reports what it caused. Source: `Assets/DreamPark/Scripts/Features/Playtest/` (runtime, `#if UNITY_EDITOR` — never in a build) and `Assets/DreamPark/Editor/Playtest/`.

- **How it drives the player**: `PlaytestAvatar` disables the Scene-view `Simulator` and writes `Camera.main` + `Left/RightHandAnchor` itself (order 32000, re-applied in LateUpdate/onBeforeRender). Everything keys off those — trackers, GameArea presence, `dp.head()/dp.hand()` — so content needs no changes. It adds kinematic probe colliders (hand spheres, floor-to-eye body capsule) parented under the active `PlayerRig`, so `dp.is_player` is true by ancestry.
- **What it sees**: `PlaytestObserver` — console + Lua errors/prints, zone changes, storage writes, probe contacts (as a per-action delta), plus snapshots: nearby interactables with short ids (`e12`), tags, live Lua scope values and function names, which hand each rig script rides on, world TMP text, head/overhead screenshots. `lua_eval` runs code in an object's script scope (read state; don't mutate unless setting up a test).
- **Game time is frozen between actions** (`PlaytestClock`), so thinking time never costs the player.
- **Built-in agent**: `DreamPark → AI Playtester` (Claude via the Messages API; key from `ANTHROPIC_API_KEY` or the window, stored in EditorPrefs only). It reads a briefing — `Assets/Content/{id}/PLAYTEST.md` (the creator's spec, optional), every prefab's wired scripts and collider tags, the Lua source, the package JSON — plays, files issues, optionally patches `.lua.txt` in the content folder and restarts Play mode to re-verify, and writes `Logs/DreamParkPlaytest/<run>/report.md` with screenshots. The run survives Play-mode domain reloads via `Library/DreamParkPlaytest/session.json`.
- **Driving it yourself from the Unity CLI** (for agents authoring content — build, play, fix, replay), one agent in Play mode at a time:
  ```
  unity command --project-path . editor_play
  unity command --project-path . dreampark_playtest_attach                 # snapshot + screenshot path
  unity command --project-path . dreampark_playtest_tools                  # action schemas
  unity command --project-path . --timeout 180 dreampark_playtest_act --tool travel_to --args '{"target":"A_MyAttraction"}'
  unity command --project-path . --timeout 180 dreampark_playtest_act --tool reach --args '{"hand":"left","target":"e12","style":"touch"}'
  unity command --project-path . dreampark_playtest_act --tool lua_eval --args '{"target":"e25","code":"get_points()"}'
  unity command --project-path . dreampark_playtest_detach                 # un-freezes time, restores the Simulator
  unity command --project-path . editor_stop
  ```
  Or hand off a whole run: `dreampark_playtest_run --goal "..." [--allow_fixes true]`, then poll `dreampark_playtest_status`. Walks are real-time — pass `--timeout` (the CLI default is 30 s) or use `travel_to` between distant attractions. Read the returned screenshot paths; the images are the ground truth.
- **Gotchas it surfaces**: a touch only counts through colliders the game put on the rig — observe's PLAYER SYSTEMS says which hand each rides (a `BothHands` tracker follows the LEFT hand in the Editor). The Editor spawns the guest wherever the Scene camera was, often nowhere near content — `travel_to` first.
