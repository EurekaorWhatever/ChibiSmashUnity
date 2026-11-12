# Port step 17: CPU opponent as a deterministic input source

Source of truth: `Fighter/FighterInput.cs` `CpuInput` (FI:138-228). Code: `Netcode/ChibiCpuInputSource.cs`.
Tests: `Assets/Tests/EditMode/Editor/ChibiCpuAiTests.cs`. Supersedes the open questions of [mode3-ai-input.md](mode3-ai-input.md).

## Design

- The CPU is an `IChibiInputSource`: it plays **only** through `ChibiInputPacket`s, like a human, so vs-AI runs the
  exact combat code used online (topology (A) of mode3-ai-input.md: `ChibiLocalSession`, no rollback).
- It reads the live state at the start of each tick through `IChibiStateAwareSource.Bind`, which `ChibiLocalSession`
  calls automatically. It never writes the state and never draws from `state.Rng`.
- Own RNG: `ChibiRng(matchSeed ^ 0xA15EED00 ^ (player + 1)·0x9E3779B9)`. Integer math only.
- Difficulty 0 = still training dummy (neutral every tick), as the original.

## Behaviour port (one decision per 60 Hz tick; the original's `x·dt·60` chances are `x` per tick)

| Original | Port |
|---|---|
| recover when `|pivot.x| > halfWidth - 0.3` or `pivot.y < groundY - 0.5` | pivot = feet + 0.725, so feet Y < -1225; move to centre, Jump while Vy < 0, Melee Skill1 while Vy < -2 u/s |
| `think = Range(0.1, 0.3) / max(0.4, skill)` | `(6 + Range(13)) · 1000 / max(400, skill‰)` ticks |
| preferred range: ranged 4.5, else reach·0.8; move in / back off | same, milli |
| block if opponent attacking within 2.5, chance 15% + 35%·skill | same (`IsAttacking` = opponent `MoveSlot >= 0`) |
| jump if opponent 1.5 above, 60% | same, on think ticks |
| attack chance (6% + 12%·skill), 75% Light | same; ranged needs inLine (|dy| < 1), melee inMelee |
| random skill 1.2%·skill if ready and in range | same |
| Ult press 2% when full and no window | same |
| ult gesture: wait 0.6-2.2 s, success 60% + 12%·difficulty | same, then the pose passes a 6-tick (0.1 s) stability filter like GestureTracker |

Presses are edges in the sim, so a press requested on consecutive ticks is sent on alternate ticks (never two ticks held).

## Deviations (accepted)

- The original turns with `MoveX = ±0.2` (a creep); the sim stick is digital, so one tick of full input turns the CPU.
- Repeated per-frame "pressed" requests (for example Jump every frame while falling) become one press every other tick;
  the outcome is the same because the sim acts on the first.

## Addendum (2026-11-10, rules v19): the CPU uses the character movesets

The CPU is still a deterministic input source (own seeded RNG, integers only, reads the state read-only, difficulty 0 =
neutral dummy). Since the character movesets (design/gdd/movesets/) it presses direction + button combinations:

- **Per-character option table** in `ChibiCpuInputSource.cs` (`Options` / `Profile`): situation, button, direction,
  weight, minimum difficulty, hold ticks. A move is only chosen if it exists, can start (cooldown, once-per-airtime,
  reload, SpearOut) and its own row says it would connect (hitbox, lunge distance, projectile range).
- **Smashes** punish (opponent in hitstun / endlag) or kill (low HP, near the edge); charged only while the hitstun
  allows (difficulty 2+). **Aerials** by relative position (fair/bair by side, uair above, dair below; more often when
  edge-guarding). **Counters / reflectors** (difficulty 2+) only when the opponent's hit would land inside the window.
- **Charge holds:** Heavy, Special or Skill3 may be held as one press (capped at 48 ticks) for charge moves; every other
  button is released for at least one tick between presses.
- **Frozen ticks (hitstop / cinematic):** until a game step has consumed the last press, the CPU keeps that stick
  direction and presses nothing new, because the step reads the stick on the tick it actually runs.
- **Recovery:** air jump below stage level, air dash for distance, up-special timed from its own rise speed so it clears
  the stage top; Kaia's original Dash & Thrust recovery is kept but only used while facing the stage (facing is locked
  in the air since OD-1).
- **Tests:** `ChibiCpuMovesetTests` (determinism per character, >= 8 distinct new moves per match, every move family
  used, difficulty 0 neutral, recovery from 2 start positions per character, press/hold discipline) plus the updated
  `ChibiCpuAiTests`; CPU-vs-CPU pairings also run in `ChibiSyncVerificationTests`.
- **Known limit:** starts deep beside the stage's side wall are not survivable for Kaia, Bruna and Taro (no wall jump or
  ledge grab exists in the sim).

## Addendum (2026-11-12, rules v20/v21): percent loop, stages and hazards

- **Kill reads by percent:** each candidate move's knockback comes from the sim's own `ChibiSimulation.KnockbackM` (same
  formula, no copy); launch distance ~ 0.44·KB² is compared with the ACTIVE KO edges of `ChibiStageTable.Get(stage)` only
  (walls never KO). Smashes are saved for kill percent, tilts/aerials build percent; on Mountain (walls, vertical KO) the
  finisher is an up smash. Ult use rises with the opponent's percent.
- **Survival:** at its own kill percent the CPU shields more, keeps wider ledge margins and stops chasing off-stage.
- **Ledges (v21):** the end of the walkable ground (MAIN half-width, or the finite floor on Bird Lair) is the ledge on every
  stage; recovery targets it, and side / lower / moving platforms are landing candidates (moving ones predicted from
  `PlatformCx/Top` at the arrival tick).
- **Hazards (only when `s.HazardsOn`):** Volcano — leave an announced fire lane (or wait outside it if too late to cross);
  Bird Lair — drop off the mesas and hold upwind during the gust, never stand in the body band; Ocean — drop to T1/T2/T3 when
  the whale telegraphs and stay below until it rises.
- **Respawn:** no input while `OutTicks`/`HoverTicks`, then one stick tick to leave the hover platform when safe.
- **Tests:** `ChibiCpuStageTests` (20 cases: determinism per stage, fire-lane avoidance, whale shelter, no fall-off on Plain,
  Bird Lair body band, respawn, kill-percent smash bias); all behaviour cases were watched failing against the pre-v20 CPU.
