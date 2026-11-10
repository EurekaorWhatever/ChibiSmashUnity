# Bruna (Heavy): moveset mapping

Sheet: `design/art/character-sheets/bruna-moveset.png` (22 moves). Rules: `README.md`. Numbers: `movesets.csv`
(21 new rows). d_jab1 = 2.5. Reach 1850. Existing kit kept: light combo (5 hits), heavy, S1 Grab & Smash,
S2 Tornado Spin, ult Smash. Heavy has no Skill3 (S3 and S3+U/D do nothing).

| # | Sheet move | Input | G/A | S/A/R | Dmg % | Mult | Kb tier | CSV id / note |
|---|---|---|---|---|---|---|---|---|
| 1 | Jab 1-2-3 | L | G | 3/2/18 | 2.5/2.5/5.0 | existing | VL -> L | **existing light combo** |
| 2 | Forward Tilt | L+F | G | 6/4/20 | 7.0 | 1670 | L -> | bruna_ftilt |
| 3 | Up Tilt | L+U | G | 7/5/24 | 7.5 | 1730 | M ^ | bruna_utilt |
| 4 | Down Tilt | L+D | G | 5/4/20 | 6.5 | 1610 | Trip | bruna_dtilt |
| 5 | Dash Attack | L@dash | G | 8/5/28 | 9.0 | 1900 | M -> | bruna_dash, self_vx 130 |
| 6 | Forward Smash | H+F | G | 14/6/36 | 18.0 | 2680 | H -> | bruna_fsmash, charge |
| 7 | Up Smash | H+U | G | 16/5/38 | 17.0 | 2610 | H ^ | bruna_usmash, charge |
| 8 | Down Smash | H+D | G | 15/6/40 | 16.0 | 2530 | H ^ both sides | bruna_dsmash, charge |
| 9 | Neutral Air | L | A | 5/4/24 | 6.0 | 1550 | L | bruna_nair, circle r 1000 |
| 10 | Forward Air | L+F | A | 6/4/26 | 8.5 | 1840 | M -> | bruna_fair |
| 11 | Back Air | L+BK | A | 6/5/28 | 9.0 | 1900 | M <- | bruna_bair |
| 12 | Up Air | L+U | A | 7/4/26 | 7.5 | 1730 | M ^ | bruna_uair |
| 13 | Down Air | L+D | A | 9/5/30 | 10.0 | 2000 | Spike (H) | bruna_dair |
| 14 | Earth Shock | B | G | 10/6/30 | 11.0 | 2100 | M -> | bruna_nspec, ground wave projectile 3333 milli |
| 15 | Hammer Rush | B+F | GA | 8/8/34 | 12.0 | 2190 | H -> | bruna_sspec, self_vx 160, level, aironce |
| 16 | Fortress Stance | B+D | G | 5/20 (hold)/18 | - | 0 | - | bruna_dspec, armor (kb x0.25), hold up to +60 |
| 17 | Rocket Hammer | B+U | GA | 9/6/30 | 8.0 | 1790 | M ^ | bruna_uspec, self_vy 240, aironce |
| 18 | Iron Uppercut | S1+U | G | 9/6/28 | 13.0 | 2280 | H ^ | bruna_uppercut, cd 240 |
| 19 | Fortress Guard | S2+D | G | 4/6/24 | 12.0 | 2190 | H diagonal | bruna_fortressguard, counter, cd 240 |
| 20 | Meteor Anvil | B+D | A | 10/40 max (sheet 5)/36 | 18.0 | 2680 | Spike (VH) | bruna_meteor, dive + crater landhit |
| 21 | Seismic Sweep | S1+D | G | 12/8/36 | 14.0 | 2370 | M ^ | bruna_seismic, centred 4800 wide, cd 360 |
| 22 | Rocket Hammer (Recovery-Attack) | S2+U | GA | 9/5/30 | 10.0 | 2000 | H ^ | bruna_rocketstrike (renamed "Rocket Hammer Strike"), cd 240 |

## Notes
- **Jab diffs**: sheet 3 hits (3/2/18); existing combo is 5 hits (PatternLength 5), base 5/1/8 slowed by the
  -15 % passive, finisher 1500. Kept.
- **Meteor Anvil** uses the sheet's own input "Air Down B", so ground B+D = Fortress Stance, air B+D = Meteor Anvil.
  Active in the CSV is the dive limit (40); the sheet's 5 is the minimum spike window. A hit ends the dive with a
  small pop (no dive suicide); landing makes the crater (1340, M ^, 2600 wide) then 36 ticks landing lag.
- **#17 and #22 share the name Rocket Hammer** on the sheet. #17 (B+U) is the pure recovery; #22 is the
  diagonal launch attack on S2+U, renamed Rocket Hammer Strike. Both `aironce` (separate bits, so both can be used
  in one airtime: strong recovery is acceptable for the heaviest, slowest fighter).
- **Fortress Stance** has no hitbox; it trades mobility for no-flinch. Grabs (Unblockable, e.g. Grab & Smash)
  ignore armor. Damage is taken in full.
- **Down Smash** sheet arrow is up; kept as H ^ with positional Dir (both sides).
