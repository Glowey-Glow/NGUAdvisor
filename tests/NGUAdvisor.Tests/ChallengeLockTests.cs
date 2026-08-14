using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // F2 (39 §F2, amendment 31 §5): the NGU-lane refusal is CORRECT and always was — what was wrong is
    // the sentence it carried. One flag (character.NGU.disabled) is set by two different causes:
    //     Troll challenge   [DECOMP] TrollChallengeController.cs:643
    //     NGU Challenge     [DECOMP] Rebirth.cs:523
    // TrollGate's wording is right for the first and wrong in BOTH clauses for the second.
    //
    // These tests pin the disambiguation and, per the brief, assert on the STRING — a wrong reason on a
    // correct refusal is the shape this corpus keeps producing, and prose in a comment does not hold it.
    public class ChallengeLockTests
    {
        private const string TrollWording = "trolled off";
        private const string RebirthClause = "clears at rebirth";

        // ---- W2a: the two causes produce different locks -----------------------------------------

        [Fact]
        public void NguChallengeActive_ProducesALock()
        {
            Assert.NotNull(ChallengeLocks.NguLane(nguChallengeActive: true));
        }

        [Fact]
        public void NguChallengeInactive_ProducesNoLock_SoTheTrollKeepsItsOwnWording()
        {
            // The Troll sets the same flag. With no challenge lock the lane falls through to TrollGate,
            // which is the correct sentence for that cause.
            Assert.Null(ChallengeLocks.NguLane(nguChallengeActive: false));
        }

        // ---- W2b / W2d: neither false clause may appear on the NGU-Challenge path ----------------

        [Fact]
        public void NguChallengeLock_NeverClaimsItIsATroll()
        {
            Assert.DoesNotContain(TrollWording, ChallengeLocks.NguChallenge);
            Assert.DoesNotContain("Troll", ChallengeLocks.NguChallenge);
        }

        [Fact]
        public void NguChallengeLock_NeverClaimsItClearsAtRebirth()
        {
            // It clears at NGUChallengeController.cs:146 (complete) and :230 (failed). resetTrolls()
            // runs only under trollChallenge.inChallenge ([DECOMP] Character.cs:929-932), so a rebirth
            // during the NGU Challenge does NOT clear it.
            Assert.DoesNotContain(RebirthClause, ChallengeLocks.NguChallenge);
        }

        [Fact]
        public void NguChallengeLock_NamesTheChallengeAndTheEffect()
        {
            // "A lane going quiet is indistinguishable from a lane that broke" (25 §4) — the reason has
            // to name the cause, not merely avoid the wrong one.
            Assert.Contains("NGU Challenge", ChallengeLocks.NguChallenge);
            Assert.Contains("no bonuses", ChallengeLocks.NguChallenge);
        }

        // ---- the same assertions through the real Pass 1 composite -------------------------------
        // FeasibilityPass is linked, so this exercises the ACTUAL gate order rather than a re-statement
        // of it: ExternalGate runs before TrollGate in NguLane, which is what makes the Troll wording
        // unreachable on the NGU-Challenge path.

        private static FeasibilityPass.ExternalConstraints Ext(string challengeLock) =>
            new FeasibilityPass.ExternalConstraints { ChallengeLock = challengeLock };

        [Fact]
        public void NguLane_InTheNguChallenge_RefusesWithTheChallengeReason()
        {
            var v = FeasibilityPass.NguLane(
                nguDisabled: true,
                external: Ext(ChallengeLocks.NguLane(nguChallengeActive: true)));

            Assert.False(v.Seated);
            Assert.Contains("NGU Challenge", v.Reason);
            Assert.DoesNotContain(TrollWording, v.Reason);
            Assert.DoesNotContain(RebirthClause, v.Reason);
        }

        [Fact]
        public void NguLane_UnderTheTroll_KeepsTheTrollReason()
        {
            // Troll: flag set, no challenge lock. This must NOT have been collateral damage.
            var v = FeasibilityPass.NguLane(
                nguDisabled: true,
                external: Ext(ChallengeLocks.NguLane(nguChallengeActive: false)));

            Assert.False(v.Seated);
            Assert.Contains(TrollWording, v.Reason);
        }

        // ---- W2c: no cached state, in either direction -------------------------------------------

        [Fact]
        public void NguLane_SeatsAgainTheMomentBothClear_NoCachedState()
        {
            var v = FeasibilityPass.NguLane(
                nguDisabled: false,
                external: Ext(ChallengeLocks.NguLane(nguChallengeActive: false)));

            Assert.True(v.Seated);
        }

        [Fact]
        public void NguLane_FlippedAcrossASequence_TracksLiveStateEveryTime()
        {
            // The challenge ends and the lanes must come back with nothing remembered. Flipping mid
            // sequence is the actual regression risk: ResourceBreakpoint froze hack ordering at parse
            // time and ChallengeOverlay cached its parsed list for a session (FeasibilityPass.cs:23-28).
            for (int i = 0; i < 3; i++)
            {
                var inChallenge = FeasibilityPass.NguLane(true, Ext(ChallengeLocks.NguLane(true)));
                Assert.False(inChallenge.Seated);
                Assert.Contains("NGU Challenge", inChallenge.Reason);

                var cleared = FeasibilityPass.NguLane(false, Ext(ChallengeLocks.NguLane(false)));
                Assert.True(cleared.Seated);
            }
        }

        [Fact]
        public void ChallengeLock_IsRefusedAheadOfTheFlag_SoTheCauseCannotBeMisreported()
        {
            // The ordering guarantee itself: even with the effect flag CLEAR, an active challenge lock
            // refuses — and with both set the challenge reason wins, never TrollGate's.
            var lockOnly = FeasibilityPass.NguLane(
                nguDisabled: false, external: Ext(ChallengeLocks.NguLane(true)));

            Assert.False(lockOnly.Seated);
            Assert.DoesNotContain(TrollWording, lockOnly.Reason);
        }
    }
}
