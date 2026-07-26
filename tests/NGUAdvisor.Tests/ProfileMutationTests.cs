using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // Guards the companion Timeline-Editor DELETE op (M11 first slice): RemoveBreakpoint mutates the shared
    // model losslessly (unmodeled systems / extras preserved) and is a safe no-op on bad input. ProfileService
    // wraps this with load -> validate -> write, so testing the model here covers the risky part headlessly.
    public class ProfileMutationTests
    {
        private const string Sample = @"{
  ""Breakpoints"": {
    ""Energy"": [
      { ""Time"": 0, ""Priorities"": [ ""NGU"" ] },
      { ""Time"": 1200, ""Priorities"": [ ""WAN"" ] }
    ],
    ""FutureSystem"": { ""keep"": ""verbatim"" }
  }
}";

        [Fact]
        public void RemoveBreakpoint_deletes_the_indexed_breakpoint()
        {
            var m = ProfileModel.Load(Sample);
            Assert.Equal(2, m.Energy.Count);
            Assert.True(m.RemoveBreakpoint("energy", 0));
            Assert.Single(m.Energy);
            Assert.Equal(1200, m.Energy[0].TimeSeconds);   // the survivor is the second breakpoint
        }

        [Fact]
        public void RemoveBreakpoint_roundtrips_and_preserves_passthrough()
        {
            var m = ProfileModel.Load(Sample);
            m.RemoveBreakpoint("energy", 1);
            var json = m.ToJson();
            Assert.Contains("FutureSystem", json);         // unmodeled system survives the mutation
            var reloaded = ProfileModel.Load(json);
            Assert.Single(reloaded.Energy);
            Assert.Equal(0, reloaded.Energy[0].TimeSeconds);
        }

        [Theory]
        [InlineData("energy", 5)]
        [InlineData("energy", -1)]
        [InlineData("nosuchsystem", 0)]
        public void RemoveBreakpoint_is_a_safe_noop_on_bad_input(string system, int index)
        {
            var m = ProfileModel.Load(Sample);
            Assert.False(m.RemoveBreakpoint(system, index));
            Assert.Equal(2, m.Energy.Count);               // unchanged
        }
    }
}
