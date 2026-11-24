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

## Phase 3: online 2-4 players (star through the host, protocol 6), code complete 2026-11-23, waiting for the lead's Unity run

Spec: `four-player-design.md` sections 2.1-2.5, 3, 7, 9. Rules: `.claude/rules/netcode-core.md` (red zone: needs the
network-programmer and security-engineer reviews before commit). Nothing committed; the lead verifies in Unity.

Design used:
- Session-level star: a guest sends ONE block (its own inputs) to the host; the host sends each guest one block per
  other player, read from its own input ring (so the host also repairs loss on forwarded streams). The host never
  changes an input; it only forwards actual inputs. 1v1 = the same star with one guest (= the protocol 5 exchange).
- Protocol 6 input datagram: blocks (slot, lastFrame, count, inputs, checksum), ack of the receiver's stream, extra
  acks (guest to host), leave frames (host only). `MaxPacketBytes` 64 -> 216 (worst case 209 B, mux limit 256).
  1v1 readers ignore the slot (one remote), so old 1v1 test messages still parse.
- Leaves (3-4 players): only the host declares (`DeclareLeft`, its own per-guest timeout, or the stall limit on the
  slowest guest). Leave frame = first frame the host does not hold for that player; it rides in every host datagram;
  every peer clears that presentMask bit from it on (`RollbackInputBuffer.SetLeft`, rollback if needed).
  A guest that sees its own slot in the leave mask was dropped. Host gone = no contest.
- Guests (3-4 players) treat any accepted host datagram as life (the host may stall on a slow guest); their stall
  limit is 5/6 of the window so the host acts first.

Checklist:
- [x] 1. Codec: protocol 6 input datagram (`ChibiInputPacket.cs`), compile OK 0 both assemblies
- [x] 2. N-stream `RollbackInputBuffer` (per-stream contiguous / prediction, `SetLeft`, `PresentMask`) and
      `ChibiRollbackSession` for 2-4 peers (star links, forwarding, per-remote checksums, `DeclareLeft`, `StepPresent`),
      compile OK 0 both assemblies
- [x] 3. Control codec: Start v6 (count, mode, teams, 4 seats, your slot; `Slot` defaults to 1 = the 1v1 guest), Roster
      (names), `ChibiGroupHandshake` (host, all Hellos -> Start + Roster per seat -> all Ready), joiner checks its own seat and
      `expectPlayers` (SEC-001); control `MaxBytes` 48 -> 96
- [x] 4. `ChibiOnlineMatch` for 2-4 (host with N-1 links, results from WinnerMask, leaver = eliminated, host left = no contest)
- [x] 5. `ChibiUtpTransport` host accepts up to 3 peers (`Peer(i)` views, `Sealed`, `DropPeer`; 1v1 path unchanged),
      `NetworkRoomManager` Relay `CreateAllocationAsync(guests)`, `HostLinkAsync(guests)`, `OnGroupConnected`; GameManager
      `StartOnline` (HostGroup / Join with `expectPlayers` and a longer join timeout), `ChibiMatch.CreateOnline` for N seats
- [x] 6. Rooms: start with 2-4 ready seats, SEC-001 lock for every seat (`GuestIds` "id|id|id", all acks), "coming soon"
      removed from rules and room screen; Quick Match = 4-seat public lobby, tag `p6r25-R`, starts at 4 or 20 s after the
      first guest (`QuickFillSeconds`)
- [x] 7. Tests: `ChibiStarNetcodeTests` (25: 2/3/4-peer lossy reordering stars, burst loss, leave at the host's frame,
      silent guest timeout, dropped guest, forged slot / leave / ack, codec v6 round trip + fuzz, Start / Roster, full 4P
      online match with names, guest forfeit, host forfeit) + `ChibiRoomRulesTests` (3-4 seats start, seat lock, acks).
      Test edit on purpose: `CanStart_ThreeOrFourSeated_NeverStarts` became `CanStart_ThreeOrFourSeatedAllReady_Starts` (spec change).
- Mono harness run 2026-11-23 (scratchpad runner, not committed): new `ChibiStarNetcodeTests` 25/25; existing NetcodeContract,
  PacketCodec, RollbackSession, RollbackInputBuffer, OnlineMatch, PlayerName, SyncVerification, Sanitize, MovesetLifecycle,
  Determinism, FourPlayer, FourPlayerRules 182/182. Golden edit: `PinnedProtocolVersion` 5 -> 6 and `GoldenHelloHex` byte 1
  `51` -> `61` (the Hello layout is unchanged; that byte holds the type nibble and the low nibble of the protocol).
- [x] 8. Docs: Network Engineering Summary (protocol 6, star, results), online-rooms.md (quick match, rooms, results)

Left for the lead / later (not done here):
- Unity run of the full EditMode suite (the Mono harness cannot load Unity Services / MonoBehaviour tests) and Play Mode
  evidence of a real 3-4 player Relay match (PC + WebGL wss); no UTP test with 3 real peers yet (`ChibiUtpTransportTests` is 1v1).
- Red-zone reviews before commit: network-programmer + security-engineer (star forwarding, leave frames, seat lock).
- Online results for 3-4 players: `ChibiMatch.WonLocal / LostLocal` now read the online outcome; the multi-results
  screen (`Hud.MultiResults`) was built offline-only in Phase 2b and is not checked online.
- Known limits: the host can forge forwarded inputs (checksums catch it unless it forges them too); guest-to-guest
  latency has one extra hop; quick match teams / 2v2 online not wired (mode = FFA); replays are not recorded for 3-4.
- LAN stays 1v1.

## Phase 3 review fixes (network: CHANGES NEEDED, security: APPROVE WITH NITS), 2026-11-24

Lead's Unity run before the fixes: EditMode 1981/1981. Mono harness after the fixes: 235/235 netcode + rooms tests
(`ChibiStarNetcodeTests` 40). Each new regression test was watched failing against the unfixed code (H1 cascade at
8+-2 and 12+-3 ticks one way; M1; M2; M3 x2; SEC-003 x3 by mutation), then passing.

- [x] H1 silent guest cascade: the host counts silence only for the guest(s) with the lowest contiguous frame, and
      `DeclareLeft` resets the other guests' silence and the stall window. Test `Leave_SilentGuestAtHighRtt_*` (4 cases).
- [x] M1 a left player's checksum at or after its leave frame is never compared (accept + compare). Test
      `Checksum_OfALeftPlayerAtOrAfterItsLeaveFrame_IsIgnored`.
- [x] M2 a guest's Leave(MatchComplete) decides nothing on the host. Test `GroupMatch_GuestSaysMatchComplete_IsNotDropped`.
- [x] M3 / SEC-002 every guest Leave reason only affects that guest (Desync claim = that guest dropped). Guests compare
      checksums with the host only (forwarded checksums are no longer sent); the host waits for every live guest's
      checksum of a frame: exactly one disagreeing guest while another agrees = that guest is dropped (`ChecksumDrops`),
      anything else = desync, no contest. Tests `Checksum_*` (3), `GroupMatch_GuestClaimsDesync_OnlyThatGuestIsDropped`.
- [x] SEC-003 + L2 seat binding: each room member publishes `k` = `ChibiRoomRules.SeatKey(secret)` (SHA-256, 16 hex,
      Member visibility); the joiner sends the secret in an optional Hello trailer (golden Hello unchanged: no secret);
      the host accepts each seat key once and the connection becomes that SEAT's sim slot (`ChibiGroupHandshake.SlotOf`);
      a 1v1 room host checks `expectKey`. Anything else is refused (InvalidConfig, the launch aborts). Tests `SeatBinding_*` (4),
      `Hello_SecretTrailer_RoundTripsAndIsOptional`.
- [x] SEC-004 room fights draw each seat's lobby name (`ChibiMatch.SetRoomNames`, sanitised by the room snapshot), not the
      handshake name. Quick match names stay self-reported (PeerDisplayName).
- [x] Guest Process nit: leaves are validated (`RollbackInputBuffer.CanSetLeft`) before any is applied.
- [x] SEC-001 documented as an accepted risk (Network Engineering Summary "Limits", four-player-design.md 2.1).

Noted, not changed:
- L1: guests' stall limit is 5/6 of the window and only the host drops the slowest guest; a griefing host can still slow a
  match to about a sixth of full speed before guests end it (no contest).
- L3: a 1v1 quick match waits up to 20 s for more players (spec: start at 4, or with 2-3 after the fill timeout).
- RTT / clock-sync nits: RTT is sampled from the ack of our own stream only (per link), and a guest's time dilation
  follows the host's advantage only; both are diagnostics / smoothing, never part of the simulation.
- A seat-key refusal aborts the whole launch (everyone presses OK again) instead of dropping just the stranger.

## Phase 3 verification-review nits (APPROVE WITH NITS), 2026-11-24

Lead's run before: Unity EditMode 1996/1996; a real 2-instance room match on protocol 6 with seat keys and lobby names.
Mono harness after: netcode + rooms 222 pass; the 7 failures are SaveData tests (JsonUtility is native: Unity only),
including the new `DevName_IsNeverWrittenToTheSave`. SEC-007 and SEC-008 tests were watched failing by mutation.

- [x] SEC-005 the seat secret is renewed in `ChibiRoomService.ResetSeat` (room entry, fight end, launch abort, seat
      change), so each seat / launch has its own secret; the new key goes out with the next player data push (OK).
- [x] SEC-006 the 1v1 room host binds the seat whenever it is given a key value: "" or a malformed key refuses every
      Hello (null = no binding: quick match / LAN). Test `OneVsOneRoomHost_GuestPublishedNoKey_RefusesEveryHello`.
- [x] SEC-008 a guest resets its stall window and silence when it applies a leave (as the host does in `DeclareLeft`).
      Test `Guest_LeaveAnnounced_ResetsItsStallWindow`.
- [x] SEC-007 hang fixed: a leave re-decides every pending checksum frame without the leaver (`ReviewPendingChecks`).
      Tests `Checksum_AwaitedGuestLeaves_*` (2). Documented rest: a guest that never sends a checksum, or copies the
      host's, is never caught by the check (it can only hide its OWN desync, and a frame waiting for it is decided when
      it leaves); the check ring is 8 frames, so a frame still waiting after 8 intervals is dropped undecided.
- [x] DEV-ONLY `ChibiDevFlags` (Netcode/ChibiDevFlags.cs), inert unless `Application.isEditor || Debug.isDebugBuild`:
      `-authProfile NAME` (or `ChibiDevFlags.SetAuthProfile("p2")` from the editor before going online) switches the
      Authentication profile before the anonymous sign-in (`NetworkRoomManager.SignInOnce`, `ChibiAccount`);
      `-devName NAME` uses a run-only name (`SaveData.diskPlayerName` keeps the real name on disk). Tests
      `DevFlags_ParseAndValidate`, `DevName_IsNeverWrittenToTheSave` (Unity only).

How to run a real 3-player Relay match on one PC (development build):
`ChibiClash.exe -authProfile p2 -devName Tester2` and `ChibiClash.exe -authProfile p3 -devName Tester3`, plus the
editor (default profile). Each window is its own anonymous player with its own claimed name.

## Proposed, not started: signed star (closes "a modified host can forge inputs") — 2026-11-24

**Status: proposal waiting for this branch's owner to confirm they are not changing the star.** Written by a second
Claude session (TP-9) so the two sessions don't collide again. Its parallel mesh port was dropped in favour of this star.

- **Goal:** the host can no longer forge or alter another player's inputs or checksums. Drop, delay and withholding stay
  possible and still end in a stall or no contest.
- **Why a hash chain:** the host does not forward guest bytes. `AddForwardBlock` (`ChibiP2PPeerLoop.cs:667`) rebuilds
  each guest's window per recipient from `_fwdAck`, so a tag over a datagram never matches what a recipient receives.
  Per stream s: `H_s(f) = HMAC(chainKey_s, H_s(f-1) | f | input)`. Each guest puts one 8-byte tag per other guest,
  `Trunc8(HMAC(pairKey(s,r), dir | L | H_s(L)))`, in block 0 for its newest frame L. The host stores the last 32 tag
  sets per guest and forwards the tag for the largest stored L it can cover. The recipient extends the chain from its
  own `H(contiguous)` and checks the tag in constant time.
- **Keys:** each player publishes an ephemeral X25519 public key (member-only player data, renewed per seat/launch).
  Pair key = `HMAC(shared, "chibi-star7"|lobbyId|launchNonce|minId|maxId)`. Refused: wrong length, non-canonical,
  own key, duplicate, low-order (all-zero secret). Host↔guest links are also wrapped in an 8-byte HMAC link tag
  (`ChibiAuthLink`). The X25519 (RFC 7748 vectors) and zero-alloc HMAC-SHA256 come from the reviewed feat/team-play code.
- **Protocol 7** (`p7r25`): worst-case datagram 227 B (MaxPacketBytes 216 → 232), about 20 % more bandwidth, no new
  Relay links, WebGL unaffected.
- **False leaves (owner decision TP-9):** a dropped guest keeps main's rule (Loss), **unless the host contradicted
  itself**: a link-tagged host datagram acked the guest's frame ≥ the drop frame, or another guest already holds that
  player's signed inputs past it. Then the result is No Contest (cause HostContradiction).
- **Still possible after it (documented):** the host sending different copies of its own inputs to different guests (a
  checksum catches it), and dropping, delaying or withholding packets.
- **Size:** about 900 lines of code and 700 of tests, then network + security review. Full plan with file:line targets is
  in the TP-9 session notes; ask before starting.
