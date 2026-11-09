# Chibi Clash: How Online Play Works (Made Simple)

This guide explains how Chibi Clash plays over the internet, in easy words.
Every technical word is explained the first time it is used.

---

## 🧱 How a fight works online

- **Same fight on both screens.** Both computers follow the same fight rules, like two people following the
  same recipe. Same buttons in, same fight out on both screens.
- **Only button presses are sent.** Your computer just tells your friend's computer "I pressed punch."
  That is tiny, so it is very fast.
- **Rollback netcode.** *Netcode* = the part of a game that handles playing over the internet.
  Messages take a moment to travel, so your game **guesses** what your friend will do and keeps going.
  If the guess was wrong, it quickly fixes the screen, so the fight never freezes.
- **Version check.** Before a fight, both games check they are the same version, like making sure two people
  play by the same rulebook. If not, the game asks you to update.
- **Tested.** Both screens stay the same even when the internet is slow or loses messages.

---

## 🌐 How players connect

- **No game server.** A *server* is a big computer a company runs to host games. Chibi Clash does not need one:
  the two players' games run the fight themselves. This is called **peer-to-peer**
  (*peer* = player, so "player to player").
- **Unity Relay** is like a **post office** run by Unity. Home internet blocks strangers from connecting
  directly, so Relay passes the messages between you. It works **anywhere in the world**, on any Wi-Fi or
  phone internet.
- **LAN** (*Local Area Network*) = everyone on the **same Wi-Fi** (same house or school). Then players connect
  directly, with no post office needed.
- **Leaving a fight:** if a player quits, **the other player wins**.
  If both get knocked out at the same moment, it is a **draw**.

---

## 🎮 Game modes (on the home page)

| Mode | How it works | Internet? |
|---|---|---|
| **MATCHMAKING** | The game finds someone to fight. It looks for players **close to you** first: your country → nearby countries → your continent → the whole world. Closer means less delay. | Needed |
| **ROOMS** | Make your own named room → your friend joins → both pick a character → both press **OK** → anyone presses **START FIGHT**. | Needed |
| **VS AI** | Fight the computer. The *AI* (a computer-controlled player) picks a random fighter. You press **START BATTLE**. | **Not needed** |
| **Local Versus / Training** | Play on one computer. | Not needed |

---

## 👤 Players

- **First time:** the game asks **your age**, then **your name**.
- **Your name** shows everywhere (menus, fights, rooms, friends, your opponent's screen) and is
  **saved automatically** on your computer. Change it any time with the name button at the top.
- **Friends:** invite people by typing their name (like `Kira#4821`).
  A stranger has to say "yes" to being friends first, so nobody can spam you.
- **Age list:** each new player's age and name are saved in a file called **`player-ages.csv`**
  (a simple table you can open in Excel). Every age can play every mode.

---

## 🔒 Safety

- Rude or fake names are blocked.
- Messages from cheaters or broken games are **ignored**.
- No invite spam, and you can **block** annoying players.
- A network expert and a security expert (AI helpers) checked everything **5 times**, and every problem was fixed.

---

## 🛡️ Keeping it safe for future work

- **Tests** = small automatic checks that make sure things still work. There are **992**, and they **all pass**.
- **Guard tests** are like alarms: if someone changes something that could break online play,
  the alarm goes off and explains what to do.
- **`docs/architecture/netcode/DEVELOPER-GUIDE.md`** shows which parts are safe to change (menus, art)
  and which parts to leave alone.

---

## 🧑‍🤝‍🧑 Can my friends play with me?

### ✅ Works right now
- **VS AI, Training, Local Versus:** nothing to set up.
- **LAN** (same Wi-Fi): works now.

### ❌ Needs setup first: internet play (Matchmaking, Rooms, Friends)

**Step 1: Connect the game to Unity (one time, by the project owner)**
1. Open the game in **Unity**.
2. Go to **Edit → Project Settings → Services**, click **Link**, and pick (or create) a project.
3. Go to **cloud.unity.com**, open that project, and **turn on**:
   - **Relay** (connects players over the internet)
   - **Lobby** (rooms and matchmaking)
   - **Friends** (friends list and invites)

**Step 2: Add your friends**
- On **cloud.unity.com** go to **Members** and invite your friends' Unity accounts.

**Step 3: Upload the code**
- Upload (*push*) the latest code to **GitHub** (*the website that stores the code*).

**Step 4: Your friends get the new version**
1. They **pull** (download) the latest code.
2. They open it in **Unity Hub** with the **same Unity version: 6000.6.7f1**.
3. They press **Play**, choose a name, and play online.

### 💡 Easier option for friends who are not developers
Make a **game file (.exe)** in Unity (**File → Build**) after Step 1, and send it to them.
They just **double-click and play**, with no Unity, GitHub or setup.

### ⚠️ Remember
- **Everyone needs the same version** of the game, or the fight will not start.
- **Test with one friend first**, since online play has not been tried live yet.
