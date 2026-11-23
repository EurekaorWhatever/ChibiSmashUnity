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

## Phase 2b: presentation for 2-4 fighters (view, HUD, camera, results, dev start)

Scope: `Netcode/View/*`, `Game/*` (ChibiMatch, MatchConfig in MatchController.cs, GameManager dev entry), `UI/Hud.cs`.
The sim is only READ (another agent changes the rules in parallel). Nothing committed; the lead verifies in Unity.

- [x] 1. MatchConfig (4 slots + `players`, `team`, `teamOf`), ChibiMatch (`PlayerCount`, `TeamMode`, `TeamOf(i)`, sources per
      slot, CPU per slot, WinnerMask rewards), `ChibiLocalLoop.Begin(cfg, IChibiInputSource[])` (N > 2 not recorded: the
      replay format is 2 streams). A 1v1 leaves every new sim config field at default, so its hash is unchanged.
- [x] 2. ChibiSimView + ChibiSimEvents/LaunchFx/ImpactFx/PostFx/BladeTrails/Audio(Rules): arrays of MaxFighters, loops to
      `ChibiSimEvents.Fighters(state)` (1v1 = 2, so 1v1 runs the same code); `Attacker()` returns -1 for an unknown
      attacker when N > 2 (review item 2), callers skip / use white; Kick bursts at ProgTarget; fighters 2/3 share
      sort bases 10/30 (`FighterSortBase`); team winners all pose. compile OK 0.
- [x] 3. HUD (`UI/Hud.cs`, `UI/Gui.cs`): N > 2 draws compact panels along the bottom (portrait, name tag, character, percent,
      stocks, ult) in the 1v1 panel style; colours `Gui.PlayerColor` P1 red / P2 blue / P3 yellow / P4 purple, Team mode =
      team frame (red / blue) with the player strip inside; name tags in `Hud.SlotColor`; eliminated = grey veil + "OUT · 3RD".
      1v1 path unchanged (key hints hidden only when N > 2). Static per-fighter arrays sized 4; P3/P4 key / pose labels guarded.
- [x] 4. Camera: FrameFighters loops all fighters not Out; zoom cap 13 (was 10) when N > 2, still clamped by MaxSize/WithinArt
- [x] 5. Results for N > 2 (`Hud.MultiResults`, offline only): title = winner / "RED TEAM WINS!" / "DRAW!" from WinnerMask,
      rows in `Place` order (shared places shown), rewards box and buttons as 1v1. Rewards: win = own bit in WinnerMask.
- [x] 6. Dev-only `GameManager.DevQuickMatch4(players, team, stage)` + inspector context menu items (editor / dev build)
- [x] 7. compile_check OK 0 (both assemblies), 2026-11-23. Not yet run in Unity (no screenshot yet).

## Phase 2a (simulation rules, RulesVersion 25), code complete 2026-11-23, waiting for the lead's Unity run

Scope: sim only (ChibiSimulation*.cs, state, config, Movesets, stage spawns) + EditMode tests. View / HUD / Game / ChibiMatch belong to the parallel agent.
Local test harness: the compile_check DLLs run under the editor's Mono (`MonoBleedingEdge/bin/mono.exe`) with a small
reflection runner (scratchpad, not committed): 1223 sim / netcode tests pass there; 33 fail only because Unity-only
assemblies are missing (audio, JSON, UTP, TestRunner). `[TestCaseSource]` tests are NOT run by that runner.

- [x] Must-fix 1 hold steal (Hold clears the old holder's ProgTarget), 3 LastHitBy cleared in Respawn, 4 Team timeout counts
      only members still in, 5 Team draw gives every member place 1, 6 grab chains order-independent (a grabber that is
      another grabber's candidate this tick whiffs; same victim: lower index wins), 7 pierce: with 3-4 fighters a foe already
      hit can still destroy / reflect the shot (1v1 keeps the original HasHit gate), 8 canonical hash dropped (all new fields
      always hashed; config mixes seats 2..3 only when they play), 9 sim nits (SplitVolley doc, Step example, duplicate
      RulesVersion summary)
- [x] presentMask: `ChibiSimState.PresentMask` (copied, hashed), `ChibiSimulation.StepPresent(s, inputs, presentMask)` (named
      apart from Step because `Step(s, default, default)` became ambiguous); a cleared bit = LeaveMatch (GoOut + Stocks 0) on
      that tick, sticky, ignored in training; `Eliminated()` also freezes leavers in 1v1
- [x] pools 128 / 24 / 8 (were 64 / 12 / 4, sized for the worst 1v1); tests P19 and S32 constants updated
- [x] 3P / 4P spawns: `SpawnOuterX` / `SpawnInnerX` per stage + `SpawnPoint()` in ChibiSimulation.Stages.cs (1v1 unchanged);
      `RespawnSpreadX` {-1200, 1200, -400, 400} for 3-4 fighters; tables in the config hash
- [x] RulesVersion 25 (remarks 25 in ChibiSimulation.cs); ChibiEscapeDashTests pin relaxed to >= 24
- [x] goldens re-recorded ON PURPOSE (NetcodeContractTests, remark "re-recorded for RulesVersion 25 (4-player rules,
      2026-11-23)"): config 0x3A6558E2, A 0x22B0CD26, B 0x1DF9931C, Hello hex c9519001208922ae1a (rules byte only);
      new fixtures C (2v2 Team, Mountain, 600 ticks) 0x6A062411 and D (4P Battle Royale, Volcano, 600 ticks) 0x0D9BD502,
      plus a run-twice test. Values computed with the Mono harness: the lead's Unity run must confirm them.
- [x] new tests `ChibiFourPlayerRulesTests.cs` (28): friendly fire (shot + melee), FFA order / places / same-tick share,
      draws FFA + Team, Team win, timeout FFA + Team (eliminated % ignored), leavers (FFA, 1v1, Team, determinism, holder),
      grab chain / cycle / same victim / grabbing a holder, hold steal, pierce destroy + no double hit, LastHitBy reset,
      spawns 1v1 kept + 3P / 4P on all 8 stages, respawn spread. The regression tests were watched failing against the
      unfixed code (mutation run): team draw places, team timeout, grab cycle / chain stun, hold steal, pierce destroy,
      LastHitBy.
- [x] `docs/Network Engineering Summary.md`: rules v25 line.

Left for others: must-fix 2 (ChibiSimView attacker fallback `1 - d` for d >= 2) and the ChibiSimView doc nit of item 9
are View (parallel agent). KO credit (counting KOs per attacker from LastHitBy) is not added. Phase 3 wires
`StepPresent` into the online session.
- Item 2 and the ChibiSimView part of item 9 are View (other agent): not touched here.

### Phase 2b notes for the lead

- Start a test match in Play Mode: select the GameManager object, Inspector right-click (or the component's three-dot
  menu) > "DEV: 4-player Battle Royale (Mountain)" / "DEV: 3-player ..." / "DEV: 2v2 Team (Mountain)"; or from code /
  unity-mcp: `Object.FindAnyObjectByType<GameManager>().DevQuickMatch4(4, team: false, stage: 3)`. P1 = keyboard / pad 1,
  P2-P4 = CPU (difficulty from settings). Editor or development build only; no menu entry.
- Rematch from that results screen starts a 1v1 (DevQuickMatch4 puts `pending` back to 2 players).
- Edited outside View/Game on purpose: `Netcode/ChibiLocalLoop.cs` (array `Begin` overload, polls every source; N > 2 not
  replay-recorded), `UI/Gui.cs` (P3 / P4 colours). No sim file touched.
- Known gaps: human players 3-4 need the device-join screen (`Bindings.Default` has layouts for players 0-1 only, so a
  HUMAN slot 2/3 would throw in `Bindings.Get`); fighters 2/3 share sprite sort bases with 0/1; CV overlay and gesture
  prompts still cover P1 / P2 only; ChibiSimTestHud stays 2-player; Phase 1 review items 1, 3-9 are sim-side (other agent).
- Evidence still to take: Play Mode screenshots of the 4-panel HUD, 2v2 colours, 4-fighter camera, results.
