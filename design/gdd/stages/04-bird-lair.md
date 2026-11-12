# Stage 6: The Giant Bird Lair (spec v1, 2026-11-11)

Source: `design/art/maps/04-giant-bird-lair.jpg` (incl. "Event frame: bird swipe"). Shared rules: `README.md`.
Numbers = proposed targets.

## Overview
| Item | Value |
|---|---|
| Theme | High rock plateau between two towering peaks, giant nest with eggs, sunset sky over a sea of clouds |
| Mood | Wild, windswept |
| KO | **Horizontal KO**: left and right KO; top none (fall back); bottom = solid cloud-sea floor |
| Event | Colossal bird sweep: crosses the arena horizontally with a gust toward one side |
| Play time | One match (up to 3:00) |
| Role | Counterpick; the gust converts damage into side KOs |

## Layout (not to scale)
```
 16000 - - - - - - - - - - SkyCapY (no KO, fall back) - - - - - - - - - - - - - - - - -
  6000 . . . . . . . . . . top of GUST BAND (everything below is pushed) . . . . . . . .
  3700 K        ~~~~~~~~~~ BIRD BODY BAND 1900..3700 ~~~~~~~~~~>                     K
  1600 O            [==L==]                       [==R==]                            O
     0 K   [###################### MAIN plateau ######################]             K
 -3000 [~~~~cloud sea floor~~~~][#### plateau body (solid) ####][~~~cloud sea floor~~]
     X: -13000 (KO)    -7000           0            7000                 +13000 (KO)
```

## Bounds and platforms
| Id | Kind | Cx | Top | HalfW | Notes |
|---|---|---|---|---|---|
| MAIN | solid plateau | 0 | 0 | 7000 | rock between the peaks; solid from 0 down to the floor (MainBottom -3000) |
| L | one-way | -3500 | 1600 | 1450 | low rock mesa top; mesa body drawn behind fighters |
| R | one-way | +3500 | 1600 | 1450 | mirror |
| FLOOR | solid floor | 0 | -3000 | 13000 | cloud sea, walk-off at both KO lines |

| Bound | Value |
|---|---|
| Side KO | feet \|X\| > 13000 |
| Top | no KO, SkyCapY 16000 |
| Bottom | solid cloud floor at -3000 |

Why a plateau down to the floor: the sheet's floating rock sits on the cloud sea; making it solid to the floor removes a
crawl space under MAIN and turns being knocked off into landing on a low shelf closer to the KO line.

### Reachability
| Target | From | Rise | Gap | Result |
|---|---|---|---|---|
| L / R | MAIN | 1600 | 0 | single jump |
| MAIN | FLOOR beside the plateau | 3000 | around the edge | double jump (4022) |

Clearance: L/R undersides 1300 over MAIN: one-way, no collision from below; mesa bodies are background art.

## Event: bird swipe
| Item | Value |
|---|---|
| Direction | `HazArg` = +1 (left to right) or -1. **Recommended: alternating**, first direction `HazRng.Range(2)`; alternative: random every event |
| Telegraph | **90 ticks**: screech, the FAR bird silhouette dives off-frame, a growing ground shadow and wind streaks enter from the start side, a side arrow at the screen edge |
| Gust | from Telegraph + 90 for 120 ticks (2 s). Applies to every fighter with feet Y < 6000 (whole arena), F3 below |
| Body pass | from Telegraph + 120 for 80 ticks: body centre `X = -dir x 17000 + dir x 425 x t` (t = 0..80), half-width 3000, band Y 1900..3700 |
| Body hit | **14 %**, knockback **High**, angle 30 deg up toward `dir`, unblockable, once per fighter per pass |
| Total | 210 ticks; first event `1200 + Range(301)` after the intro, next `900 + Range(301)` after the end (15..20 s) |

Readable rule for players: "When the bird screeches, get on the ground and hold against the wind." Standing on MAIN
(head at 1350) is below the body band; standing on L/R or jumping is inside it.

### F3 gust (values)
`GustAccel = 12000` sub/tick, `GustCap = 120000` sub (8 u/s), applied after FreeActions:
`if (Vx * dir < GustCap) Vx = clampToward(Vx + dir * 12000, dir * GustCap)`.

| Situation (slowest class, speed 80325) | Result over the 120-tick gust |
|---|---|
| Grounded, neutral input | settles at 12000 sub = 13 milli/tick, drift ~1.6 u |
| Grounded, holding against | Vx = -80325 + 12000, keeps walking upwind |
| Airborne, holding against | AirAccel 10000 < gust: net +2000 sub/tick, carried about 5 u downwind |
| Airborne, neutral | reaches 120000 in about 60 ticks, carried about 12 u: lethal unless it lands first |
| In hitstun, launched with the wind | knockback plus up to 8 u/s: the intended KO combo |

### Pacing chart (one event, ticks)
```
intensity
 high |                      ######## body
  med |              ########################  gust
  low |   ...........                        .
        telegraph 90  gust 30  pass 80   tail 10      idle 900..1200
```

## Spawn, respawn, camera
| Item | Value |
|---|---|
| Spawn | (+-4800, 775) |
| Respawn | (0, 6000); invulnerable fighters ignore gust and body (README 3.3) |
| Art rect | X -15000..15000, Y -5000..17000 |
| Max camera size | min(10, 11, 15/aspect) = 8.4 at 16:9 |

## Parallax and look
| Layer | Factor | Content (sheet) |
|---|---|---|
| FAR | 0.12 | sunset sky, enormous bird silhouette circling (cosmetic, stays when hazards are OFF but never dives), sea of clouds |
| MID | 0.40 | two towering peaks and the gigantic nest with its giant eggs |
| Gameplay | 1.00 | MAIN plateau, L/R mesa tops, cloud floor line, the bird body sprite during a pass |
| FRONT | 1.15, alpha <= 0.40 | near clouds below Y -2500, drifting feathers, wind motion lines (lines point with the gust during an event) |

Palette: Warm orange `#FF8A3C`, Teal `#13788F`, Earth brown `#8A5A3A`, Sunset gold `#FFC069`, Cloud sea `#FFF1CF`.

## Audio
| Cue | Id (proposal) | When |
|---|---|---|
| Ambience | `stage_birdlair_amb` | steady high wind, cloth-like feathers, distant cries |
| Screech | `stage_birdlair_screech` | Telegraph start, panned to the start side |
| Gust | `stage_birdlair_gust_loop` | gust window, panned with `dir` |
| Wing pass | `stage_birdlair_pass` | body pass, doppler across the stereo field |
| Music | tribal drums, wide strings; drum hit on the screech |

## Narrative beats
Fighters are trespassing at the nest; the mother bird defends the eggs. The eggs never hatch or move (no extra event).

## Competitive notes
- Counterpick. Hazards OFF: no event; layout is a legal walk-off stage with two low platforms.
- Alternating direction is recommended for fairness (each side gets the same number of gusts toward it).

## Edge cases
- Fighter on the floor shelf near the KO line during a gust toward that side: pushed at 13 milli/tick grounded; must
  hold upwind or jump back to MAIN before the gust.
- Two fighters overlap the body the same tick: both hit, index order.
- Shielding fighter: shield does not stop body damage (unblockable); gust still applies.
- Respawn during the gust: invulnerable for 90 ticks, no push; lands on MAIN before invuln ends (35-tick fall).
- Fighter above Y 6000: no gust, but SkyCapY still applies.

## Tuning knobs
| Knob | Default | Safe range | Affects |
|---|---|---|---|
| GustAccel / GustCap | 12000 / 120000 | 10000..14000 / 90000..150000 | lethality; must stay < GroundAccel 17500 |
| Body band | 1900..3700 | bottom >= 1500 (above standing head) | who the body can hit |
| Body damage / tier | 14 % / High | 10..18 % | punishment |
| Telegraph | 90 | 60..120 | readability |
| Delay base + span | 900 + 301 | base >= 600 | frequency |
| Side KO | 13000 | 12000..15000 | KO percent |

## Acceptance criteria
1. First damage or push tick is 90 ticks after Telegraph start (seeded test).
2. Grounded fighter on MAIN with neutral input drifts <= 2000 milli over a full gust; holding upwind gains ground.
3. Airborne neutral fighter at X 0 is carried >= 6000 milli in the gust direction.
4. Fighter standing on MAIN is never hit by the body; fighter standing on L or R during the pass is hit once.
5. Direction alternates every event after the first (if the alternating rule is chosen).
6. Feet Y never < -3000; |X| > 13000 -> KO; launch at any Vy returns without KO.
