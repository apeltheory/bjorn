#!/usr/bin/env bash
# Reports filed in game with "bug ...", and the failures Bjorn records about himself.
set -uo pipefail
cd "$(dirname "$0")/.."
REPORTS=runtime/reports.log
[[ -f $REPORTS ]] || { echo "Nothing recorded yet. In game, anyone can type: bug he got stuck at the door"; exit 0; }

case "${1:-summary}" in
  summary)
    echo "=== what he gave up on, most often first ==="
    grep -a '^-- ' "$REPORTS" | sed 's/^-- [0-9:]* //; s/ |.*//' | sort | uniq -c | sort -rn | head -15
    echo
    echo "=== where, to the nearest 10 metres ==="
    grep -a '^-- ' "$REPORTS" | grep -oa 'at=-\?[0-9.]*,-\?[0-9.]*,-\?[0-9.]*' |
      awk -F'[=,]' '{printf "  %d, %d\n", int($2/10)*10, int($4/10)*10}' | sort | uniq -c | sort -rn | head -10
    echo
    echo "=== empty-handed while failing ==="
    echo "  $(grep -ac 'hands=EMPTY' "$REPORTS") of $(grep -ac '^-- ' "$REPORTS") recorded failures"
    echo
    echo "=== reports filed by hand ==="
    echo "  $(grep -ac '^== ' "$REPORTS") — see: $0 reports"
    ;;
  reports) awk '/^== /{p=1} /^-- /{p=0} p' "$REPORTS" ;;
  fails)   grep -a '^-- ' "$REPORTS" | tail -"${2:-30}" ;;
  all)     cat "$REPORTS" ;;
  clear)   : > "$REPORTS"; echo "cleared" ;;
  *)       echo "usage: bugs.sh {summary|reports|fails [n]|all|clear}" >&2; exit 2 ;;
esac
