namespace NGUAdvisor.Managers
{
    // Unity-free EXP pool split — the energy vs magic fractions the balancer targets (audit M5
    // migration). Encodes the guide's difficulty/phase rules as a pure function so they're
    // headless-tested and can't silently drift:
    //
    //   Base            -> 0.5 / 0.5  (an EVEN EXP split == the 3:1 stat-VALUE ratio, since magic units
    //                                  cost 3x energy).
    //   Normal, D4       -> 0.4 / 0.6 (guide ch.4: post-CBlock2 / T6v2 and before T6v4, 2:1 value toward
    //                                  magic).
    //   Evil, pre-T7, Ygg+EXP magic NGUs capped -> 1.0 / 0.0 ENERGY ONLY (guide ch.5: "Pre-T7: buy only
    //                                  Energy AFTER you can consistently cap the Ygg/EXP magic NGUs").
    //   Evil, pre-T7, still building magic       -> 0.5 / 0.5 (keep buying magic until those NGUs cap).
    //   Evil, post-T7    -> 0.5 / 0.5 (guide ch.5: "Post-T7: balance ratios back to 3:1 Energy:Magic").
    //   Sadistic / other -> base (no separate guidance encoded here yet).
    //
    // Inputs are plain bools so the caller does the (game-coupled) difficulty/titan/NGU reads and this
    // stays pure and testable. The gated energy-only case is the point: the balancer previously used
    // base 3:1 on Evil throughout, and an earlier fix went energy-only for ALL of pre-T7 (too early) —
    // it must wait until the Ygg/EXP magic NGUs can be capped.
    public static class ExpRatio
    {
        public static void Split(bool isEvil, bool t7Killed, bool magicNgusCapped, bool normalMagicSkew,
                                 out double pe, out double pm)
        {
            if (isEvil)
            {
                if (t7Killed) { pe = 0.5; pm = 0.5; }              // post-T7: back to the base 3:1 value ratio
                else if (magicNgusCapped) { pe = 1.0; pm = 0.0; } // pre-T7, Ygg+EXP capped: energy only
                else { pe = 0.5; pm = 0.5; }                       // pre-T7, still capping Ygg+EXP: base 3:1
                return;
            }

            if (normalMagicSkew) { pe = 0.4; pm = 0.6; }
            else { pe = 0.5; pm = 0.5; }
        }
    }
}
