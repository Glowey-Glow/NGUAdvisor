using System.Collections.Generic;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // Guards the companion Timeline-Editor ADD + full content-EDIT ops (M11). The risky logic is the payload
    // parse + SEMANTIC validation (tokens/indices/codes/types) and the shared-model mutation; ProfileService
    // wraps this with load -> ProfileValidator(JSON) -> write, so validating the model + BreakpointEditor here
    // covers the risky part headlessly. Every path asserts a lossless round-trip preserves passthrough systems.
    public class BreakpointEditorTests
    {
        // Contains every modeled system plus an unmodeled passthrough system, so losslessness is testable.
        private const string Sample = @"{
  ""Breakpoints"": {
    ""Energy"":      [ { ""Time"": 0, ""Priorities"": [ ""NGU"" ] } ],
    ""Magic"":       [ { ""Time"": 0, ""Priorities"": [ ""WAN"" ] } ],
    ""R3"":          [ { ""Time"": 0, ""Priorities"": [ ""ALLHACK"" ] } ],
    ""Gear"":        [ { ""Time"": 0, ""ID"": [ 100, 200 ] } ],
    ""Diggers"":     [ { ""Time"": 0, ""List"": [ 2, 4 ] } ],
    ""Beards"":      [ { ""Time"": 0, ""List"": [ 0 ] } ],
    ""Wandoos"":     [ { ""Time"": 0, ""OS"": 0 } ],
    ""NGUDiff"":     [ { ""Time"": 0, ""Diff"": 0 } ],
    ""Consumables"": [ { ""Time"": 0, ""Items"": [ ""LC"" ] } ],
    ""Rebirth"":     [ { ""Type"": ""Time"", ""Time"": { ""h"": 24 } } ],
    ""FutureSystem"": { ""keep"": ""verbatim"" }
  }
}";

        private static ProfileModel Load() => ProfileModel.Load(Sample);

        private static void AssertLossless(ProfileModel m)
        {
            var json = m.ToJson();
            Assert.Contains("FutureSystem", json);          // unmodeled system survives
            ProfileModel.Load(json);                        // still parses cleanly
        }

        // ---- Add ----

        [Fact]
        public void Add_inserts_in_ascending_time_order()
        {
            var m = Load();
            Assert.Equal(1, m.AddBreakpoint("energy", 600));   // appended after t=0
            Assert.Equal(1, m.AddBreakpoint("energy", 300));   // inserted between 0 and 600
            Assert.Equal(new[] { 0, 300, 600 }, new[] { m.Energy[0].TimeSeconds, m.Energy[1].TimeSeconds, m.Energy[2].TimeSeconds });
            AssertLossless(m);
        }

        [Fact]
        public void Add_unknown_system_returns_minus_one()
        {
            Assert.Equal(-1, Load().AddBreakpoint("nope", 0));
        }

        [Fact]
        public void Add_then_apply_content_is_one_atomic_edit()
        {
            var m = Load();
            int i = m.AddBreakpoint("diggers", 1200);
            var r = BreakpointEditor.Apply(m, "diggers", i, 1200, "8, 11", "", null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(new List<int> { 8, 11 }, m.Diggers[i].Items);
            Assert.Equal(1200, m.Diggers[i].TimeSeconds);
            AssertLossless(m);
        }

        // ---- Priority (energy/magic/r3) ----

        [Fact]
        public void Priority_valid_tokens_are_canonicalized()
        {
            var m = Load();
            var r = BreakpointEditor.Apply(m, "energy", 0, 0, "ngu-3, wan, capaug-10:50", "", null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(new List<string> { "NGU-3", "WAN", "CAPAUG-10:50" }, m.Energy[0].Priorities);
            AssertLossless(m);
        }

        [Fact]
        public void Priority_rejects_unknown_token()
        {
            var r = BreakpointEditor.Apply(Load(), "energy", 0, 0, "NGU, BOGUS", "", null);
            Assert.False(r.Ok);
            Assert.Contains("not a known", r.Error);
        }

        [Fact]
        public void Priority_rejects_token_from_the_wrong_resource()
        {
            // HACK is an R3-only base; it must be rejected on an Energy timeline.
            var r = BreakpointEditor.Apply(Load(), "energy", 0, 0, "HACK-3", "", null);
            Assert.False(r.Ok);
            Assert.Contains("Energy", r.Error);
        }

        [Fact]
        public void Priority_rejects_index_out_of_range()
        {
            var r = BreakpointEditor.Apply(Load(), "energy", 0, 0, "NGU-99", "", null);   // energy NGU max 8
            Assert.False(r.Ok);
            Assert.Contains("out of range", r.Error);
        }

        [Fact]
        public void Priority_empty_payload_clears_the_list()
        {
            var m = Load();
            var r = BreakpointEditor.Apply(m, "energy", 0, 0, "", "", null);
            Assert.True(r.Ok, r.Error);
            Assert.Empty(m.Energy[0].Priorities);
        }

        // ---- Int lists (diggers / beards) ----

        [Fact]
        public void Diggers_valid_indices_apply()
        {
            var m = Load();
            var r = BreakpointEditor.Apply(m, "diggers", 0, 0, "0, 8, 11", "", null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(new List<int> { 0, 8, 11 }, m.Diggers[0].Items);
        }

        [Theory]
        [InlineData("diggers", "12")]   // digger slots 0-11
        [InlineData("beards", "7")]     // beard slots 0-6
        [InlineData("diggers", "x")]
        public void Int_list_rejects_out_of_range_or_nonnumeric(string sys, string payload)
        {
            var r = BreakpointEditor.Apply(Load(), sys, 0, 0, payload, "", null);
            Assert.False(r.Ok);
        }

        // ---- Value (wandoos / ngudiff) ----

        [Fact]
        public void Value_valid_in_range_applies()
        {
            var m = Load();
            Assert.True(BreakpointEditor.Apply(m, "ngudiff", 0, 0, "1", "", null).Ok);
            Assert.Equal(1, m.NGUDiff[0].Value);   // Evil
        }

        [Theory]
        [InlineData("3")]
        [InlineData("")]
        [InlineData("abc")]
        public void Value_rejects_out_of_range_or_empty(string payload)
        {
            Assert.False(BreakpointEditor.Apply(Load(), "wandoos", 0, 0, payload, "", null).Ok);
        }

        // ---- Consumables ----

        [Fact]
        public void Consumables_valid_codes_and_amounts_canonicalize()
        {
            var m = Load();
            var r = BreakpointEditor.Apply(m, "consumables", 0, 0, "lc, muffin:5", "", null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(new List<string> { "LC", "MUFFIN:5" }, m.Consumables[0].Items);
        }

        [Theory]
        [InlineData("NOPE")]
        [InlineData("LC:0")]
        [InlineData("LC:x")]
        public void Consumables_rejects_bad_code_or_amount(string payload)
        {
            Assert.False(BreakpointEditor.Apply(Load(), "consumables", 0, 0, payload, "", null).Ok);
        }

        // ---- Gear (ID list OR optimize objective) ----

        [Fact]
        public void Gear_id_list_sets_items_and_clears_objective()
        {
            var m = Load();
            m.SetGearObjective(0, "PreviousObjective", true);          // start in objective mode
            var r = BreakpointEditor.Apply(m, "gear", 0, 0, "300, 400", "", null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(new List<int> { 300, 400 }, m.Gear[0].Items);
            Assert.Equal("", m.Gear[0].Objective);
            Assert.False(m.Gear[0].ForceRespawn);
        }

        [Fact]
        public void Gear_optimize_objective_with_respawn()
        {
            var m = Load();
            var r = BreakpointEditor.Apply(m, "gear", 0, 0, "Optimize+Respawn: EnergyPower", "", null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal("EnergyPower", m.Gear[0].Objective);
            Assert.True(m.Gear[0].ForceRespawn);
            Assert.Empty(m.Gear[0].Items);
        }

        [Fact]
        public void Gear_empty_objective_is_rejected()
        {
            Assert.False(BreakpointEditor.Apply(Load(), "gear", 0, 0, "Optimize:", "", null).Ok);
        }

        // ---- Rebirth (Type + optional Target) ----

        [Fact]
        public void Rebirth_number_target_parses_scientific_notation()
        {
            var m = Load();
            var r = BreakpointEditor.Apply(m, "rebirth", 0, 3600, "Number", null, "1e6");
            Assert.True(r.Ok, r.Error);
            Assert.Equal("Number", m.Rebirth[0].Type);
            Assert.Equal(1e6, m.Rebirth[0].Target.Value, 3);
            Assert.Equal(3600, m.Rebirth[0].TimeSeconds);
        }

        [Fact]
        public void Rebirth_time_type_needs_no_target()
        {
            var m = Load();
            var r = BreakpointEditor.Apply(m, "rebirth", 0, 3600, "Time", null, "");
            Assert.True(r.Ok, r.Error);
            Assert.False(m.Rebirth[0].Target.HasValue);
        }

        [Theory]
        [InlineData("Number", "")]      // target-taking type with no target
        [InlineData("Bogus", "5")]      // unknown type
        public void Rebirth_rejects_missing_target_or_unknown_type(string type, string target)
        {
            Assert.False(BreakpointEditor.Apply(Load(), "rebirth", 0, 0, type, null, target).Ok);
        }

        // ---- Challenge tag ----

        [Fact]
        public void Challenge_valid_code_is_uppercased_and_set()
        {
            var m = Load();
            var r = BreakpointEditor.Apply(m, "energy", 0, 0, "NGU", "24hr", null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal("24HR", m.Energy[0].Challenge);
        }

        [Fact]
        public void Challenge_unknown_code_is_rejected()
        {
            Assert.False(BreakpointEditor.Apply(Load(), "energy", 0, 0, "NGU", "ZZZ", null).Ok);
        }

        [Fact]
        public void Empty_challenge_clears_the_tag()
        {
            var m = Load();
            BreakpointEditor.Apply(m, "energy", 0, 0, "NGU", "24HR", null);
            BreakpointEditor.Apply(m, "energy", 0, 0, "NGU", "", null);
            Assert.Equal("", m.Energy[0].Challenge);
        }

        // ---- Challenge rotation ("CODE-<ordinal>" in the profile, "x N" targets in the editor) ----
        //
        // The runtime (BaseRebirth.ChallengeValid) matches `ordinal == currentCompletions() + 1`, so the number
        // in a profile entry is a 1-BASED COMPLETION ORDINAL. The editor shows friendly targets and translates:
        // a bare "CODE-N" from the "x N" control EXPANDS to CODE-1..CODE-N; "CODE-A..B" is the raw/advanced form
        // that survives verbatim. The old editor treated the number as a count and deduped on CODE alone, which
        // rewrote the shipped 5-entry 100LC rotation as a single "100LC-1" — the regression guarded below.

        [Fact]
        public void Challenge_parse_reads_an_ordinal_and_clamps_it_to_the_cap()
        {
            Assert.True(BreakpointEditor.TryParseChallenge("LSC-20", out var a));
            Assert.Equal("LSC", a.Code); Assert.Equal(20, a.Index);
            Assert.True(BreakpointEditor.TryParseChallenge("24HR-99", out var b));
            Assert.Equal(10, b.Index);                       // clamped to 24HR's cap (10)
            Assert.True(BreakpointEditor.TryParseChallenge("lsc-0", out var c));
            Assert.Equal(1, c.Index);                        // clamped up to 1; code uppercased
            Assert.Equal("LSC", c.Code);
            // Ordinal 0 is not a sentinel — see ChallengeRotationTests for why redefining it was rejected.
            // Negatives clamp the same way.
            Assert.True(BreakpointEditor.TryParseChallenge("LSC--4", out var d));
            Assert.Equal(1, d.Index);
            Assert.False(BreakpointEditor.TryParseChallenge("BOGUS-3", out _));
            Assert.False(BreakpointEditor.TryParseChallenge("nodash", out _));
            Assert.False(BreakpointEditor.TryParseChallenge("LSC-3..5", out _));   // range is not a single ordinal
        }

        // THE REGRESSION: the shipped preset CBlock3.1-E100LC must survive load -> collapse -> save untouched.
        // Before the fix, CanonChallenges deduped on CODE and this five-challenge rotation became ["100LC-1"].
        [Fact]
        public void Shipped_five_challenge_rotation_round_trips_with_zero_loss()
        {
            var shipped = new[] { "100LC-1", "100LC-2", "100LC-3", "100LC-4", "100LC-5" };

            var rows = BreakpointEditor.GroupChallenges(shipped);
            Assert.Single(rows);                                        // one friendly row...
            Assert.Equal("100LC", rows[0].Code);
            Assert.Equal(1, rows[0].From);
            Assert.Equal(5, rows[0].Target);                            // ...displayed as "100 Level x 5"
            Assert.Equal(new List<int> { 1, 2, 3, 4, 5 }, rows[0].Ordinals);
            Assert.False(rows[0].Suspect);

            // Echoed back unchanged (the row's own token) -> byte-identical rotation.
            Assert.Equal(shipped, BreakpointEditor.CanonChallenges(new[] { rows[0].Entry }).ToArray());
            // ...and the friendly "x 5" control produces the same thing.
            Assert.Equal(shipped, BreakpointEditor.CanonChallenges(new[] { "100LC-5" }).ToArray());
        }

        [Fact]
        public void Expand_target_writes_the_full_ordinal_range()
        {
            Assert.Equal(new List<string> { "BASIC-1", "BASIC-2", "BASIC-3", "BASIC-4", "BASIC-5" },
                         BreakpointEditor.ExpandTarget("BASIC", 5));
            Assert.Equal(new List<string> { "BASIC-1" }, BreakpointEditor.ExpandTarget("basic", 1));
            Assert.Empty(BreakpointEditor.ExpandTarget("BOGUS", 3));
        }

        [Fact]
        public void Expand_target_clamps_above_the_cap()
        {
            var e = BreakpointEditor.ExpandTarget("24HR", 99);            // 24HR caps at 10
            Assert.Equal(10, e.Count);
            Assert.Equal("24HR-10", e[9]);
            Assert.Equal(BreakpointEditor.ExpandTarget("24HR", 10), e);
            // ...and via the write path, in both the bare and explicit target forms.
            Assert.Equal(e, BreakpointEditor.CanonChallenges(new[] { "24HR-99" }));
            Assert.Equal(e, BreakpointEditor.CanonChallenges(new[] { "24HR*99" }));
        }

        [Fact]
        public void Collapse_takes_the_highest_ordinal_of_a_contiguous_run()
        {
            var full = BreakpointEditor.GroupChallenges(new[] { "BASIC-1", "BASIC-2", "BASIC-3", "BASIC-4", "BASIC-5" });
            Assert.Single(full);
            Assert.Equal(5, full[0].Target); Assert.Equal(1, full[0].From);
            Assert.False(full[0].Partial);

            // A hand-trimmed run means the same thing — "get to five total" — so it also reads as x 5.
            var trimmed = BreakpointEditor.GroupChallenges(new[] { "BASIC-3", "BASIC-4", "BASIC-5" });
            Assert.Single(trimmed);
            Assert.Equal(5, trimmed[0].Target); Assert.Equal(3, trimmed[0].From);
            Assert.True(trimmed[0].Partial);
            // Untouched, it round-trips exactly (only retargeting expands it to the full 1..5 range).
            Assert.Equal(new[] { "BASIC-3", "BASIC-4", "BASIC-5" },
                         BreakpointEditor.CanonChallenges(new[] { trimmed[0].Entry }).ToArray());
        }

        [Fact]
        public void Non_contiguous_ordinals_survive_losslessly()
        {
            var raw = new[] { "BASIC-2", "BASIC-4" };
            var rows = BreakpointEditor.GroupChallenges(raw);
            Assert.Equal(2, rows.Count);                                  // two rows, NOT flattened into one count
            Assert.Equal("BASIC-2..2", rows[0].Entry);
            Assert.Equal("BASIC-4..4", rows[1].Entry);
            Assert.Equal(raw, BreakpointEditor.CanonChallenges(new[] { rows[0].Entry, rows[1].Entry }).ToArray());
        }

        // Rotation ORDER is meaning: BaseRebirth engages the FIRST eligible entry, so a code that appears in two
        // separate places (CBlock4 ships 24HR-2,3,4 ... then 24HR-5 last) must stay two rows in two places.
        [Fact]
        public void A_code_appearing_twice_stays_two_rows_in_place()
        {
            var raw = new[] { "24HR-2", "24HR-3", "24HR-4", "NOEC-1", "NOEC-2", "24HR-5" };
            var rows = BreakpointEditor.GroupChallenges(raw);
            Assert.Equal(3, rows.Count);
            Assert.Equal(new[] { "24HR", "NOEC", "24HR" }, new[] { rows[0].Code, rows[1].Code, rows[2].Code });
            Assert.Equal(4, rows[0].Target);
            Assert.Equal(5, rows[2].From);                                // the trailing 24HR-5 keeps its place
            var back = new List<string>();
            foreach (var r in rows) back.Add(r.Entry);
            Assert.Equal(raw, BreakpointEditor.CanonChallenges(back).ToArray());
        }

        [Fact]
        public void Canon_dedupes_on_code_and_ordinal_not_on_code_alone()
        {
            // Genuine duplicate collapses...
            Assert.Equal(new List<string> { "BASIC-1" }, BreakpointEditor.CanonChallenges(new[] { "BASIC-1..1", "BASIC-1..1" }));
            // ...while DISTINCT ordinals of the same code all survive (the data-loss bug).
            Assert.Equal(new List<string> { "BASIC-1", "BASIC-2", "BASIC-4" },
                         BreakpointEditor.CanonChallenges(new[] { "BASIC-1..2", "BASIC-4..4", "BASIC-2..2" }));
            // A repeated target is idempotent rather than additive.
            Assert.Equal(new List<string> { "BASIC-1", "BASIC-2" }, BreakpointEditor.CanonChallenges(new[] { "BASIC-2", "BASIC-2" }));
        }

        [Fact]
        public void Canon_drops_unknown_codes_and_keeps_order()
        {
            var outp = BreakpointEditor.CanonChallenges(new[] { "LSC-20..20", "BOGUS-3", "24HR-2..3", "" });
            Assert.Equal(new List<string> { "LSC-20", "24HR-2", "24HR-3" }, outp);
            Assert.Empty(BreakpointEditor.CanonChallenges(null));
        }

        // The old editor's damage shape: a lone ordinal above 1 (["BASIC-5"] written for "Basic x 5"), which
        // fires only on the 5th completion. Flagged, never rewritten — chained C-block presets author it too.
        [Fact]
        public void Lone_high_ordinal_is_flagged_repairable_but_not_rewritten()
        {
            var rows = BreakpointEditor.GroupChallenges(new[] { "BASIC-5" });
            Assert.Single(rows);
            Assert.True(rows[0].Single);
            Assert.True(rows[0].Suspect);
            Assert.Equal(5, rows[0].From);                                // cannot fire until 4 are done by hand
            Assert.Equal("BASIC-1..5", rows[0].Repair);
            Assert.Equal(new[] { "BASIC-5" }, BreakpointEditor.CanonChallenges(new[] { rows[0].Entry }).ToArray());
            Assert.Equal(new[] { "BASIC-1", "BASIC-2", "BASIC-3", "BASIC-4", "BASIC-5" },
                         BreakpointEditor.CanonChallenges(new[] { rows[0].Repair }).ToArray());

            Assert.False(BreakpointEditor.GroupChallenges(new[] { "BASIC-1" })[0].Suspect);        // ordinal 1 is normal
        }

        // Rotations of the shape SampleProfiles ships: heavily interleaved, runs that start partway in, and
        // codes that reappear later. Displaying then saving one must not move a single entry.
        [Theory]
        // CBlock4.json
        [InlineData("24HR-2 24HR-3 24HR-4 NONGU-2 NONGU-3 NONGU-4 NONGU-5 NONGU-6 NONGU-7 TC-3 TC-4 TC-5 " +
                    "BLIND-2 BLIND-3 BLIND-4 BLIND-5 BLIND-6 BLIND-7 NOTM-2 NOTM-3 NOTM-4 NOTM-5 " +
                    "NOEC-1 NOEC-2 NOEC-3 NOEC-4 NOEC-5 NORB-7 NORB-8 NORB-9 NORB-10 24HR-5")]
        // CBlock5.json as it ships now: the LSC-3..20 prefix was removed (LscAdvisor owns Laser Sword).
        [InlineData("BLIND-8 BLIND-9 BLIND-10 NONGU-8 NONGU-9 NONGU-10 " +
                    "TC-6 TC-7 NOTM-6 NOTM-7 NOTM-8 NOTM-9 NOTM-10 24HR-6 24HR-7")]
        // CBlock5.json as it shipped before that, kept because a leading opens-at-3 run in front of chained
        // codes is exactly the display-then-save shape most likely to reorder entries.
        [InlineData("LSC-3 LSC-4 LSC-5 LSC-6 LSC-7 LSC-8 LSC-9 LSC-10 LSC-11 LSC-12 LSC-13 LSC-14 LSC-15 " +
                    "LSC-16 LSC-17 LSC-18 LSC-19 LSC-20 BLIND-8 BLIND-9 BLIND-10 NONGU-8 NONGU-9 NONGU-10 " +
                    "TC-6 TC-7 NOTM-6 NOTM-7 NOTM-8 NOTM-9 NOTM-10 24HR-6 24HR-7")]
        // CBlock3.2-E.json
        [InlineData("NOTM-1 NOAUG-1 NOAUG-2 NOAUG-3 NOAUG-4 NOAUG-5 TC-1 TC-2 NONGU-1 " +
                    "BASIC-1 BASIC-2 BASIC-3 BASIC-4 BASIC-5 BLIND-1 NORB-1 NORB-2 NORB-3 NORB-4 NORB-5 NORB-6 24HR-1")]
        public void Shipped_rotations_survive_a_display_then_save(string rotation)
        {
            var raw = rotation.Split(' ');
            var entries = new List<string>();
            foreach (var g in BreakpointEditor.GroupChallenges(raw)) entries.Add(g.Entry);
            Assert.Equal(raw, BreakpointEditor.CanonChallenges(entries).ToArray());
        }

        [Fact]
        public void Live_cap_overrides_the_catalog_when_wired()
        {
            var prev = BreakpointEditor.LiveCap;
            try
            {
                BreakpointEditor.LiveCap = code => code == "BASIC" ? 3 : 0;      // 0 => fall back to the catalog
                Assert.Equal(3, BreakpointEditor.ChallengeCap("BASIC"));
                Assert.Equal(3, BreakpointEditor.ExpandTarget("BASIC", 5).Count);
                Assert.Equal(5, BreakpointEditor.ChallengeCap("100LC"));         // catalog fallback intact
            }
            finally { BreakpointEditor.LiveCap = prev; }
            Assert.Equal(5, BreakpointEditor.ChallengeCap("BASIC"));             // catalog again once unwired
        }

        // ---- Bounds ----

        [Fact]
        public void Apply_out_of_range_index_fails_cleanly()
        {
            var r = BreakpointEditor.Apply(Load(), "energy", 9, 0, "NGU", "", null);
            Assert.False(r.Ok);
            Assert.Contains("index", r.Error);
        }
    }
}
