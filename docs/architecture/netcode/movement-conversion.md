# Movement Conversion: Fighter.cs (Rigidbody2D) to ChibiSimulation (integer, 60 Hz)

> **Status**: Proposed reference spec (systems-designer, 2026-11-06). No code changed.
> **Scope**: Section "## 1. Movement" of `docs/architecture/netcode/feature-inventory.md`.
> **Rule being served**: the deterministic core must play exactly like the original.

## 0. Ground truth about the original (verified)

| Fact | Value | Source |
|---|---|---|
| Physics2D gravity | (0, -9.81) u/s² | `ProjectSettings/Physics2DSettings.asset:7` |
| Fighter gravityScale | 3.6 → effective g = **35.316 u/s²** | `Fighter.cs:93` |
| Physics step | **Fixed Timestep 0.02 s = 50 Hz** (not 60) | `ProjectSettings/TimeManager.asset:6` |
| Simulation mode | 0 = FixedUpdate | `Physics2DSettings.asset:41` |
| Interpolation | Interpolate (visual only) | `Fighter.cs:96` |
| Friction | PhysicsMaterial2D friction 0, bounciness 0 | `Fighter.cs:100` |
| Linear damping / drag | **never set**; Rigidbody2D added by `AddComponent` (`MatchController.cs:87`) so it is the Unity default 0 | grep: no `linearDamping`/`drag` in Scripts |
| Velocity cap | none in code. Only Box2D `m_MaxTranslationSpeed: 100` (`Physics2DSettings.asset:14`), unreachable in play (needs about 2.8 s of free fall) | |
| Who writes velocity | `Update()` with variable `Time.deltaTime` (`Fighter.cs:169-190`); gravity and position integration happen in the 50 Hz physics step | |

Unit conversions used below (1 tick = 1/60 s, 1 u = 1000 milli):
- speed: `u/s × 1000/60 = u/s × 50/3` milli/tick
- acceleration: `u/s² × 1000/3600 = u/s² × 5/18` milli/tick²

## 1. Conversion table

Error % = (current sim − exact) / exact.

| # | Quantity | Original (unit, file:line) | Exact sim value: fraction = decimal | Current sim constant | Error % | Notes |
|---|---|---|---|---|---|---|
| 1 | Gravity | 9.81 × 3.6 = 35.316 u/s² (`Physics2DSettings.asset:7`, `Fighter.cs:93`) | 981/100 = **9.81** milli/tick² | `Gravity = 10` | **+1.94%** | The 3.6 cancels the 1000/3600, so the exact value is the raw 9.81 |
| 2 | Run speed, Melee (Kira) | 7.6 u/s (`ClassData.cs:52`) | 380/3 = 126.667 | `BaseMoveSpeed = 127` (`ChibiCombatStats.cs:95`) | +0.26% | |
| 3 | Run speed, Heavy (Brutus) | 6.3 u/s (`ClassData.cs:64`) | 105 = 105.000 | 105 (`CS:100`) | 0% | |
| 4 | Run speed, Ranged (Pip) | 7.3 u/s (`ClassData.cs:76`) | 365/3 = 121.667 | 122 (`CS:106`) | +0.27% | |
| 5 | Run speed, Magic (Luma) | 6.9 u/s (`ClassData.cs:88`) | 115 = 115.000 | 115 (`CS:111`) | 0% | |
| 6 | Run speed, Polearm (Ren) | 7.0 u/s (`ClassData.cs:100`) | 350/3 = 116.667 | 117 (`CS:116`) | +0.29% | |
| 7 | Heavy Juggernaut ×0.85 | 6.3×0.85 = 5.355 u/s (`Fighter.cs:140`) | 357/4 = 89.25 | 105×850/1000 = 89 | −0.28% | Truncating division |
| 8 | Hex slow ×0.85 (any class) | (`Fighter.cs:141`) | Melee 323/3 = 107.667; Heavy 89.25; Ranged 103.417; Magic 97.75; Polearm 99.167 | 107 / 89 / 103 / 97 / 99 | −0.2% to −0.8% | Truncation always rounds down |
| 9 | Juggernaut + Hex | ×0.85×0.85 = ×**0.7225** | 105×0.7225 = 75.86 | permille 1000−150−150 = 700 → 73 | **−3.8%** | Original stacks multiplicatively; sim adds permille (`CS:128`) |
| 10 | Tailwind ×1.1 (Ranged passive 1 only) | (`Fighter.cs:142`, `Fighter.cs:232`) | 7.3×1.1 = 8.03 u/s → 803/6 = 133.833 | 122×1100/1000 = 134 | +0.12% | |
| 11 | Tailwind + Hex (Ranged) | ×0.85×1.1 = ×**0.935** | 113.758 | permille 950 → 115 | **+1.1%** | Additive vs multiplicative again |
| 12 | Ground accel (free) | 70 u/s² (`Fighter.cs:283`) | 175/9 = 19.444 | `GroundAccel = 20` | **+2.86%** | |
| 13 | Air accel (free) | 40 u/s² (`Fighter.cs:283`) | 100/9 = 11.111 | `AirAccel = 11` | −1.0% | |
| 14 | Attack decel (actionLock, grounded) | 25 u/s² (`Fighter.cs:286-289`) | 125/18 = 6.944 | `MoveFriction = 25` (`SIM:518`) | **+260%** | The sim used the u/s² number as milli/tick² without converting. Attacks stop dead about 3.6× faster than the original |
| 15 | Block / shield friction (grounded) | 60 u/s² (`Fighter.cs:276`) | 50/3 = 16.667 | `ShieldFriction = 40` (`SIM:432`) | **+140%** | Also unconverted |
| 16 | Ground jump | 13 u/s (`Fighter.cs:296`) | 650/3 = 216.667 | `JumpVel = 217` | +0.15% | |
| 17 | Air jump | 11.5 u/s (`Fighter.cs:302`) | 575/3 = 191.667 | `AirJumpVel = 190` | −0.87% | |
| 18 | Fast-fall accel (on top of gravity) | 45 u/s² (`Fighter.cs:307`) | 25/2 = 12.5 | `FastFall = 8` | **−36%** | |
| 19 | Fast-fall cap | −24 u/s (`Fighter.cs:307`) | −400 = −400.000 | none; global `MaxFall = 330` (−19.8 u/s) | **−17.5%** | The original clamp is `Max(v−45dt, −24)`, so holding Down while already falling faster than 24 u/s *slows* you to 24 |
| 20 | Fast-fall trigger | `v.y < 3` u/s (`Fighter.cs:307`) | 50 = 50.000 (strict <) | `Vy <= 0` (`SIM:442`) | about 0.28 s late (trigger moves from vy 3 u/s to 0) | Changes the feel at the apex |
| 21 | Normal max fall | **none** (no damping, no clamp) | none | `MaxFall = 330` (`SIM:555`) | n/a | Original free fall after 1 s is 35.3 u/s (588.6 milli/tick). The sim caps it at 19.8 u/s |
| 22 | Ground-check vy threshold | not grounded while `vy > 0.5` u/s (`Fighter.cs:253`) | 25/3 = 8.333 | none: grounded only on landing snap with `Vy <= 0` (`SIM:561`) | n/a | Original probe box reaches 0.1 u below the feet (`Fighter.cs:244`), so a fighter counts as grounded up to 100 milli above the floor while vy ≤ 8.333 |
| 23 | Stun friction | **none**: no x-velocity change while `hitstun > 0` (`Fighter.cs:179-189`) | 0 | `StunFriction = 8` applied *during* stun (`SIM:410`) | n/a (wrong rule) | |
| 24 | Disabled friction | 30 u/s² toward 0, only when grounded, `hitstun <= 0`, not held, and cannot act (input disabled: intro, pause, round end, or dead) (`Fighter.cs:186-189`) | 25/3 = 8.333 | not modelled (8 is used for stun instead) | −4% (value) | |
| 25 | Knockback decay | none: zero friction material, damping 0. Only gravity acts during stun | 0 | StunFriction 8 on ground | n/a | |
| 26 | Dash (not yet in sim) | 17 u/s for 0.13 s, vy forced to 0 (`Fighter.cs:268-272`) | 850/3 = 283.333; 0.13 s = 7.8 ticks | absent | n/a | Listed for completeness |

## 2. Integration order

The original runs Box2D semi-implicit Euler at **50 Hz** (`v += g·dt; x += v·dt`). `Update` then overwrites the horizontal velocity, applies jump and fast-fall, and the next 50 Hz step integrates the result. A 50 Hz semi-implicit arc is exactly a continuous parabola whose launch speed is reduced by `g·dt50/2`. Plain 60 Hz semi-implicit Euler reduces it by `g·dt60/2` instead, which leaves jump arcs 0.9% higher than the original.

**Each sim tick, in this order:**

1. Run input-driven velocity writes: MoveTowards accel/decel/friction on Vx, then jump (sets Vy), then fast-fall (`Vy = max(Vy − 12.5, −400)` when Down, airborne and `Vy < 50`).
2. Apply gravity: `Vy −= 9.81` with **no global clamp**.
3. Integrate position: `X += Vx`, `Y += Vy − C50` while airborne, where `C50 = 0.1·g = 0.981` milli/tick. This term makes 60 Hz samples land on the original 50 Hz Box2D trajectory (derivation: target `y(n) = n·v0 − g·n²/2 − 0.6·g·n`, semi-implicit gives `−0.5·g·n`, the difference is `0.1·g·n`).
4. Snap to ground and re-test grounded, using the probe semantics (≤ 100 milli above the floor and `Vy ≤ 8.333`) and resetting air jumps.

**Decision for the user (D1):** keep `C50` to match the 50 Hz original exactly (recommended, because the rule is "exactly like the original"), or drop it and accept arcs 0.9% higher (+21 milli on the ground-jump apex).

**Quantities that do not depend on frame rate** (safe to test): run top speed, time to top speed (linear ramp), fast-fall terminal speed, free-fall speed after N seconds (`g·t`), the "no deceleration while stunned" invariant, and apex height and time against the 50 Hz reference.

## 3. Precision scheme (recommendation: 1/900-milli velocity sub-units)

Every base movement constant has a denominator of 100, 9, 18, 3 or 2 milli. Their LCM is **900**. If velocity is stored in units of 1/900 milli per tick, **every base constant becomes an exact integer** with no carry error.

| Constant | Sub-units (1/900 milli per tick or per tick²) |
|---|---|
| Gravity 9.81 | **8829** |
| Ground accel 175/9 | 17500 |
| Air accel 100/9 | 10000 |
| Attack decel 125/18 | 6250 |
| Shield friction 50/3 | 15000 |
| Disabled friction 25/3 | 7500 |
| Fast-fall accel 12.5 | 11250 |
| Fast-fall cap 400 | 360000 |
| Fast-fall trigger 50 | 45000 |
| Ground-check vy 25/3 | 7500 |
| Jump 650/3 / Air jump 575/3 | 195000 / 172500 |
| Run: Melee / Heavy / Ranged / Magic / Polearm | 114000 / 94500 / 109500 / 103500 / 105000 |
| Dash 850/3 | 255000 |
| C50 0.981 | 882.9 → 883 (the only rounded constant) |

**State changes (rollback state, included in hash and copy):**
- `ChibiFighterSim.Vx`, `Vy` become int **sub-units per tick** (rename to `VxSub` / `VySub`). The worst value is about 1500 milli knockback × 900 = 1.35e6, which fits int32.
- Add `int XRem, YRem` in [0, 899] holding the sub-milli position remainder.
- `X`, `Y` stay in milli, so hitboxes, projectiles and stage bounds do not change.
- Integrate: `t = XRem + VxSub; X += FloorDiv(t, 900); XRem = FloorMod(t, 900);` and the same for Y, using floor division (not C# `/`) so negative velocities round consistently.
- Move-table knockback and SelfVx/SelfVy in milli/tick are multiplied by 900 when applied.
- Speed modifiers: compute **multiplicatively**, `speedSub = base × Π(mod_permille) / 1000^k`, using long arithmetic and rounding half away from zero.

**Expected max error (vs exact real arithmetic of the same integrator):**
- Gravity, jumps, run, fast-fall, frictions: **0 after 1 s and after 3 s** (exact integers).
- Modifier speeds (rounding ≤ 0.5 sub/tick): ≤ 30 sub = 0.033 milli after 1 s; ≤ 0.1 milli after 3 s.
- C50 rounding (0.1 sub/tick): 6 sub (≈ 0) after 1 s; 18 sub after 3 s.
- For comparison, the current `Gravity = 10` gives 11.4 milli/tick velocity error and **348 milli (0.35 u)** position error after 1 s of free fall, and **3.1 u** after 3 s (ignoring the MaxFall clamp).

Alternative (not recommended): per-axis remainder accumulators for each fractional acceleration. That approach needs more state fields and more code paths for the same result.

## 4. Reference test values (for QA)

Notation: n = number of `Physics()` calls counting the jump tick as n = 1. The reference is the original 50 Hz trajectory: `v0' = v0 − g·0.01 s`, `apex = v0'²/(2g)`, `t_apex = v0'/g`. Tolerances: ±3 milli for heights (integer-tick sampling of the peak loses at most `g/8 ≈ 1.2` milli, plus 1 milli rounding), ±1 tick for timings, ±1 milli/tick for speeds.

| Test | Formula | Expected | Tolerance |
|---|---|---|---|
| Ground jump apex | v0' = 13 − 0.353 = 12.647 u/s; h = v0'²/(2·35.316) | **2264 milli** (sim samples 2263.3 at n = 21 and 22) | ±3 milli. Without C50: 2284.7, outside tolerance on purpose |
| Ground jump time to apex | v0'/g = 0.3581 s | **21.5 ticks → n = 21 or 22** | ±1 tick |
| Air jump apex (from vy = 0) | v0' = 11.5 − 0.353 = 11.147; h = v0'²/(2g) | **1759 milli** (n = 19) | ±3 milli |
| Air jump time to apex | 0.3156 s | **18.9 ticks → n = 19** | ±1 tick |
| Run top speed per class | moveSpeed × 50/3 | 126.667 / 105 / 121.667 / 115 / 116.667 milli/tick (sub: table in section 3) | exact in sub-units |
| Time to top speed, ground, from rest | speed / (175/9) | Melee 6.51 → 7 ticks; Heavy 5.4 → 6; Ranged 6.26 → 7; Magic 5.91 → 6; Polearm 6.0 → 6 | exact tick |
| Same in air | speed / (100/9) | Melee 11.4 → 12; Heavy 9.45 → 10; Ranged 10.95 → 11; Magic 10.35 → 11; Polearm 10.5 → 11 | exact tick |
| Modified speeds | base × Π mods | Juggernaut 89.25; Jugg + Hex 75.86; Ranged Tailwind 133.83; Ranged Tailwind + Hex 113.76 milli/tick | ±0.01 milli/tick |
| Fast-fall engage tick (Down held from the jump) | first tick with vy < 50: 216.667 − 9.81·k < 50 | **tick k = 17** after the jump tick (sub check: 195000 − 8829·17 = 44907 < 45000). The original measures 17-18 depending on Update phase | ±1 tick |
| Fast-fall terminal | clamp then gravity | Vy after Physics = −409.81 milli/tick; displacement per tick with C50 = **−410.8** (the original 50 Hz step integrates at −24.706 u/s = −411.8) | ±2 milli/tick |
| Fast-fall reaches cap | (49.9 + 400) / 22.31 | 20.2 ticks after engage → first clamped tick = k ≈ 38 | ±1 tick |
| Free-fall speed after 1 s from rest | g·t | **−588.6 milli/tick** (35.316 u/s), no cap | ±1 milli/tick |
| Free-fall distance after 1 s | 50 Hz Box2D: 0.02·0.70632·1275 = 18.011 u | **18011 milli** (sim: 9.81·1830 + 0.981·60) | ±5 milli |
| Ground knockback slide while stunned | d = vx · t_stun (no friction) | vx = 100 milli/tick (6 u/s), stun 30 ticks → **3000 milli** | ±1 milli. The current sim gives 576 milli (StunFriction 8) |
| Slide after stun ends, no input (canAct, MoveTowards target 0 at 70 u/s²) | discrete Σ, continuous v²/(2a) = 257 milli | from 100 milli/tick: **208.3 milli**, stops in 6 ticks | ±10 milli (the original varies 197-210 with Update rate) |
| Slide while disabled (input off, not stunned) | 30 u/s² = 25/3 | from 100: 12 ticks, Σ = **550 milli** (continuous 600) | ±10 milli |
| Attack decel slide (grounded) | 125/18 per tick | from 100 milli/tick: 15 ticks to stop, Σ = 1400 − 6.944·105 = **671 milli** | ±10 milli. The current sim gives 150 |

## 5. Behaviour differences to fix

1. **Stun friction inverted.** The original applies no x-decel during hitstun and applies 30 u/s² only when grounded, not stunned, not held and unable to act (`Fighter.cs:179-189`). The sim applies 8 *during* stun (`ChibiSimulation.cs:410`). Fix: no decel in stun, and add a "disabled" branch (round intro/end, dead) at 7500 sub.
2. **Unconverted frictions.** MoveFriction 25 → 6250 sub (6.944 milli, `Fighter.cs:288`). ShieldFriction 40 → 15000 sub (16.667 milli, `Fighter.cs:276`).
3. **Global MaxFall 330 has no original equivalent.** Remove it (`ChibiSimulation.cs:555`). The only cap is fast-fall −24 u/s (`Fighter.cs:307`).
4. **Fast-fall.** Trigger `vy < 3 u/s` (not `<= 0`), accel 12.5 milli/tick² (not 8), cap −400 via `max()` so it also pulls faster falls back up to −400 (`Fighter.cs:307`, sim `ChibiSimulation.cs:442`).
5. **Blocking in the air.** The original allows blocking airborne: `wantBlock` has no Grounded test (`Fighter.cs:261-262`), and only the 60 u/s² slowdown is ground-gated (`Fighter.cs:276`). While blocking in the air, Vx is frozen (no air control) and jump and fast-fall are skipped (`return` at `Fighter.cs:277`). Gravity still acts. The sim requires `f.Grounded` to shield (`ChibiSimulation.cs:428`).
6. **Grounded semantics.** Use the 100-milli probe and `vy ≤ 8.333` rule (`Fighter.cs:244,253`), not "snap only on crossing Y = 0" (`ChibiSimulation.cs:561`). This affects when ground accel and air-jump reset apply near the floor.
7. **Modifier stacking.** Multiplicative in the original (`Fighter.cs:139-143`), additive permille in the sim (`ChibiCombatStats.cs:128`). This matters for Juggernaut + Hex (−3.8%) and Tailwind + Hex (+1.1%).
8. **Gravity 10 → 9.81 exact** (8829 sub), plus the C50 term for 50 Hz arc parity (decision D1).
9. **Jump while blocking or attacking.** In the original, jump and fast-fall run during actionLock (`Fighter.cs:291-307` sit outside the `actionLock <= 0` gate) and only blocking and dash return early. In the sim, jump and fast-fall live only in `FreeActions`, so they are unavailable during moves (`ChibiSimulation.cs:412-415`). Verify against the combat inventory before changing it.

## Unverified

- Unity's `m_MaxTranslationSpeed: 100` unit (per second or per step). Irrelevant at in-game speeds either way.
- The exact Update/FixedUpdate phase in the original (rendering fps). This is why the fast-fall engage tick and post-stun slide carry ±1 tick / ±10 milli tolerances.
- Physical floor contact details (contact offset 0.01, `Physics2DSettings.asset:21`, and the collider offset −0.05) are not modelled in the Y = 0 floor.
