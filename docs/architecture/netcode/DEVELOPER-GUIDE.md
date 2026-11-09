# Building on the online core without breaking it

Chibi Clash's online play is a deterministic rollback core (GGPO style). Two players' games simulate the same match
from the same inputs, and only inputs cross the network. That is what makes it fast and fair. It also means one thing
done in the wrong place can split the two games apart (a desync). This page says where you can build freely and what
needs care. The automated tests (900+) and the contract tripwire catch mistakes before they ship.

## 1. The map: three zones

| Zone | Folders / files | Change freely? |
|---|---|---|
| **Green: build the game here** | `UI/` (Hud, Gui), `Game/GameManager.cs` (menus, screens, onboarding), `Netcode/View/` (ChibiSimView: sprites, VFX, sounds, camera, interpolation), `Core/` save data, quests, armory, cosmetics, art, audio, scenes | **Yes.** These only READ the simulation state or the events it emits. Nothing here can desync a match. |
| **Yellow: gameplay rules (deterministic)** | `Netcode/ChibiSimulation.cs`, `ChibiFixed.cs`, `ChibiCombatStats.cs`, `ChibiStageTable.cs`, `ChibiCpuInputSource.cs` (characters, moves, damage, stages, CPU) | **Yes, with the rules in section 3**, then bump `RulesVersion` |
| **Red: the network core** | `ChibiRollbackSession` / `RollbackInputBuffer` (rollback), `ChibiNetProtocol.cs` (handshake, mux, control codec), `ChibiInputPacket.cs` (16-bit input packet), `ChibiUtpTransport.cs` (UDP/Relay), `NetworkRoomManager.cs` (rooms, matchmaking), `ChibiOnlineMatch.cs`, `ChibiSocialRules.cs` / `ChibiSocialManager.cs` (security rules), `BitBuffer.cs` | **Rarely.** Only for networking work, and with the network-programmer + security-engineer reviews |

## 2. Green zone: how to add things without touching the core

- **New screen, menu, HUD element:** `GameManager` / `Hud`. Read the match through `ChibiMatch` (`State`, `Def(i)`,
  `NameOf(i)`, `Online`, `LocalPlayer`, `Winner`). Never write to `ChibiSimState` from UI.
- **New visual or sound for a move:** react to `ChibiSimEvents` (hit, KO, ult, projectile...) in `Netcode/View/`. Events
  are derived by comparing two consecutive displayed states, so a rollback re-simulation never fires them twice. Add a
  new kind there (derived from state fields) rather than calling effects from inside the simulation.
- **Cosmetics (skins, weapon looks):** local only, never sent to the opponent, never read by the simulation.
- **New offline mode (VS CPU variant, training option):** build a `ChibiMatchConfig` and use `ChibiMatch` / `ChibiLocalLoop`.
- **Player names:** use `ChibiSocialRules.SafeDisplayName` / `PeerDisplayName` before drawing ANY name (IMGUI renders rich text).

## 3. Yellow zone: the determinism rules (the simulation and anything it calls)

1. **Integers only:** `ChibiFixed` / milli units / micro-HP. No `float`, `double`, `Mathf`, `Vector2` inside `Step`.
2. **Randomness only from the simulation's own RNG** (`ChibiRng` in the state), never `UnityEngine.Random` or `System.Random`.
3. **No clock:** no `Time.*`, `DateTime`, frame rate. Time = ticks (`GameTicks`).
4. **No hidden state:** every field that changes during a match lives in `ChibiSimState` (then `CopyFrom` and
   `ComputeChecksum` cover it, and the reflection tests fail if you forget one). No statics, no caches, no
   `Dictionary` iteration order, no `GetHashCode` of strings.
5. **No Unity objects** (GameObject, physics, Animator) inside the simulation. The view follows the sim, never the reverse.
6. **Inputs are 16 bits** (`ChibiInputPacket`). A new button means a protocol change (red zone).
7. **Bump `ChibiMatchConfig.RulesVersion`** (`ChibiSimulation.cs`) for ANY gameplay or balance change, then re-record the
   golden values in `NetcodeContractTests` (the failing test prints them). Old and new builds then refuse each other
   with a clear "different version" message instead of desyncing, and matchmaking keeps them apart (protocol tag).

## 4. The tripwires (what stops a bad change)

| Guard | Catches |
|---|---|
| `NetcodeContractTests` | ANY change to gameplay results or constants (fixture match checksums, config hash) or to the wire bytes, with a message saying what to bump |
| `ChibiSyncVerificationTests` | Two peers over a hostile link (latency, jitter, loss) end in different states; snapshot/restore at any depth differs; a state field missing from `CopyFrom` / checksum |
| `ChibiOnlineMatchTests`, `ChibiUtpTransportTests` | Handshake, version refusal, disconnect = opponent wins, desync = no contest, real UDP transport |
| `ChibiSocialRulesTests`, `ChibiPlayerNameTests`, `ChibiRegionsTests`, `PlayerIdentityTests` | Security rules: invites, injection, names, age gate, matchmaking regions |
| Handshake config hash at runtime | Two builds with different balance data never start a match together |
| `.github/workflows/tests.yml` | Runs all of the above on every push / pull request (needs the Unity license secrets, see the file) |

**Never** "fix" a red tripwire by changing only the expected value. Ask which section of this page the change belongs to.

## 5. Changing the red zone (when you must)

- Wire format (packet, control message, mux): bump `ChibiPacketCodec.ProtocolVersion`, update `GoldenHelloHex`.
  Optional trailing fields (like the player name) can stay compatible: readers must accept their absence.
- Every field read from the network is untrusted: size-check, range-check, never throw, never allocate per packet.
- Run the studio reviews: `network-programmer` (correctness, scale) and `security-engineer` (abuse), as done for
  rooms, invites and matchmaking (see online-rooms.md, online-social.md).

## 6. Before you merge (2 minutes)

1. Unity Test Runner → EditMode → Run All: everything green.
2. Changed gameplay? `RulesVersion` bumped and golden values re-recorded on purpose.
3. Changed something the player sees? Screenshot in `production/qa/evidence/`.
4. Online feature? Two-player test (LAN is enough for the core; room code / invite needs the Unity Cloud link).
