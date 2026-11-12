# ADR-0001: Damage percent and stocks replace HP

## Status

Proposed

> Who may move this to `Accepted`: the user, or `technical-director` on the user's explicit confirmation.
> It is implemented in the working tree (rules v20) but not yet reviewed (Wave C: network-programmer +
> security-engineer) or committed.

## Date

2026-11-11

## Last Verified

2026-11-11 (against `Netcode/ChibiSimulation.Percent.cs`, `ChibiSimulation.cs` RulesVersion 20, protocol 4)

## Decision Makers

User (GL-1, GL-2 in `production/session-state/active.md`), game-designer (spec), technical-director (this record),
gameplay-programmer (implementation).

## Summary

The match win condition moves from an HP bar to Smash-style damage percent, knockback that grows with percent, and
3 stocks lost only by crossing an active stage KO edge. The existing exact damage pipeline is kept and converted to
percent by one integer rule, and the knockback formula is an integer port of the Smash (Ultimate/Melee) structure so
the deterministic rollback core stays bit-exact across peers.

## Engine Compatibility

| Field | Value |
|-------|-------|
| **Engine** | Unity 6000.6.7f1 |
| **Domain** | Core (deterministic gameplay simulation) |
| **Layer** | Core |
| **Knowledge Risk** | LOW: pure C# integer code, no Unity API inside the sim |
| **References Consulted** | `docs/architecture/netcode/DEVELOPER-GUIDE.md` (determinism rules), `damage-conversion.md` (D9-1) |
| **Post-Cutoff APIs Used** | None |
| **Verification Required** | EditMode contract + sync tests green at rules v20; the spec's 15 functional acceptance criteria |

## ADR Dependencies

| Field | Value |
|-------|-------|
| **Depends On** | None (builds on decisions D9-1 damage in micro-HP and D-F1 double KO = draw) |
| **Enables** | ADR-0002 (stage KO edges are only meaningful once KO = crossing an active edge) |
| **Blocks** | Wave C review and the push of the stages / percent session |
| **Ordering Note** | Ships in the same RulesVersion (20) as ADR-0002; both change the config hash together |

## Context

### Problem Statement
The user asked for a Super Smash Bros. / Brawlhalla gameplay loop (GL-1). Under the HP model a fighter is KO'd when
`HpU <= 0` and a ring-out only costs 15 % HP, so positioning and edge play barely matter and the five new stages'
KO-edge design (ADR-0002) has nothing to bite on.

### Current State (rules v19)
- `ChibiFighterSim.HpU` (micro-HP, D9-1) drives the KO, timeout winner, ult comeback x2, CPU kill reads, HUD bar,
  results, KO events and audio.
- Knockback is a fixed per-move `KbX/KbY` (Ranged/Magic halved), independent of accumulated damage.

### Constraints
- Determinism (DEVELOPER-GUIDE section 3): integers only, no floats in `Step`, every mutable field in
  `ChibiSimState` (`CopyFrom` + `ComputeChecksum`), sim RNG only.
- Q1: one gameplay implementation, no feature dropped: 108+ existing moves must get sensible knockback with no hand
  editing, and every combo string the movesets already rely on must keep working at low percent.
- The damage pipeline (Attack x Mult, crit, defense, block, guard, parry, ward, lifesteal) is balanced and tested; it
  must not fork.
- Session deadline: everything implemented, tested and pushed in this session.

### Requirements
- Percent that only rises; knockback that grows with percent and falls with weight; 3 stocks; KO only on an active
  edge; timeout by stocks, then percent, then draw.
- Bit-identical results on both peers and in replays.

## Decision

1. **HP leaves the rollback state.** New per-fighter fields: `PctM` (milli-percent), `Stocks`, `OutTicks`,
   `HoverTicks`, `LaunchDecX/Y`, `LaunchGravP`, `KosTaken`, all copied and hashed (`CopyPercent` / `HashPercent`).
   `ChibiCombatStats.MaxHp` survives only as the source of `Weight = MaxHp / 5` (75, 80, 100, 100, 120).
2. **One conversion rule (GL-2, OD-PK1 = A).** Every hit's micro-HP from the unchanged pipeline becomes percent:
   `Pct(u) = (u x 200 + 500 000) / 1 000 000` (half-up, 0.2 % per HP). Melee, projectiles, zones, summons, hazards,
   reflect and lifesteal all go through it.
3. **Integer Smash knockback (milli-KB):**
   ```
   d   = min(hitPctM, DmgTermCapM 15 000)                  // GL-2: hit percent capped at 15 % in the formula only
   x   = p/10 + p*d/20 000                                 // p = defender PctM after the hit
   y   = x*280/(w + 100) + 18 000                          // 280 = 200 x 1.4, w = weight
   KB  = clamp((y*sP/1000 + bM) * ClassKbP/1000, 0, 400 000)
   sP  = 250 + min(mag,250 000)/250 ;  bM = 10 000 + min(mag,250 000)*2/15 (+80 000 for ult hits)
   mag = ISqrt(KbX^2 + KbY^2)                              // base/growth derived from the existing move data
   ```
   Hitstun `min(90, max(legacyStun, KB x 0.4))` for weak hits (mag < 100 000: jabs, tilts keep combo timing) and
   `clamp(KB x 0.4, 10, 90)` for strong launches (GL-3, lead KO audit 2026-11-11: with the legacy floor a 0 % smash
   kept the victim helpless ~38 ticks and KO'd from the ledge on stages with a near bottom line; every new stage's bottom
   KO line is also >= 9725 below its lowest standable top); launch speed `KB x 1530 sub/tick` along the KbX:KbY direction;
   linear launch decay 2700 sub/tick; hitstun gravity fades from 100 % at KB 60 to 0 % at KB 120.
   Set knockback (legacy velocity and stun) when `mag < 30 000` or a grounded trip, which keeps link hits and jabs
   from scattering at high percent. `ClassKbP` = 850 for Ranged and Magic (GL-2, replaces the x0.5 halving).
   All `long` intermediates (max `y*sP` under 2e9), C# truncation toward zero.
4. **Stocks and edges.** 3 stocks; a KO happens only when the feet cross an ACTIVE edge of the stage
   (`ChibiStageDef.CrossesKoEdge`); KO -> stock lost (not in training), out 60 ticks, hover up to 180, then 120 ticks
   invuln. Last stock lost ends the match; both on the same tick = draw (D-F1). 300 s timer; timeout: more stocks,
   then lower percent, then draw (GL-2, no sudden death in v1). The -15 % ring-out rule is deleted.
5. **Every constant is data in one place** (`ChibiPercentConst`) and mixed into the config hash through
   `ChibiPercentConst.Constants`, so two builds with different tuning refuse each other at the handshake.
6. **RulesVersion 20.** Golden values in `NetcodeContractTests` are re-recorded on purpose. The wire format also
   changes in the same release (protocol 4: Start carries `HazardsOff`, stage range 0..7, see ADR-0002).

### Key Interfaces
| Reader | Contract |
|---|---|
| HUD / results / events / audio (view, green zone) | read `PctM`, `Stocks`, `OutTicks`, `HoverTicks`, `KosTaken`; KO event = `Stocks` decreased or `OutTicks` became > 0; damage number = `PctM` delta |
| CPU (`ChibiCpuInputSource`) | kill read `opp.PctM >= (opp.Weight + CpuKillPctOffset) x 1000` or near-edge rule |
| Pure formula functions (unit-testable) | `ChibiSimulation.Pct`, `KnockbackM`, `HitstunFromKb`, `LaunchGravityP`, `GrowthP`, `BaseM`, `ClassKbP`, `TimeoutWinner` |
| `ChibiFighterSim.HpU` / `Hp` / `Revives` | COMPATIBILITY shims only (never read by the sim); removal tracked as debt below |

### Implementation Guidelines
- The sim must never read `HpU`, `Hp` or `Revives`; new code must use `PctM` / `Stocks` / `InPlay`.
- Any change to a `ChibiPercentConst` value must bump `RulesVersion` and re-record the goldens (it changes the hash).
- Hazard hits must go through `KnockbackM` / `HitstunFromKb` / `LaunchGravityP` (same formula, attacker class 1000).
- Never introduce a float, `Math.Pow` or `Math.Sqrt` in the formula; `ChibiFixed.ISqrt` is the only root.

## Alternatives Considered

### Alternative 1: Keep HP (status quo, add stages only)
- **Description**: HP bar stays; new stages only change ring-out geometry.
- **Pros**: No RulesVersion churn in the combat core, no HUD/CPU rewrite, balance proven.
- **Cons**: Does not deliver GL-1; KO edges and hazards matter little when a ring-out is a 15 % HP fee.
- **Estimated Effort**: Lowest.
- **Rejection Reason**: The user explicitly chose the percent loop (GL-1).

### Alternative 2: Brawlhalla-style pure force (fixed + variable force per move, no weight)
- **Description**: `KB = fixedForce + variableForce x damage`, per-move hand-tuned forces, no weight term.
- **Pros**: Simpler formula, easy to read, fewer constants.
- **Cons**: Needs two hand-tuned numbers for each of 108+ moves (violates "no hand editing" and blows the schedule);
  no weight, so Bruna and Lyra would feel the same under launch; the design pillar "weight makes Bruna a wall"
  (spec section 2) is lost.
- **Estimated Effort**: High (per-move tuning pass plus playtests).
- **Rejection Reason**: Per-move authoring cost and the loss of weight identity.

### Alternative 3 (chosen): Smash formula, integer form, base/growth derived from KbX/KbY
- **Pros**: Well-understood curve with decades of tuning precedent (SSBWiki references in the spec); weight, hit
  strength and accumulated percent all count; every move gets values automatically from data it already has;
  integer-only, so it is deterministic and cheap.
- **Cons**: More constants to tune; derived values are an approximation of per-move intent (per-move `bkb`/`kbg`
  overrides are reserved in the spec for outliers).

### Why integer (not float) for the formula
The formula runs on both peers and inside rollback re-simulation; any IEEE rounding difference between CPUs, compilers,
JIT modes or Mono/IL2CPP would desync a match. The project's rule since D9-1 is integer milli/micro units with a single
explicit rounding, and the existing `NetcodeContractTests` / `ChibiSyncVerificationTests` assume bit-exact state.
Milli-percent and milli-KB keep three decimal digits, which is far below what a player can perceive.

## Consequences

### Positive
- Delivers the requested gameplay loop with zero per-move authoring; existing combo timing preserved at low percent.
- Stage design (ADR-0002) now matters: edges, walls, floors and hazards decide stocks.
- One damage pipeline remains; all balance invariants of damage-conversion.md still hold.

### Negative
- RulesVersion 19 -> 20 and protocol 3 -> 4: v20 builds refuse older builds (clear "different version" message);
  old replays do not play back on v20.
- Golden values (fixture checksums, config hash, hello hex) re-recorded on purpose; a reviewer must confirm the
  re-record is intended and not masking an unrelated change.
- Every HP reader had to move: CPU kill logic (ai-programmer), HUD bar -> percent number + stock icons and results
  screen (ui-programmer), KO events / damage numbers / audio "ended by KO" (view). Rewards and quests read no HP
  (grep 2026-11-11); any future "flawless" quest uses "no stock lost".
- Compatibility shims `HpU` / `Hp` / `Revives` exist as technical debt.

### Neutral
- Match timer 180 s -> 300 s (3 stocks need 2-4 min).

## Risks
| Risk | Mitigation |
|---|---|
| KO percents land outside the 90-150 % target (too early / never) | `LsSubPerKb` is the main lever (+10 % -> KOs ~9 % earlier); experiential criteria in the spec; tune constants only, never the structure |
| Bruna's high-damage smashes KO absurdly early | `DmgTermCapM` 15 % (GL-2) caps the damage term |
| Zoners (Ren / Lyra) cannot close stocks | `ClassKbP` 850 (700-1000 safe range) |
| Armor (Bruna) tanks smashes at high percent | `ArmorBreakKbM` 150 000 breaks armor on big hits |
| Jab / link infinites at high percent | set knockback below `mag` 30 000; legacy stun floor; no input buffer (D-N1) |
| Overflow in the formula | `long` intermediates, `KbCapM` 400 000, `PctCapM` 999 900; a test at the caps |
| A view/CPU reader still uses the `HpU` shim and shows a full bar | Debt item: remove the shims once grep finds no reader; the sim never reads them |
| Time-out stalling | timeout ranks stocks then percent; chip damage still adds percent |

### Tuning knobs (yellow zone; each change = RulesVersion bump + goldens re-recorded)
`ChibiPercentConst`: `PctPerHpM`, `DmgTermCapM`, `KbgBase/KbgDiv`, `BkbBase/BkbNum/BkbDen`, `UltBkbBonusM`,
`ZonerKbP`, `SetKbMag`, `LsSubPerKb`, `LaunchDecay`, `GravFullKbM/GravZeroKbM`, `HitstunCap`, `ArmorBreakKbM`,
`StocksStart`, `RespawnDelay/RespawnHoverMax/RespawnInvuln`, `ComebackPctM`, `CpuKillPctOffset`; `ChibiSimConst.MatchTicks`.
Safe ranges: `design/gdd/percent-knockback.md` section 7. The HUD colour ramp is view-only (green zone, no bump).

## GDD Requirements Addressed

| GDD System | Requirement | How This ADR Addresses It |
|------------|-------------|--------------------------|
| percent-knockback.md 3.1 / 3.4 | percent, stocks, out / hover / respawn state in the rollback state | Decision 1 and 4 |
| percent-knockback.md 4.1 | one integer damage -> percent rule | Decision 2 |
| percent-knockback.md 4.2-4.6 | integer Smash knockback, base/growth from KbX/KbY, set KB, hitstun, decay, gravity fade | Decision 3 |
| percent-knockback.md 7 | every knob data-driven and in the config hash | Decision 5 |
| stages/README.md 3.1 | KO only on active edges | Decision 4 + ADR-0002 |

## Performance Implications
- **CPU**: a handful of integer multiplies and one `ISqrt` per hit; negligible against the 16.6 ms frame and the
  rollback re-simulation budget (8 frames).
- **Memory**: 8 ints per fighter in the state; snapshot size grows by 64 bytes.
- **Load Time**: none.
- **Network**: none for inputs (16-bit packet unchanged); config hash unchanged in size.

## Migration Plan
Done in one RulesVersion step (20): state fields replaced, readers migrated, goldens re-recorded. No save-data
migration (HP was never persisted).

**Rollback plan**: revert the rules v20 changeset; RulesVersion returns to 19 and the v19 goldens are in git history.

## Validation Criteria
- `percent-knockback.md` section 8 criteria 1-15 pass as EditMode tests (worked example 4.7: KBM 62 875,
  Vx = Dir x 76 958, Vy = 57 718, Stun 38, LaunchGravP 952).
- `NetcodeContractTests`, `ChibiSyncVerificationTests` (hostile link, snapshot/restore at any depth, field coverage)
  green at rules v20; full EditMode suite green.
- No `HpU` / `RingOutPenaltyPermille` read inside the sim (grep).
- Playtest (10+ CPU, 5+ human matches): median KO percent 90-150 %, median stock 40-80 s, under 15 % time-outs.
- Network-programmer and security-engineer reviews signed off before commit.

## Related
- `design/gdd/percent-knockback.md` (spec v1), decisions GL-1 / GL-2 in `production/session-state/active.md`
- ADR-0002 (stages as data, deterministic hazards)
- `docs/architecture/netcode/damage-conversion.md` (D9-1), `DEVELOPER-GUIDE.md` (yellow zone)
- Code: `Assets/ChibiClash/Scripts/Netcode/ChibiSimulation.Percent.cs`, `ChibiCombatStats.cs` (`Weight`), `ChibiSimulation.cs` (`RulesVersion`)
