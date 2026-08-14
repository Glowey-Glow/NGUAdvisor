using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NGUAdvisor.Managers
{
    // ONE-SHOT capture of Unity-scene-serialized game constants -> inject.log.
    //
    // Every value below is a `public` field on a scene MonoBehaviour with no source initialiser: it
    // exists only in the Unity scene, so the decompile shows the declaration and never the number.
    // The capture spec (audit/decisions/constant-capture-spec.md) lists exactly which ones, and this
    // reads that list and nothing else.
    //
    // SECOND BATCH (audit/09 §9, decisions/G1-D3-V9-amendment-07 §9) adds the R3 half:
    //   P0  HACKEFF[*]  — baseEffectPerLevel / milestoneEffect / milestoneThreshold, all 16 slots,
    //                     read from the SAME HacksController.properties loop as baseDivider.
    //   P1  W1[*]       — WishesController.properties: wishSpeedDivider + maxLevel, all 231.
    //       P1.perk[*]  — ItopodPerkController effectPerLevel + level cap, perks 113/114/115/217/218/219.
    //       Q1.quirk[*] — BeastQuestPerkController level cap, quirks 57/58/59/60/174/175.
    //       W2.wish[*]  — max levels of wishes 76/77/78.
    // The twelve P1 caps plus those three wish maxima are the complete set of bounds on the
    // milestoneThreshold reducers, i.e. the only data that can settle 09 §A4's divide-by-zero.
    //
    // THIRD BATCH (audit/15 §Capture items 1-2, decisions/G1-D3-V9-amendment-14 §8) adds the two
    // remaining live WishProperties fields — difficultyRequirement and effectPerLevel, all 231 —
    // read from the SAME W1 loop, exactly as the R3 batch's P0 rode the existing HACK loop.
    //
    // FOURTH BATCH (audit/15 §Capture items 3-4, decisions/G1-D3-V9-amendment-14 §8 P1) adds the
    // wish-TIME half, which is a different set of perks from the third batch's wish-VALUE half:
    //   P2.perk[*]   — ItopodPerkController maxLevel / capLevel / effectPerLevel for perks
    //                  108 (wish1(), the totalWishSpeedBonus base), 109 and 110 (the two 24 s/level
    //                  minimumWishTime reducers) and 155/156/159/160 (the four perkEffect factors of
    //                  totalWishSpeedBonus).
    //   Q2.quirk[54] — BeastQuestPerkController maxLevel / capLevel for the third 24 s/level
    //                  minimumWishTime reducer.
    // Perks 109, 110 and quirk 54 are the ONLY three terms of minimumWishTime()'s subtrahend
    // (WishesController.cs:739-745), so their three caps are the complete and only data that can
    // settle amendment 14 §6 — whether the combined reducer total can reach 600 levels and make
    // `1f / (num * 50f)` divide by zero or go negative. Same question, and settled the same way, as
    // 11 §B settled 09 §A4's hack divide-by-zero: sum the caps and compare.
    //
    // Constraints this file is built to (spec "Instrument constraints"):
    //   1. One-shot, called once from Main.Start. NOT per-tick telemetry, NOT a static constructor —
    //      a throw in a type-initializer that has captured Main.Character poisons the type for the
    //      whole process (01-architecture-decision §4.3). Hence: no static field initialisers here,
    //      everything is a local inside Run().
    //   2. CultureInfo.InvariantCulture on every number written.
    //   3. Full precision. "G9" round-trips a float and "G17" a double on .NET Framework; "R" is
    //      documented as NOT reliably round-tripping Single on this runtime, so it is deliberately
    //      not used. A rounded `f` propagates into 48 NGU curves.
    //   4. List LENGTH is logged next to the contents — a silently truncated list is worse than none.
    //   5. One tagged, greppable block, so a future capture after a game patch can be diffed.
    //
    // Every item is individually guarded: a null or a missing member yields "ERROR: ..." on that line
    // and the rest of the block still runs. A missing value is a finding, so it must be visible.
    internal static class ConstantCapture
    {
        private const string Tag = "[ConstCap]";

        public static void Run()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            try
            {
                var c = Main.Character;
                if (c == null) { Main.Log(Tag + " ABORT character=NULL"); return; }

                Main.Log(Tag + " BEGIN");
                Emit(inv, "META.gameBuild", () => c.getVersion().ToString(inv));
                Emit(inv, "META.advisorBuild", () => Main.DisplayVersion + "/" + Main.BuildTag);
                Emit(inv, "META.rebirthDifficulty", () => c.settings.rebirthDifficulty.ToString());
                Emit(inv, "META.nguLevelTrack", () => c.settings.nguLevelTrack.ToString());

                CaptureHacks(inv, c);
                CaptureNgu(inv, c);
                CaptureTimeMachine(inv, c);
                CaptureAdvancedTraining(inv, c);
                CaptureAugments(inv, c);
                CaptureBlood(inv, c);
                CaptureVerification(inv, c);

                // R3 batch (audit/09 §9, amendment 07 §9). P1 only — the three P0 hack fields ride
                // the existing CaptureHacks loop above, which is the whole point of the addendum.
                CaptureWishes(inv, c);
                CaptureReducerCaps(inv, c);

                // Wish-time batch (audit/15 §Capture items 3-4, amendment 14 §8 P1). Same two
                // controllers CaptureReducerCaps already reads, different perk/quirk ids.
                CaptureWishTimePerks(inv, c);

                Main.Log(Tag + " END");
            }
            catch (Exception e)
            {
                try { Main.Log(Tag + " FATAL " + e); } catch { }
            }
        }

        // PRIORITY. HacksController.properties[i].baseDivider is the hack price ladder. The earlier
        // harvest (2026-07-31) back-solved it from progressPerTickCap at level 0, which stops working
        // the moment a hack leaves level 0 — hack 0 was already L38 and had to be estimated. The
        // serialized field itself is level-independent and is read here directly, so the ladder is
        // recoverable at any progression state after all.
        //
        // R3 batch P0 (09 §9): the same row also carries the three fields that are the entire
        // remaining A4 gap — baseEffectPerLevel, milestoneEffect, milestoneThreshold. All three are
        // public fields on the same HackProperties object already being read for baseDivider, so this
        // is three more reads inside an existing loop, not a second instrument.
        //   baseEffectPerLevel / milestoneEffect  float  -> G9
        //   milestoneThreshold                    long   -> exact, no format specifier
        // (HackProperties.cs:4-17. hackBonus() = (1 + L*baseEffectPerLevel) * milestoneEffect^m,
        //  m = floor(L / milestoneThreshold(id)); HacksController.cs:415-428.)
        private static void CaptureHacks(CultureInfo inv, Character c)
        {
            Emit(inv, "HACK.count", () =>
            {
                var p = c.hacksController.properties;
                return p == null ? "NULL" : p.Count.ToString(inv);
            });
            for (int i = 0; i < 32; i++)
            {
                int id = i;
                List<HackProperties> props = null;
                try { props = c.hacksController.properties; } catch { }
                if (props == null || id >= props.Count) break;
                Emit(inv, "HACK[" + id.ToString(inv) + "].baseDivider", () =>
                {
                    var p = c.hacksController.properties[id];
                    if (p == null) return "NULL";
                    return F(inv, p.baseDivider) + " name=\"" + (p.hackName ?? "NULL") + "\"";
                });
                Emit(inv, "HACKEFF[" + id.ToString(inv) + "]", () =>
                {
                    var p = c.hacksController.properties[id];
                    if (p == null) return "NULL";
                    return "baseEffectPerLevel=" + F(inv, p.baseEffectPerLevel)
                         + " milestoneEffect=" + F(inv, p.milestoneEffect)
                         + " milestoneThreshold=" + p.milestoneThreshold.ToString(inv)
                         + " name=\"" + (p.hackName ?? "NULL") + "\"";
                });
            }
        }

        // P1a — WishesController.properties, every slot. Wishes.wishSize() is a hardcoded 231
        // (Wishes.cs:19-22), so that is the expected length; anything shorter is a truncated read.
        //   maxLevel          long   -> exact. maxWishLevel(id) returns this field verbatim
        //                               (WishesController.cs:690-698) — no 0-means-uncapped rule here,
        //                               unlike the perk/quirk capLevel() below.
        //   wishSpeedDivider  float  -> G9. The accessor multiplies by (level+1)
        //                               (WishesController.cs:796-799); the serialized field is the base.
        // Wishes 76/77/78 are the three hack milestoneThreshold reducers (HacksController.cs:508,
        // :516, :524) and are re-emitted on their own line by CaptureReducerCaps for the DBZ question.
        //
        // THIRD BATCH (audit/15 §Capture items 1-2, decisions/G1-D3-V9-amendment-14 §8) adds the two
        // remaining live WishProperties fields to this same loop — two more reads, no new instrument:
        //   difficultyRequirement  enum difficulty {normal, evil, sadistic}  -> name AND ordinal.
        //       The name is the answer 15 §A3 wants; the ordinal is logged beside it because every
        //       enforcement site compares ORDINALLY against character.settings.rebirthDifficulty
        //       (WishesController.cs:895-899, :959-963, :1006-1010, :1108-1113), and because a scene
        //       value outside {0,1,2} would ToString() as a bare number and otherwise look like a name.
        //   effectPerLevel         float -> G9. The linear coefficient of wishEffect()
        //       (:1114) and the additive term of totalCardTagBonus() (:1513-1517). Unlike
        //       HackProperties.milestoneEffect (11 §F3) it is NEVER raised to a power, so its ~2e-8
        //       quantisation stays ~2e-8 and does not compound — but log it exact anyway (08 §F3).
        private static void CaptureWishes(CultureInfo inv, Character c)
        {
            Emit(inv, "W1.count", () =>
            {
                var p = c.wishesController.properties;
                return p == null ? "NULL" : p.Count.ToString(inv);
            });
            Emit(inv, "W1.wishSize", () => c.wishes.wishSize().ToString(inv));
            for (int i = 0; i < 512; i++)
            {
                int id = i;
                List<WishProperties> props = null;
                try { props = c.wishesController.properties; } catch { }
                if (props == null || id >= props.Count) break;
                Emit(inv, "W1[" + id.ToString(inv) + "]", () =>
                {
                    var w = c.wishesController.properties[id];
                    if (w == null) return "NULL";
                    return "maxLevel=" + w.maxLevel.ToString(inv)
                         + " wishSpeedDivider=" + F(inv, w.wishSpeedDivider)
                         + " effectPerLevel=" + F(inv, w.effectPerLevel)
                         + " difficultyRequirement=" + w.difficultyRequirement.ToString()
                         + "(" + ((int)w.difficultyRequirement).ToString(inv) + ")"
                         + " name=\"" + (w.wishName ?? "NULL") + "\"";
                });
            }
        }

        // P1b — the caps that bound every milestoneThreshold reducer, which is the only thing that can
        // answer 09 §A4's latent divide-by-zero: milestoneThreshold(id) is an unfloored `-=` and a
        // result of exactly 0 throws on a path Character.updateCharacter() reaches every frame.
        //
        // READ THE RAW FIELD, NOT capLevel(). Both controllers define
        //     capLevel(i) => maxLevel[i] == 0 ? long.MaxValue : maxLevel[i]
        // (ItopodPerkController.cs:193-204, BeastQuestPerkController.cs:220-231), i.e. a stored 0 means
        // UNCAPPED, not "cannot be levelled". A raw 0 on any of these twelve rows makes the reducer
        // unbounded and the divide-by-zero reachable. Both are logged so the distinction is visible.
        private static void CaptureReducerCaps(CultureInfo inv, Character c)
        {
            int[] perks = { 113, 114, 115, 217, 218, 219 };
            int[] quirks = { 57, 58, 59, 60, 174, 175 };

            Emit(inv, "P1.itopod.maxLevel.n", () =>
            {
                var l = c.adventureController.itopod.maxLevel;
                return l == null ? "NULL" : l.Count.ToString(inv);
            });
            Emit(inv, "P1.itopod.effectPerLevel.n", () =>
            {
                var l = c.adventureController.itopod.effectPerLevel;
                return l == null ? "NULL" : l.Count.ToString(inv);
            });
            for (int k = 0; k < perks.Length; k++)
            {
                int id = perks[k];
                Emit(inv, "P1.perk[" + id.ToString(inv) + "]", () =>
                {
                    var p = c.adventureController.itopod;
                    return "maxLevel=" + p.maxLevel[id].ToString(inv)
                         + " capLevel=" + p.capLevel(id).ToString(inv)
                         + " effectPerLevel=" + F(inv, p.effectPerLevel[id])
                         + " name=\"" + (p.perkName == null ? "NULL" : (p.perkName[id] ?? "NULL")) + "\"";
                });
            }

            Emit(inv, "Q1.beastQuest.maxLevel.n", () =>
            {
                var l = c.beastQuestPerkController.maxLevel;
                return l == null ? "NULL" : l.Count.ToString(inv);
            });
            for (int k = 0; k < quirks.Length; k++)
            {
                int id = quirks[k];
                Emit(inv, "Q1.quirk[" + id.ToString(inv) + "]", () =>
                {
                    var q = c.beastQuestPerkController;
                    return "maxLevel=" + q.maxLevel[id].ToString(inv)
                         + " capLevel=" + q.capLevel(id).ToString(inv)
                         + " name=\"" + (q.quirkName == null ? "NULL" : (q.quirkName[id] ?? "NULL")) + "\"";
                });
            }

            // The three wish-shaped reducers, called out separately from the W1 walk because they are
            // the ones that bound hacks 8, 11 and 13.
            int[] reducerWishes = { 76, 77, 78 };
            for (int k = 0; k < reducerWishes.Length; k++)
            {
                int id = reducerWishes[k];
                Emit(inv, "W2.wish[" + id.ToString(inv) + "]", () =>
                {
                    var w = c.wishesController.properties[id];
                    if (w == null) return "NULL";
                    return "maxLevel=" + w.maxLevel.ToString(inv)
                         + " maxWishLevel=" + c.wishesController.maxWishLevel(id).ToString(inv)
                         + " name=\"" + (w.wishName ?? "NULL") + "\"";
                });
            }
        }

        // P2/Q2 — the wish-TIME constants (audit/15 §Capture items 3-4, amendment 14 §8 P1).
        //
        // Two distinct questions, one loop, because both live on the same two controllers:
        //
        //   (a) amendment 14 §6 / 15 flag 4 — minimumWishTime() is
        //           1f / ((14400f - itopod.totalWishMinReduction() - beastQuest.totalWishMinReduction()) * 50f)
        //       and each reduction is 24 SECONDS PER LEVEL of perk 109, perk 110 and quirk 54
        //       (ItopodPerkController.cs:1879-1900, BeastQuestPerkController.cs:968-981). Each of the
        //       three floors its OWN term at 0, but `num` itself has no floor: 14400/24 = 600 combined
        //       levels makes num exactly 0f and the return +Infinity, and more than 600 makes it
        //       negative, at which point Math.Min(negative, raw) drives every wish's progress
        //       backwards (WishesController.cs:758). The caps decide whether that is reachable.
        //
        //   (b) 15 §B3's B term — itopod.totalWishSpeedBonus() is
        //           1f * wish1() * perkEffect(155) * perkEffect(156) * perkEffect(159) * perkEffect(160)
        //       (ItopodPerkController.cs:1860-1863), one factor of totalWishSpeedBonuses(), which
        //       enters the saturation condition under the 1/0.17 = 5.88 exponent. wish1() reads
        //       effectPerLevel[108] directly (:1865-1877); the four perkEffect() calls read
        //       effectPerLevel[155/156/159/160] (:802-814). Same linear 1 + L*e shape as wishEffect().
        //
        // READ THE RAW FIELD AS WELL AS capLevel(), for the reason CaptureReducerCaps gives: both
        // controllers define capLevel(i) => maxLevel[i] == 0 ? long.MaxValue : maxLevel[i]
        // (ItopodPerkController.cs:193-204, BeastQuestPerkController.cs:220-231), so a stored 0 means
        // UNCAPPED. A raw 0 on perk 109, perk 110 or quirk 54 makes the 600-level total reachable and
        // the flaw live; only the raw field can say so, and capLevel() alone would hide it.
        //
        // effectPerLevel is logged for 109 and 110 too, on the same row shape P1.perk[*] uses. Neither
        // minWish1() nor minWish2() reads it — both hardcode the 24 — so whatever is stored there is
        // inert, and that is worth showing rather than assuming.
        private static void CaptureWishTimePerks(CultureInfo inv, Character c)
        {
            // 108 = wish1 base; 109, 110 = the two itopod minimumWishTime reducers;
            // 155, 156, 159, 160 = the four perkEffect factors of totalWishSpeedBonus.
            int[] perks = { 108, 109, 110, 155, 156, 159, 160 };

            Emit(inv, "P2.itopod.maxLevel.n", () =>
            {
                var l = c.adventureController.itopod.maxLevel;
                return l == null ? "NULL" : l.Count.ToString(inv);
            });
            Emit(inv, "P2.itopod.effectPerLevel.n", () =>
            {
                var l = c.adventureController.itopod.effectPerLevel;
                return l == null ? "NULL" : l.Count.ToString(inv);
            });
            for (int k = 0; k < perks.Length; k++)
            {
                int id = perks[k];
                Emit(inv, "P2.perk[" + id.ToString(inv) + "]", () =>
                {
                    var p = c.adventureController.itopod;
                    return "maxLevel=" + p.maxLevel[id].ToString(inv)
                         + " capLevel=" + p.capLevel(id).ToString(inv)
                         + " effectPerLevel=" + F(inv, p.effectPerLevel[id])
                         + " name=\"" + (p.perkName == null ? "NULL" : (p.perkName[id] ?? "NULL")) + "\"";
                });
            }

            Emit(inv, "Q2.beastQuest.maxLevel.n", () =>
            {
                var l = c.beastQuestPerkController.maxLevel;
                return l == null ? "NULL" : l.Count.ToString(inv);
            });
            Emit(inv, "Q2.quirk[54]", () =>
            {
                var q = c.beastQuestPerkController;
                return "maxLevel=" + q.maxLevel[54].ToString(inv)
                     + " capLevel=" + q.capLevel(54).ToString(inv)
                     + " name=\"" + (q.quirkName == null ? "NULL" : (q.quirkName[54] ?? "NULL")) + "\"";
            });

            // The subtrahend arithmetic, evaluated by the instrument rather than by hand, so that the
            // 600-level verdict is read off the log instead of reconstructed from it. Uses the RAW
            // maxLevel values; a raw 0 is reported as such and deliberately NOT translated to
            // long.MaxValue here, because the sum is only meaningful if all three are finite.
            Emit(inv, "CHK.wishMinReducerSum", () =>
            {
                var p = c.adventureController.itopod;
                var q = c.beastQuestPerkController;
                long m109 = p.maxLevel[109];
                long m110 = p.maxLevel[110];
                long m54 = q.maxLevel[54];
                bool anyUncapped = m109 == 0L || m110 == 0L || m54 == 0L;
                string sum = anyUncapped
                    ? "UNCAPPED"
                    : (m109 + m110 + m54).ToString(inv);
                string secondsAtMax = anyUncapped
                    ? "UNCAPPED"
                    : ((m109 + m110 + m54) * 24L).ToString(inv);
                return "perk109=" + m109.ToString(inv)
                     + " perk110=" + m110.ToString(inv)
                     + " quirk54=" + m54.ToString(inv)
                     + " combinedMaxLevels=" + sum
                     + " secondsRemovedAtMax=" + secondsAtMax
                     + " levelsFor14400s=600"
                     + " reachesZero=" + (anyUncapped ? "YES(uncapped)" : ((m109 + m110 + m54) >= 600L ? "YES" : "NO"));
            });
        }

        // E1 (P0) and E2. Energy lists are expected to be 9 long (skills 0-8), magic 7 (magicSkills 0-6).
        private static void CaptureNgu(CultureInfo inv, Character c)
        {
            var n = TryGet(() => c.NGUController);
            Emit(inv, "E1.normalEnergyBoostFactor", () => FList(inv, n.normalEnergyBoostFactor));
            Emit(inv, "E1.evilEnergyBoostFactor", () => FList(inv, n.evilEnergyBoostFactor));
            Emit(inv, "E1.sadisticEnergyBoostFactor", () => FList(inv, n.sadisticEnergyBoostFactor));
            Emit(inv, "E1.normalMagicBoostFactor", () => FList(inv, n.normalMagicBoostFactor));
            Emit(inv, "E1.evilMagicBoostFactor", () => FList(inv, n.evilMagicBoostFactor));
            Emit(inv, "E1.sadisticMagicBoostFactor", () => FList(inv, n.sadisticMagicBoostFactor));

            Emit(inv, "E2.normalEnergyNGUDividers", () => FList(inv, n.normalEnergyNGUDividers));
            Emit(inv, "E2.evilEnergyNGUDividers", () => FList(inv, n.evilEnergyNGUDividers));
            Emit(inv, "E2.sadisticEnergyNGUDividers", () => FList(inv, n.sadisticEnergyNGUDividers));
            Emit(inv, "E2.normalMagicNGUDividers", () => FList(inv, n.normalMagicNGUDividers));
            Emit(inv, "E2.evilMagicNGUDividers", () => FList(inv, n.evilMagicNGUDividers));
            Emit(inv, "E2.sadisticMagicNGUDividers", () => FList(inv, n.sadisticMagicNGUDividers));
        }

        // E3 and E4.
        private static void CaptureTimeMachine(CultureInfo inv, Character c)
        {
            var t = TryGet(() => c.timeMachineController);
            Emit(inv, "E3.baseNormalSpeedDivider", () => F(inv, t.baseNormalSpeedDivider));
            Emit(inv, "E3.baseEvilSpeedDivider", () => F(inv, t.baseEvilSpeedDivider));
            Emit(inv, "E3.baseSadisticSpeedDivider", () => F(inv, t.baseSadisticSpeedDivider));
            Emit(inv, "E4.baseNormalGoldMultiDivider", () => F(inv, t.baseNormalGoldMultiDivider));
            Emit(inv, "E4.baseEvilGoldMultiDivider", () => F(inv, t.baseEvilGoldMultiDivider));
            Emit(inv, "E4.baseSadisticGoldMultiDivider", () => F(inv, t.baseSadisticGoldMultiDivider));
        }

        // E5. Five controllers, reached by name (AllAdvancedTraining has no indexer); each carries its
        // own `id`, which is logged so the row can be matched to advancedTraining.level[id].
        private static void CaptureAdvancedTraining(CultureInfo inv, Character c)
        {
            var a = TryGet(() => c.advancedTrainingController);
            Emit(inv, "E5.length", () => a.length.ToString(inv));
            EmitAt(inv, "E5.defense", () => a.defense);
            EmitAt(inv, "E5.block", () => a.block);
            EmitAt(inv, "E5.attack", () => a.attack);
            EmitAt(inv, "E5.wandoosEnergy", () => a.wandoosEnergy);
            EmitAt(inv, "E5.wandoosMagic", () => a.wandoosMagic);
        }

        private static void EmitAt(CultureInfo inv, string key, Func<AdvancedTrainingController> get)
        {
            Emit(inv, key, () =>
            {
                var t = get();
                if (t == null) return "NULL";
                return "id=" + t.id.ToString(inv)
                     + " baseTime=" + F(inv, t.baseTime)
                     + " levelFactor=" + F(inv, t.levelFactor)
                     + " name=\"" + (t.trainingName ?? "NULL") + "\"";
            });
        }

        // E6 (7 augment pairs) and E7 (6 divider lists, each expected 7 long).
        private static void CaptureAugments(CultureInfo inv, Character c)
        {
            var g = TryGet(() => c.augmentsController);
            Emit(inv, "E6.count", () => g.augments == null ? "NULL" : g.augments.Length.ToString(inv));
            for (int i = 0; i < 7; i++)
            {
                int id = i;
                Emit(inv, "E6[" + id.ToString(inv) + "]", () =>
                {
                    var au = g.augments[id];
                    if (au == null) return "NULL";
                    return "baseAugmentCost=" + F(inv, au.baseAugmentCost)
                         + " baseUpgradeCost=" + F(inv, au.baseUpgradeCost)
                         + " baseBoost=" + au.baseBoost.ToString(inv)
                         + " augBossRequired=" + au.augBossRequired.ToString(inv)
                         + " upgradeBossRequired=" + au.upgradeBossRequired.ToString(inv)
                         + " name=\"" + (au.augName ?? "NULL") + "\"";
                });
            }

            Emit(inv, "E7.normalAugSpeedDividers", () => FList(inv, g.normalAugSpeedDividers));
            Emit(inv, "E7.evilAugSpeedDividers", () => FList(inv, g.evilAugSpeedDividers));
            Emit(inv, "E7.sadisticAugSpeedDividers", () => FList(inv, g.sadisticAugSpeedDividers));
            Emit(inv, "E7.normalUpgradeSpeedDividers", () => FList(inv, g.normalUpgradeSpeedDividers));
            Emit(inv, "E7.evilUpgradeSpeedDividers", () => FList(inv, g.evilUpgradeSpeedDividers));
            Emit(inv, "E7.sadisticUpgradeSpeedDividers", () => FList(inv, g.sadisticUpgradeSpeedDividers));
        }

        // B1, B2, B3.
        private static void CaptureBlood(CultureInfo inv, Character c)
        {
            var b = TryGet(() => c.bloodMagicController);
            Emit(inv, "B1.normalSpeedDividers", () => FList(inv, b.normalSpeedDividers));
            Emit(inv, "B1.evilSpeedDividers", () => FList(inv, b.evilSpeedDividers));
            Emit(inv, "B1.sadisticSpeedDividers", () => FList(inv, b.sadisticSpeedDividers));

            Emit(inv, "B2.count", () => b.bloodMagics == null ? "NULL" : b.bloodMagics.Length.ToString(inv));
            Emit(inv, "B2.ritualsUnlocked", () => b.ritualsUnlocked().ToString(inv));
            for (int i = 0; i < 8; i++)
            {
                int id = i;
                Emit(inv, "B2[" + id.ToString(inv) + "]", () =>
                {
                    var r = b.bloodMagics[id];
                    if (r == null) return "NULL";
                    return "baseBoost=" + r.baseBoost.ToString(inv)
                         + " baseCost=" + F(inv, r.baseCost)
                         + " baseTime=" + F(inv, r.baseTime)
                         + " bossRequired=" + r.bossRequired.ToString(inv);
                });
            }

            Emit(inv, "B3.adventureSpellCooldown", () => b.spells.adventureSpellCooldown.ToString(inv));
        }

        // V1, V2, V3.
        private static void CaptureVerification(CultureInfo inv, Character c)
        {
            // V1 — the four BASE adventure fields the pill writes to, on one line.
            Emit(inv, "V1.adventureBase", () =>
                "attack=" + F(inv, c.adventure.attack)
              + " defense=" + F(inv, c.adventure.defense)
              + " maxHP=" + F(inv, c.adventure.maxHP)
              + " regen=" + F(inv, c.adventure.regen));

            // V2 — ironPillBonus() as it stands right now, alongside the difficulty that decides
            // whether castAdventurePowerupSpell applies it at all (>= evil).
            Emit(inv, "V2.ironPillBonus", () =>
                F(inv, c.adventureController.itopod.ironPillBonus())
              + " rebirthDifficulty=" + c.settings.rebirthDifficulty.ToString()
              + " appliedAtCurrentDifficulty=" + (c.settings.rebirthDifficulty >= difficulty.evil ? "YES" : "NO"));

            // V3 — the tooltip's number vs the number the cast actually writes, side by side and NOT
            // adjudicated. Both reproduce their source exactly:
            //   spellTooltip()               RebirthPowerSpell.cs:274-279  (no clamps)
            //   castAdventurePowerupSpell()  RebirthPowerSpell.cs:219-238  (clamped to [0, 1e8])
            Emit(inv, "V3.ironPill", () =>
            {
                double blood = c.bloodMagic.bloodPoints;
                float shown = (float)Math.Floor(Math.Pow(blood, 0.25));
                if (c.settings.rebirthDifficulty >= difficulty.evil)
                    shown *= c.adventureController.itopod.ironPillBonus();
                float written = shown;
                if (written >= 100000000f) written = 100000000f;
                if (written < 0f) written = 0f;
                return "blood=" + D(inv, blood)
                     + " tooltipValue=" + F(inv, shown)
                     + " tooltipDisplay=\"" + c.display(shown) + "\""
                     + " castWrites=" + F(inv, written)
                     + " castDisplay=\"" + c.display(written) + "\""
                     + " minAdventureBlood=" + D(inv, c.bloodMagicController.spells.minAdventureBlood())
                     + " cooldownElapsed=" + D(inv, c.bloodMagic.adventureSpellTime.totalseconds);
            });

            // The spec's mandatory follow-up ("Immediately afterward — one arithmetic check") evaluates
            // (0.05 x adventure.attack)^4 against "a blood pool reachable in a realistic cast window at
            // the measured R". R is that measurement and nothing else is added for it: one call to the
            // game's own totalBloodGainedPerSecond(), which is M1's realised value.
            Emit(inv, "CHK.bloodRate", () =>
                "R=" + D(inv, c.bloodMagicController.totalBloodGainedPerSecond())
              + " bloodPoints=" + D(inv, c.bloodMagic.bloodPoints)
              + " rebirthPower=" + D(inv, c.bloodMagic.rebirthPower)
              + " rebirthSeconds=" + D(inv, c.rebirthTime.totalseconds));
        }

        private static void Emit(CultureInfo inv, string key, Func<string> read)
        {
            string val;
            try { val = read(); }
            catch (Exception e) { val = "ERROR: " + e.GetType().Name + ": " + e.Message; }
            Main.Log(Tag + " " + key + " = " + val);
        }

        private static T TryGet<T>(Func<T> f) where T : class
        {
            try { return f(); } catch { return null; }
        }

        // "G9" is the shortest guaranteed round-trip for Single on .NET Framework; "G17" for Double.
        private static string F(CultureInfo inv, float v) => v.ToString("G9", inv);
        private static string D(CultureInfo inv, double v) => v.ToString("G17", inv);

        private static string FList(CultureInfo inv, List<float> l)
        {
            if (l == null) return "NULL";
            var sb = new StringBuilder();
            sb.Append("n=").Append(l.Count.ToString(inv)).Append(" [");
            for (int i = 0; i < l.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(F(inv, l[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
