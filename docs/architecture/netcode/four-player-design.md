# Four players: 2v2 (Team) and Battle Royale. Design before code

Status: **DRAFT for the owner's decisions** (2026-11-22). No code has changed. Author: network-programmer.
Red zone (rollback, protocol, transport, rooms): needs `security-engineer` review before Phase 3 starts.

## Plain-English summary (read this first)

- **Yes, this is a big change.** Today the whole game is built around "me and the other fighter". About 50 places
  in the rules code say "the other fighter" (`1 - i`, `F[0]`, `F[1]`, `in0`/`in1`). Each must learn "which of the
  other three?" In 2v2 it must also learn "is that my teammate?"
- **The online core must change too.** Today each game talks to exactly one other game. With 4 players, every game
  must get 3 other players' button presses each frame, guess them while they travel, and fix its guesses (rollback).
- **The safe way is three steps.** (1) Rebuild the engine for up to 4 fighters but keep 1v1 *exactly* the same. The
  existing tests prove it: the recorded "golden" results must not move. (2) Add 2v2 and Battle Royale on one PC with
  gamepads and CPU players. (3) Take it online.
- **Online plan:** the room host's game also forwards inputs (a "star"). One Relay allocation for 3 guests, like
  today. It is simpler than everyone linking to everyone, and it works in the web build. Cost: a bit more delay
  between two guests, and if the host leaves, the match ends.
- **You need to decide** 7 rules before Phase 2. They are listed in section 8: friendly fire for projectiles,
  stocks in Battle Royale, what happens when a player leaves, how teams are picked, and a few others.
- **Size:** about 4 to 6 weeks of focused work across the 3 phases. Phases 1 and 2 touch combat code, so they
  need your OK first.

---

## 1. Simulation (yellow zone): what changes for N = 4

### 1.1 Data shapes

| Today | Where | Change |
|---|---|---|
| `F = { new ChibiFighterSim(), new ChibiFighterSim() }` | `ChibiSimulation.cs:987` | `F = new ChibiFighterSim[MaxFighters]` (4), plus `s.Count` (2..4) |
| `Class0, Class1; Passive0, Passive1` | `ChibiSimulation.cs:366-367`, hashed `:394-397` | `Class2/3`, `Passive2/3`, `PlayerCount`, `Mode` (Versus/Team/Royale), `TeamOf` (4 x 1 bit) as fixed fields, so the config stays an `in` struct |
| `Winner` 0 / 1 / -1 | `ChibiSimulation.cs:952` | keep it for 1v1. Add `WinnerMask` (bit per fighter) and `Place[i]` (finish order, BR) |
| `Step(s, in0, in1)` | `ChibiSimulation.cs:1168`, `GameStep :1307` | `Step(s, ChibiInputPacket[] inputs, int presentMask)`. Keep the 2-arg overload as a thin wrapper so tests and the replay keep compiling |
| `Held` / `Holding` bools ("held BY THE OPPONENT") | `ChibiSimulation.cs:732,734` | `HeldBy` (-1..3) and `HoldTarget` (-1..3). Invariant becomes `F[v].HeldBy == h <=> F[h].HoldTarget == v` |
| `MoveHit` bool (one hit per swing) | `:660`, read `:2360` | `MoveHitMask` (4 bits): one hit **per victim** per swing |
| Projectile `FHasHit` flag | `:863` | `HitMask` per projectile (pierce shots can hit each enemy once) |
| Pools: `MaxProjectiles 64`, `MaxMinions 12, MaxClouds 4` | `:60`, `:236` | measure first. Likely clouds 8, minions 24. In the config hash |

### 1.2 Every "two fighters" site in the simulation (counted with grep, 2026-11-22)

**A. "The other fighter" lookups: 27 sites.** Each needs a rule: *nearest enemy* or *every enemy*.
- `ChibiSimulation.cs` (20): `1675` TargetX, `1711` Kick range, `1960` program probe, `2042` hold victim,
  `2067/2074` grab, `2092` kick stun, `2106` Hold, `2120` Release, `2134` AdvanceMove, `2349` MeleeProbe,
  `2500/2544` projectile target + hit, `2571` aimed spawn, `2606/2609` Fire Ring, `2843/2852` minions,
  `2980/2992` clouds.
- `ChibiSimulation.Percent.cs:351`: KO frees the holder.
- `Movesets/ChibiSimulation.Movesets.cs` (6): `514` probe, `700/707` reflect (`p.Owner = 1 - p.Owner`), `724`
  tether victim, `796/818` zones.

**B. Hard-coded `F[0]` / `F[1]` / `in0` / `in1`: 15 sites.** `ChibiSimulation.cs:1007` CopyFrom, `1035` hash,
`1116-1117` spawn, `1173-1176` sanitize + latch, `1310-1311` UpdateFighter, `1317-1321` melee probe/apply,
`2034` simultaneous grabs. `Percent.cs:387` last stock, `:398` timeout.

**C. `for (int i = 0; i < 2; i++)`: 5 loops.** `ChibiSimulation.cs:2036` (hold), `:2602` (Fire Ring),
`Percent.cs:379` (KO edges), `Stages.cs:37` (platform riders), `Hazards.cs:208` (hazard hits). The last 3 just
become `i < s.Count`. `HazHitMask` is already a bit mask, so it fits 4.

**D. Two-fighter rules:** `TimeoutWinner(a, b)` (`Percent.cs:363`), match end (`Percent.cs:375-401`).

Outside the sim, with the same pattern: CPU `ChibiCpuInputSource.cs:292,338,1618` (`F[1 - _player]`),
`ChibiLocalSession.cs:60-63`, `ChibiReplay.cs:15-25,41,56,65` (file holds `p0, p1` per record),
`ChibiTransports.cs:218` (test print). Together with sections 2 to 5, that is **about 50 sim sites and about 40
non-sim sites**.

### 1.3 Proposed rules (owner decides the ones marked DECIDE)

- **Enemy:** `Enemy(i, j) = j != i && F[j].InPlay && (Mode != Team || TeamOf[i] != TeamOf[j])`.
- **Nearest enemy** (homing, TargetX, Kick, minions, clouds, aimed spawn): smallest `dx*dx + dy*dy` (long), and
  on a tie the **lowest index**. Never a Dictionary, never a sort that is not stable. No enemy left = today's "dead
  opponent" branch (`:1712` already handles it).
- **Melee, Fire Ring, zones:** hit **every** enemy in the box, in index order, once each (`MoveHitMask`).
  ApplyHit order = attacker index, then victim index. Today "both swings trade" (`:1317-1321`). It becomes "probe
  all, then apply all".
- **Projectiles:** check enemies in index order; a non-pierce shot stops at the first hit.
- **Reflect:** the reflector becomes the owner (`p.Owner = r`), not `1 - Owner`.
- **Grabs:** today two grabs on the same tick cancel (`:2035`, `if (g0 != g1)`). Proposal: a mutual grab (A grabs B
  while B grabs A) still cancels. Otherwise grabs resolve in index order, and you cannot grab someone who is held.
- **Friendly fire OFF** (2v2): teammates are skipped by every probe. DECIDE: projectiles, minions and zones too
  (recommended: yes, everything the player owns). Stage hazards hit everyone (they are neutral).
- **Global hitstop and ult cinematic** (`HitstopRealTicks`, `CinematicRealTicks`) freeze all 4 fighters. With
  4 players they happen about twice as often. Keep them for v1 and playtest. Per-fighter hitstop is a combat
  redesign (DECIDE later).
- **Hit attribution:** the view guesses the attacker as `1 - d` (`ChibiSimView.cs:407,854`). Add `LastHitBy` per
  fighter to the state (hashed). It also enables "KO credit" on the results screen.

### 1.4 KO, stocks, win, timeout

| | 1v1 (unchanged) | 2v2 Team | Battle Royale |
|---|---|---|---|
| Stocks | 3 (`StocksStart`) | 3 each (DECIDE: shared team pool?) | DECIDE (3 recommended; 2 for shorter matches) |
| Out | 0 stocks | 0 stocks: fighter leaves, teammate fights on | 0 stocks: `Place[i]` = players left + 1 |
| Win | other at 0 | both enemies at 0 | one fighter left |
| Same-tick last KOs | draw (D-F1) | both teams out on the same tick = draw | the last 2+ out together share 1st = draw |
| Timeout (300 s) | stocks, %, draw | team stocks total, then team % total, then draw | rank by stocks, then %. Tied leaders = draw |

The match ends in `ResolveBoundsAndMatchEnd`, as today. KOs run first, so a KO on the last timer tick counts.
`presentMask` (section 2.4) marks a fighter who left the match as out, at an exact frame.

### 1.5 Determinism and the tripwires

- `CopyFrom` copies all 4 slots. `ComputeChecksum` hashes `Count` fighters plus every new field (`Mode`, `TeamOf`,
  `WinnerMask`, `Place`, `HeldBy`, `HoldTarget`, `MoveHitMask`, projectile `HitMask`, `LastHitBy`). The reflection
  tests in `ChibiSyncVerificationTests` fail if one is missing.
- **Phase 1 trick (proof that 1v1 did not change):** in 1v1, hash only fighters 0..1 and mix the new config fields
  only when `PlayerCount > 2`. Then the 1v1 `GoldenChecksumA/B` and `GoldenConfigHash`
  (`NetcodeContractTests.cs:57-61`) **must not move**, and `RulesVersion` stays 24. If a golden moves, the
  refactor changed 1v1. Find out why, and never edit the expected value.
- **Phase 2:** the 4-player rules bump `RulesVersion` 24 -> 25 (`ChibiSimulation.cs:363`). Add 2v2 and BR fixture
  goldens and re-record them on purpose.
- Still integers only, no clock, only `s.Rng` / `s.HazRng`.

## 2. Rollback netcode (red zone): 4 peers

### 2.1 Topology: two options

| | A. Star (host forwards inputs) **recommended for v1** | B. Full mesh (everyone to everyone) |
|---|---|---|
| Relay | 1 host allocation, `CreateAllocationAsync(3)`, 1 join code | 6 links. Relay joins only reach the host of that join code, so peers 0..2 each create a host allocation and each peer runs up to 3 UTP drivers (UNVERIFIED: check the Relay docs, `ugs-api-check.md:100`) |
| Latency guest to guest | guest -> relay -> host -> relay -> guest. About +1 relay leg and up to 1 tick more | guest -> relay -> guest |
| Web build (WSS) | 1 socket per guest | 3 sockets per browser |
| Host leaves | match ends (no contest) | others go on |
| Trust | host could forge another player's input. Checksums catch it unless the host also forges those (review item) | each stream comes from its owner |
| Work | medium: host transport accepts 3 connections | large: connection setup and N drivers |

**Accepted risk (SEC-001, security review 2026-11-24):** a modified host can forge a guest's forwarded inputs AND the
matching checksums, so nobody detects it. Accepted for v1 (no server; the host can already end any match by leaving; it
needs a hacked client). Option B (mesh) or a server-side replay check would remove it. Since the review, guests compare
checksums with the host only and the host arbitrates: a single disagreeing guest is dropped, the host disagreeing with
the guests is a desync (no contest).

The host stays a **dumb forwarder**, not an authority. Every peer still simulates from inputs only. The session
API is per-player-stream, so option B can replace A later without touching the sim.

### 2.2 Session and input buffer

- `RollbackInputBuffer` stores 2 streams with one `_remote` (`RollbackInputBuffer.cs:29-32,57`), and one
  `RemoteContiguous` / `PredictionBase` (`:39,45,113`). It becomes N streams, each with its own contiguous frame
  and prediction base (repeat the last known input, as today).
- `ChibiRollbackSession`: `_remote = 1 - localIndex` (`ChibiP2PPeerLoop.cs:251`), `Advance()` gathers 2 inputs
  (`:311-314`). New rules: `RemoteContiguous` = **min over the remotes**. `TakeFirstMisprediction` = earliest over
  the remotes. The stall test `_frame - minContiguous <= MaxPrediction`. `TimeDilation` uses the **largest** lead.
  `OnFrameConfirmed` passes all N inputs (today `(int, in0, in1)`, `:113`).
- **Prediction:** the chance that a frame needs a rollback rises with 3 remotes. Expect a rollback almost every
  tick at depth about RTT/2 - input delay.
- **Rollback cost:** worst case at depth 8 = 9 `Step`s + 9 snapshots (`CopyFrom`) per 60 Hz tick, with
  4 x 8 = 32 inputs replayed. Fighter work grows about x2, and pair checks grow from 2 to 12. **Budget: 9 Steps of
  a 4-player state under 4 ms on the web build.** Measure it in Phase 1 with a benchmark test before going online.
- **Settings for 4 players** (they travel in the handshake, so keep them in config): input delay 2 -> 3, and
  `MaxPrediction` 8 -> 10 (still far below `RingSize 128`). These are starting values for the star's extra hop at
  the 150 ms RTT target (`rollback-gap-report.md` 2.6). Tune them by playtest.

### 2.3 Packets (bump `ChibiPacketCodec.ProtocolVersion` 5 -> 6, `ChibiInputPacket.cs:167`)

- **Guest -> host:** today's packet (own inputs, redundant window, checksum). The single `AckFrame` becomes
  **one ack per forwarded stream** (3 x 24 bits).
- **Host -> guest:** a bundle: for each of the other 3 players, a 2-bit player id, then `LastFrame`, `Count` and the
  run-length inputs (same encoding as now), plus each player's latest checksum. Forward on receive, during the same
  `PumpNetwork`, to save a tick.
- Worst case 3 x 16 x 17 bits is about 102 bytes, more than `MaxPacketBytes = 64` (`ChibiInputPacket.cs:173`).
  Raise it to 192, still under mux `MaxDatagram 256` (`ChibiNetProtocol.cs:40`) and the 1400 MTU. Untrusted-input
  rules: player id in range and not the reader's own, `Count` 1..16, no throw, no allocation.
- **Handshake:** `Start` carries 2 classes (`ChibiNetProtocol.cs:270-273`) and `ValidStart` checks seat 1 only
  (`:597`). It becomes: `PlayerCount`, `Mode`, `TeamOf`, 4 x (class, passive), and each guest checks *its own* seat.
  Guests' names are forwarded in a new `Roster` message, because the 24-char names do not fit in
  `MaxBytes = 48` (`:212`). `Ready` from all 3 guests = Agreed.
- The protocol tag `p6r25` (`NetworkRoomManager.cs:77`) keeps old builds apart in matchmaking.

### 2.4 Checksums, desync, leaving

- Every peer checks its own checksum against **each** remote's. The host forwards them. **Any mismatch = no contest
  for everyone** (no state transfer exists to repair a peer, and a vote cannot tell who is lying in P2P).
- **A player leaves or times out mid-match (DECIDE, recommendation):** the host picks a frame F after everything
  it has forwarded, and broadcasts "player p gone from F". From F on, `presentMask` clears bit p. It travels with
  the inputs, so rollback replays it the same way on every peer. The sim then KOs fighter p out (all stocks).
  The match goes on, and the leaver gets a loss. In 2v2 the teammate plays 1v2. **Stalled** (`StallLimit`) =
  the same, so one slow player cannot freeze the other three (today Stalled = no contest, `ChibiOnlineMatch.cs:253`).
- **The host leaves:** no contest for all in v1 (star). Host migration is out of scope.
- A 1v1 stays as it is: opponent leaves = you win (`ChibiOnlineMatch.cs:257-262`).

### 2.5 Bandwidth (60 Hz; about 80 bytes of IP+UDP+Relay+DTLS overhead per datagram, overhead is an estimate)

| Link | Payload | With overhead |
|---|---|---|
| 1v1 today, each way | about 18 B (11 B inputs + checksum) | about 5.9 KB/s |
| Star, guest up | about 27 B | about 6.4 KB/s |
| Star, guest down | about 50 B | about 7.8 KB/s |
| Star, host up (3 guests) | 3 x 50 B | **about 23 KB/s (190 kbit/s)** |
| Mesh, each peer up | 3 x 18 B | about 18 KB/s |

Priority: there is only one tier. Every byte is input, so there is nothing to drop. The host's upload is the
limit; 190 kbit/s fits any home broadband or phone hotspot.

## 3. Relay + Lobby

- **Allocation:** `CreateAllocationAsync(1)` at `NetworkRoomManager.cs:291` and `:352` becomes `(Seats - 1)`.
  `ChibiUtpTransport` keeps **one** `_conn` (`ChibiUtpTransport.cs:41,141`). The host side must accept and route
  3 connections (one mux per guest). Guests are unchanged.
- **Lobby size:** `CreateLobbyAsync(lobbyName, 2, ...)` (`NetworkRoomManager.cs:317`) and
  `ChibiRoomRules.Seats = 2` (`ChibiRoomRules.cs:60`, used by `ChibiRoomService.cs:210`) become seats per mode
  (2 / 4 / 4).
- **Play style:** put it in the protocol tag (`p6r25-T` / `-R` / `-V`). The quick-match filter already matches S1
  exactly (`NetworkRoomManager.cs:503`), so no new index is needed and modes never mix. Private rooms show it
  as member data.
- **Teams:** a seat-to-team map in lobby data, written by the host. DECIDE: quick match = join order
  (seats 0,2 vs 1,3) or a seeded shuffle. Private room = players pick and the host can swap.
- **Start when full:** the lobby is listed while `AvailableSlots > 0` (`:502`). When it is full, the host locks the
  lobby (`IsLocked`) and starts the handshake. All 3 guests must answer `Ready` inside the handshake timeout, or the
  host unlocks and keeps waiting. Private rooms use the existing ready/nonce flow, with `Slots[1]` per guest
  (`ChibiRoomService.cs:451,509,557,566`, `CanStart` at `ChibiRoomRules.cs:137` = all seats ready).
- **Someone leaves while waiting:** the slot frees and the lobby stays listed. A guest whose Relay link fails is
  dropped by the host, and the lobby unlocks. Waiting text: "2 of 4 players".
- Heartbeat, regions and the version gate at join (`ChibiRoomService.cs:233`) are unchanged.

## 4. Local 4 players (one PC)

- `HumanInput.Pad` is `Gamepad.all[player]` (`Fighter/FighterInput.cs:47`). The keyboard layouts exist only for
  players 0 and 1 (`FighterInput.cs:76,125`; `ChibiPlayerInputSource.cs:111,119`). Add a **"press a button to
  join"** screen that maps a device to each seat: keyboard-left, keyboard-right, pad 1..4. The seat reads its
  device, not `pads[player]`.
- The webcam pose source (`ChibiPlayerInputSource.cs:90`) serves one player. Players 3-4 use gestures from buttons.
- **CPU:** `ChibiCpuInputSource` reads `F[1 - _player]` (`:292,338,1618`). It gets the same nearest-enemy rule as
  the sim and never targets a teammate. Any seat can be CPU (`ChibiMatch._cpu`, `Game/ChibiMatch.cs:43`).
- `ChibiLocalSession` / `ChibiLocalLoop` take `IChibiInputSource[]` (`ChibiLocalSession.cs:20,60-63`,
  `ChibiLocalLoop.cs:60-67,105-106`). `ChibiMatch` arrays of 2 (`ChibiMatch.cs:39-43,164`) become 4.

## 5. View, HUD, camera, stages (green zone, plus stage data in yellow)

- **HUD:** `PlayerPanel(m, 0..1)` in the two corners (`UI/Hud.cs:52-53`, loops `:59,68,164,195`). Change to 4
  panels along the bottom, Smash style. Colours: `Gui.P1/P2` (`UI/Gui.cs:28-29`) plus P3 yellow and P4 green.
  In 2v2, a red or blue team frame with the player colour kept inside it.
  Results screen: placements (BR) or the winning team.
- **View:** `ChibiSimView` arrays of 2 (`:78-83,144,145,164,169`) and loops (`:223,305,569`). Attacker from
  `LastHitBy`, not `1 - d` (`:407,854,1003,1009`). Same for `ChibiAudioRules` (3), `ChibiBladeTrails` (1),
  `ChibiSimTestHud` (4).
- **Camera:** `FrameFighters` loops 2 (`ChibiSimView.cs:1180`). Loop over the in-play fighters, and add a max
  ortho size per stage so 4 spread-out fighters do not shrink to dots (`ChibiCameraFraming.Bounds` already clamps
  to the KO lines).
- **Stages:** `ChibiStageDef` has one `SpawnX` (P0 at -X, P1 at +X; `ChibiStageTable.cs:83-84`) and one respawn
  point (`ChibiSimulation.Stages.cs:63`). Add 4 spawn points per stage, chosen by the level-designer on solid
  ground or platforms, and 4 respawn X offsets so fighters never respawn stacked. This is stage data, so it is in
  the config hash: `RulesVersion` and goldens. 1v1 keeps using spawns 0 and 1 exactly as today.

## 6. Tests (EditMode, `Assets/Tests/EditMode/Editor/`)

1. **Phase 1 guard:** all of today's tests pass and the 1v1 goldens do not move. Add a benchmark test: 9 Steps of
   a 4-fighter state, reported (an observation, not a pass/fail time).
2. **Determinism:** 4-fighter random-input runs give the same result twice. Snapshot/restore at every depth.
   Reflection check of every new field in `CopyFrom` / checksum.
3. **Rules:** friendly fire off (melee, projectile, pierce, zone, minion, cloud, Fire Ring, grab, reflect), with
   hazards still hitting teammates. Nearest enemy including ties. One swing hits 2 enemies. Grab rules with 3
   fighters. A KO while held by a third fighter. Team elimination, BR last standing and placements.
   Same-tick last KOs = draw. Timeout ranking for both modes.
4. **Sync:** a 4-peer loopback hub with latency, jitter and loss per link (extend `ChibiTransports`). All 4 end
   equal at every checksum.
5. **Online match:** a 4-way handshake. A wrong seat or class is refused. A version mismatch. A guest leaves while
   waiting. A guest leaves mid-match = eliminated at the same frame on all peers. The host leaves = no contest.
   One desync = no contest on all. A stalled guest = eliminated.
6. **Codec:** bundle round trip, golden hex, and fuzz (truncated, bad player id, own id, count 0 / 17, 200-byte
   garbage).
7. **Rooms:** seats per mode, start only when full, teams assigned, mode in the tag so modes never match.
8. **CPU:** never targets a teammate; picks the nearest enemy.
9. **Replay:** N-input format round trip. An old 2-player file still plays (format version byte).
10. **Visual:** Play Mode screenshots of the 4-panel HUD, 2v2 colours and 4-fighter camera on every stage, saved in
    `production/qa/evidence/`.

## 7. Plan

| Phase | What | Size | Touches combat? | Main risks |
|---|---|---|---|---|
| **0** | Owner decisions (section 8). Security review of the star and the `presentMask`. Level-designer spawn points | 1-2 days | no | none |
| **1** | Engine for N fighters, **1v1 bit-identical**: arrays, `Step(inputs[])`, `HeldBy`, masks, nearest-enemy helper. Sessions, replay and view take arrays but still run with N=2 | L, 4-6 days | **yes, structurally** (every `1 - i` site), with zero behaviour change, proven by unchanged goldens | a hidden 1v1 change (goldens catch it). Perf of 4 snapshots |
| **2** | 2v2 + BR **local**: teams, friendly fire, multi-victim hits, win/timeout/placements, spawns, pools, `RulesVersion` 25, new goldens. Device join screen, CPU targeting, 4-panel HUD, camera | L-XL, 7-10 days | **yes**: hit rules, grab rules, match end | feel (global hitstop with 4), CPU quality, camera on small stages |
| **3** | **Online**: N-stream input buffer and session, star forwarding, protocol 6 + Roster, `presentMask` leave, Relay 3 connections, lobby seats/mode/teams/start-when-full. Update `docs/Network Engineering Summary.md` | XL, 8-12 days | no (netcode only) | latency for guest-to-guest pairs, host upload, Relay multi-connection on WebGL, host forging inputs |
| **4** | Polish: results and placements, team colours, playtests on real links, tuning input delay and prediction | M, 3-5 days | no | playtest availability (needs 4 people) |

Total is about 4-6 weeks. Phases 1-2 can ship on their own (local party mode) before Phase 3.

## 8. Decisions the owner must make

1. **Friendly fire:** off for melee is given. Off for projectiles, minions, zones, clouds and Fire Ring too?
   (Recommended: yes.) Do stage hazards hit teammates? (Recommended: yes, they are neutral.) Can you grab or
   reflect a teammate's shot? (Recommended: no.)
2. **Stocks:** BR 3 (recommended) or 2? 2v2: 3 per player, or one shared team pool?
3. **Player leaves mid-match:** fighter eliminated and the match continues (recommended), or no contest for all?
   And does the leaver's teammate get a loss if the team then loses?
4. **Team balancing:** quick match by join order or a seeded shuffle? Do private rooms let players pick teams?
5. **Topology:** star through the host (recommended for v1) or full mesh?
6. **Global hitstop and ult slow-mo** with 4 players: keep for v1 and judge in playtest (recommended)?
7. **Online mixed local:** 2 players on one PC joining an online 2v2. Out of scope for v1 (recommended)?

## 9. Decisions taken (2026-11-23)

The owner said "just make it", so every DECIDE item takes the recommended answer:

1. Friendly fire OFF for everything a player owns (melee, projectiles, minions, zones, clouds, Fire Ring); stage
   hazards hit everyone; no grabbing or reflecting a teammate's shot.
2. Stocks: 3 for Battle Royale; 2v2 = 3 per player (no shared pool).
3. A player who leaves is eliminated at an exact frame (`presentMask`) and the match continues.
4. Teams: join order in quick match; rooms may pick teams later (v1: join order).
5. Topology: star through the host (one Relay allocation for 3 guests).
6. Global hitstop and ult slow-mo stay for v1; judged in playtest.
7. Two local players joining one online match: out of scope for v1.

Priority from the user's PDF: every mode is players against each other, so **Battle Royale (2-4 FFA)** is the
main mode for MATCHMAKING rooms and VS ONLINE; 2v2 Team follows with the same engine.
