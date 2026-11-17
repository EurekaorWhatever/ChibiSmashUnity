# Netcode design notes (2026-11-06)

Drafted by studio agents and reviewed in session. **The user decisions below override anything in these docs that disagrees.**

## User decisions
| # | Decision |
|---|---|
| Q1 | One version of the game: the original full-feature `Fighter` game, made deterministic so it runs online. No feature may be dropped. No second gameplay implementation. |
| Q2 | Private rooms: players join with the **Lobby** code (not the Relay code). |
| Q3 | Input packet grows to 16 bits so Dash and gesture inputs go online. |
| Q4 | Mid-match disconnect: the opponent wins automatically. |
| GL-1 (2026-11-11) | Damage percent + 3 stocks replace the HP win condition (design/gdd/percent-knockback.md, [ADR-0001](../adr-0001-damage-percent-and-stocks.md)): existing damage pipeline unchanged, integer Smash knockback from percent / hit percent / weight / base + growth derived from KbX-KbY, KO only on an active stage edge. RulesVersion 20. |
| GL-2 (2026-11-11) | Percent spec choices: one rule Pct = HP x 0.2 for every hit; Ren / Lyra knockback x0.85 (was x0.5); 300 s timer, 3 stocks; final timeout tie = draw; hit percent capped at 15 % inside the formula. Blast lines: sides 8000-10000 beyond the ledge, top = max(10000 above MAIN, highest platform + 5000). |
| ST-1 (2026-11-11) | Five design stages added as indices 3..7 (Mountain, Plain, Volcano, Bird Lair, Ocean); 0..2 stay as "Classic" for tests and replays. |
| ST-2 (2026-11-11) | KO edges follow the sheet matrix; a "no KO" edge is solid (Mountain side walls; Plain / Bird Lair: no top line with a sky cap, solid floor). Per-edge flags in `ChibiStageDef` ([ADR-0002](../adr-0002-stages-as-data-and-deterministic-hazards.md)). |
| ST-3 (2026-11-11) | Moving platforms (pure F1 of GameTicks) and hazards (fire, bird swipe, whale) run in the deterministic sim, state hashed. |
| ST-4 (2026-11-11) | Stage art = the sheet illustration cut into layers; sprite positions from the same art-measured geometry (`stage-geometry.json`) the sim uses; drift check `tools/ci/check_stage_geometry.py`. |
| ST-5 (2026-11-11) | Hazards use their own RNG stream (Seed XOR "STAG"); ON by default; `HazardsOff` in the config hash and the Start message (protocol 4), host decides; platforms move with hazards off; bird alternates direction; symmetric spawns. |
| AN-1 (2026-11-11) | Move animations use the sheet's side-view pose drawings as key frames (view only, green zone); cutout tweens stay as fallback. |
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

**Developers start here:** [DEVELOPER-GUIDE.md](DEVELOPER-GUIDE.md) shows where to build freely (UI, view, menus,
content), the determinism rules for gameplay, and the tripwire tests that protect the online core.

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
| [gesture-conversion.md](gesture-conversion.md) | step 15 | Ult gesture window, CrossArms block, Super Jump, Pose Fireball, security rules applied |
| [match-flow-conversion.md](match-flow-conversion.md) | step 16 | Intro, match clock, KO / timeout winners, training, pause |
| [cpu-ai-conversion.md](cpu-ai-conversion.md) | step 17 | CPU as a deterministic input source |
| [verification-report.md](verification-report.md) | step 19 | Sync, replay, state-coverage and per-class verification results |
| [online-rooms.md](online-rooms.md) | online | Lobby code rooms, matchmaking, Relay, LAN, results on disconnect, setup steps |
| [../adr-0001-damage-percent-and-stocks.md](../adr-0001-damage-percent-and-stocks.md) | technical-director | ADR: percent + stocks replace HP, integer Smash formula, RulesVersion 20, risks and knobs |
| [../adr-0002-stages-as-data-and-deterministic-hazards.md](../adr-0002-stages-as-data-and-deterministic-hazards.md) | technical-director | ADR: stages as data rows, per-edge KO flags, F1 moving platforms, hazard RNG stream, HazardsOff, geometry drift check |
| [../../../design/gdd/percent-knockback.md](../../../design/gdd/percent-knockback.md) | game-designer | Percent, knockback, weight, hitstun, stocks spec (formulas, tuning knobs, acceptance criteria) |
| [../../../design/gdd/stages/](../../../design/gdd/stages/README.md) | level-designer, technical-artist | Shared stage rules (README) + one file per stage; `stage-geometry.json` = art-measured geometry (generated) |

## Known bugs (both fixed)
- ~~Packets over 32 bytes silently truncated~~: fixed in online part 1 (codec throws, receivers reject, transport drops oversize). See online-rooms.md section 4.
- ~~Resend window only the newest 8 inputs (lag stall)~~: fixed in online part 1 (oldest unacked first, up to 16). See online-rooms.md section 4.

## Known issues (deferred)
- SEC-002 (deferred by the lead until ranked / wagers exist): the host alone picks the match seed (crits, hazard timing); a joiner nonce mixed into the seed needs another protocol bump.
- SEC-003 remainder (known limit, 2026-11-16): a losing peer can still turn a match into a no contest by sending `Leave(Desync)` or a wrong checksum. With two peers and no authority, a faked desync cannot be told from a real one. Closing it needs a server-side replay check (netcode-architecture.md 7.6). What protocol 5 did close: `Leave(MatchComplete)` followed by a drop is now scored as a disconnect (the local player wins) instead of a no contest.
- Disconnect scoring (known limit, protocol 5): a peer whose last 1.5 s of packets are all lost after a real match end is scored as a disconnect by the other side, and both sides can still each see "opponent disconnected" when the link dies both ways. A match that is stalled for more than two thirds of the last 20 s (`ChibiRollbackSession.StalledInWindow` over `StallLimit`; about 500 ms RTT with loss or 650 ms clean, sustained, or repeated long freezes) ends as no contest for both sides: the side that was waiting sends `Leave(Stalled)`. If all three copies of that goodbye are lost, the other side falls back to the silence timeout and records a win. Measured stall share on the loopback link: 300 ms RTT + 15 % loss 26 %, 400 ms 33 %, 500 ms 47 %, 670 ms 60 %, 1 s 73 %. Accepted risk: a peer can still slow a match to about a third of full speed without ending it (a half-speed peer cannot be told from an honest 500 ms link); the sim timer still ends the match.
- NET-03 remainder (known limit): a joiner can see the host's class and stage in Start, send Ready and then never send input; the host records no contest (`PeerNeverStarted`) instead of a win. Repeat offenders would have to be tracked at the room or social layer.
- Local link loss (open decision, predates protocol 5): when OUR transport reports `Lost`, the match is still scored as a win (`ConnectionLost`), so a player who cuts their own connection while losing records a win locally. Scoring a local link loss as no contest is a design decision (it changes decision Q4).

## Protocol 5 (2026-11-16, netcode hardening)
Same wire layout as protocol 4; the rollback and result rules changed, so old and new builds refuse each other.
- NET-01: only a new remote input frame resets the silence timer (a silent peer loses by timeout as before). A peer that drip-feeds inputs trips the stall window (stalled more than two thirds of the last 4 x DisconnectTicks), which ends the match as no contest (`OpponentStalled`, decision 2026-11-16).
- NET-06: the resend window is clamped to the input ring, so a peer that never acks can no longer make `SendPacket` throw.
- NET-02: `DisconnectTicks` is capped at 600 (10 s) in `ChibiRollbackSettings.Validate`, so a joiner rejects a larger value in Start.
- NET-03: a session that never received a real remote input frame ends as no contest (`PeerNeverStarted`), not a win.
- NET-04: `LastFrame` only moves the time-sync estimate when it is within `MaxInputs` (16) of the contiguous remote frame. A sanity bound, not tamper-proofing: the `Advantage` field is unauthenticated as well.
- NET-05: `ChecksumsCompared` counts each checksum frame once.
- REP-01: `ChibiReplay.TryRead` range-checks class, passive and stage; `Verify` returns false instead of throwing. Replays now store `HazardsOff` (flags bit 2), so hazards-off replays load.
- Training matches no longer record a replay (the T ult fill is outside the input stream).

## Rules versions 22 and 23 (protocol 5 unchanged)
A rules bump changes the config hash and the Hello rules field; peers with different rules refuse each other
(`RulesMismatch`), and the room tag (`p5r22` / `p5r23`) keeps them in separate rooms. The wire layout is unchanged.
- **v22 (2026-11-14):** the Ult press fires the ultimate on a full meter; recovery trimmed; 6-tick attack buffer
  (`BufferedEdges` / `BufferTicks`) and grounded walk / turn cancel (`AttackLockTicks`): three new hashed fighter fields.
- **v23 (2026-11-17, review batch 4):** trip stun per row, Bruna down smash sideways, walk cancel "on hit" = landed or
  blocked (new hashed `MoveConnected`), dash-tail attack buffer. Goldens re-recorded: config hash `0x8E6FB4A6`, fixtures
  `0xC589DD73` / `0xCADE4CC6`, Hello `c9517001208922ae1a`.
- Local only (not on the wire): `ChibiPlayerInputSource.Muted` sends neutral packets while a menu overlay (online LEAVE
  MATCH prompt) owns the keys.

## Superseded in these docs
- Any recommendation to show the **Relay** join code (Q2 chose Lobby).
- `mode3-ai-input.md` option (A) "run ChibiSimulation directly": still valid, but the sim must first reach full feature parity with the original (Q1).
