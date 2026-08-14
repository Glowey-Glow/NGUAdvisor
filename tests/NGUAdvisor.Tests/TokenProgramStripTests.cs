using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE TOKEN-PROGRAM STRIP (amendment 11 §4.4 REPLACE · amendment 28 · audit 31 §Q4a).
    //
    // The three segment programs on the REPLACE path — AUGMENTATION, NGU+AT, EVIL NGU — used to
    // decorate their tokens with CAP and :percent. Those decorations only ever steered the prioCount
    // share model, and amendment 28 deleted that model: every seated destination is now offered
    // min(capacity, remaining / destinations-not-yet-offered), so CAP buys no escape from a divisor
    // that no longer exists and a percent-of-CUR bound that the pool exceeds never binds.
    //
    // WHAT THESE TESTS PIN is the claim the strip rests on: removing CAP and :percent changes the
    // LANE SET and the ORDER not at all. Membership and order are the load-bearing half — they decide
    // which lanes seat and in what sequence — so a strip that quietly renamed a lane, dropped one, or
    // reordered them would be a behaviour change wearing a deletion's clothes.
    //
    // ChallengeOverlay.AutoTokens itself reaches Main.Character and cannot be linked here, so the
    // before/after sequences are transcribed from it (ChallengeOverlay.cs, `case "AUGMENTATION"` /
    // `case "NGU+AT"` / `case "EVIL NGU"`) with the two dynamic parts — the value-ranked hot NGU set
    // and the surplus set — instantiated at concrete ids. PriorityCatalog is the grammar's own parser
    // and IS linkable, so lane identity is decided by the same reading the profile editor uses.
    public class TokenProgramStripTests
    {
        // The mechanical strip: drop a leading CAP, drop a trailing :percent. Nothing else.
        private static string Strip(string token)
        {
            var s = token;
            var colon = s.IndexOf(':');
            if (colon >= 0) s = s.Substring(0, colon);
            if (s.StartsWith("CAP")) s = s.Substring(3);
            return s;
        }

        private sealed class Program
        {
            public string Name;
            public string[] Before;
            public string[] After;
        }

        // Energy, AUGMENTATION: CAPALLBT, CAPBESTAUG, then the hot NGU lanes (already plain).
        private static readonly Program AugmentationEnergy = new Program
        {
            Name = "AUGMENTATION (energy)",
            Before = new[] { "CAPALLBT", "CAPBESTAUG", "NGU-0", "NGU-4" },
            After = new[] { "ALLBT", "BESTAUG", "NGU-0", "NGU-4" },
        };

        // Magic, AUGMENTATION: no energy-only tokens; the hot lanes, then the tail's gated BR-30.
        private static readonly Program AugmentationMagic = new Program
        {
            Name = "AUGMENTATION (magic)",
            Before = new[] { "NGU-0", "NGU-3", "BR-30" },
            After = new[] { "NGU-0", "NGU-3", "BR-30" },
        };

        // Energy, NGU+AT: TM, Wandoos, the BT caps, the five AT slots, hot NGUs, then surplus NGUs.
        // (No ritual on the energy pool — BR is magic-only.)
        private static readonly Program NguAtEnergy = new Program
        {
            Name = "NGU+AT (energy)",
            Before = new[] { "CAPTM:5", "CAPWAN:40", "CAPALLBT", "CAPALLAT:15", "NGU-0", "NGU-4", "CAPNGU-6", "CAPNGU-8" },
            After = new[] { "TM", "WAN", "ALLBT", "ALLAT", "NGU-0", "NGU-4", "NGU-6", "NGU-8" },
        };

        // Magic, NGU+AT: TM, Wandoos, hot NGUs, the ritual, then surplus NGUs.
        private static readonly Program NguAtMagic = new Program
        {
            Name = "NGU+AT (magic)",
            Before = new[] { "CAPTM:5", "CAPWAN:40", "NGU-0", "NGU-3", "CAPBR-300:10", "CAPNGU-4" },
            After = new[] { "TM", "WAN", "NGU-0", "NGU-3", "BR-300", "NGU-4" },
        };

        // Energy, EVIL NGU: as NGU+AT without the AT slots (AT had phase 3).
        private static readonly Program EvilNguEnergy = new Program
        {
            Name = "EVIL NGU (energy)",
            Before = new[] { "CAPTM:5", "CAPWAN:40", "CAPALLBT", "NGU-2", "NGU-5", "CAPNGU-7" },
            After = new[] { "TM", "WAN", "ALLBT", "NGU-2", "NGU-5", "NGU-7" },
        };

        private static readonly Program EvilNguMagic = new Program
        {
            Name = "EVIL NGU (magic)",
            Before = new[] { "CAPTM:5", "CAPWAN:40", "NGU-0", "NGU-3", "CAPBR-300:10", "CAPNGU-6" },
            After = new[] { "TM", "WAN", "NGU-0", "NGU-3", "BR-300", "NGU-6" },
        };

        public static IEnumerable<object[]> Programs => new[]
        {
            new object[] { AugmentationEnergy },
            new object[] { AugmentationMagic },
            new object[] { NguAtEnergy },
            new object[] { NguAtMagic },
            new object[] { EvilNguEnergy },
            new object[] { EvilNguMagic },
        };

        // THE STRIP CLAIM, lane by lane and position by position: same count, same base type, same
        // index, same order. This is what "membership and order are kept" means operationally.
        [Theory]
        [MemberData(nameof(Programs))]
        public void StrippedProgram_YieldsTheSameLaneSetInTheSameOrder(object programObj)
        {
            var p = (Program)programObj;
            Assert.Equal(p.Before.Length, p.After.Length);

            for (int i = 0; i < p.Before.Length; i++)
            {
                var before = PriorityCatalog.Parse(p.Before[i]);
                var after = PriorityCatalog.Parse(p.After[i]);

                Assert.True(before.Recognized, $"{p.Name}[{i}]: '{p.Before[i]}' is not a known token");
                Assert.True(after.Recognized, $"{p.Name}[{i}]: '{p.After[i]}' is not a known token");
                Assert.Equal(before.Base, after.Base);
                Assert.Equal(before.Index, after.Index);
            }
        }

        // And the strip is MECHANICAL — CAP off the front, :percent off the back, nothing renamed and
        // nothing re-indexed. A hand-edited token that changed a base or an index would pass the lane
        // test above only if it changed both lists the same way; this one catches that.
        [Theory]
        [MemberData(nameof(Programs))]
        public void StrippedProgram_IsExactlyTheTokensMinusCapAndPercent(object programObj)
        {
            var p = (Program)programObj;
            Assert.Equal(p.Before.Select(Strip).ToArray(), p.After);
        }

        // Nothing on the REPLACE path still carries a share decoration.
        [Theory]
        [MemberData(nameof(Programs))]
        public void StrippedProgram_CarriesNoCapAndNoPercent(object programObj)
        {
            var p = (Program)programObj;
            foreach (var token in p.After)
            {
                var t = PriorityCatalog.Parse(token);
                Assert.False(t.Cap, $"{p.Name}: '{token}' still carries CAP");
                Assert.Null(t.Percent);
            }
        }

        // The BEFORE lists really did carry them — otherwise the tests above pass vacuously and the
        // transcription has drifted from the code they claim to describe.
        [Fact]
        public void TheProgramsBeingStripped_DidCarryShareDecorations()
        {
            var decorated = Programs
                .Select(row => (Program)row[0])
                .SelectMany(p => p.Before)
                .Where(t => PriorityCatalog.Parse(t).Cap || PriorityCatalog.Parse(t).Percent.HasValue)
                .Distinct()
                .OrderBy(t => t)
                .ToArray();

            Assert.Equal(
                new[] { "CAPALLAT:15", "CAPALLBT", "CAPBESTAUG", "CAPBR-300:10", "CAPNGU-4", "CAPNGU-6",
                        "CAPNGU-7", "CAPNGU-8", "CAPTM:5", "CAPWAN:40" },
                decorated);
        }

        // THE RITUAL TOKEN IS THE ONE THAT KEEPS AN INDEX. BR's Index is secondsToRun — a MEMBERSHIP
        // filter deciding which rituals qualify (RitualMath.RitualDecide), not a share — so the strip
        // keeps `-300` and drops only CAP and :10. Dropping the index would silently change which
        // rituals run.
        [Fact]
        public void SegmentRitual_KeepsTheSecondsFilterAndLosesOnlyTheShareParts()
        {
            var before = PriorityCatalog.Parse("CAPBR-300:10");
            var after = PriorityCatalog.Parse("BR-300");

            Assert.Equal("BR", before.Base);
            Assert.Equal("BR", after.Base);
            Assert.Equal(300, before.Index.Value);
            Assert.Equal(300, after.Index.Value);
            Assert.True(before.Cap);
            Assert.False(after.Cap);
            Assert.Equal(10, before.Percent.Value);
            Assert.Null(after.Percent);
        }

        // AND IT MUST NOT COLLIDE WITH THE TAIL'S OWN TOKEN. AutoTokens ends with
        // `if (rituals && !list.Contains("BR-30") && !list.Contains(MarathonRitual)
        //     && !list.Contains(SegmentRitual)) list.Add("BR-30")`
        // — an EXACT string test. "BR-300" and "BR-30" are different strings AND different lanes
        // (300s vs 30s), so the guard has to know the new spelling; if it did not, a stripped segment
        // would look ritual-less and get a SECOND blood lane appended.
        [Fact]
        public void SegmentRitual_IsADistinctTokenFromTheTailGuardsBR30()
        {
            Assert.Equal(30, PriorityCatalog.Parse("BR-30").Index.Value);
            Assert.Equal(300, PriorityCatalog.Parse("BR-300").Index.Value);
            Assert.NotEqual(PriorityCatalog.Parse("BR-30").Index.Value,
                            PriorityCatalog.Parse("BR-300").Index.Value);
        }

        // The surplus lanes lose their CAP prefix and stay the same NGU ids. They are disjoint from
        // the hot set by construction (NguValueMath.Surplus filters `!targets.Contains`), which is why
        // dropping the prefix cannot make a surplus token collide with — or be de-duplicated away by —
        // the `if (!list.Contains(t))` guard the programs use.
        [Fact]
        public void SurplusLanes_LoseOnlyTheCapPrefix()
        {
            for (int id = 0; id <= 8; id++)
            {
                var before = PriorityCatalog.Parse($"CAPNGU-{id}");
                var after = PriorityCatalog.Parse($"NGU-{id}");
                Assert.Equal(before.Base, after.Base);
                Assert.Equal(before.Index, after.Index);
                Assert.True(before.Cap);
                Assert.False(after.Cap);
            }
        }

        [Fact]
        public void SurplusIds_AreDisjointFromTheHotSet_SoStrippingCannotCollide()
        {
            var list = new List<NguValueMath.Entry>
            {
                new NguValueMath.Entry { Id = 0, Rating = 5.0 },
                new NguValueMath.Entry { Id = 3, Rating = 2.0 },
                new NguValueMath.Entry { Id = 6, Rating = 1.5 },
            };

            var targets = new[] { 0, 3 };
            var surplus = NguValueMath.Surplus(list, targets);

            Assert.Empty(surplus.Intersect(targets));
            Assert.Equal(new[] { 6 }, surplus);
        }

        // THE GRAMMAR IS UNCHANGED — C2. The strip removed decorations from what the AUTO PROFILE
        // emits; it did not narrow what a profile may SAY. Every decorated spelling a shipped or
        // hand-written profile can contain still parses to the same lane it always did.
        [Theory]
        [InlineData("CAPTM:5", "TM", 0)]
        [InlineData("CAPTM:30", "TM", 0)]
        [InlineData("CAPWAN:40", "WAN", 0)]
        [InlineData("CAPWAN:60", "WAN", 0)]
        [InlineData("CAPALLBT", "ALLBT", 0)]
        [InlineData("CAPALLAT", "ALLAT", 0)]
        [InlineData("CAPALLAT:10", "ALLAT", 0)]
        [InlineData("CAPBESTAUG:10", "BESTAUG", 0)]
        [InlineData("CAPNGU-5", "NGU", 5)]
        [InlineData("CAPBR-300:10", "BR", 300)]
        public void DecoratedTokensStillParse_TheGrammarWasNotNarrowed(string token, string expectedBase, int expectedIndex)
        {
            var t = PriorityCatalog.Parse(token);
            Assert.True(t.Recognized);
            Assert.Equal(expectedBase, t.Base);
            Assert.Equal(expectedIndex, t.Index ?? 0);
        }
    }
}
