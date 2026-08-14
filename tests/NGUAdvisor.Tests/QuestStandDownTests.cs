using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // audit/40 §3 item 7 — "QuestManager.cs:218 and OptimizationAdvisor.cs:914 read the intent field.
    // Whenever layer 2 overrides, both are reasoning about a zone the character is not in."
    //
    // The two consumers do NOT want the same fact, and that is the whole finding:
    //   * the DIGGER VENUE law wants the ROUTED zone — where kills actually happen (FarmVenueTests).
    //   * QUESTING wants the INTENT, because it IS audit/40's R7 and R7 sits above R10. Handed the
    //     routed zone it would read its own output and oscillate (QuestStandDown's header).
    //
    // What both had in common was a hand copy of a row of the R10 chain, free to drift from the one
    // method that decides it — and QuestManager's copy HAD drifted, twice, in the same direction.
    public class QuestStandDownTests
    {
        // The expression as it stood, so every claim below is a comparison against real behaviour
        // rather than an assertion about it. Note what it takes and what the new one does not:
        // `targetItopod`, a toggle, standing in for "what routes".
        //
        //   IsZoneUnlocked(Settings.SnipeZone) && !Settings.AdventureTargetITOPOD && !AllowZoneFallback
        private static bool Old(bool snipeZoneUnlocked, bool targetItopod, bool allowZoneFallback)
            => snipeZoneUnlocked && !targetItopod && !allowZoneFallback;

        private const int FarmZone = 20;
        private const int HuntZone = 33;

        // ── DRIFT 1: THE ADVISOR'S OWN DROP FARM (R10 row 2, added at 271f5f8) ────────────────────
        //
        // audit/40 §6.1 made a drop farm outrank Target ITOPOD in R10 — the fix for the run that
        // "never left the ITOPOD". So with the farm routing zone 20 and the toggle still on,
        // Main.ResolveIntentZone returns 20 and AdvisorApply announces it.
        //
        // The old copy knew only the toggle: it answered "not sniping", shouldQuest went true, and
        // quests took R7 — which sits ABOVE R10. 271f5f8 was undone one row above the row it fixed.
        [Fact]
        public void A_drop_farm_that_won_R10_is_a_snipe_even_with_target_itopod_on()
        {
            Assert.True(QuestStandDown.IsSniping(intentZone: FarmZone,
                                                 intentZoneUnlocked: true, allowZoneFallback: false));

            // ...and this is the row that moved. The old expression said the opposite.
            Assert.False(Old(snipeZoneUnlocked: true, targetItopod: true, allowZoneFallback: false));
        }

        // ── DRIFT 2: THE GEAR HUNT (R10 row 1) ────────────────────────────────────────────────────
        //
        // Main already carried the note "user-reported: Target ITOPOD silently overrode the hunted
        // stage" — the hunt row exists for exactly this defect. The copy never learned about it, so
        // quests pre-empted a hunt the resolver had already protected.
        [Fact]
        public void A_gear_hunt_that_won_R10_is_a_snipe_even_with_target_itopod_on()
        {
            Assert.True(QuestStandDown.IsSniping(intentZone: HuntZone,
                                                 intentZoneUnlocked: true, allowZoneFallback: false));
            Assert.False(Old(snipeZoneUnlocked: true, targetItopod: true, allowZoneFallback: false));
        }

        // ── THE CASE THE TOGGLE NEVER COVERED, IN THE OTHER DIRECTION ─────────────────────────────
        //
        // Target ITOPOD is only ONE of the ways routing lands in the ITOPOD. The boost farm writes
        // Settings.SnipeZone = 1000 whenever there is no boost demand (AdvisorApply.cs:1157), the
        // phase machine's ITOPOD phase writes 1000, and R11 rewrites a locked target to 1000. In all
        // three the toggle is OFF, and IsZoneUnlocked(1000) is itopodOn — so the old expression called
        // a character standing in the ITOPOD "sniping" and refused to quest there, which is the exact
        // opposite of what its own comment said ("not farming ITOPOD").
        [Fact]
        public void The_itopod_is_never_a_snipe_however_it_was_reached()
        {
            Assert.False(QuestStandDown.IsSniping(intentZone: ZonePhase.ItopodZone,
                                                  intentZoneUnlocked: true, allowZoneFallback: false));

            // The toggle path agreed already; the written-1000 path did not, and that is the flip.
            Assert.True(Old(snipeZoneUnlocked: true, targetItopod: false, allowZoneFallback: false));
        }

        // ── WHAT MUST NOT MOVE ────────────────────────────────────────────────────────────────────

        // -1 is the SavedSettings sentinel (SavedSettings.cs:13) and CombatManager.IsZoneUnlocked
        // returns true for it (the Safe Zone). R10 would route -1 there too, so the answer is the
        // same as before and the two now agree for the same reason instead of by coincidence.
        [Fact]
        public void The_unset_sentinel_reads_exactly_as_before()
        {
            Assert.Equal(Old(snipeZoneUnlocked: true, targetItopod: false, allowZoneFallback: false),
                         QuestStandDown.IsSniping(-1, intentZoneUnlocked: true, allowZoneFallback: false));
            Assert.True(QuestStandDown.IsSniping(-1, intentZoneUnlocked: true, allowZoneFallback: false));
        }

        // R11's own test, unchanged — but asked about the zone that WOULD route rather than always
        // about Settings.SnipeZone, which was the same defect in miniature.
        [Fact]
        public void A_locked_intent_zone_is_not_a_snipe()
        {
            Assert.False(QuestStandDown.IsSniping(FarmZone, intentZoneUnlocked: false, allowZoneFallback: false));
            Assert.Equal(Old(snipeZoneUnlocked: false, targetItopod: false, allowZoneFallback: false),
                         QuestStandDown.IsSniping(FarmZone, intentZoneUnlocked: false, allowZoneFallback: false));
        }

        // ⚠ QuestManager's OWN conservatism, preserved verbatim and deliberately NOT repointed. It is
        // part of neither R10 nor R11 — R11 consults AllowZoneFallback only when the zone is locked,
        // this consults it always — so changing it would change who wins R7, which is a design
        // decision and not this fix's to make.
        [Theory]
        [InlineData(FarmZone, true)]
        [InlineData(HuntZone, true)]
        [InlineData(-1, true)]
        public void Zone_fallback_still_stands_the_snipe_down(int intentZone, bool unlocked)
        {
            Assert.False(QuestStandDown.IsSniping(intentZone, unlocked, allowZoneFallback: true));
            Assert.Equal(Old(unlocked, targetItopod: false, allowZoneFallback: true),
                         QuestStandDown.IsSniping(intentZone, unlocked, allowZoneFallback: true));
        }

        // ⚠ THE TOGGLE CANNOT REACH THE DECISION. This is the C2 answer as a type: the copy is not
        // "repointed at a better source", it is gone — the only input that can say "the ITOPOD is
        // what routes" is a zone number, from the method that owns the chain.
        [Fact]
        public void The_predicate_takes_no_toggle_at_all()
        {
            var ps = typeof(QuestStandDown)
                .GetMethod(nameof(QuestStandDown.IsSniping)).GetParameters();

            Assert.Equal(3, ps.Length);
            Assert.Equal(typeof(int), ps[0].ParameterType);      // the R10 zone, not a flag
            Assert.DoesNotContain(ps, p => p.Name.ToLowerInvariant().Contains("itopod"));
        }

        // ── THE CALL SITES THEMSELVES ─────────────────────────────────────────────────────────────
        //
        // ⚠ WITHOUT THIS, EVERY TEST ABOVE IS GREEN WITH THE FIX REVERTED. QuestManager,
        // OptimizationAdvisor and Main all reach Main.Character and cannot be linked into this
        // assembly (the reason is written into NGUAdvisor.Tests.csproj), so the WIRING — which zone
        // each consumer hands to the decision — is not reachable by a normal unit test. For the
        // digger the compiler covers it: FarmVenue.Decide takes an int, so handing it the old bool
        // expression does not build. For QuestManager nothing stops the old expression being written
        // inline again, and this is the only guard there is, so it reads the source.
        //
        // The path is resolved at COMPILE time from this file's own location, the same idiom
        // CampaignTablesTests uses for the shipped profile trees.
        private static string RepoRoot([CallerFilePath] string here = null)
        {
            // <repo>\tests\NGUAdvisor.Tests\QuestStandDownTests.cs
            var dir = Path.GetDirectoryName(here);
            while (dir != null && !Directory.Exists(Path.Combine(dir, "NGUAdvisor", "Managers")))
                dir = Path.GetDirectoryName(dir);
            return dir;
        }

        private static string Source(string name)
        {
            var path = Path.Combine(RepoRoot(), "NGUAdvisor", "Managers", name);
            Assert.True(File.Exists(path), $"call-site source not found, so nothing was measured: {path}");
            return File.ReadAllText(path);
        }

        // ⚠ CODE ONLY. These files EXPLAIN the deleted copy at length, and a guard that reads the
        // explanation as the thing it forbids fires on the fix itself — which is how this method came
        // to exist. Both call sites are single-line-commented throughout; there are no /* */ blocks.
        private static string CodeOnly(string src)
            => string.Join("\n", src.Split('\n')
                .Select(l => { int i = l.IndexOf("//"); return i < 0 ? l : l.Substring(0, i); }));

        // Layer 1's fields must not be back inside the quest decision. Scoped to the method, because
        // Settings.SnipeZone is legitimately read elsewhere in a file this size.
        [Fact]
        public void QuestManager_asks_the_resolver_and_holds_no_copy_of_the_R10_rule()
        {
            var src = Source("QuestManager.cs");
            int start = src.IndexOf("private static void UpdateShouldQuest()");
            Assert.True(start > 0, "UpdateShouldQuest not found, so nothing was measured");
            int end = src.IndexOf("\n        // One butter attempt", start);
            Assert.True(end > start, "end of UpdateShouldQuest not found, so nothing was measured");
            var body = CodeOnly(src.Substring(start, end - start));

            Assert.Contains("Main.ResolveIntentZone()", body);
            Assert.Contains("QuestStandDown.IsSniping(", body);
            // The deleted copy, and the field it used to be paired with.
            Assert.DoesNotContain("AdventureTargetITOPOD", body);
            Assert.DoesNotContain("IsZoneUnlocked(Settings.SnipeZone)", body);
        }

        // The digger venue must be handed the LIVE zone. The signature already refuses a bool; this
        // refuses the other way round it — synthesising a zone number back out of the toggles.
        [Fact]
        public void The_digger_venue_is_handed_the_live_zone()
        {
            var src = CodeOnly(Source("OptimizationAdvisor.cs"));
            Assert.Contains("currentZone = c.adventure.zone", src);
            Assert.Contains("FarmVenue.DropFarmActive, currentZone)", src);
            Assert.DoesNotContain("Main.Settings.AdventureTargetITOPOD", src);
        }

        // ⚠ R10's PRECEDENCE IS UNCHANGED, AND THAT IS THE POINT OF THE WHOLE FIX (C3: assert
        // routing, do not change it). Both consumers were repointed at Main.ResolveIntentZone / the
        // live zone WITHOUT re-ranking anything: R10's rows are still gear hunt > advisor drop farm
        // > Target ITOPOD > Settings.SnipeZone, in that order. C's own edit to the method was only
        // widening it from private to internal so a consumer could ask it.
        //
        // ⚠ WHY THE EXPECTED SEQUENCE CHANGED, AND WHY THAT IS NOT A REGRESSION. This fixture was
        // written by 4cba9d7 with a FOUR-row chain ending in the ternary
        //     return Settings.AdventureTargetITOPOD ? 1000 : Settings.SnipeZone;
        // fix/zone-contention-2 expanded that ternary into a three-branch ladder, because a ternary
        // cannot report WHICH zone Target ITOPOD discarded — it never names one — and naming it is
        // audit/40 §3 item 3's surviving half. Same two outcomes, same position in the chain, same
        // precedence; the ladder only adds the `discardedByItopod` assignment on the way past.
        //
        // So the test caught a real change and was right to. What it PINS is unchanged: the rows of
        // R10 and their ORDER, read out of the source, and that the rule has exactly one copy. What
        // it can no longer claim is that the method is byte-for-byte what C left — hence the rename
        // from The_R10_chain_in_Main_is_untouched, which would now assert something false.
        //
        // ⚠ THE EXTRACTION SPANS BOTH OVERLOADS, ON PURPOSE. `start` anchors on the parameterless
        // form, which is now a one-line expression body with no braces, so the substring runs on
        // into the `out`-overload's body and stops at ITS closing brace. That is the body carrying
        // the chain. If anyone gives the parameterless form a braced body the substring stops early,
        // rows becomes a single `return ResolveIntentZone(out _);`, and this FAILS LOUDLY rather
        // than silently measuring nothing.
        [Fact]
        public void The_R10_chain_in_Main_keeps_its_precedence()
        {
            var path = Path.Combine(RepoRoot(), "NGUAdvisor", "Main.cs");
            Assert.True(File.Exists(path), $"resolver source not found, so nothing was measured: {path}");
            var src = File.ReadAllText(path);

            int start = src.IndexOf("internal static int ResolveIntentZone()");
            Assert.True(start > 0, "ResolveIntentZone not found, so nothing was measured");
            var body = src.Substring(start, src.IndexOf("\n        }", start) - start);

            var rows = body.Split('\n').Where(l => l.TrimStart().StartsWith("if (") || l.TrimStart().StartsWith("return "))
                           .Select(l => l.Trim()).ToArray();
            Assert.Equal(new[]
            {
                "if (!Settings.CombatEnabled) return -1;",
                "if (GearHunter.Active && GearHunter.ZoneReachable()) return Settings.GearHuntZone;",
                "if (Settings.AdvisorZones && Managers.FarmVenue.DropFarmActive",
                // The expanded ternary — Target ITOPOD still sits here, still beaten by the two rows
                // above it, and now names what it discarded before returning the ITOPOD.
                "if (!Settings.AdventureTargetITOPOD) return Settings.SnipeZone;",
                "if (Settings.SnipeZone >= 0 && Settings.SnipeZone < 1000) discardedByItopod = Settings.SnipeZone;",
                "return 1000;"
            }, rows);
        }
    }
}
