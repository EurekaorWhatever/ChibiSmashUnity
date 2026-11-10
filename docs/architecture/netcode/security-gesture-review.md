# Security review: CV gesture input, PoseVerified bit, P2P receive path
Scope: read-only. ChibiInputPacket.cs, ChibiPlayerInputSource.cs, CV/Pose.cs, CV/CameraPose.cs, ChibiP2PPeerLoop.cs,
ChibiSimulation.cs (input reads only), netcode-architecture.md section 4. Online PvP: rated as multiplayer (HIGH counts as CRITICAL).
Root fact: P2P over Relay with no authority. Each peer runs the same deterministic sim on the same inputs, so a peer can
send any legal 16-bit value per frame and nobody can check where it came from. Nothing on the wire can prove "a camera saw a body".

## 1. What a modified client can fake today
| Fake | How | Impact (casual-competitive P2P) |
|---|---|---|
| PoseVerified on every packet | Set bit 15 (ChibiInputPacket.cs:27). The only gate is local: ChibiPlayerInputSource.cs:117, :80 | **Low today.** Only the debug HUD reads it (ChibiSimTestHud.cs:86). Would be Critical if the sim ever reads it (see 2) |
| Gestures from a macro | Write bits 13-14 straight into the packet, or feed the SimulatedPoseSource (Pose.cs:210-243). Its exp-smoothed lerp plus 0.003 jitter and fixed 0.95 confidence passes every TelemetryValidator check: about 7.5 u/s peak, under MaxJointSpeed 14 (Pose.cs:117); jitter defeats the frozen check (:135); confidence stays above 0.3 (:141) | **Low.** Keyboard players already make gestures through the simulated-gesture keys (ChibiPlayerInputSource.cs:72), so a macro adds timing precision only |
| Spoof TelemetryValid | A client-side bool (Pose.cs:124, :159) that starts true. IsLive is client-side too (Pose.cs:161, CameraPose.cs:165) | **Low.** Same value as setting bit 15 |
| Replay a recorded pose stream | A recorded real MoveNet landmark stream is not frozen and does not snap, so it passes all three checks (Pose.cs:141-144). Only a byte-identical loop with more than 20 frozen samples (:142) is caught | **Low.** Same reachability as a keyboard player |
TelemetryValidator catches broken cameras and naive spoofs. It is not anti-cheat. Pose.cs:113 says "the same checks run again on the
server". That is false in this topology (doc §4 already flags this).

## 2. Receiving side: does anything trust bits 12-15?
- Receive path: TryRead (ChibiInputPacket.cs:195-223) -> Read -> Sanitized (:98-103) -> Process stores the raw value
  (ChibiP2PPeerLoop.cs:259-262). No branch reads gesture or PoseVerified.
- Sim: UpdateFighter computes `edges = raw & ~PrevInput` (ChibiSimulation.cs:382-384). Edges are consumed only through
  ActionMask (:308-310, :404) and Jump (:443). Ultimate is gated by `f.Ult >= UltMax` (:452). Shield is :428, direction :438.
  Bits 12-15 have no effect on gameplay. They do enter PrevInput, which is hashed (:226). That is harmless, because both
  peers hash identical inputs.
- Other readers in Assets: only ChibiSimTestHud.cs:83-86 (display). ChibiLocalSession.cs:57-58 and P2PPeerLoop.cs:148 only mask.
- **Verdict:** today nothing trusts the gesture bits or PoseVerified in a way that changes gameplay.

Rules the sim MUST follow when gestures go live (step 15). Each forged bit must buy no more than a keyboard player gets:
1. **The sim never reads PoseVerified (bit 15).** No damage, meter, cooldown or unlock may depend on it. In P2P, a sim branch on a
   forgeable bit is an authority bypass (HIGH, so CRITICAL in multiplayer). If design wants "camera-only" perks, they cannot be enforced. Say so in the GDD.
   *Addendum (protocol 3, rules v18, 2026-11-10):* bit 15 is the Special button; no camera claim travels in the packet; the
   HUD's verified tick is local only. Special is an ordinary button with the same gates as the others, so the rule's intent
   (no gameplay from a self-reported camera claim) still holds.
2. Treat a gesture as a held button. Derive the onset edge in Step from PrevInput, the same way buttons already work.
   Apply the stability window (GestureTracker's 0.1 s, Pose.cs:190) on the input side only, as a UX filter, never as a security gate.
3. Every gesture action has the same resource gate as its button twin, checked in the sim from state: Ult meter (:452), Cd[k] (:456),
   SetCd/reload, GuardBroken, Stun. A gesture must never bypass those gates or stack on top of them. Example: gesture-Ult and button-Ult in
   the same frame fire once.
4. Every gesture-specific window (ultimate gesture window, super jump, pose fireball, CrossArms block; doc §4 parity gap)
   is a sim-state tick counter hashed in the checksum. Never take it from the packet or from wall-clock time.
5. Add a minimum re-trigger interval in sim ticks per gesture, at least the slowest real camera cadence (about 50 ms + 100 ms stability, doc §4).
   Then a macro that toggles bits every frame gains nothing over a human.
6. Every gesture action is also reachable from a button binding. Then camera vs keyboard is an accessibility choice, not
   an advantage, and forging the camera path is pointless.

## 3. Keep the 1-bit PoseVerified?
What it can prove in P2P: nothing. It is the sender's own claim (the comment at ChibiInputPacket.cs:24-26 says so honestly).
What it is good for: HUD and UX ("camera" icon), local analytics, and as a client-attested feature in post-match statistics.
Recommendation: **keep it as a cosmetic hint and label it "self-reported"**, at 1 bit per frame. Do not grow it into a "verification" system.
The doc §4 0xC8 telemetry datagram (FNV-1a over quantised keyframes) is the honest maximum. It is a commitment plus a heuristic
signal (identical hashes across gestures mean replay; frame-perfect onset distributions mean a macro), reviewed post-match or offline,
and never acted on in-match. It cannot stop a client that synthesises keyframes. Defer it until there is a ranked mode or a report
pipeline to consume it. Without a consumer it is cost with no benefit. Pose landmarks are biometric-adjacent data: any upload needs
consent plus a privacy review (GDPR/CCPA, COPPA if under-13 players are possible). Hashes without raw keyframes are low-risk.
The original spec's "hash telemetry payloads to prevent macro spoofing" is **not achievable** in this topology. Reword it to
"detect likely spoofing after the fact (heuristic)".

## 4. Malformed-input safety
- Direction 9-15: zeroed in Sanitized (ChibiInputPacket.cs:101). DirIndex also clamps (:68), so DirX/DirY never index out of bounds.
- Gesture bits: 2 bits map to 0-3, and all are valid Gesture values (None/CrossArms/PunchForward/RaiseHands). There is no out-of-range id. Gesture value 3
  without a pose is a legal input (same as a keyboard RaiseHands).
- Dash+Shield, PoseVerified with gesture None, Ult with no meter: all legal bit patterns. The sim ignores Dash and bit 15, and gates Ult on state. No crash.
- Desync: impossible from input content alone. Both peers store the same sanitised value and step it the same way. Local
  input is masked but not run through Sanitized (P2PPeerLoop.cs:148), while remote input is sanitised. If a local source ever produced
  direction > 8, the sender would store it raw. Its own DirIndex clamps to neutral and the receiver zeroes the nibble, so MoveX/Y agree.
  But PrevInput differs (raw vs zeroed), so **the checksums would differ and report a false desync** (ChibiSimulation.cs:226). Not reachable
  today: DirFromAxes only emits 0-8.
- Datagram level: wrong magic, truncation and oversize all fail TryRead (:197, :222; BitBuffer.cs:48/:80). A first frame below 0 is rejected (:222).
  Frames too far ahead are dropped (P2PPeerLoop.cs:254). Already-confirmed frames are never overwritten (:253, :257). Count is 3 bits, so at most 8 inputs, so there is no array overrun.
- Side notes (LOW, not gesture-specific): `Advantage` (-8..7) is trusted for TimeDilation (P2PPeerLoop.cs:246, :292-293).
  A lying peer can make the other run up to 8% slower. A forged checksum (:340-352) raises OnDesync and can abort a match. That griefing
  is no worse than disconnecting, as long as a desync is not scored as a loss for the victim.

## Findings
- SEC-001 MEDIUM. Misleading trust signal: PoseVerified/TelemetryValid are presented as anti-cheat (Pose.cs:110-114,
  ChibiInputPacket.cs:24, spec text). Attack: a future feature trusts them and a forged bit gains a gameplay effect. Fix: reword the comments and spec,
  and make "sim never reads bit 15" a written rule plus a test. Effort Low.
- SEC-002 LOW. Sanitized keeps PoseVerified when gesture is None (ChibiInputPacket.cs:98-103), so it accepts a non-canonical state.
  Fix: clear bit 15 when gesture == None on both the send and receive paths. Effort Low.
- SEC-003 LOW. Local input skips Sanitized (P2PPeerLoop.cs:148, ChibiLocalSession.cs:57-58). That is a latent false-desync source (see 4).
  Fix: apply Sanitized to local samples as well. Effort Low.
- SEC-004 LOW (conditional CRITICAL). Gesture actions in step 15 could bypass cooldown/meter gates if the gesture path is written apart
  from the button path. Fix: rules 1-6 in section 2, plus tests that forged gesture or PoseVerified bits on every frame produce no more actions than buttons. Effort Medium.
- Also: the camera is keyed on index 0 (CameraPose.cs:165), so an online joiner never gets the real camera. That is functional, not security, but it means
  PoseVerified is always false for the joiner. More reason not to give it any gameplay meaning.
- Doc drift: doc §4 still describes a 4-bit gesture id in bits 12-15 and a 32-byte MaxPacketBytes problem. The code uses a 2-bit id at 13-14, Dash at 12,
  and 48 bytes with a throw on overrun (ChibiInputPacket.cs:156, :188-191).

## Top 3 recommendations
1. [step 3, now] Canonicalise input in Sanitized: clear PoseVerified when gesture is None. Run Sanitized on local samples too (SEC-002/003).
   Correct the Pose.cs:113 "server" comment and the spec's "prevent macro spoofing" claim (SEC-001). No wire change. Not a blocker.
2. [step 15] Gesture actions go through the same sim gates as buttons: meter, cooldown, stun, guard-break, a sim-tick re-trigger interval and hashed
   window counters. Every gesture also has a button binding. The sim never reads bit 15. Add EditMode tests that fuzz bits 12-15 on every frame and
   assert actions <= the button-equivalent and checksum equality between two sims (SEC-004).
3. [online hookup: Relay/Lobby] Keep PoseVerified as a self-reported HUD hint only. Defer the 0xC8 hash telemetry until a post-match
   report or ranked pipeline exists, then ship it as heuristic-only with consent and a privacy review. Make sure a desync or abort from a forged checksum is never scored
   against the victim.
