using System;
using System.Globalization;
using System.Text;

namespace NGUAdvisor.Managers
{
    // THE HYSTERESIS INSTRUMENT — audit/41 §6, the "hysteresis risk" open item.
    //
    // ⚠ THIS IS NOT HYSTERESIS AND MUST NOT BECOME IT. 41 §6 records the risk, not an observation:
    //
    //     "SetTarget and Rare are re-evaluated every 10 minutes with NO MEMORY, and PPP's 2.1h cadence
    //      sits close to the 3h admission bar. Every routing change costs a ChangeGear — which zeroes
    //      energy/magic/R3 allocation until the next pass (AdvisorApply.cs:1044) — plus a digger
    //      re-level. If the zone oscillates between targets rather than settling, the fix is
    //      hysteresis, not another priority tweak."
    //
    // Nobody has yet seen an oscillation. Fitting a margin now would mean choosing a constant against
    // no data, which is the whole subject of audit/35 — a dozen constants fitted to nothing. So this
    // file MEASURES the quantity a margin would be fitted TO, and changes no routing whatsoever. The
    // "changes no routing" property is structural, not a promise: Observe is handed a value copy of a
    // decision that has already been made, it returns a report, and it has no way to reach
    // Settings.SnipeZone. Nothing here is ever read by a routing decision — if it ever is, this
    // header is the thing that was violated.
    //
    // WHAT DISTINGUISHES SETTLING FROM OSCILLATING. Not the number of changes — a run that climbs the
    // gear ladder changes route often and correctly. It is:
    //
    //   1. ELAPSED TIME SINCE THE LAST CHANGE. A route held for six hours and then replaced is
    //      progress. A route held for ten minutes — one pass of the ApplyZones throttle — is churn.
    //      This is the field the whole instrument exists for.
    //   2. REVISITS. Returning to a route that was left minutes ago is oscillation by definition;
    //      moving on to a route never held before is not. The ring below is what makes this visible.
    //   3. THE MARGIN. "By how much did it win?" A switch won by 40% needs no margin; one won by 2%
    //      is what a hysteresis band would have absorbed. Reported twice, because the two comparisons
    //      answer different questions — see Report.
    //
    // NO STATE-CHANGE THROTTLE. ConstraintParity and GearFarmPause both suppress until a signature
    // moves, because there the signature IS the event and the metric between changes is noise. Here
    // the CHANGE is the event, so throttling it would delete the entire signal. What keeps the output
    // from being a wall is the run-length counter in the header: "3 changes in 40m" is readable at a
    // glance in a way that three unrelated blocks are not.
    //
    // Unity-free, in the ZoneGate / GearFarmPause / ConstraintParity shape: pure functions here, the
    // State object owned by the caller (AdvisorApply), so the decision is testable headlessly and the
    // live writer is the only thing that touches the game.
    public static class RouteChurn
    {
        // A MEMORY BOUND, NOT A THRESHOLD. Nothing branches on this number: it caps the ring so a
        // week-long session cannot grow the record, and it bounds how far back a REVISIT can be seen.
        // The emitted line always carries the SPAN the retained changes cover, so a reader divides
        // count by span themselves rather than trusting a window somebody picked. Deliberately not a
        // tunable — a tunable here would be the first fitted constant of exactly the kind 41 §6 is
        // warning against.
        public const int HistoryDepth = 8;

        // One routing decision, as a value. Every field is something the deciding track already
        // computed — nothing here is derived, estimated, or defaulted into a number.
        public struct Route
        {
            public string Track;        // FARM / SET / RARE / IDLE / ITOPOD / BOOST / HUNT
            public int Zone;
            public string ZoneName;
            public string Reason;       // why this track picked this zone, in the track's own words

            // THE VALUE THE TRACK RANKED ON, and what that value IS. Not comparable across tracks —
            // SET ranks on hours-to-cap, RARE on hours-per-drop, BOOST on boost-value/kill — which is
            // exactly why the label travels with the number instead of the number travelling alone.
            // The two-bars defect of 41 §3 was two tracks answering different questions and the
            // difference being invisible; an unlabelled score would re-create it in the instrument.
            public double Score;
            public string ScoreLabel;   // null when the track has no continuous ranking metric

            // ⚠ THE UNIT TRAVELS WITH THE NUMBER. Most tracks rank in hours; BOOST ranks in
            // boost-value/kill. Rendering a rate through the hours formatter would print "2.4h" for a
            // quantity that is not a time, and a reader sizing a margin off that would be sizing it
            // off a fiction. There is no default that is right for both, so both are declared.
            public bool ScoreInHours;
            public bool HigherWins;     // false for hours (lower is better), true for rates

            // HOURS TO THE NEXT DROP. The one quantity that IS comparable across FARM/SET/RARE —
            // 41 §3's fix was to measure both tracks by this same cadence bar. NaN when the track has
            // no cadence (BOOST, HUNT, the two phase routes).
            public double Cadence;

            // The admission bar the track was admitted BY, and which of the two quantities it tests.
            // 41 §6 names the specific mechanism — "PPP's 2.1h cadence sits close to the 3h admission
            // bar" — so a bar crossing is the leading candidate cause of any oscillation found here,
            // and the distance to it is the number a margin would be sized from. NaN = no bar.
            public double Bar;
            public bool BarOnCadence;   // true: the bar tests Cadence. false: it tests Score.

            // The SAME ranking's second place, scored in the same pass as the winner. This is the
            // honest answer to "by how much did it win?", because both numbers are from the same
            // instant; the previous route's score is not (see Report.PrevAgeSeconds). Empty name =
            // the track has no runner-up plumbed, or nothing came second.
            public double RunnerUp;
            public string RunnerUpName;

            public string Sig => (Track ?? "?") + "#" + Zone.ToString(CultureInfo.InvariantCulture);

            public string Where => (ZoneName ?? "?") + "(" + Zone.ToString(CultureInfo.InvariantCulture) + ")";
        }

        // Optional arguments so a call site that has no cadence, no bar and no runner-up stays one
        // line. Deliberately NOT overloads: a track that acquires a cadence later should add the
        // argument, not pick a different method and quietly lose the column.
        public static Route Of(string track, int zone, string zoneName, string reason,
            double score = double.NaN, string scoreLabel = null, bool scoreInHours = true,
            bool higherWins = false, double cadence = double.NaN, double bar = double.NaN,
            bool barOnCadence = false, double runnerUp = double.NaN, string runnerUpName = null)
        {
            return new Route
            {
                Track = track,
                Zone = zone,
                ZoneName = zoneName,
                Reason = reason,
                Score = score,
                ScoreLabel = scoreLabel,
                ScoreInHours = scoreInHours,
                HigherWins = higherWins,
                Cadence = cadence,
                Bar = bar,
                BarOnCadence = barOnCadence,
                RunnerUp = runnerUp,
                RunnerUpName = runnerUpName,
            };
        }

        // The caller's state. A class, not a struct, so `Observe(state, ...)` cannot silently operate
        // on a copy and lose the history — a struct here would make every emitted run-length "1".
        public sealed class State
        {
            public bool Have;                 // a route is in force
            public Route Current;
            public DateTime CurrentSince;     // when Current was adopted
            public DateTime ScoredAt;         // when Current.Score was measured (= CurrentSince)

            public long Changes;              // total route CHANGES since load, monotone

            // The ring: the last HistoryDepth ADOPTIONS, oldest-first once wrapped. Adoption i was
            // left at adoption i+1's timestamp, which is how "left 20m ago" is computed without
            // storing departures separately.
            internal readonly string[] Sigs = new string[HistoryDepth];
            internal readonly DateTime[] At = new DateTime[HistoryDepth];
            internal int Head;                // next write index
            internal int Filled;              // entries in use, <= HistoryDepth
        }

        public struct Report
        {
            public bool Changed;              // false = the same route is still in force; emit nothing
            public bool First;                // no previous route (first decision since payload load)

            public Route Previous;
            public Route Current;

            // ⚠ THE FIELD THIS INSTRUMENT EXISTS FOR (41 §6). Wall time the replaced route was held.
            // ApplyZones runs on a 10-minute throttle, so a value at or near 10m means the route did
            // not survive a single re-evaluation.
            public TimeSpan HeldFor;

            // C3's run-length. Count is how many changes the ring still holds (<= HistoryDepth) and
            // Span is the wall time they cover, INCLUDING this one. "3 changes in 40m" is churn;
            // "3 changes in 11h" is a run making progress. Reported as a pair on purpose — a count
            // without its span is the kind of number that gets misread as a threshold.
            public int RunCount;
            public TimeSpan RunSpan;

            // OSCILLATION, as opposed to progress: this exact route was in force before, within the
            // ring. LeftAgo is how long ago it was abandoned; HeldPreviouslyFor is how long it lasted
            // that time. A pair of routes trading places produces a REVISIT on every second line.
            public bool Revisit;
            public TimeSpan RevisitLeftAgo;
            public TimeSpan RevisitHeldFor;

            // How stale Previous.Score is. It was measured when Previous was ADOPTED, not now — so
            // the vs-previous margin mixes two instants and the line has to say so. The vs-runner-up
            // margin does not have this problem, which is why both are reported.
            public double PrevAgeSeconds;
        }

        // Records the route this pass chose and reports whether it is a change.
        //
        // ⚠ CALL THIS AT THE DECISION, NOT AT THE Settings.SnipeZone WRITE. Every routing site guards
        // its write with `if (SnipeZone != x)`, so hanging the instrument off the write would miss
        // every change that keeps the zone number and moves the track — and that is a real transition
        // with a real cost: "IDLE on zone N becomes FARM on zone N the moment one-hit is reached, and
        // the zone number does not move" (AdvisorApply.cs, the farmSig note). The signature is
        // track+zone for exactly that reason.
        public static Report Observe(State s, Route r, DateTime nowUtc)
        {
            var rep = new Report { Current = r };
            if (s == null) return rep;

            if (s.Have && string.Equals(s.Current.Sig, r.Sig, StringComparison.Ordinal))
            {
                // Same route still in force. The score may have moved; that is not a routing event
                // and is deliberately not reported — this instrument measures CHANGES.
                return rep;
            }

            rep.Changed = true;
            rep.First = !s.Have;
            rep.Previous = s.Current;

            if (s.Have)
            {
                rep.HeldFor = Positive(nowUtc - s.CurrentSince);
                rep.PrevAgeSeconds = Positive(nowUtc - s.ScoredAt).TotalSeconds;
                s.Changes++;

                // REVISIT: is the incoming route already in the ring? Searched newest-first over the
                // entries BEFORE the one about to be pushed, so a route immediately re-adopted after
                // one pass away is the strongest possible hit.
                for (int back = 0; back < s.Filled; back++)
                {
                    int idx = Index(s, back);            // 0 = most recent adoption
                    if (!string.Equals(s.Sigs[idx], r.Sig, StringComparison.Ordinal)) continue;

                    rep.Revisit = true;
                    // Adoption `idx` was left when the NEXT adoption happened. `back == 0` is the
                    // route we are replacing right now, whose departure is this instant.
                    var leftAt = back == 0 ? nowUtc : s.At[Index(s, back - 1)];
                    rep.RevisitLeftAgo = Positive(nowUtc - leftAt);
                    rep.RevisitHeldFor = Positive(leftAt - s.At[idx]);
                    break;
                }
            }

            Push(s, r.Sig, nowUtc);

            // The run length counts CHANGES, so a first adoption contributes nothing to it.
            rep.RunCount = (int)Math.Min(s.Changes, HistoryDepth);
            if (rep.RunCount > 1)
            {
                // Oldest retained CHANGE. The ring holds adoptions; the first adoption of the session
                // is not a change, so when it is still in the ring it must not anchor the span.
                int oldestChange = Math.Min(s.Filled - 1, rep.RunCount - 1);
                rep.RunSpan = Positive(nowUtc - s.At[Index(s, oldestChange)]);
            }

            s.Have = true;
            s.Current = r;
            s.CurrentSince = nowUtc;
            s.ScoredAt = nowUtc;
            return rep;
        }

        private static int Index(State s, int back)
        {
            // back == 0 -> the most recently written entry.
            int i = (s.Head - 1 - back) % HistoryDepth;
            if (i < 0) i += HistoryDepth;
            return i;
        }

        private static void Push(State s, string sig, DateTime at)
        {
            s.Sigs[s.Head] = sig;
            s.At[s.Head] = at;
            s.Head = (s.Head + 1) % HistoryDepth;
            if (s.Filled < HistoryDepth) s.Filled++;
        }

        private static TimeSpan Positive(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

        // ---- rendering ------------------------------------------------------------------------

        // One block per change. Returns null when nothing changed, so the caller's emit is a null
        // check rather than a second copy of the decision.
        public static string Format(Report rep)
        {
            if (!rep.Changed) return null;
            var sb = new StringBuilder();

            sb.Append("[RouteChurn] ")
              .Append(rep.First ? "(none)" : Tag(rep.Previous.Track) + " " + rep.Previous.Where)
              .Append(" -> ").Append(Tag(rep.Current.Track)).Append(' ').Append(rep.Current.Where);

            if (rep.First)
            {
                sb.Append(" — first route since load");
            }
            else
            {
                sb.Append(" after ").Append(Dur(rep.HeldFor));
                if (rep.RunCount > 1)
                    sb.Append(" — ").Append(rep.RunCount.ToString(CultureInfo.InvariantCulture))
                      .Append(" changes in ").Append(Dur(rep.RunSpan));
                else
                    sb.Append(" — first change since load");
                if (rep.Revisit)
                    sb.Append(", REVISIT (left ").Append(Dur(rep.RevisitLeftAgo))
                      .Append(" ago after ").Append(Dur(rep.RevisitHeldFor)).Append(" on it)");
            }

            if (!rep.First)
                sb.Append("\n  left ").Append(Describe(rep.Previous))
                  .Append(" [measured ").Append(Dur(TimeSpan.FromSeconds(rep.PrevAgeSeconds)))
                  .Append(" ago]");
            sb.Append("\n  took ").Append(Describe(rep.Current));

            var vsRunner = MarginVsRunnerUp(rep.Current);
            if (vsRunner != null) sb.Append("\n  margin vs runner-up: ").Append(vsRunner);
            if (!rep.First)
            {
                var vsPrev = MarginVsPrevious(rep);
                if (vsPrev != null) sb.Append("\n  margin vs previous: ").Append(vsPrev);
            }
            return sb.ToString();
        }

        private static string Tag(string track) => string.IsNullOrEmpty(track) ? "?" : track;

        private static string Describe(Route r)
        {
            var sb = new StringBuilder();
            sb.Append(Tag(r.Track)).Append(' ').Append(r.Where).Append(": ");
            sb.Append(r.ScoreLabel == null ? "unranked" : r.ScoreLabel + " " + Val(r.Score, r.ScoreInHours));

            // RARE ranks ON its cadence, so its two columns hold one number. Printed once — a
            // duplicated figure reads as two independent measurements agreeing, which it is not.
            bool cadenceIsScore = r.ScoreInHours && !double.IsNaN(r.Cadence)
                && !double.IsNaN(r.Score) && r.Cadence == r.Score;
            if (!cadenceIsScore && !double.IsNaN(r.Cadence))
                sb.Append(", cadence ").Append(Hrs(r.Cadence));

            // The bar, ONCE, against whichever of the two quantities it actually tests — FARM is
            // admitted on hours-to-cap, SET and RARE on cadence. Naming the tested quantity is the
            // 41 §3 lesson applied to the instrument: two tracks measured by different bars, with
            // which bar left implicit, is the defect that started all of this.
            if (!double.IsNaN(r.Bar))
            {
                // Cadence is hours by definition; a bar on the SCORE is in the score's own unit.
                bool barHours = r.BarOnCadence || r.ScoreInHours;
                sb.Append(" [bar: ").Append(r.BarOnCadence ? "cadence" : (r.ScoreLabel ?? "score"))
                  .Append(" <= ").Append(Val(r.Bar, barHours)).Append(", ")
                  .Append(Distance(r.BarOnCadence ? r.Cadence : r.Score, r.Bar, barHours, r.HigherWins))
                  .Append(']');
            }

            if (!string.IsNullOrEmpty(r.RunnerUpName))
                sb.Append(", runner-up ").Append(r.RunnerUpName).Append(' ')
                  .Append(Val(r.RunnerUp, r.ScoreInHours));
            if (!string.IsNullOrEmpty(r.Reason))
                sb.Append(" — ").Append(r.Reason);
            return sb.ToString();
        }

        // How far the admitted quantity sits from the bar it had to clear. 41 §6's named mechanism:
        // "PPP's 2.1h cadence sits close to the 3h admission bar" — this is that sentence as a number,
        // so the reader does not have to do the subtraction on every line.
        private static string Distance(double value, double bar, bool hours, bool higherWins)
        {
            if (double.IsNaN(value) || double.IsNaN(bar) || double.IsInfinity(value) || bar <= 0)
                return "distance unknown";
            var slack = higherWins ? value - bar : bar - value;
            var pct = slack / bar * 100.0;
            return (slack >= 0 ? "clears by " : "over by ")
                 + Val(Math.Abs(slack), hours) + " ("
                 + Math.Abs(pct).ToString("0.#", CultureInfo.InvariantCulture) + "%)";
        }

        // THE MARGIN A HYSTERESIS BAND WOULD HAVE HAD TO ABSORB, measured at one instant: the winner
        // and second place in the SAME ranking, this pass.
        private static string MarginVsRunnerUp(Route r)
        {
            if (string.IsNullOrEmpty(r.RunnerUpName) || r.ScoreLabel == null) return null;
            if (double.IsNaN(r.Score) || double.IsNaN(r.RunnerUp)) return null;

            return r.ScoreLabel + " " + Val(r.Score, r.ScoreInHours)
                 + " vs " + r.RunnerUpName + " " + Val(r.RunnerUp, r.ScoreInHours)
                 + " — " + Gap(r.Score, r.RunnerUp, r.HigherWins, r.ScoreInHours);
        }

        // THE MARGIN OVER THE ROUTE THAT WAS REPLACED. Weaker than the runner-up margin and the line
        // says so: the previous score was measured when that route was adopted, so this compares two
        // instants. Reported anyway because it is the ONLY margin available when the two routes are on
        // different tracks — which is precisely the SetTarget-vs-Rare case 41 §6 is about.
        //
        // ⚠ IT REFUSES TO SUBTRACT UNLIKE QUANTITIES. Hours-to-cap minus hours-per-drop is a number
        // with no meaning, and printing one would recreate the two-bars defect of 41 §3 inside the
        // instrument built to detect its consequences. Different labels => say so, then fall back to
        // the cadence, which IS the quantity the two tracks share.
        private static string MarginVsPrevious(Report rep)
        {
            var a = rep.Previous;
            var b = rep.Current;
            var stale = rep.PrevAgeSeconds > 0
                ? " (the previous figure was measured " + Dur(TimeSpan.FromSeconds(rep.PrevAgeSeconds)) + " earlier)"
                : "";

            // ⚠ SAME LABEL IS NOT ENOUGH — the UNIT and the DIRECTION have to agree too, or the
            // subtraction below is between two things that only look alike.
            bool sameMetric = a.ScoreLabel != null && b.ScoreLabel != null
                && string.Equals(a.ScoreLabel, b.ScoreLabel, StringComparison.Ordinal)
                && a.ScoreInHours == b.ScoreInHours
                && a.HigherWins == b.HigherWins;

            if (sameMetric && !double.IsNaN(a.Score) && !double.IsNaN(b.Score))
                return a.ScoreLabel + " " + Val(a.Score, a.ScoreInHours)
                     + " -> " + Val(b.Score, b.ScoreInHours)
                     + " — " + Gap(b.Score, a.Score, b.HigherWins, b.ScoreInHours) + stale;

            var head = "rank metrics differ ("
                     + (a.ScoreLabel ?? "unranked") + " vs " + (b.ScoreLabel ?? "unranked")
                     + ") — not comparable";
            if (!double.IsNaN(a.Cadence) && !double.IsNaN(b.Cadence))
                return head + "; cadence " + Hrs(a.Cadence) + " -> " + Hrs(b.Cadence)
                     + " — " + Gap(b.Cadence, a.Cadence, false, true) + stale;
            return head + stale;
        }

        // "won by X (Y%)" for the winner against a comparand on the same metric.
        private static string Gap(double winner, double other, bool higherWins, bool hours)
        {
            if (double.IsNaN(winner) || double.IsNaN(other)) return "margin unknown";
            if (double.IsInfinity(winner) || double.IsInfinity(other)) return "margin unbounded";
            var by = higherWins ? winner - other : other - winner;
            var basis = Math.Abs(other);
            // ⚠ "LOST by" IS NOT A BUG REPORT. Tracks are ranked in TIERS as well as by score — SET
            // outranks RARE categorically (41 §3) — so a switch to a worse-scoring route is legal and
            // expected whenever the tier changed. It is still the single most interesting line in
            // this log, because a hysteresis band would have held the old route through it.
            var verb = by >= 0 ? "won by " : "LOST by ";
            var s = verb + Val(Math.Abs(by), hours);
            if (basis > 0 && !double.IsInfinity(basis))
                s += " (" + (Math.Abs(by) / basis * 100.0).ToString("0.#", CultureInfo.InvariantCulture) + "%)";
            return s;
        }

        private static string Val(double v, bool hours)
        {
            if (hours) return Hrs(v);
            if (double.IsNaN(v)) return "?";
            if (double.IsInfinity(v)) return "unbounded";
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        // Matches AdvisorApply.FmtH so the instrument and the route lines above it read the same.
        public static string Hrs(double h)
        {
            if (double.IsNaN(h)) return "?";
            if (double.IsInfinity(h)) return "never";
            return h >= 1
                ? h.ToString("0.#", CultureInfo.InvariantCulture) + "h"
                : (h * 60).ToString("0", CultureInfo.InvariantCulture) + "m";
        }

        // Wall-clock durations, which span seconds to days here — a route can flip inside one pass or
        // hold for a week, and both must stay legible.
        public static string Dur(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            var sec = t.TotalSeconds;
            if (sec < 90) return sec.ToString("0", CultureInfo.InvariantCulture) + "s";
            if (t.TotalMinutes < 90) return t.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + "m";
            if (t.TotalHours < 48) return t.TotalHours.ToString("0.#", CultureInfo.InvariantCulture) + "h";
            return t.TotalDays.ToString("0.#", CultureInfo.InvariantCulture) + "d";
        }
    }
}
