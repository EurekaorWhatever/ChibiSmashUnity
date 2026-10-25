# Weapon Brawl: Unity combat prototype

Implements section 4.2 of the technical document (5 weapon classes) plus the HUD and selection UI from section 5.
Everything is built from code: no prefabs, sprites or animation clips are needed.

## Run it

1. Unity 2021.3 or newer (2D or 3D template; built-in or URP).
2. Copy the `Assets/Scripts/WeaponBrawl` folder into your project.
3. **Edit > Project Settings > Player > Active Input Handling** = `Input Manager (Old)` or `Both`.
4. In a new empty scene, create an empty GameObject and add **GameBootstrap**. Press Play.

## Controls

| Action | Key | On-screen |
|---|---|---|
| Move / Jump | A, D / Space (or arrows, W) | – |
| Attack (combo loop, hold to chain) | J | big button |
| Skill 1 / 2 / 3 | Q / E / F | 3 round buttons with cooldown sweep |
| Ultimate | R | gold button (fills with the ult meter) |
| Change weapon + passive | Tab | – |
| Toggle CPU AI | T | – |
| Fill ultimate (debug) | P | – |

## Files

| Folder | What's inside |
|---|---|
| `Data/WeaponDatabase.cs` | All 5 classes: stats, 2 passives each, skills (+cooldowns), ultimates |
| `Core/Fighter.cs` | Health, damage formula (crit ×1.5, −1 dmg per 10 DEF), passives, buffs/debuffs, parry, stun, ult meter |
| `Core/Projectile.cs`, `Core/Fx.cs` | Projectiles, hit bursts, damage popups, lightning, camera shake |
| `Animation/WeaponAnimator.cs` | Procedural animation: weapon pose tweens, body lean, swing trails, squash. Also fires Animator triggers |
| `Animation/WeaponVisuals.cs` | Placeholder models: sword, hammer, bow, staff, spear |
| `Weapons/*Weapon.cs` | One class per weapon type: combo steps, 3 skills, ultimate |
| `UI/PassiveSelectionUI.cs` | Step 1: pick a class (stat cards). Step 2: pick 1 of 2 passives, then Enter Battle |
| `UI/SkillBarUI.cs`, `UI/SkillButton.cs` | Attack/skill/ult buttons with radial cooldowns, key badges, ready pulse |
| `UI/BattleHUD.cs` | Top-corner cards (portrait, health %, ult meter, status), match timer, K.O. banner |

## Using real animation clips later

Each move is a list of poses in its weapon script. To use your own art:
add an `Animator` to the fighter, assign it to `WeaponAnimator.animator`, and create triggers named
`Attack1`…`Attack5`, `Skill1`…`Skill3`, `Ultimate`. They fire automatically when the move starts.
Untick `useProcedural` if your clips should fully drive the rig. Hit timing still comes from the weapon scripts.

## Assumptions (not specified in the document, so adjust as needed)

- **Skill cooldowns**: 4–12 s (see `WeaponDatabase.cs`).
- **Ultimate charge**: +2/s passively, +0.5 per damage dealt, +0.35 per damage taken; 100 = ready.
- **Magic "Hex"**: the doc reads "1a 0%"; implemented as a **10%** chance. Debuff lasts 4 s.
- **Polearm "Armor Piercer"**: defense −15% lasts 5 s.
- **Ranged "Fleet Foot"**: triggers automatically on your first action when off cooldown.
- **Heavy** lists only 2 skills (the first bullet is blank), so its 3rd skill slot shows as locked.
- **Parry**: 0.4 s window. Reflects the full incoming hit, stuns the attacker, then counter-slashes.
- Fighters respawn 2 s after a K.O. (training-style loop). The match timer is 3:00.
