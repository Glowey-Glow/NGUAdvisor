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

        // ---- Challenge rotation ("CODE-count") ----

        [Fact]
        public void Challenge_parse_validates_code_and_clamps_count()
        {
            Assert.True(BreakpointEditor.TryParseChallenge("LSC-20", out var a));
            Assert.Equal("LSC", a.Code); Assert.Equal(20, a.Count);
            Assert.True(BreakpointEditor.TryParseChallenge("24HR-99", out var b));
            Assert.Equal(10, b.Count);                       // clamped to 24HR's cap (10)
            Assert.True(BreakpointEditor.TryParseChallenge("lsc-0", out var c));
            Assert.Equal(1, c.Count);                        // clamped up to 1; code uppercased
            Assert.Equal("LSC", c.Code);
            Assert.False(BreakpointEditor.TryParseChallenge("BOGUS-3", out _));
            Assert.False(BreakpointEditor.TryParseChallenge("nodash", out _));
        }

        [Fact]
        public void Canon_challenges_drops_unknown_clamps_and_dedupes_keeping_order()
        {
            var outp = BreakpointEditor.CanonChallenges(new[] { "LSC-20", "24HR-99", "BOGUS-3", "lsc-5" });
            Assert.Equal(new List<string> { "LSC-20", "24HR-10" }, outp);   // BOGUS dropped, 99->10, lsc dup dropped, order kept
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
