using System.Globalization;
using NGUAdvisor.Managers;
using SimpleJSON;
using Xunit;

namespace NGUAdvisor.Tests
{
    // Guards for the three-state autokill readiness published as UiBridge's `titans.ak` chip row.
    //
    // The whole point of this vocabulary is that STAT-READY IS NOT AUTOKILLABLE. The Ak table is the stat
    // path; ZoneHelpers.AutokillAvailable is the game's actual gate, and the two disagree in BOTH directions
    // (T4/T5 add an item/kill requirement the table can't express; T9-T12 unlock autokill from a kill count
    // with no stat check at all). UiBridge can't be linked here — it drags in Unity — so the classification
    // itself lives in TitanTables and is tested against the real table rows, with the live gate supplied as a
    // parameter exactly as the bridge supplies it.
    public class TitanAkReadinessTests
    {
        // Ak row indices used below, by titan index (0-based): T1 = GRB, T4 = UUG, T9 = Exile.
        private static double[] Row(int titanIndex, int version) => TitanTables.AkRow(titanIndex, version);

        [Fact]
        public void Meeting_attack_and_defense_but_failing_regen_is_not_ready()
        {
            // T4 (index 3) gates on 8e5 atk / 4e5 def / 1.4e4 HP regen. Clear the first two, miss the third.
            var req = Row(3, 1);
            Assert.True(req[2] > 0, "this test is only meaningful on a row that HAS a regen gate");

            Assert.False(TitanTables.AkStatsMet(1e6, 5e5, 1e3, req));

            // The game refuses (it checks regen too), so the honest report is "short" — not "ready", and not
            // "gated" either: "gated" would claim the stats are done and something else is in the way.
            var state = TitanTables.AkState(1e6, 5e5, 1e3, req, autokillAvailable: false);
            Assert.Equal(TitanTables.StateShort, state);
            Assert.NotEqual(TitanTables.StateReady, state);
            Assert.NotEqual(TitanTables.StateGated, state);
        }

        [Fact]
        public void A_regen_threshold_of_zero_is_satisfied_not_zero_percent()
        {
            // T1-T3 carry a sentinel 0 in the regen column: the game makes no regen check there at all.
            var req = Row(0, 1);
            Assert.Equal(0.0, req[2]);

            Assert.True(TitanTables.StatMet(0, 0));                       // no gate -> met, even at zero regen
            Assert.True(TitanTables.AkStatsMet(3000, 2500, 0, req));      // exactly-at-threshold atk/def, no regen
            Assert.Equal(100.0, TitanTables.StatPct(0, 0));               // "satisfied", NOT 0% of the way there

            // ...and a real gate at zero progress is the opposite answer, which is why the bridge must omit
            // the key rather than publish a number for a gate that does not exist.
            Assert.False(TitanTables.StatMet(0, 1.4e4));
            Assert.Equal(0.0, TitanTables.StatPct(0, 1.4e4));
        }

        [Fact]
        public void Gated_is_reachable_when_every_stat_is_met_and_the_game_still_refuses()
        {
            // The live case: T4 (index 3) additionally needs item 135 maxxed, which no amount of attack buys.
            var req = Row(3, 1);
            double atk = req[0] * 2, def = req[1] * 2, regen = req[2] * 2;

            Assert.True(TitanTables.AkStatsMet(atk, def, regen, req));
            Assert.Equal(TitanTables.StateGated, TitanTables.AkState(atk, def, regen, req, autokillAvailable: false));

            // Same stats, item gate cleared -> ready. `gated` is precisely the delta between the two.
            Assert.Equal(TitanTables.StateReady, TitanTables.AkState(atk, def, regen, req, autokillAvailable: true));
        }

        [Fact]
        public void T5_gated_on_boss_kills_behaves_the_same_way()
        {
            var req = Row(4, 1);   // T5 / Walderp: stats + boss5Kills >= 3
            Assert.Equal(TitanTables.StateGated,
                TitanTables.AkState(req[0], req[1], req[2], req, autokillAvailable: false));
        }

        [Fact]
        public void The_games_gate_outranks_the_table_when_kill_counts_unlock_autokill()
        {
            // T9-T12 return true from autokillTitanNVVAchieved on a bestiary kill count ALONE, before any
            // stat comparison. Reporting "short" there would tell the user to push stats they don't need.
            var req = Row(8, 1);   // T9 / Exile
            Assert.False(TitanTables.AkStatsMet(0, 0, 0, req));
            Assert.Equal(TitanTables.StateReady, TitanTables.AkState(0, 0, 0, req, autokillAvailable: true));
        }

        [Fact]
        public void A_missing_requirement_row_reports_unknown_rather_than_guessing()
        {
            // Unversioned titans have exactly one row; asking for v2 is not a 0% chip, it's an unknown one.
            Assert.Null(TitanTables.AkRow(3, 2));
            Assert.Equal(TitanTables.StateUnknown, TitanTables.AkState(1e9, 1e9, 1e9, null, autokillAvailable: false));

            // ...but a true gate is still a fact even with no row to compare against.
            Assert.Equal(TitanTables.StateReady, TitanTables.AkState(0, 0, 0, null, autokillAvailable: true));
        }

        [Fact]
        public void Only_twelve_titans_have_an_autokill_path()
        {
            // ZoneHelpers.AutokillAvailable returns false outright for titanIndex >= 12, so Tippi (12) and
            // Traitor (13) get no chip. Abbrev still carries all 14 for the manual kill grid.
            Assert.Equal(12, TitanTables.Ak.Length);
            Assert.Equal(14, TitanTables.Abbrev.Length);
            Assert.Null(TitanTables.AkRow(12, 1));
            Assert.Null(TitanTables.AkRow(13, 1));
            Assert.Null(TitanTables.AkRow(-1, 1));
            for (int i = 0; i < 12; i++) Assert.NotNull(TitanTables.AkRow(i, 1));
        }

        [Fact]
        public void Every_versioned_titan_row_resolves_for_every_version_the_game_can_report()
        {
            // The bridge feeds AkRow whatever ZoneHelpers.TitanVersion returns (save field + 1, i.e. 1..4).
            for (int i = 0; i < 12; i++)
            {
                int versions = TitanTables.Ak[i].Length;
                for (int v = 1; v <= versions; v++) Assert.NotNull(TitanTables.AkRow(i, v));
                Assert.Null(TitanTables.AkRow(i, versions + 1));
                Assert.Null(TitanTables.AkRow(i, 0));
            }
        }

        [Fact]
        public void Progress_percent_is_clamped_rounded_and_never_NaN()
        {
            Assert.Equal(50.0, TitanTables.StatPct(5e5, 1e6));
            Assert.Equal(100.0, TitanTables.StatPct(2e6, 1e6));    // clamped: overshoot is still "done"
            Assert.Equal(100.0, TitanTables.StatPct(1e6, 1e6));    // exactly at threshold
            Assert.Equal(0.0, TitanTables.StatPct(-1, 1e6));
            Assert.Equal(0.0, TitanTables.StatPct(double.NaN, 1e6));
            Assert.Equal(0.0, TitanTables.StatPct(double.PositiveInfinity * 0, 1e6));   // NaN
            Assert.Equal(100.0, TitanTables.StatPct(double.PositiveInfinity, 1e6));     // clamped, not Infinity

            // Whole percents only: the snapshot ships these straight into JSON.
            Assert.Equal(33.0, TitanTables.StatPct(1e6 / 3.0, 1e6));
        }

        [Fact]
        public void Stat_met_is_inclusive_of_the_threshold_like_the_games_own_comparison()
        {
            // The game uses >=, so landing exactly on the number counts.
            Assert.True(TitanTables.StatMet(3000, 3000));
            Assert.False(TitanTables.StatMet(2999.9, 3000));
            Assert.False(TitanTables.StatMet(double.NaN, 3000));
        }

        [Fact]
        public void Ak_thresholds_serialize_without_G17_float_noise()
        {
            // UiBridge.Exact() ships the raw thresholds through JSONNumber's STRING constructor, which keeps
            // the literal verbatim. Without that, SimpleJson's G17 writer turns the table's 1e23 into
            // 9.9999999999999992E+22 — 26 bytes of float noise, three times per titan, and a number the UI
            // would show the user in a form the game's own source never uses. This pins the mechanism.
            var noisy = new JSONNumber(1e23).ToString();
            Assert.NotEqual("1E+23", noisy);
            Assert.True(noisy.Length > 10, "expected G17 to expose the float's true value, got " + noisy);

            var exact = new JSONNumber(1e23.ToString("R", CultureInfo.InvariantCulture));
            Assert.Equal("1E+23", exact.ToString());
            Assert.Equal(1e23, exact.AsDouble);

            // Still a real JSON number on the far side, not a string.
            var parsed = JSON.Parse("{\"reqAtk\":" + exact + "}");
            Assert.True(parsed["reqAtk"].IsNumber);
            Assert.Equal(1e23, parsed["reqAtk"].AsDouble);

            // Every threshold in the table survives the round trip exactly.
            foreach (var titan in TitanTables.Ak)
                foreach (var ver in titan)
                    foreach (var x in ver)
                    {
                        var n = new JSONNumber(x.ToString("R", CultureInfo.InvariantCulture));
                        Assert.Equal(x, JSON.Parse("{\"x\":" + n + "}")["x"].AsDouble);
                    }
        }

        [Fact]
        public void Every_real_table_row_classifies_as_short_at_zero_stats_and_gated_when_maxxed()
        {
            // Sweep the whole table so a future row edit can't produce an unclassifiable chip.
            for (int i = 0; i < TitanTables.Ak.Length; i++)
                for (int v = 1; v <= TitanTables.Ak[i].Length; v++)
                {
                    var req = TitanTables.AkRow(i, v);
                    Assert.Equal(TitanTables.StateShort, TitanTables.AkState(0, 0, 0, req, false));
                    Assert.Equal(TitanTables.StateGated,
                        TitanTables.AkState(req[0], req[1], req[2], req, false));
                    Assert.Equal(TitanTables.StateReady,
                        TitanTables.AkState(req[0], req[1], req[2], req, true));
                }
        }

        // ------------------------------------------------------------------ respawn cooldown (`respawnSec`)
        // The chip row's state says whether the game's autokill GATE is satisfied. It says nothing about the
        // respawn clock, which the game checks separately and resets on every kill — so "ready" and "on
        // cooldown" coexist, and these guards pin the key that separates them. The rule under test is the
        // same one `regen` follows and it matters more here: `respawnSec: 0` is a positive claim that the
        // titan can be fought this second.

        [Fact]
        public void Zero_means_available_now_and_is_published_as_a_real_zero()
        {
            // The producer clamps at zero once the clock reaches spawn time, and the game reads that same
            // condition as "TITAN AVAILABLE!". Zero is a fact, not a failure — it must survive.
            Assert.Equal(0, TitanTables.RespawnSeconds(0f));
        }

        [Fact]
        public void An_unreadable_clock_is_absent_never_zero()
        {
            // Every one of these would render as "available now" if it collapsed to 0 — the exact lie the key
            // exists to stop, restated in the failure path.
            Assert.Null(TitanTables.RespawnSeconds(null));
            Assert.Null(TitanTables.RespawnSeconds(float.NaN));
            Assert.Null(TitanTables.RespawnSeconds(float.PositiveInfinity));
            Assert.Null(TitanTables.RespawnSeconds(float.NegativeInfinity));

            // A negative can only mean the producer's Mathf.Max(0f, ...) clamp is gone, i.e. we no longer
            // understand the reading. "Overdue, so available" is a guess in the one direction that hurts.
            Assert.Null(TitanTables.RespawnSeconds(-0.001f));
            Assert.Null(TitanTables.RespawnSeconds(-3600f));

            // Nothing in the game waits longer than boss12-14's 27000 s, so past a day it is an artifact.
            Assert.Null(TitanTables.RespawnSeconds((float)(TitanTables.RespawnSecMax + 1)));
            Assert.Equal(86400, TitanTables.RespawnSeconds((float)TitanTables.RespawnSecMax));
        }

        [Fact]
        public void Partial_seconds_round_up_so_a_wait_is_never_reported_as_available()
        {
            // Ceiling, not round: 0.4 s left is still a refusal, and reporting 0 would claim otherwise.
            Assert.Equal(1, TitanTables.RespawnSeconds(0.4f));
            Assert.Equal(1, TitanTables.RespawnSeconds(0.0001f));
            Assert.Equal(3600, TitanTables.RespawnSeconds(3600f));
            Assert.Equal(3600, TitanTables.RespawnSeconds(3599.2f));

            // The real ceiling: boss12-boss14 sit at 27000 s (7.5 h) with no challenge completions.
            Assert.Equal(27000, TitanTables.RespawnSeconds(27000f));
        }

        // A chip row shaped like the live capture: 12 entries carrying their own `i`, in order.
        private static JSONArray Row()
        {
            var arr = new JSONArray();
            for (int i = 0; i < 12; i++)
            {
                var o = new JSONObject();
                o["i"] = i;
                o["ab"] = TitanTables.Abbrev[i];
                o["state"] = i < 6 ? TitanTables.StateReady : TitanTables.StateShort;
                arr.Add(o);
            }
            return arr;
        }

        [Fact]
        public void The_stamp_publishes_a_clock_for_every_state_including_unknown()
        {
            // The clock is an independent read: it does not become less true because the gate read failed,
            // and a `gated` or `short` titan is on exactly the same cooldown as a `ready` one. Narrowing to
            // `ready` would also give absence a second meaning, which is the one thing it may not have.
            var arr = Row();
            ((JSONObject)arr[3])["state"] = TitanTables.StateGated;      // T4: stats met, item 135 not maxxed
            ((JSONObject)arr[7])["state"] = TitanTables.StateUnknown;    // a failed version/gate read

            TitanTables.StampRespawn(arr, i => 60f * (i + 1));

            for (int i = 0; i < 12; i++)
            {
                var o = (JSONObject)arr[i];
                Assert.True(o[TitanTables.KeyRespawn].IsNumber, "titan " + i + " lost its clock");
                Assert.Equal(60 * (i + 1), o[TitanTables.KeyRespawn].AsInt);
            }
        }

        [Fact]
        public void A_titan_that_can_be_autokilled_can_still_be_on_cooldown()
        {
            // The bug this key fixes, stated as a test: nothing about `ready` implies a zero clock.
            var arr = Row();
            TitanTables.StampRespawn(arr, i => 4823f);

            var beast = (JSONObject)arr[5];
            Assert.Equal(TitanTables.StateReady, beast["state"].Value);
            Assert.Equal(4823, beast[TitanTables.KeyRespawn].AsInt);
        }

        [Fact]
        public void A_read_that_throws_costs_that_titan_its_clock_and_nobody_elses()
        {
            // TimeTillTitanSpawn dereferences Main.Character plus two reflected members; a game update that
            // renames either yields a null and an NRE. Eleven working clocks must not go with it.
            var arr = Row();
            TitanTables.StampRespawn(arr, i =>
            {
                if (i == 6) throw new System.NullReferenceException("boss7Spawn");
                return 900f;
            });

            Assert.False(((JSONObject)arr[6])[TitanTables.KeyRespawn].IsNumber);
            for (int i = 0; i < 12; i++)
                if (i != 6) Assert.Equal(900, ((JSONObject)arr[i])[TitanTables.KeyRespawn].AsInt);
        }

        [Fact]
        public void A_clock_that_stops_being_readable_is_removed_not_frozen()
        {
            // The array is cached across ticks and re-stamped in place, so this is the failure mode that
            // actually ships: reads work, then stop. A key left behind would sit frozen on screen forever,
            // still claiming a countdown. It has to go, so absence keeps meaning "unknown".
            var arr = Row();
            TitanTables.StampRespawn(arr, i => 1200f);
            Assert.Equal(1200, ((JSONObject)arr[0])[TitanTables.KeyRespawn].AsInt);

            TitanTables.StampRespawn(arr, i => null);
            for (int i = 0; i < 12; i++)
                Assert.False(((JSONObject)arr[i])[TitanTables.KeyRespawn].IsNumber, "titan " + i + " kept a stale clock");

            // ...and the key is genuinely gone from the wire, not sitting there as null.
            Assert.DoesNotContain(TitanTables.KeyRespawn, arr.ToString());

            // Recovery works too: the row heals on the next tick that can read.
            TitanTables.StampRespawn(arr, i => 30f);
            Assert.Equal(30, ((JSONObject)arr[0])[TitanTables.KeyRespawn].AsInt);
        }

        [Fact]
        public void The_stamp_keys_off_the_entrys_own_index_and_never_invents_one()
        {
            // `titans.ak` is documented as indexed by its `i` field rather than by array position, so the
            // overlay must read that field. An entry without one gets no clock — and, critically, does not
            // get an `i` either: SimpleJson's JSONLazyCreator.AsInt WRITES a 0 into its parent on read, so a
            // careless lookup would both invent an index and stamp titan 0's cooldown onto the stranger.
            var arr = new JSONArray();
            var good = new JSONObject(); good["i"] = 5; arr.Add(good);
            var orphan = new JSONObject(); orphan["ab"] = "???"; arr.Add(orphan);

            var asked = new System.Collections.Generic.List<int>();
            TitanTables.StampRespawn(arr, i => { asked.Add(i); return 77f; });

            Assert.Equal(new[] { 5 }, asked);
            Assert.Equal(77, good[TitanTables.KeyRespawn].AsInt);
            Assert.False(orphan[TitanTables.KeyRespawn].IsNumber);
            Assert.DoesNotContain("\"i\"", orphan.ToString());
        }

        [Fact]
        public void The_stamp_survives_a_missing_row_a_null_reader_and_junk_entries()
        {
            TitanTables.StampRespawn(null, i => 1f);          // no row yet (first tick / rebuild failed)
            TitanTables.StampRespawn(Row(), null);            // no reader

            var arr = new JSONArray();
            arr.Add(new JSONString("not an entry"));
            var real = new JSONObject(); real["i"] = 0; arr.Add(real);
            TitanTables.StampRespawn(arr, i => 15f);
            Assert.Equal(15, real[TitanTables.KeyRespawn].AsInt);
        }

        [Fact]
        public void Stamped_clocks_serialize_as_plain_integers()
        {
            // These ship on the 1 Hz line; whole seconds must not arrive as G17 float noise.
            var arr = Row();
            TitanTables.StampRespawn(arr, i => 5400.0f);
            var parsed = JSON.Parse(arr.ToString());
            Assert.Contains("\"respawnSec\":5400", arr.ToString().Replace(" ", ""));
            Assert.Equal(5400, parsed[0][TitanTables.KeyRespawn].AsInt);
            Assert.True(parsed[0][TitanTables.KeyRespawn].IsNumber);
        }

        // ------------------------------------------------------------------ unlock gate (`unlocked`)
        // The third gate. `state` reports the game's autokill methods and `respawnSec` reports the cooldown;
        // NEITHER reads titan{N}Unlocked, which the game checks in both the autokill branch and the spawn
        // table for titans 6-9. A locked titan can therefore report `ready` with a zero clock forever, and
        // this key is the only thing on the wire that contradicts it.

        [Fact]
        public void The_four_riddle_titans_are_the_only_ones_with_an_unlock_flag()
        {
            // titan6/7/8/9Unlocked and nothing else exists in the game's Adventure save class, so this
            // predicate is the boundary between "read the flag" and "no such gate".
            for (int i = 0; i < TitanTables.Ak.Length; i++)
                Assert.Equal(i >= 5 && i <= 8, TitanTables.HasUnlockFlag(i));

            // Beast / Nerd / Godmother / Exile, by name, so a table reorder can't quietly move the gate.
            Assert.Equal("Beast", TitanTables.Abbrev[5]);
            Assert.Equal("Nerd", TitanTables.Abbrev[6]);
            Assert.Equal("Godmother", TitanTables.Abbrev[7]);
            Assert.Equal("Exile", TitanTables.Abbrev[8]);
        }

        [Fact]
        public void A_titan_without_an_unlock_gate_reports_unlocked_rather_than_nothing()
        {
            // Same rule as StatMet's sentinel-0 threshold: a gate that does not exist is satisfied by
            // definition. Publishing the vacuous true is what leaves absence with exactly one meaning.
            foreach (int i in new[] { 0, 4, 9, 11 })
            {
                Assert.True(TitanTables.UnlockState(i, null));
                Assert.True(TitanTables.UnlockState(i, false));   // a stray live read cannot lock these
            }
        }

        [Fact]
        public void An_unreadable_unlock_flag_is_absent_never_locked()
        {
            // The whole contract in one line: a failed read must not tell the player their Beast is locked.
            Assert.Null(TitanTables.UnlockState(5, null));
            Assert.Null(TitanTables.UnlockState(8, null));
            Assert.Null(TitanTables.UnlockState(-1, true));

            // ...and a read that DID work is reported as-is, in both directions.
            Assert.True(TitanTables.UnlockState(5, true));
            Assert.False(TitanTables.UnlockState(5, false));
        }

        [Fact]
        public void Locked_and_ready_coexist_because_they_answer_different_questions()
        {
            // The bug, restated: nothing about `state` implies the game will ever spawn the titan. The row
            // must be able to carry "the autokill gate is satisfied" and "it is locked" at the same time.
            var req = Row(5, 1);
            Assert.Equal(TitanTables.StateReady,
                TitanTables.AkState(req[0], req[1], req[2], req, autokillAvailable: true));
            Assert.False(TitanTables.UnlockState(5, false));
        }

        // ------------------------------------------------------------------ Walderp hide-and-seek (`waldo`)
        // Titan 5's respawn clock is the only one the game can FREEZE: it advances only while
        // `waldoDefeats <= waldoFinds || waldoFinds >= 4`. These pin the node that turns a stalled countdown
        // into an instruction.

        [Fact]
        public void Hiding_is_exactly_the_condition_that_freezes_the_clock()
        {
            // The game's gate is `waldoDefeats <= waldoFinds || waldoFinds >= 4`; hiding is its negation.
            // Sweep every reachable pair — defeats and finds are both 0..4 and finds never leads defeats.
            for (int d = 0; d <= TitanTables.WaldoFindsRequired; d++)
                for (int f = 0; f <= d; f++)
                {
                    bool clockRuns = d <= f || f >= TitanTables.WaldoFindsRequired;
                    Assert.Equal(!clockRuns, TitanTables.WaldoHiding(d, f));
                }

            // The `finds >= 4` half of the game's gate is redundant, and that is worth pinning: finds only
            // rise while hiding and defeats caps at 4, so `defeats > finds` already implies `finds < 4`.
            Assert.False(TitanTables.WaldoHiding(4, 4));
        }

        [Fact]
        public void The_hunt_advances_one_find_at_a_time_and_each_one_unfreezes_the_clock()
        {
            // The real loop: beat a decoy (defeats++), clock stops; find him (finds++), clock runs again.
            // Four times. A UI that reads this as "finds of 4" is right about the campaign; a UI that reads
            // it as "one find unblocks the timer" is right about right now. Both facts are on the wire.
            for (int d = 1; d <= TitanTables.WaldoFindsRequired; d++)
            {
                Assert.True(TitanTables.WaldoHiding(d, d - 1));    // decoy down, he is hidden
                Assert.False(TitanTables.WaldoHiding(d, d));       // found, clock resumes
            }
        }

        [Fact]
        public void A_hiding_walderp_publishes_where_he_is_when_that_is_known()
        {
            var o = TitanTables.WaldoNode(2, 1, 14, "Wandoos Menu");
            Assert.True(o["hiding"].AsBool);
            Assert.Equal(1, o["finds"].AsInt);
            Assert.Equal(2, o["defeats"].AsInt);
            Assert.Equal(14, o["menu"].AsInt);
            Assert.Equal("Wandoos Menu", o["menuName"].Value);
        }

        [Fact]
        public void Hiding_survives_an_unreadable_location()
        {
            // currentMenu is -1 for the whole first ~180 s after the game starts (waldoTimer is a scene
            // field, not save data) and again between each fade-out and the next relocation. He is hiding
            // throughout, so the explanation must not depend on knowing the menu.
            var o = TitanTables.WaldoNode(3, 1, null, null);
            Assert.True(o["hiding"].AsBool);
            Assert.False(o["menu"].IsNumber);
            Assert.DoesNotContain("menuName", o.ToString());

            // -1 is the game's own "not attached", not a menu id.
            Assert.False(TitanTables.WaldoNode(3, 1, -1, "   ")["menu"].IsNumber);
            Assert.DoesNotContain("menuName", TitanTables.WaldoNode(3, 1, -1, "   ").ToString());
        }

        [Fact]
        public void A_walderp_who_is_not_hiding_carries_no_menu_at_all()
        {
            // A menu id left over from the last hunt would read as a live instruction. He is not there.
            var o = TitanTables.WaldoNode(2, 2, 14, "Wandoos Menu");
            Assert.False(o["hiding"].AsBool);
            Assert.Equal(2, o["finds"].AsInt);
            Assert.DoesNotContain("menu", o.ToString());
        }

        [Fact]
        public void An_unreadable_waldo_state_is_absent_never_not_hiding()
        {
            // Same contract as respawnSec: the node's absence means "we could not look", and a UI must not
            // read it as "the clock is fine". Publishing hiding:false on a failed read would do exactly that.
            Assert.Null(TitanTables.WaldoNode(null, 1, 0, "x"));
            Assert.Null(TitanTables.WaldoNode(2, null, 0, "x"));
            Assert.Null(TitanTables.WaldoNode(null, null, null, null));
            Assert.Null(TitanTables.WaldoNode(-1, 0, null, null));
        }

        [Fact]
        public void The_waldo_node_rides_walderps_own_chip_and_serializes_plainly()
        {
            // It hangs off entry i == 4 so the frozen countdown and its explanation arrive together.
            Assert.Equal(4, TitanTables.WaldoTitanIndex);
            Assert.Equal("Walderp", TitanTables.Abbrev[TitanTables.WaldoTitanIndex]);

            var arr = Row();
            var walderp = (JSONObject)arr[TitanTables.WaldoTitanIndex];
            walderp[TitanTables.KeyWaldo] = TitanTables.WaldoNode(1, 0, 9, "Yggdrasil");
            TitanTables.StampRespawn(arr, i => 5400f);        // the stalled clock, stamped as usual

            var parsed = JSON.Parse(arr.ToString());
            var w = parsed[TitanTables.WaldoTitanIndex][TitanTables.KeyWaldo];
            Assert.True(w["hiding"].AsBool);
            Assert.Equal(0, w["finds"].AsInt);
            Assert.Equal(9, w["menu"].AsInt);
            Assert.Equal("Yggdrasil", w["menuName"].Value);
            // The clock is still published — it is a true reading of a frozen counter, and `hiding` is what
            // tells the UI not to render it as a countdown.
            Assert.Equal(5400, parsed[TitanTables.WaldoTitanIndex][TitanTables.KeyRespawn].AsInt);
        }

        [Fact]
        public void A_pathological_menu_name_cannot_bloat_the_snapshot_line()
        {
            // The name is a scene GameObject name — content we do not control — on a line published at 1 Hz.
            var o = TitanTables.WaldoNode(1, 0, 3, new string('x', 500));
            Assert.Equal(64, o["menuName"].Value.Length);
        }
    }
}
