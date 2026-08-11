using System;
using static NGUAdvisor.Main;

namespace NGUAdvisor.Managers
{
    public static class BloodMagicManager
    {
        public const double PillWorthFraction = 0.10;      // min pill effect as a fraction of base adventure power
        public const double PillMinAvailableSec = 1800.0;  // 30-min availability hold before a cast is allowed

        public abstract class Spell
        {
            protected string name;
            protected int cooldown;
            protected double minBlood;

            protected abstract bool Unlocked { get; }

            protected abstract double Time { get; }

            protected abstract double Threshold { get; }

            protected abstract bool CastOnRebirth { get; }

            protected Spell(string name, int cooldown, double minBlood)
            {
                this.name = name;
                this.cooldown = cooldown;
                this.minBlood = minBlood;
            }

            protected abstract double Effect(double bloodPoints);

            protected abstract void CastSpell();

            // Per-spell fail-safe evaluated right before casting, on top of the shared unlock/cooldown/
            // blood/threshold checks. Default: never holds. Returns true to HOLD (skip) the cast and sets
            // reason for logging. IronPill overrides this.
            protected virtual bool FailSafeHold(double effect, out string reason) { reason = null; return false; }

            // The gate LADDER is BloodPillMath.DecideCast, under characterisation test; this method
            // keeps the live reads, the logging and the actual cast. The effect is computed before the
            // decision because two of the rungs need it — the same order as the original.
            public void Cast(bool rebirth = false)
            {
                var forced = rebirth && CastOnRebirth;
                double threshold = Threshold;
                string holdReason = null;

                var verdict = BloodPillMath.DecideCast(new BloodPillMath.CastInputs
                {
                    SpellsEnabled = Settings.CastBloodSpells,
                    Unlocked = Unlocked,
                    Forced = forced,
                    Threshold = threshold,
                    TimeSec = Time,
                    CooldownSec = cooldown,
                    BloodPoints = () => _character.bloodMagic.bloodPoints,
                    MinBlood = minBlood,
                    Effect = () => Effect(_character.bloodMagic.bloodPoints),
                    FailSafeHolds = e => FailSafeHold(e, out holdReason)
                }, out var effect);

                switch (verdict)
                {
                    case BloodPillMath.CastVerdict.OnCooldown:
                        // Normal during rebirth prep (the prep loop retries) — debug only, the old Log
                        // line spammed once per attempt.
                        if (forced)
                            Main.LogDebug($"Blood Spell {name} skipped on rebirth - on cooldown");
                        return;
                    case BloodPillMath.CastVerdict.BelowMinimumBlood:
                        if (forced || Time < cooldown + 10f)
                            Log($"Casting Failed: Blood Spell {name} - Below minimum blood threshold of {minBlood:F0}");
                        return;
                    case BloodPillMath.CastVerdict.BelowPowerThreshold:
                        if (Time < cooldown + 10f)
                            Log($"Casting Failed: Blood Spell {name} - Below configured power threshold ({effect:F0} of {threshold:F0}) and not force cast on rebirth");
                        return;
                    case BloodPillMath.CastVerdict.HeldByFailSafe:
                        if (Time < cooldown + 10f)
                            Log($"Casting Failed: Blood Spell {name} - {holdReason}");
                        return;
                    case BloodPillMath.CastVerdict.Cast:
                        CastSpell();
                        Log($"Casting Blood Spell {name} @ {effect:F0} power");
                        return;
                    default:
                        // SpellsDisabled / NotUnlocked / NoUserThreshold — silent, as before.
                        return;
                }
            }

            // Planner-driven cast (BloodPlanner decided the timing): safety checks only — unlock,
            // cooldown, minimum blood — no user threshold involved.
            public bool CastPlanned()
            {
                string holdReason = null;

                var verdict = BloodPillMath.DecidePlannedCast(new BloodPillMath.CastInputs
                {
                    Unlocked = Unlocked,
                    TimeSec = Time,
                    CooldownSec = cooldown,
                    BloodPoints = () => _character.bloodMagic.bloodPoints,
                    MinBlood = minBlood,
                    Effect = () => Effect(_character.bloodMagic.bloodPoints),
                    FailSafeHolds = e => FailSafeHold(e, out holdReason)
                }, out var effect);

                if (verdict == BloodPillMath.CastVerdict.HeldByFailSafe)
                {
                    Main.LogDebug($"Blood Spell {name} held (planner): {holdReason}");
                    return false;
                }
                if (verdict != BloodPillMath.CastVerdict.Cast)
                    return false;

                CastSpell();
                Log($"Casting Blood Spell {name} @ {effect:F0} power (planner)");
                return true;
            }
        }

        public class IronPill : Spell
        {
            protected override bool Unlocked => true;

            protected override double Time => _character.bloodMagic.adventureSpellTime.totalseconds;

            protected override double Threshold => Settings.IronPillThreshold;

            protected override bool CastOnRebirth => Settings.IronPillOnRebirth;

            public IronPill(string name, int cooldown, double minBlood) : base(name, cooldown, minBlood) { }

            protected override double Effect(double bloodPoints) =>
                BloodPillMath.IronPillEffect(bloodPoints, IronPillBonus());

            // 1.0 below Evil — the bonus is a flat multiplier that does not exist on Normal.
            private static double IronPillBonus() =>
                _character.settings.rebirthDifficulty >= difficulty.evil
                    ? _character.adventureController.itopod.ironPillBonus()
                    : 1.0;

            protected override void CastSpell() => _bloodSpells.castAdventurePowerupSpell();

            // Advisor-only fail-safes (the on-rebirth forced cast bypasses these):
            //  1. Do not fire within the first 30 minutes the pill is available (past its cooldown), so
            //     blood keeps pooling into a stronger pill instead of firing the moment it comes ready.
            //  2. Do not fire for a gain under 10% of the current BASE adventure power (adventure.attack --
            //     the same base-stat yardstick BloodPlanner measures pill worth against).
            // The predicate is BloodPillMath.PillFailSafe — the same one BloodPlanner's HoldingFailSafe
            // branch consults. Report 04 §10 found this logic hand-copied between the two files; there
            // is now one implementation and both call it.
            protected override bool FailSafeHold(double effect, out string reason)
            {
                double availableFor = Time - cooldown;
                double baseAdvPower = Math.Max(1.0, _character.adventure.attack);

                switch (BloodPillMath.PillFailSafe(availableFor, effect, baseAdvPower, PillMinAvailableSec, PillWorthFraction))
                {
                    case BloodPillMath.PillHold.TooSoonAfterReady:
                        reason = $"available {Math.Max(0, availableFor) / 60.0:F0}m (< 30m fail-safe)";
                        return true;
                    case BloodPillMath.PillHold.GainTooSmall:
                        reason = $"gain {effect:F0} < 10% of base adv power {baseAdvPower:F0}";
                        return true;
                    default:
                        reason = null;
                        return false;
                }
            }
        }

        public class GuffA : Spell
        {
            protected override bool Unlocked => _character.adventure.itopod.perkLevel[72] >= 1L;

            protected override double Time => _character.bloodMagic.macguffin1Time.totalseconds;

            protected override double Threshold => Settings.BloodMacGuffinAThreshold;

            protected override bool CastOnRebirth => Settings.BloodMacGuffinAOnRebirth;

            public GuffA(string name, int cooldown, double minBlood) : base(name, cooldown, minBlood) { }

            protected override double Effect(double bloodPoints) =>
                BloodPillMath.GuffAEffect(bloodPoints, _bloodSpells.minMacguffin1Blood(), _character.wishesController.totalBloodGuffbonus());

            protected override void CastSpell() => _bloodSpells.castMacguffin1Spell();
        }

        public class GuffB : Spell
        {
            protected override bool Unlocked => _character.adventure.itopod.perkLevel[73] >= 1L && _character.settings.rebirthDifficulty >= difficulty.evil;

            protected override double Time => _character.bloodMagic.macguffin2Time.totalseconds;

            protected override double Threshold => Settings.BloodMacGuffinBThreshold;

            protected override bool CastOnRebirth => Settings.BloodMacGuffinBOnRebirth;

            public GuffB(string name, int cooldown, double minBlood) : base(name, cooldown, minBlood) { }

            protected override double Effect(double bloodPoints) =>
                BloodPillMath.GuffBEffect(bloodPoints, _bloodSpells.minMacguffin2Blood());

            protected override void CastSpell() => _bloodSpells.castMacguffin2Spell();
        }

        // Properties, not type-init captures — see ResourceBreakpoint._character. This class held the
        // WORST form of the weld: the three spell singletons below were `static readonly` built from
        // live game reads, so the type initializer needed a Character, a RebirthPowerSpell, and three
        // successful game calls before any member — including the two consts — could be touched.
        private static Character _character => Main.Character;
        private static RebirthPowerSpell _bloodSpells => _character.bloodSpells;

        // Lazily constructed, sampling the SAME cooldown/minBlood values at the SAME moment as before:
        // PillWorthFraction/PillMinAvailableSec are `const` (inlined by the compiler, they never trigger
        // type-init), so the first thing that ever ran this type's initializer was already one of these
        // three accessors. First-access construction is therefore the identical instant, not a new one.
        private static Spell _ironPill, _guffA, _guffB;

        public static Spell ironPill => _ironPill ?? (_ironPill = new IronPill("Iron Pill", _bloodSpells.adventureSpellCooldown, _bloodSpells.minAdventureBlood()));
        public static Spell guffA => _guffA ?? (_guffA = new GuffA("MacGuffin α", _bloodSpells.macguffin1Cooldown, _bloodSpells.minMacguffin1Blood()));
        public static Spell guffB => _guffB ?? (_guffB = new GuffB("MacGuffin β", _bloodSpells.macguffin2Cooldown, _bloodSpells.minMacguffin2Blood()));
    }
}
