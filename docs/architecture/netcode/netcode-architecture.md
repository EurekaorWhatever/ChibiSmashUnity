# Chibi Clash: Networking Architecture and Data Flow

| Field | Value |
|---|---|
| Status | DRAFT (Proposed). Not an ADR yet; the open decisions in section 7 must be settled first |
| Date | 2026-11-06 |
| Engine | Unity 6.6 (6000.6.7f1). Note: `docs/engine-reference/unity/VERSION.md` pins 6.3 LTS, so the 6.6 / transport 6.6.0 APIs are **not covered by the local reference**. Verify every UGS/UTP call against the installed package docs |
| Packages | `com.unity.services.multiplayer` 2.4.1, `com.unity.transport` 6.6.0, `com.unity.ai.inference` 2.6.1 (MoveNet). No NGO, no MediaPipe/OpenCV |
| Fixed decision | `ChibiRollbackSession` is **the** rollback engine. New code: `NetworkRoomManager`, `ChibiRelayTransport : IChibiTransport` |

---

## 1. Topology

```
           Unity Authentication (anonymous sign-in)      Unity Lobby (discovery only)
                    │  player id / token                     │ room code, quick-join,
                    ▼                                        ▼ match config, relay join code
   ┌──────────── Peer A (host, sim index 0) ────────────┐   ┌──── Peer B (joiner, sim index 1) ────┐
   │ full ChibiSimulation + ChibiRollbackSession         │   │ full ChibiSimulation + session       │
   │ ChibiRelayTransport ─ UTP NetworkDriver             │   │ ChibiRelayTransport ─ NetworkDriver  │
   └───────────────────────┬────────────────────────────┘   └───────────────┬──────────────────────┘
                           │ UDP (or DTLS)                                   │
                           └──────────────►  Unity Relay server  ◄───────────┘
                                         (one region, chosen at allocation)
```

- **Logical P2P, physical relay.** There is no game server: each peer runs the full deterministic sim and
  only inputs travel. But Unity Relay forwards **every** datagram (A -> Relay -> B). There is no NAT
  punch-through and no upgrade to a direct connection. The cost is the extra hop; the benefits are that it
  always connects and that neither peer sees the other's IP.
- **Relay is host/client at the allocation level.** The host creates an allocation for 1 connection and gets a
  Relay join code. The joiner joins with that code. The rollback protocol on top is symmetric.
- **Lobby only does discovery.** It carries the room code, protocol version, `ChibiMatchConfig` and the Relay
  join code. When the match starts it is idle (or deleted). No gameplay goes through Lobby.
- **Auth:** `UnityServices` initialise, then `AuthenticationService` anonymous sign-in. This is needed for
  Lobby and Relay. Identity is per device, which is not enough for ranked trust (see 7.6).
- **Existing alternative:** `ChibiUdpTransport` (ChibiTransports.cs:15) is a direct UDP socket with no NAT
  traversal. Keep it for LAN and dev testing only.

## 2. Data flow (one 60 Hz tick)

```
 LOCAL PEER
 ── render frame (Update, variable rate) ───────────────────────────────────────────────────────────
  Keyboard / Gamepad ──► DeviceInputSource (NEW; uses Core/Bindings.cs + gamepad) ─┐
  Webcam ─► WebcamFeed ─► MoveNetPoseSource (~20 Hz, CameraPose.cs:15)             │ PollRenderFrame():
        ─► GestureTracker.Tick (Stable after 0.1 s, Pose.cs:190)                   │ latch taps + gesture
        ─► TelemetryValidator gate (Pose.cs:127) ─► gesture id (0..15) ────────────┤
                                                                                    ▼
                                           CompositeInputSource (NEW) : IChibiInputSource
 ── sim tick (ChibiP2PPeerLoop.Update accumulator, 1/60 s x TimeDilation, P2PPeerLoop.cs:393) ──────
  SampleTick() ─► ChibiInputPacket.Raw  (bits 0-11 today; bits 12-15 = gesture id once WireBits = 16)
        ▼
  ChibiRollbackSession.Tick()                                   (ChibiP2PPeerLoop.cs:139)
    1 PumpNetwork  ◄── IChibiTransport.Receive ◄── remote datagrams
        Process(): remote input ring (_in/_inFrame/_inActual, 128 slots), _remoteContig, _predBase,
        _firstWrong = earliest mispredicted frame
    2 Rollback(): restore snapshot[_firstWrong], re-run Step up to the present frame (depth <= MaxPrediction 8)
    3 Store local input at frame _localLast+1  (= present frame + InputDelay 2)
    4 Advance(): snapshot -> ChibiSimulation.Step(state, in0, in1); missing remote input = repeat _predBase
       (stall instead if present frame - _remoteContig > MaxPrediction)
    5 EmitConfirmedFrames ─► ChibiReplayRecorder.Add
    6 RunChecksums (every 30 confirmed frames, FNV of ChibiSimState)
    7 SendPacket: last <= 8 unacked local inputs + ack + advantage + latest checksum
        ─► ChibiPacketCodec.Write (BitBuffer, magic 0xC7) ─► IChibiTransport.Send
        ▼
  ChibiRelayTransport (NEW)  -- demux on byte 0: 0xC7 session / 0xC8 telemetry / 0xC9 control
        ─► UTP NetworkDriver (Relay parameters, UNRELIABLE pipeline) ─► Unity Relay ─► remote peer
 ── render ─────────────────────────────────────────────────────────────────────────────────────────
  Session.State (+ RenderAlpha = _acc / TickSeconds) ─► ChibiSimView (NEW) ─► FighterVisual / FX / HUD

 REMOTE PEER: mirror image. Its ChibiRelayTransport queues our 0xC7 datagram -> its session.Process
 -> its remote ring -> possibly rollback -> Step -> same state (checked by checksum every 30 frames).
```

**ChibiRelayTransport contract** (the only new piece in the hot path):
- Owns one `NetworkDriver` built from `NetworkSettings` with Relay server data (from the `Allocation` /
  `JoinAllocation`), one `NetworkConnection`, and the default **unreliable** pipeline. Do not put inputs on
  a reliable or sequenced pipeline. The session already repairs loss by sending the last 8 inputs again and
  tolerates reordering, so reliability on top would only add head-of-line latency.
- `IChibiTransport` is pull-based and synchronous (P2PPeerLoop.cs:7-11). The transport pumps the driver
  (schedule update, complete, accept on the host, pop events into a fixed ring of preallocated buffers) the
  first time `Receive` finds its queue empty in a drain cycle. `PumpNetwork` (P2PPeerLoop.cs:229) loops until
  `Receive` returns 0, so pump once per cycle, not once per call. It must not allocate per packet.
- `Send` queues a datagram and flushes right away (UTP 2.x+ has a flush-send job; verify on 6.6.0).
  Otherwise each input waits up to one render frame before it leaves.
- Disposal: `ChibiP2PPeerLoop.OnDestroy` already disposes an `IDisposable` transport (P2PPeerLoop.cs:413).
  `NetworkDriver` is native memory, so this is required.
- It reports connection loss to `NetworkRoomManager` through a separate event. The session keeps its own
  10 s `DisconnectTicks` (P2PPeerLoop.cs:50).

**NetworkRoomManager (NEW, MonoBehaviour or plain service):** initialises services and signs in; creates or
joins rooms; publishes and reads `ChibiMatchConfig`, the protocol version and the Relay join code; builds
the `ChibiRelayTransport`; waits for the connection and a control-channel `Ready` from both sides; then
calls `ChibiP2PPeerLoop.Begin(localIndex, cfg, transport, source)`. Host = index 0, joiner = index 1.
The host generates `Seed`.

## 3. Modes

| | Mode 1: Private Room | Mode 2: Matchmaking | Mode 3: vs AI |
|---|---|---|---|
| Discovery | Private lobby; host shares its code | Lobby QuickJoin with filters (protocol version, mode); create one if none found | none |
| Transport | `ChibiRelayTransport` | `ChibiRelayTransport` | **none** |
| Local input | `CompositeInputSource` (device + gesture) | same | same |
| Opponent input | remote ring in `ChibiRollbackSession` | same | `ChibiAiInputSource` (NEW) |
| Driver | `ChibiP2PPeerLoop` + `ChibiRollbackSession` | same | local loop (NEW, see below) |

**Mode 1.** The host signs in, creates a Relay allocation (1 connection) and a private lobby, and writes the
Relay join code, protocol version and its config half into lobby data. The friend enters the lobby code,
reads the Relay join code, joins the allocation and adds their class and passive. The host locks the config
and both peers start. Share the **lobby** code, not the Relay code: the lobby is where the config is
negotiated, and it can be rejoined.

**Mode 2.** QuickJoin with filters. If nothing is found, wait a randomised backoff, retry once, then create a
public lobby and wait in it. This avoids the classic race where two searchers both create lobbies and
never meet. There is no skill or latency matching in this path.
*Honest note on Unity Matchmaker:* the brief says it is "designed for server allocation". That is
out of date. Current Multiplayer Services docs describe Matchmaker pools for **client-hosted
(P2P)** games through sessions with Relay, free when hosting goes through Relay. Unity's pages disagree on
which network option such a pool needs (Relay vs. direct), so verify on 2.4.1.
Trade-off: Matchmaker gives rules (skill, QoS region) and removes the create/join race. It costs dashboard
queue and pool setup, a ticket wait, and learning the sessions flow. Recommendation: ship Lobby QuickJoin
first, behind a `FindMatch()` seam in `NetworkRoomManager`, and swap to Matchmaker when ranked or
skill-based matchmaking is greenlit.

**Mode 3: what the existing code supports, and what is missing.**
- Supported: `ChibiSimulation.Init/Step` is a pure function that can be driven with no session at all.
  `ChibiReplay.Simulate` (ChibiReplay.cs:46-53) is exactly such a loop. `ChibiScriptedInputSource`
  (ChibiTransports.cs:104) shows the `IChibiInputSource` shape for a bot. `ChibiNetSelfTest` runs two
  sessions over a zero-latency `ChibiLoopbackLink`, which works but simulates everything twice and adds
  2 frames of input delay for no reason.
- **Not supported:**
  (a) No offline session or loop. `ChibiRollbackSession` requires a transport and a live remote. With a null
  transport it stalls after 8 frames (P2PPeerLoop.cs:145) and raises `OnDisconnect` after 600 ticks.
  (b) `ChibiP2PPeerLoop.Session` is typed `ChibiRollbackSession` (P2PPeerLoop.cs:372), so the loop cannot
  host an offline driver without a small refactor (an `IChibiSimDriver` exposing `State`, `Tick`,
  `TimeDilation`).
  (c) No sim-side AI. `CpuInput` (FighterInput.cs:139-228) is written against `Fighter`, transforms,
  float `dt` and `UnityEngine.Random`. It must be ported to read a `ChibiSimState` (read-only) and use its
  **own** `ChibiRng`. Drawing from `state.Rng` would change the sim outside `Step` and break replays.
  (d) Training mode (T key refills the ultimate, MatchController.cs:131) has no sim equivalent.
- Proposed: a `ChibiLocalSession` that steps `ChibiSimulation` directly with two `IChibiInputSource`s
  (InputDelay 0, no prediction, records a replay), hosted by the same loop. Local Versus and Training can use
  it too.

## 4. CV gesture input and telemetry

**Gesture as input (deterministic part).**
- Encode the *stable* gesture as a **4-bit id** in bits 12-15: `Gesture` enum value (ClassData.cs:7, today
  None/CrossArms/PunchForward/RaiseHands), 0 = none. Use an id rather than one bit per gesture: the
  classifier returns exactly one gesture, and an id leaves room for 15 gestures instead of 4.
- It is held *state*, like the buttons (ChibiInputPacket.cs:34-35). Gesture onset is then an edge derived in
  `Step`, prediction ("repeat last") behaves well, and rollbacks stay exact.
- Change: `WireBits` 12 -> 16 (ChibiInputPacket.cs:39). Extend `Sanitized()` (ChibiInputPacket.cs:71-76) to
  zero ids greater than the highest `Gesture` value.
- Local gating: send id 0 when `TelemetryValid` is false, mirroring Fighter.cs:373. **The remote peer cannot
  verify this gate.**
- The sim has **no gesture rules yet**: no ultimate gesture window, super jump, pose fireball or CrossArms
  block (Fighter.cs:261, 373-430). This is a parity gap (7.1).
- Camera ownership is keyed on sim index 0 (`AutoPoseSource.UsingReal`, CameraPose.cs:165). An online joiner
  (index 1) would never get the real camera. It must key on "local player" instead.
- Latency budget: up to 50 ms MoveNet interval + 100 ms stability window + 33 ms InputDelay ≈ 150-190 ms.
  That is acceptable for ultimates and specials, not for frame-tight actions.

**Optional telemetry payload (heuristic part).**
- A separate datagram with magic **0xC8**, demuxed by `ChibiRelayTransport` so `ChibiPacketCodec` and the
  session stay unchanged. It is sent once per gesture onset, repeated in the next 2 ticks as cheap
  redundancy:
  `8 magic | 24 onset sim frame | 4 gesture id | 8 window ticks | 4 samples | 3 validator flags
  (low-conf, frozen, snap) | 4 mean confidence | 32 hash` = **87 bits = 11 bytes**, a few times a minute.
- The hash is FNV-1a over quantised keyframes (9 landmarks x/y at 8 bits each, for the samples in the
  gesture window).
- **What it is not:** proof. In P2P the client computes the hash, so a macro or a modified client can
  synthesise plausible keyframes and hash them, or skip the camera entirely. No party in this topology can
  check the hash against real video.
- **What it is good for:** heuristic signals only.
  - Byte-identical hashes across repeated gestures suggest a replayed landmark stream, because real camera
    noise rarely survives quantisation unchanged.
  - Onset-timing distributions (frame-perfect reactions to the ultimate becoming ready).
  - Validator-flag rates.
  - A commitment that can be audited later if raw keyframes are ever uploaded. Pose data is
    biometric-adjacent, so any upload needs consent and privacy review.
- Existing comments contradict this topology and must be corrected:
  - "the same checks run again on the server against the uploaded telemetry" (Pose.cs:113).
  - "runs on the authoritative server only" (CombatAuthority.cs:22-24).
  - "regional dedicated servers ... Photon Fusion" in the mode card (GameManager.cs:288).

**Bandwidth (real layout, ChibiPacketCodec ChibiInputPacket.cs:110-182).**
Header = 8 magic + 24 lastFrame + 24 ack + 4 advantage + 3 count = 63 bits. First input = W bits; each next
input = 1 bit (repeat) or 1+W bits. Checksum section = 1 bit flag, plus 56 bits when set.
Online, `count` sits at 6-8, because it covers everything not yet acked (RTT plus InputDelay). Once frame 30
is hashed, **every** packet carries the latest checksum (`HasChecksum = _latestHashFrame >= 0`,
P2PPeerLoop.cs:295). The codec comment's "typical 11 bytes" (ChibiInputPacket.cs:116) therefore applies
only to the first half second.

| Case (count = 8) | W = 12 (today) | W = 16 (gesture) |
|---|---|---|
| Held inputs, no checksum (frames < 30 only) | 83 b = 11 B | 87 b = 11 B |
| Held inputs + checksum (steady state) | 139 b = **18 B** | 143 b = **18 B** |
| Every input differs + checksum (worst) | 223 b = 28 B | 255 b = **32 B = MaxPacketBytes** |

- Payload rate: 60 packets/s x 18-32 B ≈ 1.1-1.9 KB/s per direction.
- Framing overhead dominates: UDP/IPv4 is 28 B, plus Relay/UTP headers, plus a DTLS record if DTLS is used.
  The UTP/Relay header sizes are **not verified here, measure them**. Assuming about 60 B in total, the wire
  rate is about 4.7-5.5 KB/s ≈ 40 kbps per direction. Fine for any broadband or 4G link.
- **At W = 16 the worst case fills `MaxPacketBytes` (32) exactly, with zero headroom.** `BitBuffer.Write`
  silently skips an oversized write but lets later, smaller writes succeed (BitBuffer.cs:61). `SendPacket`
  never checks `Overrun` (P2PPeerLoop.cs:299-301). Any future field could therefore emit a corrupt packet,
  which the receiver drops as garbage.
- Before widening: raise `MaxPacketBytes` to 48 and assert `!Overrun` after `Write`. This is a reason to
  keep telemetry in its own message.

## 5. Determinism risks

**A. ChibiSimulation / rollback stack.** The integer core is sound: no floats, no statics with mutable state,
no Unity calls, and RNG lives in state. The remaining risks are contract and versioning risks:

| # | Risk | Where | Mitigation |
|---|---|---|---|
| A1 | `ChibiMatchConfig` (seed, classes, passives) and the local index never go on the wire. A mismatch desyncs at the first checksum | ChibiSimulation.cs:33-38; session ctor P2PPeerLoop.cs:109 | Host-authored config in lobby data; both sides echo a config hash in a control `Ready` before `Begin` |
| A2 | Move table indexed by `(int)FighterClass`; reordering the enum or changing balance between builds desyncs | ChibiSimulation.cs:62, 82-128; ClassData.cs:5; ChibiCombatStats.cs:87-122 | Protocol and content version in lobby filters; reject mismatches before connecting |
| A3 | `Stats` is copied but not hashed, so a stats-table mismatch only shows up once HP or positions diverge | ChibiSimulation.cs:139, 177-201 | Covered by A2 version gate; optionally mix a stats hash into the config hash |
| A4 | `WireBits` / `Sanitized()` decide which bits reach `Step`. Peers with different `WireBits` silently disagree on inputs | ChibiInputPacket.cs:39-40, 71-76; ChibiSimulation.cs:324-325; P2PPeerLoop.cs:148 | Same as A2: wire layout is part of the protocol version |
| A5 | Packet overrun can corrupt a datagram (see 4) | BitBuffer.cs:61; P2PPeerLoop.cs:299-301 | Assert `!Overrun`; raise capacity |
| A6 | Rollback throws if the snapshot was overwritten. Today this cannot happen (8 < 128), but an uncaught throw in `Update` would kill the match | P2PPeerLoop.cs:215-216 | Keep `MaxPrediction` well below `RingSize`; catch, then end the match as desync |
| A7 | Future AI or presentation code mutating `ChibiSimState` or drawing `state.Rng` outside `Step` | session exposes a mutable `State`, P2PPeerLoop.cs:103 | Read-only view for presenters and AI; AI gets its own `ChibiRng` |
| — | Safe, noted for reviewers: `TimeDilation` / `RenderAlpha` floats only affect *when* ticks run, not state (P2PPeerLoop.cs:101, 293, 374). Integer division and `%` are defined identically on all platforms (ChibiCombatStats.cs:161). No `Dictionary`/`HashSet` exists anywhere under `Scripts/` (grep). Inactive projectile slots are fully overwritten on spawn (ChibiSimulation.cs:584-585) | | Keep it that way: add a determinism lint to code review |

**B. Live Fighter path (used by every match today).** It cannot be rolled back or kept in lockstep. These are
listed so nobody tries to network it:
- **Unity 2D physics:** `Rigidbody2D` + `BoxCollider2D` per fighter (MatchController.cs:87-88; Fighter.cs:92-96,
  with Continuous collision and interpolation). Velocity writes at Fighter.cs:178-190, 619, 667. Ground
  check uses `Physics2D.OverlapBox` + `foreach` in broadphase order (Fighter.cs:244-246, 351-355). Minions
  also run on `Rigidbody2D` (CombatObjects.cs:137).
- **Floats everywhere:** HP and ultimate (Fighter.cs:27); damage formulas (CombatAuthority.cs:34-55).
- **Variable timestep and time scale:** `Time.deltaTime` (Fighter.cs:171; CombatObjects.cs:46, homing
  `Vector2.Lerp` at :54). `Time.timeScale` hitstop and slow-mo (MatchController.cs:108-116). Absolute
  `Time.time` timers (Fighter.cs:118, 130, 141-148, 232-235, 445, 457, 550, 563, 605, 653, 659).
  Unscaled time for gestures and the ultimate window (Fighter.cs:172, 210, 411).
- **Coroutines with `WaitForSeconds`** hold gameplay state that cannot be snapshotted (Fighter.cs:465-470,
  727-747, 784).
- **`UnityEngine.Random` in gameplay:** crits (CombatAuthority.cs:45); ward, hex and armour break
  (Fighter.cs:574, 647-648); arrow spread (Fighter.cs:871, 1025-1027); CPU AI (FighterInput.cs:176-217).
  VFX-only Random (CombatObjects.cs:244-245, 304-306) is harmless.
- **Order dependence:** the two `Fighter.Update` calls run in Unity's unspecified same-type order.
  `GetComponentsInChildren<Minion>` order (CombatObjects.cs:144).
- **Different rules from the sim:** float timer with an `Approximately` tie (MatchController.cs:135-140)
  vs. integer `MatchTicks` with a permille compare (ChibiSimulation.cs:754-758).

## 6. Performance budget (proposed)

| Item | Budget | Basis |
|---|---|---|
| Rollback worst case per tick | 8 re-sim `Step` + 1 new + snapshots ≤ **1.5 ms** on min spec | MaxPrediction 8 (P2PPeerLoop.cs:48) |
| Hitch worst case per render frame | 5 ticks (P2PPeerLoop.cs:370) x 9 steps ≈ 45 `Step` | measure with `ChibiNetSelfTest` at 6±2 ticks, 10% loss |
| GC alloc in session + transport per tick | **0 B** | session is alloc-free today; keep the transport alloc-free |
| Playable latency ceiling | stalls begin when one-way ≳ (InputDelay + MaxPrediction)/60 ≈ 167 ms | P2PPeerLoop.cs:145 |
| Bandwidth | ≤ 8 KB/s per direction including overhead | section 4 |

## 7. Gaps and open decisions

1. **Which sim is canonical? (biggest decision.)** Live matches use `Fighter` + `MatchController`.
   `ChibiSimulation` is not wired in, and no code outside `Netcode/` references it. Online *requires*
   `ChibiSimulation`.
   - **Option A (recommended):** one sim for all modes. `MatchController` drives `ChibiP2PPeerLoop` or
     `ChibiLocalSession`, and `Fighter` becomes a view.
   - **Option B:** keep Fighter offline and use the sim online. That means two implementations of every move
     that drift apart, and offline play that does not match online.
   - Parity gaps either way: dash (Fighter.cs:309), platforms and drop-through (Fighter.cs:221, 351), multiple
     stages (sim has one flat stage, ChibiSimulation.cs:15), pose specials and gesture block, the ultimate
     gesture window (Fighter.cs:396-430), minions and thunder cloud (CombatObjects.cs:119, 175), hitstop and
     cinematics, training.
   - Content scope goes to `game-designer` / `creative-director`.
2. **Presentation layer for the sim (missing).**
   - `ChibiSimView`: map `ChibiSimState` to `FighterVisual`, interpolating with `RenderAlpha`. The renderer
     must keep its own previous-state copy, and must clamp alpha because `TimeDilation` lets it exceed 1.
   - FX and audio are derived by diffing state across ticks, with suppression for effects that a rollback
     undoes.
   - Hitstop must become sim frames or visual-only. `Time.timeScale` cannot pause an online sim.
   - Escape-to-pause cannot exist online.
3. **Input sources (missing).** `ChibiKeyboardInputSource` is hardcoded WASD/J/K/L and ignores `Bindings` and
   gamepads (ChibiTransports.cs:118-157). Its mapping also differs from `HumanInput` (FighterInput.cs:28-33).
   Needed: `DeviceInputSource`, `GestureInputSource` (owns the `GestureTracker` tick that `Fighter.Update`
   does today, Fighter.cs:172), `CompositeInputSource`, `ChibiAiInputSource`.
4. **UGS API surface.**
   - **Option A:** sessions API (`MultiplayerService` / `ISession` / `SessionOptions` with Relay) plus a
     custom `INetworkHandler`. That turns off the built-in NGO/N4E integration and hands us the Relay
     connection data for UTP.
   - **Option B:** the lower-level Lobby and Relay service APIs bundled in the same package.
   - Recommendation: A if the handler exposes the Relay data cleanly on 2.4.1 (it also handles lobby
     heartbeats and gives the path to Matchmaker); otherwise B. Spike for 1 day before committing.
5. **Lobby QuickJoin vs. Matchmaker for Mode 2.** See section 3. Recommendation: QuickJoin now, Matchmaker
   later.
6. **Ranked integrity (escalate to `game-designer` / `creative-director`).**
   - The GDD and code claim server-side hit and damage validation (CombatAuthority.cs:22-24). Pure P2P cannot
     deliver that.
   - Inputs-only rollback does stop state injection. It does not stop macros, input bots, a modified client
     whose desync cannot be attributed (with 2 peers, nobody knows who is right), or lag manipulation (bounded
     by `MaxPrediction` and time dilation).
   - Ranked would need either a trusted arbiter, for example uploading both replays (`ChibiReplay.Verify` is
     already deterministic) for server re-simulation, or acceptance that ranked is casual-trust.
7. **Protocol versioning.** Add protocol, content and build version to lobby data and the QuickJoin filter. A
   peer with a different layout is otherwise dropped silently by `TryRead` and looks like a 10 s disconnect.
8. **Control channel (0xC9).** Hello (version, config hash), Ready, Rematch, Forfeit. This one can use a
   reliable pipeline because it is off the hot path.
9. **Desync and disconnect policy.** What happens on `OnDesync` (P2PPeerLoop.cs:52) and `OnDisconnect`? A
   no-contest result? Who is credited? Is a replay kept for bug reports?
10. **Rewards and economy.** `AwardRewards` treats `F[0]` as "me" (MatchController.cs:200-202), which is wrong
    for an online joiner (index 1). Coins, including posture coins from the client's own CV score
    (MatchController.cs:208-211), are client-authoritative. That is acceptable for casual play and exploitable
    for anything competitive.
11. **Connection settings.** UDP vs. DTLS for Relay (recommended: DTLS; it protects against tampering in
    transit, not against a cheating peer). Send rate: 60 Hz, or 30 Hz using the existing 8-input redundancy to
    halve header overhead. Relay region: QoS pick by the host, so the joiner pays any cross-region hop.
12. **Verification.**
    - Extend `ChibiNetSelfTest` into Unity Test Framework EditMode tests under `Assets/Tests/EditMode/Netcode/`:
      determinism, replay, gesture bits and packet overrun.
    - Add a PlayMode two-instance Relay smoke test.
    - We will know this design is right when two builds on different networks complete 20 matches with
      0 desyncs, rollback depth p95 ≤ 4, and no stalls at ≤ 120 ms RTT.

---
Sources (UGS behaviour stated above): https://docs.unity.com/en-us/mps-sdk/matchmake-session ,
https://docs.unity.com/en-us/mps-sdk/faq , https://docs.unity.com/en-us/mps-sdk/manage-session-network-connection
