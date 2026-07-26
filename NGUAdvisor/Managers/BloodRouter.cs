using System;

namespace NGUAdvisor.Managers
{
    public enum BloodRoute { Idle, NumberFloor, Gold, Loot, NumberDefault }

    // Unity-free single-sink blood routing priority (audit M5 migration). The game splits blood evenly
    // among enabled sinks, so exactly one is chosen; the three aren't commensurable:
    //   NUMBER — LINEAR, uncapped rebirth-power bank. The "Number >=" knob is a FLOOR: while rebirthPower
    //            is below it, NUMBER outranks the in-run sinks (the shipped bug treated it as a ceiling
    //            and capped a linear multiplier). NUMBER is also the DEFAULT sink when nothing else fits.
    //   Gold / Loot — in-run LOG sinks, valid only while the investment window is open.
    //   Idle — only when there is no rebirth to bank a NUMBER multi into (NORB / none scheduled).
    //
    // Predicates are lazy on purpose so this preserves the caller's exact evaluation: the floor is decided
    // WITHOUT touching the window; the window is read at most once (caller memoizes it); the loot read is
    // only probed if gold declines; and the gold/loot reads carry out-param reason strings the caller
    // needs. This is the decision only — the caller maps the result to the WantGold/WantLoot/WantRebirth
    // flags and builds the reason text from values it already has.
    public static class BloodRouter
    {
        public static BloodRoute DecideRoute(bool numberEligible, double numberFloor, double rebirthPower,
                                             Func<bool> windowOpen, Func<bool> goldAvailable, Func<bool> lootAvailable)
        {
            if (numberEligible && numberFloor > 0 && rebirthPower < numberFloor)
                return BloodRoute.NumberFloor;

            bool win = windowOpen();
            if (win && goldAvailable())
                return BloodRoute.Gold;
            if (win && lootAvailable())
                return BloodRoute.Loot;

            if (numberEligible)
                return BloodRoute.NumberDefault;
            return BloodRoute.Idle;
        }
    }
}
