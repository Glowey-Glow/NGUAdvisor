using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // WHICH BEARDS GET THE SLOTS, when there are fewer slots than beards.
    //
    // Unity-free so the order can be pinned headlessly the way HackPhase and LaneTargets are — the
    // whole point of this file is that the ORDER is assertable, because the order is the decision.
    //
    // ── THE ORDER [OPERATOR ruling 2026-08-18] ────────────────────────────────────────────────────
    //
    //   BEARd > Neckbeard > Beard Cage > (Reverse / LadyBeard, to the 1000 perm softcap)
    //         > Golden Beard (once TC7 is done) > Fu Manchu > Reverse > LadyBeard
    //
    // Reverse and LadyBeard appear TWICE and that is the shape of the rule, not a typo: they are a
    // BOUNDED PUSH first and an unbounded tail afterwards, the same structure MILEHACK gives the hack
    // sweep. While they are under the softcap they outrank Golden and Fu Manchu; once they cross it
    // they fall to the bottom. Each still ends up in the list exactly once.
    //
    // ⚠ WHY 1000 IS A REAL NUMBER AND NOT A PREFERENCE. Every beard except Fu Manchu breaks at
    // `permLevel > 1000`, where the perm bonus stops being linear in the level and becomes a square
    // root ([DECOMP] AllBeardsController.permNumberBonus:449 and its six siblings —
    // `num2 > 1000 ? 1 + sqrt(num2)*31.7*c : 1 + num2*c`). The two forms agree AT 1000 because
    // 31.7 ~ sqrt(1000), so nothing is lost by stopping there and everything after it is bought at
    // sqrt rates. The gate is therefore `permLevel < 1000` — at exactly 1000 the NEXT level is 1001,
    // which is already on the far side of the break.
    //
    // ⚠ THE BREAK IS ON permLevel, NOT beardLevel. The temp bonus has its own 1000 break on the temp
    // level; this rule is about the permanent one, which is the half that survives a rebirth. Reading
    // the wrong field would push a beard that has already saturated the bonus being aimed at.
    //
    // Fu Manchu (id 0) has NO break at all — it is linear at 5%/1% forever ([DECOMP] :350). It sits
    // low anyway because its 1%/level perm coefficient is the smallest thing here that still counts,
    // but it is the one beard for which "past the softcap" never applies.
    //
    // ⚠ NOT A VALUE MODEL, AND DELIBERATELY SO. Unlike the hack and wish rankings this is not derived
    // from a marginal-density formula, because the seven beard bonuses multiply things that are not
    // commensurable at all — adventure stats, drop chance, NGU speed, NUMBER, Wandoos speed, gold.
    // This is an operator ruling about which of those matters, and it is recorded as one.
    public static class BeardPriority
    {
        public const int FuManchu  = 0;   // Magic  — Attack/Defense, linear forever, no break
        public const int Neckbeard = 1;   // Energy — Drop Chance
        public const int Reverse   = 2;   // Magic  — NUMBER
        public const int BeardCage = 3;   // Energy — NGU speed (both)
        public const int Lady      = 4;   // Magic  — Wandoos speed
        public const int Bear      = 5;   // Energy — Adventure stats
        public const int Golden    = 6;   // Magic  — Gold / Time Machine, needs Troll Challenge 7

        // permLevel strictly ABOVE this is on the square-root side of the curve.
        public const long PermSoftcap = 1000;

        public static bool UnderSoftcap(long permLevel) => permLevel < PermSoftcap;

        /// <summary>
        /// The full priority order. Callers take as many as they have slots for.
        /// Golden is OMITTED entirely, not demoted, when Troll Challenge 7 is not done — it cannot be
        /// activated at all, and leaving it in the list would silently cost a slot to nothing.
        /// </summary>
        public static int[] Order(long reversePerm, long ladyPerm, bool goldenUnlocked)
        {
            var o = new List<int>(7) { Bear, Neckbeard, BeardCage };

            // The bounded push. Reverse before Lady, matching the ruling's own order.
            if (UnderSoftcap(reversePerm)) o.Add(Reverse);
            if (UnderSoftcap(ladyPerm)) o.Add(Lady);

            if (goldenUnlocked) o.Add(Golden);
            o.Add(FuManchu);

            // The unbounded tail — whichever of the pair already crossed the softcap.
            if (!o.Contains(Reverse)) o.Add(Reverse);
            if (!o.Contains(Lady)) o.Add(Lady);

            return o.ToArray();
        }
    }
}
