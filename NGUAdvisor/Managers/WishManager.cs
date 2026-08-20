using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static NGUAdvisor.Main;

namespace NGUAdvisor.Managers
{
    public static class WishManager
    {
        // The one wish whose MULTIPLIER subtracts - respawn time ([DECOMP]
        // WishesController.respawn1, :1373), the only `1f - level * effectPerLevel` in that
        // controller. Not the only subtracting bonus in the game (wish 20 subtracts seconds,
        // hacks 76/77/78 and BeastQuest 80/81 subtract in their own units) - it is the only one
        // this ranking scores, which is why it is the only one special-cased.
        private const int ReducerWishId = 46;

        private static Character _character => Main.Character;
        private static readonly WishesController _wc = _character.wishesController;

        private static long energy;
        private static long magic;
        private static long res3;

        private static List<Wish> Wishes => _character.wishes.wishes;

        private static int MaxSlots()
        {
            var slots = _wc.curWishSlots();
            if (slots > Settings.WishLimit)
                slots = Settings.WishLimit;
            var validWishes = GetValidWishes().Count;
            if (slots > validWishes)
                slots = validWishes;
            return slots;
        }

        private static bool Allocated(Wish wish) => wish.energy > 0 || wish.magic > 0 || wish.res3 > 0;

        public static void UpdateWishMenu()
        {
            var filteredWishes = _wc.curValidUpgradesList;
            var pods = _wc.pods;

            if (pods.Count <= 0 || filteredWishes.Count <= 0 || Wishes.Count <= 0)
                return;

            int wishToSelect = _wc.curSelectedWish;

            int firstWishOnCurrentPage = pods[0].id;
            int wishPageIndex = 0;

            if (filteredWishes.Contains(firstWishOnCurrentPage))
                wishPageIndex = filteredWishes.IndexOf(firstWishOnCurrentPage);

            int pageNumber = wishPageIndex / pods.Count;

            if (!filteredWishes.Contains(wishToSelect) && !Allocated(Wishes[wishToSelect]))
                wishToSelect = filteredWishes.FirstOrDefault(x => Allocated(Wishes[x]));

            if (wishToSelect == 0)
                wishToSelect = filteredWishes[0];

            _wc.updateMenu();

            if (pageNumber > 0)
                _wc.changePage(pageNumber);

            if (wishToSelect != _wc.curSelectedWish)
                _wc.selectNewWish(wishToSelect);
        }

        // Runs once per allocation tick, AFTER the energy/magic/R3 swaps (CustomAllocation step
        // "Wishes (share of remaining idle)"), so the % sliders bite on what the other systems
        // actually left — not on a freshly reclaimed pool (audit/38 §E4.1; the old overCap spare
        // pass and the pre-swap percent pass are both gone). Wish holdings are invisible to every
        // lane reclaim (ConstraintLayerBridge.Reclaim, R3Breakpoints.RemoveR3 — none touch
        // wishesController), so the release below is the only per-tick release: wishes hand back
        // everything and re-take percent × (old holdings + fresh residue). The un-taken remainder
        // sits idle for one tick and the next swap reabsorbs it into the lanes, so a slider below
        // 100 bleeds wish funding back to the allocators until the two reach equilibrium.
        // Latch for the locked-system notice: once per entry into the state, not once per tick.
        private static bool _lockedNoticeGiven;

        /// <summary>
        /// Hand every wish holding back to the idle pools. Called BEFORE the E/M/R3 swaps in sink mode
        /// (CustomAllocation), so the lanes get to allocate from the released resource instead of it
        /// going straight back to the wish slots.
        /// </summary>
        /// <remarks>
        /// In priority mode this same call happens inside <see cref="Allocate"/>, after the swaps — and
        /// that ordering is precisely why the wish claim compounds there. Nothing else in the advisor
        /// releases wish holdings: ConstraintLayerBridge.Reclaim covers wandoos, augments, TM, AT, NGU
        /// and BT and never wishesController, so until a gear change makes the game call
        /// removeAllEnergyAndMagic(), what the wish slots take is simply gone from the lanes.
        /// </remarks>
        public static void ReleaseHoldings()
        {
            if (!Unlocked()) return;
            _wc.removeAllResources();
        }

        /// <summary>
        /// Is the wishes system actually unlocked on this save? Nothing else in the advisor asks.
        /// </summary>
        /// <remarks>
        /// ⚠ THE ADVISOR HAD NO UNLOCK GATE AT ALL, and neither of the obvious signals is one:
        /// `curWishSlots()` returns a MINIMUM OF 1 unconditionally (decomp WishesController) and
        /// `wishSize()` is a hardcoded 231. So "no wishes available" and "wishes not unlocked yet"
        /// were indistinguishable, and ManageWishes would happily run the pass either way.
        ///
        /// It cost nothing while the pass took a SLIDER share, because a pre-unlock operator's sliders
        /// sit at 0 and 0% allocates nothing. Wish SINK MODE removed that accident: it ignores the
        /// sliders by design and claims the whole remainder, so switching it on before the system is
        /// unlocked would route every spare unit of energy, magic and R3 into a system that cannot use
        /// it — and, worse, the pre-swap ReleaseHoldings would be reaching into that controller on
        /// every tick.
        ///
        /// Wishes unlock on a T8 titan kill (the game sets wishes.wishesOn at ItemController.cs:632-635,
        /// conditioned on hacks already being on). res3On is gated exactly this way in three places
        /// already (UiBridge.cs:1378, :3058, Main.cs:1188); this is the wish-side twin that was missing.
        /// </remarks>
        public static bool Unlocked()
        {
            try { return _character != null && _character.wishes != null && _character.wishes.wishesOn; }
            catch { return false; }   // a read that throws is not evidence the system is available
        }

        public static void Allocate()
        {
            // LOCKED SYSTEM: do nothing, and do not touch the controller. Said once, because a user who
            // has turned Manage Wishes on before the T8 kill should be told why nothing is happening
            // rather than left to infer it from an empty panel. See Unlocked().
            if (!Unlocked())
            {
                if (!_lockedNoticeGiven)
                {
                    _lockedNoticeGiven = true;
                    Main.Log("Wishes are managed but the system is not unlocked yet (it unlocks on a T8 "
                           + "titan kill) — the wish pass is standing down and no energy, magic or R3 "
                           + "is being routed to it.");
                }
                return;
            }
            _lockedNoticeGiven = false;   // re-arm, so a later relapse says so again

            // SINK MODE released before the swaps; releasing again here would be a no-op (the slots are
            // already empty) but it would also hide the ordering, and the ordering IS the feature.
            if (!Settings.WishSinkMode)
                _wc.removeAllResources();

            // The > idle clamps are not decorative: above 2^53 the double product loses exactness
            // and Ceiling can land one unit past the pool (pools legitimately exceed 1e18 under
            // potions — audit/15 §A1).
            // SINK: whatever the lanes could not use, and no percentage anywhere. The lanes have already
            // run against a pool that INCLUDED last tick's wish holdings (ReleaseHoldings ran before the
            // swaps), so a capped NGU lane has taken its fill and what is still idle is genuinely spare.
            // PRIORITY: the historical behaviour — a slider share of what is idle after the swaps.
            bool sink = Settings.WishSinkMode;

            long remainingEnergy = WishShareView.Offer(sink, _character.idleEnergy, Settings.WishEnergy);
            long remainingMagic = WishShareView.Offer(sink, _character.magic.idleMagic, Settings.WishMagic);
            long remainingRes3 = WishShareView.Offer(sink, _character.res3.idleRes3, Settings.WishR3);

            // WHAT WISHES WERE OFFERED OF THE R3 POOL, recorded because this instant cannot be
            // reconstructed afterwards: the number is a percentage of the idle pool AFTER the hacks
            // took their fill and AFTER the release above handed last tick's holdings back, and the
            // R3 bar regenerates into idle continuously, so anything computed later is a different
            // pool. R3PoolView pairs it with what the wishes end up holding, and the gap between the
            // two is the only thing that separates "the slider gave them nothing" from "they were
            // given a share and no slot could take it". Energy and magic need no equivalent: their
            // offers are already on ConstraintLayer.LaneDecision.Offered.
            R3PoolView.RecordWishShare(Settings.WishR3, remainingRes3);

            // ENERGY AND MAGIC: the same instant, for the same reason, but recorded as offer AND take.
            // The board draws E/M from the constraint layer's plan, which is built BEFORE this pass and
            // therefore knows only what wishes were offered — it cannot tell a 100% take from a 0% take.
            // Differencing the running remainder below is the only place the take exists. The record is
            // in a `finally` because the loop has two early returns and a pass that leaves through one
            // of them still consumed whatever it consumed; recording only on the fall-through path would
            // silently under-report exactly the "no slot could take it" case worth seeing.
            long offeredEnergy = remainingEnergy, offeredMagic = remainingMagic;
            // The pool the take is a SHARE OF, read here and nowhere else. removeAllResources() above
            // has already handed last tick's wish holdings back to idle, so this is the whole resource
            // in play — and it is far larger than the plan pool the swap allocated from, which was
            // measured while those holdings were still held. Reporting the take against the plan pool
            // is what produced a ~300% wish lane on an end-game save.
            long idleEnergyAtPass = _character.idleEnergy;
            long idleMagicAtPass = _character.magic.idleMagic;
            try
            {

            var validWishes = GetValidWishes();
            for (var slots = MaxSlots() - _wc.numAllocatedWishes(); slots > 0; slots--)
            {
                if (validWishes.Count <= 0)
                    return;

                energy = Math.Max(0L, remainingEnergy / slots + Math.Sign(remainingEnergy % slots));
                magic = Math.Max(0L, remainingMagic / slots + Math.Sign(remainingMagic % slots));
                res3 = Math.Max(0L, remainingRes3 / slots + Math.Sign(remainingRes3 % slots));
                if (energy <= 0L && magic <= 0L && res3 <= 0L)
                    return;

                int wishId = BestWishId(validWishes);
                if (wishId < 0)
                    continue;

                validWishes.Remove(wishId);

                AllocateToWish(wishId);
                var wish = Wishes[wishId];
                remainingEnergy -= wish.energy;
                remainingMagic -= wish.magic;
                remainingRes3 -= wish.res3;
            }

            }
            finally
            {
                WishShareView.Record(offeredEnergy, offeredEnergy - remainingEnergy, idleEnergyAtPass,
                                     offeredMagic, offeredMagic - remainingMagic, idleMagicAtPass);
            }
        }

        private static List<int> GetValidWishes()
        {
            bool diffCheck(int id) => _wc.properties[id].difficultyRequirement <= _character.settings.rebirthDifficulty;
            bool levelCheck(int id) => Wishes[id].level < _wc.properties[id].maxLevel;
            var validWishes = Enumerable.Range(0, _character.wishes.wishSize()).Where(id => diffCheck(id) && levelCheck(id));
            validWishes = validWishes.Except(Settings.WishBlacklist);
            return validWishes.ToList();
        }

        private static int BestWishId(List<int> wishIds)
        {
            var maxima = wishIds.Where(id => ProgressPerTick(id, out _) > 0);
            if (!maxima.Any())
                return -1;
            if (!Settings.WeakPriorities && Settings.WishMode > 0)
                maxima = maxima.AllMaxBy(id => Settings.WishPriorities.Contains(id));
            switch (Settings.WishMode)
            {
                case 1: // Cheapest
                case 3 when _wc.numAllocatedWishes() == 0 && MaxSlots() > 1: // Balanced, first slot
                    maxima = maxima.AllMinBy(id => _wc.wishSpeedDivider(id));
                    break;
                case 2: // Fastest
                    maxima = maxima.AllMaxBy(id => ProgressPerTick(id, out _) / (1f - Wishes[id].progress));
                    break;
                case 3: // Balanced
                    if (_wc.numAllocatedWishes() == MaxSlots() - 1) // Last slot
                        maxima = maxima.AllMaxBy(id => BaseProgressPerTick(id) <= _wc.minimumWishTime() * 1.1f);
                    maxima = maxima.AllMaxBy(id => ProgressPerTick(id, out _)).AllMaxBy(id => _wc.wishSpeedDivider(id));
                    break;
                case 4: // Value — audit/59 decision 6
                    // EVERY OTHER MODE RANKS ON SPEED OR PRICE AND NEVER ON WHAT THE WISH GIVES.
                    // Cheapest takes the smallest wishSpeedDivider, Fastest takes the largest
                    // ppt/(1-progress), Balanced mixes the two — so a wish worth 0.1% per level and
                    // one worth 5% per level are indistinguishable if they finish at the same rate.
                    // That was tolerable while wishes were a fixed slider share; under sink mode the
                    // ranking decides what the ENTIRE surplus buys.
                    //
                    // wishEffect(id) = 1 + level * effectPerLevel ([DECOMP] WishesController.cs:1114)
                    // — linear, no milestone term — so the relative worth of one more level is
                    // e/(1+L*e), the same law HackMath ranks hacks by. Multiplied by ppt (already
                    // levels per tick) that is value per tick, which is the thing to maximise.
                    //
                    // ⚠ SAME LIMIT AS THE HACK RANKING: this is a percentage of THAT WISH'S OWN
                    // effect, and the effects boost different things. It is the right default when
                    // the alternative is a hand-weighted table, but do not read a small gap between
                    // two wishes as meaningful. GetValidWishes already drops anything past maxLevel
                    // or above the run's difficultyRequirement, so a wish that would return a flat
                    // 1f is never a candidate here.
                    // ⚠ WISH 46 SUBTRACTS. Every other wish is wishEffect = 1 + L*e, but respawn1()
                    // is 1 - L*e floored at 0.9 ([DECOMP] WishesController.cs:1373-1377). Ranking it
                    // with the additive law is a sign error AND misses the floor, which stops it
                    // paying anything at all once L*e reaches 0.1.
                    maxima = maxima.AllMaxBy(id => id == ReducerWishId
                        ? HackMath.ReducerValueRate(_wc.properties[id].effectPerLevel, Wishes[id].level, ProgressPerTick(id, out _))
                        : HackMath.WishValueRate(_wc.properties[id].effectPerLevel, Wishes[id].level, ProgressPerTick(id, out _)));
                    break;
            }
            maxima = maxima.AllMinBy(id =>
            {
                var i = Array.IndexOf(Settings.WishPriorities, id);
                return i == -1 ? int.MaxValue : i;
            });
            if (Settings.WishMode > 0)
                maxima = maxima.AllMaxBy(id => Wishes[id].progress);
            return maxima.First();
        }

        private static float BaseProgressPerTick(int id)
        {
            float energyFactor = Mathf.Pow(_character.totalEnergyPower() * energy, _wc.energyBias(id));
            float magicFactor = Mathf.Pow(_character.totalMagicPower() * magic, _wc.magicBias(id));
            float res3Factor = Mathf.Pow(_character.totalRes3Power() * res3, _wc.res3Bias(id));

            return energyFactor * magicFactor * res3Factor * _wc.totalWishSpeedBonuses() / _wc.wishSpeedDivider(id);
        }

        private static float ProgressPerTick(int id, out float ppt)
        {
            if (_wc.invalidID(id))
            {
                ppt = 0f;
                return 0f;
            }

            ppt = BaseProgressPerTick(id);

            // The GAME's own zero-floor, mirrored exactly — strict `<`, on a double comparison
            // ([DECOMP] WishesController.cs:754, progressPerTick). It is 1e-8, and 1e-8 sits BELOW
            // the real stall floor; the gap between them is where the field failure lives, and the
            // next guard is what closes it.
            if (ppt < 1E-8f)
                return 0f;

            if (ppt > _wc.minimumWishTime())
                return _wc.minimumWishTime();

            // ---- THE STALL FLOOR (constraint-layer-spec §5.3; 37 §S5 A3) -------------------------
            // Wish.progress is a float ([DECOMP] Wish.cs:14) accumulated by a bare `+=`
            // ([DECOMP] WishesController.cs:278), so the bar freezes wherever ppt falls to or below
            // HALF AN ULP of where it stands. The floor is ulp(progress)/2 — and every wish must
            // cross the [0.5, 1) binade to reach 1f, where ulp/2 is 2^-25 for the whole binade and
            // does not scale with progress inside it. A rate at or below 2^-25 therefore completes
            // no level no matter where the bar sits today: it may still be advancing at 0.25 (local
            // floor 2^-26) and it will park the instant it reaches 0.5.
            //
            // ⚠ THIS USED TO DIVIDE BY progress, AND A WRONG FLOOR IS WORSE THAN NO FLOOR BECAUSE IT
            // LOOKS HANDLED. `ppt / progress <= 2^-25` is `ppt <= progress × 2^-25`, which at
            // progress = 0.5 is ppt <= 1.49e-8 — HALF the real floor. So a wish at ppt ≈ 2e-8
            // cleared the game's 1e-8 floor, cleared this guard, advanced while the bar was low, and
            // froze at exactly 0.5 forever: the failure players report from the field (10 §D4). The
            // whole of that field stall lives in the gap [1e-8, 2.98e-8) that neither floor closed.
            //
            // ⚠ AND THE 499-TICK PROJECTION IS NOT THE ARGUMENT. It was clamped to 1f, and 1f is the
            // FIRST value of the next binade up (ulp 2^-23, half-ulp 2^-24 — twice the floor below
            // it), so asking "is it stalled at the projection" would defund a healthy wish precisely
            // because its bar is about to finish. The question a resource guard asks is the capacity
            // one — "is the marginal unit provably wasted?" — and that one is ABSOLUTE. The
            // projection had no other reader, so it goes with the division.
            //
            // The floor itself is CapacityPass's, not a second copy: same constant, same home as
            // HackMath.StallFloor, already pinned at the binade boundary by CapacityPassTests.
            if (CapacityPass.CannotCompleteLevel(ppt))
                return 0f;

            return ppt;
        }

        private static void AllocateToWish(int id)
        {
            if (_wc.invalidID(id))
                return;

            var ppt = ProgressPerTick(id, out var baseppt);
            if (ppt <= 0f)
                return;

            double multi = Math.Pow((double)baseppt / ppt, 1.0 / 3.0 / _wc.energyBias(id));

            _character.input.energyMagicInput = (long)Math.Ceiling(energy * 1.000002 / multi);
            _wc.addEnergy(id);

            _character.input.energyMagicInput = (long)Math.Ceiling(magic * 1.000002 / multi);
            _wc.addMagic(id);

            _character.input.energyMagicInput = (long)Math.Ceiling(res3 * 1.000002 / multi);
            _wc.addRes3(id);
        }
    }
}