# Building on the online core without breaking it

Chibi Clash's online play is a deterministic rollback core (GGPO style). Two players' games simulate the same match
from the same inputs, and only inputs cross the network. That is what makes it fast and fair. It also means one thing
done in the wrong place can split the two games apart (a desync). This page says where you can build freely and what
needs care. The automated tests (1400+) and the contract tripwire catch mistakes before they ship.

## 1. The map: three zones

| Zone | Folders / files | Change freely? |
|---|---|---|
| **Green: build the game here** | `UI/` (Hud, Gui), `Game/GameManager.cs` (menus, screens, onboarding), `Netcode/View/` (ChibiSimView: sprites, VFX, sounds, camera, interpolation), `Core/` save data, quests, armory, cosmetics, art, audio, scenes | **Yes.** These only READ the simulation state or the events it emits. Nothing here can desync a match. |
| **Yellow: gameplay rules (deterministic)** | `Netcode/ChibiSimulation*.cs` (incl. `.Percent.cs` percent / knockback / stocks, `.Stages.cs` moving platforms, `.Hazards.cs` stage events, `Movesets/`), `ChibiFixed.cs`, `ChibiCombatStats.cs`, `ChibiStageTable.cs`, `ChibiCpuInputSource.cs` (characters, moves, damage, percent, stages, hazards, CPU) | **Yes, with the rules in section 3**, then bump `RulesVersion`. Stages: section 7. Percent tuning: section 8 |
| **Red: the network core** | `ChibiRollbackSession` / `RollbackInputBuffer` (rollback), `ChibiNetProtocol.cs` (handshake, mux, control codec), `ChibiInputPacket.cs` (16-bit input packet), `ChibiUtpTransport.cs` (UDP/Relay), `NetworkRoomManager.cs` (rooms, matchmaking), `ChibiOnlineMatch.cs`, `ChibiSocialRules.cs` / `ChibiSocialManager.cs` (security rules), `BitBuffer.cs` | **Rarely.** Only for networking work, and with the network-programmer + security-engineer reviews |

## 2. Green zone: how to add things without touching the core

- **New screen, menu, HUD element:** `GameManager` / `Hud`. Read the match through `ChibiMatch` (`State`, `Def(i)`,
  `NameOf(i)`, `Online`, `LocalPlayer`, `Winner`). Never write to `ChibiSimState` from UI.
- **New visual or sound for a move:** react to `ChibiSimEvents` (hit, KO, ult, projectile...) in `Netcode/View/`. Events
  are derived by comparing two consecutive displayed states, so a rollback re-simulation never fires them twice. Add a
  new kind there (derived from state fields) rather than calling effects from inside the simulation.
- **Cosmetics (skins, weapon looks):** local only, never sent to the opponent, never read by the simulation.
- **New offline mode (VS CPU variant, training option):** build a `ChibiMatchConfig` and use `ChibiMatch` / `ChibiLocalLoop`.
- **Player names:** use `ChibiSocialRules.SafeDisplayName` / `PeerDisplayName` before drawing ANY name (IMGUI renders rich text).

## 3. Yellow zone: the determinism rules (the simulation and anything it calls)

1. **Integers only:** `ChibiFixed` / milli units / micro-HP. No `float`, `double`, `Mathf`, `Vector2` inside `Step`.
2. **Randomness only from the simulation's own RNG** (`ChibiRng` in the state), never `UnityEngine.Random` or `System.Random`.
3. **No clock:** no `Time.*`, `DateTime`, frame rate. Time = ticks (`GameTicks`).
4. **No hidden state:** every field that changes during a match lives in `ChibiSimState` (then `CopyFrom` and
   `ComputeChecksum` cover it, and the reflection tests fail if you forget one). No statics, no caches, no
   `Dictionary` iteration order, no `GetHashCode` of strings.
5. **No Unity objects** (GameObject, physics, Animator) inside the simulation. The view follows the sim, never the reverse.
6. **Inputs are 16 bits** (`ChibiInputPacket`). A new button means a protocol change (red zone).
7. **Bump `ChibiMatchConfig.RulesVersion`** (`ChibiSimulation.cs`) for ANY gameplay or balance change, then re-record the
   golden values in `NetcodeContractTests` (the failing test prints them). Old and new builds then refuse each other
   with a clear "different version" message instead of desyncing, and matchmaking keeps them apart (protocol tag).

## 4. The tripwires (what stops a bad change)

| Guard | Catches |
|---|---|
| `NetcodeContractTests` | ANY change to gameplay results or constants (fixture match checksums, config hash) or to the wire bytes, with a message saying what to bump |
| `ChibiSyncVerificationTests` | Two peers over a hostile link (latency, jitter, loss) end in different states; snapshot/restore at any depth differs; a state field missing from `CopyFrom` / checksum |
| `ChibiOnlineMatchTests`, `ChibiUtpTransportTests` | Handshake, version refusal, disconnect = opponent wins, desync = no contest, real UDP transport |
| `ChibiSocialRulesTests`, `ChibiPlayerNameTests`, `ChibiRegionsTests`, `PlayerIdentityTests` | Security rules: invites, injection, names, age gate, matchmaking regions |
| Handshake config hash at runtime | Two builds with different balance data never start a match together |
| `.github/workflows/tests.yml` | Runs all of the above on every push / pull request (needs the Unity license secrets, see the file) |

**Never** "fix" a red tripwire by changing only the expected value. Ask which section of this page the change belongs to.

## 5. Changing the red zone (when you must)

- Wire format (packet, control message, mux): bump `ChibiPacketCodec.ProtocolVersion`, update `GoldenHelloHex`.
  Optional trailing fields (like the player name) can stay compatible: readers must accept their absence.
- Every field read from the network is untrusted: size-check, range-check, never throw, never allocate per packet.
- Run the studio reviews: `network-programmer` (correctness, scale) and `security-engineer` (abuse), as done for
  rooms, invites and matchmaking (see online-rooms.md, online-social.md).

## 6. Before you merge (2 minutes)

1. Unity Test Runner → EditMode → Run All: everything green.
2. Changed gameplay? `RulesVersion` bumped and golden values re-recorded on purpose.
3. Changed something the player sees? Screenshot in `production/qa/evidence/`.
4. Online feature? Two-player test (LAN is enough for the core; room code / invite needs the Unity Cloud link).
5. Touched a stage or its art? `python tools/ci/check_stage_geometry.py` exits 0.

## 7. Stages: where things live and how to add one

Design record: [ADR-0002](../adr-0002-stages-as-data-and-deterministic-hazards.md). Rules: `design/gdd/stages/README.md`.

| What | Where | Zone |
|---|---|---|
| Source art (map design sheet pages) | `design/art/maps/0N-*.jpg` | - |
| Stage spec (KO edges, platforms, event, timings) | `design/gdd/stages/README.md` + `0N-<stage>.md` (level-designer) | - |
| Art cutter + geometry measurement | `tools/art/stages/build_stages.py` (the `STAGES` table = measured px of every platform) | tool |
| Measured geometry (single source of truth) | `design/gdd/stages/stage-geometry.json` (generated, do not hand-edit) | - |
| Cut layers (Full, Backdrop, Far, Mid, Foreground, `Plat_*`, `Haz_*`) | `Assets/ChibiClash/Resources/Stages/<Id>/` | green |
| Sim geometry, KO flags, respawn, hazard id | `Netcode/ChibiStageTable.cs` (`ChibiStageDef` rows, index = stage id) | yellow |
| Hazard timings / boxes / knockback | `ChibiHazardConst` in `Netcode/ChibiSimulation.Hazards.cs` | yellow |
| Stage view (layers, parallax, moving sprites, hazard visuals) | `Game/StageDesign.cs` (`ChibiDesignStage`), hooked from `Game/Stage.cs` | green |
| Art <-> collider drift check | `tools/ci/check_stage_geometry.py` (no Unity needed) | CI |

**Adding a new stage, end to end**

1. **Spec.** Level-designer writes `design/gdd/stages/0N-<stage>.md`: KO edge matrix (which of top / bottom / left /
   right KO, which are walls, floor or sky cap), platforms, reachability (single jump <= 2000, double <= 3200 milli),
   event and telegraph (>= 60 ticks).
2. **Art -> geometry.** Add the page to `design/art/maps/`, add a row to `STAGES` in `build_stages.py` (index, page,
   ppu, every platform's `topPx, leftPx, rightPx`, moving ids), then run
   `python tools/art/stages/build_stages.py all <Id>`. It writes `Resources/Stages/<Id>/*` (sprites imported with the
   stage's ppu and anchor pivots) and updates `stage-geometry.json`.
3. **Sim row.** Append a `ChibiStageDef` to `ChibiStageTable.Stages` (new index, bump `Count`) and copy the platforms'
   `cxMilli / topMilli / halfWMilli` from the json in the same order. Moving platforms:
   `new ChibiPlatform(cx, top, halfW, axis, min, max, period, phase)` with the json "home" position at `Max` (Y axis)
   or `Min` (X axis); period even; position is a pure function of `GameTicks` (never add platform state). KO edges
   follow the GL-2 rule (sides 8000-10000 beyond the ledge, top = max(10000, highest platform + 5000)); set
   `koSides / koTop / koBottom` false for walls, sky cap or solid floor.
4. **Hazard (optional).** New id in `ChibiStageTable.Hazard*`, constants in `ChibiHazardConst` (and its `Constants`
   array), phases in `HazardPhaseLength`, hits in `HazardHits`. Draw only from `s.HazRng`, a fixed number of draws per
   event; never from `s.Rng`.
5. **View.** Add the resource id, platform art names, display name, tagline and sky colour in `ChibiDesignStage`
   (`Game/StageDesign.cs`) and `Game/Stage.cs`. Drive hazard visuals from `HazPhase` / `HazTimer` each frame, never
   from one-shot triggers. Widen the drift check's `STAGES` range if the index is outside 3..7.
6. **Wire format.** The Start message carries the stage in 4 bits (`StageBits`, up to 16 stages) and receivers
   range-check it against `ChibiStageTable.Count`, so stages 8..15 need no protocol change. A 17th stage needs a
   wider field: protocol bump, red zone (section 5).
7. **Check.** `python tools/ci/check_stage_geometry.py` exits 0; bump `RulesVersion` (the stage is in the config
   hash) and re-record the goldens; EditMode tests for the KO set, reachability and sync; a Play Mode screenshot of
   fighters standing on every platform in `production/qa/evidence/`.

## 8. Tuning the percent system (yellow zone)

Design record: [ADR-0001](../adr-0001-damage-percent-and-stocks.md). Spec and safe ranges:
`design/gdd/percent-knockback.md` section 7.

| Knob (all in `ChibiPercentConst`, `Netcode/ChibiSimulation.Percent.cs`) | Default | What it changes |
|---|---|---|
| `LsSubPerKb` | 1530 | launch speed per KB: the main KO-percent lever (+10 % -> KOs about 9 % earlier) |
| `PctPerHpM` | 200 | percent per HP of damage (all hits) |
| `DmgTermCapM` | 15 000 | cap of the hit's percent inside the formula (stops heavy smashes KOing too early) |
| `KbgBase` / `KbgDiv`, `BkbBase` / `BkbNum` / `BkbDen` | 250 / 250, 10 000 / 2 / 15 | growth and base derived from each move's KbX/KbY |
| `UltBkbBonusM` | 80 000 | how decisive ult hits are |
| `ZonerKbP` | 850 | Ren / Lyra kill power |
| `SetKbMag` | 30 000 | below this a hit uses fixed (legacy) knockback |
| `LaunchDecay`, `GravFullKbM` / `GravZeroKbM`, `HitstunCap` | 2700, 60 000 / 120 000, 90 | launch feel, juggle arcs vs straight launches, max stun |
| `ArmorBreakKbM` | 150 000 | armor holds below this KB |
| `StocksStart`, `RespawnDelay` / `RespawnHoverMax` / `RespawnInvuln` | 3, 60 / 180 / 120 | match length and respawn |
| `ComebackPctM`, `CpuKillPctOffset` | 100 000, 10 | ult x2 threshold, CPU kill read |
| `ChibiSimConst.MatchTicks` | 300 s | timer |
| Weight | `ChibiCombatStats.Weight = MaxHp / 5` | per-class launch resistance |

Rules:
1. Change values only, never the formula structure, without a new ADR. Stay integer (`long` intermediates, no
   float / `Math.Pow`); keep the worked example of spec 4.7 as a test.
2. Every knob is in the config hash (`ChibiPercentConst.Constants`): bump `ChibiMatchConfig.RulesVersion` and re-record
   the golden values in `NetcodeContractTests` on purpose (the failing test prints them). A new constant must be added
   to the `Constants` array.
3. Re-run the KO calibration tests (spec section 8) and update the expected KO percents only when the change was
   meant to move them.
4. The HUD percent colour ramp, shake and fonts are view-only (green zone): no bump.
5. `ChibiFighterSim.HpU` / `Hp` / `Revives` are compatibility shims for old view code: never read them in the sim or in
   new code; use `PctM`, `Stocks`, `InPlay`.
