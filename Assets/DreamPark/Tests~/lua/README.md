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

The folder is `Tests~`, so Unity never imports it. Game-level integration
harnesses live with their game (e.g. `Content/LaserTag/Tests~/`).
