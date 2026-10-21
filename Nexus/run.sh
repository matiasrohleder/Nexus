#!/usr/bin/env bash
# Launch Nexus (builds if needed)
cd "$(dirname "$0")"
if [ -x "./dist/Nexus" ]; then exec ./dist/Nexus "$@"; fi
exec dotnet run -c Release "$@"
