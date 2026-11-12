# Stage 5: The Volcano Abyss (spec v1, 2026-11-11)

Source: `design/art/maps/03-volcano-abyss.jpg`. Shared rules: `README.md`. Numbers = proposed targets.

## Overview
| Item | Value |
|---|---|
| Theme | Obsidian crater, basalt rock over a magma lake, ledge staircase climbing toward the red opening above |
| Mood | Intense, dangerous |
| KO | **Vertical + Horizontal KO**: all four edges; the bottom edge is the magma lake surface |
| Event | Fire bursts: columns erupt from the magma lake in two fixed lanes beside MAIN |
| Play time | One match (up to 3:00); fastest KOs of the set (close bottom line) |
| Role | Counterpick; edge-guarding stage (fire lanes cover the recovery paths) |

## Layout (not to scale)
```
 15000 ======================== TOP KO ==============================================
  8000 |              [=L4=]                                                        |
  6000 |                        [==L3==]                                            |
  4000 |                [===L2===]                                                  |
  2000 |                                    [====L1====]                            |
     0 |   ||F||   [############ MAIN ############]          ||F||                  |
 -1200 |   ||F||   (solid underside)                         ||F||                  |
 -7500 ~~~~~~~~~~~~~~~~~~~~~~ MAGMA LAKE = BOTTOM KO ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
     X: -14000 (KO)  -8600 lane    -6000      0      6000    +8600 lane    +14000 (KO)
```

## Bounds and platforms
| Id | Kind | Cx | Top | HalfW | Span | Notes |
|---|---|---|---|---|---|---|
| MAIN | solid | 0 | 0 | 6000 | -6000..6000 | basalt rock floating above the magma; underside -1200 |
| L1 | one-way | +5650 | 2000 | 1800 | 3850..7450 | first ledge of the climb, overhangs MAIN's right edge |
| L2 | one-way | +700 | 4000 | 1650 | -950..2350 | second ledge |
| L3 | one-way | +4050 | 6000 | 1500 | 2550..5550 | third ledge |
| L4 | one-way | -150 | 8000 | 1350 | -1500..1200 | top ledge, "the path out toward the opening" |

| Bound | Value |
|---|---|
| Side KO | feet \|X\| > 14000 |
| Top KO | feet Y > 15000 |
| Bottom KO | feet Y < -7500 (touching the magma lake surface) |

The staircase is the sheet's asymmetric layout (kept on purpose). Every step is +2000 (single jump).

### Reachability
| Target | From | Rise | Horizontal gap | Result |
|---|---|---|---|---|
| L1 | MAIN (x 3850..6000 under it) | 2000 | 0 | single jump |
| L2 | L1 left end | 2000 | 1500 | single jump |
| L3 | L2 right end | 2000 | 200 | single jump |
| L4 | L3 left end | 2000 | 1350 | single jump |
| L2 | MAIN direct | 4000 | 0 | not reliable (4022 apex): intended, climb via L1 |

Clearance: L1 over MAIN 1700; L2 over MAIN 3700; L3 over L1 3700; L4 over L2 3700.

## Event: fire bursts
| Item | Value |
|---|---|
| Lanes | 2 fixed columns at X = -8600 and X = +8600, half-width 700 (sheet's two fire columns beside MAIN). L1's right end (7450 + hurtbox 375 = 7825) stays clear of the right lane (7900) |
| Column height | from the lake (-7500) up to Y 9000 |
| Choice | at Telegraph start: `HazArg = HazRng.Range(3)`: 0 left, 1 right, 2 both |
| Telegraph | **75 ticks**: lake bulges and glows in the lane, a low ember column (20 % alpha, exact lane width) rises, rumble SFX |
| Erupt | 45 ticks. Column top rises linearly over the first 6 ticks: `top = -7500 + 16500 x min(t+1, 6) / 6`; damage only where the column already is |
| Fade | 20 ticks, visual only, no damage |
| Hit | **10 %**, knockback **Med**, angle 90 deg (straight up), unblockable, once per fighter per burst |
| Delay | first burst `360 + Range(241)` ticks after the intro; next `300 + Range(241)` after each Fade (5..9 s) |

Design intent: the lanes sit exactly on the recovery paths beside MAIN. A burst hurts (10 %) but launches upward, so
it can also save a falling fighter at low percent and kill toward the top KO at high percent. MAIN, the ledges and
the centre are never hit: centre play stays clean, edge play becomes a timing read.

### Pacing chart (one cycle, ticks)
```
intensity
 high |            ####                ####
  med |        ....####            ....####
  low |________....####^^__________....####^^____
        idle   tele erupt fade  idle  tele erupt fade
        300-540 75  45   20
```

## Spawn, respawn, camera
| Item | Value |
|---|---|
| Spawn | (+-3600, 775) |
| Respawn | (-3500, 6000), the open column left of the staircase, lands on MAIN |
| Art rect | X -16000..16000, Y -9000..16500 |
| Max camera size | min(10, 12.75, 16/aspect) = 9.0 at 16:9 |

## Parallax and look
| Layer | Factor | Content (sheet) |
|---|---|---|
| FAR | 0.12 | obsidian crater walls, glowing lava cracks, faint opening to a red sky far above |
| MID | 0.40 | closer jagged black walls with rim light and more lava cracks |
| Gameplay | 1.00 | basalt MAIN, L1..L4, the magma lake surface line, fire columns (hazard sprites from the same lane table) |
| FRONT | 1.12, alpha <= 0.35 | embers drifting up, edges fading into dark void |

Palette: Crimson `#D4261C`, Orange `#FF9A1F`, Obsidian `#1B1022`, Magma core `#FFF0A0`, Rim light `#FF8A2E`.
Readability: the lake surface line is the KO line (no gap between art and rule). Ambient embers never use the
column shape or the Magma core colour, which is reserved for telegraph and eruption.

## Audio
| Cue | Id (proposal) | When |
|---|---|---|
| Ambience | `stage_volcano_amb` | deep magma rumble, crackle, distant hiss |
| Telegraph | `stage_volcano_burst_warn` | Telegraph start, rising rumble 75 ticks, panned to the lane |
| Eruption | `stage_volcano_burst` | Erupt start, roar + hiss |
| Music | driving percussion, low brass; tension layer during Telegraph |

## Narrative beats
The ledges climb toward the red opening: the only way out is up, and the top ledge is closest to the top KO.

## Competitive notes
- Counterpick. Hazards OFF: no bursts; remaining layout is a legal 4-edge stage (asymmetric).
- Side choice is not equal (staircase on the right): for ranked, the stage loser picks the spawn side (lead decision).

## Edge cases
- Fighter on the magma surface exactly (feet Y = -7500): not KO (strict), next tick falls through: KO.
- Fighter in hitstun crossing a column: hit (unblockable), one hit per burst.
- Both lanes with a fighter in each: both hit the same tick, index order.
- Respawn-invulnerable fighter in a lane: no damage, no push (README 3.3).
- Projectiles pass through columns.

## Tuning knobs
| Knob | Default | Safe range | Affects |
|---|---|---|---|
| Bottom KO (lake) | -7500 | -6500..-9725 | recovery difficulty |
| Lane X / half-width | +-8600 / 700 | X >= 8200 (keeps L1 safe) | edge-guard pressure |
| Telegraph / erupt | 75 / 45 | >= 60 / 30..60 | readability, threat |
| Delay base + span | 300 + 241 | base >= 240 | frequency |
| Damage / tier | 10 % / Med | 6..14 % | punishment vs rescue |

## Acceptance criteria
1. With a fixed seed, the first Telegraph starts at 360 + Range(241) ticks after the intro and the first damage tick
   is exactly 75 ticks later.
2. A fighter standing anywhere on MAIN or L1..L4 is never hit by a burst (1 000 seeded bursts).
3. A fighter at (-8600, -3000) during Erupt takes 10 % once and gets the Med upward knockback.
4. Feet Y < -7500 -> KO; feet Y > 15000 -> KO; |X| > 14000 -> KO.
5. Every ledge is reached by the slowest class with single jumps along L1 -> L2 -> L3 -> L4.
