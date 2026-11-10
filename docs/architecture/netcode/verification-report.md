# Port step 19: verification report (2026-11-09)

Rules version 17. Unity 6000.6.7f1. Tests: EditMode, run through the Unity editor.

## 1. Result

| Check | How | Result |
|---|---|---|
| Whole suite | all EditMode tests | **763 / 763 pass** |
| Two peers stay in sync | `ChibiSyncVerificationTests.TwoPeers_HostileLink_*`: 15 configs (each class as P1, every stage), 2 rollback sessions over a link with 6 ticks latency, 2 jitter, 5% loss, production intro, 2400 ticks, a bot pressing every input bit incl. Dash, Shield, Skill1-3, Ultimate, all 3 gestures and (protocol 3) the Special button on bit 15 plus aim up | no desync, no disconnect, identical confirmed inputs on both peers, and an offline replay of the confirmed inputs reproduces each peer's confirmed state checksum exactly |
| Ultimates under rollback | `TwoPeers_UltimatesUnderRollback_StayInSync`: 5 pairings, training mode, full meters | ults fired through gesture windows, cinematic slow-mo and hitstop with rollbacks running; no desync; re-simulation equals the confirmed state |
| Rollback restores everything | `SnapshotRestoreResimulate_AnyDepth`: every class, depths 1/3/8/15, wrong-input drills every 97 frames | resimulated state identical to a straight run every frame |
| No hidden state | `CopyFrom_CopiesEveryField_OfEveryStateClass` (reflection, every field of fighter / projectile / minion / cloud / match state) and `Checksum_ChangesWhen*` (every field must change the checksum) | pass. Mutation check: deleting one `CopyFrom` line (`Revives`) makes the test fail and name the field |
| Every class plays a full match | `CpuVsCpu_*`: all 25 class pairings, CPU difficulty 3, production intro, to KO or time-out | every match ends cleanly, no projectile or summon pool overflow, HP/meter in range, held invariant holds, replay reproduces the final state |
| Original numbers | the per-feature parity suites (movement, dash, stages, normals, damage, time scale, projectiles, multi-phase, minions/cloud, ultimates, gestures, match flow) | pass; each pins the original's constants from Fighter.cs / MatchController.cs |

Coverage measured on the sync matrix (5 configs, 2400 ticks each): 38 Pose Fireballs, 43 Super Jumps, 115 dashes,
2775 rollbacks, deepest rollback 8 frames (the MaxPrediction limit).

## 2. Play-through of each class (run and observe)

Real game path: `GameManager.QuickMatch` → `ChibiMatch` → `ChibiLocalLoop` + `ChibiSimView` + `Hud`, in Play Mode.
Screenshots in `production/qa/evidence/`:

| Class (P1) | Opponent (CPU) | Stage | Screenshot |
|---|---|---|---|
| Melee (Kira) | Magic | Sky Dojo | step19-class-melee.png |
| Heavy (Brutus) | Ranged | Neon City | step19-class-heavy.png |
| Ranged (Pip) | Polearm | Lava Forge | step19-class-ranged.png (K.O. on screen) |
| Magic (Luma) | Melee | Sky Dojo | step19-class-magic.png (ring-out, Thrust Barrage) |
| Polearm (Ren) | Heavy | Neon City | step19-class-polearm.png |

CPU vs CPU through the same host (900 ticks each): all classes landed hits, combos (best combo up to 16) and charged ult
meters; no dropped shots or summons.

## 3. Known limits

- A human playtest with real hands on the controls and a real webcam is still the final word on feel; the automated
  checks prove sameness (determinism, parity numbers), not fun.
- Training `T` (ult fill) is out-of-band and not replayed (training is offline only).
