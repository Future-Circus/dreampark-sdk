# C# → XLua Feasibility Linter — Prototype Report

**What this is:** the deterministic pre-pass for the proposed AI transpiler. Before any LLM touches a script, this classifies whether the current LuaBehaviour surface can even host it, and emits the exact reasons it can't. Run: `python3 lint_lua_feasibility.py <file-or-dir> [--md report.md] [--json out.json]`.

## Classifications

- ✅ **CLEAN** — maps 1:1 onto today's Lua surface; safe to hand to the LLM body-translator.
- 🟡 **CLEAN_WARN** — translatable, but has findings to resolve first (usually AOT config additions).
- 🟠 **NEEDS_RUNTIME** — blocked on SDK runtime work: a missing message relay, delegate signature, injection type, or the coroutine bridge.
- 🔴 **KEEP_CSHARP** — not a sensible transpile target (editor code, threading/async, SDK internals, non-MonoBehaviour).

## Results on this repo (127 scripts: Assets/DreamPark/Scripts + Assets/Content)

CLEAN=4, CLEAN_WARN=16, NEEDS_RUNTIME=12, KEEP_CSHARP=91. Full per-file findings in `SAMPLE_OUTPUT.md` / `sample_output.json`.

Caveat: this corpus is SDK-internal code, so KEEP_CSHARP dominating is the linter working correctly, not bad news. The tool's real target is `Assets/Content/{GameName}/Scripts/` in creator forks — this repo's template has essentially none yet. The gameplay-shaped SDK scripts stand in as a proxy for creator code, and there the signal is encouraging: simple interaction scripts (`CollideAudio`, `RealisticRolloff`, `EasySpawn`, trackers) come out CLEAN or CLEAN_WARN.

## Representative findings (spot-checked by hand)

- **`CollideAudio` → CLEAN.** `AudioClip` field maps to `audioClipInjections`; its `OnValidate` lives in `#if UNITY_EDITOR` and is correctly dropped. The linter initially misclassified this KEEP_CSHARP off the `using UnityEditor` — fixed by stripping `#if UNITY_EDITOR` regions before analysis. Expect this pattern to be common in creator code.
- **`EasyCounter` → NEEDS_RUNTIME.** Two `enum` inspector fields — no enum injection type exists. Cheap fix (int + named constants, or add an enum injection), and the linter says exactly that.
- **`Interactable` → NEEDS_RUNTIME.** `string[]` fields (only `GameObject[]` is injectable) and `UnityEvent<CollisionWrapper>` (unsupported delegate signature), plus a `LayerMask` AOT warning. A converter that silently dropped these would produce a script that half-works — exactly what the pre-pass exists to prevent.
- **`Reset` → NEEDS_RUNTIME.** Uses `OnApplicationQuit`, which has no relay. One-line runtime addition if wanted.

## What the sweep says about SDK runtime priorities

Tallying RUNTIME findings across the repo, the highest-leverage additions to widen the transpile target, in order: (1) more injection types — enum, `Vector2`, `LayerMask`, and non-GameObject arrays account for most NEEDS_RUNTIME hits; (2) a coroutine bridge (`util.cs_generator`) — `IEnumerator` patterns are everywhere in ordinary gameplay code; (3) a few more message relays (`OnApplicationQuit`, mouse/2D variants matter less for XR). Each also benefits hand-written Lua, so this work isn't transpiler-only.

## Known blind spots (prototype honesty)

This is regex-based; production should be a Roslyn analyzer in the editor. Specifically not handled: semantic type resolution (a field of a custom `[Serializable]` class is flagged "unknown type" rather than resolved), event delegate signatures are warned generically rather than checked against the actual event type, method-call graphs (a helper class the script calls isn't followed), `#if UNITY_EDITOR` with `#elif`, and property accessors with logic. The `unknown-on-method` warn on plain methods named `On*` (e.g. `EasyCounter.OnEvent`) is a deliberate false-positive-over-false-negative choice.

## Where this fits in the transpiler pipeline

1. **This linter** gates and annotates (Roslyn version, run from an editor window).
2. Deterministic field migration: `[SerializeField]`s → `@var` header + injection arrays, prefab/scene YAML rewired — no LLM involved.
3. LLM translates method bodies only, with the linter's findings in the prompt (e.g. "OnValidate dropped; AudioClip arrives as injected `audioClip`").
4. AOT check: every referenced type either in `DreamParkLuaConfig.LuaCallCSharp` or flagged before build.
5. Side-by-side Editor verification before the C# component is swapped out.
