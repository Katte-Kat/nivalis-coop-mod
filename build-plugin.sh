#!/usr/bin/env bash
# Build the BepInEx plugin; with --install also copy it into <GameDir>/BepInEx/plugins/NivalisCoop.
# Needs BepInEx IL2CPP installed in the game folder and the game started once (it generates BepInEx/interop).
# NIVALIS_MOD_BUILD=1 makes the shared network library target net6.0, the runtime BepInEx runs plugins on.
set -euo pipefail
: "${NIVALIS_GAME_DIR:?set NIVALIS_GAME_DIR to the Nivalis Nights game folder (BepInEx installed, game started once)}"
export NIVALIS_GAME_DIR
export NIVALIS_MOD_BUILD=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
args=(-c Release)
if [[ "${1:-}" == "install" || "${1:-}" == "--install" ]]; then args+=(-p:Install=true); shift; fi
exec dotnet build "$(dirname "$0")/NivalisCoop.Plugin" "${args[@]}" "$@"
