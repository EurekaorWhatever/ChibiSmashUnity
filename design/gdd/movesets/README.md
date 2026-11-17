# Chibi Clash movesets: shared rules (spec v1, 2026-11-09)

Source: the five sheets in `design/art/character-sheets/*-moveset.png`, decisions CH-1..CH-4
(`production/session-state/active.md`), and the live simulation (`Netcode/ChibiSimulation.cs`
`ChibiSimConst` / `ChibiMove` / `ChibiMoveTable`, `Netcode/ChibiCombatStats.cs`).

- `movesets.csv` is the **source of truth** for numbers. One row per NEW move (existing moves are not repeated).
  `damage_pct` = sheet value per hit (multi-hit: per hit; charge moves: uncharged, except Kaia Charged Cross Cut
  whose 18 % is read as the full-charge value). `mult` = per hit, before charge/Patience. `sfx_start` / `sfx_hit`
  = sound ids from `design/audio/sfx-spec.md` (start = swing/cast layer, hit = impact layer; empty = no hit).
- `kaia.md`, `bruna.md`, `ren.md`, `lyra.md`, `taro.md`: every sheet move, its mapping and the notes.
- Everything below is integer and deterministic (DEVELOPER-GUIDE section 3): no float, no trig at runtime
  (direction tables are precomputed here), no new RNG draws, every new mutable field lives in
  `ChibiFighterSim` / `ChibiSimState` and goes into `CopyFrom` and `ComputeChecksum`. RulesVersion bump.

Units (same as the sim): positions milli (1000 = 1 world unit); KbX/KbY and proj_speed in velocity sub-units
(VelSub = 900 sub = 1 milli/tick); `self_vx` / `self_vy` in **milli/tick** (exactly like `ChibiMove.SelfVx/SelfVy`,
multiplied by VelSub when applied); all durations in ticks (60 Hz = sheet frames).

---

## 1. Input -> move resolution

Notation used in the CSV `input` column: button `L` (Light/A), `H` (Heavy), `B` (Special, input bit 15),
`S1` `S2` `S3` (skills); direction `F` forward, `BK` back, `U` up, `D` down, none = neutral; `@dash` = dash window.
`ground_air`: `G` ground only, `A` air only, `GA` both.

### 1.1 Reading the stick
- `dx = input.MoveX` (-1/0/1), `dy = input.MoveY` (-1/0/1). **Vertical wins on diagonals** everywhere:
  `dy > 0` = U, `dy < 0` = D, else `dx != 0` = horizontal, else neutral.
- **Ground, horizontal = F.** FreeActions already sets `Facing = dx` before attacks, so on the ground every
  horizontal input is "forward" (a turnaround happens first).
- **Air: facing is locked (rule change, see open decision OD-1).** FreeActions sets `Facing = dx` only while
  `Grounded`. In the air `dx == Facing` = F, `dx == -Facing` = BK. Exception: a side special (`B+F`) started in the
  air sets `Facing = dx` at StartMove (turnaround side special).

### 1.2 Precedence (one move per tick; press edges, plus the rules v22/v23 attack buffer)
> **Rules v22 (2026-11-14, EL-1/TN-1/UL-1):** D-N1 is replaced by a 6-tick attack buffer (`AttackBufferTicks`): an attack
> press in the last 6 lock ticks of a move (or of a walk-cancel lock) comes out on the first free tick; a fresh press wins.
> Grounded walk / turn cancel in the last 6 recovery ticks (Light rows always, other moves only on hit). The Ult button
> fires the ultimate on a full meter (the gesture window is no longer opened). **Rules v23 (2026-11-17, B4-2/B4-3):** "on
> hit" means a landed or blocked melee hit (`MoveConnected`; a touch on an invulnerable, hovering, countered or parried
> target does not count), and attack presses in a dash's last 6 ticks are buffered too (a grounded Light stays the dash
> attack). Grounded trip stun comes from each row's `stun` (Bruna down tilt 22, Ren down tilt 26, Kaia low sweep 28).
Gates kept from today, in order: hitstun -> held/ult/pose -> dashing branch -> running move -> Shield held
(returns, nothing else) -> FreeActions. Inside FreeActions after jump/dash start, the first matching rule fires:

| # | Button edge | State | Direction | Move |
|---|---|---|---|---|
| 1 | L | Grounded and dash window | D (Kaia only) | Low Sweep (`L+D@dash`) |
| 2 | L | Grounded and dash window | any other | Dash attack (`L@dash`), `Facing = DashDir` |
| 3 | L | Grounded | U / D / F | up tilt / down tilt / forward tilt |
| 4 | L | Grounded | neutral | existing light combo (slot 0), reload + SpearOut gates unchanged |
| 5 | L | Airborne | neutral / F / BK / U / D | nair / fair / bair / uair / dair |
| 6 | H | Grounded | U / D / F | up / down / forward smash (chargeable) |
| 7 | H | Grounded neutral, or Airborne any | - | existing heavy (slot 1), gates unchanged |
| 8 | B | any | U / D / F / neutral | up / down / side / neutral special; `B+D` in air = the character's air down special if it has one (Bruna, Ren, Lyra, Taro), else the press is ignored |
| 9 | S1, S2, S3 (in that order) | any | U / D | technique slot `Sk+U` / `Sk+D` |
| 10 | S1, S2, S3 | any | neutral / horizontal | existing skill k (slots 2-4), unchanged |

Rules that complete the table:
- **Dash window** (sim terms): `Grounded && DashCd >= DashCooldown - DashAttackWindow` with
  `DashAttackWindow = 16` (so `DashCd >= 17`): from the dash start tick to 16 ticks after it (8 dash ticks + 8
  grace ticks). The **dashing branch** (DashTicks > 0) gets one new check: a Light edge while `Grounded` sets
  `DashTicks = 0` and starts rule 1/2. Heavy/B/skills during the dashing branch stay ignored.
- **Same-tick priority**: Light > Heavy > Special > Skill1 > Skill2 > Skill3 (extends D-M1).
- **Legality**: a resolved move whose `ground_air` does not match the state is not started. For B the press is
  dropped. For `Sk+U/D`: if the technique slot is **empty or wrong ground/air**, the press falls back to the
  existing neutral skill k (old behaviour preserved); if it is **on cooldown or `aironce` is spent**, nothing fires.
- **Gates**: Ranged/Magic reload (`SetCd`) gates only rules 4 and 7 (the existing projectile normals).
  Taro `SpearOut` gates every L and H move (rules 1-7), not B or skills (same as existing skills).
- **Charge / hold** read the level (held) bit of the button that started the move (H for smashes, B for B moves,
  Sk for techniques).
- Existing light in the air is replaced by aerials (CH-3). Ranged/Magic keep air shots via air Heavy and B (OD-2).

### 1.3 Common bookkeeping for every new move
Fixed frames (startup/active/recovery from the CSV, **no AttackSpeed scaling**, like skills). StartMove does:
`ComboIdx = 0, ComboWindow = 0` (like the heavy), Patience applies and `SinceAttackTicks = 0` (like slots 0-4),
`MoveMult = mult (x charge, x Patience)`, technique cooldown `TechCd[k*2 + (U?0:1)] = cd` (new array of 6,
ticked down with `Cd[]`). Ult gain, hitstop, crit, block, guard drain: existing ApplyHit, unchanged.

---

## 2. Damage conversion (sheet % -> Mult permille of Attack)

### 2.1 Formula
```
Mult      = round10( 1000 * sqrt( d / d_jab1 ) )                  single-hit move
Mult_hit  = round10( 1000 * sqrt( D_total / d_jab1 ) / n )        n-hit move whose hits are meant to all connect
```
| Symbol | Type | Range | Description |
|---|---|---|---|
| d | float (design time) | 2.0 - 18.0 | sheet damage % of the move |
| D_total | float | 7.0 - 12.5 | sheet total % of a linked multi-hit (per-hit x hits) |
| n | int | 2 - 6 | number of hits of a linked multi-hit |
| d_jab1 | float | Kaia 2.0, Bruna 2.5, Ren 2.0, Lyra 2.2, Taro 2.2 | that character's sheet jab-1 damage |
| round10 | - | - | round half up to a multiple of 10 |
| Mult | int | 390 - 3000 | permille of the fighter's Attack (CSV `mult`) |

The value is computed offline and stored as an integer in the CSV; the sim never takes a square root.

**Output range**: lowest per-hit 390 (Lyra Glyph Zone pulse, 6 pulses), lowest single hit 1170 (Lyra Prism
contact, estimated); highest uncharged 2830 (Kaia / Ren forward smash, ratio 8), highest after charge 2996
(Kaia Charged Cross Cut). It is bounded because sheet ratios top out at 9.0.

### 2.2 Why square root and not linear (deviation from "keep ratios", flagged as OD-3)
Anchors from the live game: light 1000, combo finisher 1500, heavy 1800, strongest skill 2200 (Grab & Smash),
ult single hit 3000 (Smash wave).

| Sheet ratio d/d_jab1 | 1 (jab) | 2 (jab 3) | 3.5 (tilt) | 8 (smash) | 9 (max) |
|---|---|---|---|---|---|
| linear 1000*r | 1000 | 2000 | 3500 | 8000 | 9000 |
| **sqrt (chosen)** | **1000** | **1410** (finisher 1500) | **1870** (heavy 1800) | **2830** | **3000** (ult hit 3000) |

- Linear breaks the "nothing one-shots" rule: Bruna forward smash 18/2.5 = 7200 x 35 Attack x 1.2 (Heavy bonus)
  x 1.5 crit = 454 HP > Lyra's 375 MaxHp, even before the charge.
- Linear also creates power creep: a 7 % tilt at 3500 / 34 ticks = 103 Mult per tick vs the existing light chain
  1000 / 14 ticks = 71. With sqrt **every new move stays under 71 Mult/tick** (highest: Kaia down tilt
  1730 / 29 = 60; Blade Reversal 2650 / 38 = 70 needs armor to land), so the new kit adds options, not DPS.
- sqrt keeps ordering and compresses ratios evenly (any two moves keep ratio^0.5), and lands on three existing
  anchors (1000, ~1500, 1800) and the ult ceiling (3000) at once.

### 2.3 One-shot check (worst cases, all modifiers stacked)
Damage HP = Attack x Mult/1000 x (1 + bonus) x crit(1.5) x Patience(1.5, Taro p1) x charge - floor(DEF/10).
- Bruna fsmash: 35 x 2.680 x 1.4 charge x 1.2 x 1.5 = 236.4 - 1 = **235 HP vs 375 (Lyra) = 63 %**.
- Taro fsmash (Patience): 25 x 2.700 x 1.4 x 1.5 x 1.5 = **212 HP** (57 %). Kaia CCC: 20 x 2.996 x 1.5 = 90 HP.
- Invariant for tuning: `max single hit < 2/3 of the lowest MaxHp (375)`, i.e. Bruna Mult x charge <= 3960.

### 2.4 Worked example
Kaia forward smash, sheet 16 %: `1000 * sqrt(16/2) = 2828.4 -> 2830`. Hit on Bruna (DEF 30), no crit, no charge:
20 x 2.830 = 56.6 HP raw, minus floor(30/10) = 3 -> 53.6 HP (8.9 % of 600). Full charge (60 ticks):
2830 x 1400 / 1000 = 3962 -> 79.2 - 3 = 76.2 HP.

---

## 3. Knockback conversion (sheet category -> KbX / KbY / Stun)

KbX is along `Dir` (section 4.4), KbY up (negative = down). Values in sub-units. Anchors in **bold** are existing
values: VL = existing light (37500, 30000, 17), M = existing heavy (120000, 90000, 30), VH = ult Kick hit 2
(180000, 135000, 48), M-up = Grab & Smash (36000, 135000), VH-up = ult Smash wave (90000, 180000, 48).

| Tier | -> horizontal (KbX, KbY) | ^ up | v spike | diagonal (0.8 x horiz X, 0.8 x up Y) | Stun |
|---|---|---|---|---|---|
| VL Very Low | **37500, 30000** | 15000, 52500 | 7500, -45000 | 30000, 42000 | 17 |
| L Low | 67500, 52500 | 22500, 90000 | 15000, -75000 | 54000, 72000 | 22 |
| M Medium | **120000, 90000** | **36000, 135000** | 22500, -120000 | 96000, 108000 | 30 |
| H High | 150000, 112500 | 45000, 157500 | 30000, -150000 | 120000, 126000 | 38 |
| VH Very High | **180000, 135000** | **90000, 180000** | 30000, -180000 | 144000, 144000 | 48 |

Special categories:
| Sheet word | Rule (flag) |
|---|---|
| Trip | grounded victim: (22500, 0), stun 34 (`trip`). Airborne victim: L horizontal (67500, 52500, 22). CSV holds the grounded values. |
| Spike / Down | v column (`spike`). Grounded victim: KbY = -KbY / 2 (pop-up), KbX unchanged. |
| Both sides / <-> | horizontal tier with positional Dir (`centered`). |
| Back / <- | horizontal tier with Dir = -Facing (`back`). |
| Launch / Upward / Vertical | ^ column. |
| Push | L horizontal with KbY 15000. |
| Pull | horizontal tier with Dir reversed (`pull`): victim moves toward the attacker. |
| Multi-hit (non-final hits) | LINK (13500, 9000), stun = rehit + 4 (= existing Thrust Barrage pulse). |
| Kill (Taro fsmash) | VH horizontal. |
| Sheet gives no tier | tilts L, aerials M, smashes H, specials M (stated per move). |

**Output range / ring-out check** (horizontal travel during stun = KbX/900 x Stun, milli): VL 708, L 1650,
M 4000, H 6333, VH 9600. Sky Dojo floor edge 8000, blast X 17000: VH rings out from the edge, H only from
already off-stage. The existing `HalfKnockback` rule (Ranged/Magic halved at hit time) **still applies** to every
Ren and Lyra move; CSV holds the unhalved tier value (OD-4).

Worked example: Kaia forward smash hits a fighter on her left (Facing -1): Dir -1, victim Vx = -150000 sub
(-166.7 milli/tick), Vy = +112500, Stun 38. Same move from Ren: (-75000, 56250), stun 38.

---

## 4. Hitbox conventions

Fighter origin: X = horizontal centre, Y = feet. Hurtbox: `[X-375, X+375) x [Y, Y+1350)` (HurtHalfW, HurtH).

4.1 **Box (default)**: same as `ChibiMove` today. Facing +1: `[X+box_x, X+box_x+box_w)`; Facing -1 mirrored
`[X-box_x-box_w, X-box_x)`. Vertical `[Y+box_y, Y+box_y+box_h)`. `box_x` may be negative (starts behind the centre).
`back`: computed with -Facing. `centered`: `[X-box_w/2, X+box_w/2)` (existing FCentered).

4.2 **Circle** (`radius > 0`, used for spins): centre `(X + Facing*box_x, Y + box_y)`, `box_w = box_h = 0`.
Hit test against the hurtbox AABB, integer, strict like Overlaps:
`qx = clamp(cx, hx0, hx1) - cx; qy = clamp(cy, hy0, hy1) - cy; hit = (long)qx*qx + (long)qy*qy < (long)r*r`.

4.3 **Projectile rows** (`proj_speed > 0`): `box_x`/`box_y` = spawn offset from the feet (like ProjOffX/Y), box_w/h 0.
Velocity `(Facing*proj_speed, 0)` unless `pv:VX:VY` (explicit sub/tick, VX toward Facing) or `fan`.
Spawn on the first active tick (and every `rehit` for multi-shot, existing Rehit path; the existing barrage
`yOff` jitter is NOT used by new rows). New projectiles are `FDestructible` unless `pierce` or `tether`.

4.4 **Dir** (sign of KbX): `centered` or circle with `centered` -> `sign(victim.X - X)`, tie -> Facing;
`back` -> -Facing; else Facing; `pull` negates the result. Zones: `sign(victim.X - zone centre X)`, tie -> the
owner's Facing at spawn.

4.5 **Zone rows** (`zone:*`): the box columns give the zone rectangle, computed once at spawn from the owner's X/Y/
Facing with rule 4.1, then fixed in the world.

Reach per class used for the boxes: Kaia 1650 (= existing light box), Bruna 1850, Ren 1300, Lyra 1400, Taro 2300.

---

## 5. New generic mechanics (flags)

Flag grammar in the CSV: pipe-separated, `name[:arg[:arg...]]`; list args separated by `;`, pairs by `/`.
"Move tick" t = `MoveTick` as processed by AdvanceMove (0 = first tick). Active ticks are
`[startup, startup + active)`.

| Flag | Exact semantics |
|---|---|
| `cd:N` | technique cooldown N ticks in `TechCd[]`, set at StartMove. |
| `charge:MAX:BONUS` | While `MoveTick == 0` and the trigger button is held (level) and `ChargeHeld < MAX`: `ChargeHeld++`, MoveTick does not advance, grounded friction only, no SelfV. On release or at MAX: `MoveMult = MoveMult * (1000 + BONUS * ChargeHeld / MAX) / 1000` (int division), then tick 0 runs. Smashes: `charge:60:400` (x1.4 at full). New fields `ChargeHeld`, `Charging`. |
| `chargelvl:L:MIN` | Rows sharing an input; at charge release the row with the highest MIN <= ChargeHeld replaces the started row (same MoveTick). |
| `hold:N` | On the last active tick, if the trigger is held and `HoldExt < N`: `HoldExt++` and the tick repeats (active extended, lock end +1). New field `HoldExt`. |
| `multi:N:R` | Re-arm the hit every R active ticks (existing Rehit = R), N hits. Hits 1..N-1 use LINK kb (13500, 9000, stun R+4); hit N uses the row kb/stun. Requires `active >= (N-1)*R + 1`. |
| `armor:A:B:KBP` | For move ticks A..B inclusive (`B = end` means last active tick incl. `hold`): a non-Unblockable hit deals its damage but sets no Stun, no Interrupt; if `KBP > 0` the victim's velocity is set to kb x KBP / 1000 (only if above KbMinSq), else unchanged. Hitstop still plays. |
| `counter` / `counter:proj` | During active ticks the fighter is in counter stance (`CounterTicks = 1` each tick, like ParryTicks). Checked first in ApplyHit (before the Parry skill): a non-Unblockable **melee** hit (fighter box or program probe; with `:proj` also projectiles, which are then despawned) is cancelled entirely (no damage, no kb, no ult gain, attacker `MoveHit = true`). Both fighters get `HitstopParry` (6). Defender: `Facing = toward attacker`, `Invuln = max(Invuln, 20)`, `MoveTick = startup + active`, lock ends 20 ticks after the trigger (`CounterLock = 20`), `CounterStrike = 1`. Next tick the defender runs a one-tick box probe with the row box / mult / kb / stun, **Unblockable**, then clears CounterStrike. Without a trigger the row has no hitbox and plays its full recovery. |
| `reflect:P:W` | During active ticks: an enemy projectile overlapping the row box is reflected instead of hitting: `Owner = reflector`, `Vx = -Vx`, `Vy = -Vy`, `Mult = Mult * P / 1000`, `Life = max(Life, 30)`. A non-Unblockable melee hit from the front with incoming Mult <= W is cancelled and the attacker gets the existing parry-skill reflect (ReflectStun 30, ReflectPush). The row's own mult/kb is a normal contact hit. |
| `zone:contact:ARM:LIFE` | Spawns a stationary zone (new `ChibiZoneSim` pool of 4) on the first active tick. Armed after ARM ticks; the first tick the enemy hurtbox overlaps it, one hit (row mult/kb/stun, MoveMult snapshot) and despawn. Despawns silently at LIFE. One zone per (owner, move id): a new one replaces the old. Pool full: not spawned. Zones update after projectiles, in pool index order; hits go through ApplyHit with attacker = owner (blockable, not counterable). |
| `zone:timed:DELAY:BURST` | As above, but no contact trigger: at t = DELAY the zone becomes live for BURST ticks and hits once on the first overlap, then despawns. |
| `zone:periodic:PERIOD:LIFE` | Pulses at zone ticks 0, PERIOD, 2*PERIOD... while < LIFE; each pulse hits once if overlapping. |
| `slow:N` | Zones only: every tick the enemy overlaps, `SlowTicks = max(SlowTicks, N)` (existing slow). |
| `dive:MAX` | Startup: Vx = Vy = 0 forced (hover). From the first active tick: `(Vx, Vy) = (Facing*self_vx, self_vy) * VelSub` forced every tick (self_vy negative), hitbox live, for up to MAX ticks (= CSV active). Ends early on landing (Grounded after Physics) -> `MoveTick = startup + active` (recovery = landing lag) and `landhit` fires; or on a hit -> Vy = +60 milli/tick, go to recovery in the air (prevents dive suicide). `self_vy` is NOT the usual tick-0 write for dive rows. |
| `landhit:MULT:KBX:KBY:STUN:W:H` | On a dive landing: one centred box `[X-W/2, X+W/2) x [Y, Y+H)` hit with these values (Dir positional). |
| `teleport:D` | Direction captured at StartMove from (dx, dy) (new field `MoveDir`; neutral -> up). On the first active tick: X += dirX*D, Y += dirY*D (diagonal: each axis D*707/1000). Clamp `|X| <= BlastX - 2000`, `Y <= BlastTop - 2000`; if the destination is below the main floor top while inside the floor span, Y = floor top and Grounded. Platforms ignored. `self_vy` of a teleport row is applied on that tick instead of tick 0. |
| `invuln:A:B` | At move tick A: `Invuln = max(Invuln, B - A + 1)`. |
| `aironce` | If the move starts airborne, set bit (move index) in new field `AirUsedMask`; the move cannot start airborne again until the mask clears. Cleared on landing, when a hit sets Stun, and on respawn. |
| `restorejump` | First active tick: `AirJumps = max(AirJumps, 1)`. |
| `recoil:VX:VY` | At t == startup + active (once): `Vx = VX*VelSub*Facing`, `Vy = VY*VelSub` (milli/tick). |
| `roll` | Roll direction r = dx if != 0 else -Facing. Ticks 0..startup+active-1: `Vx = r*self_vx*VelSub`, Facing unchanged. On the last active tick: `Facing = -r`. No hitbox. |
| `tether:self:SPD:MAX` / `tether:foe:SPD:MAX` | Projectile (new kind Grapple) that records an anchor when it hits the enemy, touches a stage solid top (main floor or platform, while moving down) or ends its life (`self` only: air anchor). `self`: from the anchor tick, for up to MAX ticks or until Chebyshev distance <= 300: owner velocity = (ax-X, ay-Y) * SPD / max(|ax-X|, |ay-Y|) (written last, like program velocity), then Vx/2, Vy/2. `foe`: an enemy hit uses the row kb with `pull`; a stage-touch anchor pulls the owner like `self`; expiry in the air does nothing. New fields `PullTicks`, `AnchorX`, `AnchorY`. |
| `pierce` | Projectile not removed on a fighter hit, hits each fighter once, not destructible. |
| `bounce:N:M2` | Projectile: on touching a stage solid top while Vy < 0: `Vy = -Vy`; on hitting the enemy: `Vx = -Vx`, `Vy = abs(Vy)`, re-arms after 10 ticks; each counts a bounce; after a bounce the hit Mult is M2; despawns after the N-th bounce. New projectile field `Bounces`. |
| `rehit:R` | Existing `ChibiMove.Rehit` for projectile rows: one more shot every R active ticks (ShotIdx++). |
| `pv:VX:VY` | Explicit projectile velocity (sub/tick, VX toward Facing). |
| `fan:v1;v2;...` | Multi-shot: shot i (ShotIdx) uses velocity vi = `VX/VY` (VX toward Facing). |
| `pk:Kind` | Projectile kind for the view (new kinds: WindBlade, GroundWave, Grapple, LanceArrow). |
| `spike`, `trip`, `back`, `pull`, `centered`, `level`, `unblockable` | Section 3 / 4; `centered` = existing FCentered, `level` = existing FLevel (Vy = 0 during the SelfVx window: horizontal air lunges). |
| `est` | Value not legible / not on the sheet: estimated, tune in playtest. |

Not a separate mechanic: **launcher** (= the ^ knockback column), **slide** (= self_vx), **recovery** (= self_vy +
`aironce`). Self velocity default semantics are unchanged: `self_vx` applies every tick of startup + active toward
Facing, `self_vy` once at tick 0.

New constants: `DashAttackWindow 16, LinkKbX 13500, LinkKbY 9000, LinkStunPad 4, TripKbX 22500, TripStun 34,
CounterLock 20, CounterInvuln 20, PullArrive 300, ZonePool 4, TeleportMargin 2000, DivePopVy 60, ProjRearm 10`.
New fighter fields: `TechCd[6], ChargeHeld, Charging, HoldExt, AirUsedMask, MoveDir, CounterTicks, CounterStrike,
ReflectTicks, PullTicks, AnchorX, AnchorY`; new state: zone pool; projectile: `Bounces`, `Rearm`, tether owner flag.

---

## 6. Open decisions for the lead (spec is written for the recommended option)

| # | Question | Options | Rec. |
|---|---|---|---|
| OD-1 | Air facing | A: lock facing in the air (Smash-like; needed for back air). B: keep air turning, bair only when back+A on the same tick. | A |
| OD-2 | Existing light in the air | A: aerials replace it (CH-3 literal; Ren/Lyra still shoot in the air with Heavy and B). B: keep the air arrow/bolt on neutral A for Ren/Lyra and drop their nair. | A |
| OD-3 | Damage curve | A: sqrt (section 2). B: strict linear (one-shots, 1.4x DPS creep). C: sqrt with exponent as a tuning knob (0.5 - 0.6). | A |
| OD-4 | Ren/Lyra knockback | A: keep HalfKnockback on all their moves (class identity, their spikes are weak). B: exempt new moves. | A |

Tuning knobs: exponent 0.5 (safe 0.45-0.6), tier table, `charge` bonus 400 (safe 250-500), DashAttackWindow 16
(safe 10-20), CounterLock 20, technique cooldowns (`cd:`), zone LIFE values.

## 7. Acceptance checks (for tests)
- Each CSV row resolves from exactly one input/state (no two rows of a character share input + ground_air, except
  `chargelvl` rows).
- No CSV `mult` > 3000; every row `mult / (startup + active + recovery) < 72` except counters/armored.
- Re-simulating a recorded match with the new rows twice gives identical checksums (golden values re-recorded).
