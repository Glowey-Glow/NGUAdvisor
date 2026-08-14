using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // F3 (39 §F3, amendment 31 §5): during No Equipment, gear contributes 0f to every spec
    // ([DECOMP] InventoryController.cs:647, the zero returned before any spec type is examined) while
    // every swap still pays removeAllEnergyAndMagic() across eight systems (LoadoutManager.cs:85).
    // The advisor was churning loadouts for a bonus pinned to zero.
    //
    // The gate is one predicate at one entry point. These tests pin the three things that make that
    // safe: the advisor is stopped, the restores are NOT, and nothing is remembered across the edge.
    public class GearChangeGateTests
    {
        // ---- the gate itself ----------------------------------------------------------------------

        [Fact]
        public void AdvisorSwaps_AreBlocked_DuringNoEquipment()
        {
            Assert.True(GearChangeGate.Blocks(inNoec: true, cause: GearChangeGate.Cause.Advisor));
        }

        [Fact]
        public void AdvisorSwaps_AreAllowed_OutsideNoEquipment()
        {
            Assert.False(GearChangeGate.Blocks(inNoec: false, cause: GearChangeGate.Cause.Advisor));
        }

        [Fact]
        public void TheDefaultCause_IsTheGatedOne()
        {
            // LoadoutManager.ChangeGear(ids) forwards to Cause.Advisor, so a caller that says nothing
            // is gated rather than exempt. If this enum is ever reordered so that default(Cause) stops
            // meaning Advisor, an unnamed caller would silently become exempt.
            Assert.Equal(GearChangeGate.Cause.Advisor, default(GearChangeGate.Cause));
        }

        // ---- W3c: the restore exemption ------------------------------------------------------------
        // RestoreGear (LoadoutManager.cs:42) and RestoreTempLoadout (:563) UNDO a swap. Gating them
        // would strand whatever was equipped when the challenge began, with no way back — a worse
        // failure than the churn the gate exists to prevent.

        [Fact]
        public void Restores_StillWork_DuringNoEquipment()
        {
            Assert.False(GearChangeGate.Blocks(inNoec: true, cause: GearChangeGate.Cause.Restore));
        }

        [Fact]
        public void Restores_StillWork_OutsideNoEquipment()
        {
            Assert.False(GearChangeGate.Blocks(inNoec: false, cause: GearChangeGate.Cause.Restore));
        }

        // ---- W3d: the user hotkey, deliberately undecided ------------------------------------------

        // ---- V2: the Quick Loadout hotkey, now decided ---------------------------------------------
        // This assertion was previously the inverse, pinning the deliberately-undecided pass-through.
        // [OPERATOR] has since ruled: during No Equipment the keypress is IGNORED, with a log line.

        [Fact]
        public void QuickLoadoutHotkey_IsIgnored_DuringNoEquipment()
        {
            Assert.True(GearChangeGate.Blocks(inNoec: true, cause: GearChangeGate.Cause.UserHotkey));
        }

        [Fact]
        public void QuickLoadoutHotkey_StillWorks_OutsideNoEquipment()
        {
            Assert.False(GearChangeGate.Blocks(inNoec: false, cause: GearChangeGate.Cause.UserHotkey));
        }

        [Fact]
        public void AnIgnoredKeypress_AlwaysGetsALine_NeverThrottled()
        {
            // ⚠ THE POINT OF THE RULING. TransitionLine is once-per-edge; this must NOT be, because a
            // silent second press is indistinguishable from a broken hotkey. The function carries no
            // state, so ten presses produce ten identical lines.
            var first = GearChangeGate.IgnoredHotkeyLine(true, GearChangeGate.Cause.UserHotkey);
            Assert.NotNull(first);

            for (int i = 0; i < 10; i++)
                Assert.Equal(first, GearChangeGate.IgnoredHotkeyLine(true, GearChangeGate.Cause.UserHotkey));
        }

        [Fact]
        public void TheIgnoredKeypressLine_SaysNothingIsBroken()
        {
            var line = GearChangeGate.IgnoredHotkeyLine(true, GearChangeGate.Cause.UserHotkey);
            Assert.Contains("Quick Loadout ignored", line);
            Assert.Contains("nothing is broken", line);
        }

        [Fact]
        public void NoIgnoredLine_ForTheAdvisorOrForRestores_OrOutsideTheChallenge()
        {
            // The advisor's churn is narrated once per transition, not per attempt, and a restore is
            // not ignored at all — neither may borrow the keypress wording.
            Assert.Null(GearChangeGate.IgnoredHotkeyLine(true, GearChangeGate.Cause.Advisor));
            Assert.Null(GearChangeGate.IgnoredHotkeyLine(true, GearChangeGate.Cause.Restore));
            Assert.Null(GearChangeGate.IgnoredHotkeyLine(false, GearChangeGate.Cause.UserHotkey));
        }

        [Fact]
        public void OnlyRestoreIsExempt_SoANewCauseWouldBeGatedByDefault()
        {
            // Blocks() is written as `!= Cause.Restore`, not as a list of blocked causes. If someone
            // adds a fourth cause, it is gated until they deliberately exempt it.
            foreach (GearChangeGate.Cause cause in System.Enum.GetValues(typeof(GearChangeGate.Cause)))
            {
                bool expected = cause != GearChangeGate.Cause.Restore;
                Assert.Equal(expected, GearChangeGate.Blocks(inNoec: true, cause: cause));
            }
        }

        // ---- W3e: the surfacing fires on state change and does not spam ----------------------------

        [Fact]
        public void EnteringNoEquipment_NarratesOnce()
        {
            var first = GearChangeGate.TransitionLine(inNoec: true, alreadySurfaced: false);
            Assert.NotNull(first);
            Assert.Contains("No Equipment Challenge", first);

            // Every subsequent attempted swap inside the challenge says nothing.
            Assert.Null(GearChangeGate.TransitionLine(inNoec: true, alreadySurfaced: true));
        }

        [Fact]
        public void LeavingNoEquipment_NarratesTheResume()
        {
            var resumed = GearChangeGate.TransitionLine(inNoec: false, alreadySurfaced: true);
            Assert.NotNull(resumed);
            Assert.Contains("resume", resumed);
        }

        [Fact]
        public void OutsideTheChallenge_WithNothingLatched_SaysNothing()
        {
            Assert.Null(GearChangeGate.TransitionLine(inNoec: false, alreadySurfaced: false));
        }

        [Fact]
        public void TheRefusalLine_SaysWhyAndSaysWhatStillWorks()
        {
            // "A swap silently not happening is indistinguishable from a swap that failed." The line
            // has to carry the cause AND tell the user their restore/hotkey are not broken.
            var line = GearChangeGate.TransitionLine(inNoec: true, alreadySurfaced: false);
            Assert.Contains("0f", line);
            Assert.Contains("Restores", line);
        }

        // ---- no cached state across the edge -------------------------------------------------------

        [Fact]
        public void TheGateClearsWithTheChallenge_NoCachedState()
        {
            // Flip repeatedly: the gate is a pure function of live state, so a lane that was blocked
            // must not stay blocked once the challenge ends — the failure mode FeasibilityPass.cs:23-28
            // records twice in this tree.
            for (int i = 0; i < 3; i++)
            {
                Assert.True(GearChangeGate.Blocks(true, GearChangeGate.Cause.Advisor));
                Assert.False(GearChangeGate.Blocks(false, GearChangeGate.Cause.Advisor));
            }
        }

        [Fact]
        public void NarrationLatch_RoundTripsAcrossBothEdges()
        {
            // Simulates the caller's latch over enter -> stay -> leave -> stay-out, which is the
            // sequence the once-per-transition rule actually has to survive.
            bool latch = false;
            var enter = GearChangeGate.TransitionLine(true, latch);
            latch = true;
            Assert.NotNull(enter);

            Assert.Null(GearChangeGate.TransitionLine(true, latch));

            var leave = GearChangeGate.TransitionLine(false, latch);
            latch = false;
            Assert.NotNull(leave);

            Assert.Null(GearChangeGate.TransitionLine(false, latch));
        }
    }
}
