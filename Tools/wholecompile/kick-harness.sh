#!/bin/bash
# Run Tools/Builders/KickHarness.cs OUTSIDE Unity (pure maths: no Editor, no Play mode,
# safe while other agents hold the Editor). Needs a fresh editor whole-compile first:
#   WC_TAG=<tag> Tools/wholecompile/wc.sh E && WC_TAG=<tag> Tools/wholecompile/kick-harness.sh
# Exit 0 = every check passed. In the Editor the same file runs with
#   Tools/agent/u.sh run_script --file Tools/Builders/KickHarness.cs --entry KickHarness.Run
set -e
D=$(cd "$(dirname "$0")" && pwd); ROOT=$(cd "$D/../.." && pwd)
E=$HOME/Library/Caches/dreampark-wholecompile/${WC_TAG:-default}/E
OUT=$E/kick-harness; mkdir -p "$OUT"
[ -f "$E/Assembly-CSharp.dll" ] || { echo "run WC_TAG=${WC_TAG:-default} Tools/wholecompile/wc.sh E first"; exit 2; }
cat > "$OUT/Main.cs" <<'CS'
public static class KickHarnessMain {
    public static int Main() {
        string r = KickHarness.Run(System.Environment.GetEnvironmentVariable("KICK_OUT"));
        System.Console.Write(r);
        return r.Contains(", 0 failed") ? 0 : 1;
    }
}
CS
{ grep -E '^-r:' "$E/ac.rsp"; echo "-r:\"$E/Assembly-CSharp.dll\""; echo "-target:exe"; echo "-nostdlib+"; echo "-langversion:9.0"; echo "-nowarn:CS1701,CS1702"
  echo "-out:\"$OUT/KickHarness.exe\""; echo "\"$ROOT/Tools/Builders/KickHarness.cs\""; echo "\"$OUT/Main.cs\""; } > "$OUT/t.rsp"
dotnet "/Applications/Unity/Hub/Editor/6000.3.23f1/Unity.app/Contents/Resources/Scripting/DotNetSdkRoslyn/csc.dll" @"$OUT/t.rsp" > "$OUT/build.txt" 2>&1 || { grep 'error' "$OUT/build.txt" | head -20; exit 3; }
# Every referenced managed assembly beside the exe, so the runtime can bind them.
grep -E '^-r:' "$E/ac.rsp" | sed -e 's/^-r:"//' -e 's/"$//' | grep -v '/ref/\|/shims/' | while read -r f; do cp -n "$f" "$OUT/" 2>/dev/null || true; done
cp "$E/Assembly-CSharp.dll" "$OUT/"
FX=$(dotnet --list-runtimes | awk '/Microsoft.NETCore.App/{v=$2} END{print v}')
printf '{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"%s"},"rollForward":"LatestMajor"}}' "$FX" > "$OUT/KickHarness.runtimeconfig.json"
dotnet exec --runtimeconfig "$OUT/KickHarness.runtimeconfig.json" "$OUT/KickHarness.exe"
