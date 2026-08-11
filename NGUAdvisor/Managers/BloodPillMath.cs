using System;

namespace NGUAdvisor.Managers
{
    // BloodPlanner's and BloodMagicManager's Unity-free decision cores (audit 01 §3.4, extraction
    // step 4). Report 04 §10 recorded BOTH files at zero tests; amendment 01 §P3 repeats it and adds
    // that the blood layer has no allocator at all.
    //
    // WHY THIS ONE MATTERS. Amendment 01 puts Counterfeit Gold, Spaghetti, Iron Pill and Number Boost
    // on the BLOOD layer, owned by BloodRouter/BloodPlanner — and names the Iron Pill's horizon
    // decision (record §4, open item 10) as the one place a temporal-banking model has to live. That
    // decision is the PillTiming ladder below. BloodRouter is already extracted and tested and is NOT
    // touched here.
    //
    // PARITY ONLY. No blood-layer allocator is written, no target or surplus is computed, and M1 (the
    // magic→blood conversion) is not attempted — it does not exist in the codebase and this pass does
    // not invent it.
    public static class BloodPillMath
    {
        // ---- Iron Pill timing (BloodPlanner.Analyze) --------------------------------------------

        public enum PillVerdict
        {
            // The pill cannot move current adventure stats — never pool, never cast.
            NotWorthwhile,
            // Cooldown outlasts the scheduled rebirth — don't pool blood for it.
            UnreachableThisRun,
            // On cooldown and still reachable — accruing.
            Charging,
            // Ready, but there is not yet enough blood to produce a single point of power.
            NoBloodYet,
            // Ready and fundable, but the caster's own fail-safe would refuse this cast.
            HoldingFailSafe,
            // Cast now: casting frees the cooldown to brew a SECOND pill worth more than holding.
            CastNowSecondPill,
            // Cast now: the next breakpoint is out of reach before the rebirth.
            CastNowLastBreakpoint,
            // Cast now: frequent casts of a flat ^0.25 stat beat holding for a bigger single one.
            CastNowCadence
        }

        public struct PillInputs
        {
            public double Blood;                // bloodMagic.bloodPoints
            public double Bps;                  // bloodMagicController.totalBloodGainedPerSecond()
            public double RunLeftSec;           // WandoosAdvisor.RunHorizonMinutes()*60 — CLAMPED, see below
            public double TrueRunLeftSec;       // real seconds to the scheduled rebirth; MaxValue if none
            public double CdLeftSec;
            public double MinBlood;             // bloodSpells.minAdventureBlood()
            public double AdvBaseAttack;        // max(1, adventure.attack) — the BASE stat, not the total
            public double IronPillBonus;        // itopod.ironPillBonus() on Evil+, else 1.0
            public bool AutosDraining;          // any investment auto-spell is on, so blood drains each second
            public double CapGrowthPerSec;      // EMA of magic-cap relative growth
            public double CooldownSec;          // bloodSpells.adventureSpellCooldown
            public double AvailableForSec;      // adventureSpellTime.totalseconds - cooldown
            public double PillWorthFraction;
            public double PillMinAvailableSec;
            public double PillPoolingHorizonSec;
        }

        public struct PillDecision
        {
            public PillVerdict Verdict;
            public long PowerNow;       // display value: capped at 1e8 and Evil-bonused
            public long ENow;           // raw floor(blood^0.25) — the value the ladder gates on
            public long EEnd;           // projected power by the rebirth if we keep holding
            public long PillSecond;     // power of a SECOND pill brewed after casting now
            public double ToNextSec;    // seconds to the next ^0.25 breakpoint
            public double BestPossible; // best achievable effect this run, used by the worth gate
            public double BloodAtEnd;
            public bool CastNow;
            public bool PoolingTooFarOut;   // Charging, but past the hard pooling horizon
        }

        public const long PillPowerCap = 100000000L;

        // Iron Pill effect = floor(blood^0.25) (game code; DRs-tab breakpoints: 3^4=81, 4^4=256, ...),
        // so casting just below a breakpoint wastes the whole step. Returns 0 below the cast minimum.
        public static long RawPower(double blood, double minBlood) =>
            blood >= minBlood ? (long)Math.Floor(Math.Pow(blood, 0.25)) : 0;

        // Blood pooled over the window [t0, t0+T], with bps itself growing as the magic cap grows.
        public static double PoolOver(double bps, double capGrowthPerSec, double t0, double T) =>
            T <= 0 ? 0 : bps * T * (1.0 + capGrowthPerSec * (t0 + T / 2.0));

        // The next ^0.25 breakpoint, in blood.
        public static double NextBreakpointBlood(long powerNow) => Math.Pow(powerNow + 1, 4);

        // The whole ladder, in the original's order. Every branch is a verbatim move; only the text
        // formatting stayed behind in BloodPlanner.
        //
        // CHARACTERISED — two things a reader will want to "fix" and must not:
        //  1. RunLeftSec comes from WandoosAdvisor.RunHorizonMinutes(), which CLAMPS to >= 10 minutes.
        //     That is too optimistic for pill timing, which is why TrueRunLeftSec exists as a separate
        //     input — but only the UnreachableThisRun test uses it. Every projection below still uses
        //     the clamped value.
        //  2. The HoldingFailSafe test compares PowerNow (capped and Evil-bonused) against the worth
        //     bar, while every other step of the ladder uses the raw ENow. The two differ on Evil+.
        public static PillDecision Decide(in PillInputs a)
        {
            var d = new PillDecision();

            long powerNow = RawPower(a.Blood, a.MinBlood);
            if (powerNow > 0) powerNow = (long)(powerNow * a.IronPillBonus);
            d.PowerNow = Math.Min(powerNow, PillPowerCap);

            // Worth gate: the pill grants FLAT blood^0.25 to the BASE Adventure Power/Toughness (game:
            // adventure.attack/defense += num — the summand gear multipliers then scale it). Measuring
            // against the gear-inflated total made the pill look worthless long after it stopped being
            // so (user-caught).
            d.BestPossible = Math.Pow(Math.Max(a.Blood, a.Blood + a.Bps * a.RunLeftSec), 0.25) * a.IronPillBonus;
            if (!(d.BestPossible >= a.AdvBaseAttack * a.PillWorthFraction))
            {
                d.Verdict = PillVerdict.NotWorthwhile;
                return d;
            }

            if (a.CdLeftSec > 0 && a.CdLeftSec >= a.TrueRunLeftSec)
            {
                d.Verdict = PillVerdict.UnreachableThisRun;
                return d;
            }

            d.ENow = RawPower(a.Blood, a.MinBlood);

            // While any investment auto-spell toggle is on the game drains the pool every second, so
            // blood only actually accumulates once the pooling window opens (15m before the pill is
            // ready) — project growth over that window, not the whole run.
            double poolStart = a.AutosDraining ? Math.Max(0, a.CdLeftSec - 900) : 0;
            d.BloodAtEnd = a.Blood + PoolOver(a.Bps, a.CapGrowthPerSec, poolStart, Math.Max(0, a.RunLeftSec - poolStart));
            d.EEnd = RawPower(d.BloodAtEnd, a.MinBlood);

            if (a.CdLeftSec > 0)
            {
                d.Verdict = PillVerdict.Charging;
                d.PoolingTooFarOut = a.CdLeftSec > a.PillPoolingHorizonSec;
                return d;
            }

            if (d.ENow < 1)
            {
                d.Verdict = PillVerdict.NoBloodYet;
                return d;
            }

            // Mirror the caster fail-safe (BloodMagicManager.IronPill.FailSafeHold): it holds for the
            // first 30 minutes the pill is available (pooling a stronger cast) and refuses any cast
            // under 10% of base adventure power. Don't advertise "CAST NOW" for a cast the caster will
            // refuse; keep pooling instead.
            if (a.AvailableForSec < a.PillMinAvailableSec || d.PowerNow < a.AdvBaseAttack * a.PillWorthFraction)
            {
                d.Verdict = PillVerdict.HoldingFailSafe;
                return d;
            }

            d.ToNextSec = a.Bps > 0 ? (NextBreakpointBlood(d.ENow) - a.Blood) / a.Bps : double.MaxValue;

            // Two-plan comparison. Pills grant FLAT base stats, so total gain is the SUM of casts:
            // casting now frees the cooldown to brew a SECOND pill from the growth-projected income;
            // holding grows this one pill toward EEnd. Recommend whichever total is higher.
            if (a.RunLeftSec > a.CooldownSec)
            {
                double t2 = a.AutosDraining ? Math.Max(0, a.CooldownSec - 900) : 0;
                double blood2 = PoolOver(a.Bps, a.CapGrowthPerSec, t2, Math.Max(0, a.RunLeftSec - t2));
                d.PillSecond = RawPower(blood2, a.MinBlood);
            }

            d.CastNow = true;
            if (d.PillSecond > 0 && d.ENow + d.PillSecond > d.EEnd)
                d.Verdict = PillVerdict.CastNowSecondPill;
            else if (d.ToNextSec > a.RunLeftSec - 60)
                d.Verdict = PillVerdict.CastNowLastBreakpoint;
            else
                // Cast on cooldown: the pill is a FLAT permanent add on a ^0.25 curve, so N frequent
                // casts sum to ~(T/CD)^0.75 MORE than one bigger pooled cast — holding for a higher
                // single breakpoint is never worth it and it starves the investment sinks meanwhile.
                d.Verdict = PillVerdict.CastNowCadence;
            return d;
        }

        // BloodPlanner.MagicGrowthPerSec's arithmetic: an EMA of the magic cap's relative growth per
        // second, resampled at most once a minute. The sampler STATE stays in BloodPlanner (it is
        // session state, and it deliberately reads 0 until the first 60s window has closed).
        public static double GrowthEma(double previous, double sampleRate) =>
            previous <= 0 ? sampleRate : previous * 0.7 + sampleRate * 0.3;

        public static double RelativeGrowthPerSec(double capNow, double capThen, double dtSec) =>
            dtSec <= 0 ? 0 : Math.Max(0, (capNow / capThen - 1.0) / dtSec);

        // ---- Counterfeit Gold and Spaghetti eligibility (BloodPlanner) --------------------------

        // Counterfeit gold cost-curve knee. Game: +N% GPS needs goldSpellBlood = minGold x 2^(sqrt(N)-1).
        // There is NO game cap (goldBonus = 1 + floor((log2(blood/min)+1)^2)/100 — user-corrected; an
        // earlier "<100%" cutoff discredited Counterfeit far too early). The only knee is the cost curve
        // itself: eligible while the next +1% is reachable within ~20min of the FULL blood income.
        public const double GoldKneeEtaSec = 20 * 60;

        public static double GoldPercentNow(double invested, double minGold) =>
            invested >= minGold && minGold > 0 ? Math.Floor(Math.Pow(Math.Log(invested / minGold, 2.0) + 1.0, 2.0)) : 0;

        public static double GoldNextInvestment(double minGold, double percentNow) =>
            minGold * Math.Pow(2.0, Math.Sqrt(percentNow + 1.0) - 1.0);

        public static bool GoldBelowKnee(double invested, double minGold, double bps, out double percentNow, out double etaSec)
        {
            percentNow = 0;
            etaSec = double.MaxValue;
            if (minGold <= 0) return false;
            double gb = Math.Max(0, invested);
            percentNow = GoldPercentNow(gb, minGold);
            double nextInvest = GoldNextInvestment(minGold, percentNow);
            etaSec = bps > 0 ? (nextInvest - gb) / bps : double.MaxValue;
            return etaSec <= GoldKneeEtaSec;
        }

        // Spaghetti drop chance: +1% per DOUBLING of the invested blood. Worth it only while farming a
        // zone whose recommended drop chance isn't met yet — cost doubles per +1%, so there is no reason
        // to push past the zone target.
        public static int LootPercentNow(double invested, double minLoot) =>
            invested >= minLoot && minLoot > 0 ? (int)Math.Floor(Math.Log(invested / minLoot, 2.0)) : 0;

        public static bool DcBelowRecommendation(double currentDcPercent, double recommendedDcPercent) =>
            currentDcPercent < recommendedDcPercent;

        // Gold/loot only outrank NUMBER while enough of the run remains for their LOG-scaled, in-run
        // bonus to earn back the blood it costs — both are wiped at rebirth and must be re-earned, so
        // their value decays to nothing as the deadline approaches. NUMBER is time-indifferent, so it
        // always owns the tail of the run.
        public const double InvestmentWindowFraction = 0.5;

        public static bool InvestmentWindowOpen(double rebirthTargetSec, double nowSec)
        {
            if (rebirthTargetSec <= 0) return true;
            return Math.Max(0, rebirthTargetSec - nowSec) > rebirthTargetSec * InvestmentWindowFraction;
        }

        // ---- BloodMagicManager: the shared spell-cast gate ---------------------------------------

        public enum CastVerdict
        {
            Cast,
            SpellsDisabled,          // Settings.CastBloodSpells is off
            NotUnlocked,
            NoUserThreshold,         // threshold < 1.0 and this is not a forced rebirth cast
            OnCooldown,
            BelowMinimumBlood,
            BelowPowerThreshold,
            HeldByFailSafe
        }

        // BloodPoints, Effect and FailSafeHolds are LAZY on purpose, the same way BloodRouter.DecideRoute
        // is: the original ladder never reached Effect() unless the blood gate passed, and never reached
        // FailSafeHold() unless the threshold gate passed. Effect() calls into the game (minMacguffin*Blood,
        // totalBloodGuffbonus) and FailSafeHold reads adventure.attack, so evaluating them eagerly here
        // would change WHICH game reads happen on a spell that was never going to cast. Deferring keeps
        // the extraction observationally identical, not merely arithmetically identical.
        public struct CastInputs
        {
            public bool SpellsEnabled;
            public bool Unlocked;
            public bool Forced;          // rebirth force-cast: bypasses the threshold and the fail-safe
            public double Threshold;
            public double TimeSec;       // the spell's own elapsed timer
            public double CooldownSec;
            public Func<double> BloodPoints;
            public double MinBlood;
            public Func<double> Effect;
            public Func<double, bool> FailSafeHolds;   // takes the effect the ladder just computed
        }

        // BloodMagicManager.Spell.Cast's gate ladder, verbatim and in order. `effect` is reported back
        // because the caller's log lines need the value the ladder computed — and 0 when it never got
        // far enough to compute one.
        //
        // CHARACTERISED: `threshold < 1.0` on an unforced cast returns NoUserThreshold — so a spell
        // whose configured threshold is 0 never casts at all outside a rebirth. That is what makes the
        // planner-driven path (DecidePlannedCast, which skips the threshold entirely) necessary.
        public static CastVerdict DecideCast(in CastInputs a, out double effect)
        {
            effect = 0;
            if (!a.SpellsEnabled) return CastVerdict.SpellsDisabled;
            if (!a.Unlocked) return CastVerdict.NotUnlocked;
            if (!a.Forced && a.Threshold < 1.0) return CastVerdict.NoUserThreshold;
            if (a.TimeSec < a.CooldownSec) return CastVerdict.OnCooldown;
            if (a.BloodPoints() < a.MinBlood) return CastVerdict.BelowMinimumBlood;
            effect = a.Effect();
            if (!a.Forced && a.Threshold.CompareTo(effect) > 0) return CastVerdict.BelowPowerThreshold;
            if (!a.Forced && a.FailSafeHolds(effect)) return CastVerdict.HeldByFailSafe;
            return CastVerdict.Cast;
        }

        // The planner-driven path: safety checks only — unlock, cooldown, minimum blood, fail-safe.
        // No user threshold, and the fail-safe applies even though `forced` never does here.
        public static CastVerdict DecidePlannedCast(in CastInputs a, out double effect)
        {
            effect = 0;
            if (!a.Unlocked) return CastVerdict.NotUnlocked;
            if (a.TimeSec < a.CooldownSec) return CastVerdict.OnCooldown;
            if (a.BloodPoints() < a.MinBlood) return CastVerdict.BelowMinimumBlood;
            effect = a.Effect();
            if (a.FailSafeHolds(effect)) return CastVerdict.HeldByFailSafe;
            return CastVerdict.Cast;
        }

        public enum PillHold { None, TooSoonAfterReady, GainTooSmall }

        // IronPill.FailSafeHold, and the model BloodPlanner's HoldingFailSafe branch mirrors. This is
        // the ONE piece report 04 §10 found hand-copied between the two files
        // (BloodMagicManager:139-155 vs BloodPlanner:211-220); both now call this.
        public static PillHold PillFailSafe(double availableForSec, double effect, double baseAdvPower,
                                            double minAvailableSec, double worthFraction)
        {
            if (availableForSec < minAvailableSec) return PillHold.TooSoonAfterReady;
            if (effect < Math.Max(1.0, baseAdvPower) * worthFraction) return PillHold.GainTooSmall;
            return PillHold.None;
        }

        // The three spells' effect formulas (decomp castAdventurePowerupSpell / castMacguffin*Spell).
        public static double IronPillEffect(double bloodPoints, double ironPillBonus) =>
            Math.Floor(Math.Pow(bloodPoints, 0.25)) * ironPillBonus;

        public static double GuffAEffect(double bloodPoints, double minBlood, double guffBonus) =>
            Math.Floor((Math.Log(bloodPoints / minBlood, 10.0) + 1.0) * guffBonus);

        public static double GuffBEffect(double bloodPoints, double minBlood) =>
            Math.Floor(Math.Log(bloodPoints / minBlood, 20.0) + 1.0);

        // ---- Pre-rebirth NUMBER top-up (BaseRebirth.CastBloodSpellsForRebirth) -------------------

        // THE SPELL IS A LINEAR ACCUMULATOR, WHICH IS WHY A RELATIVE FLOOR IS THE RIGHT SHAPE.
        // [DECOMP] RebirthPowerSpell.cs:166-170 — castRebirthSpell() is `rebirthPower += bloodPoints;
        // bloodPoints = 0`. No curve, no bank: within a run it makes no difference when you cast.
        // rebirthPower starts at 1.0 (BloodMagic.cs:41, and TrollChallengeController.cs:623 resets it
        // to 1.0 too) and enters nextAttackMulti / nextDefenseMulti as a plain linear factor
        // (Rebirth.cs:782-783). So the gain from a cast is EXACTLY bloodPoints / rebirthPower — a pure
        // ratio, needing no constant about blood amounts and no assumption about where in a run we are.
        //
        // ⚠ SKIPPING FORFEITS THE BLOOD. AllBloodMagicController.reset() (:40-50) zeroes bloodPoints
        // AND rebirthPower together at the rebirth, so residue left uncast is destroyed rather than
        // carried. That is the trade this floor makes, and it is only defensible because the ratio
        // says the forgone gain is ~1e-10 of the multiplier while the cost is a real allocation loop.
        //
        // WHY IT NEEDS A FLOOR AT ALL. The caller gated on `bloodPoints > 0`, and BloodPlanner spends
        // blood on NUMBER continuously through the run, so what the pre-rebirth top-up finds is float
        // residue — measured 46 times in 52 across one 36 h session, values like 0.59375 and
        // 0.560546875 (exact dyadic fractions, the signature of repeated subtraction) against a
        // rebirthPower already grown to ~7.25e9. That is a relative gain near 1e-10, and it costs a
        // full extra allocation loop: the caller returns true, which sets `delay` and runs
        // removeAllEnergy/Magic/Res3 (BaseRebirth.cs:218-220). ~10 s of unallocated pools per rebirth,
        // which at the 190-250 s rebirth cadence CBlock3.0-N runs at is about 5% of wall clock.
        //
        // THE FLOOR SELF-SCALES, which is the point — early in a run rebirthPower is still 1.0, so even
        // a single blood point is a 100% gain and casts; late in a run the same point is nothing.
        // 1e-6 sits six orders of magnitude above the observed residue and well below any real bank
        // (at rebirthPower 7.25e9 it still casts for 7,250 blood), so it separates dust from intent
        // without adjudicating anything in between.
        public const double NumberTopUpMinGain = 1e-6;

        // Fails OPEN: anything it cannot reason about (non-finite inputs, a rebirthPower at or below
        // zero that the game's own ImportExport.cs:198-200 clamp says should never occur) casts, so
        // this can only ever skip a cast it has positively shown to be negligible.
        public static bool ShouldCastNumberTopUp(double bloodPoints, double rebirthPower,
                                                double minGain = NumberTopUpMinGain)
        {
            if (!(bloodPoints > 0)) return false;                       // nothing to cast, NaN included
            if (double.IsNaN(rebirthPower) || double.IsInfinity(rebirthPower)) return true;
            if (rebirthPower <= 0) return true;                         // cannot form a ratio — cast
            return bloodPoints / rebirthPower >= minGain;
        }
    }
}
