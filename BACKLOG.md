# Bjorn — what's asked for, what's done, what's next

Running list so nothing gets lost. Newest requests at the bottom of each section.

## The one thing that matters most

**Nothing in this mod has ever been run in game.** Everything below marked *done* means
it compiles, the tests pass, and the dispatch checker is clean — not that it works.
Three real defects were found by source review alone (see the verification section of
`README.md`), so assume more. The test plan at the end of `README.md` is the priority,
starting with `Bjorn, self test`.

## Done

| # | Asked for | Where it landed |
| --- | --- | --- |
| 1 | More abilities generally | 0.3.0: status, where, scan, come, gather, harvest, deposit, withdraw, craft, repair, unequip, drop, doors, fires, emotes |
| 2 | "He can equip but not unequip" | `unequip <item>`, `unequip all` |
| 3 | "Can't understand torch but understands Torch" | Names matched with case, spaces and punctuation ignored, against localized name, raw `$item_` token and prefab name. Also fixed a null-deref that killed the whole lookup |
| 4 | Harvesting wood — "we started but didn't finish" | No prior code existed. Built chop and mine from scratch, with tool tier and stamina handled by Valheim |
| 5 | Crafting and repairing | `craft 20 wood arrows`, `repair` at a station |
| 6 | "Fully functional through text chat" | ~40 direct commands; everything else goes to the planner |
| 7 | Use Claude when he doesn't understand, recover intent | A verb only claims an order if it can find what was named, so `drop everything and follow me` reaches Claude instead of being answered literally |
| 8 | 3-minute job cap too short | `JobMinutes`, default 15, and the clock restarts after each run home |
| 9 | The full work loop | Pack fills → carries to the home chests → unloads → walks back → resumes |
| 10 | Named waypoints | `remember this as the mine`, `go to the mine`, `forget the mine` |
| 11 | Notice a blunt axe and go repair before working | `StartMend` errand: detour to a workbench, repair, return to the same spot |
| 12 | Patrolling camp guard | `guard the camp` — gears up, walks a ring sized from the buildings actually there, meets the deepest intruder first |
| 13 | Go back to his gravestone for his gear | Remembers where he fell, walks back, takes it, re-equips, heads home |
| 14 | Notice hunger, let people toss him food | Eats from his pack, asks when he has none (rotating lines, ruder after five minutes), walks up to 12 m for food dropped near him |
| 15 | Swear back when cussed out | Rate-limited comebacks; the planner is told to match the tone too |
| 16 | Join a boss hunting party / fight my target | `fight with me` — targets from around **you**, bosses first, never quits when hurt |
| 17 | Retaliate when attacked | Damage-driven: whatever actually hit him becomes the target at any range, for 15 s |
| 18 | Don't let adds pull him off the boss | Focus-fire lock while escorting a boss |
| 19 | Mule mode | `carry for me` — follows, hoovers up what you drop, runs full loads home by himself |
| 20 | Unequip and toss items from his bag | `drop`/`toss` (unequips first if held), counts (`drop 10 wood`), `give me` now walks to you |
| 21 | Drop on the floor when there aren't enough chests | Uses every chest at home; piles on the ground when none has room, and keeps working |
| 22 | "repair your stuff", "eat up" | Direct synonyms. Fixed `eat up` matching **turnip soup** via the `up` substring, and `bring me some wood` failing because `some` became part of the item name |
| 23 | A private repo | `github.com/apeltheory/valheim-companion`, secrets and decompiled game source excluded |
| 25 | Camp awareness across a multi-house base | `learn the camp` surveys via `Piece.GetAllPiecesInRadius`, saves centre and extent; guard patrols the real camp, mending finds a real bench |
| 26 | Voice — he hears you, replies in text | Plumbing done and tested, then **parked** by decision. See below |
| 28 | Drop the need to say his name | `ListenUnaddressed` (off by default): every nearby line gets one small Jev question — was this meant for him? — and silence is the answer to almost all of it. Saying his name skips the gate. Moving goods needs 0.90. An overheard line becomes a job or nothing; he never chimes in |
| 27 | Port the planner to Jev | Jev (TypeSafe System One) picks the action from all forty-five as one `choice` with calibrated confidence; `item` is read off the sentence in Python; Anthropic is called only for `chat`. Dormant without a key: the Claude planner stays the fallback, unchanged |
| 24 | Ask follow-up questions, accept replies without his name | Ask-and-listen primitive: one player, 25 seconds, one answer, and the answer can only resolve the question asked — it cannot start a job |

## Jev — WAITING ON A KEY

Written and tested against the wire contract in TypeSafe's official Python SDK
(`typesafe-sdk` 0.7.0: `POST /v1/systemone`, bearer auth, `state` + named `questions`,
answers carrying `choice`/`noul`/`score` with confidence). **Never run against the
live API** — early access is waitlisted and there is no key yet.

So the port is deliberately inert. With `TYPESAFE_API_KEY` blank the planner is the
Claude one it has always been, prompt and all; setting the key is the whole switch.
What to check on the first real call: that the answer names arrive back as `action`,
`named` and `tone`, that confidence is calibrated enough for `JEV_MIN_CONFIDENCE` to
mean anything at 0.40, and that the forty-five choice descriptions actually separate
the neighbours that used to need a paragraph of prompt — `escort` against `fight`,
`pile` against `drop_all`, `bring` against `withdraw`, `recipe` against `craft`.

The item reader is the part most likely to be wrong in play. It is pure Python with
no model behind it, so every phrasing it has not met is a possible miss; the corpus in
`tests/fixtures/jev_orders.json` holds the ones it has. A miss shows up as an empty item,
and for anything that empties a chest or a pack an empty item makes him ask rather than act.

**Rehearsing without a key.** `scripts/fake-jev.py` stands in for the API and holds the
planner to the published request contract; `scripts/rehearse.py` runs the corpus through a
real planner against it. Writing that harness caught three real defects that source review
had missed:

1. Junk items were sent to the twenty-five actions the plugin dispatches without ever
   reading `item` — harmless for `escort`, but `fight` and `harvest` do read it, so he
   would have answered *I see no there's a troll on us, deal with it to fight*.
2. Whole clauses were accepted as item names. Anything past five words is now treated as
   the reader having failed, which is safer than a filter that matches nothing.
3. The talk path is two calls back to back, Jev then Anthropic, and at the old timeouts
   could take 20 seconds against a plugin that abandons the request at 15. He would have
   given up on answers that were on their way. The budgets are now 5 and 8.

The third is the kind of thing only a rehearsal finds: every unit test passed throughout.

**Listening without his name.** The gate is the part of this port that most needs real-world
confirmation, because the corpus supplies its own answers. What to watch in the first session
with `ListenUnaddressed` on:

- Does ordinary chatter really score below 0.70? The corpus assumes 0.05-0.30 for lines like
  "let's dump this lot and head back", and that guess is the one most likely to be wrong.
- Does a polite unprefixed order clear it? "could you chop some wood for us" is assumed 0.94.
- Is 0.70 the right floor at all? Too low and he acts on conversations; too high and the
  feature does nothing and you go back to saying his name.

An unaddressed line costs two calls when it passes the gate and one when it does not, so watch
`MAX_JEV_CALLS` on a busy server. That budget wants to become a token or time budget rather
than a call count before any tick loop is built on top of it.

## Still to do

**Patrol walks into walls — deprioritised, 18 Sep.** The owner does not want time spent
here for now. Root-caused but deliberately not fixed, and the feature is left in place
rather than removed. `NextPost` is the only destination in `Companion.cs` computed
rather than taken from a real object, so it is the only one that can land inside a wall, a
cliff or the sea. It keeps the camp centre's height on sloping ground, the ring is sized by
the furthest outlying build, and the recovery gives up after eight failures — which is one try
per post, since there are only eight. Grounding each post to terrain and rejecting any that
lands inside a `Piece` is the fix. Jev cannot help here: it picks from a list, it cannot
produce a waypoint.

**No running tally of the base.** `Stock` already reads every chest in the surveyed camp and
counts his pack with them, but it is a live scan: he has to be standing there, the containers
must be in a loaded zone, and it caps at 40 chests. A saved tally, written on survey and on
each deposit, would let him answer away from home. That is persistence, not judgement — but
once it exists, "is our iron running low" is a real `score` question and "which trip matters
most" a real `choice`.

## Voice — PARKED

Decided against going further, 15 Sep. The plumbing is built, tested and dormant
(`Listen = false`, polls nothing while off), so it costs nothing to leave in place and
nothing to pick back up.

**Why parked.** The only version worth having is push-to-talk, because always-on listening
fails three ways at once: no voice-activity detection means chopped sentences or continuous
transcription; a room mic hears Discord, game audio and everyone else; and "Bjorn" is a poor
wake word — speech recognition renders it as Born, Byorn, Bjørn, B. John, all of which the
exact prefix match drops silently.

Push-to-talk fixes all three (the keypress *is* the wake signal) — but the key and mic have
to be on the machine being played on, which is the PC, not the laptop running Bjorn. That
means a client on the PC and binding the bridge past loopback. Real work, for a benefit that
is real but narrow: commanding him without taking hands off the game, since opening Valheim
chat means stopping.

**To unpark:** fuzzy wake-word matching (worth doing on its own merits), a push-to-talk
client on the PC, and a decision about exposing the bridge to the LAN. Latency to expect is
2-4s for a direct command, 10-18s if it falls through to Claude — fine for "chop wood",
useless for "look out".

### What exists now

Valheim has no voice chat of its own (checked: no VOIP class in 631 decompiled files), and
tapping a third-party voice mod's decoded audio would mean Harmony-patching another mod's
internals — fragile, version-locked, and needing identical installs on both machines.

The route taken instead skips the game entirely: speech is transcribed by anything you like,
posted to the bridge, collected by the plugin, and dispatched exactly as typed chat. Built
and tested without any audio: `POST /listen`, `GET /orders`, a bounded queue, token auth on
both, plugin polling behind a `Listen` config flag, and `scripts/say-to-bjorn.py` as the seam.

Still to choose: the transcriber. `whisper.cpp` vendored into `.tools/` would match how the
dotnet SDK and ILSpy are already handled, and runs offline. Open question is which machine
hears you — the laptop's mic works today if it is in the room; a mic on the PC would mean
binding the bridge past loopback, which is a real decision rather than a detail.

## Defects found by review, all fixed — 15 Sep

Twenty-five, across three workflows, every one in code written the same day and already
read back once. Recorded because the pattern matters more than the list: almost all of them
were code that is correct in isolation and wrong against the engine's actual behaviour.

**Silent stalls.** `Job.Mend` had no case in `NextGoal`, so the first blunt axe froze him
for good. `Peckish` never checked it had room, so a full pack plus food on the ground looped
forever — and sat above the haul that would have emptied the pack. An attacker he could not
path to was cleared and re-acquired every scan, freezing the job; an archer across a ravine
would have stopped him indefinitely.

**Never worked at all.** `Player.m_currentStation` is written only by `CraftingStation.Interact`,
which also opens the crafting window, and `UpdateStations` clears it on any frame that window
is not visible. A bot never opens it, so `GetCurrentCraftingStation()` reads null on a forge.
`repair` and `craft` — both asked for by name — could not have run.

**Silent deadlock.** `ItemData.IsWeapon()` is true for torches and bows. `Humanoid.EquipItem`
returns false and does nothing for a broken item, a return ignored at all three call sites.
A snapped axe is unequipped by the game, so `Blunt()` (which read the equipped tool) never
fired the mend errand, and `Wield` re-picked the broken axe and returned true anyway.

**Item loss.** `TombStone` carries a `Container`, so every chest scan found gravestones — a
haul run at a base where anyone had died would empty the load into the grave. `craft 20 wood
arrows` made 400 and consumed twenty times the wood.

**Engine mismatches.** Valheim slides a player past 38 degrees; `IsWalkable` accepted 48.
`IsWalkable` needs a ground hit on a mask with no water layer, so he stopped dead at any
shoreline. And `SetControls` calls `Jump()` internally *before* the reflected `m_moveDir`
correction lands, while `Character.Jump` throws the forward impulse along `m_moveDir` — so
every jump went wherever he last looked, not where he was walking.

## Done from the locomotion plan

Per-lane steering state. Six callers shared one stuck clock, one avoid commitment and one
sprint flag, so a fight interrupting a walk handed it back a clock it had not earned.
Fourteen hand-written resets were papering over it; there is now a record per lane and four
deliberate resets. Under review.

## Asked for during the first play session

| # | Asked for | Where it landed |
| --- | --- | --- |
| 27 | He ran at a wall instead of opening the door | Steering tries the handle before declaring the way shut. He had known how to work a door since 0.3.0; nothing in the movement code ever asked |
| 28 | "repair your axe" and "go chop down some trees" took the slow path | Loose word matching after every exact form fails, so natural phrasings resolve locally and offline instead of costing a call and fifteen seconds |
| 29 | Questions must not be obeyed | "What's the best wood to chop?" no longer sets him chopping. Opening word decides; "can you chop wood" is still an order |
| 30 | In-game bug reporting | Anyone types `bug <what happened>`. Captures position, job, target, body, hands, pack, surroundings **and what all twelve steering probes saw** |
| 31 | Automatic reporting over time | He records his own give-ups with the same geometry, deduplicated per spot per minute. `scripts/bugs.sh` ranks them by kind and by location |
| 32 | Answer off-topic questions in character | "Washington. A long row west, and I'd not fancy it." Correct answer, his voice, never breaks character |
| 33 | Tend the furnace, smelt ore | `tend the smelter` walks a circuit of every smelter, kiln, blast furnace and windmill within 40 m: output out, coal in, ore in, repeat until a full round shifts nothing |
| 34 | Bake in the Valheim wiki | Rejected as such. Recipes come from `ObjectDB` — the installed game's own data, exact for this version and any mods — and broader questions go to Claude. A wiki would be machinery to replace knowledge already present |
| 37 | "He's too far to hear me" | Not a hearing problem. Shouts bypass `Talker` entirely — `Chat.SendText` routes them to every player on the server with no distance check — but they arrive with a null `go`, so he dropped them when the shouter wasn't loaded. He now reads the name and position off the chat message itself. `/s Bjorn, ...` reaches him anywhere on the map |
| 36 | He could not walk to another player | `go to <name>`, `walk to <name>`, `find <name>`, `follow`/`stick with <name>`, `where is <name>`. Loaded players are live targets he can follow; anyone further off comes from `ZNet.GetPlayerList()`, which knows the whole server but only if they are sharing their position |
| 35 | Ambiguous recipe names | "How do I craft a spear" names the four spears and waits, using the follow-up window |

## Fixed from the first play session

Seven found in about forty minutes, against 29 from several hours of review — and of a
different kind. Review finds engine mismatches; play finds capability that exists but was
never wired into the behaviour that needed it.

- He picked a fight with a training dummy, which never dies, so the threat never cleared
- He claimed an empty pack while holding food — Valheim refuses a second helping of the
  same food, and one method was answering both "has he food" and "can he eat now"
- Repair demanded he already stand at a bench rather than walking to one
- Repair compared gear against the nearest station only, so a stone axe near a forge failed
- He stuck on small ledges: jumping was decided only after a direction was judged walkable,
  so he could never jump the thing that made it unwalkable
- He wedged behind a crafting station: 3.5 m of lookahead, 0.8 s of commitment, and scoring
  that weighted heading 10:1 over clearance, which steers straight back into the corner
- He ran at a closed door

## Fixed from reviewing the fixes

Four blockers, one of them introduced an hour earlier by the ledge fix:

- **The ledge fix locked him out of every roofed building.** The ground probe started 3 m up
  — above a house roof — so the ray landed on the roof and read as a two-metre wall
- A halt in deep water was permanent: regen is zeroed off the ground, so a halted swimmer
  floats there until someone walks to the machine
- The mend errand walked to a bench that could not take the tool, then back, forever
- Nothing ever put a tool back in his hands after the engine emptied them

## In flight

- **Chained thoughts** — turning every dead end into "can't do X because Y, so fix Y, then
  resume X". 13 `Halt()` calls and a dozen refusals are candidates. A workflow is designing
  the chains and separately attacking them for infinite loops; the shared errand
  architecture has to land before any chain is written.
- **Next features** — a workflow scouting survival, combat, work, awareness, movement and
  personality against the decompiled source, with every API claim adversarially checked.

## Next up

- **Ambiguity questions** — `eat meat` with cooked, serpent and lox in the pack currently
  picks one silently. The asking primitive is built; wiring it into item lookup is next.
- **Locomotion** — a third workflow is measuring sprint, jump, stuck detection and arrival
  against the real character controller.

## Camp awareness — built

`learn the camp` surveys out to 90 m using `Piece.GetAllPiecesInRadius` (a public registry
of every placed piece — no scene scanning), centres on the pieces rather than on where he
happens to stand, measures the extent, and saves both to config so he still knows the shape
of the base when most of it is unloaded. `what's in the camp` reports chests, beds, fires,
stations and a rough building count from clustering.

It changes three behaviours: `guard the camp` now patrols a ring sized to the **real** camp
instead of a fixed 30 m, watches the whole of it for intruders, mending finds the nearest
actual crafting station within 60 m instead of needing a hand-placed `workbench` waypoint,
and `self test` reports whether he knows the camp at all.

Still manual: `home` remains the specific spot he unloads at, because a camp centre is not
where the chests are. Worth revisiting once it has run.

### The API, for reference

The game gives us more than expected:

- `Piece.GetAllPiecesInRadius(point, radius, list)` — a public static registry query for
  every built piece. Better than scanning the scene.
- `EffectArea.IsPointInsideArea(point, EffectArea.Type.PlayerBase)` — the game's **own**
  test for "is this spot inside a player base". This is the native camp concept, already
  used for spawn suppression.
- `Piece.GetComfort()` and `GetAllComfortPiecesInRadius` — comfort is what makes a house a
  house.

The plan: `Bjorn, learn the camp` surveys out to ~80 m, clusters the pieces into buildings,
catalogues chests, workbenches, forges, fires and beds, and saves the camp's centre and
extent. After that he can patrol the *whole* camp rather than a ring around one point, find
the nearest real workbench to mend at instead of needing a hand-placed waypoint, choose a
chest cluster to unload into, and answer "what's in the camp".

## Rejected, with reasons

- **Building pieces.** Placement takes its position only from a ghost positioned by a
  raycast out of the game camera. `SetLookDir` cannot pitch, so aiming at the ground needs
  reflection into private `m_lookPitch`; reach is 5 m from the eye, rotation is a private
  22.5° index, auto-snap moves the result up to 0.5 m with no error channel, and nothing
  refuses an unsupported piece — it just deletes itself a second later. Multi-day work with
  a high chance of looking like it works and quietly not.
- **Planner returning a sequence of actions.** The real intelligence leap, but also how you
  get a bot that confidently does three wrong things in a row. Revisit after local chains
  land and after he has actually run.
