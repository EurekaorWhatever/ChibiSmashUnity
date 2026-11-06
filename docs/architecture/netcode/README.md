# Netcode design notes (2026-11-06)

Drafted by studio agents and reviewed in session. **The user decisions below override anything in these docs that disagrees.**

## User decisions
| # | Decision |
|---|---|
| Q1 | One version of the game: the original full-feature `Fighter` game, made deterministic so it runs online. No feature may be dropped. No second gameplay implementation. |
| Q2 | Private rooms: players join with the **Lobby** code (not the Relay code). |
| Q3 | Input packet grows to 16 bits so Dash and gesture inputs go online. |
| Q4 | Mid-match disconnect: the opponent wins automatically. |
| — | Rollback engine: keep the existing `ChibiRollbackSession`; no second rollback engine. |

## Files
| File | Author agent | Contents |
|---|---|---|
| [netcode-architecture.md](netcode-architecture.md) | technical-director | Topology, data flow, 3 modes, CV gesture design, packet budget, determinism risks |
| [rollback-gap-report.md](rollback-gap-report.md) | network-programmer | How `ChibiRollbackSession` meets the rollback spec, plus gaps and bugs |
| [mode3-ai-input.md](mode3-ai-input.md) | ai-programmer | Vs-AI input injection design |
| [ugs-api-check.md](ugs-api-check.md) | unity-specialist | UGS / Transport API signatures verified against installed packages |
| [feature-inventory.md](feature-inventory.md) | gameplay-programmer | All 148 features of the original game, determinism status, port order |

## Known existing bugs (confirmed in code)
- Packets over 32 bytes are silently truncated: `BitBuffer.cs:60`, unchecked at `ChibiP2PPeerLoop.cs:301`.
- Resend window is only the newest 8 inputs (`ChibiP2PPeerLoop.cs:278-281`); a lag of more than 8 frames on acks can stall both peers until disconnect.

## Superseded in these docs
- Any recommendation to show the **Relay** join code (Q2 chose Lobby).
- `mode3-ai-input.md` option (A) "run ChibiSimulation directly": still valid, but the sim must first reach full feature parity with the original (Q1).
