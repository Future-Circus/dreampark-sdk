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
echo "ALL SUITES PASSED"
