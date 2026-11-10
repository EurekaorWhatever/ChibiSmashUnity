# Kaia (Melee): moveset mapping

Sheet: `design/art/character-sheets/kaia-moveset.png` (24 moves). Rules and units: `README.md`. Numbers:
`movesets.csv` (22 new rows). d_jab1 = 2.0. Reach 1650. Existing kit kept: light combo, heavy, S1 Dash & Thrust,
S2 Parry, S3 Thrust Barrage, ult Sword Aura.

| # | Sheet move | Input | G/A | S/A/R | Dmg % | Mult | Kb tier | CSV id / note |
|---|---|---|---|---|---|---|---|---|
| 1 | Jab 1-2-3 | L | G | 2/2/19 | 2.0/1.8/4.0 | existing 1000 x3 + finisher 1500 | VL -> L | **existing light combo** (see diffs) |
| 2 | Forward Tilt | L+F | G | 5/5/24 | 7.0 | 1870 | L -> | kaia_ftilt |
| 3 | Up Tilt | L+U | G | 6/4/22 | 6.5 | 1800 | L ^ | kaia_utilt |
| 4 | Down Tilt | L+D | G | 5/4/20 | 6.0 | 1730 | L -> | kaia_dtilt |
| 5 | Dash Attack | L@dash | G | 8/6/28 | 9.0 | 2120 | M -> | kaia_dash, self_vx 150 |
| 6 | Forward Smash | H+F | G | 14/6/36 | 16.0 | 2830 | H -> | kaia_fsmash, charge |
| 7 | Up Smash | H+U | G | 16/5/38 | 15.0 | 2740 | H ^ | kaia_usmash, charge |
| 8 | Down Smash | H+D | G | 15/6/40 | 14.0 | 2650 | M <-> | kaia_dsmash, charge |
| 9 | Low Sweep | L+D@dash | G | 7/5/26 | 6.0 | 1730 | Trip | kaia_lowsweep, slide self_vx 120 |
| 10 | Charged Cross Cut | S3+U | G | 18/6/42 | 18.0 (full charge) | 2140 -> 2996 | H -> | kaia_ccc, cd 300 |
| 11 | Neutral Air | L | A | 5/4/24 | 6.0 | 1730 | L | kaia_nair, circle r 900 |
| 12 | Forward Air | L+F | A | 6/4/26 | 8.0 | 2000 | M -> | kaia_fair |
| 13 | Back Air | L+BK | A | 6/5/28 | 9.0 | 2120 | M <- | kaia_bair |
| 14 | Up Air | L+U | A | 7/4/26 | 7.5 | 1940 | M ^ | kaia_uair |
| 15 | Down Air | L+D | A | 9/5/30 | 10.0 | 2240 | Spike (H) | kaia_dair |
| 16 | Blade Flurry | B | GA | 8/10/30 | 1.8 x4 | 470 x4 | link, last L | kaia_nspec, multi:4:3 |
| 17 | Shadow Lunge | B+F | GA | 10/6/34 | 12.0 | 2450 | M -> | kaia_sspec, self_vx 200, level, aironce |
| 18 | Skyhook | B+U | GA | 10/6/36 | 10.0 | 2240 | H ^ | kaia_uspec, self_vy 250, aironce |
| 19 | Blade Trap | B+D | G | 12/8/38 | 8.0 | 2000 | L, stun 24 (0.4 s) | kaia_dspec, contact zone 240 ticks |
| 20 | Parry Riposte | S2+U | GA | 2/6/24 | 12.0 | 2450 | M -> | kaia_riposte, counter, cd 240 |
| 21 | Blade Reversal | S2+D | G | 4/6/28 | 14.0 | 2650 | H -> | kaia_reversal, armor ticks 4-10, cd 300 |
| 22 | Air Dodge | Dash (air) | A | - | - | - | - | **existing Dash** (no new row) |
| 23 | Wall Jump / Wall Slash | S1+U | A | 4/4/20 | 6.0 | 1730 | L -> | kaia_wallkick: **substitute** |
| 24 | Ledge Get-up / Roll | S1+D | G | 2/14/10 | - | 0 | - | kaia_roll: **substitute** |

S3+D is empty: it falls back to Thrust Barrage. Air B+D: ignored (Blade Trap is ground only).

## Notes
- **Jab diffs**: sheet is 3 hits at 2/2/19 with 2.0/1.8/4.0; the existing combo is 4 hits (PatternLength 4), base
  5/1/8 (faster with the +15 % passive), Mult 1000/1000/1000/1500, finisher kb (105000, 105000, 27). Kept as is
  (CH-3). Only the view/SFX should use the sheet's "Swish! Swish! Shraak!".
- **Low Sweep** sits on dash + down + Light ("Slide" on the sheet); down tilt stays on standing down + Light.
- **Charged Cross Cut** is the only move whose sheet damage is read as the fully charged value (18 % = 3000 cap);
  uncharged 2140 (9.2 % equivalent). Charge button = Skill3 held.
- **Air Dodge** duplicates the existing Dash, which already works in the air (8 ticks, 6 invuln, horizontal only;
  sheet says 2 startup / 6 invuln / 18 recovery, directional). Not added: a second dodge would compete with Dash and
  the Skill+U/D slot would fix its direction. Directional air dodge is a possible later change to Dash itself.
- **Wall Jump / Wall Slash -> Air Kick-off** (no walls in the sim): air only, kicks off an imaginary foothold:
  self_vy 150 at tick 0, drifts back (self_vx -60), slashes forward, `restorejump` gives back the air jump,
  `aironce`. Sheet active frames missing (4 assumed, `est`).
- **Ledge Get-up / Roll -> Tech Roll** (no ledges): ground roll, direction = held X else backward, 140 milli/tick
  for 16 ticks (2240), invuln ticks 2-13 (12, vs Dash 6), ends facing back toward the start (forward roll =
  cross-up). cd 60 so it does not replace Dash (cd 33).
- **Parry Riposte vs existing Parry skill (S2)**: Parry reflects damage over 21 ticks; Riposte is a 6-tick counter
  that strikes back. Different risk/reward, both kept.
