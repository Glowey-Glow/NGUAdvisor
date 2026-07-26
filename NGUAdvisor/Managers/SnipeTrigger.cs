namespace NGUAdvisor.Managers
{
    // The outcome of one gold-snipe re-trigger evaluation. SeedBaseline and Trigger are independent: a
    // reload-seed does NOT preclude the timer firing in the same pass (mirrors SetResnipe, where the seed
    // leaves `trigger` null so the timer check still runs).
    public struct SnipeResult
    {
        public bool SeedBaseline;   // adopt bestZone as the run's baseline silently (no snipe)
        public string Trigger;      // "new zone fightable" | "timer" | null
        public int NewZone;         // the zone that armed a new-zone re-trigger; -1 otherwise
    }

    // Unity-free gold-snipe re-trigger decision (audit M5 migration). Extracted from Main.SetResnipe so
    // the "one arm per zone" latch — the fix for the bug where a completed snipe was wiped every second
    // because any real zone read as "new zone fightable" — is locked by headless tests.
    //
    // The live reads (best zone, rebirth time), the static baseline/last-armed state, and the latch side
    // effects (clearing GoldSnipeComplete, logging) stay in Main; this is the pure decision only.
    public static class SnipeTrigger
    {
        public static SnipeResult Decide(bool armNewZone, bool hasBest, int bestZone,
                                         int furthestZone, int lastNewZoneTrigger, bool snipeComplete,
                                         bool allowTimer, bool timerHit)
        {
            var r = new SnipeResult { NewZone = -1 };

            if (armNewZone && hasBest)
            {
                // Reload seeding: the baseline is a static that comes back unknown (-1) after an advisor
                // reload. With the snipe already complete, adopt the current best zone silently instead
                // of firing — genuinely new unlocks still trigger from here on.
                if (furthestZone < 0 && snipeComplete)
                    r.SeedBaseline = true;
                // One arm per zone: a higher best zone that also exceeds the last armed zone. Without the
                // lastNewZoneTrigger latch, a gold loadout that can't clear the zone drops the ratchet
                // back and this re-fires every second, swapping gear forever.
                else if (furthestZone >= 0 && bestZone > furthestZone && bestZone > lastNewZoneTrigger)
                {
                    r.Trigger = "new zone fightable";
                    r.NewZone = bestZone;
                }
            }

            // Timer (manual only). Checked only when no new-zone trigger fired — but a seed leaves
            // Trigger null, so a seed does not suppress it (matches the original fall-through).
            if (r.Trigger == null && allowTimer && timerHit)
                r.Trigger = "timer";

            return r;
        }
    }
}
