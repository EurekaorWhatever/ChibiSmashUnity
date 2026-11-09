# Online rooms: Authentication, Lobby, Relay, Unity Transport (after port step 19)

Code: `Netcode/NetworkRoomManager.cs` (rooms), `Netcode/ChibiUtpTransport.cs` (UDP pipe), `Netcode/ChibiOnlineMatch.cs`
(handshake + rollback + result, from online part 1/2), `Game/ChibiMatch.cs` (online view), `Game/GameManager.cs` (ONLINE screen).
Tests: `ChibiUtpTransportTests` (real UDP on 127.0.0.1), `ChibiOnlineMatchTests`, `ChibiRollbackSessionTests`, `ChibiSyncVerificationTests`.

## 1. Data flow

```
 local devices / camera ──► ChibiPlayerInputSource ──► 16-bit input (dir, buttons, dash, gesture)
                                                           │
                         ChibiRollbackSession: input delay 2, predict remote, save state each frame,
                         roll back + re-simulate on a misprediction, checksum every 30 frames
                                                           │  0xC7 datagram: oldest unacked inputs (≤16, delta coded)
                         ChibiChannelMux (0xC7 inputs │ 0xC9 control: Hello / Start / Ready / Leave / Reject)
                                                           │
                         ChibiUtpTransport: Unity Transport, unreliable pipeline, 256-byte datagram cap
                                                           │  DTLS (encrypted UDP)
                                                 Unity Relay server (hides both IPs)
                                                           │
                                                   the other player (same stack)
```

Both computers run the same deterministic simulation from the same inputs; only inputs travel (about 11 bytes per
datagram at 60 Hz when held inputs repeat).

## 2. Modes

| Mode | Flow |
|---|---|
| ROOMS (create room mode) | Named rooms that stay open between fights: see "Rooms" below (`ChibiRoomService`, `ChibiRoomRules`). |
| Private room by code (API) | `NetworkRoomManager.CreatePrivateRoom(name)` / `JoinPrivateRoom(code)`: one-shot room, link on join (kept for automation). |
| Matchmaking (regional) | Closest waiting player first: see "Regional matchmaking" below. Else host a public lobby and search again every 6-8 s, so two searching players always meet. |
| LAN (no services) | `HostLan()` listens on UDP 7777 and shows this PC's IP; `JoinLan(ip)` connects. Same transport, handshake and rollback. |
| vs AI | offline, `ChibiCpuInputSource` (step 17). |

Once the link is up the host deletes the lobby (no third player can join), then `ChibiOnlineMatch` runs:
handshake (host decides seed/stage; both compare a hash of RulesVersion + every constant table; mismatched builds are
refused with a clear message), then rollback play, then a result decided only from the CONFIRMED state.

Players can be anywhere with internet (Wi-Fi or mobile data, other cities or the same room): Relay connects them,
and the Relay SDK picks the server region with the best ping (QoS) automatically. No internet → the menu says so
(`NoInternetMessage`); LAN still works on one network.

### Home page

Three main blocks: **MATCHMAKING** (starts searching at once), **ROOMS**, **VS AI** (offline, the AI picks its fighter
at random each fight, you press START BATTLE). Below: Local Versus, Training, Friends & LAN. MATCHMAKING and ROOMS need
the internet (the block says NO INTERNET otherwise); VS AI, local versus and training never do.

### Rooms (`ChibiRoomService`, `ChibiRoomRules`, tests: `ChibiRoomRulesTests`)

- ROOMS page: no rooms yet → one big **CREATE ROOM** in the middle (+ join by code). With rooms → **+ CREATE ROOM**
  top right and a block per room (name, code, players). Click a block to go in. Host at most 2 rooms.
- A room is a private 2-seat Lobby kept alive by the host's heartbeat (also during fights). Player data: name (no
  #tag), fighter, OK flag, start nonce, launch ack. Lobby data: stage, `go` nonce, Relay code.
- Inside: pick a fighter, press **OK** (locked in; **CHANGE** unlocks). OK unlocks only after a fresh read of the room.
  **START FIGHT** works only when both seats are taken and both pressed OK; either player may press it.
- Host start → host un-readies, opens a fresh Relay link (`HostLinkAsync`), publishes `{relay, go}`. Guest start →
  guest publishes a nonce, the host's 2 s poll launches (max 3 guest starts per 2 min). The guest sees the new `go`,
  acknowledges it (ack = go, OK off), joins (`JoinLink`) → handshake → match. No ack within 8 s or no link within
  25 s → cancelled; both press OK again. After the fight both return to the room.
- Starts only happen while the room screen is open; leaving it withdraws OK. The match uses exactly the fighter locked
  in with OK and the room's stage.
- Reviewed by network-programmer (races, stale launches, rate limits) and security-engineer (relaunch spam, hostile
  lobby data, age gate) agents; all findings fixed.

### Regional matchmaking (`ChibiRegions`, tests: `ChibiRegionsTests`)

- **Location** = the device's region setting (`RegionInfo.CurrentRegion`, e.g. `TH`). No GPS, no IP lookup, no permission.
  Only the 2-letter code is published, as public lobby data indexed on S2, and only for matchmaking lobbies (private
  rooms publish nothing).
- **Closeness tiers** for every country in the world (UN M49): 0 same country → 1 same sub-region (TH-VN-MY-SG) →
  2 neighbouring sub-region (TH-CN/JP/IN/AU) → 3 same continent → 4 world. Unknown or forged codes count as tier 4.
- **Widening search**: tier ≤ 1 at once, +1 tier every 8 s, worldwide after 24 s. A device with no region searches worldwide.
- **Search**: pass 1 asks the Lobby service for waiting lobbies of OUR country (S2 index, server-side filter, scales to
  any number of waiting players); pass 2 lists the 50 oldest waiting lobbies of this build. Candidates are ranked by
  tier, then by waiting time (oldest first, fair), and up to 3 are tried (`LobbyFull` / `LobbyNotFound` → next).
- **Two waiting hosts**: only the host whose lobby id is higher moves to the other one, so they never swap at once.
- **Load**: one search per 6-8 s per waiting player (random jitter); `RateLimited` → 15-20 s jittered back-off.
  Matches are P2P through Relay, so there is no game server to scale; the per-match cost is two clients + one Relay hop.
  Beyond very large populations, the next step is Unity Matchmaker (server-side tickets, QoS region + skill rules); the
  tiers above map directly onto its rules.

## 3. Results and disconnects

| Event | Result |
|---|---|
| KO / time-out in the confirmed state | win / loss / draw (same-tick double KO = draw, D-F1) |
| opponent quits (Leave), goes silent 5 s, or the transport drops | **you win** (decision Q4) |
| you quit | loss |
| checksum mismatch (desync) | no contest, no rewards |

Rewards online: PvP rules (40 win / 15 otherwise + posture), quests as offline; none for no contest.

## 4. The two known network bugs

| Bug | Status |
|---|---|
| Large packets silently cut off | Fixed (online part 1): `ChibiPacketCodec.Write` throws instead of sending a truncated datagram; receivers reject oversize; `ChibiUtpTransport` drops (and counts) any datagram over 256 bytes. Tests: `ChibiPacketCodecTests`, `Send_OversizedDatagram_IsDroppedNeverTruncated`. |
| Lag stall that froze both players | Fixed (online part 1): every datagram resends the OLDEST unacked inputs (up to 16), so a burst loss longer than the old 8-input window no longer leaves a permanent hole. Test: `BurstLossLongerThanResendWindow_BothPeersRecover` (watched failing on the old code). |

## 5. Setup needed once (needs the project owner's Unity account)

1. Unity Editor: **Edit > Project Settings > Services** → link to (or create) a Unity Cloud project.
2. Unity Dashboard (cloud.unity.com) → the project → enable **Relay**, **Lobby** and **Friends** (Authentication anonymous sign-in is on by default).
   For SIGN IN WITH GOOGLE also turn on **Unity Player Accounts** with Google as a sign-in option.
3. Both players must run the same build (the handshake refuses different rules / constants).

Until then the ONLINE screen shows exactly this instruction, and LAN play works without any setup.

## 6. Security notes (P2P, no authority)

- The sim never reads the self-reported PoseVerified bit; every gesture action has the same gates as buttons (gesture-conversion.md).
- Inputs are sanitised on receive; datagrams with a wrong magic, truncated, oversize or acking frames we never sent are dropped.
- A desync is a no contest, so a modified client cannot convert a forged checksum into a win.
- Honest limit: with two peers and no server, a player who pulls their own cable makes the OTHER player win (Q4),
  which is the intended rule; ranked play would need a server-side replay check.
