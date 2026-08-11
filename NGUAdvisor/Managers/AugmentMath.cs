using System;
using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // BestAug's and AugmentBP's Unity-free decision core (audit 01 §3.4, extraction E1).
    //
    // PARITY ONLY. Every function here is a verbatim move of code that used to live inside
    // AllocationProfiles/Breakpoints/ResourceBreakpoints/{BestAug,AugmentBP}.cs, with the live
    // `_character.…` reads lifted out into the caller and handed in as plain data. Nothing was
    // corrected on the way through, including the things a reader will notice are odd — see the
    // CHARACTERISED notes below. Fixing any of them changes allocation and is out of scope.
    //
    // Why this one first: 05 §5 records BestAug.ProjectedGain as "the most complete value model in
    // either pool" and the natural template for M0 (a common value unit). M0 has to be written against
    // something testable, so this is the model that has to come out of the Character weld first.
    public static class AugmentMath
    {
        // How far ahead the projection looks (BestAug.MaxHorizon). An hour is long enough that a slow,
        // steep aug can show its value and short enough that the linear cost model stays honest.
        public const double MaxHorizon = 3600.0;

        // One augment PAIR as the decision sees it. The caller resolves every field from the live
        // AugmentController/augs[] before calling; the core never reads game state.
        //
        // AugSecPerLevel / UpgSecPerLevel are AugTimeLeftEnergyMax() evaluated AT THIS PAIR'S SHARE —
        // i.e. the caller must have already run Split()+Share() to know what allocation to price at.
        // That ordering is load-bearing and is preserved from the original loop.
        public struct AugPairState
        {
            public int Index;              // 0..6, the augment pair id
            public bool AugLive;           // LiveHalves(): aug half can still take energy
            public bool UpgLive;           // LiveHalves(): upgrade half can still take energy
            public double Tier;            // aug.augTierBonus()
            public double BaseBoost;       // aug.baseBoost
            public double AugLevel;        // augs[i].augLevel
            public double UpgradeLevel;    // augs[i].upgradeLevel
            public float AugProgress;      // augs[i].augProgress
            public float UpgradeProgress;  // augs[i].upgradeProgress
            public double AugSecPerLevel;  // Math.Max(0.01, aug.AugTimeLeftEnergyMax(share)) or 0 when dead
            public double UpgSecPerLevel;  // Math.Max(0.01, aug.UpgradeTimeLeftEnergyMax(share)) or 0 when dead
            public double AugCost;         // aug.getAugCost()
            public double UpgradeCost;     // aug.getUpgradeCost()
            public double TotalStatBoostNow; // aug.getTotalStatBoost()
        }

        // What AllocatePairs decided. Found=false is the original's `bestAugment == -1` (allocate nothing).
        public struct BestAugPick
        {
            public bool Found;
            public int Index;
            public double Value;
            public bool AugLive;
            public bool UpgLive;
            public float AugRatio;
            public float UpgRatio;
        }

        // Seconds of run to project over, capped at MaxHorizon and by the rebirth when the profile
        // schedules one. `rebirthTargetSec` is Main.Profile.NextRebirthTargetSeconds() (<= 0 = none);
        // `nowSec` is character.rebirthTime.totalseconds.
        //
        // toRebirth means the horizon ENDS at the rebirth, which is what makes a level still in flight
        // there worth nothing (see LevelsInHorizon). Past the deadline the rebirth can still be blocked
        // — NUMBER/BOSSNUM targets are floors, not deadlines, and locks or the No-Rebirth challenge can
        // hold it — so the run continues and we keep funding on the full horizon rather than going dark.
        public static double Horizon(bool autoRebirth, double rebirthTargetSec, double nowSec, out bool toRebirth)
        {
            toRebirth = false;
            if (!autoRebirth) return MaxHorizon;
            if (rebirthTargetSec <= 0) return MaxHorizon;

            double left = rebirthTargetSec - nowSec;
            if (left <= 0 || left >= MaxHorizon) return MaxHorizon;
            toRebirth = true;
            return left;
        }

        // Levels this half gains in `horizon` seconds. The level in flight lands after `secLeft` (its
        // progress is already banked); every level after it costs c x (L+1), because the game's cost is
        // linear in the level (getAugProgressPerTick divides by level+1). With c = secPerLevel/(level+1)
        // the time for n more levels is c * (n*(level+1) + n(n+1)/2); invert for n.
        //
        // completedOnly FLOORS the result. The game pays stat boost per COMPLETED level (augLevel is an
        // integer; augProgress only carries within a run), so at the rebirth a level still in flight is
        // wiped and worth nothing. Mid-run the fraction is real: the progress is banked and the next
        // pass resumes it, so it is priced as-is.
        public static double LevelsInHorizon(double secPerLevel, double secLeft, double level, double horizon, bool completedOnly)
        {
            if (secPerLevel <= 0 || horizon <= 0) return 0;
            if (secLeft <= 0 || secLeft > secPerLevel) secLeft = secPerLevel;   // no/odd progress data

            double n;
            if (horizon <= secLeft)
            {
                n = horizon / secLeft;   // still inside the level in flight
            }
            else
            {
                double c = secPerLevel / (level + 1.0);
                double b = 2.0 * (level + 1.0) + 1.0;
                double t = horizon - secLeft;
                n = 1.0 + (-b + Math.Sqrt(b * b + 8.0 * t / c)) / 2.0;
            }
            if (completedOnly) n = Math.Floor(n);
            return n > 0 ? n : 0;
        }

        // Energy split by elasticity: boost goes as augLevel^tier x upgradeLevel^2, so the exponents
        // tier and 2 are the shares. A dead half yields its share to the live one.
        public static void Split(double tier, bool augLive, bool upgLive, out float augRatio, out float upgRatio)
        {
            if (augLive && upgLive)
            {
                augRatio = (float)(tier / (2.0 + tier));
                upgRatio = (float)(2.0 / (2.0 + tier));
            }
            else
            {
                augRatio = augLive ? 1f : 0f;
                upgRatio = upgLive ? 1f : 0f;
            }
        }

        // A half's slice of the lane budget. Note the Math.Max(1, …): a live half always gets at least
        // one unit, even when MaxAllocation * ratio truncates to zero.
        public static long Share(long maxAllocation, float ratio) =>
            ratio <= 0 ? 0 : Math.Max(1, (long)(maxAllocation * ratio));

        // Stat boost this pair would hold at the end of the horizon, minus what it holds now. The boost
        // formula is the game's own (AugmentController.getTotalStatBoost):
        //     baseBoost x (upgradeLevel^2 + 1) x augLevel^augTierBonus
        //
        // CHARACTERISED — this is an ABSOLUTE delta in augment stat-boost points, not a per-energy
        // figure, and it is not comparable to NguValueMath's dimensionless ratio (05 §5). Making the two
        // commensurable is M0's job, not this extraction's.
        public static double ProjectedGain(in AugPairState p, double horizon, bool toRebirth)
        {
            double augLeft = p.AugSecPerLevel * (1.0 - p.AugProgress);
            double upgLeft = p.UpgSecPerLevel * (1.0 - p.UpgradeProgress);

            double newAug = p.AugLive ? p.AugLevel + LevelsInHorizon(p.AugSecPerLevel, augLeft, p.AugLevel, horizon, toRebirth) : p.AugLevel;
            double newUpg = p.UpgLive ? p.UpgradeLevel + LevelsInHorizon(p.UpgSecPerLevel, upgLeft, p.UpgradeLevel, horizon, toRebirth) : p.UpgradeLevel;

            double projected = p.BaseBoost * (Math.Pow(newUpg, 2.0) + 1.0) * Math.Pow(newAug, p.Tier);
            return projected - p.TotalStatBoostNow;
        }

        // Gold gate on the half we would actually start. A level already in progress, or one about to
        // land, is worth waiting on; a cold one we cannot pay for is not. Returns true to SKIP the pair.
        //
        // CHARACTERISED — three quirks preserved verbatim:
        //  1. `upgLive ? upgrade… : aug…` reads the UPGRADE half whenever it is live, even when the aug
        //     half is live too, so a live pair is always priced on its upgrade side.
        //  2. `time` is the max of the two half-times but `cost` mixes it with one half's gold cost.
        //  3. Math.Max(1, 1.0/time) is "gold for roughly one second of running", so a fast half is
        //     priced at 1x its cost and a slow one at 1x as well (1/time < 1 whenever time > 1s).
        public static bool GoldGateBlocks(in AugPairState p, double gold)
        {
            double time = Math.Max(p.AugSecPerLevel, p.UpgSecPerLevel);
            double cost = Math.Max(1, 1.0 / time) * (p.UpgLive ? p.UpgradeCost : p.AugCost);
            float progress = p.UpgLive ? p.UpgradeProgress : p.AugProgress;
            double augLeft = p.AugSecPerLevel * (1.0 - p.AugProgress);
            double upgLeft = p.UpgSecPerLevel * (1.0 - p.UpgradeProgress);
            double timeRemaining = p.UpgLive ? upgLeft : augLeft;
            return cost > gold && (progress == 0f || timeRemaining < 10);
        }

        // The ranking itself. Pairs with neither half live are expected to have been filtered by the
        // caller (they cannot be priced), but a dead pair here is skipped anyway rather than throwing.
        //
        // CHARACTERISED — `value > bestValue` starting from 0.0 means (a) strictly-positive gains only,
        // so a pair whose projected boost is flat or negative can never win, and (b) FIRST index wins a
        // tie. Both are load-bearing to the current in-game behaviour.
        public static BestAugPick PickBest(IList<AugPairState> pairs, double gold, double horizon, bool toRebirth)
        {
            var best = new BestAugPick { Index = -1 };
            double bestValue = 0.0;
            if (pairs == null) return best;

            for (int i = 0; i < pairs.Count; i++)
            {
                var p = pairs[i];
                if (!p.AugLive && !p.UpgLive) continue;
                if (GoldGateBlocks(p, gold)) continue;

                double value = ProjectedGain(p, horizon, toRebirth);
                if (value > bestValue)
                {
                    bestValue = value;
                    Split(p.Tier, p.AugLive, p.UpgLive, out float augRatio, out float upgRatio);
                    best = new BestAugPick
                    {
                        Found = true,
                        Index = p.Index,
                        Value = value,
                        AugLive = p.AugLive,
                        UpgLive = p.UpgLive,
                        AugRatio = augRatio,
                        UpgRatio = upgRatio
                    };
                }
            }
            return best;
        }

        // ---------------------------------------------------------------------------------------
        // AugmentBP's own surface: the manual AUG-n lane's unlock/target predicates and the cap
        // arithmetic both lanes share.
        // ---------------------------------------------------------------------------------------

        // ⚠ D1 IS REVERSED (amendment 30). The advisor does NOT fund augments during the No Augs
        // challenge. This predicate is [DECOMP] ButtonShower.cs:199-203 IN FULL, both terms:
        //     if (character.bossID < 17 || character.challenges.noAugsChallenge.inChallenge)
        //     { augmentation.interactable = false; augmentationText.text = "Really Locked"; }
        //
        // 21 §C2's MECHANICAL finding is NOT overturned and is worth keeping in view, because it is
        // what makes this predicate a decision rather than a transcription: the lock really is a
        // non-interactable menu button and nothing else. Every `noAugsChallenge` reference in
        // AllAugsController.cs is a COMPLETION REWARD (:80, :81-83, :85-87, :103), none tests
        // inChallenge, advanceAug()/advanceUpgrade()/the addEnergy* entry points carry no noAugs term,
        // and AllChallengesController.cs:119-121 calls failedChallenge() from the manual-quit path
        // ("It's okay to be a wuss"), not from a violation detector. So an external allocator would
        // bypass the lock undetected, keep the augment bonus, and still collect the completion
        // rewards. [OPERATOR]: a stated rule the game does not enforce is still a rule. The advisor
        // does not do that. (amendment 30 §2.1.)
        //
        // ONE PREDICATE, BOTH LANES — that part of D1's implementation survives the reversal and is
        // the reason the reversal is one line. The refusal used to be written TWICE: the explicit
        // `!noAugsChallenge.inChallenge` guard AND the `buttons.augmentation.interactable` read on the
        // line above it are THE SAME LOCK, because :199 is what sets that flag false. Deleting either
        // one alone changed nothing. AugmentBP.cs and BestAug.cs both route through here instead, so
        // the lock now has exactly one copy in this codebase and it is this line.
        //
        // Outside the challenge this is byte-for-byte the pre-D1 behaviour (`bossID >= 17` is exactly
        // `!(bossID < 17)`, and `buttons.augmentation.interactable` was false on nothing else). The
        // surfacing below is not optional: a lane going quiet is indistinguishable from a lane that
        // broke (25 §4, at two hours' cost).
        public static bool AugmentMenuUnlocked(long bossID, bool noAugsInChallenge) =>
            bossID >= 17 && !noAugsInChallenge;

        // The refusal's surfacing line. Pure, so the wording and the once-per-entry rule are testable;
        // the LATCH is session state and lives in the caller (same split as NGUAdvisors' incumbent
        // set). Returns null when there is nothing to say — not in the challenge, or already said.
        public static string NoAugsSurfacingLine(bool inChallenge, bool alreadySurfaced)
        {
            if (!inChallenge || alreadySurfaced) return null;
            return "No Augs Challenge active — augments not funded. The game's only enforcement is " +
                   "the greyed-out menu button ([DECOMP] ButtonShower.cs:199) and no detector was " +
                   "found, but the challenge's stated rule is still the rule: the advisor honours it " +
                   "(amendment 30, reversing D1).";
        }

        // AugmentBP.Unlocked()'s index half. The caller still owns the live gates it ANDs with
        // (AugmentMenuUnlocked over character.bossID and the challenge flag) because those are pure
        // Character reads with no arithmetic in them.
        //
        // `index` here is the FLAT half-index: even = augment half of pair index/2, odd = upgrade half.
        public static bool AugmentIndexUnlocked(int index, long bossID, long augBossRequired, long upgradeBossRequired)
        {
                       // `index < 0`: ParseBreakpointArray yields Index = -1 for a malformed AUG token, and
            // C# negative modulo means -1 % 2 == -1, not 1 — so -1 failed the `== 0` test, took the
            // UPGRADE branch, and returned true whenever bossID > upgradeBossRequired. No throw:
            // the lane silently entered the priority list and inflated the prioCount divisor for
            // every other energy lane, same shape as the RIT-7 defect. (advisors/02:718 class)
            if (index < 0 || index > 13) return false;
            return index % 2 == 0 ? bossID > augBossRequired : bossID > upgradeBossRequired;
        }

        // AugmentBP.TargetMet(). A target of 0 means "no target", so the lane never reports done.
        // CHARACTERISED: unlike NGUBP and AdvancedTrainingBP, there is no `target < 0 => true` branch
        // here, so a negative augmentTarget is treated as "no target" rather than "never fund".
        public static bool AugmentTargetMet(int index, long target, long level) =>
            target != 0 && level >= target;

        // ---------------------------------------------------------------------------------------
        // BestAug.TargetMet() — the P1 wiring (decision record amendment 16 §7).
        // ---------------------------------------------------------------------------------------

        // One pair as the DONE question sees it. Every field is a game predicate the caller already
        // owns: AugmentController.augLocked() / upgradeLocked() and hitAugmentTarget() /
        // hitUpgradeTarget() ([DECOMP] AugmentController.cs:160-186). Nothing here is computed.
        public struct AugPairTargetState
        {
            public bool AugLocked;         // aug.augLocked()        — bossID <= augBossRequired
            public bool AugHitTarget;      // aug.hitAugmentTarget() — 0 target reads as NOT hit
            public bool UpgradeLocked;     // aug.upgradeLocked()
            public bool UpgradeHitTarget;  // aug.hitUpgradeTarget()
        }

        // A half is live iff the game would still let energy into it: not locked out by boss and not
        // already at its declared target. This is LiveHalves() as one expression, so the ranking's
        // notion of "live" and the lane's notion of "done" cannot drift apart.
        public static bool HalfLive(bool locked, bool hitTarget) => !locked && !hitTarget;

        // BestAug ranks ALL SEVEN pairs, so unlike AugmentBP it is not done when one half is done —
        // it is done only when NO half of ANY pair is live. `useUpgrades` is BestAug's bossID >= 37
        // gate: below it the upgrade halves are never funded, so they cannot keep the lane alive.
        //
        // Note what does NOT make this true: hitAugmentTarget() returns FALSE for a target of 0
        // ([DECOMP] AugmentController.cs:171-177), so an operator who declares no targets keeps the
        // pre-P1 behaviour exactly — the lane still never reports done. The signal is opt-in on the
        // targets the operator actually types, plus the all-locked case that used to burn a priority
        // seat while allocating nothing.
        public static bool BestAugTargetMet(IList<AugPairTargetState> pairs, bool useUpgrades)
        {
            // Caller contract, not a game state: a null list falls back to the pre-P1 answer rather
            // than silently retiring the lane.
            if (pairs == null) return false;
            for (var i = 0; i < pairs.Count; i++)
            {
                if (HalfLive(pairs[i].AugLocked, pairs[i].AugHitTarget)) return false;
                if (useUpgrades && HalfLive(pairs[i].UpgradeLocked, pairs[i].UpgradeHitTarget)) return false;
            }
            return true;
        }

        // Everything CalculateAugCapCalc needs, with the difficulty branch already resolved by the
        // caller (SpeedDivider = the normal/evil/sadistic divider for this half; DividerScale = the
        // 50000.0 the normal and evil branches multiply in, or 1.0 on sadistic).
        public struct AugCapInputs
        {
            public double Level;            // augLevel or upgradeLevel of THIS half
            public int Offset;              // 500 on the first pass, CapCalc.Offset on the second
            public double TotalEnergyPower;
            public double SpeedDivider;
            public double DividerScale;
            public double AugsSpecBonus;    // inventoryController.bonuses[specType.Augs]
            public double MacguffinBonus;   // inventory.macguffinBonuses[12]
            public double HackAugSpeed;     // hacksController.totalAugSpeedBonus()
            public double ItopodAugSpeed;   // adventureController.itopod.totalAugSpeedBonus()
            public double CardAugSpeed;     // cardsController.getBonus(cardBonus.augSpeed)
            public double NoAugsEvilCompletions;   // double, not int: the game's count widens straight
                                                   // into `1.0 + n * 0.05` with no narrowing step.
            public bool NoAugsCompletedOnce;
            public bool NoAugsEvilMaxed;
            public bool Sadistic;           // rebirthDifficulty >= sadistic
            public double SadisticDivider;  // augments[i].sadisticDivider(), only read when Sadistic
            public float Allocation;
            public long IdleEnergy;
        }

        // Verbatim AugmentBP.CalculateAugCapCalc arithmetic. Returns (Num, PPT); the two-pass wrapper
        // that re-runs it at CapCalc.Offset stays in the lane.
        //
        // CHARACTERISED: 1.00000202655792 is the game-verbatim stair-snap epsilon and must not be
        // "cleaned up" to 1.000002 — that is a DIFFERENT literal already used by WandoosBP (02 §12.4).
        public static AugCapResult AugCap(in AugCapInputs a)
        {
            double num1 = 1 / (a.TotalEnergyPower / (a.Level + 1.0 + a.Offset));
            num1 *= a.DividerScale * a.SpeedDivider;

            num1 /= 1.0 + a.AugsSpecBonus;
            num1 /= a.MacguffinBonus;
            num1 /= a.HackAugSpeed;
            num1 /= a.ItopodAugSpeed;
            num1 /= a.CardAugSpeed;
            num1 /= 1.0 + a.NoAugsEvilCompletions * 0.05;

            if (a.NoAugsCompletedOnce)
                num1 /= 1.1000000238418579;

            if (a.NoAugsEvilMaxed)
                num1 /= 1.25;

            if (a.Sadistic)
                num1 *= a.SadisticDivider;

            num1 = Math.Ceiling(num1);

            if (num1 < 1.0)
                num1 = 1.0;

            double num = Math.Ceiling(num1 / Math.Ceiling(num1 / a.Allocation) * 1.00000202655792);

            long num2 = num > a.IdleEnergy ? a.IdleEnergy : (long)num;

            return new AugCapResult { Num = num2, PPT = num / num1 };
        }

        // Mirrors AllocationProfiles.BreakpointTypes.CapCalc without depending on it, so this file
        // stays inside Managers/ and links cleanly. Offset uses the identical expression.
        public struct AugCapResult
        {
            public long Num;
            public double PPT;
            public int Offset => (int)Math.Floor(PPT * 50 * 10);
        }
    }
}
