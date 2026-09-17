---
name: bjorn
description: Start, stop, rebuild or check the Bjorn Valheim companion — the planner service and the modded game client. Use whenever asked to launch, kill, restart, stage or check on Bjorn, the bot, the planner, the bridge, or Valheim itself; or after changing plugin/Companion.cs or brain/server.py and the change needs to reach the running game.
---

# Running Bjorn

One entry point for everything: `scripts/bjorn.sh`. Prefer it over calling
`brain.sh`, `game.sh` or `prepare.py` directly — it handles ordering, staging and
the checks those scripts do not.

```sh
cd /path/to/valheim-companion   # the repo root

./scripts/bjorn.sh status          # what is running, plugin version, last log line
./scripts/bjorn.sh start           # planner, then the game
./scripts/bjorn.sh stop            # game (cleanly), then planner
./scripts/bjorn.sh restart
./scripts/bjorn.sh build           # build + tests + dispatch check + stage
./scripts/bjorn.sh logs 60         # tail the in-game plugin log

./scripts/bjorn.sh planner start|stop|restart
./scripts/bjorn.sh game start|stop|restart
```

## Joining a server

With `VALHEIM_SERVER` set in `.env` (as `ip:port`), `game start` selects the character and
joins by itself, using Valheim's own `-joinserverwithcharacter` argument. Without it, the
game stops at the main menu and someone has to join by hand — say so rather than waiting.

Confirm he actually arrived before reporting success: `./scripts/bjorn.sh logs` should show
world loading, and `Bjorn, self test` in chat is the definitive check.

## Rules that matter

**Always `status` first.** It is cheap and tells you whether you are about to
start a second copy or kill something the user is mid-session with.

**Never stage a plugin while Valheim is running.** The DLL is in use and the game
loads it once at startup. `build` detects this and refuses to stage, telling you to
stop the game first. The sequence after editing `plugin/Companion.cs` is:

```sh
./scripts/bjorn.sh game stop
./scripts/bjorn.sh build
./scripts/bjorn.sh game start
```

**Stopping Valheim is not free.** `game stop` sends SIGTERM and waits up to 30
seconds so Unity runs its normal shutdown, which is what saves the world and
character. If it does not exit in time the script leaves it alone rather than
forcing it — do not `kill -9` a running Valheim, it can lose progress. Tell the
user instead.

**Restart the planner after editing `brain/server.py` or `.env`.** It reads both
once at startup. `MAX_API_CALLS` also resets on restart, so a restart refills the
request budget.

**The planner is not required** for direct commands — the plugin answers about
forty phrases itself. It is only needed for orders that fall through to Claude.
If it will not start, say so and carry on; do not treat it as fatal.

## Checking a change reached the game

`status` prints the plugin version in source and when the staged DLL was last
written. If the staged time predates your edit, it did not get staged.

In game, `Bjorn, self test` is the fastest confirmation the new build is live: one
order, six lines back covering version, known places, surroundings, tool wear,
belly and whether the planner is reachable. It touches nothing.

## Bug reports filed in game

Anyone on the server can type `bug <what happened>` in normal chat - it is not an order,
needs no name prefix, and works in manual mode. Each one appends a snapshot to
`runtime/reports.log`: position and biome, current job and detour, target and threat,
health, stamina, food, what is in his hands and his pack, what is around him, and what
all twelve steering probes saw at that instant.

He also records his own failures without being asked — every give-up, with the same
geometry, deduplicated per spot per minute.

```sh
./scripts/bugs.sh          # ranked: what he gives up on, and where
./scripts/bugs.sh reports  # filed by hand
./scripts/bugs.sh fails 30 # raw failure lines
./scripts/bugs.sh all
```

**Start a session with `./scripts/bugs.sh`.** It is the cheapest way to find what is
actually worth fixing, and the location clustering points at the specific terrain rather
than a general complaint.

Read the report before theorising. The `steering:` line is usually the answer when the
complaint is about getting stuck, and `hands: EMPTY` explains most "he did nothing".

## Where things are

| Path | What |
| --- | --- |
| `runtime/logs/planner.log` | Planner stdout, including the startup line |
| `runtime/logs/game.log` | Launcher stdout |
| `runtime/game/BepInEx/LogOutput.log` | The plugin's own log — where `Logger.LogInfo` goes |
| `runtime/unity.log` | Unity log |
| `runtime/reports.log` | Bug reports filed in game with `bug ...` |

Do not read or print `.env` or `runtime/bridge.token`.
