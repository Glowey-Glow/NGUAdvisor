using System;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // ⚠ COUNTERFEIT GOLD MULTIPLIES A NUMBER THAT IS ZERO DURING THE NO TIME MACHINE CHALLENGE.
    //
    // [OPERATOR] caught this live in CBlock3.2-E's NOTM-1 run, 2026-08-08: the auto profile was
    // funding Counterfeit Gold during NOTM, which is wasted blood. It is not "worth less" — it is
    // provably worth NOTHING, and the game says so in one line:
    //
    //     [DECOMP] Character.grossGoldPerSecond()
    //         if (challenges.timeMachineChallenge.inChallenge) { return 0.0; }
    //         return machine.realBaseGold * ... * bloodMagicController.goldBonus() * ...;
    //
    // `bloodMagicController.goldBonus()` is EXACTLY the multiplier Counterfeit Gold buys, and it sits
    // inside the branch that never executes. Blood spent there during NOTM is multiplied by zero.
    //
    // THE DEFECT WAS A PROXY. BloodPlanner gated the gold route on `c.machine.realBaseGold > 0` — the
    // PERSISTED Time Machine stat, which keeps its pre-challenge value and therefore stays > 0 all
    // through NOTM. Eleven lines above it, `norb` was already challenge-aware; this gate was not.
    // Same family as the bossID/effectiveBossID Evil defect: a gate reading a STORED value where the
    // LIVE-EFFECTIVE one was meant.
    //
    // These tests cannot construct a Character (BloodPlanner is welded to it), so the fix is pinned at
    // the SOURCE — the same technique the bridge and hard-cap proofs use — plus a headless check that
    // the router does the right thing once the predicate answers correctly.
    public class CounterfeitGoldEngineTests
    {
        // ---- the fix, pinned where it lives ------------------------------------------------------

        [Fact]
        public void The_gold_route_asks_the_game_for_the_engine_and_never_reads_realBaseGold()
        {
            var src = CodeOnly(Source("BloodPlanner.cs"));

            // It must consult the game's own authority.
            Assert.Contains("GrossGoldPerSecond(c) > 0", src);
            Assert.Contains("c.grossGoldPerSecond()", src);

            // ⚠ AND IT MUST NOT GO BACK TO THE PROXY. `realBaseGold` is non-zero throughout NOTM, so a
            // future "simplification" back to it silently restores the waste with no test failing
            // anywhere else. CodeOnly strips comments, so the explanation at the call site — which
            // names realBaseGold on purpose — does not satisfy this.
            Assert.DoesNotContain("realBaseGold", src);
        }

        // The guard is not decoration: this predicate runs inside FillRouting's single try, so an
        // exception would abort the WHOLE routing and leave it unset. It must fail CLOSED — an engine
        // we cannot read is treated as dead, which DECLINES Counterfeit rather than funding it on a
        // guess. (Funding on a guess is the exact failure being fixed.)
        [Fact]
        public void The_gold_engine_read_is_guarded_and_fails_closed()
        {
            var src = CodeOnly(Source("BloodPlanner.cs"));
            int i = src.IndexOf("private static double GrossGoldPerSecond", StringComparison.Ordinal);
            Assert.True(i >= 0, "the guarded helper is gone — the live read is unprotected");

            var body = src.Substring(i, Math.Min(400, src.Length - i));
            Assert.Contains("try", body);
            Assert.Contains("catch", body);
            Assert.Contains("return 0", body);   // fail closed, not "assume it works"
        }

        // ---- and the router does the right thing once the predicate answers honestly ---------------

        // A dead gold engine must not take the Gold route. With NUMBER eligible it falls to the default
        // NUMBER sink — which is what [OPERATOR] asked for: "The focus should just be Blood Number
        // Boost Spell."
        [Fact]
        public void A_dead_gold_engine_routes_to_NUMBER_not_to_Counterfeit()
        {
            var route = BloodRouter.DecideRoute(
                numberEligible: true, numberFloor: 0, rebirthPower: 1,
                windowOpen: () => true,
                goldAvailable: () => false,     // grossGoldPerSecond() == 0 during NOTM
                lootAvailable: () => false);

            Assert.Equal(BloodRoute.NumberDefault, route);
            Assert.NotEqual(BloodRoute.Gold, route);
        }

        // NEGATIVE CONTROL — the assertion above must be capable of failing. Outside the challenge the
        // engine is live, and with the window open and gold below the knee the SAME call still routes
        // to Gold. Without this, "it routes to NUMBER" would be indistinguishable from a router that
        // can never choose Gold at all.
        [Fact]
        public void A_live_gold_engine_still_routes_to_Counterfeit()
        {
            var route = BloodRouter.DecideRoute(
                numberEligible: true, numberFloor: 0, rebirthPower: 1,
                windowOpen: () => true,
                goldAvailable: () => true,      // engine producing, outside NOTM
                lootAvailable: () => false);

            Assert.Equal(BloodRoute.Gold, route);
        }

        // The NUMBER floor still outranks gold even when the engine IS live — this fix must not have
        // disturbed the ladder the M5 migration locked.
        [Fact]
        public void The_NUMBER_floor_still_outranks_a_live_gold_engine()
        {
            var route = BloodRouter.DecideRoute(
                numberEligible: true, numberFloor: 100, rebirthPower: 1,
                windowOpen: () => true,
                goldAvailable: () => true,
                lootAvailable: () => false);

            Assert.Equal(BloodRoute.NumberFloor, route);
        }

        // ---- helpers (house pattern: each source-asserting file carries its own) --------------------

        private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = null)
        {
            var dir = System.IO.Path.GetDirectoryName(here);
            while (dir != null &&
                   !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "NGUAdvisor", "Managers")))
                dir = System.IO.Path.GetDirectoryName(dir);
            return dir;
        }

        private static string Source(string name)
        {
            var path = System.IO.Path.Combine(RepoRoot(), "NGUAdvisor", "Managers", name);
            Assert.True(System.IO.File.Exists(path),
                $"source not found, so nothing was measured: {path}");
            return System.IO.File.ReadAllText(path);
        }

        private static string CodeOnly(string src)
            => string.Join("\n", src.Split('\n')
                .Select(l => { int i = l.IndexOf("//", StringComparison.Ordinal); return i < 0 ? l : l.Substring(0, i); }));
    }
}
