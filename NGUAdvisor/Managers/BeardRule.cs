namespace NGUAdvisor.Managers
{
    // "May beards be worn at all right now?" — a CHALLENGE rule, decided from the challenge code
    // alone. Unity-free so it can be linked into the headless tests, in the ZoneGate / GearFarmPause
    // shape: ChallengeDetector.Current() reaches Main.Character and cannot link, but the DECISION is
    // one string in and one bool out, and the decision is the whole of the fix.
    //
    // THE GAME DOES NOT ENFORCE THIS. `beards.disabled` ([DECOMP] Beards.cs:17) is written from
    // exactly two sites, both Troll: TrollChallengeController.cs:650 sets it, :326 clears it. Nothing
    // in LevelChallenge10KController touches beards. So the 100 Level Challenge's no-beards rule is
    // an UNENFORCED rule — the same shape as No Augs, where the game's only enforcement is a greyed
    // out menu button and the advisor honours the stated rule anyway (amendment 30, reversing D1).
    //
    // WHERE THE RULE IS WRITTEN DOWN. CBlock3.1-E100LC.json says it three times — the _Preset line
    // ("with beards off"), the _Injector line ("Disable all beards and stat advancement"), and the
    // breakpoint itself, `"Beards": [{ "List": [], "Comment": "No beards" }]`. Splitting Challenge
    // Block 3 into the 3.1 (100LC) and 3.2 legs is what made that expressible per-leg at all; 3.2
    // carries a real list, [0,4,2,6,5].
    //
    // ⚠ WHY THE PROFILE COULD NOT CARRY IT ALONE. CustomAllocation.cs:239 runs the profile's beard
    // timeline only `if (Settings.ManageBeards && !Settings.AdvisorBeards …)` — the profile path and
    // the advisor path are MUTUALLY EXCLUSIVE, not layered. With the beards advisor on, Swap() is
    // never called and the empty list is unreachable, so a data-only rule is silently unenforceable
    // in the configuration most runs actually use.
    public static class BeardRule
    {
        // Explicit "wear none", as distinct from a null set's "no opinion — leave what is equipped
        // alone". THE DISTINCTION IS THE FIX: AdvisorApply.ApplyBeards returns early on a null set,
        // so an advisor that merely abstains during 100LC leaves the seven beards it equipped before
        // the challenge started still on. Forbidding has to be a positive instruction to clear.
        public static readonly int[] None = new int[0];

        // Challenge codes are ChallengeDetector's vocabulary (BASIC, NOAUG, 24HR, 100LC, NOEC, TC,
        // NORB, LSC, BLIND, NONGU, NOTM); null means "not in a challenge". Only 100LC forbids beards
        // — Troll takes them away by itself and needs no rule, and no other challenge names them.
        public static bool Forbidden(string challengeCode) => challengeCode == "100LC";

        // What a writer should actually equip, given what it wanted. Returns None under the rule and
        // the caller's own set otherwise, so the rule can be applied where INTENT is formed (keeping
        // the log line honest) as well as at the choke point in BeardManager (making it a rule rather
        // than a convention).
        public static int[] Apply(string challengeCode, int[] wanted) =>
            Forbidden(challengeCode) ? None : wanted;
    }
}
