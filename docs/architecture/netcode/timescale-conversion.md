# Time-scale conversion: hitstop, ult cinematic, finish slow-mo (step 10)

> **Scope**: Step 10, approved by the user. Port the original `Time.timeScale` effects (MatchController.cs, MC:)
> onto the deterministic sim (`Netcode/ChibiSimulation.cs`, SIM:).
> **Conventions**: 1 real tick = 1/60 s of wall time (ChibiTickClock, `unscaledDeltaTime`); D2 = the original
> is referenced at 60 fps; D1 = 50 Hz Box2D arcs reproduced by 60 Hz ticks; D-N1 = A (no input buffer).
> **New term**: a *game step* = one run of the existing v9 `Step` body (1/60 s of *game* time). In v10 a real
> tick runs 0 or 1 game steps.
> **Rule**: play exactly like the original as far as a fixed-tick deterministic sim allows.

---

## 1. Slow-mo sources in the original

`MC:108-116` recomputes the scale every rendered frame:
`scale = Paused ? 0 : min(hitstop ? 0.05 : 1, cinematic ? 0.2 : 1, finishedWithin1.2s ? 0.3 : 1)`.
Both "until" times are on **unscaled** time.

| # | Source | Trigger (call site) | Factor | Real duration | Real ticks `N = ceil(d·60)` | When |
|---|--------|---------------------|--------|---------------|------------------------------|------|
| H1 | Normal hit | `ApplyDamage` F:625 `HitStop(0.045)` | 0.05 | 0.045 s | **3** (2.7) | in match |
| H2 | Critical hit | F:625 `HitStop(0.08)` | 0.05 | 0.08 s | **5** (4.8) | in match |
| H3 | Perfect parry | F:570 `HitStop(0.1)` | 0.05 | 0.1 s | **6** (6.0, float edge, see 7) | in match |
| H4 | Parry-skill reflect | F:559 `HitStop(0.12)` (the reflect's ApplyDamage 0.045 is absorbed by max) | 0.05 | 0.12 s | **8** (7.2) | in match |
| C | Ult cinematic | `FireUltimate` F:961 → `Cinematic(f)` MC:184-193 | 0.2 | 0.8 s | **48** | in match |
| F | Finish slow-mo | `End()` MC:168-175 sets `FinishedTime` | 0.3 | 1.2 s | **72** | **after** `Finished` |
| P | Pause (Esc) | MC:106 | 0 | until unpaused | n/a | offline only |

**Stacking rules (original, kept exactly):**
- Hitstop: `hitstopUntil = max(hitstopUntil, now + d)` (MC:177) → in ticks `HitstopReal = max(HitstopReal, N)`.
- Cinematic: `cinematicUntil = now + 0.8` (assignment, MC:188). Because `now` only grows this equals a max; a second
  ult restarts the 48-tick window.
- Hitstop and cinematic count down **independently in real time**; the active factor is the **minimum** (0.05 beats
  0.2). A crit during a cinematic does not extend the cinematic.
- Finish (0.3) only exists after `Finished`; nothing gameplay-relevant runs then (MC:121 returns, `InputEnabled` is
  false, winner already fixed). **Finish slow-mo is view-only.**

**Tick count convention.** The hit happens inside frame *k* (full-speed). `Time.timeScale` set in frame *j* scales
frame *j+1*'s `deltaTime`. With MatchController's Update running after the hit in frame *k* (or the hit coming from a
physics callback before Update) the slowed frames are *k+1 .. k+N* with `N = #{ j ≥ 0 : j/60 < d } = ceil(d·60)`.
If MatchController's Update ran before the fighter's in frame *k*, the original gets `N-1`. Script execution order is
not pinned in the project, so the original is **N or N-1**; we use **N** (section 7).

---

## 2. What the original actually does during slow-mo (60 fps)

- `Update` (MatchController, Fighter, Controls) still runs **every rendered frame**. Press edges
  (`wasPressedThisFrame`) are read every frame, so an action can **start** on any real frame.
- Gameplay timers use `Time.deltaTime = scale/60`: `hitstun, actionLock, invuln, dashTimer, dashCd, superJumpCd,
  poseShotCd, SkillCd, comboTimer, dropTimer`, guard regen, `TimeLeft` (MC:135), posture sampling. Coroutine
  `WaitForSeconds` (F:470, 784, 872, 931, 1070, 1073) uses scaled time. `Time.time`-based windows (`parryUntil`,
  `spinGuardUntil`, `swordAuraUntil`, `tailwind*`) are scaled time too.
- **Unscaled** timers (do NOT slow down): `ultRetryCd` (F:210), `promptTimer` (F:228), `UltWindowLeft` (F:406-411,
  frozen only by pause), gesture tracker (F:172), camera focus (Stage.cs:152-161), flash fade (MC:119), HUD
  cinematic overlay 1.5 s (Hud.cs:18-19), results after 1.3 s (Hud.cs:62).
- Box2D: `FixedUpdate` steps every 0.02 s of **scaled** time. Real-time interval between physics steps =
  0.02/scale: **0.4 s at 0.05**, **0.1 s at 0.2**, 0.0667 s at 0.3. Positions only change on those steps; a velocity
  written in `Update` simply persists until the next step.

**Game time that passes inside each source** (game ticks = game seconds × 60; Box2D steps = game seconds / 0.02):

| Source | Real ticks | Game time | Game ticks | Box2D steps inside (expected) | P(at least one Box2D step) |
|--------|-----------|-----------|------------|-------------------------------|----------------------------|
| H1 0.045 | 3 | 2.5 ms | 0.15 | 0.125 | 12.5 % |
| H2 0.08 | 5 | 4.17 ms | 0.25 | 0.208 | 20.8 % |
| H3 0.1 | 6 | 5.0 ms | 0.30 | 0.25 | 25 % |
| H4 0.12 | 8 | 6.67 ms | 0.40 | 0.333 | 33.3 % |
| C 0.8 | 48 | 160 ms | **9.6** | 8 (exactly 8, phase-dependent ±0) | 100 % |
| F 1.2 | 72 | 360 ms | 21.6 | 18 | post-match |

(The P column assumes the Box2D accumulator phase is uniform; at most one step can occur inside any hitstop because
0.4 game ticks < one 0.02 s step.) So during a hitstop the victim is frozen in place in ≥ 66 % of cases and moves
at most one 20 ms physics step; timers move by < 0.5 tick. During a cinematic the world runs at 1/5 speed with
8 visible position updates over 0.8 s.

---

## 3. Modelling options

All three keep **one input packet per real tick** and `s.Frame` = real tick index, so rollback, input delay, the
P2P protocol and replays are unchanged. All put the slow-mo counters inside `ChibiSimState` (rolled back and hashed).

### Option A - Game-step accumulator ("freeze ticks")
Each real tick: `TimeAccum += ScalePermille`; if `TimeAccum ≥ 1000` run one game step and subtract 1000.
Hitstop 50, cinematic 200, normal 1000.

| Formula | `steps(n) = floor((A0 + Σ scale_i) / 1000)` over n real ticks |
|---|---|

| Symbol | Type | Range | Description |
|---|---|---|---|
| `A0` | int | 0-999 | `TimeAccum` before the slow-mo window |
| `scale_i` | int | {50, 200, 1000} | permille factor of real tick i |
| `n` | int | 0-48 | real ticks in the window |
| `steps` | int | 0-10 | game steps run inside the window |

Output bounded: invariant `0 ≤ TimeAccum < 1000` between ticks, at most 1 game step per real tick (max input 999+1000).
Worked example: cinematic from `A0 = 0`: 48 × 200 = 9600 → 9 steps (real ticks 5, 10, …, 45), carry 600.

- **Inputs on skipped real ticks**:
  - **A-lost**: a press edge on a real tick with no game step is discarded. A free fighter pressing during a cinematic
    loses 4 of every 5 presses. Clearly unlike the original. Rejected.
  - **A-held (recommended)**: per fighter, `LatchedEdges |= raw & ~PrevRaw` every real tick; the next game step uses
    `edges = LatchedEdges` and `raw = current raw` (level inputs such as block/hold-direction are the latest), then
    clears the latch. This is **not** an input buffer (D-N1 still holds): if the fighter is busy at that game step
    the latched edge is lost exactly as in v9. At scale 1000 latch and consume happen on the same tick, so v10 is
    bit-identical in behaviour to v9 whenever no slow-mo is active.
- **Exactness**: game time is conserved exactly (sum of permille), quantised to whole game steps. Every gameplay
  event happens at the same *game* time as the original within ±1 game tick (≤ 1/scale real ticks: ≤ 5 real
  ticks in a cinematic, ≤ 20 in a hitstop). Hitstops become a true freeze (0 game steps unless the carried
  accumulator tips over), versus the original's 0.15-0.4 tick crawl.
- **Scope**: small. A wrapper around the existing body, two state counters, `TimeAccum`, `GameTicks`, per-fighter
  `LatchedEdges`, the edge computation moved out of `UpdateFighter`, 4 hitstop call sites + 1 cinematic call site,
  `MatchTicks` and `SpawnFrame` compared against `GameTicks`.
- **Rollback**: state-only change, no protocol change. A mispredicted remote hit now also mispredicts a
  hitstop; a rollback corrects it (the view may see a 3-8 tick freeze appear/disappear; acceptable, same class
  as any mispredicted hit).

### Option B - Two-layer step
Every real tick runs the "input/action start" layer (press edges, `FreeActions` choosing and starting a move, dash,
jump impulse); timers, move progression, hit probes and physics run only on accumulator game steps.
- **Exactness**: actions start on the same real frame as the original (closest for a free opponent during a
  cinematic). Game-time error of the start is < 1 game tick, the same bound as A-held; the gain is only in *real*
  time (≤ 4 real ticks in a cinematic).
- **Scope**: large. `UpdateFighter` interleaves timer decrements, stun/dash gating, `FreeActions` and velocity
  writes in one pass (SIM:555-835); it must be split into two phases and every move-start side effect (velocity
  set, invuln, lock, projectile spawn on tick 0, jump impulse with the C50 arc term) re-checked for running outside
  a game step. A jump started on a non-step tick, for example, has no physics step to consume its `level` flag.
- **Rollback**: same state shape as A plus split-phase invariants; higher regression risk across steps 1-9.

### Option C - Fractional time everywhere
Every timer becomes permille sub-ticks (decrement by `scale`), every velocity integration multiplies by
`scale/1000` with remainder carry, gravity/arc terms (D1 C50) scaled, every 60 Hz constant from steps 1-9
re-expressed.
- **Exactness**: highest. Matches the original's crawl (0.15 tick of timers in H1) but still not Box2D's 50 Hz
  quantisation at 0.05, so not truly exact either.
- **Scope**: very large: every conversion doc (movement, dash, normals, damage, stage) changes units; rounding
  analysis redone; RNG/hit ordering untouched but every tick-count acceptance test rewritten.
- **Rollback**: fine in principle, much larger state/hash diff, highest desync-by-rounding risk.

### Recommendation: **Option A-held**
1. Its error bound (±1 game tick on any event, measured in game time) equals the ±1-frame tolerance already
   accepted in steps 1-9 for the original's float accumulation; B and C buy only real-time differences of a few
   frames inside an effect whose purpose is to *pause* the action.
2. Hitstop as a clean freeze is what the original does in 67-88 % of hits (no Box2D step), and the crawl it skips
   is < 0.5 tick.
3. Zero behaviour change outside slow-mo (regression-safe for all approved steps), smallest diff, no protocol change.

---

## 4. Match timer, KO / finish, pause

- **Match timer**: the original counts scaled time (MC:135), so a cinematic consumes 9.6 game ticks of the
  180 s clock over 48 real ticks and a hitstop 0.15-0.4. v10: the timer counts **game steps**: `s.GameTicks`;
  time-out when `GameTicks + 1 ≥ MatchTicks` (replaces `s.Frame + 1` at SIM:1189). HUD seconds =
  `(MatchTicks - GameTicks) / 60`. A match therefore lasts `MatchTicks` game steps and slightly more real ticks.
- **KO / time-out**: decided inside the game step as today. The game step that ends the match completes;
  `MatchOver` then makes `Step` return early (`Frame++` only). Hitstop/cinematic counters are irrelevant after
  that and are not decremented (frozen; harmless, they are hashed but constant).
- **Finish slow-mo**: view-only. On the first real tick where `MatchOver` is observed, the view runs 72 real ticks of
  "0.3" presentation (VFX/animation dt × 0.3, KO text, shake) and shows results after 78 real ticks (1.3 s,
  Hud.cs:62). The original keeps Box2D running post-match (fighters drift/fall at 0.3); the sim freezes positions.
  If that drift is wanted, the view may extrapolate `X + Vx·t` with gravity for the presentation only. Not hashed.
- **Pause**: not allowed online (feature-inventory row 207). Offline pause is the local loop not calling `Step`
  (outside the sim, out of scope). Because the sim has no other time source, everything (including unscaled timers)
  freezes during pause, which matches F:406.

---

## 5. Code contract (RulesVersion 10)

**Constants** (`ChibiSimConst`, data-driven tuning knobs):

| Name | Value | Source |
|---|---|---|
| `ScaleHitstop` | 50 | MC:112 |
| `ScaleCinematic` | 200 | MC:113 |
| `ScaleNormal` | 1000 | |
| `HitstopNormal` | 3 | ceil(0.045·60) F:625 |
| `HitstopCrit` | 5 | ceil(0.08·60) F:625 |
| `HitstopParry` | 6 | ceil(0.1·60) F:570 |
| `HitstopReflect` | 8 | ceil(0.12·60) F:559 |
| `CinematicTicks` | 48 | 0.8·60 MC:188 |
| `FinishSlowTicks` / `FinishScale` | 72 / 300 | MC:114, view only, not in config hash |
| `RulesVersion` | **10** | SIM:112 |

**State** (all rolled back; all mixed into the state hash in this order after `Winner`/`Stage`):
- `ChibiSimState.HitstopRealTicks` (int ≥ 0), `CinematicRealTicks` (int ≥ 0), `CinematicFighter` (int, -1 none),
  `TimeAccum` (int 0-999), `GameTicks` (int).
- `ChibiFighterSim.LatchedEdges` (int bitmask); `PrevInput` keeps its meaning (last *real* tick's raw).
- Config hash: mix the 7 gameplay constants above.

**Step order** (`Step(s, in0, in1)`):
```
if (s.MatchOver) { s.Frame++; return; }
for i in 0,1: raw = in_i.Sanitized().Raw
              f.LatchedEdges |= raw & ~f.PrevInput; f.PrevInput = raw; f.CurRaw = raw (local, not state)
scale = HitstopRealTicks > 0 ? 50 : (CinematicRealTicks > 0 ? 200 : 1000)   // min of active factors
Dec(HitstopRealTicks); Dec(CinematicRealTicks); if (CinematicRealTicks == 0) CinematicFighter = -1
// future unscaled timers (ult window, ultRetryCd, prompt) decrement HERE, every real tick
TimeAccum += scale
if (TimeAccum >= 1000) { TimeAccum -= 1000; GameStep(s, raw0, edges0, raw1, edges1); GameTicks++ }
s.Frame++
```
`GameStep` = the v9 body (UpdateFighter ×2, probes, ApplyHit, rings, projectiles, ResolveBoundsAndMatchEnd) with
`UpdateFighter` taking `raw` and `edges = f.LatchedEdges` (then `f.LatchedEdges = 0`) instead of computing
`raw & ~PrevInput`. `SpawnFrame` stores/compares `GameTicks`, not `Frame`.

**Triggers** (inside the game step):
- `ApplyHit` damage path (the sim's equivalent of every `ApplyDamage` call, incl. projectiles, fire rings, aura
  wave): `HitstopRealTicks = max(HitstopRealTicks, crit ? 5 : 3)`.
- Perfect parry branch: `max(…, 6)`. Reflect branch: `max(…, 8)`.
- Ult fire (SIM:~744): `CinematicRealTicks = 48; CinematicFighter = i`.
- A hit set on game step at real tick k slows real ticks k+1..k+N (scale is read before the decrement and before
  this tick's game step).

**View** (`ChibiSimView`): read `ScalePermille` (same expression, from the latest state) each render frame.
- Render interpolation: during slow-mo the state only changes on game steps; render positions as
  `X + Vx/900 · f` milli with `f = (TimeAccum + RenderAlpha·scale) / 1000` (velocity extrapolation, view-only,
  clamped to `f < 1`) so motion stays smooth at 1/5 speed instead of 12 Hz steps. At scale 50 this is ≤ 0.05 tick
  of motion (looks frozen, as the original).
- VFX/animator dt × scale/1000; flash fade, camera focus (0.8 s on `CinematicFighter`), HUD overlay (1.5 s from the
  real tick `CinematicRealTicks` became 48) use unscaled real time.
- Remove the `Time.timeScale = 1` save/restore hack (SimView:314-370) only once MatchController no longer drives
  the sim scene.

---

## 6. Reference test values (QA)

All start from `TimeAccum = 0` (true after any run of normal ticks with no prior slow-mo).

| # | Scenario | Expected (v10) | Tolerance vs original |
|---|----------|----------------|-----------------------|
| T1 | Normal hit lands on game step at real tick k | `HitstopRealTicks = 3`; ticks k+1..k+3 scale 50; **0** game steps; `TimeAccum = 150` after k+3; next game step at k+4 | original 2-3 slowed frames, 0.10-0.15 game ticks |
| T2 | Crit | 5 real ticks, 0 steps, accum 250 | original 4-5 frames, 0.25 ticks |
| T3 | Perfect parry | 6 real ticks, 0 steps, accum 300 | original 5-6 (float edge at exactly 0.1) |
| T4 | Reflect | 8 real ticks, 0 steps, accum 400 | original 7-8 |
| T5 | Ult fired at real tick c | `CinematicRealTicks = 48`; game steps at c+5, c+10, …, c+45 → **9 steps**, accum 600 after c+48 | original 9.6 game ticks; 9 or 10 with carry |
| T6 | Match timer across T1 / T5 | `GameTicks` +0 over the hitstop; +9 over the cinematic (+10 if `A0 ≥ 400`) | original 0.15 / 9.6 ticks of `TimeLeft` |
| T7 | Victim with kb `Vx` across a normal hit | X, Y, Vx, Vy, HpU, Stun identical on ticks k..k+3 (HP and Stun already applied at k); trajectory resumes at k+4 and equals the v9 trajectory shifted by +3 real ticks | original: frozen (87.5 %) or one 20 ms Box2D step |
| T8 | Light press by a busy fighter (attacker in lock / victim in stun) during hitstop | no move starts (lost, D-N1) | exact |
| T9 | Light press by a **free** fighter at real tick k+2 of a hitstop (e.g. projectile hit) | latched; move starts on game step at k+4 (game tick g+1) | original starts at game time g+0.1; same game tick ±1 |
| T10 | Opponent (free) presses Light at real tick c+1 of a cinematic, releases at c+2 | move starts on game step at c+5; its active frames follow in game steps (each 5 real ticks apart) | start ±1 game tick; ≤ 4 real ticks later |
| T11 | Same as T10 with A-lost | (regression guard) must NOT be lost | |
| T12 | Crit (5) lands at real tick c+20 of a cinematic | ticks c+21..c+25 scale 50; cinematic still ends at c+48 (counted in real ticks during hitstop); steps in c+1..c+48 = floor((20·200 + 5·50 + 23·200)/1000) = **8** | min-factor and independent countdowns exact |
| T13 | Two hits same game step (trade, normal + crit) | `HitstopRealTicks = 5` (max) | exact |
| T14 | No slow-mo for a whole replay | state trajectory equal to v9 except the new fields and `RulesVersion` | exact (regression) |
| T15 | 7 consecutive isolated normal hitstops | accum 150 each → the 7th window reaches 1050 → exactly one game step inside it | models the original's carried phase |
| T16 | KO on game step at tick k | `MatchOver`; no further game steps; view shows 72 ticks of 0.3 presentation, results at k+78 | view-only |
| T17 | Hash | changing any of `HitstopRealTicks, CinematicRealTicks, CinematicFighter, TimeAccum, GameTicks, LatchedEdges` changes the state hash | exact |

---

## 7. Unverifiable / open points

- **N vs N-1 slowed frames** in the original depends on script execution order (MatchController vs Fighter Update)
  and on whether `ApplyDamage` runs from Update or a physics callback; not pinned in the project. We use N.
- `HitStop(0.1)` and `Cinematic` 0.8 / finish 1.2 sit on exact multiples of 1/60; in the original float
  `unscaledTime` makes the last frame ±1.
- Whether blocked hits (chip/guard drain) reach F:625: the sim must trigger hitstop on exactly the paths where the
  original calls `ApplyDamage` (confirm when implementing, damage-conversion.md section 3).
- Rigidbody2D interpolation setting in the original (affects how smooth the cinematic looked) not checked; the view
  extrapolation is a presentation choice.
- The ult gesture window / `ultRetryCd` / prompt are not in the sim yet (inventory row 133); when added they go in
  the real-tick section of `Step`, not the game step.

---

## 8. Technical director review (2026-11-08)

**Verdict: adopt Option A-held, with the amendments in 8.3 before QA writes tests.** B and C are not justified.

### 8.1 Rollback / online impact (A-held)
- **Input frames vs game steps: consistent.** `Step` stays a pure function `(state at real tick N, inputs N) -> state N+1`.
  A real tick with no game step still consumes an input frame and still changes state (`Frame`, counters, `TimeAccum`,
  `LatchedEdges`, `PrevInput`), so prediction, snapshot-per-frame rollback, `OnFrameConfirmed` and replays (one input
  pair per real tick) need **no change**. `LatchedEdges` is derived only from inputs and state, lives in
  `ChibiFighterSim`, is copied by `CopyFrom` and hashed: deterministic and rollback-safe, provided it is added to
  `CopyFrom` and `Hash` (T17 must cover the copy too, not only the hash).
- **Mispredictions on skipped ticks** change `LatchedEdges` and so trigger a rollback, which is correct; the visible
  effect only appears at the next game step. Hitstop/cinematic therefore *hide* rollbacks (fewer visible corrections),
  a known benefit in rollback fighters.
- **Depth / cost:** unchanged in real ticks (`MaxPrediction = 8`, ring 128). During a 48-tick cinematic re-simulation is
  cheaper (4 of 5 ticks run no game step). No new worst case.
- **Checksums every 30 real ticks:** fine; the new fields are hashed, frozen ticks still change the hash via `Frame`.
- **Time dilation / ChibiTickClock:** untouched; both work on real ticks. The sim must never read `Time.timeScale`.

### 8.2 D-N1 rule (exact)
D-N1 is defined in **game time**: no game time passes inside a frozen hitstop, so "the moment of the press" and "the
next game step" are the same game instant (±1 game tick, the tolerance used in steps 1-9). Rule:
> A press edge on any real tick is latched and **counts as a press at the next game step only**. That game step
> accepts or loses it under the v9 rules (busy = lost). The latch is cleared by that step whether used or not; it is
> never carried to a second step.

Accepted deviations (document as QA cases): (a) two different buttons pressed in one window resolve by the sim priority
(Ult > skills > Light > Heavy), not by which came first; (b) in a cinematic a press up to 4 real ticks before a lock ends
can be accepted (< 1 game tick, within tolerance). Neither is an input buffer.

### 8.3 Required contract changes (section 5)
1. **Block level across skipped ticks.** `raw = current raw` loses a Block tap that starts and ends between two game
   steps (shield is level-driven, `ShieldTick`/parry start from it). Mirror the existing input-source rule
   (damage-conversion.md section 7, `_shieldLatch`): add `ChibiFighterSim.LatchedHeld` (Block bit OR-ed over the real
   ticks since the last game step), game step uses `raw | LatchedHeld`, then clears it. Rolled back and hashed. QA case:
   Block tap at c+1..c+2 of a cinematic -> shield active on the step at c+5.
2. **View interpolation formula is wrong after any carry.** `TimeAccum` keeps the carried phase forever (e.g. 150 after
   one hitstop), so at full speed `f = (150 + alpha*1000)/1000` reaches 1.15, gets clamped and every frame stalls for the
   last 15 % of the tick (judder for the rest of the match). Use view-only `sinceStep` = real ticks since `GameTicks`
   last changed: `f = min(0.999, (sinceStep + RenderAlpha) * scale / 1000)`. At scale 1000 this is plain `RenderAlpha`.
3. **Replay format.** CCRP v1 stores no RulesVersion/config hash, so a v9 replay silently re-simulates under v10 and
   only fails `Verify`. Bump the replay `Version` to 2 and store `ChibiMatchConfig` config hash; reject mismatches.
4. **Real-tick section is a named function.** Put the decrement of `HitstopRealTicks`/`CinematicRealTicks` (and later
   ult window, `ultRetryCd`, prompt) in `RealTickTimers(s)` called before the game step, so steps 11-19 have one place
   for unscaled timers and reviewers can check each new timer is placed by the original's scaled/unscaled use.
5. Order inside `Step` as written in section 5 is correct (scale read before decrement; hit at k slows k+1..k+N).
   Keep `MatchOver` early return before latching. Every `s.Frame` read in gameplay (match timer, `SpawnFrame`) moves to
   `GameTicks`; add a test that grep-equivalently asserts no game-step code reads `Frame` (or review checklist item).

### 8.4 Risk
- **Steps 1-9 (345 tests):** "no change outside slow-mo" is true, but most combat tests *contain* hits, so any test
  that counts ticks after a hit (knockback arcs, stun, combo window, trades) shifts by 3-8 real ticks. Mitigation: a
  test helper that advances by game steps (`StepGame` = step real ticks until `GameTicks` increments) and convert those
  tests to it; do NOT add a sim flag that disables hitstop (that is a second gameplay path, against Q1). Expect the
  diff to be in test helpers, not in expected values.
- **Steps 11-19:** projectiles/minions/multi-phase moves count game steps (low risk, `SpawnFrame` already noted);
  ult window, retry cooldown, prompt and gesture windows are real time (must go in `RealTickTimers`; gesture tracking
  stays in the input source on unscaled time); match-flow timers (results 1.3 s, finish slow-mo) are real time/view;
  AI (step 19) must classify each original AI timer as scaled or unscaled the same way. Highest residual risk is a
  future timer put in the wrong section, hence item 4.
