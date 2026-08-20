using System;
using Xunit;
using Injector = NGUAdvisor.Managers.AdvisorInstance;
using Companion = NGUAdvisorCompanion.AdvisorInstance;

namespace NGUAdvisor.Tests
{
    /// <summary>
    /// The instance-suffix rules, asserted across BOTH halves at once.
    ///
    /// The advisor DLL (net48, inside the game's Mono domain) and the companion host (net8 WinForms) each
    /// carry their own AdvisorInstance, because they read the id from different places. What they cannot
    /// be allowed to disagree on is the NAMES those ids produce — a disagreement is not a crash, it is a
    /// UI that connects to nothing, or worse, to the wrong game. This file links both sources and compares
    /// them directly; it is the only mechanism that keeps them together.
    /// </summary>
    public class AdvisorInstanceTests
    {
        // Every id shape worth caring about: none, ordinary, the separator characters that MUST be
        // dropped rather than escaped, whitespace, punctuation-only, unicode, and over-long.
        //
        // Held as a plain array and wrapped for [MemberData], rather than as a TheoryData literal, so
        // the cross-product test below can enumerate the same list as STRINGS. Enumerating TheoryData
        // itself yields rows, not values.
        private static readonly string[] RawIds =
        {
            null, "", "bench", "melody-repro", "b", "UPPER_case-9",
            @"has\backslash", "has/slash", "has space", "has.dot", "has:colon",
            "Global\\evil", "...", "   ", "\t\n",
            "melodyé中", "-", "_",
        };

        public static TheoryData<string> Ids()
        {
            var data = new TheoryData<string>();
            foreach (var id in RawIds) data.Add(id);
            data.Add(new string('x', 200));
            return data;
        }

        // --- the default instance is the OLD product, byte for byte -----------------------------------
        // This is the compatibility assertion, and it is written as literals ON PURPOSE. Deriving the
        // expected value from the same constants the code uses would assert nothing: the point is that a
        // new advisor DLL still finds the 2.4.0 companion.exe already installed on a user's machine, and
        // that build\deploy.ps1's companion restart (which passes no instance) lands on the live pipes.
        [Fact]
        public void Default_instance_names_are_the_legacy_constants()
        {
            Assert.Equal("NGUAdvisorUI", Injector.SnapshotPipeFor(""));
            Assert.Equal("NGUAdvisorUICmd", Injector.CommandPipeFor(""));
            Assert.Equal("NGUAdvisorCompanionSingleton", Injector.CompanionMutexFor(""));

            Assert.Equal("NGUAdvisorUI", Companion.SnapshotPipeFor(""));
            Assert.Equal("NGUAdvisorUICmd", Companion.CommandPipeFor(""));
            Assert.Equal("NGUAdvisorCompanionSingleton", Companion.CompanionMutexFor(""));
        }

        [Fact]
        public void Null_and_empty_and_punctuation_only_ids_all_collapse_to_the_default()
        {
            foreach (var raw in new[] { null, "", "   ", "...", @"\\", "///", "\t" })
            {
                Assert.Equal("", Injector.Sanitize(raw));
                Assert.Equal("NGUAdvisorUI", Injector.SnapshotPipeFor(Injector.Sanitize(raw)));
            }
        }

        // --- the two halves agree ----------------------------------------------------------------------
        [Theory]
        [MemberData(nameof(Ids))]
        public void Sanitize_agrees_across_both_halves(string raw)
        {
            Assert.Equal(Injector.Sanitize(raw), Companion.Sanitize(raw));
        }

        [Theory]
        [MemberData(nameof(Ids))]
        public void All_three_names_agree_across_both_halves(string raw)
        {
            var id = Injector.Sanitize(raw);
            Assert.Equal(Injector.SnapshotPipeFor(id), Companion.SnapshotPipeFor(id));
            Assert.Equal(Injector.CommandPipeFor(id), Companion.CommandPipeFor(id));
            Assert.Equal(Injector.CompanionMutexFor(id), Companion.CompanionMutexFor(id));
        }

        /// <summary>
        /// The aliasing this design exists to avoid, asserted for every pair of ids in the table.
        ///
        /// PipeClient's ONE-argument constructor derives the command pipe as snapshot + "Cmd". Applied to
        /// decorated names that collides: instance "x" would command on "NGUAdvisorUI-xCmd", which is
        /// instance "xCmd"'s SNAPSHOT pipe — and NamedPipeServerStream is created with maxInstances 1, so
        /// whichever advisor binds second silently gets no UI. Decorating the two bases independently
        /// makes it impossible: every snapshot name starts "NGUAdvisorUI-" and every command name
        /// "NGUAdvisorUICmd-", which differ at the character after "NGUAdvisorUI".
        ///
        /// With no id the two schemes agree on "NGUAdvisorUICmd", which is why nothing about this is
        /// observable by running the live game — only by running two.
        /// </summary>
        [Theory]
        [MemberData(nameof(Ids))]
        public void No_command_pipe_can_alias_another_instances_snapshot_pipe(string raw)
        {
            var mine = Injector.CommandPipeFor(Injector.Sanitize(raw));
            foreach (var other in RawIds)
                Assert.NotEqual(Injector.SnapshotPipeFor(Injector.Sanitize(other)), mine);
        }

        // --- sanitising --------------------------------------------------------------------------------
        [Theory]
        [InlineData("bench", "bench")]
        [InlineData("Melody-Repro_2", "Melody-Repro_2")]
        [InlineData("has space", "hasspace")]
        [InlineData(@"has\backslash", "hasbackslash")]
        [InlineData("Global\\evil", "Globalevil")]
        [InlineData("a.b:c/d", "abcd")]
        [InlineData("mélody", "mlody")]
        public void Sanitize_keeps_only_the_safe_alphabet(string raw, string expected)
        {
            Assert.Equal(expected, Injector.Sanitize(raw));
        }

        [Fact]
        public void Sanitize_truncates_to_the_documented_cap()
        {
            var id = Injector.Sanitize(new string('x', 500));
            Assert.Equal(Injector.MaxIdChars, id.Length);
            Assert.Equal(Companion.MaxIdChars, Injector.MaxIdChars);
        }

        [Theory]
        [MemberData(nameof(Ids))]
        public void No_sanitised_id_can_reach_a_namespace_or_path_separator(string raw)
        {
            var id = Injector.Sanitize(raw);
            foreach (var name in new[] { Injector.SnapshotPipeFor(id), Injector.CommandPipeFor(id),
                                         Injector.CompanionMutexFor(id) })
            {
                Assert.DoesNotContain("\\", name, StringComparison.Ordinal);
                Assert.DoesNotContain("/", name, StringComparison.Ordinal);
                Assert.DoesNotContain(" ", name, StringComparison.Ordinal);
                // A mutex name is capped at 260 chars and a pipe name at 256; the id cap keeps us far
                // below both no matter what was in the environment variable.
                Assert.True(name.Length < 128, "name too long: " + name);
            }
        }

        // --- distinctness ------------------------------------------------------------------------------
        [Fact]
        public void A_bench_instance_shares_no_name_with_the_live_one()
        {
            var live = new[] { Injector.SnapshotPipeFor(""), Injector.CommandPipeFor(""), Injector.CompanionMutexFor("") };
            var bench = new[] { Injector.SnapshotPipeFor("bench"), Injector.CommandPipeFor("bench"), Injector.CompanionMutexFor("bench") };

            foreach (var l in live)
                foreach (var b in bench)
                    Assert.NotEqual(l, b);
        }

        [Fact]
        public void Two_different_ids_never_produce_the_same_name()
        {
            Assert.NotEqual(Injector.SnapshotPipeFor("bench"), Injector.SnapshotPipeFor("bench2"));
            Assert.NotEqual(Injector.CommandPipeFor("bench"), Injector.CommandPipeFor("bench2"));
            Assert.NotEqual(Injector.CompanionMutexFor("bench"), Injector.CompanionMutexFor("bench2"));

            // The adversarial pair for the old snapshot+"Cmd" scheme, which produced ONE name for both.
            Assert.NotEqual(Injector.CommandPipeFor("x"), Injector.SnapshotPipeFor("xCmd"));
        }
    }
}
