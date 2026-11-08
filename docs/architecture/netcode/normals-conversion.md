# Normals Conversion: Fighter.cs light/heavy to ChibiSimulation (integer, 60 Hz)

Step 8 of the port order (feature-inventory.md, "Suggested port order"). Rule: play exactly like the original.
Reference frame rate: **60 Hz display (decision D2 = A, dash-conversion.md)**. Original = `Fighter.cs` (F:),
`CombatObjects.cs` (CO:), `ClassData.cs` (CD:), `CombatAuthority.cs` (CA:). Sim = `Netcode/ChibiSimulation.cs` (SIM:),
`Netcode/ChibiCombatStats.cs` (CS:). Sim units: position milli, velocity sub = 1/900 milli/tick (VelSub 900).
**1 u/s = 1000/60 milli/tick = 15000 sub.**

## 0. Ground truth about the original (verified from source)

| # | Fact | Source |
|---|------|--------|
| 1 | `dur = (heavy ? 0.42 : 0.24) / AttackSpeed`; `actionLock = dur`; strike coroutine `NormalStrike(dur * 0.4, ...)` | F:456, F:462, F:465 |
| 2 | Combo: `if (Time.time - lastAttackTime > 0.9) comboIndex = 0`; `finisher = !heavy && idx == patternLength-1`; heavy resets `comboIndex = 0`; `lastAttackTime = Time.time` at **DoNormal start** (light and heavy) | F:457-463 |
| 3 | Mult: heavy 1.8, finisher 1.5, else 1.0, times Polearm patience bonus 1.5 | F:461, F:443-451 |
| 4 | Strike kb / stun: heavy (8, 6) / 0.5 s; finisher (7, 7) / 0.45 s; light (2.5, 2) / 0.28 s. **The finisher has its own kb, not light x1.5** | F:471-472 |
| 5 | Reload: `setCooldownUntil = Time.time + 1.5` **inside the strike** (finisher, rangedNormals) | F:474, F:62 |
| 6 | Melee: `MeleeBox(reach * (heavy ? 1.15 : 1), 1.3, ...)`, one check, `kb.x *= Facing` | F:488, F:497-505 |
| 7 | Arrow: `Shoot((0.5, 0.05), (F·(heavy ? 21 : 18), 0), kb·0.6, stun)`, radius 0.25, life 1.3 | F:477, F:536-541 |
| 8 | Bolt: `Shoot((0.6, 0.25), (F·14, 0), kb·0.6, stun)`, size 0.35/0.55, radius size·0.8, life 1.2 | F:479-485 |
| 9 | Sword Aura wave on **every** normal strike (light and heavy, any branch) while `Time.time < swordAuraUntil`: `Shoot((0.7, 0), (F·15, 0), HitSpec(0.8, (4, 2), 0.25, projectile))`, radius 0.5, life 0.9. `Pierce` is never set (default false), so it **does not pierce** | F:490-494, CO:104 |
| 10 | `ReceiveHit`: `kb *= KnockbackScale(attacker)` (0.5 for Ranged/Magic, CA:40-41), then `ApplyDamage`: `hitstun = max(hitstun, stun)`; **`rb.linearVelocity = kb`** (velocity SET, not added) if `kb² > 0.01`. Blocked: kb·0.25 and stun 0 | F:548, F:596, F:611-619 |
| 11 | Hitstun: no x-velocity change at all while `hitstun > 0` (no friction branch) | F:179-190 |
| 12 | Timer order per frame: `TickTimers` (`hitstun -= dt`, `actionLock -= dt`) runs **before** the action gate | F:174, F:201-204, F:318 |
| 13 | Dispatch: `if (actionLock <= 0) { if (Light && !NormalsLocked) DoNormal(false); else if (Heavy && !NormalsLocked) DoNormal(true); else skills }`. **Light beats Heavy; normals beat skills.** There is **no input buffer**: a press while `actionLock > 0`, while dashing (HandleActions returns in the dash branch), while `hitstun > 0` (`canAct` false), or while `NormalsLocked` is simply lost | F:318-333, F:179-185 |
| 14 | Projectile: spawned from the coroutine (after Update), so its first `Update` is the **next frame**. Per frame: `Life -= dt`, move `pos += Vel·dt`, hit check (`OverlapsCircle`), **then** expire if `Life <= 0` | CO:44-111 |
| 15 | AttackSpeed is **multiplicative**: Swift Blade (Melee P0) x1.15, Crusher (Heavy P0) x0.85, Hex sluggish x0.85 | F:123-133 |
| 16 | Hit centre: `Center` = collider centre = feet + 675 milli (collider 1.35 tall, offset -0.05) | F:98-99 |

## 1. Timing formulas (60 Hz, frame 0 = the frame DoNormal runs)

`WaitForSeconds(delay)` started on frame 0 resumes on the **first frame k >= 1 where k/60 >= delay**. `actionLock`
is decremented before the gate, so the next normal is accepted on the first frame where `dur - k/60 <= 0`.

### 1a. Strike frame

`k_strike = ceil(24 · baseMs · 1000 / spPpm)` (because 0.4 · 60 = 24, and dur = baseMs/1000 · 10^6/spPpm s)

### 1b. Lock frame (first frame a new normal is accepted)

`k_lock = ceil(60 · baseMs · 1000 / spPpm)`

### 1c. Sim move layout (StartMove on tick 0; AdvanceMove runs t = 0 on tick 1; hit probe at t = Startup on tick Startup+1; MoveSlot = -1 at end of tick S+A+R; FreeActions can restart on tick S+A+R+1)

`Startup = k_strike - 1`, `Active = 1`, `Recovery = k_lock - k_strike - 1` (so `Startup + Active + Recovery = k_lock - 1`)

| Symbol | Type | Range | Description |
|--------|------|-------|-------------|
| baseMs | int | {240, 420} | light 0.24 s / heavy 0.42 s |
| classP | int | {850, 1000, 1150} | class attack speed permille (Crusher / none / Swift Blade) |
| hexP | int | {850, 1000} | Hex sluggish permille |
| spPpm | int | 722 500 .. 1 150 000 | `classP · hexP` (parts per million; multiplicative like F:127-130) |
| k_strike | int | 6 .. 14 | frame of the single hit check / shot |
| k_lock | int | 13 .. 35 | first frame a new normal is accepted |
| Startup, Recovery | int | 5..13, 6..20 | sim row values |

Output range: bounded by the spPpm set; minimum at Swift light (6 / 13), maximum at Crusher+Hex heavy (14 / 35).
No zero or negative: Recovery >= 6 for every combination. All products fit in int32 (max 24·420·1000 = 10 080 000).
Worked example (base light): 24·240·1000/10^6 = 5.76 -> 6; 60·240·1000/10^6 = 14.4 -> 15; Startup 5, Active 1, Recovery 8.

| Speed case | spPpm | Light: dur s / strike x60 / lock x60 | Light S/A/R (total) | Heavy: dur / strike x60 / lock x60 | Heavy S/A/R (total) |
|---|---|---|---|---|---|
| Base (all classes) | 1 000 000 | 0.24 / 5.76→6 / 14.4→15 | **5/1/8 (14)** | 0.42 / 10.08→11 / 25.2→26 | **10/1/14 (25)** |
| Swift Blade | 1 150 000 | 0.20870 / 5.009→6 / 12.52→13 | **5/1/6 (12)** | 0.36522 / 8.765→9 / 21.91→22 | **8/1/12 (21)** |
| Crusher, or Hex alone | 850 000 | 0.28235 / 6.776→7 / 16.94→17 | **6/1/9 (16)** | 0.49412 / 11.86→12 / 29.65→30 | **11/1/17 (29)** |
| Swift + Hex | 977 500 | 0.24552 / 5.893→6 / 14.73→15 | 5/1/8 (14) = base | 0.42967 / 10.31→11 / 25.78→26 | 10/1/14 (25) = base |
| Crusher + Hex | 722 500 | 0.33218 / 7.972→8 / 19.93→20 | **7/1/11 (19)** | 0.58131 / 13.95→14 / 34.88→35 | **13/1/20 (34)** |

**ScaleFrames does not reproduce this.** Today `ScaleFrames` ceil-scales Startup and Recovery separately with an
**additive** permille (CS:146: Swift+Hex = 1000, Crusher+Hex = 700). Example: base Recovery 8 at 1150 → ceil(6.96) = 7,
original needs 6. Rule for code: for slots 0/1 compute `k_strike`, `k_lock` from baseMs and multiplicative spPpm with the
formulas above (integer ceil `(a + b - 1) / b`), then derive Startup/Recovery. Do not scale table frames.
(Skills keep ScaleFrames until their own step; the additive-vs-multiplicative stat is shared, see section 8.)

Fragile case: Swift light strike = 5.009 frames (0.15 ms margin). Exact at an ideal 60 Hz; real vsync jitter could
make the original fire on frame 5. Use 6.

## 2. Per class values (original → sim)

Knockback units. Today `ApplyHit` does `D.Vx = Dir · KbX · VelSub`, `D.Vy = KbY · VelSub` (SIM:1000-1001): velocity is
**set**, like the original, with KbX/KbY in milli/tick. Original kb in u/s → sim sub = `kb · 15000`. 2.5 u/s =
41.667 milli/tick is not representable in milli/tick, so **store kb in sub units** (see contract). Ranged/Magic:
stored = kb·0.6 (F:477, F:483); `HalfKnockback` (SIM:943, = CA:40) halves it at hit time, all exact in sub.

`kbSub = kb_us · 15000` · `stunTicks = ceil(stun_s · 60)` (the victim acts on the first frame where `stun - k/60 <= 0`;
the sim's `Dec` at the top of the tick then `Stun > 0` gate gives the same tick).
Example: heavy 8 u/s → 120000 sub = 133.33 milli/tick; stun 0.28 s → 16.8 → 17 ticks.
Range: kbSub 9000..120000 after halving, stun 15..30 ticks, no zero/negative values.

| Class (pattern, reach) | Move | Mult | Strike / lock frame (base) | KbX, KbY sub (stored → after half) | Stun ticks | Delivery |
|---|---|---|---|---|---|---|
| Melee (4, 1.35) | light 1-3 | 1000 | 6 / 15 | 37500, 30000 | 17 | MeleeBox (rules v3 box, unchanged) |
| | light 4 = finisher | 1500 | 6 / 15 | 105000, 105000 | 27 | MeleeBox |
| | heavy | 1800 | 11 / 26 | 120000, 90000 | 30 | MeleeBox reach x1.15 |
| Heavy (**5**, 1.55) | light 1-4 / **5th = finisher** / heavy | same as Melee | same | same as Melee | 17 / 27 / 30 | MeleeBox |
| Polearm (4, 2.0) | light / finisher / heavy | same | same | same | 17 / 27 / 30 | MeleeBox |
| Ranged (4) | light | 1000 | 6 / 15 | 22500, 18000 → 11250, 9000 | 17 | arrow 18 u/s |
| | finisher (+reload) | 1500 | 6 / 15 | 63000, 63000 → 31500, 31500 | 27 | arrow 18 u/s |
| | heavy | 1800 | 11 / 26 | 72000, 54000 → 36000, 27000 | 30 | arrow 21 u/s |
| Magic (4) | light / finisher (+reload) / heavy | 1000 / 1500 / 1800 | as Ranged | as Ranged | 17 / 27 / 30 | bolt 14 u/s |

Today's table vs the above (biggest mismatches):

| Row | Today S/A/R, kb, stun | Should be (base) |
|---|---|---|
| Melee light / heavy | 4/3/9, 45/35 milli (=40500/31500 sub), 16 / 10/4/16, 130/100, 30 | 5/1/8, 37500/30000, 17 / 10/1/14, 120000/90000, 30 |
| Heavy light / heavy | 6/4/12, 55/40, 18 / 16/5/20, 140/110, 32 | same as Melee (no class differences in the original; Crusher only via speed) |
| Polearm light / heavy | 5/3/10, 45/35, 16 / 12/4/18, 130/100, 30 | same as Melee |
| Ranged light / heavy | 5/1/10, 45/30, 10, life 45 / 12/1/18, 110/80, 24, life 50 | 5/1/8, 22500/18000, 17, life 78 / 10/1/14, 72000/54000, 30, life 78 |
| Magic light / heavy | 6/1/12, 40/30, 14, 230 milli/t, life 50 / 14/1/20, 100/70, 26, 200 milli/t, life 60 | 5/1/8, 17, 233.33 milli/t, life 72 / 10/1/14, 30, 233.33, life 72 |
| Finisher | light kb x1.5 via MoveKbScale, stun unchanged | explicit (7, 7) u/s and 27 ticks |
| Active | 3-5 ticks of hit checks on melee | 1 (single MeleeBox call) |

Total lock today: Melee light 16 ticks → next light on tick 17 (original 15); Heavy-class heavy 41 → tick 42 (original 26).

## 3. Combo rules

- Window: reset when `elapsed > 0.9 s`, elapsed measured from the **start** (DoNormal frame) of the previous normal,
  light or heavy. At 60 Hz a light on frame j after the previous start chains for j <= 54 and resets for j >= 55
  (j = 54 is exact 0.9: float-equality risk, ±1 frame).
- Sim today: `ComboWindow = 54` at StartMove, `Dec` at tick top, reset if `<= 0` → resets at j >= 54. **Set
  `ComboWindowTicks = 55`.**
- Finisher: `idx == patternLength - 1` → 4th light (Melee, Ranged, Magic, Polearm), 5th light (Heavy). Then idx wraps to 0.
- Heavy: comboIndex = 0 (sim `ComboIdx = 0; ComboWindow = 0` is equivalent).
- Note: F:722 also writes `lastAttackTime` (a skill path). If that refreshes the combo window for lights, step 10 must set
  ComboWindow there too. Not verified here.

## 4. Reload (Archer / Mage)

- Starts on the **finisher strike frame** k_strike (F:474), lasts 1.5 s: normals accepted again on the first frame
  `Time.time >= strike + 1.5` → **strike tick + 90** (exact-equality float, ±1 frame). For a base-speed finisher started on
  tick 0: strike tick 6, normals accepted from tick **96**, ignored on tick 95.
- If the attacker is hit before the strike, the original never sets the reload (coroutine does not reach F:474, assuming
  `Interrupt` stops it, see section 10).
- Sim today: `SetCd = MoveStartup + 90` at StartMove → accepted from tick S + 90 = 95: **one tick early**, and set even if
  the move is interrupted. Fix: set `SetCd = 90` on the strike tick (in AdvanceMove, when the finisher shot arms).
  Because `Dec(SetCd)` runs at the top of the next tick, accepted at strike + 90. 
- While locked a Light/Heavy press is ignored (not deferred); dispatch falls through to skills (F:320-332). Skills are
  not blocked by the reload.

## 5. Input handling, decision D-N1

Original: no buffer anywhere in the normal path (fact 13). Sim today (SIM:505-509, 584-595): every action edge
(Light, Heavy, Skill1-3, Ultimate; not Dash) is ORed into `BufBtn` and kept for up to `InputBufferTicks = 6` ticks, so a
press during the last 6 ticks of recovery, of a dash, of hitstun, or of the reload still fires.

| Option | Behaviour | Pros | Cons |
|---|---|---|---|
| **A: match the original (Recommended)** | Light/Heavy read from this tick's press edge only; a press while the move runs, during a dash, during stun or reload is lost | Exact original feel and timing; removes 2 hashed fields; tests in section 9 are exact | Online, input delay plus no buffer feels stricter; players must press on or after the unlock tick |
| B: keep a 6-tick buffer | as today | More forgiving under input delay | Next normal fires up to 6 ticks earlier than a human could in the original (presses that the original drops still act); combo/lock tests differ |

If A: remove the Light/Heavy bits from `ActionMask`; FreeActions tests `edges & Light`, then `edges & Heavy`.
Also fix priority (independent of A/B): original **Light > Heavy**, and normals are checked **before** skills; sim checks
ult > skills > heavy > light. Skills/ult share the buffer and the priority: same original rule (no buffer, Light > Heavy >
skills), but that is **step 12** to decide; if the buffer is kept for skills meanwhile, `BufBtn/BufAge` stay with a
Skill/Ult-only mask. If both are removed, delete `BufBtn`, `BufAge` from state, copy, hash and reset.

## 6. Sword Aura wave

| Field | Original | Sim value | Today |
|---|---|---|---|
| Trigger | every normal strike (light, finisher, heavy) while aura active, at strike time | arm tick of slots 0 and 1 while `AuraTicks > 0` | slot 0 only |
| Spawn | Center + (F·0.7, 0) | X + F·700, Y + 675 | X + F·600, Y + 600 |
| Speed | 15 u/s | 250 milli/tick = 225000 sub | 280 |
| Life | 0.9 s → 54 moves (CO:47, 108) | 54 (with the expiry-after-hit order, section 7) | 40 |
| Hit | mult 0.8, kb (4, 2), stun 0.25, projectile (blockable), non-piercing | 800, 60000 / 30000 sub, 15 ticks, destroyed on hit | 800, 120/80 milli, 12 |
| Hit size | circle r 0.5 (visual 0.25 x 1.2) | r 500 (AABB today 150 x 100, step 11) | |

Spawned in addition to the normal's own hit/shot (same tick). Melee only in practice (only Melee's ult sets the aura).

## 7. Projectile normals

| | Arrow light | Arrow heavy | Bolt light | Bolt heavy |
|---|---|---|---|---|
| Spawn (feet-relative milli) | X + F·500, Y + 725 | same | X + F·600, Y + 925 | same |
| Speed | 18 u/s = 300 milli/t = 270000 sub | 21 = 350 = 315000 | 14 = 233.33 = **210000 sub** | same |
| Life | 1.3 s → 78 moves | 78 | 1.2 s → 72 | 72 |
| Hit radius (original circle) | 250 | 250 | 280 | 440 |
| Sim today | spawn X+450, Y+700 for all; AABB half 150 x 100 | | | |

- Per-frame order (fact 14): first move on the frame **after** the strike; each frame move, hit check, then expire, so a
  life of L frames gives L moves and L hit checks. Sim UpdateProjectiles does `Life--` and expires **before** the hit
  check (L-1 checks). Contract: move expiry after the hit check and keep Life = 78/72/54; confirm in code that a
  projectile spawned on tick k does not move until tick k+1 (tick order not verified here).
- Life values 1.3·60, 1.2·60, 0.9·60 are exact integers: float accumulation can shift the original by ±1 frame.
- 14 u/s is not a whole milli/tick: needs sub-unit ProjSpeed (or 233, -0.14%).
- Circle vs AABB mismatch (ProjHalfW 150 / ProjHalfH 100 vs radius 250-440, against the 750 x 1350 body): **step 11**.
- Kb sign on hit follows projectile Vx (CO:101) = sim `Dir = Sign(p.Vx)`.

## 8. Attack power (step 9, recorded only)

`AttackPower = attack · (Heavy class ? 1.2 : 1) · (weakened ? 0.9 : 1)` (F:112-121). The Heavy x1.2 applies to **both**
passives (no passive check). Sim: weaken -100 permille in RollDamage (CS:179); Heavy x1.2 not verified in CS. Patience
(Polearm P1, >= 3 s since lastAttackTime, x1.5) in sim uses IdleTicks; check in step 9. AttackSpeed: sim uses additive
permille (CS:146), original multiplicative; the fix in section 1 must be shared with skills later.

## 9. Contract for code (RulesVersion 8)

1. `RulesVersion = 8` with remark "normals matched to original (timing, kb, stun, finisher, reload, aura, projectiles)".
2. Kb in **sub units** in `ChibiMove.KbX/KbY`, `PendingHit`, `ChibiProjectileSim`; `ApplyHit` sets `D.Vx = Dir·KbX`,
   `D.Vy = KbY` (blocked: `KbX / 4`). Convert every existing row x900 (behaviour unchanged for skills). Same for
   `ProjSpeed` (sub/tick; existing rows x900).
3. Slots 0/1: `BaseMs` (240/420) replaces Startup/Recovery; Active = 1; StartMove computes Startup/Recovery per section 1
   with multiplicative `spPpm = classP · hexP`.
4. Light finisher values per class: `FinKbX, FinKbY, FinStun` (105000/105000/27 melee classes; 63000/63000/27 stored for
   Ranged/Magic); mult 1500. New hashed fighter field `MoveFinisher` (0/1) so the strike tick picks them and the reload.
   Drop `MoveKbScale` for normals.
5. Stun ticks 17 / 27 / 30; aura 15.
6. Projectile spawn offsets per move: `ProjOffX, ProjOffY` (feet-relative milli): arrow 500/725, bolt 600/925, aura 700/675.
7. `SetCd = 90` set on the finisher strike tick (Ranged/Magic), not in StartMove.
8. `ComboWindowTicks = 55`.
9. Aura wave on slots 0 and 1; values per section 6.
10. Projectile expiry after hit check; lives 78 / 72 / 54; no move on the spawn tick.
11. Dispatch priority Light > Heavy > skills. If D-N1 = A: Light/Heavy from this tick's edges only, no buffer.

## 10. Reference test values (base speed, grounded, stage centre, attacker at X = 0 facing +1, press on tick 0)

| Test | Expected | Tolerance |
|---|---|---|
| Melee light hit tick | probe hits on tick 6 (victim in box) and on no other tick | exact |
| Melee heavy hit tick | tick 11 | exact |
| Light lock | second Light on tick 14 ignored (A: and lost, no attack on tick 15 without a new press); Light on tick 15 starts a move | exact |
| Heavy lock | Light on 25 ignored, on 26 accepted | exact |
| Swift light / heavy | hit 6 / 9, accepted 13 / 22 | exact |
| Crusher light / heavy | hit 7 / 12, accepted 17 / 30 | exact |
| Crusher + Hex heavy | hit 14, accepted 35 | exact |
| Melee light kb | victim Vx = 37500, Vy = 30000 sub on hit tick H; X(H+16) - X(H) = 16 · 41.667 = 666.7 milli | ±1 milli |
| Melee heavy kb | Vx 120000; X(H+29) - X(H) = 3866.7 milli; victim acts on tick H+30 | ±1 milli, exact tick |
| Finisher (Melee 4th light, lights on 0/15/30/45) | 4th hit kb 105000/105000, stun 27; X(H+26) - X(H) = 3033.3 | ±1 milli |
| Heavy class finisher | the **5th** light (tick 60) is the finisher; the 4th is not | exact |
| Combo reset | light on 0, next light on 54 = idx 1; on 55 = idx 0 (no finisher progress) | exact (original ±1) |
| Heavy resets chain | L, L, H, L, L, L, L: finisher is the 7th press | exact |
| Ranged light kb (after halving) | Vx 11250 sub = 12.5 milli/t; 16 ticks → 200 milli | ±1 |
| Magic heavy kb | Vx 36000 = 40 milli/t; 29 ticks → 1160 milli | ±1 |
| Arrow light | spawn tick 6 at (500, 725), first move tick 7, 300 milli/tick, last position tick 84 = 500 + 78·300 = 23900 (blast bound may cut it first), gone at end of tick 84 | exact |
| Arrow heavy | spawn tick 11, 350 milli/tick, 78 moves | exact |
| Bolt | 233.33 milli/tick: X after n moves = 600 + floor(n·210000/900); 72 moves → 17400 | ±1 milli |
| Reload (finisher on tick 45 of chain 0/15/30/45) | strike tick 51; Light on tick 140 ignored, tick 141 accepted; Skill press on tick 60 still works | exact (original ±1) |
| Reload interrupted | Ranged hit on tick 50 before the strike: SetCd stays 0 | exact |
| Aura on heavy | Melee with AuraTicks > 0, Heavy on tick 0: wave spawns tick 11 at (700, 675), 250 milli/tick, 54 moves, destroyed on first hit, kb 60000/30000, stun 15 | exact |
| D-N1 = A buffer | Light pressed on tick 10 of a light (lock until 15), no further presses: no move starts on ticks 15-20 | exact |
| Priority | Light + Heavy on the same free tick: light starts | exact |

## 11. Not verifiable from the files read

- Unity `WaitForSeconds` uses `>=` against scaled `Time.time` (assumed). Exact-integer products (0.9, 1.5, 0.45·60, 0.5·60,
  life·60) are ±1 frame in the original due to float accumulation.
- `Match.HitStop(0.045 / 0.08)` on every hit (F:625) pauses scaled time in the original, stretching lock, strike, stun and
  reload timers by ~3-5 frames after a hit. Not modelled in the sim; needs its own decision.
- Whether `Interrupt()` (F:~665-673) stops the NormalStrike coroutine (assumed yes: actionLock = 0 there).
- Whether `Controls.Light/Heavy` are press edges (assumed yes).
- Sim tick order of UpdateProjectiles relative to fighter steps (spawn-tick movement).
- F:722 lastAttackTime write from a skill path (combo-window refresh).
