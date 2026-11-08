# Ultimates Conversion: Fighter.cs FireUltimate + ult coroutines to ChibiSimulation (integer, 60 Hz) — port step 14

Status: draft for review (systems-designer, 2026-11-08). Values marked **TBD-verify** need a source read or a sim run
before tests pin them.

Sources: `Assets/ChibiClash/Scripts/Fighter/Fighter.cs` (F:), `Assets/ChibiClash/Scripts/Netcode/ChibiSimulation.cs`
(SIM:). Builds on `projectile-conversion.md` (rows 11-13, section 4 split, D-P1) and `multiphase-conversion.md`
(section 0 facts, section 1 frame-0 rule, Held / Holding, Interrupt). Decision prefix: **D-U**.

Units: positions milli (1 u = 1000), velocity sub = 1/900 milli per tick, so **1 u/s = 15000 sub**; 1 s = 60 game steps.
Ult cinematic (CinematicRealTicks 48 at 200 permille) already exists and is unchanged: all ult timings below are in
**game steps**, which the cinematic stretches in real time exactly as it stretches the original's `Time.deltaTime`.

Timing rules used (multiphase-conversion.md section 0 fact 4, section 1 rule 5): `WaitForSeconds(d)` yielded on frame
j resumes on frame `j + ceil(60·d)`; a constant loop `for (t = 0; t < T; t += dt)` runs bodies on frames `0..ceil(60·T)-1`
and the code after it on frame `ceil(60·T)`; when `60·d` is an exact integer the float accumulation can land ±1 frame
(flagged "exact-integer ±1", same treatment as step 12).

---

## 0. Ground truth about the ult start (verified from source)

| # | Fact | Source |
|---|---|---|
| 1 | The ult is fired from `UpdateUltWindow()` at **F:176**, before `var v = rb.linearVelocity` (F:178), before the `canAct` test (F:179) and before `HandleActions` (F:184). It is not inside `HandleActions` like skills. | F:170-190, F:408-425 |
| 2 | Its only gates are `match && Match.InputEnabled && !IsDead` (F:417). It does **not** check hitstun, `held`, `actionLock`, dash or shield. In the original you can ult while stunned, while held, mid-move and mid-dash. | F:417 |
| 3 | **Consequence for F:190 (the D-M2 glitch): it does not happen for ults.** A frame-0 velocity write in an ult coroutine runs at F:176, F:178 then *reads* it into `v`, and F:190 writes it back. So Smash's jump (15 u/s) and the Kick's out-of-range dash on frame 0 **stick**. The Kick's self `vy 12` is written after a `WaitForSeconds` (frame >= 1, after Update), so it sticks too. No A/B decision is needed for F:190. | F:176-190, F:988, F:1047, F:1071 |
| 4 | But `HandleActions` still runs after the ult on the same frame (hitstun was just cleared, so `canAct` is true unless held): grounded with `actionLock > 0` → `v.x = MoveTowards(v.x, 0, 25·dt)` (F:286-289); Jump (not gated by actionLock) → `v.y = 13` grounded / `11.5` air jump (F:291-305); fast-fall (F:307); Dash press starts a dash (F:309-316). | F:258-316 |
| 5 | Common body F:954-962: `Ult = 0; UltsUsed++; Interrupt(); hitstun = 0; invuln = max(invuln, 0.6); Match.Cinematic(this)`. `Interrupt()` = `StopAllCoroutines; actionLock = 0; dashTimer = 0; blocking = false; ReleaseHeld()` (F:670-677). | F:954-962, F:670-677 |
| 6 | Class ults then **assign** (not max) their own invuln: Smash 1.4, Arrows 0.8, Kick 2.0 (F:987, F:1013, F:1039). An assignment can *lower* a larger value (e.g. respawn invuln 90 ticks → Arrows 48). Sword Aura and Fire Ring keep `max(invuln, 0.6)`. | F:960, F:987, F:1013, F:1039 |
| 7 | Sword Aura and Fire Ring set **no actionLock**; after `Interrupt()` the lock is 0, so the fighter is free on the press frame itself (a Light pressed on the same frame fires in F:320). | F:958, F:966, F:975 |
| 8 | `Stun(t)` (used by the Kick) does `hitstun = max(hitstun, t); Interrupt(); if (!held) V = (0, min(0, vy))` and does **not** check invuln. | F:663-668 |

## 1. Common ult start — sim contract

New helper `FireUlt(s, i)` (called by `StartMove` for slot 5, before the class program):

```
Interrupt(s, i)            // SIM:2069-2074 — MoveSlot -1, DashTicks 0, Shielding false, ShieldTick 0, Release(s, i) (verified)
f.Stun = 0                 // F:959
f.Invuln = max(f.Invuln, 36)   // 0.6 s = 36 (SIM:1238 already does this)
s.CinematicRealTicks = 48; s.CinematicFighter = i   // SIM:1235-1236, unchanged
f.Ult = 0; f.UltsUsed++    // SIM:1237, unchanged
then the class part (section 2), which may assign Invuln
```

Invuln convention (as DashInvuln, SIM:117): `Invuln = N` set on tick 0 → immune on ticks 0..N-1.

**Ult while held / stunned (stand-in, D-U1).** The sim's stand-in (SIM:1129-1132) fires only on the free action path,
after jump and dash (SIM:1124-1125), on a full meter, and returns. So in step 14 the sim cannot ult while stunned, held,
mid-move or mid-dash — unlike the original (fact 2). The original trigger is a gesture window (F:396-434), which is
**step 15**. Step 14 keeps the stand-in where it is, but implements the full common body (Interrupt, Stun = 0) so that
step 15 only has to move the call site. See D-U1.

**Frame-0 velocity of ults (implementation note, not a decision).** multiphase-conversion.md fact 2 / section 1 skip
frame-0 velocity writes of skill programs. **Ult programs are the exception** (fact 3): their frame-0 velocity is
applied on the press tick. Then the same-tick `HandleActions` adjustments of fact 4 apply (grounded decel 25 u/s² =
`MoveFriction` 6250 sub per tick, SIM:27-28, verified). Ordering difference under
the stand-in: the sim processes Jump before the ult (SIM:1124 before 1132), the original after (F:176 before F:291). A
same-tick Jump + Ult press: original Smash ends with vy 13 (jump overwrites), sim with 15 unless the ult write is
skipped when this tick's jump edge already set Vy. Covered by D-U1.

---

## 2. Per-Ultimate conversion

### 2.1 Melee — Sword Aura (F:966, F:490-494)

| Frame | Original |
|---|---|
| 0 (press) | common body; `swordAuraUntil = now + 10` → 600 steps; no actionLock (fact 7) |
| any normal strike while aura | `NormalStrike` (light, finisher **and** heavy) also shoots a wave: offset (0.7, 0) from Center, 15 u/s, spec 0.8 / kb (4,2) / stun 0.25, radius 0.5, life 0.9 s (F:490-494) |

**Sim today.** Row `Mv(10, 1, 20, flags: A)` SIM:410 → `AuraTicks = 600` is set on the row's active frame (SIM:1574),
i.e. tick 10, and the fighter is locked 31 ticks. Wave on normals SIM:1576-1585 (MoveSlot <= 1, Melee, AuraTicks > 0):
spawn (X + F·700, Y + 675), 225000 sub, R 500, life 54, mult 800, kb 60000/30000, stun 15 — **matches** F:490-494.

**Differences.** Aura starts 10 ticks late and the fighter is locked 31 ticks; the original is instant and free.

**Conversion (D-U2 = A).** No row program: on the press tick `AuraTicks = AuraDuration (600)`, `MoveSlot = -1`
(free from tick 1; the stand-in `return` eats other presses of the press tick, step 15 removes that). Aura counts down
in TickTimers (SIM:1018) and is not cleared by Interrupt (timestamp in the original). TBD-verify: is `swordAuraUntil`
reset on ring-out/respawn in the original (RingOut F:692+)? Sim InitFighter clears it only at match start (SIM:880).

### 2.2 Heavy — Smash (F:969, F:984-1008)

| Frame | Original | Sim action |
|---|---|---|
| 0 (press, F:176) | common; `actionLock = 1.4`; `invuln = 1.4` (assign → **84**); `V = (0, 15)` — **sticks** (fact 3), then same-frame HandleActions: grounded decel on vx (vx already 0), Jump may overwrite vy | Invuln = 84; Vx = 0, Vy = **225000**; MovePhase 0 |
| 1 .. | loop body: `if (t > 0.15 && Grounded) break` with t = k/60 on frame k. No velocity writes after frame 0: gravity and the player's air jump / fast-fall act normally; vx is not steered (actionLock > 0, F:280) | none |
| S | slam: first frame k in **10..65** with `Grounded` (GroundCheck at start of frame k, F:175), else k = **66** (loop end, 1.1 s; exact-integer ±1). t > 0.15 first true at k = 10 (9/60 = 0.15 exact-integer ±1) | slam on tick S using `groundedAtStart` (as Leap Slam) |
| S | two waves, dir = -1 and +1 (not Facing): pos (Center.x + dir·0.6, bounds.min.y + 0.55), vel dir·13 u/s, spec 3.0 / kb (6, 12) / stun 0.8, R 0.6, life 1.1 s, Pierce, !Destructible, GrowRate 1.4; then `actionLock = 0.3` (assign) | spawn 2 × `SmashWave`: (X ± 600, Y + 550), Vx ±195000, R 600, GrowPerTick 14, Life 66, Mult 3000 (no attack-speed b), KbX 90000, KbY 180000, Stun 48, flags Pierce|Indestructible (projectile-conversion.md row 11). Spawn order dir -1 then +1. `MoveLockEnd = S + 18` |
| S + 18 | free (0.3 s = 18, exact-integer ±1, same as Leap's 0.2 → 12) | MoveSlot -1 |

Initial `MoveLockEnd` = 66 + 18 = 84 (timeout bound, like `LeapTimeout + LeapAfterSlam`). Invuln (84) ends independently
of the slam tick.

Wave growth: `r(n) = 600 + 14n` per update, first update on the tick after spawn (projectile-conversion.md section
3c / "first update" row), `r(66) = 1524`; growth before the hit test. Kb sign from `Vx >= 0`, so each wave pushes away
from the Heavy.

**Sim today.** Row `Mv(20, 4, 20, 3000, 0, 5000, 0, 900, 100, 200, 48, flags: C)` SIM:419: a centred melee box stand-in
on tick 20, lock 44, no jump, no waves, Invuln 36 only. `GrowPerTick` is unused (SIM:1750 pragma "wired in port step 14").

**Differences.** No jump, no landing wait, box instead of two travelling growing waves, invuln 36 instead of 84, lock 44
instead of S + 18.

**Conversion.** New program `PSmash` (ProgramOf Heavy slot 5). Frame-0 velocity write applied (section 1 note). Slam
detection identical to PLeap (reuse its code path with Smash constants: `SmashMinFrame 10`, `SmashTimeout 66`,
`SmashAfterSlam 18`, `SmashJumpVel 225000`, `SmashInvuln 84`). No new state: MovePhase 0 → 1 at slam, MoveLockEnd.

### 2.3 Ranged — Thousand Arrows (F:972, F:1010-1034)

| Frame | Original | Sim action |
|---|---|---|
| 0 (press, F:176) | common; `actionLock = 0.8` → 48; `invuln = 0.8` (assign → **48**); Shoot lead: offset (0.5, 0.3) from Center, vel (F·9, 12), harmless, !Destructible, R 0, life 0.45 s; `yield break` | spawn `VolleyLead` at (X + F·500, Y + 975), (F·135000, 180000), R 0, Life 27, flags Harmless|Indestructible (row 12); `MoveLockEnd = 48` |
| lead expiry | OnExpire at the lead's position: 18 arrows, each target = Opponent.Center + (Range(-2.5,2.5), Range(-0.5,0.5)), from = lead + insideUnitCircle·0.6, speed Range(14,19) u/s, spec 0.45 / kb (1.5,1) / stun 0.15, R 0.25, life 1.5 s | `SplitVolley(s, owner, lx, ly)` SIM:1896/1906 — already built (D-P1 rejection sampling, row 13) |
| 48 | free | MoveSlot -1 |

The lead is a straight-line projectile (TBD-verify: no gravity on projectiles, projectile-conversion.md section 3). It
expires by Life 27 or by bounds; the split uses the lead's last position (`lx, ly`). The ult itself has no velocity
write and does not use attack speed or the reload.

**Sim today.** SIM:434-436: a horizontal `VolleyArrow` stream row (stand-in, `Mv(14, 30, 20, 450, rehit 3)`).

**Differences.** Stream instead of lead + 18-arrow rain; invuln 36 instead of 48; lock 64 instead of 48.

**Conversion.** Replace the row with a row-less program `PArrows` (or a one-shot in StartMove): spawn lead on tick 0,
lock 48. Split machinery unchanged.

### 2.4 Magic — Fire Ring (F:974-976)

| Frame | Original |
|---|---|
| 0 (press) | common; `FireRingUntil = now + 10` (600); `FireRing.Spawn(this)`; no actionLock (fact 7) |

**Sim today.** Row `Mv(12, 1, 20, flags: R)` SIM:451 → `RingTicks = RingDuration (600)` on the active frame (SIM:1575),
i.e. tick 12, lock 33. Ring damage / projectile destruction: `TickFireRings` (SIM:1937), `RingRadius 2100` (SIM:96)
matches F:526 (2.1).

**Confirmed vs. not.** The ring's *behaviour* (radius, duration 600, destroy destructible projectiles) matches. The
*start* does not: the original sets the ring on the press frame with no lock; the sim starts it on tick 12 and locks 33
ticks. TBD-verify by reading SIM:1560-1580 that nothing else sets `RingTicks` on tick 0. Fix together with Sword Aura
under D-U2 (instant, no lock, `MoveSlot = -1`).

### 2.5 Polearm — Kick (F:979, F:1036-1079)

Frame 0 (press, F:176): common; `actionLock = 1.9` → **114**; `invuln = 2.0` (assign → **120**).
`inRange = !o.IsDead && |o.Center.x - Center.x| < 8 && |o.Center.y - Center.y| < 3` (strict; Centers share the 0.675
offset, so the sim uses feet: `|oX - X| < 8000 && |oY - Y| < 3000`, read on the press tick before Physics).

**Branch A — out of range**

| Frame | Original | Sim action |
|---|---|---|
| 0 | loop t = 0: `V = (F·20, 0)` — sticks (fact 3); then same-frame HandleActions: grounded → vx -= 25·dt | Vx = F·300000 then grounded decel `MoveFriction` → F·293750 (airborne: 300000), Vy 0 |
| 1..14 | `V = (F·20, 0)` after Update every frame (loop 0.25 s → 15 bodies, frames 0..14) | Vx = F·300000, Vy = 0 before Physics (ProgramVelocity) |
| 15 | `MeleeBox(2.2, 1.4, spec 2.0 / kb (8, 6) / stun 0.5)` centre Center + (F·1.25, 0); `yield break`; actionLock stays 1.9 | box centre (X + F·1250, Y + 675), half 1100 × 700, Mult 2000, KbX 120000, KbY 90000, Stun 30, blockable; pre-Physics position |
| 15..113 | no writes; HandleActions grounded decel 25 u/s² with lock > 0 | — |
| 114 | free | MoveSlot -1 |

`0.25·60 = 15` is exact-integer ±1 (box on 15 or 16, step-12 treatment).

**Branch B — in range**

| Frame | Original | Sim action |
|---|---|---|
| 0 | `SetFacing(toward o)`; `from = transform.position`; `to = (o.x - F·1.3, o.y)` (transform positions, i.e. feet); loop t = 0: `V = 0`, `pos = Lerp(from, to, 0) = from`. Same-frame HandleActions: Jump may set vy (fact 4) | Facing = sign(oX - X) (oX == X → -1, as `>`); store `KickFromX/Y = X/Y`, `KickToX = oX - F·1300`, `KickToY = oY`; V = 0 |
| 1..8 | `V = 0; pos = Lerp(from, to, k/9)` (t/0.15 = k/9). Loop has 9 bodies (0.15·60 = 9, exact-integer ±1); **the lerp never reaches t = 1** (last 8/9) | before Physics: `X = FromX + (ToX - FromX)·k / 9`, `Y = FromY + (ToY - FromY)·k / 9` (C# int truncation toward 0), Vx = Vy = 0; then Physics runs (one tick of gravity / collision, like Unity moving a dynamic body then stepping) |
| 9 | after the loop: `o.Stun(1.6)` (max → 96, Interrupt victim, `if !held V = (0, min(0, vy))`), `o.SetHeld(true)` (kinematic, V 0), `heldTarget = o`, `o.ReceiveHit(1.5, kb 0, stun 1.2, melee, unblockable)` | `StunF(s, 1-i, 96)` (SIM:2077-2085: max + Interrupt + Vx 0 / Vy min 0, no invuln check — verified), `StartHold(s, i)` (SIM:1479) **then** hit: Mult 1500, Kb 0, Stun 72, FUnblockable, via the normal hit path (SIM:1967, skips if o.Invuln > 0) |
| 10..29 | holding; `WaitForSeconds(0.35)` → resumes frame 9 + 21 = **30** (exact-integer ±1). Attacker: no writes, lock > 0 | victim Held: kinematic, V 0, stays at its frame-9 position (SIM:1033-1035) |
| 30 | `V = (0, 12)` (frame >= 1 → sticks) | Vy = **180000**, Vx = 0 |
| 31..49 | `WaitForSeconds(0.32)` → 30 + ceil(19.2) = **50** | attacker rises under gravity; victim stays held in place |
| 50 | `ReleaseHeld()` (victim dynamic, V 0) then `o.ReceiveHit(2.5, kb (F·12, 9), stun 0.8, melee, unblockable)` | `ReleaseHeld(s, i)` (SIM:1489) then hit: Mult 2500, KbX F·180000, KbY 135000, Stun 48, FUnblockable |
| 114 | free (lock 1.9 from frame 0) | MoveSlot -1 |
| 120 | invuln ends (immune 0..119) | — |

Victim hitstun: 96 from frame 9 (ends frame 105); the 48 at frame 50 is below the remaining 55 if the hit path uses
max (TBD-verify ApplyHit stun rule). Kick hits are not projectiles: no attack-speed, no patience (slot 5 excluded,
SIM:1223).

Hold invariants reuse step 12: `F[i].Holding == F[1-i].Held`; the single Interrupt release path releases the victim if
the Polearm is interrupted (it is invulnerable 120 ticks, so mainly ring-out SIM:2321 and match end).

**Sim today.** Row `Mv(18, 4, 30, 4000, 150, 1500, 0, 1300, 140, 190, 72, flags: U)` SIM:463: one unblockable box on
tick 18, lock 52, invuln 36. No range check, no dash, no teleport, no hold, no rise.

**Differences.** Everything except "unblockable": two branches, teleport lerp, 41-frame hold, two hits, self rise,
invuln 120, lock 114.

**Conversion.** New program `PKick`, `MovePhase`: 0 = deciding (tick 0), 1 = dash (A), 2 = lerp (B), 3 = holding,
4 = done. Lerp and hold happen in the program-frame slot before the melee probes (as HoldPhase in step 12).

---

## 3. Decisions

**D-U1 — where the stand-in ult fires in step 14.** In the original the ult goes off from the gesture window at the very
start of the fighter's frame, even while stunned, held, mid-move or mid-dash (fact 1-2). The sim's stand-in only fires
when the fighter is free, after jump and dash.
- **A (recommended): keep the stand-in where it is until step 15**, but write the full common start (Interrupt,
  Stun = 0, invuln) and let the ult's frame-0 velocity win over a same-tick jump. Step 15 then only moves the call to the
  start of the fighter step together with the gesture window. Plain words: in step 14 you can't ult out of a stun yet;
  that comes with gestures.
- B: move the ult check to the start of the fighter step now (before the stun/held gate), still on a button. Closer to
  the original now, but the button behaviour would change again in step 15 and held-ult (a Polearm teleporting out of a
  Heavy grab) needs step-15 tests anyway.

**D-U2 — Sword Aura and Fire Ring start time.** Original: buff on the press frame, fighter free at once. Sim: buff on
tick 10 / 12 and a 31 / 33 tick lock.
- **A (recommended): copy** — buff on tick 0, no lock. These are rows only because step 9 needed a stand-in.
- B: keep the rows (a small "casting" lock). Changes how the ult feels versus the original; no reason found to keep it.

**D-U3 — Kick lerp storage.**
- **A (recommended): 4 new fighter fields** `KickFromX, KickFromY, KickToX, KickToY` (copied + hashed). Clear and
  testable.
- B: reuse `GrabLX / GrabLY` for `to` (Polearm never grabs) + 2 new fields. Saves 2 ints, couples two moves.

**D-U4 — Kick on an invulnerable victim in range.** `Stun()` and `SetHeld()` ignore invuln; only `ReceiveHit` checks it
(fact 8). So a respawning (invulnerable) opponent within range gets held 41 frames and stunned 96 ticks without taking
the damage.
- **A (recommended): copy** (parity is the goal of the port; it is rare and visible).
- B: skip the whole in-range branch when the victim is invulnerable (treat as out of range).

---

## 4. State and code contract (rules version: next after the current; TBD-verify number)

New fighter fields (Copy + Hash + InitFighter clear): `KickFromX, KickFromY, KickToX, KickToY` (D-U3 = A). Everything
else reuses existing state: `MoveSlot 5`, `MoveTick`, `MovePhase`, `MoveLockEnd`, `Held / Holding`, `AuraTicks`,
`RingTicks`, `Invuln`, `Stun`, projectile `GrowPerTick` (now written; remove the CS0649 pragma at SIM:1750).

New constants (in the config hash, SIM:271-276 pattern): `SmashJumpVel 225000, SmashMinFrame 10, SmashTimeout 66,
SmashAfterSlam 18, SmashInvuln 84, SmashWaveDx 600, SmashWaveDy 550, SmashWaveVel 195000, SmashWaveR 600,
SmashWaveGrow 14, SmashWaveLife 66, SmashWaveMult 3000, SmashWaveKbX 90000, SmashWaveKbY 180000, SmashWaveStun 48;
ArrowsLock 48, ArrowsInvuln 48, LeadDx 500, LeadDy 975, LeadVx 135000, LeadVy 180000, LeadLife 27;
KickLock 114, KickInvuln 120, KickRangeX 8000, KickRangeY 3000, KickDashVel 300000, KickDashFrames 15, KickBoxDx 1250,
KickBoxW 2200, KickBoxH 1400, KickBoxMult 2000, KickBoxKbX 120000, KickBoxKbY 90000, KickBoxStun 30, KickLerpFrames 9,
KickBehind 1300, KickStun 96, KickHit1Mult 1500, KickHit1Stun 72, KickRiseFrame 30, KickRiseVel 180000,
KickReleaseFrame 50, KickHit2Mult 2500, KickHit2KbX 180000, KickHit2KbY 135000, KickHit2Stun 48; UltCommonInvuln 36`.

Rows SIM:410, 419, 434-436, 451, 463 become lock/cooldown-free placeholders (or are removed for slot 5); `ProgramOf`
gains Heavy 5 → PSmash, Ranged 5 → PArrows, Polearm 5 → PKick; Melee/Magic 5 handled inline in FireUlt.

**RNG draw order.** Step 14 adds **no** draws. The only ult randomness is the Thousand Arrows split, already specified
by D-P1 (projectile-conversion.md section 4): drawn inside `UpdateProjectiles` at the lead's expiry, in projectile slot
order. Smash and Kick are fully deterministic. (Fx.Burst randomness is view-only.)

---

## 5. Reference values (stage centre, grounded, attacker at X = 0, Facing +1, at rest, ult pressed on tick 0, no other input, no hitstop; cinematic stretches real time only)

| # | Case | Expected |
|---|---|---|
| R1 | Any ult | tick 0: Ult 0, UltsUsed +1, CinematicRealTicks 48, Stun 0, Invuln >= 36 |
| R2 | Sword Aura | tick 0: AuraTicks 600, MoveSlot -1 on tick 1; Light on tick 1 → wave spawned on its strike tick |
| R3 | Fire Ring | tick 0: RingTicks 600, free on tick 1 |
| R4 | Smash | tick 0: Vy 225000, Invuln 84; slam tick S = sim run **TBD-verify** (first groundedAtStart tick >= 10); waves at (±600, 550) relative to the slam-tick position, Vx ±195000, R 600 → 614 after first update; free S + 18 |
| R5 | Smash off-stage (never lands) | slam on tick 66 in the air, free 84 |
| R6 | Thousand Arrows | tick 0: lead at (500, 975), V (135000, 180000), Life 27, Invuln 48; split of 18 arrows on the lead's expiry tick (**TBD-verify** 27 or 28 per projectile lifecycle); free 48 |
| R7 | Kick in range, opponent at X 5000 (same Y) | to = 3700; X on ticks 1..8 = 411, 822, 1233, 1644, 2055, 2466, 2877, 3288; tick 9: victim Held, Stun >= 72 (96 from Stun), damage 1.5x; tick 30 attacker Vy 180000; tick 50 victim released, hit 2.5x kb (180000, 135000); free 114; Invuln 120 |
| R8 | Kick out of range, opponent at X 9000 | tick 0 Vx 293750, ticks 1..14 Vx 300000; box on tick 15 centred X(15) + 1250 (X(15) ≈ 4990, **TBD-verify** sim run); free 114 |
| R9 | Kick range edges | dx 7999 → in range; dx 8000 → out; dy 2999 → in; dy 3000 → out |

---

## 6. Test list for qa-tester (EditMode, pure sim)

| # | Setup | Expected | Source |
|---|---|---|---|
| 1 | Each class, Ult 1000, press Ult on a free tick | Ult 0, UltsUsed 1, CinematicRealTicks 48, CinematicFighter = i | F:956-961 |
| 2 | Each class, Invuln 0 before | Invuln after tick 0: Melee/Magic 36, Heavy 84, Ranged 48, Polearm 120 | F:960, 987, 1013, 1039 |
| 3 | Ranged with Invuln 90 (respawn) presses Ult | Invuln becomes 48 (assignment lowers it) | F:1013 |
| 4 | Ult with meter 999 | nothing; the press falls through to normal handling | SIM:1132 stand-in |
| 5 | Stunned fighter with full meter presses Ult (D-U1 = A) | no ult in step 14 (documented step-15 gap) | F:417 vs SIM:1129 |
| 6 | Sword Aura, then Light on tick 1, then Heavy | aura on tick 0 (600), free tick 1, each normal strike spawns an AuraWave (spec per SIM:1580-1583) | F:966, F:490-494 |
| 7 | Sword Aura: normal strike on tick 600 vs 599 | 599 → wave, 600 → none | F:490 |
| 8 | Fire Ring | RingTicks 600 on tick 0, free tick 1; ring destroys a destructible arrow at distance 2000, not at 2200 | F:975, F:526 |
| 9 | Smash grounded | Vy 225000 on tick 0; no slam on ticks 1-9 even if grounded; slam on first grounded tick >= 10; two SmashWaves ±195000 at (X ± 600, Y + 550); free S + 18 | F:986-1007 |
| 10 | Smash wave growth | R = 600 + 14n after n updates; 1524 at n = 66; pierces (hits fighter, continues); not destroyed by projectiles/Fire Ring | F:1001-1003, projectile row 11 |
| 11 | Smash off a ledge (no ground) | slam on tick 66, free 84 | F:990 |
| 12 | Smash with an air jump on tick 20 | Vy set to AirJumpVel (actionLock does not gate jump); slam later | F:291-305 |
| 13 | Thousand Arrows | lead spawn values R6; no damage from the lead; 18 VolleyArrows at expiry; free 48 | F:1012-1032 |
| 14 | Thousand Arrows determinism | same seed + inputs → identical arrow positions/velocities, state hash equal after rollback replay | D-P1 |
| 15 | Kick in range (R7) | lerp X values exact; tick 9 Held/Holding set, hit 1.5x unblockable (against a shielding victim too) | F:1054-1068 |
| 16 | Kick hold | victim V 0, no gravity, position fixed ticks 9-49; victim inputs ignored | F:1065, F:179/190 |
| 17 | Kick rise and release | attacker Vy 180000 on tick 30; tick 50 release then 2.5x hit kb (F·180000, 135000), unblockable | F:1071-1076 |
| 18 | Kick facing | opponent behind (X -5000) → Facing -1, to = -5000 + 1300 | F:1054-1056 |
| 19 | Kick out of range (R8) | Vx per tick per R8; box on tick 15 with spec 2.0 / kb (8,6) / stun 30, blockable; free 114 | F:1042-1051 |
| 20 | Kick range edges (R9) | strict `<` both axes | F:1041 |
| 21 | Kick on a dead (HpU <= 0) opponent in range | out-of-range branch | F:1041 |
| 22 | Kick on invulnerable victim in range (D-U4 = A) | held + stun 96, no damage from either hit | F:663-668, F:1064-1068 |
| 23 | Kick victim rings out / attacker rings out during the hold | single release path: Held == Holding == false, no hit 2 | F:692-697, SIM:2321 |
| 24 | Rollback mid-Kick (save on tick 5, 20, 40) and resim | hash equal; Kick fields copied | section 4 |
| 25 | Ult during dash (stand-in fires on the dash-start tick) | Interrupt clears DashTicks | F:674 |

## 7. Not verified / TBD-verify

1. Smash slam tick and Kick out-of-range positions: pin by a sim run (as step-12 amendment 9).
2. (Resolved) actionLock grounded decel = `MoveFriction` 6250 sub/tick (SIM:28), so R8 tick 0 Vx = 293750.
3. (Resolved) sim `Interrupt` releases via `Release(s, i)` (SIM:2073); `StunF` (SIM:2077) matches F:663-668.
4. Lead expiry tick (27 vs 28) and no projectile gravity.
5. Sword Aura / Fire Ring reset on ring-out in the original.
6. Victim hitstun rule on the second Kick hit (max vs assign).
7. Exact-integer ±1 frames: Smash min frame 10 (0.15), timeout 66 (1.1), after-slam 18 (0.3); Kick dash 15 (0.25),
   lerp 9 (0.15), hold resume 30 (0.35).

---

## Technical-director review

Verdict: **APPROVE WITH AMENDMENTS** (technical-director, 2026-11-08). Spot-checked against F:170-190, F:455-503,
F:585-630, F:655-708, F:954-1079, CombatObjects.cs:27-115 (CO:) and SIM:1360, 1725-1745, 1840-1900, 2037, 2310-2330.

**Confirmed from source.** Common body and order (F:956-961); class invuln is an assignment (F:987, 1013, 1039);
Smash slam window 10..65 / timeout 66, spawn (X ± 600, Y + 550), dir -1 then +1, `actionLock = 0.3` assign; Smash
growth `r = Radius · max(1, scale.y)` with scale.y += 1.4·dt, so 600 + 14 n milli and growth before the hit test
(CO:77, CO:97); Arrows lead offset from Center with Facing on x only (F:532), vel (F·9, 12), R 0, life 0.45; Kick
range test strict on Centers, facing `>` (tie → -1), `to` from feet, lerp values R7, frame 9 order Stun → SetHeld → hit,
frame 30 vy 12, frame 50 release then hit; Sword Aura wave is in `NormalStrike` after both the shot and the MeleeBox
branch, so light, finisher and heavy (F:490); Fire Ring and Aura set no lock.

**TBD-verify resolved (section 7).**
- Item 4: projectiles have no gravity (CO:75 `pos += Vel·dt`). Lead expiry: the sim skips the spawn tick (SIM:1844) and
  decrements on ticks 1..27, so the split is on **tick 27**. Pin 27 (original 27 nominal, ±1 float). Bounds expiry also
  splits, in both (CO:108, SIM:1891).
- Item 5: Aura and Fire Ring are **not** reset by ring-out. RingOut (F:692-708) clears only parry and spin guard;
  Revive clears only HP and hitstun. The sim ring-out (SIM:2316-2328) already leaves AuraTicks / RingTicks alone. Keep it.
- Item 6: the second Kick hit uses **max** (F:616 `hitstun = max`; SIM:2037 same). Victim stun stays 96 from tick 9.
- Item 7: off-by-ones stay "exact-integer ±1"; pin the sim's integer choice by test, as step 12.

**Amendments.**
1. **Kick out-of-range box size is wrong.** MeleeBox (F:498-499) is centre Center + F·(reach/2 + 0.15), size
   (reach + 0.3, height). For reach 2.2 that is a box from the feet X to X + F·2500, height 1400 around Center. In the
   sim's `FacingBox` form: `bx 0, bw 2500`, y from Y + 675 - 700 to Y + 675 + 700. Replace `half 1100 × 700` and
   `KickBoxW 2200` with `KickBoxBx 0, KickBoxBw 2500, KickBoxH 1400`. Use the same overlap test (strict or inclusive) as
   the ported normal MeleeBox, and state which in the doc.
2. **D-U1 wording contradicts section 1.** The original's same-frame jump overwrites the ult's vy (fact 4: Smash ends
   at 13, not 15). D-U1 A must say: apply the ult's frame-0 writes, then a jump edge processed this tick keeps its vy
   (and the ult's vx write still applies). Same rule for Kick A (vy 0 → jump vy) and Kick B (V 0 → jump vy). Add a test.
3. **Kick teleport must clear sub-unit remainders.** Each lerp tick sets X and Y directly; also set `XRem = YRem = 0`
   (as ring-out does, SIM:2325), or the remainder from the previous tick leaks into the position and the R7 values drift.
4. **Kick `to` read order.** `to` and the range test read the opponent's position on the press tick. State that this is
   the opponent's position as of the attacker's slot in the fighter loop (index order, same as the grab), and test it
   once with the Polearm as fighter 0 and once as fighter 1.
5. **Smash wave pierce is hit-once.** The original sets `hasHit` on the first hit and never resets it for a Pierce
   wave (CO:89, CO:100): one hit per wave, then it passes through. Test 10 must assert exactly one hit per wave, and the
   sim's Pierce flag must keep its once-per-projectile rule.
6. **Hitstop in tests.** The Kick's first hit causes hitstop. Tests R7 and 15-17 must count in game steps
   (`MoveTick`), not in `Step` calls, or turn hitstop off in the fixture and say so.
7. **Victim ring-out mid-hold.** Original RingOut Interrupts the holder (F:695), so the Polearm's Kick stops and its
   lock goes to 0 at once (it is free that tick). Test 23 must assert the Polearm's `MoveSlot -1` and lock 0, not only
   Held/Holding false and no hit 2. SIM:2322 already does this.
8. **Housekeeping.** RulesVersion 13 → **14** (SIM:243). All new constants go in the config hash. Copy, hash and
   InitFighter-clear the four Kick fields. Remove the CS0649 pragma once `GrowPerTick` is written.

**Missing tests (add to section 6).**
- Same-tick Jump + Ult for Smash (vy = jump value) and Kick A (amendment 2).
- Kick B rollback across the lerp ticks 1..8 (save on tick 4) with nonzero XRem before the press (amendment 3).
- Kick in range with the Polearm as fighter 1 (amendment 4).
- Smash wave hits a fighter once only, and a second wave hits separately (amendment 5).
- Aura and Fire Ring survive the owner's ring-out (item 5).
- Kick victim ring-out frees the Polearm at once (amendment 7).
- Arrows split on tick 27, and on an earlier tick when the lead leaves the bounds.
- Config hash changes when any new constant changes; RulesVersion is 14.

**Decisions.**
- D-U1: agree with A, with amendment 2. Step 15 moves the call site; do not move it twice.
- D-U2: agree with A. The rows were stand-ins; no reason to keep a lock the original never had.
- D-U3: agree with A. Four ints is nothing for rollback; coupling to Grab fields is the kind of thing that breaks later.
- D-U4: agree with A. Parity, and it is deterministic either way. Note it in the balance notes so it is a known
  behaviour, not a bug report later.

No online concern beyond amendments 3 and 4. Step 14 adds no RNG draws; the only randomness stays inside D-P1.
