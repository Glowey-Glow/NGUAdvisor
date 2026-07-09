# NGU Idle — Strategy Knowledge Base

Distilled from the community guide (https://sayolove.github.io/ngu-guide) plus the Boost Almanac and
PP/EXP-income sheets. This is the **source of truth** for authoring stage/goal profiles, choosing gear
optimizer objectives, and driving the status HUD's "what you're working toward" targets.

External references:
- Guide: https://sayolove.github.io/ngu-guide/en/intro/
- Gear Optimizer tool: https://gmiclotte.github.io/gear-optimizer/#/ (our native optimizer reimplements its scoring)
- Boost Almanac (Google Sheet): id `1UyOPvZ_Gen02xfJZuPGnOQNVETxoQXlYJ9ObHmmDWRI`
- PP/EXP income (Google Sheet): id `1v9yA1Cv8W7OS1Vo_3LU3rHVBsPXaFRzP4T7Di2ZT3YY` (gid 1550539240)
- Wiki: https://ngu-idle.fandom.com/wiki/NGU_Idle_Wiki

> Note: these Google Sheets are not machine-fetchable here; mine them opportunistically for exact boost
> values and rebirth-length breakpoints during implementation.

---

## Progression: 8 chapters

The game is organized as 8 chapters gated primarily by **rebirth difficulty** (Normal → Evil → Sadistic)
and **Titan** kills (T1–T12). Each chapter has characteristic energy/magic allocation, fruit order,
challenge sequence, and loadout goals. Detection heuristic in `Managers/StageDetector.cs` uses
`Character.settings.rebirthDifficulty` + `Character.highestBoss` (boss thresholds are approximate — see the
table at the bottom; refine with titan-version reads if needed).

### Ch.1 Start-HSB (Normal, pre-T1)
- 30-minute rebirths as often as possible (farm boss EXP, cut skill costs). Extend to 1h after Boss 58.
- Basic Training: Attack (auto-syncs Block); progress all tiers to max speed (~25 min) to unlock Advanced Training.
- Energy spend order: Total ESpeed 25 → Base EBars 4 → Total ESpeed 50 → Base ECap 300k; then ratio **1 : 37.5k : 1** (Power:Cap:Bars).
- Augments unlock Boss 17: buy the most expensive augment finishable in 30 min.
- Adventure sets: Tutorial → Sewers (B7) → Forest (B17) → Cave of Many Things (B37) → The Sky (B48) → HSB (B58).
- Time Machine unlocks Boss 30 (wear Gold Drops gear in furthest zone to set gold, don't level yet).
- Blood Magic Boss 37 (Poke Yourself / Blood Number Boost).
- ITOPOD after Pissed Off Key: climb to floor 100 (~16 PP). Perks: first 5 Newbie (0–4) → 2× Instant AT Levels (18) → alternate Gen Energy Power I (6) / Gen Energy Cap I (8).
- **Milestone:** manually kill T1 (Gordon Ramsay Bolton) ~1350/1350 P/T (2300/2100 for idle).

### Ch.2 T1-Mega (Normal, T1→T4)
- Heavy Time Machine + augment upgrades. Progress adventure zones. Fruit: FoG 2 → FoPa 1 → FoA 1 → Pom 1 → FoPa 2 → FoG 4 → Pom 3 → FoG 10.
- After Boss 100: start T4 puzzle; do a micro challenge-block (5 basic + 1 24h).
- **Milestone:** long rebirth to defeat T4, farm Mega gear.

### Ch.3 T4-BAE (Normal, beards)
- Switch to 24h rebirths for beard farming (BEARd > Neckbeard > Beard Cage).
- Resource split: **energy half ADV / half DC; magic → YGG**.
- Challenge order: T4 → Mini-CBlock → Beardverse → T5 → CBlock1 → BDW/BAE.
- Fruit: FoG 10 → Pom 5 → (FoPa1 + FoA1 + FoK1 + FoL1) → Pom 10 → FoL 5.

### Ch.4 T6 (Normal, mid scaling)
- Long rebirth until T6 weapon drop. Energy:Magic ratio 3:1, then 2:1 after CBlock2.
- Manual major quests; idle minor quests. Farm chocolate gear at ~1.3T power.

### Ch.5 Evil-IDP (Evil begins)
- Start with a single basic challenge (free entry). Focus AT early for EV exploder (gold).
- Push to Boss 125 to unlock T7. NGU: normal for first 23h, evil NGUs the last hour (increase evil hours per T7 kill).
- Don't buy R3 until post-T8 (optional earlier if UI lag).

### Ch.6 T8-JRPG (Evil, R3 sink)
- Start buying R3 upon T8. Daycare-focused loadout for looty/pendant progress.
- Sequence: snipe Typo set → CBlock4 → Hackday 1 → buy E/M to 3M/1M power → resume R3 → evil NGUs after blackbeard.
- Endgame: farm Typo → snipe Fad → max Typo → farm Fad → snipe JRPG → max Fad → long RB to T9.

### Ch.7 T9 (Evil/late)
- 24 manual kills for AK. Cards: tag Adv/Hack/Wish, cast only Meh+, yeet rest.
- Energy:Magic 6M/2M → 24M/8M; continue R3.
- Sequence: max set → nuts → BEUC → BEUC CBlock → BEUC Hackday (250+ R3 power) → snipe Rad at v3 → long RB to Rad set + 50+ soul points.
- **Milestone:** kill v4 24×, reach Boss 300.

### Ch.8 Sadistic (endgame)
- Entry challenge: all Basics, No Aug, 100 Lvl, No Equip, No RB, first two Trolls.
- Buy Fertilizer; 23h rebirths with Muffins (aim 2 rebirths/muffin). Hackday whenever ≥1.3× adventure multiplier; alternate with snugday.

### Rules of thumb
1. Scale rebirth length to highest fruit tier once T2 unlocks.
2. Post-T4 consolidate: 1h Time Machine, 1h Advanced Training, 22h NGUs.
3. Snipe upcoming adventure sets before maxing current ones when zones are close.
4. Energy:Magic ratio transitions: 1:37.5k:1 early → 3:1 T6 → 2:1 post-CBlock2 → 6M:2M T9 → 24M:8M late T9.
5. Delay R3 until T8; evil NGUs only after enough progress; prioritize manual quests early in rebirths.

---

## Gear Optimizer objectives (per goal)

The guide's GO advice: **early game optimize "Power"; mid/late run multiple loadouts, each focused on ONE
priority, plus a couple Respawn items** (Respawn matters more as systems scale). Our native optimizer
(`Managers/GearObjectives.cs`) exposes these objectives; each maps to game stat spec(s):

| Goal | Objective(s) | Notes |
|---|---|---|
| Adventure push | Power / Toughness / Adventure | Ch.1–2 primary |
| Respawn | Respawn | always run ≥1 respawn item late (TopRespawn toggle) |
| Time Machine | Time Machine (E/M cap+power) | 1h/rebirth post-T4 |
| Advanced Training | Advanced Training (AT Speed) | 1h/rebirth post-T4 |
| Augments | Augments (Aug Speed) | |
| Beards | Beards (Beard Speed) | Ch.3 farming |
| NGUs | Energy NGU / Magic NGU / NGUs | 22h/rebirth post-T4 |
| Drops / Gold | Drop Chance / Gold Drops | Adv/DC split |
| Yggdrasil | Yggdrasil (Seeds>EXP>Gold>AP) | harvest set |
| EXP | Experience | |
| Wishes / Hacks | Wishes / Hacks | later chapters |
| Daycare | Daycare | Ch.6 looty/pendant |
| Cooking | Cooking | |

NGU allocation rule (from GO NGU tab): run an NGU while gaining **>1.05×/hr**; run **Respawn** when
**<0.95×/hr**; otherwise split **Adventure/Drop Chance** and **Yggdrasil/EXP**.

**PP has no gear spec** (perk points come from rebirths, not gear) — it cannot be a gear objective.

### NGU lists (match injector NGU tokens)
- Energy NGUs: Augments, Wandoos, Respawn, Gold, Adventure-a, Power-a, Drop Chance, Magic-NGU, PP.
- Magic NGUs: Yggdrasil, Exp, Power-b, Number, Time Machine, Energy-NGU, Adventure-b.

---

## Stage detection map (heuristic — `Managers/StageDetector.cs`)

| Difficulty | Highest boss | Chapter |
|---|---|---|
| Normal | < 58 | Ch.1 Start-HSB |
| Normal | 58–99 | Ch.2 T1-Mega |
| Normal | 100–128 | Ch.3 T4-BAE |
| Normal | ≥ 129 | Ch.4 T6 |
| Evil | < 150 | Ch.5 Evil-IDP |
| Evil | 150–249 | Ch.6 T8-JRPG |
| Evil | ≥ 250 | Ch.7 T9 |
| Sadistic | any | Ch.8 Sadistic |

Boss thresholds are approximate placeholders — tune against real play / titan-version reads. Detection is a
**hint only**; profile switches are always user-confirmed.

---

## Existing sample profile library

`NGU/sampleprofiles/` is already grouped by difficulty + goal and is the base for the curated stage/goal
presets (Phase 1): `Normal/` (24hr, 24hr-AdvDC, 24hr-PAWG, LRB-*, CBlock*, BeastRB, Miniblock*),
`Evil/` (EvilStart, 24hr-*Evil, CBlock*, HackDay, RAD*, LRB-*), `Sadistic/`, plus top-level cblock*/24hr.
Phase 1 will re-express these with objective-based gear timelines so loadouts auto-optimize.

## Guide spend orders (sayolove ngu-guide, ch2-5) — implemented in Managers/SpendPlanner.cs

**ITOPOD perks (PP), Normal:** Newbie perks -> Generic Energy Power/Cap I -> Bonus Titan EXP x1 (online-AK EXP bug) -> What a Crappy Perk -> A Digger Slot -> Boosted Boosts I (10) -> Faster NGU Energy (until CBlock1) -> post-CBlock1 Ygg block: I want your seeds / First Harvest / FoK sucks 1+2 -> (ch4) Generic Magic Power/Cap I, Faster NGU Magic, BB1 max, E/M Bar I, AT/Beard/TM Level Banks I+II, Inventory I+II, Wandoos Lover, Bonus Boss EXP, BB2.
**Evil (ch5, PARTIAL - verify names in-game at Evil):** Beard/AT Banks 3+4 -> Fib 1 -> EM Pow/Cap/NGU 2 to L10 -> Fib 3 -> Welcome to Evil (~boss 120) -> EM until CBlock3 -> Banks 5 -> EM until easy normal NGU BB (2:2:1) -> Fib 34 -> finish EM -> Energy Bars 2 -> Adventure through T8 -> Magic Bars 2 when cheap.

**Beast quirks (QP):** ch4: Baby's First Quirk: Adventure (300 QP, +25% adv). Evil ch5: finish EM Pow/Cap 1 (Baby's First set) -> Beard/AT Banks 1 -> Beasted Boosts 2 -> Adventure Quirk during T8 LRB.

**Yggdrasil tiers (seeds):** ch3: FoG 10 -> Pom 5 -> FoK 1 + FoL 1 -> Pom 10 -> FoL 5. Post-TC3 (cap 24): FoG 24 -> Pom 24 -> FoK 24 -> FoL 24 -> FoPa/FoA 24 -> FoAP 24 -> FoPb/FoN 24 -> FoR 24. Eat/harvest: harvest FoG early (seeds), eat when diggers capped; harvest fruits before FoL until L12+ then eat; poop Pom always, others at max. Evil ch5: FoR 24 -> GuffA 12 -> GuffB 1 -> Quirk 4; later Melon 24 -> Quirk 12 -> GuffA 24 -> Quirk 24 -> PowerD 24 -> GuffB 24 (Melon 8 out-seeds Pom 24; poop Melon 8+).

## CORRECTION (EXP ratios — verified against guide verbatim + Blaze Ratioz)
"Split EXP evenly into energy/magic (3:1 E:M base)" means: EXP split is 1:1 (EVEN); the 3:1 E:M is the
resulting PURCHASED-VALUE ratio (magic units cost exactly 3x energy: pow 450 vs 150 EXP/unit, cap 3 vs 1
per 250, bars 240 vs 80). 5:160k:4 pow:cap:bars is likewise a UNIT/value ratio. Blaze's Ratioz tab
compares stat VALUES, same convention. Post-T6v2 the guide targets a 2:1 VALUE ratio (EXP 2:3 toward
magic) until T6v4 accs + BB, then back to 3:1 values into Evil. R3 joins the ratio at Evil (E:M:R3, TBD).
