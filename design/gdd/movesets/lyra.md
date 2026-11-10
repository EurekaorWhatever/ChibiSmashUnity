# Lyra (Magic): moveset mapping

Sheet: `design/art/character-sheets/lyra-moveset.png` (25 moves incl. 3 defensive). Rules: `README.md`. Numbers:
`movesets.csv` (21 new rows). d_jab1 = 2.2. Reach 1400. **HalfKnockback applies to every Lyra hit.** Existing kit
kept: bolt light combo, big bolt heavy, S1 Homing Fireball, S2 Summon Minions, S3 Thunder Cloud, ult Fire Ring.

| # | Sheet move | Input | G/A | S/A/R | Dmg % | Mult | Kb tier | CSV id / note |
|---|---|---|---|---|---|---|---|---|
| 1 | Jab 1-2-3 | L | G | 2/2/17, 2/2/19, 3/3/28 | 2.2/2.0/4.0 | existing | L, L, M | **existing bolt combo** |
| 2 | Forward Tilt | L+F | G | 5/5/25 | 7.0 | 1780 | M -> | lyra_ftilt |
| 3 | Up Tilt | L+U | G | 6/6/27 | 6.5 | 1720 | M ^ | lyra_utilt |
| 4 | Down Tilt | L+D | G | 5/5/23 | 5.8 | 1620 | L -> | lyra_dtilt |
| 5 | Dash Attack | L@dash | G | 7/6/32 | 9.0 | 2020 | M -> | lyra_dash |
| 6 | Forward Smash | H+F | G | 16/6/40 | 16.0 | 2700 | H -> | lyra_fsmash, charge |
| 7 | Up Smash | H+U | G | 15/7/42 | 15.0 | 2610 | H ^ | lyra_usmash, charge |
| 8 | Down Smash | H+D | G | 17/8/44 | 15.5 | 2650 | H both sides | lyra_dsmash, charge |
| 9 | Neutral Air | L | A | 4/5/28 | 6.0 | 1650 | L | lyra_nair, circle r 850 |
| 10 | Forward Air | L+F | A | 6/5/30 | 8.0 | 1910 | M -> | lyra_fair |
| 11 | Back Air | L+BK | A | 5/5/29 | 7.5 | 1850 | M <- | lyra_bair |
| 12 | Up Air | L+U | A | 5/5/28 | 7.0 | 1780 | M ^ | lyra_uair |
| 13 | Down Air | L+D | A | 7/6/36 | 9.0 | 2020 | Spike (H) | lyra_dair |
| 14 | Dodge / Air Dodge | Dash | GA | - | - | - | - | **existing Dash** (sheet 8/10 invuln vs 6) |
| 15 | Parry (Perfect Guard) | Shield | GA | window 3 | - | - | - | **existing shield perfect parry** (window 11) |
| 16 | Guard / Shield | Shield | GA | hold | - | - | - | **existing shield** |
| 17 | Arcane Shot | B | GA | 10/4/32 | 7.0 | 1780 | M -> | lyra_nspec, bolt 240000 sub/tick, life 60 |
| 18 | Spell Lance | B+F | GA | 12/6/36 | 11.0 | 2240 | M -> | lyra_sspec, long thrust box 2600 |
| 19 | Glyph Zone | B+D | G | 14/120 field/28 | 2.0 per pulse | 390 x6 | VL | lyra_dspec, periodic zone + slow |
| 20 | Blink Ascension | B+U | GA | 8/8/28 | 6.0 | 1650 | M ^ | lyra_uspec, teleport 2000, aironce |
| 21 | Starfall Trap | S1+D | G | 18/2/30 est | 10 est | 2130 | M | lyra_starfall, timed zone 2000 ahead, delay 60 |
| 22 | Astral Lift | S1+U | G | 16/10/30 est | 8 est | 1910 | H ^ | lyra_lift, updraft column |
| 23 | Prism Reflection | S2+D | GA | 12/24/33 | 3 est | 1170 | L | lyra_prism, reflect x1.25, negates melee Mult <= 1200 |
| 24 | Comet Dive | B+D | A | 14/40 max (sheet 6)/42 | 12 est | 2340 | Spike (M) | lyra_comet, diagonal dive + landhit |
| 25 | Blink Ascension (Recovery+) | S2+U | GA | 18/20/30 | 8 est | 1910 | M ^ | lyra_blinkplus (renamed Blink Ascension+), teleport 3500 |

S3+U / S3+D are empty: they fall back to Thunder Cloud.

## Notes
- **Illegible values**: the "Unique / Extended Skills" panel text is garbled. Starfall, Astral Lift, Prism,
  Comet Dive and Blink+ damage, and Starfall/Astral recovery, are estimates (`est` flag). Knockback words were legible.
- **Comet Dive** has no sheet input; it takes air down+B, matching the other dives (Bruna Meteor Anvil, Ren Falling
  Pin, Taro Falling Spear). Dive at (150, -300) milli/tick; landing burst 1170, M ^, 2400 wide.
- **Glyph Zone**: 3000 wide, 1000 tall, centred on Lyra; pulses at zone ticks 0, 20, ..., 100 (6 x 390 = 12 % sheet
  total); slow refreshed to 30 ticks every tick the enemy stands in it. One zone per Lyra; recasting replaces it.
- **Starfall Trap**: rune centred 2000 ahead of Lyra, live 60 ticks after spawn for 12 ticks, 2200 tall
  (stars hit airborne foes above the rune too).
- **Prism Reflection**: projectiles touching the prism box (1200 ahead, 1600 tall) are reflected (owner swap,
  velocity reversed, Mult x1.25, life >= 30). Weak melee (Mult <= 1200: lights, tilts of most classes before
  Patience) from the front is negated and the attacker gets the existing parry-skill reflect stun (30).
- **Blink Ascension (both)**: direction captured at the press (Skill+Up always includes up; B+U too), diagonal
  scaled 707/1000; teleport cannot enter the blast margin (2000) or the floor.
- **Defensive panel**: the sheet's dodge/parry/guard are the existing Dash and Shield. Sheet values (8/10 invuln,
  3-tick perfect window) differ from the live ones (6, 11) and are NOT applied (they would change every class).
