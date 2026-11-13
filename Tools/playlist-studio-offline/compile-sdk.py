#!/usr/bin/env python3
"""Offline compile check of the WHOLE SDK (Assembly-CSharp + Assembly-CSharp-Editor) without opening Unity:
reuses Unity's own last compile response files (Library/Bee/artifacts/*.dag/*.rsp, so defines and references are
exactly the editor's) with the source lists refreshed from disk. Output outside the repo; exit 0 = zero errors.
  python3 Tools/playlist-studio-offline/compile-sdk.py [extra.cs ...]   (extra files compile against the editor dll)"""
import glob, os, subprocess, sys
SDK = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
OUT = os.path.expanduser('~/Library/Caches/dreampark-playlist-studio/sdkcompile'); os.makedirs(OUT, exist_ok=True)
CSC = '/Applications/Unity/Hub/Editor/6000.3.23f1/Unity.app/Contents/Resources/Scripting/DotNetSdkRoslyn/csc.dll'
os.chdir(SDK)
dags = sorted(glob.glob('Library/Bee/artifacts/*.dag/Assembly-CSharp-Editor.rsp'), key=os.path.getmtime)
if not dags: sys.exit('no Unity rsp files: open the project in Unity once')
DAG = os.path.dirname(dags[-1])
asmdef_dirs = [r for r, _, fs in os.walk('Assets') if any(f.endswith(('.asmdef', '.asmref')) for f in fs)]
owned = lambda p: any(p.startswith(d + '/') for d in asmdef_dirs)
special = lambda p: p.startswith(('Assets/Plugins/', 'Assets/Standard Assets/')) or '~/' in p or '/.' in p
allcs = [os.path.join(r, f) for r, _, fs in os.walk('Assets') for f in fs if f.endswith('.cs')]
allcs = [p for p in allcs if not owned(p) and not special(p)]
bad = 0
for name, editor in (('Assembly-CSharp', False), ('Assembly-CSharp-Editor', True)):
    lines = open(os.path.join(DAG, name + '.rsp')).read().splitlines()
    old = [l.strip('"') for l in lines if l.endswith('.cs"')]
    keep = [l for l in lines if not l.endswith('.cs"') and not l.startswith(('-out', '-refout', '/additionalfile', '-generatedfilesout')) and 'analyzer' not in l.lower()]
    keep = [('-r:"%s/Assembly-CSharp.ref.dll"' % OUT) if 'Assembly-CSharp.ref.dll' in l else l for l in keep]
    srcs = sorted(set(p for p in allcs if ('/Editor/' in p) == editor) | set(p for p in old if os.path.exists(p)))
    rsp = os.path.join(OUT, name + '.rsp')
    open(rsp, 'w').write('\n'.join(keep + ['-out:"%s/%s.dll"' % (OUT, name), '-refout:"%s/%s.ref.dll"' % (OUT, name),
        '-nowarn:CS0618,CS0649,CS0414,CS0169,CS0067,CS0162,CS0219,CS0168'] + ['"%s"' % s for s in srcs]) + '\n')
    r = subprocess.run(['dotnet', CSC, '@' + rsp], capture_output=True, text=True)
    errs = sorted(set(l for l in r.stdout.splitlines() if ' error ' in l))
    print(name, 'sources', len(srcs), 'errors', len(errs)); [print('  ', e) for e in errs[:40]]
    bad += len(errs)
    if errs: break
if not bad and len(sys.argv) > 1:
    lines = open(os.path.join(OUT, 'Assembly-CSharp-Editor.rsp')).read().splitlines()
    keep = [l for l in lines if not l.endswith('.cs"') and not l.startswith(('-out', '-refout'))]
    rsp = os.path.join(OUT, 'extra.rsp')
    open(rsp, 'w').write('\n'.join(keep + ['-r:"%s/Assembly-CSharp-Editor.dll"' % OUT, '-out:"%s/Extra.dll"' % OUT] + ['"%s"' % s for s in sys.argv[1:]]) + '\n')
    r = subprocess.run(['dotnet', CSC, '@' + rsp], capture_output=True, text=True)
    errs = sorted(set(l for l in r.stdout.splitlines() if ' error ' in l))
    print('extra', len(sys.argv) - 1, 'files, errors', len(errs)); [print('  ', e) for e in errs[:20]]
    bad += len(errs)
sys.exit(1 if bad else 0)
