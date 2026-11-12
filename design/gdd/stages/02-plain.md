# Stage 4: Plain of Unity (spec v1, 2026-11-11)

Source: `design/art/maps/02-plain-of-unity.jpg`. Shared rules: `README.md`. Numbers = proposed targets.

## Overview
| Item | Value |
|---|---|
| Theme | Endless meadow, huge cumulus clouds, sun rays, lone trees, floating petals |
| Mood | Peaceful but epic |
| KO | **Horizontal KO**: left and right KO; top none (fall back); bottom = solid ground |
| Event | None |
| Play time | One match (up to 3:00) |
| Role | Neutral starter: flat, symmetric, no platforms, no hazards (the "final destination" stage) |

## Layout (not to scale)
```
 16000 - - - - - - - - - SkyCapY (no KO: fighters stop rising and fall back) - - - - - - -
       K                                                                              K
       O                                                                              O
     0 K[==================================== MAIN (ground) ===========================]K
       solid below (no bottom KO)
     X: -15000 (KO)                          0                                 +15000 (KO)
```

## Bounds and platforms
| Id | Kind | Cx | Top | HalfW | Notes |
|---|---|---|---|---|---|
| MAIN | solid floor | 0 | 0 | 15000 | flat ground to the KO lines; grass fades at both ends |

| Bound | Value |
|---|---|
| Side KO | feet \|X\| > 15000 (ground ends exactly at the KO line: walk-off) |
| Top | no KO, SkyCapY 16000 |
| Bottom | solid floor at 0, nothing below |

Why 15000: no fighter can fail a recovery here, so the only KO is a horizontal launch; the line sits slightly inside
Sky Dojo's 17000 so KO percents stay comparable. Whole width fits the camera at size 8.4 (16:9).

Reachability: nothing to reach (no platforms).

## Spawn, respawn, camera
| Item | Value |
|---|---|
| Spawn | (+-4000, 775) |
| Respawn | (0, 6000) |
| Art rect | X -17000..17000, Y -4000..17000; camera Y clamp keeps the ground in the lower third (centre Y >= 3500) |
| Max camera size | min(10, 10.5, 17/aspect) = 9.6 at 16:9 |

## Parallax and look
| Layer | Factor | Content (sheet) |
|---|---|---|
| FAR | 0.10 | bright blue sky, huge cumulus clouds, sun rays, distant rolling hills; clouds drift (view only) |
| MID | 0.40 | nearer hill, a few lone trees, tall grass blowing, wildflowers |
| Gameplay | 1.00 | flat ground strip and its top edge line |
| FRONT | 1.15, alpha <= 0.40 | front grass blades, wind streaks, petals, below Y -300 only; faded edges near the KO lines |

Palette: Emerald `#199A4A`, Sky blue `#1B8CF0`, Sunlit yellow `#FFF0A3`, Lit grass `#C9F35F`, Petal `#FFC2D6`.

Readability: the KO line must be visible although the ground continues in the art: grass fades to pale at
|X| 13500..15000 and a faint petal-swirl marks the line.

## Audio
| Cue | Id (proposal) | When |
|---|---|---|
| Ambience | `stage_plain_amb` | wind over grass, skylarks, distant chimes |
| Music | broad, hopeful theme with a strong melodic build; full arrangement at the last stock |

## Narrative beats
A meeting ground ("Unity"): open, nothing to hide behind, a fair duel under the sky.

## Pacing
No event. Pure player-driven pressure toward the edges.

## Competitive notes
- Neutral starter (recommended first stage for ranked and online quick play).
- Walk-off consequences: a cornered fighter cannot escape below or around; edge camping is weaker than on platform
  stages because the camper stands at the KO line. Lead may add a ranked rule later (for example, no ledge
  invincibility exists anyway). Hazards OFF changes nothing.

## Edge cases
- Fighter walks or dashes past X 15000: KO (self-destruct).
- Downward knockback (meteor) on the ground: lands, Vy = 0; no bounce unless percent-knockback.md defines one.
- Fighter launched at very high Vy: clamped at SkyCapY then falls; the off-screen marker shows it.
- Projectiles leave through the side: despawn at ProjBoundX as today.

## Tuning knobs
| Knob | Default | Safe range | Affects |
|---|---|---|---|
| Side KO / ground half-width | 15000 | 13000..17000 | KO percent |
| SkyCapY | 16000 | 14000..20000 | time spent above the camera |

## Acceptance criteria
1. Fighter walking right reaches X 15000 on the ground and is KO'd on the first tick X > 15000.
2. Fighter with Vy 900000 never exceeds Y 16000 and lands on the ground; no KO.
3. No tick ever has a fighter with feet Y < 0 (solid floor).
