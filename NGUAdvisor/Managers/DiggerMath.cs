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
    //  * MaxAffordableLevel — the highest level the budget affords, ported verbatim from the game's
    //    auto-level formula. The old even gps/count split collapsed every digger to level 1 on Evil
    //    (per-level drains dwarf gross/count); this is the number the greedy allocator sets per digger.
    //
    // The greedy LOOP stays in DiggerManager: it re-reads live drains between diggers (leveling one
    // changes the next one's available budget), which is inherently game-coupled and not pure.
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

        // The highest level whose gold drain the budget `cap` covers, clamped to [curLevel, maxLevel].
        // If the budget can't even cover level 1 (caller passes drainAtLevel1), keep curLevel. Pure port
        // of DiggerManager.SetLevelMaxAffordable's arithmetic; the overshoot revert (which reads the
        // whole active set's live drain) stays in the caller because it is game-coupled.
        public static long MaxAffordableLevel(double cap, double baseDrain, double growthRate,
                                              double drainAtLevel1, long curLevel, long maxLevel)
        {
            if (cap < drainAtLevel1)
                return curLevel;
            long lvl = (long)Math.Floor(Math.Log(cap / baseDrain, growthRate) + 1L);
            if (lvl < curLevel) lvl = curLevel;
            if (lvl > maxLevel) lvl = maxLevel;
            return lvl;
        }
    }
}
