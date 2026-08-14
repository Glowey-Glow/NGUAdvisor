using System;
using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    public static class BeardManager
    {
        private static Character _character => Main.Character;
        private static readonly AllBeardsController _bc = _character.allBeards;

        private static int[] _savedBeards;
        private static int[] _tempBeards;

        private static readonly int[] TitanBeards = { 5, 1, 6 };
        private static readonly int[] YggBeards = { 6 };
        private static readonly int[] PitBeards = { 6 };

        private static List<int> ActiveBeards { get => _character.beards.activeBeards; }

        public static void SaveBeards() => _savedBeards = ActiveBeards?.ToArray();

        public static void SaveTempBeards() => _tempBeards = ActiveBeards?.ToArray();

        public static void RestoreBeards() => EquipBeards(_savedBeards);

        public static void RestoreTempBeards() => EquipBeards(_tempBeards);

        public static void EquipBeards(LockType currentLock)
        {
            switch (currentLock)
            {
                case LockType.Titan:
                    EquipBeards(TitanBeards);
                    return;
                case LockType.Yggdrasil:
                    EquipBeards(YggBeards);
                    return;
                case LockType.MoneyPit:
                    EquipBeards(PitBeards);
                    return;
            }
        }

        public static bool EquipBeards(int[] beards)
        {
            if (!_character.buttons.beards.interactable)
                return false;

            // THE CHALLENGE RULE, at the one point every writer funnels through — the advisor set
            // (AdvisorApply), the profile timeline (BeardBreakpoints), the QuickBeards hotkey
            // (Main.cs:627), the three mode-lock swaps (LockManager.cs:190/:237/:279) and both
            // restores all arrive here. Applying it once here is what makes it a RULE: a new writer
            // cannot forget it, and the mode locks in particular would otherwise re-equip TitanBeards
            // for the length of every titan window inside a 100LC run.
            //
            // Callers that FORM a set apply BeardRule themselves as well, so their log lines say what
            // actually happened; this is the backstop under them, not a substitute for it.
            beards = BeardRule.Apply(ChallengeDetector.Current(), beards);

            if (beards?.Length > 0 == false)
            {
                _bc.clearActiveBeards();
                return true;
            }

            var allEquipped = true;

            // Trying to keep the golden beard on
            if (_character.allChallenges.trollChallenge.completions() >= 7)
            {
                if (beards.Length > _bc.capBeards())
                {
                    Array.Resize(ref beards, _bc.capBeards());
                    allEquipped = false;
                }

                if (Array.Exists(beards, x => x == 6) && ActiveBeards.Exists(x => x == 6))
                {
                    foreach (var beard in ActiveBeards.FindAll(x => x != 6))
                        _bc.deactivateBeard(beard);
                    beards = Array.FindAll(beards, x => x != 6);
                }
                else
                {
                    _bc.clearActiveBeards();
                }
            }
            else
            {
                if (Array.Exists(beards, x => x == 6))
                {
                    beards = Array.FindAll(beards, x => x != 6);
                    allEquipped = false;
                }

                if (beards.Length > _bc.capBeards())
                {
                    Array.Resize(ref beards, _bc.capBeards());
                    allEquipped = false;
                }

                _bc.clearActiveBeards();
            }

            foreach (var beard in beards)
                _bc.activateBeard(beard);

            _bc.refreshMenu();

            WriteLedger.Record("beards.active",
                beards.Length == 0 ? "none" : string.Join(", ", Array.ConvertAll(beards, b => (b + 1).ToString())),
                allEquipped ? "advisor or profile beard set for this phase"
                            : "requested set was larger than the beard cap and was trimmed",
                ChallengeOverlay.Segment,
                "Cleared and re-activated as a whole set, not toggled individually",
                "The 100LC challenge rule can empty this set deliberately — an empty set is a decision",
                "Replaced on the next set change; nothing restores it on its own");

            return allEquipped;
        }
    }
}
