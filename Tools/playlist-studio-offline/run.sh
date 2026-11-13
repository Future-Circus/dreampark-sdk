#!/bin/bash
# Editor-free tests of the SDK PLAYLIST STUDIO (Oct 18 2026): generator post-processing (validate,
# de-dupe, repair, fit filter), Arena/Sequence/Adventure → playlist migration, the fit matrix with the
# SHARED core planner, and the authoring JSON round trip. Builds OUTSIDE the repo; exit 0 = all pass.
#   Tools/playlist-studio-offline/run.sh
# Also checks the shared planner copy is current when dreampark-core sits next to this checkout.
set -e
D=$(cd "$(dirname "$0")" && pwd); SDK=$(cd "$D/../.." && pwd)
CORE=${DREAMPARK_CORE:-$(cd "$SDK/.." && pwd)/dreampark-core}
if [ -x "$CORE/Tools/playlist-pack-offline/sync-sdk-packer.sh" ]; then
  DREAMPARK_SDK="$SDK" "$CORE/Tools/playlist-pack-offline/sync-sdk-packer.sh" --check || echo "WARNING: the shared planner copy is stale (core changed): run dreampark-core/Tools/playlist-pack-offline/sync-sdk-packer.sh"
fi
B=$HOME/Library/Caches/dreampark-playlist-studio/harness; mkdir -p "$B"
rm -f "$B"/Program*.cs; cp "$D"/Program*.cs "$D/Stubs.cs" "$D/PlaylistStudioOffline.csproj" "$B/"
cd "$B" && dotnet run -c Release -p:SDK="$SDK" -- "$SDK" "$D/fixtures" "${OUT:-$D/out}" "$@"
