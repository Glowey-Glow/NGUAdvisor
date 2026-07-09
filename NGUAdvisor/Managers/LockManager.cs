using static NGUAdvisor.Main;

namespace NGUAdvisor.Managers
{
    public enum LockType
    {
        Titan,
        Yggdrasil,
        MoneyPit,
        Gold,
        Quest,
        Cooking,
        None
    }

    public static class LockManager
    {
        private static LockType currentLock;
        private static bool _swappedFromQuest;
        private static bool _swappedDiggers;
        private static bool _swappedBeards;

        public static bool HasTitanLock() => currentLock == LockType.Titan;

        public static bool HasMoneyPitLock() => currentLock == LockType.MoneyPit;

        public static bool HasGoldLock() => currentLock == LockType.Gold;

        public static bool HasQuestLock() => currentLock == LockType.Quest;

        public static bool HasCookingLock() => currentLock == LockType.Cooking;

        public static bool CanSwap() => currentLock == LockType.None || HasQuestLock();

        private static void AcquireLock(LockType newLock)
        {
            _swappedFromQuest = currentLock == LockType.Quest;
            currentLock = newLock;
        }

        private static bool CanAcquireNewLock(LockType newLock)
        {
            return currentLock != newLock && CanSwap();
        }

        internal static void ReleaseLock()
        {
            currentLock = LockType.None;
        }

        private static void SaveConfiguration()
        {
            if (!_swappedFromQuest)
                LoadoutManager.SaveCurrentLoadout();
            BeardManager.SaveBeards();
            DiggerManager.SaveDiggers();
        }

        private static void RestoreConfiguration()
        {
            LoadoutManager.RestoreGear();
            // The restored gear predates the lock — if the segment/objective moved meanwhile, it's
            // stale. Let the gear refresh re-evaluate on the next tick with its bypass re-armed.
            AdvisorApply.GearRestored();

            if (_swappedFromQuest)
            {
                _swappedFromQuest = false;
                if (Settings.AutoQuest)
                {
                    AcquireLock(LockType.Quest);
                    if (Settings.QuestLoadout.Length > 0 || !string.IsNullOrEmpty(Settings.QuestObjective))
                        LoadoutManager.ChangeGear(GearOptimizer.ResolveModeGear(Settings.QuestObjective, Settings.QuestObjectiveRespawn, Settings.QuestLoadout));
                }
            }
            else
            {
                ReleaseLock();
            }

            if (_swappedBeards)
            {
                BeardManager.RestoreBeards();
                _swappedBeards = false;
            }

            if (_swappedDiggers)
            {
                DiggerManager.RestoreDiggers();
                _swappedDiggers = false;
            }
        }

        public static void TryTitanSwap()
        {
            if (CanAcquireNewLock(LockType.Titan) && ZoneHelpers.AnyTitansSpawningSoon())
            {
                AcquireLock(LockType.Titan);
                SaveConfiguration();

                if (ZoneHelpers.ShouldRunGoldLoadout())
                {
                    Log("Switching to Gold Drop configuration for titans");
                    LoadoutManager.ChangeGear(GearOptimizer.ResolveGoldGear());
                }
                else if (ZoneHelpers.ShouldRunTitanLoadout())
                {
                    Log("Switching to Titan configuration");
                    LoadoutManager.ChangeGear(GearOptimizer.ResolveTitanGear());
                }

                if (Settings.SwapTitanBeards)
                {
                    BeardManager.EquipBeards(currentLock);
                    _swappedBeards = true;
                }

                if (Settings.SwapTitanDiggers)
                {
                    DiggerManager.EquipDiggers(currentLock);
                    DiggerManager.RecapDiggers();
                    _swappedDiggers = true;
                }
            }
            else if (currentLock == LockType.Titan)
            {
                RestoreConfiguration();
            }
        }

        public static bool TryYggdrasilSwap(bool forced = false)
        {
            if (YggdrasilManager.NeedsHarvest(forced))
            {
                if (CanAcquireNewLock(LockType.Yggdrasil))
                {
                    AcquireLock(LockType.Yggdrasil);
                    SaveConfiguration();

                    if (Settings.SwapYggdrasilLoadouts && (forced || YggdrasilManager.NeedsSwap()))
                    {
                        Log("Switching to Yggdrasil configuration");
                        LoadoutManager.ChangeGear(GearOptimizer.ResolveModeGear(Settings.YggdrasilObjective, Settings.YggdrasilObjectiveRespawn, Settings.YggdrasilLoadout));
                    }
                    else
                    {
                        Log("Switching to Yggdrasil configuration without gear swap");
                    }

                    if (Settings.SwapYggdrasilBeards)
                    {
                        BeardManager.EquipBeards(currentLock);
                        _swappedBeards = true;
                    }

                    if (Settings.SwapYggdrasilDiggers)
                    {
                        DiggerManager.EquipDiggers(currentLock);
                        DiggerManager.RecapDiggers();
                        _swappedDiggers = true;
                    }

                    return true;
                }
            }
            else if (currentLock == LockType.Yggdrasil)
            {
                RestoreConfiguration();
            }
            return false;
        }

        public static bool TryMoneyPitSwap(int[] loadout = null, int[] diggers = null, bool shockwave = false)
        {
            if (CanAcquireNewLock(LockType.MoneyPit))
            {
                AcquireLock(LockType.MoneyPit);
                SaveConfiguration();

                if (loadout?.Length > 0)
                    LoadoutManager.ChangeGear(loadout, shockwave);

                if (diggers?.Length > 0 && Settings.SwapPitDiggers)
                {
                    BeardManager.EquipBeards(currentLock);
                    _swappedBeards = true;

                    DiggerManager.EquipDiggers(diggers);
                    DiggerManager.RecapDiggers();
                    _swappedDiggers = true;
                }

                return true;
            }
            else if (currentLock == LockType.MoneyPit)
            {
                RestoreConfiguration();
            }
            return false;
        }

        public static bool TryGoldDropSwap()
        {
            if (CanAcquireNewLock(LockType.Gold))
            {
                AcquireLock(LockType.Gold);
                SaveConfiguration();

                Log("Switching to Gold configuration");
                LoadoutManager.ChangeGear(GearOptimizer.ResolveGoldGear());

                return true;
            }
            else if (currentLock == LockType.Gold)
            {
                if (Settings.ManageGoldLoadouts)
                {
                    Log("Gold Loadout kill done. Turning off setting and swapping gear");
                    Settings.GoldSnipeComplete = true;
                }
                RestoreConfiguration();
            }
            return false;
        }

        public static bool TryQuestSwap()
        {
            if (CanAcquireNewLock(LockType.Quest))
            {
                AcquireLock(LockType.Quest);
                SaveConfiguration();

                if (Settings.ManageQuestLoadouts)
                {
                    Log("Switching to Quest configuration");
                    LoadoutManager.ChangeGear(GearOptimizer.ResolveModeGear(Settings.QuestObjective, Settings.QuestObjectiveRespawn, Settings.QuestLoadout));
                }

                return true;
            }
            else if (currentLock == LockType.Quest)
            {
                RestoreConfiguration();
            }
            return false;
        }

        public static bool TryCookingSwap()
        {
            if (CanAcquireNewLock(LockType.Cooking))
            {
                AcquireLock(LockType.Cooking);
                SaveConfiguration();

                Log("Switching to Cooking configuration");
                LoadoutManager.ChangeGear(GearOptimizer.ResolveModeGear(Settings.CookingObjective, Settings.CookingObjectiveRespawn, Settings.CookingLoadout));

                return true;
            }
            else if (currentLock == LockType.Cooking)
            {
                RestoreConfiguration();
            }
            return false;
        }

        public static string GetLockTypeName()
        {
            switch (currentLock)
            {
                case LockType.Cooking:
                    return "Cooking";
                case LockType.Gold:
                    return "Gold";
                case LockType.MoneyPit:
                    return "Money Pit";
                case LockType.None:
                    return "Default";
                case LockType.Quest:
                    return "Quest";
                case LockType.Titan:
                    return "Titan";
                case LockType.Yggdrasil:
                    return "Yggdrasil";
            }
            return "Unknown";
        }
    }
}
