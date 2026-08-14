using System;

namespace NGUAdvisor.AllocationProfiles.BreakpointTypes
{
    public class RitualBP : ResourceBreakpoint
    {
        protected override bool CorrectResourceType() => Type == ResourceType.Magic;

        // `<`, not `<=`: ritualsUnlocked() returns a COUNT, not a top index (decomp
        // AllBloodMagicController.ritualsUnlocked — 7 normally, 8 once the Troll challenge has 6+
        // completions), and ritual[] is 0-based. With `<=`, RIT-7 on a 7-ritual account reported VALID,
        // so it entered the priority list as a non-cap lane, inflated the equal-share divisor for every
        // other magic lane, and then allocated nothing — BloodMagicController.addMagic refuses a locked
        // ritual and only shows a tooltip. BR.CastRituals already had this right (`i >= ritualsUnlocked()
        // → continue`); this is the same test. Shipped profiles that hit it: SampleProfiles/Evil/
        // 24hr-EarlyEvil.json and CBlock3.1-E100LC.json both list RIT-7.
        protected override bool Unlocked() =>
            Managers.RitualMath.RitualIndexUnlocked(Index, _character.bloodMagicController.ritualsUnlocked())
            && _character.buttons.bloodMagic.interactable;

        // THE `false` STAYS. Amendment 16 §7 and 20 §2.7 both name `ritualsUnlocked` as the game signal
        // this lane discards; that attribution does not survive contact with the decomp, and the
        // difference matters enough to write down rather than quietly wire something else.
        //
        // WHAT ritualsUnlocked ACTUALLY IS: a LOCK, not a target. [DECOMP] BloodMagicController.cs:132
        // and :182 refuse `addMagic`/`cap` for `id >= ritualsUnlocked()` with a 2-second tooltip and
        // move nothing. That is an Unlocked() question, and THE ADVISOR ALREADY ASKS IT — see the
        // predicate directly above, and BR.CastRituals' matching `i >= ritualsUnlocked() → continue`.
        // Wiring it into TargetMet() as well would be a duplicate of a test that already passes, and
        // would change no allocation at all: IsValid() is `correctType && unlocked && !targetMet`, so a
        // term that is only ever true when `unlocked` is already false cannot move the answer.
        //
        // AND THERE IS NO RITUAL TARGET TO WIRE INSTEAD. Verified the same way 20 §2.8 verified
        // Wandoos: the string `target` does not occur, case-insensitively, in ANY of the four
        // blood-magic files — AllBloodMagicController.cs, BloodMagicController.cs, BloodMagic.cs,
        // Ritual.cs. Zero hits in all four. Widening the search to every decomp file that mentions
        // "ritual" leaves only Character.cs, ImportExport.cs and TrollChallengeController.cs, and every
        // `target` in those belongs to the beast quest, Advanced Training, the NGUs, or the Troll
        // challenge's boss number. Rituals have no target field, no hitTarget(), no cascade and no
        // reclaim. Their bar cycles forever and blood accrues forever: THERE IS NOTHING TO BE DONE
        // WITH. This lane's `false` is faithful, exactly as WandoosBP's is.
        //
        // TWO THINGS THE SEARCH DID TURN UP, NEITHER OF WHICH IS A TARGET AND NEITHER IMPLEMENTED HERE:
        //
        //  1. A saturation ceiling, structurally identical to the one BasicTrainingBP is now wired to.
        //     `capValue()` (BloodMagicController.cs:244) is the magic at which the bar fills in one
        //     tick, and the game discards the overflow — `progress = 0f` on completion, not `-= 1f`
        //     (:66-76) — so magic above it buys nothing. `addMagic` is `ritual[id].magic += num`
        //     (:130-153), the same accumulating shape that made the Basic Training defect permanent.
        //     GetRitualCap caps each PASS at capValue but nothing tests what the ritual is already
        //     holding, so repeated passes can still stack magic past the ceiling.
        //  2. A hard freeze on the boss gate. `updateBloodMagic` returns without adding any progress
        //     while `bossID <= ritual[id].boss` (:47), so magic parked in a boss-locked ritual produces
        //     exactly nothing. Neither Unlocked() above nor BR.CastRituals tests it — it is an unlock
        //     question, and the advisor asks a different one (`buttons.bloodMagic.interactable`).
        //
        // Both are the same silent-loss CLASS this P1 pass closes elsewhere, and both are candidates
        // for a follow-up. Neither is the signal that was briefed, so neither is being substituted for
        // it here: choosing a different terminator than the record names is a decision to record, not
        // one to make inside a lane.
        protected override bool TargetMet() => Managers.LaneTargets.NeverDone();

        public override bool Allocate()
        {
            float goldCost = _character.bloodMagicController.bloodMagics[Index].baseCost * _character.totalDiscount();
            if (Managers.RitualMath.RitualGoldGateBlocks(goldCost, _character.realGold, _character.bloodMagic.ritual[Index].progress))
            {
                if (_character.bloodMagic.ritual[Index].magic > 0)
                    _character.bloodMagicController.bloodMagics[Index].removeAllMagic();

                return false;
            }

            var cap = GetRitualCap(Index);
            SetInput(Math.Min(cap, MaxAllocation));
            _character.bloodMagicController.bloodMagics[Index].add();
            return true;
        }

        // Live reads only — the arithmetic is Managers.RitualMath.RitualCap, under characterisation
        // test. The whole difficulty branch collapses into one term, including its `else return 0L`
        // fall-through, which is preserved as UnknownDifficulty.
        private long GetRitualCap(int index)
        {
            var diff = _character.settings.rebirthDifficulty;
            var bmc = _character.bloodMagicController;

            double term = 0;
            bool unknown = false;
            if (diff == difficulty.normal)
                term = 50000.0 * bmc.normalSpeedDividers[index];
            else if (diff == difficulty.evil)
                term = 50000.0 * bmc.evilSpeedDividers[index];
            else if (diff == difficulty.sadistic)
                term = bmc.bloodMagics[index].sadisticDivider() * bmc.sadisticSpeedDividers[index];
            else
                unknown = true;

            return Managers.RitualMath.RitualCap(new Managers.RitualMath.RitualCapInputs
            {
                TotalMagicPower = _character.totalMagicPower(),
                SpeedBonus = bmc.bloodMagics[index].totalBloodMagicSpeedBonus(),
                DifficultyTerm = term,
                UnknownDifficulty = unknown,
                MaxAllocation = MaxAllocation,
                IdleMagic = _character.magic.idleMagic
            });
        }
    }
}
