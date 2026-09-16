using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace Bjorn {
[BepInPlugin("local.bjorn.companion", "Bjorn", "0.4.0")]
public class Companion : BaseUnityPlugin {
    internal static Companion Instance;
    // Jobs are the only orders that need steering every tick. Everything else
    // finishes inside Receive or Decide and leaves the bot standing.
    enum Job { None, Follow, Come, Home, Bed, Fetch, Harvest, Chop, Mine, Fight, Haul, Mend, Resume, Patrol, Grave, Deliver, Escort, Mule, Tend }
    // Outcome of one tick of walking toward a point.
    enum Step { Moving, Arrived, Blocked, Stuck }
    // Every job and every overlay walks through StepToward, so they share the rules.
    // What they must NOT share is the bookkeeping: a fight that interrupts a walk was
    // handing the walk back a stuck clock it had not earned, and a retargeted sweep
    // inherited the last target's. One record per caller, and the fourteen hand-written
    // resets that used to paper over it are gone.
    enum Lane { Job, Fight, Flee, Pickup, Backoff }
    class Steering {
        public float touched;        // when this lane last ran
        public Vector3 goal;         // what it was walking to
        public Vector3 previous;     // where he stood last tick
        public float stuckTime;      // seconds frozen on the spot
        public float avoidUntil;     // committed to rounding something until this time
        public Vector3 avoidDirection;
        public bool sprinting;       // held across ticks, for hysteresis
    }
    // Positional, so a new Lane needs a new record here - the mismatch would be an
    // IndexOutOfRange at runtime, not a compile error.
    readonly Steering[] lanes = { new Steering(), new Steering(), new Steering(), new Steering(), new Steering() };
    Steering Lane_(Lane lane) { return lanes[(int)lane]; }
    // Forces a clean start where the goal itself has not moved far enough to notice:
    // the next tree in a sweep may be three paces away, and the clock he ran up
    // against the last one is not his.
    void Fresh(Lane lane) { Lane_(lane).touched = 0f; }
    void FreshAll() { foreach (var s in lanes) s.touched = 0f; }
    const float TargetSeconds = 30f; // Abandon one tree, rock, or foe that will not fall.
    ConfigEntry<string> botName, tokenFile, homePosition, bedPosition, waypoints, campCentre;
    ConfigEntry<float> campRadius;
    Vector3 camp;
    float campSpan;
    bool campKnown;
    ConfigEntry<float> jobRadius, jobMinutes, guardRadius;
    ConfigEntry<bool> botEnabled, defendSelf, pileOver, listen;
    ConfigEntry<KeyboardShortcut> toggleKey;
    UnityWebRequest pendingRequest;
    Harmony harmony;
    Job job;
    Player target;
    Bed bed;
    Vector3 destination;
    Vector3 anchor;
    string sweepFilter;
    int collected, hauls;
    float sweepUntil, reachedAt, swingUntil;
    Component sweepTarget;
    Job errand;      // The interrupted real job, held for the length of a detour chain.
    int detours;     // Detours this job has spawned. Only a new order clears it.
    int futile;      // Consecutive detours that changed nothing in the world.
    bool mendRefused;
    const int MaxDetours = 12, MaxFutile = 3;
    Character threat;      // What he is fighting right now, whatever job he was on.
    Character hurtBy;      // Whoever last actually landed a hit on him.
    float hurtAt;
    Vector3 fightFrom;     // Where a self-defence fight started, so he does not chase far.
    float threatScan, lastShout, grazeCheck, morselScan, lastBeg, morselAt, hungrySince, handsChecked;
    int begged;
    ItemDrop morsel;   // Food on the ground he is walking over to collect.
    ItemDrop salvage;  // Anything on the ground he is collecting while acting as a mule.
    Smelter furnace;   // The smelter or kiln he is walking to.
    int lastRound;     // Tally at the end of the last circuit of the fires.
    float salvageScan, salvageAt;
    int muled;
    int patrolStep, postsFailed;
    bool scoring;    // Whether the current breakable counts toward the tally.

    float patrolRing, postUntil;
    Vector3 graveSpot;
    Vector3 dryGround;   // Last solid ground he stood on; the way out of deep water.
    bool graveKnown;
    Player deliverTo;      // Who is owed a delivery once the fetching is done.
    string deliverFilter;
    readonly HashSet<int> visited = new HashSet<int>();
    // Spots he has dumped a load at during this job. His own sweeps skip them, or he
    // would collect his own pile, fill up, carry it back and pile it again forever.
    readonly List<Vector3> piles = new List<Vector3>();
    readonly Dictionary<string, Vector3> places = new Dictionary<string, Vector3>();
    bool busy;
    int generation;
    float lastOrder, lastDeaf, lastBanter, lastExplain, doorUntil;
    Player asked;            // Who he put a question to, and is listening to.
    string[] choices;        // Accepted answers; null means yes or no.
    Action<string> onAnswer;
    float askedUntil;
    int bantered;
    float jumpUntil;
    static readonly FieldInfo move = AccessTools.Field(typeof(Character), "m_moveDir");
    static readonly FieldInfo autorun = AccessTools.Field(typeof(Player), "m_autoRun");
    // Humanoid.ShowHandItems is protected, and it is the only thing that undoes
    // HideHandItems - which SetCraftingStation and swimming both call.
    static readonly MethodInfo showHands = AccessTools.Method(typeof(Humanoid), "ShowHandItems", new[] { typeof(bool), typeof(bool) });
    // Spoken phrase to Valheim's emote id. Only "sit" is a held pose; the rest play once.
    static readonly Dictionary<string, string> emotes = new Dictionary<string, string> {
        {"wave", "wave"}, {"sit", "sit"}, {"sit down", "sit"}, {"challenge", "challenge"},
        {"cheer", "cheer"}, {"no no no", "nonono"}, {"thumbs up", "thumbsup"}, {"point", "point"},
        {"blow kiss", "blowkiss"}, {"bow", "bow"}, {"cower", "cower"}, {"cry", "cry"},
        {"despair", "despair"}, {"flex", "flex"}, {"beckon", "comehere"}, {"headbang", "headbang"},
        {"kneel", "kneel"}, {"laugh", "laugh"}, {"roar", "roar"}, {"shrug", "shrug"},
        {"dance", "dance"}, {"relax", "relax"}, {"toast", "toast"}, {"rest", "rest"},
        {"vibe", "vibe"}, {"love you", "loveyou"},
    };
    internal bool Active => botEnabled.Value && Player.m_localPlayer != null;
    bool IsSweep => job == Job.Fetch || job == Job.Harvest || job == Job.Chop || job == Job.Mine || job == Job.Fight;
    float SweepRadius => Mathf.Clamp(jobRadius.Value, 5f, 100f);
    float SweepSeconds => Mathf.Clamp(jobMinutes.Value, 0.5f, 120f) * 60f;
    float GuardSpan => Mathf.Clamp(guardRadius.Value, 8f, 120f);

    void Awake() {
        Instance = this;
        botEnabled = Config.Bind("Bot", "Enabled", true, "Automatic controls. Toggle in-game with ToggleKey; manual mode ignores orders.");
        toggleKey = Config.Bind("Bot", "ToggleKey", new KeyboardShortcut(KeyCode.F8), "Switch between manual and bot control.");
        botName = Config.Bind("Bot", "Name", "Bjorn", "Address chat with this name followed by a space, comma, or colon.");
        homePosition = Config.Bind("Bot", "HomePosition", "", "Legacy single home position; imported into Waypoints on first load.");
        bedPosition = Config.Bind("Bot", "BedPosition", "", "Saved bed position written by 'claim this bed'.");
        waypoints = Config.Bind("Bot", "Waypoints", "", "Named places as name=x,y,z separated by semicolons. 'home' is where he unloads a full pack.");
        jobRadius = Config.Bind("Bot", "JobRadius", 25f, "How far from the spot he was ordered a gathering job may range, in metres (5-100).");
        jobMinutes = Config.Bind("Bot", "JobMinutes", 15f, "How long one load of a gathering job may take before he gives up, in minutes (0.5-120). The clock restarts after each run home.");
        campCentre = Config.Bind("Bot", "CampCentre", "", "Centre of the surveyed camp, written by 'learn the camp'.");
        campRadius = Config.Bind("Bot", "CampRadius", 30f, "How far the surveyed camp reaches from its centre, in metres.");
        guardRadius = Config.Bind("Bot", "GuardRadius", 30f, "How far from a camp's centre counts as inside it while on guard, in metres (8-120).");
        pileOver = Config.Bind("Bot", "PileWhenNoChest", true, "On a run home, leave anything the chest cannot take on the ground rather than stopping the job. Dropped items persist in Valheim.");
        defendSelf = Config.Bind("Bot", "DefendSelf", true, "Fight back at anything hostile that comes close while doing other work. Guard duty ignores this and always fights.");
        tokenFile = Config.Bind("Bridge", "TokenFile", "/home/apel-xps/Work/valheim-companion/runtime/bridge.token", "Local bridge token file.");
        listen = Config.Bind("Bridge", "Listen", false, "Poll the bridge for spoken orders. Leave off unless something is transcribing speech into it.");
        LoadPlaces();
        LoadCamp();
        harmony = new Harmony("local.bjorn.companion");
        harmony.PatchAll();
        Application.runInBackground = true;
        StartCoroutine(ListenLoop());
        Logger.LogInfo("Bjorn ready. Any nearby player can address orders to Bjorn.");
    }
    void Update() {
        if (asked && Time.unscaledTime >= askedUntil) Forget("No matter, then.");
        var player = Player.m_localPlayer;
        if (!player || !Application.isFocused || !toggleKey.Value.IsDown()) return;
        generation++; // Reject late decisions even if bot mode is re-enabled immediately.
        Halt();
        pendingRequest?.Abort();
        autorun.SetValue(player, false);
        player.SetControls(Vector3.zero, false, false, false, false, false, false, false, false, false, false);
        move.SetValue(player, Vector3.zero);
        botEnabled.Value = !botEnabled.Value;
        Config.Save();
        string status = botEnabled.Value ? "Bjorn: BOT control — awaiting orders" : "Bjorn: MANUAL control — you have control";
        MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, status);
        Logger.LogInfo(status);
    }
    void OnDestroy() { harmony?.UnpatchSelf(); Instance = null; }
    void Say(string text) {
        if (Chat.instance && !string.IsNullOrWhiteSpace(text)) {
            Chat.instance.SendText(Talker.Type.Normal, text.Substring(0, Math.Min(180, text.Length)));
            Logger.LogInfo("Said: " + text);
        }
    }
    void Halt(string reason = null) {
        job = Job.None; errand = Job.None; target = null; bed = null; sweepTarget = null; threat = null;
        FreshAll();
        furnace = null; lastRound = 0;
        asked = null; choices = null; onAnswer = null;
        skippedDrops.Clear(); unreachable.Clear();
        deliverTo = null; deliverFilter = null; morsel = null;
        // piles is NOT cleared here. FinishSweep halts and a chain re-enters at once,
        // so clearing would hand the next sweep an empty skip list and let him collect
        // the load he just dumped, carry it home, and dump it again.
        sweepFilter = null; visited.Clear(); reachedAt = 0f;
        if (reason != null) { Logger.LogInfo("Stopped: " + reason); Say(reason); }
    }
    // Valheim's own pickup gives up on carry weight well before the last slot fills,
    // so a haul has to trigger on either.
    static bool Loaded(Player player) {
        return !player.GetInventory().HaveEmptySlot() ||
               player.GetInventory().GetTotalWeight() >= player.GetMaxCarryWeight() * 0.9f;
    }
    // Clear any running job and start a fresh one from the bot's current spot.
    void Begin(Job next) {
        Halt();
        job = next;
        // Job.Resume falls back to `anchor`, which only Sweep and Guard ever wrote - so
        // on a fresh session a Resume after a haul walked to world origin. Where the
        // order was given is always a safe answer.
        anchor = Player.m_localPlayer ? Player.m_localPlayer.transform.position : Vector3.zero;
        piles.Clear();
        detours = 0; futile = 0; mendRefused = false;
    }
    static bool Any(string value, params string[] options) { return options.Contains(value); }

    // ---- Asking, and listening for the answer ----------------------------
    //
    // When he asks something, the next reply from that same player counts even if it
    // does not start with his name. The window is deliberately narrow: one player,
    // 25 seconds, one answer, and the answer can ONLY resolve the question he asked -
    // it cannot start an arbitrary job. Anything that does not parse as an answer is
    // ignored and the window stays open, so ordinary chat is never hijacked.
    void AskFor(Player who, string question, string[] options, Action<string> answer) {
        if (!who) return;
        asked = who; choices = options; onAnswer = answer;
        askedUntil = Time.unscaledTime + 25f;
        Say(question);
    }
    static readonly string[] yesWords = { "yes", "aye", "yeah", "yep", "sure", "ok", "okay", "do it", "go on", "go ahead", "please" };
    static readonly string[] noWords = { "no", "nay", "nope", "dont", "don't", "leave it", "forget it", "never mind", "nevermind" };
    void Forget(string why) {
        if (!asked) return;
        asked = null; choices = null; onAnswer = null;
        if (why != null) Say(why);
    }
    void Answered(string text) {
        string reply = Bare(text);
        if (reply == null) return;
        string pick = null;
        if (choices == null) {
            if (Any(reply, yesWords)) pick = "yes";
            else if (Any(reply, noWords)) pick = "no";
        } else {
            // "cooked" answers "Cooked meat"; so does the whole name.
            pick = choices.FirstOrDefault(c => KeyMatches(c, Key(reply)) || KeyMatches(reply, Key(c)));
            if (pick == null && Any(reply, noWords)) { Forget("As you like."); return; }
        }
        // Not an answer to what he asked: leave it alone and keep listening.
        if (pick == null) return;
        var act = onAnswer;
        asked = null; choices = null; onAnswer = null;
        Logger.LogInfo("Took '" + reply + "' as the answer.");
        if (pick != "no") act?.Invoke(pick); else Say("Right, I'll leave it.");
    }
    // Abuse gets answered in kind. Matched on whole words so "assess" and "Scunthorpe"
    // do not set him off.
    static readonly HashSet<string> curses = new HashSet<string> {
        "fuck", "fucking", "fucker", "fucked", "shit", "shite", "shitty", "bullshit",
        "bastard", "cunt", "prick", "dick", "arse", "ass", "asshole", "arsehole",
        "twat", "wanker", "bollocks", "bitch", "damn", "goddamn", "piss", "crap",
        "tosser", "knob", "idiot", "moron", "useless", "stupid", "pathetic", "worthless",
    };
    static readonly string[] comebacks = {
        "Fuck me sideways, the little jarl has opinions. Swing an axe before you swing your mouth.",
        "Piss off. I have shat in colder forests than the one you were born in.",
        "By Odin's hairy arse, keep talking and I will go back to the fucking trees.",
        "Big words from a man who cannot carry his own bloody wood. Fuck you too.",
        "Say that again and I will drop this whole bastard load in the sea.",
        "You kiss your mother with that mouth? Mine is dead, so I will say what I like.",
        "Careful, little one. I have butchered things politer than you before breakfast.",
        "Ha! That is the most spirit you have shown all sodding day. Now help me carry this shit.",
        "I am a grown fucking Viking and I will not be spoken to like a draugr's arse.",
        "Grumble all you want, you soft-handed shit. The wood still needs chopping.",
        "Arse. Piss. Bollocks. There, now we have both said our piece. Back to work.",
        "That is rich from someone who dies to greylings. Fuck off and let me swing.",
    };
    static bool Cursing(string simple) {
        foreach (var word in simple.Split(new[] { ' ', ',', '.', '!', '?', ';', ':', '\'', '"', '-' }, StringSplitOptions.RemoveEmptyEntries))
            if (curses.Contains(word)) return true;
        return false;
    }
    // Splits "deposit iron" into the verb and the original-case remainder. `simple`
    // only differs from `order` in case and trailing punctuation, so offsets line up.
    static bool Prefixed(string simple, string order, string head, out string rest) {
        rest = null;
        if (!simple.StartsWith(head, StringComparison.Ordinal)) return false;
        rest = order.Substring(head.Length).Trim(' ', ',', '.', '!', '?');
        return rest.Length > 0;
    }
    // People do not say "wood", they say "some wood" or "the wood, please". Those pad
    // words otherwise become part of the name and match nothing.
    static readonly HashSet<string> filler = new HashSet<string> {
        "some", "a", "an", "the", "my", "your", "our", "any", "more", "all",
        "please", "now", "up", "of", "that", "this", "it", "for", "me", "us",
    };
    static string Bare(string text) {
        string value = Normalize(text);
        if (value == null) return null;
        var words = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 0 && filler.Contains(words[0])) words.RemoveAt(0);
        while (words.Count > 0 && filler.Contains(words[words.Count - 1])) words.RemoveAt(words.Count - 1);
        return words.Count == 0 ? null : string.Join(" ", words);
    }
    // People do not phrase orders the way a command table is written. "go chop down
    // some trees" and "repair your axe" both used to cost a fifteen-second round trip
    // to the planner, and a call off the budget, for something he can do this instant
    // and could still do with the planner down. Matched on whole words, only after
    // every exact and prefixed form has failed.
    static bool Mentions(string[] words, params string[] any) {
        foreach (var word in words)
            foreach (var option in any)
                if (word == option) return true;
        return false;
    }
    bool Loosely(string simple, Player speaker) {
        var words = simple.Split(new[] { ' ', ',', '.', '!', '?', ';', ':', '\'' }, StringSplitOptions.RemoveEmptyEntries);
        // "don't follow me" and "no, stop chopping" must not be read as orders to do it.
        if (Mentions(words, "dont", "don't", "never", "nor", "instead")) return false;
        // Nor may a question be. "What's the best wood to chop?" mentions chopping and
        // wood and is not an instruction to go and do it. Opening with what, why, how
        // and the rest marks a question; "can you chop wood" is a polite order and is
        // deliberately left alone.
        if (words.Length > 0 && Any(words[0], "what", "whats", "what's", "which", "why",
                                    "when", "who", "whose", "where", "how", "is", "was", "does", "did"))
            return false;
        if (Mentions(words, "repair", "mend", "fix", "repaired")) { Repair(); return true; }
        if (Mentions(words, "chop", "chopping", "fell", "felling", "cut") &&
            Mentions(words, "tree", "trees", "wood", "timber", "log", "logs")) { Chop(); return true; }
        if (Mentions(words, "mine", "mining", "dig") &&
            Mentions(words, "stone", "ore", "rock", "rocks", "copper", "tin", "iron", "silver")) { Mine(); return true; }
        if (Mentions(words, "guard", "patrol", "defend", "watch") &&
            Mentions(words, "camp", "base", "home", "village", "bounds")) { Guard(null); return true; }
        if (Mentions(words, "forage", "berries", "mushrooms")) { Harvest(null); return true; }
        if (Mentions(words, "follow")) { Follow(speaker); return true; }
        return false;
    }
    static string Normalize(string text) {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Trim().Trim('.', '!', '?').ToLowerInvariant();
    }
    // Anyone on the server can file one, addressed to nobody: "bug he got stuck at the
    // doorway". The note is the least useful part - what matters is the state captured
    // with it, especially what the steering probes saw at that moment.
    bool Reported(string text, Player speaker) {
        if (!text.StartsWith("bug", StringComparison.OrdinalIgnoreCase)) return false;
        // "bugs" and "bugger" are not bug reports.
        if (text.Length > 3 && " ,:.!-".IndexOf(text[3]) < 0) return false;
        string note = text.Length > 3 ? text.Substring(3).Trim(' ', ',', ':', '.', '!', '-') : "";
        var me = Player.m_localPlayer;
        var report = new StringBuilder();
        report.AppendLine("== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ==");
        report.AppendLine("from   : " + (speaker ? speaker.GetPlayerName() : "someone out of sight"));
        report.AppendLine("note   : " + (note.Length > 0 ? note : "(none given)"));
        if (!me) {
            report.AppendLine("state  : no local player - he was not spawned");
        } else {
            Vector3 at = me.transform.position;
            report.AppendLine("at     : " + Vector3ToConfig(at) + " in the " + Spaced(Heightmap.FindBiome(at).ToString()));
            report.AppendLine("job    : " + job + (errand != Job.None ? " (detour from " + errand + ")" : "") + " - " + JobWord());
            report.AppendLine("target : player=" + (target ? target.GetPlayerName() : "none") +
                              " sweep=" + (sweepTarget ? sweepTarget.name : "none") +
                              " threat=" + (threat ? Localization.instance.Localize(threat.m_name) : "none") +
                              " filter=" + (sweepFilter ?? "none"));
            report.AppendLine("body   : health " + Mathf.RoundToInt(me.GetHealth()) + "/" + Mathf.RoundToInt(me.GetMaxHealth()) +
                              ", stamina " + Mathf.RoundToInt(me.GetStamina()) + "/" + Mathf.RoundToInt(me.GetMaxStamina()) +
                              ", foods " + me.GetFoods().Count + "/3, swimming=" + me.IsSwimming());
            var held = Held(me);
            report.AppendLine("hands  : " + (held != null
                ? Localization.instance.Localize(held.m_shared.m_name) + " " + held.m_durability.ToString("0") + "/" + held.GetMaxDurability().ToString("0")
                : "EMPTY"));
            report.AppendLine("pack   : " + string.Join(", ", me.GetInventory().GetAllItems()
                .GroupBy(i => Localization.instance.Localize(i.m_shared.m_name))
                .Select(g => g.Key + " x" + g.Sum(i => i.m_stack)).Take(20)));
            report.AppendLine("around : " + Nearby<Container>(at, 8f).Count(IsChest) + " chests, " +
                              StationsAround(at, 8f).Count + " stations, " +
                              Nearby<Door>(at, 4f).Count() + " doors, " +
                              Nearby<ItemDrop>(at, 12f).Count(d => !d.IsPiece()) + " drops");
            report.AppendLine("places : " + (places.Count == 0 ? "none" : string.Join(", ", places.Keys)) +
                              (campKnown ? "; camp " + Mathf.RoundToInt(campSpan) + "m at " + Vector3ToConfig(camp) : "; no camp"));
            // The geometry at the moment of the complaint. This is the part that turns
            // "he got stuck on something" into an answer.
            Vector3 facing = target ? target.transform.position - at : me.transform.forward;
            facing.y = 0f;
            if (facing.sqrMagnitude < 0.01f) facing = Vector3.forward;
            report.AppendLine("steering: " + Probes(at, facing.normalized,
                LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain")));
        }
        string path = null;
        try {
            string folder = Path.GetDirectoryName(tokenFile.Value);
            path = Path.Combine(folder, "reports.log");
            File.AppendAllText(path, report.ToString() + "\n");
        } catch (Exception problem) {
            Logger.LogWarning("Could not write the report: " + problem.Message);
        }
        Logger.LogInfo("BUG REPORT\n" + report);
        Say(path != null ? "Noted. I've written down where I was and what I could see." : "Noted, though I could not write it down.");
        return true;
    }
    // He already knows when he has failed - he says so out loud. Recording those
    // moments beats a snapshot on a timer, which would mostly catch him walking along
    // quite happily.
    readonly Dictionary<string, float> stumbles = new Dictionary<string, float>();
    void Stumble(string what) {
        var me = Player.m_localPlayer;
        Vector3 at = me ? me.transform.position : Vector3.zero;
        // One line per distinct failure per five-metre square per minute. A bot wedged
        // in a corner would otherwise write the same line fifty times a second.
        string key = what + "@" + Mathf.RoundToInt(at.x / 5f) + "," + Mathf.RoundToInt(at.z / 5f);
        if (stumbles.TryGetValue(key, out float last) && Time.time - last < 60f) return;
        stumbles[key] = Time.time;
        string line = "-- " + DateTime.Now.ToString("HH:mm:ss") + " " + what +
                      " | job=" + job + (errand != Job.None ? "/" + errand : "") +
                      " | at=" + Vector3ToConfig(at);
        if (me) {
            Vector3 facing = target ? target.transform.position - at
                : (destination != Vector3.zero ? destination - at : me.transform.forward);
            facing.y = 0f;
            if (facing.sqrMagnitude < 0.01f) facing = Vector3.forward;
            var held = Held(me);
            line += " | hands=" + (held != null ? Localization.instance.Localize(held.m_shared.m_name) : "EMPTY") +
                    " | probes=" + Probes(at, facing.normalized,
                        LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain"));
        }
        try { File.AppendAllText(Path.Combine(Path.GetDirectoryName(tokenFile.Value), "reports.log"), line + "\n"); }
        catch { }
        Logger.LogInfo(line);
    }
    internal void Receive(GameObject source, long senderId, string text) {
        var me = Player.m_localPlayer;
        if (text == null) return;
        var prefix = botName.Value;
        bool addressed = text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && text.Length > prefix.Length &&
                         " ,:".IndexOf(text[prefix.Length]) >= 0;
        bool listening = asked && Time.unscaledTime < askedUntil;
        if (!addressed && !listening) {
            // A bug report is not an order. It works in manual mode, from any player,
            // and whether or not he can see who spoke.
            if (Reported(text, source ? source.GetComponent<Player>() : null)) return;
            return;
        }
        if (!Active) {
            Logger.LogInfo("Addressed order ignored: bot is in manual mode or not spawned.");
            return;
        }
        // Nearby chat already supplies the speaking character. Object creation IDs
        // need not match the peer ID delivering a chat RPC through a server.
        var speaker = source ? source.GetComponent<Player>() : null;
        if (!speaker && !source)
            speaker = Player.GetAllPlayers().FirstOrDefault(p =>
                p.GetComponent<ZNetView>()?.GetZDO()?.GetOwner() == senderId);
        if (speaker == me) {
            Logger.LogInfo("Addressed order ignored: sent from Bjorn's own character.");
            return;
        }
        if (!speaker) {
            Logger.LogInfo("Addressed order ignored: speaking player is not loaded nearby.");
            return;
        }
        Dispatch(speaker, text, addressed);
    }
    // Spoken orders arrive here from the bridge. They take the identical path to
    // typed chat - same name prefix, same cooldown, same safety - so listening can
    // never do anything typing could not. The nearest player is treated as the
    // speaker, since that is who is talking to him.
    internal void Spoken(string text) {
        var me = Player.m_localPlayer;
        if (!Active || string.IsNullOrWhiteSpace(text) || !me) return;
        var speaker = Player.GetAllPlayers()
            .Where(other => other && other != me && Vector3.Distance(other.transform.position, me.transform.position) <= 40f)
            .OrderBy(other => Vector3.Distance(other.transform.position, me.transform.position)).FirstOrDefault();
        if (!speaker) { Logger.LogInfo("Heard '" + text + "' but nobody is near enough to be speaking."); return; }
        var prefix = botName.Value;
        bool addressed = text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && text.Length > prefix.Length &&
                         " ,:".IndexOf(text[prefix.Length]) >= 0;
        if (!addressed && !(asked && Time.unscaledTime < askedUntil)) {
            Logger.LogInfo("Heard '" + text + "' but it was not addressed to me.");
            return;
        }
        Logger.LogInfo("Heard: " + text);
        Dispatch(speaker, text, addressed);
    }
    void Dispatch(Player speaker, string text, bool addressed) {
        var me = Player.m_localPlayer;
        var prefix = botName.Value;
        if (!addressed) {
            // Only the person he asked can answer, and only that question.
            if (speaker == asked) Answered(text);
            return;
        }
        // A fresh order by name supersedes whatever he was waiting to hear.
        Forget(null);
        var order = text.Substring(prefix.Length).Trim(' ', ',', ':');
        if (order.Length == 0 || order.Length > 500) return;
        var simple = order.ToLowerInvariant().TrimEnd('.', '!');
        var plain = simple.TrimEnd('?');
        if (Any(plain, "what can you do", "help", "commands", "what commands do you know")) { Help(); return; }
        if (Any(simple, "stay", "stop", "wait", "wait here", "stay here")) {
            generation++; Halt("I'll hold here."); return;
        }
        // Answered before the cooldown gate so an insult always lands, but rate-limited
        // so he cannot be goaded into spamming the whole server's chat.
        if (Cursing(simple)) {
            if (Time.unscaledTime - lastBanter > 4) {
                lastBanter = Time.unscaledTime;
                Say(comebacks[bantered++ % comebacks.Length]);
            }
            return;
        }
        // Everything past this point acts on the world, so it waits its turn behind a
        // pending planner request and behind the order cooldown.
        if (busy || Time.unscaledTime - lastOrder < 2) {
            // Say so rather than going quiet: silence is indistinguishable from not
            // having heard, and prompts the rephrase that blocks the next order too.
            Logger.LogInfo("Addressed order ignored: planner busy or command cooldown.");
            if (Time.unscaledTime - lastDeaf > 5) { lastDeaf = Time.unscaledTime; Say(busy ? "One thing at a time — I'm still thinking." : "A moment."); }
            return;
        }
        Logger.LogInfo("Order: " + order);
        lastOrder = Time.unscaledTime;
        if (Any(simple, "follow", "follow me")) { Follow(speaker); return; }
        if (Any(simple, "come", "come here", "come to me", "over here")) { Come(speaker); return; }
        if (Any(plain, "inventory", "what are you carrying")) { Inventory(); return; }
        if (Any(plain, "status", "report", "how are you", "how are you holding up", "how do you fare")) { Status(); return; }
        if (Any(plain, "where are you", "where", "location", "position")) { Where(); return; }
        if (Any(plain, "look around", "what do you see", "scan", "what is nearby", "anything nearby")) { Scan(); return; }
        if (Any(plain, "self test", "selftest", "sound off", "check yourself", "diagnostics", "are you working")) { SelfTest(); return; }
        if (Any(simple, "tend the fires", "tend the smelter", "tend the furnace", "tend the kiln", "work the smelter",
                        "feed the smelter", "feed the furnace", "smelt", "tend", "mind the fires")) { Tend(null); return; }
        if (Any(simple, "learn the camp", "survey the camp", "look over the camp", "study the camp", "learn this place")) { Survey(true); return; }
        if (Any(plain, "what is in the camp", "whats in the camp", "describe the camp", "tell me about the camp", "how big is the camp")) { CampReport(null); return; }
        if (Any(simple, "set home")) { RememberPlace("home"); return; }
        if (Any(plain, "what places do you know", "list places", "places", "waypoints", "where can you go")) { ListPlaces(); return; }
        // Bed phrases are matched before the "go to <place>" and "remember this as
        // <place>" forms so a bed never becomes a waypoint called "bed".
        if (Any(simple, "claim this bed", "claim bed", "remember this bed")) { ClaimNearbyBed(); return; }
        if (Any(simple, "go to bed", "go bed", "sleep")) { GoToBed(); return; }
        if (Any(simple, "go home", "return home", "come home")) { GoHome(); return; }
        if (Any(simple, "repair", "repair your gear", "repair your stuff", "repair your kit", "repair yourself",
                        "fix your gear", "fix your stuff", "mend your gear", "mend your kit", "mend")) { Repair(); return; }
        if (Any(simple, "eat", "eat up", "eat something", "have a bite", "get some food in you", "feed yourself")) { EatBest(); return; }
        if (Any(simple, "gather", "loot", "pick up the loot", "collect the drops", "pick up items")) { Fetch(null); return; }
        if (Any(simple, "harvest", "forage", "pick berries", "pick what you can")) { Harvest(null); return; }
        if (Any(simple, "chop", "chop wood", "cut wood", "fell trees", "chop trees", "gather wood")) { Chop(); return; }
        if (Any(simple, "mine", "mine stone", "mine ore", "break rocks", "gather stone")) { Mine(); return; }
        if (Any(simple, "fight", "attack", "defend me", "defend", "guard me", "kill it")) { Fight(null); return; }
        if (Any(simple, "guard", "patrol", "stand guard", "guard the camp", "patrol the camp", "watch the camp", "guard home", "keep watch")) { Guard(null); return; }
        if (Any(simple, "fight with me", "help me fight", "join me", "on me", "watch my back", "with me", "cover me", "let's hunt", "lets hunt", "boss hunt", "to arms")) { Escort(speaker); return; }
        if (Any(simple, "mule", "be my mule", "carry for me", "carry my loot", "haul for me", "pick up after me", "porter")) { Mule(speaker); return; }
        if (Any(simple, "take it home", "take it to base", "drop it at base", "unload at home", "run it home", "take it back")) { HaulNow(speaker); return; }
        if (Any(simple, "gear up", "arm yourself", "put your gear on")) { GearUp(me); Inventory(); return; }
        if (Any(simple, "get your gear", "fetch your grave", "go to your grave", "recover your gear")) {
            if (graveKnown) AfterRespawn(); else Say("I have no grave to go back to.");
            return;
        }
        if (Any(simple, "take everything", "take all", "empty the chest", "loot the chest")) { Withdraw(null); return; }
        if (Any(simple, "drop everything", "drop all", "drop your pack")) { DropAll(); return; }
        if (Any(simple, "drop it here", "dump it", "dump your pack", "pile it here", "pile it up", "leave it here", "stack it here")) {
            int piled = Pile(me);
            Say(piled == 0 ? "I've nothing worth piling up." :
                "Piled " + piled + (piled == 1 ? " stack" : " stacks") + " here. My gear stays with me.");
            return;
        }
        if (Any(simple, "feed the fire", "stoke the fire", "tend the fire", "add wood to the fire")) { FeedFire(); return; }
        if (Any(simple, "open the door", "open door", "open")) { UseDoor(true); return; }
        if (Any(simple, "close the door", "close door", "close", "shut the door")) { UseDoor(false); return; }
        if (Any(simple, "deposit", "stash", "unload", "empty your pack", "put everything in the chest")) { Deposit(null); return; }
        if (Any(simple, "unequip all", "put your gear away", "stow your gear")) { UnequipAll(); return; }
        // Each verb below only claims the order if it can actually find what was
        // named. "drop everything and follow me" or "pick a fight" find nothing, so
        // they fall past these lines to the planner, which reads the whole sentence.
        string rest;
        foreach (var lead in new[] { "how do i make ", "how do you make ", "how is ", "how do i craft ",
                                     "what do i need for ", "what do you need for ", "what does it take for ",
                                     "recipe for ", "whats in ", "what's in ", "what makes " })
            if (Prefixed(simple, order, lead, out rest) && Recipes(speaker, rest)) return;
        if (Prefixed(simple, order, "remember this as ", out rest) && RememberPlace(rest)) return;
        if (Prefixed(simple, order, "remember this place as ", out rest) && RememberPlace(rest)) return;
        if (Prefixed(simple, order, "remember here as ", out rest) && RememberPlace(rest)) return;
        if (Prefixed(simple, order, "tend ", out rest) && Tend(rest)) return;
        if (Prefixed(simple, order, "guard ", out rest) && Guard(rest)) return;
        if (Prefixed(simple, order, "patrol ", out rest) && Guard(rest)) return;
        if (Prefixed(simple, order, "go to ", out rest) && GoToPlace(rest)) return;
        if (Prefixed(simple, order, "head to ", out rest) && GoToPlace(rest)) return;
        if (Prefixed(simple, order, "forget ", out rest) && ForgetPlace(rest)) return;
        if (Prefixed(simple, order, "pick up ", out rest) && Fetch(rest)) return;
        if (Prefixed(simple, order, "gather ", out rest) && Fetch(rest)) return;
        if (Prefixed(simple, order, "harvest ", out rest) && Harvest(rest)) return;
        if (Prefixed(simple, order, "pick ", out rest) && Harvest(rest)) return;
        if (Prefixed(simple, order, "deposit ", out rest) && Deposit(rest)) return;
        if (Prefixed(simple, order, "stash ", out rest) && Deposit(rest)) return;
        if (Prefixed(simple, order, "bring me ", out rest) && Bring(speaker, rest)) return;
        if (Prefixed(simple, order, "fetch me ", out rest) && Bring(speaker, rest)) return;
        if (Prefixed(simple, order, "hand me ", out rest) && Bring(speaker, rest)) return;
        if (Prefixed(simple, order, "get me ", out rest) && Bring(speaker, rest)) return;
        if (Prefixed(simple, order, "bring ", out rest) && Bring(speaker, rest)) return;
        if (Prefixed(simple, order, "find ", out rest) && Bring(speaker, rest)) return;
        if (Prefixed(simple, order, "give me ", out rest) && Bring(speaker, rest)) return;
        if (Prefixed(simple, order, "toss me ", out rest) && Bring(speaker, rest)) return;
        if (Prefixed(simple, order, "throw me ", out rest) && Bring(speaker, rest)) return;
        if (Prefixed(simple, order, "pass me ", out rest) && Bring(speaker, rest)) return;
        if (Prefixed(simple, order, "drop ", out rest) && DropNamed(rest)) return;
        if (Prefixed(simple, order, "toss ", out rest) && DropNamed(rest)) return;
        if (Prefixed(simple, order, "unequip ", out rest) && UnequipNamed(rest)) return;
        if (Prefixed(simple, order, "craft ", out rest) && Craft(rest)) return;
        if (Prefixed(simple, order, "make ", out rest) && Craft(rest)) return;
        if (Prefixed(simple, order, "attack ", out rest) && Fight(rest)) return;
        if (Prefixed(simple, order, "kill ", out rest) && Fight(rest)) return;
        if (Prefixed(simple, order, "take ", out rest) && Withdraw(rest)) return;
        if (Prefixed(simple, order, "eat ", out rest) && (EatNamed(rest) || Bare(rest) == null && EatBest())) return;
        if (Prefixed(simple, order, "equip ", out rest) && EquipNamed(rest)) return;
        if (Prefixed(simple, order, "emote ", out rest) && Emote(Normalize(rest))) return;
        if (Emote(simple)) return;
        if (Loosely(simple, speaker)) return;
        Logger.LogInfo("No direct command matched; asking the planner to read it.");
        StartCoroutine(Decide(order, speaker, ++generation));
    }
    void Follow(Player speaker) { Begin(Job.Follow); target = speaker; Logger.LogInfo("Follow target set."); Say("I'll follow your lead."); }
    void Come(Player speaker) { Begin(Job.Come); target = speaker; Say("I'm coming to you."); }
    // Sticks with you and fights what you are fighting, bosses first, and will not
    // quit the job when hurt - he backs off, heals, and closes again.
    void Escort(Player speaker) {
        Begin(Job.Escort);
        target = speaker;
        GearUp(Player.m_localPlayer);
        var weapon = Held(Player.m_localPlayer);
        Say(weapon == null ? "I'm with you, though I've no weapon to raise."
            : "I'm with you. My " + Localization.instance.Localize(weapon.m_shared.m_name) + " is yours — point me at it.");
    }
    void GoHome() {
        if (!GoToPlace("home")) Say("I have no home remembered yet. Stand where you want it and say 'remember this as home'.");
    }
    void ClaimNearbyBed() {
        var player = Player.m_localPlayer;
        var found = Nearest<Bed>(player.transform.position, 4f);
        if (!found) { Say("No bed is near enough to claim."); return; }
        found.Interact(player, false, false);
        bedPosition.Value = Vector3ToConfig(found.transform.position);
        Config.Save();
        Say("This bed is remembered. I’ll return here when called.");
    }
    void GoToBed() {
        if (!TryParsePosition(bedPosition.Value, out var saved)) { Say("I have no bed remembered yet."); return; }
        var found = Nearest<Bed>(saved, 3f);
        if (!found) { Say("I cannot see the remembered bed here."); return; }
        Begin(Job.Bed); bed = found; destination = found.transform.position;
        Say("I’m going to my bed.");
    }
    static string Vector3ToConfig(Vector3 value) { return value.x.ToString("R") + "," + value.y.ToString("R") + "," + value.z.ToString("R"); }
    bool TryParsePosition(string text, out Vector3 value) {
        value = Vector3.zero;
        var parts = text.Split(',');
        return parts.Length == 3 && float.TryParse(parts[0], out value.x) && float.TryParse(parts[1], out value.y) && float.TryParse(parts[2], out value.z);
    }
    bool TryParseHome(out Vector3 value) { return places.TryGetValue("home", out value); }

    // ---- Named places ----------------------------------------------------

    // Stored as "name=x,y,z;name=x,y,z". A pre-0.4 HomePosition is imported once so
    // an existing waypoint is not lost.
    void LoadPlaces() {
        places.Clear();
        foreach (var entry in waypoints.Value.Split(';')) {
            int split = entry.IndexOf('=');
            if (split <= 0) continue;
            string name = PlaceName(entry.Substring(0, split));
            if (name != null && TryParsePosition(entry.Substring(split + 1), out var position)) places[name] = position;
        }
        if (!places.ContainsKey("home") && TryParsePosition(homePosition.Value, out var legacy)) {
            places["home"] = legacy;
            SavePlaces();
        }
    }
    void SavePlaces() {
        waypoints.Value = string.Join(";", places.Select(p => p.Key + "=" + Vector3ToConfig(p.Value)));
        Config.Save();
    }
    // "the Mine!" becomes "mine". Separators would corrupt the config line, so a name
    // containing one is refused rather than silently mangled.
    static string PlaceName(string raw) {
        string name = Normalize(raw);
        if (name == null) return null;
        if (name.StartsWith("the ", StringComparison.Ordinal)) name = name.Substring(4).Trim();
        if (name.Length == 0 || name.Length > 40 || name.IndexOf('=') >= 0 || name.IndexOf(';') >= 0) return null;
        return name;
    }
    bool RememberPlace(string raw) {
        string name = PlaceName(raw);
        if (name == null) return false;
        places[name] = Player.m_localPlayer.transform.position;
        SavePlaces();
        Say(name == "home" ? "This place is home now. I'll bring full loads back here." : "I'll remember this place as the " + name + ".");
        return true;
    }
    bool GoToPlace(string raw) {
        string name = PlaceName(raw);
        if (name == null || !places.TryGetValue(name, out var position)) return false;
        Begin(Job.Home); destination = position;
        Say(name == "home" ? "I know the way home. I’m heading back." : "Heading for the " + name + ".");
        return true;
    }
    bool ForgetPlace(string raw) {
        string name = PlaceName(raw);
        if (name == null || !places.Remove(name)) return false;
        SavePlaces();
        Say("The " + name + " is forgotten.");
        return true;
    }
    void ListPlaces() {
        if (places.Count == 0) { Say("I know no places yet. Say 'remember this as home'."); return; }
        var here = Player.m_localPlayer.transform.position;
        Say("Places I know: " + string.Join(", ", places.OrderBy(p => p.Key)
            .Select(p => p.Key + " (" + Mathf.RoundToInt(Vector3.Distance(here, p.Value)) + " paces " + Compass(p.Value - here) + ")")));
    }

    // ---- Reporting -------------------------------------------------------

    void Inventory() {
        var items = Player.m_localPlayer.GetInventory().GetAllItems();
        var names = items.GroupBy(i => i.m_shared.m_name).Select(g => Localization.instance.Localize(g.Key) + " x" + g.Sum(i => i.m_stack));
        Say(items.Count == 0 ? "My pack is empty." : "In my pack: " + string.Join(", ", names));
    }
    string JobWord() { return JobWord(job); }
    string JobWord(Job which) {
        switch (which) {
            case Job.Follow: return target ? "following " + target.GetPlayerName() : "following";
            case Job.Come: return "on my way to you";
            case Job.Escort: return target ? "fighting alongside " + target.GetPlayerName() : "fighting alongside you";
            case Job.Mule: return "carrying for you, " + muled + " picked up so far";
            case Job.Home: return "walking home";
            case Job.Bed: return "walking to my bed";
            case Job.Fetch: return "gathering what has fallen";
            case Job.Harvest: return "foraging";
            case Job.Chop: return "felling trees";
            case Job.Mine: return "breaking rock";
            case Job.Fight: return "in a fight";
            case Job.Patrol: return "on guard";
            case Job.Mend: return "off to mend my gear";
            case Job.Grave: return "going back for my gear";
            case Job.Tend: return "tending the fires";
            case Job.Deliver: return "bringing you " + (deliverFilter ?? "something");
            case Job.Haul: return "carrying a full pack home";
            case Job.Resume: return "walking back to the job";
            default: return "standing ready";
        }
    }
    void Status() {
        var me = Player.m_localPlayer;
        var parts = new List<string> {
            "Health " + Mathf.RoundToInt(me.GetHealth()) + " of " + Mathf.RoundToInt(me.GetMaxHealth()),
            "stamina " + Mathf.RoundToInt(me.GetStamina()) + " of " + Mathf.RoundToInt(me.GetMaxStamina())
        };
        var foods = me.GetFoods().Where(f => f.m_item != null)
            .Select(f => Localization.instance.Localize(f.m_item.m_shared.m_name)).ToList();
        parts.Add(foods.Count == 0 ? "nothing in my belly" : "fed on " + string.Join(" and ", foods));
        var seman = me.GetSEMan();
        var marks = new List<string>();
        if (seman.HaveStatusEffect(SEMan.s_statusEffectRested)) marks.Add("rested");
        if (seman.HaveStatusEffect(SEMan.s_statusEffectWet)) marks.Add("wet");
        if (seman.HaveStatusEffect(SEMan.s_statusEffectCold) || seman.HaveStatusEffect(SEMan.s_statusEffectFreezing)) marks.Add("cold");
        if (marks.Count > 0) parts.Add(string.Join(" and ", marks));
        parts.Add(JobWord());
        Say(string.Join(", ", parts) + ".");
    }
    // Everything the first in-game session needs to know, in one order, touching
    // nothing. "It does not work" becomes a specific line.
    void SelfTest() {
        var me = Player.m_localPlayer;
        var here = me.transform.position;
        Say("Bjorn " + Info.Metadata.Version + ", bot control on, " + JobWord() + ".");

        Say(places.Count == 0
            ? "No places remembered. Say 'remember this as home' by a chest."
            : "Places: " + string.Join(", ", places.OrderBy(x => x.Key)
                .Select(x => x.Key + " " + Mathf.RoundToInt(Vector3.Distance(here, x.Value)) + " paces " + Compass(x.Value - here)))
              + (TryParsePosition(bedPosition.Value, out _) ? ". Bed remembered." : ". No bed."));

        Say(campKnown
            ? "I know this camp: " + Mathf.RoundToInt(campSpan * 2f) + " paces across, centre " + Mathf.RoundToInt(Vector3.Distance(here, camp)) + " paces " + Compass(camp - here) + "."
            : "I've not surveyed a camp. Say 'learn the camp' standing in it.");
        int chests = Nearby<Container>(here, 8f).Count(IsChest);
        var station = StationAt(here, 2.5f);
        Say("Around me: " + (chests == 0 ? "no chest" : chests + (chests == 1 ? " chest" : " chests")) + ", " +
            (station ? Localization.instance.Localize(station.m_name) + " in range" : "no station in range") + ", " +
            Nearby<ItemDrop>(here, 12f).Count(d => !d.IsPiece()) + " items on the ground.");

        Say("Kit: " + Tool(Skills.SkillType.Axes, "axe") + ", " + Tool(Skills.SkillType.Pickaxes, "pickaxe") + ", " +
            (WeaponWord() ?? "no weapon") + ", " +
            me.GetInventory().GetAllItems().Count(i => i.m_equipped && i.GetArmor() > 0f) + " armour worn.");

        var foods = me.GetFoods().Count;
        // Both facts, because they differ: he can be carrying a meal he is not yet
        // allowed a second helping of.
        Say("Belly: " + foods + " of 3 slots, " +
            (!HasFood(me) ? "nothing to eat in the pack." : CanEatNow(me) ? "food in the pack." : "food in the pack, none of it ready yet.") +
            " Health " + Mathf.RoundToInt(me.GetHealth()) + ", stamina " + Mathf.RoundToInt(me.GetStamina()) + ".");

        bool bridge;
        try { bridge = File.ReadAllText(tokenFile.Value).Trim().Length > 0; } catch { bridge = false; }
        Say(bridge ? "I can reach my thoughts. Ask me something I don't know a word for."
                   : "No bridge token, so no Claude. Direct orders still work.");
    }
    // Names the best tool of a kind and how worn it is, or says he has none.
    string Tool(Skills.SkillType kind, string word) {
        var best = Player.m_localPlayer.GetInventory().GetAllItems()
            .Where(i => i.m_shared.m_skillType == kind && i.IsEquipable())
            .OrderByDescending(i => i.m_shared.m_toolTier).FirstOrDefault();
        if (best == null) return "no " + word;
        float max = best.GetMaxDurability();
        int wear = max > 0f ? Mathf.RoundToInt(best.m_durability / max * 100f) : 100;
        return Localization.instance.Localize(best.m_shared.m_name) + " " + wear + "%";
    }
    string WeaponWord() {
        var held = Held(Player.m_localPlayer);
        return held != null && IsFightingWeapon(held) ? "holding " + Localization.instance.Localize(held.m_shared.m_name) : null;
    }
    void Where() {
        var position = Player.m_localPlayer.transform.position;
        var text = "I stand in the " + Spaced(Heightmap.FindBiome(position).ToString()) +
                   " at " + Mathf.RoundToInt(position.x) + ", " + Mathf.RoundToInt(position.z) + ".";
        if (TryParseHome(out var home)) {
            var delta = home - position; delta.y = 0;
            text += " Home lies " + Mathf.RoundToInt(delta.magnitude) + " paces " + Compass(delta) + ".";
        }
        Say(text);
    }
    void Scan() {
        var me = Player.m_localPlayer;
        var position = me.transform.position;
        var creatures = Character.GetAllCharacters()
            .Where(c => c && c != me && !c.IsDead() && Vector3.Distance(c.transform.position, position) <= 30f).ToList();
        var parts = new List<string>();
        var beasts = creatures.Where(c => !c.IsPlayer() && !c.IsTamed() && c.GetFaction() != Character.Faction.TrainingDummy).ToList();
        if (beasts.Count > 0)
            parts.Add(string.Join(", ", beasts.GroupBy(c => Localization.instance.Localize(c.m_name))
                .OrderByDescending(g => g.Count()).Take(3).Select(g => g.Count() + " " + g.Key)));
        int tamed = creatures.Count(c => c.IsTamed());
        if (tamed > 0) parts.Add(tamed + " tamed");
        int others = creatures.Count(c => c.IsPlayer());
        if (others > 0) parts.Add(others + (others == 1 ? " other viking" : " other vikings"));
        int chests = Nearby<Container>(position, 30f).Count();
        if (chests > 0) parts.Add(chests + (chests == 1 ? " chest" : " chests"));
        int forage = Nearby<Pickable>(position, 30f).Count(p => p.CanBePicked());
        if (forage > 0) parts.Add(forage + " to forage");
        int drops = Nearby<ItemDrop>(position, 30f).Count(d => !d.IsPiece());
        if (drops > 0) parts.Add(drops + (drops == 1 ? " dropped item" : " dropped items"));
        Say(parts.Count == 0 ? "Nothing stirs within thirty paces." : "Within thirty paces: " + string.Join("; ", parts) + ".");
    }
    static string Spaced(string name) {
        var text = new StringBuilder();
        foreach (char letter in name) {
            if (char.IsUpper(letter) && text.Length > 0) text.Append(' ');
            text.Append(letter);
        }
        return text.ToString();
    }
    static string Compass(Vector3 delta) {
        // Valheim's map puts north along +Z.
        string[] points = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
        float angle = (Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg + 360f) % 360f;
        return points[Mathf.RoundToInt(angle / 45f) % 8];
    }

    // ---- Items -----------------------------------------------------------

    // Names are compared with case, spaces and punctuation stripped, so "torch",
    // "Torch", "wood arrows" and the "WoodArrow" prefab all answer to each other.
    // The untranslated "$item_torch" token is matched too, in case localization
    // has not loaded on this client.
    static string Key(string text) {
        if (text == null) return "";
        var builder = new StringBuilder();
        foreach (char letter in text) if (char.IsLetterOrDigit(letter)) builder.Append(char.ToLowerInvariant(letter));
        return builder.ToString();
    }
    static bool KeyMatches(string candidate, string key) {
        if (key.Length == 0) return false;
        string name = Key(candidate);
        if (name == key) return true;
        // A two-letter fragment is inside half the item list - "up" is in "turnip
        // soup" - so only requests of real length are matched loosely.
        return key.Length >= 3 && name.Contains(key);
    }
    static bool NameMatches(ItemDrop.ItemData item, string wanted) {
        string key = Key(wanted);
        return KeyMatches(Localization.instance.Localize(item.m_shared.m_name), key) ||
               KeyMatches(item.m_shared.m_name, key) ||
               (item.m_dropPrefab && KeyMatches(item.m_dropPrefab.name, key));
    }
    ItemDrop.ItemData FindItem(string requested, bool foodOnly, bool equipOnly) {
        string wanted = Bare(requested);
        if (wanted == null) return null;
        var items = Player.m_localPlayer.GetInventory().GetAllItems();
        var found = items.FirstOrDefault(item => {
            if (foodOnly && item.m_shared.m_food <= 0f) return false;
            if (equipOnly && !item.IsEquipable()) return false;
            return NameMatches(item, wanted);
        });
        if (found == null)
            Logger.LogInfo("No item matched '" + requested + "'. Carrying: " +
                string.Join(", ", items.Select(i => Localization.instance.Localize(i.m_shared.m_name))));
        return found;
    }
    // These return false when nothing in the pack answers to the name, which is the
    // usual sign the sentence was never really an order — the planner reads it next.
    bool EatNamed(string requested) {
        var item = FindItem(requested, foodOnly: true, equipOnly: false);
        if (item == null) return false;
        if (!Player.m_localPlayer.CanEat(item, false)) {
            Say("I've " + Localization.instance.Localize(item.m_shared.m_name) + " on me, but I can't stomach more of it yet.");
            return true;
        }
        Say(Player.m_localPlayer.EatFood(item) ? "I’ve eaten the " + Localization.instance.Localize(item.m_shared.m_name) + "." : "I cannot eat that yet.");
        return true;
    }
    bool EquipNamed(string requested) {
        var item = FindItem(requested, foodOnly: false, equipOnly: true);
        if (item == null) return false;
        Player.m_localPlayer.EquipItem(item);
        Say("I’ve equipped the " + Localization.instance.Localize(item.m_shared.m_name) + ".");
        return true;
    }
    bool UnequipNamed(string requested) {
        string wanted = Bare(requested);
        var me = Player.m_localPlayer;
        var item = wanted == null ? null : me.GetInventory().GetAllItems().FirstOrDefault(i => i.m_equipped && NameMatches(i, wanted));
        if (item == null) return false;
        me.UnequipItem(item);
        Say("I’ve stowed the " + Localization.instance.Localize(item.m_shared.m_name) + ".");
        return true;
    }
    void UnequipAll() {
        var me = Player.m_localPlayer;
        var worn = me.GetInventory().GetAllItems().Where(i => i.m_equipped).ToList();
        foreach (var item in worn) me.UnequipItem(item);
        Say(worn.Count == 0 ? "My hands are already empty." : "Gear stowed — " + worn.Count + " pieces.");
    }
    // Drops at his own feet. `requested` may lead with a count ("10 wood"); without
    // one he parts with every stack of it. DropItem unequips first, so he will hand
    // over the axe in his hands if you ask for it by name.
    bool DropNamed(string requested) {
        var me = Player.m_localPlayer;
        string wanted = Bare(requested);
        if (wanted == null) return false;
        int asked = SplitCount(ref wanted, cap: 9999, none: 0);  // 0 means "all of it"
        var matching = me.GetInventory().GetAllItems().Where(i => NameMatches(i, wanted)).ToList();
        if (matching.Count == 0) return false;
        string name = Localization.instance.Localize(matching[0].m_shared.m_name);
        int left = asked, given = 0;
        foreach (var item in matching) {
            int take = asked == 0 ? item.m_stack : Mathf.Min(left, item.m_stack);
            if (take <= 0) break;
            if (!me.DropItem(me.GetInventory(), item, take)) continue;
            given += take;
            if (asked != 0 && (left -= take) <= 0) break;
        }
        if (given == 0) { Say("I cannot part with the " + name + "."); return true; }
        Say("There — " + given + " " + name + (asked != 0 && given < asked ? ", which is all I had." : " for you."));
        return true;
    }
    // Moves unequipped items into a chest and reports how many stacks went in.
    // `full` tells the caller the chest ran out of room rather than the pack being empty.
    // What an automatic haul is willing to part with. Told to deposit explicitly he
    // will hand over anything, but a haul mid-job must not leave him without his axe,
    // his food or his torch.
    static bool Spare(ItemDrop.ItemData item) {
        if (item.m_shared.m_food > 0f) return false;
        switch (item.m_shared.m_itemType) {
            case ItemDrop.ItemData.ItemType.Material:
            case ItemDrop.ItemData.ItemType.Trophy:
            case ItemDrop.ItemData.ItemType.Fish:
                return true;
            default:
                return false;
        }
    }
    int MoveInto(Container chest, string filter, bool keepKit, out bool full) {
        full = false;
        var me = Player.m_localPlayer;
        var view = chest ? chest.GetComponent<ZNetView>() : null;
        if (!view || !view.IsValid()) return 0;
        // Owning the chest's ZDO is what lets its inventory write back to the world.
        view.ClaimOwnership();
        var into = chest.GetInventory();
        int moved = 0;
        foreach (var item in me.GetInventory().GetAllItems().ToList()) {
            if (item.m_equipped) continue;
            if (keepKit && !Spare(item)) continue;
            if (filter != null && !NameMatches(item, filter)) continue;
            if (!into.CanAddItem(item) || !into.AddItem(item)) { full = true; continue; }
            me.GetInventory().RemoveItem(item);
            moved++;
        }
        return moved;
    }
    bool Deposit(string requested) {
        var me = Player.m_localPlayer;
        string filter = Bare(requested);
        var chest = Nearby<Container>(me.transform.position, 5f).Where(IsChest)
            .OrderBy(c => Vector3.Distance(c.transform.position, me.transform.position)).FirstOrDefault();
        if (!chest) { Say("No chest stands close enough to fill. Say 'dump it' and I'll pile it here instead."); return true; }
        var view = chest.GetComponent<ZNetView>();
        if (!view || !view.IsValid()) { Say("That chest will not answer me."); return true; }
        bool full;
        int moved = MoveInto(chest, filter, false, out full);
        if (moved == 0) {
            if (filter != null && !full) return false;
            Say(full ? "The chest has no room left. Say 'dump it' and I'll pile it here." : "I have nothing to put in there.");
            return true;
        }
        Say("I put " + moved + (moved == 1 ? " stack" : " stacks") + " in the chest" + (full ? ", then it filled up." : "."));
        return true;
    }
    bool Withdraw(string requested) {
        var me = Player.m_localPlayer;
        string filter = Bare(requested);
        var chest = Nearby<Container>(me.transform.position, 5f).Where(IsChest)
            .OrderBy(c => Vector3.Distance(c.transform.position, me.transform.position)).FirstOrDefault();
        if (!chest) {
            // "take a look around" is not an order to raid a chest that is not there.
            if (filter != null) return false;
            Say("No chest stands close enough to open.");
            return true;
        }
        var view = chest.GetComponent<ZNetView>();
        if (!view || !view.IsValid()) { Say("That chest will not answer me."); return true; }
        view.ClaimOwnership();
        var from = chest.GetInventory();
        int taken = 0;
        bool full = false;
        foreach (var item in from.GetAllItems().ToList()) {
            if (filter != null && !NameMatches(item, filter)) continue;
            if (!me.GetInventory().CanAddItem(item) || !me.GetInventory().AddItem(item)) { full = true; continue; }
            from.RemoveItem(item);
            taken++;
        }
        if (taken == 0) {
            if (filter != null && !full) return false;
            Say(full ? "My pack has no room for it." : "The chest holds nothing like that.");
            return true;
        }
        Say("I took " + taken + (taken == 1 ? " stack" : " stacks") + " from the chest" + (full ? ", then my pack filled." : "."));
        return true;
    }
    void DropAll() {
        var me = Player.m_localPlayer;
        var loose = me.GetInventory().GetAllItems().Where(i => !i.m_equipped).ToList();
        int dropped = loose.Count(i => me.DropItem(me.GetInventory(), i, i.m_stack));
        Say(dropped == 0 ? "I have nothing loose to drop." : "Dropped " + dropped + " stacks at my feet.");
    }
    void FeedFire() {
        var me = Player.m_localPlayer;
        var fire = Nearest<Fireplace>(me.transform.position, 5f);
        if (!fire) { Say("No fire burns within reach."); return; }
        int added = 0;
        // Each interaction feeds one log. The alt flag skips the on/off toggle that
        // some fires answer a plain use with.
        while (added < 20 && fire.Interact(me, false, true)) added++;
        Say(added == 0 ? "The fire wants nothing, or I carry no fuel for it." : "Fed the fire " + added + (added == 1 ? " log." : " logs."));
    }
    void UseDoor(bool wantOpen) {
        var me = Player.m_localPlayer;
        var door = Nearest<Door>(me.transform.position, 5f);
        if (!door) { Say("There is no door within reach."); return; }
        var view = door.GetComponent<ZNetView>();
        bool open = view && view.IsValid() && view.GetZDO().GetInt(ZDOVars.s_state) != 0;
        if (open == wantOpen) { Say(open ? "The door already stands open." : "The door is already shut."); return; }
        Say(door.Interact(me, false, false) ? (wantOpen ? "Opened." : "Shut.") : "That door will not move for me.");
    }
    // Valheim only sets Player.m_currentStation from CraftingStation.Interact, which
    // also opens the crafting window, and Player.UpdateStations clears it again on the
    // next frame that window is not visible. A bot never opens it, so
    // GetCurrentCraftingStation() reads null while standing on a forge. Find the
    // station directly instead. CraftingStation.m_useDistance is 2 metres.
    CraftingStation StationAt(Vector3 where, float radius) {
        var me = Player.m_localPlayer;
        return Nearby<CraftingStation>(where, radius)
            .Where(s => s && s.CheckUsable(me, false))
            .OrderBy(s => Vector3.Distance(s.transform.position, where)).FirstOrDefault();
    }
    // Scanning the loaded scene is not free and Drive asks every tick, so cache it.
    float stationScan;
    CraftingStation stationNear;
    CraftingStation StationNear(Vector3 where, float radius) {
        if (Time.time < stationScan) return stationNear;
        stationScan = Time.time + 0.5f;
        return stationNear = StationAt(where, radius);
    }
    // Durability is written directly, so repairing needs no engine gate - only the
    // right station to be standing there.

    // Standing on the bench already is the lucky case. Otherwise walk to one rather
    // than refusing - being told "stand at a workbench" by someone who could simply
    // go and stand at it is the kind of answer that makes him feel like a tool.
    void Repair() {
        var me = Player.m_localPlayer;
        string trouble;
        int mended = Mend(StationsAround(me.transform.position, 5f), out trouble);
        if (mended > 0) { Say("Mended " + mended + (mended == 1 ? " piece" : " pieces") + " of gear."); return; }
        var worn = WornGear();
        if (worn.Count == 0) { Say("Nothing of mine needs mending."); return; }
        // Something IS worn but nothing here will take it. Find a station that would
        // and walk to it, rather than reporting that the gear is fine.
        var stations = StationsAround(me.transform.position, 40f);
        var target = stations.FirstOrDefault(s => worn.Any(i => CanRepair(i, s)));
        if (target) {
            Begin(Job.Mend);
            destination = target.transform.position;
            Say("The " + Localization.instance.Localize(target.m_name) + "'s the one for it. Walking over.");
            return;
        }
        Say(trouble != null ? "I can't mend here — " + trouble + "." : "I can see no station that would mend my gear.");
    }
    // Mirrors Valheim's own repair test: the station must be the one the recipe
    // names, at a high enough level.
    static bool CanRepair(ItemDrop.ItemData item, CraftingStation station) {
        if (!item.m_shared.m_canBeReparied || item.m_durability >= item.GetMaxDurability()) return false;
        var recipe = ObjectDB.instance ? ObjectDB.instance.GetRecipe(item) : null;
        if (recipe == null || (!recipe.m_craftingStation && !recipe.m_repairStation)) return false;
        bool named = (recipe.m_repairStation && recipe.m_repairStation.m_name == station.m_name) ||
                     (recipe.m_craftingStation && recipe.m_craftingStation.m_name == station.m_name) ||
                     item.m_worldLevel < Game.m_worldLevel;   // vanilla allows this too
        return named && Mathf.Min(station.GetLevel(), 4) >= recipe.m_minStationLevel;
    }
    // Every usable station within reach, nearest first. Repairing against only the
    // closest is wrong the moment a camp has more than one: a stone axe wants the
    // workbench even when the forge happens to be a pace nearer.
    List<CraftingStation> StationsAround(Vector3 where, float radius) {
        var me = Player.m_localPlayer;
        return Nearby<CraftingStation>(where, radius).Where(s => s && s.CheckUsable(me, false))
            .OrderBy(s => Vector3.Distance(s.transform.position, where)).ToList();
    }
    static List<ItemDrop.ItemData> WornGear() {
        var worn = new List<ItemDrop.ItemData>();
        Player.m_localPlayer.GetInventory().GetWornItems(worn);
        return worn;
    }
    // Says which station a piece actually wants, rather than "nothing needs mending".
    static string WhyNotMended(ItemDrop.ItemData item, List<CraftingStation> stations) {
        string name = Localization.instance.Localize(item.m_shared.m_name);
        if (!item.m_shared.m_canBeReparied) return "the " + name + " cannot be mended at all";
        var recipe = ObjectDB.instance ? ObjectDB.instance.GetRecipe(item) : null;
        if (recipe == null) return "I know no recipe for the " + name;
        var needs = recipe.m_repairStation ? recipe.m_repairStation : recipe.m_craftingStation;
        if (!needs) return "nothing mends the " + name;
        string wants = Localization.instance.Localize(needs.m_name);
        return stations.Any(s => s.m_name == needs.m_name)
            ? "the " + wants + " here is too low a level for the " + name
            : "the " + name + " needs a " + wants;
    }
    int Mend(List<CraftingStation> stations, out string trouble) {
        trouble = null;
        var me = Player.m_localPlayer;
        int mended = 0;
        foreach (var item in WornGear()) {
            var station = stations.FirstOrDefault(s => CanRepair(item, s));
            if (station == null) {
                if (trouble == null) trouble = WhyNotMended(item, stations);
                Logger.LogInfo("Not mending " + Localization.instance.Localize(item.m_shared.m_name) +
                               " (" + item.m_durability.ToString("0") + "/" + item.GetMaxDurability().ToString("0") + "): " +
                               WhyNotMended(item, stations));
                continue;
            }
            me.RaiseSkill(Skills.SkillType.Crafting, 1f - item.m_durability / item.GetMaxDurability());
            item.m_durability = item.GetMaxDurability();
            mended++;
        }
        return mended;
    }
    // "craft 20 wood arrows" — splits an optional leading count off the item name.
    static int SplitCount(ref string wanted, int cap = 20, int none = 1) {
        int space = wanted.IndexOf(' ');
        int count;
        if (space <= 0 || !int.TryParse(wanted.Substring(0, space), out count) || count < 1) return none;
        wanted = wanted.Substring(space + 1).Trim();
        return Mathf.Min(count, cap);
    }
    bool Craft(string requested) {
        var me = Player.m_localPlayer;
        string wanted = Bare(requested);
        if (wanted == null || !ObjectDB.instance) return false;
        int asked = SplitCount(ref wanted);  // crafting caps at 20
        // Upgrades and the one-ingredient recipes need a chosen item and quality,
        // which only the crafting panel can supply; plain recipes are all I attempt.
        var usable = ObjectDB.instance.m_recipes.Where(r =>
            r && r.m_enabled && r.m_item && !r.m_noCraftOnlyUpgrade && !r.m_requireOnlyOneIngredient).ToList();
        string key = Key(wanted);
        var recipe = usable.FirstOrDefault(r => Key(RecipeName(r)) == key) ?? usable.FirstOrDefault(r => KeyMatches(RecipeName(r), key));
        if (recipe == null) return false;
        string name = Localization.instance.Localize(recipe.m_item.m_itemData.m_shared.m_name);
        var needed = recipe.GetRequiredStation(1);
        // HaveRequirements checks m_currentStation, which is null for a bot, so set it
        // for the duration of the craft. UpdateStations clears it again next frame.
        var standing = needed ? StationAt(me.transform.position, 2.5f) : null;
        if (needed && !standing) {
            var seen = StationAt(me.transform.position, 40f);
            Say(seen
                ? "I need a " + Localization.instance.Localize(needed.m_name) + " for that, and I'm " + Mathf.RoundToInt(Vector3.Distance(seen.transform.position, me.transform.position)) + " paces off. Walk me in."
                : "I must stand at a " + Localization.instance.Localize(needed.m_name) + " to make " + name + ".");
            return true;
        }
        if (standing) me.SetCraftingStation(standing);
        long crafter = Game.instance.GetPlayerProfile().GetPlayerID();
        int made = 0;
        bool full = false;
        for (int i = 0; i < asked && made < asked; i++) {
            if (!me.HaveRequirements(recipe, false, 1)) break;
            if (!me.GetInventory().CanAddItem(recipe.m_item.gameObject, recipe.m_amount)) { full = true; break; }
            me.GetInventory().AddItem(recipe.m_item.gameObject.name, recipe.m_amount, 1, 0, crafter, me.GetPlayerName(), false);
            me.ConsumeResources(recipe.m_resources, 1);
            me.RaiseSkill(Skills.SkillType.Crafting);
            made += recipe.m_amount;
        }
        if (made == 0) { Say(full ? "My pack is too full to make " + name + "." : "I lack what the " + name + " needs."); return true; }
        Say("Made " + made + " " + name + (made < asked ? ", then ran short." : "."));
        return true;
    }
    // Recipe questions are answered from ObjectDB, not from anything remembered. That
    // is the installed game's own data, so it is exactly right for this version and
    // any mods, costs no API call, and works with the planner down.
    void TellRecipe(Recipe recipe) {
        string name = Localization.instance.Localize(recipe.m_item.m_itemData.m_shared.m_name);
        var parts = recipe.m_resources.Where(x => x.m_resItem)
            .Select(x => x.m_amount + " " + Localization.instance.Localize(x.m_resItem.m_itemData.m_shared.m_name)).ToList();
        var station = recipe.GetRequiredStation(1);
        string made = recipe.m_amount > 1 ? recipe.m_amount + " " + name : name;
        Say(parts.Count == 0
            ? made + " takes nothing I can name."
            : made + ": " + string.Join(", ", parts) +
              (station ? ", at a " + Localization.instance.Localize(station.m_name) + "." : ", by hand."));
    }
    bool Recipes(Player speaker, string wanted) {
        string key = Key(Bare(wanted) ?? "");
        if (key.Length < 3 || !ObjectDB.instance) return false;
        var known = ObjectDB.instance.m_recipes.Where(r => r && r.m_item && r.m_enabled).ToList();
        var exact = known.FirstOrDefault(r => Key(RecipeName(r)) == key);
        if (exact != null) { TellRecipe(exact); return true; }
        // "spear" is four different spears. Rather than answering about whichever
        // happened to be first in the table, name them and wait - the reply does not
        // need his name in front of it.
        var loose = known.Where(r => KeyMatches(RecipeName(r), key))
            .GroupBy(RecipeName).Select(g => g.First()).Take(5).ToList();
        if (loose.Count == 0) return false;
        if (loose.Count == 1) { TellRecipe(loose[0]); return true; }
        var names = loose.Select(RecipeName).ToArray();
        AskFor(speaker, "Which — " + string.Join(", ", names) + "?", names, pick => {
            var chosen = loose.FirstOrDefault(r => RecipeName(r) == pick);
            if (chosen != null) TellRecipe(chosen);
        });
        return true;
    }
    static string RecipeName(Recipe recipe) {
        return Localization.instance.Localize(recipe.m_item.m_itemData.m_shared.m_name);
    }
    bool Emote(string word) {
        string emote;
        if (word == null || !emotes.TryGetValue(word.Trim(), out emote)) return false;
        // Only a held pose needs the job stopped; Valheim cancels it the moment he
        // moves. A one-shot plays over whatever he is doing and costs nothing.
        if (emote == "sit") Halt("Right, I'll sit a while.");
        Player.m_localPlayer.StartEmote(emote, emote != "sit");
        return true;
    }

    // ---- Collecting sweeps ----------------------------------------------

    static IEnumerable<T> Nearby<T>(Vector3 origin, float radius) where T : Component {
        return FindObjectsByType<T>(FindObjectsSortMode.None)
            .Where(c => c && Vector3.Distance(c.transform.position, origin) <= radius);
    }
    static T Nearest<T>(Vector3 origin, float radius) where T : Component {
        return Nearby<T>(origin, radius).OrderBy(c => Vector3.Distance(c.transform.position, origin)).FirstOrDefault();
    }
    // TombStone carries a Container, so an unfiltered scan finds gravestones and will
    // happily empty a load into someone's grave. Chests only.
    static bool IsChest(Container box) {
        return box && !box.GetComponent<TombStone>();
    }
    static bool IsLive(Component part) {
        var view = part ? part.GetComponent<ZNetView>() : null;
        return view && view.IsValid();
    }
    // Scans take their centre, filter, and skip set as arguments so a sweep can be
    // probed for targets before it cancels whatever the bot is already doing.
    static bool Skipped(HashSet<int> skip, Component part) { return skip != null && skip.Contains(part.GetInstanceID()); }
    ItemDrop NextDrop(Vector3 centre, string filter, HashSet<int> skip, Vector3 from) {
        return Nearby<ItemDrop>(centre, SweepRadius)
            .Where(d => !Skipped(skip, d) && !d.IsPiece() && IsLive(d) &&
                        d.m_itemData?.m_shared != null && !NearAPile(piles, d.transform.position) &&
                        (filter == null || NameMatches(d.m_itemData, filter)))
            .OrderBy(d => Vector3.Distance(d.transform.position, from)).FirstOrDefault();
    }
    Pickable NextPickable(Vector3 centre, string filter, HashSet<int> skip, Vector3 from) {
        return Nearby<Pickable>(centre, SweepRadius)
            .Where(p => !Skipped(skip, p) && p.CanBePicked() && !p.GetPicked() &&
                        (filter == null || KeyMatches(PickableName(p), Key(filter))))
            .OrderBy(p => Vector3.Distance(p.transform.position, from)).FirstOrDefault();
    }
    // Standing trees and the trunks they leave behind are both worth an axe; rocks
    // and ore veins both answer to a pickaxe.
    IEnumerable<Component> Breakables(Job kind, Vector3 centre) {
        if (kind == Job.Chop)
            return Nearby<TreeBase>(centre, SweepRadius).Cast<Component>()
                .Concat(Nearby<TreeLog>(centre, SweepRadius).Cast<Component>());
        return Nearby<MineRock>(centre, SweepRadius).Cast<Component>()
            .Concat(Nearby<MineRock5>(centre, SweepRadius).Cast<Component>());
    }
    Component NextBreakable(Job kind, Vector3 centre, HashSet<int> skip, Vector3 from) {
        return Breakables(kind, centre)
            .Where(c => !Skipped(skip, c) && IsLive(c))
            .OrderBy(c => Vector3.Distance(c.transform.position, from)).FirstOrDefault();
    }
    // Valheim's faction rules say a training dummy is an enemy of players, which is
    // right for swinging at one on purpose and wrong for choosing a fight. A dummy
    // never dies, so picking one means fighting it forever and ignoring every order.
    // He will still attack one if you name it.
    static bool WorthFighting(Character c, Player me) {
        return c && c != me && !c.IsDead() && !c.IsPlayer() && !c.IsTamed() &&
               c.GetFaction() != Character.Faction.TrainingDummy && BaseAI.IsEnemy(me, c);
    }
    // Valheim's own faction rules decide what counts as a foe, so tamed beasts and
    // other vikings are never targets.
    Character NextFoe(Vector3 centre, string filter, HashSet<int> skip, Vector3 from) {
        var me = Player.m_localPlayer;
        return Character.GetAllCharacters()
            .Where(c => (filter != null ? c && c != me && !c.IsDead() && !c.IsPlayer() && !c.IsTamed() && BaseAI.IsEnemy(me, c)
                                        : WorthFighting(c, me)) &&
                        !Skipped(skip, c) &&
                        Vector3.Distance(c.transform.position, centre) <= SweepRadius &&
                        (filter == null || KeyMatches(Localization.instance.Localize(c.m_name), Key(filter))))
            .OrderBy(c => Vector3.Distance(c.transform.position, from)).FirstOrDefault();
    }
    // ---- Looking after himself -------------------------------------------

    // Keeps his three food slots topped up from whatever he carries, best first.
    // Valheim's own CanEat decides whether a slot is really free.
    // "eat up" names nothing, so he picks the best thing he carries and says so.
    bool EatBest() {
        var player = Player.m_localPlayer;
        var best = player.GetInventory().GetAllItems()
            .Where(i => i.m_shared.m_food > 0f && player.CanEat(i, false))
            .OrderByDescending(i => i.m_shared.m_food + i.m_shared.m_foodStamina)
            .FirstOrDefault();
        if (best == null) {
            Say(player.GetFoods().Count >= 3 ? "I'm full as a jarl at Yule."
                : HasFood(player) ? "I've food on me, but I can't stomach more of it yet. Give it a while."
                : "I've nothing left to eat. Toss me something.");
            return true;
        }
        string name = Localization.instance.Localize(best.m_shared.m_name);
        Say(player.EatFood(best) ? "Aye. " + name + " it is." : "I cannot stomach the " + name + " yet.");
        return true;
    }
    void Graze(Player player) {
        if (Time.time < grazeCheck) return;
        grazeCheck = Time.time + 8f;
        var best = player.GetInventory().GetAllItems()
            .Where(i => i.m_shared.m_food > 0f && player.CanEat(i, false))
            .OrderByDescending(i => i.m_shared.m_food + i.m_shared.m_foodStamina)
            .FirstOrDefault();
        if (best == null) return;
        string name = Localization.instance.Localize(best.m_shared.m_name);
        if (player.EatFood(best)) Logger.LogInfo("Ate " + name + " to keep working.");
    }

    // Hungry, with nothing in the pack to fix it: food lying within a dozen paces is
    // worth the walk, and otherwise he says so rather than quietly starving. Tossing
    // him something is all it takes.
    // Two different questions, which were being answered by one method. Valheim
    // refuses a second helping of the same food until it is half burnt down
    // (Player.Food.CanEatAgain), so a man with a full pack of raspberries who just
    // ate raspberries has plenty of food and nothing he can eat this minute.
    static bool HasFood(Player player) {
        return player.GetInventory().GetAllItems().Any(i => i.m_shared.m_food > 0f);
    }
    bool CanEatNow(Player player) {
        return player.GetInventory().GetAllItems().Any(i => i.m_shared.m_food > 0f && player.CanEat(i, false));
    }
    bool Peckish(Player player) {
        if (player.GetFoods().Count >= 3 || HasFood(player)) { morsel = null; return false; }
        if (morsel && IsLive(morsel)) return true;
        morsel = null;
        if (Time.time < morselScan) return false;
        morselScan = Time.time + 2f;
        morsel = Nearby<ItemDrop>(player.transform.position, 12f)
            .Where(d => IsLive(d) && !d.IsPiece() && d.m_itemData?.m_shared != null &&
                        !skippedDrops.Contains(d.GetInstanceID()) &&
                        player.GetInventory().CanAddItem(d.m_itemData) &&
                        d.m_itemData.m_shared.m_food > 0f && player.CanEat(d.m_itemData, false))
            .OrderBy(d => Vector3.Distance(d.transform.position, player.transform.position)).FirstOrDefault();
        if (!morsel) { Beg(player); return false; }
        morselAt = 0f;
        return true;
    }
    // Rotating so he does not repeat himself, and getting less polite the longer he
    // goes without. Cycling an index rather than drawing at random keeps it readable.
    static readonly string[] begging = {
        "My belly's empty. Toss me something and I'll keep swinging.",
        "I could eat. Anything you can spare?",
        "Working hungry is poor work. Have you food?",
        "I'd trade an hour of chopping for a cooked meat right now.",
        "Nothing in my pack but air. Food, if you have it.",
    };
    static readonly string[] starving = {
        "Still nothing to eat. I'm starting to look at that boar differently.",
        "A man cannot fell trees on an empty belly. Food. Please.",
        "I have eaten nothing at all. Say the word and I'll go hunt something myself.",
        "My strength is going. Anything. A mushroom. A berry. I am not proud.",
    };
    static readonly string[] thanks = {
        "My thanks. That'll do nicely.",
        "Ha! Good. That'll keep me swinging.",
        "You're a good sort.",
        "Aye, that's the stuff.",
    };
    void Beg(Player player) {
        // Nothing to complain about if he is fed, or if he is carrying a meal and
        // simply has to wait for the last one to burn down.
        if (player.GetFoods().Count > 0 || HasFood(player)) { hungrySince = 0f; return; }
        if (hungrySince == 0f) hungrySince = Time.time;
        // Nags sooner the longer he has gone without.
        float wait = Time.time - hungrySince > 300f ? 60f : 120f;
        if (Time.time - lastBeg < wait) return;
        lastBeg = Time.time;
        var lines = Time.time - hungrySince > 300f ? starving : begging;
        Say(lines[begged++ % lines.Length]);
    }
    // Walk over to one dropped item and take it. Clears `which` once the errand is
    // settled one way or another; returns true only on an actual pickup.
    // Drops he has given up on, so a single unreachable item cannot make him shuttle
    // back and forth forever instead of following you.
    readonly HashSet<int> skippedDrops = new HashSet<int>();
    bool FetchDrop(Player player, ref ItemDrop which, ref float since) {
        var step = StepToward(Lane.Pickup, player, which.transform.position, 1.6f);
        if (step != Step.Arrived) {
            if (step != Step.Moving) { skippedDrops.Add(which.GetInstanceID()); which = null; Fresh(Lane.Pickup); }
            return false;
        }
        // Only the ZDO owner may pick an item up, so ask and retry for a few seconds.
        if (!which.CanPickup()) {
            if (since == 0f) since = Time.time;
            if (Time.time - since > 3f) { skippedDrops.Add(which.GetInstanceID()); which = null; }
            else which.RequestOwn();
            return false;
        }
        var drop = which;
        which = null;
        return player.Pickup(drop.gameObject, autoequip: false);
    }
    void TakeMorsel(Player player) {
        if (!FetchDrop(player, ref morsel, ref morselAt)) return;
        grazeCheck = 0f;   // Eat it on the next tick rather than waiting out the timer.
        hungrySince = 0f;
        Say(thanks[begged++ % thanks.Length]);
    }

    // ---- Carrying for you ------------------------------------------------

    // While muling he shadows you and picks up anything you walk past, until he is
    // loaded. He never grabs what he cannot fit.
    bool Scavenging(Player player) {
        if (job != Job.Mule || errand != Job.None || Loaded(player)) { salvage = null; return false; }
        if (salvage && IsLive(salvage)) return true;
        salvage = null;
        if (Time.time < salvageScan) return false;
        salvageScan = Time.time + 1f;
        salvage = Nearby<ItemDrop>(player.transform.position, 12f)
            .Where(d => IsLive(d) && !d.IsPiece() && d.m_itemData?.m_shared != null &&
                        !skippedDrops.Contains(d.GetInstanceID()) &&
                        !NearAPile(piles, d.transform.position) &&
                        player.GetInventory().CanAddItem(d.m_itemData))
            .OrderBy(d => Vector3.Distance(d.transform.position, player.transform.position)).FirstOrDefault();
        if (!salvage) return false;
        salvageAt = 0f;
        return true;
    }
    void Scavenge(Player player) {
        if (FetchDrop(player, ref salvage, ref salvageAt)) muled++;
    }
    void Mule(Player speaker) {
        Begin(Job.Mule);
        target = speaker;
        muled = 0; hauls = 0;
        Say(places.ContainsKey("home")
            ? "I'll walk with you and carry what you leave. When I'm full I'll run it home and come back."
            : "I'll walk with you and carry what you leave. Say 'remember this as home' at a chest and I'll run full loads there.");
    }
    // "take it home" on demand, rather than waiting for the pack to fill.
    bool HaulNow(Player speaker) {
        var me = Player.m_localPlayer;
        if ((IsSweep || job == Job.Mule) && errand == Job.None &&
            Needed(me, out var need, out var spot, out var said) && Detour(need, spot, said)) return true;
        if (!places.TryGetValue("home", out var home)) {
            Say("I know no home to take it to. Stand by a chest and say 'remember this as home'.");
            return true;
        }
        Begin(Job.Haul);
        destination = home;
        // Come back to whoever asked once the pack is empty.
        errand = Job.Come; target = speaker;
        Say("Taking it home.");
        return true;
    }

    // ---- Bringing things to people ---------------------------------------

    static readonly string[] minedThings = { "stone", "ore", "copper", "tin", "iron", "silver", "rock", "obsidian", "flametal" };
    void StartDeliver(Player who, string owed) {
        Begin(Job.Deliver);
        target = who; deliverTo = who; deliverFilter = owed;
        Say("Bringing you " + owed + ".");
    }
    void HandOver(Player player) {
        string owed = deliverFilter;
        if (owed != null && DropNamed(owed)) { Halt(); return; }
        int dropped = 0;
        foreach (var item in player.GetInventory().GetAllItems().ToList()) {
            if (item.m_equipped) continue;
            if (owed != null && !NameMatches(item, owed)) continue;
            if (player.DropItem(player.GetInventory(), item, item.m_stack)) dropped++;
        }
        Halt(dropped == 0
            ? "I came back with no " + owed + " to give you."
            : "There — " + dropped + (dropped == 1 ? " stack" : " stacks") + " of " + owed + " for you.");
    }
    // "bring me some wood": hand it over if he has it, otherwise go and get some
    // first. The delivery is remembered across the gathering job and runs when it ends.
    bool Bring(Player speaker, string requested) {
        var me = Player.m_localPlayer;
        string asked = Bare(requested);
        if (asked == null || !speaker) return false;
        // Split any leading count off before matching: "10 wood" must still find wood.
        // The count is carried through to the hand-over so he gives you what you asked.
        string filter = asked;
        int count = SplitCount(ref filter, cap: 9999, none: 0);
        if (filter.Length == 0) return false;
        string owed = count == 0 ? filter : count + " " + filter;
        if (FindItem(filter, false, false) != null) { StartDeliver(speaker, owed); return true; }
        Vector3 here = me.transform.position;
        string key = Key(filter);
        bool started;
        if (Probe(Job.Fetch, here, filter, null, here) != null) started = Fetch(filter);
        else if (key.Contains("wood") || key.Contains("log") || key.Contains("timber")) { Chop(); started = job == Job.Chop; }
        else if (minedThings.Any(m => key.Contains(m))) { Mine(); started = job == Job.Mine; }
        else if (Probe(Job.Harvest, here, filter, null, here) != null) started = Harvest(filter);
        else return false;
        if (!started) return false;
        deliverTo = speaker; deliverFilter = owed;
        return true;
    }

    // ---- Dying and getting the gear back ---------------------------------

    internal void Struck(HitData hit) {
        if (!Active) return;
        var attacker = hit.GetAttacker();
        if (!attacker || attacker == Player.m_localPlayer || attacker.IsPlayer() || attacker.IsTamed() ||
            attacker.GetFaction() == Character.Faction.TrainingDummy) return;
        hurtBy = attacker;
        hurtAt = Time.time;
    }
    internal void RememberGrave(Vector3 where) {
        graveSpot = where; graveKnown = true;
        Logger.LogInfo("Died at " + Vector3ToConfig(where) + "; will try to recover the grave.");
    }
    internal void AfterRespawn() {
        if (!graveKnown || !Active) return;
        Begin(Job.Grave);
        destination = graveSpot;
        Say("I fell. I'm going back for my gear.");
    }
    void AtGrave(Player player) {
        var stone = Nearest<TombStone>(player.transform.position, 8f);
        var box = stone ? stone.GetComponent<Container>() : null;
        var view = stone ? stone.GetComponent<ZNetView>() : null;
        if (!box || !view || !view.IsValid()) { graveKnown = false; Halt("I came to where I fell, but my grave is not here."); return; }
        // Moving the items across directly rather than calling Interact, because the
        // vanilla path opens the inventory window when the load will not all fit.
        view.ClaimOwnership();
        var from = box.GetInventory();
        int taken = 0;
        foreach (var item in from.GetAllItems().ToList()) {
            if (!player.GetInventory().CanAddItem(item) || !player.GetInventory().AddItem(item)) continue;
            from.RemoveItem(item);
            taken++;
        }
        int left = from.GetAllItems().Count;
        // Keep the grave remembered unless something actually came out of it, so
        // "get your gear" can try again after making room.
        if (taken == 0) { Halt("My grave is here, but I cannot carry what is in it. Free me some room and say 'get your gear'."); return; }
        graveKnown = false;
        GearUp(player);
        Say("I have my gear back" + (left > 0 ? ", though " + left + " stacks stay behind." : ".") + (places.ContainsKey("home") ? " Heading home." : ""));
        if (places.ContainsKey("home")) GoToPlace("home"); else Halt();
    }

    // ---- Tending the fires -----------------------------------------------
    //
    // Smelters, kilns, blast furnaces and windmills are all Smelter, and all driven by
    // three public Switches rather than a plain Interact. Each switch handles exactly
    // one unit and returns whether it did anything, so the loop is also the count -
    // no need for the private queue and fuel accessors.
    int Work(Smelter smelter, Player player) {
        int moved = 0;
        // Take the finished metal out first, or there may be no room to put ore in.
        for (int i = 0; i < 40 && smelter.m_emptyOreSwitch && smelter.m_emptyOreSwitch.Interact(player, false, false); i++) moved++;
        for (int i = 0; i < 40 && smelter.m_addWoodSwitch && smelter.m_addWoodSwitch.Interact(player, false, false); i++) moved++;
        for (int i = 0; i < 40 && smelter.m_addOreSwitch && smelter.m_addOreSwitch.Interact(player, false, false); i++) moved++;
        if (moved > 0) Logger.LogInfo("Worked " + smelter.m_name + ": " + moved + " items in or out.");
        return moved;
    }
    Smelter NextFurnace(Vector3 from) {
        return Nearby<Smelter>(anchor, 40f)
            .Where(s => s && !visited.Contains(s.GetInstanceID()) && IsLive(s))
            .OrderBy(s => Vector3.Distance(s.transform.position, from)).FirstOrDefault();
    }
    bool Tend(string raw) {
        var me = Player.m_localPlayer;
        string filter = Bare(raw);
        var found = Nearby<Smelter>(me.transform.position, 40f)
            .Where(s => s && IsLive(s) && (filter == null || KeyMatches(s.m_name, Key(filter)))).ToList();
        if (found.Count == 0) {
            if (filter != null) return false;   // a named thing he cannot see: let the planner read it
            Say("I see no smelter or kiln near enough to tend.");
            return true;
        }
        Begin(Job.Tend);
        anchor = me.transform.position;
        sweepFilter = filter;
        collected = 0;
        sweepUntil = Time.time + SweepSeconds;
        Say(found.Count == 1
            ? "I'll see to the " + found[0].m_name + "."
            : "I'll work the " + found.Count + " of them, and keep at it.");
        return true;
    }

    // ---- Knowing the camp ------------------------------------------------
    //
    // Valheim keeps a registry of every placed piece, so he can survey a base rather
    // than being told about it one waypoint at a time. The survey is saved, so he
    // still knows the shape of the camp when most of it is out of loading range.

    void LoadCamp() {
        campKnown = TryParsePosition(campCentre.Value, out camp);
        if (campKnown) campSpan = Mathf.Clamp(campRadius.Value, 8f, 120f);
    }
    // Groups pieces that are within `reach` of each other, and returns the size of
    // each group. Used only to say roughly how many buildings there are, so a rough
    // answer is fine; it is never used to decide anything.
    static List<int> Clusters(List<Vector3> points, float reach) {
        var sizes = new List<int>();
        var taken = new bool[points.Count];
        var queue = new List<int>();
        for (int seed = 0; seed < points.Count; seed++) {
            if (taken[seed]) continue;
            taken[seed] = true;
            queue.Clear();
            queue.Add(seed);
            int size = 0;
            for (int at = 0; at < queue.Count; at++) {
                size++;
                for (int other = 0; other < points.Count; other++) {
                    if (taken[other] || Vector3.Distance(points[queue[at]], points[other]) > reach) continue;
                    taken[other] = true;
                    queue.Add(other);
                }
            }
            sizes.Add(size);
        }
        return sizes;
    }
    bool Survey(bool speak) {
        var me = Player.m_localPlayer;
        var here = me.transform.position;
        var built = new List<Piece>();
        Piece.GetAllPiecesInRadius(here, 90f, built);
        built = built.Where(b => b && b.GetComponent<ZNetView>()).ToList();
        if (built.Count < 5) {
            if (speak) Say("I see nothing built here worth calling a camp.");
            return false;
        }
        // Centre on the pieces themselves, not on where he happens to be standing.
        Vector3 middle = Vector3.zero;
        foreach (var piece in built) middle += piece.transform.position;
        middle /= built.Count;
        middle.y = here.y;
        float reach = built.Max(b => Vector3.Distance(new Vector3(b.transform.position.x, middle.y, b.transform.position.z), middle));
        camp = middle;
        campSpan = Mathf.Clamp(reach + 5f, 8f, 120f);
        campKnown = true;
        campCentre.Value = Vector3ToConfig(camp);
        campRadius.Value = campSpan;
        Config.Save();
        if (speak) CampReport(built);
        Logger.LogInfo("Surveyed camp: " + built.Count + " pieces, span " + campSpan);
        return true;
    }
    void CampReport(List<Piece> built) {
        if (built == null) {
            if (!campKnown) { Say("I've not looked over the camp yet. Say 'learn the camp' while you're standing in it."); return; }
            built = new List<Piece>();
            Piece.GetAllPiecesInRadius(camp, campSpan + 5f, built);
        }
        // Clusters of a handful of pieces are a wall or a fence, not a building.
        int houses = Clusters(built.Select(b => b.transform.position).ToList(), 6f).Count(size => size >= 15);
        Say("Your camp runs " + Mathf.RoundToInt(campSpan * 2f) + " paces across, " + built.Count + " pieces" +
            (houses > 0 ? ", about " + houses + (houses == 1 ? " building." : " buildings.") : "."));
        var stations = Nearby<CraftingStation>(camp, campSpan).GroupBy(s => Localization.instance.Localize(s.m_name))
            .Select(g => g.Count() + " " + g.Key).ToList();
        Say("In it: " + Nearby<Container>(camp, campSpan).Count(IsChest) + " chests, " +
            Nearby<Bed>(camp, campSpan).Count() + " beds, " +
            Nearby<Fireplace>(camp, campSpan).Count() + " fires" +
            (stations.Count > 0 ? ", " + string.Join(", ", stations) + "." : ", no stations."));
        Say("I'll guard all of it if you say 'guard the camp', and mend at the nearest bench without being told where.");
    }
    // The centre he should treat as base: the surveyed camp if he has one, else the
    // home waypoint, else where he stands.
    bool CampOr(out Vector3 centre, out float span, out string name) {
        if (campKnown) { centre = camp; span = campSpan; name = "the camp"; return true; }
        if (places.TryGetValue("home", out centre)) { span = GuardSpan; name = "home"; return true; }
        centre = Player.m_localPlayer.transform.position; span = GuardSpan; name = "this ground"; return false;
    }

    // ---- Guard duty ------------------------------------------------------

    static readonly ItemDrop.ItemData.ItemType[] armourSlots = {
        ItemDrop.ItemData.ItemType.Helmet, ItemDrop.ItemData.ItemType.Chest,
        ItemDrop.ItemData.ItemType.Legs, ItemDrop.ItemData.ItemType.Shoulder,
        ItemDrop.ItemData.ItemType.Shield,
    };
    // Put on the best of everything he carries. EquipItem sorts out conflicts such as
    // a two-handed weapon refusing a shield.
    void GearUp(Player player) {
        WieldWeapon(player);
        var carried = player.GetInventory().GetAllItems();
        foreach (var slot in armourSlots) {
            var best = carried.Where(i => i.m_shared.m_itemType == slot && i.IsEquipable())
                .OrderByDescending(i => i.GetArmor()).ThenByDescending(i => i.m_quality).FirstOrDefault();
            if (best != null && Usable(best)) Equip(player, best);
        }
    }
    // Walk a ring that hugs whatever has actually been built here, so the patrol
    // follows the camp rather than an arbitrary circle.
    float CampRadius(Vector3 centre) {
        var built = Nearby<Piece>(centre, GuardSpan).ToList();
        if (built.Count == 0) return 8f;
        float far = built.Max(b => Vector3.Distance(b.transform.position, centre));
        return Mathf.Clamp(far + 3f, 6f, GuardSpan);
    }
    void NextPost() {
        patrolStep = (patrolStep + 1) % 8;
        float angle = patrolStep * 45f * Mathf.Deg2Rad;
        destination = anchor + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * patrolRing;
        // A new post is a new walk however short the hop, and the hop is short: a
        // 45-degree step round a 6 m ring is a 4.59 m chord, under the auto-reset.
        Fresh(Lane.Job);
    }
    bool Guard(string raw) {
        var me = Player.m_localPlayer;
        Vector3 centre;
        string name;
        if (raw == null) {
            // Prefer the surveyed camp: it knows its real extent, so the patrol covers
            // the whole base instead of a fixed ring around one point.
            if (!campKnown) Survey(false);
            CampOr(out centre, out _, out name);
        } else {
            name = PlaceName(raw);
            if (name == null || !places.TryGetValue(name, out centre)) return false;
        }
        Begin(Job.Patrol);
        anchor = centre;
        // A surveyed camp knows how big it is; otherwise fall back to measuring the
        // buildings near the point he was given.
        patrolRing = raw == null && campKnown ? Mathf.Clamp(campSpan * 0.85f, 6f, GuardSpan * 2f) : CampRadius(centre);
        postsFailed = 0; postUntil = 0f;
        patrolStep = 0;
        NextPost();
        GearUp(me);
        var weapon = Held(me);
        Say(weapon == null
            ? "I'll watch " + name + ", though I have no weapon to hand."
            : "I'll walk the bounds of " + name + " with my " + Localization.instance.Localize(weapon.m_shared.m_name) + " and keep it clear.");
        return true;
    }
    // ItemData.IsWeapon() is true for torches and bows as well. A torch carries fire
    // damage, so ordering by damage will happily send him into melee holding one.
    // Humanoid.GetCurrentWeapon falls back to m_unarmedWeapon, so it is NEVER null - a
    // bare-handed bot reports "Unarmed" and every "is he holding something" test passes.
    // That silently defeated EnsureHeld, whose whole job is to put a tool back in his
    // hands when the engine has emptied them.
    static ItemDrop.ItemData Held(Player player) {
        var held = player ? player.GetCurrentWeapon() : null;
        return held != null && held.m_shared.m_skillType != Skills.SkillType.Unarmed ? held : null;
    }
    static bool IsFightingWeapon(ItemDrop.ItemData item) {
        var kind = item.m_shared.m_itemType;
        return item.IsWeapon() && kind != ItemDrop.ItemData.ItemType.Torch && kind != ItemDrop.ItemData.ItemType.Bow;
    }
    static bool Usable(ItemDrop.ItemData item) {
        return !item.m_shared.m_useDurability || item.m_durability > 0f;
    }
    // Humanoid.EquipItem returns false and does nothing for a broken item, mid-attack,
    // or while swimming off the ground. Ignoring that return is how he ends up swinging
    // at air forever with nothing in his hands and nothing to say about it.
    bool Equip(Player player, ItemDrop.ItemData item) {
        if (item.m_equipped) return true;
        if (player.EquipItem(item)) return true;
        Logger.LogInfo("Refused to equip " + Localization.instance.Localize(item.m_shared.m_name) +
                       " (durability " + item.m_durability + ")");
        return false;
    }
    ItemDrop.ItemData BestTool(Skills.SkillType kind) {
        return Player.m_localPlayer.GetInventory().GetAllItems()
            .Where(i => i.m_shared.m_skillType == kind && i.IsEquipable())
            .OrderByDescending(i => Usable(i)).ThenByDescending(i => i.m_shared.m_toolTier)
            .ThenByDescending(i => i.m_quality).FirstOrDefault();
    }
    bool WieldWeapon(Player player) {
        var held = Held(player);
        if (held != null && IsFightingWeapon(held) && Usable(held)) return true;
        var best = player.GetInventory().GetAllItems()
            .Where(i => IsFightingWeapon(i) && i.IsEquipable() && Usable(i))
            .OrderByDescending(i => i.GetDamage().GetTotalDamage()).FirstOrDefault();
        return best != null && Equip(player, best);
    }
    // Walk to the nearest point on the target's own collider rather than its pivot.
    // A felled log is metres long: aiming at its centre put him far outside his own
    // swing, so the axe never connected.
    static Vector3 Edge(Component what, Vector3 from) {
        Vector3 best = Vector3.zero;
        float nearest = float.MaxValue;
        foreach (var collider in what.GetComponentsInChildren<Collider>()) {
            if (!collider || !collider.enabled || collider.isTrigger) continue;
            var mesh = collider as MeshCollider;
            if (mesh != null && !mesh.convex) continue;  // ClosestPoint is undefined on these.
            Vector3 point = collider.ClosestPoint(from);
            float gap = Vector3.Distance(point, from);
            if (gap < nearest) { nearest = gap; best = point; }
        }
        if (nearest < float.MaxValue) return best;
        // Nothing usable: aim at the nearest child instead of the pivot, which on a
        // rock or a felled log sits inside the mesh where he can never stand.
        foreach (var part in what.GetComponentsInChildren<Transform>()) {
            float gap = Vector3.Distance(part.position, from);
            if (gap < nearest) { nearest = gap; best = part.position; }
        }
        return nearest < float.MaxValue ? best : what.transform.position;
    }
    // Measured against Edge(), so this is the weapon's own reach to the target's
    // surface. Atgeirs and polearms genuinely outrange 2.8 m.
    static float Reach(Player player, Component what) {
        var weapon = Held(player);
        float swing = weapon?.m_shared?.m_attack != null ? weapon.m_shared.m_attack.m_attackRange : 2f;
        return Mathf.Clamp(swing, 1.6f, 4f);
    }
    // Picks up (or keeps) the best tool of a kind. Returns false when there is none.
    bool Wield(Player player, Skills.SkillType kind) {
        var held = Held(player);
        if (held != null && held.m_shared.m_skillType == kind && Usable(held)) return true;
        var tool = BestTool(kind);
        if (tool == null) return false;
        if (!Usable(tool)) {
            // Valheim unequips a tool the moment it breaks, so this is otherwise
            // invisible: no tool in hand, nothing said, and nothing happening.
            Say("My " + Localization.instance.Localize(tool.m_shared.m_name) + " has snapped. Mend it and I'll get back to work.");
            return false;
        }
        return Equip(player, tool);
    }
    // Face the target and swing on the weapon's own cadence. Leaves a little stamina
    // so Valheim's exhaustion never strands him mid-fight.
    // Three engine paths empty his hands and none of them refill: a snapped tool is
    // unequipped, UpdateEquipment hides them while swimming, and SetCraftingStation
    // hides them too. The only ShowHandItems call in the whole game is the player's
    // own hide key, so a bot would swing at trees with nothing in its fists forever.
    void EnsureHeld(Player player, Skills.SkillType kind) {
        if (Held(player) != null || Time.time < handsChecked) return;
        handsChecked = Time.time + 1f;
        showHands?.Invoke(player, new object[] { false, false });
        if (Held(player) != null) return;
        if (kind == Skills.SkillType.None) WieldWeapon(player); else Wield(player, kind);
    }
    void SwingAt(Player player, Component what) {
        Vector3 aim = Edge(what, player.transform.position) - player.transform.position;
        if (aim.sqrMagnitude > 0.0001f) { player.SetLookDir(aim.normalized); player.FaceLookDirection(); }
        if (Time.time < swingUntil || player.GetStamina() < 10f) return;
        if (player.StartAttack(null, false)) swingUntil = Time.time + 0.4f;
    }
    void Swing(Player player) {
        EnsureHeld(player, ToolFor(job));
        if (reachedAt == 0f) reachedAt = Time.time;
        if (Time.time - reachedAt > TargetSeconds) { Skip(); return; }
        SwingAt(player, sweepTarget);
    }
    void Skip() {
        if (sweepTarget) { Stumble("gave up on " + sweepTarget.name); visited.Add(sweepTarget.GetInstanceID()); }
        Fresh(Lane.Job);
        sweepTarget = null; reachedAt = 0f;    }
    // A Pickable without an item prefab throws inside GetHoverName; treat it as unnamed.
    static string PickableName(Pickable pickable) {
        try { return Localization.instance.Localize(pickable.GetHoverName()); }
        catch { return ""; }
    }
    // Every sweep has the same shape: probe for a target from where the bot stands,
    // and only cancel the current job once one is found. A named target that is not
    // there returns false so the caller can hand the sentence to the planner instead.
    bool Sweep(Job kind, string requested, string empty, string starting) {
        Vector3 here = Player.m_localPlayer.transform.position;
        string filter = Bare(requested);
        if (Probe(kind, here, filter, null, here) == null) {
            if (filter != null) return false;
            Say(empty);
            return true;
        }
        Begin(kind);
        anchor = here; sweepFilter = filter;
        collected = 0; hauls = 0; sweepUntil = Time.time + SweepSeconds; reachedAt = 0f;
        Say(starting);
        return true;
    }
    // Which tool a working job leans on. Fighting is left out on purpose: breaking
    // off mid-fight to mend a sword is how a companion gets killed.
    static Skills.SkillType ToolFor(Job kind) {
        if (kind == Job.Chop) return Skills.SkillType.Axes;
        if (kind == Job.Mine) return Skills.SkillType.Pickaxes;
        return Skills.SkillType.None;
    }
    // True when the wielded tool of that kind is nearly spent and worth mending.
    bool Blunt(Skills.SkillType kind) {
        if (kind == Skills.SkillType.None) return false;
        // Inventory, not the hands: a tool that has already broken is unequipped.
        var tool = BestTool(kind);
        if (tool == null) return false;
        if (!tool.m_shared.m_useDurability || !tool.m_shared.m_canBeReparied) return false;
        float max = tool.GetMaxDurability();
        return max > 0f && tool.m_durability / max <= 0.15f;
    }
    // Break off to mend, then come back to the same spot and carry on. Uses the same
    // errand slot as hauling, so only one detour is ever in flight.
    // ---- Detours ---------------------------------------------------------
    //
    // The errand slot holds the interrupted real job and nothing else. Detours hand off
    // sideways to each other and never stack, because a detour is not a stored plan -
    // it is a predicate on current world state. A queued detour is a stale one: by the
    // time it popped, the pack might be empty and the axe mended. Re-deriving is
    // smaller, and it makes losing or duplicating a detour structurally impossible.
    static int Rank(Job kind) { return kind == Job.Haul ? 3 : kind == Job.Mend ? 1 : 0; }

    // Everything he might break off to do, derived from the world alone.
    bool Needed(Player me, out Job kind, out Vector3 where, out string line) {
        kind = Job.None; where = Vector3.zero; line = null;
        if (Loaded(me) && places.TryGetValue("home", out where)) {
            kind = Job.Haul;
            line = "My pack is full. I'll run this home and come back for the rest.";
            return true;
        }
        // Mid-detour `job` is Haul or Mend, whose tool is None. Read the errand, or
        // "unload at home, then mend at the bench beside it" can never be seen.
        var need = ToolFor(errand != Job.None ? errand : job);
        if (!Blunt(need)) return false;
        var tool = BestTool(need);
        string name = tool != null ? Localization.instance.Localize(tool.m_shared.m_name) : "tool";
        var station = tool == null ? null
            : StationsAround(me.transform.position, 60f).FirstOrDefault(s => CanRepair(tool, s));
        if (station) where = station.transform.position;
        else if (campKnown) where = camp;
        else if (!places.TryGetValue("workbench", out where) && !places.TryGetValue("home", out where)) {
            if (!mendRefused) {
                mendRefused = true;   // said once per job, not fifty times a second
                Say("My " + name + " is nearly spent and I know no bench to mend it at. Say 'learn the camp' in your base.");
            }
            return false;
        }
        kind = Job.Mend;
        line = "My " + name + " is nearly spent. I'll mend it and come back.";
        return true;
    }

    // The only way into, or between, detours. Writes the errand slot exactly once per
    // chain: the first call stores the interrupted job, later hand-offs only re-aim.
    bool Detour(Job kind, Vector3 where, string line) {
        if (errand == Job.None) {
            // Only a sweep or a mule run may be interrupted. Every other job reads
            // `destination`, which a detour overwrites. This guard is what keeps it safe.
            if (!IsSweep && job != Job.Mule) return false;
            errand = job;
        } else if (Rank(kind) <= Rank(job)) return false;   // hand-offs only go up
        if (++detours > MaxDetours) { Abandon("I've been back and forth " + (detours - 1) + " times over this. It's beyond me for now."); return true; }
        if (futile >= MaxFutile) { Abandon("Three trips and nothing to show for them. I'll stop."); return true; }
        job = kind;
        destination = where;
        sweepTarget = null; reachedAt = 0f;
        Fresh(Lane.Job);
        if (line != null) Say(line);
        return true;
    }

    // Ends every detour. `gained` is verified progress in the world - stacks actually
    // moved, gear actually mended - never elapsed time and never merely arriving.
    void AfterDetour(Player me, bool gained) {
        futile = gained ? 0 : futile + 1;
        var was = job;
        job = Job.None;   // this detour is over, so the next is a hand-off, not a
                          // nesting, and Rank must not compare against the finished one
        if (Needed(me, out var kind, out var where, out var line)) {
            // The same need still true at zero distance with nothing gained means the
            // trip cannot help: full chests with piling off, a bench that will not serve.
            if (kind == was && !gained) { Abandon("I came all this way and it changed nothing."); return; }
            if (Detour(kind, where, line)) return;
        }
        job = Job.Resume; reachedAt = 0f;
        Fresh(Lane.Job);
    }

    // A detour that fails takes the job with it, and says which - a dropped order should
    // never be something the player has to infer from silence.
    void Abandon(string why) {
        var lost = errand;
        if (lost == Job.None) { Halt(why); return; }
        string what = JobWord(lost);
        Halt(why + " " + char.ToUpperInvariant(what[0]) + what.Substring(1) + " is off.");
    }
    // One place where verified progress in the world is recorded.
    // Verified progress in the world, wherever it happens. Work done between detours
    // has to clear the strike count, or three fruitless trips end a job he has been
    // productive on all along.
    void Progress(int much = 1) { collected += much; if (much > 0) futile = 0; }

    void Mend(Player player) {
        var standing = StationsAround(player.transform.position, 5f);
        if (standing.Count > 0) {
            string trouble;
            int mended = Mend(standing, out trouble);
            string said = mended == 0
                ? (trouble != null ? "I can't mend here — " + trouble + "." : "Nothing of mine needed mending after all.")
                : "Mended " + mended + " of my own.";
            // A mend that interrupted a job goes back to it; a plain "repair" is done.
            if (errand != Job.None) { Say(said); AfterDetour(player, mended > 0); }
            else Halt(said);
            return;
        }
        var station = StationAt(player.transform.position, 15f);
        if (station) {
            Vector3 spot = station.transform.position;
            // Only restart the patience clock when genuinely heading somewhere new.
            // Retargeting the same bench every tick reset it forever, so a bench he
            // could not quite reach - a raised one, say - never timed out.
            if (Vector3.Distance(spot, destination) > 0.5f) { destination = spot; reachedAt = 0f; return; }
        }
        if (reachedAt == 0f) reachedAt = Time.time;
        if (Time.time - reachedAt > 8f)
            Stumble(station ? "bench unreachable" : "no bench where I went");
            Halt(station ? "I'm at the bench but can't get close enough to work." : "I came to mend, but there's no station here.");
    }
    Component Probe(Job kind, Vector3 centre, string filter, HashSet<int> skip, Vector3 from) {
        switch (kind) {
            case Job.Fetch: return NextDrop(centre, filter, skip, from);
            case Job.Harvest: return NextPickable(centre, filter, skip, from);
            case Job.Fight: return NextFoe(centre, filter, skip, from);
            default: return NextBreakable(kind, centre, skip, from);
        }
    }
    bool Fetch(string requested) {
        string filter = Bare(requested);
        return Sweep(Job.Fetch, requested,
            "Nothing lies on the ground near here.",
            filter == null ? "I'll gather what has fallen." : "I'll gather the " + filter + ".");
    }
    bool Harvest(string requested) {
        string filter = Bare(requested);
        return Sweep(Job.Harvest, requested,
            "There is nothing to forage near here.",
            filter == null ? "I'll pick what grows here." : "I'll pick the " + filter + ".");
    }
    void Chop() {
        if (!Wield(Player.m_localPlayer, Skills.SkillType.Axes)) { Say("I have no axe to swing."); return; }
        Sweep(Job.Chop, null, "No trees stand close enough to fell.", "I'll put the axe to these trees.");
    }
    void Mine() {
        if (!Wield(Player.m_localPlayer, Skills.SkillType.Pickaxes)) { Say("I have no pickaxe to swing."); return; }
        Sweep(Job.Mine, null, "There is no rock near here worth breaking.", "I'll break what rock I can reach.");
    }
    bool Fight(string requested) {
        var me = Player.m_localPlayer;
        string filter = Bare(requested);
        Vector3 here = me.transform.position;
        // Check there is really something to fight before drawing a weapon, so
        // "kill some time" leaves him holding whatever he already had.
        if (NextFoe(here, filter, null, here) == null) {
            if (filter != null) return false;
            Say("Nothing hostile stirs near me.");
            return true;
        }
        if (!WieldWeapon(me)) { Say("I have no weapon to raise."); return true; }
        return Sweep(Job.Fight, requested,
            "Nothing hostile stirs near me.",
            filter == null ? "Stand back. I'll meet them." : "I'll go for the " + filter + ".");
    }
    // Turns a finished chop or mine into a gather over the same ground, keeping the
    // tally so the final report covers the whole job.
    void GleanOrFinish(string why) {
        var felling = job;
        string done = felling == Job.Chop ? "I felled " + collected + "." : "I broke " + collected + ".";
        if (collected == 0) { FinishSweep(why); return; }
        var player = Player.m_localPlayer;
        visited.Clear();
        sweepFilter = null;
        job = Job.Fetch;
        sweepTarget = null; reachedAt = 0f;        sweepUntil = Time.time + SweepSeconds;
        if (NextDrop(anchor, null, visited, player.transform.position) == null) {
            job = felling;  // Nothing to glean: report the felling, not a gather.
            FinishSweep(why);
            return;
        }
        collected = 0;
        Say(done + " " + why + " Now to gather it up.");
    }
    void FinishSweep(string why) {
        string what;
        switch (job) {
            case Job.Harvest: what = "picked"; break;
            case Job.Chop: what = "felled"; break;
            case Job.Mine: what = "broke"; break;
            case Job.Fight: what = "put down"; break;
            default: what = "gathered"; break;
        }
        string trips = hauls == 0 ? "" : " Carried " + hauls + (hauls == 1 ? " load" : " loads") + " home.";
        var owed = deliverTo; string owedWhat = deliverFilter;
        Halt(collected == 0 ? "I " + what + " nothing. " + why + trips : "I " + what + " " + collected + ". " + why + trips);
        if (owed) StartDeliver(owed, owedWhat);
    }

    // ---- Steering --------------------------------------------------------

    // One line per group of Receive's dispatch below. Each stays under the 180
    // characters Say will cut at, so nothing goes missing mid-sentence.
    void Help() {
        Say("I follow, come here, stay, and walk to any place I know. Say 'remember this as home', or name one: 'remember this as the mine', then 'go to the mine'.");
        Say("Ask me for inventory, status, where I am, or what I see nearby.");
        Say("Set me to work: gather, harvest, chop, mine. I keep at it, run full loads home to the chest, mend a blunt axe, and come back to it until the ground is bare.");
        Say("Say 'bring me some wood' and I'll fetch it and put it in your hands. Say 'guard the camp' and I'll arm myself and walk the bounds.");
        Say("I fight back on my own, eat when I need to, and go back for my gear when I fall.");
        Say("Also: deposit, take all, craft, repair, eat, equip, unequip, drop, open the door, feed the fire — and emotes like wave and dance.");
    }
    // Only runs when Listen is on, so there is no traffic for anyone not using voice.
    IEnumerator ListenLoop() {
        var wait = new WaitForSeconds(1.5f);
        while (true) {
            yield return wait;
            if (!listen.Value || !Active || busy) continue;
            string token;
            try { token = File.ReadAllText(tokenFile.Value).Trim(); } catch { continue; }
            using (var request = UnityWebRequest.Get("http://127.0.0.1:8765/orders")) {
                request.SetRequestHeader("Authorization", "Bearer " + token);
                request.timeout = 5;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success) continue;
                Heard heard = null;
                try { heard = JsonConvert.DeserializeObject<Heard>(request.downloadHandler.text); } catch { }
                if (heard?.orders == null) continue;
                foreach (var line in heard.orders) Spoken(line);
            }
        }
    }
    class Heard { public string[] orders; }
    IEnumerator Decide(string order, Player speaker, int version) {
        string token;
        try { token = File.ReadAllText(tokenFile.Value).Trim(); }
        catch { Say("My thoughts are quiet. I can still follow, stay, or report inventory."); yield break; }
        busy = true;
        Say("Give me a moment to think on that.");
        var me = Player.m_localPlayer;
        var payload = JsonConvert.SerializeObject(new { message = order, state = new {
            health = me.GetHealth(), maxHealth = me.GetMaxHealth(),
            stamina = me.GetStamina(), maxStamina = me.GetMaxStamina(),
            task = job.ToString().ToLowerInvariant(),
            biome = Heightmap.FindBiome(me.transform.position).ToString(),
            haveBed = TryParsePosition(bedPosition.Value, out _),
            places = places.Keys.ToArray(),
            chestNearby = Nearby<Container>(me.transform.position, 5f).Any(IsChest),
            stationNearby = StationNear(me.transform.position, 2.5f) != null,
            inventory = me.GetInventory().GetAllItems().Take(40).Select(i => new { name = Localization.instance.Localize(i.m_shared.m_name), count = i.m_stack })
        }});
        using (var request = new UnityWebRequest("http://127.0.0.1:8765/decide", "POST")) {
            pendingRequest = request;
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + token);
            request.timeout = 15;
            yield return request.SendWebRequest();
            pendingRequest = null;
            busy = false;
            if (version != generation || !Active || Player.m_localPlayer != me || !speaker) yield break;
            if (request.result != UnityWebRequest.Result.Success) { Say("My thoughts falter. Speak a simple order."); yield break; }
            Decision decision = null;
            try { decision = JsonConvert.DeserializeObject<Decision>(request.downloadHandler.text); } catch { }
            if (decision == null) yield break;
            // A blank item means "everything" for the sweeps but is a real argument for
            // eat and equip, where the reply is the planner's usual fallback slot.
            string item = Normalize(decision.item);
            switch (decision.action) {
                case "follow": Follow(speaker); break;
                case "come": Come(speaker); break;
                case "escort": Escort(speaker); break;
                case "mule": Mule(speaker); break;
                case "haul": HaulNow(speaker); break;
                case "stay": Halt("I'll hold here."); break;
                case "inventory": Inventory(); break;
                case "status": Status(); break;
                case "where": Where(); break;
                case "scan": Scan(); break;
                case "self_test": SelfTest(); break;
                case "survey": Survey(true); break;
                case "tend": if (!Tend(item)) Say("I see no " + item + " to tend."); break;
                case "recipe": if (!Recipes(speaker, item ?? decision.reply)) Say("I know no recipe by that name."); break;
                case "camp": CampReport(null); break;
                case "remember_home": RememberPlace(item ?? "home"); break;
                case "go_home": if (item == null) GoHome(); else if (!GoToPlace(item)) Say("I know no place called that."); break;
                case "places": ListPlaces(); break;
                case "claim_bed": ClaimNearbyBed(); break;
                case "go_to_bed": GoToBed(); break;
                case "eat": if (item == null) EatBest(); else if (!EatNamed(item)) Say("I have no food by that name."); break;
                case "equip": if (!EquipNamed(item ?? decision.reply)) Say("I have nothing like that to equip."); break;
                case "unequip": if (item == null) UnequipAll(); else if (!UnequipNamed(item)) Say("I have nothing like that equipped."); break;
                case "drop": if (!DropNamed(item ?? decision.reply)) Say("I carry nothing by that name."); break;
                case "bring": if (!Bring(speaker, item ?? decision.reply)) Say("I cannot find any " + (item ?? "of that") + " to bring you."); break;
                case "gather": if (!Fetch(item)) Say("I see no " + item + " on the ground."); break;
                case "harvest": if (!Harvest(item)) Say("I find no " + item + " growing here."); break;
                case "chop": Chop(); break;
                case "mine": Mine(); break;
                case "fight": if (!Fight(item)) Say("I see no " + item + " to fight."); break;
                case "guard": if (!Guard(item)) Say("I know no place called that to guard."); break;
                case "deposit": if (!Deposit(item)) Say("I have no " + item + " to put away."); break;
                case "withdraw": if (!Withdraw(item)) Say("I find no " + item + " to take."); break;
                case "drop_all": DropAll(); break;
                case "pile": {
                    int piled = Pile(me);
                    Say(piled == 0 ? "I've nothing worth piling up." : "Piled " + piled + " here. My gear stays with me.");
                    break;
                }
                case "craft": if (!Craft(item ?? decision.reply)) Say("I know no recipe for that."); break;
                case "repair": Repair(); break;
                case "feed_fire": FeedFire(); break;
                case "open_door": UseDoor(true); break;
                case "close_door": UseDoor(false); break;
                case "emote": if (!Emote(item)) Say(decision.reply); break;
                case "chat": Say(decision.reply); break;
            }
        }
    }
    class Decision { public string action; public string reply; public string item; }
    // Picks the point to walk toward for the running job, retargeting a sweep when
    // its current target is gone. Returns false once the job has finished or failed.
    bool NextGoal(Player player, out Vector3 goal, out float arrival) {
        goal = player.transform.position; arrival = 3f;
        switch (job) {
            case Job.Follow:
            case Job.Come:
            case Job.Escort:
            case Job.Mule:
                if (!target) { Stumble("lost the player"); Halt("I have lost you."); return false; }
                goal = target.transform.position;
                arrival = job == Job.Follow || job == Job.Come ? 3f : 4f;
                return true;
            case Job.Home:
            case Job.Bed:
            case Job.Haul:
                goal = destination;
                return true;
            case Job.Mend:
                // Deliberately tighter than the 1.8 m retarget test inside Mend, or he
                // arrives, is told he is still too far, retargets, and never closes.
                goal = destination; arrival = 1.5f;
                return true;
            case Job.Resume:
                // A mule run ends back at whoever he is carrying for, not at a spot.
                if (errand == Job.Mule || errand == Job.Come) {
                    if (!target) { Halt("Unloaded, but I've lost you."); return false; }
                    goal = target.transform.position; arrival = 4f;
                } else goal = anchor;
                return true;
            case Job.Patrol:
                goal = destination; arrival = 2.5f;
                return true;
            case Job.Grave:
                goal = destination;
                return true;
            case Job.Tend:
                if (Time.time > sweepUntil) { Halt(collected == 0 ? "Nothing more to do at the fires." : "Fires tended: " + collected + " items shifted."); return false; }
                if (!furnace) {
                    furnace = NextFurnace(player.transform.position);
                    // A full round with nothing to shift means the ore or the coal has
                    // run out, and standing there poking a cold smelter helps nobody.
                    if (!furnace && collected > lastRound) { lastRound = collected; visited.Clear(); furnace = NextFurnace(player.transform.position); }
                    if (!furnace) { Halt(collected == 0 ? "Nothing to shift — no ore, no coal, or nothing ready." : "Fires tended: " + collected + " items shifted."); return false; }
                }
                goal = furnace.transform.position; arrival = 2.5f;
                return true;
            case Job.Deliver:
                if (!target) { Halt("I have what you asked for, but I have lost you."); return false; }
                goal = target.transform.position; arrival = 2.5f;
                return true;
            case Job.Fetch:
                if (!sweepTarget || !IsLive(sweepTarget)) {
                    sweepTarget = NextDrop(anchor, sweepFilter, visited, player.transform.position);
                    reachedAt = 0f;
                    if (!sweepTarget) { FinishSweep("Nothing more lies about."); return false; }
                }
                goal = sweepTarget.transform.position; arrival = 1.6f;
                return true;
            case Job.Harvest:
                var pickable = sweepTarget as Pickable;
                if (!pickable || pickable.GetPicked()) {
                    sweepTarget = NextPickable(anchor, sweepFilter, visited, player.transform.position);
                    reachedAt = 0f;
                    if (!sweepTarget) { FinishSweep("Nothing more grows within reach."); return false; }
                }
                goal = sweepTarget.transform.position; arrival = 3.5f;
                return true;
            case Job.Chop:
            case Job.Mine:
                if (!sweepTarget) {
                    // Felling a tree destroys the trunk and spawns a log, which is also
                    // a target - so only the trunk scores, or every tree counts twice.
                    if (reachedAt != 0f && scoring) Progress();
                    reachedAt = 0f;
                    sweepTarget = NextBreakable(job, anchor, visited, player.transform.position);
                    // Felling scatters the wood well outside Valheim's 2 m pickup, so
                    // the job is not done until he has swept up what he knocked down.
                    if (!sweepTarget) { GleanOrFinish(job == Job.Chop ? "No tree is left standing here." : "No rock is left to break here."); return false; }
                }
                scoring = sweepTarget is TreeBase || sweepTarget is MineRock || sweepTarget is MineRock5;
                goal = Edge(sweepTarget, player.transform.position); arrival = Reach(player, sweepTarget);
                return true;
            case Job.Fight:
                var foe = sweepTarget as Character;
                if (!foe || foe.IsDead()) {
                    if (foe && foe.IsDead()) Progress();
                    reachedAt = 0f;
                    sweepTarget = NextFoe(anchor, sweepFilter, visited, player.transform.position);
                    if (!sweepTarget) { FinishSweep("The ground is quiet again."); return false; }
                }
                goal = Edge(sweepTarget, player.transform.position); arrival = Reach(player, sweepTarget);
                return true;
            default:
                return false;
        }
    }
    void Arrive(Player player) {
        switch (job) {
            case Job.Follow:
            case Job.Escort:
            case Job.Mule: break;
            case Job.Come: Halt("I'm here."); break;
            case Job.Home: Halt("I’m home."); break;
            case Job.Bed:
                if (bed) bed.Interact(player, false, false);
                Halt("I’ve reached the bed. The bed will decide if sleep is possible.");
                break;
            case Job.Patrol: postsFailed = 0; NextPost(); break;
            case Job.Grave: AtGrave(player); break;
            case Job.Tend:
                visited.Add(furnace.GetInstanceID());
                Progress(Work(furnace, player));
                furnace = null;
                break;
            case Job.Deliver: HandOver(player); break;
            case Job.Mend: Mend(player); break;
            case Job.Haul: Unload(player); break;
            case Job.Resume:
                var back = errand;
                errand = Job.None;
                sweepTarget = null; reachedAt = 0f;                sweepUntil = Time.time + SweepSeconds; // Each load gets a fresh clock.
                if (back == Job.Come) { Halt("Unloaded, and back with you."); break; }
                job = back;
                Say(back == Job.Mule ? "Back with you. Load me up." : "Back to it.");
                break;
            case Job.Fetch: TakeDrop(player); break;
            case Job.Harvest:
                var pickable = (Pickable)sweepTarget;
                visited.Add(pickable.GetInstanceID());
                sweepTarget = null;
                if (pickable.Interact(player, false, false)) Progress();
                break;
            case Job.Chop:
            case Job.Mine:
            case Job.Fight:
                Swing(player);
                break;
        }
    }
    // He is standing at the chest anyway: take a few meals if he has none on him.
    int Restock(Player player, Container chest) {
        if (!chest || HasFood(player)) return 0;
        var from = chest.GetInventory();
        int taken = 0;
        foreach (var item in from.GetAllItems().ToList()) {
            if (taken >= 3) break;
            if (item.m_shared.m_food <= 0f) continue;
            if (!player.GetInventory().CanAddItem(item) || !player.GetInventory().AddItem(item)) continue;
            from.RemoveItem(item);
            taken++;
        }
        if (taken > 0) grazeCheck = 0f;
        return taken;
    }
    // Drops the haul-worthy part of the pack on the ground and keeps his kit. Items
    // dropped in Valheim persist, so a pile at home is a slower chest, not a loss.
    int Pile(Player player) {
        int dropped = 0;
        foreach (var item in player.GetInventory().GetAllItems().ToList()) {
            if (item.m_equipped || !Spare(item)) continue;
            if (player.DropItem(player.GetInventory(), item, item.m_stack)) dropped++;
        }
        if (dropped > 0) piles.Add(player.transform.position);
        return dropped;
    }
    static bool NearAPile(List<Vector3> spots, Vector3 where) {
        foreach (var spot in spots) if (Vector3.Distance(spot, where) < 5f) return true;
        return false;
    }
    void Unload(Player player) {
        // A base is usually a row of chests, not one. Work along them nearest first
        // so a single full chest never stalls the run.
        var chests = Nearby<Container>(player.transform.position, 8f).Where(IsChest)
            .OrderBy(c => Vector3.Distance(c.transform.position, player.transform.position)).Take(8).ToList();
        int moved = 0;
        foreach (var chest in chests) {
            bool full;
            moved += MoveInto(chest, null, true, out full);
        }
        // Chests missing or all full must not end the job: leave the rest on the
        // ground at home and carry on, rather than standing there holding it.
        int piled = Loaded(player) && pileOver.Value ? Pile(player) : 0;
        if (moved == 0 && piled == 0) {
            Halt(chests.Count == 0
                ? (pileOver.Value ? "I'm home with a full pack and nothing here to put it in." : "No chest here, and you've told me not to pile it up.")
                : (pileOver.Value ? "The chests at home are full and there's nothing of mine worth leaving." : "The chests are full and you've told me not to pile the rest up."));
            return;
        }
        hauls++;
        int grabbed = 0;
        foreach (var chest in chests) { grabbed = Restock(player, chest); if (grabbed > 0) break; }
        string what = moved > 0 && piled > 0
                ? "Chests took " + moved + ", the rest is piled beside them."
            : moved > 0
                ? "Unloaded " + moved + (moved == 1 ? " stack" : " stacks") + " into " + chests.Count + (chests.Count == 1 ? " chest." : " chests.")
            : (chests.Count == 0 ? "No chest here — piled " : "Chests are full — piled ") + piled + " on the ground.";
        Say(what + (grabbed > 0 ? " Took food for the road." : "") + " Going back for more.");
        job = Job.Resume; reachedAt = 0f;    }
    void TakeDrop(Player player) {
        var drop = (ItemDrop)sweepTarget;
        // Only the ZDO owner may pick an item up, so ask first and try again next tick.
        if (!drop.CanPickup()) {
            if (reachedAt == 0f) reachedAt = Time.time;
            if (Time.time - reachedAt > 3f) Skip();
            else drop.RequestOwn();
            return;
        }
        if (player.Pickup(drop.gameObject, autoequip: false)) { Skip(); Progress(); return; }
        // Leave it unvisited: after a run home he should come back for this one.
        sweepTarget = null; reachedAt = 0f;
        if (Loaded(player) && !(Needed(player, out var need, out var spot, out var said) && Detour(need, spot, said)))
            FinishSweep("My pack has no room left.");
    }
    // One tick of walking toward a point. Every job and the fight overlay share it,
    // so steering, jumping, sprinting and stuck detection behave the same everywhere.
    Step StepToward(Lane lane, Player player, Vector3 goal, float arrival) {
        var s = Lane_(lane);
        // A lane idle for a moment, or whose goal has jumped, starts clean. Drift is
        // not a jump: following a moving player must keep its stuck clock running.
        if (Time.time - s.touched > 0.5f || Vector3.Distance(s.goal, goal) > 5f) {
            s.previous = player.transform.position;
            s.stuckTime = 0f;
            s.avoidUntil = 0f;
            s.sprinting = false;
        }
        s.touched = Time.time;
        s.goal = goal;
        Vector3 delta = goal - player.transform.position;
        delta.y = 0;
        // Clearing here covers every handover that can only happen from an arrival:
        // Mule, Resume, Unload, ResumeSweep, GleanOrFinish. Without it he could snag
        // while following, have you walk back into his arrival radius, and then be
        // declared wedged over a stall that had already cleared.
        if (delta.magnitude < arrival) { s.previous = player.transform.position; s.stuckTime = 0f; return Step.Arrived; }
        var direction = SelectWalkDirection(s, player.transform.position, delta.normalized);
        if (direction == Vector3.zero) {
            // Try the handle before declaring the way shut.
            if (TryDoor(player, delta)) return Step.Moving;
            ExplainBlocked(player.transform.position, delta.normalized,
                LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain"));
            return Step.Blocked;
        }
        direction.y = 0;
        direction.Normalize();
        int mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain");
        bool jump = Time.time >= jumpUntil && CanJumpForward(player.transform.position, direction, mask);
        if (jump) jumpUntil = Time.time + 1.2f;
        if (Vector3.Distance(s.previous, player.transform.position) < 0.01f) s.stuckTime += Time.fixedDeltaTime; else s.stuckTime = 0f;
        s.previous = player.transform.position;
        if (s.stuckTime > 3) return Step.Stuck;
        // Feed the real player controller so sprint and jump are handled like
        // ordinary input instead of only changing the replicated move vector.
        // Sprinting suppresses regen entirely and costs a further second of dead time
        // after it stops (Player.RPC_UseStamina resets m_staminaRegenTimer), so the
        // band has to be wide or he flickers in and out of a run every second.
        float stamina = player.GetStamina() / Mathf.Max(1f, player.GetMaxStamina());
        s.sprinting = delta.magnitude > 6f && (s.sprinting ? stamina > 0.15f : stamina > 0.4f);
        // Jump is deliberately NOT passed to SetControls: it fires Jump() internally,
        // before the line below puts the real heading into m_moveDir, so the leap
        // would be thrown along a stale look direction.
        player.SetControls(direction, false, false, false, false, false, false, false, false, s.sprinting, false);
        move.SetValue(player, direction);
        if (jump) player.Jump();
        return Step.Moving;
    }

    // ---- Fighting back ---------------------------------------------------

    // A fight is an overlay, not a job: it interrupts whatever he was doing and the
    // job resumes untouched the moment the ground is clear.
    bool Threatened(Player player) {
        bool guarding = job == Job.Patrol;
        bool escorting = job == Job.Escort && target;
        if (!guarding && !escorting && (!defendSelf.Value || job == Job.Fight)) { threat = null; return false; }
        // Badly hurt, he breaks off rather than trading blows he cannot win. While
        // escorting that means falling back to you and healing up, not standing still.
        if (player.GetHealth() < player.GetMaxHealth() * 0.35f || player.IsSwimming()) { threat = null; return false; }
        // Something that actually hit him outranks anything merely standing nearby:
        // an archer at thirty paces would otherwise be ignored entirely.
        if (hurtBy && !hurtBy.IsDead() && Time.time - hurtAt < 15f && threat != hurtBy) {
            // Focus fire. While he is on a boss for you, adds do not get to pull him
            // off it - that is the whole point of being asked along on the hunt.
            bool lockedOn = escorting && threat && !threat.IsDead() && threat.IsBoss();
            if (!lockedOn && WieldWeapon(player)) {
                threat = hurtBy;
                fightFrom = player.transform.position;
                Fresh(Lane.Fight);   // a new foe is a new approach, however close it stands
                if (Time.time - lastShout > 10f) {
                    lastShout = Time.time;
                    Say("Right — that one wants a fight. " + Localization.instance.Localize(hurtBy.m_name) + ".");
                }
                return true;
            }
        }
        // Keep the current foe while it lives and stays inside the leash.
        Vector3 leashFrom = guarding ? anchor : (escorting ? target.transform.position : fightFrom);
        float leash = guarding ? GuardSpan * 1.3f : (escorting ? 30f : 20f);
        if (threat && !threat.IsDead() && Vector3.Distance(threat.transform.position, leashFrom) <= leash) return true;
        threat = null;
        if (Time.time < threatScan) return false;
        threatScan = Time.time + 0.4f;
        // Escorting, he watches around YOU, not himself, so he goes for what the party
        // is fighting rather than whatever happens to be nearest him. On guard duty he
        // watches the whole camp. Otherwise he only answers what comes near him.
        Vector3 watchFrom = guarding ? anchor : (escorting ? target.transform.position : player.transform.position);
        float watch = guarding ? Mathf.Max(GuardSpan, patrolRing + 8f) : (escorting ? 22f : 12f);
        var found = Character.GetAllCharacters()
            .Where(c => WorthFighting(c, player) && Vector3.Distance(c.transform.position, watchFrom) <= watch)
            // The boss is the point of the hunt; adds are a distraction from it.
            .OrderByDescending(c => c.IsBoss()).ThenBy(c => Vector3.Distance(c.transform.position, watchFrom))
            .FirstOrDefault();
        if (found && unreachable.Contains(found.GetInstanceID())) {
            // Already proved he cannot get to this one. Take the next best, or none.
            found = Character.GetAllCharacters()
                .Where(c => WorthFighting(c, player) && !unreachable.Contains(c.GetInstanceID()) &&
                            Vector3.Distance(c.transform.position, watchFrom) <= watch)
                .OrderByDescending(c => c.IsBoss()).ThenBy(c => Vector3.Distance(c.transform.position, watchFrom))
                .FirstOrDefault();
        }
        if (!found || !WieldWeapon(player)) return false;
        threat = found;
        fightFrom = player.transform.position;
        Fresh(Lane.Fight);   // likewise: a pack member two paces on is still a new approach
        string name = Localization.instance.Localize(found.m_name);
        Logger.LogInfo("Engaging " + name);
        if (Time.time - lastShout > 10f) {
            lastShout = Time.time;
            Say(guarding ? name + " in the camp. I'll see to it."
                : found.IsBoss() ? "The " + name + ". Together, then." : name + "! Stand back.");
        }
        return true;
    }
    // Below the fighting threshold with something still hitting him, standing still is
    // just dying slowly. Run from the attacker, homeward if home lies that way.
    bool Fleeing(Player player) {
        if (player.GetHealth() >= player.GetMaxHealth() * 0.35f) return false;
        return hurtBy && !hurtBy.IsDead() && Time.time - hurtAt < 8f &&
               Vector3.Distance(hurtBy.transform.position, player.transform.position) < 25f;
    }
    void Flee(Player player) {
        if (Time.time - lastShout > 8f) { lastShout = Time.time; Say("I'm hurt. Falling back — finish it without me."); }
        Vector3 away = player.transform.position - hurtBy.transform.position;
        away.y = 0;
        if (away.sqrMagnitude < 0.0001f) away = player.transform.forward;
        away.Normalize();
        Vector3 goal = player.transform.position + away * 12f;
        if (places.TryGetValue("home", out var home)) {
            Vector3 toHome = home - player.transform.position;
            toHome.y = 0;
            if (toHome.magnitude > 5f && Vector3.Dot(toHome.normalized, away) > 0f) goal = home;
        }
        StepToward(Lane.Flee, player, goal, 2f);
    }
    // Foes he has proved he cannot reach, so he stops re-picking them every scan.
    // Cleared periodically, because terrain and the foe both move.
    readonly HashSet<int> unreachable = new HashSet<int>();
    float forgetUnreachable;
    bool winded;   // Backing off; needs real stamina back before closing again.
    void Engage(Player player) {
        EnsureHeld(player, Skills.SkillType.None);
        if (Time.time > forgetUnreachable) { unreachable.Clear(); forgetUnreachable = Time.time + 30f; }
        Vector3 edge = Edge(threat, player.transform.position);
        // Out of stamina in melee is just free hits for the other side. Back off,
        // let it come back, and close again when there is something to swing with.
        if (player.GetStamina() < (winded ? 25f : 8f) && player.GetMaxStamina() > 0f) {
            winded = true;
            Vector3 away = player.transform.position - edge; away.y = 0;
            if (away.sqrMagnitude > 0.0001f && away.magnitude < Reach(player, threat) * 2.5f) {
                SwingAt(player, threat);  // keep facing it while giving ground
                StepToward(Lane.Backoff, player, player.transform.position + away.normalized * 6f, 0.5f);
                return;
            }
        }
        winded = false;
        var step = StepToward(Lane.Fight, player, edge, Reach(player, threat));
        if (step == Step.Arrived) { SwingAt(player, threat); return; }
        // Cannot reach it: remember that, so the next scan does not pick it straight
        // back up and freeze the job he was doing.
        if (step != Step.Moving) {
            unreachable.Add(threat.GetInstanceID());
            Fresh(Lane.Fight);
            Stumble("cannot reach " + Localization.instance.Localize(threat.m_name));
            threat = null;        }
    }

    internal void Drive(Player player) {
        player.SetControls(Vector3.zero, false, false, false, false, false, false, false, false, false, false);
        autorun.SetValue(player, false);
        move.SetValue(player, Vector3.zero);
        if (player.IsDead()) { Halt(); return; }
        if (!player.IsSwimming()) dryGround = player.transform.position;   // where to swim back to
        Graze(player);
        if (Threatened(player)) { Engage(player); return; }
        if (Fleeing(player)) { Flee(player); return; }
        if (job == Job.None) {
            // Idle: nothing to detour from, so a meal is all that is left to want.
            if (Peckish(player)) TakeMorsel(player);
            return;
        }
        // Guard duty holds its post while hurt; every other job breaks off.
        // Nothing may stop him while he is afloat. Player.UpdateStamina zeroes the regen
        // multiplier off the ground, and OnSwimming only drains while moving, so a bot
        // halted in deep water floats there at that stamina until someone walks to the
        // machine - no in-game order can recover him.
        if (player.IsSwimming()) {
            if (Time.time - lastShout > 8f) { lastShout = Time.time; Say("I'm out of my depth. Making for shore."); }
            if (dryGround != Vector3.zero) StepToward(Lane.Flee, player, dryGround, 2.5f);
            return;
        }
        if (job != Job.Patrol && job != Job.Escort && player.GetHealth() < player.GetMaxHealth() * 0.3f) { Stumble("hurt, broke off"); Halt("I must stop here. I cannot go on safely."); return; }
        if (IsSweep && Time.time > sweepUntil) { FinishSweep("I have spent long enough at it."); return; }
        // Checked before picking a target: a full pack or a blunt tool means the next
        // thing to do is the errand, not another tree.
        // One question, asked in one place: is there anything he should break off to do?
        if ((IsSweep || job == Job.Mule) && errand == Job.None &&
            Needed(player, out var need, out var spot, out var said) && Detour(need, spot, said)) return;
        // Below the detours, so a full pack is emptied before he tries to pick up a
        // meal he has no room for, and never while he is part-way through an errand.
        if (errand == Job.None && job != Job.Deliver && job != Job.Grave && Peckish(player)) { TakeMorsel(player); return; }
        if (Scavenging(player)) { Scavenge(player); return; }
        if (!NextGoal(player, out Vector3 goal, out float arrival)) return;
        if ((job == Job.Follow || job == Job.Escort) && target) {
            Vector3 gap = target.transform.position - player.transform.position;
            // Only genuine distance ends a follow. A height gap is a staircase or a
            // boulder far more often than it is somewhere he cannot go, so he walks
            // to below you and lets the stuck check decide.
            if (gap.magnitude > 35 || Math.Abs(gap.y) > 20) { Stumble("leash broke"); Halt("You are beyond my reach. Return for me."); return; }
        }
        var step = StepToward(Lane.Job, player, goal, arrival);
        switch (step) {
            case Step.Arrived: Arrive(player); return;
            case Step.Moving: return;
            default:
                // A blocked sweep target or patrol post is skipped, not fatal.
                if (player.IsSwimming()) return;   // never stand still afloat
                if (IsSweep && sweepTarget) { Skip(); return; }
                if (job == Job.Patrol) {
                    // Try the next post, but no faster than a person would, and give up
                    // rather than spinning through the ring forever.
                    if (Time.time < postUntil) return;
                    postUntil = Time.time + 1f;
                    if (++postsFailed >= 8) { Stumble("patrol boxed in"); Halt("I cannot walk the bounds from here. Set me somewhere clearer."); return; }
                    NextPost();                    return;
                }
                // Wedged and shoving is a different failure from having nowhere to go,
                // and they want different fixes, so say which.
                Stumble(step == Step.Stuck ? "wedged" : "no way through");
                Halt(step == Step.Stuck ? "I'm wedged fast here. I'll wait." : "The way is blocked. I will wait here.");
                return;
        }
    }

    const float StepUp = 1.4f;    // The tallest lip he can hop onto.
    const float StepDown = 2.5f;  // The deepest drop worth taking on purpose.

    // How the ground just ahead compares with where he stands. Measuring the height
    // directly beats inferring it from a chest-height ray: a ledge lower than his
    // chest is invisible to that ray, and a ledge higher than it looks like a wall.
    static bool GroundAhead(Vector3 origin, Vector3 direction, float distance, int mask, out float rise, out float slope) {
        rise = 0f;
        slope = 90f;
        // Start just above the tallest lip he could step onto, never at head height.
        // A probe 3 m up begins ABOVE the roof of an ordinary house, so the ray lands
        // on the roof and every heading indoors reads as a two-metre wall - which
        // locked him out of the one place a workbench can legally be.
        Vector3 probe = origin + direction * distance + Vector3.up * (StepUp + 0.1f);
        if (!Physics.Raycast(probe, Vector3.down, out RaycastHit hit, StepUp + StepDown + 0.2f, mask)) return false;
        rise = hit.point.y - origin.y;
        slope = Vector3.Angle(hit.normal, Vector3.up);
        return true;
    }
    bool CanJumpForward(Vector3 origin, Vector3 direction, int mask) {
        direction.y = 0;
        direction.Normalize();
        if (!GroundAhead(origin, direction, 1.1f, mask, out float rise, out float slope)) return false;
        // Worth a jump only for a lip he can land on top of - not a slope he can walk
        // and not a wall he cannot clear.
        return rise >= 0.25f && rise <= StepUp && slope <= 40f;
    }

    // A closed door is a wall, and correctly reads as one - so steering rejects it and
    // he stands there shoving it. He has known how to work a handle since 0.3.0; he
    // was simply never asked to before giving up.
    bool TryDoor(Player player, Vector3 heading) {
        if (Time.time < doorUntil) return false;
        heading.y = 0f;
        if (heading.sqrMagnitude < 0.01f) return false;
        heading.Normalize();
        var door = Nearby<Door>(player.transform.position, 3f)
            .Where(d => {
                Vector3 gap = d.transform.position - player.transform.position;
                gap.y = 0f;
                // Only a door actually in his way, not one behind him.
                return gap.sqrMagnitude > 0.01f && Vector3.Dot(gap.normalized, heading) > 0.3f;
            })
            .OrderBy(d => Vector3.Distance(d.transform.position, player.transform.position)).FirstOrDefault();
        if (!door) return false;
        var view = door.GetComponent<ZNetView>();
        if (!view || !view.IsValid()) return false;
        if (view.GetZDO().GetInt(ZDOVars.s_state) != 0) return false;   // already open; something else blocks
        doorUntil = Time.time + 1.5f;   // let it swing before judging the way again
        if (!door.Interact(player, false, false)) {
            Logger.LogInfo("A door bars the way and will not open for me - locked, or warded.");
            return false;
        }
        Logger.LogInfo("Opened a door in the way.");
        return true;
    }
    // Logged when he gives up, so a stuck report becomes a lookup instead of a guess.
    string Probes(Vector3 origin, Vector3 desired, int mask) {
        var reasons = new List<string>();
        // The same headings SelectWalkDirection tries, or the report describes a search
        // he never ran.
        foreach (float angle in turns) {
            Vector3 candidate = Quaternion.AngleAxis(angle, Vector3.up) * desired;
            var refused = WhyRefused(origin, candidate, mask, out float rise, out float slope);
            string why;
            switch (refused) {
                case Refusal.None: why = "clear"; break;
                case Refusal.Void: why = "nothing to stand on"; break;
                case Refusal.Wall: why = "wall " + rise.ToString("0.0"); break;
                case Refusal.Drop: why = "drop " + rise.ToString("0.0"); break;
                case Refusal.Slope: why = "slope " + slope.ToString("0"); break;
                case Refusal.Chest: why = "wall at chest"; break;
                default: continue;
            }
            reasons.Add(angle.ToString("0") + "=" + why);
        }
        var shut = Nearby<Door>(origin, 3f).FirstOrDefault();
        return string.Join("; ", reasons) + (shut ? " (a door is within reach and did not open)" : "");
    }
    void ExplainBlocked(Vector3 origin, Vector3 desired, int mask) {
        if (Time.time - lastExplain < 5f) return;
        lastExplain = Time.time;
        Logger.LogInfo("Nowhere to step from " + Vector3ToConfig(origin) + ": " + Probes(origin, desired, mask));
    }
    // How far he can see along a heading before something stops him.
    static float Clearance(Vector3 origin, Vector3 direction, int mask, float range) {
        return Physics.Raycast(origin + Vector3.up * 0.6f, direction, out RaycastHit hit, range, mask)
            ? hit.distance : range;
    }
    static readonly float[] turns = { 0f, -30f, 30f, -60f, 60f, -90f, 90f, -120f, 120f, -150f, 150f, 180f };

    Vector3 SelectWalkDirection(Steering s, Vector3 origin, Vector3 desired) {
        int mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain");
        // Committed to rounding something: hold that line. A building takes several
        // metres to clear, and re-deciding every tick is what walks him back into it.
        if (Time.time < s.avoidUntil && IsWalkable(origin, s.avoidDirection, mask)) return s.avoidDirection;

        bool barred = !IsWalkable(origin, desired, mask);
        Vector3 best = Vector3.zero;
        float bestScore = float.NegativeInfinity;
        foreach (float angle in turns) {
            Vector3 candidate = Quaternion.AngleAxis(angle, Vector3.up) * desired;
            candidate.y = 0f;
            if (candidate.sqrMagnitude < 0.01f) continue;
            candidate.Normalize();
            if (!IsWalkable(origin, candidate, mask)) continue;
            float toward = Vector3.Dot(candidate, desired);
            // Look well past the next step, or he commits to a line that dead-ends a
            // metre later - which is how a workbench and its extensions trap him.
            float clear = Clearance(origin, candidate, mask, 6f);
            // With the straight line barred, open ground matters more than heading.
            // Hugging the goal direction is precisely what wedges him into a corner.
            float score = barred ? clear * 3f + toward * 2f : toward * 10f + clear;
            if (score > bestScore) { bestScore = score; best = candidate; }
        }
        if (best != Vector3.zero && Vector3.Dot(best, desired) < 0.95f) {
            s.avoidDirection = best;
            // Long enough to walk the length of a building, not just past a trunk.
            s.avoidUntil = Time.time + (barred ? 3f : 1f);
        }
        return best;
    }

    // Why a heading is refused. The decision and the diagnostic read the SAME function,
    // so the log can never disagree with what the steering actually did - the first
    // version of the report labelled open meadow as "blocked at chest", which is the
    // worst possible bug in the thing you reach for when something is stuck.
    enum Refusal { None, NoHeading, Void, Wall, Drop, Slope, Chest }

    Refusal WhyRefused(Vector3 origin, Vector3 direction, int mask, out float rise, out float slope) {
        rise = 0f; slope = 0f;
        direction.y = 0;
        if (direction.sqrMagnitude < 0.01f) return Refusal.NoHeading;
        direction.Normalize();
        if (!GroundAhead(origin, direction, 1.35f, mask, out rise, out slope)) {
            // No floor: open water reads exactly like a void, because the mask holds no
            // water layer. Once he is actually swimming that is fine to cross, and Drive
            // breaks off if the stamina keeping him up runs low.
            return Player.m_localPlayer && Player.m_localPlayer.IsSwimming() ? Refusal.None : Refusal.Void;
        }
        if (rise > StepUp) return Refusal.Wall;
        if (rise < -StepDown) return Refusal.Drop;
        // Valheim slides a player on anything past 38 degrees (Character.GetSlideAngle),
        // at 5 m/s and with no steering authority, so stay under it.
        if (slope > 35f) return Refusal.Slope;
        // A face at chest height only blocks when it is not a lip he can hop. This is the
        // case that stranded him: jumping used to be decided only AFTER a direction was
        // judged walkable, so he could never jump the thing that made it unwalkable.
        Vector3 chest = origin + Vector3.up * 0.65f;
        if (Physics.Raycast(chest, direction, out RaycastHit obstacle, 1.35f, mask) && obstacle.normal.y < 0.55f)
            return rise >= 0.25f ? Refusal.None : Refusal.Chest;
        return Refusal.None;
    }
    bool IsWalkable(Vector3 origin, Vector3 direction, int mask) {
        return WhyRefused(origin, direction, mask, out _, out _) == Refusal.None;
    }
    [HarmonyPatch(typeof(PlayerController), "FixedUpdate")]
    class Controls {
        static bool Prefix(PlayerController __instance) {
            var self = Instance;
            if (self == null || !self.Active || __instance.GetComponent<Player>() != Player.m_localPlayer) return true;
            self.Drive(Player.m_localPlayer); return false;
        }
    }
    internal void Note(string line) { Logger.LogInfo(line); }

    // Valheim's own -joinserverwithcharacter hardcodes FileSource.Local
    // (FejdStartup.SelectCharacter(name, FileHelpers.FileSource.Local)), so a character
    // kept in Steam Cloud is not found and a blank one is silently created and played.
    // Look the profile up and use the source it actually lives in.
    [HarmonyPatch(typeof(FejdStartup), "SelectCharacter")]
    class RealCharacter {
        static void Prefix(string fileName, ref FileHelpers.FileSource fileSource) {
            var profiles = SaveSystem.GetAllPlayerProfiles();
            if (profiles == null) return;
            var match = profiles
                .Where(p => p != null && string.Equals(p.GetFilename(), fileName, StringComparison.OrdinalIgnoreCase))
                // A name present in both places means a stray local copy shadowing the
                // real one, which is exactly what the hardcoded Local source creates.
                .OrderBy(p => p.m_fileSource == FileHelpers.FileSource.Local ? 1 : 0)
                .FirstOrDefault();
            if (match == null) {
                Instance?.Note("No saved character called '" + fileName + "'. Valheim will make a blank one.");
                return;
            }
            if (match.m_fileSource == fileSource) return;
            Instance?.Note("Character '" + fileName + "' lives in " + match.m_fileSource +
                           ", not " + fileSource + ". Loading the real one.");
            fileSource = match.m_fileSource;
        }
    }

    // Retaliation is driven by real damage, not by proximity, so a ranged attacker
    // that never comes close still gets answered.
    [HarmonyPatch(typeof(Character), "Damage")]
    class Hurt {
        static void Postfix(Character __instance, HitData hit) {
            if (Instance == null || hit == null || __instance != Player.m_localPlayer) return;
            Instance.Struck(hit);
        }
    }
    // Death clears m_localPlayer, so the spot is captured before the game handles it
    // and acted on once the new body has spawned.
    [HarmonyPatch(typeof(Player), "OnDeath")]
    class Death {
        static void Prefix(Player __instance) {
            if (Instance != null && __instance == Player.m_localPlayer) Instance.RememberGrave(__instance.transform.position);
        }
    }
    [HarmonyPatch(typeof(Player), "OnSpawned")]
    class Spawned {
        static void Postfix(Player __instance) {
            if (Instance != null && Player.m_localPlayer == __instance) Instance.AfterRespawn();
        }
    }
    [HarmonyPatch(typeof(Chat), "OnNewChatMessage")]
    class Messages {
        static void Postfix(GameObject go, long senderID, string text) { Instance?.Receive(go, senderID, text); }
    }
}
}
