using SimpleJSON;
using System;
using System.Linq;

namespace NGUAdvisor.AllocationProfiles.BreakpointTypes
{
    public abstract class BaseBreakpoints<T>
    {
        public class Breakpoint
        {
            public double time;
            public T priorities;
            // Optional challenge tag: this breakpoint only applies while that challenge is active (uppercase
            // code, e.g. "NOTM"). null/empty = untagged (the normal timeline, used when no challenge match).
            public string challenge;

            // "FOCUS ON X UNTIL DONE": while this is set and unmet, the timeline does not advance past
            // this breakpoint even though a later one's time has arrived. Null = the old behaviour, a
            // step that ends purely on the clock.
            public Managers.UntilCondition until;
            public string untilText;      // as authored, so the UI and the log can quote it back
            public string untilError;     // set when the clause would not parse — surfaced, never silent

            public Breakpoint(JSONNode bp, T priorities)
            {
                time = ParseTime(bp["Time"]);
                var ch = bp["Challenge"];
                challenge = (ch != null && !string.IsNullOrEmpty(ch.Value)) ? ch.Value.ToUpper() : null;

                // A clause that will not parse must NOT silently become "no condition" — that would
                // turn a typo into a step that ends early and looks deliberate. It is kept as an error
                // and reported; the breakpoint then behaves as it did before, which is the safe side.
                var un = bp["Until"];
                if (un != null && !string.IsNullOrEmpty(un.Value))
                {
                    untilText = un.Value;
                    Managers.UntilCondition parsed; string err;
                    if (Managers.UntilCondition.TryParse(untilText, out parsed, out err))
                    {
                        until = parsed;
                        // Said out loud on the way IN as well as on the way out. A condition that is
                        // read but never reached is indistinguishable from one that was never read at
                        // all, and both look like "the feature does nothing".
                        try { Main.Log($"Profile: step at {time:0}s ends on — {parsed.Describe()}"); } catch { }
                    }
                    else
                    {
                        untilError = err;
                        // Said out loud at load. A step that quietly reverted to clock-only because of
                        // a typo is the worst outcome available here: it looks like it worked.
                        try { Main.Log($"Profile: ignoring \"Until\": \"{untilText}\" — {err}. That step will end on its time instead."); }
                        catch { }
                    }
                }

                this.priorities = priorities;
            }

            private static double ParseTime(JSONNode timeNode)
            {
                var time = 0;

                if (timeNode.IsObject)
                {
                    foreach (var N in timeNode)
                    {
                        if (N.Value.IsNumber)
                        {
                            switch (N.Key.ToLower())
                            {
                                case "h":
                                    time += 60 * 60 * N.Value.AsInt;
                                    break;
                                case "m":
                                    time += 60 * N.Value.AsInt;
                                    break;
                                default:
                                    time += N.Value.AsInt;
                                    break;
                            }
                        }
                    }
                }

                if (timeNode.IsNumber)
                    time = timeNode.AsInt;

                return time;
            }
        }

        // Property, not a type-init capture — see ResourceBreakpoint._character for why.
        protected static Character _character => Main.Character;
        protected Breakpoint[] breakpoints = new Breakpoint[0];
        protected Breakpoint current = null;
        protected bool swapped = false;
        // The challenge under which `current` was selected, so a change of active challenge re-triggers a swap.
        protected string currentChallenge = null;

        public int Length => breakpoints.Length;

        protected BaseBreakpoints() { }

        protected BaseBreakpoints(JSONNode bps, Func<JSONNode, T> selector)
        {
            breakpoints = bps?.Children.Select(bp => new Breakpoint(bp, selector(bp))).OrderByDescending(x => x.time).ToArray();
        }

        // Challenge-aware selection: while a challenge is active, prefer a breakpoint tagged for it; otherwise
        // (or if none matches) fall back to the untagged breakpoints = the normal time-based timeline.
        // breakpoints are sorted descending by time, so the first whose time has passed is the latest one.
        public Breakpoint GetCurrentBreakpoint()
        {
            if (breakpoints == null)
                return null;

            double t = Main.Character.rebirthTime.totalseconds;
            var cur = Managers.ChallengeDetector.Current();

            // THE HOLD. A breakpoint carrying an unmet `until` keeps the timeline where it is, even
            // though a later breakpoint's time has arrived. That is the whole of "focus on X until
            // done": the step ends on its outcome, not on the clock.
            //
            // ⚠ A CHALLENGE STARTING OR ENDING BREAKS THE HOLD, deliberately. Challenge-tagged
            // breakpoints are a separate timeline and the challenge is the bigger event — a hold set
            // on the normal timeline must not strand a run inside a challenge it was never written
            // for. Everything else waits.
            if (cur != null)
            {
                var tagged = PickWithHolds(cur, t);
                if (tagged != null) return tagged;
            }
            return PickWithHolds(null, t);
        }

        // Selection, walking the REACHED breakpoints oldest-first and stopping at the first one whose
        // `until` is not yet met. That breakpoint blocks everything after it — which is exactly what
        // "focus on X until done" means: a later step's time arriving does not entitle it to run.
        //
        // ⚠ STATELESS ON PURPOSE, AND THE FIRST VERSION WAS NOT. It held on the `current` field, which
        // looked right and never once fired in game: the wrapper is rebuilt often enough that `current`
        // was null on every sampled pass, so the branch simply never ran. Nothing about that failure was
        // visible — no error, no log, the feature just did nothing. Deriving the answer from the
        // breakpoint list and the clock each time removes the dependency entirely.
        private Breakpoint PickWithHolds(string chal, double t)
        {
            Managers.UntilFacts facts = default(Managers.UntilFacts);
            bool factsRead = false;
            Breakpoint chosen = null;

            // breakpoints are sorted DESCENDING by time, so walking backwards is oldest-first.
            for (int i = breakpoints.Length - 1; i >= 0; i--)
            {
                var b = breakpoints[i];
                if (b.challenge != chal) continue;
                if (t <= b.time) continue;          // not reached yet

                chosen = b;                          // reached, and nothing before it is holding

                if (b.until == null) continue;
                if (!factsRead) { facts = Managers.UntilFactsProvider.Read(); factsRead = true; }

                Managers.UntilClause met;
                if (!b.until.IsMet(facts, out met))
                {
                    Managers.UntilFactsProvider.NoteHold(b.untilText, b.until);
                    return b;                        // blocks every later step
                }
                Managers.UntilFactsProvider.NoteMet(b.untilText, met);
            }

            return chosen;
        }

        public void Swap()
        {
            var cur = Managers.ChallengeDetector.Current();
            var bp = GetCurrentBreakpoint();
            if (bp == null)
            {
                current = null;
                currentChallenge = cur;
                OnNoBreakpoint();
                return;
            }

            // Re-swap when the selected breakpoint changes OR the active challenge changes.
            if (bp != current || cur != currentChallenge)
            {
                current = bp;
                currentChallenge = cur;
                swapped = false;
            }

            if (swapped)
                return;

            swapped = PerformSwap(bp);
        }

        protected abstract bool PerformSwap(Breakpoint bp);

        // No breakpoint applies right now (empty timeline, or the run is younger than the first entry).
        // Subclasses that publish state ABOUT the active breakpoint must clear it here: `current = null`
        // only clears the field, and Swap() is not virtual, so there is nowhere else to hook.
        // Only GearBreakpoints overrides this today; the empty base keeps every other lane unchanged.
        protected virtual void OnNoBreakpoint() { }

        public virtual void Reset() { current = null; currentChallenge = null; }
    }
}
