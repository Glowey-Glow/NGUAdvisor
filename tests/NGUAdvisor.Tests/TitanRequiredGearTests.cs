using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // GEAR A FIGHT REQUIRES FOR A MECHANIC, as opposed to gear that scores well.
    //
    // Operator-reported 2026-08-07: does the titan advisor wear item 135, the Ring of Apathy, for T4
    // UUG? It did not, and could not have — every objective in the gear pipeline is a set of STATS and
    // this item has none.
    //
    // [DECOMP] ItemNameDesc.cs:2659-2677 — itemName[135] = "Ring of Apathy", type = Accessory, and
    // curAttack / capAttack / curDefense / capDefense are all 0f with specType1/2/3 = None. Zero on
    // every axis a scorer can read.
    //
    // [DECOMP] InventoryController.apathyCheck() walks character.inventory.accs — the EQUIPPED slots —
    // for id 135 and returns its LEVEL, or -1 when not worn. EnemyAI:715-753 (and again :1516-1545):
    //
    //     < 0    invincible = true; growCount += 400; growCount *= 2      <- the fight cannot be won
    //     < 100  growCount += (100 - level) scaled by (2 - level/100)     <- winnable, still growing
    //     >= 100 the insult does nothing
    //
    // So "main priority accessory until you can AK it" is exactly right, and the AK boundary is the
    // right one: an autokilled spawn never reaches EnemyAI at all.
    //
    // The pin itself (GearOptimizer) and the routing gate (ZoneHelpers/Main) both read Main.Character
    // and cannot link into this headless project. What links is the table that says WHICH item.
    public class TitanRequiredGearTests
    {
        [Fact]
        public void T4_requires_the_apathy_ring_and_nothing_else_does()
        {
            // Index 3 is UUG: TitanZones[3] == 14, and [DECOMP] AdventureController.cs:1798 maps
            // zone 14 -> "UUG THE UNMENTIONABLE", spawned as enemyType.bigBoss4 (:2074).
            Assert.Equal(135, TitanTables.RequiredAccessoryFor(3));
            Assert.Equal(TitanTables.ApathyRingId, TitanTables.RequiredAccessoryFor(3));

            for (int i = 0; i < TitanTables.RequiredAccessory.Length; i++)
            {
                if (i == 3) continue;
                Assert.Equal(0, TitanTables.RequiredAccessoryFor(i));
            }
        }

        [Fact]
        public void The_table_covers_every_titan()
        {
            // One row per titan, so a second mechanic item never needs a code change — and so an index
            // that exists in Abbrev cannot fall off the end of this table.
            Assert.Equal(TitanTables.Abbrev.Length, TitanTables.RequiredAccessory.Length);
            Assert.Equal("UUG", TitanTables.Abbrev[3]);
        }

        [Fact]
        public void Out_of_range_indexes_require_nothing_rather_than_throwing()
        {
            // This is read on the routing path every tick; an exception there would take the whole
            // adventure step down.
            Assert.Equal(0, TitanTables.RequiredAccessoryFor(-1));
            Assert.Equal(0, TitanTables.RequiredAccessoryFor(-999));
            Assert.Equal(0, TitanTables.RequiredAccessoryFor(TitanTables.RequiredAccessory.Length));
            Assert.Equal(0, TitanTables.RequiredAccessoryFor(int.MaxValue));
        }

        [Fact]
        public void The_full_suppression_level_is_the_games_own_number()
        {
            // EnemyAI compares apathyCheck() against 100 twice (`< 100` then `>= 100`). Below it the
            // ring works but UUG still grows, which is a WARNING, not a gate — the gate is `< 0`.
            Assert.Equal(100, TitanTables.ApathyFullLevel);
        }

        [Fact]
        public void Requiring_an_item_is_not_the_same_question_as_autokill()
        {
            // The trap this whole change exists to avoid. Autokill reads itemList.itemMaxxed[135] —
            // "levelled to max" — and ZoneHelpers.AutokillAvailable case 3 already checks it. The
            // mechanic reads inventory.accs — "worn right now". A player can satisfy the first and
            // fail the second; that state is exactly what the advisor used to walk into.
            //
            // The AK stat table for T4 is the other half of that gate and must stay as measured.
            var t4 = TitanTables.Ak[3][0];
            Assert.Equal(8e5, t4[0]);      // attack
            Assert.Equal(4e5, t4[1]);      // defense
            Assert.Equal(1.4e4, t4[2]);    // HP regen — a REAL gate from T4 up
        }
    }
}
