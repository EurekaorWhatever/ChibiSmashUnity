# Damage Percent, Knockback and Stocks (spec v1, 2026-11-11)

Owner: game-designer. Decision: GL-1 (`production/session-state/active.md`). Implements in `Netcode/ChibiSimulation*.cs`,
`ChibiCombatStats.cs`, `ChibiStageTable.cs` (gameplay-programmer), `ChibiCpuInputSource.cs` (ai-programmer), HUD/results
(ui-programmer). RulesVersion bump; goldens re-recorded on purpose; network + security review before commit.

Sources (structure only; every constant below is recalibrated to our units):
[SSBWiki Knockback](https://www.ssbwiki.com/KB), [SSBWiki Launch](https://www.ssbwiki.com/Launch),
[SSBWiki Hitstun](https://www.ssbwiki.com/Hitstun), [McLeodGaming wiki: Knockback (Melee-form formula)](https://mcleodgaming.fandom.com/wiki/Knockback),
[Smashboards KB / launch speed / hitstun calculator (KB x 0.4 hitstun, KB x 0.03 launch speed)](https://smashboards.com/threads/tool-knockback-launch-speed-hitstun-hitlag-shieldstun-calculation-v1-11.324878/),
[Ultimate weight list (Pichu 62, Jigglypuff 68, Mario 98, Bowser 135)](https://www.gfinityesports.com/article/the-super-smash-bros-ultimate-roster-s-weight-list-heavy-light),
[Brawlhalla wiki: Terminology (fixed vs variable force)](https://brawlhalla.wiki.gg/wiki/Terminology), [Brawlhalla wiki: Axe (fixed/variable force, base stun per move)](https://brawlhalla.wiki.gg/wiki/Axe),
[Brawlhalla archive: Combat mechanics (KOs ~150-200, red meter)](https://brawlhalla-archive.fandom.com/wiki/Combat_mechanics),
[Brawlhalla wiki: Stock (3 stocks, 8:00, most stocks at time-out)](https://brawlhalla.wiki.gg/wiki/Stock),
[SSBU Unified European Ruleset (3 stocks, 7 min, time-out: stocks then percent)](https://www.ssbwiki.com/Unified_European_Ruleset).

---

## 1. Overview

Fighters no longer have an HP bar. Every hit still runs the existing exact damage pipeline (Attack x Mult, damage
bonus, weaken, crit, defense, block, guard, parry, ward) and the resulting micro-HP is converted by ONE integer rule
into **damage percent** that only goes up. Knockback is computed per hit from the defender's percent, the hit's
percent, the defender's **weight** and the move's **base knockback / knockback growth** (Smash structure). Each move's
base/growth is derived automatically from its existing KbX/KbY magnitude, so all 108+ moves get values with no hand
editing. A fighter is KO'd only by crossing an **active KO edge** of the stage; each KO costs one of 3 **stocks** and
resets percent to 0. The last fighter with stocks wins; at time-out fewer stocks lost wins, then lower percent, else draw.

## 2. Player Fantasy

- **Rising danger (Challenge, Sensation).** The big percent number creeping from white to red tells both players
  "one clean hit ends this now". Every exchange at high percent is tense; every exchange at low percent is a combo
  playground (short launches, long true strings).
- **The finisher (Sensation, Expression).** Charging a smash near the ledge and sending a red-percent opponent off
  screen is the payoff of the whole stock. Ultimates are cinematic and decisive.
- **Comeback is always possible (Competence, Autonomy).** At 150 % you are not "nearly dead", you are "one read
  away": survival comes from positioning and shielding, not a shrinking bar. Weight makes Bruna feel like a wall and
  Lyra feel slippery-but-fragile.
- Brawlhalla's readable colour meter + Smash's percent number: the state is legible at a glance (Competence: the
  player always knows WHY they flew: high %, strong move, light character, near the edge).

## 3. Detailed Rules

### 3.1 State changes (all in `ChibiFighterSim`, all in `CopyFrom` + `ComputeChecksum`)
| Field | Type | Meaning |
|---|---|---|
| `PctM` | int | damage percent in milli-percent (1000 = 1.0 %), 0..`PctCapM` |
| `Stocks` | int | stocks left (starts `StocksStart`) |
| `OutTicks` | int | > 0 = KO'd and off-screen (respawn delay) |
| `HoverTicks` | int | > 0 = on the respawn point, gravity off, untouchable |
| `LaunchDecX`, `LaunchDecY` | int | per-tick launch decay in sub-units (DecY carries the sign of the launch's Y) |
| `LaunchGravP` | int | gravity permille while in hitstun (1000 = normal) |
| `KosTaken` | int | stats (replaces `Revives` meaning in training too) |

`HpU` is removed from the state (the `Hp` accessor goes; readers listed in section 6). `ChibiCombatStats.MaxHp` stays
only as the source of `Weight` (new field, section 4.3). `RingOutPenaltyPermille` is deleted.

### 3.2 Hit resolution (`ApplyHit`, order of steps and RNG draws unchanged)
0. Gate: `MatchOver || A.OutTicks > 0 || D.OutTicks > 0 || D.HoverTicks > 0 || D.Invuln > 0` -> nothing (was `HpU <= 0`).
   The `HalfKnockback` halving line is deleted (replaced by `ClassKbP`, 4.4).
1. Counter stance / parry-skill reflect: unchanged, except the reflect damage `refU` is added to the attacker as
   percent (`A.PctM += Pct(refU)`), and the reflect push stays the fixed `ReflectPushX/Y` (set knockback).
2. Perfect parry, 3. Ward: unchanged (no percent).
4. Damage: `dmgU` exactly as today. `D.PctM = min(PctCapM, D.PctM + Pct(dmgU))`. `dPctM = Pct(dmgU)` is the hit's
   percent `d` for the knockback formula (after crit, charge, Patience, defense; capped at `DmgTermCapM` in the formula only).
5. Blocked (shield held, not broken): chip percent is added (step 4); push = legacy `KbX/4, KbY/4` exactly as today
   (shield push never scales with percent, like Smash). No hitstun.
6. Unblocked: compute `KB` (4.4). If the hit is **set knockback** (4.5): velocity = legacy `(Dir*KbX, KbY)`, stun =
   legacy `h.Stun`, no decay fields. Otherwise: launch velocity, `LaunchDecX/Y`, `LaunchGravP` and
   `Stun = max(D.Stun, Hitstun(KB, h.Stun))` (4.6). Velocity is written through `SetKnockback` (held victims and the
   `KbMinSq` gate unchanged). Guard break keeps `stun >= GuardBreakStunTicks` as the legacy floor.
7. Armor (`ArmorAbsorbs`, movesets README 5): damage and percent apply. If `KB < ArmorBreakKbM` the armor holds:
   no stun, no Interrupt; if `KBP > 0` velocity = launch velocity x KBP / 1000 (still `KbMinSq`-gated). If
   `KB >= ArmorBreakKbM` the armor breaks and step 6 runs normally.
8. OnHitLanded: unchanged, except lifesteal lowers the attacker's percent: `A.PctM = max(0, A.PctM - Pct(heal))`, and
   the comeback ult doubling condition becomes `A.PctM >= ComebackPctM` (was "under 30 % HP").

### 3.3 Hitstun physics (stun branch of `UpdateFighter`, before Physics)
While `Stun > 0`: `Vx = Approach(Vx, 0, LaunchDecX)`; if `Vy != 0` and `sign(Vy) == sign(LaunchDecY)`:
`Vy = Approach(Vy, 0, |LaunchDecY|)` (the decay only brakes the launch, never the fall caused by gravity).
Physics applies `Gravity * LaunchGravP / 1000` (integer) instead of `Gravity` while `Stun > 0`. When `Stun` reaches
0: `LaunchDecX = LaunchDecY = 0`, `LaunchGravP = 1000`. After hitstun the existing air control (AirAccel 10000 sub)
is the only brake: that post-hitstun drift is part of the calibration (4.8). No DI, no hitstun cancel (v1).

### 3.4 KO edges and stocks (`ResolveBoundsAndMatchEnd`)
- Each stage defines four edges (left X, right X, top Y, bottom Y on feet coordinates, strict `>`/`<` as today) and
  for each edge `KoActive`. Positions and which edges are active belong to the stage specs (`design/gdd/stages/`, ST-2).
  A fighter crossing an **active** edge is KO'd; an **inactive** edge is a solid boundary (clamp, velocity on that
  axis = 0), it never KOs. There is no other KO: percent never KOs by itself.
- On KO (only while `Running`, as today): `Interrupt` self; release / free a held pair (existing ring-out code);
  `Stocks--` (not in training); `KosTaken++`; `OutTicks = RespawnDelay`; Vx = Vy = 0, Stun = 0, decay fields cleared;
  emit KO event (view: blast effect at the crossing point).
- If after the KO loop any fighter has `Stocks == 0`: `MatchOver`; winner = the other; both 0 on the same tick =
  draw (D-F1 unchanged).
- Respawn when `OutTicks` reaches 0: position = stage `RespawnX/RespawnY`, `PctM = 0`, Vx = Vy = 0, `Grounded = false`,
  `HoverTicks = RespawnHoverMax`, statuses cleared (Slow, Weaken, Sluggish, ArmorBreak, Guard broken; guard refilled),
  `AirUsedMask = 0`, `AirJumps` refilled, `ParryTicks = GuardTicks = PullTicks = 0`, cooldowns and Ult kept.
- Hover: gravity off, cannot be hit (gate 0). Ends on the first tick with any direction or button pressed, or at 0;
  at the end `Invuln = RespawnInvuln` and normal play resumes (the ending input is processed that tick).
- Time-out (`MatchClock >= MatchTicks`, after the KO step, so a KO on the last tick counts): more `Stocks` wins;
  equal -> lower `PctM` wins; equal -> draw. No sudden death in v1 (OD-PK4).
- Training: KO -> same respawn path, `Stocks` unchanged, `PctM = 0`. No timer (unchanged).

### 3.5 Everything that read HP (must change)
| Reader | New rule |
|---|---|
| `ApplyHit` gates, `Movesets` gates (`HpU <= 0` at Movesets.cs 358, 582, 716, 727), CPU `inRange` (1627), ult gate (1245), input gates (1221, 1350) | `OutTicks > 0 \|\| HoverTicks > 0` |
| Ult gain x2 "under 30 % HP" | `PctM >= ComebackPctM` (100 %) |
| Lifesteal (Melee P1) | lowers own percent (3.2 step 8) |
| Ring-out -15 % HP | deleted: crossing an active edge is a KO |
| `TimeoutWinner` (HP fraction) | stocks, then lower PctM, then draw |
| CPU `Killable` (`KillHpPermille`) | `opp.PctM >= (opp.Weight + CpuKillPctOffset) * 1000`, OR the existing near-edge rule; CPU prefers smashes/ult when true |
| HUD bar (`Hud.cs` 164-176), test HUD | big percent number per player, `floor(PctM/1000)` + small `.x` digit, colour ramp (Tuning), 3 stock icons, shake on hit |
| Results "HP left %" (`Hud.cs` 410) | "Stocks left N / final %" |
| `ChibiSimEvents` KO (HpU <= 0) and damage numbers (HpU delta) | KO = `Stocks` decreased or `OutTicks` became > 0; damage number = PctM delta shown as "+x.x%" |
| Audio end (`ChibiAudio*` HpU <= 0) | "ended by KO" = a fighter has `Stocks == 0` |
| Legacy `Fighter.cs` / `MatchController.cs` (non-netcode path) | out of scope; not used by the netcode match |
| Rewards / quests | none read HP (grep 2026-11-11); any future "flawless" uses "no stock lost" |
| `teleport` clamp `BlastX - 2000` | clamp to the nearest edge (active or solid) minus `TeleportMargin` |

## 4. Formulas (all integer, `long` intermediates, C# truncation toward zero unless stated)

### 4.1 Damage -> percent
`Pct(u) = (u * PctPerHpM + 500_000) / 1_000_000`  (u = micro-HP >= 0, result milli-percent, half-up).
`PctPerHpM = 200` (0.2 % per HP: 5 HP = 1 %; Kaia's old 500 HP bar = 100 %).

| Hit (no crit, uncharged) | HP | Percent |
|---|---|---|
| Kaia jab (Mult 1000) on Kaia | 20 - 2 = 18 | 3.6 |
| Ren jab on Kaia / Lyra jab on Kaia | 13 / 18 | 2.6 / 3.6 |
| Kaia ftilt (1870) on Kaia | 35.4 | 7.08 |
| Kaia fsmash (2830) on Kaia / Lyra / Bruna | 54.6 / 55.6 / 53.6 | 10.92 / 11.12 / 10.72 |
| Kaia fsmash full charge (x1.4) on Kaia | 77.24 | 15.45 |
| Taro fsmash (2700) on Kaia | 65.5 | 13.1 |
| Bruna jab / fsmash (2680) on Lyra | 41 / 111.56 | 8.2 / 22.31 |

Why one uniform rule (recommended, OD-PK1 = A): it keeps every number the balance work already proved (DPS cap
< 71 Mult/tick, one-shot invariant, Heavy +20 %, crit, defense) and is one code path for melee, projectiles, zones,
summons, hazards, reflect and lifesteal. Cost: the sqrt-compressed Mult (README 2.2) makes smashes only ~3x a jab
(the sheets say 8x); the knockback tiers (4.4), the damage term and charging restore the gap in KO power.
Option B (sheet % as percent for CSV moves, `d_jab1 x (Mult/1000)^2` for legacy moves, then x crit/charge/bonus)
matches the sheets exactly but forks the pipeline in two and drops Attack/Defense from new moves; not recommended.

### 4.2 Knockback (Smash structure, Ultimate/Melee form)
Real form: `KB = ((p/10 + p*d/20) * 200/(w+100) * 1.4 + 18) * s/100 + b`, p = defender percent AFTER the hit,
d = the hit's percent, w = weight, s = growth (100 = x1), b = base. Integer form (milli-KB, `KBM`):
```
dC   = min(dPctM, DmgTermCapM)
x    = pM / 10 + pM * dC / 20_000                       // milli
y    = x * 280 / (w + 100) + 18_000                     // 280 = 200 x 1.4
KBM  = y * sP / 1000 + bM                               // sP growth permille, bM base milli-KB
KBM  = KBM * ClassKbP(attacker) / 1000
KBM  = clamp(KBM, 0, KbCapM)
```
| Var | Range | Notes |
|---|---|---|
| pM | 0..999 900 | defender `PctM` after step 4 |
| dPctM | 0..~50 000 | this hit's percent (a crit charged Bruna smash ~47 %) |
| w | 75..120 | 4.3 |
| sP | 250..1250 | 4.4 |
| bM | 10 000..140 000 | 4.4 (incl. ult bonus) |
| KBM | 0..KbCapM 400 000 | overflow: max x*280 < 3e8, y*sP < 2e9 -> use long |

### 4.3 Weight
`Weight = MaxHp / 5`: Lyra (Magic) **75**, Ren (Ranged) **80**, Kaia (Melee) **100**, Taro (Polearm) **100**,
Bruna (Heavy) **120**. Mirrors Ultimate's spread (Pichu 62 .. Mario 98 .. Bowser 135) compressed to a 5-cast roster;
weight factor `280/(w+100)`: 1.600 / 1.556 / 1.400 / 1.400 / 1.273. Weight affects knockback only (not gravity).

### 4.4 Base and growth from the existing KbX/KbY (no hand editing)
`mag = ISqrt(KbX^2 + KbY^2)` of the PendingHit as it reaches ApplyHit (after spike / trip / link rewrites, before any
class scaling; `ChibiFixed.ISqrt`). Clamp mag to 250 000.
```
sP = KbgBase + mag / KbgDiv          = 250 + mag / 250
bM = BkbBase + mag * BkbNum / BkbDen = 10_000 + mag * 2 / 15      (+ UltBkbBonusM if the hit is an ult hit)
```
| Tier (README 3, horizontal) | mag | sP | bM |
|---|---|---|---|
| VL light (37500, 30000) | 48 023 | 442 | 16 403 |
| L (67500, 52500) | 85 513 | 592 | 21 401 |
| M heavy (120000, 90000) | 150 000 | 850 | 30 000 |
| H smash (150000, 112500) | 187 500 | 1000 | 35 000 |
| VH (180000, 135000) | 225 000 | 1150 | 40 000 |

Optional per-move override: CSV columns `bkb`, `kbg` (0 = derived), and `ChibiMove` fields of the same name.
`ClassKbP`: Ranged and Magic **850**, others 1000 (replaces HalfKnockback x500, which would push their KOs past 250 %;
OD-PK2). **Ult hit** = any hit created by the ultimate (slot 5, its programs, projectiles and summons):
`UltBkbBonusM = 80_000`.

### 4.5 Set knockback (no growth)
A hit is set-knockback when `mag < SetKbMag` (30 000) or it is a grounded `trip`: multi-hit link hits
(13500, 9000), Thrust pulses, Volley arrows, trips. Velocity and stun are the legacy values; percent still adds.
This keeps multi-hits from scattering at high percent and keeps every combo string intact.

### 4.6 Hitstun, launch, decay, gravity
```
Hitstun  = min(HitstunCap, max(h.Stun, KBM * 4 / 10_000))       // KB x 0.4, legacy Stun is the floor
LS       = KBM * LsSubPerKb / 1000                              // launch speed, sub/tick (1530 = 1.7 milli/tick per KB)
Vx       = h.Dir * (LS * |KbX| / mag);   Vy = LS * KbY / mag      // mag 0: velocity unchanged, stun = legacy
LaunchDecX = LaunchDecay * |KbX| / mag;  LaunchDecY = LaunchDecay * KbY / mag   // LaunchDecay 2700 sub/tick (3 milli/tick^2)
LaunchGravP = clamp((GravZeroKbM - KBM) * 1000 / (GravZeroKbM - GravFullKbM), 0, 1000)   // 1000 at KB<=60, 0 at KB>=120
```
The legacy Stun floor keeps today's combo timing at low percent; above ~KB 95 the 0.4 rule takes over. Gravity fades
out for real launches so strong hits fly in near-straight lines (Smash look) and vertical KOs are reachable; weak
hits keep today's juggle arcs.

### 4.7 Worked example: Kaia fsmash (uncharged, no crit) on Kaia at 0 %
d = 10 920, p = 10 920. x = 1 092 + 10 920 x 10 920 / 20 000 = 1 092 + 5 962 = 7 054. y = 7 054 x 280 / 200 + 18 000
= 27 875. KBM = 27 875 x 1000 / 1000 + 35 000 = 62 875 (KB 62.9). LS = 96 198 sub. Vx = Dir x 76 958, Vy = 57 718.
Hitstun = max(38, 25) = 38. LaunchGravP = (120 000 - 62 875) x 1000 / 60 000 = 952.

### 4.8 KO calibration (for stage designers and tests)
With T = 0.4 KB, v0 = 1.7 KB milli/tick, decay 3 milli/tick^2 along the launch, post-hitstun brake 11.1 milli/tick^2,
unit direction (ux, uy): horizontal travel `X ~= KB^2 (0.44 ux + 0.0113 ux^2)`; vertical rise (gravity 0)
`Y ~= KB^2 (0.44 uy - ... ) ~= 0.434 KB^2` for uy ~0.96. For the standard 4:3 launch (ux 0.8): `X ~= 0.359 KB^2`.
| Distance to the active edge | KB to KO (4:3 side) | KB to KO (up, uy 0.96) |
|---|---|---|
| 9 000 (Sky Dojo ledge -> side line) | 158 | 144 |
| 10 000 (recommended top line above the main floor) | 167 | 152 |
| 17 000 (Sky Dojo centre -> side line) | 218 | 198 |
Stage guidance: side KO line 8 000-10 000 beyond the ledge; top line about 10 000 above the main floor and at least
5 000 above the highest platform (two jumps reach ~4 250); bottom line as today (~9 700 below the floor).

Resulting KO percents (percent BEFORE the hit, uncharged, no crit; side line 9 000 from the ledge):
| Move -> victim | ledge | centre |
|---|---|---|
| Kaia fsmash -> Lyra / Kaia / Bruna | 89 / 108 / 119 | 146 / 174 / 190 |
| Kaia fsmash full charge -> Kaia | 71 | 125 |
| Lyra fsmash (ClassKbP 850) -> Kaia | 143 | ~220 |
| Bruna fsmash -> Lyra / Kaia | 55 / 64 | 99 / 112 |
| Kaia up smash -> Kaia (top line 10 000) | 119 (vertical) | - |
| Taro Kick ult hit 2 -> Kaia | 3 | 56 |
| Kaia jab -> anyone | never below 300 % | - |

## 5. Edge Cases
- **Percent cap**: `PctM` saturates at `PctCapM` 999 900 (HUD shows 999 %); knockback still computed (KB capped).
- **mag = 0** (KbX = KbY = 0, e.g. pure-status hits): percent adds, velocity unchanged, stun = legacy, no decay.
- **KB below the `KbMinSq` gate**: velocity unchanged (as today), hitstun still applied.
- **Huge KB**: `KbCapM` 400 000 and `HitstunCap` 90 bound speed (612 000 sub) and stun; travel is far past any line.
- **Hit during hitstun**: velocity, decay and gravity fields are overwritten by the new launch; `Stun = max(old, new)`.
- **Trade (both hit on one tick)**: h0 then h1 as today; percent and launches both apply; KO checks run at tick end.
- **Same-tick double KO**: both lose a stock and respawn; if both reach 0 stocks -> draw (D-F1).
- **KO while holding / held**: existing ring-out release code runs before the stock loss; the partner is freed.
- **KO during intro / after MatchOver**: no KO (edges only checked while `Running`, as today).
- **KO on the last timer tick**: the KO step runs before the timer, so stocks decide.
- **Projectile / zone / summon still alive when its owner is KO'd**: keeps running (as today); the victim is
  untouchable while Out or Hovering, and hits on an Out attacker are ignored (gate 0).
- **Inactive edge**: never KOs. A fighter launched into a solid wall/floor stops on that axis; hitstun continues.
- **Grounded spike**: the existing pop-up rewrite (KbY = -KbY/2) runs first, so the formula launches upward weakly.
- **Spike off-stage**: downward launch with gravity faded -> fast drop through the bottom line at high percent:
  intended (spikes are the earliest KOs off-stage). On-stage a downward launch is stopped by the floor.
- **Armor tanking** (degenerate: Bruna armor eating smashes at 180 %): `ArmorBreakKbM` breaks armor on big hits.
- **Lifesteal at 0 %**: clamps at 0 (no negative percent).
- **Jab / link infinite at high percent**: set knockback and low growth keep jabs from launching; the legacy stun
  floor is unchanged, so no new infinites appear (existing D-N1 no-buffer rule applies).
- **Respawn camping**: hover is untouchable; `RespawnInvuln` 120 ticks starts when the hover ends.
- **Stalling to win on time**: time-out uses stocks then percent; a leader who runs still takes percent from chip.
- **Edge-guarding a respawning fighter**: impossible during Out/Hover; respawn point is above stage centre.

## 6. Dependencies
| System | Direction | Contract |
|---|---|---|
| Damage pipeline (`ChibiCombatStats`, damage-conversion.md) | in | provides `dmgU`, unchanged; this spec consumes it via `Pct()` |
| Movesets (`design/gdd/movesets/README.md` 3, `movesets.csv`) | in | KbX/KbY/Stun stay the source of direction, tier and stun floor; optional `bkb`/`kbg` columns; OD-4 superseded by `ClassKbP` |
| Stages (`design/gdd/stages/*.md`, level-designer; `ChibiStageTable`) | in | per stage: 4 edge positions + `KoActive` flags + RespawnX/Y; must use the 4.8 table for line placement |
| Hazards (ST-3) | in | hazard hits go through `ApplyHit`-equivalent with their own d / bM / sP (or set knockback) |
| CPU (`ChibiCpuInputSource`) | out | reads `PctM`, `Weight`, `Stocks` (3.5) |
| HUD / results / events / audio (ui-programmer) | out | reads `PctM`, `Stocks`, `OutTicks`, `HoverTicks` |
| Netcode determinism (`netcode-core.md`) | both | all fields hashed; integer only; RulesVersion bump; goldens re-recorded |

## 7. Tuning Knobs (all `ChibiSimConst` / data, mixed into the config hash)
| Knob | Default | Safe range | Category | Effect / rationale |
|---|---|---|---|---|
| `PctPerHpM` | 200 | 150-260 | curve | percent per HP; 200 = old Kaia bar is 100 % |
| `PctCapM` | 999 900 | fixed | gate | display/overflow cap |
| `DmgTermCapM` | 15 000 | 12 000-25 000 | curve | caps d in `p*d/20`; stops Bruna's 22 % smash KOing at ~40 % |
| `KbgBase`, `KbgDiv` | 250, 250 | 150-350, 200-320 | curve | growth by tier (H smash = 1000) |
| `BkbBase`, `BkbNum/BkbDen` | 10 000, 2/15 | 5 000-20 000 | curve | base by tier (H = 35 000) |
| `UltBkbBonusM` | 80 000 | 40 000-120 000 | curve | ult decisiveness (Kick ult KOs at ~56 % from centre) |
| `ClassKbP` Ranged/Magic | 850 | 700-1000 | curve | zoner kill power (OD-PK2) |
| `SetKbMag` | 30 000 | 20 000-40 000 | feel | below = fixed knockback |
| `LsSubPerKb` | 1530 | 1300-1800 | curve | launch speed per KB; main KO-percent lever (+10 % -> KOs ~9 % earlier) |
| `LaunchDecay` | 2700 | 1800-3600 | feel | how fast launches slow |
| `GravFullKbM`, `GravZeroKbM` | 60 000, 120 000 | 40-80k, 100-160k | feel | juggle arcs vs straight launches |
| `HitstunCap` | 90 | 60-150 | feel | max stun ticks |
| `ArmorBreakKbM` | 150 000 | 100 000-250 000 | gate | armor holds below |
| `StocksStart` | 3 | 1-5 | gate | match length |
| `MatchTicks` | 300 s (was 180 s) | 180-480 s | gate | 3 stocks need ~2-4 min (OD-PK3) |
| `RespawnDelay` | 60 | 30-120 | feel | KO effect time |
| `RespawnHoverMax` | 180 | 60-300 | feel | max time on the respawn point |
| `RespawnInvuln` | 120 (was 90) | 60-180 | feel | after hover |
| `ComebackPctM` | 100 000 | 80-150k | curve | ult gain x2 threshold |
| `CpuKillPctOffset` | 10 | 0-40 | feel | CPU kill read = weight + 10 % |
| HUD ramp (view only) | 0 % white, 50 yellow, 100 orange, 150 red, 200+ dark red | - | feel | Brawlhalla-style read |

## 8. Acceptance Criteria
Functional (EditMode, deterministic; "edge" = victim feet at `HalfWidth - 400` facing in, attacker inside, Sky Dojo
side line 9 000 beyond the ledge; no crit forced by seeding a non-crit RNG state; uncharged; thresholds asserted with
margin >= 15 %):
1. `Pct(18_000_000) == 3600`; Kaia jab on Kaia adds 3.6 %; Bruna fsmash on Lyra adds 22.312 % (22 312).
2. Weights: Lyra 75, Ren 80, Kaia 100, Taro 100, Bruna 120.
3. Kaia fsmash on Kaia at 0 %: KBM 62 875, Vx = Dir x 76 958, Vy = 57 718, Stun 38, LaunchGravP 952 (4.7).
4. Kaia fsmash on Lyra: 0 % centre no KO; 60 % ledge no KO; 110 % ledge KO.
5. Bruna fsmash on Lyra: 0 % centre no KO; 30 % ledge no KO; 120 % ledge KO (and 120 % centre KO).
6. Kaia fsmash on Kaia: 90 % ledge no KO; 130 % ledge KO.
7. Kaia jab chain on Kaia at 300 %: never KOs from the centre; at 0 % each jab's stun = 17 (legacy floor).
8. A link hit at 300 % sets velocity exactly (Dir x 13 500, 9 000) and stun = rehit + 4.
9. Kaia up smash on Kaia under a top line 10 000 above the floor: 100 % no KO, 140 % KO.
10. Taro Kick ult hit 2 on Kaia at the centre: 30 % no KO, 70 % KO.
11. Stocks: KO -> Stocks 3 -> 2, PctM 0, OutTicks = 60, then hover, then Invuln 120. Last stock -> MatchOver, other
    wins. Both last stocks same tick -> Winner -1. Training KO -> Stocks unchanged, PctM 0.
12. Time-out: (2 stocks, 150 %) beats (1 stock, 0 %); (1, 40 %) beats (1, 41 %); (1, 40 %) vs (1, 40 %) -> draw.
13. Crossing an inactive edge never KOs; the fighter is clamped.
14. Lifesteal at PctM 2 000 healing 3 % -> PctM 0. Comeback ult x2 at PctM >= 100 000 only.
15. No `HpU` / `RingOutPenaltyPermille` reference remains in the sim; RulesVersion bumped; two runs of a recorded
    match give identical checksums.
Experiential (playtest, 10+ CPU and 5+ human matches): median KO percent 90-150 %; >= 70 % of KOs from smashes,
ults, strong aerials or spikes; median stock 40-80 s; < 15 % of matches reach time-out at 300 s; testers can say
who is closer to a KO from the HUD alone within 1 s.

### Open decisions for the lead
| # | Question | Options | Rec. |
|---|---|---|---|
| OD-PK1 | Damage -> percent | A uniform `Pct()`; B sheet % for CSV moves | A |
| OD-PK2 | Ren/Lyra knockback | A `ClassKbP` 850; B keep HalfKnockback (KOs > 250 %) | A |
| OD-PK3 | Timer | A 300 s; B keep 180 s (more time-outs) | A |
| OD-PK4 | Final time-out tie | A draw; B sudden death (300 %, 1 stock, new state) | A |
| OD-PK5 | `DmgTermCapM` | A 15 %; B no cap (Smash-faithful, Bruna KOs ~40 %) | A |
