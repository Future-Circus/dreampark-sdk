# Prop streaming sample (`dp.spawn_prop`)

`prop_hub_sample.lua.txt` is a hub attraction that streams two props into two slots at runtime,
only while each slot is "open": the shape of a hub whose portal slots load their portal props when
a portal opens. Each prop is its own bundle closure under Smart bundling, so only what opens downloads.

## Set it up in your content folder

1. Make two props: `P_SampleCrystal` and `P_SampleLantern` (prefabs with `PropTemplate`, any art) in
   `Assets/Content/{YourGame}/`. Or name your own and set the `propA` / `propB` strings.
2. Make (or open) a hub attraction `A_*` (`AttractionTemplate`). Add two empty children, `Slot A` and
   `Slot B`, a couple of metres apart, each with an authored stand-in (a closed frame, a plinth): the
   preview image is rendered from the prefab and must never show an empty room.
3. Add a `LuaBehaviour` to the hub root, assign `prop_hub_sample.lua.txt`, sync its variables and wire
   `slotA` / `slotB` to the two slots.
4. Press Play with the Park Simulator on. Every `openSeconds` the hub moves to the next phase: A open,
   A + B open, B open, both closed. The console prints each open / load / close; `status()` returns the
   same as a string (Playtest: `lua_eval` with code `status()`).

## What it shows

- `dp.preload_prop(name)` in `onready()`: both closures download early.
- `dp.spawn_prop(name, { parent = slot, key = 'slot:a', seat = true })`: the prop is parented into the
  slot before its `Awake`, culled and paused with the park, and keyed so its NetIds match on every
  headset. Phases come from `dp.clock()`, so every headset opens the same slot at the same moment.
- `h.despawn()` closes a slot; the attraction leaving despawns everything it spawned.
- `dp.on_prop_loaded` / `dp.on_prop_failed` report loads and errors (`not-found`, `download-failed`,
  `not-a-prop`).
- `if not dp.spawn_prop then` keeps the stand-ins on apps that predate the API.

Full contract: `Docs/CLAUDE.md` § Loading props at runtime.
