#!/usr/bin/env bash
# Build the release DLL into dist/ and print its sha256 (for the NOMNOM manifest).
#   ./build.sh                      # uses ../raw/game (the planner repo's copy of the game files)
#   GAME_DIR="/path/to/Nuclear Option" ./build.sh
set -euo pipefail
cd "$(dirname "$0")"
DOTNET="$(command -v dotnet || echo "$HOME/.dotnet/dotnet")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
ARGS=(-c Release -o dist --nologo -v quiet)
if [ -n "${GAME_DIR:-}" ]; then ARGS+=("-p:GameDir=$GAME_DIR"); fi
"$DOTNET" build NOPlannerLink/NOPlannerLink.csproj "${ARGS[@]}"
rm -f dist/*.pdb dist/*.deps.json
"$DOTNET" test LinkCore.Tests --nologo -v quiet
echo "dist/NOPlannerLink.dll  sha256:$(shasum -a 256 dist/NOPlannerLink.dll | cut -d' ' -f1)"
