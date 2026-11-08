# Damage Conversion: CombatAuthority / Fighter.ReceiveHit to ChibiSimulation (integer, 60 Hz)

Step 9 of the port. Status: approved scope (damage rules). Rule: play exactly like the original.
Sources: `CA` = Core/CombatAuthority.cs, `F` = Fighter/Fighter.cs, `CD` = Core/ClassData.cs,
`CS` = Netcode/ChibiCombatStats.cs, `SIM` = Netcode/ChibiSimulation.cs, `FI-INV` = feature-inventory.md.
Conventions from normals-conversion.md: 60 ticks/s, velocities in sub units (1 u/s = 15000 sub, 1/900 milli/tick),
`stunTicks = ceil(stun_s · 60)`, PendingHits resolved after both fighters' Step (SIM:504-505), timers decrement at
the start of a fighter's Step (SIM:534-543) before actions.

## 0. Ground truth about the original (verified from source)

| # | Fact | Source |
|---|---|---|
| 1 | `Hp`, `GuardHp`, `Ult` are **float**. Damage is never rounded; only the popup text uses `RoundToInt(dmg)` | F:613, F:586, F:624, F:438 |
| 2 | `AttackPower = attack · (Heavy ? 1.2 : 1) · (weakened ? 0.9 : 1)`; Heavy x1.2 has **no passive check** (Crusher and Juggernaut both) | F:112-121 |
| 3 | `RollDamage = AttackPower · mult · (crit ? 1 + critDmg : 1)`, crit = `Random.value < CritRate`, always one draw | CA:43-49 |
| 4 | `CritRate = critRate + (Ranged && P0 ? 0.2 : 0)` additive; critRate 0.05, critDmg 0.5 for every class | F:147, CD:17-18 |
| 5 | `Defense = def.defense · (armorBroken ? 0.85 : 1)` is **fractional** (25.5, 17, 8.5) | F:148 |
| 6 | `ApplyDefense = max(1, raw − floor(DEF/10))`; `BlockedFraction = clamp(0.5 − DEF·0.01, 0.1, 0.5)` both use the fractional Defense | CA:34, CA:52-55 |
| 7 | `GuardMax = 40 + 3·def.defense` uses the **base** defense, fixed at Init | F:90, CA:37 |
| 8 | Guard regen `+24·dt` when `!blocking && !GuardBroken`, capped at GuardMax; `blocking` is last frame's value | F:202-203 |
| 9 | ReceiveHit order: dead / invuln / `!Match.Running` → kb·KnockbackScale → parry stance → perfect parry → Ward → crit roll → guard drain (raw) → break or block fraction → defense → ApplyDamage → OnHitLanded | F:545-599 |
| 10 | ApplyDamage: `Hp = max(0, Hp−dmg)`; **if stun > 0**: `hitstun = max(hitstun, stun)` + Interrupt(); **if `!held && kb² > 0.01`**: velocity **set** to kb (X and Y); `GainUlt(1.5)` always; KO if Hp ≤ 0. It does **not** call Stun() | F:611-628 |
| 11 | Stun(t): `hitstun = max`, Interrupt(), velocity = `(0, min(0, vy))` — used by the perfect parry | F:663-668 |
| 12 | Interrupt(): stop coroutines, actionLock 0, dashTimer 0, blocking false, release held. **Does not clear parryUntil or spinGuardUntil** (only RingOut does, F:698) | F:670-677 |
| 13 | OnHitLanded: blocked → +1 ult and return. Else HitsLanded, combo, `gain = 3 (+2 at combo ≥ 3)`, x2 if attacker Hp < 30% MaxHp, then life steal `dmg·0.05`, then Hex roll, then Armor Break roll | F:630-649 |
| 14 | Patience: `Polearm && P1 && Time.time − lastAttackTime ≥ 3`; lastAttackTime starts at −10, set by DoNormal and UseSkill at start; being hit does not touch it | F:443-451, F:463, F:721-722, FI-INV 2c |

## 1. Damage formula (integer, exact)

### 1a. Why integer HP is not enough

Today's sim truncates after every multiply and stores integer HP (CS:204-212, SIM:1055). Mismatches against the float original:
Heavy heavy 75.6 → 75; Heavy crit heavy 113.4 → 112; Ranged crit heavy 40.5 → 40; Blood Edge on a light hit vs DEF 20
heals 18·0.05 = 0.9 → **0** (the passive almost never works); Armor Break on Heavy gives DEF 25 not 25.5 (block fraction
0.25 instead of 0.245), on Ranged/Magic 8 not 8.5 (0.42 instead of 0.415). Decision D9-1 below fixes this with fine units.

### 1b. Units (decision D9-1, recommended; alternatives in section 11)

- **HP and guard in micro-HP** (`int`, 1 HP = 1 000 000 µ). Max 600 HP = 6·10⁸ < 2³¹. Displayed HP = `HpU / 1e6`.
- **Intermediate damage in nano-HP** (`long`, 1 HP = 10⁹ n). Every factor is permille with ≤ 3 decimals (mult) or 1
  decimal (1.2, 0.9, 1.5), so the product is exact in nano; one final half-up rounding to micro.
- **Defense in hundredths** `DefH = Defense · (armorBroken ? 85 : 100)` (2550 / 1700 / 850 when broken): exact for any int.
- Error vs the exact real-number result: ≤ 0.5 µHP per hit. Float32 noise in the original at 500 HP is ~30 µHP, so the
  sim is at least as close to the design numbers as the original is.

### 1c. Formulas

```
RawN   = Attack · 10⁹ · Mult / 1000                       (Mult already includes finisher x1.5 and Patience x1.5)
RawN   = RawN · (1000 + DamageBonusPermille) / 1000       (Heavy: 1200, both passives — CS:105)
if weakened: RawN = RawN · 900 / 1000                     (Hex weaken, F:118)
crit   = Rng.Roll(CritRatePermille)                       (ALWAYS one draw)
if crit:    RawN = RawN · (1000 + CritDamagePermille) / 1000
BlockedPcm = clamp(50000 − 10·DefH, 10000, 50000)            (= BlockedFraction · 100000)
X      = blocked ? RawN · BlockedPcm / 100000 : RawN
DmgN   = max(10⁹, X − (DefH / 1000) · 10⁹)                (DefH/1000 = floor(DEF/10), integer division)
DmgU   = (DmgN + 500) / 1000                              (half-up to micro-HP)
```

| Symbol | Type | Range | Description |
|---|---|---|---|
| Attack | int | 15–35 | CD attack (CS Create) |
| Mult | int permille | 400–~5000 | move multiplier incl. finisher/Patience |
| DamageBonusPermille | int | 0 or 200 | Heavy x1.2 |
| weakened | bool | | attacker `WeakenTicks > 0` |
| CritRatePermille | int | 50 or 250 | Eagle Eye +200 |
| CritDamagePermille | int | 500 | x1.5 |
| DefH | int | 850–3000 | defender defense in hundredths |
| blocked | bool | | defender blocking and guard survived |
| RawN | long | ~4·10⁹–~3·10¹¹ | raw damage, nano-HP; also the guard drain |
| DmgU | int | 1 000 000–~3·10⁸ | final damage, micro-HP |

Output range: DmgU ≥ 1 HP by the max (CA:54); no negative or zero damage. Overflow: largest product
`RawN · 50000 ≈ 1.5·10¹⁶` < 9.2·10¹⁸. Worked example: Heavy heavy (Mult 1800) crit vs Melee blocking:
RawN = 35·10⁹·1.8 = 63·10⁹ → ·1.2 = 75.6·10⁹ → crit 113.4·10⁹; BlockedPcm = 50000−2000 = 30000 → X = 34.02·10⁹;
DmgN = 34.02·10⁹ − 2·10⁹ = 32.02·10⁹ → DmgU = 32 020 000 (32.02 HP). Guard drain = 113.4 HP.

### 1d. Per-class table (base stats → sim)

| Class | ATK | HP → MaxHpU | DEF → DefH (AB) | floor(DEF/10) (AB) | Block frac (AB) | GuardMax → GuardMaxU | AttackPower | Crit permille |
|---|---|---|---|---|---|---|---|---|
| Melee | 20 | 500 → 5·10⁸ | 2000 (1700) | 2 (1) | 0.30 (0.33) | 100 → 10⁸ | 20 | 50 |
| Heavy | 35 | 600 → 6·10⁸ | 3000 (2550) | 3 (2) | 0.20 (0.245) | 130 → 1.3·10⁸ | 42 (both passives) | 50 |
| Ranged | 15 | 400 → 4·10⁸ | 1000 (850) | 1 (0) | 0.40 (0.415) | 70 → 7·10⁷ | 15 | 50 / 250 Eagle Eye |
| Magic | 20 | 375 → 3.75·10⁸ | 1000 (850) | 1 (0) | 0.40 (0.415) | 70 → 7·10⁷ | 20 | 50 |
| Polearm | 25 | 500 → 5·10⁸ | 2000 (1700) | 2 (1) | 0.30 (0.33) | 100 → 10⁸ | 25 | 50 |

## 2. Block, guard, guard break

| Rule | Original | Sim (target) |
|---|---|---|
| Guard drain | `GuardHp −= raw` (crit included, before block fraction) | `GuardU −= RawN / 1000` (exact) |
| Survives (`GuardU > 0`) | dmg = ApplyDefense(raw·frac); stun 0; kb·0.25 | DmgU via 1c with blocked; no stun, **no Interrupt** (blocking stays); velocity set to `(Dir·KbX/4, KbY/4)` **X and Y** if kb² threshold passes |
| Chip ult | defender +1.5 (ApplyDamage), attacker +1 | defender +15, attacker +10 (already) |
| Break (`GuardU ≤ 0`) | GuardHp 0, blocking false, broken 3 s; full damage, stun = max(spec, 1.0 s) | GuardU 0, GuardBrokenTicks 180, Shielding false; unblocked path with stun = max(h.Stun, 60) |
| Break counts as a landed hit | yes (blocked = false → OnHitLanded full: combo, ult, life steal, procs) | yes |
| Regen | +24·dt while !blocking && !broken, cap GuardMax | `GuardU = min(GuardMaxU, GuardU + 400 000)` per tick under the same conditions (exact at 60 Hz, no remainder) |
| Broken window | `Time.time < t0 + 3` → frames k = 0..179 | set 180 on tick T; Dec makes it 0 on tick T+180, where regen and raising the shield are allowed again |
| Kb threshold | `kb.sqrMagnitude > 0.01` (|kb| > 0.1 u/s) | `(long)vx² + vy² > 2 250 000` on the vector actually applied (1500 sub = 0.1 u/s) |

Chip division: `Dir · (KbX / 4)`, `KbY / 4`, C# truncation toward zero (symmetric for both facings). Not every value is
a multiple of 4: Ranged light after halving is 11250 → 2812 (exact 2812.5). Error ≤ 0.75 sub/tick (< 0.001 milli/tick),
accepted.

## 3. Perfect parry and Parry skill reflect

### 3a. Perfect parry (F:563-572)
Window: `t − blockStart ≤ 0.18`. At the 60 Hz reference the hit on the k-th tick after the raise tick qualifies when
k/60 ≤ 0.18 → k ≤ 10. **ParryWindow = 11 ticks, ShieldTick 0..10** (ShieldTick 0 = raise tick; hits resolve after the
Step that raised it). Not near a float boundary (10/60 = 0.1667, 11/60 = 0.1833). Today's SIM:1028 `ShieldTick < 11` is correct.

Effects: defender Parries++, +150 ult; shield stays up, no guard drain, no damage, no draws. Attacker: `Stun(27)`
(0.45·60 = 27): `Stun = max(Stun, 27)`, Interrupt(), **Vx = 0, Vy = min(0, Vy)**. Unblockable hits skip it. Several hits
inside the window are all parried (blockStart is not reset). Hitstop 0.1 s: step 10.

### 3b. Parry skill reflect (F:550-561), checked before the perfect parry, skips unblockable
1. `ParryTicks = 0`, defender Parries++, defender +150 ult.
2. Reflected = RawN from the **attacker's** stats (crit draw, attacker weaken) with `blocked = false`, then defense with the
   **attacker's** DefH (incl. Armor Break). No Ward roll, no block, no OnHitLanded, no procs.
3. ApplyDamage to the attacker: HpU −= DmgU; stun 30 (0.5 s) → `Stun = max`, Interrupt(); velocity **set** to
   `(sx · 75 000, 60 000)` sub (5 and 4 u/s), `sx = A.X ≥ D.X ? +1 : −1` (Unity `Mathf.Sign(0) = +1`; positions, not hit Dir);
   **attacker +15 ult** (the "when hit" 1.5); KO check on the attacker.
4. Hitstop 0.12 s: step 10.

## 4. ApplyDamage (unblocked hit or guard break) exact order

1. `HpU = max(0, HpU − DmgU)`.
2. If stun > 0: `Stun = max(Stun, stun)`; Interrupt(D) = MoveSlot −1, DashTicks 0, Shielding false, ShieldTick 0
   (held release when grabs land). **Not** ParryTicks / GuardTicks (F:670-677 leaves parryUntil/spinGuardUntil).
3. If kb² > threshold: `Vx = Dir · KbX`, `Vy = KbY` (velocity set; Stun()'s zeroing is NOT applied). Kb 0 → velocity untouched.
4. Defender +15 ult (0..1000 scale = original 0..100 x10; every gain has ≤ 1 decimal so x10 is exact).
5. KO if `HpU ≤ 0` (match over; later hits that tick are ignored, = `!Match.Running`).
6. Attacker (OnHitLanded): HitsLanded++, ComboHits++, ComboTimer 66, CombosLanded on multiples of PatternLength;
   gain = 30 (+20 at ComboHits ≥ 3), x2 if `(long)A.HpU·10 < (long)A.MaxHp·10⁶·3`; then life steal; then Hex / Armor Break roll.
   Sim constants UltPerHit 30 / UltPerComboHit 50 / UltWhenHit 15 / UltPerParry 150 / blocked 10 all match the original.

A stun-0 unblockable hit on a shielding defender leaves the shield and the current move running (today SIM:1069 clears
them unconditionally: mismatch).

## 5. Passives

| Passive | Exact rule | Sim |
|---|---|---|
| Crusher / Juggernaut (Heavy) | AttackPower x1.2 on both; atk speed x0.85 (step 8) / move x0.85 (movement) | DamageBonusPermille 200 for both (CS:105, verified) |
| Blood Edge (Melee P1) | after ult gain: `Hp = min(MaxHp, Hp + dmg·0.05)`, dmg = final damage; only landed (incl. guard break) hits; not blocked, not reflect | `HealU = (DmgU·50 + 500)/1000`, `HpU = min(MaxHpU, HpU + HealU)` |
| Eagle Eye (Ranged P0) | crit 0.05 + 0.2 = 0.25 | 250 (verified) |
| Ward (Magic P1, defender) | after both parries, before crit: 20% → return Negated. Negates **everything**: no crit draw, damage, guard drain, kb, stun, interrupt, ult (either side), combo, procs. Applies to unblockable hits and while blocking | `Rng.Roll(200)` → return |
| Hex (Magic P0, attacker) | landed hit (not blocked, incl. break), 10% → slow/weaken/sluggish until t+4; refresh, no stack | Slow/Weaken/SluggishTicks = 240; active on hit tick T .. T+239 |
| Armor Break (Polearm P0) | same gate, 10% → DEF x0.85 until t+5 (affects block fraction and floor) | ArmorBreakTicks = 300; DefH x0.85 |
| Patience (Polearm P1) | mult x1.5 if ≥ 3 s since the START of the last normal or skill; first attack of the match qualifies; normals and skills, not ult; being hit, stun, dash, blocking do not reset | `SinceAttackTicks` (sec. 8): +1 every tick, init 600; bonus if ≥ 180 at StartMove slot 0..4, then reset to 0 |

Patience at the boundary: the original's float `Time.time` difference at exactly 180 frames can land on either side of
3.0; the sim fixes it to **180 qualifies, 179 does not**.

## 6. Deterministic roll order (all from `s.Rng`)

Per resolved hit, in this order; each draw happens only under its gate, and every gate reads sim state only:

| # | Draw | Gate | Draws |
|---|---|---|---|
| 0 | none | MatchOver, D dead, D.Invuln > 0 | stop, 0 |
| 1 | Reflect crit (attacker) | D.ParryTicks > 0 && !unblockable | 1, stop |
| 2 | none | perfect parry | 0, stop |
| 3 | Ward | D is Magic P1 | 1, stop if < 200 |
| 4 | Crit | always reached here | 1 |
| 5 | Hex | landed unblocked hit, A is Magic P0 | 1 |
| 6 | Armor Break | landed unblocked hit, A is Polearm P0 | 1 |

Max 3 draws per hit (5 and 6 are exclusive by class). Gated draws are safe: both peers hold identical state, so
"always consume" is not needed for alignment; the gates mirror the original's `&&` short-circuit so the probability per
event is the original's. Hits resolve in the fixed SIM order (h0, h1, then projectiles by index). `Roll(p)` uses
`NextU32 % 1000` (modulo bias 2.3·10⁻⁷, accepted). Arrow spread (step 10) draws at spawn time in spawn order.
**AI must own a separate ChibiRng** (seed = match seed ^ constant) and never touch `s.Rng`.

## 7. Short shield taps (input side, step-3 leftover)

Problem: the sampler reads Block as held once per tick; a press+release between two samples is lost. In the original a
one-frame block sets blockStart and can perfect-parry.

Fix (input collector, not sim): keep `shieldLatch`, set on any render frame where Block (key, pad, or later gesture) is
down; at packet sampling `Shield = heldNow || shieldLatch`, then `shieldLatch = false`. No new bit, packet stays 16-bit,
Shield keeps its "held" meaning. A tap therefore produces exactly one tick of Shield = 1.

Sim treatment (no change needed): that tick FreeActions raises with ShieldTick 0; hits resolving that tick are perfect
parries; next tick Shield = 0 drops it. Matches the original's one-frame block. Not buffered: if the fighter is not free
that tick the tap is ignored (original requires canAct). Known loss: release + re-press inside one tick looks like a
continuous hold, so the parry window does not restart (two transitions in 16.7 ms; accepted).

## 8. Contract for code (RulesVersion 9)

1. `RulesVersion = 9`, remark "damage matched to original (micro-HP, exact defense, block/guard, parries, passives)".
2. ChibiFighterSim: `Hp` → `HpU` (int µHP); `GuardHp10` → `GuardU` (int µHP); `IdleTicks` → `SinceAttackTicks`
   (init 600 = lastAttackTime −10; +1 at the start of every Step in all states, cap 1<<20). Update Copy and Hash.
3. ChibiCombatStats: add `HpScale = 1_000_000`, `GuardRegenPerTickU = 400_000`, `GuardMaxU(def)`, `DefenseH(s, ab)`,
   `BlockedPcm(defH)`, `RawDamageNano(atk, mult, weakened, ref rng, out crit)`, `FinalDamageMicro(rawN, defH, blocked)`,
   `LifestealMicro(dmgU, permille)`. **Remove** `RollDamage`, `MitigateDefense`, `EffectiveDefense`, `BlockedPermille`,
   `GuardMax10`, `GuardRegenPerTick`. Keep HexDuration 240, ArmorBreakDuration 300, GuardBreakStunTicks 60, GuardBrokenTicks 180.
4. ChibiSimConst: `ParryWindow = 11` (unchanged), `PerfectParryStun = 27`, `ReflectStun = 30`, `ReflectPushX = 75_000`,
   `ReflectPushY = 60_000` (replace 120/80 milli), `KbMinSq = 2_250_000`, `PatienceInit = 600`.
5. Helpers: `Interrupt(f)` (MoveSlot −1, DashTicks 0, Shielding false, ShieldTick 0); `StunF(f, t)` = max + Interrupt +
   Vx 0, Vy min(0, Vy). Step's stun branch (SIM:562) must stop clearing ParryTicks and GuardTicks.
6. ApplyHit: order of sections 3–6; gate `MatchOver` first; reflect uses position sign and gives the attacker +15;
   perfect parry uses StunF; blocked chip sets Vx **and** Vy; Interrupt and shield/move clearing only when stun > 0;
   velocity only above KbMinSq; never clear ParryTicks/GuardTicks on hit.
7. StartMove: Patience for slots 0..4 only; reset SinceAttackTicks for slots 0..4 only (ult: verify, sec. 11).
8. Elsewhere: ring-out penalty `HpU −= MaxHp·150_000`; low-HP x2 in long; KO `HpU ≤ 0`; HUD/AI read `HpU/1e6f` and
   `GuardU/GuardMaxU`. Input collector shield latch (sec. 7).

## 9. Reference test values (exact in µHP unless noted; tolerance vs original float ±100 µHP)

**Forcing RNG**: no production hook. A test helper `RngFind(start, params Func<int,bool>[] want)` steps candidate
states (start, start+1, …) until the next k values of `NextU32()%1000` satisfy the predicates, then writes
`s.Rng.State`. Example: crit on = `v < 50`, crit off = `v ≥ 250` (works for Eagle Eye too), Ward on `< 200`, Hex/AB on `< 100`.
Fixed search start keeps tests deterministic. Draw counts are checked by comparing `s.Rng.State` after the hit with a
copy advanced exactly N times.

Unblocked damage, non-crit (light Mult 1000 / heavy 1800), defender order Melee, Heavy, Ranged, Magic, Polearm:

| Attacker | Light | Heavy | Heavy crit |
|---|---|---|---|
| Melee / Magic | 18, 17, 19, 19, 18 | 34, 33, 35, 35, 34 | 52, 51, 53, 53, 52 |
| Heavy (P0 and P1 identical) | 40, 39, 41, 41, 40 | 73.6, 72.6, 74.6, 74.6, 73.6 | 111.4, 110.4, 112.4, 112.4, 111.4 |
| Ranged | 13, 12, 14, 14, 13 | 25, 24, 26, 26, 25 | 38.5, 37.5, 39.5, 39.5, 38.5 |
| Polearm (P0, not first attack) | 23, 22, 24, 24, 23 | 43, 42, 44, 44, 43 | 65.5, 64.5, 66.5, 66.5, 65.5 |

| Case | Expected |
|---|---|
| Block chip: Melee light vs Melee | guard 100 → 80; dmg 4; Vx = ±9375, Vy = 7500 sub; Stun 0; still Shielding; D ult +15, A +10 |
| Block chip: Ranged light vs Heavy | 15·0.2 = 3 − 3 → 1 (min); kb 22500/18000 → halved 11250/9000 → chip (±2812, 2250) sub |
| Heavy heavy vs Melee block ×2 | 1st: guard 24.4, dmg 20.68, Vx ±30000 Vy 22500; 2nd: break, dmg 73.6, Stun 60, GuardBrokenTicks 180, full kb (±120000, 90000) |
| Heavy heavy vs Heavy block | guard 130 → 54.4; chip 12.12 |
| Regen | Melee guard 80 → 100 in 50 ticks; 0 → 100 in 250; +24.0 HP per 60 ticks; no regen while Shielding or broken |
| Break no-block | Shield held from break tick T: Shielding false through T+179, true on T+180 |
| Perfect parry | shield raised tick R, hit resolves R+10 → parry, attacker Stun 27, Vx 0, Vy ≤ 0; R+11 → normal block |
| Reflect: Heavy light into Melee Parry stance | Heavy loses 42 − 3 = 39 (crit 60); Stun 30; V = (sx·75000, 60000); Heavy ult +15, Melee +150; 1 draw |
| Ward | Magic P1 hit with draw < 200: HpU, GuardU, V, Stun, both Ult unchanged, exactly 1 draw; ≥ 200 + crit off: Melee light → 19 |
| Hex | Magic P0 light vs Melee 18 + proc: 240 ticks (active T..T+239); hexed Heavy heavy vs Melee = 75.6·0.9 − 2 = 66.04 |
| Armor Break | Polearm light vs Heavy 22 → proc → next 23; Polearm heavy into broken Heavy block: 45·0.245 − 2 = 9.025; Melee light vs broken Ranged = 20 |
| Life steal | Melee P1 light vs Melee at HpU 3·10⁸ → +900 000; crit +1 400 000; blocked hit +0 |
| Patience | first Polearm P1 light at tick 0 → 37.5 − 2 = 35.5 vs Melee; next light 179 ticks later → 23, 180 later → 35.5; a 60-tick stun in between does not reset; heavy with bonus = 67.5 − 2 = 65.5 |
| Heavy x1.2 | Heavy P0 and P1 light vs Melee both 40 |
| Short tap | packet Shield = 1 on tick R only, hit resolves R → parry; resolves R+1 → full hit; collector at 144 Hz: press+release between samples → exactly one sampled Shield = 1 |
| Stun-0 unblockable on shield | Shielding and MoveSlot unchanged |

## 10. Mismatches in today's sim (fixed by section 8)

1. Truncating integer damage/HP (Heavy heavy 75 not 75.6; crits; life steal mostly 0).
2. Armor Break defense truncated (25 / 8 instead of 25.5 / 8.5) → wrong block fraction.
3. Guard drained by truncated damage.
4. Blocked chip sets only Vx (original X and Y); velocity set even below the 0.1 u/s threshold.
5. Unblocked hit always clears Shielding/MoveSlot/ParryTicks/GuardTicks, even with stun 0; Interrupt never clears ParryTicks/GuardTicks in the original (SIM:562 and SIM:1069).
6. Perfect parry does not zero attacker Vx / clamp Vy.
7. Reflect push 120/80 milli/tick (7.2/4.8 u/s) instead of 5/4 u/s; direction from hit Dir instead of positions; attacker misses +15 ult; incomplete Interrupt.
8. Patience counts only free ticks, starts at 0 (first attack does not qualify), and applies to the ult.
9. No MatchOver gate in ApplyHit.

## 11. Open decisions and not verifiable from the files read

- D9-1 alternatives: (a) micro-HP + nano intermediate (recommended, exact); (b) milli-HP with rounding each step (simpler, life steal and chip within ±0.001 HP, still visible drift near KO); (c) keep integer HP (keeps all section 10 item 1 mismatches). User/creative call if (a) is too invasive for HUD/AI.
- Whether the ult start sets lastAttackTime (FI-INV says Patience is "not ult"; the ult dispatch was not read).
- Which skills apply the Patience `b` (F:727-729 onward, step 10); skills that ignore it still reset the timer.
- Whether a Ward-negated or parried projectile is destroyed (Projectile code not read).
- Same-tick trade: the original's Unity coroutine order can stop a stunned fighter's strike that frame; the sim keeps the trade (h0 then h1). Unverifiable without the engine.
- `IsDead` / `Match.Running` exact definitions (assumed Hp ≤ 0 and match not over; the sim has no intro).
- HUD rounding of displayed HP.
- Skill-start refresh of the light-combo window: the original resets comboIndex by `lastAttackTime > 0.9`, which skills also set; the sim's ComboWindow ignores skills. Could reuse `SinceAttackTicks > 54`; belongs to normals/step 10.
- Exact table kb for every skill: verify chip values when the step-10 move table lands.
