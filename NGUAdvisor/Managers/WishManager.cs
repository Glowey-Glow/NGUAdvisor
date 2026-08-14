using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static NGUAdvisor.Main;

namespace NGUAdvisor.Managers
{
    public static class WishManager
    {
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
        public static void Allocate()
        {
            _wc.removeAllResources();

            // The > idle clamps are not decorative: above 2^53 the double product loses exactness
            // and Ceiling can land one unit past the pool (pools legitimately exceed 1e18 under
            // potions — audit/15 §A1).
            long remainingEnergy = (long)Math.Ceiling(_character.idleEnergy * Settings.WishEnergy / 100.0);
            if (remainingEnergy > _character.idleEnergy)
                remainingEnergy = _character.idleEnergy;

            long remainingMagic = (long)Math.Ceiling(_character.magic.idleMagic * Settings.WishMagic / 100.0);
            if (remainingMagic > _character.magic.idleMagic)
                remainingMagic = _character.magic.idleMagic;

            long remainingRes3 = (long)Math.Ceiling(_character.res3.idleRes3 * Settings.WishR3 / 100.0);
            if (remainingRes3 > _character.res3.idleRes3)
                remainingRes3 = _character.res3.idleRes3;

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