# Stage Conversion: Stage.cs + Fighter.cs (Box2D) to ChibiSimulation (integer, 60 Hz)

Status: Proposed (steps 6-7: stages and one-way platforms). Rule: play exactly like the original.
Conventions follow `movement-conversion.md` (1 u = 1000 milli, 60 ticks/s, velocity sub-unit 1/900 milli/tick,
C50 arc term, D1) and `dash-conversion.md` (D2: timers are the original at 60 fps, `ticks = seconds x 60`, ceil).

## 0. Ground truth about the original (verified)

| Fact | Value | Source |
|---|---|---|
| Stage list | 0 Sky Dojo, 1 Neon City, 2 Lava Forge; any other index builds Sky Dojo (`default:`) | `Stage.cs:19-51` |
| groundY (main-stage top, world) | -2.5 on all three stages | `Stage.cs:29,37,45` |
| Main stage collider | solid `BoxCollider2D`, centre (0, groundY-0.6), size (2·halfWidth, 1.2) → top -2.5, bottom -3.7, sides ±halfWidth | `Stage.cs:74-80` |
| Main-stage art | "Top" sprite is 0.2 u wider than the collider (cosmetic only) | `Stage.cs:83` |
| Platform collider | `BoxCollider2D` (width, 0.3), centre = given pos, `usedByEffector`; `PlatformEffector2D useOneWay, surfaceArc 170` | `Stage.cs:86-96` |
| Ring-out bounds | blastX = halfWidth+9, killY = groundY-9 = -11.5, ceilingY = 16, tested on **transform.position** with strict `>` / `<` | `Stage.cs:65-67`, `MatchController.cs:148` |
| Spawns / respawn | transform (±halfWidth·0.45, groundY+1.5) / (0, 6) | `Stage.cs:68-69`, `MatchController.cs:86` |
| Fighter collider | 0.75 x 1.35, offset (0, -0.05) → **feet = transform.y - 0.725**, head = transform.y + 0.625, half-width 0.375; x offset 0 | `Fighter.cs:97-99` |
| Fighter vs fighter | collision ignored | `MatchController.cs:72` |
| Ground probe | `OverlapBox` centre (cx, feet-0.05), size (0.9·0.75 = 0.675, 0.1) → band [feet-0.1, feet], half-width 0.3375; skips self, triggers, `droppedPlatform`, non-static bodies (minions); `vy > 0.5` forces not grounded; grounded → airJumps = 1 | `Fighter.cs:240-256` |
| Drop-through | `Jump && Down && Grounded && TryDropThrough()`: same probe box, first collider with a `PlatformEffector2D` → `IgnoreCollision(true)`, `dropTimer = 0.35`; jump consumed (no jump, no FX). On the main stage it returns false → normal ground jump | `Fighter.cs:291-297,347-361` |
| Drop timer | decremented in `TickTimers` (start of Update, before `GroundCheck`); at ≤ 0 restores collision with that one platform | `Fighter.cs:216-224` |
| Ring-out side effects | HP -15% of max, position = respawn, velocity 0, hitstun 0, invuln 1.5 s; **does not touch dropTimer/droppedPlatform** | `Fighter.cs:692-708` |
| Lava Forge lava | sprite only, no collider, no hazard | `Stage.cs:39` |
| Body | Continuous CCD, friction 0, bounciness 0 | `Fighter.cs:95,100` |

**Coordinate conversion.** Sim Y is the fighter's **feet** (collider bottom), relative to groundY:
`simY = (transform.y - 0.725 - (-2.5)) x 1000 = (transform.y + 1.775) x 1000`; surfaces: `simY = (worldY + 2.5) x 1000`.
Sim X = transform.x x 1000 (collider has no x offset). Fighter box in sim: x ∈ [X-375, X+375], y ∈ [Y, Y+1350].

## 1. Stage table (sim milli)

Main stage for all: top 0, bottom -1200, sides ±HalfWidth. Platform thickness 300 (top - 300 = underside).
Platform top = (pos.y + 0.15 + 2.5) x 1000.

| | Sky Dojo (0) | Neon City (1) | Lava Forge (2) |
|---|---|---|---|
| HalfWidth | 8000 | 9000 | 7000 |
| Platform A (cx, top, halfW) | (-4200, 3050, 1700) | (-5500, 2450, 1700) | (-3600, 3250, 1500) |
| Platform B | (4200, 3050, 1700) | (5500, 2450, 1700) | (3600, 3250, 1500) |
| Platform C | (0, 5550, 1500) | (0, 4650, 2000) | none |
| BlastX (ring-out if \|X\| > ) | 17000 | 18000 | 16000 |
| KillY (ring-out if Y < ) | -9725 | -9725 | -9725 |
| CeilingY (ring-out if Y > ) | 17775 | 17775 | 17775 |
| Spawn P0 / P1 (X, Y) | (∓3600, 775) | (∓4050, 775) | (∓3150, 775) |
| Respawn (X, Y) | (0, 7775) | (0, 7775) | (0, 7775) |

Platform order A, B, C is the build order and is the platform index (0, 1, 2) used by `DropPlatform`.

**Current sim vs original (all are fixes):**

| Const | Sim now | Original (Sky Dojo) | Note |
|---|---|---|---|
| `StageHalfWidth` | 8000 | per stage | becomes table |
| `BlastX` | 17000 | 17000 / 18000 / 16000 | per stage |
| `BlastBottom` | -9000 | **-9725** | sim forgot the 725 feet offset |
| `BlastTop` | 18000 | **17775** | 16 - 0.725 + 2.5 |
| `RespawnY` | 6000 | **7775** | same offset |
| Spawn | (±3600, 0) grounded | (±halfWidth·450, **775**) airborne | original drops 0.775 u onto the stage, lands about tick 12 |

Respawn consequence: at X = 0 the respawned fighter lands on platform C on Sky Dojo (5550) and Neon City (4650); on Lava Forge it lands on the main stage.

## 2. One-way platforms (surfaceArc 170)

The effector keeps a contact only when its normal is within ±85° of up, so in practice a platform collides only with a body coming down onto its top. Box2D/Unity also keeps a contact that was rejected at its start rejected for as long as it lasts, so a fighter that entered from below or from the side never snaps up mid-pass.

**Deterministic landing rule** (per platform p not currently dropped, evaluated after integration):

```
landP = Vy <= 0 && prevY >= top_p && Y <= top_p && |X - cx_p| < halfW_p + 375
if landP: Y = top_p; YRem = 0; Vy = 0; landed = true
```

| Symbol | Type | Range | Description |
|---|---|---|---|
| prevY, Y | int milli | -10000..18000 | feet before / after this tick's integration |
| Vy | int sub | ±~400000 | velocity after gravity this tick |
| top_p, cx_p, halfW_p | int milli | table §1 | platform geometry |
| 375 | int milli | const `HurtHalfW` | fighter collider half-width (support width) |
| landP | bool | | true → snap onto the platform |

- Bounded output: Y is moved only to exactly `top_p`. If more than one surface qualifies in a tick (not reachable with this geometry, since the max fall is 400 milli/tick and tops are at least 2450 apart), take the highest top.
- **Feet inside the slab** (top-300 < Y < top) when the fighter starts falling: **no landing, ever, until the feet go back above the top.** `prevY >= top` is false on every tick of that pass, which matches Box2D keeping the rejected contact.
- Sides and underside: no collision, no push, no velocity change.
- Worked example: Sky Dojo platform A, X = -4200, prevY = 3120, Y = 3010, Vy = -60000 → |dx| = 0 < 2075 and 3120 ≥ 3050 ≥ 3010 → Y = 3050, Vy = 0.

Support width (375) and probe width (337.5) differ. See §5.

## 3. Drop-through

| Item | Rule |
|---|---|
| Trigger | Jump press with Down held (`dirY < 0`) while `Grounded`, and the ground probe overlaps a platform (not the main stage). The first platform in index order wins; only one can overlap. |
| Effect | `DropTicks = 21`, `DropPlatform = index`. **The jump is consumed:** no ground jump, no air-jump spend, Vy unchanged. |
| On the main stage | Probe finds no platform → falls through to the normal ground jump (Vy = JumpVel). There is no drop. |
| Duration | 0.35 s x 60 = **21 ticks** (exact, no ceil needed). Set on tick T in the action phase. At the start of each later tick, `DropTicks--`; when it reaches 0 (start of tick T+21) `DropPlatform = -1` before that tick's probe and physics. The platform is ignored for the physics of ticks T..T+20 = 21 steps. |
| What is ignored | Only `DropPlatform`, for both landing (§2) and the ground probe (§5). Other platforms and the main stage still apply. |
| Timer ends while overlapping | Collision is restored, but the §2 rule needs `prevY >= top`, so a fighter whose feet are below the top keeps falling through (Box2D's rejected contact). With no fast-fall the feet are about 2290 milli below the top after 21 ticks anyway. |
| Re-drop while DropTicks > 0 | Overwrite the slot (the old platform comes back). The original leaks the old ignore forever, but this cannot happen with this geometry (it needs a second platform within 0.35 s of falling). |
| Ring-out | Does **not** clear DropTicks/DropPlatform (as in the original). |
| Fast-fall | Not on the drop tick (Grounded is still true then), but from the next tick if Down is held, using the existing fast-fall rule. |

## 4. Solid main stage

Box: x ∈ [-HW, HW], y ∈ [-1200, 0]. Fighter box as in §0. Overlap means `|X| < HW + 375 && Y < 0 && Y + 1350 > -1200`.
Resolve after integration, in this order (this is the swept-AABB face choice; Box2D CCD with bounciness 0 zeroes only the normal velocity component):

1. **Top (land):** `Vy <= 0 && prevY >= 0 && Y <= 0 && |X| < HW + 375` → Y = 0, YRem = 0, Vy = 0, landed.
2. Else if overlapping and `prevY + 1350 <= -1200` → **underside bonk:** Y = -2550, YRem = 0, `Vy = min(Vy, 0)`. Vx unchanged.
3. Else if overlapping → **side wall:** X = sign(prevX)·(HW + 375), XRem = 0; if Vx points inward, Vx = 0. Vy unchanged (friction is 0, so there is no wall drag).
4. Fallback (overlapping with prevX already inside, which cannot happen in play): treat as top: Y = 0, Vy = 0.

Pinned against the wall: each tick the air accel adds +10000 sub toward the wall, step 3 zeroes it again, and X stays at ±(HW+375). The original does the same (Update adds 40·dt, then the physics step zeroes it).

**The sim's `onStage = |X| <= StageHalfWidth` (centre test) is wrong.** The original stands on the stage out to |X| < HW + 375.

## 5. Ground state and the probe on platforms

A platform is ground in every sense: `AirJumps = 1`, ground accel/friction, ground jump, drop-through.

Probe, extended exactly from `Fighter.cs:244` (band [Y-100, Y], half-width 337.5) to every surface s (main stage: top 0, bottom -1200, cx 0, halfW HW; platforms from §1, skipping `DropPlatform`):

```
probe_s  = (top_s - thick_s) < Y && Y <= top_s + 100 && 2·|X - cx_s| < 2·halfW_s + 675
Grounded = landed || (Vy <= GroundProbeMaxVy && OR_s probe_s)
```

Use the integer form `2·|dx| < 2·halfW + 675` so the 337.5 half-width stays exact. For the main stage this is the same as today's `0 <= Y <= 100` once §4 keeps the feet from going below 0 while over it.

Consequences, all present in the original and kept:
- **Ledge band:** for halfW+337.5 ≤ |dx| < halfW+375 the fighter is supported (does not fall) but **not Grounded**: Jump spends the air jump and air accel applies.
- **Mid-air grounding inside a slab:** a fighter rising from below whose apex leaves the feet inside the 300-milli slab counts as Grounded on the ticks where Vy ≤ 7500 and the feet are in (top-300, top+100]. That resets AirJumps and allows a ground jump or a drop-through (which eats the jump). On **Neon City**, a plain ground jump under a side platform does this: the apex is about 2393, inside (2150, 2450], for about 7-8 ticks.

**Arc-term gating (decision D-S1).** `Physics()` uses `airborne = !Grounded || Vy > 0` for the C50 term. That is wrong in the slab case (Grounded but falling freely). Options:
1. **Gate on support (Recommended):** airborne = not resting at the start of the tick, i.e. no surface s with `Y == top_s`, horizontal overlap `|X-cx_s| < halfW_s + 375`, not dropped. It is derived from state, so no new field and no hash change. It also fixes the near-floor 0 < Y ≤ 100 case by at most 1 milli per tick.
2. **Keep gating on Grounded:** less code, but the fall inside a slab drifts by about 1 milli per tick from the original.

## 6. Ring-out

The check runs after physics, on feet coordinates, with strict comparisons: `|X| > BlastX || Y < KillY || Y > CeilingY` (values in §1).
**The ceiling is a ring-out, not a KO.** All edges are the same event: -15% max HP (the existing `RingOutPenaltyPermille 150`), respawn at (0, 7775) with Vx = Vy = 0, Stun 0, Invuln 90, airborne. A KO happens only if that penalty brings HP to 0.

## 7. Projectiles and minions vs the stage (for later steps)

- **Projectile** (`CombatObjects.cs:8-118`): no Collider2D/Rigidbody2D (no `Physics2D` calls), so it passes through the main stage and platforms. The sim's current despawn by `BlastX/BlastBottom/BlastTop` must become per-stage, and the original projectile's own despawn rule still needs to be checked in that step.
- **Minion** (`CombatObjects.cs:119-146`): dynamic Rigidbody2D, gravityScale 3 (g = 29.43 u/s²), CircleCollider r 0.22. It ignores both fighters and other minions, so it **collides with the solid main stage and lands on one-way platforms** (the effector applies with its default collider mask; not checked). It has no drop-through, and fighters' probes ignore it (non-static). The minion step needs the §2 and §4 rules with a radius-220 circle approximated as a box, half-width 220, feet = centre - 220.
- The CPU AI (`FighterInput.cs:165`) reads `halfWidth - 0.3` and `groundY - 0.5` (transform). This matters for the AI port, not for this step.

## 8. Code contract

- `ChibiMatchConfig.Stage` (int). Valid 0..2; any other value = 0 (matches `default:`). Mix it into `ComputeHash` after Passive1. Bump `RulesVersion` to 7 (stages, platforms, feet-offset blast/respawn fixes).
- `static readonly ChibiStageDef[] Stages` (index = Stage), immutable, not part of the state. Fields: `HalfWidth, BlastX, KillY, CeilingY, SpawnX, SpawnY, RespawnX, RespawnY`, `ChibiPlatform[] Platforms` (`Cx, Top, HalfW`). Consts: `MainBottom = -1200`, `PlatThick = 300`, `ProbeHalfW2 = 675` (2 x 337.5), `SupportHalfW = HurtHalfW = 375`, `HurtH = 1350`.
- Per-fighter state (in `CopyFrom` and `Hash`): `int DropTicks` (0..21), `int DropPlatform` (-1 or 0..2). Init: 0 / -1. Not cleared by ring-out.
- `Init`: X = ±SpawnX, Y = 775, Grounded = false, Vy = 0. `Physics()` takes the stage def and applies §4, then §2, then §5, all using prevX/prevY from before integration.
- Remove `StageHalfWidth`, `BlastX`, `BlastBottom`, `BlastTop`, `RespawnY` from `ChibiSimConst` (or keep them only as Sky Dojo aliases) so nothing reads the stale values.

## 9. Reference test values (QA)

Tolerances: positions ±1 milli where exact, tick counts ±1 (variable-dt original), Box2D rest offset (contact offset 0.01) not modelled.

| # | Setup | Expected |
|---|---|---|
| 1 | Sky Dojo, X=-4200, Y=4000, Vy=0, airborne | lands Y=3050, Vy=0, Grounded, AirJumps=1, about tick 14 |
| 2 | Neon City, X=5500, Y=7000 falling | lands Y=2450 |
| 3 | Lava Forge, X=3600, Y=5000 falling | lands Y=3250 |
| 4 | Sky Dojo platform C, X=0, Y=7775 (respawn) | lands Y=5550 about tick 21; Neon C (Y=4650) about tick 25; Lava main stage Y=0 about tick 40 |
| 5 | Sky Dojo, grounded at X=-4200 Y=0, ground jump | apex about 2393 (< 2750), never snapped to 3050, returns to Y=0 |
| 6 | Same, air jump at apex | feet pass 3050 going up with no snap, then land Y=3050 on the way down |
| 7 | Neon City, X=5500, ground jump | no snap at 2450; Grounded true for about 7-8 mid-air ticks with feet in (2150, 2450]; AirJumps reset to 1; lands Y=0 |
| 8 | Walk off Sky Dojo A left edge | Grounded at X=-6237, not Grounded at -6238 (ledge band), still supported down to X > -6275, falls at X ≤ -6275 |
| 9 | Main stage edge, Sky Dojo | Grounded while \|X\| ≤ 8337, supported while \|X\| < 8375 |
| 10 | Sky Dojo A resting, Down+Jump on tick T | Vy unchanged (no jump), DropTicks=21, DropPlatform=0; feet below 3050 from tick T; DropPlatform=-1 at start of T+21; no re-land on A; lands Y=0 about tick T+24 (no fast-fall) |
| 11 | Main stage Y=0, Down+Jump | normal ground jump Vy=195000; DropTicks stays 0 |
| 12 | Down+Jump while airborne with an air jump left | normal air jump (Grounded false → no drop) |
| 13 | Sky Dojo, X=-9000, Y=-600, holding right | X clamps to -8375, Vx=0 every tick while Y ∈ (-2550, 0) |
| 14 | Sky Dojo, X=0, Y=-3000, Vy=172500 | head reaches -1200 → Y=-2550, Vy=0, then falls |
| 15 | Ring-out Sky Dojo | X=17001 ring-out, 17000 not; Y=-9726 ring-out, -9725 not; Y=17776 ring-out, 17775 not |
| 16 | Ring-out Neon / Lava | X bound 18000 / 16000; Y bounds as row 15 |
| 17 | Spawns | (∓3600 / ∓4050 / ∓3150, 775), airborne, land Y=0 about tick 12 |
| 18 | Config hash | Stage 0 vs 1 → different `ComputeHash`; Stage 5 plays as Sky Dojo |
| 19 | Rollback | save mid-drop (DropTicks=10), restore, replay → identical hash |

## Unverified

- Box2D fat-AABB contact persistence: if a rising fighter's feet clear a platform top by only a few milli and it falls back, the original might keep the rejected contact and fall through (D-S2). Recommendation: keep the clean §2 test and check empirically in the original (jump so the apex is just above a top).
- Strict vs inclusive edges of `OverlapBox` and contact skin (about 10 milli) at the support/probe borders.
- Whether 21 float decrements of 1/60 from 0.35 reach ≤ 0 on frame 21 or 22 (D2 says 21).
- PlatformEffector2D collider mask for minions; the original projectile despawn rule.
- Tick counts in §9 are analytic (semi-implicit Euler + C50) and need confirming by test runs.
