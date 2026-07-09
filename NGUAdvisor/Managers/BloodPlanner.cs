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
                        double r = Math.Max(0, (cap / _capSample - 1.0) / dt);
                        _capGrowthPerSec = _capGrowthPerSec <= 0 ? r : _capGrowthPerSec * 0.7 + r * 0.3;
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

        public static bool PillWorthPursuing()
        {
            if ((DateTime.UtcNow - _pillWorthAt).TotalSeconds < 60) return _pillWorthCache;
            _pillWorthAt = DateTime.UtcNow;
            try
            {
                var p = Analyze();
                _pillWorthCache = p.Known && p.PillWorthwhile && !p.UnreachableThisRun;
            }
            catch { _pillWorthCache = false; }
            return _pillWorthCache;
        }

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
                double trueRunLeft = double.MaxValue;
                try { double tgt = Main.Profile != null ? Main.Profile.NextRebirthTargetSeconds() : -1; if (tgt > 0) trueRunLeft = Math.Max(0, tgt - c.rebirthTime.totalseconds); } catch { }
                double cdLeft = Math.Max(0, spells.adventureSpellCooldown - c.bloodMagic.adventureSpellTime.totalseconds);
                double minBlood = spells.minAdventureBlood();

                p.Known = true;

                // Worth gate (user rule): the pill grants FLAT blood^0.25 to the BASE Adventure
                // Power/Toughness (game: character.adventure.attack/defense += num — the summand
                // gear multipliers then scale). So the yardstick is the BASE stat, not
                // totalAdvAttack: measuring against the gear-inflated total made the pill look
                // worthless long after it stopped being so (user-caught). Evil+ pills are further
                // multiplied by the ITOPOD ironPillBonus.
                double advNow = 1;
                try { advNow = Math.Max(1, c.adventure.attack); } catch { }
                double bestPossible = Math.Pow(Math.Max(blood, blood + bps * runLeft), 0.25);
                try
                {
                    if (c.settings.rebirthDifficulty >= difficulty.evil)
                        bestPossible *= c.adventureController.itopod.ironPillBonus();
                }
                catch { }
                bool pillWorth = bestPossible >= advNow * 0.001;
                p.PillWorthwhile = pillWorth;
                if (!pillWorth)
                {
                    p.Text = $"Iron Pill skipped: +{bestPossible:0} vs {ExpBalancer.Fmt(advNow)} BASE adv power — no meaningful gain at this stage";
                    p.Optimal = true;
                    return p;
                }

                if (cdLeft > 0 && cdLeft >= trueRunLeft)
                {
                    p.UnreachableThisRun = true;
                    p.Text = $"Iron Pill on cooldown ({FmtH(cdLeft)}) — won't be ready before rebirth ({FmtH(trueRunLeft)} left); not pooling blood for it";
                    p.Optimal = true;
                    return p;
                }

                // Effect now and best achievable before the run ends. Audit fix: while any investment
                // auto-spell toggle is on, the game drains the pool every second — blood only actually
                // accumulates once the pooling window opens (15m before the pill is ready), so project
                // growth over that window, not the whole run. bps itself GROWS with magic cap over the
                // run — PoolOver projects pooled blood over [t0, t0+T] with the measured growth rate.
                long eNow = blood >= minBlood ? (long)Math.Floor(Math.Pow(blood, 0.25)) : 0;
                bool autosDraining = c.bloodMagic.rebirthAutoSpell || c.bloodMagic.lootAutoSpell || c.bloodMagic.goldAutoSpell;
                double capGrowth = MagicGrowthPerSec(c);
                double PoolOver(double t0, double T) => T <= 0 ? 0 : bps * T * (1.0 + capGrowth * (t0 + T / 2.0));

                double poolStart = autosDraining ? Math.Max(0, cdLeft - 900) : 0;
                double bloodAtEnd = blood + PoolOver(poolStart, Math.Max(0, runLeft - poolStart));
                long eEnd = bloodAtEnd >= minBlood ? (long)Math.Floor(Math.Pow(bloodAtEnd, 0.25)) : 0;

                if (cdLeft > 0)
                {
                    p.Text = $"Iron Pill ready in {FmtH(cdLeft)} — projected power {eEnd} by rebirth (+{ExpBalancer.Fmt(bps)}/s blood)";
                    p.Severity = 0;
                    return p;
                }

                // Pill is ready. Next breakpoint and whether it's reachable before rebirth.
                if (eNow < 1)
                {
                    p.Text = bps > 0
                        ? $"Iron Pill ready — accruing blood ({ExpBalancer.Fmt(blood)}, +{ExpBalancer.Fmt(bps)}/s)"
                        : "Iron Pill ready but NO blood income — allocate magic to rituals";
                    p.Severity = bps > 0 ? 0 : 1;
                    return p;
                }

                double nextBp = Math.Pow(eNow + 1, 4);
                double toNext = bps > 0 ? (nextBp - blood) / bps : double.MaxValue;

                // Two-plan comparison (user ask: "is NOW the best time to cast?"). Pills grant FLAT
                // base stats, so total gain is the SUM of casts: casting now frees the cooldown to
                // brew a SECOND pill from the growth-projected income; holding grows this one pill
                // toward eEnd. Recommend whichever total is higher.
                double cd = spells.adventureSpellCooldown;
                long pillSecond = 0;
                if (runLeft > cd)
                {
                    double t2 = autosDraining ? Math.Max(0, cd - 900) : 0;
                    double blood2 = PoolOver(t2, Math.Max(0, runLeft - t2));
                    if (blood2 >= minBlood) pillSecond = (long)Math.Floor(Math.Pow(blood2, 0.25));
                }

                if (pillSecond > 0 && eNow + pillSecond > eEnd)
                {
                    p.CastIronNow = true;
                    p.Text = $"Cast Iron Pill NOW at power {eNow} — a second pill (~{pillSecond}) brews by rebirth; {eNow}+{pillSecond} beats holding for {eEnd}";
                    p.Severity = 1;
                }
                else if (toNext > runLeft - 60)
                {
                    p.CastIronNow = true;
                    p.Text = $"Cast Iron Pill NOW at power {eNow} — next breakpoint ({eNow + 1}) is out of reach this run";
                    p.Severity = 1;
                }
                else
                {
                    // Cast on cooldown: the pill is a FLAT permanent add on a ^0.25 curve, so N frequent
                    // casts sum to ~(T/CD)^0.75 MORE than one bigger pooled cast — holding for a higher
                    // single breakpoint is never worth it and it starves the investment sinks meanwhile.
                    p.CastIronNow = true;
                    p.Text = $"Cast Iron Pill NOW at power {eNow} — frequent casts beat holding (flat ^0.25 stat sums)";
                    p.Severity = 1;
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
        //  - Spaghetti (drop chance) while the farm zone's recommended DC isn't met.
        //  - NUMBER boost is linear with no diminishing returns — the default sink — but dead in NORB
        //    (no rebirth = no Number gain) and pointless when rebirth is off.
        public static void FillRouting(ref Plan p)
        {
            try
            {
                var c = Main.Character;
                if (c == null || c.bossID <= 36) return;   // game gates auto-spells until boss 37

                p.RouteKnown = true;

                double cdLeft = Math.Max(0, c.bloodSpells.adventureSpellCooldown - c.bloodMagic.adventureSpellTime.totalseconds);
                double runLeft = WandoosAdvisor.RunHorizonMinutes() * 60.0;
                double trueRunLeft = double.MaxValue;
                try { double tgt = Main.Profile != null ? Main.Profile.NextRebirthTargetSeconds() : -1; if (tgt > 0) trueRunLeft = Math.Max(0, tgt - c.rebirthTime.totalseconds); } catch { }
                // Pool ONLY for a pill that can move the needle AND can be cast before rebirth (user-
                // reported: it pooled magic into blood for a pill whose cooldown outlasted the run).
                if (Main.Settings != null && Main.Settings.CastBloodSpells && p.PillWorthwhile
                    && !p.UnreachableThisRun && cdLeft < Math.Min(trueRunLeft, 900))
                {
                    p.PoolForPill = true;
                    p.RouteReason = "pooling for Iron Pill";
                    return;
                }

                // SINGLE-SINK routing (user pick): all leftover blood-time goes to the ONE best use for the
                // run's goal, each capped at its optimal level. The game splits blood EVENLY among enabled
                // toggles, so enabling several DILUTES them — pick exactly one. Priority auto-derived from
                // context: Counterfeit gold (gold-starved for augs) > Spaghetti (farming below zone DC) >
                // NUMBER (bank to target). All three investments PERSIST past rebirth (only the pool resets).
                bool norb = false;
                try { norb = ChallengeDetector.Current() == "NORB"; } catch { }
                bool rebirthOn = Main.Profile != null && Main.Profile.NextRebirthTargetSeconds() > 0;
                double bps = 0;
                try { bps = c.bloodMagicController.totalBloodGainedPerSecond(); } catch { }
                double rebirthPower = 1;
                try { rebirthPower = c.bloodMagic.rebirthPower; } catch { }
                double numTarget = Main.Settings != null ? Main.Settings.BloodNumberThreshold : 0;

                p.WantGold = p.WantLoot = p.WantRebirth = false;

                if (c.machine.realBaseGold > 0 && OptimizationAdvisor.GoldStarvedForAugs(c, 2.0)
                    && GoldBelowKnee(c, bps, out var goldReason))
                {
                    p.WantGold = true;
                    p.RouteReason = goldReason;
                }
                else if (DcBelowZoneRec(c, out var dcReason))
                {
                    p.WantLoot = true;
                    p.RouteReason = dcReason;
                }
                else if (!norb && rebirthOn && numTarget > 0 && rebirthPower < numTarget)
                {
                    p.WantRebirth = true;
                    bool ceiling = false;
                    try { ceiling = c.bossID - 1 >= OptimizationAdvisor.BossUnlockCeiling(); } catch { }
                    p.RouteReason = ceiling
                        ? $"NUMBER (boss EXP push · banking to {ExpBalancer.Fmt(numTarget)})"
                        : $"NUMBER (banking to {ExpBalancer.Fmt(numTarget)})";
                }
                else
                {
                    // Nothing worth banking: keep every auto-spell OFF so blood magic doesn't drain the
                    // marathon's magic cap (BR-30 gates on a live NUMBER/pill). Set a Number target to bank.
                    p.RouteReason = numTarget <= 0 && rebirthOn && !norb
                        ? "blood idle — set a Number target to bank rebirth power"
                        : "blood idle — no sink worth banking now";
                }
            }
            catch (Exception e) { Main.LogDebug($"BloodPlanner routing: {e.Message}"); }
        }

        // Counterfeit gold cost-curve knee. Game: +N% GPS needs goldSpellBlood = minGold x 2^(sqrt(N)-1).
        // Eligible while below the +100% game cap AND the next +1% is reachable within ~20min of the FULL
        // blood income (single-sink → no sharing). Past that the step is too slow to be worth the pool.
        private static bool GoldBelowKnee(Character c, double bps, out string reason)
        {
            reason = null;
            try
            {
                double gb = Math.Max(0, c.bloodMagic.goldSpellBlood);
                double gm = c.bloodSpells.minGoldBlood();
                if (gm <= 0) return false;
                double cur = gb >= gm ? Math.Floor(Math.Pow(Math.Log(gb / gm, 2.0) + 1.0, 2.0)) : 0;
                if (cur >= 100) return false;
                double nextInvest = gm * Math.Pow(2.0, Math.Sqrt(cur + 1.0) - 1.0);
                double eta = bps > 0 ? (nextInvest - gb) / bps : double.MaxValue;
                if (eta > 20 * 60) return false;
                reason = $"Counterfeit gold +{cur:0}% GPS (next +1% in ~{Math.Max(1, eta / 60):0}m)";
                return true;
            }
            catch { return false; }
        }

        // Spaghetti drop chance: worth it only while zone-farming a zone whose recommended drop chance
        // isn't met yet. Cost DOUBLES per +1%, so there's no reason to push past the zone target.
        private static bool DcBelowZoneRec(Character c, out string reason)
        {
            reason = null;
            try
            {
                if (Main.Settings == null || !Main.Settings.GoldCBlockMode) return false;
                int zone = ZoneStatHelper.GetBestZone()?.Zone ?? -1;
                if (zone < 0 || !ZoneStatHelper.RecommendedDcPercent.TryGetValue(zone, out var rec)) return false;
                double cur = c.lootFactor() * 100.0;
                if (cur >= rec) return false;
                reason = $"Spaghetti DC — {cur:0}% < zone rec {rec:0}%";
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
                    int cur = (int)Math.Floor(Math.Log(lb / lm, 2.0));
                    parts.Add($"Spaghetti +{cur}% DC (next at {ExpBalancer.Fmt(lm * Math.Pow(2, cur + 1))})");
                }
                double gb = c.bloodMagic.goldSpellBlood, gm = s.minGoldBlood();
                if (gb >= gm && gm > 0)
                {
                    // Exact game formula: floor((log2(invested/min)+1)^2).
                    double cur = Math.Floor(Math.Pow(Math.Log(gb / gm, 2.0) + 1.0, 2.0));
                    double nextAt = gm * Math.Pow(2.0, Math.Sqrt(cur + 1.0) - 1.0);
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
