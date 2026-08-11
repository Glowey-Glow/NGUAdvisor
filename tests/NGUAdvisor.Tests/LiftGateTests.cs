using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // LIFT GATES — the field that says WHEN a stop stops being a stop.
    //
    // ⚠ THE MECHANISM HAS NO LIVE ROW, AND IT IS STILL TESTED HERE ON PURPOSE. It was added at
    // c3f8122 for Respawn 401, which [OPERATOR] then removed outright — 401 was "the 'best' for
    // where the user is at at the time", not a property of the curve, and the allocator already
    // computes best use of energy. So every row in ObjectiveTable is currently ungated.
    //
    // The mechanism stays because it is the schema's second gate-shaped field alongside
    // CampaignScope, and because an untested mechanism is how the next conditional row gets filed
    // as a terminality value instead. These tests drive it with SYNTHETIC rows so it cannot rot
    // between the row that motivated it and the row that next needs it.
    //
    // ⚠ TERMINALITY AND GATING ARE DIFFERENT AXES — that is the durable finding, and it survived
    // the removal. Terminality describes the CURVE: what does the next level buy — nothing
    // (terminal), almost nothing (the `diminishing` kind amendment 35 §3 specifies and this enum
    // still lacks), or normally (precondition). A gate describes PROGRESSION STATE. Amendment 35 §3
    // recorded the cost of mixing them: Block AT was filed PRECONDITION by one document and
    // TERMINAL by another because "both were reaching for a kind that did not exist".
    public class LiftGateTests
    {
        private static readonly string[] BeastV4Lifted = { TargetPass.GateBeastV4 };
        private static readonly string[] NoGatesLifted = new string[0];

        // A synthetic gated row. Deliberately NOT read out of ObjectiveTable — there is no gated
        // row there now, and pinning the mechanism to whichever row happens to be gated is what
        // made these tests break when the row left.
        private static TargetPass.TargetRow GatedRow(string gate = TargetPass.GateBeastV4) =>
            new TargetPass.TargetRow
            {
                System = TargetPass.SysNguEnergy,
                Index = 2,
                Track = TargetPass.Track.Normal,
                Kind = TargetPass.RowKind.Level,
                Terminality = TargetPass.Terminality.Terminal,
                ValueLow = 401,
                ValueHigh = 401,
                LiftGate = gate,
                Objective = "synthetic — exercises the gate, not a live row",
                Cite = "LiftGateTests",
            };

        private static TargetPass.TargetRow UngatedRow()
        {
            var row = GatedRow();
            row.LiftGate = null;
            return row;
        }

        // ---- the gate's two states ---------------------------------------------------------------

        [Fact]
        public void Before_the_lift_a_gated_row_is_still_a_stop()
        {
            var route = TargetPass.Route(GatedRow(), NoGatesLifted);

            Assert.Equal(TargetPass.Disposition.WriteTarget, route.Disposition);
            Assert.Equal(401L, route.TargetToWrite);
        }

        [Fact]
        public void After_the_lift_a_gated_row_no_longer_speaks()
        {
            var route = TargetPass.Route(GatedRow(), BeastV4Lifted);

            Assert.NotEqual(TargetPass.Disposition.WriteTarget, route.Disposition);
            Assert.Equal(0L, route.TargetToWrite);
            Assert.Contains("lift-gate", route.Reason, StringComparison.OrdinalIgnoreCase);
        }

        // ---- fail closed, twice over --------------------------------------------------------------

        [Fact]
        public void An_unrecognised_gate_is_refused_not_written()
        {
            var route = TargetPass.Route(GatedRow("no-such-gate-in-this-build"), BeastV4Lifted);

            Assert.Equal(TargetPass.Disposition.Refused, route.Disposition);
            Assert.Equal(0L, route.TargetToWrite);
            Assert.Contains("unrecognised", route.Reason, StringComparison.OrdinalIgnoreCase);
        }

        // ⚠ THE ONE THAT MATTERS MOST. Silence about the gate is not permission to treat the row as
        // unconditional — "I did not look" and "none are satisfied" are different answers.
        [Fact]
        public void A_gate_unaware_caller_cannot_write_a_gated_row()
        {
            var route = TargetPass.Route(GatedRow());   // no gate state at all

            Assert.Equal(TargetPass.Disposition.Refused, route.Disposition);
            Assert.Equal(0L, route.TargetToWrite);
            Assert.Contains("gate-UNAWARE", route.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void An_ungated_row_routes_identically_with_and_without_gate_state()
        {
            var blind = TargetPass.Route(UngatedRow());
            var aware = TargetPass.Route(UngatedRow(), BeastV4Lifted);

            Assert.Equal(TargetPass.Disposition.WriteTarget, blind.Disposition);
            Assert.Equal(blind.Disposition, aware.Disposition);
            Assert.Equal(blind.TargetToWrite, aware.TargetToWrite);
        }

        [Fact]
        public void A_lifted_gate_refuses_it_does_not_write_the_unset_sentinel()
        {
            var route = TargetPass.Route(GatedRow(), BeastV4Lifted);

            Assert.Equal(0L, route.TargetToWrite);
            Assert.NotEqual(TargetPass.Disposition.WriteTarget, route.Disposition);
        }

        // ---- the live table carries no gate, and that is asserted, not assumed --------------------

        // If a gated row is ever added, this fails and whoever added it must come here and say which
        // gate and why. ⚠ Amendment 35 §3's Block AT is explicitly NOT a candidate: its problem is
        // the curve axis (asymptotic — every level still buys something), so it needs `diminishing`,
        // not a gate. It is TERMINAL today by operator ruling, which is a third thing again.
        [Fact]
        public void No_row_in_the_live_table_carries_a_lift_gate()
        {
            var gated = ObjectiveTable.LaneRows.Where(r => r.LiftGate != null).ToList();

            Assert.True(gated.Count == 0,
                "a gated row appeared — say which gate and why, and give it its own test:\n  " +
                string.Join("\n  ", gated.Select(r => r.System + " " + r.Objective + " gate=" + r.LiftGate)));
        }

        // The gate registry still has exactly the one name, so an unrecognised-gate test is testing
        // something real rather than passing because every name is unknown.
        [Fact]
        public void The_known_gate_registry_still_recognises_beast_v4_and_not_junk()
        {
            Assert.True(TargetPass.IsKnownLiftGate(TargetPass.GateBeastV4));
            Assert.False(TargetPass.IsKnownLiftGate("no-such-gate-in-this-build"));
            Assert.False(TargetPass.IsKnownLiftGate(null));
        }
    }
}
