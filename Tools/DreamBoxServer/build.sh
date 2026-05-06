#!/usr/bin/env bash
# Build self-contained single-file DreamBoxRelay binaries for all dev platforms.
#
# Usage:
#   ./build.sh                  # build all platforms
#   ./build.sh osx-arm64        # build one specific platform
#   ./build.sh osx-arm64 win-x64
#
# Output: dist/<rid>/DreamBoxRelay (or DreamBoxRelay.exe on Windows)
#         plus the adjacent config/ and wwwroot/ folders.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/DreamBoxRelay.csproj"
OUT_ROOT="$SCRIPT_DIR/dist"

ALL_RIDS=(win-x64 osx-arm64 osx-x64 linux-x64)

if [[ $# -gt 0 ]]; then
  RIDS=("$@")
else
  RIDS=("${ALL_RIDS[@]}")
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "error: 'dotnet' not found on PATH." >&2
  echo "install the .NET 9 SDK from https://dotnet.microsoft.com/download/dotnet/9.0" >&2
  exit 1
fi

echo "dotnet: $(dotnet --version)"
echo "building: ${RIDS[*]}"
echo "output:   $OUT_ROOT"
echo

for rid in "${RIDS[@]}"; do
  out="$OUT_ROOT/$rid"
  echo "→ publishing $rid..."
  rm -rf "$out"
  dotnet publish "$PROJECT" \
    -c Release \
    -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -o "$out" \
    --nologo \
    --verbosity quiet
  echo "   done: $out"
done

echo
echo "all builds complete."
