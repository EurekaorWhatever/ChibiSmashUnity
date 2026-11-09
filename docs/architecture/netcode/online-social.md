# Player names, friends and match invites

Code: `Netcode/ChibiSocialManager.cs` (services), `Netcode/ChibiSocialRules.cs` (pure security rules),
`Game/GameManager.cs` (ONLINE > FRIENDS tab, invite pop-up). Tests: `ChibiSocialRulesTests`.
Package: `com.unity.services.friends` 1.4.0.

## Player flow

1. ONLINE > **FRIENDS**. First time: choose a name (3-16 letters, numbers or _). Unity adds a tag: `Kira#4821`.
2. Give your name to a friend. They type it in the box and press **ADD FRIEND**.
3. You see their request: **ACCEPT**, **DECLINE** or **BLOCK**.
4. Friends show a green dot when online. Press **INVITE**: the game makes a private room and sends its code to that friend.
5. The friend sees a **MATCH INVITE** pop-up on any menu screen (60 s): **ACCEPT** joins the room, **DECLINE** tells you.
6. Room → Relay → handshake → match, exactly like room codes. Friends see you as busy while you play.

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
| Privacy | Only the chosen name and online status are shared; no IP (Relay hides it); no camera data |

## Quality notes

- The rules are pure C# with 44 unit tests (names, injection, codes, every invite rejection path).
- The live Friends / Lobby / Relay calls cannot be unit-tested without a linked Unity Cloud project. After linking, test with
  two players: add by name, accept, invite, accept, play, decline, block.

## Setup

Unity Dashboard → project → enable **Relay**, **Lobby** and **Friends** (plus the project link in Edit > Project Settings >
Services). Until then the FRIENDS tab shows that instruction; LAN play still works.
