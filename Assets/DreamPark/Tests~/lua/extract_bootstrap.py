#!/usr/bin/env python3
"""Pull the Lua bootstrap block (the C# verbatim string) out of
DreamParkLuaAPI.cs so the harnesses run the EXACT text that ships.

  python3 extract_bootstrap.py [path/to/DreamParkLuaAPI.cs] [out.lua]
"""
import os, sys

here = os.path.dirname(os.path.abspath(__file__))
src = sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, "..", "..", "Scripts", "Core", "DreamParkLuaAPI.cs")
out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(here, "dp_bootstrap.lua")

text = open(src, encoding="utf-8").read()
start = text.index('env.DoString(@"') + len('env.DoString(@"')
end = text.index('", "dp.api.bootstrap");', start)
block = text[start:end]

# Inside a verbatim string, "" would be an escaped quote. The block is written
# to contain none at all (string.char(34) is the idiom); enforce that here so
# a stray quote fails the suite instead of shipping.
if '"' in block:
    sys.exit("FAIL: double quote inside the dp.api.bootstrap verbatim block")

open(out, "w", encoding="utf-8").write(block)
print("extracted %d chars -> %s" % (len(block), out))
