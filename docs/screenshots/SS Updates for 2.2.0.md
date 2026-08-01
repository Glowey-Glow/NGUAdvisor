# Screenshots to retake for 2.2.0 — "Loadouts"

Every shot below changed visually in 2.2.0. Filenames are the ones `readme.MD` already references, so
replacing the file is enough — no README edit needed unless a **NEW** row says otherwise.

Take them at **1346 × 1184** (the reference viewport) with live data, not a fresh save: several of these
readouts are empty or say "measuring" until the advisor has run for a few minutes.

---

## Must retake — the panel changed

| File | What is different, and what the shot needs to show |
|---|---|
| `loadouts.png` | **Rebuilt.** Needs the new **Main / idle** block at the top (objective picker + "Equip the best set now" + the optimiser's picks), and at least one mode showing its inline arming switches with the status line under them. Ideally capture a mode that is **not armed**, so the "Not armed — “Swap gear for titans” is off" sentence is visible; that sentence is the whole point of the release. |
| `loadouts-quest-shockwave.png` | Same page further down. Show the **Fill from objective** button and a filled list with named items (`[188] "Edgy Helmet"`), plus the **Last gear swap** block underneath. Best captured shortly after a real swap so it reads "N of N swapped in · N slots kept". |
| `boosts.png` | **Priority Boosts** now shows the time-to-cap chips, a per-row "at cap in 2h 10m", the **Boosting** chip naming the current item, and the drag grips. Put 3–4 items in the priority list first — with one item there is nothing to show. Also note the boost **type** priority list lost its Add row and now drags. |
| `overview.png` | **Current stage** now carries the auto profile's segment plan (`TM HOUR › AT HOUR › RECOVERY › NGU MARATHON`, current step accented, run clock). Take it with **Auto profile ON** so the chain is visible; a manual profile shows "Manual profile" instead, which is worth a second shot if you want both. |
| `yggdrasil.png` | Orchard tiles now carry the **poop markers** — filled brown for "poop belongs here", hollow for "poop is here but something else is better" — plus the summary line naming the targets. Take it while at least one fruit is mis-placed so both marker states appear. |
| `diggers.png` | Breakpoint editing is now a **drag list with named slots** and an **Active** count field, not a comma-separated text box. Capture the Profile Editor with a digger breakpoint open. |
| `beards.png` | Same drag list (no count field — a beard list's length is its own count). Capture with a breakpoint open. |
| `settings.png` | The gear row was relabelled and split: **Gear (let the advisor equip)**, **Gear: advisor picks the set**, **Gear: re-check when new gear drops**. The old single "Advisor gear refresh" row is gone. |
| `titans.png` | Now carries the **Gear for this system** chip row (source + will/won't swap + Edit →). |
| `gold-moneypit-advisor.png` | Same chip row. |
| `quests-advisor.png` | Same chip row. |
| `challenges.png` | Completed campaign blocks now **fold** behind a "Completed campaigns (N)" header. Take it with at least one block complete, folded — that is the default state. |

## Worth retaking — smaller visual change

| File | Why |
|---|---|
| `gear.png` | The timeline summary is unchanged, but the view now sits alongside the Main-gear controls on Loadouts; check it still reads correctly next to them. |
| `inventory.png` | Item-ID input boxes are wider (they used to truncate to "ite"). Only worth retaking if the old shot shows a truncated placeholder. |

## No change — leave as-is

`adventure-itopod.png` · `blood.png` · `cards.png` · `consumables.png` · `energy-magic-r3.png` ·
`exp.png` · `gold-moneypit-manual.png` · `ngu.png` · `perks-quirks.png` · `quests-manual.png` ·
`rebirth.png` · `wandoos.png` · `wishes.png` · `injected.png`

---

## Two things that will make the shots wrong if you skip them

1. **Press F5 (or restart) before shooting.** The companion and the advisor version independently. A
   companion showing 2.2.0's page against a 2.1.x advisor renders empty chips and a "needs a newer
   advisor" toast — which would be captured as if it were the feature.
2. **Give it a few minutes of running.** The boost ETA reads "measuring the rate…" until the advisor
   has two 60-second samples, and the growth strip needs the same. Those are correct states, but they
   are not the ones to advertise.
