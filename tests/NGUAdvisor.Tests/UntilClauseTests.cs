using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // "Focus on X until done" — the vocabulary that lets a profile terminate on an OUTCOME instead of
    // a stopwatch. Worth pinning hard, because the failure mode is not an exception: a clause that can
    // never be met is a step that silently never advances, and a run that quietly stops progressing is
    // exactly the class of defect this whole audit started from.
    public class UntilClauseTests
    {
        private static UntilCondition Parse(string s)
        {
            UntilCondition c; string err;
            Assert.True(UntilCondition.TryParse(s, out c, out err), s + " -> " + err);
            return c;
        }

        private static UntilFacts Facts(double run = 0, double gold = 0, double atk = 0,
                                        double def = 0, double energy = 0, double magic = 0, double versions = 0)
            => new UntilFacts { RunSeconds = run, Gold = gold, Attack = atk, Defence = def,
                                Energy = energy, Magic = magic, TitanVersions = versions };

        [Fact]
        public void A_goal_is_met_when_the_number_arrives()
        {
            var c = Parse("gold >= 2.4T");
            UntilClause met;
            Assert.False(c.IsMet(Facts(gold: 2.3e12), out met));
            Assert.True(c.IsMet(Facts(gold: 2.4e12), out met));
            Assert.Equal(UntilSubject.Gold, met.Subject);
        }

        // The deadline half. "bank 2.4T, or give up after 45 minutes" is ONE intent — the time is an
        // escape hatch, not a second goal — which is why clauses are OR and the first met one wins.
        [Fact]
        public void The_escape_hatch_fires_when_the_goal_does_not()
        {
            var c = Parse("gold >= 2.4T or run >= 45m");
            UntilClause met;

            Assert.False(c.IsMet(Facts(gold: 1e12, run: 600), out met));
            Assert.True(c.IsMet(Facts(gold: 1e12, run: 2700), out met));
            Assert.Equal(UntilSubject.Run, met.Subject);   // and it says WHICH one ended the step
        }

        [Fact]
        public void The_goal_wins_when_both_are_met_because_it_is_named_first()
        {
            var c = Parse("gold >= 1B or run >= 1m");
            UntilClause met;
            Assert.True(c.IsMet(Facts(gold: 5e9, run: 3600), out met));
            Assert.Equal(UntilSubject.Gold, met.Subject);
        }

        [Theory]
        [InlineData("run >= 90s", 90)]
        [InlineData("run >= 45m", 2700)]
        [InlineData("run >= 24h", 86400)]
        [InlineData("run >= 1.5h", 5400)]
        public void Durations_read_the_way_a_person_writes_a_deadline(string text, double expected)
            => Assert.Equal(expected, Parse(text).Clauses[0].Value);

        [Theory]
        [InlineData("gold >= 500K", 5e5)]
        [InlineData("gold >= 2.4T", 2.4e12)]
        [InlineData("gold >= 12B", 1.2e10)]
        [InlineData("energy >= 3.5M", 3.5e6)]
        public void Magnitudes_read_the_way_this_game_writes_every_number(string text, double expected)
            => Assert.Equal(expected, Parse(text).Clauses[0].Value);

        [Fact]
        public void A_bare_number_is_still_a_number()
            => Assert.Equal(1234, Parse("gold >= 1234").Clauses[0].Value);

        [Theory]
        [InlineData("attack >= 1e15", UntilSubject.Attack)]
        [InlineData("atk >= 1e15", UntilSubject.Attack)]
        [InlineData("power >= 1e15", UntilSubject.Attack)]
        [InlineData("defence >= 1e15", UntilSubject.Defence)]
        [InlineData("defense >= 1e15", UntilSubject.Defence)]   // both spellings, deliberately
        [InlineData("def >= 1e15", UntilSubject.Defence)]
        [InlineData("versions >= 2", UntilSubject.TitanVersions)]
        public void Subjects_accept_the_names_people_actually_type(string text, UntilSubject expected)
            => Assert.Equal(expected, Parse(text).Clauses[0].Subject);

        [Fact]
        public void A_falling_target_is_expressible_too()
        {
            var c = Parse("gold <= 1B");
            UntilClause met;
            Assert.False(c.IsMet(Facts(gold: 2e9), out met));
            Assert.True(c.IsMet(Facts(gold: 5e8), out met));
        }

        // Rejections carry a REASON. These run over files the operator hand-edits, and a profile that
        // fails to load without saying why is worse than one that refuses a clause and names it.
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("gold")]                 // no operator
        [InlineData("gold > 1B")]            // > is not >=; no silent coercion
        [InlineData("wealth >= 1B")]         // unknown subject
        [InlineData("gold >= banana")]       // unreadable number
        [InlineData("gold >= -5")]           // negative target
        public void Malformed_clauses_are_refused_with_an_explanation(string text)
        {
            UntilCondition c; string err;
            Assert.False(UntilCondition.TryParse(text, out c, out err));
            Assert.False(string.IsNullOrEmpty(err), "refused \"" + text + "\" with no reason");
            Assert.Null(c);
        }

        [Fact]
        public void One_bad_clause_refuses_the_whole_condition_rather_than_half_applying_it()
        {
            UntilCondition c; string err;
            Assert.False(UntilCondition.TryParse("gold >= 1B or wealth >= 2B", out c, out err));
            Assert.Contains("wealth", err);
        }

        // The UI has to be able to say what a step is waiting for without the operator reading JSON.
        [Fact]
        public void It_reads_back_in_the_operators_words()
        {
            Assert.Equal("until run time reaches 45m", Parse("run >= 45m").Describe());
            Assert.Contains("gold reaches", Parse("gold >= 2.4T").Describe());
            Assert.Contains(", or ", Parse("gold >= 2.4T or run >= 45m").Describe());
            Assert.Equal("until titan versions beaten reaches 2", Parse("versions >= 2").Describe());
        }

        // A condition with nothing in it must read as "never ends", not as "already done" — the two
        // are opposite failures and only one of them is recoverable by waiting.
        [Fact]
        public void An_empty_condition_never_fires_and_says_so()
        {
            var c = new UntilCondition();
            UntilClause met;
            Assert.False(c.IsMet(Facts(run: 1e9, gold: 1e30), out met));
            Assert.Null(met);
            Assert.Contains("never", c.Describe());
        }

        [Fact]
        public void Whitespace_and_case_do_not_change_the_meaning()
        {
            var a = Parse("gold>=2.4T");
            var b = Parse("  GOLD   >=   2.4t  ");
            Assert.Equal(a.Clauses[0].Subject, b.Clauses[0].Subject);
            Assert.Equal(a.Clauses[0].Value, b.Clauses[0].Value);
        }

        // ---- the hold semantics, as the timeline applies them --------------------------------------
        // A breakpoint with an unmet condition keeps the timeline where it is even though a later
        // breakpoint's time has arrived. These pin the two directions that matter, because both
        // failures are silent: holding forever looks like a slow run, and never holding looks like the
        // feature is simply off.

        [Fact]
        public void A_met_condition_releases_the_step()
        {
            var c = Parse("gold >= 1B");
            UntilClause met;
            Assert.True(c.IsMet(Facts(gold: 1e9), out met));   // -> the timeline may advance
        }

        [Fact]
        public void An_unmet_condition_holds_the_step_no_matter_how_late_it_is()
        {
            var c = Parse("gold >= 1B");
            UntilClause met;
            // Run time is enormous; the ONLY thing that ends this step is the gold clause.
            Assert.False(c.IsMet(Facts(gold: 9e8, run: 86400 * 7), out met));
        }

        // The escape hatch is what makes an unreachable goal survivable. Without it, a condition that
        // can never be met is a run that never advances again.
        [Fact]
        public void An_unreachable_goal_still_ends_if_it_was_given_a_deadline()
        {
            var reachable = Parse("gold >= 1e30 or run >= 1h");
            UntilClause met;
            Assert.True(reachable.IsMet(Facts(gold: 0, run: 3600), out met));
            Assert.Equal(UntilSubject.Run, met.Subject);

            var trap = Parse("gold >= 1e30");
            Assert.False(trap.IsMet(Facts(gold: 0, run: 86400 * 30), out met));   // held for a month
        }

        // Titan progress reads the BESTIARY, not the difficulty selector — the selector is what the
        // advisor is chasing, and a condition about progress must read progress. This pins the
        // vocabulary; UntilFactsProvider is what supplies the number.
        [Fact]
        public void Titan_versions_is_a_progress_subject()
        {
            var c = Parse("versions >= 2");
            UntilClause met;
            Assert.False(c.IsMet(Facts(versions: 1), out met));
            Assert.True(c.IsMet(Facts(versions: 2), out met));
        }

        [Fact]
        public void Three_clauses_are_as_valid_as_two()
        {
            var c = Parse("gold >= 9T or attack >= 9e20 or run >= 2h");
            Assert.Equal(3, c.Clauses.Count);
            UntilClause met;
            Assert.True(c.IsMet(Facts(run: 7200), out met));
            Assert.Equal(UntilSubject.Run, met.Subject);
        }
    }
}
