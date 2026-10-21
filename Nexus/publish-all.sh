#!/usr/bin/env bash
# Build self-contained Nexus binaries for Windows, macOS (Intel + Apple Silicon) and Linux.
# Self-contained = the target machine does NOT need .NET installed.
# Run from the Nexus project directory. Cross-compiles fine from any OS.
set -e
cd "$(dirname "$0")"

RIDS=(win-x64 osx-arm64 osx-x64 linux-x64)

for rid in "${RIDS[@]}"; do
  echo ">> Publishing $rid ..."
  dotnet publish -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "dist/$rid"
done

echo
echo "Done. Binaries:"
echo "  Windows : dist/win-x64/Nexus.exe"
echo "  macOS   : dist/osx-arm64/Nexus  (Apple Silicon) | dist/osx-x64/Nexus (Intel)"
echo "  Linux   : dist/linux-x64/Nexus"
