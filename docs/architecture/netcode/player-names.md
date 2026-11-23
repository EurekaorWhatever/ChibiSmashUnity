# Worldwide-unique player names

Status: implemented 2026-11-22, waiting for the script deploy and a Play Mode check.
Spec: `production/HANDOFF-2026-11-22.md`, NEXT item 4.

## Design

- The name typed at first launch is the player's identity (invites, pairing). It belongs to the Unity player id of
  this device (anonymous sign-in, or the Google account when signed in).
- **The server decides.** A Cloud Code script, `Assets/ChibiClash/CloudCode/Chibi_Clash.js`, deployed as **Chibi_Clash** (the name chosen in the dashboard), checks the
  name with the same rules as `ChibiSocialRules.ValidateName` (3-16 letters / digits / `_`, blocked words) and reserves
  it for `context.playerId`. The client cannot write the registry and never sees another player's id.
- Names are unique **case-insensitively**: the key is the lower-case name ("Kira" and "KIRA" are one name). The player
  keeps the casing they typed.

| Action | Params | Reply `status` |
|---|---|---|
| `claim` | `name` | `ok` (with `name`), `taken`, `invalid`, `error` |
| `check` | `name` | `available`, `yours`, `taken`, `invalid`, `error` |
| `join` | none | `ok` (this player's launch counted, at most once per UTC day) |
| `stats` | none | `ok` with `players`, `joins` |

## Data layout (Cloud Save, private custom items)

Private custom items can be read and written only by server code (the Cloud Code service token). Players cannot read
them through the client SDK, so player ids do not leak.

| Custom id | Key | Value |
|---|---|---|
| `chibi-names-XX` | lower-case name | `{ playerId, name, claimedAt }`; a released name is `{ playerId: "", name: "", releasedAt }` |
| `chibi-name-owners-XX` | player id | `{ name, key, claimedAt, nonce }` (one current name per player) |
| `chibi-stats` | `players` | `{ count }`: unique players who ever claimed a name |
| `chibi-stats` | `joins` | `{ count }`: player-days with a launch (one counted `join` per player per UTC day) |
| `chibi-joins-XX` | player id | `{ day, nonce }`: last UTC day this player's join was counted (checked on the server) |

**Shards.** Cloud Save allows at most **2000 keys per custom ID**, so one `chibi-names` would cap the game at 2000
names. `XX` is a shard `00`..`ff` chosen by an FNV-1a hash of the key (`shard()` in the script): 256 custom IDs hold
about 400 000 names / players before one gets near 2000 keys. `SHARDS` must never change once live.

To look at them: Unity Cloud dashboard > Cloud Save > Custom Items, then search a custom id (for example
`chibi-names-3f`, or `chibi-stats` for the totals).

## Races

- Claiming a name that already has an item (ours) updates it **with its writeLock**: atomic (409 -> `taken`).
- A brand-new key cannot be created conditionally in Cloud Save. The script writes, waits 300 ms, reads back and only
  answers `ok` if it is still the owner. Both claims can only "win" if the second write lands more than 300 ms after
  the first while its read happened before it, which is very unlikely. Even then it self-heals: the client claims the
  name again before every online session, and the loser then gets `taken` and is asked for a new name.
- Released names are **deleted** (under their writeLock), so renames never use up a shard's 2000 keys. The next claim
  of a deleted name is a create, handled by the read-back above.
- **Rename cooldown:** a different name within 60 s of the player's last claim is refused with `wait` (the game shows
  "You changed your name a moment ago. Try again in a minute."). Re-claiming the same name is always allowed.
- The reverse index (`chibi-name-owners-XX`) is a compare-and-swap against the version read just before the write:
  an update carries its writeLock (409 if a parallel claim by the same player wrote first); a first-ever entry is a
  create, so it carries a nonce and is read back, and of two parallel first claims the last writer very likely keeps
  its name (the read-back can miss a write landing more than 150 ms later; then the player holds two names until the
  next claim releases the extra one). On a conflict or a failure after the name was written, the script tries to
  delete that fresh name again. That release is best effort: if it fails too (service error), the name stays reserved for this player
  until an operator frees it. The `players` counter can count one player twice in that rare parallel case.
- Counters use writeLock with 6 jittered retries. Only the very first create of a counter can lose one count.
- A player's first-ever join marker is a create: the script reads it back (nonce) so parallel joins count once.

## Client (`Assets/ChibiClash/Scripts/Netcode/`)

- `ChibiNameRegistryRules` (pure, tested in `ChibiNameRegistryRulesTests`): reads replies with `JsonUtility`
  (stripping-safe), anything odd becomes `ServiceUnavailable`, exact text "This name is already taken", dev skip rule.
- `ChibiNameRegistry`: `CheckAsync`, `ClaimAsync`, `JoinOncePerLaunch`, `StatsAsync` through
  `CloudCodeService.Instance.CallEndpointAsync("Chibi_Clash", args)` after `NetworkRoomManager.EnsureSignedInAsync()`.
  10 s timeout, polled per frame (no timer threads, works in the Web build; Cloud Code is HTTPS). Never throws.
  Warnings are rate-limited (one per 30 s).
- `GameManager.Online.cs`:
  - Name window SAVE / LET'S FIGHT: local rules, then claim (button shows CHECKING...). `taken` keeps the window open
    with "This name is already taken"; SUGGEST still works.
  - Offline, under 13, or service down: the name is saved locally, **unverified**, and the game stays fully playable
    offline (Vs CPU, training, local).
  - **Name gate in the services**: `EnsureOnlineNameAsync` is wired into `NetworkRoomManager.NameGate` (checked in
    its sign-in step: quick match, private rooms, room links), `ChibiRoomService.NameGate` (room create / join) and,
    through the room service, `ChibiNameInvites` (sending an invite by name, and looking for invites to us). In this
    game client those are the online entry points we know of; the name is claimed once per session before them.
    Taken meanwhile: the flow fails and the name window opens with the taken message. Service down: refused in
    release builds with a clear message. A claim for an old name is dropped if the saved name changes meanwhile.
  - Unity's public player name (friends) and the Google account name are only updated with a claimed name
    (`ChibiSocialManager.NameVerified`, and `OnAccountChanged` pushes only a name verified for the new player id);
    an unverified name stays local until the gate confirms it.
  - Known gap (low): the invite gate checks the saved name, while invites use Unity's displayed name. If pushing a
    new name to Unity fails, invites can briefly use the previous (already released) name.
  - Editor and development builds (`Application.isEditor || Debug.isDebugBuild`) skip the claim for DevHost /
    DevGuest / DevPlayer, and go on when the service is down (so dev automation works before the deploy). Release
    builds never skip.
  - `join` is called once per launch, when the friends layer has signed in and the player is 13+.

## Deploy the script (dashboard, no programming)

1. Open https://cloud.unity.com, choose the project, and pick the environment **production** (top bar).
2. Left menu: **Cloud Code** > **Scripts** > **Create script** (or "+ Create").
3. Name: `Chibi_Clash` (exactly; it must match `ChibiNameRegistry.ScriptName`). Type: **API**. Click Next / Create.
4. Delete the sample code. Open `Assets/ChibiClash/CloudCode/Chibi_Clash.js` in Notepad, select all, copy, paste it into the editor.
5. **Parameters** (panel next to the code, "Add parameter"):
   - `action`, type **String**, **Required** ticked.
   - `name`, type **String**, Required **not** ticked.
6. Click **Save**, then **Publish** (confirm). The script must show a published version.
7. Test it in the dashboard (Run / Test panel): set `action` = `stats`, Run. Expect `{"status":"ok","players":0,"joins":0}`
   (the test runs as a test player).

Re-deploying after a change: open the script, paste the new code, Save, Publish. Clients need no update unless the
parameters change. Alternative: put the file in Unity's Deployment window (Cloud Code JS script asset, file name =
script name) or the UGS CLI; the in-script `module.exports.params` covers parameters there.

## Limits

- Anonymous accounts are free, so one person can reserve many names with many devices (no captcha). Cloud Code's own
  per-player rate limits apply. A moderator can free a name by deleting its key in the right `chibi-names-XX` custom item in the dashboard.
- Signing in with a Google account changes the player id: a name claimed anonymously must be claimed again by the
  account (it shows "taken" until an operator moves it). The account keeps its name afterwards.
- The blocked-word list is duplicated in C# and JS: change both together.
- A Cloud Save custom id holds at most 2000 keys: names are sharded over 256 ids (about 400 000 names), and released
  names are deleted. `stats` is approximate under extreme launch bursts (a count is skipped after 6 conflicts, logged
  in Cloud Code logs).
- **Names never expire**: they are not freed when a player stops playing (no clean-up job).
- Names are unique only when claimed through the game. Room, invite and match-handshake names are still
  self-reported by the client, so a modified client can skip the claim and show any name.
