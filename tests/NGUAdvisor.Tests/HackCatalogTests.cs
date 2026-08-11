using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // The hack index->name table and the R3 priority grammar have to agree, because they describe the same
    // thing to two different audiences: the grammar decides which HACK-n tokens parse, the table decides what
    // the editor and the logs call slot n. They are declared in separate files, so nothing but a test stops
    // them drifting — and the failure mode is silent (an unnameable token, or a name for a slot you cannot
    // address).
    //
    // The bound is 14, not 15. The game's Hacks.hacksSize() is 16, but index 15 ("THE END") takes no R3, is
    // only reachable once every other hack is hard-capped, and HackBP rejects it. Listing it here would
    // advertise a slot the allocator refuses to fund.
    public class HackCatalogTests
    {
        [Fact]
        public void The_table_covers_hacks_zero_through_fourteen_exactly_once()
        {
            var ids = SystemCatalog.Hacks.Select(kv => kv.Key).ToArray();
            Assert.Equal(Enumerable.Range(0, 15).ToArray(), ids);
        }

        [Fact]
        public void Every_hack_has_a_distinct_nonempty_name()
        {
            var names = SystemCatalog.Hacks.Select(kv => kv.Value).ToList();
            Assert.DoesNotContain(names, string.IsNullOrWhiteSpace);
            Assert.Equal(names.Count, new HashSet<string>(names).Count);
        }

        // If the grammar ever widened to HACK-15 the table would silently stop covering it, and a profile
        // could address a slot with no name. Pin them together rather than to the literal 14.
        [Fact]
        public void The_grammar_addresses_exactly_the_hacks_the_table_names()
        {
            var hack = PriorityCatalog.Find(ResourceKind.R3, "HACK");
            Assert.NotNull(hack);
            Assert.True(hack.HasIndex);
            Assert.Equal(SystemCatalog.Hacks.Count - 1, hack.IndexMax);
        }

        [Fact]
        public void NameOf_resolves_a_known_hack_and_falls_back_for_an_unknown_one()
        {
            Assert.Equal("Hack Hack", SystemCatalog.NameOf(SystemCatalog.Hacks, 13));
            Assert.Equal("15", SystemCatalog.NameOf(SystemCatalog.Hacks, 15));
        }
    }
}
