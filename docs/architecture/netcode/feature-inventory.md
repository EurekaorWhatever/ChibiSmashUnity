# Chibi Clash: Feature Inventory for the Deterministic (Rollback) Rewrite

Scope: the original full-feature Fighter path, compared against `Netcode/ChibiSimulation.cs`.
All paths are relative to `Assets/ChibiClash/Scripts/`. Abbreviations: **F** = `Fighter/Fighter.cs`, **FI** = `Fighter/FighterInput.cs`, **FV** = `Fighter/FighterVisual.cs`, **CO** = `Combat/CombatObjects.cs`, **CA** = `Core/CombatAuthority.cs`, **CD** = `Core/ClassData.cs`, **B** = `Core/Bindings.cs`, **MC** = `Game/MatchController.cs`, **ST** = `Game/Stage.cs`, **GM** = `Game/GameManager.cs`, **P** = `CV/Pose.cs`, **CP** = `CV/CameraPose.cs`, **SIM** = `Netcode/ChibiSimulation.cs`, **CS** = `Netcode/ChibiCombatStats.cs`, **IP** = `Netcode/ChibiInputPacket.cs`.

Non-determinism legend: **RB** = Rigidbody2D velocity/integration, **PQ** = Physics2D query or collision (OverlapBox, IgnoreCollision, PlatformEffector2D), **T** = `Time.time` / `Time.deltaTime` / `Time.unscaledDeltaTime` / `Time.timeScale`, **CR** = coroutine / `WaitForSeconds` / `yield return null`, **RNG** = `UnityEngine.Random`, **FL** = float math, **UO** = Update-order dependence (MonoBehaviour.Update order across Fighters / Projectiles / Minions / MatchController), **TR** = reads or writes `transform.position` directly.

Every row in the original code has FL, so FL is listed only where it matters more than usual (accumulators, Lerp, normalize, sqrt).

## Cross-cutting facts that affect every feature

| Fact | Where | Consequence |
|---|---|---|
| All fighter logic runs in `Update()` with variable `Time.deltaTime` | F:169-197 | Results depend on frame rate. A fixed 60 Hz tick is needed (SIM:8 `TickRate = 60`) |
| `Time.timeScale` drives hitstop, cinematic and finish slow-mo, and it scales *gameplay* dt, `WaitForSeconds` and Rigidbody2D | MC:108-116 | Hitstop and slow-mo change gameplay timing, so they are not just visual. They have to be modelled as tick counts in the sim |
| One global `UnityEngine.Random` stream is shared by gameplay rolls (crit CA:45, Ward F:574, Hex F:647, Armor Break F:648, arrow spread F:871 and F:1025-1027, CPU FI:176-217) and by visuals (Fx.Burst CO:304-306, FireRing sparks CO:244-245, camera shake ST:179, pose jitter P:236) | as listed | A gameplay roll's outcome depends on how many particles were emitted beforehand. Gameplay needs its own seeded RNG in state (SIM uses `ChibiRng`, SIM:224) |
| Hit resolution is immediate and called from whoever's Update runs first (fighter A's MeleeBox calls B.ReceiveHit) | F:497-505, CO:89-105 | Order bias toward Update order. SIM probes both fighters, then applies (SIM:327-331) |
| `Interrupt()` relies on `StopAllCoroutines()` to cancel pending strikes | F:670-677 | Every coroutine move needs an explicit move state machine (SIM `MoveSlot/MoveTick`) |
| Ult is 0..100 float; SIM uses 0..1000 int. Velocities are units/s in the original and milli-units/tick in SIM (x1000/60) | F:13, SIM:24 | Unit conversion needed for every constant |

---

## 1. Movement

| Feature | Where (file:line) | Non-deterministic parts | In ChibiSimulation? | Visual-only? |
|---|---|---|---|---|
| Horizontal run with MoveTowards accel (70 ground / 40 air u/s²), only when actionLock<=0 | F:280-285 | RB, T, FL | yes (SIM:411-412, GroundAccel 20 / AirAccel 11 milli/tick²) | no |
| Decel to 0 at 25 u/s² while attacking on the ground | F:286-289 | RB, T | yes (SIM:490 MoveFriction) | no |
| Facing follows input when free; `SetFacing` updates visual | F:284, F:363-367 | none (int) | yes (SIM:413) | no (the visual flip is visual) |
| Ground jump 13 u/s + dust burst | F:294-298 | RB, RNG (Fx) | yes (SIM:418 JumpVel 217) | no (the burst is visual) |
| One air jump 11.5 u/s, reset on ground | F:299-304, F:255 | RB | yes (SIM:419, SIM:535) | no |
| Fast-fall: Down in air while vy<3: -45 u/s², capped at -24 u/s | F:307 | RB, T | partial (SIM:414 FastFall 8, cap is the global MaxFall 330 ≈ 19.8 u/s; trigger is vy<=0, not vy<3) | no |
| Drop through one-way platform (Down+Jump), 0.35 s IgnoreCollision | F:293, F:347-361, F:216-224 | PQ (OverlapBox, IgnoreCollision, PlatformEffector2D), T | **no** (SIM has no platforms) | no |
| Dash: 0.13 s at 17 u/s, 0.55 s cd, 0.1 s invuln, direction from input or facing; overrides velocity; blocks blocking | F:309-316, F:268-272, F:262 | RB, T, RNG (Fx) | **no** (no Dash button in IP:10-21) | no |
| Ground check: OverlapBox under feet, ignores triggers, dynamic bodies and the dropped platform; not grounded while vy>0.5 | F:240-256 | PQ, UO | partial (SIM:531-536: flat Y=0 floor, |X|<=StageHalfWidth only) | no |
| Gravity: gravityScale 3.6 (≈35.3 u/s²), continuous collision, interpolation, zero friction | F:92-100 | RB, T | yes (SIM:526 Gravity 10 ≈ 36 u/s²); global MaxFall 330 has no original equivalent | no (interpolation is visual) |
| Stun / disabled friction: 30 u/s² toward 0 while grounded and not stunned/held | F:186-189 | RB, T | partial (SIM:382 StunFriction 8 is applied *during* stun; the original applies it only when not stunned, e.g. intro/paused/dead) | no |
| Fighters pass through each other | MC:72 | PQ | yes (SIM has no fighter-fighter collision) | no |
| Move speed modifiers: Heavy Juggernaut x0.85, Hex slow x0.85, Tailwind x1.1 | F:135-145 | T (`Time.time < slowUntil`) | yes (CS:126 EffectiveMoveSpeed) | no |

**Count: 13**

---

## 2. Attacks and moves

### 2a. Normals (all classes)

| Feature | Where (file:line) | Non-deterministic parts | In ChibiSimulation? | Visual-only? |
|---|---|---|---|---|
| Light/heavy start: duration 0.24 s / 0.42 s divided by AttackSpeed; actionLock = dur | F:453-466 | T, FL | yes (SIM:436-446 startup/active/recovery scaled by CS:139 ScaleFrames) | no |
| Light combo chain: index resets after 0.9 s; finisher at patternLength-1 = x1.5 mult; heavy x1.8 and resets the chain | F:457-461 | T (`Time.time - lastAttackTime`) | yes (SIM:448-461, ComboWindowTicks 54) | no |
| Delayed strike at dur*0.4 via coroutine; kb/stun by type (heavy 8,6 / 0.5; finisher 7,7 / 0.45; light 2.5,2 / 0.28) | F:465, F:468-495 | CR (`WaitForSeconds`), T | partial (SIM uses per-class frame data SIM:84-127; values were re-tuned per class, not copied) | no |
| Melee hitbox: AABB in front at reach*(heavy 1.15), height 1.3 | F:488, F:497-505, F:507-512 | FL, UO (immediate ReceiveHit) | yes (SIM:541-576 MeleeProbe + Overlaps) | no |
| Ranged normals: arrow 18 u/s (heavy 21), kb x0.6, life 1.3 s, trail | F:475-478, F:536-541 | (see Projectiles) | yes (SIM:104-105 ps 300/350) | trail is visual |
| Magic normals: bolt 14 u/s, size 0.35/0.55, kb x0.6 | F:479-485 | (see Projectiles) | yes (SIM:113-114) | colour is visual |
| Set reload: after the 4th shot, Archer/Mage normals locked 1.5 s (`NormalsLocked`) | F:62-63, F:337, F:474 | T | yes (SIM:430-431, SIM:455-456 SetCooldownTicks 90). In the original, reload starts when the strike fires; in SIM it starts at move start + startup | no |
| Spear out blocks normals (DoNormal silently returns) | F:455 | none | **no** | no |
| Sword Aura wave on every normal (light **and** heavy) | F:490-494 | T | partial (SIM:514-515 light only) | no |
| Attack speed modifiers: Melee Swift Blade x1.15, Heavy Crusher x0.85, Hex sluggish x0.85 | F:123-133 | T | yes (CS:132 AttackSpeedPermille) | no |
| Attack power: Heavy x1.2 for **both** passives, Hex weaken x0.9 | F:112-121 | T | verify (CS:50-51 DamageBonusPermille exists; Heavy-both-passives not confirmed in what was read) | no |
| `TryNormal` test hook | F:340-345 | none | no (test API) | no |

### 2b. Skills (dispatch plus per class)

| Feature | Where (file:line) | Non-deterministic parts | In ChibiSimulation? | Visual-only? |
|---|---|---|---|---|
| Skill dispatch: first ready skill whose button is pressed; cooldown from ClassDef; skill-name popup | F:318-333, F:718-751, F:211 | T | yes (SIM:425-429 priority ult > skills > heavy > light; the original has light > heavy > skills, and ult is separate) | popup is visual |
| Input buffer (6 ticks) | none in the original (edge-triggered only) | n/a | SIM-only addition (SIM:375-377) | no |
| **Melee: Dash & Thrust** - 0.22 s at 18 u/s, one hit 1.6x, kb 7,4, stun 0.45, then 3 u/s | F:753-766 | CR (per-frame loop), RB, T | yes (SIM:88 vx 300) | no |
| **Melee: Parry** - 0.35 s reflect stance, lock 0.4 | F:768-773 | T | yes (SIM:89 FParry) | no |
| **Melee: Thrust Barrage** - 6 hits 0.55x every 0.11 s, last hit kb 6,4 | F:775-786 | CR (`WaitForSeconds(0.11)`), T | partial (SIM:90 rehit every 6 ticks; the larger last-hit knockback is missing) | no |
| **Heavy: Grab** - box check, stun 1.2, target held (kinematic), lifted over 0.3 s, slammed over 0.12 s, unblockable 2.2x kb 4,8 | F:788-818 | CR, TR (target `transform.position` Lerp), RB (bodyType Kinematic), T | partial (SIM:97 unblockable melee box; no hold/carry, no lift/slam positions) | lift/slam animation is visual, but the held state and final position are not |
| **Heavy: Tornado Spin** - 1.0 s, steerable 3.5 u/s by MoveX, hit every 0.2 s in a 1.6 radius circle, 0.7x | F:820-842 | CR, RB, T, FL | partial (SIM:98 centered box, rehit 12 ticks; no steering during the move) | no |
| **Ranged: Backstep Shot** - 0.15 s at -16,2 u/s, then a big arrow 1.5x | F:844-855 | CR, RB, T | yes (SIM:106 vx -280, projectile) | no |
| **Ranged: Bowstring Choke** - instant box 1.1x1.3, 1.2x, kb 11,5, stun 0.6 | F:857-863 | none beyond the hit | yes (SIM:107) | no |
| **Ranged: Arrow Barrage** - 5 arrows every 0.09 s, random Y offset -0.25..0.3 | F:865-874 | CR, **RNG**, T | partial (SIM:108 deterministic yOff pattern SIM:505) | no |
| **Magic: Homing Fireball** - 9 u/s, 3 s life, 2D steer (Lerp 3.5/s toward target, constant speed) | F:876-884, CO:51-55 | FL (normalize/Lerp), T | partial (SIM:600-605 steers vertical velocity only) | no |
| **Magic: Summon Minions** - 3 walking minions | F:886-892 (see section 5) | see section 5 | partial (SIM:116 "3 seeking orbs (minion stand-in)") | no |
| **Magic: Thunder Cloud** - follows target 4.6 s, strikes every 1.1 s (first at 0.8 s) | F:894-899 (see section 5) | see section 5 | partial (SIM:117 one targeted strike after 40 ticks) | no |
| **Polearm: Leap Slam** - jump 6,14, waits until grounded (max 1.3 s), box 2.2x1.6, 1.8x | F:901-917 | CR, RB, T, PQ (Grounded) | partial (SIM:124 fixed 22-tick startup, not "until landing") | no |
| **Polearm: Spin Guard** - 1.2 s projectile-destroy aura, 4 hits every 0.3 s in a 1.5 circle | F:919-933, F:525 | CR (`WaitForSeconds`), T | yes (SIM:125 FSpinGuard, rehit 18; SIM:616) | no |
| **Polearm: Throw & Recall** - spear 16 u/s, non-destructible, returns after 0.45 s at 18 u/s homing to owner with 0.8x mult and can re-hit; weapon hidden until it returns | F:935-950, CO:57-72 | T, FL, callback `OnGone` on Destroy | partial (SIM:126 plain projectile; no return, no re-hit, not indestructible, no spearOut) | weapon hide is visual |

### 2c. Passives

| Feature | Where (file:line) | Non-deterministic parts | In ChibiSimulation? | Visual-only? |
|---|---|---|---|---|
| Melee Swift Blade +15% atk speed / Blood Edge 5% life steal | F:128, F:646 | FL | yes (CS:50, CS:53, SIM:720) | no |
| Heavy Crusher / Juggernaut | F:117, F:129, F:140 | FL | yes / verify (see 2a) | no |
| Ranged Eagle Eye +20% crit | F:147 | none | yes (CS:44 via Create) | no |
| Ranged Tailwind: +10% speed 10 s, every 30 s, first proc when the match starts | F:232-237 | T | yes (SIM:368-372) | popup is visual |
| Magic Hex: 10% on hit, 4 s slow/weaken/sluggish | F:647, F:651-655 | **RNG**, T | yes (SIM:721-722) | no |
| Magic Ward: 20% to negate a hit | F:574-579 | **RNG** | yes (SIM:679) | no |
| Polearm Armor Break: 10% on hit, -15% DEF for 5 s | F:648, F:657-661, F:148 | **RNG**, T | yes (SIM:723-724) | no |
| Polearm Patience: >=3 s since the last attack or skill => x1.5 (normals and skills, not ult); the first attack of the match qualifies (lastAttackTime=-10) | F:443-451, F:53, F:721-722 | T | partial (SIM:463-465: IdleTicks counts only while free and starts at 0, so the first attack does not qualify; also applies to the ult) | popup is visual |

**Count: 12 normals + 16 skills + 8 passives = 36**

---

## 3. Defense

| Feature | Where (file:line) | Non-deterministic parts | In ChibiSimulation? | Visual-only? |
|---|---|---|---|---|
| Block (hold): needs actionLock<=0, no dash, guard not broken; works **in the air**; ground friction 60 u/s² | F:261-264, F:274-278 | T (blockStart = Time.time), RB | partial (SIM:400-405 needs Grounded; friction 40 milli/tick² differs) | no |
| Block via CrossArms camera gesture (not during ult window, telemetry valid) | F:261 | T (gesture timing), CV | **no** | no |
| Perfect parry: block raised <=0.18 s => attacker stunned 0.45 s, +15 ult, hitstop 0.1 | F:563-572, CA:28 | T | yes (SIM:670-677 ParryWindow 11, stun 27); no hitstop | burst/text are visual |
| Parry skill reflect: reflected damage rolled from the attacker vs the attacker's DEF, push away 5,4, stun 0.5, +15 ult, hitstop 0.12 | F:550-561 | RNG (crit), T | yes (SIM:657-668); no hitstop; attacker does not get the +1.5 "when hit" ult | text is visual |
| Guard meter: drains by raw damage, regen 24/s when not blocking and not broken; max 40+3·DEF | F:65-66, F:202-203, F:586-593, CA:31, CA:37 | T, FL | yes (SIM:363-365, SIM:685; x10 ints) | no |
| Guard break: guard 0 => full damage, stun >=1.0 s, cannot block 3 s, shake | F:587-592, F:601-609, CA:29-30 | T | yes (SIM:686-693) | burst/shake are visual |
| Blocked damage fraction clamp(0.5-DEF·0.01, 0.1, 0.5); chip knockback x0.25, no stun | F:593, F:596, CA:34 | FL | yes (SIM:694, SIM:702) (SIM blocks only X knockback; the original applies x0.25 to both X and Y) | no |
| Defense mitigation: max(1, raw - floor(DEF/10)) | F:595, CA:52-55 | FL | yes (CS:151) | no |
| Damage roll and crit (critRate, critDmg) | CA:43-49 | **RNG**, FL | yes (CS:163 with ChibiRng) | no |
| Knockback scale x0.5 for Archer/Mage attackers | F:548, CA:40-41 | none | yes (SIM:654) | no |
| Invulnerability check (also blocked when `!Match.Running`) | F:547 | none | yes (SIM:653); no Running gate | no |
| ApplyDamage: HP, stun max(), interrupt, set velocity to kb (not while held), +1.5 ult, hitstop 0.045/0.08 crit, shake, KO trigger | F:611-628 | RB, T | yes for HP/stun/kb/ult (SIM:697-710); hitstop **no** | flash/burst/number popup/shake are visual |
| Stun() helper: zero vx, keep falling vy, interrupt | F:663-668 | RB | partial (SIM sets Stun but leaves velocity alone) | no |
| Interrupt: cancel coroutines, actionLock, dash, block, release held target | F:670-677, F:679-683 | CR | partial (SIM:710 MoveSlot=-1 etc.; no held state) | no |
| Projectile destroy by Spin Guard (r 1.6) / Fire Ring (r 2.1), destructible only | F:522-528 | T, FL (Distance) | yes (SIM:613-616), but SIM has no non-destructible flag | no |

**Count: 15**

---

## 4. Ultimates and gestures

| Feature | Where (file:line) | Non-deterministic parts | In ChibiSimulation? | Visual-only? |
|---|---|---|---|---|
| Ult gain sources: hit 3 (+2 at combo>=3), x2 under 30% HP; blocked hit +1; parry +15; being damaged +1.5; cap 100 | F:436-439, F:557, F:566, F:621, F:632-644 | FL | yes (SIM:25, SIM:698-719) | no |
| Training "T" key sets P1 ult to max | MC:129-132 | input outside the input struct | **no** | no |
| Open ult window (button, ult full, not retry cd): 3 s prompt "Perform: <gesture>" | F:396-403, F:13-14 | none | **no** (SIM fires the ult directly on the button, SIM:424) | prompt text is HUD |
| Ult window countdown on **unscaled** time, frozen while paused | F:405-411 | T (unscaledDeltaTime) | **no** | no |
| Gesture match: Stable == class gesture for >=0.15 s; rejected if telemetry invalid; fires even while stunned/held (only needs InputEnabled and alive) | F:412-425 | T, CV data | **no** | no |
| Window timeout => Failed, 1.5 s retry cooldown (unscaled) | F:426-433, F:210 | T (unscaled) | **no** | no |
| Prompt state machine Hidden/Preparing/Detected/Failed with timers | F:8, F:38-40, F:226-230 | T (unscaled) | **no** | yes (HUD feedback; derive from sim state) |
| FireUltimate common: ult=0, UltsUsed++, interrupt, clear hitstun, invuln >=0.6 s, cinematic | F:954-962 | T | partial (SIM:469-473 invuln 36 ticks; does not clear stun; no cinematic) | burst is visual |
| **Melee: Sword Aura** 10 s, wave on normals | F:966, F:490-494 | T | partial (SIM:91, SIM:511, SIM:514; light only) | aura glow is visual (FV:219-222) |
| **Heavy: Smash** - jump 15, invuln 1.4, wait until grounded, two piercing indestructible waves both directions 13 u/s, 3.0x, growing height 1.4/s | F:969, F:984-1008 | CR, RB, T, PQ (Grounded) | partial (SIM:100 centered 5.0-wide box at fixed frames; no waves, no pierce, no grow) | no |
| **Ranged: Thousand Arrows** - harmless lead arrow 0.45 s, on expiry 18 arrows aimed at the opponent with random spread/speed | F:972, F:1010-1034 | **RNG**, FL (normalize), callback OnExpire | partial (SIM:109 horizontal stream, rehit 3 ticks) | no |
| **Magic: Fire Ring** 10 s, 0.4x hit every 0.5 s within 2.1, destroys projectiles | F:974-976, CO:224-255 | T, RNG (sparks only) | yes (SIM:118, SIM:629-640, SIM:615) | ring sprite/sparks are visual |
| **Polearm: Polearm Kick** - out of range (8x3): dash 20 u/s 0.25 s + box; in range: teleport-lerp to target 0.15 s, hold, unblockable 1.5x, 0.35 s, self vy 12, 0.32 s, release, unblockable 2.5x kb 12,9; invuln 2.0 | F:979, F:1036-1079 | CR, TR, RB, T | partial (SIM:127 single unblockable box) | no |
| Pose: Super Jump (RaiseHands just started, 3 s cd): vy 19, refill air jump | F:375-382 | CV, T | **no** | no |
| Pose: Fireball (PunchForward just started, 4 s cd): 0.8x projectile 13 u/s | F:383-391 | CV, T | **no** | no |
| Poses disabled during the ult window or when telemetry is invalid | F:373 | CV | **no** | no |
| GestureTracker: Raw classify, 0.1 s candidate => Stable, StableTime, JustStarted | P:149-197 | T (unscaled dt from F:172), FL | **no** | no |
| GestureClassifier (keypoint rules) and posture score | P:46-112 | FL | **no** | no |
| TelemetryValidator: low confidence, frozen feed (>20 samples), instant snap (>14/s) => invalid for 1 s | P:115-145 | T, FL | **no** | no |
| SimulatedPoseSource: key/d-pad gesture => template pose with random jitter | P:210-285 | **RNG** (P:236) | **no** | no |
| MoveNet camera pose source (20 Hz inference, smoothing) and AutoPoseSource (P1 real camera if enabled) | CP:12-147, CP:151-190 | T (unscaled), async inference, FL | **no** (cannot be in the sim; must become an input bit) | no |
| Webcam feed / brightness check | P:289-350 | T (unscaled) | no | yes (device layer) |

**Count: 22**

---

## 5. Projectiles, minions and combat objects

| Feature | Where (file:line) | Non-deterministic parts | In ChibiSimulation? | Visual-only? |
|---|---|---|---|---|
| Projectile spawn (owner, pos = Center + offset·facing, vel, HitSpec, radius, life) | CO:27-42, F:530-534 | GameObject instantiate, UO (new objects run Update from the next frame) | yes (SIM:578-589, fixed pool of 24 SIM:22) | sprites are visual |
| Projectile movement and life countdown | CO:44-47, CO:74-75 | T, FL, TR | yes (SIM:606-607) | no |
| Homing steer (2D, constant speed) | CO:51-55 | T, FL (normalize, Lerp) | partial (Y-only, SIM:600-605) | no |
| Return/recall (ReturnAfter, ReturnMult, re-arm hit, home to owner at 18 u/s, despawn within 0.5) | CO:57-72 | T, FL | **no** | no |
| Pierce (does not despawn on hit) | CO:15, CO:104 | none | **no** | no |
| Harmless (no hit test; used by the Thousand Arrows lead) | CO:15, CO:89 | none | **no** | no |
| Destructible flag (spear and Smash waves ignore Spin Guard/Fire Ring) | CO:16, F:524 | none | **no** (all SIM projectiles are destructible) | no |
| GrowRate: Y scale grows, which also grows the hit radius | CO:17, CO:77, CO:97 | T, FL, TR (localScale is gameplay) | **no** | no (the scale feeds the hit radius) |
| OnExpire / OnGone callbacks (Thousand Arrows split; spear return flag) | CO:21-22, CO:110, CO:115 | callback on `Destroy` (end-of-frame timing) | **no** | no |
| Single hit per projectile (`hasHit`), knockback X sign from velocity | CO:89-105 | FL | yes (SIM:618-624, Dir = Sign(Vx)) | no |
| Hit test: circle (radius x scale.y) vs fighter AABB | CO:97-98, F:514-519 | FL | partial (SIM uses a fixed 150x100 box vs hurtbox, SIM:618-619) | no |
| Despawn bounds |x|>40, y<-30, y>40 | CO:108 | none | partial (SIM uses the blast zones, SIM:609) | no |
| Trail particles | CO:79-87 | RNG (Fx) | n/a | yes |
| **Minion**: Rigidbody2D gravity 3, walks 3.5 u/s toward the target, bonks 0.35x when |dx|<0.7 and |dy|<0.9, 1 s cd, hops back; 6 s life; ignores fighters and other minions | CO:119-171 | RB, PQ (IgnoreCollision, stage collision), T, UO, GetComponentsInChildren order | partial (SIM:116 stand-in homing orbs) | sprites/eye flip are visual |
| **ThunderCloud**: spawn 3 above the owner, Lerp toward target+3.2 at 2.5/s, strike every 1.1 s (first at 0.8) if |dx|<0.75, 0.8x kb 0,-3 projectile-type, 4.6 s life | CO:175-220 | T, FL (Lerp), TR | partial (SIM:117 one strike) | bolt sprite is visual |
| **FireRing** object: lifetime tied to owner.FireRingUntil, damage tick 0.5 s | CO:224-255 | T, RNG (sparks), FL | yes (SIM:629-640) | rotation/pulse/sparks are visual |
| Fx particles and floating text popups | CO:259-325 | RNG (shared stream), T | n/a | yes (but must stop consuming the gameplay RNG) |

**Count: 17**

---

## 6. Stage and platforms

| Feature | Where (file:line) | Non-deterministic parts | In ChibiSimulation? | Visual-only? |
|---|---|---|---|---|
| Three stages: Sky Dojo (halfWidth 8), Neon City (9), Lava Forge (7), groundY -2.5 | ST:19-56 | none | partial (SIM:15 one StageHalfWidth 8000; no stage index in ChibiMatchConfig SIM:33-38) | no |
| Blast zones: blastX = halfWidth+9, killY = groundY-9, ceilingY = 16 (world) | ST:65-67, MC:145-149 | TR | partial (SIM:16 BlastX 17000, BlastBottom -9000 match Sky Dojo; BlastTop 18000 vs original 18.5 above ground) | no |
| Spawns ±halfWidth·0.45 at groundY+1.5 | ST:68 | none | yes for Sky Dojo (SIM:297-298 ±3600, Y=0) | no |
| Respawn point (0, 6) world = 8.5 above ground | ST:69 | none | partial (SIM:16 RespawnY 6000 above ground) | no |
| Main solid stage collider (BoxCollider2D halfWidth·2 x 1.2) | ST:74-84 | PQ | yes as flat floor Y=0 (SIM:532-536); no side walls/underside collision | art is visual |
| One-way floating platforms (PlatformEffector2D, surfaceArc 170): 3 / 3 / 2 per stage | ST:32-34, ST:41-42, ST:48-50, ST:86-99 | PQ | **no** | art is visual |
| Lava visual (no damage) | ST:39 | none | n/a | yes |
| Decor (System.Random(7), deterministic) | ST:101-121 | none | n/a | yes |
| Camera: frame both fighters, zoom by distance, ult focus, shake | ST:125-183 | T (unscaled), RNG (shake) | n/a | yes |

**Count: 9**

---

## 7. Match flow

| Feature | Where (file:line) | Non-deterministic parts | In ChibiSimulation? | Visual-only? |
|---|---|---|---|---|
| Intro countdown 2.2 s (unscaled) before Running | MC:26, MC:122-127 | T (unscaled) | **no** (SIM starts live at frame 0) | no |
| InputEnabled = Running && !Paused gates all actions | MC:41, F:179 | none | **no** | no |
| Pause (Esc), timeScale 0, ult window frozen | MC:106, MC:109, F:406 | T | **no** (and not allowed online) | no |
| Match timer 180 s, counts scaled dt (so it slows during hitstop/slow-mo) | MC:27, MC:135 | T | partial (SIM:9, SIM:754 counts frames; no slow-down) | no |
| Timeout winner by HP ratio, Approximately => draw | MC:136-141 | FL (Approximately) | yes (SIM:754-758 integer ratio) | no |
| KO: first fighter to reach 0 loses (no double KO), K.O. text, shake | MC:154-166, F:627 | UO (who calls OnKO first) | partial (SIM:748-752 double KO = draw) | text/shake are visual |
| Ring out: blast zone => -15% max HP, respawn, 1.5 s invuln, interrupt self and an opponent holding us, clear parry/spin guard | F:692-708, MC:145-149 | TR, RB, UO | yes (SIM:734-745, RespawnInvuln 90); no held/opponent interrupt | burst/text are visual |
| Hitstop: timeScale 0.05 for 0.045-0.12 s (unscaled) | MC:112, MC:177, F:559, F:570, F:625 | T (gameplay-affecting) | **no** | no (it changes gameplay timing) |
| Ult cinematic: timeScale 0.2 for 0.8 s, camera focus, flash | MC:113, MC:184-193 | T | **no** | flash/focus are visual; the slow-mo is gameplay |
| Finish slow-mo 0.3 for 1.2 s after the end | MC:114 | T | **no** | yes (after the match ends) |
| Training: no timer, KO => full revive, CPU difficulty 0 dummy, T fills ult | MC:68, MC:129-132, MC:157-162, F:710-714, MC:92 | none | **no** | no |
| Rematch / quick match / start from config | GM:114-131, GM:207 | none | n/a (shell) | no |
| Screen shake setting | MC:179-182 | RNG (via camera) | n/a | yes |

**Count: 13**

---

## 8. Inputs

| Feature | Where (file:line) | Non-deterministic parts | In ChibiSimulation? | Visual-only? |
|---|---|---|---|---|
| FighterInput command struct: MoveX (float), Down, Jump, Light, Heavy, Block, Dash, Ult, Skill[3] | FI:7-25 | FL (MoveX) | partial (IP:10-21 has Dir/Light/Heavy/Jump/Shield/Skill1-3/Ultimate; **no Dash**; Down comes from Dir) | no |
| Keyboard via rebindable Bindings (13 actions x 2 players, keys or mouse buttons, swap on conflict, save upgrade) | FI:50-68, B:8-105 | none (device polling) | n/a (device layer) | no |
| P2 numpad extras | FI:74-84 | none | n/a | no |
| Gamepad N for player N (stick threshold 0.25, Down < -0.6) | FI:87-102 | none | n/a | no |
| Press vs hold semantics (wasPressedThisFrame per render frame) | FI:59-67 | T (render-frame edges) | yes (SIM:354-356 derives edges from PrevInput) | no |
| Simulated gestures: P1 keys 1/2/3, P2 8/9/0 or numpad 7/8/9, d-pad | FI:106-135 | none | **no** (IP reserves bits 12..15, not sent) | no |
| Slouch toggle (P / -) for posture testing | FI:71, FI:83 | none | no | no (posture affects rewards) |
| Webcam pose for P1 when enabled | MC:95, CP:151-180 | async | **no** | no |

**Count: 8**

---

## 9. AI (CpuInput)

| Feature | Where (file:line) | Non-deterministic parts | In ChibiSimulation? | Visual-only? |
|---|---|---|---|---|
| Difficulty 0 = idle training dummy | FI:156, MC:92 | none | **no** | no |
| Off-stage recovery: move to centre, jump when falling, Melee dash-thrust skill | FI:163-171 | TR, FL | **no** | no |
| Think timer 0.1-0.3 s / skill; spacing by class (ranged 4.5, melee reach·0.8); jump if the target is above | FI:173-184 | **RNG**, T | **no** | no |
| Reactive block when the opponent IsAttacking within 2.5 | FI:182, FI:186-188 | **RNG** | **no** | no |
| Turn to face; attack chance per frame scaled by dt·60; 75% light | FI:190-199 | **RNG**, T | **no** | no |
| Random skill use | FI:201-205 | **RNG**, T | **no** | no |
| Ult press when full | FI:207 | **RNG**, T | **no** | no |
| Ult gesture: random delay 0.6-2.2 s, success 60% + 12%·difficulty | FI:210-227 | **RNG**, T | **no** | no |

**Count: 8**. CPU is offline only, but in the single-version game it drives the same sim, so it should produce `ChibiInputPacket`s from sim state with its own seeded RNG.

---

## 10. Rewards and progression hooks

| Feature | Where (file:line) | Non-deterministic parts | In ChibiSimulation? | Visual-only? |
|---|---|---|---|---|
| Per-fighter counters: HitsLanded, CombosLanded, UltsUsed, Parries, MaxCombo, ComboCount/comboTimer 1.1 s | F:42-44, F:213-214, F:633-639 | T | partial (SIM:157 has HitsLanded/CombosLanded/UltsUsed/Parries, ComboTimer 66; **no MaxCombo**) | combo popup is visual |
| Match coins: PvE 50/15, PvP 40/15 | MC:204-206 | none | n/a (post-match, outside the sim) | no |
| Posture coins: PostureAverage·30; posture sampled per frame for non-CPU fighters | MC:150, MC:208-211, P:199-206 | T, CV | **no** (outside the sim) | no |
| Quest progress: win, pve, posture>=0.8, combo, ult, parry | MC:213-218 | none | n/a (reads sim counters) | no |
| Save stats: matches, wins, losses, coins | MC:220-224 | none | n/a | no |
| No rewards in training | MC:198 | none | n/a | no |
| Equipped skin / weapon variant (cosmetic) | MC:97-98, CD, Core/WeaponDef.cs:10-57 | none | n/a | yes |

**Count: 7**

---

## Feature count summary

| Area | Features | SIM yes | SIM partial | SIM no | n/a / visual-only |
|---|---|---|---|---|---|
| Movement | 13 | 8 | 3 | 2 | 0 |
| Attacks/moves (normals 12, skills 16, passives 8) | 36 | 19 | 13 | 2 (+1 test hook, 1 verify) | 0 |
| Defense | 15 | 10 | 4 | 1 | 0 |
| Ultimates & gestures | 22 | 2 | 5 | 14 | 1 |
| Projectiles / minions / objects | 17 | 3 | 6 | 6 | 2 |
| Stage & platforms | 9 | 2 | 3 | 1 | 3 |
| Match flow | 13 | 2 | 3 | 6 | 2 |
| Inputs | 8 | 1 | 1 | 3 | 3 |
| AI | 8 | 0 | 0 | 8 | 0 |
| Rewards / progression | 7 | 0 | 1 | 1 | 5 |
| **Total** | **148** | **47** | **39** | **44** | **16** |

So **83 features are missing or partial** in ChibiSimulation (39 partial + 44 no), out of 132 that are not visual-only or n/a. The "yes" column is approximate: several "yes" rows have re-tuned numbers, not copied ones. Verify each against the original constants during the port.

---

## Gameplay state that must be saved/restored for rollback

### Per fighter (original field => SIM equivalent if any)
- Position X, Y (`transform.position`); velocity (`rb.linearVelocity`) => X, Y, Vx, Vy
- `Facing`, `Grounded`, `airJumps` => Facing, Grounded, AirJumps
- `Hp`, `MaxHp` (constant), `Ult` => Hp, Ult
- `hitstun`, `invuln` => Stun, Invuln
- `actionLock` plus the **coroutine program counter and locals** of the current move (DashThrust t/done, ThrustBarrage k, Grab phase/t/start/top/slam, TornadoSpin t/tick, BackstepShot t, ArrowBarrage k, LeapSlam t, SpinGuard k, UltSmash t, UltPolearmKick phase/t/from/to, NormalStrike delay/mult/heavy/finisher) => MoveSlot, MoveTick, MoveStartup/Active/Recovery, MoveMult, MoveKbScale, MoveHit, ShotIdx, TargetX (needs extension for multi-phase moves)
- `dashTimer`, `dashCd`, `dashDir` => **missing**
- `comboIndex`, `lastAttackTime` => ComboIdx, ComboWindow, IdleTicks (patience)
- `ComboCount`, `comboTimer`, `MaxCombo` => ComboHits, ComboTimer; **MaxCombo missing**
- `blocking`, `blockStart` => Shielding, ShieldTick
- `GuardHp`, `guardBrokenUntil` => GuardHp10, GuardBrokenTicks
- `parryUntil`, `spinGuardUntil`, `swordAuraUntil`, `FireRingUntil` => ParryTicks, GuardTicks, AuraTicks, RingTicks
- `slowUntil`, `weakenUntil`, `sluggishUntil`, `defDownUntil` => Slow/Weaken/Sluggish/ArmorBreakTicks
- `tailwindUntil`, `tailwindReady` => TailwindTicks, TailwindCd
- `setCooldownUntil` => SetCd
- `SkillCd[3]` => Cd[3]
- `superJumpCd`, `poseShotCd` => **missing**
- `UltWindow`, `UltWindowLeft`, `ultRetryCd`, `Prompt`, `promptTimer` (Prompt/PromptMessage can be derived for the HUD) => **missing**
- `spearOut` => **missing**
- `held`, `heldTarget`, Rigidbody bodyType Kinematic while held => **missing**
- `droppedPlatform`, `dropTimer` (and the IgnoreCollision pair) => **missing**
- Stats: `HitsLanded`, `CombosLanded`, `UltsUsed`, `Parries` => same
- Input edge state => PrevInput, BufBtn, BufAge
- Gesture state if gestures enter the sim as input: last gesture id (for the JustStarted edge), gesture held ticks (StableTime >= 0.15 s) => **missing**. `TelemetryValid` must be decided before the packet is sent (input side), not inside the sim.

### Per projectile
- Active, Owner, X, Y, Vx, Vy, Life, HitSpec (Mult, KbX, KbY, Stun, projectile, unblockable) => SIM has all but the unblockable flag
- `Homing` => yes
- `Radius`, `Pierce`, `Harmless`, `Destructible`, `GrowRate` + current Y scale, `ReturnAfter`, `ReturnMult`, `returning`, `hasHit` => **missing**
- OnExpire behaviour (Thousand Arrows split) and OnGone (spear return) => must become a projectile "kind" enum, not callbacks
- Visual-only: TrailColor, trailTimer, sprite, colour, rotation

### Per minion (up to 3 per cast, can overlap across casts: 12 s cd vs 6 s life, so max 3 alive unless the cd is shortened)
- Active, Owner, X, Y, Vx, Vy, Grounded, `life`, `attackCd`, facing (localScale sign, visual)

### Per thunder cloud
- Active, Owner, X, Y, `life`, `strike` timer; `boltTimer` (visual)

### Match level
- Frame / tick, `TimeLeft` (or derive from frame), `IntroTimer`, `Running`, `Finished`, `Winner`
- `hitstopUntil`, `cinematicUntil` (as tick counters, plus the time-scale effect on gameplay), finish slow-mo timer (visual only after the end)
- `Paused` (local only; offline)
- Gameplay RNG state => SIM `Rng`
- Training flags (constant config): training, revive, no timer
- Config constants (not rollback state, but must match on both peers): classes, passives, stage index, seed, time limit
- Out of rollback (local, after the match): RewardLog, CoinsEarned, posture sums, Fx popups, camera, FlashAlpha

---

## Suggested port order

1. Tick foundation: fixed 60 Hz step driving the sim, integer/fixed-point units, seeded gameplay RNG separated from visual RNG, sim-state checksum (extend `ChibiSimState`).
2. View layer: a presenter that reads sim state each render frame and drives FighterVisual/Fx/Camera, with Fx using its own RNG; no gameplay in MonoBehaviour.Update.
3. Input mapping: HumanInput/Bindings/gamepad => `ChibiInputPacket`, adding a Dash bit and the gesture bits 12..15 (raise `WireBits`).
4. Movement parity: run/air accel, jump/air jump, fast-fall constants, gravity, blocking-in-air, stun friction semantics.
5. Dash (timer, cd, invuln, direction).
6. Stage data in the sim: per-stage halfWidth/blast/respawn from a stage table + stage index in `ChibiMatchConfig`.
7. One-way platforms + drop-through as integer AABB platform landing.
8. Normals: light chain, heavy, finisher, attack speed, strike timing matched to dur*0.4, Sword Aura on light+heavy, reload, spearOut lock.
9. Damage pipeline parity: crit, defense, block fraction (X+Y chip kb), guard meter/break, perfect parry, parry skill (+1.5 ult on reflect), Ward, Hex, Armor Break, life steal, Patience semantics (first attack, skills, not ult).
10. Hitstop and ult cinematic as sim tick freezes/slow factor (deterministic), match timer behaviour under them.
11. Projectile kinds: pierce, harmless, destructible, grow, circle-vs-AABB radius, 2D homing, return/recall, split-on-expire; pool size check (Thousand Arrows 18 + others vs 24).
12. Multi-phase move state (replacing coroutines): Tornado steering, Leap Slam until landing, Grab hold/lift/slam with a held state, Thrust Barrage last-hit kb, Arrow Barrage spread from sim RNG.
13. Minions (integer body with gravity, stage collision, bonk logic) and Thunder Cloud (follow, repeated strikes).
14. Ultimates parity: Smash waves, Thousand Arrows lead+split, Polearm Kick teleport/hold/two hits, per-ult invuln, stun clear on fire.
15. Gesture-driven input in the sim: ult window (3 s, retry 1.5 s, fires while stunned), CrossArms block, Super Jump, Pose Fireball; telemetry validity resolved on the input side.
16. Match flow: intro countdown, KO ordering (single winner vs double-KO draw decision), ring-out held-release, training mode (no timer, revive, T ult fill as an input or debug command), pause offline only.
17. CPU AI rewritten as a deterministic packet generator from sim state with its own seeded RNG (difficulty 0 dummy).
18. Rewards/progression hooked to end-of-match sim counters (+MaxCombo) and local posture average.
19. Verification: replay/desync tests (existing `ChibiReplay`), parity tests per feature against the values in this inventory, then a run-and-observe pass per class.
