#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../runtime/game"
exec bash ./start_game_bepinex.sh -screen-width 960 -screen-height 540 -screen-fullscreen 0 "$@"
