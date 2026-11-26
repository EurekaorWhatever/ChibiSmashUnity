# 🌐 Network Engineering Summary: Chibi Clash

🎮 Unity 6000.6.7f1 · 📡 protocol 6 · 📜 rules v25 · 👥 2-4 players online · 📅 2026-11-23

🔗 Linked to Unity Gaming Services (Relay, Lobby and Friends turned on; Relay region tested: Singapore)

## 💡 The idea

Chibi Clash uses **rollback netcode**. Many modern fighting games use it too.

- 🚫🖥️ **No game server.** Both players' computers run the same game.
- 🎮 **Only button presses are sent.** About 11 bytes, 60 times per second.
- 🔮 **Guess, then fix.** If the other player's input is late, the game guesses it. If the guess was wrong, the game
  goes back a few frames and fixes it before you see anything.
- ⚡ **Your controls always feel instant**, even with lag.

## 🏗️ How it connects

```
🧑 Player A ──► 🖥️ game ──► 📶 internet ──► ☁️ Unity Relay ◄── 📶 internet ◄── 🖥️ game ◄── 🧑 Player B
```

⭐ **3-4 players use a star.** Every guest connects to the host's game (one Relay code for everyone). The host's game
passes each player's button presses on to the others. It is only a messenger: it never decides anything, every game
still runs the full match itself.

```
🧑 Guest 1 ─┐
🧑 Guest 2 ─┼──► ☁️ Relay ──► 🖥️ host game (passes inputs on) ──► ☁️ Relay ──► every other player
🧑 Guest 3 ─┘
```

| Part | What it does |
|---|---|
| 🔑 Unity Authentication | Signs the player in |
| 🚪 Unity Lobby | Rooms and matchmaking (no gameplay) |
| ☁️ Unity Relay | Passes messages between players and hides their IP addresses |
| 📦 Unity Transport | Sends fast messages: UDP on PC, secure WebSockets (wss) in the browser |
| ⏪ `ChibiRollbackSession` | Guesses, goes back and fixes, checks both games match |
| 🧠 `ChibiSimulation` | The game rules |

## 🔢 Key numbers

| Setting | Value |
|---|---|
| ⏱️ Game speed | 60 ticks per second |
| 🎮 Input size | 16 bits per player per tick |
| ⏳ Input delay | 2 frames (1v1) · 3 frames (3-4 players) |
| ⏪ Most frames it can go back | 8 (1v1) · 10 (3-4 players) |
| 🔁 Inputs sent again in each message | up to 16 per player (fixes lost messages) |
| ✅ "Do all games match?" check | every 30 frames, every game against every other |
| 📦 Biggest input message | 216 bytes (256 for any message) |
| 📶 Host upload with 4 players | about 23 KB/s (190 kbit/s) |

## 🎯 Same result on both computers

Rollback only works if both computers get **exactly** the same result. So the game was rebuilt:

- 🔢 **Whole numbers only:** no decimals, no Unity physics.
- 🎲 **Its own dice:** a random number generator saved inside the match.
- 💾 **One save box:** the whole match is in one place, so it is easy to save, load and compare.
- 👀 **Visuals only watch:** animations, effects and the HUD read the game but never change it.

## 🕹️ Game modes

| Mode | How you connect |
|---|---|
| 🏠 Matchmaking (rooms) | PLAY → MATCHMAKING. Make a private room (2-4 players) and share its code. 👥 The fight starts when everyone in the room (2, 3 or 4 players) pressed OK. 3-4 players = 👑 Battle Royale. |
| 📨 Invite by name | In your room, type a friend's in-game name. They see the invite for 60 seconds and press join. ✅ Tested with 2 games: joined in 4 seconds. |
| ⚡ Quick match | Vs Online → Solo. 👑 Battle Royale for up to 4. Finds the closest players first (your country → region → world). Starts at once with 4, or with 2-3 after 20 seconds. |
| 🏢 LAN | Same Wi-Fi or network, no internet needed (PC version only) |
| 🤖 Vs CPU / Training | Offline, the same game rules. Vs CPU: easy, medium or hard. |

🤝 Before each online match, both games check they are the same version. Different versions are not allowed to
play together.

🆕 Rules v25 (2026-11-23): the game rules now know 3-4 fighters (👑 Battle Royale and 👥 2v2, no friendly fire in 2v2).
A player who leaves is out at an exact frame and the match goes on. A v24 game and a v25 game refuse each other.

🆕 Protocol 6 (2026-11-23): online for 2-4 players. Messages now say which player they belong to, and a new "Roster"
message shares every player's name. A protocol 5 game and a protocol 6 game refuse each other.

## 🏆 Match results

| What happens | Result |
|---|---|
| 💥 KO or time runs out | Win / lose / draw (both KO at the same moment = draw) |
| 🚪 Other player quits or disconnects (1v1) | You win |
| 🚪 A guest quits or disconnects (3-4 players) | That player is out (a loss for them), the match goes on |
| 🧮 One guest's game disagrees, or a guest claims "games disagree" (3-4 players) | That guest is out, the match goes on |
| 🏠 The host quits (3-4 players) | No contest for everyone |
| ⚠️ Games stop matching, or very bad lag | No contest |

## ⚠️ Limits

- 👮 **No server means no referee.** A player can force a "no contest". The host picks the random seed.
- ⭐ **In 3-4 player games the host passes everyone's inputs on.** A modified host could change a guest's button
  presses and also fake the matching checks, so nobody would notice. 👍 We accept this risk for now (no game server).
  The host can already end any match by leaving, and this needs a hacked game.
- 🧮 **Only the host compares every game (3-4 players).** If just one guest's game disagrees, that guest is out and the
  match goes on. If the host's own game disagrees, it is a no contest.
  🙈 A guest that sends no check, or copies the host's, can hide only its own wrong game. If the check is waiting for
  a player who leaves, it is decided without them.
- 🪑 **Room seats are locked to players.** Each player shows a secret code's fingerprint in the room. Only the player
  with that seat's secret can take it in the fight, and the secret is new for every fight. Room fights show each
  player's room name.
- 📶 **Guest to guest goes through the host**, so it has one extra hop of lag.
- 🏷️ **Names are unique when claimed through the game.** A small server script (Cloud Code "ChibiNames") keeps one
  list of every name and counts players and daily launches. A name someone else has shows "This name is already
  taken". 📴 Offline, the name is saved on your computer and claimed before you play online. Details:
  `docs/architecture/netcode/player-names.md`.
- 🎭 **Names in rooms, invites and the match handshake are still self-reported.** A modified game can skip the claim
  and show any name. Invites still show who sent them, nothing joins by itself, and at most 3 invites wait at once.
- ⏳ **Names never expire**, even when a player stops playing.
- 🌍 **Web version** (GitHub Pages link): online play goes through Relay with secure WebSockets. LAN is not possible
  in a browser. 🤝 Web and PC players can play each other (Unity supports this cross-play).
  ⏱️ The browser has no extra threads, so online waits use Unity's `Awaitable.WaitForSecondsAsync`, never
  `Task.Delay` (a .NET timer that may never fire on the web).

## ✅ Testing

🧪 Automatic tests check the network code: **1,863 passing** at the last full run. GitHub runs them on every push too.

- 🔐 known-result tests
- ⏪ rollback tests
- 📶 real network tests
- 🎬 replay tests

👀 Network and security reviewers checked the changes.

📚 More details: `docs/architecture/netcode/`
