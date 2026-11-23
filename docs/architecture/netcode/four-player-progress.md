# Four players, Phase 1 progress (engine for N fighters, 1v1 bit-identical)

Spec: `four-player-design.md` sections 1, 1.1-1.5, 6, 7, 9. Rules: `.claude/rules/netcode-core.md`.
Status: Phase 1 code complete, waiting for the lead's second Unity test run. Nothing committed (the lead verifies in Unity first).

## Design used (keep to it so 1v1 goldens never move)

- `ChibiSimState.MaxFighters = 4`; `F` always has 4 slots; `PlayerCount` (2..4) is set by Init. `CopyFrom` copies all 4.
- New config fields: `Class2/3`, `Passive2/3`, `PlayerCount`, `Mode` (`ChibiMatchMode.FreeForAll` = 0 default, `Team` = 1),
  `TeamOf` (bit i = team of fighter i). Mixed into `ComputeHash` only when `PlayerCount > 2 || Mode != 0 || TeamOf != 0`.
- New fighter fields: `HeldBy`, `HoldTarget`, `MoveHitMask`, `LastHitBy`, `ProgTarget` (victim of a grab / kick program).
  `Held`, `Holding`, `MoveHit` become get-only properties (`HeldBy >= 0`, `HoldTarget >= 0`, `MoveHitMask != 0`), so the
  old hash positions mix the same 0/1 values.
- New projectile field `HitMask` (bit per victim). `FHasHit` is kept (the view reads it) and set/cleared together with it.
- New state fields: `PlayerCount`, `Mode`, `TeamOf`, `WinnerMask`, `Place[4]`.
- **Hashing.** 1v1 (`PlayerCount == 2`): fighters 0..1 hashed as before; all new values are mixed at the END of
  `ComputeChecksum` and **only when they differ from their 1v1 canonical value** (foe index fields in {-1, 1-i}, masks in
  {0, 1 << (1-i)}, Mode 0, TeamOf 0, WinnerMask / Place equal to what Winner + MatchOver imply). So a 1v1 run mixes
  nothing new (goldens unchanged) while the 1v1 reflection tests still see every new field change the checksum.
  N > 2: hash `PlayerCount` fighters and mix every new field unconditionally.
- Helpers: `IsFoe(s,i,j)` (j != i, j < PlayerCount, other team in Team mode), `IsEnemy` (= IsFoe and InPlay),
  `NearestEnemy(s,i)` (smallest dx²+dy² as long, lowest index on tie, -1 if none),
  `Foe(s,i)` (1v1: always 1-i, exactly like today; N>2: nearest enemy, else nearest foe ignoring InPlay).
- Melee: probe all (attacker i, victim j) pairs into a stackalloc PendingHit[16], then apply in attacker, victim order.
  Probe side effects that happen once per swing (LandHitPending, CounterStrike, dive pop) run once per attacker.
- Grabs: N == 2 keeps `if (g0 != g1)`; N > 2: candidate per grabber, mutual grab cancels both, else index order; a held
  fighter cannot be grabbed. `Hold` steals a victim from another holder (keeps the invariant).
- N > 2 match end: eliminated fighters (0 stocks) stay Out, `Place` = players left + 1 (same tick shares a place);
  FFA ends with <= 1 left, Team with <= 1 team left; timeout ranks by stocks then percent (team totals in Team mode).

## Checklist

- [x] 1. State arrays, config fields, CopyFrom, checksum (new file `ChibiSimulation.Players.cs`)
- [x] 2. `Step(s, ReadOnlySpan<ChibiInputPacket>)` + `Step(s, in0, in1)` wrapper (stackalloc, no allocation)
- [x] 3. Sim call sites: ChibiSimulation.cs, Percent.cs, Stages.cs, Hazards.cs, Movesets/ChibiSimulation.Movesets.cs
      (grep for `1 - ` in the sim finds no fighter lookups left; `ChibiTimeScaleParityTests.Snap` loops all 4 slots, harmless)
- [x] 4. Callers (N = 2 still the only count they run): `ChibiLocalSession` takes `IChibiInputSource[]` (2-source ctor
      forwards; new `OnFrameAll` event; reused input buffer), `ChibiCpuInputSource` targets `ChibiSimulation.Foe` and accepts
      players 0..3, `ChibiSimView` attributes hits with `Attacker()` (LastHitBy, else 1 - d).
      NOT changed on purpose: `ChibiP2PPeerLoop` (red zone, Phase 3), `ChibiReplay` file format (p0 / p1; the N-input
      format with a version byte is design test 9, Phase 3), HUD (4-panel HUD is Phase 2).
- [x] 5. Tests: `Assets/Tests/EditMode/Editor/ChibiFourPlayerTests.cs`
- [x] compile_check OK 0 (both assemblies), 2026-11-23
- Lead run 1 (steps 1-3): 1894 / 1920 passed, all NetcodeContractTests passed (goldens unchanged). Fixed after it:
  - `ChibiMultiPhaseParityTests` reflection helper: "Held" / "Holding" map to the int fields `HeldBy` / `HoldTarget`;
    `Held()` / `Holding()` read the properties (24 tests).
  - 1v1 behaviour change found and removed: `ProjHitFoes` gated each victim on `HitMask` also in 1v1. A test helper
    (`Shot`) reuses a pool slot without clearing `HitMask`, so a stale bit blocked the Spin Guard destroy. 1v1 now gates
    on `FHasHit` only (exactly v24); `HitMask` gates only when PlayerCount > 2.
  - `NearestEnemy` test setup was wrong (F[1] was nearer to F[3] than F[0]); expectation corrected, exact tie added.
- Test edit: `ChibiCpuPositioningTests.MoveOut` now writes `MoveHitMask` (MoveHit is read-only). No golden touched.

## Left for later phases

- Phase 2: friendly-fire rules in Team mode are already enforced by `IsFoe`; still to do: spawn points from level design,
  pool sizes (clouds 8 / minions 24), 4-panel HUD, camera, device join, CPU team awareness, RulesVersion 25 + new goldens.
- Phase 3: `Step` presentMask, N-stream input buffer, replay N-input format, P2P loop.
- `docs/Network Engineering Summary.md` not updated: no version, protocol or online-play change in Phase 1.

## 1v1 risk spots

- `Foe()` must return `1 - i` in 1v1 even when the opponent is out (today's code reads the dead opponent's X).
- Grab slam / kick hits for N > 2 check `ProgTarget`; in 1v1 that check is skipped.
- Projectile per-victim gate uses `HitMask` instead of `FHasHit`; equal in 1v1 only while every FHasHit clear also clears
  HitMask (return start, Rearm, reflect, spawn).
- Test helpers that build projectiles by hand (no `SpawnProjectile`) leave `HitMask` stale: harmless in 1v1 now.

## Phase 1 verified (2026-11-23)

- Unity EditMode 1921/1921 (19 new ChibiFourPlayerTests; NetcodeContractTests goldens unchanged, RulesVersion 24).
- Play Mode: 1v1 Vs CPU on Mountain, 26 s, KO + respawn, no errors (`production/qa/evidence/2026-11-23-4p-phase1-1v1-cpu-fight.png`).
- network-programmer review: APPROVE WITH NITS (no 1v1 change, no hidden 1v1 desync).

## Must fix in Phase 2 (from the Phase 1 review)

1. Hold steal (ChibiSimulation.cs ~2194): `Release(old holder)` leaves MovePhase=1 / ProgTarget, so its grab slam can hit a victim it no longer holds. Interrupt the old holder or clear its program.
2. ChibiSimView.cs ~859: attacker fallback `1 - d` is -1 for d >= 2 (hazard hits do not set LastHitBy) -> F[-1]. Guard it.
3. LastHitBy is never reset on KO / respawn: reset it before using it for KO credit.
4. Players.cs ~310: Team timeout counts eliminated members' frozen PctM.
5. Players.cs ~349: Team draw keeps elimination places (3 / 4 shown in a draw).
6. Grab chains (~2120): grabbing a fighter that holds someone is allowed and index-order dependent: add a test.
7. Pierce shots: once a pierce shot hit fighter j, j can no longer destroy / reflect it (~2665): decide (recommended: j can still reflect/destroy).
8. Drop the "1v1 canonical" checksum trick when Phase 2 moves to RulesVersion 25 (re-record goldens on purpose).
9. Nits: misplaced doc comments (ChibiSimulation.cs ~2650-2659 ProjHitFoes / SplitVolley; ChibiSimView.cs ~852-856), Step doc example allocates.
