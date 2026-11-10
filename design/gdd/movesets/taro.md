# Taro (Polearm): moveset mapping

Sheet: `design/art/character-sheets/taro-moveset.png` (21 moves). Rules: `README.md`. Numbers: `movesets.csv`
(20 new rows). d_jab1 = 2.2. Reach 2300. Existing kit kept: light combo, heavy, S1 Leap Slam, S2 Spin Guard,
S3 Throw Spear, ult Kick. `SpearOut` blocks all of Taro's Light and Heavy moves (old and new), not B or skills.

| # | Sheet move | Input | G/A | S/A/R | Dmg % | Mult | Kb tier | CSV id / note |
|---|---|---|---|---|---|---|---|---|
| 1 | Jab 1-2-3 | L | G | 2/2/19 | 2.2/1.7/4.0 | existing | L | **existing light combo** |
| 2 | Forward Tilt | L+F | G | 5/5/25 | 7.0 | 1780 | L -> (sheet "Horizontal") | taro_ftilt |
| 3 | Up Tilt | L+U | G | 6/6/28 | 6.5 | 1720 | L ^ ("Vertical") | taro_utilt |
| 4 | Down Tilt | L+D | G | 6/4/24 | 5.5 | 1580 | L -> | taro_dtilt |
| 5 | Dash Attack | L@dash | G | 9/6/36 | 9.0 | 2020 | M -> | taro_dash |
| 6 | Forward Smash | H+F | G | 16/6/46 | 16.0 | 2700 | VH -> ("High (Kill)") | taro_fsmash, charge |
| 7 | Up Smash | H+U | G | 14/6/48 | 15.0 | 2610 | H ^ | taro_usmash, charge |
| 8 | Down Smash | H+D | G | 15/7/50 | 14.0 | 2520 | M both sides | taro_dsmash, 5200 wide, charge |
| 9 | Neutral Air | L | A | 4/5/30 | 6.0 | 1650 | L | taro_nair, circle r 1300 |
| 10 | Forward Air | L+F | A | 6/6/34 | 8.5 | 1970 | M -> | taro_fair |
| 11 | Back Air | L+BK | A | 8/6/32 | 9.0 | 2020 | M <- | taro_bair |
| 12 | Up Air | L+U | A | 7/5/36 | 7.5 | 1850 | M ^ | taro_uair |
| 13 | Down Air | L+D | A | 10/5/38 | 10.5 | 2180 | Spike (H) | taro_dair |
| 14 | Wind Lance | B | GA | 11/3/36 | 8.0 | 1910 | M -> | taro_nspec, wind blade 9000 milli |
| 15 | Piercing Dash | B+F | GA | 9/6/40 | 11.0 | 2240 | M -> | taro_sspec, self_vx 200, level, aironce |
| 16 | Jade Stance | B+D | G | 5/14/26 | - (8 est) | 1910 | M -> | taro_dspec, counter incl. projectiles |
| 17 | Staff Vault | B+U | GA | 6/12/28 | 6.0 | 1650 | M ^ | taro_uspec, self_vy 260, aironce |
| 18 | Skyhook | S1+U | G | 7/5/32 | 9.5 | 2080 | M ^ (launcher) | taro_skyhook, cd 240 |
| 19 | Crescent Reaper | S2+U | GA | 12/8/42 | 13.0 | 2430 | H, circle r 1600 | taro_reaper, cd 300 |
| 20 | Jade Counter | S2+D | G | 4/6/34 | 12.0 | 2340 | H -> ("Strong") | taro_jadecounter, melee counter, cd 240 |
| 21 | Falling Spear | B+D | A | 8/40 max (sheet 6)/40 | 16.0 | 2700 | Spike (VH) | taro_fallingspear, dive + landhit |

S1+D, S3+U, S3+D are empty: they fall back to Leap Slam / Throw Spear.

## Notes
- **Jab diffs**: sheet 3 hits 2.2/1.7/4.0; existing combo 4 hits, finisher 1500. Kept.
- **Tiers inferred**: Taro's sheet gives directions without strength for most moves; rule from README section 3
  (tilts L, aerials M, smashes H) except where the sheet says High/Kill/Strong/Spike.
- **Two counters**: Jade Stance (down+B) is the long 14-tick stance that also absorbs projectiles and answers with
  an M slash (sheet damage "-": 8 % assumed, `est`); Jade Counter (S2+D) is the precise 6-tick melee-only parry
  with an H strike. Existing Spin Guard (S2) stays the third defensive tool.
- **Falling Spear** takes air down+B (Jade Stance is ground only), sheet "Aerial Spike / Finisher". Straight
  dive at -330 milli/tick; landing burst 1350, M ^.
- **Staff Vault** "can catch ledges": no ledges in the sim; it is a high jump with a spinning hitbox
  (peak about 3400 milli) and `aironce`.
- **Piercing Dash** "pushes through foes": fighters have no body collision in the sim, so it passes through
  already; nothing extra needed.
- Combo examples on the sheet (Up Tilt -> Nair -> Fair, Fair -> Dair spike) work with the tier stun values
  (L ^ stun 22 vs nair startup 4).
