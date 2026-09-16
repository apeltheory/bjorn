#!/usr/bin/env bash
# Show the reports filed in game with "bug <what happened>".
set -uo pipefail
cd "$(dirname "$0")/.."
REPORTS=runtime/reports.log
[[ -f $REPORTS ]] || { echo "No reports yet. In game, anyone can type: bug he got stuck at the door"; exit 0; }
case "${1:-last}" in
  all)   cat "$REPORTS" ;;
  count) grep -c '^== ' "$REPORTS" ;;
  *)     awk -v n="${1:-1}" '/^== /{c++} c>total-n' total="$(grep -c '^== ' "$REPORTS")" "$REPORTS" ;;
esac
