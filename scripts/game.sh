#!/usr/bin/env bash
# Launch the modded client. With a server configured in .env he selects his character
# and joins by himself; without one this drops at the main menu as before.
set -euo pipefail
cd "$(dirname "$0")/.."
[[ -f .env ]] && { set -a; source .env; set +a; }

JOIN=()
if [[ -n "${VALHEIM_SERVER:-}" ]]; then
  # Valheim's own argument: it picks the character AND joins. It requires three further
  # arguments to follow it, which the screen settings below satisfy.
  JOIN+=(-joinserverwithcharacter "$VALHEIM_SERVER" "${VALHEIM_CHARACTER:-Bjorn}")
  [[ -n "${VALHEIM_PASSWORD:-}" ]] && JOIN+=(-password "$VALHEIM_PASSWORD")
  echo "joining ${VALHEIM_SERVER} as ${VALHEIM_CHARACTER:-Bjorn}" >&2
else
  echo "no VALHEIM_SERVER in .env - starting at the menu, join by hand" >&2
fi

cd runtime/game
exec bash ./start_game_bepinex.sh "${JOIN[@]}" -screen-width 960 -screen-height 540 -screen-fullscreen 0 "$@"
