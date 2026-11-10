# Chibi Clash: SFX Specification (procedural synthesis)

Status: DRAFT v1 (sound-designer, 2026-11-09). Waiting for audio-director review.
Consumers: tools-programmer (`tools/audio/*` generator), `Netcode/View/ChibiAudio.cs` (playback).
Companion: `design/audio/sfx-events.md` maps game events to these ids and sets the mixing rules.

> **Governance notes (flagged, not resolved here)**
> - The project has no audio style guide and no naming convention yet. Both belong to audio-director.
>   This spec uses `<owner>_<word>` snake_case ids (`core_*` shared, `<character>_*` per character) and
>   *proposes* a combat reverb-tail cap of 400 ms (stingers 1.5 s). audio-director should confirm or replace them.
> - The sonic palette per character comes straight from each costume sheet's "SFX STYLE (NOTES)" panel
>   (`design/art/character-sheets/*-costume.png`). The core palette (section 3) is my proposal.
> - `core_victory_sting` and `core_draw_sting` are short tonal SFX stings, not music. If audio-director
>   commissions composed cues, those replace these ids.

---

## 1. Output format and generator conventions

| Item | Rule |
|---|---|
| Format | WAV, 44 100 Hz, mono, 16-bit PCM, no metadata beyond `fmt`/`data`. |
| Files | `<id>_<nn>.wav`, `nn` = 01..V (V = variant count in the table). Example: `kaia_swish_01.wav` .. `kaia_swish_04.wav`. |
| Determinism | numpy RNG seeded per file with `crc32(id) + nn`. Re-running the generator must produce byte-identical files. |
| Rendering | Render each layer, apply its envelope and gain, sum, then: DC block (HP 20 Hz, 1-pole), `tanh` soft clip at drive 1.2, peak-normalise to the **Peak** column, 2 ms linear fade-in (skip for sounds marked `click-onset`), 6 ms fade-out, trim trailing samples below -70 dBFS. |
| Sample-rate safety | Oscillators other than sine use polyBLEP or 4x oversampling; no partial above 18 kHz. |
| Filter sweeps | RBJ biquads (LP/HP/BP), coefficients recomputed every 32 samples; sweeps are exponential in frequency unless marked `lin`. |
| Runtime variation | Besides the baked variants, the player applies the **Pitch** jitter at playback (`AudioSource.pitch`). Baked variants already apply their own pitch/time offsets (below). |

### 1.1 Recipe notation (used in every table row)

Layers are separated by ` + `. Each layer: `SOURCE [-> FILTER] {ENVELOPE} GAIN [@OFFSET]`.

| Token | Meaning |
|---|---|
| `wn`, `pn`, `bn` | white / pink / brown noise |
| `sin F`, `tri F`, `saw F`, `sq F` | oscillator at F Hz. `sin 800>300/60` = exponential glide 800 to 300 Hz over 60 ms |
| `fm C:R I a>b/T` | 2-op FM: carrier C Hz, modulator ratio R, index from a to b over T ms |
| `bell F` | inharmonic bell bank: sine partials F x {1, 2.76, 5.40, 8.93} at 0/-6/-12/-18 dB, partial decays 100/60/35/20 % of the layer's R |
| `metal F` | bar/plate bank: F x {1, 1.47, 2.09, 2.56, 3.31, 4.23} at 0/-3/-5/-8/-10/-14 dB, random phase, decays 100/80/60/45/35/25 % of R |
| `click F` | 1.5 ms decaying sine burst at F Hz (transient) |
| `wood F` | 2-pole resonator (Q 12) at F Hz excited by a 0.5 ms noise burst (pole knock) |
| `-> bp F Q`, `-> lp F`, `-> hp F` | biquad; `bp 6k>1.5k Q2/120` = centre swept 6 kHz to 1.5 kHz over 120 ms |
| `-> am R d` | tremolo: R Hz, depth d (0..1) |
| `-> dist d` | tanh drive d before the envelope |
| `-> rev T m` | small Schroeder reverb, RT60 T ms, wet mix m (0..1); obey the tail cap |
| `{A/D/S/R}` | ADSR in ms; S = sustain level 0..1, held until R starts at the duration end. `{p A/R}` = percussive: linear attack A ms, exponential decay to -60 dB at R ms |
| `G dB` | layer gain relative to the mix before normalisation |
| `@T` | layer starts T ms after sound start |

### 1.2 Variation column

`Vn: p±x% t±y%` = bake **n** variants; variant k uses pitch factor uniform in ±x % (multiplies every frequency) and time factor
uniform in ±y % (stretches every envelope time, glide and sweep), plus its own noise seed. Variant 01 is always the nominal
recipe (factor 1.0) so it can be auditioned as the reference.

### 1.3 Mix metadata columns

| Column | Meaning |
|---|---|
| Peak | peak-normalisation target, dBFS (the loudness contract; a bus fader sits on top) |
| Bus | `UI`, `CMB` (combat), `MOV` (movement / foley), `PRJ` (projectiles / summons), `STG` (stingers, flow) |
| Pri | voice priority 0..100, higher wins when stealing |
| Max | max simultaneous voices of this id (all fighters together) |
| CD | minimum re-trigger interval for the same id **and the same fighter**, ms |
| Pitch | runtime random pitch, ± % |

Spatial: the game is a single-camera 2D stage. All sounds are 2D (`spatialBlend = 0`), **no distance attenuation**.
Fighter / projectile sounds pan with screen X: `pan = clamp(x / halfStageWidth, -1, 1) * 0.5`. UI, flow and stingers are centred.

---

## 2. Frequency-space plan (anti-masking)

A hit is two layers that must not fight: the **core body** (shared, low-mid weight) and the **character weapon layer** (timbre).

| Layer family | Owns | Kept out of |
|---|---|---|
| Core hit body (`core_hit_*`) | 60 Hz - 2 kHz (thump + crack body) | LP 3 kHz on all body layers |
| Character weapon layer | 800 Hz - 12 kHz (metal, chime, string, wood) | HP 250 Hz on every character impact (Bruna: HP 120 Hz, she owns the sub) |
| Swings / whooshes | 1 - 9 kHz band-passed noise | never below 300 Hz (HP 300) so they do not smear impacts |
| Movement / foley | 150 Hz - 4 kHz, quiet (-14 dBFS and below) | |
| UI | 1 - 6 kHz tonal blips | never used during play except pause menu |
| Stingers / KO | full band | they duck everything else (sfx-events.md section 4) |

---

## 3. Character styles (from the sheets)

| Character | Sheet words | Sheet "SFX STYLE (NOTES)" | Synthesis translation |
|---|---|---|---|
| Core (shared) | - | (proposal) chibi-arcade: bright, punchy, short, slightly cartoony pitch drops | sine thumps with fast downward glides, band-passed noise cracks, tails <= 300 ms |
| Kaia (Melee) | SHK! SWISH! CLINK! | Crisp, punchy, readable. Speed and precision. Clean metal cuts with light, rapid swishes. Snappy, satisfying impacts (avoid heavy, bulky sounds). | high band-pass noise swishes 2-9 kHz, thin `metal` partials at 2.5-4 kHz, HP 250 Hz on everything, nothing longer than 350 ms |
| Bruna (Heavy) | THOOM! CLANG! KRASH! BAM! | Weighty, metallic, physical. Clear, punchy, readable in a fight. Strong low end (heavy "hit" feel) with crisp metallic accents. Distinct layers for slam, impact, guard break, armor movement. | sine/brown-noise sub thumps 40-120 Hz with pitch drops, low `metal` 300-900 Hz plates, crunch = distorted noise; tails up to 600 ms on slams |
| Ren (Ranged) | TWANG! WHIP! PFFT! ZING! | Clean, punchy, readable. Speed and precision. Distinct draw, release and flight cues for rhythm and timing. Bright, crisp techno-fantasy (mechanical + energy mix). | plucked-string `saw` twang 110-220 Hz with LP decay, thin noise whips, mechanical `click`s, energy = FM/square tones 1-3 kHz |
| Lyra (Magic) | FWOOM! TING! SHIM! VMM! | Airy, elegant, readable. Glassy chimes, soft whooshes, magical hums. Clear attack, mobility and defense distinction. Bright and energetic for PvP. | **attack = chimes** (`bell`/FM 1.5-6 kHz), **mobility = shimmer** (high noise + fast sine arps), **defense = hum** (detuned 110-220 Hz with slow beating) |
| Taro (Polearm) | SWOON! SHRAK! WHRR! TCHK! | Clean, punchy, readable. Reach, precision, rhythm. Blend of sharp cuts, whooshes and focused impact. Fast PvP clarity without clutter. | long smooth whooshes (wider, 250-380 ms), `wood` knocks 700-1400 Hz for the pole, jade `bell` accents, short clean blade cuts |

---

## 4. Core set (32 sounds)

### 4.1 UI (bus UI)

| id | Use | Dur | Recipe | Var | Peak | Pri | Max | CD | Pitch |
|---|---|---|---|---|---|---|---|---|---|
| core_ui_navigate | cursor move | 45 | `sq 1320 -> lp 5k {p 1/40} 0 + click 3k {p 0/8} -8` | V3: p±3% t±0% | -14 | 20 | 2 | 35 | 2 |
| core_ui_confirm | accept / button | 160 | `tri 880 {p 2/70} 0 + tri 1320 {p 2/110} -3 @55 + wn -> bp 6k Q3 {p 0/20} -18` | V2: p±2% | -10 | 30 | 2 | 60 | 0 |
| core_ui_back | cancel / back | 140 | `tri 1100>660/90 {p 2/120} 0 + click 2k {p 0/8} -10` | V2: p±2% | -12 | 30 | 2 | 60 | 0 |
| core_ui_error | invalid action | 200 | `sq 220 -> lp 1.8k {p 2/90} 0 + sq 207 -> lp 1.8k {p 2/90} -2 @90` | V1 | -12 | 30 | 1 | 150 | 0 |
| core_ui_lockin | character locked in | 420 | `bell 1760 {p 1/380} 0 + wn -> bp 9k>3k Q1.5/250 {p 5/260} -9 + sin 110>70/120 {p 2/150} -8` | V1 | -6 | 50 | 2 | 150 | 0 |

### 4.2 Match flow (bus STG)

| id | Use | Dur | Recipe | Var | Peak | Pri | Max | CD | Pitch |
|---|---|---|---|---|---|---|---|---|---|
| core_countdown_tick | "3", "2", "1" | 260 | `sq 660 -> lp 4k {p 2/200} 0 + sin 1320 {p 2/150} -10 + click 4k {p 0/6} -6` | V1 (UI raises pitch +0/+6/+12 % for 3/2/1) | -6 | 80 | 1 | 200 | 0 |
| core_countdown_go | "GO!" first input tick | 700 | `saw 330 + saw 495 + saw 660 -> lp 6k>2k/500 {5/0/1/400} 0 (each -4) + wn -> bp 3k>8k Q1/200 {p 10/300} -6 + sin 90>50/150 {p 2/250} -4` | V1 | -2 | 90 | 1 | 1000 | 0 |
| core_ko | KO (HP to zero, match end) | 1400 | `sin 120>35/400 -> dist 2 {p 1/900} 0 + bn -> lp 400 {p 2/1200} -4 + wn -> bp 2.5k>600 Q0.8/600 {p 0/700} -6 + metal 410 {p 0/1100} -12 + rev 1200 0.25` | V2: p±3% | -1 | 100 | 1 | 1000 | 0 |
| core_ringout | blast-off / respawn trigger | 900 | `wn -> bp 1.5k>8k Q2/250 {p 5/300} 0 + sin 800>3200/250 {p 2/280} -8 + sin 70>40/300 -> dist 1.5 {p 1/600} -2 @200 + bn -> lp 300 {p 0/700} -6 @200 + rev 900 0.2` | V2: p±4% | -2 | 95 | 2 | 500 | 0 |
| core_respawn | fighter reappears (same RingOut event, played 90 ms later) | 450 | `sin 523>1046/200 {p 5/350} 0 + sin 784>1568/200 {p 5/350} -4 @40 + wn -> hp 6k {p 10/300} -16` | V1 | -10 | 60 | 2 | 500 | 0 |
| core_victory_sting | winner banner | 1800 | `bell 523 {p 2/900} 0 + bell 659 {p 2/900} -1 @150 + bell 784 {p 2/900} -1 @300 + bell 1046 {p 2/1400} 0 @450 + saw 261+392 -> lp 3k {40/0/1/900} -10 @450 + wn -> bp 7k Q0.7 {p 30/900} -16 @450 (cymbal) + rev 1500 0.3` | V1 | -1 | 100 | 1 | - | 0 |
| core_draw_sting | double KO / draw | 1400 | `bell 523 {p 2/700} 0 + bell 494 {p 2/700} -2 @200 + bell 440 {p 2/1100} -2 @400 + sq 220 -> lp 1.2k {20/0/1/800} -14 @400 + rev 1200 0.3` | V1 | -3 | 100 | 1 | - | 0 |
| core_revive | training revive | 600 | `sin 392>784/300 {p 10/500} 0 + sin 588>1176/300 {p 10/500} -4 + wn -> bp 4k>10k Q2/400 {p 20/400} -12` | V1 | -8 | 60 | 1 | 500 | 0 |

### 4.3 Combat (bus CMB)

| id | Use | Dur | Recipe | Var | Peak | Pri | Max | CD | Pitch |
|---|---|---|---|---|---|---|---|---|---|
| core_hit_light | body of a light hit (dmg below threshold) | 140 | click-onset `sin 260>110/40 {p 0/90} 0 + wn -> bp 1.6k Q1.2 -> lp 3k {p 0/50} -4 + click 1.2k {p 0/5} -6` | V4: p±6% t±8% | -6 | 60 | 4 | 60 | 5 |
| core_hit_heavy | body of a heavy hit | 280 | click-onset `sin 150>45/90 -> dist 1.8 {p 0/220} 0 + wn -> bp 900 Q0.9 -> lp 3k {p 0/120} -3 + bn -> lp 250 {p 0/200} -6 + click 900 {p 0/6} -4` | V4: p±5% t±8% | -3 | 70 | 3 | 80 | 4 |
| core_hit_crit | crit accent (layered ON TOP of light/heavy) | 380 | `sin 2400>1200/60 {p 0/120} -6 + wn -> bp 5k>2k Q3/200 {p 0/250} 0 + metal 1250 {p 0/350} -8 + sin 55 -> dist 2 {p 0/200} -6` | V3: p±4% | -2 | 80 | 2 | 120 | 3 |
| core_block | chip damage into a raised guard | 170 | click-onset `metal 780 -> hp 300 {p 0/150} 0 + wn -> bp 2.2k Q1.5 {p 0/40} -4 + sin 180>120/30 {p 0/60} -10` | V4: p±5% t±6% | -7 | 55 | 3 | 70 | 4 |
| core_guard_break | guard shatters | 650 | click-onset `wn -> dist 3 -> bp 3k>900 Q0.8/300 {p 0/450} 0 + metal 620 {p 0/500} -4 + bell 2100 {p 0/400} -8 (glass) + sin 90>40/200 -> dist 2 {p 0/400} -3 + rev 500 0.2` | V2: p±3% | -1 | 90 | 2 | 300 | 2 |
| core_parry | successful parry (Parried) | 450 | click-onset `bell 2637 {p 0/420} 0 + bell 3951 {p 0/300} -5 + wn -> hp 5k {p 0/60} -8 + sin 1200>2400/80 {p 0/120} -12` | V2: p±2% | -3 | 85 | 2 | 150 | 0 |
| core_shield_up | guard raised | 220 | `sin 220 -> am 18 0.4 {20/40/0.6/120} 0 + sin 330 {20/40/0.6/120} -6 + wn -> bp 2k Q2 {p 10/120} -14` | V2: p±3% | -14 | 30 | 2 | 120 | 3 |

### 4.4 Movement (bus MOV)

| id | Use | Dur | Recipe | Var | Peak | Pri | Max | CD | Pitch |
|---|---|---|---|---|---|---|---|---|---|
| core_jump | ground jump | 160 | `wn -> bp 900>2.5k Q1.5/120 {p 5/130} 0 + sin 180>360/80 {p 2/90} -10` | V3: p±6% t±10% | -15 | 25 | 2 | 80 | 5 |
| core_double_jump | air jump (AirJumps fell) | 200 | `wn -> bp 1.5k>5k Q2/150 {p 5/170} 0 + sin 440>880/120 {p 5/150} -8` | V3: p±5% t±10% | -14 | 25 | 2 | 80 | 5 |
| core_land | landing (Grounded false to true) | 120 | click-onset `bn -> lp 600 {p 0/90} 0 + wn -> bp 1.2k Q1 {p 0/40} -8` | V4: p±8% t±10% | -16 | 20 | 2 | 100 | 6 |
| core_dash | dodge / dash start (DashStarted) | 190 | `wn -> bp 1.2k>4.5k Q1.8/160 {p 8/170} 0 + pn -> hp 4k {p 2/60} -10` | V3: p±6% t±8% | -12 | 35 | 2 | 90 | 5 |
| core_super_jump | Super Jump pose | 500 | `wn -> bp 600>6k Q2/350 {p 20/450} 0 + sin 200>900/350 {p 20/400} -6 + sin 60>40/150 {p 0/200} -6` | V1 | -6 | 60 | 2 | 300 | 0 |

### 4.5 Projectiles and summons (bus PRJ)

| id | Use | Dur | Recipe | Var | Peak | Pri | Max | CD | Pitch |
|---|---|---|---|---|---|---|---|---|---|
| core_proj_spawn | fallback spawn for an unmapped projectile kind | 150 | `wn -> bp 2k>5k Q2/100 {p 3/130} 0 + sin 900>1400/60 {p 2/80} -10` | V3: p±6% | -12 | 40 | 4 | 50 | 5 |
| core_proj_hit | fallback projectile impact body (plays with core_hit_light/heavy rules, see events) | 150 | click-onset `wn -> bp 2.5k Q1 {p 0/90} 0 + sin 400>150/50 {p 0/100} -4` | V3: p±6% | -8 | 55 | 4 | 60 | 5 |
| core_poof | projectile / minion / cloud expires without hitting | 180 | `pn -> bp 1.8k>700 Q1/150 {p 4/160} 0 + sin 700>350/120 {p 2/120} -14` | V3: p±8% | -18 | 15 | 3 | 60 | 6 |

### 4.6 Ultimate (bus STG unless noted)

| id | Use | Dur | Recipe | Var | Peak | Pri | Max | CD | Pitch |
|---|---|---|---|---|---|---|---|---|---|
| core_ult_ready | ult meter reached UltMax | 650 | `bell 1046 {p 2/500} 0 + bell 1568 {p 2/550} -3 @80 + sin 523 -> am 7 0.3 {30/0/1/400} -10 + wn -> hp 7k {p 50/400} -16` | V1 | -6 | 70 | 2 | 1000 | 0 |
| core_ult_prompt | gesture window opened (prompt = Preparing) | 400 | `sin 440 -> am 12 0.5 {40/0/1/250} 0 + sin 660 -> am 12 0.5 {40/0/1/250} -6` | V1 | -12 | 50 | 2 | 300 | 0 |
| core_ult_fail | gesture failed (prompt = Failed) | 350 | `sq 330>220/250 -> lp 1.5k {p 5/300} 0 + wn -> lp 1k {p 5/150} -14` | V1 | -12 | 50 | 2 | 300 | 0 |
| core_ult_cinematic | ult cinematic (0.8 s, CinematicStarted); the user's `<char>_ult` layer plays on top | 1100 | `wn -> bp 300>6k Q1.5/700 {200/0/1/150} 0 (riser) + saw 110>440/700 -> lp 800>5k/700 {200/0/1/150} -8 + sin 70>35/300 -> dist 2 {p 0/350} -2 @750 + wn -> bp 1.5k Q0.7 {p 0/300} -4 @750 + rev 800 0.25` | V1 | -1 | 95 | 1 | 800 | 0 |

---

## 5. Character sets

Every character impact layer has HP 250 Hz unless the row says otherwise. Columns as in section 4; all rows are bus CMB unless the Bus is named in the Use cell.

### 5.1 Kaia (Melee, Twin Curved Swords): 13 sounds. Crisp, light, metal; never bulky.

| id | Role / Use | Dur | Recipe | Var | Peak | Pri | Max | CD | Pitch |
|---|---|---|---|---|---|---|---|---|---|
| kaia_swish | **light swing** ("Swish!"): jab 1-2, tilts, light aerials | 150 | `wn -> bp 2.5k>7k>3.5k Q2/120 {p 15/130} 0 + pn -> hp 6k {p 5/60} -10 + sin 2600>3200/80 {p 2/50} -26` | V4: p±7% t±10% | -10 | 40 | 3 | 45 | 6 |
| kaia_shrak | **heavy swing** ("SHRAK!"): jab 3, smashes, heavy | 260 | `wn -> bp 1.8k>6k>2k Q1.6/220 {p 25/230} 0 + metal 2900 {p 5/200} -14 @60 + pn -> hp 3k {p 10/120} -8` | V3: p±5% t±8% | -7 | 55 | 2 | 80 | 4 |
| kaia_shk | **impact** ("SHK!"): weapon layer on every Kaia hit | 120 | click-onset `metal 3300 {p 0/110} 0 + wn -> bp 6k Q2 {p 0/30} -4 + click 4k {p 0/4} -6` | V4: p±6% t±8% | -6 | 65 | 4 | 50 | 5 |
| kaia_clink | parry / blade clash ("CLINK!"), layered on core_parry | 300 | click-onset `metal 2480 {p 0/280} 0 + bell 3720 {p 0/200} -6 + click 5k {p 0/4} -4` | V3: p±3% | -4 | 85 | 2 | 120 | 2 |
| kaia_whoosh | rush / dash attack / side special ("Whoosh!") | 230 | `wn -> bp 1.2k>5k Q1.4/200 {p 30/200} 0 + pn -> hp 5k {p 20/150} -8` | V3: p±6% t±8% | -9 | 45 | 2 | 70 | 5 |
| kaia_shing | upswing ("Shing!", "Shink!") | 220 | `wn -> bp 2k>8k Q2.5/180 {p 10/190} 0 + sin 2000>4000/150 {p 5/180} -14` | V3: p±5% | -9 | 45 | 2 | 70 | 4 |
| kaia_vwoom | spiral / up smash / up special ("Vwoom!", "Vwoop!") | 320 | `wn -> bp 800>4k Q3/280 -> am 22 0.5 {p 20/300} 0 + sin 300>900/280 {p 20/280} -12` | V2: p±5% | -8 | 50 | 2 | 100 | 3 |
| kaia_skrrt | lowsweep / down tilt slide ("Skrff!", "Skrrt!") | 240 | `wn -> bp 2.5k Q4 -> am 45 0.6 {10/0/1/120} 0 + pn -> lp 1.5k {p 5/200} -6` | V3: p±6% t±10% | -10 | 40 | 2 | 80 | 5 |
| kaia_clang | X-cut / blade reversal ("CLANG!", "KLANG!") | 420 | click-onset `metal 1650 {p 0/400} 0 + metal 2470 {p 0/300} -4 + wn -> bp 3k Q1 {p 0/60} -4 + rev 350 0.15` | V2: p±3% | -3 | 75 | 2 | 150 | 2 |
| kaia_thrak | dive / down air spike ("THRAK!") | 260 | click-onset `wn -> bp 4k>1.2k Q1.5/150 {p 0/200} 0 + metal 1900 {p 0/200} -8 + sin 300>140/60 {p 0/120} -8` | V3: p±4% | -5 | 65 | 2 | 100 | 3 |
| kaia_tshing | special cast: blade trap / mark ("Tshing!"), parry stance | 360 | `bell 3135 {p 2/340} 0 + wn -> bp 7k Q3 {p 2/120} -8 + sin 1568 -> am 30 0.4 {p 5/300} -12` | V2: p±3% | -8 | 55 | 2 | 150 | 2 |
| kaia_dash | Kaia's dodge, air dodge ("Fwsht!"), replaces core_dash | 170 | `wn -> bp 3k>9k Q2/140 {p 4/150} 0 + pn -> hp 7k {p 2/70} -6` | V3: p±6% t±8% | -12 | 35 | 2 | 90 | 6 |
| kaia_ult | Sword Aura ignite (ult layer) and AuraWave spawn (bus PRJ, -6 dB, V3) | 600 | `wn -> bp 1k>7k Q2/400 {50/0/1/250} 0 + metal 1320 {p 50/500} -8 + sin 660 -> am 9 0.3 {50/0/1/300} -12 + rev 400 0.2` | V3: p±4% | -4 | 80 | 3 | 120 | 3 |

### 5.2 Bruna (Heavy, War Hammer): 14 sounds. Weighty low end + crisp metal; separate slam / impact / break / armor layers.

Bruna's impacts use HP 120 Hz (not 250); she owns the sub band. Her swings keep HP 300 like everyone else's.

| id | Role / Use | Dur | Recipe | Var | Peak | Pri | Max | CD | Pitch |
|---|---|---|---|---|---|---|---|---|---|
| bruna_whoosh | **light swing** ("Woom!", "Whoosh!"): jabs, tilts, aerials | 240 | `wn -> bp 500>1.8k>700 Q1.2/200 {p 40/210} 0 + bn -> bp 250 Q1 {p 40/200} -8` | V4: p±6% t±10% | -10 | 40 | 2 | 60 | 5 |
| bruna_swoosh | **heavy swing** wind-up+swing: smashes, heavy | 380 | `wn -> bp 300>1.5k Q1/340 {p 120/250} 0 + bn -> lp 300 {p 120/260} -4 + metal 520 -> am 14 0.5 {p 30/200} -18 (hammer rattle)` | V3: p±5% t±8% | -7 | 55 | 2 | 100 | 4 |
| bruna_thud | **impact light** ("Thud!") | 180 | click-onset `sin 140>60/60 {p 0/150} 0 + bn -> lp 500 {p 0/110} -4 + wood 420 {p 0/40} -8` | V4: p±6% t±8% | -5 | 65 | 3 | 55 | 5 |
| bruna_bam | **impact heavy** ("BAM!"): heavy hit confirm | 320 | click-onset `sin 110>40/120 -> dist 2.2 {p 0/280} 0 + wn -> bp 1.2k Q0.8 -> dist 2 {p 0/120} -3 + metal 640 {p 0/250} -10` | V3: p±4% t±6% | -2 | 80 | 2 | 90 | 3 |
| bruna_clang | metal contact ("CLANG!"): up tilt, up air, her blocks | 380 | click-onset `metal 880 {p 0/360} 0 + metal 1320 {p 0/250} -5 + sin 220>180/80 {p 0/150} -8` | V3: p±4% | -4 | 65 | 2 | 100 | 3 |
| bruna_thoom | deep slam ("THOOM!"): down air, landing of slams, Meteor Anvil | 600 | click-onset `sin 80>32/250 -> dist 1.5 {p 0/550} 0 + bn -> lp 200 {p 0/500} -2 + wn -> bp 600>200 Q0.7/300 {p 0/300} -8 + rev 500 0.15` | V2: p±4% | -2 | 80 | 2 | 150 | 3 |
| bruna_kra_thud | down smash dual shockwave ("KRA-THUD!"), Seismic Sweep | 650 | click-onset `wn -> dist 3 -> bp 2.5k>800 Q1/200 {p 0/200} 0 + sin 90>35/250 -> dist 2 {p 0/550} -1 @90 + bn -> lp 250 {p 0/550} -3 @90 + wn -> bp 500 Q0.8 -> am 30 0.5 {p 0/400} -10 @120 (debris)` | V2: p±3% | -1 | 85 | 2 | 200 | 2 |
| bruna_krash | guard break / destruction ("KRASH!"), layered on core_guard_break when Bruna breaks | 550 | click-onset `wn -> dist 4 -> bp 3.5k>1k Q0.7/300 {p 0/450} 0 + metal 470 {p 0/500} -4 + bell 1900 {p 0/300} -10 + sin 70 {p 0/300} -6` | V2: p±3% | -1 | 90 | 1 | 300 | 2 |
| bruna_smash | "SMASH!": forward smash, ULT Smash, SmashWave spawn (bus PRJ for the wave) | 700 | click-onset `sin 100>30/300 -> dist 2.5 {p 0/650} 0 + wn -> bp 1k>300 Q0.6/400 {p 0/500} -2 + metal 410 {p 0/600} -8 + rev 600 0.2` | V2: p±3% | -1 | 85 | 2 | 200 | 2 |
| bruna_rumble | special cast: Earth Shock ground wave ("Rumble!") | 500 | `bn -> lp 300 -> am 16 0.6 {30/0/1/250} 0 + sin 55 -> am 16 0.5 {30/0/1/250} -4 + wn -> bp 700 Q1 {p 30/400} -10` | V2: p±5% | -4 | 60 | 2 | 150 | 3 |
| bruna_rush | rush / Hammer Rush / dash attack ("Charge!", "Rush!") | 420 | `wn -> bp 400>2.5k Q1/350 {p 60/350} 0 + saw 90>140/350 -> lp 600 {p 60/350} -8 + metal 700 -> am 20 0.5 {p 10/300} -16` | V3: p±5% t±6% | -6 | 55 | 2 | 120 | 4 |
| bruna_guard | guard: Fortress Stance / Fortress Guard armor ("Guard!") | 450 | `metal 560 {p 0/300} 0 + sin 140 -> am 6 0.3 {40/0/1/300} -6 + sin 280 {40/0/1/300} -12 + click 1.5k {p 0/4} -6` | V2: p±3% | -6 | 55 | 2 | 200 | 2 |
| bruna_vroom | launch / Rocket Hammer / Iron Uppercut ("Vroom!", "Launch!", "Uppercut!") | 480 | `wn -> bp 400>3k Q1.2/350 -> dist 1.5 {p 20/420} 0 + saw 70>210/350 -> lp 900 {p 20/420} -6 + sin 50 {p 0/200} -6` | V3: p±4% | -4 | 65 | 2 | 150 | 3 |
| bruna_armor | foley: armor clink (grab, heavy landings, idle step accents; bus MOV) | 160 | click-onset `metal 1180 {p 0/140} 0 + metal 1760 {p 0/90} -6 @25 + bn -> lp 400 {p 0/60} -10` | V4: p±8% t±10% | -16 | 20 | 2 | 120 | 6 |

### 5.3 Ren (Ranged, Mechanized Recurve Bow): 13 sounds. Draw / release / flight / impact are four distinct cues.

| id | Role / Use | Dur | Recipe | Var | Peak | Pri | Max | CD | Pitch |
|---|---|---|---|---|---|---|---|---|---|
| ren_thwip | **light swing** (bow melee "Thwip!"): jabs, tilts | 130 | `wn -> bp 3k>7k Q2.5/100 {p 8/110} 0 + saw 220 -> lp 1.2k {p 0/60} -14` | V4: p±7% t±10% | -11 | 40 | 3 | 45 | 6 |
| ren_whoosh | **heavy swing** ("Swoosh!", "Whoosh!"): melee smashes, aerials | 230 | `wn -> bp 1.5k>6k>2.5k Q1.6/200 {p 20/210} 0 + pn -> hp 5k {p 10/120} -8` | V3: p±6% t±8% | -9 | 50 | 2 | 70 | 5 |
| ren_twang | **release** ("TWANG!"): every shoot, layered with ren_whip | 300 | click-onset `saw 165 -> lp 4k>600/200 {p 0/280} 0 + saw 166.5 -> lp 4k>600/200 {p 0/260} -3 + click 2.5k {p 0/4} -4 + sin 82 {p 0/120} -10` | V4: p±5% t±6% | -7 | 60 | 3 | 50 | 4 |
| ren_whip | arrow flight ("WHIP!"), on Arrow / VolleyArrow spawn (bus PRJ) | 180 | `wn -> bp 6k>2.5k Q3/170 {p 5/170} 0 + sin 3500>2200/150 {p 5/150} -18` | V4: p±6% t±10% | -12 | 45 | 4 | 40 | 6 |
| ren_draw | bow draw / charge (Focus Shot hold, charged moves) | 500 | `saw 90 -> lp 400 -> am 35 0.6 {80/0/1/120} -6 (creak) + fm 600:2 I0>4/450 -> lp 4k {300/0/1/120} 0 (energy rise) + click 1.8k {p 0/4} -4 @0` | V2: p±3% | -10 | 45 | 2 | 200 | 2 |
| ren_pfft | module load / switch / trap place ("PFFT!", "Click"): special cast | 140 | click-onset `click 2.2k {p 0/4} 0 + click 3.1k {p 0/4} -2 @35 + wn -> bp 1.8k Q1.2 {p 2/80} -6 @35` | V3: p±5% | -10 | 45 | 2 | 80 | 4 |
| ren_zing | **impact** ("ZING!"): weapon layer on every Ren hit (melee or arrow) | 220 | click-onset `fm 2200:1.41 I3>0/150 {p 0/200} 0 + wn -> hp 5k {p 0/30} -6 + click 4k {p 0/4} -6` | V4: p±5% t±6% | -6 | 65 | 4 | 50 | 4 |
| ren_bang | charged / power shot ("BANG!", "Veeew!"): ArrowBig, forward smash | 480 | click-onset `wn -> bp 2k>600 Q0.8/250 -> dist 2 {p 0/250} 0 + fm 900:2 I5>0/350 -> lp 6k {p 0/400} -4 + sin 120>50/150 {p 0/250} -4 + rev 300 0.15` | V2: p±3% | -3 | 75 | 2 | 150 | 2 |
| ren_trap_boom | Trap Mine trigger ("Boom!") | 450 | click-onset `click 3k {p 0/4} 0 + wn -> bp 1.5k>400 Q0.7/300 -> dist 2 {p 0/380} 0 @60 + sin 100>40/200 {p 0/350} -3 @60` | V2: p±4% | -2 | 75 | 2 | 150 | 3 |
| ren_zip | grapple / rope pull / Bowstring Choke ("Zip!") | 260 | `wn -> bp 2k>7k Q4/220 {p 5/240} 0 + saw 300>1200/200 -> lp 3k {p 5/220} -12` | V3: p±5% | -10 | 45 | 2 | 100 | 4 |
| ren_volley | volley tick ("Fsh!"): Arrow Barrage, Skyward Volley, Thousand Arrows splits (bus PRJ) | 90 | `wn -> bp 5k>2.5k Q2.5/80 {p 2/85} 0 + click 2.8k {p 0/3} -10` | V4: p±8% t±10% | -14 | 35 | 3 | 60 | 8 |
| ren_ping | ricochet / bounce ("Ping!"), also arrow hits terrain | 220 | click-onset `bell 3400 {p 0/200} 0 + click 5k {p 0/3} -6` | V3: p±6% | -10 | 45 | 2 | 60 | 5 |
| ren_thump | slam-type moves: down smash, down air, Falling Pin landing ("THUMP!", "Thud!", "Impact!") | 260 | click-onset `sin 160>60/80 {p 0/220} 0 + bn -> lp 500 {p 0/150} -4 + wn -> bp 2k Q1 {p 0/40} -8` | V3: p±5% | -5 | 60 | 2 | 100 | 4 |

### 5.4 Lyra (Magic, Ring Focus & Casting Rod): 13 sounds. Attack = chime, mobility = shimmer, defense = hum.

| id | Role / Use | Dur | Recipe | Var | Peak | Pri | Max | CD | Pitch |
|---|---|---|---|---|---|---|---|---|---|
| lyra_ping | **light swing** (jab "Ping!"): small star spark | 140 | `bell 2793 {p 2/130} 0 + wn -> bp 5k>8k Q2/80 {p 5/90} -10` | V4: p±5% t±8% | -12 | 40 | 3 | 45 | 5 |
| lyra_swish | **light arc** ("Swish!", "Fwish!", "Whirl!"): tilts, aerials | 200 | `pn -> bp 1.5k>5k Q1.3/170 {p 15/180} 0 + sin 1760>2637/150 -> am 25 0.4 {p 10/170} -16` | V4: p±6% t±10% | -11 | 40 | 3 | 55 | 5 |
| lyra_flash | **heavy swing** ("Flash!", "Bam!", "Shiing!"): jab 3, smashes, heavy | 380 | `pn -> bp 800>6k Q1/300 {p 20/320} 0 + bell 1568 {p 20/350} -4 + bell 2349 {p 20/300} -8 @40 + sin 80>50/150 {p 10/200} -10` | V3: p±4% t±6% | -6 | 55 | 2 | 100 | 3 |
| lyra_ting | **impact** ("TING!"): weapon layer on every Lyra hit, also lock-on | 260 | click-onset `fm 2093:3.5 I4>0.5/200 {p 0/240} 0 + bell 4186 {p 0/150} -8 + wn -> hp 6k {p 0/25} -8` | V4: p±5% t±6% | -6 | 65 | 4 | 50 | 4 |
| lyra_fwoom | **special cast** ("FWOOM!"): generic cast start, glyph / Spell Lance / Astral Lift | 340 | `pn -> bp 400>2.5k Q0.9/250 {p 40/300} 0 + sin 330>660/250 -> am 8 0.3 {p 40/300} -10 + wn -> hp 8k {p 20/200} -18` | V3: p±5% t±8% | -8 | 50 | 2 | 100 | 4 |
| lyra_shim | **mobility** ("SHIM!"): Lyra's dash / dodge / Blink, replaces core_dash | 300 | `wn -> bp 3k>9k Q2/250 {p 10/280} 0 + sin 2637 {p 0/60} -14 + sin 3136 {p 0/60} -14 @60 + sin 3951 {p 0/60} -14 @120 + sin 5274 {p 0/80} -16 @180` | V3: p±4% t±8% | -11 | 40 | 2 | 90 | 4 |
| lyra_vmm | **defense** ("VMM!"): shield up, Prism Reflection, Glyph Zone, charge; loop-capable | 700 | `sin 147 -> am 4 0.3 {60/0/1/200} 0 + sin 148.5 {60/0/1/200} -2 + sin 294 {60/0/1/200} -10 + fm 588:1 I1 -> lp 2k {60/0/1/200} -16`. Also export `lyra_vmm_loop.wav`: the sustain only, 1000 ms, zero-crossing aligned for seamless looping | V2: p±2% | -10 | 45 | 2 | 200 | 0 |
| lyra_pew | Bolt / BoltBig / arcane shot spawn ("Pew!"), bus PRJ | 180 | `sq 1800>600/120 -> lp 5k {p 2/160} 0 + bell 2637 {p 2/120} -8 + wn -> hp 6k {p 0/40} -14` | V4: p±6% t±8% | -10 | 45 | 4 | 50 | 5 |
| lyra_fireball | Homing Fireball / PoseFireball spawn (fire whoosh), bus PRJ | 450 | `pn -> bp 400>1.6k Q0.8/300 -> am 28 0.3 {30/0/1/250} 0 + bn -> lp 600 {30/0/1/250} -6 + sin 220>300/300 {p 30/350} -14` | V3: p±5% | -8 | 50 | 3 | 100 | 4 |
| lyra_boom | magic explosion ("Boom!"): fireball hit, Comet Dive, down smash, Starfall | 480 | click-onset `wn -> bp 1.8k>500 Q0.7/300 -> dist 1.8 {p 0/400} 0 + sin 110>45/200 {p 0/350} -3 + bell 1760 {p 0/350} -8 + rev 400 0.2` | V3: p±4% | -3 | 75 | 2 | 120 | 3 |
| lyra_summon | Summon Minions (MinionSpawned), bus PRJ | 380 | `sin 523>1046/200 {p 5/300} 0 + sin 784 {p 5/250} -6 @80 + pn -> bp 2k Q1.5 {p 5/200} -12 + click 1.5k {p 0/3} -6 @200 (pop)` | V3: p±6% | -10 | 40 | 3 | 80 | 5 |
| lyra_thunder | Thunder Cloud strike (CloudStrike); cloud forming uses lyra_fwoom at pitch 0.8 | 600 | click-onset `wn -> dist 3 -> hp 1k {p 0/80} 0 + wn -> bp 2k Q1 -> am 60 0.6 {p 0/200} -4 + bn -> lp 250 {p 20/550} -2 + rev 500 0.2` | V3: p±5% | -3 | 70 | 2 | 150 | 3 |
| lyra_ult | Fire Ring ignition (ult layer) | 800 | `bn -> lp 800>2.5k/500 {100/0/1/300} 0 + pn -> bp 1.2k Q0.8 -> am 18 0.3 {100/0/1/300} -4 + bell 1046 {p 100/600} -6 + bell 1568 {p 100/600} -9 + rev 600 0.25` | V1 | -3 | 85 | 1 | 500 | 0 |

### 5.5 Taro (Polearm, Jadeleaf Glaive): 13 sounds. Long clean arcs, wooden pole knocks, jade chimes, rhythm.

| id | Role / Use | Dur | Recipe | Var | Peak | Pri | Max | CD | Pitch |
|---|---|---|---|---|---|---|---|---|---|
| taro_shk | **light thrust** (jab "Shk! Shk!") | 120 | `wn -> bp 3.5k>6k Q3/90 {p 4/100} 0 + wood 1100 {p 0/40} -10` | V4: p±6% t±8% | -11 | 40 | 3 | 45 | 6 |
| taro_swoon | **light/wide swing** ("SWOON!", "Swish!", "Whiff!"): tilts, sweeps, aerials | 320 | `wn -> bp 900>4k>1.8k Q1.2/280 {p 40/280} 0 + pn -> hp 4k {p 30/200} -10` | V4: p±5% t±10% | -10 | 40 | 3 | 60 | 5 |
| taro_heavy_swing | **heavy swing**: smashes, heavy (longer, lower SWOON) | 420 | `wn -> bp 600>3k>1.2k Q1/380 {p 80/330} 0 + bn -> bp 300 Q1 {p 80/300} -10 + wood 800 {p 0/50} -10 (grip creak)` | V3: p±4% t±8% | -7 | 55 | 2 | 100 | 4 |
| taro_shrak | **impact** ("SHRAK!"): weapon layer on every Taro hit | 180 | click-onset `wn -> bp 4.5k>2k Q1.8/120 {p 0/150} 0 + metal 2200 {p 0/150} -8 + wood 950 {p 0/50} -8` | V4: p±5% t±8% | -6 | 65 | 4 | 50 | 4 |
| taro_whrr | spin ("WHRR!"): Spin Guard, Crescent Reaper, Staff Vault spin; loop-capable | 600 | `wn -> bp 1.2k>2.8k Q2/600 -> am 14>22/600 0.7 {40/0/1/120} 0 + sin 180 -> am 14 0.4 {40/0/1/120} -14`. Also export `taro_whrr_loop.wav` (sustain, 500 ms, seamless) | V2: p±4% | -9 | 50 | 2 | 150 | 3 |
| taro_tchk | pole plant ("TCHK!"): Taro's dash / dodge (replaces core_dash), Pole-Vault Step, spear catch | 110 | click-onset `wood 1350 {p 0/60} 0 + wood 820 {p 0/80} -3 @12 + wn -> bp 3k Q2 {p 0/20} -8` | V4: p±5% t±8% | -9 | 45 | 2 | 80 | 4 |
| taro_zaa | rush / long thrust ("Zaa!", "Zash!"): Piercing Dash, dash attack, forward smash wind-up | 300 | `wn -> bp 2k>7k Q3/260 {p 15/270} 0 + saw 400>900/250 -> lp 3k {p 15/250} -16` | V3: p±5% t±8% | -8 | 50 | 2 | 100 | 4 |
| taro_wind_lance | special cast: Wind Lance blade (bus PRJ) ("Fwoom!") | 380 | `wn -> bp 700>3.5k Q1.4/300 {p 20/350} 0 + bell 2349 {p 20/250} -12 + pn -> hp 6k {p 10/200} -12` | V3: p±5% | -9 | 50 | 3 | 100 | 4 |
| taro_crash | heavy kill hit ("Crash!"): forward smash, Polearm Kick finish | 360 | click-onset `wn -> dist 2.5 -> bp 2k>700 Q0.8/200 {p 0/300} 0 + metal 900 {p 0/300} -6 + sin 120>45/120 {p 0/250} -4` | V2: p±3% | -2 | 80 | 2 | 150 | 2 |
| taro_boom | ground shock ("Boom!"): down smash, Falling Spear, Leap Slam landing, dive | 480 | click-onset `sin 120>40/200 -> dist 1.6 {p 0/420} 0 + bn -> lp 300 {p 0/400} -3 + wn -> bp 1k>300 Q0.7/250 {p 0/250} -6 + bell 1175 {p 0/300} -14 (jade)` | V2: p±4% | -3 | 75 | 2 | 150 | 3 |
| taro_ting | jade counter / guard ("Ting!", "Kriii!"): Jade Stance, Jade Counter, layered on core_parry when Taro parries | 420 | click-onset `bell 2349 {p 0/400} 0 + bell 3520 {p 0/280} -6 + sin 1175 -> am 30 0.5 {p 10/350} -12` | V2: p±3% | -5 | 80 | 2 | 150 | 2 |
| taro_clang | uppercut / hook ("Clang!"): Skyhook, up smash launch | 300 | click-onset `metal 1450 {p 0/280} 0 + wn -> bp 2k>5k Q1.5/150 {p 0/200} -4 + wood 700 {p 0/50} -10` | V3: p±4% | -5 | 65 | 2 | 100 | 3 |
| taro_spear_throw | Throw & Recall: Spear spawn (spinning flight, bus PRJ); recall catch = taro_tchk | 400 | `wn -> bp 1.5k>3k Q2.5/380 -> am 18 0.7 {20/0/1/150} 0 + sin 240 -> am 18 0.4 {20/0/1/150} -14` | V3: p±5% | -10 | 50 | 2 | 100 | 4 |

---

## 6. Counts and file budget

| Set | Sounds | Baked files (sum of V) |
|---|---|---|
| Core | 32 | 67 |
| Kaia | 13 | 38 |
| Bruna | 14 | 39 |
| Ren | 13 | 41 |
| Lyra | 13 (+1 loop) | 40 + 1 |
| Taro | 13 (+1 loop) | 39 + 1 |
| **Total** | **98 ids (+2 loops)** | **266 WAVs** (264 variants + 2 loops), about 9 MB at 16-bit mono (average 0.35 s) |

Recommended Unity import: Load Type "Decompress On Load" for everything under 1 s, "Compressed In Memory" (Vorbis q 0.7) for stingers; Force To Mono on; Preload Audio Data on.

## 7. Open questions for audio-director

1. Confirm the core palette (section 3, row "Core") and the 400 ms combat reverb-tail cap.
2. Confirm the naming convention `<owner>_<word>` before the generator bakes file names in.
3. Victory / draw stings: keep as SFX, or reserve for composed music cues?
4. Music bus: this spec ducks a `MUS` bus (sfx-events.md section 4) that does not exist yet.
