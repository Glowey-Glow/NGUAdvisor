namespace NGUAdvisor.Managers
{
    // Beards — the constraint layer's special case (constraint-layer-spec §6), explicit because a
    // spec that omits them produces code that drops them: they were absent from every consumer
    // enumeration in the audit until 21 §5.
    //
    // Beards allocate NOTHING. There is no beards[id].energy field and no addEnergy; what they gate
    // on is the POOL BAR being full ([DECOMP] AllBeardsController.cs:55-85):
    //
    //     energy beards  :60-63   if (character.curEnergy < character.totalCapEnergy()) return 0f;
    //     magic beards   :74-77   if (character.magic.curMagic < character.totalCapMagic()) return 0f;
    //
    // (usesEnergy[id] picks the side — the spec quotes only the energy line; the magic side is the
    // same gate against the magic bar.) So: Pass 1 applies — a predicate on pool STATE, not on
    // allocation. Pass 2 does not — never allocated to, never capped; CapacityPass.Table carries
    // them as CapSource.None and refuses structurally. They are a P1 claimant with zero allocation
    // cost, the only system with that shape, which is why they still take a SEAT: the Campaign
    // Advisor's ranking needs them even though the allocator will never hand them a unit.
    //
    // ⚠ THE WIPE. wipeEnergy()/wipeEnergyMagic() ([DECOMP] TrollChallengeController.cs:738-744,
    // :754-762) set curEnergy = 0 (the combined wipe zeroes curMagic too), stalling EVERY beard on
    // that bar until it refills to cap. Ordinary allocation does NOT break the gate — every add*
    // draws from idleEnergy only (e.g. WishesController.cs:910-915), and the bar the beards read is
    // curEnergy. A Troll-specific kill nothing else triggers; the reason string names it so the
    // surfacing layer can tell "wiped" from "still refilling".
    public static class BeardGate
    {
        public struct BeardState
        {
            // beards.disabled ([DECOMP] Beards.cs:17) — polled, no event. For beards the refusal is
            // final for the run: the flag clears only at the rebirth (resetTrolls, Character.cs:
            // 928-932) that also discards the temp levels accrued while it was set.
            public bool Disabled;
            public double CurBar;   // curEnergy | magic.curMagic — whichever side usesEnergy[id] picks
            public double CapBar;   // totalCapEnergy() | totalCapMagic()
            public FeasibilityPass.ExternalConstraints External;
        }

        public static FeasibilityPass.Verdict Feasible(in BeardState s)
        {
            var v = FeasibilityPass.ExternalGate(s.External);
            if (!v.Seated)
                return v;

            v = FeasibilityPass.TrollGate(s.Disabled, "beards.disabled");
            if (!v.Seated)
                return v;

            // The game's comparator is strict < to refuse, so a bar exactly at cap ticks.
            if (s.CurBar >= s.CapBar)
                return FeasibilityPass.Verdict.Seat();

            if (s.CurBar <= 0)
                return FeasibilityPass.Verdict.Refuse(
                    "bar at zero — wipeEnergy()/wipeEnergyMagic() stalls every beard until the bar refills to cap");

            return FeasibilityPass.Verdict.Refuse("bar below cap — beards tick only with the pool bar full");
        }
    }
}
