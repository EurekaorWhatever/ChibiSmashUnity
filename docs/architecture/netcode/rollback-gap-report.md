# Rollback gap report: RollbackInputBuffer requirements vs existing ChibiRollbackSession

Option (a): no second rollback engine. This maps the requested RollbackInputBuffer capabilities onto the
existing code and lists real gaps. Paths are relative to `Assets/ChibiClash/Scripts/Netcode/`. Line numbers
are from the files as read on 2026-11-06.

No file in the repo (outside `Library/`) mentions `RollbackInputBuffer`, so the requirements are taken from
the brief: deterministic frame history, state save/restore hooks, and the local vs remote prediction loop.

## 1. Requirement mapping

| Requirement | Where it already exists | Status |
|---|---|---|
| Per-player input history keyed by frame | `ChibiP2PPeerLoop.cs:65-68`, rings `_in/_inFrame/_inActual` (128 slots, `RingSize` :45). A slot is valid for frame N only if `_inFrame[p][slot]==N` (:182-205) | Present |
| Actual vs predicted flag per input | `_inActual` (:68), `IsActual` (:201-205) | Present |
| Deterministic step | `ChibiSimulation.Step` (`ChibiSimulation.cs:320`): integer-only, no statics, Rng is in state (`ChibiSimulation.cs:218-220` comment) | Present |
| State save (snapshot per frame) | `Advance()` copies state into `_snap[frame & mask]` before every step (`ChibiP2PPeerLoop.cs:170-180`) using `ChibiSimState.CopyFrom` (`ChibiSimulation.cs:235-240`) | Present, internal only |
| State restore + re-simulate | `Rollback()` (`ChibiP2PPeerLoop.cs:207-225`) restores the first mispredicted frame and re-runs `Advance()` up to the present in the same tick | Present |
| Misprediction detection | `Process()` sets `_firstWrong` when an actual remote input differs from the stored prediction (:259-262) | Present |
| Remote prediction | "repeat newest contiguous remote input": `_predBase` (:77), set at :268, used at :188-190 | Present (one fixed strategy) |
| Local input delay | `InputDelay = 2` (:47); local input for tick T is stored at `_localLast+1` (:147-149) | Present (const, not config) |
| Prediction window / stall | `MaxPrediction = 8` (:48); tick stalls if `_frame - _remoteContig > 8` (:145-153) | Present (const, not config) |
| Confirmed-frame output (replays, spectators) | `OnFrameConfirmed` (:54, emitted :306-314), consumed by `ChibiReplayRecorder` (`ChibiReplay.cs:9-19`) | Present |
| Desync detection | State hash every 30 confirmed frames, compared on both peers (:316-358); `ComputeChecksum` `ChibiSimulation.cs:253` | Present (detect only) |
| Frame-advantage sync | `TimeDilation` up to 1.08 on the leading peer (:289-293) | Present |
| Loss repair | Last up to 8 unacked inputs in every datagram (:276-302); codec `ChibiInputPacket.cs:119-183` | Present, but see bug 2.1 |
| Pure C#, headless-testable | `ChibiRollbackSession` has no Unity types; `ChibiNetSelfTest.Run` (`ChibiTransports.cs:164-211`) runs two peers over a lossy loopback | Present |

Conclusion: every core RollbackInputBuffer capability already exists. The gaps are below.

## 2. Bugs and real gaps (by severity)

### 2.1 HIGH: redundancy window can drop unacked inputs, which deadlocks both peers
- `SendPacket` sends the newest `count` inputs ending at `_localLast`, with `count = _localLast - _remoteAck` capped at `MaxInputs = 8` (`ChibiP2PPeerLoop.cs:278-286`, `ChibiInputPacket.cs:97`; the wire count field is 3 bits, `ChibiInputPacket.cs:133`).
- The unacked span `_localLast - _remoteAck` is not bounded by 8. It is about RTT in ticks plus `InputDelay`, and with stalls it can reach roughly `InputDelay + MaxPrediction` plus RTT (well over 8 at 150 ms RTT, which is 9 ticks).
- When a frame falls out of the window before it is acked, it is never sent again. The remote's `_remoteContig` (:265-269) then cannot pass the hole, so the remote stalls. Its acks stop, our `_remoteAck` freezes, and our window stays on the newest 8 frames, so the hole is never filled. Both peers stall until `OnDisconnect` fires after 600 ticks (:159-163).
- Trigger: loss of about 8 consecutive datagrams (roughly 133 ms of burst loss, common on Wi-Fi), or a start where one peer runs more than 8 frames before the other receives anything. `ChibiNetSelfTest` uses independent per-packet loss (`ChibiTransports.cs:65`), so it is unlikely to hit this.
- Fix options: (a) start the window at the oldest unacked frame (`_remoteAck + 1`) rather than ending it at the newest; (b) widen `count` to 4 bits / 16 inputs (bump the protocol version); (c) also stop sampling local input when `_localLast - _remoteAck >= MaxInputs`. (a) plus (b) is the smallest correct change. Add a burst-loss mode to `ChibiLoopbackLink` to cover it with a test.
- The NetworkRoomManager draft avoids the start-up variant by pushing the first 0xC7 datagram back into the transport instead of dropping it. It cannot avoid burst loss.

### 2.2 MEDIUM: disconnect handling is silence-only and nobody listens
- The only signal is 600 silent ticks (10 s), from `DisconnectTicks` (:50) at :159-163. There is no "goodbye" datagram, so a peer quitting a match takes 10 s to show.
- `_disconnected` clears on any packet (:238), so `OnDisconnect` can fire repeatedly on a flapping link.
- `ChibiP2PPeerLoop.Begin` subscribes to neither `OnDisconnect` nor `OnDesync` (:380-391), and nothing in `Game/` references the netcode. GameManager must subscribe to both.
- Transport-level disconnects (`ChibiRelayTransport.State == Disconnected` in the draft) are not visible to the session. Suggest a `ChibiP2PPeerLoop` check of `(transport as ChibiRelayTransport)?.State` each Update, or an optional `IChibiTransport` status member.
- No reconnect or resync. That would need serialising `ChibiSimState` (only `CopyFrom` exists, no byte serializer) plus input-history transfer. Out of scope; flag to game-designer how a mid-match drop should resolve (forfeit, draw, or AI takeover).
- Silence is counted in ticks, not wall time. A backgrounded mobile client stops ticking and never times out locally; only its peer does.

### 2.3 MEDIUM: rollback wire format is not versioned, and config agreement is not checked
- Rollback datagrams carry only magic `0xC7` (`ChibiInputPacket.cs:121`), with no version byte. This breaks the "all network messages versioned" rule.
- `ChibiMatchConfig` (`ChibiSimulation.cs:33-38`: Seed, classes, passives) must be identical on both peers, but nothing verifies it. A mismatch only shows as `OnDesync` at frame 30.
- The draft handshake (`ChibiHandshake`, magic 0xC8, version byte) carries the config from host to joiner, and the lobby filters on `Application.version + "-p" + Version`. Recommended follow-up: include a hash of the config and the move table (`ChibiMoveTable`) in the handshake, so balance-data mismatches between builds are refused before the fight.

### 2.4 MEDIUM: no offline session for Mode 3 (VS AI)
- `ChibiRollbackSession` needs a transport and a remote peer (:109-134). `ChibiP2PPeerLoop` is hard-typed to it (:372, :384). No local/offline session type exists.
- Needed: a `ChibiLocalSession` that steps `ChibiSimulation.Step` with local input plus AI input. It needs no snapshots, no transport, `TimeDilation = 1`, and the same `State`/`Frame`/`Tick()` surface. Put both behind a small `IChibiSession` interface so the peer loop and renderer are shared. `OnFrameConfirmed` can then be raised every frame, so replays still work.
- No AI input source exists for the deterministic sim. `ChibiScriptedInputSource` (`ChibiTransports.cs:104-111`) and `ChibiNetSelfTest.Bot` (:214-232) are open-loop scripts. A reactive bot needs read access to `ChibiSimState` (pass it in the constructor) and must be deterministic if VS-AI replays should verify.
- Observed but not traced end to end: the shipped VS CPU / Local Versus / Training modes (`Game/GameManager.cs:286-289`) do not reference `ChibiClash.Netcode` at all. Grep found no netcode references in Game/Core/UI/Fighter/Combat. They appear to run the float-based MonoBehaviour combat path (`Core/CombatAuthority.cs` uses `float`/`Mathf`). That means two combat implementations. Online play on `ChibiSimulation` will not match offline play unless offline moves onto `ChibiSimulation` too. Raise this with lead-programmer and gameplay-programmer. Also `GameManager.cs:288` still says "needs Photon Fusion backend".

### 2.5 LOW: no hooks for state outside ChibiSimState and no rollback notification for presentation
- Save/restore is internal (`CopyFrom`). There is no external hook, by design, because the whole sim is in `ChibiSimState`. That is correct for gameplay.
- But the view layer gets no "rolled back from F to G" event. If VFX or audio trigger off state changes seen in `Session.State`, they can double-fire or play for hits that a rollback removes. Suggest `event Action<int,int> OnRollback` raised in `Rollback()` (:207-225), plus the convention that one-shot effects key off confirmed frames or are idempotent per (frame, event id).

### 2.6 LOW: tuning constants are hard-coded
- `InputDelay`, `MaxPrediction`, `ChecksumInterval`, `DisconnectTicks` (:47-50) and `MaxTicksPerRenderFrame` (:370) are `const`. The project rule is that sync parameters live in config.
- `InputDelay` must be equal on both peers, because frames `[0, InputDelay)` are pre-seeded neutral (:126-133). If it becomes config, it must go through the handshake.
- Suggested starting values for a 150 ms RTT target: InputDelay 2 (33 ms), MaxPrediction 8 (133 ms), and a disconnect timeout of 3-5 s once a goodbye message exists.

### 2.7 LOW: catch-up after a hitch only slows the leader
- After a render hitch the loop drops the backlog (`ChibiP2PPeerLoop.cs:408`), so that peer falls behind.
- `TimeDilation` only slows the peer that is ahead, by up to +8% (:292-293); the one behind never speeds up. Convergence relies on the leader stalling at `MaxPrediction`, which shows as hitches on the leader.
- Acceptable for now; consider symmetric dilation (0.92-1.08).

### 2.8 Notes for security-engineer (P2P, no authority)
- Inputs from the wire are sanitised (`ChibiInputPacket.cs:71-76`, :82), so a peer cannot inject illegal input values. It is still authoritative over its own inputs, as in any P2P rollback game, so input macros cannot be prevented.
- A malicious peer can send `AckFrame` beyond our `_localLast` (`:247`). That makes `count` fall to 1 (:279), which turns off our redundancy and makes bug 2.1 reachable at will. Clamp `_remoteAck` to `_localLast`.
- A malicious peer can also send a huge `LastFrame` (:245). The effect is bounded: the wire clamps advantage to -8..7, and `TimeDilation` is capped at 1.08.
- Relay + DTLS (the draft's default) limits datagrams to the two allocation members and encrypts them. Relay always relays, so there is no direct IP exposure between players.

### 2.9 Not gaps (checked)
- **Snapshot ring overflow:** the rollback depth is at most about `MaxPrediction + 1` against a 128-slot ring. The throw at :215-216 is unreachable in practice and works as an assertion.
- **Remote input ring:** frames more than 64 ahead are discarded (:254).
- **24-bit wire frame counter:** wraps after about 77 h (`ChibiInputPacket.cs:117-118`), far beyond a 180 s match (`ChibiSimConst.MatchTicks`).
- **Prediction strategy:** "repeat last input" is safe for attacks, because press edges are derived from `PrevInput` inside the sim (`ChibiInputPacket.cs:34-35`), so a predicted held button never creates a new press. Only one strategy exists and it is not pluggable. Fine for 1v1.

## 3. Unsupported by design (would need a new design, not a patch)
- **Spectators:** none. A spectator needs only the confirmed input stream (`OnFrameConfirmed`) plus `ChibiMatchConfig`, so it can be relayed by one peer or uploaded as a live replay without touching the session. That would be a new message type and Relay connection count (`maxConnections` > 1).
- **More than 2 players:** not supported anywhere. `_remote = 1 - localIndex` (:112), 2-element arrays (:66-68), `ChibiSimState.F` has 2 fighters (`ChibiSimulation.cs:227`), `Step(state, in0, in1)` (:320), and the datagram has no player id. Supporting it is a rewrite of the session and sim API, not an extension.

## 4. Bandwidth and network numbers (for devops-engineer)

These are for one player, in each direction:
- **Rate:** 60 datagrams/s at a 60 Hz tick (`ChibiSimConst.TickRate`).
- **Payload:** 11 B typical, 28 B worst case (`ChibiInputPacket.cs:116`), so 0.66-1.7 KB/s.
- **On the wire:** with UDP/IP, Relay and DTLS headers (estimated 60-80 B per datagram, not measured), about 4-6 KB/s.
- **Priority tiers:** one. The input stream is the only in-match traffic.
- **Protocol:** Unity Relay over DTLS (UDP). Relay always relays; there is no NAT punch-through and no direct P2P path.
