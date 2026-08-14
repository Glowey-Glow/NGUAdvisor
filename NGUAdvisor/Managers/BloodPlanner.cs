using System;
using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // Blood Magic planner: replaces hand-picked spell thresholds with breakpoint math.
    //
    // Live inputs (all game reads): blood on hand (bloodMagic.bloodPoints), total blood/sec from the
    // active rituals at the current magic allocation (bloodMagicController.totalBloodGainedPerSecond),
    // Iron Pill cooldown state (bloodSpells.adventureSpellCooldown vs bloodMagic.adventureSpellTime),
    // and the run's remaining time (profile rebirth target via WandoosAdvisor.RunHorizonMinutes).
    //
    // Iron Pill effect = floor(blood^0.25) (game code; DRs-tab breakpoints: 3^4=81, 4^4=256, ...), so
    // casting just below a breakpoint wastes the whole step. The optimal cast is the LAST breakpoint
    // reachable this run: hold while the next breakpoint is reachable before rebirth, cast when it
    // isn't. Blood is lost on rebirth, so the existing force-cast-on-rebirth path stays as the net.
    public static class BloodPlanner
    {
        public struct Plan
        {
            public bool Known;
            public string Text;
            public int Severity;
            public bool Optimal;
            public bool CastIronNow;
            // Desired investment-spell routing (the game's auto-toggles split ALL blood evenly among
            // whichever are on, every second — which also starves the Iron Pill's pool):
            public bool RouteKnown;
            public bool PillWorthwhile;   // false = the pill can't move current adventure stats — never pool/cast
            public bool UnreachableThisRun;   // pill cooldown outlasts the scheduled rebirth — don't pool blood for it
            public bool PoolForPill;   // all autos off while charging the pill
            public bool WantRebirth;   // Blood NUMBER Boost: linear (rebirthPower += blood), no cap — default sink
            public bool WantLoot;      // Spaghetti: log2(invested/min)% drop chance
            public bool WantGold;      // Counterfeit Gold: log2(invested/min)^2 % GPS (needs TM base gold)
            public string RouteReason;
            public double CdLeftSec, CdTotalSec;   // Iron Pill cooldown state — the panel bar charges toward ready
            public long PillPowerNow;              // what casting the current pool grants (game formula); 0 = below cast min
        }

        // Magic-cap growth sampler (user ask: time the pill against Magic Cap/Power growth, not just
        // current income). Ritual blood/sec scales with the magic feeding it, which scales with cap
        // as the run grows it — so future pooling is faster than bps-now suggests. EMA of the cap's
        // relative growth per second; statics reset on reload and the sampler re-seeds (growth reads
        // 0 until the first 60s window — conservative, never over-promises a second pill).
        private static double _capSample;
        private static DateTime _capSampleAt = DateTime.MinValue;
        private static double _capGrowthPerSec;

        private static double MagicGrowthPerSec(Character c)
        {
            try
            {
                double cap = c.totalCapMagic();
                var now = DateTime.UtcNow;
                if (_capSampleAt == DateTime.MinValue || _capSample <= 0)
                {
                    _capSample = cap;
                    _capSampleAt = now;
                }
                else
                {
                    double dt = (now - _capSampleAt).TotalSeconds;
                    if (dt >= 60)
                    {
                        double r = BloodPillMath.RelativeGrowthPerSec(cap, _capSample, dt);
                        _capGrowthPerSec = BloodPillMath.GrowthEma(_capGrowthPerSec, r);
                        _capSample = cap;
                        _capSampleAt = now;
                    }
                }
            }
            catch { }
            return _capGrowthPerSec;
        }

        // Cheap cached check for allocation decisions (marathon ritual gating).
        private static bool _pillWorthCache = true;
        private static DateTime _pillWorthAt = DateTime.MinValue;

        // HARD pooling horizon (user rule): never treat the pill as a live blood consumer more than
        // 1 hour before it comes off cooldown. Rituals fed for a pill hours away are pure NGU-magic
        // loss — the pool builds in the final hour instead.
        private const double PillPoolingHorizonSec = 3600;

        public static bool PillWorthPursuing()
        {
            if ((DateTime.UtcNow - _pillWorthAt).TotalSeconds < 60) return _pillWorthCache;
            _pillWorthAt = DateTime.UtcNow;
            try
            {
                var p = Analyze();
                double cdLeft = 0;
                try
                {
                    var c = Main.Character;
                    cdLeft = Math.Max(0, c.bloodSpells.adventureSpellCooldown - c.bloodMagic.adventureSpellTime.totalseconds);
                }
                catch { }
                _pillWorthCache = p.Known && p.PillWorthwhile && !p.UnreachableThisRun
                    && cdLeft <= PillPoolingHorizonSec;
            }
            catch { _pillWorthCache = false; }
            return _pillWorthCache;
        }

        // Live reads, then BloodPillMath.Decide, then the presentation. The LADDER — every branch and
        // its order — is in the core and under characterisation test; what is left here is the game
        // state it needs and the text each verdict produces.
        public static Plan Analyze()
        {
            var p = new Plan();
            try
            {
                var c = Main.Character;
                if (c == null) return p;
                var spells = c.bloodSpells;
                if (spells == null) return p;

                double blood = c.bloodMagic.bloodPoints;
                double bps = 0;
                try { bps = c.bloodMagicController.totalBloodGainedPerSecond(); } catch { }
                double runLeft = WandoosAdvisor.RunHorizonMinutes() * 60.0;
                // TRUE remaining time to the scheduled rebirth. RunHorizonMinutes clamps to >=10min for
                // the Wandoos projection, which is too optimistic for pill timing — use the real value
                // here (MaxValue when no rebirth is scheduled: the pill will eventually come off cooldown).
                double trueRunLeft = RunLeftSeconds(c);
                double cdLeft = Math.Max(0, spells.adventureSpellCooldown - c.bloodMagic.adventureSpellTime.totalseconds);
                double availableFor = c.bloodMagic.adventureSpellTime.totalseconds - spells.adventureSpellCooldown;

                // Worth yardstick (user rule): the pill grants FLAT blood^0.25 to the BASE Adventure
                // Power/Toughness (game: character.adventure.attack/defense += num — the summand gear
                // multipliers then scale). So the yardstick is the BASE stat, not totalAdvAttack:
                // measuring against the gear-inflated total made the pill look worthless long after it
                // stopped being so (user-caught). Evil+ pills are further multiplied by the ITOPOD
                // ironPillBonus, which is 1.0 below Evil.
                double advNow = 1;
                try { advNow = Math.Max(1, c.adventure.attack); } catch { }
                double ironBonus = 1.0;
                try { if (c.settings.rebirthDifficulty >= difficulty.evil) ironBonus = c.adventureController.itopod.ironPillBonus(); } catch { }

                var d = BloodPillMath.Decide(new BloodPillMath.PillInputs
                {
                    Blood = blood,
                    Bps = bps,
                    RunLeftSec = runLeft,
                    TrueRunLeftSec = trueRunLeft,
                    CdLeftSec = cdLeft,
                    MinBlood = spells.minAdventureBlood(),
                    AdvBaseAttack = advNow,
                    IronPillBonus = ironBonus,
                    // While any investment auto-spell toggle is on, the game drains the pool every
                    // second, so blood only actually accumulates once the pooling window opens.
                    AutosDraining = c.bloodMagic.rebirthAutoSpell || c.bloodMagic.lootAutoSpell || c.bloodMagic.goldAutoSpell,
                    CapGrowthPerSec = MagicGrowthPerSec(c),
                    CooldownSec = spells.adventureSpellCooldown,
                    AvailableForSec = availableFor,
                    PillWorthFraction = BloodMagicManager.PillWorthFraction,
                    PillMinAvailableSec = BloodMagicManager.PillMinAvailableSec,
                    PillPoolingHorizonSec = PillPoolingHorizonSec
                });

                p.Known = true;
                p.CdLeftSec = cdLeft;
                p.CdTotalSec = Math.Max(1.0, spells.adventureSpellCooldown);
                // Display-only cast value (decomp castAdventurePowerupSpell): floor(blood^0.25),
                // ×ironPillBonus on Evil+, capped 1e8. The breakpoint math keeps using the raw root —
                // the bonus is a flat multiplier that cancels out of timing.
                p.PillPowerNow = d.PowerNow;
                p.PillWorthwhile = d.Verdict != BloodPillMath.PillVerdict.NotWorthwhile;
                p.UnreachableThisRun = d.Verdict == BloodPillMath.PillVerdict.UnreachableThisRun;
                p.CastIronNow = d.CastNow;

                switch (d.Verdict)
                {
                    case BloodPillMath.PillVerdict.NotWorthwhile:
                        p.Text = $"Iron Pill skipped: +{d.BestPossible:0} vs {ExpBalancer.Fmt(advNow)} BASE adv power — no meaningful gain at this stage";
                        p.Optimal = true;
                        break;

                    case BloodPillMath.PillVerdict.UnreachableThisRun:
                        p.Text = $"Iron Pill on cooldown ({FmtH(cdLeft)}) — won't be ready before rebirth ({FmtH(trueRunLeft)} left); not pooling blood for it";
                        p.Optimal = true;
                        break;

                    case BloodPillMath.PillVerdict.Charging:
                        p.Text = d.PoolingTooFarOut
                            ? $"Iron Pill ready in {FmtH(cdLeft)} — too far out to pool for (magic feeds NGUs until the last hour)"
                            : $"Iron Pill ready in {FmtH(cdLeft)} — projected power {d.EEnd} by rebirth (+{ExpBalancer.Fmt(bps)}/s blood)";
                        p.Severity = 0;
                        break;

                    case BloodPillMath.PillVerdict.NoBloodYet:
                        p.Text = bps > 0
                            ? $"Iron Pill ready — accruing blood ({ExpBalancer.Fmt(blood)}, +{ExpBalancer.Fmt(bps)}/s)"
                            : "Iron Pill ready but NO blood income — allocate magic to rituals";
                        p.Severity = bps > 0 ? 0 : 1;
                        break;

                    case BloodPillMath.PillVerdict.HoldingFailSafe:
                        // The caster's own fail-safe (BloodMagicManager.IronPill.FailSafeHold) would
                        // refuse this cast, so don't advertise "CAST NOW" for it; keep pooling instead
                        // (PoolForPill stays set by FillRouting while cdLeft < 900).
                        p.Text = availableFor < BloodMagicManager.PillMinAvailableSec
                            ? $"Iron Pill ready — pooling {FmtH(Math.Max(0, BloodMagicManager.PillMinAvailableSec - availableFor))} more (fail-safe holds the first 30m for a stronger cast)"
                            : $"Iron Pill ready — holding: cast {p.PillPowerNow} < 10% of base adv power {ExpBalancer.Fmt(advNow)}";
                        p.Severity = 0;
                        break;

                    case BloodPillMath.PillVerdict.CastNowSecondPill:
                        p.Text = $"Cast Iron Pill NOW at power {d.ENow} — a second pill (~{d.PillSecond}) brews by rebirth; {d.ENow}+{d.PillSecond} beats holding for {d.EEnd}";
                        p.Severity = 1;
                        break;

                    case BloodPillMath.PillVerdict.CastNowLastBreakpoint:
                        p.Text = $"Cast Iron Pill NOW at power {d.ENow} — next breakpoint ({d.ENow + 1}) is out of reach this run";
                        p.Severity = 1;
                        break;

                    default:
                        // CastNowCadence. The pill is a FLAT permanent add on a ^0.25 curve, so N
                        // frequent casts sum to ~(T/CD)^0.75 MORE than one bigger pooled cast — holding
                        // for a higher single breakpoint is never worth it and it starves the
                        // investment sinks meanwhile.
                        p.Text = $"Cast Iron Pill NOW at power {d.ENow} — frequent casts beat holding (flat ^0.25 stat sums)";
                        p.Severity = 1;
                        break;
                }
                return p;
            }
            catch (Exception e) { Main.LogDebug($"BloodPlanner: {e.Message}"); return p; }
        }

        // Decide the investment-spell routing. The game's autoSpell() splits blood evenly among the
        // enabled toggles each second, so "setting the spells properly" means choosing WHICH are on:
        //  - While the Iron Pill is charging (ready or ready soon), everything is OFF to pool.
        //  - Counterfeit Gold only helps if the Time Machine has base gold to multiply, and matters
        //    most while gold-starved for augments.
        //  - Spaghetti (drop chance) while the farm zone's recommended DC isn't met AND the user's
        //    SpaghettiThreshold cap hasn't been bought yet. Titans are deliberately not part of this
        //    test — their loot does not roll against drop chance at all; see DcBelowZoneRec.
        //  - NUMBER boost is the default sink — dead only in NORB (no rebirth = the banked multi is
        //    never cashed) and when no rebirth is scheduled.
        public static void FillRouting(ref Plan p)
        {
            try
            {
                var c = Main.Character;
                if (c == null || c.bossID <= 36) return;   // game gates auto-spells until boss 37

                p.RouteKnown = true;

                double cdLeft = Math.Max(0, c.bloodSpells.adventureSpellCooldown - c.bloodMagic.adventureSpellTime.totalseconds);
                double trueRunLeft = RunLeftSeconds(c);
                // Pool ONLY for a pill that can move the needle AND can be cast before rebirth (user-
                // reported: it pooled magic into blood for a pill whose cooldown outlasted the run).
                // The gate mirrors ApplyBlood's own (AdvisorBlood && CastBloodSpells): with
                // CastBloodSpells on but AdvisorBlood off nothing casts on planner timing, so pooling
                // would strand the blood until the rebirth force-cast.
                if (AdvisorOwnsBlood() && p.PillWorthwhile
                    && !p.UnreachableThisRun && cdLeft < Math.Min(trueRunLeft, 900))
                {
                    p.PoolForPill = true;
                    p.RouteReason = "pooling for Iron Pill";
                    return;
                }

                // SINGLE-SINK routing (user pick): the game splits blood EVENLY among enabled toggles,
                // so enabling several DILUTES them — pick exactly one.
                //
                // The three sinks are NOT commensurable (decomp):
                //   NUMBER  rebirthPower += blood  — LINEAR and uncapped (RebirthPowerSpell), and a
                //           straight multiplier on the WHOLE next-run attack/defense multi
                //           (Rebirth.calculateNextMultis), re-based to 1.0 every rebirth.
                //   Gold    1 + floor((log2(b/min)+1)^2)/100   — LOG.
                //   Loot    +1% drop chance per DOUBLING of b  — LOG.
                // All three are wiped by bloodMagicController.reset() at rebirth — an earlier comment
                // here claimed they persist; they do not. Only NUMBER leaves anything behind, as the
                // multi banked a moment earlier by setNewMultis().
                //
                // So gold/loot buy IN-RUN throughput whose value decays with the time left to earn it
                // back, while NUMBER banks the same blood at a linear rate whenever it is spent. Order:
                // NUMBER floor > gold > loot (while the investment window is open) > NUMBER.
                bool norb = false;
                try { norb = ChallengeDetector.Current() == "NORB"; } catch { }
                // ⚠ ASK WHETHER A REBIRTH IS COMING — DO NOT ASK WHEN IT IS.
                // This used to be `NextRebirthTargetSeconds() > 0`, which is a SECONDS figure and only
                // exists for a target expressed in seconds. A NUMBER/BOSSES rebirth entry written
                // without a "Time" key parses to RebirthTime 0 (BreakpointWrapper starts every entry
                // at 0 and only overwrites it when "Time" is present), so that test read it as "no
                // rebirth" and blood went idle on eleven shipped profiles — including CBlock3.2-E,
                // whose whole rebirth trigger IS rebirth power at 1000x, the very quantity the NUMBER
                // spell banks. [OPERATOR] caught it live mid-CBlock3.2-E, 2026-08-08, with blood
                // pooling uncast behind "no rebirth scheduled to bank NUMBER for".
                //
                // RebirthSchedule composes the four facts that actually decide it, and covers all five
                // trigger types: the profile's four (armed iff any entry has RebirthTime >= 0, the
                // same filter DoRebirth uses) plus MoneyPitRunRebirth, which lives outside the profile
                // entirely. NORB and AutoRebirth stay as they were — both must still route to idle.
                var outlook = RebirthOutlook(norb);
                bool rebirthOn = outlook == RebirthSchedule.Outlook.Coming;
                double bps = 0;
                try { bps = c.bloodMagicController.totalBloodGainedPerSecond(); } catch { }
                double rebirthPower = 1;
                try { rebirthPower = c.bloodMagic.rebirthPower; } catch { }
                double numTarget = Main.Settings != null ? Main.Settings.BloodNumberThreshold : 0;

                p.WantGold = p.WantLoot = p.WantRebirth = false;

                // This was `!norb && rebirthOn`. NORB is now one of the four facts RebirthOutlook
                // weighs — and the FIRST it weighs, so it still overrides everything — which means
                // re-testing it here would be a second copy of a test that already ran. The idle
                // message below reads the same `outlook`, so the routing decision and the sentence
                // explaining it cannot disagree about which challenge is on.
                bool numberEligible = rebirthOn;

                // Routing PRIORITY is the pure ladder in BloodRouter.DecideRoute (audit M5): NUMBER floor
                // > gold > loot (while the investment window is open) > NUMBER default > idle. The
                // "Number ≥" knob is a FLOOR, not a ceiling (the old code stopped at the target, capping
                // a linear uncapped multiplier and — because the auto profile funds rituals only while a
                // sink is live — cutting blood income for the rest of the run). Predicates stay LAZY so
                // evaluation is identical to before: the floor is decided without touching the window,
                // the window is read at most once (memoized here), gold/loot carry their out-param reason
                // strings, and loot is only probed if gold declines.
                bool windowOpen = false, windowComputed = false;
                Func<bool> window = () =>
                {
                    if (!windowComputed) { windowOpen = !numberEligible || InvestmentWindowOpen(c); windowComputed = true; }
                    return windowOpen;
                };
                string goldReason = null, dcReason = null;
                var route = BloodRouter.DecideRoute(
                    numberEligible, numTarget, rebirthPower,
                    window,
                    // ⚠ ASK THE GAME WHETHER GOLD IS BEING EARNED — DO NOT READ realBaseGold.
                    // This used to gate on `c.machine.realBaseGold > 0`, which is the PERSISTED Time
                    // Machine stat: it keeps its pre-challenge value, so it stays > 0 during the No
                    // Time Machine challenge and the gate passed. But NOTM zeroes the whole engine —
                    // [DECOMP] Character.grossGoldPerSecond() opens with
                    //     if (challenges.timeMachineChallenge.inChallenge) { return 0.0; }
                    // and `bloodMagicController.goldBonus()`, the multiplier Counterfeit Gold BUYS, is
                    // a term inside the branch that never executes. So blood spent on Counterfeit
                    // during NOTM is multiplied by zero — not merely worth less, provably worth
                    // NOTHING. [OPERATOR] caught it live in CBlock3.2-E's NOTM-1 run, 2026-08-08.
                    //
                    // Gating on grossGoldPerSecond() > 0 asks the game's own authority instead of a
                    // proxy for it, so this also covers every OTHER reason the engine can be dead
                    // (TM not yet unlocked, bar not filling) without enumerating them here — the same
                    // reason LaneTargets defers to the game's own target predicates. `norb` eleven
                    // lines above was already challenge-aware; this gate was the one that was not.
                    () => GrossGoldPerSecond(c) > 0
                        && (OptimizationAdvisor.GoldStarvedForAugs(c, 2.0) || OptimizationAdvisor.GoldStarvedForDiggers(c))
                        && GoldBelowKnee(c, bps, out goldReason),
                    () => DcBelowZoneRec(c, out dcReason));

                switch (route)
                {
                    case BloodRoute.NumberFloor:
                        p.WantRebirth = true;
                        p.RouteReason = $"NUMBER (below floor {ExpBalancer.Fmt(numTarget)} · now x{ExpBalancer.Fmt(rebirthPower)})";
                        break;
                    case BloodRoute.Gold:
                        p.WantGold = true;
                        p.RouteReason = goldReason + (OptimizationAdvisor.GoldStarvedForAugs(c, 2.0) ? " · augs unfunded" : " · digger upgrades unfunded");
                        break;
                    case BloodRoute.Loot:
                        p.WantLoot = true;
                        p.RouteReason = dcReason;
                        break;
                    case BloodRoute.NumberDefault:
                        // DEFAULT SINK. Any blood still pooled at rebirth is force-cast here anyway
                        // (BaseRebirth.CastBloodSpellsForRebirth), so routing it now costs nothing and
                        // starts compounding immediately.
                        p.WantRebirth = true;
                        bool ceiling = false;
                        try { ceiling = BossScale.IsPastBossCeiling(c.effectiveBossID(), OptimizationAdvisor.BossUnlockCeiling()); } catch { }
                        string why = windowOpen ? "default sink" : "banking the run's tail";
                        p.RouteReason = ceiling
                            ? $"NUMBER (boss EXP push · {why} · now x{ExpBalancer.Fmt(rebirthPower)})"
                            : $"NUMBER ({why} · now x{ExpBalancer.Fmt(rebirthPower)})";
                        break;
                    default:
                        // Genuinely nothing to bank: keep every auto-spell OFF so blood magic doesn't
                        // drain the marathon's magic cap (BR-30 gates on a live sink).
                        //
                        // NAME THE ACTUAL CAUSE. One sentence used to cover three different situations
                        // and asserted the wrong one for two of them — "no rebirth scheduled" was
                        // printed against a profile that had scheduled a rebirth, which is what made
                        // the defect read as correct behaviour for as long as it did.
                        switch (outlook)
                        {
                            case RebirthSchedule.Outlook.NoRebirthChallenge:
                                p.RouteReason = "blood idle — NORB: no rebirth to cash a NUMBER multi into";
                                break;
                            case RebirthSchedule.Outlook.AutoRebirthOff:
                                p.RouteReason = "blood idle — Auto Rebirth is off, so a banked NUMBER multi is never cashed";
                                break;
                            default:
                                p.RouteReason = "blood idle — the profile schedules no rebirth to bank NUMBER for";
                                break;
                        }
                        break;
                }
            }
            catch (Exception e) { Main.LogDebug($"BloodPlanner routing: {e.Message}"); }
        }

        // The four live facts behind "is a rebirth coming"; the decision itself is RebirthSchedule.
        // `norb` is passed in rather than re-read so the routing decision and the message it prints
        // cannot disagree with each other about the challenge.
        //
        // FAILS CLOSED. A null Settings or Profile reads as "no rebirth", which routes blood to idle —
        // the same direction the old `Main.Profile != null && ... > 0` failed in, and the safe one:
        // idling banks nothing, whereas casting on a run that never rebirths spends the marathon's
        // magic cap for a multi that is wiped unclaimed.
        private static RebirthSchedule.Outlook RebirthOutlook(bool norb)
        {
            bool autoRebirth = false, moneyPit = false, armed = false;
            try
            {
                var s = Main.Settings;
                if (s != null) { autoRebirth = s.AutoRebirth; moneyPit = s.MoneyPitRunMode; }
            }
            catch { }
            try { armed = Main.Profile != null && Main.Profile.RebirthIsArmed(); } catch { }
            return RebirthSchedule.Current(autoRebirth, norb, armed, moneyPit);
        }

        // TRUE seconds to the scheduled rebirth; MaxValue when none is scheduled.
        private static double RunLeftSeconds(Character c)
        {
            try
            {
                double tgt = Main.Profile != null ? Main.Profile.NextRebirthTargetSeconds() : -1;
                if (tgt > 0) return Math.Max(0, tgt - c.rebirthTime.totalseconds);
            }
            catch { }
            return double.MaxValue;
        }

        // Gold/loot only outrank NUMBER while enough of the run remains for their LOG-scaled, in-run
        // bonus to earn back the blood it costs — both are wiped at rebirth and must be re-earned, so
        // their value decays to nothing as the deadline approaches. NUMBER is time-indifferent, so it
        // always owns the tail of the run. The predicate is BloodPillMath.InvestmentWindowOpen.
        private static bool InvestmentWindowOpen(Character c)
        {
            try
            {
                double tgt = Main.Profile != null ? Main.Profile.NextRebirthTargetSeconds() : -1;
                return BloodPillMath.InvestmentWindowOpen(tgt, c.rebirthTime.totalseconds);
            }
            catch { return true; }
        }

        // Mirrors ApplyBlood's gate (AdvisorApply: AdvisorBlood, then CastBloodSpells). Both must be on
        // for the advisor to own blood routing and pill timing.
        private static bool AdvisorOwnsBlood()
        {
            try
            {
                var s = Main.Settings;
                return s != null && s.AdvisorBlood && s.CastBloodSpells;
            }
            catch { return false; }
        }

        // Does blood have a live consumer worth spending ritual magic on? The auto profile asks this
        // before funding BR-30 (ChallengeOverlay).
        //
        // It must answer with the routing INTENT, not with the toggles ApplyBlood last wrote. The old
        // code read the live flags, which meant the profile could only fund rituals AFTER a sink was
        // already live — and ApplyBlood is throttled to 60s, so the flags lag by up to a tick. Combined
        // with NUMBER being gated behind a threshold that defaults to 0, that was a deadlock: no
        // threshold -> no live toggle -> no rituals -> no blood -> NUMBER stuck at 1.0 forever, which
        // bit hardest late-game once the Iron Pill stopped being worth pursuing and was no longer
        // holding rituals open on its own.
        //
        // When the advisor does NOT own blood, the live toggles ARE the intent (set by the user or by
        // AutoSpellSwap in Main), so read them.
        private static bool _bloodMattersCache = true;
        private static DateTime _bloodMattersAt = DateTime.MinValue;

        public static bool BloodMatters()
        {
            if ((DateTime.UtcNow - _bloodMattersAt).TotalSeconds < 10) return _bloodMattersCache;
            _bloodMattersAt = DateTime.UtcNow;
            try
            {
                var c = Main.Character;
                if (c == null) return _bloodMattersCache = true;
                if (!AdvisorOwnsBlood())
                {
                    var bm = c.bloodMagic;
                    return _bloodMattersCache = bm.rebirthAutoSpell || bm.lootAutoSpell || bm.goldAutoSpell;
                }
                var p = Analyze();
                FillRouting(ref p);
                _bloodMattersCache = (p.RouteKnown && (p.PoolForPill || p.WantRebirth || p.WantLoot || p.WantGold))
                    || PillWorthPursuing();
            }
            catch { _bloodMattersCache = true; }   // fail-safe: keep rituals if the state read throws
            return _bloodMattersCache;
        }

        // Counterfeit gold cost-curve knee. Game: +N% GPS needs goldSpellBlood = minGold x 2^(sqrt(N)-1).
        // There is NO game cap (goldBonus = 1 + floor((log2(blood/min)+1)^2)/100 — user-corrected; the
        // old "<100%" cutoff here discredited Counterfeit far too early). The only knee is the cost
        // curve itself: eligible while the next +1% is reachable within ~20min of the FULL blood
        // income (single-sink → no sharing). Past that the step is too slow to be worth the pool.
        // The game's own answer to "is the gold engine producing anything right now", guarded because
        // it is a live read used inside a routing predicate: a throw here would abort FillRouting's
        // whole try and silently leave the routing unset, so it FAILS CLOSED — a gold engine we cannot
        // read is treated as dead, which declines Counterfeit rather than funding it on a guess.
        // ⚠ Deliberately NOT `machine.realBaseGold` — see the call site for why that proxy is wrong
        // during the No Time Machine challenge.
        private static double GrossGoldPerSecond(Character c)
        {
            try { return c.grossGoldPerSecond(); }
            catch (Exception e) { Main.LogDebug($"BloodPlanner gold engine: {e.Message}"); return 0; }
        }

        private static bool GoldBelowKnee(Character c, double bps, out string reason)
        {
            reason = null;
            try
            {
                if (!BloodPillMath.GoldBelowKnee(c.bloodMagic.goldSpellBlood, c.bloodSpells.minGoldBlood(), bps,
                                                 out double cur, out double eta))
                    return false;
                reason = $"Counterfeit gold +{cur:0}% GPS (next +1% in ~{Math.Max(1, eta / 60):0}m)";
                return true;
            }
            catch { return false; }
        }

        // Spaghetti drop chance: worth it only while zone-farming a zone whose recommended drop chance
        // isn't met yet. Cost DOUBLES per +1%, so there's no reason to push past the zone target.
        //
        // NOT gated on GoldCBlockMode. It used to be, and that was a category error — drop chance has
        // nothing to do with the gold C-block mode, and the gate made a correct decision UNREACHABLE:
        // move to a zone whose recommendation exceeds your drop chance and Spaghetti becomes genuinely
        // worth funding, but the mode flag would still refuse. The zone test is the real question and
        // now the only one.
        //
        // ⚠ TITAN ZONES ARE ABSENT FROM RecommendedDcPercent ON PURPOSE, not by oversight. Titan loot
        // does not roll against drop chance: [DECOMP] LootDrop.cs makeTitanLoot and the first
        // Random.Range(1,6) gear piece are UNCONDITIONAL, and the only roll (the second piece) is
        // `value < 0.5 * lootFactor` with value in [0,1) — guaranteed from lootFactor >= 2. What gates
        // titan gear is the 5-way piece lottery, which no amount of drop chance affects. Ordinary zones
        // need a table because they roll against the CUBE-ROOTED factor with 4-15% caps; titans bypass
        // both. Do not "fix" the table by adding titan zones to it.
        private static bool DcBelowZoneRec(Character c, out string reason)
        {
            reason = null;
            try
            {
                if (Main.Settings == null) return false;
                int zone = ZoneStatHelper.GetBestZone()?.Zone ?? -1;
                if (zone < 0 || !ZoneStatHelper.RecommendedDcPercent.TryGetValue(zone, out var rec)) return false;
                double cur = c.lootFactor() * 100.0;
                if (!BloodPillMath.DcBelowRecommendation(cur, rec)) return false;

                // The user's SpaghettiThreshold as a CAP. Main.cs owns the same setting but only while
                // the advisor is NOT managing blood, so with CastBloodSpells on it was read by nobody
                // and the configured number silently did nothing. Honouring it here gives it one
                // meaning under both writers: how many percent to buy before stopping. 0 = don't.
                int cap = Main.Settings.SpaghettiThreshold;
                if (cap <= 0) return false;
                double lb = c.bloodMagic.lootSpellBlood, lm = c.bloodSpells.minLootBlood();
                int have = lm > 0 && lb >= lm ? BloodPillMath.LootPercentNow(lb, lm) : 0;
                if (have >= cap) return false;

                reason = $"Spaghetti DC — {cur:0}% < zone rec {rec:0}% · +{have}% of {cap}% bought";
                return true;
            }
            catch { return false; }
        }

        // Compact investment status for the expanded detail row.
        public static string InvestmentDetail()
        {
            try
            {
                var c = Main.Character;
                var s = c.bloodSpells;
                var parts = new List<string>();
                double lb = c.bloodMagic.lootSpellBlood, lm = s.minLootBlood();
                if (lb >= lm && lm > 0)
                {
                    int cur = BloodPillMath.LootPercentNow(lb, lm);
                    parts.Add($"Spaghetti +{cur}% DC (next at {ExpBalancer.Fmt(lm * Math.Pow(2, cur + 1))})");
                }
                double gb = c.bloodMagic.goldSpellBlood, gm = s.minGoldBlood();
                if (gb >= gm && gm > 0)
                {
                    // Exact game formula: floor((log2(invested/min)+1)^2).
                    double cur = BloodPillMath.GoldPercentNow(gb, gm);
                    double nextAt = BloodPillMath.GoldNextInvestment(gm, cur);
                    parts.Add($"Gold +{cur:0}% GPS (next at {ExpBalancer.Fmt(nextAt)})");
                }
                if (c.bloodMagic.rebirthPower > 1)
                    parts.Add($"NUMBER x{ExpBalancer.Fmt(c.bloodMagic.rebirthPower)}");
                return parts.Count > 0 ? string.Join(" · ", parts.ToArray()) : null;
            }
            catch { return null; }
        }

        private static string FmtH(double seconds)
        {
            if (seconds < 90) return $"{seconds:0}s";
            if (seconds < 3600) return $"{seconds / 60:0}m";
            return $"{seconds / 3600:0.#}h";
        }
    }
}
