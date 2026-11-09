# Port step 15: gesture controls in the deterministic sim (rules v15)

Source of truth: `Fighter/Fighter.cs` (F), `CV/Pose.cs` (P). Security constraints: [security-gesture-review.md](security-gesture-review.md) section 2, rules 1-6.
Tests: `Assets/Tests/EditMode/Editor/ChibiGestureParityTests.cs`.

## 1. Model

A gesture is a **held input** in packet bits 13-14 (`Gesture` id 0-3), exactly like a held button. The input side sends
`GestureTracker.Stable` (the 0.1 s candidate filter stays on the input side as a UX filter). A gesture the
`TelemetryValidator` rejects is sent as `None` (the original ignores it at F:261, F:373, F:413). Bit 15 (PoseVerified)
is a self-reported HUD hint: **the sim never reads it** (rule 1, pinned by `PoseVerifiedBit_NeverChangesTheSimulation`).

| Original | Sim (rollback state, hashed) | Clock |
|---|---|---|
| `StableTime` (P:196, unscaled) | `GestureTicks`: consecutive real ticks the id has been held (1 on the first tick, cap 255) | real tick |
| `JustStarted` (P:194) | `LatchedPose`: onset id latched per real tick, consumed (and cleared) by the next game step (D-N1) | real → game |
| `UltWindow` / `UltWindowLeft` (F:396-433) | `UltWindowTicks` (> 0 = open), 180 | real tick |
| `ultRetryCd` (F:210, F:432) | `UltRetryTicks`, 90 | real tick |
| match inside the window (F:417) | `UltFirePending`: closes the window on the real tick, fires at the top of the next game step | real → game |
| `Prompt` / `promptTimer` (F:226-230) | `UltPrompt` (`ChibiUltPrompt`) + `UltPromptTicks` (120 Detected, 78 Failed) | real tick |
| `superJumpCd`, `poseShotCd` (F:208-209, scaled dt) | `SuperJumpCd` 180, `PoseShotCd` 240 | game step |
| `actionLock = 0.25` of the Pose Fireball | move slot 6 (`PoseSlot`), lock 15, pose tick = lock frame 0 | game step |

## 2. Order inside one real tick (mirrors F:169-197)

1. `Latch` (per fighter): gesture hold count, onset latch, button edges, held Shield; CrossArms adds Shield while no window is open.
2. `RealTickTimers`: hitstop, cinematic, then per fighter `UltWindowRealTick`: retry wait, prompt timer, window
   countdown, match (class gesture held >= 9 real ticks, alive, input enabled) → `UltFirePending`; else timeout →
   Failed + retry 90.
3. Game step (when the time accumulator allows). In `UpdateFighter`, after the timers:
   - **Ult fire** (`UltFirePending`): before every action gate, so it fires while stunned or held (F:176 runs before
     canAct at F:179). `StartMove(5)` (Interrupt, Stun 0, invuln, cinematic, class part) then the class frame-0
     velocity (`UltFireVelocity`). The rest of the tick sees that velocity (F:178 reads it after the ult started).
   - Held: an ult fired while held keeps running (coroutines are not stopped by being held) but the body stays at V 0.
     The holder keeps holding, as in the original.
   - canAct: `HandlePoses` (F:183) then `TryOpenUltWindow` (F:266: before the dash/block returns, so the Ult press
     opens the window while dashing, blocking or attacking). A lock started this tick (locking ult or Pose Fireball)
     runs the rest of HandleActions with actionLock > 0: grounded 25 u/s² decel, jump, fast-fall, dash; no block.

## 3. Poses (F:371-392)

Only when able to act and no ult window is open. An onset on cooldown is lost (JustStarted lasts one frame).
- **RaiseHands → Super Jump:** Vy 19 u/s (285000 sub), AirJumps 1, cd 3 s. Grounded is not changed (GroundCheck already ran).
  During a dash the dash velocity overrides Vy (F:268-271), so only the cooldown and air jump stick.
- **PunchForward → Pose Fireball** (needs actionLock <= 0): feet + (F·600, 775), 13 u/s, r 300, life 96, x0.8,
  kb (4, 3) u/s, stun 18, destructible; cd 4 s; 0.25 s lock. No Patience, no lastAttackTime reset (not a normal or skill).

## 4. Security rules applied (security-gesture-review.md section 2)

| Rule | How |
|---|---|
| 1 never read bit 15 | Sim never references `PoseVerified`; `Sanitized` clears it when gesture is None (SEC-002). Test with bit 15 flipped on every tick. |
| 2 gesture = held button | Edges and hold counts derived from `PrevInput` inside the sim; stability filter only on the input side. |
| 3 same gates as buttons | Ult needs the full meter and the window; poses need canAct, no window, cooldowns, actionLock; CrossArms block needs the same gates as Shield. Gesture + Ult button on the same tick fire once. |
| 4 windows are sim counters | Every counter above is rollback state and hashed. |
| 5 re-trigger interval | Ult: 9-tick hold. Poses: 3 s / 4 s cooldowns. Fuzz test flips the gesture every tick. |
| 6 reachable from buttons | Simulated-gesture keys (1/2/3, P2 8/9/0) already produce the same bits. |

## 5. Deviations from the original (accepted)

- **Pause:** the original keeps counting `ultRetryCd` while paused (unscaled time). The sim's offline pause stops
  stepping, so every timer freezes. Pause is offline only.
- **Telemetry flicker:** the original keeps `StableTime` while telemetry is invalid; the input side sends None, so the
  hold count restarts. Ultra-rare, and only stricter.
- **"Rejected: &lt;reason&gt;" prompt text** is local-only (it depends on the local camera) and is not in sim state.

## 6. Test fixture

`ChibiTestTime.ArmUltFire(s, who)` fills the meter and sets `UltFirePending`: the state right after a gesture matched.
The step-14 ultimate parity tests use it so their "tick 0 = fire tick" numbering is unchanged; the full window flow is
covered separately. Two step-14 tests changed meaning with the real flow: T05 (a stunned fighter's gesture ult now
fires and clears the stun) and T06 (a dash running at fire time is cleared; a Dash pressed on the fire tick starts after it).
