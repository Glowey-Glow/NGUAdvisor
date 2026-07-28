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
                if (cap <= 0 || maxAllocation <= 0) return;
                double share = maxAllocation / cap;
                if (double.IsNaN(share) || share <= 0) return;
                if (share > 1.0) share = 1.0;
                if (energy) LastShareEnergy = share; else LastShareMagic = share;
            }
            catch { }
        }

        protected override bool CorrectResourceType() => Type == ResourceType.Energy || Type == ResourceType.Magic;

        protected override bool Unlocked() => _character.buttons.wandoos.interactable && !_character.wandoos98.disabled;

        protected override bool TargetMet() => false;

        public override bool Allocate()
        {
            if (Type == ResourceType.Energy)
                AllocateEnergy();
            else
                AllocateMagic();
            return true;
        }

        private void AllocateEnergy()
        {
            RecordShare(true, MaxAllocation);
            var num = Math.Ceiling((double)_character.wandoos98Controller.baseEnergyTime() / _character.totalWandoosEnergySpeed());
            if (num < 1.0)
                num = 1.0;
            var num1 = Math.Ceiling(num / Math.Ceiling(num / MaxAllocation) * 1.000002f);
            long num2;
            if (num1 > _character.idleEnergy)
                num2 = _character.idleEnergy;
            else
                num2 = (long)num1;
            SetInput(num2);
            _character.wandoos98Controller.addEnergy();
        }

        private void AllocateMagic()
        {
            RecordShare(false, MaxAllocation);
            var num = Math.Ceiling((double)_character.wandoos98Controller.baseMagicTime() / _character.totalWandoosMagicSpeed());
            if (num < 1.0)
                num = 1.0;
            var num1 = Math.Ceiling(num / Math.Ceiling(num / MaxAllocation) * 1.000002f);
            long num2;
            if (num1 > _character.magic.idleMagic)
                num2 = _character.magic.idleMagic;
            else
                num2 = (long)num1;
            SetInput(num2);
            _character.wandoos98Controller.addMagic();
        }
    }
}
