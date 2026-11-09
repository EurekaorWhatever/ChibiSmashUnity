# Port step 16: match flow in the deterministic sim (rules v16)

Source of truth: `Game/MatchController.cs` (MC), `Fighter/Fighter.cs` RingOut / Revive (F:692-714), `UI/Hud.cs:47-53`.
Tests: `Assets/Tests/EditMode/Editor/ChibiMatchFlowTests.cs`.

## 1. Config (both in the config hash)

| Field | Meaning |
|---|---|
| `Intro` | Play the 2.2 s countdown before input (production matches, online and offline). Unit tests leave it off. |
| `Training` | No timer; a KO revives instead of ending the match; the T key may fill P1's meter. |

## 2. State and order

| Original | Sim (rollback state, hashed) |
|---|---|
| `IntroTimer` 2.2 s unscaled (MC:124) | `IntroRealTicks` = 132, decremented at the END of each real tick, so real ticks 0..131 have input off |
| `Running` / `InputEnabled` (MC:35, MC:41) | `ChibiSimState.Running` = intro over and not MatchOver; actions, poses, the ult window and the ult fire need it |
| `TimeLeft -= Time.deltaTime` while Running (MC:135) | `MatchClock`: game steps while running (so hitstop and slow-mo slow it, the intro does not count) |
| ring-out check only while Running (MC:121-149) | `ResolveBoundsAndMatchEnd` returns at once while not running |
| `Mathf.Approximately(a, b)` HP ratio (MC:139-140) | `TimeoutWinner`: exact cross-multiplication `HpU0·Max1` vs `HpU1·Max0` (the old permille ratio called gaps up to 0.1% a draw) |
| first fighter to reach 0 loses (Update order) | same-tick double KO = draw (user decision D-F1); a single KO ends the match at the end of that game step |
| training OnKO → Revive (MC:157-161, F:710-714) | HP to max, Stun 0, `Revives++` (view shows "REVIVED") |
| training T key (MC:131) | `ChibiSimulation.TrainingFillUlt(s, 0)`: an out-of-band command between Steps, offline training only |
| `Paused` (MC:106) | offline only: `ChibiLocalLoop` stops stepping the sim (Esc). Every sim timer freezes. Online never pauses. |

During the intro the fighters still run their timers, gravity and the grounded 30 u/s² slowdown (F:186-189), as in the original.
The end-of-step order is: ring-outs → KOs (or training revives) → match clock and timeout. After `MatchOver` the sim only
advances `Frame`; the finish slow-mo (1.2 s at 0.3) stays a view effect.

## 3. Deviations (accepted)

- Training `TrainingFillUlt` is not an input bit, so a training session's replay does not reproduce T presses.
  Training is offline-only and is never replayed or compared.
- The original kept counting unscaled timers while paused; the sim freezes them all (see gesture-conversion.md section 5).
