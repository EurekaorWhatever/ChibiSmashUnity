# Multi-phase Moves Conversion: Fighter.cs skill coroutines to ChibiSimulation (integer, 60 Hz) — port step 12

Rule: play exactly like the original (README Q1). Original = `Fighter.cs` (F:), sim = `Netcode/ChibiSimulation.cs` (SIM:),
`Netcode/ChibiCombatStats.cs` (CS:). Units as in movement-conversion.md / normals-conversion.md: position milli (feet,
X = body centre), velocity sub = 1/900 milli/tick, **1 u/s = 15000 sub**, time = 60 Hz **game steps** (`GameTicks`; after
step 10 hitstop and the ult cinematic pause-and-step, so every coroutine `Time.deltaTime` / `WaitForSeconds` maps to game
steps). `Center` = feet + 675 milli (F:98-99). Values marked **TBD-verify** were not checked against source in this pass.

Scope: feature-inventory.md rows 72-82 (Thrust Barrage, Grab, Tornado Spin, Backstep Shot, Arrow Barrage, Leap Slam, Spin
Guard), Interrupt/held (rows 117, 119, 211, state list line 300/314), dispatch priority (F:318-333), skill attack speed.

---

## 0. Ground truth about the original (verified from source)

| # | Fact | Source |
|---|------|--------|
| 1 | Per frame: `TickTimers` (`actionLock -= dt`, `hitstun -= dt`, `SkillCd -= dt`) → `GroundCheck` → `UpdateUltWindow` → `canAct = InputEnabled && !IsDead && hitstun <= 0 && !held` → `HandleActions(ref v)` → **`if (!held) rb.linearVelocity = v`** | F:169-190 |
| 2 | `UseSkill` is called from inside `HandleActions`; `StartCoroutine` runs the coroutine body **inline** up to its first `yield`. So a coroutine's frame-0 code runs during Update and **any `rb.linearVelocity` it writes on frame 0 is overwritten by F:190** (same mechanism as Dash & Thrust, dash-conversion.md section 4). From frame 1 on, coroutine code runs after Update, so its velocity writes stick | F:184, F:190, F:718-751 |
| 3 | `yield return null` resumes next frame. A loop `for (t = 0; t < T; t += Time.deltaTime) { body; yield return null; }` runs `body` on frames 0..N-1 with **N = ceil(60·T)**, the code after the loop runs on frame N | Unity semantics |
| 4 | `WaitForSeconds(d)` yielded on frame j resumes on the first frame k with (k - j)/60 >= d → **k = j + ceil(60·d)** | normals-conversion.md section 1 |
| 5 | When 60·T or 60·d is an exact integer (0.3, 0.6, 0.8, 1.0, 1.1, 1.2 ...) the float sum of `deltaTime` decides ±1 frame in the original; this doc takes the exact-math value | normals-conversion.md section 11 |
| 6 | Unity frame order: physics step(s) → Update → coroutines. So the coroutine on frame k sees positions **before** the sim's Physics of tick k (the sim integrates the velocity set on tick k at the end of tick k, the original at the start of frame k+1) | Unity loop |
| 7 | **AttackSpeed is read only in `DoNormal` (F:456)**. No skill coroutine reads `AttackSpeed`; all skill waits and loop lengths are constants. Hex "sluggish" and Swift Blade / Crusher do **not** change any skill timing | F:123-133, F:456, F:718-933 |
| 8 | `UseSkill`: `SkillCd[i] = Def.skillCooldowns[i]`, `b = TakePatienceBonus()`, `lastAttackTime = Time.time`, then the switch | F:718-751 |
| 9 | `Interrupt()`: `StopAllCoroutines`, `actionLock = 0`, `dashTimer = 0`, `blocking = false`, `ReleaseHeld()` (releases the fighter **this one is holding**). It does **not** touch `parryUntil`, `spinGuardUntil`, `held` (its own held flag), cooldowns | F:670-683 |
| 10 | `SetHeld(on)`: `held = on`, body Kinematic/Dynamic, **velocity = 0** both ways | F:685-690 |
| 11 | While `held`: `canAct` false (no input at all), F:190 skips the velocity write, `ApplyDamage` skips knockback (`if (!held && kb² > 0.01)`), `Stun()` skips the velocity change. Timers, damage, hitstun still apply | F:179, F:190, F:619, F:667 |
| 12 | `RingOut`: Interrupt self; if the opponent is holding us, `Opponent.Interrupt()`; if we are held, `SetHeld(false)`; clear parry/spin guard; -15 %; respawn; invuln 1.5 | F:692-708 |
| 13 | `MeleeBox(reach, h)`: centre `Center + (Facing·(reach/2 + 0.15), 0)`, size `(reach + 0.3, h)`, **inclusive** AABB test, `kb.x *= Facing` | F:497-512 |
| 14 | `OverlapsCircle(p, r)`: closest point of the target AABB, `<= r²` (inclusive). Hit direction in Tornado / Spin Guard = `Mathf.Sign(o.Center.x - Center.x)`; **`Mathf.Sign(0) = +1`** | F:514-519, F:836, F:928 |
| 15 | Ult: `Controls.Ult` only **opens the gesture window** (F:266, inside `HandleActions` before the dash/block/actionLock checks, needs only `canAct`); firing happens in `UpdateUltWindow` on a later frame (F:176, before `HandleActions`, even while stunned/held). The ult is **not in the Light/Heavy/skill chain at all** | F:266, F:396-434 |

---

## 1. Timing contract for program moves ("frame-0 rule")

Let **frame f** = original frame number counted from the `UseSkill` frame (f = 0). Sim layout for the seven moves below
(today's row layout `Startup/Active/Recovery/Rehit` cannot express them and is replaced by a per-move program):

1. `StartMove` (tick 0) sets `MoveTick = 0`, then immediately runs the move program for **frame 0** (same tick), so after
   tick 0 `MoveTick = 1`; on tick k `AdvanceMove` runs **frame k = MoveTick** and increments it. `MeleeProbe` evaluates
   the frame just run (`MoveTick - 1`), exactly as today.
2. **Frame-0 velocity writes are skipped** (fact 2): tick 0 keeps the FreeActions velocity (run accel), as the original.
3. Hit checks, projectile spawns and position snapshots on frame f happen on **tick f**. Sim probes run after Physics
   (existing convention from step 8); the original checks before that frame's motion (fact 6). Same 1-tick offset as the
   normals; accepted.
4. **Lock**: new field `MoveLockEnd` = first tick on which a new action (normal/skill) is accepted = first frame where
   the original's `actionLock <= 0` after `TickTimers`. `MoveSlot = -1` at the end of tick `MoveLockEnd - 1`; FreeActions
   can start a move on tick `MoveLockEnd`. Every coroutine below ends on or before its lock end (checked per move), so
   no two coroutines ever overlap and one `MoveSlot` suffices.
5. Constant per-frame loops use `N = ceil(60·T)`; `WaitForSeconds(d)` chains use period `ceil(60·d)`.
6. Skills **never** use `ScaleFrames` or any attack-speed value (fact 7). Remove the `else` branch at SIM:949-954 for
   slots 2-4 (all classes, not only these seven moves). `AttackSpeedPermille` then has no callers (CS:183, keep or delete).

Lock tick formula, `lockTicks = ceil(60 · lockSeconds)`:

| Symbol | Type | Range | Description |
|---|---|---|---|
| lockSeconds | float (source constant) | 0.2 .. 1.2 | the original `actionLock` value that last governs the move |
| f0 | int | 0 .. 78 | frame on which that value was assigned |
| MoveLockEnd | int | 21 .. 90 | `f0 + ceil(60 · lockSeconds)` |

Output range: bounded; min 21 (Grab whiff, f0 0, 0.35 s), max 90 (Leap Slam timeout, f0 78, 0.2 s). No zero/negative.
Worked example: Arrow Barrage, f0 = 0, 0.6 s → 36: a Light on tick 35 is lost, on tick 36 it starts.

---

## 2. Per-move conversion

Conversions used: kb `u/s · 15000` sub, stun `ceil(s · 60)` ticks, mult permille (Patience `b` applied by StartMove as today).

### 2.1 Melee Thrust Barrage (F:775-786)

Original phase table:

| Frame | Event |
|---|---|
| 0 | `actionLock = 0.8`; hit k=0 (inline) |
| 7, 14, 21, 28 | hits k=1..4 (`WaitForSeconds(0.11)`: 6.6 → 7 frames each) |
| 35 | hit k=5 = last, kb (6, 4) |
| 42 | last wait ends, coroutine ends (no effect) |
| 48 | lock ends (0.8·60 = 48, exact-integer ±1). `max(actionLock, 0.15)` never binds (0.8 - 35/60 = 0.217) |

Every hit: `MeleeBox(1.5, 1.1)`, mult 550, kb (0.8, 0.5) = **12000 / 7500 sub** (last: **90000 / 60000**), stun 0.25 =
**15 ticks**, independent hits (each frame checks again, no "hit once"). Box (feet-relative, toward Facing): **BoxX 0, BoxW
1800, BoxY 125, BoxH 1100**. No velocity writes: grounded friction (MoveFriction 6250 sub/tick) while locked, as today.

Sim today (SIM:310): `Mv(4, 36, 10, 550, 200, 1500, 100, 1100, 20, 15, 10, rehit: 6)` → hits on ticks 5, 11, 17, 23, 29, 35,
box [200, 1700], kb 18000/13500 sub on all six, stun 10, move free on tick 51.

Differences: first hit 5 ticks late, period 6 not 7, no big last hit, kb/stun/box wrong, lock 50 vs 48.

Conversion: hit frames `{0, 7, 14, 21, 28, 35}`; last-hit kb on frame 35; `MoveLockEnd = 48`.

### 2.2 Heavy Grab (F:788-818) and the held state

Original phase table (frame 0 = press):

| Frame | Event |
|---|---|
| 0 | `actionLock = 0.35`. Check: opponent alive, `o.invuln <= 0`, `o.OverlapsBox(Center + (F·0.8, 0), (1.3, 1.4))`. **Fail → coroutine ends, lock ends frame 21** (0.35·60 = 21, ±1) |
| 0 (success) | `actionLock = 1.1`; `o.Stun(1.2)` (hitstun max, `o.Interrupt()`, velocity (0, min(0, vy))); `o.SetHeld(true)` (velocity 0, kinematic); `heldTarget = o`; `start = o.pos`; `top = self.pos + (F·0.2, 1.5)`; lift loop frame 0: `o.pos = start` |
| 1..17 | `o.pos = Lerp(start, top, f/18)` (0.3·60 = 18 iterations: frames 0..17) |
| 18 | lift loop exits; `slam = self.pos + (F·1.0, 0)`; slam loop j = 0: `o.pos = top` |
| 19..25 | `o.pos = Lerp(top, slam, j/7.2)` with j = f - 18 (0.12·60 = 7.2 → 8 iterations, j = 0..7). **The victim never reaches `slam`: last position is j = 7, fraction 35/36** |
| 26 | `ReleaseHeld()` (victim `held = false`, velocity 0, dynamic), then `o.ReceiveHit(2.2·b, kb (F·4, 8), stun 0.7, unblockable)` |
| 66 | holder's lock ends (1.1·60 = 66, ±1) |
| 72 | victim's hitstun ends (1.2·60 = 72, ±1); the slam's 0.7 s stun is absorbed: at frame 26 the remaining stun is 46 ticks > 42 |

Grab box (feet-relative toward Facing): **BoxX 150, BoxW 1300, BoxY -25, BoxH 1400**, inclusive test. Not a hit: it
ignores shield, perfect parry, Parry stance and Spin Guard (only `invuln` and death stop it). Slam hit: mult 2200, kb
**60000 / 120000 sub** (Heavy is not halved), stun **42**, unblockable (skips Parry stance reflect and perfect parry);
Ward and crit draws still happen (damage-conversion.md section 6). No hitstop at grab start (`Stun()` has none); hitstop
3/5 on the slam.

Positions are in feet coordinates (pivot offset is the same for both fighters, so `transform.position` deltas equal feet
deltas). Integer Lerp, `DivRound` = round half away from zero (SIM:1257):

```
lift  (f = 1..17):  P = S + DivRound((T - S) · f, 18)
slam  (f = 18..25): P = T + DivRound((L - T) · 5·(f - 18), 36)        // j / 7.2 = 5j / 36
T = holder(f=0) + (Facing·200, 1500)     L = holder(f=18) + (Facing·1000, 0)     S = victim(f=0)
```

| Symbol | Type | Range | Description |
|---|---|---|---|
| S | int2 milli | stage | victim feet at the grab check |
| T | int2 milli | stage | lift top, holder feet + (±200, 1500) at frame 0 |
| L | int2 milli | stage | slam point, holder feet + (±1000, 0) at frame 18 |
| f | int | 1..25 | grab frame |
| P | int2 milli | between S, T, L | victim feet written this tick |

Output range: P stays on the segment S→T (frames 1-17) or T→L (18-25), never beyond L (max fraction 35/36). Products fit
int32 for stage-sized deltas (|ΔX| < 60000 · 35 = 2.1·10^6). Worked example in section 6 (G2).

Snapshot ticks: S and T are taken on tick 0 in the grab phase (after Physics, rule 3), L on tick 18 from the holder's
position **before** its Physics of tick 18 (in AdvanceMove, fact 6). Because tick 0 is checked after Physics, S and T are
one tick of motion later than the original; accepted (same convention as all probes).

**Held state (new, both fighters):**

| Item | Holder (`Holding = 1`) | Victim (`Held = 1`) |
|---|---|---|
| Input | normal move rules: jump, fast-fall and dash still allowed (outside the lock gate, F:291-316); no facing change | **none** (`canAct` false): no shield, move, jump, dash, normal, skill |
| Velocity / gravity | normal (its own Physics) | `Vx = Vy = 0`, `XRem = YRem = 0`, **Physics skipped** (kinematic: no gravity, no stage collision, no landing) |
| Position | normal | written by the holder's program each tick f = 1..25; unchanged on tick 0 and on the release tick |
| Grounded | normal probe | `false` while held (TBD-verify: the original's `GroundCheck` still runs on the kinematic body and can read true on frames 0-1; no effect because it cannot act; only `airJumps = 1` could differ) |
| Timers | normal | all tick (Stun, cooldowns, Invuln, guard regen, Patience) |
| Hit by anything | normal | damage and stun apply, **no knockback**, `StunF` leaves velocity at 0 |
| Interrupt (stun > 0 hit, perfect-parry stun, reflect, ring-out, ult fire) | `Holding = 0`, victim `Held = 0`, victim V = 0; victim keeps its remaining Stun and falls from where it is | releases only a fighter **it** holds (none); `Held` stays 1 (fact 9) |
| Ring-out | Interrupt → releases victim | release: holder `Interrupt()` (holder free **at once**, lock 0), victim `Held = 0`, then normal respawn |
| Grab end (tick 26) | `Holding = 0`; lock continues to 66 | `Held = 0`, V = 0, then the slam hit sets V = kb |

Who can hit a held victim: only the holder's in-flight projectiles (Heavy has none outside the ult, step 14) — so in
practice the slam is the only hit. Who can hit the holder: the victim's in-flight projectiles and Fire Ring (and later
minions / cloud, step 13). Victim's ult while held fires in the original (F:417); not in the sim until step 15
(TBD in step 14/15: the ult coroutine of a held fighter must not move it while `Held`).

Tick placement (symmetry): the grab check, the victim position writes and the release + slam hit run in a new
**`GrabPhase(s)`** after `MeleeProbe`/`ApplyHit` and before `TickFireRings` (SIM:759-765). Reason: the victim must skip
Physics on every tick it starts held, independent of fighter index; doing release/hit inside the holder's
`UpdateFighter` would let a victim with index 1 get one tick of gravity before the kb (index-dependent result). A
grabber stunned by this tick's melee hit has `MoveSlot = -1` and its grab does nothing (order "victim first", one of the
two orders the original can run in). Simultaneous grabs: decision D-M3.

Sim today (SIM:317): `Mv(8, 3, 24, 2200, 150, 1000, 100, 1200, 40, 150, 40, flags: U)`: an unblockable melee box on ticks
9-11, kb 36000/135000 sub, stun 40, no hold, no lift/slam, no invuln check, free on tick 36.

### 2.3 Heavy Tornado Spin (F:820-842)

| Frame | Event |
|---|---|
| 0 | `actionLock = 1.05`; loop iteration t = 0: velocity write (overwritten by F:190, fact 2); `tick -= dt` → -1/60 → **hit**, `tick = 0.2` |
| 1..59 | `actionLock = max(actionLock, 0.1)`; **`Vx = MoveX · 3.5 u/s`, Vy unchanged** (after Update, sticks); hit when `0.2 - (f - h)/60 <= 0` → every 12 frames |
| hits | **0, 12, 24, 36, 48** (5 hits; 60 is outside the loop). 0.2·60 = 12 is exact-integer, ±1 |
| 60 | loop exits (1.0·60 = 60, ±1): no write; grounded friction resumes |
| 65 | lock ends: 1.05 - f/60 < 0.1 from frame 58, so frames 58 and 59 reset it to 0.1, then 6 frames → **65** (±1) |

Hit: circle r **1600** at `(X, Y + 675)` vs the victim AABB (inclusive, `CircleHitsFighter` SIM:1289), mult 700, kb
`(dir · 45000, 45000)` sub, stun **15**, `dir = o.X >= f.X ? +1 : -1` (Mathf.Sign(0) = +1). Facing never changes.
Steering: `Vx = MoveX · 52500` sub on ticks 1..59 (MoveX ∈ {-1, 0, 1} in the packet); no friction those ticks (the write
replaces it); Vy keeps gravity/jumps (a jump press during the spin works, F:291 runs before the coroutine).

Sim today (SIM:318): centered box 2400 x 1400, hits t = 6, 18, 30, 42, 54 → ticks 7, 19, 31, 43, 55, kb 45000/45000,
stun 15, no steering, free on tick 81. Differences: first hit 7 late, box not circle, no steering, lock 80 vs 65,
dir 0 → Facing (should be +1).

### 2.4 Ranged Backstep Shot (F:844-855)

| Frame | Event |
|---|---|
| 0 | `actionLock = 0.4`; loop t = 0: velocity (-F·16, 2) **overwritten** (fact 2) |
| 1..8 | `V = (-Facing · 240000, 30000)` sub, after Update (sticks; overrides a jump press, fact 1) |
| 9 | loop exits (0.15·60 = 9, ±1): `Vx = -Facing · 30000`, Vy kept; spawn the big arrow (row 6 of projectile-conversion.md: (500, 725), 21 u/s, life 78, kb 75000/45000 stored, stun 24, mult 1500) from the position before tick-9 Physics |
| 24 | lock ends (0.4·60 = 24, ±1) |

Vy = 2 u/s > 0.5 lifts the fighter off the ground check (F:253) from tick 2, so ticks 2-9 are airborne (arc term applies).
Vy is **not** forced to 0, so `level = false` (normal arc term C50, not the doubled one).

Sim today (SIM:329): `Mv(9, 1, 12, vx: -280)` → Vx -252000 (-16.8 u/s) on ticks 1..10, Vy untouched, shot on tick 10, no
end velocity, free on tick 23. Differences: speed -280 vs -266.67 milli/tick, missing Vy 2 u/s, one extra motion tick,
shot 1 tick late, no -2 u/s exit, lock 22 vs 24.

### 2.5 Ranged Arrow Barrage (F:865-874)

| Frame | Event |
|---|---|
| 0 | `actionLock = 0.6`; arrow 1 |
| 6, 12, 18, 24 | arrows 2-5 (`WaitForSeconds(0.09)`: 5.4 → 6) |
| 30 | last wait ends (no effect) |
| 36 | lock ends (0.6·60 = 36, ±1) |

Arrow: `SpawnArrow(0.6·b, kb (1.2, 0.8), 0.18, Random.Range(-0.25, 0.3), small)` → spawn `(X + F·500, Y + 675 + yOff)`,
18 u/s, life 78, kb **18000 / 12000** sub stored (no x0.6 for skills; halved at hit time → 9000 / 6000), stun **11**
(10.8 → 11), mult 600, Destructible.

RNG mapping: `Random.Range(float a, float b)` is uniform on [a, b] inclusive. Sim:
`yOff = -250 + s.Rng.Range(551)` → integer milli in **[-250, 300]**, 551 values, one `NextU32` per arrow (modulo bias
2^32 mod 551 / 2^32 ≈ 10^-7, negligible). Drawn on the spawn tick **before** `SpawnProjectile`, and drawn even if the pool is
full (the original calls Random.Range before SpawnArrow).

Sim today (SIM:333, yOff pattern SIM:1034): arrows on ticks 7, 12, 17, 22, 27 with `((ShotIdx·3) % 5 - 2)·110`, free on
tick 42. Differences: first shot 7 late, period 5 vs 6, deterministic pattern instead of random spread, lock 41 vs 36.

### 2.6 Polearm Leap Slam (F:901-917)

| Frame | Event |
|---|---|
| 0 | `actionLock = 1.0`; `rb.linearVelocity = (F·6, 14)` — **overwritten by F:190 (fact 2): the original never leaps**. Loop t = 0: no break |
| 1..77 | `actionLock = max(actionLock, 0.1)`; `if (t > 0.15 && Grounded) break` — t = f/60 > 0.15 → f >= 10 (f = 9 is exact 0.15, float ±1) |
| S | slam: S = first f in 10..77 with Grounded (the GroundCheck at the start of frame f = the sim's `Grounded` from the previous tick's probe), else **S = 78** (1.3·60 = 78, ±1). `MeleeBox(2.2, 1.6)`, then `actionLock = 0.2` (assignment: shortens a longer lock) |
| S + 12 | lock ends (0.2·60 = 12, ±1) |

Box: **BoxX 0, BoxW 2500, BoxY -125, BoxH 1600**; mult 1800, kb **90000 / 120000** sub (dir Facing), stun **30**.
Grounded press in the original: no leap, slam on frame **10**, free on **22**. Airborne press: keeps falling, slams on the
first grounded frame >= 10 (or 78 in the air).

This is a glitch (the move's name, animation and its "wait until landing" loop only make sense with a jump). Decision D-M2.
If D-M2 = B: write `V = (Facing · 90000, 210000)` sub on **tick 1** (where a coroutine write would first stick), set
`Grounded = false`; no further writes; airborne Vx is kept (no air friction while locked); the landing tick L is found by
the normal Physics. **Landing tick for a grounded start on the main stage: TBD-verify by running the sim** (estimate from
14 u/s and gravity 3.6·9.81: flight ≈ 0.79 s ≈ 48 ticks, slam ≈ tick 49, free ≈ 61).

Sim today (SIM:358): `Mv(22, 5, 14, 1800, 100, 1900, 0, 1200, 100, 150, 30, vx: 100, vy: 230)`: leaps with (6, 13.8) u/s on
tick 1, Vx 6 u/s for ticks 1..27, box on ticks 23-27 regardless of landing, kb 90000/135000, free on tick 42.

### 2.7 Polearm Spin Guard (F:919-933)

| Frame | Event |
|---|---|
| 0 | `spinGuardUntil = now + 1.2`, `actionLock = 1.2`; hit 1 |
| 18, 36, 54 | hits 2-4 (`WaitForSeconds(0.3)`: 18, exact-integer ±1) |
| 72 | last wait ends; lock ends (1.2·60 = 72, ±1); projectile-destroy aura ends (Time.time < until: frames 0..71) |

Hit: circle r **1500** at Center, mult 300, kb `(dir · 37500, 30000)` sub, stun **12**, dir as Tornado. Aura: `GuardTicks
= 72` **once on tick 0** (Dec at the top of the tick → active on ticks 0..71; destroy radius 1.6, SIM:1294).
**The aura survives an Interrupt** (fact 9) and is cleared only by ring-out.

Sim today (SIM:359): centered box 3000 x 1200, hits t = 2, 20, 38, 56 → ticks 3, 21, 39, 57, kb 36000/31500, stun 12,
`GuardTicks = 1` refreshed each active tick (ticks 3..72; dies 1 tick after an interrupt), free on tick 77.
Differences: hits 3 late, box not circle, kb, aura start/end and interrupt persistence, lock 76 vs 72.

---

## 3. Interrupt semantics per move (what cancels, what persists)

Cancelled by any stun > 0 hit, perfect-parry stun, Parry reflect, ring-out (and the ult fire, step 14/15). Not cancelled by
blocked chip, stun-0 hits, a dash or a jump. Always persists: the cooldown, the Patience reset, projectiles already spawned.

| Move | Cancelled | Persists |
|---|---|---|
| Thrust Barrage | remaining hits (incl. the big last hit) | earlier hits' kb/stun on the victim |
| Grab | lift/slam, slam hit; victim released at its current position with V = 0, keeps remaining Stun (≤ 72) | victim's Stun from the grab |
| Tornado | remaining hits, steering (velocity becomes the knockback) | — |
| Backstep | remaining motion and the arrow (if before tick 9), the exit velocity | — |
| Arrow Barrage | remaining arrows | spawned arrows |
| Leap Slam | the slam (no hit) | (B only) the leap velocity until replaced by kb |
| Spin Guard | remaining hits | **projectile-destroy aura until tick 71** |

Sim: `Interrupt(f)` (SIM:1530) additionally: `if (f.Holding) Release(s, f)`; `MoveLockEnd`/`MovePhase` become irrelevant
once `MoveSlot = -1`. `Interrupt` must not clear `GuardTicks`/`ParryTicks` (already so). `StunF` and `SetKnockback` skip
velocity when `Held`.

---

## 4. Dispatch priority (decision D-M1)

Original (F:318-333, fact 15), on a frame with `actionLock <= 0` and not blocking/dashing:
`Light && !reload → normal` (if `spearOut`, DoNormal returns at once and **nothing else fires this frame**) `else Heavy &&
!reload → normal` (same spear rule) `else` first pressed ready skill 0, 1, 2. Light beats Heavy, normals beat skills,
the reload falls through to skills. The **ult is outside this chain**: the button opens a gesture window at F:266 even
during an attack lock or dash, and the ult fires on a later frame from the gesture (step 15).

Sim today (SIM:898-912): Ult > Skill1 > Skill2 > Skill3 > (reload / spear return) > Light > Heavy.

| Option | What it means for players |
|---|---|
| **A: copy the original (Recommended)** | Pressing Light and a skill on the same 1/60 s gives the Light. With a spear in flight, a Light/Heavy press uses up that moment (no skill fires). During reload, skill presses work. Ult: kept first and outside the chain (its full behaviour — opening during locks — comes with the gesture window in step 15; until then the stand-in ult fires on the press when free, and other same-tick presses are lost). |
| B: keep the sim order | Same-tick Light + skill gives the skill; matches nothing in the original; tests differ from the original on same-tick presses. |

Recommendation A: it is the user's rule (exact copy) and D-N1 already said skill/ult order is aligned in step 12. Update the
timescale-conversion.md 8.2(a) wording to "Light > Heavy > skills (ult separate)" when A is chosen.

---

## 5. Decisions

**D-M1 — Button priority.** Section 4. Recommend **A** (copy the original).

**D-M2 — Leap Slam never leaps in the original.** The jump speed is written on the press frame and immediately replaced
by the normal movement speed (F:904 vs F:190), so in the original a grounded Polearm does a slam in place after 10 frames.
- A: copy it exactly — no leap; grounded slam at tick 10, free at tick 22.
- **B: make the leap work (Recommended)** — jump (6, 14) u/s on tick 1, slam on landing (tick ≥ 10, max 78), free 12
  ticks later.
Recommendation B in plain words: the move is clearly meant to jump (name, swing animation, "wait until you land" logic), and
you already chose not to copy a physics glitch in D-S1. Choose A if players of the original are used to the in-place slam.

**D-M3 — Two Heavies grab on the same tick.** The original lets whichever fighter Unity happened to update first win (not
defined by the code). Online, "player 1 always wins" would be unfair.
- A: player index 0 wins the tie.
- **B: both grabs miss (Recommended)** — each gets the normal whiff (21-tick lock). Fair, and it can only happen on the
  exact same 1/60 s.

**D-M4 (minor, no change recommended).** Exact-integer timings (lock 48, 66, 72, 36, 24, 21; Tornado 12-frame period and
60-frame loop; Spin Guard 18) are ±1 frame in the original from float time. Take the exact value (as steps 8-11 did).

---

## 6. State and code contract (RulesVersion 12)

**New `ChibiFighterSim` fields** (zero in InitFighter, copied in `CopyFrom`, appended to `Hash` after `SpearOut` in this order):

| Field | Type / range | Meaning |
|---|---|---|
| `MoveLockEnd` | int 0..90 | tick index (MoveTick) at which the move ends and actions are accepted (section 1) |
| `MovePhase` | int 0..1 | Leap: 0 waiting, 1 slammed. Grab: 0 none/whiff, 1 holding (`Holding` mirrors it) |
| `Held` | bool | this fighter is held (kinematic) |
| `Holding` | bool | this fighter holds the opponent |
| `GrabSX, GrabSY` | int milli | S |
| `GrabTX, GrabTY` | int milli | T |
| `GrabLX, GrabLY` | int milli | L (set on frame 18) |

Removed: nothing hashed (rows lose `Rehit`/`Startup` meaning for these seven moves; `ShotIdx` may stay as the arrow count).
No new RNG state (Arrow Barrage uses `s.Rng`).

**Code points:**
1. `RulesVersion = 12`, remark "multi-phase skills matched to original (program moves, held state, priority)".
2. Skill StartMove: no ScaleFrames; set `MoveLockEnd`; run frame 0 at once (section 1); per-move program switch in
   `AdvanceMove` keyed by (Class, slot) for the seven moves; `MeleeProbe` derives hit frames and shapes from the frame
   number (pure function of state; circle shapes via `CircleHitsFighter` from `(X, Y + 675)`), per-hit kb (Barrage last).
3. `MoveSlot = -1` when `MoveTick >= MoveLockEnd` (replaces S+A+R for these moves).
4. `UpdateFighter`: `if (f.Held) { Shielding = false; Vx = Vy = 0; skip actions and Physics; }` after the timers.
5. `GrabPhase` after melee ApplyHit (section 2.2), with D-M3; release helper used by grab end, Interrupt, ring-out.
6. `ResolveBoundsAndMatchEnd`: on ring-out of a held fighter → holder `Interrupt` + release; of a holder → release first.
7. `FreeActions` priority per D-M1.
8. Arrow Barrage RNG draw per section 2.5. Spin Guard `GuardTicks = 72` on tick 0.
9. Rows: kb/stun/box values in section 2 (all in sub units, no x VelSub on these).

**RNG draw order in one game step** (adds to damage-conversion.md section 6): UpdateFighter(0) [F0 barrage yOff] →
UpdateFighter(1) [F1 barrage yOff] → melee ApplyHit draws (h0 then h1) → GrabPhase slam hits (fighter 0's then 1's: Ward,
crit, Hex/Armor Break) → fire rings → projectiles.

---

## 7. Reference values (stage centre, grounded, attacker at X = 0 facing +1, at rest, no input after the press, press on tick 0, no hitstop unless stated; with hitstop the same values hold in game steps)

| # | Check | Expected |
|---|---|---|
| R1 | Thrust Barrage hit ticks (victim in box at X = 1000) | 0, 7, 14, 21, 28, 35; kb 12000/7500 on 0-28, 90000/60000 on 35; stun 15; Light accepted on tick 48, lost on 47 |
| R2 | Grab whiff (victim at X = 3000) | no hold; Light lost on 20, accepted on 21; cooldown spent |
| R3 | Grab lift, victim at (1000, 0) | f9 (600, 750); f17 (244, 1417); f18 (200, 1500) |
| R4 | Grab slam | f19 (311, 1292); f25 (978, 42); f26 released at (978, 42), V = (60000, 120000) |
| R5 | Grab stun | victim Stun 72 on tick 0; at tick 26 Stun = 46 stays 46; victim acts on tick 72; holder free on tick 66 |
| R6 | Tornado hits | ticks 0, 12, 24, 36, 48 (5 hits), victim inside r 1600; free on 65, lost on 64 |
| R7 | Tornado steering | MoveX 0 on tick 0, +1 from tick 1: X(after 59) - X(after 0) = 59 · 52500 / 900 = **3441.67 → 3441 or 3442 milli** (remainder); tick 60 Vx 46250; tick 64 Vx 21250 (grounded) |
| R8 | Backstep | Vx -240000 / Vy 30000 written on ticks 1-8; tick 9 Vx -30000, arrow spawned on tick 9 at (X9 - 500, Y9 + 725); free on 24 |
| R9 | Arrow Barrage | arrows on 0, 6, 12, 18, 24; yOff_n = -250 + (NextU32 of a cloned RNG % 551) in spawn order; free on 36 |
| R10 | Leap Slam A, grounded | slam probe tick 10; free tick 22 |
| R11 | Leap Slam, airborne landing at L | slam on tick max(L, 10) if L ≤ 77; never lands → 78; free slam + 12 |
| R12 | Leap Slam B, grounded | tick 1 V = (90000, 210000); slam on the landing tick (TBD-verify, ≈ 49) |
| R13 | Spin Guard | hits 0, 18, 36, 54; GuardTicks 72 on tick 0, a destructible arrow inside 1.6 is destroyed on tick 71, not on 72; free on 72 |

---

## 8. Test list for qa-tester (EditMode, pure sim)

| # | Setup | Expected | Source |
|---|---|---|---|
| 1 | Melee, Skill3 on tick 0, victim at X 1000 | R1 hit ticks and kb; 6 HP drops | F:775-786 |
| 2 | Same, attacker hit (stun > 0) on tick 20 | hits 0, 7, 14 only; no big last hit; MoveSlot -1 | F:670 |
| 3 | Melee Swift Blade + Hexed | same hit ticks as test 1 (no attack-speed effect) | F:456 vs F:775 |
| 4 | Heavy Grab, victim at 1000 | R3-R5 positions, tick by tick | F:788-818 |
| 5 | Grab, victim at X 1825 (touching edge) | grab connects (inclusive); at 1826 whiffs | F:507-511 |
| 6 | Grab, victim dashing (Invuln > 0) | whiff, lock 21 | F:793 |
| 7 | Grab, victim shielding / in Parry stance / Spin Guard | grab connects; shield dropped | F:793-798 |
| 8 | Grab; victim index 0 vs index 1 (swap slots) | identical victim trajectory and release position | GrabPhase symmetry |
| 9 | Held victim: Vx/Vy stay 0, no gravity, presses (Jump, Dash, Light, Shield) on ticks 1-25 do nothing | position = R3/R4 path only | F:179, F:190 |
| 10 | Holder hit by a victim's arrow (stun > 0) on tick 10 | victim Held 0 at its tick-10 position, V 0, falls, keeps Stun; holder MoveSlot -1 | F:670-683 |
| 11 | Held victim pushed past BlastX (grab near the edge) | victim ring-out; holder Interrupted, free next tick; victim not held | F:692-697 |
| 12 | Holder rings out while holding | victim released, V 0, Stun kept | F:695, F:681 |
| 13 | Slam vs Ward (Magic P1) | 1 Ward draw; on success no damage, victim still released | F:574-579 |
| 14 | D-M3: both Heavies grab on the same tick | per decision (B: both whiff, lock 21) | — |
| 15 | Tornado, victim inside r 1600 | R6 | F:820-842 |
| 16 | Tornado steering | R7; Facing unchanged with MoveX opposite | F:828 |
| 17 | Tornado dir when X equal | victim kb Vx = +45000 | F:836 |
| 18 | Tornado, Jump on tick 20 | Vy = jump velocity, steering continues | F:291, F:828 |
| 19 | Backstep | R8; Jump on tick 3 is overridden by Vy 30000 on tick 3 | F:844-855 |
| 20 | Arrow Barrage, fixed seed | R9; yOff in [-250, 300]; 5 draws total, in spawn order, before that tick's damage draws | F:865-874 |
| 21 | Arrow Barrage, pool full | draw still consumed; DroppedShots +1 | F:871 |
| 22 | Leap Slam grounded | R10 (A) or R12 (B) | F:901-917 |
| 23 | Leap Slam started in the air, landing on tick 30 | slam on tick 30, free 42 | F:909 |
| 24 | Leap Slam off-stage, never lands | slam on tick 78 in the air | F:906 |
| 25 | Spin Guard | R13 | F:919-933 |
| 26 | Spin Guard interrupted on tick 10 | no more hits; aura still destroys a projectile on tick 60 | F:670, F:525 |
| 27 | Priority (D-M1 = A): Light + Skill1 same free tick | Light starts | F:320-330 |
| 28 | SpearOut + Light + Skill2 same tick | nothing starts | F:455 |
| 29 | Reload + Light + Skill1 | Skill1 starts | F:320-326 |
| 30 | Locks: a press one tick before each MoveLockEnd is lost (D-N1), at MoveLockEnd it starts | 48 / 21 / 66 / 65 / 24 / 36 / 22 / 72 | sections 2.1-2.7 |
| 31 | Hash/copy: change each new field (MoveLockEnd, MovePhase, Held, Holding, Grab*) | hash changes; CopyFrom round-trip equal | section 6 |
| 32 | Rollback across a grab (save at tick 10, resim to 30) | identical state hash | section 6 |

---

## 9. Not verified / TBD-verify

- (Verified, no change) Skill cooldowns ClassData.cs:57-105 x60 equal the sim `cd:` values for all classes (Melee
  240/300/420, Heavy 360/420, Ranged 240/360/420, Magic 240/720/600, Polearm 300/360/360); set on the press tick, usable
  again on tick `cd` in both. Arrow spawn offset confirmed: `Shoot((0.5, yOff))` from Center (F:536-541).
- Leap Slam B landing tick (run the sim).
- Original `GroundCheck` on a kinematic held body (Grounded / airJumps while held).
- `ReceiveHit` invuln check placement for the slam hit (assumed like SIM step 0).
- Rigidbody2D interpolation (F:96): the original reads `transform.position` (interpolated) for S, T, L; the sim uses
  the simulated position. Not reproducible; accepted.
- Ult while held (step 14/15) and whether ult coroutines read AttackSpeed (assumed no).
- Observation outside scope: Dash & Thrust row (SIM:308) frees on tick 25; the original's 0.4 s lock frees on frame 24.

---

## Technical-director review (2026-11-08)

**Verdict: APPROVE WITH AMENDMENTS.** Spot-checked against F:169-333, F:497-519, F:544-708, F:718-933 and SIM:706-1592:
Thrust Barrage frames 0/7/../35, kb 12000/7500 and 90000/60000, stun 15, box 0..1800 / 125+1100; Grab box 150+1300 /
-25+1400, lift 18 and slam 8 iterations, 5j/36, R3/R4 numbers recomputed; stun 72, lock 66, whiff 21; Tornado hits
0/12/24/36/48, lock 65, steer 52500, kb 45000; Backstep -240000/30000 on 1-8, -30000 on 9; Arrow Barrage 0/6/12/18/24,
kb 18000/12000, stun 11, `Range(-0.25, 0.3)` before `SpawnArrow`; Leap Slam box 0..2500 / -125+1600, kb 90000/120000,
stun 30, break at f >= 10, timeout 78; Spin Guard 0/18/36/54, r 1500, kb 37500/30000, stun 12, aura frames 0..71; AttackSpeed
read only at F:456. All correct.

Amendments:

1. **Grab start must run before `MeleeProbe`, not after `ApplyHit`.** The original grab check runs inline in Update.
   Unity runs every Update before any coroutine resumes, so a hit that comes from a coroutine on that frame (normal
   strikes, `WaitForSeconds` skill hits, Tornado frames >= 1) can never beat a grab on the same frame. The victim's
   `Stun` -> `Interrupt` stops that coroutine. With the placement in section 2.2, the grab loses to every hit on the same
   tick, which never happens in the original. New order inside `GameStep`: UpdateFighter(0), UpdateFighter(1),
   **GrabPhase** (start check, D-M3, victim Stun/Interrupt/Held, position writes for f = 1..25, release on f = 26), then
   MeleeProbe(0/1), ApplyHit, rings, projectiles, bounds. The victim's MoveSlot is then -1 before its probe, so its hit
   on that tick is cancelled, as in the original. Only frame-0 inline skill hits (Barrage k=0, Tornado f0, Spin Guard
   hit 1) depend on update order in the original. "Grab first" is one valid order. The slam itself (f = 26) becomes the
   holder's `PendingHit` from `MeleeProbe`: it always targets the victim, needs no box test, and is unblockable. It is
   applied after the release, so the RNG order stays "melee h0 then h1". Update section 2.2 (tick placement), section 6
   item 5 and the RNG-order paragraph. Symmetry is kept, because both fighters' Physics have run before GrabPhase.
2. **Program velocity writes go after jump/fast-fall/dash start** in the locked branch. Today SIM:860-865 runs
   `AdvanceMove` before `JumpAndFastFall`, so a jump overrides the move's write. The original works the other way: the
   coroutine writes after Update. Without this change, test 19 (Backstep overrides a jump) fails. Split AdvanceMove
   into "timeline/probes" and "velocity write", and do the write last. Observation only: the existing Dash & Thrust
   `FLevel` write has the same order question. Check it, do not change it in this step.
3. **Leap Slam grounded test uses a snapshot of `Grounded` taken at the start of the tick.** `JumpAndFastFall` sets
   `Grounded = false`. In the original, `Grounded` keeps its GroundCheck value for the whole frame, so a jump on the
   landing frame still slams.
4. **One release path.** `Interrupt` takes `(s, i)` and releases `Holding`. Every cancel path goes through it: reflect
   (SIM:1448), stun > 0 (SIM:1502), `StunF`, the UpdateFighter stun branch (SIM:834, which today sets MoveSlot directly)
   and ring-out (SIM:1572). Release must be idempotent, because both fighters can ring out on the same tick. Name the
   held machinery after "holder/held", not "grab", so the Polearm ult kick (F:1036-1066, `heldTarget`) reuses it in step 14.
5. **Overlap rule.** `Overlaps` (SIM:1209) is strict, but the original `OverlapsBox` is inclusive, and test 5 needs
   inclusive. State which helper the grab uses (new inclusive test). State whether the Barrage and Leap boxes keep the
   step-8 strict convention.
6. **State.** `Holding` duplicates `MovePhase == 1` for the grab. Keep it only together with a per-tick invariant test,
   `F[i].Holding == F[1-i].Held`. Mix the new step-12 constants into the config hash next to `RulesVersion = 12`. v11
   replays then fail the hash check instead of desyncing.
7. **Attack speed.** The removal of ScaleFrames also covers slot 5 (ult), or the doc says it is left for step 14. Add one
   test for a skill outside these seven (e.g. Choke while Hexed), because the change is global.
8. **Tests to add:** (a) grab against a victim Light strike on the same tick: grab wins, and the strike does not land;
   (b) grab against a victim Tornado frame-0 hit on the same tick: grab wins; (c) Leap Slam with Jump on the landing
   tick: it still slams; (d) grab under forced hitstop and slow-mo: victim positions per game step equal R3/R4;
   (e) rollback where the remote grab press arrives late (mispredict across tick 0 and across tick 26): the re-simulated
   hash equals a straight run; (f) holder and victim ring out on the same tick; (g) slam KO: match ends, `Held` cleared;
   (h) invariant from amendment 6 every tick of tests 4-14; (i) config hash v11 != v12.
9. **If D-M2 = B:** pin the landing tick as an exact number in R12 and test 22. To get it, run the sim with the existing
   Physics (gravity, the C50 arc term, `level = false`, the D-S1 resting gate). Do this before the story closes. "About
   49" cannot be asserted.

Ult in step 12 (D-M1 = A): keep today's stand-in unchanged. The ult is checked first, on the press edge, only when free,
and only if the meter is full. If the meter is not full, the press falls through to Light > Heavy > skills, so it never
eats a press. Same-tick Ult + Light (meter full) gives the ult and drops the Light. The original would do both. That is a
known temporary difference until step 15. No ult buffering (D-N1).

Decisions: D-M1 A, copy the original order. D-M2 B, if the user accepts that the Polearm gains a 14 u/s air boost every
300 ticks (a recovery tool the original never had); otherwise A. Both are deterministic. B only adds a slam tick that
depends on landing, and a mispredicted Down or Jump during the leap corrects that tick through normal rollback.
D-M3 B, both miss. Player 0 is the host side, so A would give one peer a permanent edge.

## Errata from the test pass (qa-tester, 2026-11-08; checked by main session)
1. R8: the Backstep arrow spawns at (X9 + Facing·500, Y9 + 725), not X9 - 500 — SpawnArrow shoots forward via Shoot(offset.x·Facing) (F:854, F:530-534). Confirmed against the original.
2. Test 23 / R11 values ("landing on tick 30, slam 30, free 42") assume D-M2 = A. Under the chosen D-M2 = B the landing tick comes from a sim run (amendment 9), like R12; asserted structurally until pinned.
3. Test 30 lock list: Leap Slam's 22 is the option-A value; under B the lock ends at slam + 12 (tested in test 22).
4. Values computed by the tester that must be confirmed on the first green run: test 10 release point (556, 833), test 11 ring-out tick 24, test 24 off-stage flight (marked TBD-verify in the test).
5. Amendment 9 pinned by the gameplay-programmer's sim run (D-M2 = B): test 22 / R12 slam on tick 48 at X 4793 (first grounded at end of tick 47), first free tick 60; test 23 (start Y 3000) lands on a Sky Dojo platform, slam 48, free 60; test 24 (off-stage) slams on tick 78 in the air at (8700, -3112), free 90. Errata 4 values confirmed: test 10 release (556, 833), test 11 ring-out tick 24.
6. Section 6: grab MovePhase stays 1 after the frame-26 release until the move ends, so Holding equals MovePhase == 1 only on frames 0-25.
