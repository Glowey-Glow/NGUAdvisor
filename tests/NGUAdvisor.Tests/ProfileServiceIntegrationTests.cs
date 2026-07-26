using System;
using System.IO;
using NGUAdvisorCompanion;   // the companion's real ProfileService (linked source)
using SimpleJSON;
using Xunit;

namespace NGUAdvisor.Tests
{
    // End-to-end test of the companion's ACTUAL Timeline-Editor write path — the same ProfileService methods
    // the running companion calls in-game — against a real profile file in a temp dir. Covers the full round
    // trip: load -> mutate -> semantic-validate (BreakpointEditor) -> structural-validate (ProfileValidator)
    // -> write in place -> re-read into the display "timelines" message. Nothing here needs the game.
    public class ProfileServiceIntegrationTests : IDisposable
    {
        private readonly string _dir;
        private const string Name = "TestProfile";

        private const string Seed = @"{
  ""Breakpoints"": {
    ""Energy"":  [ { ""Time"": 0, ""Priorities"": [ ""NGU"" ] } ],
    ""Diggers"": [ { ""Time"": 0, ""List"": [ 2 ] } ],
    ""NGUDiff"": [ { ""Time"": 0, ""Diff"": 0 } ],
    ""Rebirth"": [ { ""Type"": ""Time"", ""Time"": { ""h"": 24 } } ],
    ""FutureSystem"": { ""keep"": ""verbatim"" }
  }
}";

        public ProfileServiceIntegrationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "NGUAdvisorTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, Name + ".json"), Seed);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static JSONArray Rows(string timelinesMsg, string system) =>
            JSON.Parse(timelinesMsg)["systems"][system].AsArray;

        private string OnDisk() => File.ReadAllText(Path.Combine(_dir, Name + ".json"));

        [Fact]
        public void Add_then_edit_energy_persists_and_reloads()
        {
            // ADD an energy breakpoint at 20m with content, in one call.
            var msg = ProfileService.AddBreakpoint(_dir, Name, "energy", 1200, "NGU-3, WAN", "", null);
            var rows = Rows(msg, "energy");
            Assert.Equal(2, rows.Count);
            Assert.Equal("NGU-3, WAN", rows[1]["payload"].Value);      // sorted after t=0, content applied
            Assert.Equal(1200, rows[1]["sec"].AsInt);

            // EDIT it: add a percent-capped token + a challenge tag.
            var msg2 = ProfileService.EditBreakpoint(_dir, Name, "energy", 1, 1800, "NGU-3, CAPAUG-10:50", "24HR", null);
            var rows2 = Rows(msg2, "energy");
            Assert.Equal("NGU-3, CAPAUG-10:50", rows2[1]["payload"].Value);
            Assert.Equal("24HR", rows2[1]["challenge"].Value);
            Assert.Equal(1800, rows2[1]["sec"].AsInt);

            // The unmodeled system survived every write.
            Assert.Contains("FutureSystem", OnDisk());
        }

        [Fact]
        public void Edit_rebirth_type_and_target_round_trips()
        {
            var msg = ProfileService.EditBreakpoint(_dir, Name, "rebirth", 0, 3600, "Number", null, "1000000");
            var row = Rows(msg, "rebirth")[0];
            Assert.Equal("Number", row["payload"].Value);
            Assert.Equal("1000000", row["target"].Value);
            Assert.Contains("Number", OnDisk());
        }

        [Fact]
        public void Invalid_payload_throws_and_does_not_touch_the_file()
        {
            var before = OnDisk();
            // digger slot 99 is out of range (0-11) — BreakpointEditor must reject before any write.
            var ex = Assert.Throws<Exception>(() =>
                ProfileService.EditBreakpoint(_dir, Name, "diggers", 0, 0, "99", "", null));
            Assert.Contains("range", ex.Message);
            Assert.Equal(before, OnDisk());          // file untouched on a rejected edit
        }

        [Fact]
        public void SetChallenges_writes_canonical_rotation_and_reloads()
        {
            // 24HR kept at 3; LSC 99 clamped to its cap (20); the profile gains a Challenges array.
            var msg = ProfileService.SetChallenges(_dir, Name, new[] { "24HR-3", "LSC-99" });
            var chal = JSON.Parse(msg)["challenges"].AsArray;
            Assert.Equal(2, chal.Count);
            Assert.Equal("24HR", chal[0]["code"].Value);
            Assert.Equal(3, chal[0]["count"].AsInt);
            Assert.Equal("LSC", chal[1]["code"].Value);
            Assert.Equal(20, chal[1]["count"].AsInt);        // clamped
            var disk = OnDisk();
            Assert.Contains("Challenges", disk);
            Assert.Contains("LSC-20", disk);
            Assert.Contains("FutureSystem", disk);           // passthrough survived
        }

        [Fact]
        public void Add_edit_delete_full_lifecycle()
        {
            ProfileService.AddBreakpoint(_dir, Name, "ngudiff", 600, "1", "", null);   // add Evil at 10m
            var afterAdd = Rows(ProfileService.BuildTimelinesJson(_dir, Name), "ngudiff");
            Assert.Equal(2, afterAdd.Count);

            var afterDel = Rows(ProfileService.DeleteBreakpoint(_dir, Name, "ngudiff", 1), "ngudiff");
            Assert.Equal(1, afterDel.Count);         // back to just the seed breakpoint
            Assert.Contains("FutureSystem", OnDisk());
        }
    }
}
