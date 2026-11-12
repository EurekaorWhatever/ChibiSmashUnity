# Stage 7: The Abandoned Ocean Monument (spec v1, 2026-11-11)

Source: `design/art/maps/05-abandoned-ocean-monument.jpg`. Shared rules: `README.md`. Numbers = proposed targets.

## Overview
| Item | Value |
|---|---|
| Theme | Sunken temple: marble pillars, broken arches, rune-carved stone tiers, coral, jellyfish, light rays |
| Mood | Mysterious, awe-inspiring |
| KO | **Vertical + Horizontal KO**: all four edges |
| Event | Whale descent: a huge whale sinks from the top to just above MAIN, holds, then rises; players drop to T1/T2/T3 |
| Play time | One match (up to 3:00) |
| Role | With hazards OFF, the most standard 4-edge symmetric stage of the set (second neutral starter) |

## Layout (not to scale)
```
 12000 ========================== TOP KO =================================================
       |              [/////////// WHALE  (X -8500..8500, 3500 tall) ///////////]        |
       |                   descends to bottom Y 600, holds 180, rises                    |
     0 |                  [############## MAIN ##############]                           |
 -1200 |                  (solid underside)                                              |
 -3100 |   [=======T1=======]                       [=======T2=======]                   |
 -5800 |                         [=========T3=========]                                  |
-11500 ========================== BOTTOM KO ==============================================
     X: -14000 (KO)  -9250     -6000  -3400    0    3400  6000     9250      +14000 (KO)
```

## Bounds and platforms
| Id | Kind | Cx | Top | HalfW | Span | Notes |
|---|---|---|---|---|---|---|
| MAIN | solid | 0 | 0 | 6000 | -6000..6000 | ancient stone, rune band; underside -1200 |
| T1 | one-way | -6450 | -3100 | 2800 | -9250..-3650 | tiered lower ledge, escape from the whale |
| T2 | one-way | +6450 | -3100 | 2800 | 3650..9250 | mirror |
| T3 | one-way | 0 | -5800 | 3400 | -3400..3400 | lowest tier, last escape |

| Bound | Value |
|---|---|
| Side KO | feet \|X\| > 14000 |
| Top KO | feet Y > 12000 |
| Bottom KO | feet Y < -11500 (5700 below T3) |

### Reachability
| Target | From | Rise | Gap / route | Result |
|---|---|---|---|---|
| T1 / T2 | MAIN | falls 3100 | walk off MAIN's edge, land on the outer part | drop |
| T3 | T1 / T2 inner end | falls 2700 | gap 250 | drop |
| T1 / T2 | T3 end | 2700 | gap 250 | double jump |
| MAIN | T1 / T2 outer part (\|x\| > 6375) | 3100 | around MAIN's edge | double jump (4022, 900 margin) |

Clearance: MAIN underside -1200 over T1/T2 (1900 gap > 1350) and over T3 (4600). A fighter on T1/T2 under MAIN
cannot jump straight up into MAIN (solid): the route out is the outer part, which is the sheet's tier shape.

## Event: whale descent
Whale body: X -8500..8500, height 3500, Y [Bottom, Bottom + 3500].

| Phase | Ticks | Whale bottom Y |
|---|---|---|
| Telegraph | **120** | stays at 12000 (above the top KO): whale song, the arena darkens, bubbles stream down, the MID whale silhouette turns toward the camera, a dark band grows at the top edge |
| Descend | 285 | `12000 - 40 x t` until 600 |
| Hold | 180 | 600 (just above MAIN) |
| Rise | 190 | `600 + 60 x t` until >= 12000 |
| Idle | `1500 + Range(601)` | first event `1800 + Range(601)` after the intro (25..35 s between events) |

**Collision (crush):** a non-invulnerable fighter whose hurtbox overlaps the body:
- first contact this event: **12 %**, knockback **Med**, angle 60 deg below horizontal, outward (`X >= 0 -> +1`, else -1;
  deterministic tie rule), unblockable;
- every tick while still overlapping (no more damage): `Vy = min(Vy, -180000)` and `Vx = outward x max(|Vx|, 60000)`.
  A fighter pinned on MAIN is squeezed out over the edge and falls onto T1/T2.

**How long players must stay low:** MAIN standing height (head 1350) is unsafe while bottom < 1350: last 19 ticks of
Descend + 180 Hold + first 13 ticks of Rise = **212 ticks (3.5 s)**. Jumping on MAIN is unsafe from bottom < 3613
(about 75 ticks before Hold). Safe spots: T1, T2, T3, and anything with |X| > 8875.

### Pacing chart (one event, ticks)
```
intensity
 high |                        ##########
  med |               #########          ####
  low |  ...........##                       ###
        telegraph 120  descend 285   hold 180  rise 190       idle 1500..2100
```

## Spawn, respawn, camera
| Item | Value |
|---|---|
| Spawn | (+-3600, 775) |
| Respawn | (0, 6000); **during Telegraph..Rise: (0, -4300)**, falls onto T3 under MAIN |
| Art rect | X -16000..16000, Y -12500..13000 |
| Max camera size | min(10, 12.75, 16/aspect) = 9.0 at 16:9 |

## Parallax and look
| Layer | Factor | Content (sheet) |
|---|---|---|
| FAR | 0.10 | deep water, light rays from the surface, distant colonnade |
| MID | 0.40 | marble pillars, broken arches, coral, seaweed, cyan runes, fish, jellyfish; a cosmetic whale swims here when idle (and when hazards are OFF) |
| Gameplay | 1.00 | MAIN, T1, T2, T3; the event whale (drawn from the event state, not the MID whale) |
| FRONT | 1.12, alpha <= 0.35 | rising bubbles, floating particles |

Palette: Deep blue `#073F7A`, Teal `#0B6AA6`, Glowing cyan `#7CF8FF`, Marble `#B9DFE3`, Coral `#FF6F91`.
Readability: the event whale gets a rim light in Glowing cyan and a hard ink outline; the cosmetic MID whale stays
desaturated and smaller so the two are never confused.

## Audio
| Cue | Id (proposal) | When |
|---|---|---|
| Ambience | `stage_ocean_amb` | muffled water, bubbles, slow sonar-like pads |
| Whale song | `stage_ocean_whale_call` | Telegraph start |
| Pressure | `stage_ocean_whale_descend` | low swell through Descend and Hold, filter opens as it nears |
| Crush | `stage_ocean_whale_hit` | first contact per fighter |
| Music | ethereal choir and harp; music ducks under the whale song |

## Narrative beats
The monument is the whale's home; it does not attack, it returns to rest on the old temple, and the fighters are in
the way. The rune glow on the tiers brightens during an event to show the escape route.

## Competitive notes
- Hazards OFF: symmetric, all-KO, legal neutral (recommended second starter).
- Hazards ON: the event forces both players down for 3.5 s near the bottom KO: high-risk counterpick.

## Edge cases
- Fighter respawning during the event: spawns at (0, -4300), invulnerable 90 ticks, ignores the whale.
- Fighter launched upward into the descending whale: crushed back down (12 %, once), then pushed while overlapping.
- Fighter at exactly X = 0 on MAIN: pushed toward +X (deterministic tie rule).
- Whale over a fighter on T1's outer part (X -8500..-9250): bottom at 600 never reaches feet -3100 + 1350; safe.
- KO by the whale push off the bottom: hazard KO credit rule (README 3.3).

## Tuning knobs
| Knob | Default | Safe range | Affects |
|---|---|---|---|
| Hold | 180 | 120..300 | time forced onto tiers |
| Descend speed / Rise speed | 40 / 60 milli/tick | 30..60 / 40..90 | warning time vs threat |
| Hold bottom Y | 600 | 300..1000 (< 1350) | MAIN unsafe while held |
| Damage / tier | 12 % / Med | 8..16 % | punishment |
| Delay base + span | 1500 + 601 | base >= 1200 | frequency |
| Top / bottom KO | 12000 / -11500 | 11000..15000 / -10000..-13000 | KO percents |

## Acceptance criteria
1. The first possible contact with a fighter standing on MAIN happens >= 120 ticks after Telegraph start (seeded test).
2. A fighter idling on MAIN through an event is hit once (12 %) and ends on T1/T2 or below, never stuck inside the whale.
3. Fighters on T1, T2, T3 for a whole event take 0 damage.
4. Respawn during the event happens at (0, -4300) and lands on T3.
5. T1 -> MAIN and T3 -> T1 succeed with a double jump for the slowest class.
6. All four KO lines trigger at the listed values (strict compare).
