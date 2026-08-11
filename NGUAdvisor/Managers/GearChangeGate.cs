namespace NGUAdvisor.Managers
{
    // F3 (39 §F3, amendment 31 §5): gear swaps during the No Equipment Challenge are pure cost.
    //
    // THE MECHANIC. [DECOMP] InventoryController.cs:644-650:
    //     public float equipSpecBonus(specType type, Equipment equip)
    //     {
    //         float num = 0f;
    //         if (character.challenges.noEquipmentChallenge.inChallenge) return num;   // :647
    // The zero is at the TOP, before any spec type is examined. specBonus(type) (:679-684) sums
    // equipSpecBonus across weapon, head, chest, legs, boots, weapon2 and every accessory, and :3403 /
    // :3442 confirm EnergyCap and MagicCap are ordinary specTypes routed through that same function.
    // So during No Equipment, gear contributes 0f to EVERY spec — energy cap, magic cap, power, bars.
    // There is no exception path. (The cube goes too: cubePower/cubeToughness and both softcaps return
    // 0f at :554/:568/:577/:591.)
    //
    // AND EVERY SWAP COSTS. LoadoutManager.ChangeGear calls _character.removeAllEnergyAndMagic()
    // (LoadoutManager.cs:85), zeroing energy/magic/R3 across eight systems until the next allocation
    // pass — AdvisorApply.cs:858's own comment puts that at "up to 10s of nothing across eight
    // systems", and 38 §E2 traced it. The advisor was therefore paying a full eight-system allocation
    // reset, repeatedly, to equip items whose entire contribution is pinned to 0f.
    //
    // ⚠ NOT A RULE VIOLATION. The No Equipment string forbids nothing — it states an effect, and the
    // game applies that effect either way (39 §R2.5). This is a WASTE guard, exactly like the NGU
    // Challenge lock, not a compliance gate. 21 §C2 predicted this shape: "a gear optimizer scoring on
    // curAttack will happily churn loadouts for zero effect."
    //
    // WHY THE DECISION LIVES HERE. The rule is enforced at ONE predicate, called from the ONE entry
    // point (LoadoutManager.ChangeGear), rather than at the fifteen external call sites — 38 §E7's
    // pool-filter lesson inverted: a rule enforced at a predicate cannot be forgotten by a caller, and
    // does not rot when a sixteenth site is added. Unity-free so the decision is testable; the live
    // NOEC read stays in the caller.
    public static class GearChangeGate
    {
        // WHY AN ENUM AND NOT A bool. The two restore paths must still work (below), and the shape of
        // that exception matters: a bare `bool skipGate` is settable by accident and reads as noise at
        // the call site, while inspecting the caller (a stack walk, or matching on a name) is exactly
        // the fragile coupling this class exists to avoid. A named cause is checkable, greppable, and
        // forces a new caller to say what it is — and the DEFAULT path through ChangeGear is Advisor,
        // so a caller that says nothing is gated.
        public enum Cause
        {
            // The advisor deciding to swap on its own initiative: profile gear breakpoints, the
            // optimizer, and every LockManager objective swap. The only cause that is gated.
            Advisor = 0,

            // UNDOING a swap — LoadoutManager.RestoreGear (:42) and RestoreTempLoadout (:563).
            // ⚠ MUST NEVER BE GATED. These put back what was equipped before the advisor touched it.
            // Blocking them would STRAND the challenge-entry loadout with no way back, which is a worse
            // failure than the churn: the cost being avoided is one allocation reset, and the cost of
            // being wrong here is the user's gear left wherever a swap happened to leave it.
            Restore = 1,

            // Main.cs's F8 Quick Loadout hotkey — a deliberate user action.
            // [OPERATOR] RULING: during No Equipment this is IGNORED, with a log line. It is gated
            // like Advisor, but it is NOT the same cause and must stay distinct: the user pressed a
            // key and nothing happened, so it owes them a sentence every single time (see
            // IgnoredHotkeyLine), whereas the advisor's own churn is narrated once per transition.
            UserHotkey = 2,
        }

        // The gate. True = do not swap.
        //
        // Note what is NOT here: no check that the requested gear differs from the current gear, and no
        // knowledge of what is being equipped. ChangeGear's own set-comparison early-out
        // (LoadoutManager.cs:50) already handles the no-op case and runs first; this answers only
        // "should the advisor be moving gear at all right now".
        // ⚠ THE EXEMPTION IS Restore ALONE, expressed as `!= Cause.Restore` rather than as a list of
        // blocked causes. A new cause added to the enum is therefore GATED by default during No
        // Equipment, and has to argue its way out — the same fail-closed posture as Cause.Advisor
        // being default(Cause). Restore is exempt because blocking it strands the challenge-entry
        // loadout with no way back.
        public static bool Blocks(bool inNoec, Cause cause) => inNoec && cause != Cause.Restore;

        // The surfacing line, or null when there is nothing to say. Pure, so the wording and the
        // once-per-transition rule are testable; the LATCH is session state and lives in the caller —
        // the same split as AugmentMath.NoAugsSurfacingLine (:242-252) and the ZoneGate / ConstraintParity
        // throttles. "A swap silently not happening is indistinguishable from a swap that failed."
        //
        // Fires on BOTH edges. The resume line matters as much as the refusal: without it the feed shows
        // gear activity stopping and never says it started again.
        public static string TransitionLine(bool inNoec, bool alreadySurfaced)
        {
            if (inNoec && !alreadySurfaced)
                return "No Equipment Challenge — gear swaps suspended. Equipment contributes 0f to " +
                       "every spec for the challenge's duration ([DECOMP] InventoryController.cs:647), " +
                       "so a swap would buy nothing and cost a full energy/magic/R3 reset across eight " +
                       "systems. Restores and the Quick Loadout hotkey still work.";

            if (!inNoec && alreadySurfaced)
                return "No Equipment Challenge over — gear swaps resume.";

            return null;
        }

        // The IGNORED-KEYPRESS line, or null when this is not an ignored keypress.
        //
        // ⚠ DELIBERATELY NOT THROTTLED, and that is the whole point of it being a separate function
        // from TransitionLine. A keypress is a discrete user action: the user pressed F8, the gear did
        // not change, and without a line every time that is indistinguishable from a broken hotkey.
        // TransitionLine's once-per-edge rule is right for the advisor's own churn — it fires without
        // anyone asking — and wrong here, where a silent second press reads as a bug.
        //
        // The caller must NOT latch this. It carries no state by construction so it cannot be latched
        // by accident.
        public static string IgnoredHotkeyLine(bool inNoec, Cause cause)
        {
            if (!inNoec || cause != Cause.UserHotkey) return null;

            return "Quick Loadout ignored — the No Equipment Challenge is active, so gear is " +
                   "irrelevant: equipment contributes 0f to every spec until the challenge ends " +
                   "([DECOMP] InventoryController.cs:647). Nothing was swapped and nothing is broken; " +
                   "press again once the challenge is over.";
        }
    }
}
