namespace NGUAdvisor.Managers
{
    // WHAT ACTUALLY ROUTED, AND WHAT IT DISPLACED.
    //
    // audit/40 §0: there are TWO chains, not one. Layer 1 (AdvisorApply.ApplyZones + the UI + the
    // profile merge) WRITES Settings.SnipeZone. Layer 2 (Main.SnipeZone, every frame) RESOLVES the
    // zone actually adventured, and Settings.SnipeZone is one input among many to it — consulted
    // after SEVEN earlier returns. Winning the write does not mean being adventured.
    //
    // audit/40 §3 items 1, 2, 4, 5 and 6 are one defect wearing five hats: the advisor logs
    // "Advisor: farm zone -> X" at the moment of WRITING, layer 2 then routes somewhere else, and
    // nothing distinguishes an intent from a fact. Item 3 was written as a PREDICTION of what that
    // costs and then happened exactly — SnipeZone 20, "rare farm -> Chocolate World" logged every
    // pass, and the character in the ITOPOD for a whole run (audit/41 §2.1). That is the amendment
    // 25 §4 shape: a correct decision and a broken one read identically.
    //
    // ⚠ THIS SURFACES; IT DOES NOT RE-ORDER. Every priority in audit/40 §2 is untouched, and several
    // are deliberate — AdvisorApply.cs: "CBlock and pit-run gold logic own zones and must win".
    // Changing who wins is a design decision and is NOT made here. The only claim made by this file
    // is that a silent override is worse than a loud one.
    //
    // WHY IT IS A LATCH AND NOT A LOG CALL. Main.SnipeZone runs from LateUpdate, i.e. every frame.
    // ShouldSurface compares three ints and allocates nothing; the message is built only on a
    // transition, so the steady state costs three comparisons per frame and zero lines.
    public static class ZoneRouting
    {
        // One row per audit/40 §2 gate that can move the zone away from what the chain intended,
        // plus the four "wanted a zone, had none" fall-throughs of §3 item 6.
        public enum Cause
        {
            // R14 — the chain's own answer was routed; nothing displaced it.
            None = 0,

            // OWNERS. Each returns from the resolver before Settings.SnipeZone is consulted at all
            // (audit/40 §2 rows R3-R8), or rewrites the resolved zone afterwards (R12, R13).
            PitRun,             // R3  Main.cs — money-pit run
            TimeMachineEmpty,   // R4  Main.cs — realBaseGold == 0, zone 0 refills it
            GoldSnipe,          // R5  Main.cs — the furthest clearable zone, in the gold loadout
            Titan,              // R6  Main.cs — a spawning titan with no autokill
            Quest,              // R7  Main.cs — QuestManager.IsQuesting()
            CBlockGold,         // R8  Main.cs — GoldCBlockMode with the TM unavailable
            EvilClimb,          // R12 Main.cs — the climb needs bosses and gold
            GoldStarvedAugs,    // R13 Main.cs — the ITOPOD drops no gold and augments have stalled

            // A TOGGLE, not a gate — but it displaces exactly as an owner does, and for as long as it
            // is on. audit/40 §3 item 3's SURVIVING HALF. R10 resolves gear hunt > advisor drop farm >
            // Settings.AdventureTargetITOPOD > Settings.SnipeZone (Main.ResolveIntentZone), so 271f5f8
            // rescued only the drop-farm case; §6.1 recorded that everything else still loses to the
            // toggle — "Target ITOPOD keeps its meaning everywhere else, including for the boost farm,
            // a wider change than the defect justified". WHO WINS IS DELIBERATE AND IS NOT TOUCHED.
            // The silence was not: AdvisorApply.cs:1175-1179 logs "farm zone -> X" for the boost farm
            // and R10 discards it one pass later, which is exactly the shape that cost a full run.
            TargetItopod,       // R10 Main.cs — Settings.AdventureTargetITOPOD

            // A REWRITE, not an owner: the chain's zone is not unlocked, so it is replaced in place.
            UnlockFallback,     // R11 Main.cs

            // HAND-BACKS. audit/40 §3 item 6: each of these is a deliberate "nothing fightable yet,
            // use normal routing" case WITH a comment and WITHOUT a line, so a gate that correctly
            // declined and a gate that never fired are indistinguishable in the log.
            GoldSnipeNoZone,
            CBlockNoZone,
            EvilClimbNoZone,
            GoldStarvedNoZone,
        }

        // An OWNER holds routing for as long as its condition holds, so it is the one kind of cause
        // that has something to say when it lets go again.
        public static bool IsOwner(Cause c)
        {
            switch (c)
            {
                case Cause.PitRun:
                case Cause.TimeMachineEmpty:
                case Cause.GoldSnipe:
                case Cause.Titan:
                case Cause.Quest:
                case Cause.CBlockGold:
                case Cause.EvilClimb:
                case Cause.GoldStarvedAugs:
                case Cause.TargetItopod:
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsHandover(Cause c)
        {
            switch (c)
            {
                case Cause.GoldSnipeNoZone:
                case Cause.CBlockNoZone:
                case Cause.EvilClimbNoZone:
                case Cause.GoldStarvedNoZone:
                    return true;
                default:
                    return false;
            }
        }

        // The subject of the sentence. Kept separate from the sentence so the resume line and the
        // displacement line cannot drift into naming the same gate two different things.
        public static string Owner(Cause c)
        {
            switch (c)
            {
                case Cause.PitRun: return "the money-pit run";
                case Cause.TimeMachineEmpty: return "the empty Time Machine";
                case Cause.GoldSnipe:
                case Cause.GoldSnipeNoZone: return "the gold snipe";
                case Cause.Titan: return "a spawning titan";
                case Cause.Quest: return "a quest";
                case Cause.CBlockGold:
                case Cause.CBlockNoZone: return "CBlock gold";
                case Cause.EvilClimb:
                case Cause.EvilClimbNoZone: return "EVIL CLIMB";
                case Cause.GoldStarvedAugs:
                case Cause.GoldStarvedNoZone: return "the gold-starved augment override";
                // The operator-facing name of the toggle, verbatim from the panel, because the whole
                // point of the line is that they can go and turn it off.
                case Cause.TargetItopod: return "Target ITOPOD";
                default: return null;
            }
        }

        // The line, or null when there is nothing to say. PURE — every input is passed in, including
        // the previous cause, so the whole decision is testable without the game.
        //
        // intended  the zone the chain WOULD have routed (audit/40 R10), or -1 when there is none
        // routed    the zone actually routed, or -1 when the owner drives it itself (the pit run
        //           alternates zone 0 / ITOPOD within one hold, and naming either would make the
        //           line re-emit on every flip)
        //
        // previousSpoke  whether the state being LEFT ever produced a line. A release is only worth
        //                a line when the hold was reported: a quest that happened to agree with the
        //                zone target displaced nothing, and "a quest released adventure routing"
        //                after a silent hold is a line about an event the operator never saw begin.
        //                Minor quests are frequent, so this is the difference between a report and a
        //                stream.
        public static string Describe(Cause previous, bool previousSpoke, Cause now,
                                      int intended, string intendedName,
                                      int routed, string routedName)
        {
            if (now == Cause.None)
            {
                if (!previousSpoke) return null;

                // audit/40 §6.3's rule, applied here: an override that names what resumes afterwards
                // is the difference between a report and a riddle. Hand-backs already said "normal
                // routing continues", so they get no second line.
                //
                // The toggle gets its own sentence at BOTH ends. It never "held" or "released"
                // routing the way a gate does — it won a row of the R10 chain and then stopped
                // winning it, which happens either because the operator switched it off or because a
                // gear hunt or a drop farm took the row above it. Naming the zone that routes now is
                // what tells those two apart without a second line.
                if (previous == Cause.TargetItopod)
                    return routedName == null
                        ? "Target ITOPOD no longer holds routing"
                        : "Target ITOPOD no longer holds routing -> " + routedName;
                if (IsOwner(previous))
                    return routedName == null
                        ? Owner(previous) + " released adventure routing"
                        : Owner(previous) + " released adventure routing -> " + routedName;
                if (previous == Cause.UnlockFallback && routedName != null)
                    return "the zone target is unlocked again -> routing to " + routedName;
                return null;
            }

            if (IsHandover(now))
                return Owner(now) + " has no clearable zone yet -> normal routing continues";

            if (now == Cause.UnlockFallback)
            {
                if (intended < 0 || routedName == null) return null;
                return "zone target " + intendedName + " is locked -> routing to " + routedName + " instead";
            }

            // R12 / R13 rewrite an ITOPOD result rather than pre-empting a farm zone, so they are
            // reported as what they are — an override of the ITOPOD, not a displaced farm target.
            if (now == Cause.EvilClimb || now == Cause.GoldStarvedAugs)
            {
                if (routedName == null) return null;
                return Owner(now) + " overrides ITOPOD routing -> " + routedName;
            }

            // R10's toggle. NOT "owns adventure routing": it never pre-empted the chain, it won a row
            // INSIDE it, so the sentence says what actually happened. The zone it routes is always
            // 1000 and naming it twice reads as two facts, so only the discarded target is named.
            if (now == Cause.TargetItopod)
            {
                if (intended < 0 || intended == routed) return null;
                return "Target ITOPOD is on -> the ITOPOD is being adventured; zone target "
                     + intendedName + " is not, while it stays on";
            }

            // OWNERS R3-R8. Silence is correct when nothing was displaced: an unset target (-1, the
            // SavedSettings sentinel) or a target the owner happens to agree with is not contention.
            if (intended < 0 || intended == routed) return null;

            return routed < 0
                ? Owner(now) + " owns adventure routing; zone target " + intendedName
                    + " is not being adventured while it holds"
                : Owner(now) + " owns adventure routing -> " + routedName + "; zone target "
                    + intendedName + " is not being adventured while it holds";
        }

        // The feed's "why". Every one of these is a reason already recorded in a code comment at the
        // gate; audit/40 §3's finding is that none of them ever reached the operator.
        public static string Reason(Cause previous, Cause now)
        {
            switch (now)
            {
                case Cause.PitRun: return "pit-run gold logic owns zones and must win";
                case Cause.TimeMachineEmpty: return "Time Machine gold is 0 — zone 0 refills it";
                case Cause.GoldSnipe: return "the gold snipe outranks the farm zone";
                case Cause.Titan: return "a titan spawn outranks the farm zone";
                case Cause.Quest: return "a quest outranks the farm zone";
                case Cause.CBlockGold: return "CBlock gold logic owns zones and must win";
                case Cause.UnlockFallback: return "the zone target is not unlocked";
                case Cause.EvilClimb: return "the climb needs bosses and gold; the ITOPOD gives neither";
                case Cause.GoldStarvedAugs: return "ITOPOD enemies drop no gold and the augments have stalled";
                // audit/40 §6.1: only a gear hunt or an advisor drop farm outranks the toggle. The
                // boost farm and a hand-set dropdown zone lose to it BY DESIGN — so the reason names
                // what would beat it rather than implying something is broken.
                case Cause.TargetItopod:
                    return "Target ITOPOD outranks the zone target — only a gear hunt or an advisor drop farm beats it";
                case Cause.GoldSnipeNoZone:
                case Cause.CBlockNoZone:
                case Cause.EvilClimbNoZone:
                case Cause.GoldStarvedNoZone:
                    return "nothing clearable yet — this is a decline, not a fault";
                default:
                    return IsOwner(previous) || previous == Cause.UnlockFallback
                        ? "the override released"
                        : "normal routing";
            }
        }

        // ---- the latch ----------------------------------------------------------------------
        // Three ints, compared on a per-frame path. Deliberately NOT the string-signature idiom the
        // 10-minute advisor passes use (GearFarmPause.ShouldSurface): this one runs ~60x/second.

        private static Cause _last = Cause.None;
        private static int _lastIntended = int.MinValue;
        private static int _lastRouted = int.MinValue;
        private static bool _lastSpoke;

        public static bool ShouldSurface(Cause now, int intended, int routed,
                                         out Cause previous, out bool previousSpoke)
        {
            previous = _last;
            previousSpoke = _lastSpoke;
            if (now == _last && intended == _lastIntended && routed == _lastRouted) return false;
            _last = now;
            _lastIntended = intended;
            _lastRouted = routed;
            _lastSpoke = false;   // provisional; the caller reports back via Spoke()
            return true;
        }

        // The caller reports whether the line it built was non-empty, so the NEXT release knows
        // whether there was a hold to release from. Kept as a call rather than inferred inside
        // ShouldSurface because Describe needs the zone NAMES, which only the live side can resolve.
        public static void Spoke(bool spoke) { _lastSpoke = spoke; }

        public static Cause Last => _last;

        // Test seam, and the payload-reload seam: statics survive nothing, but a test asserting
        // first-time-only behaviour has to be able to clear the latch.
        public static void Reset()
        {
            _last = Cause.None;
            _lastIntended = int.MinValue;
            _lastRouted = int.MinValue;
            _lastSpoke = false;
        }

        // ---- LAYER 1'S HALF OF THE SAME SENTENCE — audit/40 §3 item 2 -------------------------
        //
        // Describe() above answers "what is being adventured INSTEAD", from Main.SnipeZone, at the
        // frame the displacement happens. That already closes the layer-2 half of every row: R4/R5/
        // R6/R7 — §7's corrected membership for the four contenders ApplyZones does NOT stand down
        // for — each carry a NoteRouting call today (Main.cs:1386, :1400, :1438, :1449), as do R3 and
        // R8, which it does stand down for.
        //
        // ITEM 2 IS ABOUT THE OTHER LINE. AdvisorApply writes and announces at the moment it PICKS a
        // target, on a 10-minute cadence, and quotes an ETA with it:
        //
        //     Advisor: rare farm -> Chocolate World (a drop every ~4h, 46 merges left = ~295h)
        //
        // An ETA is a claim about hours that do not start elapsing while an owner holds the row. That
        // is §3 item 1 restated at the writer: "the log line is a record of an intent, and reads as a
        // statement of fact. Nothing distinguishes the two." The resolver's own line cannot cover it:
        // ShouldSurface is latched on three ints, so a TRACK change that keeps the zone number (set →
        // rare on the same zone, a re-announce after a payload reload wipes the layer-1 signatures)
        // moves none of them — the advisor re-announces and the resolver correctly stays quiet.
        //
        // ⚠ IT DECIDES NOTHING, AND IT RE-DERIVES NOTHING. A suffix on a line that was already going
        // to be written, built from `Last` — the cause the resolver itself computed. audit/40 §2's
        // precedence is deliberate and is untouched; a second copy of "who owns the row" would be
        // free to drift from the one that decides it, which is why Owner() is shared rather than
        // re-spelled. The tense is the difference: Describe reports one frame, this reports a hold
        // the operator is about to start counting hours against.
        //
        // TWO SILENCES, and they are the same two bounds audit/40 §6.1 writes the drop-farm row with
        // (`0 <= SnipeZone < 1000`):
        //   -1    the SavedSettings sentinel (SavedSettings.cs:13). Nothing was written, so nothing
        //         was displaced — Describe's own rule at the owner rows.
        //   1000  IS the ITOPOD (audit/40 §0: "zone 1000, not a separate system"). The advisor names
        //         it only as its FALLBACK venue — the boost farm with no boost demand, and the ITOPOD
        //         phase — never with an ETA to invalidate, and it is where routing lands by default
        //         anyway. A line saying an owner took the ITOPOD away is a line about nothing.
        //
        // Only IsOwner causes speak. A hand-back means normal routing continues, i.e. the announced
        // zone IS being adventured; UnlockFallback is a rewrite of a locked target (§3 item 4) whose
        // sentence is Describe's, and the advisor's own targets come from zones it can already farm.
        public static string OwnerNote(Cause now, int target, string targetName)
        {
            if (!IsOwner(now)) return "";
            if (target < 0 || target >= 1000) return "";
            return " — " + Owner(now) + " owns adventure routing right now; "
                 + (string.IsNullOrEmpty(targetName) ? "this zone" : targetName)
                 + " is not being adventured while it holds";
        }
    }
}
