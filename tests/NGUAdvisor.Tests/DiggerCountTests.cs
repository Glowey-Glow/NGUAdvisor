using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // A digger breakpoint's "Count" is how many diggers should be ACTIVE there, which is deliberately
    // NOT the length of its slot list: the list is a priority ORDER, so you can name your top two and
    // still ask for four active, leaving the advisor to pick the rest.
    //
    // The load-bearing property is that a profile which never uses the key round-trips byte-identically
    // and behaves exactly as it did before the key existed.
    public class DiggerCountTests
    {
        private const string NoCount = @"{ ""Breakpoints"": {
            ""Diggers"": [ { ""Time"": { ""h"": 0 }, ""List"": [3, 8, 0] } ]
        } }";

        private const string WithCount = @"{ ""Breakpoints"": {
            ""Diggers"": [ { ""Time"": { ""h"": 0 }, ""List"": [3, 8], ""Count"": 4 } ]
        } }";

        [Fact]
        public void ACountIsReadBack()
        {
            var m = ProfileModel.Load(WithCount);
            Assert.Single(m.Diggers);
            Assert.Equal(4, m.Diggers[0].Count);
            Assert.Equal(new[] { 3, 8 }, m.Diggers[0].Items.ToArray());
        }

        [Fact]
        public void AbsentCountReadsAsZero()
        {
            var m = ProfileModel.Load(NoCount);
            Assert.Equal(0, m.Diggers[0].Count);
        }

        [Fact]
        public void ACountSurvivesARoundTrip()
        {
            var back = ProfileModel.Load(ProfileModel.Load(WithCount).ToJson());
            Assert.Equal(4, back.Diggers[0].Count);
            Assert.Equal(new[] { 3, 8 }, back.Diggers[0].Items.ToArray());
        }

        [Fact]
        public void AProfileWithoutACountDoesNotGrowOne()
        {
            // The whole compatibility claim: emitting Count unconditionally would rewrite every shipped
            // profile on the first edit, and a "0" would then have to mean something.
            var json = ProfileModel.Load(NoCount).ToJson();
            Assert.DoesNotContain("\"Count\"", json);
            Assert.Equal(0, ProfileModel.Load(json).Diggers[0].Count);
        }

        [Fact]
        public void TheEditorAcceptsATrailingCount()
        {
            var m = ProfileModel.Load(NoCount);
            var r = BreakpointEditor.Apply(m, "diggers", 0, 0, "3, 8 x4", "", "");
            Assert.True(r.Ok, r.Error);
            Assert.Equal(new[] { 3, 8 }, m.Diggers[0].Items.ToArray());
            Assert.Equal(4, m.Diggers[0].Count);
        }

        [Fact]
        public void TheEditorStillAcceptsAPlainList()
        {
            var m = ProfileModel.Load(NoCount);
            var r = BreakpointEditor.Apply(m, "diggers", 0, 0, "3, 8, 0", "", "");
            Assert.True(r.Ok, r.Error);
            Assert.Equal(new[] { 3, 8, 0 }, m.Diggers[0].Items.ToArray());
            Assert.Equal(0, m.Diggers[0].Count);
        }

        [Fact]
        public void ACountSmallerThanTheListIsRejected()
        {
            // Listing four and asking for two is a contradiction the user should see, not a silent
            // truncation of the order they just typed.
            var m = ProfileModel.Load(NoCount);
            var r = BreakpointEditor.Apply(m, "diggers", 0, 0, "3, 8, 0, 1 x2", "", "");
            Assert.False(r.Ok);
            Assert.Contains("only 2", r.Error);
        }

        [Theory]
        [InlineData("3, 8 x0")]
        [InlineData("3, 8 x13")]
        [InlineData("3, 8 x-1")]
        public void AnOutOfRangeCountIsRejected(string payload)
        {
            var m = ProfileModel.Load(NoCount);
            var r = BreakpointEditor.Apply(m, "diggers", 0, 0, payload, "", "");
            Assert.False(r.Ok);
        }

        [Fact]
        public void ClearingTheCountIsPossible()
        {
            var m = ProfileModel.Load(WithCount);
            Assert.Equal(4, m.Diggers[0].Count);
            var r = BreakpointEditor.Apply(m, "diggers", 0, 0, "3, 8", "", "");
            Assert.True(r.Ok, r.Error);
            Assert.Equal(0, m.Diggers[0].Count);
            Assert.DoesNotContain("\"Count\"", m.ToJson());
        }

        [Fact]
        public void BeardsDoNotTakeACount()
        {
            // Only diggers have an activation count; a beard list's length IS the count, bounded by
            // capBeards(). An "x4" on a beard line must therefore be read as a bad slot id, not silently
            // swallowed as a count.
            var m = ProfileModel.Load(@"{ ""Breakpoints"": { ""Beards"": [ { ""Time"": { ""h"": 0 }, ""List"": [0, 5] } ] } }");
            var r = BreakpointEditor.Apply(m, "beards", 0, 0, "0, 5 x4", "", "");
            Assert.False(r.Ok);
        }

        [Fact]
        public void OutOfRangeSlotIdsAreStillRejectedWithACount()
        {
            var m = ProfileModel.Load(NoCount);
            var r = BreakpointEditor.Apply(m, "diggers", 0, 0, "3, 99 x4", "", "");
            Assert.False(r.Ok);
            Assert.Contains("99", r.Error);
        }
    }
}
