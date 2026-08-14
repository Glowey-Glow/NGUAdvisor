namespace NGUAdvisor.Managers
{
    // The game side of AdventureFloor. Split out for one reason: AdventureFloor is the arithmetic that
    // must be checkable without a game build, and a single Character read in that file makes it
    // unlinkable by the test project. Same split, same reason, as UntilClause / UntilFactsProvider.
    //
    // Everything here is a read. Nothing in this file may write to the save.
    public static class AdventureFloorReader
    {
        public static AdventureFloor.Reading Attack() => Read(true);
        public static AdventureFloor.Reading Defence() => Read(false);

        private static AdventureFloor.Reading Read(bool attack)
        {
            var r = new AdventureFloor.Reading { Known = false };
            try
            {
                var c = Main.Character;
                var ic = Main.InventoryController;
                if (c == null || ic == null) return r;

                // The bracket, exactly as [DECOMP] Character.totalAdvAttack builds it:
                //     (adventure.attack + gearBonus + cubePower) × <everything else>
                // Measuring the multiplier as total/bracket means a new game factor needs no change
                // here. Rebuilding the product by hand would be a second copy of a game formula, and
                // this codebase already has one defect from exactly that.
                double total = attack ? c.totalAdvAttack() : c.totalAdvDefense();
                double gear = attack ? ic.adventureAttackBonus() : ic.adventureDefenseBonus();
                double cube = attack ? ic.cubePower() : ic.cubeToughness();
                double baseStat = attack ? c.adventure.attack : c.adventure.defense;

                double m = AdventureFloor.MultiplierFrom(total, baseStat + gear + cube);
                if (double.IsNaN(m)) return r;

                r.Multiplier = m;
                // Reported for diagnostics only — the floor is on the whole bracket and must NOT subtract
                // this, because GearOptimizer.FloorStats already counts the cube and the nude base.
                r.NonGearBase = baseStat + cube;
                r.Known = true;
                return r;
            }
            catch { return r; }
        }
    }
}
