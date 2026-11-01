# Weapon Brawl: Unity combat prototype

Implements section 4.2 of the technical document (5 weapon classes), with an arcade fighting-game UI:
modern-anime / comic look, P1 coral vs P2 cyan on off-white, and hard-angled panels throughout.
Everything is built from code: no prefabs, sprites or animation clips are needed.

## Run it

1. Unity 2021.3 or newer (2D or 3D template; built-in or URP).
2. Copy the `Assets/Scripts/WeaponBrawl` folder into your project.
3. **Edit > Project Settings > Player > Active Input Handling** = `Input Manager (Old)` or `Both`.
4. In a new empty scene, create an empty GameObject and add **GameBootstrap**. Press Play.

Gamepad stick and D-pad axes are added to the Input Manager automatically when Unity imports the scripts.
If sticks don't respond, run **Tools > Weapon Brawl > Install Gamepad Axes**.

## Character select flow

1. **Fighter:** move the cursor on the grid and confirm (the sixth cell is RANDOM).
2. **Passive and look:** up/down chooses 1 of 2 passives, left/right changes the weapon's look (for example hammer, mace or claymore). Confirm, and the panel stamps READY. The look never affects gameplay: every weapon of a type has exactly the same stats, skills and passives.
3. **Second slot:** P2 presses Confirm at any time to join. Otherwise P1 picks for the CPU.
4. **Stage:** when both are ready, pick a stage left/right, then confirm or press Start to fight.

Tabs: **LOCAL** (versus P2 or the CPU), **TRAINING** (the opponent is a passive dummy), **SETTINGS** (match time, CPU AI,
camera shake), **ONLINE** (placeholder; this build has no netcode). Mouse and touch also work on every element.

## Controls

Prompts on screen switch automatically between gamepad and keyboard, based on the last device used.

| Action | Gamepad (Xbox layout) | P1 keyboard | P2 keyboard |
|---|---|---|---|
| Move / menu cursor | Left stick, D-pad | W A S D | Arrows |
| Menu confirm / back | A / B | J or Enter / X, Backspace or Esc | M / N |
| Menu tabs | LB / RB | Q / E | – |
| Start match | Start | Enter | Right Shift |
| Jump | A | Space or W | Up |
| Attack (hold to chain) | X | J | M |
| Skill 1 / 2 / 3 | LB / RB / Y | Q / E / F | K / L / ; |
| Ultimate | B | R | O |
| Back to character select | Back | Tab | – |

In battle, `T` toggles the CPU's AI and `F1` fills the ultimate meter (debug).

## Adding real portrait art

The portraits are stand-ins drawn from polygons, because illustrated anime art can't be generated in code. To use real art,
import each image as a Sprite at `Assets/Resources/Portraits/<Class>.png`, where `<Class>` is `Melee`, `Heavy`,
`Ranged`, `Magic` or `Polearm`. Draw the fighter facing right; P2's side is mirrored automatically.

## Files

| Folder | What's inside |
|---|---|
| `Data/WeaponDatabase.cs` | All 5 classes: stats, playstyle tag, 2 passives each, skills (+cooldowns), ultimates |
| `Data/StageDatabase.cs` | The 3 stages, plus `MatchConfig` (the result of character select) |
| `Core/GameInput.cs` | Per-player gamepad + keyboard bindings, menu repeat, prompt labels |
| `Core/Fighter.cs` | Health, damage formula (crit ×1.5, −1 dmg per 10 DEF), passives, buffs/debuffs, parry, stun, ult meter |
| `Core/Projectile.cs`, `Core/Fx.cs` | Projectiles, hit bursts, damage popups, lightning, camera shake |
| `Animation/WeaponAnimator.cs` | Procedural animation: weapon pose tweens, body lean, swing trails, squash. Also fires Animator triggers |
| `Animation/WeaponVisuals.cs` | Placeholder models: sword, hammer, bow, staff, spear |
| `Weapons/*Weapon.cs` | One class per weapon type: combo steps, 3 skills, ultimate |
| `UI/CharacterSelectUI.cs` | Tabs, roster grid, P1/P2 portraits, passives, stage carousel, settings, button legend |
| `UI/Style/` | `UITheme` (palette), `SkewPanel` (parallelograms, trapezoids), `ComicArt` (splatters, brush, halftone), `ComicFigure` (stand-in art) |
| `UI/SkillBarUI.cs`, `UI/SkillButton.cs` | Attack/skill/ult buttons with cooldown shutters and live button prompts |
| `UI/BattleHUD.cs` | Top-corner cards (weapon icon, health %, ult meter, status), match timer, K.O. banner |
| `Editor/InputAxesInstaller.cs` | Adds the per-player gamepad axes to the Input Manager |

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
- **Heavy "Energy"**: the doc gives no numbers; implemented as a 6 s cooldown, 1.2× damage and a 1 s stun on hit.
- **Weapon looks**: the doc's examples, shown as cosmetic variants. Magic's elements only recolor the staff and its normal bolts; Fireball and Fire Ring stay fire, as the doc describes them. Slingshots fire stones with the same hit size as arrows.
- **Parry**: 0.4 s window. Reflects the full incoming hit, stuns the attacker, then counter-slashes.
- **Playstyle tags**: Melee = Rush Down, Heavy = Grappler, Ranged = Zoner, Magic = Setplay, Polearm = Mix Up.
- **Gamepads**: button numbers assume an Xbox-style pad on Windows. Other pads need the mapping in `GameInput.cs` changed.
- Fighters respawn 2 s after a K.O. (training-style loop). The default match time is 3:00.
