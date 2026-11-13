# Agent guide changelog

Dated notes moved out of the agent guide (`CLAUDE.md`) so game agents load only current rules. Not shipped to games.

## 2026-11-11: one attraction, one size (12 × 18 ft)

"One attraction, every size" (support rooms from 6 × 10 ft up with layout frames: a Minimum frame, custom frames, grow
limits; "if a size gap exists, add a frame") became "One attraction, one size: 12 × 18 ft" (director-approved): every
attraction is one Base layout for a 12 × 18 ft room with no frames, and the shrink/grow bake covers the rooms around it.
The frame system stays for existing content. Updated: the guide, ATTRACTIONS.md (new § One attraction, one size; § Layout
Frames marked legacy), PACKAGES.md (rule, legacy frames subsection, Arena/Sequence wording), LAYOUT_FRAMES_SPEC.md status.
The "never ship size duplicates as separate attractions" rule is kept. Pre-upload checks and inspector tools were not
changed yet.

## 2026-11-11: authored-state rule replaced

"The attraction prefab IS the attraction" ("do not spawn the contents of an attraction at runtime ... must not conjure
them") became "Author the stage and one of every actor. Instantiate the crowd." (director-approved): the prefab holds the
stage, one exemplar of every actor and the fixed content the game counts; waves, projectiles, traffic, pickups and every
particle effect are instantiated and destroyed. The old wording made agents pre-place big hidden pools (ArcadeLand's Toad
Road ~750 nested copies), which slowed attraction loading. ATTRACTIONS.md § The attraction prefab, the guide summary and
SHADERS.md (particles) were updated; new Warning check `hidden-pools` (more than 8 hidden copies of one thing under one
parent of an attraction).

## 2026-11-11: guide split

The 143k-char `CLAUDE.md` became a short game agent guide plus reference docs in `Assets/DreamPark/Docs/`
(UNITY_CLI, ATTRACTIONS, PACKAGE_RULES, SHADERS, PLAYERS, PORTALS, PLAYER_DATA, LUA_API, LUA_RUNTIME,
MULTIPLAYER_RULES, PLAYTESTER, SHIPPING), `CREATOR_ONBOARDING.md` and `SDK_MAINTAINERS.md`. Section text moved
nearly verbatim; dated headings were kept in the reference docs.

## When sections of the guide landed

| Date | Section |
|---|---|
| Sept 2026 | Layout Frames (Sept 2026) |
| Oct 2026 | Wall Layers (Oct 2026) |
| Sept 2026 | Entries & Exits (Sept 2026) |
| Oct 2026 | Routes: walking between attractions (Oct 2026, Phase 1) |
| Sept 2026 | Packages: Arena, Sequence & Adventure (Sept 2026) |
| Oct 2026 | Phone players & gestures (Oct 2026) |
| Oct 2026 | Hand-held objects (Oct 2026) |
| Oct 2026 | Portals: a window into another place (Oct 2026) |
| Oct 2026 | Badges: where they're earned — auto links + manual `earnAt` (Oct 2026) |
| Oct 2026 | Items (catalog + per-instance metadata, Oct 2026) |
| Sept 2026 | AI Playtester — play the game before you call it done (Sept 2026) |
| Oct 2026 | Multiplayer (Oct 2026: the platform does the netcode) |
| Nov 2026 | Loading props at runtime (Nov 2026) |
| Oct 2026 | Device time: `dp.time` (Oct 2026, `DreamParkLuaTime.cs`) |
| Oct 2026 | Room light: `dp.light` (Oct 2026, `DreamParkLuaLight.cs`) |
| July 2026 | What the optimizer guarantees (July 2026) |
| Sept 2026 | XLua `Gen/` is SDK-owned — updates replace it, never merge it (Sept 2026) |
| Oct 2026 | Platform notes (Oct 2026) |

## Platform notes (Oct 2026)
- **Overhead depth occlusion.** `DepthMaskCeiling` builds a dome mask over the player and is the ONLY writer of the global `EnvironmentDepthManager.MaskBias`. **Content must not write `MaskBias`**: a game that does breaks overhead occlusion park-wide. Lift a floor mask a few cm instead.
- **`dp.on_occurrence_removed(fn[, owner])`** → `fn({occurrence_id, resource, anchor})` when ONE attraction of a package is removed while the title stays loaded (an Arena rect set to "No Attraction", a swap, a repack that retires a rect), BEFORE its anchor is destroyed. Returns a handle (`h.off()` / `dp.off_occurrence_removed(h|fn)`); inside a script it is scoped to that script's package and dropped with the script. Still clean up in `ondisable`/`ondestroy`, and call it guarded: `if dp.on_occurrence_removed then dp.on_occurrence_removed(fn) end`. See PACKAGES.md.
- **The Lua pump never stops.** One title's Lua error no longer disables `dp.on_tap`/`on_touch`/peer/`shared_state` dispatch for every title; it logs `[DreamParkLuaPump] <name> threw (pump keeps running)` at most once per 10 s. Fix the error it names.
- **Compiled-chunk cache.** `LuaBehaviour` compiles each TextAsset once and runs it per scope instead of per instance. A file that can't be compiled that way falls back to the old path.
- **Sequence minimum room.** The Overlay and Game Over levels no longer raise a Sequence's `minimumRoomMeters` (they still count toward the maximum). Recompile and upload a title to pick it up.
- **Sessions.** A solo device that connects to a lone, YOUNGER room keeps its game (clock, seed, shared state, physics bodies) and the room adopts it; an established room still wins. Leader order is synced → headset → seniority → id. `RemoteRig.RegisterTemplate` also re-arms peers that are already built. LAN sessions recover on their own from network changes, a paused or restarted host, and a rebinding discovery listener.
