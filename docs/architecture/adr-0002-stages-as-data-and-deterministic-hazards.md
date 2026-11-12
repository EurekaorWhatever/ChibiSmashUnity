# ADR-0002: Design stages as data, with deterministic hazards and art-measured geometry

## Status

Proposed

> Who may move this to `Accepted`: the user, or `technical-director` on the user's explicit confirmation.
> Implemented in the working tree (rules v20, protocol 4); Wave C review pending.

## Date

2026-11-11

## Last Verified

2026-11-11 (against `ChibiStageTable.cs`, `ChibiSimulation.Stages.cs`, `ChibiSimulation.Hazards.cs`,
`tools/ci/check_stage_geometry.py`, `design/gdd/stages/stage-geometry.json` schema 1)

## Decision Makers

User (ST-1..ST-5), level-designer (stage specs), technical-artist (art cut + geometry json), technical-director
(this record), gameplay-programmer (implementation).

## Summary

Five new stages (indices 3..7) each need their own KO edges, solid walls / floors, moving platforms and a stage event,
and their art must line up with the colliders exactly. Stages are immutable data rows (`ChibiStageDef`) with per-edge
KO flags; moving platforms are pure functions of `GameTicks`; hazards are a small state machine in `ChibiSimState`
driven by their own RNG stream and switched by a `HazardsOff` config field carried in the handshake; platform
geometry is measured once from the art into `stage-geometry.json`, the single source of truth, guarded by a drift check.

## Engine Compatibility

| Field | Value |
|-------|-------|
| **Engine** | Unity 6000.6.7f1 |
| **Domain** | Core (deterministic sim) + Presentation (stage view) |
| **Layer** | Core |
| **Knowledge Risk** | LOW: the sim part is pure C#; the view uses only `SpriteRenderer` / `Resources.Load<Sprite>` |
| **References Consulted** | `docs/architecture/netcode/stage-conversion.md` (D-S1/S2), `DEVELOPER-GUIDE.md` |
| **Post-Cutoff APIs Used** | None |
| **Verification Required** | Sprites land on colliders in Play Mode (screenshot per stage in `production/qa/evidence/`) |

## ADR Dependencies

| Field | Value |
|-------|-------|
| **Depends On** | ADR-0001 (KO = crossing an active edge; hazard hits use the percent knockback formula) |
| **Enables** | Future stages added as data rows; stage-aware CPU |
| **Blocks** | Wave C review and the push of the stages / percent session |
| **Ordering Note** | Same RulesVersion (20) and protocol (4) as ADR-0001 |

## Context

### Problem Statement
The map design sheet defines five stages with different KO edge sets (ST-2: Mountain has side walls; Plain and Bird
Lair have no top line and a solid bottom), moving platforms and stage events (ST-3). Everything that touches a
fighter must stay deterministic under rollback, and art cut from a single illustration must sit pixel-exact on the
colliders (ST-4).

### Current State (rules v19)
`ChibiStageDef` had three classic stages (0..2) with all four edges as ring-out lines, static platforms only, no
events. Collider numbers were hand-converted from the original `Stage.cs`; art was procedural (`StageArt.cs`).

### Constraints
- Determinism rules (DEVELOPER-GUIDE section 3); rollback re-simulates up to 8 ticks, so any stage motion or event
  must be reproducible from the rolled-back state.
- Stages 0..2 must keep working (tests, replays) as "Classic" (ST-1).
- Toggling hazards must never shift crits or replays (ST-5); the host decides in private rooms.
- One code path for every stage: no per-stage subclasses in the sim.

### Requirements
- Per-edge KO, walls, solid floors, a sky cap; moving platforms that carry riders; three events (fire, bird, whale)
  with telegraphs >= 60 ticks; hazards ON by default with a toggle.
- Art and colliders agree by construction, and a change on either side that breaks the agreement is caught.

## Decision

1. **Stage = immutable data row.** `ChibiStageDef` carries main half-width and underside, the four edge positions
   (`BlastX`, `KillY`, `CeilingY`) and per-edge flags `KoSides`, `KoTop`, `KoBottom`, spawn / respawn points, a
   hazard id and a `ChibiPlatform[]`. The state stores only the stage index. A **false** flag turns the same edge
   into a solid boundary: side wall at `|X| = BlastX` (centre clamped to `BlastX - HurtHalfW`, velocity into the
   wall zeroed, projectiles despawn), solid floor whose top is `KillY` (lands, cannot be dropped through), sky cap at
   `CeilingY` (clamp, `Vy <= 0`). `CrossesKoEdge` is the only KO test.
2. **Moving platforms are pure functions of `GameTicks`** (formula F1, integer smoothstep ping-pong on one axis:
   `Min`, `Max`, even `Period`, `Phase`). No platform state exists, so rollback is correct by construction. Riders
   resting on a platform at the end of the previous step are carried by `F1(t) - F1(t-1)` first in the step
   (`CarryRiders`); the view reads `PlatformCx/PlatformTop` at `PlatformClock` (= the tick the last step simulated).
   Moving platforms are terrain, not hazards: they move with hazards off (ST-5).
3. **Hazards are a rollback-state machine with their own RNG.** `ChibiSimState` gains `HazardsOn`, `HazPhase`,
   `HazTimer`, `HazElapsed`, `HazArg`, `HazCount`, `HazHitMask`, `HazRng` (copied and hashed). `HazRng` is a separate
   xorshift32 stream seeded `Seed XOR 0x53544147` ("STAG"), with exactly two draws per event (delay at event end,
   lane / direction at Telegraph start). The combat `Rng` is never touched, so crits and replays do not move when
   hazards are toggled. Tick order: carry riders -> inputs -> hazard forces (gust) -> integrate / collide -> combat
   -> hazard hits -> event advance -> KO / match end. Hazard hits use the ADR-0001 knockback formula, are
   unblockable, once per fighter per event, and ignore any invulnerable / out / hovering fighter. Bird direction
   alternates after a random first draw (ST-5). View telegraphs are driven by `HazPhase` / `HazTimer`, never
   one-shot triggers, so a rolled-back telegraph never plays twice.
4. **`HazardsOff` is match config.** `ChibiMatchConfig.HazardsOff` (default false = hazards ON) is mixed into the
   config hash and carried as a 1-bit field after the stage in the Start control message (protocol 4); the host's
   value wins in private rooms. With hazards off the machine stays Idle and `HazRng` is never advanced.
5. **Geometry measured from the art is the single source of truth.** `tools/art/stages/build_stages.py` cuts the
   sheet illustration into layers and writes `design/gdd/stages/stage-geometry.json` (ppu, origin, every platform in
   px and milli, sprite pivots). `ChibiStageTable` copies the platform numbers from the json (moving platforms: the
   json holds the art "home" position = `Max` for Y movers, `Min` for X movers). Sprites are imported with the
   stage's ppu and a pivot at their anchor, and `ChibiDesignStage` places them at the sim's coordinates, so art and
   colliders line up by construction. KO edge positions are NOT art: they come from the GL-2 rule (sides 8000-10000
   beyond the ledge, top = max(10000, highest platform + 5000)) and the stage specs.
6. **Drift check.** `python tools/ci/check_stage_geometry.py` compares the json against the C# table for stages 3..7
   (main half-width, platform count, every cx / top / halfW within 2 milli) and exits 1 on any drift. It needs no
   Unity licence, so it runs in CI on every push (wired by devops-engineer into `.github/workflows/tests.yml`).
7. **Whole stage in the config hash.** `MixStageHash` mixes every field of the selected stage, every platform's F1
   parameters and every `ChibiHazardConst` value: two builds with different geometry or hazard tuning refuse each
   other at the handshake instead of desyncing.

### Key Interfaces
| Interface | Contract |
|---|---|
| `ChibiStageTable.Get(i)` -> `ChibiStageDef` | read-only data; out-of-range indices play as Sky Dojo |
| `ChibiPlatform.CxAt(t)` / `TopAt(t)` / `Pos(...)` | pure F1; `Moves` false for static platforms |
| `ChibiSimulation.PlatformCx/PlatformTop/PlatformClock` | view / CPU read of moving platforms |
| `FireColumnTop`, `FireLaneOn`, `BirdGustActive`, `BirdBodyActive`, `BirdBodyX`, `WhaleBottom` | view / CPU read of hazards |
| `ChibiMatchConfig.HazardsOff` | config field, hashed, sent in Start (protocol 4) |
| `stage-geometry.json` (schema 1) | written by `build_stages.py` only ("do not hand-edit") |

### Implementation Guidelines
- Stage geometry must never be hand-edited in `ChibiStageTable` without regenerating / re-measuring the json; the
  drift check must stay green.
- Hazard code must never draw from `s.Rng`; the draw count per event must stay fixed.
- Stage motion must never be stored as state when it can be a function of `GameTicks`.
- View code must drive stage visuals from sim state each frame, never from events fired inside `Step`.
- Any change to a stage row, platform, or `ChibiHazardConst` value bumps `RulesVersion` and re-records the goldens.

## Alternatives Considered

### Alternative 1: Stages as Unity scenes / prefabs with physics colliders
- **Pros**: Level designers edit visually; familiar Unity workflow.
- **Cons**: Unity physics and transforms are float and not part of the rollback state; violates DEVELOPER-GUIDE rule 5.
- **Rejection Reason**: Not deterministic; would need a second gameplay implementation (Q1).

### Alternative 2: Moving platforms as state (position + velocity integrated per tick)
- **Pros**: Allows reactive platforms (fall when stood on).
- **Cons**: More rollback state, more hash fields, error accumulation, nothing in the sheet needs it.
- **Rejection Reason**: F1 of `GameTicks` gives the same motion with zero state; revisit only if a reactive platform is designed.

### Alternative 3: Hazards on the combat RNG
- **Pros**: One stream, slightly less code.
- **Cons**: Turning hazards on/off shifts every later crit and spread draw; replays and balance comparisons diverge.
- **Rejection Reason**: ST-5.

### Alternative 4: Hazards as a view-side effect or a client-local option
- **Rejection Reason**: Anything that pushes or damages a fighter must be in the sim; a client-local toggle would desync.

### Alternative 5: Hand-typed collider numbers with art authored to match
- **Cons**: Two sources of truth; every art tweak silently misaligns colliders (the "standing on air" bug class).
- **Rejection Reason**: ST-4 asks for alignment by construction; one measured json plus a drift check removes the class.

## Consequences

### Positive
- New stages are a data row + art; one sim code path for all eight stages.
- Rollback correctness of platforms is structural; hazards are covered by the existing copy / hash / reflection tests.
- Art / collider misalignment is caught before merge, without a Unity licence.

### Negative
- `RulesVersion` 20 and protocol 4 (Start carries `HazardsOff`, stage range 0..7): older builds are refused.
- `BlastX` / `KillY` / `CeilingY` now mean "KO line OR solid boundary" depending on the flag; readers must check the
  flag (debt: rename or split in a later cleanup).
- The drift check parses C# with regular expressions and duplicates a few C# constants (`MainBottom`, `SkyCapY`, ...);
  a reformat of `ChibiStageTable` can break it (fails loudly, never passes silently).
- CPU must become stage-aware (avoid fire lanes / bird band, drop under the whale): ai-programmer scope.

### Neutral
- Stages 0..2 keep all-four-edges KO and no hazard ("Classic").

## Risks
| Risk | Mitigation |
|---|---|
| A rider carry or swept landing differs between peers | pure integer F1; sync tests with rollback across platform reversals |
| Hazard telegraph replays twice after rollback | view reads `HazPhase` / `HazTimer` state, never one-shot triggers |
| Sheet art re-measured, table not updated | drift check fails in CI |
| Hazards unfair (unreadable) | telegraph >= 60 ticks enforced by an EditMode test; hazards toggle |
| Edge placement makes KO percents stage-dependent | GL-2 edge rule + ADR-0001 calibration table (spec 4.8) |
| Mixed-version lobbies | handshake rejects protocol / rules / config-hash mismatch with a clear reason |

## GDD Requirements Addressed

| GDD System | Requirement | How This ADR Addresses It |
|------------|-------------|--------------------------|
| stages/README.md 3.1 | per-edge KO matrix, walls, solid floor, sky cap | Decision 1 |
| stages/README.md 3.2, F1 | moving platforms, riders carried, rollback-safe | Decision 2 |
| stages/README.md 3.3 | event machine, own RNG stream, telegraph, tick order | Decision 3 |
| stages/README.md 3.4 | hazards toggle in the config hash | Decision 4 |
| stages/README.md 3.5, ST-4 | art from the same geometry table as the colliders | Decisions 5-6 |
| percent-knockback.md 3.4 / 6 | KO only on active edges; hazard hits through the knockback formula | Decisions 1, 3 |

## Performance Implications
- **CPU**: F1 is a few integer ops per moving platform per tick (and once more for the delta); hazard tests are
  2 fighters x 1 box per tick. Negligible in the 8-frame rollback budget.
- **Memory**: 8 ints + 1 RNG word in the state. Stage art: 5 stages of 3840 x 2160 layers loaded on demand by the
  view only (one stage at a time); watch texture memory on low-end targets (import compression is a technical-artist call).
- **Load Time**: stage art loaded via `Resources` per match; acceptable for 5 stages, revisit Addressables if the count grows.
- **Network**: +1 bit in the Start control message; nothing per tick.

## Migration Plan
Stages 0..2 converted to the new row format with all flags true (unchanged behaviour). New stages added at 3..7.

**Rollback plan**: revert the rules v20 changeset (stage rows 3..7, hazards file, protocol 4 field).

## Validation Criteria
- `design/gdd/stages/README.md` section 8 criteria 1-8 pass as EditMode tests (KO set per stage, walls, sky cap,
  reachability, F1 values at 0 / P/4 / P/2 / 3P/4 and rider delta, sync with rollback across each Telegraph start,
  telegraph >= 60 ticks, `HazRng` untouched over 10 800 ticks with hazards off).
- `check_stage_geometry.py` exits 0; `NetcodeContractTests` re-recorded at rules v20 / protocol 4.
- Play Mode screenshot per stage showing fighters standing on every platform sprite (`production/qa/evidence/`).
- Network-programmer + security-engineer review of the `HazardsOff` wire field (range, host authority).

## Related
- ADR-0001 (percent and stocks)
- `design/gdd/stages/README.md` + `01..05-*.md`, `design/gdd/stages/stage-geometry.json`, decisions ST-1..ST-5
- `docs/architecture/netcode/stage-conversion.md` (D-S1/S2, classic stages)
- Code: `Netcode/ChibiStageTable.cs`, `ChibiSimulation.Stages.cs`, `ChibiSimulation.Hazards.cs`, `Game/StageDesign.cs`,
  `tools/art/stages/build_stages.py`, `tools/ci/check_stage_geometry.py`
