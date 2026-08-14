using System;
using System.Collections.Generic;
using System.Linq;

namespace NGUAdvisor.Managers
{
    // NGU value calculation — REVISED (user field report 2026-07-11): the advisor was funding NGUs
    // the Gear Optimizer site scored ~1.04 while a 1.95-rated NGU idled, and E7 Magic / E8 PP /
    // M5 Energy / M6 Adventure-β never ran because the old chapter candidate lists excluded them.
    // Now EVERY unlocked NGU is a candidate and the ranking uses the game's exact math:
    //
    //   levels/hr = power / speedDivider(id) x allocation x multiplierStack / (level+1) x 50 x 3600
    //               (decomp NGUController.progressPerTick — the stack here matches it term for term)
    //   value     = every NGU bonus is 1 + level x boostFactor on the current track (decomp
    //               AllNGUController), so the x/hr score = (1 + f(L+Δ)) / (1 + fL) — the same
    //               per-NGU rating the GO site shows. Respawn (E2) is the one nonlinear curve
    //               (lower is better, hard floors) and is valued by its own curve, so it naturally
    //               drops out at the floor.
    //
    // THE MODEL ITSELF NOW LIVES IN Managers/NguValueMath (audit 01 §3.4, extraction E2 — report 03
    // §10 asked for it by that name). This file is the live-state half: it reads Character, builds the
    // plain-old-data candidate list, caches the result and formats the summary. ValueRatio, Build,
    // Pick, Surplus and Stabilize are all in the core, under characterisation test in
    // tests/NGUAdvisor.Tests/NguValueMathTests.cs — including the three defects that extraction did NOT
    // fix (linear pricing above the break level, the pool/count share model, Rating-vs-Ratio).
    public static class NGUAdvisors
    {
        public class Plan
        {
            public bool Known;
            public List<NguValueMath.Entry> Energy = new List<NguValueMath.Entry>();
            public List<NguValueMath.Entry> Magic = new List<NguValueMath.Entry>();
            // ⚠ THESE ARE NGU **IDs**, NOT TARGET LEVELS. "Targets" here means "the NGUs this pool
            // should be aimed at" — the HOT SET. Nothing in this file, or in anything that reads it,
            // ever produces or writes a target LEVEL.
            //
            // Recorded because the name has already misled a corpus reader: `37-spec-reality.md`
            // §S4.4/§S5 F flags this component as computing "the NGU target levels amendment 21 §1
            // says never exist." It does not. The two live consumers turn these ids into PROFILE
            // TOKENS — `ChallengeOverlay.ChapterNgus` emits `NGU-{i}` (:704-709) and
            // `ChapterNgusSurplus` emits `CAPNGU-{i}` / `NGU-{i}` (:726-731) — which is lane
            // MEMBERSHIP, orthogonal to a target; the third (`OptimizationAdvisor.cs:505-518`) reads
            // only `.Length` for a display row. Each seated NGU lane still runs pure rate semantics:
            // `NGUBP.TargetMet` reads the GAME's `NGU.skills[i].target` field (:29-42), which this
            // advisor never writes, and 0 is the game's unset sentinel so the lane never reports done
            // ([DECOMP] AllNGUController.cs:1311-1314). That is amendment 21 §1 exactly, and `31`
            // §362 already classified this ranking as membership selection.
            public int[] EnergyTargets = new int[0];
            public int[] MagicTargets = new int[0];
            // Positive-value NGUs that didn't make the hot set, by rating — the surplus-energy
            // lanes (the game hard-caps every NGU at ONE level per tick, so a hot lane can't
            // drink more than its cap amount; leftovers belong in additional lanes, not deeper).
            public int[] EnergySurplus = new int[0];
            public int[] MagicSurplus = new int[0];
            public string Summary = "";
        }

        public static readonly string[] ENames = NguValueMath.ENames;
        public static readonly string[] MNames = NguValueMath.MNames;

        private static Plan _cache;
        private static DateTime _cacheAt = DateTime.MinValue;

        private static int[] _incumbentEnergy = new int[0];
        private static int[] _incumbentMagic = new int[0];

        private static double Mul(Func<double> f)
        {
            try { var v = f(); return v > 0 ? v : 1; } catch { return 1; }
        }

        // The full speed-multiplier stack from the game's progressPerTick (everything independent
        // of the specific NGU): itopod, macguffin, NGU-speed NGUs, diggers, hacks, beast quirks,
        // wishes, cards, troll-challenge x3, sadistic divider. The old version missed the last six.
        private static double SpeedMult(Character c, bool magic)
        {
            double m;
            if (magic)
            {
                m = Mul(() => c.totalNGUSpeedBonus())
                    * Mul(() => c.adventureController.itopod.totalMagicNGUBonus())
                    * Mul(() => c.inventory.macguffinBonuses[5])
                    * Mul(() => c.NGUController.magicNGUBonus())
                    * Mul(() => c.allDiggers.totalMagicNGUBonus())
                    * Mul(() => c.hacksController.totalMagicNGUBonus())
                    * Mul(() => c.beastQuestPerkController.totalMagicNGUSpeed())
                    * Mul(() => c.wishesController.totalMagicNGUSpeed())
                    * Mul(() => c.cardsController.getBonus(cardBonus.magicNGUSpeed));
                try { if (c.allChallenges.trollChallenge.completions() >= 1) m *= 3.0; } catch { }
            }
            else
            {
                m = Mul(() => c.totalNGUSpeedBonus())
                    * Mul(() => c.adventureController.itopod.totalEnergyNGUBonus())
                    * Mul(() => c.inventory.macguffinBonuses[4])
                    * Mul(() => c.NGUController.energyNGUBonus())
                    * Mul(() => c.allDiggers.totalEnergyNGUBonus())
                    * Mul(() => c.hacksController.totalEnergyNGUBonus())
                    * Mul(() => c.beastQuestPerkController.totalEnergyNGUSpeed())
                    * Mul(() => c.wishesController.totalEnergyNGUSpeed())
                    * Mul(() => c.cardsController.getBonus(cardBonus.energyNGUSpeed));
                try { if (c.allChallenges.trollChallenge.sadisticCompletions() >= 1) m *= 3.0; } catch { }
            }
            try
            {
                if (c.settings.nguLevelTrack >= difficulty.sadistic)
                    m /= magic ? c.NGUController.NGUMagic[0].sadisticDivider() : c.NGUController.NGU[0].sadisticDivider();
            }
            catch { }
            return m;
        }

        // Level on the track currently being leveled.
        private static long Level(Character c, bool magic, int id)
        {
            var s = magic ? c.NGU.magicSkills[id] : c.NGU.skills[id];
            switch (c.settings.nguLevelTrack)
            {
                case difficulty.evil: return s.evilLevel;
                case difficulty.sadistic: return s.sadisticLevel;
                default: return s.level;
            }
        }

        // boostFactor for the track being leveled (0 when unreadable -> level-ratio fallback).
        private static double Factor(Character c, bool magic, int id)
        {
            try
            {
                switch (c.settings.nguLevelTrack)
                {
                    case difficulty.evil:
                        return magic ? c.NGUController.evilMagicBoostFactor[id] : c.NGUController.evilEnergyBoostFactor[id];
                    case difficulty.sadistic:
                        return magic ? c.NGUController.sadisticMagicBoostFactor[id] : c.NGUController.sadisticEnergyBoostFactor[id];
                    default:
                        return magic ? c.NGUController.normalMagicBoostFactor[id] : c.NGUController.normalEnergyBoostFactor[id];
                }
            }
            catch { return 0; }
        }

        // Live reads -> plain data. One entry per candidate id that survives its own read; a throwing
        // read drops just that NGU, exactly as the per-candidate try/catch in the old Build did.
        private static List<NguValueMath.NguCandidate> Candidates(Character c, int[] ids, bool magic)
        {
            var list = new List<NguValueMath.NguCandidate>();
            if (ids == null) return list;
            bool normalTrack = true;
            try { normalTrack = c.settings.nguLevelTrack == difficulty.normal; } catch { }
            foreach (var id in ids)
            {
                try
                {
                    list.Add(new NguValueMath.NguCandidate
                    {
                        Id = id,
                        Level = Level(c, magic, id),
                        Divider = magic ? c.NGUController.magicSpeedDivider(id) : c.NGUController.energySpeedDivider(id),
                        Factor = Factor(c, magic, id),
                        IsRespawn = !magic && id == 2,
                        NormalTrack = normalTrack
                    });
                }
                catch { }
            }
            return list;
        }

        public static Plan Compute(int[] energyCandidates, int[] magicCandidates)
        {
            if (_cache != null && (DateTime.UtcNow - _cacheAt).TotalSeconds < 30) return _cache;
            var p = new Plan();
            try
            {
                var c = Main.Character;
                if (c == null || c.NGU == null) { _cache = p; return p; }

                double ePool = Math.Max(1, c.curEnergy);
                double mPool = Math.Max(1, c.magic.curMagic);

                var eCands = Candidates(c, energyCandidates, false);
                var mCands = Candidates(c, magicCandidates, true);

                p.Energy = NguValueMath.Build(eCands, false, Math.Max(1, c.totalEnergyPower()), SpeedMult(c, false), ePool);
                p.Magic = NguValueMath.Build(mCands, true, Math.Max(1, c.totalMagicPower()), SpeedMult(c, true), mPool);

                p.EnergyTargets = Stabilized(p.Energy, NguValueMath.Pick(p.Energy, Index(eCands), ePool), false);
                p.MagicTargets = Stabilized(p.Magic, NguValueMath.Pick(p.Magic, Index(mCands), mPool), true);
                p.EnergySurplus = NguValueMath.Surplus(p.Energy, p.EnergyTargets);
                p.MagicSurplus = NguValueMath.Surplus(p.Magic, p.MagicTargets);

                string Fmt(List<NguValueMath.Entry> l) => l.Count == 0 ? "-"
                    : string.Join(", ", l.Take(3).Select(x => $"{x.Name} ×{Math.Min(x.Rating, 9.99):0.00}/hr").ToArray());
                // NOTE: Summary rounds to two decimals, so lanes reading an identical "×1.17/hr" only
                // proves they agree within ~0.85%. That is not enough to tell a converged tie from a
                // Build() bug — to re-check, log Level/LphPerUnit/Rating per lane at full precision
                // (F6) and compare the INPUTS: they differed 233x/1680x while Rating held to 0.04%,
                // which is what proved convergence real. See [[ngu-marathon-convergence]].
                p.Summary = $"E: {Fmt(p.Energy)} · M: {Fmt(p.Magic)}";
                p.Known = p.Energy.Count > 0 || p.Magic.Count > 0;
            }
            catch (Exception e) { Main.LogDebug($"NGUAdvisors: {e.Message}"); }
            _cache = p;
            _cacheAt = DateTime.UtcNow;
            return p;
        }

        private static Dictionary<int, NguValueMath.NguCandidate> Index(List<NguValueMath.NguCandidate> cands)
        {
            var d = new Dictionary<int, NguValueMath.NguCandidate>();
            foreach (var n in cands) d[n.Id] = n;
            return d;
        }

        // The incumbent set is SESSION state, so it stays here rather than in the pure core.
        private static int[] Stabilized(List<NguValueMath.Entry> all, int[] fresh, bool magic)
        {
            var incumbent = magic ? _incumbentMagic : _incumbentEnergy;
            var final = NguValueMath.Stabilize(all, fresh, incumbent);
            if (magic) _incumbentMagic = final; else _incumbentEnergy = final;
            return final;
        }
    }
}
