# Unity CLI

Driving the Unity Editor from a shell. How agents share or split Editors and run play sessions: `Tools/agent/PLAYBOOK.md`.
`§ Name` points to a section of these docs; the table at the end of `Assets/DreamPark/Docs/CLAUDE.md` maps every section to its file.

## Unity CLI
The template uses Unity's standard `com.unity.pipeline` package, so an agent can drive the open Editor from a
shell with the `unity` CLI. Always aim it at THIS project:

```
unity command --project-path . <command> [--param value ...]
unity command --project-path . --timeout 180 <long command> ...   # the CLI default is 30 s
```

**Drive the Editor through the CLI**, never through screen control or computer use:

| Need | How |
|---|---|
| Any Unity command | `unity command --project-path . <cmd>`. While the Editor reloads (domain reload, entering Play mode) the command cannot connect: wait a few seconds and retry. A **timeout** is different: the operation may still run, so check its result before retrying. |
| Prefab/scene/asset generation, editor harnesses | A builder in `Tools/Builders/*.cs`, run with `unity command --project-path . run_script --file Tools/Builders/X.cs --entry X.Build`: compiled in memory in <1 s, no domain reload, full `UnityEditor` API. Keep builders idempotent; rebuild only what changed. |
| Screenshots | `unity command --project-path . capture_game_view --format json` (or `capture_scene_view`) returns the PNG as base64; decode it to a file outside `Assets/`. Never pass `--save_path` (it lands under `Assets/`). |
| Checking Lua | The Lua surface gate (§ The Lua surface gate) runs on every upload and player build. Play the change before calling it done. |

- **Play session:** `unity command --project-path . editor_play` → test → `editor_stop`. Only one agent may be in Play mode (or open scenes) in an Editor at a time; how agents get their Editor is in `Tools/agent/PLAYBOOK.md`. Keep it under 5 min; always stop, even on failure.
- **Never add C# under `Assets/` during active work**: it triggers a domain reload (~20 s) for everyone using that Editor. Harnesses and generators are builders in `Tools/Builders/`. Staging folders must end in `~`.
- **Never save, revert or discard a scene you did not open.** Commands that load scenes (`run_tests`, `open_scene`, builds) raise the modal "Scene(s) Have Been Modified" when a scene is dirty; use your own test scene, and run pure-logic tests offline.
- **A modal dialog blocks every command** ("Main thread operation timed out"): report it with its exact text at once; don't retry in a loop. `eval --timeout` is in milliseconds.
- **Tag your log lines** with your track and filter `get_console_logs` by it. Keep scratch files in a per-agent folder, never under `Assets/`.

Meta XR SDK 205's experimental Agent Bridge/DevAgent is separate and must remain disabled.
`DreamParkOpenXRSetup` clears its machine-local address and token during setup and immediately
before/after builds so those values cannot enter shipped content.
