using NGUAdvisor.Managers;
using System;

namespace NGUAdvisor.AllocationProfiles.BreakpointTypes
{
    public class BR : ResourceBreakpoint
    {
        protected override bool CorrectResourceType() => Type == ResourceType.Magic;

        protected override bool Unlocked() => _character.buttons.bloodMagic.interactable;

        // THE `false` STAYS — see the long note on RitualBP.TargetMet for the evidence. Short version:
        // amendment 16 §7 names `ritualsUnlocked` as this lane's discarded game signal, but that is a
        // LOCK and CastRituals below already applies it (`i >= ritualsUnlocked() → continue`); and an
        // exhaustive search finds no ritual target anywhere in the decomp, so there is nothing else to
        // wire. Rituals cycle forever and never report done.
        //
        // BR has a second reason on top of RitualBP's, and it is about this lane's shape rather than
        // the game's: BR's `Index` is a DURATION in seconds, not a ritual id (05 §6.2 flags the same
        // ambiguity). One BR lane spans every unlocked ritual, so even a per-ritual terminator would
        // not answer BR's question — the lane would be done only when EVERY unlocked ritual were, which
        // is a fold over a signal that does not exist.
        protected override bool TargetMet() => Managers.LaneTargets.NeverDone();

        // Latch: log only on transition, since this runs on every allocation pass.
        private static bool? _lastFunded;

        public override bool Allocate()
        {
            long allocated = CastRituals(Index);
            bool funded = allocated > 0;
            // BR is the one lane that can sit in the profile and still silently receive NOTHING: it is
            // non-CAP (an equal share of *idle* magic), so anything CAP-with-no-percent ahead of it in
            // the list drinks the pool first — and PerformSwap then DROPS any priority that allocates
            // zero, so it vanishes for the rest of the pass without a word. That is invisible from the
            // token list alone, which is why blood income read as "on" while rebirthPower sat frozen.
            if (_lastFunded != funded)
            {
                _lastFunded = funded;
                Main.Log(funded
                    ? $"BR-{Index}: rituals funded with {allocated:N0} magic"
                    : $"BR-{Index}: allocated NOTHING (share {MaxAllocation:N0}, idle magic {_character.magic.idleMagic:N0}) — rituals get no magic, blood income is zero");
            }
            return funded;
        }

        // The run's planned end, read from the LIVE profile (as BestAug/BloodPlanner/WandoosAdvisor do).
        // The old `RebirthTime` property was never populated: ParseBreakpointArray took a rebirthTime
        // argument that no caller ever passed, so it stayed 0, the guard below could never fire, and
        // rituals that cannot finish before the rebirth were funded anyway. -1 = no scheduled rebirth.
        private double RebirthDeadline()
        {
            if (!Main.Settings.AutoRebirth) return -1;
            try { return Main.Profile != null ? Main.Profile.NextRebirthTargetSeconds() : -1; } catch { return -1; }
        }

        // Live reads and the game calls only — the per-ritual decision is RitualMath.RitualDecide and
        // the arithmetic is RitualMath.{ProgressPerTick,TimeLeft,MaxAllocationFor}, all under
        // characterisation test. This is the magic layer's ENTIRE blood-facing surface (amendment 01:
        // at the magic layer, the four blood sinks are ONE consumer), so it is where M1 will attach.
        private long CastRituals(int secondsToRun)
        {
            long allocationLeft = MaxAllocation;
            var totalAllocated = 0L;
            double deadline = RebirthDeadline();

            for (var i = _character.bloodMagic.ritual.Count - 1; i >= 0; i--)
            {
                if (allocationLeft <= 0)
                    break;
                if (_character.magic.idleMagic == 0)
                    break;
                if (i >= _character.bloodMagicController.ritualsUnlocked())
                    continue;

                var state = new RitualMath.RitualState
                {
                    Id = i,
                    Unlocked = true,   // the loop already skipped locked rituals above
                    GoldCost = _character.bloodMagicController.bloodMagics[i].baseCost * _character.totalDiscount(),
                    Progress = _character.bloodMagic.ritual[i].progress
                };

                // RitualTimeLeft is passed lazily: it runs the whole ritual-rate stack, and the
                // original never reached it when the gold gate already refused the ritual.
                int id = i;
                long left = allocationLeft;
                var action = RitualMath.RitualDecide(state, _character.realGold, secondsToRun,
                                                     _character.rebirthTime.totalseconds, deadline,
                                                     () => RitualTimeLeft(id, left));

                if (action != RitualMath.RitualAction.Fund)
                {
                    if (_character.bloodMagic.ritual[i].magic > 0)
                        _character.bloodMagicController.bloodMagics[i].removeAllMagic();

                    continue;
                }

                var cap = CalculateMaxAllocation(i, allocationLeft);
                SetInput(cap);
                _character.bloodMagicController.bloodMagics[i].add();
                totalAllocated += cap;
                allocationLeft -= cap;
            }

            return totalAllocated;
        }

        private float RitualProgressPerTick(int id, long remaining)
        {
            var diff = _character.settings.rebirthDifficulty;
            var bmc = _character.bloodMagicController;

            // The difficulty branch resolves to (scale, divider): normal and evil scale by 50000.0 and
            // sadistic does not. An unrecognised difficulty leaves the rate undivided, as before.
            double scale = 1.0, divider = 1.0;
            if (diff == difficulty.normal) { scale = 50000.0; divider = bmc.normalSpeedDividers[id]; }
            else if (diff == difficulty.evil) { scale = 50000.0; divider = bmc.evilSpeedDividers[id]; }
            else if (diff == difficulty.sadistic) { divider = bmc.sadisticSpeedDividers[id]; }

            return RitualMath.ProgressPerTick(new RitualMath.RitualRateInputs
            {
                Remaining = remaining,
                TotalMagicPower = _character.totalMagicPower(),
                DividerScale = scale,
                SpeedDivider = divider,
                Sadistic = diff >= difficulty.sadistic,
                SadisticDivider = diff >= difficulty.sadistic ? bmc.bloodMagics[id].sadisticDivider() : 1.0,
                SpeedBonus = bmc.bloodMagics[id].totalBloodMagicSpeedBonus()
            });
        }

        public float RitualTimeLeft(int id, long remaining) =>
            RitualMath.TimeLeft(_character.bloodMagic.ritual[id].progress, RitualProgressPerTick(id, remaining));

        private long CalculateMaxAllocation(int id, long remaining) =>
            RitualMath.MaxAllocationFor(_character.bloodMagicController.bloodMagics[id].capValue(), remaining);
    }
}
