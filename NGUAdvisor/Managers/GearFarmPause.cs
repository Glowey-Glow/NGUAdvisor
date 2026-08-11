using System;

namespace NGUAdvisor.Managers
{
    // THE GEAR-FARM CHALLENGE PAUSE — decision record amendment 25 §5, made urgent by amendment 26 §3.
    //
    // WHY THIS IS A GUARD ON LIVE BEHAVIOUR, NOT A GUARD ON A FUTURE FEATURE.
    // Amendment 25 §2 recorded GearFarmAdvisor's output as reaching "a text string and nothing else —
    // the same advise-don't-act shape as the NGU chip". Amendment 26 §3 checked and found that FALSE:
    // AdvisorApply.ApplyZones already writes Settings.SnipeZone from GearFarmAdvisor.Analyze(). So this
    // is not authorising a new writer, it is closing a missing gate on one that has been running all
    // along — without it, gear farming overrides the adventure zone in the middle of a challenge.
    //
    // LASER SWORD IS THE SOLE EXCEPTION, and it is exceptional in the game's own code, not by taste.
    // audit/21 §C4: LSC is the only one of the eleven whose Update() does not test bossID and the only
    // one with no targetBoss() at all — completion is augs[6].augLevel >= completions+2 AND
    // augs[6].upgradeLevel >= completions+2 ([DECOMP] LaserSwordChallengeController.cs:37-40, :79-92),
    // the only non-boss challenge goal (audit/23 §8 carries the same pair as CAPAUG-12 / CAPAUG-13).
    // Its restrictions are "Absolutely nothing!" and it resets nothing. It therefore does not contend
    // with gear farming for the adventure zone, and pausing the farm inside it would buy nothing.
    //
    // ⚠ THE BOSS RECOVERY HALF OF AMENDMENT 25 §5 IS DELIBERATELY ABSENT — BLOCKED, NOT FORGOTTEN.
    // §5's table lists two pauses. Only the challenge one is implemented here. Amendment 26 §2 decided
    // the existing RECOVERY segment would gain a second terminator (bossID >= CurrentHighestBoss); §2.2
    // item 1 then checked what RECOVERY actually terminates on and CLOSED AGAINST THAT DECISION:
    // ChallengeOverlay.cs:567 is `else if (numberCheap && runSec < 14400)`, i.e. a hard 4-hour time box
    // disjoined with `Bosses30 >= 2.0` — and Bosses30 is a projected RATE, a first derivative,
    // non-monotone, and NaN for the first 600 s of every run. A monotone watermark comparison is
    // additive with neither half. The two amendments disagree, amendment 26 §6 edit 5 was never
    // applied, and what replaces it is a design decision nobody has taken. MASTER RECORD:
    // campaign-advisor-spec §6.3. Do not infer a Boss Recovery pause from this file's existence.
    //
    // Unity-free on purpose — the ZoneGate / ConstraintParity pattern. The caller reads
    // ChallengeDetector.Current() (the ONE detector; do not write a second) and owns the surfacing
    // latch; every decision below is a pure function of the code handed in.
    public static class GearFarmPause
    {
        // ChallengeDetector.Current()'s code for the Laser Sword Challenge.
        public const string LaserSword = "LSC";

        // Verbatim from amendment 25 §5's "Surfaced as" column, kept as a constant so the string the
        // decision record specifies is provably the string the operator sees. Message() appends which
        // challenge caused it — the pause is one line every ten minutes at most, so identifying it
        // costs nothing and a bare "a challenge" would send the operator hunting.
        public const string PauseMessage = "Pausing Farm due to Active Challenge";
        public const string ResumeMessage = "Resuming Farm — no challenge active";

        // challengeCode: ChallengeDetector.Current() — null when no challenge is running.
        public static bool IsPaused(string challengeCode)
        {
            if (string.IsNullOrEmpty(challengeCode)) return false;
            return !string.Equals(challengeCode, LaserSword, StringComparison.Ordinal);
        }

        // The surfacing signature: the challenge that is holding the farm, or null when it runs.
        // Folding "no challenge" and "Laser Sword" onto the same null is the point — LSC is not a
        // pause, so entering it from another challenge is a RESUME and says so.
        public static string Signature(string challengeCode)
        {
            return IsPaused(challengeCode) ? challengeCode : null;
        }

        // STATE-CHANGE ONLY. ApplyZones' gear branch runs behind a 10-minute throttle rather than
        // ConstraintParity's ~1 Hz, but the reason for the latch is the same one and stronger: this is
        // a PHASE TRANSITION, not a metric, so there is nothing for a slow-refresh re-emit to report.
        // Repeating "Pausing Farm" every ten minutes for the length of a challenge would train the
        // operator to ignore the line — which is the failure this surfacing exists to prevent
        // (a farm that stops without saying so is indistinguishable from a farm that broke; the
        // 25 §4 failure mode, found at two hours' cost).
        public static bool ShouldSurface(string signature, string lastSignature)
        {
            return (signature ?? "") != (lastSignature ?? "");
        }

        // The line for a signature transition. null (running) => the resume line.
        public static string Message(string signature)
        {
            return string.IsNullOrEmpty(signature)
                ? ResumeMessage
                : PauseMessage + " (" + signature + ")";
        }

        // The overlay feed's reason column for the same transition.
        public static string Reason(string signature)
        {
            return string.IsNullOrEmpty(signature)
                ? "challenge cleared"
                : "gear farming does not run inside a challenge";
        }
    }
}
