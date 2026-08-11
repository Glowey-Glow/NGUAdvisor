using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // PASS 1 — feasibility (constraint-layer-spec §4). One test per predicate, positive and negative,
    // plus the two structural properties the spec makes non-negotiable: the seat rule (§4.1) and the
    // maxed-wish guard (§4.4).
    public class FeasibilityPassTests
    {
        // ---- the seat rule -----------------------------------------------------------------------

        // The RIT-7 defect, restated as an assertion: a refused lane must be unable to appear in the
        // priority count. Three lanes seat, two refuse; the divisor is 3, not 5, and the refused
        // lanes are only reachable through Refusals, each carrying its reason.
        [Fact]
        public void A_refused_lane_holds_no_seat_and_cannot_inflate_the_divisor()
        {
            var roster = new SeatRoster();
            roster.Add("TM", FeasibilityPass.Verdict.Seat());
            roster.Add("AUG-0", FeasibilityPass.Verdict.Seat());
            roster.Add("NGU-2", FeasibilityPass.Verdict.Seat());
            roster.Add("RIT-7", FeasibilityPass.Verdict.Refuse("boss gate: bossID 10 has not passed required 90"));
            roster.Add("WISH-3", FeasibilityPass.Verdict.Refuse("wish maxed"));

            Assert.Equal(3, roster.SeatCount);
            Assert.Equal(300 / 3, roster.EqualShare(300));   // 100 each — NOT 300/5 = 60
            Assert.DoesNotContain("RIT-7", roster.Seated);
            Assert.DoesNotContain("WISH-3", roster.Seated);
            Assert.Equal(2, roster.Refusals.Count);
            Assert.All(roster.Refusals, r => Assert.False(string.IsNullOrEmpty(r.Reason)));
        }

        // default(Verdict) must be a refusal, not a seat — an unevaluated lane is fail-closed, the
        // same rule ZoneGate applies to an unknown zone.
        [Fact]
        public void An_unevaluated_verdict_is_a_refusal_with_a_reason()
        {
            var v = default(FeasibilityPass.Verdict);

            Assert.False(v.Seated);
            Assert.Equal("unevaluated", v.Reason);
        }

        [Fact]
        public void A_seated_verdict_carries_no_reason_and_a_refusal_always_carries_one()
        {
            Assert.Null(FeasibilityPass.Verdict.Seat().Reason);
            Assert.Equal("infeasible (no reason given)", FeasibilityPass.Verdict.Refuse(null).Reason);
            Assert.Equal("gold stall", FeasibilityPass.Verdict.Refuse("gold stall").Reason);
        }

        [Fact]
        public void An_all_refused_tick_yields_a_zero_share_rather_than_a_divide()
        {
            var roster = new SeatRoster();
            roster.Add("AT-0", FeasibilityPass.Verdict.Refuse("AT prerequisite"));

            Assert.Equal(0, roster.SeatCount);
            Assert.Equal(0, roster.EqualShare(1_000_000));
        }

        // ---- gold sufficiency (TimeMachineController.cs:334-344/:377-387, AugmentController.cs:
        // 226-233/:266-273, BloodMagicController.cs:53-59) ----------------------------------------

        [Fact]
        public void Gold_below_cost_on_an_unstarted_bar_refuses_outright()
        {
            var v = FeasibilityPass.GoldGate(realGold: 999.0, cost: 1000.0, barProgress: 0.0);

            Assert.False(v.Seated);
            Assert.Contains("gold stall", v.Reason);
        }

        // Binary means binary in both directions: equality PAYS (every game comparator is strict <),
        // and a mid-bar lane is not gold-blocked at all — the game finishes the bar it already
        // charged for.
        [Fact]
        public void Gold_at_cost_pays_and_a_started_bar_is_never_gold_blocked()
        {
            Assert.True(FeasibilityPass.GoldGate(1000.0, 1000.0, 0.0).Seated);
            Assert.True(FeasibilityPass.GoldGate(0.0, 1000.0, 0.5).Seated);
        }

        // ---- boss gates (AugmentController.cs:78-81/:216/:256, BloodMagicController.cs:49) -------

        [Fact]
        public void Boss_gate_passes_only_strictly_past_the_requirement()
        {
            Assert.False(FeasibilityPass.BossGate(bossId: 57, bossRequired: 58).Seated);
            Assert.False(FeasibilityPass.BossGate(bossId: 58, bossRequired: 58).Seated);
            Assert.True(FeasibilityPass.BossGate(bossId: 59, bossRequired: 58).Seated);
        }

        // ---- the Time Machine boss lock (TimeMachineController.cs:81 — a source literal, one gate
        // shared by both halves; amendment 23 §7 item 4) -------------------------------------------

        // The literal read exactly as written: `bossID > 29`, so the requirement is 29 under the
        // strict > — NOT 30. Seating begins at bossID 30, which is what "unlocks on Boss 30" means.
        [Fact]
        public void The_time_machine_boss_requirement_is_the_decomp_literal_29()
        {
            Assert.Equal(29, FeasibilityPass.TimeMachineBossRequired);
        }

        // Rebirth sets bossID = 0 (Rebirth.cs:691), so a boss-locked TM half must REFUSE, not seat
        // and produce nothing — under 100LC's rebirth cadence both halves would otherwise hold
        // seats at the start of every cycle, the RIT-7 divisor shape ~20 times an hour.
        [Fact]
        public void A_boss_locked_time_machine_half_refuses_with_the_boss_reason()
        {
            var v = FeasibilityPass.TimeMachineHalf(new FeasibilityPass.TimeMachineHalfState
            {
                BossId = 0,
                BossRequired = FeasibilityPass.TimeMachineBossRequired,
                RealGold = 1e9, Cost = 1e6, BarProgress = 0,
            });

            Assert.False(v.Seated);
            Assert.Contains("boss gate", v.Reason);
        }

        [Fact]
        public void The_time_machine_seats_at_bossID_30_when_gold_clears()
        {
            var v = FeasibilityPass.TimeMachineHalf(new FeasibilityPass.TimeMachineHalfState
            {
                BossId = 30,
                BossRequired = FeasibilityPass.TimeMachineBossRequired,
                RealGold = 1e9, Cost = 1e6, BarProgress = 0,
            });

            Assert.True(v.Seated);
        }

        // THE STRICT BOUNDARY: at bossID == required the lane is STILL locked — BossGate's pass
        // sense is strict >, matching the game's `bossID > 29` at :81.
        [Fact]
        public void At_bossID_equal_to_the_requirement_the_time_machine_is_still_locked()
        {
            var v = FeasibilityPass.TimeMachineHalf(new FeasibilityPass.TimeMachineHalfState
            {
                BossId = FeasibilityPass.TimeMachineBossRequired,
                BossRequired = FeasibilityPass.TimeMachineBossRequired,
                RealGold = 1e9, Cost = 1e6, BarProgress = 0,
            });

            Assert.False(v.Seated);
            Assert.Contains("boss gate", v.Reason);
        }

        // A rebirth mid-run re-locks with no cached state: the same pure function that seated at
        // bossID 31 refuses at bossID 0 — nothing is held between calls to go stale (spec §4.5).
        [Fact]
        public void A_rebirth_re_locks_the_time_machine_with_no_cached_state()
        {
            var unlocked = new FeasibilityPass.TimeMachineHalfState
            {
                BossId = 31, BossRequired = FeasibilityPass.TimeMachineBossRequired,
                RealGold = 1e9, Cost = 1e6, BarProgress = 0,
            };
            var reborn = unlocked;
            reborn.BossId = 0;

            Assert.True(FeasibilityPass.TimeMachineHalf(unlocked).Seated);
            Assert.False(FeasibilityPass.TimeMachineHalf(reborn).Seated);
            Assert.Contains("boss gate", FeasibilityPass.TimeMachineHalf(reborn).Reason);
        }

        // ---- wish difficulty gate (WishesController.cs:895) --------------------------------------

        [Fact]
        public void Wish_above_the_rebirth_difficulty_refuses_and_at_or_below_seats()
        {
            Assert.False(FeasibilityPass.WishDifficultyGate(difficultyRequirement: 2, rebirthDifficulty: 1).Seated);
            Assert.True(FeasibilityPass.WishDifficultyGate(difficultyRequirement: 1, rebirthDifficulty: 1).Seated);
            Assert.True(FeasibilityPass.WishDifficultyGate(difficultyRequirement: 0, rebirthDifficulty: 1).Seated);
        }

        // ---- THE MAXED-WISH GUARD (spec §4.4 — the game omits this test) -------------------------

        // addEnergy/addMagic/addRes3 accept resources onto a maxed wish (WishesController.cs:884-919
        // has no maxWishLevel test) while the tick skips it forever (:274) — the guard the game does
        // not have, non-negotiable here.
        [Fact]
        public void A_maxed_wish_is_refused_no_matter_how_it_is_funded()
        {
            var v = FeasibilityPass.Wish(new FeasibilityPass.WishState
            {
                DifficultyRequirement = 0,
                RebirthDifficulty = 2,
                Level = 5,
                MaxWishLevel = 5,
                Energy = 1_000_000, Magic = 1_000_000, Res3 = 1_000_000,
            });

            Assert.False(v.Seated);
            Assert.Contains("wish maxed", v.Reason);
        }

        [Fact]
        public void A_wish_below_its_max_level_seats_when_everything_else_holds()
        {
            Assert.True(FeasibilityPass.WishMaxLevelGate(level: 4, maxWishLevel: 5).Seated);
            Assert.False(FeasibilityPass.WishMaxLevelGate(level: 5, maxWishLevel: 5).Seated);
        }

        // maxWishLevel(invalidID) returns 0L (WishesController.cs:690-696): an invalid id must land
        // in the refusal branch, not seat on a 0 >= 0 technicality read the other way.
        [Fact]
        public void An_invalid_wish_id_with_maxWishLevel_zero_is_refused()
        {
            Assert.False(FeasibilityPass.WishMaxLevelGate(level: 0, maxWishLevel: 0).Seated);
        }

        // ---- the wish AND-gate (WishesController.cs:699-706, factors :801-844) -------------------

        [Theory]
        [InlineData(0, 1, 1)]
        [InlineData(1, 0, 1)]
        [InlineData(1, 1, 0)]
        [InlineData(0, 0, 0)]
        public void A_wish_with_any_unfunded_resource_is_refused(long e, long m, long r3)
        {
            var v = FeasibilityPass.WishAndGate(e, m, r3);

            Assert.False(v.Seated);
            Assert.Contains("AND-gate", v.Reason);
        }

        [Fact]
        public void A_wish_with_all_three_resources_funded_seats()
        {
            Assert.True(FeasibilityPass.WishAndGate(1, 1, 1).Seated);
        }

        // ---- AT prerequisite (AllAdvancedTraining.cs:40-47, AdvancedTrainingController.cs:151) ---

        [Theory]
        [InlineData(24999, 25000)]
        [InlineData(25000, 24999)]
        [InlineData(0, 0)]
        public void AT_refuses_until_BOTH_BT_slot_4_sides_reach_25000(long atk, long def)
        {
            var v = FeasibilityPass.AtPrerequisiteGate(atk, def);

            Assert.False(v.Seated);
            Assert.Contains("25000", v.Reason);
        }

        [Fact]
        public void AT_prerequisite_seats_at_exactly_25000_both_sides()
        {
            Assert.True(FeasibilityPass.AtPrerequisiteGate(25000, 25000).Seated);
        }

        // ---- wish 190 (AdvancedTrainingController.cs:128-131) ------------------------------------

        [Fact]
        public void Owning_wish_190_makes_AT_permanently_infeasible()
        {
            var v = FeasibilityPass.AdvancedTraining(new FeasibilityPass.AdvancedTrainingState
            {
                AttackTrainingSlot4 = 25000,
                DefenseTrainingSlot4 = 25000,
                Wish190Level = 1,
            });

            Assert.False(v.Seated);
            Assert.Contains("wish 190", v.Reason);
        }

        [Fact]
        public void Without_wish_190_AT_seats_once_the_prerequisite_is_met()
        {
            var v = FeasibilityPass.AdvancedTraining(new FeasibilityPass.AdvancedTrainingState
            {
                AttackTrainingSlot4 = 25000,
                DefenseTrainingSlot4 = 25000,
                Wish190Level = 0,
            });

            Assert.True(v.Seated);
        }

        // The permanent reason outranks the transient one: an account owning wish 190 whose BT has
        // not yet re-earned the prerequisite must surface wish 190, not the BT number that clears
        // mid-run.
        [Fact]
        public void Wish_190_outranks_the_BT_prerequisite_in_the_surfaced_reason()
        {
            var v = FeasibilityPass.AdvancedTraining(new FeasibilityPass.AdvancedTrainingState
            {
                AttackTrainingSlot4 = 0,
                DefenseTrainingSlot4 = 0,
                Wish190Level = 3,
            });

            Assert.Contains("wish 190", v.Reason);
        }

        // ---- trolled off (NUMBERSSGOUP.cs:13, Beards.cs:17, Wandoos98.cs:33, Inventory.cs:68) ----

        [Fact]
        public void A_trolled_off_lane_is_refused_and_names_its_flag()
        {
            var v = FeasibilityPass.TrollGate(disabled: true, flagPath: "wandoos98.disabled");

            Assert.False(v.Seated);
            Assert.Contains("wandoos98.disabled", v.Reason);
        }

        [Fact]
        public void A_lane_whose_troll_flag_is_clear_seats()
        {
            Assert.True(FeasibilityPass.TrollGate(disabled: false, flagPath: "NGU.disabled").Seated);
        }

        // ---- external constraints — the slot designed now, filled later (spec §4.3) --------------

        [Fact]
        public void A_challenge_lock_refuses_with_the_senders_reason_surfaced_verbatim()
        {
            var v = FeasibilityPass.PlainLane(new FeasibilityPass.ExternalConstraints
            {
                ChallengeLock = "No Time Machine: output zeroed at Character.cs:1970",
            });

            Assert.False(v.Seated);
            Assert.Contains("No Time Machine", v.Reason);
        }

        [Fact]
        public void An_opt_out_refuses_and_no_external_constraint_seats()
        {
            Assert.False(FeasibilityPass.PlainLane(new FeasibilityPass.ExternalConstraints { OptOut = "user disabled rituals" }).Seated);
            Assert.True(FeasibilityPass.PlainLane(new FeasibilityPass.ExternalConstraints()).Seated);
        }

        // ---- re-evaluation, not caching (spec §4.5) ----------------------------------------------

        // The predicates are pure: the same lane refused for gold on one tick seats on the next the
        // moment gold arrives, with no state to clear. This is trivially true of a pure function —
        // which is the point; the test pins the shape so a cache cannot be added without failing it.
        [Fact]
        public void A_gold_stalled_lane_comes_back_the_tick_gold_arrives()
        {
            var starving = new FeasibilityPass.TimeMachineHalfState
            {
                BossId = 30, BossRequired = FeasibilityPass.TimeMachineBossRequired,
                RealGold = 0, Cost = 1e9, BarProgress = 0,
            };
            var funded = new FeasibilityPass.TimeMachineHalfState
            {
                BossId = 30, BossRequired = FeasibilityPass.TimeMachineBossRequired,
                RealGold = 1e9, Cost = 1e9, BarProgress = 0,
            };

            Assert.False(FeasibilityPass.TimeMachineHalf(starving).Seated);
            Assert.True(FeasibilityPass.TimeMachineHalf(funded).Seated);
        }

        // ---- composites route their first refusal ------------------------------------------------

        [Fact]
        public void The_augment_composite_reports_the_boss_gate_before_the_gold_gate()
        {
            var v = FeasibilityPass.AugmentHalf(new FeasibilityPass.AugmentHalfState
            {
                BossId = 10,
                BossRequired = 58,
                RealGold = 0,
                Cost = 1e9,
                BarProgress = 0,
            });

            Assert.False(v.Seated);
            Assert.Contains("boss gate", v.Reason);
        }

        [Fact]
        public void The_ritual_composite_seats_when_boss_and_gold_both_clear()
        {
            var v = FeasibilityPass.Ritual(new FeasibilityPass.RitualState
            {
                BossId = 91,
                BossRequired = 90,
                RealGold = 5e6,
                Cost = 1e6,
                BarProgress = 0,
            });

            Assert.True(v.Seated);
        }

        [Fact]
        public void The_troll_lane_composites_check_the_lock_before_the_flag()
        {
            var locked = new FeasibilityPass.ExternalConstraints { ChallengeLock = "No NGU" };

            var v = FeasibilityPass.NguLane(nguDisabled: true, external: locked);

            Assert.False(v.Seated);
            Assert.Contains("challenge lock", v.Reason);
        }
    }
}
