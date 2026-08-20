using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // Guards for the community guide's "Items to Keep" table (GuideGear) and its horizon gating.
    // The table IS the product: a wrong id silently protects the wrong item, and an over-eager
    // horizon lets a guide-listed item land in TRASH while the guide still wants it held. The
    // spot-checked ids below were each resolved against the decomp's ItemNameDesc.cs and verified
    // against the item's actual specType columns (see GuideGear's class comment) — they pin the
    // resolution work, most importantly the traps: Lemmiwinks is item 195 ("A Small Gerbil"), and
    // Tentacle of the Exile is the id-330 accessory, NOT the spec-less id-337 quest item.
    public class GuideGearTests
    {
        [Fact]
        public void Table_has_no_duplicate_ids()
        {
            var dupes = GuideGear.Entries.GroupBy(e => e.Id).Where(g => g.Count() > 1)
                .Select(g => g.Key).ToArray();
            Assert.True(dupes.Length == 0, "duplicate guide ids: " + string.Join(", ", dupes.Select(x => x.ToString()).ToArray()));
        }

        [Fact]
        public void Every_entry_is_well_formed()
        {
            foreach (var e in GuideGear.Entries)
            {
                // Equipment ids live in (0, 600) — the game's item table bound.
                Assert.InRange(e.Id, 1, 599);
                // The guide's keep lists exist for chapters 2..8 only (ch.1 gear is all replaceable).
                Assert.InRange(e.FoundCh, 2, 8);
                // A horizon before the chapter that names the item would be self-contradictory.
                Assert.InRange(e.KeepUntil, e.FoundCh, 9);
                Assert.False(string.IsNullOrEmpty(e.Reason), $"id {e.Id} has an empty reason");
            }
        }

        [Theory]
        // Resolution traps pinned by hand against the decomp (id, expected KeepUntil).
        [InlineData(195, 7)]   // "Lemmiwinks" = A Small Gerbil, keep until Sadistic
        [InlineData(330, 8)]   // Tentacle of the Exile = the ACCESSORY (337 is the quest item)
        [InlineData(171, 9)]   // Green Heart = "My Green Heart <3", keep forever
        [InlineData(297, 6)]   // Grey Heart = "My Grey Heart <3", forced through ch.6, optimizer after
        [InlineData(149, 9)]   // UUG's 'Special' Ring, keep forever
        [InlineData(193, 9)]   // "Apple" = A Giant Apple (412 is a Sadistic weapon), keep forever
        [InlineData(91, 3)]    // The Sands of Time, keep to next chapter
        [InlineData(138, 4)]   // Ring of Utility, through end of Normal
        [InlineData(190, 6)]   // A Shrunken Voodoo Doll, until late ch.6
        public void Spot_checked_ids_resolve_with_their_horizons(int id, int keepUntil)
        {
            Assert.True(GuideGear.TryGet(id, out var e), $"id {id} missing from the guide table");
            Assert.Equal(keepUntil, e.KeepUntil);
        }

        [Fact]
        public void Ids_that_must_not_be_in_the_table()
        {
            // The wrong halves of the two known name collisions: picking these up again would mean the
            // table regressed to name matching.
            Assert.False(GuideGear.TryGet(337, out _), "337 is the spec-less Tentacle QUEST item");
            Assert.False(GuideGear.TryGet(412, out _), "412 (An Ordinary Apple) is a Sadistic weapon, not the ch.4 keep");
        }

        [Fact]
        public void Horizon_gating_holds_through_the_last_chapter_and_lapses_after()
        {
            var e = new GuideGear.Entry { Id = 1, FoundCh = 3, KeepUntil = 5, Reason = "x" };
            Assert.True(GuideGear.KeepActive(e, 3));
            Assert.True(GuideGear.KeepActive(e, 5));   // "until late ch.5" rounds UP to all of ch.5
            Assert.False(GuideGear.KeepActive(e, 6));
            Assert.False(GuideGear.KeepActive(e, 8));
        }

        [Fact]
        public void Unknown_chapter_keeps_everything()
        {
            // Chapter 0 = ProgressionAnalyzer not ready. An un-detected chapter must never be the
            // reason an item lands in TRASH, so every hold stays active.
            foreach (var e in GuideGear.Entries)
                Assert.True(GuideGear.KeepActive(e, 0));
        }

        [Fact]
        public void Forever_entries_survive_every_chapter()
        {
            foreach (var e in GuideGear.Entries.Where(x => x.KeepUntil == 9))
                for (int ch = 1; ch <= 8; ch++)
                    Assert.True(GuideGear.KeepActive(e, ch), $"forever id {e.Id} lapsed at ch.{ch}");
        }

        [Fact]
        public void Each_chapter_from_2_contributes_entries()
        {
            // Chapters 2-8 each publish an "Items to Keep" list; an empty chapter here means a
            // transcription slice went missing, not that the guide went quiet.
            var byCh = new HashSet<int>(GuideGear.Entries.Select(e => e.FoundCh));
            for (int ch = 2; ch <= 8; ch++)
                Assert.Contains(ch, byCh);
        }
    }
}
