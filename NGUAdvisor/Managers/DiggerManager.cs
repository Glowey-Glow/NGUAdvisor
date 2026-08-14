using System;
using System.Collections.Generic;
using System.Linq;
using static NGUAdvisor.Main;

namespace NGUAdvisor.Managers
{
    public static class DiggerManager
    {
        private static Character _character => Main.Character;
        private static readonly AllGoldDiggerController _dc = _character.allDiggers;

        private static int[] _savedDiggers;
        private static int[] _tempDiggers;
        private static int[] _curDiggers;
        private static int _cheapestDigger;

        public static LockType CurrentLock { get; set; }
        private static readonly int[] TitanDiggers = { 11, 8, 3, 0 };
        private static readonly int[] YggDiggers = { 11, 8 };

        private static List<GoldDigger> Diggers => _character.diggers.diggers;

        private static List<int> ActiveDiggers => _character.diggers.activeDiggers;

        public static void SaveDiggers() => _savedDiggers = ActiveDiggers?.OrderFrom(_curDiggers).ToArray();

        public static void RestoreDiggers()
        {
            EquipDiggers(_savedDiggers);
            RecapDiggers();
        }

        public static void SaveTempDiggers() => _tempDiggers = ActiveDiggers?.OrderFrom(_curDiggers).ToArray();

        public static void RestoreTempDiggers()
        {
            EquipDiggers(_tempDiggers);
            RecapDiggers();
        }

        public static void EquipDiggers(LockType currentLock)
        {
            switch (currentLock)
            {
                case LockType.Titan:
                    EquipDiggers(TitanDiggers, true);
                    return;
                case LockType.Yggdrasil:
                    EquipDiggers(YggDiggers, true);
                    return;
            }
        }

        public static bool EquipDiggers(int[] diggers, bool ignoreCap = false)
        {
            if (!_character.buttons.diggers.interactable)
                return false;

            if (diggers?.Length > 0 == false)
            {
                _dc.clearAllActiveDiggers();
                _curDiggers = null;
                return true;
            }

            // No gold income means no digger can run — bail BEFORE clearing, or a retry loop strips
            // the active set every pass (post-rebirth "diggers never turn on" report: the old code
            // cleared, failed to activate anything at 0 GPS, and repeated every 10s).
            if (_character.grossGoldPerSecond() <= 0.0)
                return false;

            // Only ask for what can actually run: leveled diggers, at most one per slot. A set that
            // names locked/unleveled diggers (advisor or profile) must not fail forever over them.
            var usable = diggers.Where(d => d >= 0 && d < Diggers.Count && Diggers[d].maxLevel > 0)
                                .Distinct()
                                .Take(_dc.maxDiggerSlots())
                                .ToArray();
            if (usable.Length < diggers.Length)
                Main.LogDebug($"EquipDiggers: using {usable.Length}/{diggers.Length} of requested set (rest locked/unleveled or over slot count)");
            if (usable.Length == 0)
                return false;

            _dc.clearAllActiveDiggers();

            var gps = 0.0;
            if (!ignoreCap)
                gps = _character.grossGoldPerSecond() * (100.0 - Settings.DiggerCap) / 100.0;

            var allEquipped = true;

            foreach (var digger in usable)
            {
                Diggers[digger].curLevel = 1;
                if (_character.goldPerSecond() - _dc.drain(digger, true) >= gps)
                    _dc.activateDigger(digger);

                allEquipped &= Diggers[digger].active;
            }

            _curDiggers = diggers.ToArray();

            UpdateCheapestDigger();

            _dc.refreshMenu();

            WriteLedger.Record("diggers.active",
                string.Join(", ", usable.Select(d => d.ToString()).ToArray()),
                allEquipped ? "advisor or profile digger set for this venue"
                            : "some of the requested set could not be afforded at the current gold rate",
                ChallengeOverlay.Segment,
                usable.Length < diggers.Length
                    ? $"Asked for {diggers.Length}, ran {usable.Length} — the rest are locked, unlevelled or past the slot count"
                    : $"All {usable.Length} of the requested set are running",
                ignoreCap ? "Gold cap ignored: a lock owns this set" : $"Held back to leave {Settings.DiggerCap}% of gold income",
                "Cleared and rebuilt as a whole set; the next venue change replaces it");
            return allEquipped;
        }

        // The advisor's converge-in-place path, distinct from the clear-and-rebuild EquipDiggers that
        // the lock-owned swaps and restore still use. The recommendation is reconciled member by member:
        // a digger already active for the right reason keeps its slot AND its level, only obsolete
        // members are dropped, and only missing members are attempted. A member that cannot afford
        // activation this pass is simply left missing — no clear, no level reset, no churn — and retried
        // whole on a later pass. Returns true only once every requested digger is actually active; the
        // caller runs RecapDiggers (levels + gold-spending upgrades) only on that complete set.
        //
        // This does NOT touch _savedDiggers/_tempDiggers/_curDiggers/_swappedDiggers: those belong to the
        // temporary-swap/restore machinery, which is deliberately left on the old clear-and-rebuild path.
        internal static bool ReconcileAdvisorDiggers(int[] requestedDiggers, out bool membershipChanged)
        {
            membershipChanged = false;

            if (!_character.buttons.diggers.interactable)
                return false;

            // Membership must be judged at the LEVEL-1 baseline. RecapDiggers (called right after by
            // ApplyDiggers) resets every active digger to level 1 and redistributes the whole DiggerCap
            // budget by priority — so a kept member's inflated level must NOT make the set read as "full"
            // and freeze out a cheap, higher-value newcomer. The affordability gate below reads the live
            // net (goldPerSecond), which recap leaves sitting at ~the reserve, so net − anyPositiveDrain
            // was ALWAYS below the reserve and no new member could ever join once the active set first
            // saturated the budget (user-caught: the cheap Stats digger, base 1e12, permanently locked out
            // of an open slot). Resetting to level 1 here restores true headroom; recap re-levels at once.
            foreach (var id in ActiveDiggers)
                if (id >= 0 && id < Diggers.Count)
                    Diggers[id].curLevel = 1;

            // Defensive normalization with the executor's own rules (valid, leveled, distinct, capped).
            // The planner already applies these, but the boundary must stay safe for a future caller.
            var target = requestedDiggers?
                .Where(d => d >= 0 && d < Diggers.Count && Diggers[d].maxLevel > 0)
                .Distinct()
                .Take(_dc.maxDiggerSlots())
                .ToArray() ?? new int[0];

            // Drop obsolete members off a SNAPSHOT — activateDigger mutates ActiveDiggers, so the live
            // list must never be the thing being enumerated while toggling. Read the toggle result:
            // membership only changed if the digger actually went inactive.
            foreach (var id in ActiveDiggers.ToArray())
            {
                if (Array.IndexOf(target, id) < 0)
                {
                    _dc.activateDigger(id);
                    if (!Diggers[id].active)
                        membershipChanged = true;
                }
            }

            // An empty request is complete once nothing is active — done AFTER removal so obsolete
            // members are actually dropped, and never falling through to activation.
            if (target.Length == 0)
            {
                if (membershipChanged)
                    _dc.refreshMenu();
                return ActiveDiggers.Count == 0;
            }

            // Attempt only the missing members, in recommendation order, through the EXACT production
            // affordability precheck. No income means nothing can run — leave the current set untouched.
            var gross = _character.grossGoldPerSecond();
            if (gross > 0.0)
            {
                var gps = gross * (100.0 - Settings.DiggerCap) / 100.0;
                foreach (var digger in target)
                {
                    if (Diggers[digger].active)
                        continue;
                    if (ActiveDiggers.Count >= _dc.maxDiggerSlots())
                        break;

                    Diggers[digger].curLevel = 1;
                    if (_character.goldPerSecond() - _dc.drain(digger, true) >= gps)
                    {
                        _dc.activateDigger(digger);
                        // The game runs its own gross-GPS gate and can still refuse — read the result
                        // rather than assume, and never clear/rebuild on a refusal.
                        if (Diggers[digger].active)
                            membershipChanged = true;
                    }
                }
            }

            if (membershipChanged)
                _dc.refreshMenu();

            // Complete only when the live active set EXACTLY matches the target — same count AND
            // membership that ApplyDiggers' early-return checks, so a lingering extra active member
            // can never read as complete and trigger a spurious success log or a next-tick re-run.
            var activeAfter = ActiveDiggers.ToArray();
            return activeAfter.Length == target.Length && target.All(activeAfter.Contains);
        }

        // Lock/restore/quick/profile callers: EquipDiggers just wrote _curDiggers, so it IS the live
        // priority order — level against it.
        public static void RecapDiggers(bool ignoreCap = false) => RecapDiggers(_curDiggers, ignoreCap);

        // Advisor path overload. ReconcileAdvisorDiggers deliberately does NOT update _curDiggers, so the
        // parameterless overload would level the greedy budget in a STALE (last lock swap) or null order,
        // silently discarding the recommendation's ranking — Adventure-leads / Stats-on-push / DC-on-titan
        // never reached the leveler, so during a tight-budget push (Evil) the cubic Stats digger got
        // leveled last on leftover budget instead of first. ApplyDiggers passes CurrentDiggerSet() here so
        // the greedy allocation honors the priorities the selector actually computed.
        public static void RecapDiggers(int[] priorityOrder, bool ignoreCap = false)
        {
            if (!_character.buttons.diggers.interactable)
                return;

            var gps = _character.grossGoldPerSecond();
            if (gps == 0.0)
                return;

            if (!ignoreCap)
                gps *= Settings.DiggerCap / 100.0;

            // VALUE-RANKED allocation across the whole active set (DiggerMath.AllocateLevels).
            //
            // This replaced a sequential residual greedy that walked `ordered` and gave each digger the
            // most it could afford out of whatever the diggers above it had left. That made position #1
            // mean "take everything", not "rank first": measured on a live Evil save, the top three
            // diggers held 99.66% of gross (86.1 / 11.3 / 2.2) while the other eight shared 0.34% and
            // several sat at level 1 — and because each budget was a `gross - sum(above)` cancellation,
            // the ~0.03%/min drift in gross swung the tail through ~10 levels every tick.
            //
            // priorityOrder still matters, but for what it was written for: MEMBERSHIP and tie-breaks.
            // It no longer decides budget share, so reordering the profile list no longer silently moves
            // 86% of the gold economy from one digger to another.
            var ordered = ActiveDiggers?.OrderFrom(priorityOrder).ToArray() ?? new int[0];
            if (ordered.Length == 0)
                return;

            var levels = DiggerMath.AllocateLevels(gps, BuildSpecs(ordered));
            for (int i = 0; i < ordered.Length && i < levels.Length; i++)
                Diggers[ordered[i]].curLevel = levels[i];

            // AllocateLevels guarantees sum(drain) <= budget from the game's own tables, so an overshoot
            // here means something is out of sync (a table read failed, or a digger drains outside the
            // consumesGPS accounting). SAY SO rather than silently reverting: the old per-digger revert
            // restored `curLevel`, but curLevel had already been reset to 1 two lines above it, so a
            // one-ulp overshoot cost that digger its entire allocation instead of one level.
            try
            {
                double liveDrain = _dc.totalGPSDrain(), liveGross = _character.grossGoldPerSecond();
                if (liveGross < liveDrain)
                    Main.LogDebug($"[DiggerDbg] WARNING: allocated drain {liveDrain:0.##e0} exceeds gross {liveGross:0.##e0}");
            }
            catch { }

            UpgradeCheapestDigger();
            _dc.refreshMenu();

            LogRecap(ordered, gps);
        }

        // The running levels RecapDiggers WOULD set right now, keyed by digger id.
        //
        // Exists so the advisor's digger readout and the applier answer the same question from the same
        // code. They used to disagree structurally: the readout computed its own per-digger
        // affordability against `netGps + that digger's drain`, which at DiggerCap 100 (net GPS ~0) is
        // just the digger's own current drain — so its "can run higher" count was identically zero and
        // the row was pinned to "Ideal set is running" no matter how skewed the allocation actually was
        // (user-confirmed 2026-08-01, while one digger held 86% of gross).
        //
        // Returns empty when the recap could not run at all (menu locked, no gold income), which the
        // caller should read as "no plan to compare against", not as "nothing to do".
        public static Dictionary<int, long> PlannedLevels(int[] priorityOrder)
        {
            var result = new Dictionary<int, long>();
            try
            {
                if (!_character.buttons.diggers.interactable)
                    return result;

                var gps = _character.grossGoldPerSecond();
                if (gps <= 0.0)
                    return result;
                gps *= Settings.DiggerCap / 100.0;

                var ordered = ActiveDiggers?.OrderFrom(priorityOrder).ToArray() ?? new int[0];
                if (ordered.Length == 0)
                    return result;

                var levels = DiggerMath.AllocateLevels(gps, BuildSpecs(ordered));
                for (int i = 0; i < ordered.Length && i < levels.Length; i++)
                    result[ordered[i]] = levels[i];
            }
            catch { }
            return result;
        }

        // Digger 2 (Stats) is the one digger whose level enters its bonus cubed
        // (AllGoldDiggerController.totalStatBonus: Math.Pow(curLevel, 3.0)). Everything else is linear.
        private const int StatsDiggerId = 2;

        // POLICY KNOB, deliberately flat. Multiplies a digger's log-value in the allocator — "how much
        // do we care about this digger's growth relative to the others". All 1.0 states no opinion, so
        // the allocator simply equalises marginal growth, which is the defensible default when the twelve
        // bonuses buy genuinely different things (adventure power, raw stats, NGU speed, EXP, blood...).
        // Tune HERE if a run wants a bias. Do NOT express that bias by reordering the priority list:
        // order is membership and tie-breaks, weight is budget share, and conflating the two is what the
        // old allocator did.
        private static readonly double[] DiggerWeights = { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };

        private static double DiggerWeight(int id)
            => id >= 0 && id < DiggerWeights.Length ? DiggerWeights[id] : 1.0;

        // Live read of the game's own digger tables — baseGPSDrain / gpsGrowthRate / startingBoost /
        // boostPerLevel are all public Lists on AllGoldDiggerController, so nothing here is transcribed
        // data that can drift from the game. A short/missing list degrades that digger to "no growth
        // value" rather than throwing, which keeps a bad read from killing the whole recap.
        private static DiggerMath.DiggerSpec[] BuildSpecs(int[] ids)
        {
            var specs = new DiggerMath.DiggerSpec[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                int id = ids[i];
                specs[i] = new DiggerMath.DiggerSpec
                {
                    Id = id,
                    BaseDrain = Read(_dc.baseGPSDrain, id, 0.0),
                    Growth = Read(_dc.gpsGrowthRate, id, 1.0),
                    MaxLevel = id >= 0 && id < Diggers.Count ? Diggers[id].maxLevel : 0L,
                    StartingBoost = Read(_dc.startingBoost, id, 0.0),
                    BoostPerLevel = Read(_dc.boostPerLevel, id, 0.0),
                    Cubic = id == StatsDiggerId,
                    Weight = DiggerWeight(id)
                };
            }
            return specs;
        }

        private static double Read(List<double> list, int i, double fallback)
            => list != null && i >= 0 && i < list.Count ? list[i] : fallback;

        // Post-recap diagnostic (validation aid). Dumps the greedy PRIORITY ORDER and the resulting
        // running level + drain per active digger, so the ordering can be confirmed live from inject.log.
        // Debug-channel and throttled — the advisor recaps every ~30s and this must not spam.
        private static DateTime _lastRecapDbg = DateTime.MinValue;

        private static void LogRecap(int[] ordered, double budget)
        {
            if ((DateTime.UtcNow - _lastRecapDbg).TotalSeconds < 60)
                return;
            _lastRecapDbg = DateTime.UtcNow;
            try
            {
                // SHARE, not just drain. The failure this replaced was a DISTRIBUTION problem — one
                // digger holding 86% of the budget while the tail sat at level 1 — and that is invisible
                // in a list of absolute drains at these magnitudes. Printing each digger's percentage of
                // the allocated total, plus how much of the budget was used, makes a skewed allocation
                // obvious at a glance instead of requiring the numbers to be summed by hand.
                double spent = 0.0;
                foreach (var d in ordered) spent += _dc.drain(d, 0, true);

                var parts = ordered
                    .Select(d => $"{d}:L{Diggers[d].curLevel}/{Diggers[d].maxLevel}"
                               + $"({_dc.drain(d, 0, true):0.##e0} · {(spent > 0 ? 100.0 * _dc.drain(d, 0, true) / spent : 0.0):0.0}%)")
                    .ToArray();
                Main.LogDebug($"[DiggerDbg] gross={_character.grossGoldPerSecond():0.##e0} budget={budget:0.##e0} "
                            + $"spent={spent:0.##e0} ({(budget > 0 ? 100.0 * spent / budget : 0.0):0.0}% of budget) "
                            + $"order=[{string.Join(" ", ordered.Select(d => d.ToString()).ToArray())}] -> {string.Join(", ", parts)}");
            }
            catch { }
        }


        public static void UpdateCheapestDigger()
        {
            if (!Settings.UpgradeDiggers)
                return;
            _cheapestDigger = -1;
            for (var i = 0; i < Diggers.Count; i++)
            {
                if (_cheapestDigger == -1)
                    _cheapestDigger = i;
                if (_dc.upgradeCost(i) < _dc.upgradeCost(_cheapestDigger))
                    _cheapestDigger = i;
            }
        }

        // Buys maxLevel upgrades with GOLD (distinct from the running levels RecapDiggers sets, which
        // cost GPS drain). Target selection is deliberately the global cheapest across all twelve, even
        // inactive ones: the upgrade's payoff is totalLevelBonus, which sums maxLevel over EVERY digger
        // regardless of whether it runs (AllGoldDiggerController.sumOfAllLevels), so cheapest-first is
        // genuinely optimal for that term.
        //
        // WHAT CHANGED: the reserve. This used to recurse until the gold pile was gone behind nothing but
        // Settings.MoneyPitThreshold, which defaults to 1e5 — not a reserve at all past the early game.
        // A level of totalLevelBonus is worth roughly +0.005% on the digger bonuses; the same gold in the
        // augment / Time Machine / Money Pit ladder compounds. So this now yields whenever gold is not
        // comfortably clear of the cheapest live augment purchase, which is the one competing sink the
        // advisor can price. It matters most exactly when it used to be worst: the moment a titan gold
        // bank lands, the old loop would have spent the whole bank here.
        public static void UpgradeCheapestDigger()
        {
            if (!Settings.UpgradeDiggers)
                return;
            if (!_character.buttons.diggers.interactable)
                return;

            // Iterative, not recursive: with a large bank this could previously recurse once per level.
            for (int guard = 0; guard < 500; guard++)
            {
                if (_cheapestDigger == -1)
                    return;
                if (OptimizationAdvisor.GoldStarvedForAugs(_character, 2.0))
                    return;
                if (_dc.upgradeCost(_cheapestDigger) + Settings.MoneyPitThreshold > _character.realGold)
                    return;

                Log("Upgrading Digger " + _cheapestDigger);
                _dc.upgradeMaxLevel(_cheapestDigger);
                UpdateCheapestDigger();
            }
        }
    }
}
