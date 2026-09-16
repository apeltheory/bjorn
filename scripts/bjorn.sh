#!/usr/bin/env bash
# Start, stop and inspect Bjorn. One entry point so nobody has to remember which
# script does what, or which order things go in.
set -uo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"
LOGS="$ROOT/runtime/logs"
PORT="${BJORN_PORT:-8765}"
mkdir -p "$LOGS"

# pgrep -f matches this script's own command line, so check what each hit really is.
pid_of() { # pattern comm-prefix
  local pid
  for pid in $(pgrep -f "$1" 2>/dev/null); do
    [[ $pid == $$ || $pid == $PPID ]] && continue
    [[ $(cat "/proc/$pid/comm" 2>/dev/null) == $2* ]] && { echo "$pid"; return; }
  done
}
planner_pid() { pid_of 'brain/server\.py' python3; }
game_pid()    { pid_of 'valheim\.x86_64' valheim; }

wait_gone() { # pid seconds
  local pid=$1 left=${2:-15}
  while (( left-- > 0 )) && kill -0 "$pid" 2>/dev/null; do sleep 1; done
  ! kill -0 "$pid" 2>/dev/null
}

planner_start() {
  local pid; pid=$(planner_pid)
  if [[ -n $pid ]]; then echo "planner already running (pid $pid)"; return 0; fi
  if ss -lptn "sport = :$PORT" 2>/dev/null | grep -q LISTEN; then
    echo "port $PORT is held by something else; not starting" >&2; return 1
  fi
  nohup ./scripts/brain.sh >"$LOGS/planner.log" 2>&1 &
  sleep 2
  pid=$(planner_pid)
  if [[ -z $pid ]]; then echo "planner failed to start; see $LOGS/planner.log" >&2; tail -5 "$LOGS/planner.log" >&2; return 1; fi
  echo "planner started (pid $pid), logging to $LOGS/planner.log"
  head -1 "$LOGS/planner.log"
}

planner_stop() {
  local pid; pid=$(planner_pid)
  if [[ -z $pid ]]; then echo "planner not running"; return 0; fi
  kill "$pid" 2>/dev/null
  if wait_gone "$pid" 10; then echo "planner stopped"; else kill -9 "$pid" 2>/dev/null; echo "planner killed"; fi
}

game_start() {
  local pid; pid=$(game_pid)
  if [[ -n $pid ]]; then echo "Valheim already running (pid $pid)"; return 0; fi
  # Stage whatever was last built, so the game never loads a stale plugin.
  python3 scripts/prepare.py >/dev/null || { echo "staging failed" >&2; return 1; }
  nohup ./scripts/game.sh -logFile "$ROOT/runtime/unity.log" >"$LOGS/game.log" 2>&1 &
  echo "Valheim launching; plugin log at runtime/game/BepInEx/LogOutput.log"
}

game_stop() {
  local pid; pid=$(game_pid)
  if [[ -z $pid ]]; then echo "Valheim not running"; return 0; fi
  # SIGTERM lets Unity run its normal shutdown, which saves. Give it room.
  kill "$pid" 2>/dev/null
  if wait_gone "$pid" 30; then echo "Valheim exited cleanly (saved)"; else
    echo "Valheim did not exit in 30s; leaving it alone so it does not lose the world" >&2; return 1
  fi
}

state() { [[ -n $1 ]] && echo "running (pid $1)" || echo "stopped"; }

status() {
  local p g version
  p=$(planner_pid); g=$(game_pid)
  version=$(sed -n 's/.*BepInPlugin("[^"]*", "[^"]*", "\([^"]*\)").*/\1/p' plugin/Companion.cs | head -1)
  echo "planner : $(state "$p")"
  echo "Valheim : $(state "$g")"
  echo "port $PORT: $(ss -lptn "sport = :$PORT" 2>/dev/null | grep -q LISTEN && echo held || echo free)"
  echo "plugin  : ${version:-unknown} in source, staged $(date -r runtime/game/BepInEx/plugins/Bjorn.dll '+%d %b %H:%M' 2>/dev/null || echo never)"
  if [[ -f runtime/game/BepInEx/LogOutput.log ]]; then
    echo "last log: $(grep -aE 'Bjorn|Error|Exception' runtime/game/BepInEx/LogOutput.log 2>/dev/null | tail -1 | cut -c1-110)"
  fi
}

build() {
  ./scripts/build.sh >"$LOGS/build.log" 2>&1 || { echo "build FAILED" >&2; grep -E "error CS" "$LOGS/build.log" | head >&2; return 1; }
  python3 -m unittest discover -s tests >/dev/null 2>&1 || { echo "tests FAILED" >&2; python3 -m unittest discover -s tests 2>&1 | tail -15 >&2; return 1; }
  python3 scripts/check-commands.py >/dev/null || { echo "dispatch check FAILED" >&2; python3 scripts/check-commands.py >&2; return 1; }
  if [[ -n $(game_pid) ]]; then
    echo "built and checked; Valheim is running so the plugin was NOT staged - stop it first"
  else
    python3 scripts/prepare.py >/dev/null && echo "built, checked and staged"
  fi
}

case "${1:-status}" in
  status)  status ;;
  start)   planner_start && game_start ;;
  stop)    game_stop; planner_stop ;;
  restart) game_stop; planner_stop; planner_start && game_start ;;
  planner) case "${2:-status}" in start) planner_start ;; stop) planner_stop ;; restart) planner_stop; planner_start ;; *) status ;; esac ;;
  game)    case "${2:-status}" in start) game_start ;; stop) game_stop ;; restart) game_stop; game_start ;; *) status ;; esac ;;
  build)   build ;;
  logs)    tail -n "${2:-40}" runtime/game/BepInEx/LogOutput.log ;;
  *)       echo "usage: bjorn.sh {status|start|stop|restart|build|logs [n]|planner start|stop|game start|stop}" >&2; exit 2 ;;
esac
