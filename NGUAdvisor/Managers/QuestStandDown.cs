namespace NGUAdvisor.Managers
{
    // ── WHETHER A COMMITTED ZONE SNIPE STANDS QUESTING DOWN (audit/40 §3 item 7) ──────────────────
    //
    // audit/40 §0: there are TWO chains. Layer 1 WRITES Settings.SnipeZone; layer 2 (Main.SnipeZone)
    // decides the zone actually adventured, and "most of what is written in layer 1 is discarded,
    // silently, by layer 2". §3 item 7 names this call site: it read the layer-1 INTENT field, so
    // "whenever layer 2 overrides, [it is] reasoning about a zone the character is not in".
    //
    // ⚠ THIS CONSUMER LEGITIMATELY WANTS THE INTENT, NOT THE ROUTED ZONE — and that is forced, not a
    // concession. `shouldQuest` feeds QuestManager.IsQuesting(), which IS audit/40's R7, and R7 sits
    // ABOVE R10 in the same chain. Handed the zone that actually routed, this predicate would read
    // its own output: quests win R7 -> the character stands in the quest zone (real, unlocked,
    // < 1000) -> "sniping" -> quests stand down -> routing returns to the ITOPOD -> "not sniping" ->
    // quests win again. A self-latching oscillator. The right question is the one
    // Main.ResolveIntentZone answers — what R10 WOULD route if it were reached — which is by
    // construction independent of R7. The digger venue law (FarmVenue) is the opposite case and
    // takes the live zone; the two consumers of §3 item 7 do not want the same fact.
    //
    // ── WHAT WAS DELETED ──────────────────────────────────────────────────────────────────────────
    // The expression was, at QuestManager.UpdateShouldQuest:
    //
    //     IsZoneUnlocked(Settings.SnipeZone) && !Settings.AdventureTargetITOPOD && !AllowZoneFallback
    //
    // `!Settings.AdventureTargetITOPOD` was a SECOND COPY of one row of the R10 chain, and it had
    // drifted from the row that decides it twice — always the same way, because the copy knew only
    // the toggle and never the two rows that outrank it:
    //
    //   * GEAR HUNT (R10 row 1), fixed in Main for exactly this — "user-reported: Target ITOPOD
    //     silently overrode the hunted stage". With the hunt on and the toggle on, R10 routes
    //     GearHuntZone; the copy answered "not sniping", so quests took R7 and pre-empted the hunt.
    //   * THE ADVISOR'S OWN DROP FARM (R10 row 2), added at 271f5f8 / audit/40 §6.1 — the fix for the
    //     run that "never left the ITOPOD". With the farm routing zone 20 and the toggle still on,
    //     R10 routes zone 20 and announces it; the copy answered "not sniping", quests took R7, and
    //     271f5f8 was undone one row ABOVE the row it fixed.
    //
    // So the copy is gone and this predicate takes no toggle at all: the only way to answer "is the
    // ITOPOD what routes" is a zone number, from the one method that owns the chain. That also fixes
    // the case the toggle never covered — the boost farm writes Settings.SnipeZone = 1000 whenever
    // there is no boost demand (AdvisorApply.cs:1157) and the phase machine's ITOPOD phase writes
    // 1000 too, and IsZoneUnlocked(1000) is itopodOn, so the old expression called a character
    // standing in the ITOPOD "sniping" and refused to quest there. The comment above the old line
    // already said the intended rule — "not farming ITOPOD" — and this is the first time the code
    // has said the same thing.
    //
    // ── TWO TERMS ARE KEPT VERBATIM, AND NEITHER IS R10'S ─────────────────────────────────────────
    //   * the unlock test is R11's own (Main.cs, the tempZone rewrite) — but asked about the zone
    //     that WOULD route rather than always about Settings.SnipeZone, which was the same defect in
    //     miniature. -1 keeps reading exactly as before: it is the SavedSettings sentinel
    //     (SavedSettings.cs:13) and CombatManager.IsZoneUnlocked returns true for it (the Safe Zone),
    //     which is also where R10 would send the character, so the two agree.
    //   * !allowZoneFallback is QuestManager's OWN conservatism and is part of NEITHER R10 nor R11 —
    //     R11 consults AllowZoneFallback only when the zone is locked, this consults it always.
    //     Whether that is right is a separate question and a design decision; it is preserved
    //     unchanged so this change is exactly one thing.
    public static class QuestStandDown
    {
        // intentZone         : Main.ResolveIntentZone() — R10's answer, gear hunt > advisor drop
        //                      farm > Target ITOPOD > Settings.SnipeZone.
        // intentZoneUnlocked : CombatManager.IsZoneUnlocked(intentZone) — R11's test, asked about
        //                      R10's zone rather than about Settings.SnipeZone.
        // allowZoneFallback  : Settings.AllowZoneFallback, preserved verbatim (see above).
        public static bool IsSniping(int intentZone, bool intentZoneUnlocked, bool allowZoneFallback)
            => intentZoneUnlocked
            && intentZone < ZonePhase.ItopodZone
            && !allowZoneFallback;
    }
}
