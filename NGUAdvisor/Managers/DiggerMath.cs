using System;
using System.Collections.Generic;
using System.Linq;

namespace NGUAdvisor.Managers
{
    // Unity-free digger decision math (audit M5 migration). The two pieces of DiggerManager that had
    // shipped bugs and don't need the live game to compute:
    //
    //  * OrderByPriority — the greedy PRIORITY ordering RecapDiggers levels in. A stale/empty order
    //    (Bug3) fed the leveler the wrong ranking, so the cubic Stats digger got leveled LAST on
    //    leftover budget during a tight Evil push. This is that exact ordering, now headless-tested.
    //  * AllocateLevels — the running level for EVERY active digger, chosen together under one drain
    //    budget. This replaced a per-digger "most it can afford out of the remainder" rule that handed
    //    86% of the gold economy to whichever digger sorted first and left the tail thrashing between
    //    level 1 and level 10 tick to tick. The whole allocation is now pure and headless-testable;
    //    DiggerManager only reads the game's tables and writes the resulting levels back.
    public static class DiggerMath
    {
        // Elements of `items` reordered to match `priority`'s order, with items not in `priority`
        // appended in their original order; duplicates removed. priority == null => items unchanged.
        // (Same expression the OrderFrom extension used, relocated here so it is unit-testable.)
        public static IEnumerable<T> OrderByPriority<T>(IEnumerable<T> items, IEnumerable<T> priority)
        {
            if (priority == null)
                return items;
            return priority.Concat(items).Intersect(items);
        }

        // One digger's inputs to the value-ranked allocation. Every field except Weight is read live off
        // AllGoldDiggerController (baseGPSDrain / gpsGrowthRate / startingBoost / boostPerLevel are all
        // public Lists on it), so nothing here is a transcribed constant that can drift from the game.
        public struct DiggerSpec
        {
            public int Id;
            public double BaseDrain;       // baseGPSDrain[id]
            public double Growth;          // gpsGrowthRate[id]
            public long MaxLevel;          // diggers[id].maxLevel — the gold-bought ceiling
            public double StartingBoost;   // startingBoost[id]
            public double BoostPerLevel;   // boostPerLevel[id]
            public bool Cubic;             // level enters the bonus as level^3 (digger 2, Stats)
            public double Weight;          // policy multiplier; 1.0 = "value every digger's growth alike"
        }

        // Gold-per-second drain of running this digger at `level`. drain(0) = 0; drain(L) = base*g^(L-1),
        // matching AllGoldDiggerController.drain (which short-circuits to 0 at curLevel <= 0).
        public static double DrainAt(DiggerSpec s, long level)
            => level <= 0 ? 0.0 : s.BaseDrain * Math.Pow(s.Growth, level - 1);

        // The digger's own multiplier at `level`, matching the game's bonus getters:
        //   linear: 1 + (startingBoost + level*boostPerLevel)
        //   cubic : 1 + (startingBoost + level^3*boostPerLevel)   (digger 2 / Stats, AGDC.totalStatBonus)
        // The game also multiplies by totalLevelBonus(), but that is a function of the SUM of maxLevel
        // across all twelve diggers — a factor common to every digger and independent of the running
        // levels this allocator chooses, so it cancels out of every comparison below and is omitted.
        public static double BonusAt(DiggerSpec s, long level)
        {
            double lvlTerm = s.Cubic ? Math.Pow(level, 3.0) : level;
            double bonus = 1.0 + (s.StartingBoost + lvlTerm * s.BoostPerLevel);
            return bonus < 1.0 ? 1.0 : bonus;      // the game floors each bonus at 1.0
        }

        // Distribute running levels across the active diggers to maximise total value under a drain budget.
        //
        // WHY THIS REPLACED THE SEQUENTIAL ALLOCATOR. The old rule walked the priority list and gave each
        // digger MaxAffordableLevel against everything the budget had left, so position #1 took everything
        // it could afford and the rest divided the remainder. Measured on a live Evil save: the top three
        // diggers took 99.66% of gross (86.1% / 11.3% / 2.2%) and the remaining eight shared 0.34%, several
        // of them pinned at level 1. That is not a priority ordering doing its job — it is an artifact of
        // composing a MEMBERSHIP ranking with a residual allocator, and it made the marginal value of a
        // level differ across diggers by six orders of magnitude.
        //
        // It also caused the visible level thrash. Each digger's budget was `gross - sum(drains above it)`,
        // a catastrophic cancellation whose relative error is amplified by gross/remaining — around 1e6 by
        // the last digger — so the ~0.03%/min drift in gross swung the tail through ~10 levels a tick, and
        // every level the mid-table gained wiped the bottom of the list.
        //
        // The rule here is the standard one: buy the single cheapest-per-unit-value level available
        // anywhere in the set, repeatedly, until nothing else fits. Because each digger's cost grows
        // geometrically while its log-bonus gain shrinks, every digger's value/cost ratio is strictly
        // decreasing, and greedy on a decreasing ratio is exactly the marginal-value-equalising optimum —
        // no shadow price to calibrate and no weights required to make it well-defined.
        //
        // Value is measured as the change in LOG bonus because the bonuses multiply into different
        // products (adventure power, raw stats, NGU speed, EXP...). Log makes "+10% of what this digger
        // already gives" comparable across all of them; Weight is the deliberate policy knob on top, and
        // defaults to 1.0 everywhere so the default behaviour states no opinion about which system matters.
        //
        // A linear scan per level beats a heap here: the set is at most twelve diggers, so this is a few
        // thousand comparisons per recap.
        public static long[] AllocateLevels(double budget, DiggerSpec[] specs)
        {
            if (specs == null || specs.Length == 0)
                return new long[0];

            var levels = new long[specs.Length];
            double spent = 0.0;

            // FLOOR. An active digger runs at level 1 or not at all — the game has no level-0 active
            // state — and membership is not this function's job, so every digger with a bought maxLevel
            // starts at 1 and its drain is charged first. If the floor alone exceeds the budget the
            // caller is over-committed on membership; levels still come back at 1 (the old code did the
            // same) and the loop below simply buys nothing.
            for (int i = 0; i < specs.Length; i++)
            {
                levels[i] = specs[i].MaxLevel > 0 ? 1L : 0L;
                spent += DrainAt(specs[i], levels[i]);
            }

            // Guard against a pathological table (zero/negative growth) turning this into a spin.
            long guard = 0, guardMax = 200000;
            while (++guard < guardMax)
            {
                int best = -1;
                double bestRatio = 0.0, bestCost = 0.0;

                for (int i = 0; i < specs.Length; i++)
                {
                    if (levels[i] <= 0 || levels[i] >= specs[i].MaxLevel)
                        continue;

                    double cost = DrainAt(specs[i], levels[i] + 1) - DrainAt(specs[i], levels[i]);
                    if (cost <= 0.0 || spent + cost > budget)
                        continue;

                    double gain = Math.Log(BonusAt(specs[i], levels[i] + 1)) - Math.Log(BonusAt(specs[i], levels[i]));
                    gain *= specs[i].Weight;
                    if (gain <= 0.0)
                        continue;

                    double ratio = gain / cost;
                    if (ratio > bestRatio)
                    {
                        bestRatio = ratio;
                        best = i;
                        bestCost = cost;
                    }
                }

                if (best < 0)
                    break;

                levels[best]++;
                spent += bestCost;
            }

            return levels;
        }

        // MaxAffordableLevel (and DiggerManager.SetLevelMaxAffordable, its only caller) were removed when
        // AllocateLevels replaced the sequential residual allocator: "the most THIS digger can afford out
        // of what's left" is precisely the question that produced the 86%-to-one-digger split, so there
        // is no correct caller for it any more.
        //
        // Two of its unit tests were also removed rather than kept green, because both exercised inputs
        // production could not supply: one passed drainAtLevel1 = 200 when the caller always computed
        // that argument while curLevel was 0 (and AllGoldDiggerController.drain short-circuits to 0.0 at
        // curLevel <= 0, so it was ALWAYS 0.0), and one passed curLevel = 10 when the caller had just
        // reset every digger to 1. Green tests over a dead parameter subspace are worse than no tests.
    }
}
