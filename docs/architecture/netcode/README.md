# Netcode design notes (2026-11-06)

Drafted by studio agents and reviewed in session. **The user decisions below override anything in these docs that disagrees.**

## User decisions
| # | Decision |
|---|---|
| Q1 | One version of the game: the original full-feature `Fighter` game, made deterministic so it runs online. No feature may be dropped. No second gameplay implementation. |
| Q2 | Private rooms: players join with the **Lobby** code (not the Relay code). |
| Q3 | Input packet grows to 16 bits so Dash and gesture inputs go online. |
| Q4 | Mid-match disconnect: the opponent wins automatically. |
| — | Rollback engine: keep the existing `ChibiRollbackSession`; no second rollback engine. |
| D-G1 (2026-11-09) | Gesture controls (gesture-conversion.md): Ult button opens the 3 s window (180 real ticks, 1.5 s retry); class gesture held 9 real ticks fires the ult even while stunned or held (holder keeps holding, like the original); CrossArms blocks like Shield; RaiseHands Super Jump; PunchForward Pose Fireball; sim never reads PoseVerified. Replaces D-U1's stand-in. RulesVersion 15. |
| D-F1 (2026-11-09) | Double KO on the same tick is a draw (user choice). |
| D-U1..U4 (2026-11-08) | Ultimates (ultimates-conversion.md incl. technical-director review amendments 1-8): D-U1 = A, the ult stand-in button keeps firing only when free until step 15 (full common start built now: Interrupt, Stun = 0, invuln); D-U2 = A, Sword Aura and Fire Ring start instantly with no lock like the original; D-U4 = A, Polearm Kick on an invulnerable victim in range still holds/stuns with no damage like the original (balance note). Technical default D-U3 = A (4 Kick lerp fields). Ult frame-0 velocity writes are kept (original fires the ult before the F:178 velocity read). RulesVersion 14. |
| D-C1..C5 (2026-11-08) | Minions + Thunder Cloud (minion-cloud-conversion.md incl. technical-director review amendments 1-9): D-C1 = A, copy the original's ground friction (≈3.26 u/s walk, 50 Hz basis) incl. wall cling and the landing-step friction; D-C4 = A, parrying a minion/cloud hit punishes the Mage like the original (user-chosen). Technical defaults taken as recommended: D-C2 = A (minion stands while its centre is over a surface), D-C3 = A (order fighters → projectiles → minions → clouds, by slot), D-C5 = A (cloud position in 1/1000 milli, round half away). Pools MaxMinions 12 / MaxClouds 4 + DroppedSummons. RulesVersion 13. |
| D-M1..M3 (2026-11-08) | Multi-phase moves (multiphase-conversion.md incl. technical-director review amendments 1-9): D-M1 = A, press priority copies the original Light > Heavy > skills (ult kept as today's stand-in until step 15, never swallows another press when the meter is not full); D-M2 = B, Leap Slam really leaps (do NOT copy the original's overwritten-velocity glitch; slam on landing, tick 10 earliest, 78 timeout; user accepts the new air boost); D-M3 = B, mutual same-tick grabs both miss. RulesVersion 12. |
| D-P1..P3 (2026-11-08) | Projectiles (projectile-conversion.md incl. technical-director review amendments 1-8): D-P1 = A, split spread by rejection sampling inside the circle (max 16 tries, sim RNG); D-P2 = A, MaxProjectiles 64 with an in-state dropped-shot counter; D-P3 = A, Mage seeking-orb stand-in uses the new 2D homing. RulesVersion 11. |
| D10 (2026-11-08) | Slow-motion: option A-held (timescale-conversion.md §3/§5 + technical-director review §8): integer accumulator runs one game step per 1000 permille; presses on frozen real ticks count at the next game step only (then D-N1 rules apply); Block latched as held; view interpolation per §8(b); replay format v2; all gameplay reads of s.Frame move to GameTicks. |
| D9-1 (2026-11-08) | Damage: option (a) — HP and guard stored in micro-HP, damage computed exactly in nano units, defense in hundredths, one final rounding (damage-conversion.md §8). Matches the original's float HP. |
| D-N1 (2026-11-08) | Input: NO input buffer, like the original (normals-conversion.md option A). A press while busy (attack lock, dash, hitstun, reload) is lost. Applies to all action presses; skill/ult priority order is aligned in step 12. |
| D-S1/S2 (2026-11-07) | Stages (stage-conversion.md): do NOT copy the "grounded inside a platform slab" glitch — grounded only when truly resting on a surface (C50 arc term gated on resting on a surface); platforms use the clean rule "falling onto the top from above always lands" (no Box2D slip-through). |
| D2 (2026-11-07) | Dash: copy the original at 60 Hz (dash-conversion.md option A): 8 DashTicks, ~1983 milli on the ground. |
| D1 (2026-11-06) | Movement: add the 50 Hz arc-correction term (movement-conversion.md decision D1) so 60 Hz jump/fall arcs land on the original's 50 Hz Box2D arcs. Velocities move to exact 1/900 milli/tick units. |

## Files
| File | Author agent | Contents |
|---|---|---|
| [netcode-architecture.md](netcode-architecture.md) | technical-director | Topology, data flow, 3 modes, CV gesture design, packet budget, determinism risks |
| [rollback-gap-report.md](rollback-gap-report.md) | network-programmer | How `ChibiRollbackSession` meets the rollback spec, plus gaps and bugs |
| [mode3-ai-input.md](mode3-ai-input.md) | ai-programmer | Vs-AI input injection design |
| [ugs-api-check.md](ugs-api-check.md) | unity-specialist | UGS / Transport API signatures verified against installed packages |
| [feature-inventory.md](feature-inventory.md) | gameplay-programmer | All 148 features of the original game, determinism status, port order |
| [security-gesture-review.md](security-gesture-review.md) | security-engineer | What a modified client can fake (gesture bits, PoseVerified); rules for step 15; PoseVerified is self-reported only |
| [movement-conversion.md](movement-conversion.md) | systems-designer | Step 4: every original movement value converted to sim units, precision scheme, reference test values |

## Known existing bugs (confirmed in code)
- Packets over 32 bytes are silently truncated: `BitBuffer.cs:60`, unchecked at `ChibiP2PPeerLoop.cs:301`.
- Resend window is only the newest 8 inputs (`ChibiP2PPeerLoop.cs:278-281`); a lag of more than 8 frames on acks can stall both peers until disconnect.

## Superseded in these docs
- Any recommendation to show the **Relay** join code (Q2 chose Lobby).
- `mode3-ai-input.md` option (A) "run ChibiSimulation directly": still valid, but the sim must first reach full feature parity with the original (Q1).
