# Chibi Clash stages: shared rules (spec v1, 2026-11-11)

Owner: level-designer. Source: `design/art/maps/00..05-*.jpg` (Map Design Sheet), decisions ST-1..ST-4 and GL-1 in
`production/session-state/active.md`, live sim constants (`Netcode/ChibiSimulation.cs` `ChibiSimConst`,
`Netcode/ChibiStageTable.cs`). Per-stage files: `01-mountain.md`, `02-plain.md`, `03-volcano.md`, `04-bird-lair.md`,
`05-ocean.md`.

**Status of the numbers.** Every coordinate here is a PROPOSED gameplay target. The stage technical-artist measures
the art and publishes the final numbers in `design/gdd/stages/stage-geometry.json`. If the measured art moves a value,
the json wins for geometry, but the checks in section 5 (reachability, clearance) must still pass; if they do not,
raise it with the level-designer instead of shipping an unreachable platform.

## 1. Overview

Five stages are added as indices 3..7 (ST-1): 3 Mountain of the Beginning, 4 Plain of Unity, 5 Volcano Abyss,
6 Giant Bird Lair, 7 Abandoned Ocean Monument. Each has its own KO edges (sheet matrix), a platform set, and at most
one stage event. Everything that touches gameplay runs inside the deterministic sim: integers only, schedules from
`GameTicks` and an in-state RNG, every mutable field in `ChibiSimState` (`CopyFrom` + `ComputeChecksum`).
Damage is in percent and knockback uses the tier names Low / Med / High defined by `design/gdd/percent-knockback.md`
(game-designer, written in parallel); this file never sets knockback numbers.

## 2. Units and conventions

- Positions in milli (1000 = 1 world unit). Y is feet-relative: MAIN top = 0 on every stage
  (sim Y = (world Y + 2.5) x 1000, as today). X = 0 is the stage centre; every stage is left/right symmetric unless
  the sheet says otherwise (Volcano's staircase).
- Velocities in sub-units (VelSub 900 = 1 milli/tick), durations in ticks (60/s), on `GameTicks` (so hazards freeze
  during hitstop and slow-mo exactly like fighters).
- Platform rows give `Cx`, `Top`, `HalfW`. One-way platforms keep `PlatThick` 300 and the drop-through rule.
  "Solid" = full box collider (cannot be dropped through, blocks from the side and from below).

### Movement envelope used for every reachability check (computed from ChibiSimConst, not estimated)
| Quantity | Value | How |
|---|---|---|
| Ground jump apex (feet) | **2263** | Vy 195000, gravity 8829/tick, arc 883/tick, 22 ticks |
| Air jump gain | **1759** | Vy 172500, 19 ticks |
| Ground + air jump (air jump at apex) | **4022** | |
| Slowest ground speed | 89 milli/tick | Heavy 94500 sub with the -15 % passive |
| Hurtbox | 750 x 1350 | HurtHalfW 375, HurtH 1350 |

The brief quoted "about 3.0 / 5.5 units"; the constants give 2.26 / 4.02. This spec uses the constants. Design limits
with margin: **single-jump step <= 2000**, **double-jump step <= 3200**, horizontal gap at a +2000 step <= 1600
(slowest class, measured on the rising-to-falling window of 13..30 ticks). Head clearance: a one-way platform
underside must sit >= 1350 above any surface under it (no visual "head inside slab" while standing).

## 3. Detailed rules

### 3.1 KO edges (sheet matrix, exact)
| Stage (index) | Top | Bottom | Left | Right | Event |
|---|---|---|---|---|---|
| 3 Mountain of the Beginning | KO | KO | wall | wall | none (moving platforms A, B, D) |
| 4 Plain of Unity | none | solid floor | KO | KO | none |
| 5 Volcano Abyss | KO | KO (magma lake) | KO | KO | fire bursts |
| 6 Giant Bird Lair | none | solid floor (cloud sea) | KO | KO | bird swipe |
| 7 Abandoned Ocean Monument | KO | KO | KO | KO | whale descent |

A KO edge works like today's ring-out lines (strict compare on feet X / feet Y). A "no KO" edge is never a KO:

- **Side wall** (Mountain): a vertical solid plane at `X = +-WallX`, infinite in Y. A fighter's centre is clamped to
  `|X| <= WallX - HurtHalfW`. Any velocity into the wall (walking, dash, knockback, gust) is set to 0 on contact;
  Vy is kept. No wall bounce, no wall jump, no damage (unless percent-knockback.md later adds a wall tech/bounce;
  this spec is ready for it: the contact tick is the hook). Projectiles despawn on touching a wall.
- **Solid floor** (Plain, Bird Lair): the lowest walkable surface spans the whole stage to the side KO lines. A fighter
  lands on it like on MAIN; downward knockback into it stops Vy (landing) and keeps Vx. It cannot be dropped through.
  These two stages are walk-offs: walking past the side KO line is a KO (inherent to ST-2).
- **No top blast** (Plain, Bird Lair): no KO upward. Safety cap `SkyCapY`: when feet Y > SkyCapY, Y = SkyCapY and
  Vy = min(Vy, 0). The fighter just falls back. No damage, no bounce. The view shows an off-screen marker.

### 3.2 Moving platforms (Mountain A, B, D)
- Position is a **pure function of GameTicks** (formula F1): no new state field, rollback-safe by construction.
- Motion is a smooth ping-pong between `Min` and `Max` on one axis, period `P` ticks, phase `Ph` ticks.
- **Riders are carried:** a fighter (or minion) resting on platform k at the start of the tick gets
  `X += dX_k`, `Y += dY_k` (F1 delta) before its own integration. A platform moving up catches a falling fighter
  with the swept test against the platform's previous top and current top. A platform moving up never pushes a
  fighter from below (one-way). Drop-through is unchanged.
- Platform step runs first in the tick (before fighter physics), so collisions use this tick's positions.

### 3.3 Stage events (hazards)
- One event machine per stage: `Idle -> Telegraph -> Active phases -> Idle`. State in `ChibiSimState`:
  `HazPhase`, `HazTimer`, `HazArg` (lane or direction), `HazCount`, `HazHitMask` (bit per fighter, cleared at each
  event start), `HazRng` (uint, xorshift32 `ChibiRng`). All copied and hashed.
- **Separate RNG stream (recommended, lead to confirm, see section 8):** `HazRng` seeded from the match seed XOR
  `0x53544147`. Hazards never draw from the combat `Rng`, so crits and replays do not shift when hazards are on/off.
  Draws happen only at two points per event: the delay draw when an event ends (or at fight start) and the lane or
  direction draw at Telegraph start. Fixed draw count per event.
- Timers start counting when the intro ends (`IntroTicks`), on game steps only.
- **Telegraph >= 60 ticks before any damage or push** (each stage states its exact value).
- **Hazard hits:** damage in percent, knockback tier + fixed angle, unblockable (shield does not stop them),
  one hit per fighter per event (`HazHitMask`) unless the stage says otherwise. Any invulnerability (respawn 90,
  dash i-frames, ult invuln) ignores hazard damage AND hazard pushes. Hazards do not touch projectiles or minions.
  A hazard KO credits no player (self-destruct rules of percent-knockback.md apply; if the fighter was hit by a
  player within the last-hit window that spec defines, the credit goes to that player).
- **Tick order:** platforms (F1) -> inputs/FreeActions -> hazard velocity adds (gust, push) -> integrate/collide ->
  combat -> hazard hit tests (damage + knockback) -> event machine advance -> bounds / KO.

### 3.4 Hazards off
- Match option `StageHazards` (bool, default ON for local and casual, OFF for ranked: lead to confirm). Part of the
  match config and its hash; set by the host in online rooms; shown on stage select as a toggle.
- OFF: the event machine stays `Idle` forever, no telegraph, no RNG draws, `HazRng` untouched. Cosmetic background
  creatures may still appear in the FAR layer but never in the gameplay plane or with a telegraph look.
- Moving platforms are terrain, not hazards: they keep moving when hazards are OFF (alternative for the lead:
  freeze them at their t=0 position).

### 3.5 Spawn, respawn, camera, layers
- Spawn: P0 at (-SpawnX, 775), P1 at (+SpawnX, 775), airborne, as today. Respawn: per stage, airborne, 90 ticks
  invuln. Ocean has an event-time respawn point (see 05-ocean.md).
- Camera: the existing follow/zoom stays (size 5.5..10, pad 3.2). New per stage: an **art rect**; the camera centre is
  clamped so the view stays inside it, and max orthographic size = min(10, rectHalfH, rectHalfW / aspect). Art must
  cover the whole rect.
- Parallax (view only): FAR 0.10..0.20, MID 0.35..0.50, gameplay plane 1.00 (platform and hazard sprites are drawn
  from the same geometry table as the colliders, ST-4: never parallaxed), optional FRONT overlay 1.10..1.20 at
  <= 40 % alpha, never covering the MAIN top band.

## 4. Formulas

**F1 moving platform position** (all `long`, all terms >= 0, truncating division):
```
p = (GameTicks + Ph) mod P            H = P / 2   (P even)
u = (p <= H) ? p : P - p              // 0..H..0 triangle
S = u*u*(3H - 2u)                     // smoothstep numerator, 0..H^3
pos(t) = Min + (Max - Min) * S / H^3
dPos = pos(t) - pos(t-1)              // rider carry this tick
```
Peak speed = 1.5 (Max - Min) / H milli/tick. Example Mountain D: Min -5600, Max 5600, P 900, Ph 0: H 450, peak
1.5 x 11200 / 450 = 37 milli/tick (2.2 u/s); at t = 225 (u = 225): S = 225^2 x (1350 - 450) = 45 562 500,
H^3 = 91 125 000, pos = -5600 + 11200 x 45 562 500 / 91 125 000 = 0. Max intermediate 11200 x 91 125 000 ~ 1.0e12:
fits in `long`.

**F2 event delay** `delay = Base + HazRng.Range(Span)` (ticks; Range is `NextU32 % Span`).

**F3 gust** (Bird Lair) after FreeActions, for each non-invulnerable fighter in the gust band:
`if (Vx * dir < GustCap) Vx = min-toward(Vx + dir * GustAccel, dir * GustCap)`.

Stage files give the per-stage values and worked examples.

## 5. Edge cases (shared)
- Fighter standing on a moving platform when it reverses: carried by dPos, never launched (dPos <= peak speed).
- Fighter dropping through a moving platform: `DropPlatform` index is stable (array order), works as today.
- Two fighters hit by the same hazard tick: both resolve in fighter index order (deterministic), each once.
- KO during an event: the event keeps running; the respawning fighter is immune while invulnerable.
- Match ends during an event: the event state is irrelevant; nothing carries over.
- Rollback across an event start: all state is in `ChibiSimState`; telegraph VFX/SFX in the view must be driven by
  sim state (`HazPhase`, `HazTimer`), not by one-shot triggers, so a rolled-back telegraph never plays twice.
- Hazards OFF in a replay recorded with ON: impossible, the flag is in the config hash.

## 6. Dependencies
- `design/gdd/percent-knockback.md` (Low/Med/High tiers, angles, hitstun, last-hit credit, wall/floor tech if any).
  That spec should list stages as a dependency (bidirectional).
- `design/gdd/stages/stage-geometry.json` (technical-artist, final measured geometry).
- `Netcode/ChibiStageTable.cs`, `ChibiSimulation.cs` (gameplay-programmer): per-stage KO flags, WallX, floor,
  SkyCapY, MainBottom per stage, moving platforms, event machine.
- View/UI (ui-programmer): camera clamp, telegraphs, off-screen marker, hazards toggle.
- CPU (ai-programmer): must read `HazPhase` to avoid the bird band, ledge-hop under the whale, avoid fire lanes.
- Audio (audio-director): ids below are proposals for `design/audio/sfx-spec.md` (bus STG).

## 7. Tuning knobs (shared)
| Knob | Default | Safe range | Affects |
|---|---|---|---|
| Single-jump step limit | 2000 | 1800..2200 | platform reachability |
| Double-jump step limit | 3200 | 3000..3600 | platform reachability |
| SkyCapY | 16000 | 14000..20000 | time spent off-screen above (Plain, Bird) |
| Telegraph minimum | 60 | 60..120 | hazard readability (never below 60) |
| StageHazards default | ON casual / OFF ranked | bool | mode rules |

## 8. Acceptance criteria (shared)
1. For every stage, the KO edge set equals the matrix in 3.1 (unit test per stage: a fighter placed past each edge is
   KO'd or not as listed).
2. Mountain: a fighter driven into a wall at any speed stops with Vx = 0 and is never KO'd horizontally.
3. Plain / Bird: a fighter launched upward at any speed returns to the floor with no KO; feet Y never exceeds SkyCapY.
4. Every platform passes the reachability table in its stage file (EditMode test: scripted jumps from the listed
   launch surface land on it, slowest class).
5. Moving platform positions at ticks 0, P/4, P/2, 3P/4 equal F1; a rider's X/Y change equals dPos every tick.
6. Two sims with the same inputs and seed have equal checksums every tick with hazards ON (sync test), and a
   rollback of 8 ticks across each event's Telegraph start reproduces the checksum.
7. Every hazard's first damage tick is >= 60 ticks after its Telegraph start (test reads HazPhase/HazTimer).
8. With StageHazards OFF, HazRng state is unchanged after 10 800 ticks and no hazard damage occurs.
