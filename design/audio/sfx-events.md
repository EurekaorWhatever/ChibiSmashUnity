# Chibi Clash: SFX Event Map and Mixing Rules

Status: DRAFT v1 (sound-designer, 2026-11-09). Waiting for audio-director review.
Sound ids and their recipes: `design/audio/sfx-spec.md`. Event source: `Assets/ChibiClash/Scripts/Netcode/View/ChibiSimEvents.cs`.
Implementer: `Netcode/View/ChibiAudio.cs` (tools-programmer). The view only reads `ChibiSimState`/events; audio never writes sim state.

Notation: `id` = play; `id x0.8` = playback pitch 0.8; `id -6` = extra -6 dB on top of the bus; `A + B` = both at once.
"Owner char" = the character of the fighter in `Index` (or the projectile / minion `Owner`). "Attacker" in 1v1 = `1 - defender`.

---

## 1. Playback contract

1. **2D only.** `spatialBlend = 0`, pan from screen X: `pan = clamp(x / halfStageWidth, -1, 1) * 0.5`. UI, flow, stingers centred.
2. **Real time, not sim time.** Hitstop, the ult cinematic and any slow-mo must not pitch or pause SFX. Do not route through `Time.timeScale`.
3. **Variant pick:** shuffle-bag per id (no variant twice in a row), then the runtime pitch jitter from the spec's Pitch column.
4. **Per-character override:** when an id below names a character sound, use the **owner char**'s sound. A `<char>_dash` / `_shim` / `_tchk` row replaces `core_dash` for that character only.

## 2. Simulation events (`ChibiSimEventKind`)

| Event | Condition / payload | Sound | Notes |
|---|---|---|---|
| MoveStarted | Value = slot, Value2 = combo index | section 3 (by move anim kind) or section 3.2 (existing slots) | the swing / cast sound. Never the impact. |
| Damaged | Index = defender, Value = whole HP lost | **hit = body + weapon layer** (section 2.1) | Value 0 (micro-HP ticks, burns) plays nothing |
| ShieldChanged | On = true | `core_shield_up` (Lyra: `lyra_vmm`; Bruna: `bruna_guard -6`) | On = false: silent, unless it is a guard break (2.2) |
| Parried | Index = parrying fighter | `core_parry` + owner layer: Kaia `kaia_clink`, Taro `taro_ting`, Bruna `bruna_clang`, Ren `ren_zing`, Lyra `lyra_ting` | |
| StunChanged | On | silent (the hit already speaks) | Off: silent |
| RingOut | Index = fighter blasted | `core_ringout`, then `core_respawn` 90 ms later | |
| KO | Index = fighter at 0 HP | `core_ko` (once per match even on a double KO) then, 900 ms later, `core_victory_sting` or `core_draw_sting` (same-tick double KO = draw) | **confirmed frames only** (section 6) |
| ProjectileSpawned | Value = owner, Value2 = `ChibiProjKind` | section 4 | |
| ProjectileGone | same | section 4 | |
| DashStarted | Value = dir | owner dash: Kaia `kaia_dash`, Lyra `lyra_shim`, Taro `taro_tchk`, Bruna `core_dash x0.85 + bruna_armor`, Ren `core_dash` | |
| CinematicStarted | Index = ult user | `core_ult_cinematic` + user's ult layer: `kaia_ult`, `bruna_smash`, `ren_draw`, `lyra_ult`, `taro_zaa` | starts the cinematic duck (section 5.3) |
| ProgramFrame | Value = slot, Value2 = move frame | section 3.3 cadence table only; never 1:1 | a multi-step diff can deliver up to 120 at once: play at most the **latest** qualifying frame per batch |
| MovePhaseChanged | Value = new phase, Value2 = slot | section 3.3 | |
| MinionSpawned | Value = owner | `lyra_summon` | Max 3 voices; 3 minions on one tick = one sound + 2 at -6 dB 40/80 ms later |
| MinionGone | | `core_poof` | |
| CloudSpawned | | `lyra_fwoom x0.8` | |
| CloudGone | | `core_poof x0.9` | |
| CloudStrike | | `lyra_thunder` | |
| PoseSuperJump | | `core_super_jump` | |
| UltPromptChanged | Value = `ChibiUltPrompt` | Preparing: `core_ult_prompt`; Detected: nothing (cinematic follows); Failed: `core_ult_fail`; Hidden: nothing | local player only (Index = local fighter); the opponent's prompt is silent |
| Revived | training | `core_revive` | |

### 2.1 Hit composition (Damaged)

`hit = body + weapon`, both started on the same frame.

| Part | Rule |
|---|---|
| Body | `core_hit_heavy` if Value >= **HeavyHitHp** (tuning knob, default 8, to be tuned against `ChibiCombatStats` damage) else `core_hit_light`. Add `core_hit_crit` on a crit (2.2). |
| Weapon | attacker's impact: Kaia `kaia_shk`, Bruna `bruna_thud` (light) / `bruna_bam` (heavy), Ren `ren_zing`, Lyra `lyra_ting`, Taro `taro_shrak` (heavy kill moves: `taro_crash`). |
| Projectile hit | if the same diff batch holds a ProjectileGone owned by the attacker, use section 4's "hit" column as the weapon layer and `core_proj_hit` as the body instead. |
| Blocked | defender Shielding in the previous state and still shielding: `core_block` only (Bruna as attacker: `bruna_clang -4` instead), no weapon layer. |

### 2.2 Derived events (no `ChibiSimEventKind` exists; the view reads them from prev/cur state)

These are view-side reads only (allowed by `.claude/rules/netcode-core.md`); flagged for the tools-programmer, no sim change requested.

| Sound event | Detection (prev -> cur, per fighter) | Sound |
|---|---|---|
| Jump | `Grounded` true -> false and the fighter moved up (not a walk-off) | `core_jump` |
| Double jump | `AirJumps` decreased | `core_double_jump` |
| Land | `Grounded` false -> true | `core_land` (Bruna: `+ bruna_armor`); suppressed if a slam/dive landing sound plays this frame |
| Guard break | `GuardBrokenTicks` rose to `GuardBrokenTicks` const (180) | `core_guard_break` (+ `bruna_krash` if the attacker is Bruna); replaces the block sound on that frame |
| Crit | `HitstopRealTicks` rose to `HitstopCrit` (5) on a frame with a Damaged event | add `core_hit_crit` |
| Ult ready | `Ult` crossed up to `UltMax` (1000) | `core_ult_ready` (local player full volume, opponent -8 dB) |
| Countdown | intro number shown by the HUD changes (3/2/1) | `core_countdown_tick` at pitch 1.0 / 1.06 / 1.12 |
| GO | first real tick with input (`IntroRealTicks` reaches 0) | `core_countdown_go` |

---

## 3. Moves

### 3.1 By move animation kind (sheet moves, from the moveset data table)

The systems-designer's moveset table (`design/gdd/movesets/*`, Wave 1) is expected to tag each move with an animation kind.
**Request to systems-designer:** add `sfx_start` and `sfx_hit` override columns; when present they win over this table.
Heavy (H) = smashes, charged moves, jab finisher (combo index 2), heavy slot; Light (L) = everything else.

| Kind | Kaia | Bruna | Ren | Lyra | Taro |
|---|---|---|---|---|---|
| jab | `kaia_swish` (finisher `kaia_shrak`) | `bruna_whoosh` (finisher `bruna_swoosh`) | `ren_thwip` | `lyra_ping` (finisher `lyra_flash`) | `taro_shk` (finisher `taro_swoon`) |
| swing | L `kaia_swish` / H `kaia_shrak` | L `bruna_whoosh` / H `bruna_swoosh` | L `ren_thwip` / H `ren_whoosh` | L `lyra_swish` / H `lyra_flash` | L `taro_swoon` / H `taro_heavy_swing` |
| upswing | `kaia_shing` (H `kaia_vwoom`) | L `bruna_whoosh` / H `bruna_swoosh` | `ren_whoosh` | L `lyra_swish` / H `lyra_flash` | L `taro_swoon` / H `taro_heavy_swing` |
| lowsweep | `kaia_skrrt` | `bruna_whoosh x0.9` | `ren_thwip x0.9` | `lyra_swish x0.9` | `taro_swoon x0.95` |
| thrust | `kaia_whoosh` | `bruna_rush` | `ren_thwip` | `lyra_swish` | L `taro_shk` / H `taro_zaa` |
| slam | `kaia_shrak` | `bruna_swoosh` | `ren_whoosh` | `lyra_flash` | `taro_heavy_swing` |
| spin | `kaia_vwoom` | `bruna_whoosh` (cadence 3.3) | `ren_whoosh` | `lyra_swish` | `taro_whrr` |
| shoot | `kaia_whoosh` | `bruna_rumble` | `ren_twang` (+ `ren_draw` while held/charging) | `lyra_pew` | `taro_wind_lance` |
| cast | `kaia_tshing` | `bruna_rumble` | `ren_pfft` | `lyra_fwoom` | `taro_wind_lance` |
| dive | `kaia_whoosh x0.9` | `bruna_swoosh` | `ren_whoosh x0.9` | `lyra_fwoom` | `taro_zaa x0.9` |
| uppercut | `kaia_vwoom` | `bruna_vroom` | `ren_whoosh x1.1` | `lyra_flash` | `taro_clang` |
| guard | `kaia_tshing` | `bruna_guard` | `ren_pfft` | `lyra_vmm` | `taro_ting` |
| rush | `kaia_whoosh` | `bruna_rush` | `ren_whoosh` | `lyra_shim` | `taro_zaa` |
| launch | `kaia_vwoom` | `bruna_vroom` | `ren_zip` | `lyra_fwoom` | `taro_tchk` + `taro_whrr -6` |

**Contact / landing layer** for slam and dive (played when the move lands: `Grounded` rises while the move is active, or on its first hit): Kaia `kaia_thrak`, Bruna `bruna_thoom` (down smash / Seismic Sweep: `bruna_kra_thud`), Ren `ren_thump`, Lyra `lyra_boom`, Taro `taro_boom`.

Signature overrides (sheet word wins over the kind default): Kaia Charged Cross Cut / Blade Reversal `kaia_clang`, Down Special Blade Trap `kaia_tshing`; Bruna forward smash `bruna_smash`, Earth Shock `bruna_rumble`, Rocket Hammer / Iron Uppercut `bruna_vroom`; Ren Trap Mine trigger `ren_trap_boom`, Grapple Arrow `ren_zip`, Ricochet bounce `ren_ping`; Lyra Prism Reflection / Glyph Zone `lyra_vmm`, Blink Ascension `lyra_shim`, Comet Dive / Starfall land `lyra_boom`; Taro Jade Stance / Jade Counter `taro_ting`, Staff Vault `taro_tchk + taro_whrr`, Skyhook `taro_clang`, Piercing Dash `taro_zaa`.

### 3.2 Existing kit (MoveStarted slot; CH-2 keeps these on their current inputs)

| Slot | Kaia (Melee) | Bruna (Heavy) | Ren (Ranged) | Lyra (Magic) | Taro (Polearm) |
|---|---|---|---|---|---|
| 0 light combo | jab row of 3.1 | jab row | jab row | jab row | jab row |
| 1 heavy | `kaia_shrak` | `bruna_swoosh` | `ren_whoosh` | `lyra_flash` | `taro_heavy_swing` |
| 2 skill 1 | Dash & Thrust: `kaia_whoosh` | Grab (program): `bruna_armor` | Backstep Shot (program): `core_dash` + `ren_pfft` | Homing Fireball: `lyra_fwoom` | Leap Slam (program): `taro_tchk` |
| 3 skill 2 | Parry stance: `kaia_tshing` | Tornado Spin (program): cadence 3.3 | Bowstring Choke: `ren_zip` | Summon Minions: `lyra_fwoom` | Spin Guard (program): `taro_whrr_loop` |
| 4 skill 3 | Thrust Barrage (program): cadence 3.3 | - | Arrow Barrage (program): `ren_draw` | Thunder Cloud: `lyra_fwoom x0.8` | Throw & Recall: `taro_heavy_swing` |
| 5 ult | Sword Aura: `kaia_ult` (also via CinematicStarted, dedupe) | ULT Smash (program): see 3.3 | Thousand Arrows (program): `ren_draw` | Fire Ring: `lyra_ult` | Polearm Kick (program): see 3.3 |
| 6 pose fireball | `core_proj_spawn` (the projectile row in section 4 carries the sound) | same | same | same | same |

### 3.3 Program moves: cadence and phases

| Move | ProgramFrame rule | MovePhaseChanged |
|---|---|---|
| Kaia Thrust Barrage | `kaia_swish` every 6th frame (Value2 % 6 == 0), weapon-layer attenuation of 5.2 applies | - |
| Bruna Grab | - | connected: `bruna_thud` + `bruna_armor` |
| Bruna Tornado Spin | `bruna_whoosh` every 12th frame, alternating pitch 1.0 / 1.06 | - |
| Bruna ULT Smash | - | slammed: `bruna_smash` + `bruna_kra_thud -4` |
| Ren Backstep Shot / Arrow Barrage / Thousand Arrows | none (projectile spawns carry the sound) | - |
| Taro Leap Slam | - | landed: `taro_boom` |
| Taro Spin Guard | `taro_whrr_loop` loops while the move is active (On); stop with 80 ms fade at move end | - |
| Taro Polearm Kick | - | lerp: `taro_zaa`; hold: `taro_shrak`; release: `taro_crash` |

---

## 4. Projectiles (`ChibiProjKind`)

"Expire" = ProjectileGone with no Damaged event for the owner's opponent in the same batch.

| Kind | Spawn | Hit (weapon layer, 2.1) | Expire |
|---|---|---|---|
| Arrow | `ren_whip` | `ren_zing` | `core_poof -6` |
| ArrowBig | `ren_bang` (+ `ren_whip x0.85`) | `ren_zing` + `core_hit_heavy` | `core_poof` |
| Bolt | `lyra_pew` | `lyra_ting` | `core_poof -6` |
| BoltBig | `lyra_pew x0.8` | `lyra_ting x0.85` | `core_poof` |
| AuraWave | `kaia_ult -6 x1.2` | `kaia_shk` | `core_poof -6` |
| Fireball | `lyra_fireball` | `lyra_boom` | `lyra_boom -6` (it always bursts) |
| Spear | `taro_spear_throw` | `taro_shrak` | `taro_tchk` (recall catch) |
| SmashWave | `bruna_rumble` | `bruna_bam` | `core_poof x0.8` |
| VolleyLead | `ren_bang x1.1` | `ren_zing` | `ren_volley` (it splits) |
| VolleyArrow | `ren_volley` (max 3 voices, 60 ms CD) | `ren_zing -3` | silent |
| PoseFireball | `lyra_fireball x1.15` (any owner) | `lyra_boom -3` | `lyra_boom -9` |
| any unmapped kind | `core_proj_spawn` | `core_proj_hit` | `core_poof` |

---

## 5. Mixing rules

### 5.1 Buses and voice budget

One pool of **24** AudioSources. Bus caps are hard; when a bus is full, steal the lowest priority voice in that bus (oldest on ties). Never steal a voice with Pri >= 90.

| Bus | Fader | Max voices | Contents |
|---|---|---|---|
| STG | 0 dB | 3 | countdown, KO, ring-out, stingers, ult cinematic |
| CMB | 0 dB | 12 | swings, impacts, parry, block, guard break |
| PRJ | -2 dB | 6 | projectile spawn / expire, minions, clouds |
| MOV | -4 dB | 4 | jump, land, dash, foley |
| UI | -3 dB | 3 | menus |
| MUS | -8 dB | (owned by audio-director, not yet existing) | ducking target only |

Per-id limits and per-fighter cooldowns: the Max and CD columns of `sfx-spec.md`. A request inside the CD is dropped, not queued.

### 5.2 Multi-hit (anti machine-gun)

1. **Per defender hit window 60 ms:** a second Damaged inside it plays nothing, unless it is heavier (light to heavy, or crit), which replaces the body layer.
2. **Consecutive hits** on the same defender within 300 ms of each other (flurries, Thrust Barrage, Tornado Spin, volleys): weapon layer -3 dB per hit, floor -9 dB; body layer drops to `core_hit_light -6`. The counter resets after 300 ms without a hit. The final hit of a string at or above HeavyHitHp plays at full level.
3. Volley / barrage spawns: at most 1 spawn sound per 60 ms per owner.

### 5.3 Ducking

| Trigger | Ducked | Amount | Attack / hold / release | Exempt |
|---|---|---|---|---|
| KO (`core_ko`) | CMB, PRJ, MOV | -12 dB | 20 ms / 1400 ms / 600 ms | STG |
| KO | UI, MUS | -6 / -8 dB | same | |
| Ult cinematic (CinematicStarted; 48 real ticks = 0.8 s) | CMB, PRJ, MOV | -9 dB | 30 ms / until CinematicRealTicks = 0 / 250 ms | `core_ult_cinematic` and the user's ult layer |
| Ult cinematic | MUS | -6 dB | same | |
| Guard break, crit | MOV, PRJ | -4 dB | 10 ms / 100 ms / 150 ms | |
| Ring-out | MOV | -6 dB | 10 ms / 300 ms / 300 ms | |

Duck amounts do not stack: the deepest active duck per bus wins.

### 5.4 Menu actions (bus UI)

| Action | Sound |
|---|---|
| Move cursor, change tab, scroll list | `core_ui_navigate` (35 ms CD) |
| Slider / value step | `core_ui_navigate x1.1` (80 ms CD) |
| Confirm, open panel, pause open | `core_ui_confirm` |
| Back, close panel, pause close | `core_ui_back` |
| Disabled button, invalid input, join failed | `core_ui_error` |
| Character locked in, room joined, match found | `core_ui_lockin` |

---

## 6. Rollback and event dedupe (required)

`ChibiSimEvents.Diff` is called with (shown state, now state). After a rollback the view diffs the pre-rollback shown state
against the re-simulated state, so **corrections can re-emit an event that already played** (shifted by a frame or two),
and a multi-step diff attributes every change to the newest frame. The player must dedupe:

1. **Key** = `(Kind, Index, Value, Value2, simFrame)`, simFrame = the `Frame` of the diff's `cur` state (for ProgramFrame, use the move frame in Value2 plus the move start frame).
2. Keep a ring buffer of the last 64 played keys. Drop a new event whose `(Kind, Index, Value, Value2)` matches a stored key with `|simFrame - stored| <= 3` (rollback drift tolerance; tuning knob `DedupeFrames`).
3. **Confirmed-only stingers:** `core_ko`, `core_victory_sting`, `core_draw_sting` play only once the KO frame is confirmed by both peers' inputs (no rollback can undo it). The hitstop and KO slow-down hide the delay. Everything else plays immediately (a mispredicted swing that never happened is an accepted cost; it cannot be un-played).
4. A sound already playing is never cut because a rollback removed its cause, except `taro_whrr_loop` / `lyra_vmm_loop`, which stop (80 ms fade) when the re-simulated state no longer has the move or shield active.
5. Offline sessions never roll back; the same code path must still work with no rollbacks.

---

## 7. Tuning knobs (for data, not code constants)

| Knob | Default | Purpose |
|---|---|---|
| HeavyHitHp | 8 | light / heavy body threshold (whole HP) |
| HitWindowMs | 60 | per-defender multi-hit window |
| StringResetMs | 300 | consecutive-hit attenuation reset |
| StringStepDb / StringFloorDb | -3 / -9 | flurry attenuation |
| DedupeFrames | 3 | rollback dedupe tolerance |
| PanWidth | 0.5 | stereo spread of fighter sounds |
| Bus faders and duck amounts | section 5 | mix |

---

## 8. Stages, hazards and the stocks loop (rules v20, tools-programmer, 2026-11-11)

Sounds: `sfx-spec.md` section 8. Code: `ChibiStageSfx` and `ChibiSfxRouter.StocksLoop` (`Netcode/View/ChibiAudioRules.cs`),
`ChibiAudio.PlayAmbience / StopAmbience / StageHazard`, calls in `Game/StageDesign.cs`. Tests: `ChibiAudioTests` (stocks loop,
stage audio). Everything reads the sim and never writes it.

### 8.1 Ambience beds (bus AMB)

| When | Call | Sound |
|---|---|---|
| `ChibiDesignStage.Build(stage)` | `ChibiAudio.PlayAmbience(stage)` (keeps the token) | `stage_mountain_amb` / `stage_plain_amb` / `stage_volcano_amb` / `stage_birdlair_amb` / `stage_ocean_amb` (stages 3..7); Classic stages 0..2 have none |
| the stage view is destroyed | `ChibiAudio.StopAmbience(token)` | fade out 1 s, only if no newer stage started its own bed |

Beds play on two dedicated looping AudioSources (not the 24-voice pool, never stolen): fade in 1.5 s, equal-power crossfade
between stages, random start offset, bus fader -2 dB. Same stage again (rematch): the running bed continues. Ducks: KO -8 dB,
stock KO -6 dB, ult cinematic -6 dB.

### 8.2 Hazards (bus PRJ), on phase changes only

`ChibiAudio.StageHazard(tracker, state)` is called at the top of `SyncFire`, `SyncBird` and `SyncWhale` every rendered frame;
the per-stage `ChibiStageSfx` tracker plays each **(HazCount, phase)** once:

1. A phase plays the first time its bit is seen for the newest event number (`HazCount`).
2. Rollback re-showing the same phase, or an older `HazCount`, plays nothing. (Hazards use their own RNG stream and game steps,
   not inputs, so a rollback never moves them; the dedupe is a safety net.)
3. A phase first seen more than `LateTicks` = 20 game ticks in (stage built mid-event, long catch-up) is marked, not played.

| Stage | Phase (HazPhase) | Sound | Pan |
|---|---|---|---|
| Volcano | Telegraph (75 ticks) | `stage_volcano_burst_warn`, once per active lane (HazArg 0 left, 1 right, 2 both) | lane X |
| Volcano | Erupt | `stage_volcano_burst`, per active lane | lane X |
| Bird Lair | Telegraph (90) | `stage_birdlair_screech` | start side (-dir) |
| Bird Lair | Swipe start | `stage_birdlair_gust` | a quarter toward dir |
| Bird Lair | Swipe tick 30 (body pass, pseudo phase 7) | `stage_birdlair_pass` | **sweeps** -dir to +dir over 80 ticks (`SfxRequest.PanSweepSec`) |
| Ocean | Telegraph (120) | `stage_ocean_whale_call` | centre |
| Ocean | Descend | `stage_ocean_whale_descend` | centre |
| Ocean | Hold | (none: the descend swell is still ringing) | |
| Ocean | Rise | `stage_ocean_whale_rise` | centre |

### 8.3 Percent and stocks loop (derived in `ChibiSfxRouter.Step`, prev vs cur)

| Condition (fighter i) | Sound | Dedupe key |
|---|---|---|
| `KosTaken` rose and stocks remain (or training) | `core_ko_blast` at the KO point (X stays at the crossing), duck StockKo: CMB/PRJ/MOV -8 dB, AMB -6 dB, 10 / 600 / 400 ms | (KO, i, KosTaken), no frame drift |
| ... and `Stocks` is now 1 (not training) | `core_last_stock`, 0.7 s later (`LastStockDelaySec`) | same |
| ... and `Stocks` is now 0 | nothing here: the match ends that tick and `EmitEnd` plays **`core_final_stock`** (replaces `core_ko`, Ko duck) then the victory / draw sting 0.9 s later. Online it waits for confirmation (section 6.3), so a rolled-back final KO is never announced | |
| `HoverTicks` rose from 0 (respawn platform) | `core_respawn_chime` | (respawn, i, KosTaken) |
| `PctM` rose to >= 60 % (`HeavyLaunchPctM`) and the launch speed sqrt(Vx²+Vy²) reached KB 100 x 1530 sub (`HeavyLaunchSub`) with a jump of at least half that | `core_launch_heavy`, pitch 1.0 down to 0.9 as the speed doubles | (launch, i, PctM) +-3 frames |
| `RingOut` event | nothing in v20 (KO and respawn come from the rows above, so they can never double) | |

`core_ko`, `core_ringout`, `core_respawn` stay in the library for direct `ChibiAudio.Play` callers.

Tuning knobs added: `HeavyLaunchSub` (153 000), `HeavyLaunchPctM` (60 000), `LastStockDelaySec` (0.7), `HazardLateTicks` (20),
AMB fader (-2 dB), bed fades (1.5 s in, 1 s out).
