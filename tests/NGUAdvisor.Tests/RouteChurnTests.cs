using System;
using System.Collections.Generic;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE HYSTERESIS INSTRUMENT — audit/41 §6.
    //
    // 41 §6 records an oscillation as a RISK, not an observation: "SetTarget and Rare are re-evaluated
    // every 10 minutes with NO MEMORY, and PPP's 2.1h cadence sits close to the 3h admission bar ...
    // If the zone oscillates between targets rather than settling, the fix is hysteresis, not another
    // priority tweak." Nobody has measured a margin, so nothing here fits one. What is under test is
    // the measurement: the elapsed field that separates settling from oscillating, the run-length
    // counter that makes a wall of lines readable, and the assertion that the instrument cannot move
    // a target.
    public class RouteChurnTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

        private static RouteChurn.Route Set(int zone, string name, double cap, double cadence,
            double runnerUp = double.NaN, string runnerUpName = null) =>
            RouteChurn.Of("SET", zone, name, "set completion outranks set-less accessories",
                score: cap, scoreLabel: "cap", cadence: cadence, bar: 3.0, barOnCadence: true,
                runnerUp: runnerUp, runnerUpName: runnerUpName);

        private static RouteChurn.Route Rare(int zone, string name, double perDrop,
            double runnerUp = double.NaN, string runnerUpName = null) =>
            RouteChurn.Of("RARE", zone, name, "drops arrive regularly at the current drop chance",
                score: perDrop, scoreLabel: "drop", cadence: perDrop, bar: 3.0, barOnCadence: true,
                runnerUp: runnerUp, runnerUpName: runnerUpName);

        // ---- C1: THE ELAPSED FIELD ---------------------------------------------------------------

        [Fact]
        public void The_first_route_is_not_a_change_and_carries_no_elapsed_time()
        {
            var s = new RouteChurn.State();
            var rep = RouteChurn.Observe(s, Set(24, "Power Pyramid", 137.1, 2.1), T0);

            Assert.True(rep.Changed);
            Assert.True(rep.First);
            // ⚠ NOT ZERO-AS-A-MEASUREMENT. Statics wipe on payload reload, so the alternative is an
            // invented elapsed time on the first line of every session — and the elapsed field is the
            // one thing this instrument exists to produce.
            Assert.Equal(TimeSpan.Zero, rep.HeldFor);
            Assert.Contains("first route since load", RouteChurn.Format(rep));
        }

        [Fact]
        public void An_unchanged_route_reports_no_change_and_emits_nothing()
        {
            var s = new RouteChurn.State();
            RouteChurn.Observe(s, Set(24, "Power Pyramid", 137.1, 2.1), T0);

            // Same track, same zone, a later pass, a moved score. Not a routing event.
            var rep = RouteChurn.Observe(s, Set(24, "Power Pyramid", 96.0, 1.8), T0.AddMinutes(10));

            Assert.False(rep.Changed);
            Assert.Null(RouteChurn.Format(rep));
        }

        [Fact]
        public void Elapsed_time_since_the_last_change_is_the_held_duration_of_the_route_replaced()
        {
            var s = new RouteChurn.State();
            RouteChurn.Observe(s, Set(24, "Power Pyramid", 137.1, 2.1), T0);
            var rep = RouteChurn.Observe(s, Rare(31, "Sky Scraper", 1.3), T0.AddMinutes(20));

            Assert.True(rep.Changed);
            Assert.False(rep.First);
            Assert.Equal(TimeSpan.FromMinutes(20), rep.HeldFor);
            Assert.Equal("SET", rep.Previous.Track);
            Assert.Equal(24, rep.Previous.Zone);
            Assert.Contains("after 20m", RouteChurn.Format(rep));
        }

        [Fact]
        public void A_settled_run_and_an_oscillating_run_differ_only_in_the_elapsed_field()
        {
            // Identical route SEQUENCE, different clocks. Nothing but the timestamps separates them,
            // which is precisely why the elapsed field is the instrument and the route names are not.
            var settled = new RouteChurn.State();
            RouteChurn.Observe(settled, Set(24, "Power Pyramid", 137.1, 2.1), T0);
            var settledRep = RouteChurn.Observe(settled, Rare(31, "Sky Scraper", 1.3), T0.AddHours(9));

            var churning = new RouteChurn.State();
            RouteChurn.Observe(churning, Set(24, "Power Pyramid", 137.1, 2.1), T0);
            var churnRep = RouteChurn.Observe(churning, Rare(31, "Sky Scraper", 1.3), T0.AddMinutes(10));

            Assert.Equal(TimeSpan.FromHours(9), settledRep.HeldFor);
            // 10 minutes is exactly one ApplyZones pass — the route did not survive a single
            // re-evaluation.
            Assert.Equal(TimeSpan.FromMinutes(10), churnRep.HeldFor);
        }

        // ---- C1: BOTH REASONS --------------------------------------------------------------------

        [Fact]
        public void Both_routes_reasons_survive_into_the_line()
        {
            var s = new RouteChurn.State();
            RouteChurn.Observe(s, Set(24, "Power Pyramid", 137.1, 2.1), T0);
            var line = RouteChurn.Format(RouteChurn.Observe(s, Rare(31, "Sky Scraper", 1.3), T0.AddMinutes(20)));

            Assert.Contains("set completion outranks set-less accessories", line);
            Assert.Contains("drops arrive regularly at the current drop chance", line);
            Assert.Contains("left SET Power Pyramid(24)", line);
            Assert.Contains("took RARE Sky Scraper(31)", line);
            // TRACK THEN ZONE ON BOTH SIDES of the header arrow. The track is what the reader scans
            // for; an ordering that flips halfway across the line makes an A->B->A pattern harder to
            // see, which is the one pattern this log exists to make obvious.
            Assert.Contains("[RouteChurn] SET Power Pyramid(24) -> RARE Sky Scraper(31)", line);
        }

        // ---- C3: THE RUN-LENGTH COUNTER ----------------------------------------------------------

        [Fact]
        public void The_run_length_counter_counts_changes_not_adoptions()
        {
            var s = new RouteChurn.State();
            // Adoption 1 is not a change.
            var first = RouteChurn.Observe(s, Set(24, "A", 137.1, 2.1), T0);
            Assert.Equal(0, first.RunCount);

            var c1 = RouteChurn.Observe(s, Rare(31, "B", 1.3), T0.AddMinutes(10));
            Assert.Equal(1, c1.RunCount);
            Assert.Contains("first change since load", RouteChurn.Format(c1));
        }

        [Fact]
        public void Three_changes_in_forty_minutes_reports_as_three_changes_in_forty_minutes()
        {
            var s = new RouteChurn.State();
            RouteChurn.Observe(s, Set(24, "Power Pyramid", 137.1, 2.1), T0);           // adopt
            RouteChurn.Observe(s, Rare(31, "Sky Scraper", 1.3), T0.AddMinutes(10));    // change 1
            RouteChurn.Observe(s, Set(24, "Power Pyramid", 137.1, 2.1), T0.AddMinutes(30));  // change 2
            var rep = RouteChurn.Observe(s, Rare(31, "Sky Scraper", 1.3), T0.AddMinutes(50)); // change 3

            Assert.Equal(3, rep.RunCount);
            // Span runs from the OLDEST RETAINED CHANGE (t+10) to now (t+50).
            Assert.Equal(TimeSpan.FromMinutes(40), rep.RunSpan);
            Assert.Contains("3 changes in 40m", RouteChurn.Format(rep));
        }

        [Fact]
        public void The_same_count_over_a_long_span_reads_as_settled()
        {
            // ⚠ THE COUNT ALONE IS NOT THE SIGNAL, which is why the span always travels with it. A
            // count without its span is the kind of number that gets misread as a threshold.
            var s = new RouteChurn.State();
            RouteChurn.Observe(s, Set(24, "A", 137.1, 2.1), T0);
            RouteChurn.Observe(s, Rare(31, "B", 1.3), T0.AddHours(3));
            RouteChurn.Observe(s, Set(24, "A", 137.1, 2.1), T0.AddHours(9));
            var rep = RouteChurn.Observe(s, Rare(31, "B", 1.3), T0.AddHours(14));

            Assert.Equal(3, rep.RunCount);
            Assert.Equal(TimeSpan.FromHours(11), rep.RunSpan);
            Assert.Contains("3 changes in 11h", RouteChurn.Format(rep));
        }

        [Fact]
        public void The_run_length_is_bounded_by_the_ring_and_never_exceeds_it()
        {
            var s = new RouteChurn.State();
            RouteChurn.Route rep0 = Set(24, "A", 137.1, 2.1);
            RouteChurn.Observe(s, rep0, T0);

            RouteChurn.Report last = default(RouteChurn.Report);
            for (int i = 1; i <= 40; i++)
                last = RouteChurn.Observe(s,
                    i % 2 == 1 ? Rare(31, "B", 1.3) : Set(24, "A", 137.1, 2.1),
                    T0.AddMinutes(10 * i));

            Assert.Equal(40, s.Changes);                       // the true total is still exact
            Assert.Equal(RouteChurn.HistoryDepth, last.RunCount);
            // A MEMORY BOUND, not a window: the span is whatever the retained changes happen to
            // cover, and it is emitted so a reader divides for themselves.
            Assert.Equal(TimeSpan.FromMinutes(10 * (RouteChurn.HistoryDepth - 1)), last.RunSpan);
        }

        // ---- OSCILLATION vs PROGRESS -------------------------------------------------------------

        [Fact]
        public void Returning_to_a_route_just_left_is_flagged_as_a_revisit()
        {
            var s = new RouteChurn.State();
            RouteChurn.Observe(s, Set(24, "Power Pyramid", 137.1, 2.1), T0);
            RouteChurn.Observe(s, Rare(31, "Sky Scraper", 1.3), T0.AddMinutes(10));
            var rep = RouteChurn.Observe(s, Set(24, "Power Pyramid", 137.1, 2.1), T0.AddMinutes(20));

            Assert.True(rep.Revisit);
            Assert.Equal(TimeSpan.FromMinutes(10), rep.RevisitLeftAgo);   // left at t+10, now t+20
            Assert.Equal(TimeSpan.FromMinutes(10), rep.RevisitHeldFor);   // held T0 -> t+10
            Assert.Contains("REVISIT", RouteChurn.Format(rep));
        }

        [Fact]
        public void Moving_on_to_a_route_never_held_is_not_a_revisit()
        {
            var s = new RouteChurn.State();
            RouteChurn.Observe(s, Set(24, "A", 137.1, 2.1), T0);
            RouteChurn.Observe(s, Set(22, "B", 210.0, 2.4), T0.AddHours(4));
            var rep = RouteChurn.Observe(s, Rare(31, "C", 1.3), T0.AddHours(9));

            Assert.False(rep.Revisit);
            Assert.DoesNotContain("REVISIT", RouteChurn.Format(rep));
        }

        [Fact]
        public void A_track_change_that_keeps_the_zone_number_is_still_a_change()
        {
            // "IDLE on zone N becomes FARM on zone N the moment one-hit is reached, and the zone
            // number does not move" — AdvisorApply's farmSig note. That transition costs a ChangeGear
            // and a digger re-level exactly like any other, so the signature is track+zone.
            var s = new RouteChurn.State();
            RouteChurn.Observe(s, RouteChurn.Of("IDLE", 24, "Power Pyramid", "accessory outstanding"), T0);
            var rep = RouteChurn.Observe(s,
                RouteChurn.Of("FARM", 24, "Power Pyramid", "one-hit met, set not capped",
                    score: 2.4, scoreLabel: "cap", cadence: 1.1, bar: 3.0),
                T0.AddMinutes(10));

            Assert.True(rep.Changed);
            Assert.Equal("IDLE", rep.Previous.Track);
            Assert.Equal("FARM", rep.Current.Track);
        }

        // ---- C2: THE MARGIN ----------------------------------------------------------------------

        [Fact]
        public void The_runner_up_margin_is_measured_at_one_instant()
        {
            var s = new RouteChurn.State();
            RouteChurn.Observe(s, Set(24, "Power Pyramid", 137.1, 2.1), T0);
            var line = RouteChurn.Format(RouteChurn.Observe(s,
                Rare(31, "Sky Scraper", 1.3, runnerUp: 2.4, runnerUpName: "Bad Fruit"),
                T0.AddMinutes(20)));

            Assert.Contains("margin vs runner-up:", line);
            Assert.Contains("Bad Fruit", line);
            // 2.4 - 1.3 = 1.1h, i.e. 45.8% of the runner-up's 2.4h.
            Assert.Contains("won by 1.1h (45.8%)", line);
        }

        [Fact]
        public void The_previous_margin_is_reported_with_its_staleness()
        {
            var s = new RouteChurn.State();
            RouteChurn.Observe(s, Set(24, "A", 137.1, 2.1), T0);
            var line = RouteChurn.Format(RouteChurn.Observe(s, Set(22, "B", 96.0, 2.4), T0.AddMinutes(20)));

            Assert.Contains("margin vs previous: cap 137.1h -> 96h", line);
            // The previous score was measured when THAT route was adopted, not now. Saying so is the
            // difference between a margin and a coincidence.
            Assert.Contains("measured 20m earlier", line);
        }

        [Fact]
        public void Unlike_metrics_are_refused_rather_than_subtracted()
        {
            // ⚠ THE 41 §3 DEFECT, INSIDE THE INSTRUMENT. Hours-to-cap minus hours-per-drop is a number
            // with no meaning; printing one would re-create the two-bars problem in the very tool
            // built to detect its consequences. The shared quantity — cadence — is reported instead.
            var s = new RouteChurn.State();
            RouteChurn.Observe(s, Set(24, "Power Pyramid", 137.1, 2.1), T0);
            var line = RouteChurn.Format(RouteChurn.Observe(s, Rare(31, "Sky Scraper", 1.3), T0.AddMinutes(20)));

            Assert.Contains("rank metrics differ (cap vs drop) — not comparable", line);
            Assert.Contains("cadence 2.1h -> 1.3h", line);
            Assert.DoesNotContain("cap 137.1h -> ", line);
        }

        [Fact]
        public void A_switch_to_a_worse_scoring_route_says_LOST_rather_than_hiding_it()
        {
            // Legal and expected — SET outranks RARE categorically (41 §3), so a tier change can move
            // routing to a worse-scoring target. It is also the single most interesting line here: a
            // hysteresis band would have held the old route through exactly this.
            var s = new RouteChurn.State();
            RouteChurn.Observe(s, Set(24, "A", 96.0, 2.1), T0);
            var line = RouteChurn.Format(RouteChurn.Observe(s, Set(22, "B", 137.1, 2.4), T0.AddMinutes(10)));

            Assert.Contains("LOST by", line);
        }

        [Fact]
        public void The_distance_to_the_admission_bar_is_reported_because_that_is_the_named_mechanism()
        {
            // 41 §6: "PPP's 2.1h cadence sits close to the 3h admission bar." A bar crossing is the
            // leading candidate cause of any oscillation this instrument finds, so the slack is a
            // column rather than something the reader subtracts on every line.
            var s = new RouteChurn.State();
            var line = RouteChurn.Format(RouteChurn.Observe(s, Set(24, "Power Pyramid", 137.1, 2.1), T0));

            Assert.Contains("bar: cadence <= 3h", line);
            Assert.Contains("clears by 54m (30%)", line);
        }

        [Fact]
        public void A_rate_ranked_track_is_never_printed_as_hours_nor_subtracted_from_one()
        {
            // BOOST ranks in boost-value/kill, higher-wins. Rendering that through the hours
            // formatter would print "2.4h" for a quantity that is not a time.
            var s = new RouteChurn.State();
            RouteChurn.Observe(s,
                RouteChurn.Of("BOOST", 43, "Z", "2.4 boost-value/kill",
                    score: 2.4, scoreLabel: "boost-value/kill", scoreInHours: false, higherWins: true),
                T0);
            var line = RouteChurn.Format(RouteChurn.Observe(s,
                RouteChurn.Of("BOOST", 44, "Y", "3 boost-value/kill",
                    score: 3.0, scoreLabel: "boost-value/kill", scoreInHours: false, higherWins: true),
                T0.AddHours(2)));

            Assert.Contains("boost-value/kill 2.4 -> 3", line);
            Assert.DoesNotContain("2.4h", line);
            Assert.Contains("won by 0.6 (25%)", line);   // higher wins: 3 - 2.4
        }

        [Fact]
        public void An_unreachable_cadence_reports_never_rather_than_a_number()
        {
            var s = new RouteChurn.State();
            var line = RouteChurn.Format(RouteChurn.Observe(s,
                Set(24, "A", double.PositiveInfinity, double.PositiveInfinity), T0));

            Assert.Contains("never", line);
            Assert.DoesNotContain("∞", line);
        }

        // ---- C4: THE INSTRUMENT MUST NOT CHANGE ROUTING -------------------------------------------

        // A stand-in for ApplyZones' decision: pure, and given no access to the churn state. The point
        // of the harness is that it is the SAME oracle in both runs — if Observe ever acquired a way
        // to influence a decision, it would have to do it through shared mutable state, and this is
        // what would catch that.
        private static RouteChurn.Route Decide(int pass)
        {
            // Deliberately a flapper: SET and RARE trade places every pass, which is the exact shape
            // 41 §6 warns about and the worst case for an instrument that might interfere.
            return pass % 2 == 0
                ? Set(24, "Power Pyramid", 137.1, 2.1, runnerUp: 210.0, runnerUpName: "Chocolate World")
                : Rare(31, "Sky Scraper", 1.3, runnerUp: 2.4, runnerUpName: "Bad Fruit");
        }

        [Fact]
        public void The_same_inputs_produce_the_same_target_with_the_instrument_on_and_off()
        {
            var withInstrument = new List<string>();
            var without = new List<string>();

            var s = new RouteChurn.State();
            for (int pass = 0; pass < 24; pass++)
            {
                var r = Decide(pass);
                RouteChurn.Format(RouteChurn.Observe(s, r, T0.AddMinutes(10 * pass)));  // instrument ON
                withInstrument.Add(r.Sig);
            }

            for (int pass = 0; pass < 24; pass++)
                without.Add(Decide(pass).Sig);                                          // instrument OFF

            Assert.Equal(without, withInstrument);
        }

        [Fact]
        public void Observe_does_not_mutate_the_decision_it_is_handed()
        {
            var s = new RouteChurn.State();
            var r = Set(24, "Power Pyramid", 137.1, 2.1, runnerUp: 210.0, runnerUpName: "Chocolate World");
            var rep = RouteChurn.Observe(s, r, T0);

            // Route is a struct passed by value, so this is a structural guarantee rather than a
            // convention — the assertion is here so a future change to a class or a `ref` fails loudly.
            Assert.Equal("SET#24", r.Sig);
            Assert.Equal(24, r.Zone);
            Assert.Equal(137.1, r.Score);
            Assert.Equal("SET#24", rep.Current.Sig);
        }

        [Fact]
        public void A_null_state_reports_no_change_and_cannot_throw()
        {
            // The live caller wraps this in a try/catch, but an instrument that can throw into a
            // routing path is a routing change by another name.
            var rep = RouteChurn.Observe(null, Set(24, "A", 137.1, 2.1), T0);

            Assert.False(rep.Changed);
            Assert.Null(RouteChurn.Format(rep));
        }
    }
}
