using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // CHARACTERISATION tests for LaneTargets (extraction step 3).
    //
    // Report 02 §D2-23 recorded "all 11 resource lanes + the share formula + the token parser" as
    // untested. These are the first tests over the lanes' TargetMet() surface. They also assert the
    // inventory TABLE against the predicates, so the "which lanes can never report done" count that
    // amendment 01 §P1 depends on cannot drift out of sync with the code again.
    public class LaneTargetsTests
    {
        // ---------------- the inventory table ----------------

        // ELEVEN rows over TEN classes. The row identity is (Lane, Pool), not Lane, because
        // TimeMachineBP is two consumers (amendment 05 §4) — see Table_TimeMachineIsTwoRows below.
        [Fact]
        public void Table_CoversTenLaneClassesAsElevenConsumerRows()
        {
            Assert.Equal(11, LaneTargets.Table.Length);
            Assert.Equal(10, LaneTargets.Table.Select(t => t.Lane).Distinct().Count());
            Assert.Equal(11, LaneTargets.Table.Select(t => t.Lane + "/" + t.Pool).Distinct().Count());
        }

        [Fact]
        public void Table_EveryRowNamesItsPool()
        {
            foreach (var t in LaneTargets.Table)
                Assert.Contains(t.Pool, new[] { "Energy", "Magic", "R3", "Energy|Magic" });
        }

        // THE P1 LEDGER. The inventory started at FIVE lanes hardcoding `TargetMet() => false` — 05
        // §6.3 enumerated four (WandoosBP, BasicTrainingBP, RitualBP, BestAug) and omitted BR, which
        // is `=> false` too; amendment 01 §P1 had the right list. Each lane wired to the game's own
        // terminator comes off this list, and this assertion is what makes that deliberate rather than
        // incidental. Wired (amendment 16 §7): BestAug, BasicTrainingBP.
        //
        // THE THREE THAT REMAIN ARE NOT AWAITING WORK. Each was checked by exhaustive search of the
        // decomp and each lane's TargetMet() carries the citation: no `target` occurs anywhere in the
        // two Wandoos files, nor in any of the four blood-magic files. Amendment 16 §7's "only
        // WandoosBP's false is faithful" is one lane short in the other direction — it names
        // `ritualsUnlocked` as RitualBP's and BR's discarded signal, but that is a LOCK the advisor
        // already applies in Unlocked(), not a target. THREE of the five are faithful, not one.
        //
        // So this list shrinking again is a red flag, not progress. Anything removed from it needs a
        // named game predicate, not a synthesised one.
        [Fact]
        public void Table_TheHardcodedFalseLedgerShrinksOnlyDeliberately()
        {
            var never = LaneTargets.HardcodedFalseLanes.Select(t => t.Lane).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "BR", "RitualBP", "WandoosBP" }, never);
        }

        // The residue is exactly the magic side plus Wandoos, and that is the shape amendment 16 §4
        // depends on: the ENERGY pool has exactly one unterminated consumer (Wandoos), which is why
        // "the smallest set of consumers needing a common value unit" comes out at zero. A second
        // unterminated energy lane would re-open that. Pinned so a future wiring pass notices.
        [Fact]
        public void Table_TheEnergyPoolHasExactlyOneUnterminatedConsumer()
        {
            var unterminatedEnergy = LaneTargets.Table
                .Where(t => t.Kind == LaneTargets.TargetKind.HardcodedFalse && t.Pool.Contains("Energy"))
                .Select(t => t.Lane).ToArray();
            Assert.Equal(new[] { "WandoosBP" }, unterminatedEnergy);
        }

        [Fact]
        public void Table_HardcodedFalseLanesReadNothingAndHaveNoNeverFundMarker()
        {
            foreach (var t in LaneTargets.HardcodedFalseLanes)
            {
                Assert.Equal("—", t.Reads);
                Assert.False(t.NeverFundMarker);
            }
        }

        [Fact]
        public void Table_ThresholdLanesAllNameTheFieldTheyRead()
        {
            foreach (var t in LaneTargets.Table.Where(x => x.Kind != LaneTargets.TargetKind.HardcodedFalse))
                Assert.NotEqual("—", t.Reads);
        }

        // 05 §6.4 left "is TM one consumer or two?" undecided in code and the table used to record that
        // with a PoolDependent flag on a single merged row. Amendment 05 §4 decided it: TWO consumers,
        // separate value models, satisfied independently. The flag is gone and the split is structural.
        [Fact]
        public void Table_TimeMachineIsTwoRowsOnePerPoolReadingDifferentTargets()
        {
            var tm = LaneTargets.Table.Where(t => t.Lane == "TimeMachineBP")
                                      .OrderBy(t => t.Pool, StringComparer.Ordinal).ToArray();
            Assert.Equal(2, tm.Length);
            Assert.Equal(new[] { "Energy", "Magic" }, tm.Select(t => t.Pool).ToArray());

            Assert.Contains("speedTarget", tm[0].Reads);
            Assert.Contains("levelSpeed", tm[0].Reads);
            Assert.Contains("multiTarget", tm[1].Reads);
            Assert.Contains("levelGoldMulti", tm[1].Reads);

            // Separate consumers means neither row may mention the other's fields — a merged row is
            // what this test exists to prevent coming back.
            Assert.DoesNotContain("multiTarget", tm[0].Reads);
            Assert.DoesNotContain("speedTarget", tm[1].Reads);
        }

        // TimeMachineBP is the ONLY class split across rows. NGUBP and WandoosBP also parse into either
        // pool, but each reads the SAME target either way, so they stay one consumer on one row.
        [Fact]
        public void Table_TimeMachineIsTheOnlyClassSplitAcrossRows()
        {
            var split = LaneTargets.Table.GroupBy(t => t.Lane).Where(g => g.Count() > 1)
                                         .Select(g => g.Key).ToArray();
            Assert.Equal(new[] { "TimeMachineBP" }, split);

            foreach (var lane in new[] { "NGUBP", "WandoosBP" })
                Assert.Equal("Energy|Magic", LaneTargets.Table.Single(t => t.Lane == lane).Pool);
        }

        // Three lanes spell "never fund this" out as its own branch; the rest reach the same answer
        // only because levels are non-negative. A tidy-up that unifies the bodies must preserve both
        // shapes. BestAug joins the incidental group: it reads the same two game predicates AugmentBP
        // does, and neither has a `target < 0` branch.
        [Fact]
        public void Table_NeverFundMarkerIsExplicitInThreeLanesAndIncidentalInTheRest()
        {
            var explicitly = LaneTargets.Table.Where(t => t.NeverFundMarker && t.ExplicitNeverFund)
                                              .Select(t => t.Lane).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var incidental = LaneTargets.Table.Where(t => t.NeverFundMarker && !t.ExplicitNeverFund)
                                              .Select(t => t.Lane).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "AdvancedTrainingBP", "HackBP", "NGUBP" }, explicitly);
            Assert.Equal(new[] { "AugmentBP", "BestAug", "TimeMachineBP" }, incidental);
        }

        [Fact]
        public void Table_EveryLaneNamesAtLeastOneProfileToken()
        {
            foreach (var t in LaneTargets.Table)
                Assert.False(string.IsNullOrWhiteSpace(t.Tokens));
        }

        // ---------------- the predicates ----------------

        // FLIPPED. This test was written first asserting the defect — BestAug's row was
        // HardcodedFalse, Reads "—", and TargetMet() was LaneTargets.NeverDone(). Both halves now
        // assert the game signal instead: amendment 16 §7 / audit 20 §2.7.
        [Fact]
        public void BestAug_DelegatesToTheGamesOwnAugmentTargetPredicates()
        {
            var row = LaneTargets.Table.Single(t => t.Lane == "BestAug");
            Assert.Equal(LaneTargets.TargetKind.GameDelegated, row.Kind);
            Assert.NotEqual("—", row.Reads);
        }

        // A single live half anywhere in the 7 pairs keeps the lane fundable; the lane is done only
        // when every half is locked or at target. This is the difference between BestAug and AugmentBP
        // and it is the whole reason the fold lives in a core rather than being inlined per half.
        [Fact]
        public void BestAugTargetMet_OneLiveHalfAnywhereKeepsTheLaneFundable()
        {
            var done = AllPairs(augLocked: false, augHit: true, upgLocked: false, upgHit: true);
            Assert.True(LaneTargets.BestAugTargetMet(done, useUpgrades: true));

            for (var i = 0; i < 7; i++)
            {
                var oneAugLive = AllPairs(false, true, false, true);
                oneAugLive[i] = new AugmentMath.AugPairTargetState
                { AugLocked = false, AugHitTarget = false, UpgradeLocked = false, UpgradeHitTarget = true };
                Assert.False(LaneTargets.BestAugTargetMet(oneAugLive, useUpgrades: true));

                var oneUpgLive = AllPairs(false, true, false, true);
                oneUpgLive[i] = new AugmentMath.AugPairTargetState
                { AugLocked = false, AugHitTarget = true, UpgradeLocked = false, UpgradeHitTarget = false };
                Assert.False(LaneTargets.BestAugTargetMet(oneUpgLive, useUpgrades: true));
            }
        }

        // Below boss 37 the upgrade halves are never funded, so a live upgrade half must NOT keep the
        // lane alive — that is exactly the seat BestAug used to hold while allocating nothing.
        [Fact]
        public void BestAugTargetMet_ALiveUpgradeHalfCountsOnlyWhenUpgradesAreFundable()
        {
            var upgradesOnly = AllPairs(augLocked: false, augHit: true, upgLocked: false, upgHit: false);
            Assert.False(LaneTargets.BestAugTargetMet(upgradesOnly, useUpgrades: true));
            Assert.True(LaneTargets.BestAugTargetMet(upgradesOnly, useUpgrades: false));
        }

        // All seven pairs boss-locked: the lane is done. Pre-change it sat in the priority list
        // diluting every other energy lane's share and allocating nothing.
        [Fact]
        public void BestAugTargetMet_AllPairsBossLockedReportsDone()
        {
            var locked = AllPairs(augLocked: true, augHit: false, upgLocked: true, upgHit: false);
            Assert.True(LaneTargets.BestAugTargetMet(locked, useUpgrades: true));
            Assert.True(LaneTargets.BestAugTargetMet(locked, useUpgrades: false));
        }

        // THE OPT-IN PROPERTY. hitAugmentTarget() returns FALSE for a target of 0 ([DECOMP]
        // AugmentController.cs:171-177), so an operator who declares no targets sees no change at all:
        // every unlocked half stays live and the lane never reports done, exactly as before P1.
        [Fact]
        public void BestAugTargetMet_WithNoDeclaredTargetsTheLaneStillNeverReportsDone()
        {
            var noTargets = AllPairs(augLocked: false, augHit: false, upgLocked: false, upgHit: false);
            Assert.False(LaneTargets.BestAugTargetMet(noTargets, useUpgrades: true));
            Assert.False(LaneTargets.BestAugTargetMet(noTargets, useUpgrades: false));
        }

        [Fact]
        public void BestAugTargetMet_NullPairsFallsBackToTheOldAnswer()
        {
            Assert.False(LaneTargets.BestAugTargetMet(null, useUpgrades: true));
        }

        // HalfLive is the one definition LiveHalves() (the ranking) and BestAugTargetMet() (the lane)
        // both use, so "worth ranking" and "not done" cannot drift apart.
        [Theory]
        [InlineData(false, false, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        public void HalfLive_IsNotLockedAndNotAtTarget(bool locked, bool hitTarget, bool expected)
        {
            Assert.Equal(expected, AugmentMath.HalfLive(locked, hitTarget));
        }

        private static List<AugmentMath.AugPairTargetState> AllPairs(bool augLocked, bool augHit, bool upgLocked, bool upgHit)
        {
            var pairs = new List<AugmentMath.AugPairTargetState>(7);
            for (var i = 0; i < 7; i++)
                pairs.Add(new AugmentMath.AugPairTargetState
                {
                    AugLocked = augLocked,
                    AugHitTarget = augHit,
                    UpgradeLocked = upgLocked,
                    UpgradeHitTarget = upgHit
                });
            return pairs;
        }

        // FLIPPED. Written first asserting the defect — row HardcodedFalse, Reads "—", TargetMet() was
        // LaneTargets.NeverDone(). Now asserts the game's own ceiling (amendment 16 §7; audit 20 §2.2).
        [Fact]
        public void BasicTraining_ReadsTheGamesOwnEnergyCeiling()
        {
            var row = LaneTargets.Table.Single(t => t.Lane == "BasicTrainingBP");
            Assert.Equal(LaneTargets.TargetKind.GameCeiling, row.Kind);
            Assert.NotEqual("—", row.Reads);
        }

        // The ceiling is `>=`, verbatim from OffenseTraining.cs:99. One unit short is NOT done.
        [Fact]
        public void BasicTrainingTargetMet_IsMetAtTheCapNotOnlyAboveIt()
        {
            Assert.False(LaneTargets.BasicTrainingTargetMet(2499, 2500));
            Assert.True(LaneTargets.BasicTrainingTargetMet(2500, 2500));
            Assert.True(LaneTargets.BasicTrainingTargetMet(2501, 2500));
        }

        // An empty slot is never done, at any cap the game can produce.
        [Fact]
        public void BasicTrainingTargetMet_AnEmptySlotIsNeverDone()
        {
            foreach (long cap in new long[] { 1, 2500, 15000, 30000, 50000, 70000, 100000 })
                Assert.False(LaneTargets.BasicTrainingTargetMet(0, cap));
        }

        // The floor Rebirth.resetTraining() enforces is 1, not 0 ([DECOMP] Rebirth.cs:652-655). Pinned
        // because it is the reason no zero-cap guard exists: at cap 1 an empty slot is still fundable,
        // so the predicate does not retire a lane the game would still level.
        [Fact]
        public void BasicTrainingTargetMet_AtTheRebirthFloorOfOneTheSlotIsStillFundableWhileEmpty()
        {
            Assert.False(LaneTargets.BasicTrainingTargetMet(0, 1));
            Assert.True(LaneTargets.BasicTrainingTargetMet(1, 1));
        }

        [Fact]
        public void NeverDone_IsFalseForever()
        {
            Assert.False(LaneTargets.NeverDone());
        }

        [Fact]
        public void AdvancedTrainingTargetMet_NegativeTargetIsTheExplicitNeverFundMarker()
        {
            Assert.True(LaneTargets.AdvancedTrainingTargetMet(-1, 0));
            Assert.True(LaneTargets.AdvancedTrainingTargetMet(long.MinValue, 0));
        }

        [Fact]
        public void AdvancedTrainingTargetMet_ZeroTargetNeverReportsDone()
        {
            Assert.False(LaneTargets.AdvancedTrainingTargetMet(0, long.MaxValue));
        }

        [Fact]
        public void AdvancedTrainingTargetMet_MetAtOrAboveAPositiveTarget()
        {
            Assert.False(LaneTargets.AdvancedTrainingTargetMet(100, 99));
            Assert.True(LaneTargets.AdvancedTrainingTargetMet(100, 100));
        }

        [Fact]
        public void AdvancedTrainingAndNgu_AgreeOnEveryTargetLevelCombination()
        {
            // AT writes `target != 0`, NGU writes `target > 0`. After each one's negative branch the
            // two are equivalent — pinned here so a later unification is provably a no-op.
            foreach (long target in new long[] { -5, -1, 0, 1, 100 })
                foreach (long level in new long[] { 0, 1, 99, 100, 101 })
                    Assert.Equal(LaneTargets.NguTargetMet(target, level),
                                 LaneTargets.AdvancedTrainingTargetMet(target, level));
        }

        [Fact]
        public void TimeMachineTargetMet_ZeroTargetNeverReportsDone()
        {
            Assert.False(LaneTargets.TimeMachineTargetMet(0, long.MaxValue));
        }

        [Fact]
        public void TimeMachineTargetMet_MetAtOrAboveAPositiveTarget()
        {
            Assert.False(LaneTargets.TimeMachineTargetMet(50, 49));
            Assert.True(LaneTargets.TimeMachineTargetMet(50, 50));
        }

        [Fact]
        public void TimeMachineTargetMet_NegativeTargetIsMetOnlyIncidentally()
        {
            // No `target < 0` branch — it reports done because any level >= a negative number.
            Assert.True(LaneTargets.TimeMachineTargetMet(-1, 0));
        }

        [Fact]
        public void AugmentAndTimeMachine_ShareTheIdenticalIncidentalShape()
        {
            foreach (long target in new long[] { -5, -1, 0, 1, 100 })
                foreach (long level in new long[] { 0, 1, 99, 100, 101 })
                    Assert.Equal(LaneTargets.TimeMachineTargetMet(target, level),
                                 LaneTargets.AugmentTargetMet(0, target, level));
        }

        // ---------------- IsValid ----------------

        [Theory]
        [InlineData(true, true, false, true)]     // the only combination that funds
        [InlineData(false, true, false, false)]
        [InlineData(true, false, false, false)]
        [InlineData(true, true, true, false)]
        public void IsValid_IsCorrectTypeAndUnlockedAndNotTargetMet(bool ct, bool unlocked, bool met, bool expected)
        {
            Assert.Equal(expected, LaneTargets.IsValid(ct, unlocked, met));
        }

        // The structural consequence P1 is about, stated as a test: for the five `=> false` lanes the
        // third term is a constant, so IsValid reduces to "correct pool AND unlocked". Such a lane
        // cannot leave the priority list by being satisfied — only by being locked out.
        [Fact]
        public void IsValid_ForAHardcodedFalseLane_ReducesToTypeAndUnlock()
        {
            foreach (bool ct in new[] { true, false })
                foreach (bool unlocked in new[] { true, false })
                    Assert.Equal(ct && unlocked, LaneTargets.IsValid(ct, unlocked, LaneTargets.NeverDone()));
        }

        // ---------------- Label ----------------

        [Fact]
        public void Label_MirrorsTheProfileTokenSyntax()
        {
            Assert.Equal("CAPNGU-5", LaneTargets.Label("NGUBP", isCap: true, index: 5));
            Assert.Equal("NGU-0", LaneTargets.Label("NGUBP", isCap: false, index: 0));
            Assert.Equal("CAPBR-30", LaneTargets.Label("BR", isCap: true, index: 30));
        }

        // [QUIRK] The label is built by stripping the literal "BP" from the class name, so a lane whose
        // name has no "BP" (BR, BestAug) round-trips to a token that is not the one that constructed it:
        // BestAug labels as "BestAug-0", where the profile token is BESTAUG. Harmless — the label is
        // diagnostic only — but it means Label output is not universally paste-back-able.
        [Fact]
        public void QUIRK_Label_DoesNotRoundTripForLanesWhoseNameLacksTheBPSuffix()
        {
            Assert.Equal("CAPBestAug-0", LaneTargets.Label("BestAug", true, 0));
            Assert.NotEqual("CAPBESTAUG-0", LaneTargets.Label("BestAug", true, 0));
        }
    }
}
