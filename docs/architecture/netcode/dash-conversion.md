# Dash Conversion: Fighter.cs dash to ChibiSimulation (integer, 60 Hz)

> **Status**: Proposed reference spec (systems-designer, 2026-11-07). No code changed.
> **Scope**: Step 5 (Dash), approved by the user. Follows `movement-conversion.md` (units, integration order, D1 = match the original's 50 Hz Box2D behaviour).
> **Rule being served**: the deterministic core must play exactly like the original.
> **Units**: 1 tick = 1/60 s; position in milli (1 u = 1000); velocity in sub-units = 1/900 milli per tick (`VelSub = 900`); `u/s × 50/3 = milli/tick`.

## 0. Ground truth about the original (verified)

| Fact | Source |
|---|---|
| `TickTimers` runs at the top of every `Update`, before `HandleActions`: `invuln -= dt; dashTimer -= dt; dashCd -= dt` | `Fighter.cs:174, 205-207` |
| `HandleActions` runs only when `canAct = InputEnabled && !IsDead && hitstun <= 0 && !held` | `Fighter.cs:179-185` |
| `wantBlock` requires `actionLock <= 0 && dashTimer <= 0 && !GuardBroken` | `Fighter.cs:261-262` |
| Dash active: `if (dashTimer > 0) { v = (dashDir*17, 0); return; }`. Returns before blocking, movement, facing, jump, fast-fall, dash start and attacks | `Fighter.cs:268-272` |
| Block active: `return` before movement, jump, fast-fall, **dash start** and attacks | `Fighter.cs:274-278` |
| Dash start is **outside** the `actionLock <= 0` gate, after jump and fast-fall, before attacks | `Fighter.cs:309-316` |
| Start: `dashDir = |mx| > 0.1 ? sign(mx) : Facing; dashTimer = 0.13; dashCd = 0.55; invuln = max(invuln, 0.1)` | `Fighter.cs:311-314` |
| `Controls.Dash` is a press edge (`wasPressedThisFrame`) | `FighterInput.cs:63, 78, 97` |
| Velocity reaches Box2D only via `rb.linearVelocity = v` at the end of `Update` | `Fighter.cs:190` |
| `Interrupt()` clears `dashTimer` (and actionLock, blocking, coroutines, grab). Called from `ApplyDamage` **only when stun > 0**, `Stun()`, `RingOut()` | `Fighter.cs:614-618, 663-677, 695` |
| `ReceiveHit` misses if `invuln > 0` | `Fighter.cs:547` |
| Dash speed is the literal 17. `MoveSpeed` (Hex, Tailwind, Juggernaut) is not used | `Fighter.cs:135-143, 270` |
| `RingOut` does not touch `dashCd` | `Fighter.cs:692-708` |
| Frame rate: `Application.targetFrameRate = 120`, but the default quality level (Ultra, index 5) has `vSyncCount: 1`, which makes Unity ignore `targetFrameRate`. Real rate = display refresh | `GameManager.cs:46`, `QualitySettings.asset:7, 297` |

**Key timing fact.** The dash velocity is first written on the frame *after* the press (on the press frame the active check at 268 runs before the start code at 309). It is written on every frame where `dashTimer` is still > 0 after the decrement. At 60 fps: `0.13 − k/60 > 0` ⇔ k ≤ 7, so frames 1..7. The velocity is held from frame 1's `Update` to frame 8's `Update` = **7/60 s**, not 0.13 s.

## 1. Rules table

| # | Quantity | Original (source) | Sim value | Exact fraction / rounding |
|---|---|---|---|---|
| 1 | Dash speed | 17 u/s (`:270`) | `DashSpeedSub = 255000` (283.333 milli/tick) | 17 × 50/3 = 850/3 milli → ×900 = 255000, exact |
| 2 | Dash duration | 0.13 s timer (`:312`) | `DashTicks = 8` at start; motion on ticks where `DashTicks > 0` after `Dec` → **ticks 1..7** (7 motion ticks) | ceil(0.13 × 60) = ceil(7.8) = 8. Reproduces the 60 fps original exactly (motion frames 1..7) |
| 3 | Cooldown | 0.55 s (`:313`) | `DashCd = 33`; start allowed when `DashCd == 0` after `Dec` → first allowed at tick 33 | 0.55 × 60 = 33.0 exact. Frame-rate independent (66 frames at 120 fps = same 0.55 s) |
| 4 | Invulnerability | max(invuln, 0.1 s) (`:314`) | `Invuln = max(Invuln, 6)` | 0.1 × 60 = 6.0 exact. Immune on ticks 0..5, hittable from tick 6 |
| 5 | Vertical velocity | forced 0 every dash frame (`:270`) | `Vy = 0` on each motion tick | exact |
| 6 | Air sag per motion tick | Box2D applies gravity inside each 50 Hz step, starting from vy = 0 every time: drop per step = g·dt50² = 35.316 × 0.0004 = 0.0141264 u. 50 steps/s → 706.32 milli/s | **11.772 milli/tick = 10595 sub** (= Gravity 8829 + 2 × ArcC50 883 = 10595) | 706.32/60 = 11.772 → ×900 = 10594.8 → 10595 (only rounded value, −0.2 sub/tick) |
| 7 | Vy left after the dash | at 60 fps, a 50 Hz step falls in the last frame with probability 5/6, leaving vy = −0.706 u/s; mean = −0.5886 u/s | `Vy = −8829` sub (−9.81 milli/tick) after the last motion tick | exactly what the normal `Vy -= Gravity` gives. No special code |
| 8 | Direction | sign(MoveX) if |MoveX| > 0.1, else Facing (`:311`) | `DashDir = input.MoveX != 0 ? input.MoveX : Facing` | MoveX is digital (−1/0/1) in the packet |
| 9 | Speed modifiers | none | none: Hex, Tailwind, Juggernaut, Sluggish do not scale the dash | |
| 10 | Exit speed | none. `v.x` stays 17 u/s on the first post-dash frame, then normal MoveTowards (70 / 40 u/s², or 25 u/s² grounded in actionLock, or **no decel airborne in actionLock**) | `Vx` stays 255000 and the normal branch decelerates it | there is no "dash end" write in the original |

**Which reference quantity is preserved.** Distance per dash = dash speed × time the velocity is held, integrated by the 50 Hz Box2D steps that fall in that window. At 60 fps the window holds 5 or 6 steps depending on phase (1700 or 2040 milli). The mean is 17 × 7/60 = **1983.33 milli**. The 60 Hz sim integrates 7 × 283.333 = 1983.33 milli exactly: it matches the mean with no rounding. The "0.13 × 17 = 2.21 u" figure is only reached as fps → ∞ and is never the original's behaviour. The number of 50 Hz steps is the wrong target, because it depends on phase.

**Gravity during the dash (answer).** Yes. `Update` zeroes vy, but gravity is applied inside every Box2D step, so each step during the dash moves the fighter down by 14.126 milli. On the ground the floor contact cancels this, so there is no drop. In the air the expected drop over a 60 fps dash is 5.833 steps × 14.1264 = **82.40 milli** (5 steps 70.6, 6 steps 84.8). With plain D1 Physics the sim would drop 7 × (8829 + 883) / 900 = 75.5 milli (−8.4%). That is why row 6 uses the doubled arc term on motion ticks: 7 × 10595 / 900 = **82.41 milli**.

### Decision D2 (for the user): which original frame rate is the reference

vSync is on at the default quality level, so the original runs at the monitor's refresh rate. This cannot be verified from code (section 7).

| Option | Model | Ground dash | Air sag | Post-dash Vy | Cost |
|---|---|---|---|---|---|
| **A (Recommended)**: 60 Hz display | motion ticks 1..7 | **1983.3 milli** | 82.41 milli | −8829 | none: integer exact, the sim's native rate |
| B: 120 Hz display | motion ticks 1..7, plus the start tick integrates the average of normal and dash velocity (`Vx = (Vx + dashVx)/2`, `Vy = Vy/2` on tick 0 only) | 2125.0 milli (7.5 × 283.33) | 88.3 milli | −4415 | one special case on the start tick |
| C: 8 full ticks | motion ticks 1..8 | 2266.7 milli | 94.2 milli | −8829 | +14% vs 60 Hz, matches no real frame rate |

The rest of this document uses **A**.

## 2. Edge cases (the original's answer → sim rule)

| Case | Original | Sim rule |
|---|---|---|
| Dash during an attack (startup, active or recovery: actionLock > 0) | **Allowed.** Dash start sits outside the actionLock gate (`:309`). Attacks do not reduce the cooldown | Dash start is tried in both the free branch and the move branch |
| Does the attack still strike? | **Yes.** No `Interrupt`, so the attack coroutine keeps running and keeps hit-checking. A coroutine that writes velocity every frame (Dash & Thrust `:760`, others at `:828, 849, 1047`) runs **after** `Update`, so it overrides the dash velocity. Normals do not write velocity, so the dash moves the fighter while the swing continues | Dash motion is written first. `AdvanceMove` still advances the timeline and arms hits. Its SelfVx/SelfVy writes override the dash on the ticks where they apply. Its MoveFriction branch is skipped while dashing |
| After a dash inside an attack | v.x = 17 u/s, then 25 u/s² decel if grounded; **airborne: no decel at all** until actionLock ends (`:286-289`) | Falls out of the existing AdvanceMove rules (MoveFriction 6250 grounded, nothing airborne) |
| During hitstun / stun | **No.** `HandleActions` is not called (`:179`). The press is lost, not buffered. Timers keep ticking | Dash start only in the non-stun branches. `DashCd` and `DashTicks` decrement unconditionally |
| While grabbed (`held`) | No (canAct false) | n/a until the grab is in the sim; same gate as stun |
| While shielding | **Cannot start.** Blocking `return`s (`:277`) before the dash start. Exception: guard broken, or actionLock > 0, makes `wantBlock` false, so the dash is allowed. The press is not consumed and the cooldown is not spent | In `FreeActions`, the dash start goes after the shield `return` |
| Shield held during a dash | Not blocking while `dashTimer > 0` (`:262`). When the dash ends with shield still held, blocking starts on that frame with a **fresh `blockStart`**, which opens a new perfect-parry window | `Shielding = false; ShieldTick = 0` on motion ticks. The next free tick starts `ShieldTick` at 0 |
| Air dash limit | **Unlimited.** Only the 0.55 s cooldown gates it. No per-jump counter, and `airJumps` is not touched. Repeated air dashes stall the fall | No air counter |
| Dash + jump on the same tick | Jump first (`:291`), then dash start. The next frame's dash sets vy = 0, so the jump survives 1 tick only | Same order: JumpAndFastFall, then TryDashStart |
| Dash + attack on the same tick | Both start: attack selection (`:318`) runs after the dash start | TryDashStart before the attack priority block |
| Attack / jump pressed during the dash | **Lost**: HandleActions returns at `:271`, and inputs are press edges | The sim's `BufBtn` (InputBufferTicks) would fire them after the dash. That is a deviation from the original, owned by the combat conversion. Flagged, not decided here |
| Dash pressed during dash / cooldown | Ignored, not buffered | Edge only. **Exclude `Dash` from `ActionMask`** |
| Facing | Free state: the movement block (`:284`) runs before the dash start, so dashing backwards **turns** the fighter. During an attack facing is not updated, so a back-dash keeps facing (a true backdash). Facing is frozen during the dash (`:271` returns before `:284`) | Free branch: facing is already set at `ChibiSimulation.cs:477` before TryDashStart. Move branch: unchanged. Motion ticks: no facing write |
| Hit during the dash | Invuln covers frames 0..5, so only hits on motion ticks 6 and 7 can land. A hit with stun > 0 → `Interrupt` → dash ends, knockback replaces velocity. A hit with stun == 0 does **not** cancel it: the next frame's dash write overwrites the knockback | In `ApplyHit`: `if (stun > 0) D.DashTicks = 0`. The same goes for the parry reflect (stun 0.5 s) and the perfect-parry attacker stun |
| Invuln stacking | `max`, never shortens a longer invuln (respawn 1.5 s, ult) | `Invuln = Math.Max(Invuln, 6)` |
| Stage edge / ring-out | No edge check. The dash carries the fighter off the stage horizontally with only the 82-milli sag. Ring-out → `Interrupt` clears the dash. `dashCd` is kept | Physics unchanged (off-stage = no landing). In the respawn block of `ResolveBoundsAndMatchEnd`: `DashTicks = 0`. Do **not** reset `DashCd` |
| Hitstop | `Match.HitStop` scales time, freezing dt-based timers and physics together | Out of scope here. Whatever the sim does for hitstop must freeze `DashTicks`/`DashCd` together with motion |

## 3. State, rollback, and tick order

**New `ChibiFighterSim` fields (rollback state):** `int DashTicks` (0..8), `int DashCd` (0..33), `int DashDir` (−1/0/+1). Add them to `CopyFrom`, append them to `Hash` (after the existing fields, so hash order stays defined), and zero them in `InitFighter`. Bump the sim schema/version note at `ChibiSimulation.cs:56` (next entry: "6: dash"). Update the `ChibiInputPacket.cs:21` doc comment ("not yet consumed").

**Constants (`ChibiSimConst`):** `DashSpeedSub = 255000`, `DashDuration = 8`, `DashCooldown = 33`, `DashInvuln = 6`, `DashSagSub = 10595` (or express it as `Gravity + 2*ArcC50`).

**`UpdateFighter` order:**

1. Timers: `Dec(DashTicks); Dec(DashCd);` alongside `Stun` and `Invuln`.
2. `bool dashing = f.Stun == 0 && f.DashTicks > 0;`
3. Branch:
   - `Stun > 0`: unchanged (no dash start, no motion).
   - **`dashing`** (new, checked before the move/free split), *dash motion*: `Vx = DashDir * DashSpeedSub; Vy = 0; Shielding = false; ShieldTick = 0;`. If `MoveSlot >= 0`, call `AdvanceMove` in a dashing mode: timeline + hits + SelfVx/SelfVy overrides, no MoveFriction. No jump, fast-fall, facing, shield, attacks or dash start.
   - `MoveSlot >= 0`: `AdvanceMove` → `JumpAndFastFall` → **TryDashStart**.
   - free: `FreeActions` with **TryDashStart** inserted after `JumpAndFastFall` (`ChibiSimulation.cs:478`) and before the attack priority block (`:481`). It stays after the shield `return`.
4. `Physics(f)`: if `dashing` and airborne, integrate Y with `Vy − 2*ArcC50` (Vy is −8829 after gravity → −10595 total). Otherwise unchanged.

**TryDashStart** (`edges & Dash` and `DashCd == 0`): `DashDir = MoveX != 0 ? MoveX : Facing; DashTicks = 8; DashCd = 33; Invuln = max(Invuln, 6);`. The start tick integrates the **normal** velocity (original frame 0).

**Dash end**: implicit. The first tick with `DashTicks == 0` after `Dec` runs the normal branch with `Vx = ±255000` and `Vy = −8829` inherited.

**Cancel**: `ApplyHit` (stun > 0, including the parry-reflect and perfect-parry attacker paths) and ring-out respawn set `DashTicks = 0`.

## 4. Dash & Thrust end speed

Correction to the brief: Dash & Thrust (Melee skill 1, `Fighter.cs:753-766`) is a separate coroutine. It does **not** use the 13-tick dash. It writes `(Facing·18, 0)` u/s every frame for `t < 0.22 s`, then `(Facing·3, 0)` once. actionLock is 0.4 s.

- Original timing: the first loop iteration runs inside `UseSkill` during `Update` and is overwritten by `rb.linearVelocity = v` at `:190`. So 18 u/s is held from frame 1 to the exit frame: (ceil(0.22·fps) − 1)/fps = **13/60 s at both 60 and 120 fps** → 3900 milli (at 50 Hz: 10 or 11 steps = 3600 or 3960).
- Sim: SelfVx 300 milli/tick (270000 sub) on move ticks 1..13 = 3900 milli. Tick 0 keeps normal velocity.
- **End speed**: 3 u/s = **50 milli/tick = 45000 sub**, `Vy = 0`, written **once on move tick 14** (the first tick after the last 18 u/s tick, which is `t == activeEnd` if the row is laid out as above). It replaces that tick's MoveFriction. From tick 15, grounded MoveFriction 6250/tick (airborne: no decel until the move ends).
- Airborne Dash & Thrust has the same forced-vy=0 sag as the dash: use the `2*ArcC50` term on ticks 1..14.
- Verify that the existing row at `ChibiSimulation.cs:132` produces exactly this layout. Not checked here.

## 5. Reference test values (model A)

Tolerance basis: sim results are integer-exact against the formulas below. The original varies by phase (one 50 Hz step = 340 milli at dash speed), so the sim is compared to the original's **mean**.

| Test | Setup | Expected | Tolerance |
|---|---|---|---|
| Ground dash distance (motion only) | grounded, idle, Dash edge at tick 0 | X(7) − X(0) = 7 × 255000 / 900 = **1983.33 → 1983 milli** | ±1 milli (remainder) |
| Ground dash + slide, no input | same, read until Vx = 0 | slide Σ_{k=1..14}(255000 − 17500k) = 1732500 sub = 1925 milli. **Total 3908 milli**, at rest on tick 22 | ±1 milli, exact tick |
| Air dash distance + slide, no input | airborne, Vy ≈ 0 | dash 1983 milli; slide Σ_{k=1..25}(255000 − 10000k) = **3472 milli** | ±1 milli |
| Air dash height change | start at Vy = 0, high above the floor | Y(7) − Y(0) = −7 × 10595 / 900 = **−82.4 milli**. Original mean −82.40 (5/6 steps: −70.6 / −84.8). Vy after tick 7 = −8829 | ±1 milli |
| Ground dash height | grounded | 0 change, Grounded stays true | exact |
| Direction | MoveX = −1, Facing = +1, free state | DashDir = −1, Facing becomes −1 on tick 0 | exact |
| Direction, neutral | MoveX = 0, Facing = −1 | DashDir = −1 | exact |
| Backdash during attack | Light started, then Dash with MoveX opposite Facing | DashDir opposite, Facing unchanged | exact |
| Cooldown | Dash at tick 0, Dash edges at 32 and 33 | tick 32 blocked, **tick 33 starts** | exact (original at 60 fps with float accumulation: 33, possibly 34) |
| Invuln window | opponent hit overlapping on tick k after the dash start | k = 0..5 → miss; **k = 6 → lands** | exact |
| Shield during dash | Shield held from tick 1 | Shielding false on ticks 1..7; tick 8 Shielding true with ShieldTick 0 (fresh parry window) | exact |
| Shield + Dash same tick (free) | Shield held, Dash edge | no dash, DashCd stays 0 | exact |
| Hit during dash | stun > 0 hit lands on tick 6 | DashTicks → 0; displacement before the hit = 6 × 283.33 = **1700 milli** (tick 6 integrates before hits resolve); tick 7 uses knockback | ±1 milli |
| Stun-0 hit during dash | hit with stun 0 on tick 6 | dash continues; tick 7 Vx = DashSpeed again | exact |
| Air dash unlimited | airborne, Dash at ticks 0 and 33 | both start | exact |
| Dash inside grounded normal | Light at t0, Dash on the same tick | the swing still arms hits on schedule; after tick 7, Vx decays by 6250/tick while the move lasts | exact |
| Dash & Thrust total slide | grounded Melee skill 1, no input | 3900 (ticks 1..13) + 50 (tick 14) + Σ_{k=1..7}(45000 − 6250k)/900 = 155.6 → **4105.6 milli** | ±1 milli vs formula; the original mean is the same, with 50 Hz phase spread ±360 milli |
| Speed modifiers | Hexed / Tailwind / Juggernaut | dash distance unchanged, 1983 milli | exact |

## 6. Behaviour differences to watch

1. The sim's input buffer would replay attacks pressed during a dash. The original drops them (section 2). Decide this in the combat conversion.
2. Off-stage dashes keep the fighter level for 7 ticks. This is intended (original), but it extends recovery reach.
3. The post-dash momentum (17 u/s carried into normal deceleration) is the largest part of the dash's total travel (1925 of 3908 milli on the ground). Do not add a dash-end velocity reset.

## 7. Not verifiable from code

- The original's real frame rate (vSync on at the default quality level, so it depends on the display). This drives decision D2.
- Float accumulation of `0.55f`/`0.1f` minus repeated `deltaTime`: the boundary frame could be off by 1.
- Unity's script execution order between the two fighters (whether a tick-0 hit lands before the dasher's `Update`). The sim resolves hits after both updates, so tick 0 is always protected.
- `HandlePoses` (`:183`) and `TryOpenUltWindow` (`:266`) run before the dash-active return, so pose and ult-window logic can still run during a dash. Not covered here.
- Hitstop interaction (time scale) in the sim.
