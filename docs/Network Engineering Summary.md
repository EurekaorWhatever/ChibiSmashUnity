# 🌐 Network Engineering Summary: Chibi Clash

🎮 Unity 6000.6.7f1 · 📡 protocol 5 · 📜 rules v24 · 📅 2026-11-22

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
| ⏳ Input delay | 2 frames |
| ⏪ Most frames it can go back | 8 |
| 🔁 Inputs sent again in each message | up to 16 (fixes lost messages) |
| ✅ "Do both games match?" check | every 30 frames |
| 📦 Biggest message | 256 bytes |

## 🎯 Same result on both computers

Rollback only works if both computers get **exactly** the same result. So the game was rebuilt:

- 🔢 **Whole numbers only:** no decimals, no Unity physics.
- 🎲 **Its own dice:** a random number generator saved inside the match.
- 💾 **One save box:** the whole match is in one place, so it is easy to save, load and compare.
- 👀 **Visuals only watch:** animations, effects and the HUD read the game but never change it.

## 🕹️ Game modes

| Mode | How you connect |
|---|---|
| 🏠 Rooms | Make a private room and share its code with a friend |
| 🔍 Matchmaking | Finds the closest player first (your country → region → world) |
| 🏢 LAN | Same Wi-Fi or network, no internet needed (PC version only) |
| 🤖 vs AI / Local / Training | Offline, the same game rules |

🤝 Before each online match, both games check they are the same version. Different versions are not allowed to
play together.

## 🏆 Match results

| What happens | Result |
|---|---|
| 💥 KO or time runs out | Win / lose / draw (both KO at the same moment = draw) |
| 🚪 Other player quits or disconnects | You win |
| ⚠️ Games stop matching, or very bad lag | No contest |

## ⚠️ Limits

- 👮 **No server means no referee.** A player can force a "no contest". The host picks the random seed.
- 🌍 **Web version** (GitHub Pages link): online play goes through Relay with secure WebSockets. LAN is not possible
  in a browser. 🤝 Web and PC players can play each other (Unity supports this cross-play).

## ✅ Testing

🧪 Automatic tests check the network code: **1,541 passing** at the last full run.

- 🔐 known-result tests
- ⏪ rollback tests
- 📶 real network tests
- 🎬 replay tests

👀 Network and security reviewers checked the changes.

📚 More details: `docs/architecture/netcode/`
