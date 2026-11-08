# Projectile Conversion: CombatObjects.Projectile to ChibiSimulation (integer, 60 Hz) — port step 11

Status: Proposed (design for step 11, RulesVersion 11). Author: systems-designer, 2026-11-08.

Sources: `CO` = `Assets/ChibiClash/Scripts/Combat/CombatObjects.cs`, `F` = `Assets/ChibiClash/Scripts/Fighter/Fighter.cs`,
`CA` = `Assets/ChibiClash/Scripts/Core/CombatAuthority.cs`, `SIM` = `Assets/ChibiClash/Scripts/Netcode/ChibiSimulation.cs`,
`FI` = `docs/architecture/netcode/feature-inventory.md`.

Units (as in movement-conversion.md section 3 and normals-conversion.md section 2): positions milli (1 u = 1000),
fighter `Y` = feet, velocities in sub-units (1/900 milli per tick): `sub = u/s × 15000`. Times: `ticks = s × 60`
(Life, ReturnAfter); stun `ceil(s × 60)`; knockback `u/s × 15000`; mult permille. One "update" = one game step
(`GameTicks`) of `UpdateProjectiles`; hitstop/cinematic are already handled by step 10 (the update simply does not run
on a real tick without a game step).

---

## 0. Ground truth about the original (verified from source)

| Fact | Value | Source |
|---|---|---|
| Fighter collider | BoxCollider2D size (0.75, 1.35), offset (0, -0.05) | F:97-99 |
| `Center` | `col.bounds.center` = feet + 0.675 | F:32 |
| Sim fighter AABB | x in [X-375, X+375], y in [Y, Y+1350] (`HurtHalfW 375`, `HurtH 1350`, feet = `bounds.min.y`) | SIM:52, SIM:1089-1093 |
| Sim `Center` | (X, Y+675) | derived |
| `Shoot` position | `Center + (offset.x·Facing, offset.y)` → sim (X + F·offX·1000, Y + 675 + offY·1000) | F:530-534 |
| `Projectile.Spawn` defaults | Radius from arg, Life from arg, Homing/Pierce/Harmless false, Destructible **true**, GrowRate 0, ReturnAfter -1, localScale (1,1,1) | CO:13-19, CO:27-42 |
| `HitSpec.projectile` | set `true` by every projectile but **never read** anywhere (grep) → no rule depends on it | CA:10-15 |
| First update | a projectile created in frame k first runs `Update` in frame k+1 (also the Thousand Arrows split arrows, created inside the lead's Update) | Unity lifecycle; SIM `SpawnFrame` rule (SIM:1119) |
| Patience bonus `b` | `TakePatienceBonus` is 1.5 only for Polearm P1 (F:443-451); so `b` = 1 for every projectile except the spear | F:445 |

---

## 1. Every projectile the original spawns

Spawn offsets are sim milli relative to the feet (X toward Facing). Speeds in sub-units/tick. `Life` in updates.
`r` = hit radius (milli). Kb in sub-units before the Archer/Mage x0.5 at hit time (SIM `HalfKnockback`).

| # | Projectile | Source | Spawn (dx, dy) | Vel (sub) | r | Visual size (u) | Life | Mult / kb / stun | Flags | Step |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | Arrow, light/finisher | F:477, F:536-541 | (500F, 725) | 18 u/s → 270000·F | 250 | 0.6 x 0.08 | 1.3 s → 78 | mult (1000 / 1500); kb (2.5,2)/(7,7)×0.6 → 22500/18000, 63000/63000; stun 17 / 27 | — | 11 |
| 2 | Arrow, heavy | F:477, F:538 | (500F, 725) | 21 u/s → 315000·F | 250 | 0.9 x 0.14 | 78 | 1800; (8,6)×0.6 → 72000/54000; 30 | — | 11 |
| 3 | Bolt, light/finisher | F:479-484 | (600F, 925) | 14 u/s → 210000·F | 0.35·0.8 → **280** | 0.35 | 1.2 s → 72 | as row 1 | — | 11 |
| 4 | Bolt, heavy | F:479-484 | (600F, 925) | 210000·F | 0.55·0.8 → **440** | 0.55 | 72 | as row 2 | — | 11 |
| 5 | Sword Aura wave | F:490-494 | (700F, 675) | 15 u/s → 225000·F | **500** | 0.25 x 1.2 | 0.9 s → 54 | 800; (4,2) → 60000/30000; ceil(15)=15 | — | 11 |
| 6 | Backstep Shot (big arrow) | F:854, F:538 | (500F, 725) | 21 u/s → 315000·F | 250 | 0.9 x 0.14 | 78 | 1500·b; (5,3) → 75000/45000; 0.4 → 24 | — | 11 (timing: 12) |
| 7 | Arrow Barrage arrow x5 | F:871, F:538 | (500F, 675 + yOff), yOff = Range(-0.25, 0.3) | 18 u/s → 270000·F | 250 | 0.6 x 0.08 | 78 | 600·b; (1.2,0.8) → 18000/12000; 0.18 → 11 | — | 11 (spread RNG + 0.09 s cadence: 12) |
| 8 | Homing Fireball | F:876-884 | (600F, 975) | 9 u/s → 135000·F | **380** | 0.55 | 3 s → **180** | 1600·b; (5,4) → 75000/60000; 0.4 → 24 | Homing | 11 |
| 9 | Spear (Throw & Recall) | F:935-950 | (600F, 775) | 16 u/s → 240000·F | **300** | 1.5 x 0.1 | 3 s → 180 | 1400·b; (5,3) → 75000/45000; 0.35 → 21; return mult 800·b | !Destructible, ReturnAfter 0.45 s → 27, OnGone → spearOut = false | 11 |
| 10 | Pose Fireball | F:388-389 | (600F, 775) | 13 u/s → 195000·F | 300 | 0.45 | 1.6 s → 96 | 800 (no b); (4,3) → 60000/45000; 0.3 → 18 | — | 15 (spawn only; machinery = row 1) |
| 11 | Smash wave x2 (dir -1 and +1, not Facing) | F:996-1004 | (600·dir, **550**) (y = bounds.min.y + 0.55) | 13 u/s → 195000·dir | 600, grows | 0.5 x 1.1 | 1.1 s → 66 | 3000 (no b); (6,12) → 90000/180000; 0.8 → 48 | Pierce, !Destructible, GrowRate 1.4 | 14 (machinery built + tested in 11) |
| 12 | Thousand Arrows lead | F:1015-1032 | (500F, 975) | (9F, 12) u/s → (135000F, 180000) | 0 | 1 x 0.18 | 0.45 s → 27 | 0 / 0 / 0 | Harmless, !Destructible, OnExpire → split | 14 (machinery 11) |
| 13 | Thousand Arrows split arrow x18 | F:1022-1031 | lead pos + insideUnitCircle·0.6 (absolute, not Shoot) | toward Opponent.Center + spread, speed Range(14, 19) u/s | 250 | 0.55 x 0.07 | 1.5 s → 90 | 450 (no b); (1.5,1) → 22500/15000; 0.15 → 9 | — | 14 (machinery 11) |

Not projectiles in the original: **ThunderCloud** (CO:175-220, its own object with a direct strike; step 13),
**Minions** (CO:119-171, step 13), **FireRing** (CO:224-255, already ported). None of them calls `Projectile.Spawn`
(grep: the only `Projectile.Spawn`/`Shoot` calls are rows 1-13).

### 1a. Sim today vs original (SIM:255-303, SIM:925-943, SIM:1099-1155)

| # | Sim today | Differs |
|---|---|---|
| 1-4 | Normal rows SIM:279-289: offsets, speeds, life, kb correct (step 8) | hit = AABB 150 x 100 instead of circle r 250/280/440 |
| 5 | SIM:941 aura constants correct | r 500 (AABB today) |
| 6 | SIM:281 `kx 80 ky 50` → 72000/45000; `ps 300` = 270000 (18 u/s); `pl 45`; offset 450/700 | KbX 75000, speed 315000, Life 78, offset (500, 725), r 250 |
| 7 | SIM:283 `kx 20 ky 15 stun 9` → 18000/13500/9; `pl 40`; yOff pattern ±220 around 700 | KbY 12000, stun 11, Life 78, base Y 675 (+ random yOff, step 12), r 250 |
| 8 | SIM:290 `kx 85 ky 65` → 76500/58500; `pl 150`; offset 450/700; Y-only steer (SIM:1123-1129) toward Y+650 | kb 75000/60000, Life 180, offset (600, 975), 2D constant-speed homing (section 3b) toward Y+675, r 380 |
| 9 | SIM:301 plain shot: `kx 85` (76500), `ps 270` (243000), `pl 45`, destructible, no return, no spearOut | KbX 75000, speed 240000, Life 180, offset (600, 775), r 300, !Destructible, return (3c), re-hit on return at 800·b, SpearOut |
| 10 | absent | step 15 |
| 11 | SIM:275 centered 5000-wide box | step 14 |
| 12-13 | SIM:284 horizontal stream, rehit 3 | step 14 |
| — | SIM:291 Magic skill 2 "3 seeking orbs" (sim-only stand-in for minions, `FHoming`) | not in the original; removed in step 13 (decision D-P3) |
| all | Destroy check from (X, Y+**650**) with `<=`, any projectile, before the hit check | (X, Y+**675**), strict `<`, only `Destructible && !Harmless && !HasHit` |
| all | `Dir = Sign(Vx)` → 0 when Vx = 0 | original `Vel.x >= 0 ? +1 : -1` (CO:101) |
| all | `Life--` after the move | same result (nothing in between reads Life except the return clamp); move it to the top to mirror CO:47 |

---

## 2. Per-update order (CO:44-111) converted

Runs inside `UpdateProjectiles` (after both fighters' update, melee hits and fire rings, as today SIM:649-663), slots
in index order 0..Max-1. `O` = owner fighter, `T` = `F[1 - Owner]`. `cT = (T.X, T.Y + 675)`, `cO = (O.X, O.Y + 675)`.

```
skip if !Active or SpawnFrame == GameTicks                       // first update on the next game step
1  Life -= 1                                                      // CO:47
2  if Homing: SteerHoming(ref Vx, ref Vy, Speed, cT - (X, Y))     // CO:51-55, uses pos BEFORE the move (CO:49)
3  if ReturnTicks > 0:                                            // CO:57-66 (ReturnAfter > 0)
       ReturnTicks -= 1
       if ReturnTicks == 0 and !Returning: Returning = true; HasHit = false; Mult = ReturnMult
4  if Returning:                                                  // CO:67-72
       (dx, dy) = cO - (X, Y)
       (Vx, Vy) = ScaleTo(dx, dy, ReturnSpeed = 270000)           // 18 u/s
       if dx² + dy² < 500²: Despawn(p); continue                  // strict, BEFORE the move
       Life = max(Life, 30)                                       // 0.5 s
5  Integrate(X, XRem, Vx); Integrate(Y, YRem, Vy)                 // CO:74 (existing floor-division Integrate)
6  Radius += GrowPerTick                                          // CO:77, CO:97 (section 3c)
7  if !Harmless and !HasHit:                                      // CO:89 (target is never null in a match)
       if Destructible and DestroyedBy(T):  Despawn(p); continue  // CO:91-96, F:522-528
       if CircleHitsFighter(T, X, Y, Radius):                     // CO:97-98, F:514-519
           dir = Vx >= 0 ? +1 : -1                                // CO:101 (post-steer, post-return Vx)
           HasHit = true                                          // set even if ApplyHit misses (invuln/dead) — CO:103
           ApplyHit(s, Owner, 1-Owner, {Mult, |KbX|, KbY, Stun, Dir = dir})
           if !Pierce and ReturnTicks == 0 and !Returning: Despawn(p); continue   // CO:104
8  if Life <= 0 or |X| > 40000 or Y < -27500 or Y > 42500:       // CO:108 (SIM:48 consts)
       (lx, ly) = (X, Y)
       Despawn(p)                                                 // frees the slot first
       if Kind == VolleyLead: SplitVolley(s, Owner, lx, ly)       // CO:110 OnExpire
Despawn(p): Active = false; if Kind == Spear: O.SpearOut = false  // CO:115 OnGone on EVERY destroy path
```

`DestroyedBy(T)` = `(T spin guard active and d² < 1600²) or (T fire ring active and d² < 2100²)` with
`d = (X, Y) - cT` after the move, `long` math, **strict** `<` (F:525-526). "Active" = the existing `GuardTicks > 0` /
`RingTicks > 0` (their durations are not step 11).

Semantics that fall out of this order (all exactly as the original):
- **Pierce** means "does not vanish on hit", not "hits again": `HasHit` stays set, so a piercing wave hits each
  fighter at most once (CO:89, CO:103-104). Each Smash wave has its own `HasHit`.
- **Spear**: may hit once on the way out (not destroyed: `ReturnTicks > 0`), then `HasHit` is cleared when the return
  starts, so it may hit once more on the way back at `ReturnMult`, kb sign now toward the owner. Never destroyed by
  Spin Guard / Fire Ring. While returning, Life can never reach 0 (clamped to 30 each update), so it ends only by
  reaching the owner (or by the bounds, unreachable in practice).
- A projectile that touches an invulnerable or KO'd target is still consumed (`HasHit` + despawn), because
  `ReceiveHit` returning Miss does not matter (CO:102-104). The sim already behaves this way.
- `HasHit` also turns off the destroy check (CO:89), irrelevant today (no projectile is both Pierce and Destructible).
- A Harmless projectile (lead) is never hit-tested nor destroyed.
- `SpearOut` cleared during the game step is seen by the next step's `UpdateFighter`, as the original's end-of-frame
  `OnDestroy` is seen by the next frame's `DoNormal`.

---

## 3. Integer formulas

Helpers (pure static, unit-testable):
- `ISqrt(long n)` = floor(√n) for n ≥ 0, exact integer method (bitwise or Newton with final correction).
- `DivRound(long n, long d)` (d > 0) = round half away from zero: `n >= 0 ? (n + d/2) / d : -((-n + d/2) / d)`.
- `ScaleTo(dx, dy, S)`: `L = ISqrt(dx² + dy²)`; `L == 0 → (0, 0)` (Unity `normalized` of a zero vector is zero);
  else `(DivRound(dx·S, L), DivRound(dy·S, L))`.

### 3a. Circle vs fighter AABB (F:514-519)

```
cx = clamp(px, T.X - 375, T.X + 375);  cy = clamp(py, T.Y, T.Y + 1350)
hit = (long)(cx - px)² + (long)(cy - py)² <= (long)r²          // inclusive, as the original
```

| Symbol | Type | Range | Description |
|---|---|---|---|
| px, py | int | ±42500 | projectile position, milli |
| T.X, T.Y | int | blast zone | target feet, milli |
| r | int | 0..1524 | current hit radius, milli |
| hit | bool | | true = overlap |

Output: boolean; all terms ≤ (85000)² ≈ 7.2e9, fits `long`. Worked: target at (3000, 0), arrow r 250 at
(2375, 725): cx = 2625, dx = 250 → 62500 ≤ 62500 → hit; at x 2374 → 63001 → miss.

### 3b. Homing (CO:51-55)

Original: `speed = |V|; V = Lerp(V, dir·speed, 3.5·dt).normalized · speed`, `3.5·dt = 3.5/60 = 7/120`.
Sim stores `Speed` (sub) at spawn = |spawn velocity| (exact for horizontal shots; the original's `Vel.magnitude`
is constant up to float noise, so storing it is the exact copy and prevents integer drift).

```
(Dx, Dy) = ScaleTo(tx - px, ty - py, Speed)                 // desired velocity, |D| = Speed (or 0)
Bx = 113·Vx + 7·Dx ;  By = 113·Vy + 7·Dy                    // = 120 × Lerp(V, D, 7/120)
(Vx, Vy) = ScaleTo(Bx, By, Speed)                           // normalise × speed; the ×120 cancels
```

| Symbol | Type | Range | Description |
|---|---|---|---|
| Vx, Vy | int | ±Speed | velocity, sub/tick |
| Speed | int | 135000 (fireball); orb 99000 | stored magnitude |
| tx - px, ty - py | int | ±85000 | to target Center, milli, pos before the move |
| Bx, By | long | ±3.6e7 | blended vector |

Output range: |V| = Speed ± 1 sub per component (rounding). Overflow: `dx·S` ≤ 8.5e4 × 1.35e5 ≈ 1.2e10, `B²` ≤ 2.6e15,
`B·S` ≤ 4.9e12 — all `long`. Degenerate: target exactly at the projectile → D = 0 → V unchanged (same as Unity:
Lerp toward 0 then normalise gives the old direction). V = 0 is impossible (Speed > 0).

Reference values (exact for this formula):
- **H1** `SteerHoming(Vx 135000, Vy 0, S 135000, dx 0, dy +1000)`: D = (0, 135000), B = (15255000, 945000),
  ISqrt(B²) = 15284241 → **(134742, 8347)** (float: 3.5448° turn, (134741.8, 8346.8)).
- **H2** Fireball tick 1: Mage at feet (0, 0) facing +1, target feet (3000, 0) standing still. Spawn (600, 975),
  V = (135000, 0). dx = 2400, dy = -300, L = 2418, D = (133995, -16749), B = (16192965, -117243),
  ISqrt = 16193389 → **V = (134996, -977)**; after the move pos = **(749, 973)**, XRem 896, YRem 823.
- **H3** Speed invariant: for any update, `|Vx² + Vy² - Speed²| <= 2·Speed + 2`.

### 3c. Return (CO:57-72) and growth (CO:77, CO:97)

Return: `ReturnTicks = 27` (0.45 s), `ReturnSpeed = 270000` (18 u/s), arrive radius 500 (strict), Life floor 30.
ScaleTo as above with `cO - pos`.

Growth: the original radius is `Radius · max(1, scale.y)`, `scale.y = 1 + GrowRate · n/60` after n updates. For a
constant rate this is linear: `r(n) = Radius + Radius·GrowRate·n/60`. Smash wave: 600 · 1.4 / 60 = **14 milli per
update exactly**, so store `Radius` (current, milli) and `GrowPerTick = 14`; `r(n) = 600 + 14n`, `r(66) = 1524`.
(The view draws scale.y = Radius / 600.) If a future GrowRate does not give an integer, store scale in 1/60000 units
instead; not needed for any original projectile.

| Symbol | Type | Range | Description |
|---|---|---|---|
| n | int | 0..66 | updates since spawn (the wave grows before its hit check) |
| r(n) | int | 600..1524 | hit radius milli |

Reference values:
- **R1 Spear return**: Polearm feet (0, 0) facing +1, standing still; opponent at X = -5000 (never touched). Spawn
  (600, 775), V = 240000. After update 26: X = 600 + floor(240000·26/900) = **7533**, Y 775, XRem 300. Update 27:
  ReturnTicks 0 → returning; dx = -7533, dy = -100, L = 7533 → **V = (-270000, -3584)**; after the move X = 7233,
  Y = 771. Pre-move distance ≈ 7533.7 - 300·k after k return moves; first `< 500` at k = 24 → **despawn on update 51**
  (spawn game tick + 51), SpearOut false at the end of that step, a normal press is accepted on step + 52. Life at
  despawn = 180 - 51 = 129.
- **R2 Wave hit**: Heavy feet (0, 0); target feet (4000, 0) standing. +1 wave spawn (600, 550), 195000 sub
  (216.667 milli/update). X(n) = 600 + floor(216.667n). Box near edge 3625: hit when 3625 - X(n) ≤ 600 + 14n → first
  at **n = 11** (X 2983, r 754, gap 642). Without growth it would be n = 12.

---

## 4. Thousand Arrows split (F:1019-1032)

On the lead's expiry (update 27, step 8 of section 2), with the lead's post-move position (lx, ly): for k = 0..17,
spawn one arrow, drawing from `s.Rng` in this fixed order (C# left-to-right evaluation of F:1024-1027):

| Draw | Original | Sim (milli / sub) |
|---|---|---|
| 1 | `Random.Range(-2.5, 2.5)` (inclusive) | `ox = Range(5001) - 2500` |
| 2 | `Random.Range(-0.5, 0.5)` | `oy = Range(1001) - 500` |
| 3+ | `Random.insideUnitCircle · 0.6` | rejection: `do { cx = Range(1201) - 600; cy = Range(1201) - 600 } while cx² + cy² > 360000`, at most 16 tries, then (0, 0) (decision D-P1) |
| last | `Random.Range(14, 19)` speed | `S = 210000 + Range(75001)` sub (14..19 u/s) |

```
target = (T.X + ox, T.Y + 675 + oy)                   // Opponent.Center at split time (never null)
from   = (lx + cx, ly + cy)
(Vx, Vy) = ScaleTo(target - from, S)                  // integer normalise; L == 0 → (0, 0) like Unity
spawn VolleyArrow at from: r 250, Life 90, Mult 450, KbX 22500, KbY 15000, Stun 9, Destructible, SpawnFrame = GameTicks
```

The lead's slot is freed before the 18 spawns (it may be reused by arrow 0). New arrows carry `SpawnFrame = GameTicks`
so they first move on the next step, even in slots after the lead's. Lead end point (reference **V1**): from feet
(X, Y) facing F, the lead moves (135000F, 180000)·27/900 = (4050F, 5400) exactly → split at **(X + 4550F, Y + 6375)**.
Draw count per volley: 4 + 2·(extra rejection tries) per arrow; 18 arrows ≈ 92 draws on average (π/4 acceptance).

---

## 5. Pool size

Worst case alive at once, per fighter (Life in updates; fastest normal = light, 0.24 s ≥ 14 updates apart; a light
x3 + heavy loop never reaches the finisher, so Archer/Mage can avoid the reload — F:459-460, F:474):

| Class | Contributors | Max alive |
|---|---|---|
| Ranged | 18 split arrows (live ticks 28..117 after the ult) + Arrow Barrage 5 fired right after the ult's 0.8 s lock + 1-2 normal/backstep arrows fired just before the ult (life 78) + Pose Fireball 1 | **≈ 26** |
| Magic | bolts 72 / 14 → 6, Fireball 1 (life 180, cd 4 s), Pose Fireball 1 (life 96, cd 4 s) (+3 orbs until step 13) | 8 (11) |
| Melee | aura waves 54 / 14 → 4, Pose Fireball 1 | 5 |
| Heavy | 2 waves, Pose Fireball 1 | 3 |
| Polearm | spear 1 (normals blocked while out), Pose Fireball 1 | 2 |

Worst match: Ranged mirror with both ults overlapping ≈ **52**. Today's 24 drops shots (one volley + six arrows from the
other archer = 24 already). **Recommendation: `MaxProjectiles = 64`** (12-field projectiles x 64 is a trivial copy and
hash cost). Keep the "pool exhausted = drop" fallback (deterministic) but count drops in a debug counter that tests
assert stays 0.

---

## 6. State and code contract (RulesVersion 11)

`ChibiProjectileSim` new fields (all copied in `CopyFrom`, all mixed in `ComputeChecksum` after `SpawnFrame`, in this
order):

| Field | Type | Meaning |
|---|---|---|
| `Kind` | int (`ChibiProjKind`) | replaces OnExpire / OnGone and tells the view the sprite |
| `Flags` | int bitmask | `Homing 1, Pierce 2, Harmless 4, Destructible 8, Returning 16, HasHit 32` (replaces `bool Homing`) |
| `Radius` | int milli | current hit radius (grows) |
| `GrowPerTick` | int milli | 0 or 14 |
| `Speed` | int sub | homing magnitude |
| `ReturnTicks` | int | > 0 counting down; 0 = no return pending |
| `ReturnMult` | int permille | mult after the return starts |

`ChibiFighterSim.SpearOut` (bool, copy + hash): set by the spear spawn, cleared by `Despawn` of a Spear. While set, a
light/heavy press starts nothing and changes nothing (F:455 returns before combo/lastAttackTime). Skills are not gated
(a second throw is possible only if the first spear is out for > 6 s; then the first one's OnGone clears SpearOut —
copied as is).

`enum ChibiProjKind { Arrow, ArrowBig, Bolt, BoltBig, AuraWave, Fireball, Spear, SmashWave, VolleyLead, VolleyArrow,
PoseFireball, SeekerOrb }` — behaviour hooks: `Spear` → OnGone; `VolleyLead` → OnExpire split. Everything else is
data (a per-kind spawn table: offset, vel, r, life, flags, grow, return; mult/kb/stun from the move or the table).

`SpawnProjectile` takes a spec (Kind, X, Y, Vx, Vy, Radius, Life, Mult, KbX, KbY, Stun, Flags, GrowPerTick,
ReturnTicks, ReturnMult) and sets `Speed = ISqrt(Vx² + Vy²)`, `XRem = YRem = 0`, `SpawnFrame = GameTicks`. Remove
`ProjHalfW / ProjHalfH`. `ChibiMove` normal rows need a radius per row (or the kind). Bump `RulesVersion` to 11.

---

## 7. Decisions

Everything in sections 2-3 copies the original exactly (up to the ±1 milli / ±1 sub rounding and the ±1 frame float
timing already accepted in earlier docs). Only these need a choice:

**D-P1 — the random numbers of Thousand Arrows (and later Arrow Barrage).** The original's random values cannot be
copied (Unity's random stream is shared with particle effects). We can copy the *spread pattern*.
- **A (recommended)**: same spread as the original — target box 5 x 1, start point anywhere inside a 0.6 circle, speed
  14-19 — using "pick a point in the square, retry if outside the circle" (max 16 tries).
- B: start point anywhere in a 1.2 x 1.2 square (no retry, always 5 draws). Slightly wider spread at the corners than
  the original.

**D-P2 — pool size.** A: **64 slots (recommended)**, never drops a shot in any match. B: 48, enough for every case
except a Ranged mirror with both ults at once.

**D-P3 — the Mage's "3 seeking orbs" (a sim-only stand-in for minions until step 13).** A (recommended): give them the
new real homing (they will curve in 2D instead of only up/down), Kind `SeekerOrb`, r 250, until step 13 replaces them
with minions. B: keep the old up/down steering for them only (extra code that is thrown away in step 13).

Not a decision (fixed to match the original): knockback direction when a shot flies straight up or down (`Vx = 0`)
is "+" like the original, not "none" as the sim does today.

---

## 8. Test cases (qa-tester; EditMode, pure sim)

| ID | Setup | Expected | Source |
|---|---|---|---|
| P1 | `CircleHitsFighter` target feet (3000,0), r 250, point (2375,725) / (2374,725) | true / false | F:514-519 |
| P2 | Heavy bolt (r 440), Mage feet (0,0) F+1, target feet (3000,0) still | hit on update **7** (X 2233); old AABB gave 9 | F:482-484 |
| P3 | Light arrow r 250, same setup (Archer) | hit on update 7 (X 2600) | F:538-539 |
| P4 | `SteerHoming` H1 | (134742, 8347) | CO:54 |
| P5 | Fireball H2, first update | V (134996, -977), pos (749, 973) | CO:51-55, F:880 |
| P6 | Fireball 180 updates, any target path | speed invariant H3 every update; despawn on update 180 if it never hits | CO:47, CO:108, F:881 |
| P7 | Spear R1 | X 7533 after 26; V (-270000, -3584) on 27; despawn on 51; SpearOut false after 51; light press on 51 ignored, on 52 accepted | CO:57-72, F:455, F:938-949 |
| P8 | Spear outbound hit: target at feet (2000, 0) | hits once at 1400·b, keeps flying (not despawned), no second outbound hit, second hit allowed after update 27 at 800·b with kb X sign toward the owner | CO:63-64, CO:101-104 |
| P9 | Spear vs Polearm Spin Guard / Fire Ring target | not destroyed | F:942, F:524 |
| P10 | Arrow vs target with GuardTicks > 0, centre distance exactly 1600 / 1599 | survives / destroyed (strict <, from Y+675) | F:525 |
| P11 | Fire Ring 2100 / 2099 | survives / destroyed | F:526 |
| P12 | Wave R2 | hit on update 11; still active after the hit; no second hit; Radius 1524 on update 66, gone after it | CO:77, CO:97, CO:104, F:1000-1003 |
| P13 | Two Smash waves, target standing on the Heavy (overlapping both) | hit by each wave once (2 hits total) | CO:89, F:997 |
| P14 | Lead V1 from feet (0,0) F+1 | split on update 27 at (4550, 6375); 18 VolleyArrows; none moves on that step | F:1015-1019, CO:110 |
| P15 | Split with a fixed seed | draw order ox, oy, (cx,cy)+, S per arrow; replay of the same seed gives identical arrows; every arrow `|V|` within ±2 of its S; S in [210000, 285000] | F:1024-1027 |
| P16 | Lead is Harmless: target standing on the lead's path | no hit, no damage, lead not destroyed by a Fire Ring | CO:89, F:1017-1018 |
| P17 | Arrow hits an invulnerable target (dash i-frames) | no damage, arrow despawned | CO:102-104 |
| P18 | Kb sign with Vx = 0 (`ScaleTo` vertical arrow) | Dir = +1 | CO:101 |
| P19 | Pool: Ranged mirror, both ult + barrage + normals | drop counter 0 with 64 slots | section 5 |
| P20 | Rollback: snapshot mid-return spear / mid-growth wave / pending lead, restore, step 60 | checksum equals the uninterrupted run; every new field differs → checksum differs | section 6 |
| P21 | Backstep / Barrage / Fireball / Spear spawn values | offsets, speeds, Life, kb, stun = table rows 6-9 | F:854, F:871, F:880, F:940 |

---

## Technical-director review

Reviewer: technical-director, 2026-11-08. Verdict: **APPROVE WITH AMENDMENTS**. The design is deterministic and faithful.
The amendments are small, but A1 and A2 must land with step 11.

### Checked and confirmed

- **Fidelity (verified against source).** Arrow (500, 725) at 18/21 u/s, r 250, life 78 (F:536-541). Bolt (600, 925),
  r = size × 0.8 = 280/440, life 72 (F:479-484). Aura (700, 675), r 500, stun 15 (F:490-494). Fireball (600, 975),
  9 u/s, r 380, life 180 (F:876-884). Spear (600, 775), 16 u/s, r 300, ReturnAfter 0.45, ReturnMult 0.8·b, !Destructible,
  OnGone (F:935-950). Smash x = Center ± 0.6, y = min.y + 0.55, r 600, GrowRate 1.4, Pierce, !Destructible (F:996-1004).
  Lead (500, 975), (9F, 12), life 27, Harmless (F:1015-1018). Return 18 u/s, strict `< 0.5` before the move, Life floor
  0.5 (CO:67-72). Destroy check: after the move, from Center, strict `<`, gated by `!hasHit` and Destructible
  (CO:89-96, F:522-528). Growth comes before the hit test (CO:77, CO:97). Kb sign is `Vel.x >= 0` (CO:101). The sim
  AABB (`HurtHalfW 375`, `HurtH 1350`, SIM:52, SIM:1089-1093) matches the BoxCollider2D (F:97-99).
- **Reference values.** I recomputed H1, H2, R1 (7533 / (-270000, -3584) / despawn on 51), R2 (n = 11; 12 without
  growth), P1, P2 (7), P3 (7) and V1 independently. All match.
- **Determinism.** Every formula is integer-only. `long` covers every product (the largest is about 2.6e15). `DivRound`
  rounds negatives symmetrically. The existing `Integrate` uses floor division. The slots run in index order, and spawns
  take the first free slot. That order is pool state, which is rolled back, so both peers agree. It differs from Unity's
  Update order only when two projectiles hit on the same step. That is acceptable. All RNG is drawn from `s.Rng` inside
  the sim.

### Required amendments

1. **A1 — SpearOut must not stick when the pool is full.** Set `SpearOut` only when `SpawnProjectile` succeeds (it
   returns the slot or -1). Otherwise a dropped spear leaves the Polearm unable to use normals for the rest of the match.
2. **A2 — Reset every field on spawn and on Init.** `SpawnProjectile` must write every new field (Kind, Flags, Radius,
   GrowPerTick, Speed, ReturnTicks, ReturnMult), because slots are reused. `InitFighter` must clear `SpearOut`.
3. **A3 — Make `ScaleTo` precise for short vectors.** `L = ISqrt(...)` floors, so `|result|` is off by up to S/L. I
   measured up to 809 sub (0.3 %) for split arrows. That breaks P15's ±2 assert and section 7's ±1-sub claim. The fix:
   when `max(|dx|, |dy|) < 2^20`, multiply both by 1024 before `ISqrt` and the divide. Overflow stays below 2^61. The
   error then drops below 1 sub. H1 and H2 final velocities are unchanged (I checked), but H2's intermediate D becomes
   (133958, -16745).
4. **A4 — Keep the dropped-shot counter in state.** `int DroppedShots` goes in `ChibiSimState`, copied in `CopyFrom` and
   mixed into `ComputeChecksum`. A static or view-side counter would count again on every rollback re-simulation.
5. **A5 — Correct the RNG draw count in section 4.** Each arrow draws `5 + 2·extra` values, not `4 + ...`. The average
   is about 5.55 per arrow, so about 100 per volley. The variable count is deterministic, but it shifts every later
   draw (damage rolls). Tests must not hard-code RNG state after a volley unless it comes from a recorded seed.
6. **A6 — Add the view and event consumers to the step 11 scope.** `ChibiSimView.cs:111` reads `ProjHalfW/ProjHalfH`, so
   removing them breaks the build. Draw from Kind and Radius instead. `ChibiSimEvents` detects slot reuse with an
   Owner/Life heuristic, and the view interpolates whenever Owner matches. Both should use `SpawnFrame` changing, which
   is exact. The lead-to-arrow-0 reuse within one step is the new case this matters for.
7. **A7 — State the ring-out rule and test it.** Ring-out does not touch projectiles or `SpearOut`: `Interrupt` and
   `RingOut` never reset `spearOut` (F:670-710). A returning spear retargets the owner's respawn point, and homing
   retargets the respawned opponent. Add a test (P22). A KO ends the match, so no projectile outlives it.
8. **A8 — Update the hashes.** Bump `RulesVersion` to 11 and add a remark line. Also mix `MaxProjectiles` into
   `ComputeHash`, like the time-scale constants, so peers with different pool sizes are refused.

### Rollback cost

Each slot holds about 23 ints. At 64 slots that is about 6 KB per snapshot, and the 128-entry ring is about 750 KB,
allocated once. Copying is about 64 field-wise copies per tick, and the hash covers active slots only (52 worst case).
Both are microseconds, even with 8-frame re-simulation. A fixed array is correct: no allocation, no GC, and a slot order
that is itself rolled-back state. Going from 48 to 64 slots costs about 1.5 KB per snapshot, which is negligible.

### Decisions

- **D-P1: agree with A.** Rejection sampling matches the original's disc. The 16-try cap almost never triggers (about
  2e-11 per arrow) and is deterministic. The variable draw count is fine (see A5).
- **D-P2: agree with A, 64 slots.** A worst case of 52 leaves 48 with too little headroom, and the extra cost is trivial.
- **D-P3: agree with A.** Reusing the real homing for the orbs adds no throwaway code, and RulesVersion 11 covers the
  behaviour change.

## Errata from the test pass (qa-tester, 2026-11-08)
1. §3c R1, update 27 return velocity: with amendment A3 (pre-scale ×1024) it is **(-269976, -3584)**, not (-270000, -3584). The old value came from ISqrt flooring to exactly |dx|; the pre-scaled value is the correct one (true 270000·7533/7533.66). Rest of R1 unchanged.
2. §3b H2 intermediate B with A3 is (16192706, -117215) from D = (133958, -16745). Final V unchanged.
3. §4 draw count is 5 + 2·extra per arrow (amendment A5), not 4 + 2·extra.
4. Test P22 (amendment A7, ring-out keeps projectiles and SpearOut; returning spear / homing retarget the respawn point) is part of the test list.
