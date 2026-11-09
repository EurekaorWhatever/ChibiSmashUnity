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
