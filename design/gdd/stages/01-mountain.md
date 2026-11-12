# Stage 3: Mountain of the Beginning (spec v1, 2026-11-11)

Source: `design/art/maps/01-mountain-of-the-beginning.jpg`. Shared rules: `README.md`. Numbers = proposed targets
(final geometry: `stage-geometry.json`).

## Overview
| Item | Value |
|---|---|
| Theme | Jade karst peaks, giant waterfall, floating rocks with glowing jade runes over a misty bottomless drop |
| Mood | Calm, heroic first stage |
| KO | **Vertical KO**: top KO, bottom KO; left and right are solid walls (no KO) |
| Event | None. Danger = open sky above and the misty drop below. Platforms A, B, D move |
| Play time | One match (up to 3:00); typical stock lost every 40..60 s (vertical-only KOs) |
| Role | Starter / learning stage; vertical-launch moves matter, horizontal finishers cannot KO |

## Layout (not to scale; table is authoritative)
```
 14000 ===================== TOP KO =========================================
       |W                                                                 W|
  5200 |W            [==D==] <-- slides X -5600 .. +5600 -->              W|
  3200 |W                      [=======C=======]                          W|
  2600 |W [====A====]  ^                                    [====B====]   W|
   600 |W   A, B rise/fall 600..2600 (mirror, in phase)                   W|
     0 |W              [#############  MAIN  #############]               W|
 -1200 |W              (solid underside)        mist                      W|
 -8000 ===================== BOTTOM KO (misty drop) ========================
     X: -13500 (wall)        -6000          0          6000         +13500 (wall)
```

## Bounds and platforms
| Id | Kind | Cx | Top | HalfW | Notes |
|---|---|---|---|---|---|
| MAIN | solid | 0 | 0 | 6000 | carved stone, Chinese key pattern; underside -1200 |
| A | one-way, moves Y | -8900 | 600..2600 | 2000 | jade runes; recovery "elevator" on the left |
| B | one-way, moves Y | +8900 | 600..2600 | 2000 | mirror of A |
| C | one-way, static | 0 | 3200 | 2300 | centre top platform |
| D | one-way, moves X | -5600..+5600 | 5200 | 1700 | jade runes; sweeps over C |

| Bound | Value |
|---|---|
| Walls | X = +-13500 (fighter centre clamp +-13125), infinite height |
| Top KO | feet Y > 14000 |
| Bottom KO | feet Y < -8000 |
| BlastX | none (walls) |

Why closer vertical KOs than Sky Dojo (17775 / -9725): walls remove every horizontal KO, so vertical launches must
finish at comparable percents.

### Moving platforms (F1 in README)
| Id | Axis | Min | Max | Period P | Phase Ph | Peak speed | t = 0 position |
|---|---|---|---|---|---|---|---|
| A | Y | 600 | 2600 | 360 | 90 | 16.7 milli/tick | 1600 rising |
| B | Y | 600 | 2600 | 360 | 90 | 16.7 milli/tick | 1600 rising (mirror, same phase) |
| D | X | -5600 | +5600 | 900 | 0 | 37 milli/tick | -5600 (art position) |

Riders carried (README 3.2). A and B in phase keeps the stage symmetric for both spawns; a half-period offset
(Ph_B = 270) is a tuning option for more variety.

### Reachability (slowest class, limits README section 2)
| Target | From | Rise | Horizontal gap | Result |
|---|---|---|---|---|
| A / B at 2600 | MAIN edge | 2600 | 900 | double jump |
| A / B at <= 2000 | MAIN edge | <= 2000 | 900 | single jump |
| A / B at 600 | below MAIN (recovering) | catch while falling | - | recovery elevator |
| C | MAIN | 3200 | 0 (above) | double jump (limit 3200) |
| D | C | 2000 | <= 1600 (D at an end) / 0 (D over C) | single jump |
| D | A / B at 2600 | 2600 | overlap 400 at D's end | double jump |

Clearance: C underside 2900 over MAIN (>= 1350); D underside 4900 over C top 3200 (1700); A/B never over MAIN.

## Spawn, respawn, camera
| Item | Value |
|---|---|
| Spawn | (+-3000, 775) |
| Respawn | (0, 8000), lands on D, C or MAIN |
| Art rect | X -15500..15500, Y -9500..15500 (cliff art behind the walls) |
| Max camera size | min(10, 12.5, 15.5/aspect) = 8.7 at 16:9 |

## Parallax and look
| Layer | Factor | Content (sheet) |
|---|---|---|
| FAR | 0.12 | golden morning sky, drifting clouds, misty karst peaks, small pagoda on a distant peak |
| MID | 0.40 | jade karst cliffs, the huge waterfall with glowing spray, pines on the rock; the cliff faces sit on the wall lines so the walls read as rock |
| Gameplay | 1.00 | MAIN, A, B, C, D sprites from the geometry table; rune glow pulses with motion |
| FRONT | 1.15, alpha <= 0.35 | low mist band below Y -1500 hiding the drop |

Palette: Jade `#2FAE7C`, Sky blue `#2F9FE6`, Warm gold `#FFD27A`, Stone `#B6A98D`, Rune glow `#7DFFD0`.

Readability: wall faces get a darker rim + vertical moss line so players learn "rock = no KO". Moving platforms carry
the cyan rune glow; static C has none (sheet).

## Audio
| Cue | Id (proposal) | When |
|---|---|---|
| Ambience | `stage_mountain_amb` | waterfall bed (low-passed), wind through pines, distant birds |
| Rune hum | `stage_mountain_rune_loop` | quiet loop at A/B/D, pitch follows speed (view only) |
| Wall thud | `stage_wall_hit` | knockback contact with a wall (shared with nothing else) |
| Music | calm heroic theme, light percussion; layer up when either fighter is at the last stock |

## Narrative beats (environment only)
The pagoda and the carved MAIN suggest the old training ground where every journey starts; the runes moving the
rocks hint that the mountain itself tests the newcomer. No text.

## Pacing
No event. Intensity is player-driven; the moving D creates a 15 s rhythm for top-platform play, A/B a 6 s rhythm
for edge play.

## Competitive notes
- Hazard-free; legal as a starter. Walls make it a "vertical KO" counterpick for up-launch characters.
- Hazards OFF changes nothing (no event); platforms keep moving (README 3.4).

## Edge cases
- Fighter pinned against the wall by a combo: Vx zeroed each hit, vertical knockback still applies (can be juggled
  up the wall to the top KO; intended).
- Fighter on A when A reaches Min 600 while a second fighter stands below it: none can stand below A (A is over the
  void); a falling fighter under A passes through (one-way).
- D sliding into a fighter standing on C: one-way, no push; D passes over (1700 clearance).
- Respawn while D passes X = 0: the respawned fighter may land on D and be carried; fine.

## Tuning knobs
| Knob | Default | Safe range | Affects |
|---|---|---|---|
| Top / bottom KO | 14000 / -8000 | 12000..17775 / -6000..-9725 | KO percent (walls remove side KOs) |
| WallX | 13500 | 12000..15000 | edge space beside A/B |
| A/B Min, Max, P | 600, 2600, 360 | Max <= 3200 | recovery help, edge play rhythm |
| D travel, P | +-5600, 900 | P >= 600 | top platform rhythm, rider comfort |

## Acceptance criteria
1. Fighter pushed at Vx 300000 into X = 13500 stops at centre 13125, Vx = 0, no KO, for 600 ticks.
2. Feet Y 14001 -> KO; feet Y -8001 -> KO.
3. A at GameTicks 0 has top 1600, at 90 top 2600, at 270 top 600 (F1, Ph 90).
4. D at GameTicks 0 has Cx -5600, at 450 Cx +5600.
5. A rider standing on D for 900 ticks ends at the same relative X on D (no drift).
6. Reachability table: scripted jumps for the slowest class succeed for every row.
