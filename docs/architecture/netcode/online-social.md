# Player names, friends and match invites

Code: `Netcode/ChibiSocialManager.cs` (services), `Netcode/ChibiSocialRules.cs` (pure security rules),
`Game/GameManager.cs` (ONLINE > FRIENDS tab, invite pop-up). Tests: `ChibiSocialRulesTests`.
Package: `com.unity.services.friends` 1.4.0.

## Onboarding: age, account, name (first launch)

1. **HOW OLD ARE YOU?** Digits only (`PlayerIdentity.ParseAge`, `SaveData.age` + `ageBand`). Every age can play every
   mode (product decision 2026-11-09; `PlayerIdentity.RequireTeenForOnline = false` brings back a 13+ online gate).
   The age is for market analysis (see "Player age record").
2. **KEEP YOUR FIGHTER NAME** (`ChibiAccount`): **SIGN IN WITH GOOGLE** (Unity Player Accounts: the browser opens
   Unity's sign-in page, "Continue with Google"; one click when the browser is already signed in to Google) or
   **PLAY WITHOUT ACCOUNT**. Signed in: the Unity player is bound to that account, its public name follows it to every
   computer, and Unity remembers the session, so the next launch signs the same account in silently
   (`RestoreOnLaunch`). Changeable later from the name button: **SIGN OUT / SWITCH**.
3. **WHAT IS YOUR NAME?** 3-16 letters, numbers or _ (advice: a fighter name, not a real name). This is the player's
   identity everywhere: name badge in every menu header, character select, HUD, tags, results, rooms, friends,
   invites, and the opponent's screen online.
4. Change it any time: click the name badge → **CHANGE YOUR NAME** (pushed to the account when signed in).

### One identity per account (`PlayerIdentity.SwitchProfile`, `SaveData.profiles`)

| Situation | Result |
|---|---|
| Same Google account, any launch | Signed in by itself; name and info kept |
| Same account on another computer | Its public name comes back (`Kira#4821` → `Kira`); age asked once on that computer |
| First sign-in after playing as guest | The guest's name and age become the account's |
| Another Google account | A new player: no name, asked age and name again (its own row in the age record) |
| Back to an account used before on this computer | Its name and age come back |
| Sign out | Back to the guest identity of this computer |

## Player age record (market analysis)

`Core/PlayerAgeLedger.cs`, tests in `PlayerIdentityTests`. One CSV row per new player, written when the player has given
their age AND created their identity name (`GameManager.RecordNewPlayer`). Another account on the same computer is a
new player (its own row); the guest's first account is the same person (no second row).

- File: `<Application.persistentDataPath>/player-ages.csv`. On this Windows editor:
  `C:\Users\<you>\AppData\LocalLow\DefaultCompany\My project\player-ages.csv` (the built game uses its company /
  product name instead).
- Columns: `utc_time,player_name,age,age_group,country,platform,game_version`; age groups under13, 13-17, 18-24,
  25-34, 35-44, 45+; country from the device region setting.
- Every value is cleaned (no commas, quotes, newlines or leading `=`), so a name can never break the columns or inject
  a spreadsheet formula. Disk errors never break the game.
- The file is per device. To see all players worldwide in one place, send the same row to a cloud service later
  (for example Unity Analytics or Cloud Save); the row builder is ready for it.
- Privacy: the row holds the player name and age. Tell players in the privacy policy, and check local rules before
  release (Thailand PDPA, GDPR; some countries require parental consent for players under 13).

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
| Privacy | Only the chosen name and online status are shared; no IP (Relay hides it); no camera data; the age is kept in the local age record (see above); matchmaking shares a country code, never GPS |
| Invite by name to a stranger | Never a direct pop-up: a friend request first; the invite follows only after THEY accept; one waiting target, 10 min limit |
| Children | All ages may play online (product decision); names are filtered, no chat exists, BLOCK is on every request. `RequireTeenForOnline` restores a 13+ gate if a market requires it |
| Tampered save file | `CleanSavedName` re-validates the saved name; an unknown age group asks again |
| Hostile peer name in the match | 6-bit alphabet on the wire, length-checked, re-sanitised before drawing |

## Quality notes

- The rules are pure C# with 44 unit tests (names, injection, codes, every invite rejection path).
- The live Friends / Lobby / Relay calls cannot be unit-tested without a linked Unity Cloud project. After linking, test with
  two players: add by name, accept, invite, accept, play, decline, block.

## Setup

Unity Dashboard → project → enable **Relay**, **Lobby** and **Friends** (plus the project link in Edit > Project Settings >
Services). For SIGN IN WITH GOOGLE: Dashboard → Player Authentication → **Unity Player Accounts** on, with Google as a
sign-in option. Until then the FRIENDS tab and the sign-in button show that instruction; LAN and VS AI still work.
