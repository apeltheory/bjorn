# Bjorn — Valheim companion

Bjorn runs a real Valheim client on a spare machine using its own Steam account. You play on your PC. A C# BepInEx plugin controls his character, and a local Python service reads natural-language orders and gives him a grounded Viking personality. Jev, TypeSafe's System One model, picks which of his forty-five actions an order means; Anthropic writes the lines when he has to actually talk. Jev is optional — without a key the planner runs on Anthropic alone, exactly as it did before.

## Quick start

1. Sign this laptop's Steam into Bjorn's account, which needs access to Valheim.
2. Open a terminal and start the planner:

   ```sh
   ./scripts/brain.sh
   ```

   Leave that terminal open. If it reports `Address already in use`, a service is already listening on port 8765; check the existing planner instead of starting another copy.

3. Open another terminal and launch the modded game:

   ```sh
   ./scripts/game.sh
   ```

   **Use this launcher. Launching normally through Steam does not load this separate mod profile.** The launcher starts a 960×540 window; lower graphics quality further in Valheim's settings if needed.

4. **He can join by himself.** Put your server in `.env` and the launcher takes it from there:

   ```sh
   python3 scripts/set-server.py        # prompts; the password stays invisible
   ```

   or by hand in `.env`:

   ```sh
   VALHEIM_SERVER=192.168.1.50:2456     # a dedicated server, as ip:port
   VALHEIM_CHARACTER=Bjorn
   VALHEIM_PASSWORD=yourpassword
   ```

   That uses Valheim's own `-joinserverwithcharacter` argument, which selects the
   character and joins in one step — no mod involved, and nothing to click. Leave
   `VALHEIM_SERVER` blank and it starts at the menu as before, to be joined by hand.

   Valheim's own handler hardcodes `FileSource.Local` when selecting the character, so a
   character kept in **Steam Cloud** is not found and a blank one is silently created and
   played instead. The plugin patches `FejdStartup.SelectCharacter` to look the profile up
   and use the source it actually lives in. If you see `Loading the real one` in the log,
   that is this working.

   This works for a **dedicated server**, addressed by ip:port. A world hosted from
   someone's game client is reached through Steam rather than an address, so that still
   needs joining by hand. The launcher uses whichever Steam account is signed in; it does
   not log Steam in for you.
5. Press **F8** while the game is focused to switch to bot control. The on-screen message confirms the mode. The last configured mode was **manual** (`Enabled = false`).
6. From your PC, stand nearby and type `Bjorn, inventory` in normal chat. Then try `Bjorn, follow me` and `Bjorn, stay`.

The planner is not installed as an automatic background service. Basic commands work without it, but natural-language requests need it running.

## Taking over manually

Press **F8** in-game:

- **MANUAL control:** use keyboard and mouse normally; Bjorn ignores orders.
- **BOT control:** the plugin controls movement and waits for an order.

Taking over clears the follow target and cancels the pending AI request. Switching back does not resume an old task. The selected mode is saved across restarts. An on-screen message confirms each change.

The F8 feature is installed in version 0.1.2 and needs a game restart to load if the game was already running during installation. After that, toggling does not require restarts. Its in-game behavior still needs verification.

## Talking to Bjorn

Any nearby player can address him. Start the message with `Bjorn` or `bjorn`, followed by a space, comma, or colon. Capitalization does not matter; `Bjornson` is not an address.

### Without his name (opt in)

Set `ListenUnaddressed = true` in the game config and he reads every nearby line,
but acts on almost none of them. Each unprefixed line costs one small Jev question —
*was this meant for him?* — and anything below `ADDRESSED_FLOOR` (0.70) is dropped
without another word. Saying his name still works and skips the question entirely,
so it stays the fast, certain way to reach him.

Three rules keep it from being a menace:

- **An overheard line becomes a job or it becomes nothing.** He never answers back
  into a conversation he was not part of, so "I wonder where copper comes from"
  between two players gets silence, not a lecture.
- **Moving goods needs near-certainty (0.90).** "Let's dump this lot and head back"
  reads a great deal like `pile`, and acting on a misheard conversation is the one
  mistake that costs real work.
- **A real order outranks a guess.** If he is still thinking about stray chat when
  someone says his name, the guess is abandoned mid-flight.

It needs a Jev key. Without one there is no gate, and without a gate there is no
safe way to act on a room full of people talking, so nothing unaddressed is ever
acted on.

The plugin answers these exact phrases itself, without an API call. Anything else
addressed to him goes to the planner, which picks one of the same actions.

| Message | Intended behavior |
| --- | --- |
| `Bjorn, follow me` | Follow the player who spoke |
| `Bjorn, come here` | Walk to the speaker once, then stop |
| `bjorn stay` | Stop and wait |
| `Bjorn, go home` / `go to bed` | Walk to the saved waypoint or bed |
| `Bjorn, inventory` | Report actual carried items |
| `Bjorn, status` | Report health, stamina, food, and current job |
| `Bjorn, where are you?` | Report biome, coordinates, and bearing to home |
| `Bjorn, look around` | Report nearby creatures, chests, forage, and drops |
| `Bjorn, gather` / `gather flint` | Walk to and pick up dropped items nearby |
| `Bjorn, harvest` / `pick raspberries` | Walk to and pick berries, mushrooms, and other pickables |
| `Bjorn, chop wood` | Equip an axe and fell nearby trees and logs |
| `Bjorn, mine` | Equip a pickaxe and break nearby rock and ore |
| `Bjorn, defend me` / `attack greyling` | Equip a weapon and engage hostile creatures nearby |
| `Bjorn, deposit` / `stash wood` | Move items into a chest within five metres |
| `Bjorn, take all` / `take wood` | Take items out of that chest |
| `Bjorn, craft 20 wood arrows` | Craft a plain recipe at the station he stands at |
| `Bjorn, repair` | Mend worn gear at the station he stands at |
| `Bjorn, eat cooked meat` | Eat a matching food item if Valheim allows it |
| `Bjorn, equip iron sword` | Equip a matching item from inventory |
| `Bjorn, unequip shield` / `unequip all` | Put equipped gear away |
| `Bjorn, drop wood` / `give me flint` | Drop a stack at his feet for you |
| `Bjorn, open the door` / `close the door` | Use a door within five metres |
| `Bjorn, feed the fire` | Add fuel to a fireplace within five metres |
| `Bjorn, wave` / `cheer` / `sit` / `dance` | Play an emote |
| `Bjorn, what can you do?` | List the commands he answers directly |
| `Bjorn, come along with me` | Ask the planner to interpret the order |
| `Bjorn, how are you holding up?` | Ask the planner for a reply using current state |

Normal chat reaches Bjorn within 15 metres; stand close to give orders. Following stops if the target gets more than 35 metres away. Replies use normal nearby chat. The speaking character must be loaded on the bot client. **Bjorn ignores messages from his own character**, so testing by typing into Bjorn's own client does not work yet. Have another player give the orders.

While an AI request is pending, other orders are ignored except stop/stay. Orders are also limited to one every two seconds, except stop/stay. Any nearby player can redirect or stop him.

## Current capabilities and limits

He answers roughly forty orders directly and sends anything else to Claude. The
sections below describe what actually happens, not what is planned.

### Working on his own

`gather` `harvest` `chop` `mine` run as **jobs**, not one-shot actions. A job anchors
to where the order was given, ranges `JobRadius` metres (25 by default), and keeps
going by itself:

- **Full pack → run it home.** He walks to the `home` waypoint and works along *every*
  chest within eight metres, nearest first, so one full chest does not stall the run.
  Then back to the anchor and on with the job. He keeps his food, tools and weapons;
  only materials, trophies and fish go in.
- **No chest, or all of them full → he piles it on the ground** at home and keeps
  working, rather than standing there holding it. Dropped items persist in Valheim, so
  a pile is a slower chest, not a loss. He says which happened every time. Set
  `PileWhenNoChest = false` if you would rather he stopped and told you.
- **Blunt axe → go and mend it.** Below 15% durability he breaks off for a
  `workbench` waypoint (or `home`), repairs, and returns to the same spot.
- **Chopping gathers its own wood.** Felling scatters wood far outside Valheim's
  two-metre pickup, so when no trees are left he sweeps the ground for what he
  dropped before reporting. Mining does the same.
- **He eats.** Any food he carries goes into a free food slot while he works, and he
  takes a few meals out of the home chest whenever he unloads there.
- **He asks when he has nothing.** With empty food slots and no food in his pack he
  says so, about once every two minutes. Toss food on the ground near him and he will
  walk up to twelve metres to collect it, thank you, and eat it. That is the whole
  mechanism -- no fishing, no cooking, no foraging for a meal.

Each load gets a fresh `JobMinutes` clock (15 by default). A tree, rock or foe that
has not fallen in 30 seconds is skipped.

### Carrying for you (mule)

`carry for me` / `be my mule` / `pick up after me` puts him in **mule** mode: he walks with
you and picks up anything you leave on the ground within twelve metres, skipping whatever
will not fit. When he fills up he runs the load to the `home` chest by himself and comes
back to you. `take it home` sends him on that run early; he unloads and returns.

Without a `home` waypoint he still carries, he just cannot unload — he says so when you
set him going.

### Fetching and delivering

`Bjorn, bring me some wood` is the one order that chains. If he carries it, he walks
to you and drops it at your feet. If he does not, he works out how to get it — pick
it off the ground, chop it, or mine it — and delivers when the job ends. `fetch me`,
`hand me`, `get me`, `give me`, `toss me`, `pass me` and `find` all work the same way,
and all of them walk to you first.

Counts work: `bring me 20 stone`, `drop 10 wood`. Without a count he hands over every
stack of it.

### Dropping on the floor

You do not need chests for any of this. `dump it` / `drop it here` / `pile it up` makes
him pile his cargo where he stands — materials, trophies and fish — while keeping his
tools, food and armour. `drop everything` is the blunter version: everything unequipped,
kit included.

### Unequipping and dropping

`unequip shield` takes one thing off; `unequip all` strips everything worn. `drop wood`
drops at his own feet where he stands; `toss` is a synonym. Dropping something he is
holding works — Valheim unequips it first — so `drop your axe` does what it says.
`drop everything` empties the pack but leaves his gear on.

### Guarding

`guard the camp` puts on the best helmet, chest, legs, cape and shield he carries,
draws his highest-damage weapon, and walks a ring around the camp. The ring is sized
from the buildings actually standing there, so the patrol follows your base rather
than an arbitrary circle. Anything hostile inside `GuardRadius` (30 m) is met,
deepest intruder first, and he returns to the ring when it is down.

### Fighting alongside you

`fight with me` / `watch my back` / `join me` puts him in **escort**: he gears up,
sticks with you, and picks targets from around **you** rather than around himself, so
he goes for what the party is fighting instead of whatever wandered closest to him.
Bosses are picked first, and once he is on a boss, adds hitting him do **not** pull
him off it. Unlike other jobs, escort is never abandoned when he is hurt -- he backs
off, recovers, and closes again.

### Retaliation

Two triggers, and they are different:

- **Damage.** Anything that actually lands a hit on him becomes his target for the
  next 15 seconds, at any range. This is what answers an archer at thirty paces. The
  one exception is the boss lock above.
- **Proximity.** `DefendSelf` (on by default) also makes him answer anything hostile
  that comes within 12 metres while he is working, then resume the job.

He gives ground when his stamina runs out rather than standing in melee, and below
35% health he stops swinging and runs -- away from whatever is hitting him, toward
home if home lies that way.

### Dying

When he dies he remembers the spot. On respawn he walks back to the gravestone, takes
his gear, re-equips it, and heads home. `get your gear` retries if it went wrong.

### Named places

`remember this as home`, `remember this as the mine`, `go to the mine`, `forget the
mine`, `what places do you know`. `home` is special: it is where full loads are
unloaded and where he returns after recovering his gear.

### Storage and crafting

Both need him standing close: a chest within five metres for `deposit` and `take`,
and a crafting station in range for `craft` and `repair`. Crafting attempts plain
quality-1 recipes only — not upgrades, and not recipes taking a choice of ingredient.

### Limits

He has no block, no dodge, no ranged attack and no potion use. He walks into melee
and swings. Against a boss he is an extra axe and an extra target, not a competent
fighter -- expect him to die, recover his gear, and come back.

No building, portals, boats, carts, taming, cooking at a station, or hotbar use. He
works only with tools and materials he already carries, and only within the job
radius — he cannot go looking for a forest that is not already near him. He has no
memory between orders. Movement is local steering: he rounds obstacles but cannot
solve complex terrain or navigate around large structures, which is the main thing
likely to strand him on a long walk home.

The companion mod is client-side; it does not require a server mod.

## Talking to him out loud

Valheim has no voice chat of its own — there is no VOIP class anywhere in the game — so
nothing taps the game's audio. Speech reaches him by a different route:

    you speak -> anything that transcribes -> POST /listen -> the plugin polls
             -> the same dispatch as typed chat -> he answers in chat

`scripts/say-to-bjorn.py "Bjorn, chop wood"` is that seam, and works with no microphone at
all, which is how the path is tested. Point any speech-to-text you like at it.

The important property: **a spoken order can do nothing a typed one could not.** It still
has to start with his name, it still goes through the same cooldown, the same safety rules
and the same fall-through to the planner. The nearest player within 40 m is treated as the
speaker, so `follow me` and `bring me wood` know who they mean; with nobody near, he logs
that he heard it and does nothing.

Two things already built fit this well. The name prefix is the wake word, so it needs no
separate one. And after he asks a question, the next thing you say counts without his name,
because the follow-up window does not care whether the answer was typed or spoken.

Speech recognition will mangle `flint` and `greyling` constantly. That matters less than it
sounds: names are matched with case, spacing and punctuation ignored, and anything that
still misses goes to Claude, which usually recovers the intent.

Turn it on with `Listen = true` in the mod config. It is off by default and polls nothing
while off.

## Reporting a bug from in game

Anyone on the server can type `bug <what happened>` in normal chat:

```
bug he ran at the wall instead of opening the door
```

It is not an order — no name prefix, no cooldown, and it works in manual mode or when he
is not spawned. He confirms in chat and appends a snapshot to `runtime/reports.log`: where
he was, the job and any detour, his target and threat, health and stamina and food, what is
in his hands and his pack, what is around him, and **what all twelve steering probes saw at
that instant**.

That last part is the point. "He got stuck on something" is a paraphrase; the probe line is
the geometry:

```
steering: 0=wall 1.8; -30=slope 47; 30=blocked at chest, rise 0.9 (a door is within reach and did not open)
```

He also records his **own** failures, unprompted. Every time he gives up — no way through,
wedged, lost you, leash broke, gave up on a tree, could not reach a foe, bench unreachable —
he appends a line with the same geometry, deduplicated to one per failure per five-metre
square per minute so a wedged bot cannot flood the file.

```
-- 19:30:01 no way through | job=Home | at=286,40,311 | hands=Stone axe | probes=0=wall 1.8; -30=slope 47
```

That turns anecdote into a ranked list:

```sh
./scripts/bugs.sh              # what he gives up on most, and where
./scripts/bugs.sh reports      # the ones filed by hand
./scripts/bugs.sh fails 30     # raw failure lines
./scripts/bugs.sh clear
```

The summary clusters failures by kind and by location to the nearest ten metres, so a
patch of terrain that defeats him repeatedly shows up as a coordinate rather than a
feeling. It also counts how many failures happened with `hands=EMPTY`, which is its own
distinct bug.

## Tending the fires

`tend the smelter` / `tend the kiln` / `tend the furnace` / `smelt`. Smelters, kilns, blast
furnaces and windmills are all the same component in Valheim, driven by three switches
rather than an ordinary interaction, so one order covers all of them.

He walks a circuit of every one within 40 metres: takes the finished metal out first (or
there may be no room to put ore in), tops up the coal, loads the ore, and moves to the next.
He keeps circling while there is anything left to shift, and stops when a full round moves
nothing — which is what running out of ore or coal looks like from the outside.

## Asking him things about Valheim

**Recipes come from the game, not from memory.**

```
Bjorn, what do I need for a bronze axe?
Bjorn, how do I make wood arrows?
Bjorn, recipe for a fine bow
```

He reads those out of `ObjectDB` — the installed game's own data — so the answer is exactly
right for your version and any mods you run, costs no API call, and works with the planner
down. Model memory can drift on exact quantities; the game cannot.

Anything else about Valheim — where something is found, which boss comes next, what a
creature is weak to — goes to the planner and is answered plainly, in his voice. There is no
wiki baked in and there does not need to be.

## Asking him things

Anything that is not an order goes to the planner and comes back in his own voice, with
the answer correct:

| You say | He says |
| --- | --- |
| `Bjorn, what's the capital of the United States?` | Washington. A long row west, and I'd not fancy it. |
| `Bjorn, what's 5+5?` | Ten. Count your fingers, that is what they are for. |

He never refuses, never explains that he is a game companion, and never breaks character
to say he cannot help. He is a Viking, not an oracle — he may be baffled you asked and
will not know the modern word for a thing, but the fact has to be right.

A question is told from an order by how it opens. `what`, `why`, `how`, `which`, `when`,
`who`, `where` mark a question, so **"what's the best wood to chop?"** is answered rather
than obeyed. `can you`, `could you`, `go and` are orders, so **"can you chop wood"** still
sets him working.

This is the one feature that genuinely needs the planner running, and it costs a call from
the budget and a few seconds. Everything he can do himself stays instant and works offline.

## When he does not understand

Every order that no direct command claims is sent to the planner, which reads the
whole sentence and picks one action. A command only claims an order if it can find
what was named, so `drop everything and follow me` and `pick a fight with that troll`
fall through to the planner rather than being answered literally.

### Which model reads it

Jev is a *System One* model: it does not write text at all, it returns typed
decisions — a pick from a named list, a yes/no probability, a score — each with a
calibrated confidence. Choosing one of Bjorn's forty-five actions is exactly that
shape, so when a `TYPESAFE_API_KEY` is set the planner asks Jev three questions in
one request: which action this is, whether the speaker named anything in
particular, and what register the message is in.

That leaves the two things Jev cannot do:

- **The item.** Jev returns a choice, never a word, so the thing that was named is
  read off the sentence in Python (`extract_item`): one leading verb goes, then the
  words that carry nothing on their own, and what remains is the name. `craft 20
  wood arrows` keeps its count, `take the wood out of the chest` keeps only the
  wood, and `eat up` names nothing rather than matching turnip soup.
- **The talking.** `chat` — questions about Valheim, sums, riddles, insults — still
  goes to Anthropic, which is now asked only for a line in his voice and never for
  an action. Without an Anthropic key he falls back to a canned line and keeps
  working.

Two things are safer than before. A pick below `JEV_MIN_CONFIDENCE` becomes talk
instead of a guessed job, and the actions that can empty a chest or a pack need both
a clear pick and, when Jev says the speaker named something, an item actually read
off the sentence — otherwise he asks. An empty item means *everything*, which is how
a whole chest gets emptied by mistake.

**None of this has been run against the live Jev API.** It is written to the wire
contract published in TypeSafe's official Python SDK, and it is dormant until a key
exists: with `TYPESAFE_API_KEY` blank the planner is byte-for-byte the Claude one it
has always been.

### Rehearsing it without a key

`scripts/fake-jev.py` stands in for `api.typesafe.ai`. It cannot imitate Jev's
judgement, so it does not try: the answers come from a corpus in
`tests/fixtures/jev_orders.json`, where each order carries the answer Jev is
*imagined* to give and the decision that should fall out of it. What it does do is
hold the planner to the published contract — a request missing `state`, or a choice
with no criteria, or more than 255 of them, comes back 422 exactly as the real API
would, so a malformed request fails here rather than on the first live call.

```sh
python3 scripts/rehearse.py          # every order, with what he decided and said
python3 scripts/rehearse.py --quiet  # failures and the tally only
```

It drives a real `Planner` over real HTTP and exits non-zero on any surprise, so it
runs inside the test suite as well. Three real bugs came out of writing it: junk
items were being sent to the twenty-five actions the plugin runs without reading
`item`; whole clauses were being accepted as item names, which would have had him
answer *I see no there's a troll on us, deal with it to fight*; and the two-call
talk path could take 20 seconds against a plugin that gives up at 15.

When a key does arrive, record the real answers and replace each `jev` block in the
corpus. Anything that then fails is a genuine disagreement between the model and
the planner, which is the thing worth finding.

Item, creature and recipe names are matched with case, spaces and punctuation
ignored, against the localized name, the raw `$item_torch` token, and the prefab
name — so `torch`, `Torch`, `wood arrows` and `WoodArrow` all match. A failed lookup
logs what he was actually carrying to `runtime/game/BepInEx/LogOutput.log`.

Orders arriving while a planner request is pending, or within two seconds of the last
one, are refused — but he now says so (`One thing at a time`) rather than going
silent. `stop` and `stay` always get through.

Normal chat only reaches 15 metres. Stand close.

## Installed versions and verification

Status recorded September 15, 2026:

| Component | Version / status |
| --- | --- |
| Bjorn plugin | 0.4.0 built and installed; adds the self-running work loop (haul home, mend, glean), bring/deliver, camp patrol, self-defence, self-feeding, named waypoints, and grave recovery on top of 0.3.0 |
| BepInExPack Valheim | 5.4.2350 |
| Better Networking | tibijczyk fork 2.3.4 installed; restart and compatibility check pending |
| Installed Valheim at initial test | l-1.0.12, network version 40, Steam build 25253764 |
| Anthropic planner | Configured locally; API and live bridge requests succeeded |
| Jev planner | Code complete and rehearsed against a contract-checking stand-in; **never run against the live API**, no early-access key yet |

**Verified:** Bjorn 0.1.1 loaded and joined the friend's server with matching network versions. Six Python tests passed. The bridge rejected an invalid token and accepted an authenticated command. A natural-language request through the running planner received an Anthropic response.

**Not yet verified:** everything added in 0.3.0 and 0.4.0. It compiles with zero errors and the Python tests pass, but **no part of it has been run in game.** Specifically unobserved: whether swings connect at all (chop, mine, fight all go through Valheim's real attack path), the haul/mend/glean loop, patrol, self-defence, grave recovery, chest transfers, crafting, repair, doors and fires. Also still unverified: 0.1.2 hotkey operation, in-game chat-to-action behavior, multiplayer following, local obstacle steering, and Better Networking compatibility with this game version. Do not treat the successful server join as a full bot gameplay test.

Three defects found by source review and fixed before any of this ran, noted because they show the class of thing still likely to be wrong: an automatic haul deposited his own axe, food and torch into the chest; the haul triggered on free slots while Valheim's pickup gives up on carry weight, so a heavy load would never have triggered one; and he stood up to 5.6 m from a felled log and swung, because the approach distance was measured from the log's pivot rather than its surface. The last one would have made chopping silently do nothing.

Better Networking's package targets an earlier Valheim release. Its documentation says unmodded peers are supported, but compression only works between machines running the mod. Check startup logs after restarting. To disable it, quit through the game menu and move `runtime/game/BepInEx/plugins/BetterNetworking` outside the plugins directory.

## API key and settings

The key is already saved locally in `.env`. **Do not paste it into chat, logs, or handoff notes.** To enter or replace it without a text editor:

```sh
python3 scripts/set-key.py            # the Jev (TypeSafe) planner key
python3 scripts/set-key.py anthropic  # the Anthropic key he speaks with
```

Paste with Ctrl+Shift+V, then press Enter. Input stays invisible, and setting one key leaves the other alone. Restart the planner after changing a key or model.

The `.env` settings are:

- `TYPESAFE_API_KEY`: private Jev credential. Blank until early access arrives, and everything falls back to Anthropic.
- `TYPESAFE_MODEL`: defaults to `jev-latest`.
- `MAX_JEV_CALLS`: defaults to 2000 per planner process. Jev is charged per input token at a small fraction of a frontier call, so this cap is far looser than the Anthropic one.
- `JEV_MIN_CONFIDENCE`: defaults to `0.40`. Jev returns a calibrated confidence with every pick; below this he treats the order as talk rather than guessing at a job. Chest and pack emptying needs `0.60` regardless.
- `ANTHROPIC_API_KEY`: private API credential.
- `ANTHROPIC_MODEL`: configured model, initially `claude-sonnet-5`.
- `MAX_API_CALLS`: defaults to 100 per planner process. This is a request cap, **not a dollar spending cap**. Failed requests count, and restarting resets it.

With both keys set, an order that names a job costs one Jev call and nothing else; only `chat` reaches Anthropic. The planner prints which path it is on at startup.

The bridge binds to `127.0.0.1:8765` and uses a generated token in `runtime/bridge.token`. It sends addressed orders plus bot health, task, and inventory to whichever model is answering, not the entire chat stream. Basic commands execute locally without API calls. The Jev timeout is 8 seconds and the Anthropic timeout is 12; the plugin waits at most 15.

Game configuration lives at:

```text
runtime/game/BepInEx/config/local.bjorn.companion.cfg
```

`Enabled` controls bot/manual mode; `Name` is the chat prefix; `ToggleKey` defaults to F8; `TokenFile` identifies the bridge token. Added in 0.4.0:

- `Waypoints`: named places as `name=x,y,z;name=x,y,z`. Written by `remember this as ...`. A pre-0.4 `HomePosition` is imported once.
- `JobRadius` (25): how far from the order spot a working job may range, 5-100 m.
- `JobMinutes` (15): how long one load may take before he gives up, 0.5-120. The clock restarts after each run home.
- `GuardRadius` (30): how far from a camp's centre counts as inside it on patrol, 8-120 m.
- `DefendSelf` (true): fight back at anything hostile that comes close during other work. Guard duty ignores this and always fights.
- `PileWhenNoChest` (true): on a run home, leave anything the chests cannot take on the ground rather than ending the job.
- `CampCentre` / `CampRadius`: written by `learn the camp`; what he knows of the base's shape.
- `Listen` (false): poll the bridge for spoken orders. Leave off unless something is transcribing speech into it.

Other than the in-game F8 toggle, edit configuration while the game is closed.

## Files for another agent

| Path | Purpose |
| --- | --- |
| `plugin/Companion.cs` | Chat hooks, controls, jobs, item and station actions, F8 toggle, bridge calls |
| `plugin/Bjorn.csproj` | C# project referencing installed game assemblies |
| `brain/server.py` | Python standard-library HTTP bridge, Jev action planner, and Anthropic voice |
| `tests/test_brain.py` | Offline command, validation, item reading, both API contracts, call caps, and the plugin dispatch contract |
| `tests/fixtures/jev_orders.json` | The order corpus: what Jev is imagined to answer, and where each order should land |
| `scripts/fake-jev.py` | Stand-in for `api.typesafe.ai`; enforces the request contract, serves fixture answers |
| `scripts/rehearse.py` | Run the corpus through a real planner against the stand-in |
| `scripts/build.sh` | Compile the plugin |
| `scripts/prepare.py` | Stage game symlinks, BepInEx, and compiled Bjorn plugin |
| `scripts/game.sh` | Launch the modded client |
| `scripts/brain.sh` | Load `.env` and run the planner |
| `scripts/set-key.py` | Privately save the Jev or Anthropic API key |
| `scripts/set-server.py` | Save the server to join, with the password entered invisibly |
| `scripts/smoke.py` | Test authentication and a basic order against the running bridge |
| `scripts/check-api.py` | Make one real planner request and report which model answered |
| `scripts/check-commands.py` | Static checks on the chat dispatch: unreachable commands, duplicates, truncated chat lines |
| `scripts/say-to-bjorn.py` | Push a line to Bjorn as if spoken; the seam any speech-to-text plugs into |
| `scripts/bugs.sh` | Read the reports filed in game with `bug ...` |
| `runtime/game/BepInEx/plugins/` | Installed Bjorn and Better Networking plugins |
| `runtime/game/BepInEx/LogOutput.log` | Mod/game log |
| `runtime/unity.log` | Unity log from launches that explicitly selected this file |
| `.tools/` | Local SDK, ILSpy, downloaded packages, inspection output, build log |
| `.env` and `runtime/bridge.token` | Secrets; do not disclose |

The profile links game assets from the Steam Valheim directory (by default `~/.local/share/Steam/steamapps/common/Valheim`; override with `VALHEIM_DIR`). It is **not a separate installation of Steam or a separate save-data sandbox**. Steam login and character saves use the normal desktop user's environment. Game updates affect the linked files and can require rebuilding or fixing mods.

## Build and checks

From the project folder:

```sh
./scripts/build.sh
python3 -m unittest discover -s tests -v
python3 scripts/check-commands.py
```

`check-commands.py` statically checks the chat dispatch, which nothing else can test
without the game: commands made unreachable by an earlier branch claiming the phrase,
duplicate phrases, question-shaped phrases matched against the wrong variable (a
trailing `?` defeats them), and chat lines that would be cut at 180 characters. It
exits non-zero on any finding.

Close Valheim before staging a new build:

```sh
python3 scripts/prepare.py
```

This copies the local BepInEx pack and compiled Bjorn plugin into the runtime profile. It does not download dependencies or reinstall Better Networking from scratch. SDK and downloaded packages are already in `.tools`; a fresh checkout on another machine needs those prerequisites and valid game paths. Builds currently have warnings; the last build completed with zero errors.

With the planner running:

```sh
python3 scripts/smoke.py
```

For a direct API check (uses one small paid request against whichever planner is configured):

```sh
python3 scripts/check-api.py
```

To exercise the Jev path with no key and no spend:

```sh
python3 scripts/rehearse.py
```

## Next gameplay test

1. Restart the modded client and check that Bjorn 0.3.0 and Better Networking load without errors.
2. Join using the bot account; have another player stand nearby.
3. Press F8 to enable bot mode and say **`Bjorn, self test`**. One order, six lines back: version, known places, chests and stations around him, tool wear, belly, and whether he can reach the planner. It touches nothing, so it is safe to run first and it turns most failures into a specific line. Then try `inventory`, `status`, `where are you` and `look around`.
4. Test `follow me`, `come here`, and `stay` on clear ground, then stopping at an obstacle.
5. Give him an axe and say `chop wood` next to a few trees. Watch whether he faces the trunk, swings, and moves on when it falls.
6. Repeat with a pickaxe and `mine`, then with drops on the ground and `gather`.
7. Stand him at a workbench: `repair`, then `craft 20 wood arrows`.
8. Next to a chest: `deposit`, then `take all`.
9. `equip torch`, then `unequip torch`. Check lowercase and capitalised names both work.
10. Say something deliberately loose, such as `drop everything and come with me`, and confirm it reaches the planner instead of being answered literally.
11. **The work loop.** `remember this as home` next to a chest, walk him to trees, `chop wood`. Watch for: swings connecting, the pack filling, the walk home, the chest taking only materials, the walk back, and the gather sweep at the end. This is the longest untested path in the mod.
12. **Guard.** `guard the camp` at the base, then let something wander in. Check he gears up, meets it, and goes back to patrolling.
13. **Bring.** `bring me some wood` with none carried — he should chop, gather, walk to you, and drop it.
13b. **Escort.** `fight with me`, then pull something. Check he targets what you are on, not what is nearest him, and that he does not quit at low health.
13c. **Hunger.** Strip his food, wait, and confirm he asks for some. Drop food near him and check he walks over, thanks you, and eats it.
13d. **Mule.** `carry for me`, then drop loot as you walk. Check he picks it up, fills, runs home, unloads, and catches back up.
14. **Death.** Let him die somewhere reachable and confirm he walks back to the stone, takes his gear, re-equips, and heads home.
15. Press F8 during a job: manual movement should work immediately.
16. Toggle during an AI request: a late response must not resume the old task.

## Sources

- [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)
- [Installed Better Networking fork](https://thunderstore.io/c/valheim/p/tibijczyk/BetterNetworking_Valheim/)
