#!/usr/bin/env python3
"""Pull the Lua bootstrap block (the C# verbatim string) out of
DreamParkLuaAPI.cs so the harnesses run the EXACT text that ships.

  python3 extract_bootstrap.py [path/to/DreamParkLuaAPI.cs] [out.lua] [chunk name]

The chunk name (default dp.api.bootstrap) picks the last verbatim block
before `", "<name>");`, so other bootstraps (dp.time.bootstrap in
DreamParkLuaTime.cs) come out the same way.
"""
import os, sys

here = os.path.dirname(os.path.abspath(__file__))
src = sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, "..", "..", "Scripts", "Core", "DreamParkLuaAPI.cs")
out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(here, "dp_bootstrap.lua")

name = sys.argv[3] if len(sys.argv) > 3 else "dp.api.bootstrap"

text = open(src, encoding="utf-8").read()
end = text.index('", "%s");' % name)
start = text.rindex('env.DoString(@"', 0, end) + len('env.DoString(@"')
block = text[start:end]

# Inside a verbatim string, "" would be an escaped quote. The block is written
# to contain none at all (string.char(34) is the idiom); enforce that here so
# a stray quote fails the suite instead of shipping.
if '"' in block:
    sys.exit("FAIL: double quote inside the %s verbatim block" % name)

open(out, "w", encoding="utf-8").write(block)
print("extracted %d chars -> %s" % (len(block), out))
