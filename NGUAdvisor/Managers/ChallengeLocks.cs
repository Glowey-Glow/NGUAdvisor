namespace NGUAdvisor.Managers
{
    // The challenge-layer lane locks, as plain strings. Unity-free and linked into the test assembly,
    // because the WORDING is the thing under test here: F2 (39 §F2, amendment 31 §5) was a correct
    // refusal carrying a sentence that named the wrong cause, for the whole challenge.
    //
    // WHY THIS IS A ChallengeLock AND NOT A NEW PREDICATE. FeasibilityPass.cs:80-85 already specifies
    // this exact slot: "the Campaign Advisor sends a ChallengeLock for allocation-layer locks (No
    // Rebirth) AND effect-layer locks (No Equipment / No NGU / No Time Machine — the game accepts the
    // allocation and returns nothing, so they must be modelled as infeasible anyway)". No NGU is named
    // there by the spec. So the lock is the DOCUMENTED door and nothing in Pass 1 changes: ExternalGate
    // (FeasibilityPass.cs:103-112) already refuses on a non-null ChallengeLock and surfaces the
    // sender's reason verbatim, and it runs BEFORE TrollGate in every composite.
    //
    // ⚠ THE PROBLEM IT SOLVES — ONE FLAG, TWO CAUSES. NGU lanes are refused on character.NGU.disabled
    // (ConstraintLayerBridge.cs:300 -> FeasibilityPass.NguLane -> TrollGate). That flag is set by TWO
    // different things and the flag alone cannot tell you which:
    //     Troll challenge   [DECOMP] TrollChallengeController.cs:643
    //     NGU Challenge     [DECOMP] Rebirth.cs:523, inside engageNGUChallenge()
    // TrollGate's sentence — "trolled off: NGU.disabled is set (polled; clears at rebirth)" — is right
    // for the first and wrong in BOTH clauses for the second. So the cause must be resolved from
    // CHALLENGE STATE, not from the flag, and that resolution happens in the bridge, which is the only
    // layer that can see both. When this returns null the lane falls through to TrollGate unchanged and
    // the Troll keeps its own wording.
    public static class ChallengeLocks
    {
        // ⚠ MUST NOT contain "trolled" or "clears at rebirth". Both are false during the NGU Challenge
        // and both are asserted against in ChallengeLockTests.
        //
        // "no bonuses" is the game's own word: [DECOMP] NGUChallengeController.cs:289 —
        // "Restrictions: NGU's provide absolutely no bonuses!". It states an EFFECT, not a prohibition,
        // which is why this is a waste guard rather than a compliance gate (39 §R2.10). The deferred-
        // value nuance is carried because it is the only lane in 21 §D4's table for which it is true:
        // levels still tick and NGU levels survive rebirth.
        public const string NguChallenge =
            "NGU Challenge active — NGUs provide no bonuses for its duration " +
            "([DECOMP] NGUChallengeController.cs:289), so allocation here returns nothing now. " +
            "Levels still tick and survive rebirth, making this deferred value rather than pure waste. " +
            "Ends on completion or failure (NGUChallengeController.cs:146/:230) — not at rebirth.";

        // The NGU lane's challenge-layer lock, or null when no challenge locks it.
        //
        // Keyed on the CHALLENGE flag (challenges.nguChallenge.inChallenge, [DECOMP] Challenges.cs:24),
        // deliberately NOT on NGU.disabled — reading the effect flag here would reintroduce exactly the
        // ambiguity this exists to remove. Pure and unlatched: the caller re-reads live every tick
        // (constraint-layer-spec §4.5), so the lock lifts the instant the challenge ends.
        public static string NguLane(bool nguChallengeActive) => nguChallengeActive ? NguChallenge : null;
    }
}
