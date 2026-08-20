using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NGUAdvisor.Managers
{
    // THE OBJECTIVE LAYER'S COMPARISON RUN — amendment 34 §7.1, sequenced by amendment 33 §5's
    // "leave the working mechanism alone until the replacement is proven". The objective layer is an
    // ADDITIVE second membership source [OPERATOR]: it is computed against the same tick's state as
    // the profile's membership, the two are compared, and the PROFILE IS STILL THE ONE USED.
    //
    // ⚠ NOTHING HERE CHANGES ALLOCATION, and the proof is structural rather than a spot check. This
    // file takes VALUE COPIES of four facts per lane (label, class name, index, pool) and returns a
    // report. It is handed no Plan, no LaneSpec, no ResourceBreakpoint and no Character; there is no
    // type in this file's signature through which a unit of the pool could move. Compare() is called
    // AFTER the fill has already run and committed (ConstraintLayerBridge.PerformSwap), for the same
    // reason ConstraintParity is: the counterfactual is computed against a tick that is already over.
    //
    // THE SHAPE IS ConstraintParity's, deliberately, and not a second idiom: compute the
    // counterfactual, use the live one, log the divergence on a signature-keyed throttle. It reuses
    // ConstraintParity.ShouldEmit and its two constants outright rather than copying them — two
    // copies of a 30s floor drift, and the throttle is not the thing under test here.
    //
    // ⚠ A SILENCE IS NOT A DROP (34 §6). The objective table records 94 silences with reasons, and
    // "the table says nothing about this lane" means THE OBJECTIVE LAYER HAS NO OPINION — never "the
    // objective layer would remove it". That distinction is why ObjectiveReader carries two surfaces,
    // and this file uses BOTH: Slot() answers "does the guide say anything here" (Availability) and
    // LevelSlot() answers "does it supply a stopping level" (the only question Pass 3 can consume).
    // A lane the guide is mute about, and a lane the guide discusses at length without ever naming a
    // level, are DIFFERENT no-opinions and render differently.
    //
    // ⚠ ONLY A `level` ROW IS A LANE INSTRUCTION (23 §0.3). 32 of the table's 70 rows are kind=Level;
    // the other 38 are rate (10), time (9) and predicate (19). A predicate is a target SELECTOR
    // computed upstream and re-emitted as a level — reading one as a membership instruction is
    // exactly the caller error TargetPass.RowKind's header names. Only Level rows reach the ADD side
    // of this comparison, and a slot whose every row is rate/time/predicate is a NO-OPINION.
    public static class ObjectiveParity
    {
        // ⚠ THERE IS DELIBERATELY NO `Drop` VERDICT, and its absence is a finding rather than an
        // omission. The objective table has no negative membership vocabulary: it names slots and
        // supplies levels, and everything else it does is silence (23 §7's ledger — "no level
        // exists", never "do not fund"), a non-level kind, or an unadjudicated conflict. C4's rule
        // and the table's own content agree, so the honest drop count is structurally zero and there
        // is no enum member here that a later edit could quietly start populating from a silence.
        public enum Verdict
        {
            // Fail-closed default: a default(Row) is unevaluated, never an agreement.
            Unevaluated = 0,

            // The profile seats this lane AND the objective layer supplies a standing stopping level
            // for it. The only class on which the two sources are commensurable at all.
            BothLevel,

            // The objective layer supplies a standing level for a slot the profile does NOT seat.
            // THE ADD — the one direction in which this comparison can say the profile is missing
            // something.
            ObjectiveAdds,

            // The slot's only level rows are campaign-scoped (23 §4's two 100LC terminals, which
            // TargetRow.CampaignScope exists to stop writing as standing targets). NOT an add: this
            // comparator is given no campaign signal and does not invent one. See C6.
            CampaignScopedOnly,

            // The profile seats it and the guide SPEAKS about it — a rate, a time box, a selector —
            // but never with a level. No opinion on membership, and 34 §6's whole point: losing this
            // to the ledger would lose the guidance, folding it into agreement would lose the ledger.
            NoOpinionGuidanceWithoutLevel,

            // The profile seats it and the guide is mute. The ledger's recorded reason travels with
            // it. NOT a drop.
            NoOpinionSilent,

            // 23 records two irreconcilable readings of its own sentence and adjudicates neither
            // (the M0/M1 pair). No number is emitted on either reading, so no membership claim is.
            NoOpinionConflict,

            // The membership seats a lane 23's schema cannot NAME — BasicTrainingBP, RitualBP, BR,
            // HackBP, beards, and BestAug (deliberately unnamed: its augment pair is chosen inside
            // Allocate(), so it has no slot to key a row to — TargetPass.cs:147-159). Outside the
            // comparison in both directions: neither an agreement nor a disagreement.
            Unnameable
        }

        // WHERE THE MEMBERSHIP BEING COMPARED ACTUALLY CAME FROM.
        //
        // ⚠ THIS FIELD EXISTS BECAUSE THE RENDERER USED TO GUESS, AND GUESSED WRONG IN BOTH DIRECTIONS
        // AT ONCE. Measured on a live save 2026-08-18 (audit/59 §A): the block asserted "THE PROFILE IS
        // STILL THE ONE ALLOCATING" while auto profile had SUBSTITUTED the token list
        // (ChallengeOverlay.TransformPriorities:319-323), and it labelled the generated lanes
        // "(profile #10)" through "(profile #13)" on a profile whose Energy row holds THREE tokens.
        // Both halves of that are the same mistake: the file was handed a list and assumed its origin.
        //
        // Unstated is the fail-closed default, in the same spirit as Verdict.Unevaluated. A caller that
        // does not say must NOT have its lanes rendered as the operator's own — the whole defect was a
        // confident sentence about a membership nobody had asked about.
        public enum MembershipSource
        {
            Unstated = 0,

            // The profile's own token list, after the null filter and IsValid(), unsubstituted.
            Profile,

            // ChallengeOverlay.AutoGenerated(type) replaced the list wholesale for this segment. The
            // profile's tokens survive only as a fallback if the generated list comes back empty.
            AutoProfile,

            // A strip challenge gutted the list and a challenge template replaced it.
            ChallengeTemplate,

            // Every token in the profile's list was inactive and a fallback list was injected.
            ChallengeFallback,

            // Laser Sword: the sword augment lanes pushed in FRONT of whatever the base list was.
            ChallengeOverlay,
        }

        // The sentence that replaced "THE PROFILE IS STILL THE ONE ALLOCATING". Public so the wording
        // is assertable — an accurate report that later drifts back to a confident wrong sentence is
        // the exact regression this whole field guards, and prose is the thing a test has to pin.
        public static string SourceSentence(MembershipSource source)
        {
            switch (source)
            {
                case MembershipSource.Profile:
                    return "The lanes below are YOUR PROFILE'S list, and the profile is what allocated this tick.";
                case MembershipSource.AutoProfile:
                    return "⚠ The lanes below are NOT from your profile. Auto Profile generated this segment's " +
                           "list and THAT is what allocated this tick — your own tokens were not consulted.";
                case MembershipSource.ChallengeTemplate:
                    return "⚠ The lanes below are NOT from your profile. A challenge template replaced the list " +
                           "for this challenge and THAT is what allocated this tick.";
                case MembershipSource.ChallengeFallback:
                    return "⚠ The lanes below are NOT from your profile. Every token in your list was inactive, " +
                           "so a fallback list was injected and THAT is what allocated this tick.";
                case MembershipSource.ChallengeOverlay:
                    return "⚠ The lanes below are not your list as authored: the Laser Sword overlay pushed the " +
                           "sword augment lanes in front of the base list for the duration of the challenge.";
                default:
                    return "⚠ The caller did not record where this membership came from, so do not read it as " +
                           "your profile's list.";
            }
        }

        // The word the rank is rendered with. "profile #3" is a claim about ORIGIN, not just position,
        // and it was false on every auto-profile tick.
        public static string RankWord(MembershipSource source)
        {
            switch (source)
            {
                case MembershipSource.Profile:           return "profile";
                case MembershipSource.AutoProfile:       return "auto-profile";
                case MembershipSource.ChallengeTemplate: return "template";
                case MembershipSource.ChallengeFallback: return "fallback";
                case MembershipSource.ChallengeOverlay:  return "LSC list";
                default:                                 return "membership";
            }
        }

        // The four facts about a profile lane this comparison needs — all value copies, taken by the
        // caller from ResourceBreakpoint's read-only diagnostic surface (Label :87, LaneIndex :104,
        // LaneType :106). The lane object itself deliberately does not cross into this file.
        public struct ProfileLane
        {
            public string Label;       // the profile token, e.g. "NGU-4" / "CAPTM"
            public string ClassName;   // GetType().Name — TargetPass.SystemForLane's key
            public int Index;
            public bool EnergyPool;
        }

        public struct Row
        {
            public Verdict Verdict;
            public string Label;        // the profile token, or "<system> id N" for an add
            public string System;       // null exactly when Unnameable
            public int Id;
            public TargetPass.Track Track;

            // Position in the COMPARED MEMBERSHIP's order; -1 when that membership does not seat it.
            // ⚠ The field name is historical and the rank is NOT necessarily the profile's: what the
            // rank counts is whatever list the caller supplied, and Report.Source is the only thing
            // that says whose list that was. Rendering it as "(profile #N)" unconditionally is the
            // defect audit/59 §A measured; RankWord(Source) is what it renders through now.
            // ⚠ There is no ObjectiveRank counterpart — see C6: the table's order is 23 §2's
            // transcription order, grouped by system, and is not a priority claim.
            public int ProfileRank;

            public string Detail;       // the level and its terminality, the kinds present, or the
                                        // ledger's reason — never a bare number for a silence
        }

        public struct Report
        {
            public bool ChapterKnown;
            public int Chapter;
            public bool EnergyPool;
            public TargetPass.Track NguTrack;
            public TargetPass.Track RunTrack;

            // Whose list the Rows below describe. Unstated until a caller says otherwise.
            public MembershipSource Source;

            public IList<Row> Rows;

            public int Agreements;
            public int Adds;
            public int CampaignScoped;
            public int NoOpinion;
            public int Unnameable;

            // Non-null exactly when the comparison DID NOT RUN. A held report carries no rows and
            // never emits — but it is latched by the caller rather than discarded, because a
            // comparison that silently never ran is indistinguishable from one that always agrees.
            public string HeldReason;

            public bool Held { get { return HeldReason != null; } }
        }

        private static readonly Row[] NoRows = new Row[0];

        // Which systems can appear in which pool, each derived from the lane class's own
        // CorrectResourceType() rather than assumed: AdvancedTrainingBP.cs:35 and AugmentBP.cs:8 are
        // Energy-only; NGUBP.cs:69, TimeMachineBP.cs:7 and WandoosBP.cs:30 accept both pools and
        // TargetPass.SystemForLane already splits the first two BY pool (ngu-energy/ngu-magic,
        // tm-speed/tm-goldmulti). Wandoos is the one system that genuinely appears in both.
        private static readonly string[] EnergySystems =
        {
            TargetPass.SysAugments,
            TargetPass.SysNguEnergy,
            TargetPass.SysAt,
            TargetPass.SysTmSpeed,
            TargetPass.SysWandoos,
        };

        private static readonly string[] MagicSystems =
        {
            TargetPass.SysNguMagic,
            TargetPass.SysTmGoldMulti,
            TargetPass.SysWandoos,
        };

        // The track a row for this system would be keyed to — the SAME split
        // ConstraintLayerBridge.LaneStateFor makes (:427-453), not a second reading of it. Only the
        // NGUs carry the game's three-way level split ([DECOMP] NGU.cs:8-16), so they follow the
        // live selector settings.nguLevelTrack; every other system has one field and follows the
        // run's difficulty.
        public static TargetPass.Track TrackFor(string system, TargetPass.Track nguTrack,
            TargetPass.Track runTrack)
        {
            return TargetPass.IsNguSystem(system) ? nguTrack : runTrack;
        }

        // ---- the comparison ------------------------------------------------------------------------

        // ⚠ CHAPTER 0 IS NOT A CHAPTER, AND QUERYING IT IS WORSE THAN NOT COMPARING. StageDetector
        // returns Unknown — Known=false, Chapter=0 — on ANY exception (StageDetector.cs:92-96), and
        // ObjectiveTable.ChapterMatches(:905-909) treats a query chapter of 0 as ChapterAny, which
        // MATCHES EVERY ROW IN THE TABLE. A comparator that took chapter 0 at face value would
        // therefore report the guide's entire 70-row corpus against one pool's membership, every
        // tick the detector threw. The discriminator is Stage.Known, never `chapter != 0`; the
        // range check below is the second door, so a Known stage carrying an out-of-range chapter
        // cannot reach the table either.
        //
        // A held comparison is also the answer to advisors/01:406 — a detector blip must not be
        // allowed to say anything, and the caller reads the stage FRESH each tick rather than
        // through ChallengeOverlay's 60s cache, so a blip costs one tick instead of sixty seconds.
        //
        // `source` is OPTIONAL and defaults to Unstated deliberately: a caller that has not been taught
        // to say where its membership came from must produce a report that admits it, not one that
        // silently inherits the old "this is the profile's list" claim. See MembershipSource.
        public static Report Compare(int chapter, bool chapterKnown, TargetPass.Track nguTrack,
            TargetPass.Track runTrack, bool energyPool, IList<ProfileLane> lanes,
            MembershipSource source = MembershipSource.Unstated)
        {
            var report = new Report
            {
                ChapterKnown = chapterKnown,
                Chapter = chapter,
                EnergyPool = energyPool,
                NguTrack = nguTrack,
                RunTrack = runTrack,
                Source = source,
                Rows = NoRows,
            };

            if (!chapterKnown)
            {
                report.HeldReason = "chapter unknown — StageDetector returned Unknown this tick " +
                                    "(StageDetector.cs:92-96); a chapter-0 query matches EVERY row " +
                                    "in the table (ObjectiveTable.ChapterMatches), so no comparison " +
                                    "is made rather than a false one";
                return report;
            }

            if (chapter < 1 || chapter > 8)
            {
                report.HeldReason = "chapter " + chapter.ToString(CultureInfo.InvariantCulture) +
                                    " is outside 1-8 — the table is keyed 1-8 plus ChapterAny, and " +
                                    "querying ChapterAny would match every row";
                return report;
            }

            // A track the caller could not read is the same hazard one step down: TrackMatches
            // admits only TrackNeutral and AllTracks rows against Unspecified, so every level row
            // would vanish and every slot would read as a silence. Hold instead.
            if (nguTrack == TargetPass.Track.Unspecified || runTrack == TargetPass.Track.Unspecified)
            {
                report.HeldReason = "track unreadable this tick (ngu " + nguTrack + ", run " +
                                    runTrack + ") — an Unspecified query admits no level row, which " +
                                    "would read as a silence on every slot";
                return report;
            }

            var rows = new List<Row>();

            // Which (system, id) slots the profile seats, and where in its order. Built first so the
            // ADD direction below can ask the question it actually needs — "is this slot a member" —
            // rather than re-walking the lane list per slot.
            var seated = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < lanes.Count; i++)
            {
                var lane = lanes[i];
                var system = TargetPass.SystemForLane(lane.ClassName, lane.EnergyPool);
                if (system == null)
                {
                    rows.Add(new Row
                    {
                        Verdict = Verdict.Unnameable,
                        Label = lane.Label,
                        System = null,
                        Id = lane.Index,
                        ProfileRank = i,
                        Detail = "23's schema names no system for " + (lane.ClassName ?? "?") +
                                 " — outside the comparison, not a disagreement " +
                                 "(TargetPass.SystemForLane)",
                    });
                    report.Unnameable++;
                    continue;
                }

                var track = TrackFor(system, nguTrack, runTrack);
                seated[Key(system, lane.Index)] = i;
                rows.Add(Classify(chapter, track, system, lane.Index, lane.Label, i));
            }

            // THE ADD DIRECTION. Every slot the pool can hold, asked the ONE question Pass 3 could
            // consume — does the guide supply a stopping level here — and reported only when the
            // profile does not already seat it. A slot with no level contributes nothing here: it is
            // not an add, and (C4) it is not a drop either.
            var systems = energyPool ? EnergySystems : MagicSystems;
            for (int s = 0; s < systems.Length; s++)
            {
                var system = systems[s];
                var track = TrackFor(system, nguTrack, runTrack);
                int idCount = ObjectiveReader.IdCount(system);
                for (int id = 0; id < idCount; id++)
                {
                    if (seated.ContainsKey(Key(system, id)))
                        continue;

                    var answer = ObjectiveReader.LevelSlot(chapter, track, system, id);
                    if (!answer.HasLevel)
                        continue;

                    string detail;
                    bool standing = DescribeLevels(answer.LevelRows, out detail);
                    rows.Add(new Row
                    {
                        Verdict = standing ? Verdict.ObjectiveAdds : Verdict.CampaignScopedOnly,
                        Label = SlotName(system, id),
                        System = system,
                        Id = id,
                        Track = track,
                        ProfileRank = -1,
                        Detail = detail,
                    });
                    if (standing) report.Adds++; else report.CampaignScoped++;
                }
            }

            foreach (var r in rows)
            {
                switch (r.Verdict)
                {
                    case Verdict.BothLevel: report.Agreements++; break;
                    case Verdict.CampaignScopedOnly:
                        if (r.ProfileRank >= 0) report.CampaignScoped++;
                        break;
                    case Verdict.NoOpinionGuidanceWithoutLevel:
                    case Verdict.NoOpinionSilent:
                    case Verdict.NoOpinionConflict:
                        report.NoOpinion++;
                        break;
                }
            }

            report.Rows = rows;
            return report;
        }

        // One seated, nameable lane against the objective layer. BOTH reader surfaces are consulted,
        // in the order 34 §6 defines them: Availability first (does the guide say ANYTHING here —
        // and a conflict outranks a row set), then the level question.
        private static Row Classify(int chapter, TargetPass.Track track, string system, int id,
            string label, int rank)
        {
            var row = new Row
            {
                Label = label,
                System = system,
                Id = id,
                Track = track,
                ProfileRank = rank,
            };

            var slot = ObjectiveReader.Slot(chapter, track, system, id);
            if (slot.Availability == ObjectiveReader.Availability.Conflict)
            {
                row.Verdict = Verdict.NoOpinionConflict;
                row.Detail = slot.Reason;
                return row;
            }

            var answer = ObjectiveReader.LevelSlot(chapter, track, system, id);
            if (answer.HasLevel)
            {
                string detail;
                bool standing = DescribeLevels(answer.LevelRows, out detail);
                row.Verdict = standing ? Verdict.BothLevel : Verdict.CampaignScopedOnly;
                row.Detail = detail;
                return row;
            }

            // NO LEVEL. Two different no-opinions, and collapsing them is what 34 §6 calls losing
            // either the guidance or the ledger entry.
            if (answer.OtherRows != null && answer.OtherRows.Count > 0)
            {
                row.Verdict = Verdict.NoOpinionGuidanceWithoutLevel;
                row.Detail = "no level; the guide speaks here in " + KindTags(answer.OtherRows) +
                             " — none of which is a lane instruction (23 §0.3)";
                return row;
            }

            row.Verdict = Verdict.NoOpinionSilent;
            row.Detail = answer.Reason;
            return row;
        }

        // Renders a slot's level rows, and answers whether ANY of them is standing. All-campaign
        // -scoped is a different verdict: 23 §4's two 100LC terminals must never read as standing.
        private static bool DescribeLevels(IList<ObjectiveTable.LaneRow> levels, out string detail)
        {
            bool standing = false;
            var sb = new StringBuilder();
            for (int i = 0; i < levels.Count; i++)
            {
                var r = levels[i];
                if (r.CampaignScope == null)
                    standing = true;
                if (sb.Length > 0)
                    sb.Append("; ");
                sb.Append("level ").Append(Range(r.ValueLow, r.ValueHigh))
                  .Append(" [").Append(TerminalityTag(r.Terminality)).Append("]");
                if (r.CampaignScope != null)
                    sb.Append(" scope=").Append(r.CampaignScope);
                if (!string.IsNullOrEmpty(r.Cite))
                    sb.Append(" ").Append(r.Cite);
            }
            detail = sb.ToString();
            return standing;
        }

        private static string Range(long low, long high)
        {
            return low == high
                ? NumberFormatter.Abbrev(low)
                : NumberFormatter.Abbrev(low) + "-" + NumberFormatter.Abbrev(high);
        }

        // ⚠ Terminality is a FIELD, never derived from prose (23 §0.5). The pair that established
        // that — Respawn 401 terminal because its curve saturates, Adventure a's softcap 1000 a
        // precondition because its does not, from the same word "softcap" — is now HALF a pair:
        // [OPERATOR] removed the Respawn row 2026-08-07, and the standing terminal that remains
        // (AT Block at 100,000) is terminal by ruling, not by curve shape. Which only makes the
        // point harder: there is no prose, and no curve, this could be recomputed from. It renders
        // the field.
        private static string TerminalityTag(TargetPass.Terminality t)
        {
            switch (t)
            {
                case TargetPass.Terminality.Terminal: return "terminal";
                case TargetPass.Terminality.Precondition: return "PRECONDITION — not a stopping point";
                case TargetPass.Terminality.Ambiguous: return "ambiguous — needs an operator decision";
                default: return "terminality unfilled";
            }
        }

        private static string KindTags(IList<ObjectiveTable.LaneRow> rows)
        {
            var seen = new List<string>();
            for (int i = 0; i < rows.Count; i++)
            {
                var tag = rows[i].Kind.ToString().ToLowerInvariant();
                if (!seen.Contains(tag))
                    seen.Add(tag);
            }
            seen.Sort(StringComparer.Ordinal);
            return string.Join("/", seen.ToArray()) + " row(s)";
        }

        private static string Key(string system, int id) =>
            system + "#" + id.ToString(CultureInfo.InvariantCulture);

        private static string SlotName(string system, int id) =>
            system + " id " + id.ToString(CultureInfo.InvariantCulture);

        // ---- the throttle ---------------------------------------------------------------------------

        // Amount-insensitive by construction — there are no amounts here. The signature is the SET of
        // (verdict, slot) pairs, which is what carries the signal: a chapter change, a track switch,
        // a profile edit or a lane leaving the membership all move it; nothing else does.
        //
        // ⚠ A HELD REPORT SIGNS AS EMPTY, so ShouldEmit refuses it and a chapter-unknown tick can
        // never produce a divergence report. The caller latches the held state on its own channel.
        public static string Signature(in Report report)
        {
            if (report.Held || report.Rows == null || report.Rows.Count == 0)
                return "";

            var keys = new List<string>(report.Rows.Count);
            foreach (var r in report.Rows)
                keys.Add(r.Verdict + ":" + (r.Label ?? "?"));
            keys.Sort(StringComparer.Ordinal);
            // The SOURCE is in the signature because it changes what the block SAYS, not just what it
            // lists. Auto profile coming on mid-run usually moves the lane set too — but "usually" is
            // not a guarantee, and a tick where the headline flips from "your profile allocated this"
            // to "your tokens were not consulted" must not be throttled away as unchanged.
            return "ch" + report.Chapter.ToString(CultureInfo.InvariantCulture) + "|" +
                   report.Source + "|" + string.Join("|", keys.ToArray());
        }

        // ConstraintParity owns the throttle decision and its two constants — 30s floor on change,
        // 600s refresh on unchanged. Reused rather than restated: Analyze runs ~86,400x/day and two
        // copies of that arithmetic would drift.
        public static bool ShouldEmit(string signature, string lastSignature,
            double secondsSinceLastEmit) =>
            ConstraintParity.ShouldEmit(signature, lastSignature, secondsSinceLastEmit);

        // ---- rendering ------------------------------------------------------------------------------

        // One log block per emission. The headline is the count line — on a real profile most lanes
        // land in NO OPINION, and reading that as "the guide wants your profile gutted" is the exact
        // misreading C4 forbids, so the header says what no-opinion means before the rows do.
        public static string Format(string pool, in Report report)
        {
            // A held report has nothing to say, and a default(Report) is not a comparison that
            // agreed on everything — both render as no block at all rather than as an empty one.
            if (report.Held || report.Rows == null || report.Rows.Count == 0)
                return null;

            var sb = new StringBuilder();
            sb.Append("Objective comparison [").Append(pool).Append("] chapter ")
              .Append(report.Chapter.ToString(CultureInfo.InvariantCulture))
              .Append(" (ngu track ").Append(report.NguTrack)
              .Append(", run ").Append(report.RunTrack).Append("): ")
              // ⚠ "in both", NOT "agree". The two sources agree on MEMBERSHIP here and on nothing
              // else: most of the guide's level rows are PRECONDITIONS ("2-3k Adventure a before
              // Beardverse"), which are not stopping points and which the profile has no way to
              // express either way (23 §0.4). The per-row detail carries the terminality.
              .Append(report.Agreements.ToString(CultureInfo.InvariantCulture)).Append(" in both, ")
              .Append(report.Adds.ToString(CultureInfo.InvariantCulture)).Append(" the guide would ADD, ")
              .Append(report.NoOpinion.ToString(CultureInfo.InvariantCulture)).Append(" NO OPINION, ")
              .Append(report.Unnameable.ToString(CultureInfo.InvariantCulture)).Append(" unnameable");
            if (report.CampaignScoped > 0)
                sb.Append(", ").Append(report.CampaignScoped.ToString(CultureInfo.InvariantCulture))
                  .Append(" campaign-scoped");
            // ⚠ TWO CLAIMS, AND THEY ARE NOT THE SAME CLAIM. "Nothing here was applied" is structural
            // and always true — this file is handed value copies and returns a report. "The profile is
            // the one allocating" is a claim about WHICH LIST allocated, and it was FALSE on every
            // auto-profile tick: the header asserted it while the profile's tokens had been substituted
            // out (audit/59 §A, measured 2026-08-18). Only the first is stated unconditionally now; the
            // second comes from Report.Source, which the caller has to supply.
            sb.Append("\n  NOTHING HERE WAS APPLIED — this is a comparison, not an allocation. ")
              .Append(SourceSentence(report.Source))
              .Append(" NO OPINION is not a drop: the objective table has no way to say \"remove this lane\", ")
              .Append("so a slot it is silent on is one it has nothing to say about.");

            AppendGroup(sb, report, Verdict.ObjectiveAdds, "WOULD ADD");
            AppendGroup(sb, report, Verdict.BothLevel, "IN BOTH (the guide also names a level here)");
            AppendGroup(sb, report, Verdict.CampaignScopedOnly, "CAMPAIGN-SCOPED (not an add)");
            AppendGroup(sb, report, Verdict.NoOpinionConflict, "NO OPINION (conflict)");
            AppendGroup(sb, report, Verdict.NoOpinionGuidanceWithoutLevel,
                "NO OPINION (the guide speaks, but not with a level)");
            AppendGroup(sb, report, Verdict.NoOpinionSilent, "NO OPINION (silent)");
            AppendGroup(sb, report, Verdict.Unnameable, "OUTSIDE THE COMPARISON (schema names no system)");
            return sb.ToString();
        }

        private static void AppendGroup(StringBuilder sb, in Report report, Verdict verdict,
            string heading)
        {
            bool headed = false;
            foreach (var r in report.Rows)
            {
                if (r.Verdict != verdict)
                    continue;
                if (!headed)
                {
                    sb.Append("\n  ").Append(heading).Append(":");
                    headed = true;
                }
                sb.Append("\n    ").Append(r.Label);
                // The membership's ORDER is a fact only the membership has — see C6. Rendered where it
                // exists and simply absent where it does not, rather than faked as a rank.
                //
                // ⚠ THE WORD IS NOT A CONSTANT. "(profile #12)" was printed for lanes an operator's
                // three-token profile never named, because the label was hardcoded while the list
                // underneath it had been substituted. RankWord keeps the two in step.
                if (r.ProfileRank >= 0)
                    sb.Append(" (").Append(RankWord(report.Source)).Append(" #")
                      .Append((r.ProfileRank + 1).ToString(CultureInfo.InvariantCulture)).Append(")");
                if (!string.IsNullOrEmpty(r.Detail))
                    sb.Append(": ").Append(r.Detail);
            }
        }
    }
}
