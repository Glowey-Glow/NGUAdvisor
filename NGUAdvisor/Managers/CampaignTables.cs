using System;
using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // The guide's challenge-block campaign as a static table, plus the pure derivations the companion's
    // `campaign` snapshot node is built from.
    //
    // WHY A TABLE AND NOT A SHIPPED JSON. This is REFERENCE data, not user data: the block sequence is the
    // community guide's structure (https://github.com/sayolove/ngu-guide), not something a player configures.
    // A JSON manifest beside the profiles would only invite drift between the campaign's idea of a block and
    // the profiles it names. Same reasoning — and the same shape — as TitanTables: pure data + pure functions,
    // no Unity, no game types, no file IO, so the whole thing links into the test assembly and the derivations
    // are unit-testable WITHOUT the game attached. UiBridge does the live reads and the JSON emission.
    //
    // ONE SPINE, NOT THREE TRACKS. The guide states the order in four separate TL;DR lines (Ch3/Ch4/Ch6/Ch8)
    // and it crosses difficulties: Normal blocks, then an Evil entry Basic, then Evil blocks, then Sadistic.
    // `Order` is therefore a single total order over the whole campaign, and difficulty is an ATTRIBUTE OF A
    // LEG, not a partition of the table. Modelling it as three parallel tracks would lose the sequencing that
    // is the entire point of the view.
    //
    // A BLOCK CAN SPAN DIFFICULTIES. CBlock 3 (Ch5) is one guide block with two legs: an Evil challenge list
    // plus a deliberate return trip to Normal ("Finish all Normal challenges (Rebirth to Normal, wait 3
    // minutes, then start challenges)") to clear the leftovers Ch4 told you to skip. That is why
    // CBlock3.0-N ships in the Normal folder while CBlock3.1-E100LC / CBlock3.2-E ship in Evil, and why all
    // three are Chapter 5. Flattening the legs would either misfile CBlock3.0-N as a Chapter 4 artifact or
    // lose the cross-difficulty step entirely.
    //
    // DIFFICULTY LIVES HERE BECAUSE IT LIVES NOWHERE ELSE. Nothing inside a profile declares the difficulty
    // it was written for; the sample tree records it in the FOLDER name, and the install flow (copy the files
    // into <profiles>\) plus the flat loader (Directory.GetFiles with no AllDirectories, resolution by bare
    // name) throw that folder away. `NGUDiff` is NOT it — that is the NGU level-track knob. So this table is
    // the only place a profile is bound to a difficulty, which is exactly what makes the per-difficulty
    // completion arithmetic below possible at all.
    public static class CampaignTables
    {
        // ---------------------------------------------------------------- vocabulary
        public const string Normal = "Normal";
        public const string Evil = "Evil";
        public const string Sadistic = "Sadistic";

        public const string KindBlock = "block";   // a block the guide names and orders
        public const string KindBasic = "basic";   // the Evil-entry Basic: sequenced by the guide, not a block
        public const string KindMopUp = "mopup";   // shipped sweep with no guide block behind it (PostEND)

        public const string StateComplete = "complete";
        public const string StateActive = "active";
        public const string StateNext = "next";
        public const string StateUpcoming = "upcoming";

        public const string BreakStranded = "stranded";  // the union has a gap and entries sit above it
        public const string BreakAbsent = "absent";      // the guide prescribes ordinals no profile supplies

        public const string MalformedParse = "parse";    // the file would not parse at all
        public const string MalformedNested = "nested";  // Challenges nested inside Breakpoints.Rebirth

        // Difficulty order along the spine. -1 = not a difficulty we sequence.
        public static int DifficultyRank(string difficulty)
        {
            if (string.Equals(difficulty, Normal, StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(difficulty, Evil, StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(difficulty, Sadistic, StringComparison.OrdinalIgnoreCase)) return 2;
            return -1;
        }

        // ---------------------------------------------------------------- advisor-owned challenges
        // Laser Sword is NOT profile-owned and must never be reported as a broken chain.
        //
        // The advisor runs it itself: LscAdvisor.Compute() estimates time-to-target from the GAME'S OWN aug
        // rate functions (so gear/macguffin/hack/card aug-speed bonuses and the sadistic divider are in the
        // number by construction), charges the full level cost because engaging LSC zeroes the sword's aug and
        // upgrade levels, applies a 2x safety factor and recommends at <= 48 minutes. BaseRebirth
        // .TryStartAdvisorLsc() then redirects an ALREADY-SCHEDULED rebirth into it. It is deliberately NOT
        // consulted by AnyChallengesValid(), so it never pulls a rebirth forward, and profile-scheduled
        // challenges keep precedence (TryStartChallenge tries FirstOrDefault(ChallengeValid) first and only
        // then falls through). ChallengeOverlay additionally prepends CAPAUG-12/13 while LSC runs so the sword
        // finishes fast.
        //
        // So LSC completions are meant to accrue OPPORTUNISTICALLY across the whole campaign — which is also
        // why the guide never names LSC in any block's challenge list (it appears four times in 52 pages, none
        // of them a block roster). CBlock2.0-LSC and CBlock5 used to carry LSC-3..20 runs; they were a
        // redundant second mechanism that could not fire as shipped (3 can never match with 1 and 2 absent)
        // and both have been removed. PostEND-Challenges still ships LSC-1..20, the only chained-from-1 run
        // anywhere, and it is reported here rather than as a break. This category stays because the codes are
        // advisor-owned by nature, not because any particular profile happens to list them today.
        //
        // BOTH advisor paths are gated on Settings.AdvisorChallenges — entry (BaseRebirth.cs:136) AND the
        // sword focus (ChallengeOverlay.cs:240). With that switch off neither mechanism runs, which is a
        // reportable state, not a defect in the presets.
        public static readonly string[] AdvisorOwnedCodes = { "LSC" };

        public static bool IsAdvisorOwned(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            foreach (var c in AdvisorOwnedCodes)
                if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // The advisor that owns a code, for the UI's "who is supposed to run this" line.
        public static string OwnerOf(string code) => IsAdvisorOwned(code) ? "LscAdvisor" : null;

        // ---------------------------------------------------------------- table shape

        // One difficulty-scoped leg of a block. A block has one leg (the usual case) or two (CBlock 3).
        public sealed class Leg
        {
            public readonly string Difficulty;
            public readonly string[] Profiles;    // shipped filenames (no extension), in run order
            public readonly string[] Legacy;      // superseded files kept only so the node can REPORT them
            public readonly string[] Ordinals;    // the guide's own list, as CODE-n
            public readonly string[] Optional;    // guide-sanctioned extras ("the fourth is optional")
            public readonly string[] OpenEnded;   // codes the guide leaves to the player ("as many as you want")
            public readonly string Note;

            public Leg(string difficulty, string[] profiles, string[] ordinals,
                       string[] optional = null, string[] openEnded = null, string[] legacy = null, string note = null)
            {
                Difficulty = difficulty;
                Profiles = profiles ?? new string[0];
                Ordinals = ordinals ?? new string[0];
                Optional = optional ?? new string[0];
                OpenEnded = openEnded ?? new string[0];
                Legacy = legacy ?? new string[0];
                Note = note;
            }
        }

        public sealed class Block
        {
            public readonly string Id;            // stable, lowercase, safe as a UI key
            public readonly string Name;          // the guide's own name for it
            public readonly int Chapter;          // guide chapter (Ch1..Ch8)
            public readonly int Order;            // position along the single campaign spine, 1-based
            public readonly int GuideNumber;      // the guide's own block number (1..9); 0 = not a numbered block
            public readonly string Kind;          // KindBlock / KindBasic / KindMopUp
            public readonly Leg[] Legs;
            public readonly string EntryGate;     // human text — the guide states no machine-checkable test
            public readonly string HandsBackTo;   // the prose handoff that ends the block
            public readonly int CadenceMinutes;   // rebirth cadence in wall-clock minutes; 0 = guide doesn't say
            public readonly string Note;

            public Block(string id, string name, int chapter, int order, int guideNumber, string kind,
                         Leg[] legs, string entryGate, string handsBackTo, int cadenceMinutes = 0, string note = null)
            {
                Id = id; Name = name; Chapter = chapter; Order = order; GuideNumber = guideNumber;
                Kind = kind; Legs = legs ?? new Leg[0];
                EntryGate = entryGate; HandsBackTo = handsBackTo; CadenceMinutes = cadenceMinutes; Note = note;
            }

            // A block with no profile behind it exists in the guide but not on disk. Every block ships one
            // today (the Micro-CBlock was the last that did not); this stays because the campaign can grow a
            // guide block before a profile for it is written, and the derivations below key off it.
            public bool Shipped
            {
                get { foreach (var l in Legs) if (l.Profiles.Length > 0) return true; return false; }
            }

            // "Evil" for a single-leg block, "Evil + Normal" for CBlock 3. The UI shows this verbatim.
            public string DifficultyLabel
            {
                get
                {
                    var seen = new List<string>();
                    foreach (var l in Legs) if (!seen.Contains(l.Difficulty)) seen.Add(l.Difficulty);
                    return string.Join(" + ", seen.ToArray());
                }
            }

            public string PrimaryDifficulty => Legs.Length > 0 ? Legs[0].Difficulty : null;
        }

        // ---------------------------------------------------------------- the campaign
        //
        // Ordinals are the guide's words converted to CODE-n, assuming a fresh account on that difficulty.
        // Where the guide gives a cumulative number ("up to the fourth No Rebirth") the range is the REMAINDER
        // left for that block. Where the guide is silent the field is empty and the note says so — a silent
        // guide is a finding, not a gap to fill by guessing.
        public static readonly Block[] Blocks =
        {
            new Block("micro", "Micro-CBlock", 2, 1, 1, KindBlock,
                new[]
                {
                    new Leg(Normal,
                        new[] { "C-Microblock1-Basics" },
                        new[] { "BASIC-1", "BASIC-2", "BASIC-3", "BASIC-4", "BASIC-5", "24HR-1" },
                        note: "5 Basic Challenges and 1 24H Challenge (Ch2). The Basics moved here from the " +
                              "Mini-CBlock because they need no NGU levels (T4P); Mini keeps them only as a fallback. " +
                              "Challenge order is forced, not a preference: 24H does not unlock until a Basic has " +
                              "been finished inside 24 hours, so the profile runs BASIC-1..5 and then 24HR-1.")
                },
                "Boss 100 defeated, some AVSP gear, T4 unlock puzzle started",
                "LRB to Mega / T4",
                15,   // "It's suggested to do 15 minute rebirths to optimize number gain" (Ch2)
                "Everything the Normal campaign chains off BASIC and 24HR starts here: 24HR-1 in this block is " +
                "what lets C-Miniblock2.4-Finish's 24HR-2 fire, and so on up to CBlock3.0-N's 24HR-10. The table " +
                "predated C-Microblock1-Basics.json and named no profile at all, which stranded that whole line."),

            new Block("mini", "Mini-CBlock", 3, 2, 2, KindBlock,
                new[]
                {
                    new Leg(Normal,
                        new[] { "C-Miniblock2.1-100lc", "C-Miniblock2.2-NoTM", "C-Miniblock2.3-NoRB", "C-Miniblock2.4-Finish" },
                        new[] { "NORB-1", "BLIND-1", "NOTM-1", "100LC-1", "100LC-2", "100LC-3", "100LC-4", "100LC-5", "24HR-2" },
                        note: "Guide order is NoRB 1 -> Blind 1 -> NoTM 1 -> 5x100LC -> 24H; the profiles run " +
                              "100LC first. Every ordinal involved is 1, so this is a strategy difference, not a chain break.")
                },
                "T4 AutoKill, then push PAWGs (Power/Augments/Wandoos/Gold NGUs) to 500 each",
                "24h rebirths focusing NGU Adventure",
                0,    // guide doesn't say; only "Expect the NoRB to take a while, potentially 12+ hours"
                "Four legs, one per guide clause. C-Miniblock2.3-NoRB used to be an unfinished stub — empty " +
                "Challenges, no rebirth plan, deliberately not auto-installed — which meant NORB-1 was supplied " +
                "by nothing and every later Normal No-Rebirth (CBlock1's 2..4, CBlock2's 5..10) was stranded. It " +
                "now runs NORB-1 on the Number 100 plan its three siblings use and ships like them."),

            new Block("cblock1", "CBlock1", 3, 3, 3, KindBlock,
                new[]
                {
                    new Leg(Normal,
                        new[] { "CBlock1" },
                        new[]
                        {
                            "NOAUG-1", "NOAUG-2", "NOAUG-3", "NOAUG-4", "NOAUG-5",
                            "NOEC-1", "NOEC-2", "NOEC-3", "NOEC-4", "NOEC-5",
                            "NORB-2", "NORB-3", "NORB-4",
                            "BLIND-2", "BLIND-3", "BLIND-4",
                            "NONGU-1", "NONGU-2", "NONGU-3", "NONGU-4",
                            "TC-1", "TC-2", "TC-3",
                            "NOTM-2", "NOTM-3",
                            "24HR-3"
                        },
                        optional: new[] { "TC-4" },   // "with the fourth optional for a beard slot" (Ch3)
                        note: "Exact match to the guide's list, with the optional TC-4 taken. Most important: " +
                              "Troll Challenge 3 and No Rebirth 4.")
                },
                "T5 killed and CCoD obtained; PAWGs 5k each; 1k% Rich Jerks Attack/Defense Boost",
                "24h rebirths, NGUs and Beards on Adventure; dump the block's EXP into Magic for a 5:1 E:M",
                0,    // guide doesn't say for the block; Chal's 100LC tip is 3m ideally, under 10m
                null),

            new Block("cblock2", "CBlock2", 4, 4, 4, KindBlock,
                new[]
                {
                    new Leg(Normal,
                        // CBlock2-Normal is the name that ACTUALLY INSTALLS (Presets\CBlock2-Normal.json);
                        // CBlock2 is the SampleProfiles file it supersedes and is kept so a player who copied
                        // the sample tree in by hand still resolves onto this block. They are the same
                        // 26-challenge rotation, so a player holding BOTH will see the health report name every
                        // one of those ordinals as a duplicate — expected, and self-healing at runtime
                        // (whichever fires first spends the completion). Only one of the two needs to be present.
                        new[] { "CBlock2-Normal", "CBlock2", "CBlock2.0-LSC" },
                        new[]
                        {
                            "NORB-5", "NORB-6", "NORB-7", "NORB-8", "NORB-9", "NORB-10",
                            "BLIND-5", "BLIND-6", "BLIND-7", "BLIND-8", "BLIND-9", "BLIND-10",
                            "NONGU-5", "NONGU-6", "NONGU-7", "NONGU-8", "NONGU-9", "NONGU-10",
                            "TC-5", "TC-6", "TC-7",
                            "NOTM-4", "NOTM-5",
                            "24HR-4", "24HR-5"
                        },
                        note: "\"Finish all challenges aside from the last 5 NoTM and 24H\" (Ch4). The profile " +
                              "runs 24HR-4..6, one past the guide's stop; harmless because CBlock3.0-N resumes at 7. " +
                              "CBlock2.0-LSC contributes NO ordinals: its LSC-3..20 queue was removed and it is now " +
                              "an ALLOCATION profile — the Number -> 100000 plan and Aug 12/13 push to run while " +
                              "LscAdvisor takes you through Laser Sword (LSC is the only challenge that does not " +
                              "reset the number). It is kept on this block because that is the block it tunes for.")
                },
                "T6v2 killed; 150k PAWGs; 10k% Rich Jerks A/D; Voodoo Doll and Stealthiest",
                "T6v3 push (Beasted Boosts / Boosted Boosts / ICB)",
                0,    // guide doesn't say for the block
                null),

            // Not a block, but the guide sequences it — so it sits in the spine with its own kind.
            new Block("evil-entry-basic", "Evil entry Basic", 5, 5, 0, KindBasic,
                new[]
                {
                    new Leg(Evil,
                        new[] { "EvilStart" },
                        new[] { "BASIC-1" },
                        note: "\"After entering Evil, start a Basic challenge as soon as possible... Don't do more " +
                              "than 1\" (Ch5). Evil BASIC-1 IS provided — just not by a *block* file, which is why " +
                              "a filename sweep for \"block\" misses it. CBlock3.2-E also lists BASIC-1; that is " +
                              "self-healing (the duplicate simply never matches once this one has fired).")
                },
                "Entering Evil: T6v4 defeated once, Boss 300 defeated once, 1m% Attack Boost from Rich Jerks + Perks",
                "30-minute rebirth climb to Boss 60-80, stopping when a rebirth no longer gains 100x number",
                30,   // "Do 30 minute rebirths at the start" (Ch5)
                "One Basic only. The guide is explicit that more is a waste: \"you don't need the adventure " +
                "stats, it's more important to climb.\""),

            new Block("cblock3", "CBlock 3", 5, 6, 5, KindBlock,
                new[]
                {
                    new Leg(Evil,
                        new[] { "CBlock3.1-E100LC", "CBlock3.2-E" },
                        new[]
                        {
                            "100LC-1", "100LC-2", "100LC-3", "100LC-4", "100LC-5",
                            "NOAUG-1", "NOAUG-2", "NOAUG-3", "NOAUG-4", "NOAUG-5",
                            "BASIC-1", "BASIC-2", "BASIC-3", "BASIC-4", "BASIC-5",
                            "TC-1",
                            "NONGU-1", "NOTM-1",
                            "NORB-1", "NORB-2", "NORB-3", "NORB-4", "NORB-5", "NORB-6",
                            "BLIND-1"
                        },
                        optional: new[] { "TC-2" },   // "1 is mandatory, 2 is strongly recommended but optional"
                        legacy: new[] { "cblock3-evil" },
                        note: "The Evil leg."),
                    new Leg(Normal,
                        new[] { "CBlock3.0-N" },
                        new[]
                        {
                            "NOTM-6", "NOTM-7", "NOTM-8", "NOTM-9", "NOTM-10",
                            "24HR-7", "24HR-8", "24HR-9", "24HR-10"
                        },
                        note: "The Normal return trip: \"Finish all Normal challenges (Rebirth to Normal, wait 3 " +
                              "minutes, then start challenges)\" (Ch5) — i.e. the last-5 NoTM and 24H that Ch4 told " +
                              "you to leave. Chapter 5 work living in the Normal folder, the thing most likely to " +
                              "be misread as a Chapter 4 artifact.")
                },
                "T7 killed and Boss 125 reachable within an hour; Incriminating Evidence maxed; Grey Heart bought",
                "24h rebirths focusing Adventure stats, then Adventure Hack",
                0,    // guide doesn't say
                "One guide block, two difficulties, three files. The Evil and Normal completion counters are " +
                "separate, which is what makes the return trip work at all."),

            new Block("cblock4", "CBlock 4", 6, 7, 6, KindBlock,
                new[]
                {
                    new Leg(Evil,
                        new[] { "CBlock4" },
                        new[]
                        {
                            "NORB-7", "NORB-8", "NORB-9", "NORB-10",
                            "TC-3", "TC-4", "TC-5",
                            "NONGU-2", "NONGU-3", "NONGU-4", "NONGU-5", "NONGU-6", "NONGU-7",
                            "NOEC-1", "NOEC-2", "NOEC-3", "NOEC-4", "NOEC-5"
                        },
                        openEnded: new[] { "BLIND", "24HR", "NOTM" },   // "as many as you want to do" (Ch6)
                        // NO LEGACY ENTRY, DELIBERATELY. The superseded sampleprofiles\cblock4.json has the
                        // same nested-Challenges bug as cblock3-evil, but its bare name is the SAME NAME as
                        // the shipped Evil\CBlock4.json once case is folded — and profile resolution is by
                        // bare name on a case-insensitive filesystem. Listing it here would make the campaign
                        // resolve "cblock4" to the good file and then report the good file as legacy. It is a
                        // live instance of the silent flat-enumeration collision, so it is named in the note
                        // instead of modelled as a separately addressable profile.
                        note: "Guide gives FINAL completion numbers, not counts. Most important: Troll 5 and " +
                              "NoNGU 7, which provide Hack Speed for Hack Day 1. Only 8 NoRBs are strictly needed " +
                              "to get T9's timer to 1 hour. Loadout: best Aug item (Meeple), best Wandoos item " +
                              "(Cocktail), then EMPC up to a 3-minute cap time.")
                },
                "1 of each Typo item obtained (Typo snipe at ~150 Quin / 68 Quin P/T)",
                "Hack Day 1",
                0,    // guide doesn't say; Ch6 FAQ states the global default of 24h rebirths
                "Order matters: starting challenges resets AT / Temp Beards / Banks, so the Typo snipe has to " +
                "happen BEFORE the block or you climb back up for Hack Day 1 gear. Note: the superseded " +
                "sampleprofiles\\cblock4.json (Challenges nested inside Breakpoints.Rebirth, loads as zero) " +
                "cannot be tracked separately — flattened into the profiles dir its name collides with this " +
                "block's own CBlock4.json. Delete it rather than copying it in."),

            new Block("beucblock", "BEUCBlock", 7, 8, 7, KindBlock,
                new[]
                {
                    new Leg(Evil,
                        new[] { "CBlock5", "FinalEvil24hCBlock" },
                        new[]
                        {
                            "TC-6", "TC-7",
                            "NONGU-8", "NONGU-9", "NONGU-10",
                            "BLIND-8", "BLIND-9", "BLIND-10",
                            "24HR-6", "24HR-7", "24HR-8", "24HR-9", "24HR-10",
                            "NOTM-6", "NOTM-7", "NOTM-8", "NOTM-9", "NOTM-10"
                        },
                        note: "\"Finish all Trolls, NoNGUs, Blinds. Complete up to 5 24Hs and NoTMs\" (Ch7). After " +
                              "CBlock 4's four, five more 24Hs is 6..10: CBlock5 takes 6..7 and FinalEvil24hCBlock " +
                              "the remaining 8..10. FinalEvil24hCBlock used to open at 9, which nothing could " +
                              "reach — CBlock3.2-E supplies Evil 24HR-1, CBlock4 2..5 and CBlock5 6..7, so 8 was " +
                              "supplied by no profile and both its entries were dead. CBlock5 also used to carry " +
                              "an LSC-3..20 run the guide never asks for; removed — see AdvisorOwnedCodes.")
                },
                "BEUC obtained (max the T9 set -> Sack of the Exile -> assemble the Exile parts). No prep required.",
                "BEUCDay hackday, then 24h rebirths",
                0,    // guide doesn't say; "1-2 days free" is a duration for the whole block, not a cadence
                "Called \"The Final CBlock\" in Ch7, which is NOT the Sadistic Final CBlock of Ch8."),

            new Block("sad-entry", "Sadistic entry CBlock", 8, 9, 8, KindBlock,
                new[]
                {
                    new Leg(Sadistic,
                        new[] { "SADCBlock1" },
                        new[]
                        {
                            "BASIC-1", "BASIC-2", "BASIC-3", "BASIC-4", "BASIC-5",
                            "NOAUG-1", "NOAUG-2", "NOAUG-3", "NOAUG-4", "NOAUG-5",
                            "100LC-1", "100LC-2", "100LC-3", "100LC-4", "100LC-5",
                            "NOEC-1", "NOEC-2", "NOEC-3", "NOEC-4", "NOEC-5",
                            "NORB-1", "NORB-2", "NORB-3", "NORB-4", "NORB-5",
                            "NORB-6", "NORB-7", "NORB-8", "NORB-9", "NORB-10",
                            "TC-1", "TC-2"
                        },
                        note: "Exact match to the guide's list. The profile ADDS 24HR-1..2, which the guide does " +
                              "not mention but which seeds the Sadistic 24H chain that FinalCBlock continues at 3 " +
                              "and PostEND-Challenges finishes at 8..10. TC2 is called out specifically: it moves " +
                              "peak guff efficiency from 30m to 24h of a rebirth, which is what makes muff " +
                              "rebirths worth running.")
                },
                "Entering Sadistic: Boss 300 defeated on Evil and T9v4 killed 24 times",
                "23h muff rebirths -> West World snipe -> LRB to T10v1",
                0,    // guide doesn't say for the block
                "Sadistic is the only difficulty whose supplied union is complete with no gaps."),

            new Block("sad-final", "Final CBlock", 8, 10, 9, KindBlock,
                new[]
                {
                    new Leg(Sadistic,
                        new[] { "FinalCBlock" },
                        new[]
                        {
                            "TC-3", "TC-4", "TC-5", "TC-6", "TC-7",
                            "BLIND-1", "BLIND-2", "BLIND-3", "BLIND-4", "BLIND-5",
                            "BLIND-6", "BLIND-7", "BLIND-8", "BLIND-9", "BLIND-10"
                        },
                        optional: new[] { "24HR-3", "24HR-4", "24HR-5", "24HR-6", "24HR-7" },   // "anything with rewards"
                        note: "\"Finish TC's, Blinds, anything with rewards (skip NoTM/NoNGU)\" (Ch8). The profile " +
                              "omitting NoTM and NoNGU is an EXACT MATCH, not a gap — PostEND-Challenges picks " +
                              "them up. The 24HR-3..7 it does take is the \"anything with rewards\" clause.")
                },
                "Post-T11 / T11v1 AutoKill, with the T11 set maxed",
                "Muff rebirths from T11v1 to T11v2",
                0,    // guide doesn't say
                null),

            // Shipped, sequenced after everything, but the guide has no block for it.
            new Block("postend", "Post-END challenges", 8, 11, 0, KindMopUp,
                new[]
                {
                    new Leg(Sadistic,
                        new[] { "PostEND-Challenges" },
                        new[]
                        {
                            "NONGU-1", "NONGU-2", "NONGU-3", "NONGU-4", "NONGU-5",
                            "NONGU-6", "NONGU-7", "NONGU-8", "NONGU-9", "NONGU-10",
                            "NOTM-1", "NOTM-2", "NOTM-3", "NOTM-4", "NOTM-5",
                            "NOTM-6", "NOTM-7", "NOTM-8", "NOTM-9", "NOTM-10",
                            "24HR-8", "24HR-9", "24HR-10"
                        },
                        note: "The Sadistic sweep for everything Ch8's Final CBlock deliberately skipped. It also " +
                              "runs LSC-1..20 — the only profile anywhere that starts LSC at 1.")
                },
                "After THE END",
                "-",
                0,
                "Not a guide block. Shipped, and sequenced last so the campaign view does not pretend the " +
                "Sadistic Final CBlock is the end of the challenge work."),
        };

        // ---------------------------------------------------------------- what actually installs
        //
        // PresetInstaller writes every embedded Presets\*.json into the player's profiles dir on startup —
        // FLAT (the difficulty folder of the sample tree is not part of the resource name) and
        // non-destructively (an existing file is never overwritten). So the set of names above that a fresh
        // install can actually resolve is exactly the set of Presets\*.json basenames.
        //
        // WHY THIS EXISTS. The table used to be transcribed from SampleProfiles\<difficulty>\, which ships to
        // nobody — it is repo-tree content linked from the readme. A leg naming a profile that no preset
        // installs points the campaign view at a file the player does not have, and nothing said so. The entry
        // below is the only deliberate exception; anything else that fails to resolve is a mistake,
        // and the test that pins this can tell the two apart only because the exception is written down
        // WITH its reason. A name listed here that DOES install is equally a defect (a stale exemption), so
        // the test checks both directions.
        //
        // C-Miniblock2.3-NoRB WAS the second entry: an unfinished stub with an empty Challenges array and no
        // rebirth plan, held back rather than shipped empty. It now runs NORB-1 and auto-installs, so the
        // exemption is gone — which is what "a stale exemption is the same defect wearing the other hat" is
        // there to force.
        public static readonly Dictionary<string, string> NotAutoInstalled =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "CBlock2",
              "Superseded by CBlock2-Normal, which is the file that auto-installs: same 26-challenge rotation, " +
              "plus challenge-tagged Energy/Magic breakpoints, R3 and NGUDiff that the sample lacks. The sample " +
              "stays in SampleProfiles for reference and still resolves onto this block if a player copied the " +
              "tree in by hand." },
        };

        // True when a fresh install resolves this campaign profile without the player copying anything in.
        public static bool IsAutoInstalled(string profileName) =>
            !string.IsNullOrEmpty(profileName) && !NotAutoInstalled.ContainsKey(profileName);

        // ---------------------------------------------------------------- lookups

        // The block a profile belongs to, or null. Match is by bare name, case-insensitive — the same way the
        // loader resolves profiles (CustomAllocation / ProfileService), so a name that matches here is a name
        // the runtime would actually load. A profile the user wrote themselves is simply NOT in the campaign,
        // and callers must say that rather than guess a nearest block.
        public static Block BlockOfProfile(string profileName)
        {
            if (string.IsNullOrEmpty(profileName)) return null;
            foreach (var b in Blocks)
                foreach (var l in b.Legs)
                    foreach (var p in l.Profiles)
                        if (string.Equals(p, profileName, StringComparison.OrdinalIgnoreCase)) return b;
            return null;
        }

        // The leg (hence the difficulty) a profile belongs to, or null.
        public static Leg LegOfProfile(string profileName)
        {
            if (string.IsNullOrEmpty(profileName)) return null;
            foreach (var b in Blocks)
                foreach (var l in b.Legs)
                    foreach (var p in l.Profiles)
                        if (string.Equals(p, profileName, StringComparison.OrdinalIgnoreCase)) return l;
            return null;
        }

        // Every profile the campaign names, with its difficulty and owning block, in spine order. This is the
        // read list: the caller resolves each name to a file and hands back what it found.
        public sealed class ProfileRef
        {
            public readonly string Name, Difficulty, BlockId;
            public readonly bool IsLegacy;
            public ProfileRef(string name, string difficulty, string blockId, bool isLegacy)
            { Name = name; Difficulty = difficulty; BlockId = blockId; IsLegacy = isLegacy; }
        }

        public static List<ProfileRef> AllProfiles(bool includeLegacy)
        {
            var list = new List<ProfileRef>();
            foreach (var b in Blocks)
                foreach (var l in b.Legs)
                {
                    foreach (var p in l.Profiles) list.Add(new ProfileRef(p, l.Difficulty, b.Id, false));
                    if (includeLegacy)
                        foreach (var p in l.Legacy) list.Add(new ProfileRef(p, l.Difficulty, b.Id, true));
                }
            return list;
        }

        // Completion cap for a code (SystemCatalog is the headless fallback for the game's maxCompletions;
        // BaseRebirth guards on the LIVE value, so this is only used to spot entries that are obviously
        // over-cap in a preset). 0 = unknown code.
        public static int CapOf(string code)
        {
            if (string.IsNullOrEmpty(code)) return 0;
            foreach (var ci in SystemCatalog.Challenges)
                if (string.Equals(ci.Code, code, StringComparison.OrdinalIgnoreCase)) return ci.Cap;
            return 0;
        }

        // Mirrors BaseRebirth.ParseChallenges exactly: uppercase, require a '-', code = text before the first
        // '-', ordinal = int.TryParse of the text after it. Anything that fails here is DROPPED by the runtime
        // too, silently — which is why the health report surfaces it.
        public static bool TryParseOrdinal(string raw, out string code, out int index)
        {
            code = null; index = 0;
            if (string.IsNullOrEmpty(raw)) return false;
            var s = raw.Trim().ToUpperInvariant();
            int dash = s.IndexOf('-');
            if (dash <= 0 || dash == s.Length - 1) return false;
            code = s.Substring(0, dash);
            return int.TryParse(s.Substring(dash + 1), out index);
        }

        // What one leg REQUIRES, per code: the highest chain ordinal in its Ordinals list, advisor-
        // owned codes excluded (they accrue opportunistically and gate nothing). This is the table's
        // own data reduced, so a consumer asking "is this leg finished on the live counters?" —
        // ChallengeOverlay's CBlock-3 hack-phase gate via HackPhase.ChainSatisfied — reads the same
        // rows Status() reads instead of hardcoding a second copy of the guide's list. Optional
        // ordinals (TC-2, "strongly recommended but optional") are excluded by construction: they
        // live in Leg.Optional, not Leg.Ordinals. Empty result = no such block/leg, or nothing
        // parseable — the caller must treat that as unverifiable, not as satisfied.
        public static Dictionary<string, int> LegRequirements(string blockId, string difficulty)
        {
            var req = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in Blocks)
            {
                if (!string.Equals(b.Id, blockId, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var l in b.Legs)
                {
                    if (!string.Equals(l.Difficulty, difficulty, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var raw in l.Ordinals)
                    {
                        if (!TryParseOrdinal(raw, out var code, out var idx)) continue;
                        if (IsAdvisorOwned(code)) continue;
                        if (!req.TryGetValue(code, out var cur) || idx > cur) req[code] = idx;
                    }
                }
            }
            return req;
        }

        // ---------------------------------------------------------------- health inputs

        // What the caller found on disk for one campaign profile.
        public sealed class Supply
        {
            public string Profile;       // bare name as it appears in the table
            public string Difficulty;    // from the table, NOT from the file (the file never says)
            public string BlockId;
            public bool IsLegacy;
            public bool Found;           // a LOADABLE file resolved (flat dir only — see NestedOnly)
            public string NestedIn;      // difficulty folder holding an unloadable copy, when Found is false
            public string Malformed;     // null / MalformedParse / MalformedNested
            public int HiddenCount;      // ordinals visible in the malformed location, for the report
            public List<string> Ordinals = new List<string>();   // raw strings, exactly as read
        }

        // Classify one profile's raw JSON into a Supply. Pure over TEXT (the caller owns the file IO), so the
        // malformed-vs-empty distinction is unit-testable without a profiles directory.
        //
        // MALFORMED IS NOT EMPTY. The two legacy files nest `Challenges` INSIDE `Breakpoints.Rebirth`. Rebirth
        // is a modeled key, so ProfileModel routes it to LoadRebirth, which iterates ArrayChildren — empty for
        // an object — and the nested array is simply never seen. The file then loads with ZERO challenges and
        // nothing anywhere reports a problem. "No challenges" and "twenty challenges in the wrong place" are
        // different statements and only one of them tells the user what to do, so we detect the second: zero
        // modeled challenges PLUS a non-array Rebirth carrying a Challenges array is the signature.
        public static Supply ReadSupply(string profile, string difficulty, string blockId, bool isLegacy, string json)
        {
            var sup = new Supply
            {
                Profile = profile,
                Difficulty = difficulty,
                BlockId = blockId,
                IsLegacy = isLegacy,
                Found = json != null,
            };
            if (json == null) return sup;

            try
            {
                var pm = ProfileModel.Load(json);
                if (pm.Challenges != null) sup.Ordinals.AddRange(pm.Challenges);
            }
            catch { sup.Malformed = MalformedParse; }

            if (sup.Malformed == null && sup.Ordinals.Count == 0)
            {
                try
                {
                    var rb = SimpleJSON.JSON.Parse(json)["Breakpoints"]["Rebirth"];
                    if (rb != null && rb.IsObject)
                    {
                        var nested = rb["Challenges"];
                        if (nested != null && nested.IsArray)
                        {
                            sup.Malformed = MalformedNested;
                            sup.HiddenCount = nested.AsArray.Count;
                        }
                    }
                }
                catch { }
            }
            return sup;
        }

        // ---------------------------------------------------------------- health outputs

        public sealed class ChainBreak
        {
            public string Difficulty;
            public string Code;
            public string Kind;            // BreakStranded / BreakAbsent
            public int Missing;            // the first ordinal nothing supplies
            public int Reach;              // highest ordinal that can actually fire from a fresh counter
            public int StrandedCount;      // entries that exist in a profile and can never fire
            public int StrandedFrom, StrandedTo;
            public List<string> Profiles = new List<string>();   // profiles holding the stranded entries
            public string Text;
        }

        // One per (difficulty, code) — NOT one per code. The Normal and Evil LSC runs open at 3 and are inert;
        // the Sadistic one opens at 1 and is not. Aggregating them would hide exactly the difference that
        // makes this reportable.
        public sealed class AdvisorNote
        {
            public string Difficulty;
            public string Code;
            public string Owner;           // the advisor that runs it
            public int Entries;            // profile entries for this code on this difficulty
            public bool EntriesReachable;  // could those entries ever fire as chained? (they ship 3..20)
            public List<string> Profiles = new List<string>();
            public string Text;
        }

        public sealed class HealthReport
        {
            public List<ChainBreak> Breaks = new List<ChainBreak>();
            public List<AdvisorNote> Advisor = new List<AdvisorNote>();
            public List<string> Malformed = new List<string>();
            public List<string> Dropped = new List<string>();     // entries the runtime parser would discard
            public List<string> Duplicates = new List<string>();  // same ordinal supplied twice on a difficulty
            public List<string> MissingFiles = new List<string>();
        }

        // ---------------------------------------------------------------- the derivation
        //
        // THE FIRING CONTRACT (BaseRebirth.RCTarget.ChallengeValid, verified in code):
        //
        //     index <= maxCompletions && index == curCompletions() + 1
        //
        // and selection is _challenges.FirstOrDefault(x => x.ChallengeValid()). Three consequences drive
        // everything below:
        //   * an ordinal at or below the counter is SPENT — a plain predicate miss, inert;
        //   * an ordinal above counter+1 is SKIPPED — also a plain predicate miss;
        //   * so a gap does not halt the block, it silently amputates ONE challenge code. Every other code in
        //     the same profile keeps firing.
        //
        // Walking from a fresh counter (0) on a difficulty, ordinal n fires only if 1..n are all supplied
        // somewhere in the campaign. `reach` is the largest such n. Anything supplied above `reach` can never
        // fire — it is STRANDED — and `reach + 1` is the ordinal that would unstick it.
        //
        // NOTHING HERE IS HARDCODED. The breaks fall out of the union of what the profiles actually supply, so
        // fixing a preset makes the corresponding break disappear with no code change — which is the whole
        // point of deriving it, and is exactly what happened: the four documented breaks (Normal BASIC/24HR
        // from the unwired Micro-CBlock, Normal NORB from the empty C-Miniblock2.3-NoRB stub, Evil 24HR from
        // FinalEvil24hCBlock opening at 9) all closed by editing profiles and one Leg, with this function
        // untouched. It now derives an empty break list from the shipped set; reintroduce a gap and it says so.
        public static HealthReport Health(IEnumerable<Supply> supplies)
        {
            var report = new HealthReport();
            if (supplies == null) return report;
            var all = new List<Supply>(supplies);   // enumerated twice below; don't re-walk a lazy sequence

            // (difficulty, code) -> ordinal -> profiles supplying it
            var supplied = new Dictionary<string, SortedDictionary<int, List<string>>>(StringComparer.OrdinalIgnoreCase);
            var advisorEntries = new Dictionary<string, SortedDictionary<int, List<string>>>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in all)
            {
                if (s == null) continue;
                if (s.IsLegacy)
                {
                    // Legacy files are REPORTED, never counted. They nest Challenges inside Breakpoints.Rebirth,
                    // so the current schema loads them with zero challenges and nothing in them can fire.
                    if (s.Found && !string.IsNullOrEmpty(s.Malformed)) AddMalformed(report, s);
                    continue;
                }
                if (!s.Found)
                {
                    // A name the presets deliberately do not install is still "missing" — the block really
                    // does lose those ordinals — but it is not something the player did wrong, so it carries
                    // its reason instead of reading as a file they forgot to copy in.
                    string why;
                    if (!string.IsNullOrEmpty(s.NestedIn))
                        // A copy exists but the runtime cannot load it: both the profile list
                        // (Directory.GetFiles, no AllDirectories) and the loader (Path.Combine of the bare
                        // name) read the FLAT directory only. Reporting it as supplied was worse than
                        // useless -- it invented duplicates against the flat file that really does supply
                        // those ordinals, and it would have hidden a real break for any block whose only
                        // copy is nested. Name the folder so the fix is one drag.
                        report.MissingFiles.Add(s.Profile + " — a copy exists in " + s.NestedIn + "\\ but the "
                            + "advisor only loads profiles from the top level; move it up one folder to use it");
                    else
                        report.MissingFiles.Add(NotAutoInstalled.TryGetValue(s.Profile, out why)
                            ? s.Profile + " — not auto-installed by design: " + why
                            : s.Profile);
                    continue;
                }
                if (!string.IsNullOrEmpty(s.Malformed)) AddMalformed(report, s);
                if (s.Ordinals == null) continue;

                foreach (var raw in s.Ordinals)
                {
                    string code; int idx;
                    if (!TryParseOrdinal(raw, out code, out idx))
                    { report.Dropped.Add(s.Profile + ": \"" + raw + "\" is not CODE-n and is discarded at parse"); continue; }

                    int cap = CapOf(code);
                    if (cap == 0)
                    { report.Dropped.Add(s.Profile + ": " + raw + " — unknown challenge code, discarded at parse"); continue; }
                    if (idx < 1 || idx > cap)
                    { report.Dropped.Add(s.Profile + ": " + raw + " — ordinal outside 1.." + cap + ", can never be valid"); continue; }

                    var bucket = IsAdvisorOwned(code) ? advisorEntries : supplied;
                    Add(bucket, s.Difficulty, code, idx, s.Profile);
                }
            }

            // Duplicates: the same ordinal supplied by two profiles on one difficulty. Self-healing at runtime
            // (whichever fires first spends it), but worth naming — Evil BASIC-1 is deliberately double-shipped.
            foreach (var kv in supplied)
                foreach (var o in kv.Value)
                    if (o.Value.Count > 1)
                        report.Duplicates.Add(kv.Key.Replace("|", " ") + "-" + o.Key + " supplied by " + string.Join(", ", o.Value.ToArray()));

            // Stranded: a gap with entries above it.
            foreach (var kv in supplied)
            {
                var parts = kv.Key.Split('|');
                string difficulty = parts[0], code = parts[1];
                var ordinals = kv.Value;

                int reach = 0;
                while (ordinals.ContainsKey(reach + 1)) reach++;

                var stranded = new List<int>();
                var holders = new List<string>();
                foreach (var o in ordinals)
                    if (o.Key > reach)
                    {
                        stranded.Add(o.Key);
                        foreach (var p in o.Value) if (!holders.Contains(p)) holders.Add(p);
                    }
                if (stranded.Count == 0) continue;

                var br = new ChainBreak
                {
                    Difficulty = difficulty,
                    Code = code,
                    Kind = BreakStranded,
                    Missing = reach + 1,
                    Reach = reach,
                    StrandedCount = stranded.Count,
                    StrandedFrom = stranded[0],
                    StrandedTo = stranded[stranded.Count - 1],
                };
                br.Profiles.AddRange(holders);
                br.Text = difficulty + " " + code + "-" + br.Missing + " is supplied by nothing, so "
                        + code + "-" + br.StrandedFrom + (br.StrandedCount > 1 ? ".." + br.StrandedTo : "")
                        + " can never fire (" + br.StrandedCount + " entr" + (br.StrandedCount == 1 ? "y" : "ies")
                        + " in " + string.Join(", ", holders.ToArray()) + ")";
                report.Breaks.Add(br);
            }

            // Absent: the guide prescribes ordinals for a (difficulty, code) that no profile supplies at all.
            // No entries are stranded because there are no entries — but the completions are still lost, and
            // the downstream stranding (Normal 24HR) is the visible symptom.
            //
            // ONLY FOR LEGS WE COULD ACTUALLY READ. A leg whose files are missing from disk tells us nothing
            // about what it would have supplied, and claiming otherwise would turn a fresh install — where the
            // sample profiles have not been copied in yet — into a wall of thirty "absent" breaks that say
            // nothing except "you have no profiles". A leg with no profiles at all trivially qualifies (there
            // is nothing to fail to read), which is the case this check was written for: the Micro-CBlock used
            // to name none, so its BASIC-1..5 and 24HR-1 were reported absent against a leg that shipped
            // nothing. It names C-Microblock1-Basics now, so that path is currently unexercised by real data.
            var readable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in all) if (s != null && s.Found && !s.IsLegacy) readable.Add(s.Profile);

            foreach (var b in Blocks)
                foreach (var l in b.Legs)
                {
                    bool wholeLegRead = true;
                    foreach (var p in l.Profiles) if (!readable.Contains(p)) { wholeLegRead = false; break; }
                    if (!wholeLegRead) continue;

                    var byCode = new SortedDictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var raw in l.Ordinals)
                    {
                        string code; int idx;
                        if (!TryParseOrdinal(raw, out code, out idx)) continue;
                        if (IsAdvisorOwned(code)) continue;
                        List<int> got;
                        if (!byCode.TryGetValue(code, out got)) { got = new List<int>(); byCode[code] = got; }
                        got.Add(idx);
                    }
                    foreach (var kv in byCode)
                    {
                        string key = Key(l.Difficulty, kv.Key);
                        if (supplied.ContainsKey(key)) continue;              // partially supplied -> stranded covers it
                        if (AlreadyAbsent(report, l.Difficulty, kv.Key)) continue;
                        kv.Value.Sort();
                        var br = new ChainBreak
                        {
                            Difficulty = l.Difficulty,
                            Code = kv.Key,
                            Kind = BreakAbsent,
                            Missing = kv.Value[0],
                            Reach = 0,
                            StrandedCount = 0,
                            StrandedFrom = kv.Value[0],
                            StrandedTo = kv.Value[kv.Value.Count - 1],
                        };
                        br.Text = l.Difficulty + " " + kv.Key + "-" + br.StrandedFrom
                                + (kv.Value.Count > 1 ? ".." + br.StrandedTo : "")
                                + " is prescribed by " + b.Name + " and supplied by no profile"
                                + (b.Shipped ? "" : " (no profile ships for that block)");
                        report.Breaks.Add(br);
                    }
                }

            // Advisor-owned codes get their own category. A gap here is NOT a chain break: the advisor runs
            // these itself and the profile entries are a redundant second mechanism.
            foreach (var kv in advisorEntries)
            {
                var parts = kv.Key.Split('|');
                var n = new AdvisorNote
                {
                    Difficulty = parts[0],
                    Code = parts[1],
                    Owner = OwnerOf(parts[1]),
                    EntriesReachable = true,
                };
                int reach = 0;
                while (kv.Value.ContainsKey(reach + 1)) reach++;
                foreach (var o in kv.Value)
                {
                    n.Entries += o.Value.Count;
                    if (o.Key > reach) n.EntriesReachable = false;
                    foreach (var p in o.Value) if (!n.Profiles.Contains(p)) n.Profiles.Add(p);
                }
                // Per-row text carries ONLY what differs by difficulty. The shared explanation of why this
                // code is advisor-owned is emitted once as `health.advisorNote` — repeating it on every row
                // cost ~1.2 KB of identical prose on a line published once a second.
                n.Text = n.Difficulty + ": " + n.Entries + " " + n.Code + " entr"
                       + (n.Entries == 1 ? "y" : "ies") + " in " + string.Join(", ", n.Profiles.ToArray())
                       + (n.EntriesReachable
                            ? ", chained from 1 — they would fire if reached."
                            : " open above 1 and can never fire, so " + n.Owner + " is the only path.");
                report.Advisor.Add(n);
            }
            report.Advisor.Sort((a, b2) =>
            {
                int d = DifficultyRank(a.Difficulty).CompareTo(DifficultyRank(b2.Difficulty));
                return d != 0 ? d : string.Compare(a.Code, b2.Code, StringComparison.OrdinalIgnoreCase);
            });

            report.Breaks.Sort((a, b2) =>
            {
                int d = DifficultyRank(a.Difficulty).CompareTo(DifficultyRank(b2.Difficulty));
                if (d != 0) return d;
                return string.Compare(a.Code, b2.Code, StringComparison.OrdinalIgnoreCase);
            });
            return report;
        }

        private static string Key(string difficulty, string code) => difficulty + "|" + code;

        private static void Add(Dictionary<string, SortedDictionary<int, List<string>>> map,
                                string difficulty, string code, int idx, string profile)
        {
            string key = Key(difficulty, code);
            SortedDictionary<int, List<string>> byOrdinal;
            if (!map.TryGetValue(key, out byOrdinal)) { byOrdinal = new SortedDictionary<int, List<string>>(); map[key] = byOrdinal; }
            List<string> holders;
            if (!byOrdinal.TryGetValue(idx, out holders)) { holders = new List<string>(); byOrdinal[idx] = holders; }
            if (!holders.Contains(profile)) holders.Add(profile);
        }

        private static bool AlreadyAbsent(HealthReport r, string difficulty, string code)
        {
            foreach (var b in r.Breaks)
                if (b.Kind == BreakAbsent
                    && string.Equals(b.Difficulty, difficulty, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(b.Code, code, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void AddMalformed(HealthReport r, Supply s)
        {
            r.Malformed.Add(s.Malformed == MalformedNested
                ? s.Profile + ": Challenges are nested inside Breakpoints.Rebirth, so the current schema loads "
                  + s.HiddenCount + " challenge(s) as ZERO — malformed, not empty"
                : s.Profile + ": the file could not be parsed, so it supplies no challenges");
        }

        // ---------------------------------------------------------------- per-block status

        public sealed class LegStatus
        {
            public string Difficulty;
            public int Done, Required, Opportunistic, Stranded;
            public bool Counted;          // this leg's difficulty is the one being played, so Done is real
        }

        public sealed class BlockStatus
        {
            public Block Block;
            public string State;
            public int Done;              // supplied chain ordinals already spent on their own difficulty
            public int Required;          // supplied chain ordinals (excludes advisor-owned codes)
            public int Opportunistic;     // supplied advisor-owned ordinals
            public int Stranded;          // supplied chain ordinals that can never fire
            public int FilesMissing;      // profiles the block names that are not on disk
            public bool Counted;          // false => Done is not a verifiable number for this block right now
            public List<LegStatus> Legs = new List<LegStatus>();
        }

        // Join the table against live state.
        //
        // COMPLETIONS ARE PER-DIFFICULTY AND THE GAME ONLY EXPOSES THE CURRENT ONE. That is not a limitation to
        // paper over: a block on a difficulty the player is not on cannot have its progress verified at all, so
        // it reports Counted=false and the UI must not draw a progress number for it. `liveCompletions` is
        // code -> currentCompletions() for `liveDifficulty` only; pass null when the game is not attached.
        public static List<BlockStatus> Status(IEnumerable<Supply> supplies, string liveDifficulty,
                                               IDictionary<string, int> liveCompletions, string activeProfile)
        {
            // profile -> parsed ordinals
            var byProfile = new Dictionary<string, List<KeyValuePair<string, int>>>(StringComparer.OrdinalIgnoreCase);
            var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (supplies != null)
                foreach (var s in supplies)
                {
                    if (s == null || s.IsLegacy) continue;
                    if (!s.Found) { missing.Add(s.Profile); continue; }
                    if (s.Ordinals == null) continue;
                    List<KeyValuePair<string, int>> list;
                    if (!byProfile.TryGetValue(s.Profile, out list)) { list = new List<KeyValuePair<string, int>>(); byProfile[s.Profile] = list; }
                    foreach (var raw in s.Ordinals)
                    {
                        string code; int idx;
                        if (!TryParseOrdinal(raw, out code, out idx)) continue;
                        int cap = CapOf(code);
                        if (cap == 0 || idx < 1 || idx > cap) continue;
                        list.Add(new KeyValuePair<string, int>(code, idx));
                    }
                }

            // Reach per (difficulty, code) so a block can report how many of its own entries are stranded.
            var reach = ReachMap(byProfile);

            int liveRank = DifficultyRank(liveDifficulty);
            var activeBlock = BlockOfProfile(activeProfile);

            var result = new List<BlockStatus>();
            foreach (var b in Blocks)
            {
                var st = new BlockStatus { Block = b, Counted = true };
                bool anyLegAbove = false, anyLegHere = false;
                bool countableSatisfied = true;      // every leg we CAN verify is finished
                bool unverifiedLegsAllBehind = true; // every leg we cannot verify sits on an earlier difficulty

                foreach (var l in b.Legs)
                {
                    int rank = DifficultyRank(l.Difficulty);
                    if (rank > liveRank) anyLegAbove = true;
                    if (rank == liveRank && liveRank >= 0) anyLegHere = true;

                    bool countable = liveRank >= 0 && rank == liveRank && liveCompletions != null;
                    if (!countable)
                    {
                        st.Counted = false;
                        if (!(liveRank >= 0 && rank >= 0 && rank < liveRank)) unverifiedLegsAllBehind = false;
                    }

                    var ls = new LegStatus { Difficulty = l.Difficulty, Counted = countable };
                    foreach (var p in l.Profiles)
                    {
                        if (missing.Contains(p)) st.FilesMissing++;
                        List<KeyValuePair<string, int>> ords;
                        if (!byProfile.TryGetValue(p, out ords)) continue;
                        foreach (var o in ords)
                        {
                            if (IsAdvisorOwned(o.Key)) { ls.Opportunistic++; continue; }
                            ls.Required++;
                            int r;
                            if (reach.TryGetValue(Key(l.Difficulty, o.Key), out r) && o.Value > r) ls.Stranded++;
                            int cur;
                            if (countable && liveCompletions.TryGetValue(o.Key, out cur) && o.Value <= cur) ls.Done++;
                        }
                    }
                    if (countable && ls.Required > 0 && ls.Done < ls.Required) countableSatisfied = false;
                    st.Legs.Add(ls);
                    st.Done += ls.Done; st.Required += ls.Required;
                    st.Opportunistic += ls.Opportunistic; st.Stranded += ls.Stranded;
                }

                if (b.Legs.Length == 0) st.Counted = false;

                // STATE. Deliberately conservative — the only things we can read directly are the active
                // profile name and the CURRENT difficulty's completion counters. Everything else is inference,
                // and where inference runs out the block reports Counted=false rather than inventing a number.
                //
                // A block with no profile is never a candidate for `next`: nothing would run it, so parking the
                // campaign's "do this next" marker on it would be a dead end. It reports by difficulty
                // position only, and the real message is carried by shipped=false plus the `absent` chain
                // break its ordinals produce. The Micro-CBlock was the last block in this state; it names
                // C-Microblock1-Basics now, so nothing shipped takes this branch today.
                if (activeBlock != null && ReferenceEquals(activeBlock, b)) st.State = StateActive;
                else if (!b.Shipped)
                {
                    st.State = (liveRank >= 0 && !anyLegHere && !anyLegAbove) ? StateComplete : StateUpcoming;
                    st.Counted = false;
                }
                else if (st.Counted && st.Required > 0 && st.Done >= st.Required) st.State = StateComplete;
                else if (liveRank >= 0 && countableSatisfied && unverifiedLegsAllBehind && !anyLegAbove)
                {
                    // Every leg we can check is done and the rest are on difficulties already left behind.
                    st.State = StateComplete;
                    st.Counted = false;
                }
                else if (anyLegAbove && !anyLegHere) st.State = StateUpcoming;
                else st.State = null;   // candidate — resolved below
                result.Add(st);
            }

            // The earliest unresolved candidate is `next`; everything after it is `upcoming`.
            bool nextTaken = false;
            foreach (var st in result)
            {
                if (st.State != null) continue;
                st.State = nextTaken ? StateUpcoming : StateNext;
                nextTaken = true;
            }
            return result;
        }

        // ---------------------------------------------------------------- the `campaign` snapshot node
        //
        // The exact wire shape the companion consumes. It lives HERE rather than in UiBridge for the same
        // reason TitanTables owns the AK vocabulary: UiBridge cannot be linked into the test assembly (Unity +
        // Assembly-CSharp), so anything written there is untestable. UiBridge keeps only the live reads.
        //
        // `liveCompletions` is code -> currentCompletions() for `liveDifficulty` ONLY — the game exposes no
        // other difficulty's counters. Pass null when the game is not attached; the node then publishes
        // known:false and every block reports counted:false rather than inventing progress.
        public static SimpleJSON.JSONObject ToJson(IEnumerable<Supply> supplies, string liveDifficulty,
                                                   IDictionary<string, int> liveCompletions,
                                                   string activeProfile, bool autoProfile, bool advisorChallenges)
        {
            var node = new SimpleJSON.JSONObject();
            node["known"] = liveCompletions != null && DifficultyRank(liveDifficulty) >= 0;
            node["difficulty"] = liveDifficulty ?? "";

            // --- blocks, in spine order ---
            var blocks = new SimpleJSON.JSONArray();
            foreach (var st in Status(supplies, liveDifficulty, liveCompletions, autoProfile ? null : activeProfile))
            {
                var b = st.Block;
                var o = new SimpleJSON.JSONObject();
                o["id"] = b.Id;
                o["name"] = b.Name;
                o["chapter"] = b.Chapter;
                o["order"] = b.Order;
                if (b.GuideNumber > 0) o["guideNo"] = b.GuideNumber;   // absent = not one of the guide's nine
                o["kind"] = b.Kind;
                o["difficulty"] = b.DifficultyLabel;      // "Evil + Normal" for the block that spans two
                o["state"] = st.State;
                o["done"] = st.Done;
                o["required"] = st.Required;
                o["counted"] = st.Counted;                // false => `done` is not a verifiable number
                o["shipped"] = b.Shipped;
                if (st.Opportunistic > 0) o["opportunistic"] = st.Opportunistic;   // advisor-owned entries
                if (st.Stranded > 0) o["stranded"] = st.Stranded;
                // Files the block names that are not on disk. Without this a block whose profiles were never
                // copied in reads as a bare 0/0 and looks like a block with nothing to do.
                if (st.FilesMissing > 0) o["filesMissing"] = st.FilesMissing;
                o["entry"] = b.EntryGate ?? "";
                o["handsBackTo"] = b.HandsBackTo ?? "";
                if (b.CadenceMinutes > 0) o["cadenceMin"] = b.CadenceMinutes;      // absent = guide doesn't say
                if (!string.IsNullOrEmpty(b.Note)) o["note"] = b.Note;

                var profs = new SimpleJSON.JSONArray();
                foreach (var l in b.Legs) foreach (var p in l.Profiles) profs.Add(p);
                o["profiles"] = profs;

                // Per-leg split, emitted ONLY for the block that spans difficulties. For a single-leg block it
                // would just repeat the block's own numbers.
                if (b.Legs.Length > 1)
                {
                    var legs = new SimpleJSON.JSONArray();
                    foreach (var ls in st.Legs)
                    {
                        var lo = new SimpleJSON.JSONObject();
                        lo["difficulty"] = ls.Difficulty;
                        lo["done"] = ls.Done;
                        lo["required"] = ls.Required;
                        lo["counted"] = ls.Counted;
                        legs.Add(lo);
                    }
                    o["legs"] = legs;
                }
                blocks.Add(o);
            }
            node["blocks"] = blocks;

            // --- which block the active profile is ---
            var act = new SimpleJSON.JSONObject();
            act["profile"] = activeProfile ?? "";
            act["autoProfile"] = autoProfile;
            if (autoProfile)
            {
                act["inCampaign"] = false;
                act["note"] = "AUTO (advisor) — the campaign does not drive profile selection";
            }
            else
            {
                var blk = BlockOfProfile(activeProfile);
                var leg = LegOfProfile(activeProfile);
                act["inCampaign"] = blk != null;
                if (blk != null)
                {
                    act["block"] = blk.Id;
                    act["blockName"] = blk.Name;
                    act["profileDifficulty"] = leg != null ? leg.Difficulty : "";
                    // Completions are difficulty-local, so running an Evil rotation on Normal does not fail
                    // loudly — it fires ordinals against the WRONG counter. Nothing else in the advisor can
                    // catch this, because nothing else knows which difficulty a profile was written for.
                    if (leg != null && DifficultyRank(liveDifficulty) >= 0
                        && !string.Equals(leg.Difficulty, liveDifficulty, StringComparison.OrdinalIgnoreCase))
                    {
                        act["difficultyMismatch"] = true;
                        act["note"] = activeProfile + " is a " + leg.Difficulty + " rotation but you are playing "
                                    + liveDifficulty + " — its ordinals will be matched against the "
                                    + liveDifficulty + " counters";
                    }
                }
                else
                {
                    // A profile the user wrote is simply not part of the campaign. Say so; do not guess a
                    // nearest block.
                    act["note"] = string.IsNullOrEmpty(activeProfile)
                        ? "no profile selected"
                        : "\"" + activeProfile + "\" is not a campaign profile";
                }
            }
            node["active"] = act;

            // --- chain health ---
            var health = new SimpleJSON.JSONObject();
            var rep = Health(supplies);

            var breaks = new SimpleJSON.JSONArray();
            foreach (var br in rep.Breaks)
            {
                var o = new SimpleJSON.JSONObject();
                o["difficulty"] = br.Difficulty;
                o["code"] = br.Code;
                o["kind"] = br.Kind;                 // "stranded" | "absent"
                o["missing"] = br.Missing;           // the ordinal that would unstick the line
                o["reach"] = br.Reach;               // highest ordinal that can fire from a fresh counter
                o["stranded"] = br.StrandedCount;
                o["from"] = br.StrandedFrom;
                o["to"] = br.StrandedTo;
                var ps = new SimpleJSON.JSONArray();
                foreach (var p in br.Profiles) ps.Add(p);
                o["profiles"] = ps;
                o["text"] = br.Text ?? "";
                breaks.Add(o);
            }
            health["breaks"] = breaks;

            // Advisor-owned challenges are NOT chain breaks — see AdvisorOwnedCodes. Publishing LSC as
            // "stranded" would be a false positive; the switch that runs it IS reportable, and with
            // AdvisorChallenges off neither the entry path nor the in-challenge sword focus is running.
            var advisor = new SimpleJSON.JSONArray();
            foreach (var n in rep.Advisor)
            {
                var o = new SimpleJSON.JSONObject();
                o["difficulty"] = n.Difficulty;
                o["code"] = n.Code;
                o["owner"] = n.Owner ?? "";
                o["entries"] = n.Entries;
                o["entriesReachable"] = n.EntriesReachable;
                o["enabled"] = advisorChallenges;
                o["max"] = CapOf(n.Code);
                var ps = new SimpleJSON.JSONArray();
                foreach (var p in n.Profiles) ps.Add(p);
                o["profiles"] = ps;
                // `cur` ONLY on the difficulty being played — publishing the Evil counter next to the
                // Sadistic row would read as progress on a run that is not being played.
                if (liveCompletions != null && string.Equals(n.Difficulty, liveDifficulty, StringComparison.OrdinalIgnoreCase))
                {
                    int cur;
                    if (liveCompletions.TryGetValue(n.Code, out cur)) o["cur"] = cur;
                }
                o["text"] = n.Text ?? "";
                advisor.Add(o);
            }
            health["advisor"] = advisor;
            if (advisor.Count > 0)
            {
                health["advisorNote"] =
                    "Laser Sword is run by LscAdvisor, not by a profile rotation. It is the only challenge "
                    + "that does not reset the number, so the advisor rides an ALREADY-SCHEDULED rebirth into "
                    + "it whenever the sword target fits inside the augs hour it would spend anyway — it never "
                    + "pulls a rebirth forward, and a profile-scheduled challenge still takes precedence. The "
                    + "guide names LSC in no block's list for the same reason. These are therefore NOT chain "
                    + "breaks."
                    + (advisorChallenges
                        ? ""
                        : " Settings.AdvisorChallenges is OFF, so neither the entry path nor the in-challenge "
                          + "sword focus is running — nothing is completing Laser Sword right now.");
            }

            AddStrings(health, "malformed", rep.Malformed);
            AddStrings(health, "dropped", rep.Dropped);
            AddStrings(health, "duplicates", rep.Duplicates);
            AddStrings(health, "missingFiles", rep.MissingFiles);
            node["health"] = health;
            return node;
        }

        // Absent means "nothing to report" — an empty array would make the UI draw an empty section header.
        private static void AddStrings(SimpleJSON.JSONObject host, string key, List<string> values)
        {
            if (values == null || values.Count == 0) return;
            var arr = new SimpleJSON.JSONArray();
            foreach (var v in values) arr.Add(v);
            host[key] = arr;
        }

        private static Dictionary<string, int> ReachMap(Dictionary<string, List<KeyValuePair<string, int>>> byProfile)
        {
            var supplied = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in Blocks)
                foreach (var l in b.Legs)
                    foreach (var p in l.Profiles)
                    {
                        List<KeyValuePair<string, int>> ords;
                        if (!byProfile.TryGetValue(p, out ords)) continue;
                        foreach (var o in ords)
                        {
                            string key = Key(l.Difficulty, o.Key);
                            HashSet<int> set;
                            if (!supplied.TryGetValue(key, out set)) { set = new HashSet<int>(); supplied[key] = set; }
                            set.Add(o.Value);
                        }
                    }
            var reach = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in supplied)
            {
                int r = 0;
                while (kv.Value.Contains(r + 1)) r++;
                reach[kv.Key] = r;
            }
            return reach;
        }
    }
}
