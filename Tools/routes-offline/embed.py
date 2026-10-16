#!/usr/bin/env python3
"""Embed dp_routes.lua + dp_routes_unity.lua into Assets/DreamPark/Scripts/Core/DreamParkLuaRoutes.cs.
   python3 Tools/routes-offline/embed.py [--check]   (--check: exit 1 if the .cs is stale)"""
import os, re, sys
here = os.path.dirname(os.path.abspath(__file__))
root = os.path.abspath(os.path.join(here, '..', '..'))
cs = os.path.join(root, 'Assets/DreamPark/Scripts/Core/DreamParkLuaRoutes.cs')
src = open(cs, encoding='utf-8').read()
out = src
for name in ('dp_routes.lua', 'dp_routes_unity.lua'):
    lua = open(os.path.join(here, name), encoding='utf-8').read()
    if '"' in lua:
        line = [i + 1 for i, l in enumerate(lua.split('\n')) if '"' in l]
        sys.exit(f'{name}: double quotes not allowed (C# verbatim string), lines {line}')
    pat = re.compile(r'/\*BEGIN ' + re.escape(name) + r'\*/@".*?"/\*END ' + re.escape(name) + r'\*/', re.S)
    if not pat.search(out): sys.exit(f'markers for {name} not found')
    out = pat.sub(lambda m: '/*BEGIN ' + name + '*/@"' + lua + '"/*END ' + name + '*/', out)
if '--check' in sys.argv:
    print('up to date' if out == src else 'STALE'); sys.exit(0 if out == src else 1)
if out != src:
    open(cs, 'w', encoding='utf-8').write(out); print('embedded into', cs)
else:
    print('already up to date')
