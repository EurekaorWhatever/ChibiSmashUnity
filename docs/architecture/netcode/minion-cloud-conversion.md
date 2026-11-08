# Port step 13: Mage minions and Thunder Cloud (conversion spec)

Status: draft for review (2026-11-08). Targets RulesVersion 13.
Sources: `CombatObjects.cs` (CO) Minion :119-171, ThunderCloud :175-220; `Fighter.cs` (F) dispatch :740-744,
SummonMinions :886-892, SummonCloud :894-899, ReceiveHit :545-599; `CombatAuthority.cs` HitSpec.Make :13;
feature-inventory.md rows 79-80, 174-175, sections "Per minion" / "Per thunder cloud", port order item 13.
Conventions reused: units and `Integrate` (movement-conversion.md), C50 arc term (movement-conversion.md D1),
stage geometry and D-S1/D-S2 (stage-conversion.md), timers `ticks = seconds x 60` (dash-conversion.md D2),
`SpawnFrame` "first acts on the next game step" (projectile-conversion.md), frame-0 rule (multiphase-conversion.md section 1).

Values marked **TBD-verify** are derived analytically and must be confirmed by the first test run or an editor measurement.

---

## 0. Unit conversions used in this doc

| Original | Sim | How |
|---|---|---|
| 1 world unit (u) | 1000 milli | |
| 1 u/s | 15000 sub/tick | 1000 / 60 x 900 |
| 3.5 u/s (walk) | **52500** sub/tick | exact |
| 2 u/s (hop-back x, bonk kb) | **30000** sub/tick | exact |
| 5 u/s (hop y) | **75000** sub/tick | exact |
| 3 u/s (cloud kb y) | **45000** sub/tick | exact |
| gravityScale 3 → 9.81 x 3 = 29.43 u/s² | **MinionGravity = 7358** sub/tick² | 29430 / 3600 x 900 = 7357.5, rounded half up (see 1.2) |
| C50 arc term for that gravity = 0.1 g | **MinionArc = 736** sub/tick | 735.75, rounded |
| seconds | ticks = seconds x 60 | life 6 s = 360, attackCd 0.4 s = 24, bonk cd 1 s = 60, cloud life 4.6 s = 276, first strike 0.8 s = 48, period 1.1 s = 66, bolt 0.15 s = 9, minion stun 0.15 s = 9, cloud stun 0.35 s = 21, SummonMinions lock 0.4 s = 24, SummonCloud lock 0.3 s = 18 |
| HitSpec mult 0.35 / 0.8 | Mult 350 / 800 permille | |

Knockback is stored like projectile KbX/KbY (15000 sub per u/s; Homing Fireball kb (5, 4) is stored 75000 / 60000) and is
halved at hit time by `ChibiCombatStats.HalfKnockback(Magic)` exactly like every other Mage hit (F:548 KnockbackScale).

---

## 1. Minion

### 1.1 Spawn (F:886-892, CO:126-147)

- Skill 1 of the Magic class. The skill press runs `SummonMinions()` inline (frame 0 of the move): actionLock 0.4 s
  = **24 ticks**, no hitbox, then all 3 minions spawn on that same tick.
- Positions, k = 0, 1, 2 in this order (= slot allocation order):
  `X = f.X + f.Facing * (600 + 500k)`, `Y = f.Y + CenterOffY(675) + 300 = f.Y + 975`.
  So offsets 600 / 1100 / 1600 milli ahead of the Mage, 975 above the feet. `f.X/f.Y` are read at the same point in
  `UpdateFighter` where the Shot moves read the spawn position today (**TBD-verify** pre- or post-Physics; use the
  same as `Shot` so the frame-0 rule is consistent).
- Initial state: `Vx = Vy = 0`, `XRem = YRem = 0`, `Life = 360`, `AttackCd = 24`, `Facing = +1` (visual only until
  its first update), `SpawnTick = s.GameTicks`.
- A minion with `SpawnTick == s.GameTicks` is skipped by `UpdateMinions` that step (Unity: a component added during
  Update gets its first Update next frame). Its first update is the next game step ("tick 1" in this doc).
- Body: CircleCollider2D radius 0.22 → **MinionR = 220** milli. `X, Y` is the circle centre.
- Collisions: `IgnoreCollision` with the owner, the opponent and every existing minion (CO:142-145; later minions
  ignore earlier ones too, so all minion pairs are ignored, both owners' minions included). Result: **a minion collides
  only with the stage** (solid main stage box + one-way platforms). No minion-fighter push, no minion-minion push.
  Projectiles in the original use distance checks, not colliders, so they pass through minions. Verified: the only
  Collider2D objects in a match are the stage boxes (Stage.cs:79, :91), the fighters (MatchController.cs:88) and the
  minions (CO:140); Fire Ring, clouds and projectiles have none.
- Material: the minion collider has **no PhysicsMaterial2D**, so Unity's default friction 0.4 applies against the stage
  (fighters use a 0-friction material, F:100). See decision D-C1.

### 1.2 Per-tick update (CO:149-171), exact order

Runs once per game step (Time.deltaTime is scaled, so hitstop / cinematic slow it exactly like fighters, step 10).

1. `Life -= 1; AttackCd -= 1;`
2. Despawn if `Life <= 0` or `Y < -20000` (CO:155; centre below -20 u). `target == null` never happens in a match.
   Despawn = `Active = false` (the burst FX is view-only). Return.
3. `dx = T.X - X`, `dy = (T.Y + 675) - Y` where T = opponent (`Center` = feet + 675).
   `dir = dx >= 0 ? +1 : -1` (Mathf.Sign(0) = +1). `Facing = dir` (view flip).
4. `Vx = |dx| > 400 ? dir * 52500 : 0` (Vy kept).
5. Bonk: if `|dx| < 700 && |dy| < 900 && AttackCd <= 0`:
   - `AttackCd = 60`
   - `ApplyHit(s, owner, 1 - owner, { Mult 350, KbX 30000, KbY 30000, Stun 9, Dir = dir, Unblockable = false })`
     (the result is ignored: cd and hop happen even on a miss, invulnerable target, parry, ward or block)
   - `Vx = -dir * 30000; Vy = 75000` (hop back)
6. Physics (new `MinionPhysics`, section 1.3).

Note on the hop-back: the original writes x velocity again on the very next Update (step 4), so the "-2 u/s back"
lasts only one physics step. In the sim it lasts exactly the bonk tick, then step 4 overwrites it. This is the faithful
copy; the visible hop is almost entirely vertical (5 u/s up).

Note on the bonk using the pre-move position: steps 3-5 use the position from the previous tick, Physics moves it
afterwards. Same as the original (Update reads `transform.position` set by the last physics step).

### 1.3 MinionPhysics (gravity scale 3 + stage)

Same structure as fighter `Physics()` (movement-conversion.md section 2, stage-conversion.md sections 2, 4, 5), with
the minion's own gravity, arc term and circle body:

```
resting  = IsMinionResting(m)                       // bottom (Y - 220) exactly on a surface top, centre over it (D-C2)
airborne = !resting || Vy > 0
Vy -= MinionGravity                                  // 7358
if (resting && Vy <= 0 && D-C1 = A) Vx = towards0(Vx, MinionFrictionSub)   // see D-C1
Integrate(X, XRem, Vx)
Integrate(Y, YRem, airborne ? Vy - MinionArc : Vy)   // 736
stage: main box top (land, bottom crossing 0 from above while Vy <= 0, centre over the box),
       underside (Y + 220 crossing MainBottom from below: Y = MainBottom - 220, Vy = min(Vy, 0)),
       side wall (circle overlapping the box side from outside: X = side * (HW + 220), Vx inward = 0)
platforms: land only when the bottom crosses the top from above while falling (D-S2 clean rule), centre over the
           slab (D-C2); no drop-through, no underside, no sides (PlatformEffector2D one-way)
landing: Y = top + 220, YRem = 0, Vy = 0
```

- Gravity is rounded (7357.5 → 7358, arc 735.75 → 736). Error: about 0.5 sub/tick², under 1 milli after a full
  second of falling. No decision needed (same kind of rounding as `DashSagSub`, dash-conversion.md row 6).
- No horizontal bounds. A minion that walks off the stage falls until `Y < -20000` and despawns (step 2). The fighter
  blast zones and ring-out do not apply to minions.

### 1.4 Despawn, respawn, match end

| Situation | Original | Sim |
|---|---|---|
| Life reaches 0 (360 updates) | destroyed (CO:155) | `Active = false` on update 360 |
| Falls below y -20 | destroyed | same, `Y < -20000` |
| Opponent rings out and respawns | minion keeps chasing the new position | same (reads T each tick) |
| Opponent respawn invulnerability (90 ticks) | ReceiveHit misses, cd still set to 1 s, still hops | same (ApplyHit returns early, step 5 still runs) |
| Owner rings out / respawns | minions live on, still owned | same |
| Parry stance / perfect shield parry by the target | ReceiveHit treats the minion as a hit from the **Mage**: reflect damages the Mage, perfect parry stuns the Mage 27 ticks, wherever the Mage stands | same (decision D-C4) |
| Match ends (KO or timer) | `!Match.Running`: hits miss; minions keep walking visually | `Step` returns early on `MatchOver`, everything frozen. The view may keep idling them; no state impact |
| Pool full | n/a | spawn dropped, `DroppedSummons++` (tests assert 0) |

### 1.5 Update order

Unity calls `Update` on Fighter, Projectile, Minion and ThunderCloud components in an order that the project never sets
(no `DefaultExecutionOrder`), and Rigidbody2D physics runs in FixedUpdate before all Updates. There is no "true"
order to copy. Proposed sim order inside `GameStep` (decision D-C3):

```
UpdateFighter 0, 1 -> HoldPhase -> melee probes/apply -> TickFireRings -> UpdateProjectiles
  -> UpdateMinions (slot 0..MaxMinions-1) -> UpdateClouds (slot 0..MaxClouds-1) -> ResolveBoundsAndMatchEnd
```

Each minion does its update and its physics before the next slot is processed (they cannot touch each other).
Order only matters for RNG draws (crit / ward / hex inside ApplyHit) and for which hit lands first when a KO happens.

---

## 2. Thunder Cloud

### 2.1 Spawn (F:894-899, CO:183-197)

- Skill 2 of the Magic class, inline on the press tick: actionLock 0.3 s = **18 ticks**, no hitbox. Replaces the
  current stand-in strike move (SIM:403 `Mv(40, 3, 14, 1400, ... flags: C | T)`).
- `X = f.X`, `Y = f.Y + 675 + 3000 = f.Y + 3675` (owner Center + 3 u up).
- `Life = 276`, `StrikeTicks = 48`, `BoltTicks = 0`, `SpawnTick = s.GameTicks`; first update next game step.
- No collider, no gravity, not a projectile. It is a direct-hit object (not `SpawnProjectile`), so: projectile pool,
  `DestroyedBy` (Spin Guard / Fire Ring projectile destruction, F:521) and the projectile bounds do **not** apply.

### 2.2 Per-tick update (CO:199-220), exact order

1. `Life -= 1`; if `Life <= 0` → `Active = false`, return.
2. Follow: goal `GX = T.X`, `GY = T.Y + 675 + 3200 = T.Y + 3875`.
   Original `Lerp(p, goal, 2.5 * dt)` = `p + (goal - p) / 24` per tick (2.5 / 60 = 1/24; t never exceeds 1).
   Integer form (decision D-C5, recommended A): the cloud keeps position in **micro-units** (`CX, CY`, 1 milli = 1000):
   ```
   CX += RoundHalfAway((GX*1000 - CX), 24)      // RoundHalfAway(n, d) = sign(n) * ((|n| + d/2) / d)
   CY += RoundHalfAway((GY*1000 - CY), 24)
   X = FloorDiv(CX, 1000); Y = FloorDiv(CY, 1000)   // milli, for the strike check and the view
   ```
   Residual error vs. the float original: at most 0.5 micro per tick, never accumulates past 12 micro (the step
   shrinks the error by 23/24 each tick). Stops within 12 micro of the goal; a pure milli version would stall up to
   23 milli away.
3. `BoltTicks -= 1` (if > 0). View draws the bolt while `BoltTicks > 0` (CO:209-210).
4. `StrikeTicks -= 1`; if `StrikeTicks <= 0`:
   - `StrikeTicks = 66` (assignment, not `+=`: no drift), `BoltTicks = 9`
   - if `|T.X - X| < 750` (x only, no y check; X after this tick's follow):
     `ApplyHit(s, owner, 1 - owner, { Mult 800, KbX 0, KbY -45000, Stun 21, Dir = +1, Unblockable = false })`

Strike updates: 48, 114, 180, 246. Update 276 despawns before the strike check, so **at most 4 strikes** per cloud.

### 2.3 "Projectile-type" hit

`HitSpec.Make(0.8, (0,-3), 0.35, projectile: true)` sets `HitSpec.projectile`, but **nothing reads that field**
(grep: no `.projectile` read in Fighter / CombatAuthority). So the cloud strike resolves exactly like a melee hit
from the Mage through the normal `ApplyHit`: parry stance reflects onto the Mage, perfect shield parry stuns the Mage,
Ward can negate, blocking takes chip + guard damage, crit/hex draws as usual. It does not use the projectile code path
and cannot be destroyed by Spin Guard or Fire Ring. Same for the minion bonk (plain `HitSpec.Make`, no flag).

---

## 3. Counts, pools and state

### 3.1 Max simultaneous

| Object | Cooldown (sim move table) | Life | Per Mage alive | Both fighters Mage |
|---|---|---|---|---|
| Minion | 720 ticks (12 s) **TBD-verify** against ClassData | 360 + 1 | 3 (one cast) | 6 |
| Cloud | 600 ticks (10 s) **TBD-verify** | 276 + 1 | 1 | 2 |

No cooldown reset or reduction is known for these skills (**TBD-verify**: ult / passive effects on skill cd).

**Pool sizes (recommended):** `MaxMinions = 12`, `MaxClouds = 4` (2x headroom, cheap: about 13 ints per slot).
Overflow drops the spawn and increments `DroppedSummons` (same pattern as `DroppedShots`, D-P2). Both constants are
mixed into the config hash next to `MaxProjectiles` (peers with different pool sizes would desync).

### 3.2 New state (all copied in `CopyFrom`, all hashed in `ComputeChecksum`)

`ChibiMinionSim` (class, like `ChibiProjectileSim`):
`bool Active; int Owner, X, Y, XRem, YRem, Vx, Vy, Life, AttackCd, Facing, SpawnTick;`
(hash Active always; the rest only when Active, same as projectiles)

`ChibiCloudSim`:
`bool Active; int Owner, CX, CY, Life, StrikeTicks, BoltTicks, SpawnTick;` (X/Y milli are derived from CX/CY, not stored)

`ChibiSimState`: `ChibiMinionSim[] Minions = new [MaxMinions]`, `ChibiCloudSim[] Clouds = new [MaxClouds]`,
`int DroppedSummons`. `Init` clears all slots and the counter.

`ChibiSimConst`: `MinionGravity 7358, MinionArc 736, MinionR 220, MinionWalkSub 52500, MinionStopDx 400,
MinionBonkDx 700, MinionBonkDy 900, MinionLife 360, MinionFirstCd 24, MinionCd 60, MinionMult 350, MinionKb 30000,
MinionStun 9, MinionHopVx 30000, MinionHopVy 75000, MinionKillY -20000, MinionSpawnOx 600, MinionSpawnStep 500,
MinionSpawnOy 300, MinionFrictionSub (D-C1), MaxMinions 12; CloudSpawnOy 3000, CloudGoalOy 3200, CloudLerpDiv 24,
CloudLife 276, CloudFirstStrike 48, CloudPeriod 66, CloudBolt 9, CloudStrikeDx 750, CloudMult 800, CloudKbY -45000,
CloudStun 21, MaxClouds 4.` RulesVersion 12 → 13 (remark line lists step 13).

### 3.3 Removal of the stand-ins

- Move table Magic slot 3 (`Shot(... ChibiProjKind.SeekerOrb ...)`, SIM:400-402) → a no-hit 24-tick lock move whose
  press tick calls `SpawnMinions`. Slot 4 (thunder strike stand-in, SIM:403) → a no-hit 18-tick lock move whose press
  tick calls `SpawnCloud`. Cooldowns stay as they are (720 / 600) unless the TBD-verify above says otherwise.
  Exact `Mv(...)` field mapping is the programmer's (startup/active/recovery must sum to the lock with no hitbox).
- Delete `ChibiProjKind.SeekerOrb` (last enum member, so no other kind index shifts) and its view sprite case.
- Feature inventory rows 79, 80, 174, 175 move to "yes" after the tests below pass.

---

## 4. Reference values (analytic, **TBD-verify** by test)

Setup common: stage 0, fighters on the main stage top (`Y = 0`), opponent standing still, no inputs, D-C1 = A unless
stated. "Tick n" = the n-th minion/cloud update after the spawn tick (spawn tick = tick 0).

| # | Quantity | Value |
|---|---|---|
| R1 | Minion fall from spawn (centre 975, bottom 755 above the ground) to landing | lands on **tick 14** (cumulative drop after 13 ticks 679146 sub < 679500 needed; after 14: 782894). Then `Y = 220`, `Vy = 0` |
| R2 | Airborne walk | 52500 sub/tick: 60 ticks = exactly **3500 milli** |
| R3 | Grounded walk, D-C1 = A | 48968 sub/tick (3.26456 u/s): 60 ticks = 2938080 sub = **3264 milli + 480 rem**. D-C1 = B: 3500 milli |
| R4 | First bonk, Mage at X = 0 facing right, opponent feet at X = 3000; minion k = 0 spawns at X = 600 | D-C1 = A: **tick 32** (after tick 31 dx = 659 < 700; after tick 30 dx = 713). D-C1 = B: **tick 31** (after 30 dx = 650; after 29 dx = 708). cd (24) is not the limiter |
| R5 | Bonk timing limited by cd: minion spawned already within reach (opponent 1000 ahead, k = 0 at dx 400) | first bonk **tick 24**, next tick 84 if still in reach |
| R6 | Hop after a bonk on the ground | airborne 19 more ticks, lands on **bonk tick + 19** (Vy 75000; sum test: 3679(n+1) >= 74264 first at n = 20 counting the bonk tick) |
| R7 | Minion life | inactive after **tick 360**, active on tick 359 |
| R8 | Cloud X, owner at X 0, opponent fixed at X 3000 | X_n = 3000 (1 - (23/24)^n): **n = 24: 1919.8 → 1919 or 1920**, **n = 60: 2766.6 → 2766 or 2767** (test asserts within ±1 milli of the real value; exact integer from the 2.2 algorithm) |
| R9 | Cloud Y, both on the ground | starts 3675, goal 3875: Y_n = 3875 - 200 (23/24)^n |
| R10 | Strike ticks | **48, 114, 180, 246**; inactive after tick 276; 4 strikes max |
| R11 | Cloud strike hit, opponent defenceless, Mage attacker | Mult 800, KbY -45000 halved → -22500, stun 21 |
| R12 | Minion bonk hit | Mult 350, kb (dir·30000, 30000) halved → (dir·15000, 15000), stun 9 |

---

## 5. Decisions for the user

**D-C1 - Minion floor friction (exact copy needs Box2D friction).** The minion has no "no-friction" material, so on
the ground Unity's default friction (0.4) shaves about 0.235 u/s off its walk every physics step. Its real walking
speed on the ground is about 3.26 u/s, not the 3.5 u/s written in the code (in the air it is the full 3.5).
- **A (recommended):** copy it: on the ground the minion walks 48968 sub/tick (about 3.26 u/s); in the air 52500.
  Matches what players actually see in the original. Needs one editor measurement to confirm the number (TBD-verify).
- **B:** ignore friction, always 3.5 u/s. Simpler, but minions arrive about 7% sooner than in the original.

**D-C2 - Minion on a ledge corner.** The original body is a circle; at a ledge edge Box2D lets it roll/slide off the
rounded corner, which an integer sim cannot copy exactly.
- **A (recommended):** the minion stands on a surface while its centre is over it (|X - edge| inside the top), falls
  as soon as the centre passes the edge, and is pushed out by the main stage side once it is below the top. Clear and
  testable; differs from the original only within 0.22 u of a ledge.
- **B:** treat the minion as a 0.44 u square (stands while any part is over the top). Minions hang on ledges a bit
  longer than in the original.

**D-C3 - Update order of minions and clouds.** The original never fixes which object runs first in a frame.
- **A (recommended):** fighters, then projectiles, then minions, then clouds, each by slot number. Matches the order
  the existing sim already uses for projectiles (objects after fighters).
- **B:** minions and clouds before projectiles. No visible difference except which hit lands first in a rare
  same-tick KO.

**D-C4 - Parry against a minion or the cloud hurts the far-away Mage.** In the original, a parry or perfect shield
against a minion bonk or a thunder strike stuns or damages the Mage, even across the stage, because the hit is filed
as coming from the Mage. This looks like an oversight, but it is what the original does.
- **A (recommended):** copy it (the rule for this port is "exactly like the original").
- **B:** a parried minion/cloud hit is simply cancelled (no stun, no reflect damage to the Mage).

**D-C5 - Cloud follow rounding.** The cloud moves 1/24 of the way to its goal each tick, which never comes out in
whole units.
- **A (recommended):** keep the cloud position in thousandths of a milli and round each step; the result stays within
  a hundredth of a milli of the original.
- **B:** keep it in milli and round down; simpler, but the cloud parks up to 0.023 u short of directly above the
  target (could miss a strike that the original would land when the target stands at the 0.75 u edge).

---

## 6. Test list (qa-tester)

All EditMode, deterministic, in `Assets/Tests/EditMode/` next to the existing sim tests. "Press skill N" = one tick
with the skill button edge for the Mage.

1. **Minion spawn positions.** Mage feet (0, 0) facing right presses skill 1 → 3 active minions at (600, 975),
   (1100, 975), (1600, 975); facing left mirrors X. Lock 24 ticks. (F:888-891)
2. **First update next step.** On the spawn tick the minions have not moved; on tick 1 Vy = -7358. (CO:126, SpawnTick)
3. **Landing tick.** R1: minion k = 0 lands on tick 14 with Y = 220, Vy = 0; still airborne after tick 13. (CO:138)
4. **Walk speeds.** R2 / R3 per D-C1. (CO:164)
5. **Stop distance.** Opponent within 400 in x → Vx = 0; at 401 → full walk speed. Sign(0) → dir +1. (CO:162-164)
6. **First bonk tick.** R4 setup → hit lands on tick 32 (D-C1 = A) / 31 (B); opponent HP drops once. (CO:165-168)
7. **Initial and repeat cd.** R5 → first bonk tick 24, second tick 84, none between. (CO:124, :167)
8. **Bonk dy limit.** Opponent standing on a platform so |dy| >= 900 → no bonk while the minion stands under it. (CO:165)
9. **Hop back.** On the bonk tick Vx = -dir·30000, Vy = 75000; on the next tick Vx is back to walk/0; lands bonk + 19. (CO:169, R6)
10. **Bonk hit spec.** Defenceless opponent: damage = 0.35x roll, kb after halving (dir·15000, 15000), stun 9. (CO:168, F:548)
11. **Bonk on invulnerable target.** Opponent in respawn invuln: no damage, AttackCd still 60, hop still happens. (F:547)
12. **Parry stance vs minion.** Opponent in Parry stance → Mage takes reflect damage and ReflectStun; minion stays. (F:550-560, D-C4)
13. **Perfect shield vs cloud.** Shield raised within 11 ticks of a strike → Mage stunned 27, no damage to the opponent. (F:563-571, D-C4)
14. **Minion life.** Active on tick 359, inactive after tick 360. (CO:123, :155)
15. **Fall despawn.** Minion walked off the stage despawns on the first tick with Y < -20000; never earlier by an x bound. (CO:155)
16. **Minions ignore fighters and each other.** Minion overlapping a fighter or another minion is never pushed. (CO:142-145)
17. **One-way platform.** Minion jumping up through a platform is not stopped; falling onto it lands at top + 220. (Stage.cs:94, D-S2)
18. **Main stage side and underside.** Minion below the top pushed out to X = ±(HW + 220); rising into the underside stops at MainBottom - 220. (D-C2)
19. **Cloud spawn.** Press skill 2 → one cloud at (f.X, f.Y + 3675); lock 18 ticks. (F:896-898, CO:188)
20. **Cloud follow.** R8 / R9 within ±1 milli; exact integers equal the 2.2 algorithm. (CO:206-207)
21. **Strike schedule.** R10: strike checks on 48, 114, 180, 246 only; inactive after 276; BoltTicks = 9 on each strike tick. (CO:178-179, :211-216)
22. **Strike x window.** |dx| = 749 → hit; 750 → no hit; no y condition (opponent high above still hit). (CO:217)
23. **Strike hit spec.** Mult 800, KbY -22500 after halving, stun 21, blockable (blocked → chip + guard damage). (CO:218, F:581-596)
24. **Not a projectile.** Spin Guard / Fire Ring active on the opponent does not remove the cloud or minions; projectile pool unchanged. (CO:175-220, F:521)
25. **Both Mages, max load.** Both cast minions and cloud as soon as cooldowns allow for 1800 ticks → at most 6 minions and 2 clouds alive, `DroppedSummons == 0`. (section 3.1)
26. **Rollback.** Snapshot mid-life (minions airborne + cloud mid-strike), run 120 ticks, restore, rerun with the same inputs → identical checksums every tick. Changing any one minion/cloud field changes the checksum. (section 3.2)
27. **Hitstop slows them.** During a 3-real-tick hitstop the minions and clouds do not update except on the game steps. (step 10)
28. **Match end freeze.** After MatchOver no minion/cloud field changes. (F:547 !Match.Running)
29. **Stand-in removed.** `ChibiProjKind` has no SeekerOrb; Magic skill 1 spawns no projectile. (section 3.3)
30. **Config hash.** Changing MaxMinions or MaxClouds changes the config hash. (section 3.1)

---

## Technical-director review

Verdict (2026-11-08): **APPROVE WITH AMENDMENTS.**

Checked against the original: CO:119-220, Fighter.cs:100 (fighter NoFriction material), :736-746 (dispatch, Magic
skill index 1 = minions, 2 = cloud), :886-899, :545-575 (ReceiveHit), CombatAuthority.cs:13-15, ClassData.cs:93
(Magic `skillCooldowns = {4, 12, 10}`), TimeManager.asset (Fixed Timestep 0.02), Physics2DSettings.asset (gravity -9.81,
`m_DefaultMaterial` none, 8 velocity iterations), GameManager.cs:46 (targetFrameRate 120).

Spot-checks that match: walk 3.5 / stop 0.4 / bonk 0.7 x 0.9 / cd 0.4 then 1 / life 6 / kill y -20 / hop (-2 dir, 5);
spawn `Center + (Facing (0.6 + 0.5k), 0.3)`; minion radius 0.22, gravityScale 3, IgnoreCollision with both fighters and
all minions; cloud spawn Center + 3 up, goal Center + 3.2 up, `Lerp(2.5 dt)`, strike 0.8 then 1.1, bolt 0.15, x-only
window 0.75; hit specs (0.35, (2 dir, 2), 0.15) and (0.8, (0, -3), 0.35, projectile); `HitSpec.projectile` is only
written (CombatAuthority.cs:13-15), never read; cooldowns 12 s / 10 s = 720 / 600 are confirmed, so both TBD-verify
marks in 3.1 and 3.3 can be removed. Determinism design (integer body, rounded gravity/arc, micro-unit cloud, fixed slot
order, pools with drop counter, active-slot hashing, pool sizes in the config hash, RulesVersion 13) is sound.

### Amendments

1. **D-C1 basis must be stated.** 48968 comes from one 50 Hz Box2D step: `0.4 x 29.43 x 0.02 = 0.2354 u/s` removed
   per physics step, then the step integrates the reduced speed; `Update` (60-120 fps, always at least one per 50 Hz
   step when fps >= 50) resets Vx to 3.5 before the next step. Write this derivation in 1.3 / D-C1 so it does not read
   as a 60 Hz figure. Combined friction is `sqrt(0.4 x 0.4) = 0.4` (minion and stage colliders both have no material,
   no project default material; Box2D/Unity default combine = geometric mean). Fighters get `sqrt(0 x 0.4) = 0`.
2. **Wall cling (missing, required if D-C1 = A).** Friction also acts on the main-stage side wall. A minion below the
   stage top that walks into the side presses with normal impulse m x 3.5 per step, so friction can remove up to
   1.4 u/s of Vy per 50 Hz step, more than gravity adds (0.59). In the original it **sticks to the wall** (and can bonk
   an opponent standing near the ledge) instead of sliding down. Sim rule proposal: while in side contact with Vx
   into the wall, `Vy = towards0(Vy - MinionGravity, MinionWallFrictionSub)` with `MinionWallFrictionSub = 17500`
   (1.4 u/s x 50/60 per tick), no arc term, then Vx inward = 0. Add a test. Under D-C1 = B the minion slides down.
3. **Landing-step friction (note, not modelled).** On the first contact step Box2D's normal impulse equals the fall
   speed, so friction removes up to `0.4 x |Vy|` of Vx once per landing (about 50 milli of travel after the spawn
   drop). Record it as a known deviation in D-C1 A, or fold it into the landing branch; the editor check in amendment 4
   decides.
4. **Editor measurement stays, but is cheap and not blocking.** The 3.26 u/s figure is analytic and plausible to
   about 0.1%. Confirm once in the editor: log one minion's x on flat ground over 60 frames at 120 fps, plus one
   wall-cling case. If the measurement differs by more than 1%, re-derive before RulesVersion 13 ships.
5. **View events.** Add spawn and despawn burst events (CO:146, :156) and the strike burst (CO:214) to the view feed,
   keyed by `(slot, SpawnTick)` like projectiles (A6), so a rollback that reuses a slot never replays or loses a burst.
6. **Bolt visibility is one tick early.** The original sets `bolt.enabled = boltTimer > 0` before the strike resets the
   timer (CO:209-215), so the bolt shows from the tick after the strike. View rule: draw while `0 < BoltTicks < 9`
   (or decrement after the strike). View-only, no state change.
7. **Spawn must write every field** of the slot (including XRem/YRem/Facing), because inactive slots are not hashed and
   stale values would otherwise survive into a new life unnoticed by the checksum.
8. **Resolve the spawn-position TBD now:** the original reads `Center` in `Update` after this frame's FixedUpdate. Use
   the same read point as `Shot` and state it as the rule, not a TBD.
9. **Wording.** The cloud's "within 12 micro of the original" is relative to a 60 fps float run; the original itself is
   frame-rate dependent (120 fps gives about 2.5% different follow after 1 s). Say so, so testers do not chase it.

### Missing tests to add

- Wall cling (amendment 2), and a minion walking off the ledge then the opponent returning onstage.
- Two minions bonking on the same tick: hit and RNG order follows slot order (D-C3).
- Owner rings out mid-life: minions and cloud keep acting, attacker is still the owner.
- Rollback across a slot reuse: despawn, respawn into the same slot, restore earlier; checksums and view events match.
- `Init` / rematch clears all slots and `DroppedSummons`.
- Bolt view timing (amendment 6) as a view test if a view harness exists.

### Decisions

- **D-C1: A**, with amendments 1-4. The friction is real, derivable without the editor, and visible (7% slower walk,
  wall cling).
- **D-C2: A.** Clear, testable, deviation limited to 0.22 u at ledges.
- **D-C3: A.** Matches the existing objects-after-fighters order; order only matters for RNG and same-tick KO.
- **D-C4: A.** Deterministic, so no online risk; the reflect also launches the Mage away from the target. Flag it to
  game-designer as a possible future balance change, not a port change.
- **D-C5: A.** Integer range is safe (positions in micro stay far below 2^31).
