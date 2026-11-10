# Ren (Ranged): moveset mapping

Sheet: `design/art/character-sheets/ren-moveset.png` (22 moves). Rules: `README.md`. Numbers: `movesets.csv`
(24 rows: Focus Shot has 3 charge-level rows, Rebound Kick has ground and air rows). d_jab1 = 2.0. Melee reach
1300. **HalfKnockback applies to every Ren hit** (CSV holds the unhalved tier). Existing kit kept: arrow light
combo, big arrow heavy, S1 Backstep Shot, S2 Bowstring Choke, S3 Arrow Barrage, ult Thousand Arrows.

| # | Sheet move | Input | G/A | S/A/R | Dmg % | Mult | Kb tier | CSV id / note |
|---|---|---|---|---|---|---|---|---|
| 1 | Jab 1-2-3 | L | G | 3/2-3/19-33 | 2.0/1.6/4.2 | existing | L -> M | **existing arrow combo** |
| 2 | Forward Tilt | L+F | G | 5/5/24 | 6.5 | 1800 | M -> | ren_ftilt, short arrow (3000 milli) |
| 3 | Up Tilt | L+U | G | 6/6/27 | 6.0 | 1730 | M ^ | ren_utilt |
| 4 | Down Tilt | L+D | G | 6/4/25 | 5.5 | 1660 | Trip | ren_dtilt |
| 5 | Dash Attack | L@dash | G | 8/6/36 | 8.0 | 2000 | M -> | ren_dash, slide self_vx 150 |
| 6 | Forward Smash | H+F | G | 15/6/48 | 16.0 | 2830 | H -> | ren_fsmash, big arrow 7200 milli, charge |
| 7 | Up Smash | H+U | G | 16/6/50 | 15.0 | 2740 | H ^ | ren_usmash, tall pillar box, charge |
| 8 | Down Smash | H+D | G | 14/7/46 | 14.0 | 2650 | M both sides | ren_dsmash, charge |
| 9 | Neutral Air | L | A | 5/5/24 | 7.0 (3 hits) | 620 x3 | link, last L | ren_nair, multi:3:2 |
| 10 | Forward Air | L+F | A | 7/5/28 | 8.5 | 2060 | M -> | ren_fair |
| 11 | Back Air | L+BK | A | 7/5/27 | 8.0 | 2000 | M <- | ren_bair |
| 12 | Up Air | L+U | A | 6/4/26 | 7.5 | 1940 | M ^ | ren_uair |
| 13 | Down Air | L+D | A | 8/5/30 | 10.0 | 2240 | Spike (H) | ren_dair |
| 14 | Focus Shot | B (hold) | GA | 12/4/28 | 6/10/14 | 1730/2240/2650 | L/M/H -> | ren_nspec_lv1-3: Lv2 at 20 held ticks, Lv3 at 40 |
| 15 | Piercing Arrow | B+F | GA | 14/6/36 | 12 (6 after 1st) | 2450 | M -> | ren_sspec, pierce, 500 milli/tick |
| 16 | Trap Mine | B+D | G | 10/1/22 | 9.0 | 2120 | M ^ (launch) | ren_dspec, contact zone, arm 30, life 600 |
| 17 | Grapple Arrow | B+U | GA | 8/4 (latch)/33 | 4.0 | 1410 | L | ren_uspec, tether:self at 60 deg: **substitute** |
| 18 | Skyward Volley | S3+U | GA | 12/18/40 | 2.5 x5 | 500 x5 | L ^ each | ren_volley, 5-arrow fan, cd 360 |
| 19 | Ricochet Arrow | S3+D | GA | 14/8/34 | 7.0 / 5.0 bounce | 1870 / 1580 | M -> | ren_ricochet, 3 bounces, cd 240 |
| 20 | Falling Pin | B+D | A | 10/6/32 | 12.0 | 2450 | Spike (H) | ren_fallingpin, straight-down arrow |
| 21 | Rebound Kick | S1+D | G / A | 6/4/26 | 6.0 | 1730 | L push | ren_rebound / ren_rebound_air, recoil |
| 22 | Grapple Arrow (Extended) | S1+U | GA | 10/4/38 | 5.0 | 1580 | L pull | ren_grapple2, tether:foe |

S2+U / S2+D are empty: they fall back to Bowstring Choke.

## Skyward Volley fan (sub/tick, X toward Facing; speed 240000, angles 60/70/80/90/100 deg)
| Shot | 0 | 1 | 2 | 3 | 4 |
|---|---|---|---|---|---|
| (Vx, Vy) | (120000, 207846) | (82085, 225526) | (41676, 236354) | (0, 240000) | (-41676, 236354) |
Shots at active ticks 0, 4, 8, 12, 16 (`rehit:4`), spawned at feet + (300, 1100).

## Notes
- **Jab diffs**: sheet 3 arrows, existing combo 4 arrows (finisher 4th, reload 90 ticks). Kept.
- **Input conflicts resolved**: the sheet puts Skyward Volley on up+B, Ricochet on B hold, Rebound Kick on side+B,
  which are the four core specials' inputs. They move to skill techniques. Falling Pin keeps its sheet input
  (air down+B) as the air variant of Trap Mine.
- **Grapple Arrow (#17) substitute** (no walls / ledges): the arrow flies at 60 deg up-forward for 8 ticks
  (4000 milli). Anchor = enemy hit, floor/platform top touch, or end of flight (air anchor). Ren is pulled to it
  at 300 milli/tick for up to 20 ticks. `aironce`.
- **Grapple Extended (#22)**: horizontal 6000 milli. Enemy hit = pulled toward Ren (L, stun 30 for a follow-up);
  touching the floor/platform = self pull; missing in the air = nothing. The sheet's "Slash!" is the hit sound,
  no extra strike.
- **Ricochet**: "Hold, Release" aiming is dropped (fires on press at a fixed down-forward angle, (2, -1)); bounces
  off floor/platform tops and off Ren's target, max 3, 1580 after any bounce.
- **Piercing Arrow** "6 % after the first hit": in 1v1 there is no second fighter, so only `pierce`
  (not stopped, not destructible) is implemented.
- **Falling Pin** is a projectile, not a dive: arrow straight down at 330000 sub/tick with a small hop
  (self_vy 80). As a Ren move its spike is halved (VH-like kill power is lost, see OD-4).
- **Rebound Kick**: ground recoil (-200, 0) milli/tick; air recoil (-150, +170) and `aironce`.
