# Player names, friends and match invites

Code: `Netcode/ChibiSocialManager.cs` (services), `Netcode/ChibiSocialRules.cs` (pure security rules),
`Game/GameManager.cs` (ONLINE > FRIENDS tab, invite pop-up). Tests: `ChibiSocialRulesTests`.
Package: `com.unity.services.friends` 1.4.0.

## Onboarding: age, then name (first launch)

1. **HOW OLD ARE YOU?** A neutral question (digits only). Only the age GROUP is saved (`PlayerIdentity`, `SaveData.ageBand`):
   under 13 / 13-17 / 18+. Never the age or a birthday. Under 13 the ONLINE card is locked (13+), and everything
   offline stays open. The age cannot be changed in the menu (no instant retry of the gate).
2. **WHAT IS YOUR NAME?** 3-16 letters, numbers or _ (advice: a fighter name, not a real name). Saved in
   `SaveData.playerName`. This is the player's identity everywhere: the name badge in every menu header, character
   select, HUD, tags over fighters, results, friends list, invites, and the opponent's screen online.
3. Change it any time: click the name badge (top of every menu) → **CHANGE YOUR NAME**. Signed in, Unity is renamed
   at once; otherwise on the next sign-in (`ChibiSocialManager.PreferredName`). Friends stay linked by player id.

## Player flow

1. ONLINE > **FRIENDS**. Unity adds a tag to your name: `Kira#4821` (shown as YOU: ...).
2. Type a player's name and press:
   - **INVITE**: a friend gets the invite at once. Anyone else first gets a friend request; when they accept, the
     invite goes out automatically (one waiting at a time, lapses after 10 min, **STOP** cancels).
   - **ADD FRIEND**: only the request.
3. You see requests: **ACCEPT**, **DECLINE** or **BLOCK**.
4. Friends show a green dot when online; **INVITE** on a row makes a private room and sends its code.
5. The friend sees a **MATCH INVITE** pop-up on any menu screen (60 s): **ACCEPT** joins the room, **DECLINE** tells you.
   If you **CANCEL** / **BACK**, the friend's pop-up is withdrawn.
6. Room → Relay → handshake → match. Both names travel in the handshake, so each side sees `Kira vs Pip`.
   Friends see you as busy while you play. Works from anywhere with internet (Relay), not only the same network.

## Names in the match (handshake trailer)

Hello and Start carry an optional trailer: 5-bit length (0-24), then 6 bits per character, an index into a 64-character
alphabet (`A-Z a-z 0-9 _ #`). There are no `<`, `>`, `=`, spaces or control characters, so rich-text injection is
impossible by construction, and `SafeDisplayName` runs again before drawing. A datagram without the trailer (an older build)
means "no name", so the protocol version did not change. A length above 24 or a truncated name is malformed. Names are
cosmetic: not in the match hash and never read by the simulation. Tests: `ChibiPlayerNameTests`.

## Services used

| Need | Service / API |
|---|---|
| Player id, name `Name#1234` | Unity Authentication: `SignInAnonymouslyAsync`, `UpdatePlayerNameAsync`, `GetPlayerNameAsync` |
| Requests, friend list, online status | Unity Friends: `AddFriendByNameAsync`, `AddFriendAsync` (accept), `DeleteIncomingFriendRequestAsync`, `DeleteFriendAsync`, `AddBlockAsync`, `SetPresenceAvailabilityAsync` |
| Invite delivery | Unity Friends messaging: `MessageAsync(friendId, ChibiInviteMessage)` / `MessageReceived` |
| The match itself | private Lobby room + Relay (online-rooms.md) |

## Security review (2026-11-09)

| Risk | Defence |
|---|---|
| Stranger sends invites / pop-up spam | Only friends per OUR Unity Friends list (never per message); one invite per friend per 5 s; at most 3 pending; expire after 60 s (local clock); ignored while in a match |
| Malformed or hostile message | `ValidateInvite`: kind / code / build tag checked, oversized fields rejected; bad messages dropped silently (no attacker text echoed) |
| Rich-text / control-char injection through names (`<size=500>`) | Every name drawn passes `SafeDisplayName` (letters, digits, `_`, `#` only, max 24); the sender name shown comes from our friends list, not the message |
| Bad room code | `NormalizeCode` (4-12 letters/digits) before joining; the Lobby service is the authority for the room |
| Different game version | Invite carries the build tag; mismatches are refused with a clear message; the match handshake checks again |
| Request spam from one player | **BLOCK** on every request (Unity blocks requests, messages and invites from them) |
| Event threading | Friends events only enqueue (bounded at 64); lists change on the main thread in `Update`; handlers unsubscribed on destroy |
| Accidental friend removal | Two-click REMOVE ("SURE?") |
| Privacy | Only the chosen name and online status are shared; no IP (Relay hides it); no camera data; age kept as a group only; matchmaking shares a country code, never GPS |
| Invite by name to a stranger | Never a direct pop-up: a friend request first; the invite follows only after THEY accept; one waiting target, 10 min limit |
| Children | Age gate: under 13, no online friends, invites or matchmaking (offline modes open) |
| Tampered save file | `CleanSavedName` re-validates the saved name; an unknown age group asks again |
| Hostile peer name in the match | 6-bit alphabet on the wire, length-checked, re-sanitised before drawing |

## Quality notes

- The rules are pure C# with 44 unit tests (names, injection, codes, every invite rejection path).
- The live Friends / Lobby / Relay calls cannot be unit-tested without a linked Unity Cloud project. After linking, test with
  two players: add by name, accept, invite, accept, play, decline, block.

## Setup

Unity Dashboard → project → enable **Relay**, **Lobby** and **Friends** (plus the project link in Edit > Project Settings >
Services). Until then the FRIENDS tab shows that instruction; LAN play still works.
