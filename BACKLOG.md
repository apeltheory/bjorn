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
| 24 | Ask follow-up questions, accept replies without his name | Ask-and-listen primitive: one player, 25 seconds, one answer, and the answer can only resolve the question asked — it cannot start a job |

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
