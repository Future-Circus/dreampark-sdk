# SDK Lua tests

The multiplayer surface of `dp.*` lives as a Lua bootstrap embedded in
`Scripts/Core/DreamParkLuaAPI.cs` (a C# verbatim string). These harnesses run
that EXACT text — extracted, never copied — against fake Unity maths and a
fake presence backend, so the thing tested is the thing that ships.

```
./run_tests.sh          # needs python3 + lua5.4 (or 5.3)
```

| file | covers |
| --- | --- |
| `extract_bootstrap.py` | pulls the verbatim block out of DreamParkLuaAPI.cs; fails on any `"` inside it (the block must use `string.char(34)`) |
| `typed_wire_harness.lua` | `dp.pack` / `dp.unpack` / `dp.dir`: every Unity value goes out park-local and lands at the physically identical spot on a headset whose park sits at a different world pose; identity without a park |
| `peers_harness.lua` | `dp.me` / `dp.peers` / `dp.peer` / `dp.set_state` / `dp.on_peer_join` / `dp.on_peer_leave`: state bags unpack to Unity values, views are cached not rebuilt, join fires for players already present then per arrival (never twice), leave/rejoin re-fires, throwing handlers are contained, owner-bound handlers drop when their owner dies, `off_*` unregisters |
| `time_harness.lua` | `dp.time` (`DreamParkLuaTime.cs`, chunk `dp.time.bootstrap`): `now` / `utc` field names (os.date's + `ms`, `utc_offset`, `unix`), `hour()` as a float, `format` argument handling, `simulated` / `debug_set(nil)`, false on a device, merges into an existing `dp` |
| `light_harness.lua` | `dp.light` (`DreamParkLuaLight.cs`, chunk `dp.light.bootstrap`, extracted to `light_bootstrap.lua`): fallback (ambient 1, the scene sun, confidence 0, not live), live pass-through, direction a unit `Vector3`, every read but `simulated()` stamps the measure request, `debug_set` (Vector3 / `{x, y, z}` / nil / refused values / false on a device), and the two documented examples (hidden object with hysteresis, sundial) |
| `gestures_harness.lua` | `dp.device` / `dp.is_phone` / `dp.is_headset` / `dp.device_of` passthrough; `dp.on_tap` / `on_touch` / `on_drag` / `on_pinch`: routing by kind, phases in order, handle `off()` and `off_*` by handle or fn, owner pruning, throwing handlers contained, events drained with no listener |

The folder is `Tests~`, so Unity never imports it. Game-level integration
harnesses live with their game (e.g. `Content/MyGame/Tests~/`).
