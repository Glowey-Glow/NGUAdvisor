using System;

namespace NGUAdvisor.AllocationProfiles.BreakpointTypes
{
    public class WandoosBP : ResourceBreakpoint
    {
        // The share of the resource CAP this profile actually hands Wandoos, recorded as a RATIO rather than
        // an absolute so it stays valid as the cap grows between breakpoint swaps (a swap may be hours apart).
        // WandoosAdvisor's OS comparator needs it: projecting at the full cap overstates any OS that does not
        // saturate, by exactly 1/share, and the presets run CAPWAN:50 / :30 / :20 / :10 -- so a 2-10x error on
        // the levels and a much larger one on the ratio between two OSs. Taken from MaxAllocation rather than
        // the parsed CapPercent because MaxAllocation is what the allocator actually resolved: it already
        // folds in IsCap, the non-cap prioCount split, and the min() against the idle pool.
        // -1 = never observed, which the comparator reads as "no better information than the full cap".
        public static double LastShareEnergy = -1.0;
        public static double LastShareMagic = -1.0;

        private static void RecordShare(bool energy, long maxAllocation)
        {
            try
            {
                double cap = energy ? _character.totalCapEnergy() : _character.totalCapMagic();
                double share = Managers.LaneCapMath.ShareOfCap(maxAllocation, cap);
                if (share < 0) return;   // unreadable cap / empty budget — keep the previous observation
                if (energy) LastShareEnergy = share; else LastShareMagic = share;
            }
            catch { }
        }

        protected override bool CorrectResourceType() => Type == ResourceType.Energy || Type == ResourceType.Magic;

        protected override bool Unlocked() => _character.buttons.wandoos.interactable && !_character.wandoos98.disabled;

        // THE `false` STAYS, AND IT IS FAITHFUL. Do not "fix" this to match the other lanes.
        //
        // Audit 20 §2.8 establishes it by exhaustive search and it was re-run before this comment was
        // written: the string `target` does not occur ANYWHERE in [DECOMP] Wandoos98Controller.cs or
        // Wandoos98.cs — zero hits, case-insensitive, in either file — and neither carries a level cap,
        // hard cap or max-level test. There is no cascade and no reclaim either, not even the
        // hardcoded-constant kind Basic Training turns out to have (20 §2.2).
        //
        // Wandoos is THE ONLY SURVIVOR of the energy pool's seven consumers: every other one carries a
        // target field with a game-side cascade or reclaim, a hard ceiling, or a game-supplied maximum
        // (20 §2.8, amendment 16 §4). That is not an oversight to be patched — it is load-bearing.
        // Amendment 16 §4 answers "what is the smallest set of consumers needing a common value unit?"
        // with ZERO, and the reason it comes out that way is precisely that the surplus has exactly one
        // unterminated destination: a sink that is alone needs no comparison to route to it. Giving
        // Wandoos a synthetic target would manufacture a second unterminated consumer and re-open a
        // question the audit closed.
        protected override bool TargetMet() => Managers.LaneTargets.NeverDone();

        public override bool Allocate()
        {
            if (Type == ResourceType.Energy)
                AllocateEnergy();
            else
                AllocateMagic();
            return true;
        }

        // Live reads only — the arithmetic is Managers.LaneCapMath.WandoosCap, which keeps this lane's
        // 1.000002f epsilon rather than the 1.00000202655792 the other seven copies use. That is
        // deliberate and game-verbatim ([DECOMP] Wandoos98Controller.cs:577); see LaneCapMath.
        private void AllocateEnergy()
        {
            RecordShare(true, MaxAllocation);
            SetInput(Managers.LaneCapMath.WandoosCap(
                _character.wandoos98Controller.baseEnergyTime(),
                _character.totalWandoosEnergySpeed(),
                MaxAllocation,
                _character.idleEnergy));
            _character.wandoos98Controller.addEnergy();
        }

        private void AllocateMagic()
        {
            RecordShare(false, MaxAllocation);
            SetInput(Managers.LaneCapMath.WandoosCap(
                _character.wandoos98Controller.baseMagicTime(),
                _character.totalWandoosMagicSpeed(),
                MaxAllocation,
                _character.magic.idleMagic));
            _character.wandoos98Controller.addMagic();
        }
    }
}
