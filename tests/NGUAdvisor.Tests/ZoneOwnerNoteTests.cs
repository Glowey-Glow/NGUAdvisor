using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // audit/40 §3 item 2 — "ApplyZones guards on only two of the six contenders", still live per §6.4.
    //
    // ZoneRoutingTests pins LAYER 2: what Main.SnipeZone says at the frame it displaces the intent
    // field. These pin LAYER 1: what AdvisorApply says at the moment it WRITES a target and quotes an
    // ETA against it. Both halves are needed because the two lines answer different questions and
    // neither can be inferred from the other — the resolver's latch is three ints, so a track change
    // that keeps the zone number re-announces at layer 1 and is correctly silent at layer 2.
    //
    // WHO WINS IS NOT UNDER TEST AND IS NOT CHANGED. audit/40 §2's order is deliberate on every row;
    // OwnerNote is a suffix on a line that was already going to be written.
    //
    // Collection: this class asserts on ZoneRouting.Last, which is process-wide. See TestCollections.cs.
    [Collection(TestCollections.ZoneRoutingState)]
    public class ZoneOwnerNoteTests
    {
        private static string Name(int zone) => zone >= 1000 ? "ITOPOD" : "Zone " + zone;

        // §7's CORRECTED membership of the four contenders ApplyZones does not stand down for. §3
        // item 2 listed R3 and omitted R4; R3 cannot fire without MoneyPitRunMode (ShockwaveTier()
        // returns double? and null <= 1e18 is false), which :988 already covers, and R4 the empty
        // Time Machine is gated on no advisor toggle at all.
        [Theory]
        [InlineData(ZoneRouting.Cause.TimeMachineEmpty)]   // R4 — the row §3 item 2 omitted
        [InlineData(ZoneRouting.Cause.GoldSnipe)]          // R5
        [InlineData(ZoneRouting.Cause.Titan)]              // R6
        [InlineData(ZoneRouting.Cause.Quest)]              // R7
        public void The_four_uncovered_contenders_qualify_the_line_that_announces_a_target(
            ZoneRouting.Cause c)
        {
            var note = ZoneRouting.OwnerNote(c, 20, Name(20));

            Assert.NotEqual("", note);
            Assert.Contains(ZoneRouting.Owner(c), note);       // WHAT TOOK THE ROW
            Assert.Contains("Zone 20", note);                  // THE DISCARDED TARGET
            Assert.Contains("not being adventured", note);
        }

        // The two ApplyZones DOES stand down for still qualify a line, for the same reason
        // ZoneRoutingTests keeps them: the stand-down stops the ADVISOR writing, it does not stop a
        // gear hunt (which is above the stand-down at :993, outside the throttle) from announcing.
        [Theory]
        [InlineData(ZoneRouting.Cause.PitRun)]        // R3 — via MoneyPitRunMode
        [InlineData(ZoneRouting.Cause.CBlockGold)]    // R8 — via GoldCBlockMode
        public void The_two_covered_contenders_still_qualify_a_line(ZoneRouting.Cause c)
        {
            Assert.NotEqual("", ZoneRouting.OwnerNote(c, 20, Name(20)));
        }

        // R12/R13 rewrite an ITOPOD result rather than pre-empting the chain, but they hold for as
        // long as their condition does and an announced farm zone is not adventured while they do.
        // Membership comes from IsOwner, never from a second list — that is the whole point of
        // asking ZoneRouting instead of re-spelling the six rows here.
        [Fact]
        public void Every_owner_row_qualifies_a_line_and_no_other_row_does()
        {
            foreach (ZoneRouting.Cause c in System.Enum.GetValues(typeof(ZoneRouting.Cause)))
            {
                var note = ZoneRouting.OwnerNote(c, 20, Name(20));
                if (ZoneRouting.IsOwner(c))
                {
                    Assert.NotEqual("", note);
                    Assert.Contains(ZoneRouting.Owner(c), note);
                }
                else
                {
                    // None, UnlockFallback and the four hand-backs. A hand-back means normal routing
                    // continues, i.e. the announced zone IS being adventured; UnlockFallback is
                    // audit/40 §3 item 4's rewrite and Describe already owns its sentence.
                    Assert.Equal("", note);
                }
            }
        }

        // ---- audit/40 §6.1's two silences ----------------------------------------------------
        //
        // These are the SAME bounds the drop-farm row of R10 is written with (`0 <= SnipeZone <
        // 1000`). Both must stay silent, and both would speak if the bound were dropped — which is
        // exactly what the reverted-fix check below asserts.

        [Fact]
        public void An_unset_target_is_not_a_contention()
        {
            // -1 is the SavedSettings sentinel (SavedSettings.cs:13, `_snipeZone = -1`). Nothing was
            // written, so an owner holding the row displaced nothing.
            Assert.Equal("", ZoneRouting.OwnerNote(ZoneRouting.Cause.Quest, -1, null));
            Assert.Equal("", ZoneRouting.OwnerNote(ZoneRouting.Cause.Titan, -1, "Zone -1"));
        }

        [Fact]
        public void The_ITOPOD_is_not_a_contention()
        {
            // audit/40 §0: "ITOPOD is zone 1000, not a separate system". The advisor names 1000 only
            // as its fallback venue — the boost farm with no boost demand (AdvisorApply, `target =
            // 1000`) and the ITOPOD phase — never with an ETA to invalidate, and it is where routing
            // lands by default anyway.
            Assert.Equal("", ZoneRouting.OwnerNote(ZoneRouting.Cause.Quest, 1000, "ITOPOD"));
            Assert.Equal("", ZoneRouting.OwnerNote(ZoneRouting.Cause.Titan, 1000, "ITOPOD"));

            // Zone 999 is a real zone and is NOT the sentinel-or-ITOPOD case, so the boundary is
            // pinned on the speaking side too: a `> 1000` bound would silence nothing, a `>= 999`
            // bound would silence a real farm target.
            Assert.NotEqual("", ZoneRouting.OwnerNote(ZoneRouting.Cause.Quest, 999, "Zone 999"));
            Assert.NotEqual("", ZoneRouting.OwnerNote(ZoneRouting.Cause.Quest, 0, "Safe Zone"));
        }

        // ⚠ THE REVERTED-FIX CHECK, stated as a test rather than as a claim in a commit message.
        // A "green result that means NOT MEASURED" is this project's most repeated failure. The two
        // silences above are the only assertions in this file that a naive implementation would pass
        // for free, so the guard they depend on is spelled out here: with `if (target < 0 || target
        // >= 1000) return "";` removed, OwnerNote reduces to the owner branch alone — and that branch
        // demonstrably speaks for both -1 and 1000, because it speaks for every other input with the
        // same cause.
        [Fact]
        public void The_silences_are_a_guard_and_not_an_accident_of_the_cause()
        {
            const ZoneRouting.Cause c = ZoneRouting.Cause.Quest;

            // Same cause, same name-shape, only the zone number differs. The cause cannot be what
            // makes -1 and 1000 quiet, so the bound is.
            Assert.NotEqual("", ZoneRouting.OwnerNote(c, 20, "Zone 20"));
            Assert.Equal("", ZoneRouting.OwnerNote(c, -1, "Zone -1"));
            Assert.Equal("", ZoneRouting.OwnerNote(c, 1000, "Zone 1000"));

            // And a name is never what decides it either: a target with no resolvable name still
            // speaks, so "silent" can only ever mean the bound or a non-owner.
            Assert.NotEqual("", ZoneRouting.OwnerNote(c, 20, null));
            Assert.Contains("this zone", ZoneRouting.OwnerNote(c, 20, null));
        }

        // ---- the note is a suffix, and it must read as one -----------------------------------

        [Fact]
        public void The_note_appends_to_an_existing_line_rather_than_replacing_it()
        {
            // The live shape: AdvisorApply builds the line, then concatenates. The note must open
            // with its own separator or it runs into the ETA it is qualifying.
            var line = "rare farm -> Chocolate World (a drop every ~4h, 46 merges left = ~295h)"
                     + ZoneRouting.OwnerNote(ZoneRouting.Cause.Quest, 20, "Chocolate World");

            Assert.StartsWith("rare farm -> Chocolate World", line);
            Assert.Contains("~295h)", line);
            Assert.Contains(" — a quest owns adventure routing right now", line);
            Assert.EndsWith("Chocolate World is not being adventured while it holds", line);
        }

        // It composes with ItopodOverrideNote's suffix, which sits between the line and this one on
        // the set / rare / FARM lines. Two separate overrides, two separate clauses, one line.
        [Fact]
        public void The_note_composes_with_the_Target_ITOPOD_note()
        {
            var line = "set farm -> Zone 20 (2 set item(s) left)"
                     + " — overriding Target ITOPOD, which is still on"
                     + ZoneRouting.OwnerNote(ZoneRouting.Cause.Titan, 20, "Zone 20");

            Assert.Contains("overriding Target ITOPOD", line);
            Assert.Contains("a spawning titan owns adventure routing right now", line);
        }

        // ⚠ ROUTING IS UNCHANGED, AND THIS IS THE TYPE-LEVEL PROOF. OwnerNote takes three values and
        // returns a string. It cannot reach Settings.SnipeZone, cannot reach the resolver's latch,
        // and is not read back by anything that decides a zone — the same shape RouteChurn's C4 uses
        // ("it measures, it never decides"). Calling it in every combination leaves the latch, which
        // IS the resolver's state, byte-identical.
        //
        // ⚠ AND THAT REASONING IS TRUE ABOUT OwnerNote WHILE BEING TOO WEAK FOR THIS TEST, WHICH IS
        // WHY THE [Collection] ON THIS CLASS IS LOAD-BEARING AND NOT DECORATION. The paragraph above
        // defends OwnerNote's purity. The assertions below claim something strictly stronger: that
        // NOTHING IN THE PROCESS moved the latch between the Reset() on entry and the read on exit.
        // OwnerNote's purity cannot buy that, because the latch is process-wide and another test
        // class can write it midway. The Reset() calls at both ends show the author knew the state
        // was shared — but a reset at the start does not help against a concurrent writer, and a
        // reset at the end only protects whoever runs next.
        //
        // This failed exactly that way: ZoneRoutingTests drove the latch to Cause.Quest in parallel
        // and :180 read Quest instead of Titan. The collection is what closes it. DO NOT WEAKEN THE
        // ASSERTIONS TO MAKE THIS GREEN — they caught a real isolation defect, which is their job;
        // if this fails again, something is running that is not in the collection.
        [Fact]
        public void Producing_the_note_does_not_touch_the_resolver_state()
        {
            ZoneRouting.Reset();
            Assert.True(ZoneRouting.ShouldSurface(ZoneRouting.Cause.Titan, 20, 178, out _, out _));
            ZoneRouting.Spoke(true);
            Assert.Equal(ZoneRouting.Cause.Titan, ZoneRouting.Last);

            foreach (ZoneRouting.Cause c in System.Enum.GetValues(typeof(ZoneRouting.Cause)))
                foreach (var z in new[] { -1, 0, 20, 999, 1000, 1001 })
                    ZoneRouting.OwnerNote(c, z, Name(z));

            // Unchanged cause, and — the part that matters — the latch still suppresses the frame it
            // was suppressing before, so no line was gained or lost by asking the question.
            Assert.Equal(ZoneRouting.Cause.Titan, ZoneRouting.Last);
            Assert.False(ZoneRouting.ShouldSurface(ZoneRouting.Cause.Titan, 20, 178, out _, out _));

            ZoneRouting.Reset();
        }

        // The two layers must name the same gate the same way, or the operator reads one hold as
        // two events. Owner() is the shared subject precisely so this cannot drift.
        [Theory]
        [InlineData(ZoneRouting.Cause.TimeMachineEmpty)]
        [InlineData(ZoneRouting.Cause.GoldSnipe)]
        [InlineData(ZoneRouting.Cause.Titan)]
        [InlineData(ZoneRouting.Cause.Quest)]
        [InlineData(ZoneRouting.Cause.PitRun)]
        [InlineData(ZoneRouting.Cause.CBlockGold)]
        public void Both_layers_name_the_gate_identically(ZoneRouting.Cause c)
        {
            var layer2 = ZoneRouting.Describe(ZoneRouting.Cause.None, true, c,
                                              20, Name(20), 178, Name(178));
            var layer1 = ZoneRouting.OwnerNote(c, 20, Name(20));

            Assert.Contains(ZoneRouting.Owner(c), layer2);
            Assert.Contains(ZoneRouting.Owner(c), layer1);
            // Same verb phrase too — "owns adventure routing" is the layer-2 wording for R3-R8.
            Assert.Contains("owns adventure routing", layer1);
        }
    }
}
