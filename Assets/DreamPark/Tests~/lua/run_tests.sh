#!/bin/sh
# SDK Lua test suite. Runs the EXACT bootstrap text embedded in
# DreamParkLuaAPI.cs (extracted, then parsed and exercised) against fake
# Unity/relay implementations. Needs python3 and lua5.4 (or lua5.3).
#   cd Assets/DreamPark/Tests~/lua && ./run_tests.sh
set -e
cd "$(dirname "$0")"

LUA=""
for candidate in lua5.4 lua5.3 lua; do
    if command -v "$candidate" >/dev/null 2>&1; then LUA="$candidate"; break; fi
done
[ -n "$LUA" ] || { echo "no lua interpreter found (need lua5.3+)"; exit 1; }

python3 extract_bootstrap.py
echo "== syntax =="
$LUA -e "assert(loadfile('dp_bootstrap.lua')); print('  dp.api.bootstrap parses ($LUA)')"
echo "== typed wire (dp.pack / dp.unpack across two parks) =="
$LUA typed_wire_harness.lua dp_bootstrap.lua
echo "== peers (dp.peers / on_peer_join / state bags) =="
$LUA peers_harness.lua dp_bootstrap.lua
echo "== gestures + device (dp.on_tap / on_touch / on_drag / on_pinch, dp.device) =="
$LUA gestures_harness.lua dp_bootstrap.lua
echo "== device time (dp.time.now / utc / hour / format / debug_set) =="
python3 extract_bootstrap.py ../../Scripts/Core/DreamParkLuaTime.cs dp_time_bootstrap.lua dp.time.bootstrap
$LUA time_harness.lua dp_time_bootstrap.lua
echo "== room light (dp.light.ambient / direction / confidence / live / debug_set) =="
python3 extract_bootstrap.py ../../Scripts/Core/DreamParkLuaLight.cs light_bootstrap.lua dp.light.bootstrap
$LUA light_harness.lua light_bootstrap.lua
echo "ALL SUITES PASSED"
