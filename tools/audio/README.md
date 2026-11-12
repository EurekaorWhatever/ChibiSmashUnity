# tools/audio: procedural SFX

`gen_sfx.py` renders every sound in `design/audio/sfx-spec.md` into WAV files the game loads at runtime.
Needs Python 3 and numpy (nothing else). The Unity editor can stay open.

## Regenerate

```
python tools/audio/gen_sfx.py              # everything (about 10 s), then validates and prints a table
python tools/audio/gen_sfx.py --check      # render and validate in memory, write nothing
python tools/audio/gen_sfx.py --only ren_  # only ids containing "ren_" (stale files are not cleaned up)
```

Output (all generated, do not edit by hand):

| Path | What |
|---|---|
| `Assets/ChibiClash/Resources/Audio/Sfx/<id>_<nn>.wav` | one file per baked variant, 44.1 kHz mono 16-bit, `nn` = 01..V |
| `Assets/ChibiClash/Resources/Audio/Sfx/lyra_vmm_loop.wav`, `taro_whrr_loop.wav` | seamless sustain loops |
| `*.wav.meta` | importer settings, written before the WAV: Force To Mono, normalize off, preload on; Decompress On Load under 1 s, Compressed In Memory (Vorbis 0.7) for 1 s and longer |
| `Assets/ChibiClash/Resources/Audio/sfx_meta.txt` | runtime mix table (bus, priority, max voices, cooldown, pitch jitter, peak, variants) read by `ChibiAudio` |

The same spec always gives the same bytes (seed = `crc32(id) + nn`). Files that did not change are not rewritten, so
Unity only reimports what changed. WAVs that no longer exist in the spec are deleted (full runs only).

Validation fails the run (exit code 1) when a file clips, misses its Peak column by more than 0.1 dB, has DC offset above
0.005, is near silent (RMS below -60 dBFS), is longer than its Dur (times the variant's time factor), or a loop has a
seam jump.

## Add or change a sound

1. Add or edit a row in the right table of `design/audio/sfx-spec.md` (sections 4.x core, 5.x per character). The row
   must keep the 10 columns: `id | Use | Dur | Recipe | Var | Peak | Pri | Max | CD | Pitch`. The id is
   `<owner>_<word>` in lower snake case. Put `click-onset` before the backticked recipe for sounds with no fade-in.
2. Run `python tools/audio/gen_sfx.py` and check the table for your id.
3. To make the game play it, map an event to it in `Assets/ChibiClash/Scripts/Netcode/View/ChibiAudioRules.cs`
   (`ChibiSfxRouter` for simulation events, `ChibiSfxMap.Move` for move kinds), or call `ChibiAudio.Play("<id>")`.
4. Bus: sections 4.1-4.6 set the bus; in section 5 write `bus PRJ` (or `MOV`, ...) in the Use cell, otherwise `CMB`.

## Recipe notation (spec 1.1) and how it is interpreted

`SOURCE [-> FILTER ...] {ENVELOPE} GAIN [@OFFSET]`, layers joined by ` + `.

- Sources: `wn pn bn` (pink/brown are FFT-shaped white noise), `sin tri saw sq F` (glide `800>300/60`), `fm C:R I a>b/T`,
  `bell F`, `metal F`, `click F`, `wood F`. `saw 261+392` is two oscillators in one layer.
- Chain: `bp/lp/hp F [Qq][/T]` (RBJ biquads, coefficients every 32 samples, exponential sweeps, multi-point
  `2.5k>7k>3.5k` split evenly over T), `am R d` (rate sweep allowed), `dist d`, `rev T m`.
- Envelopes: `{p A/R}` linear attack, exponential to -60 dB at R. `{A/D/S/R}` with the release ending at the end of the
  sound. `bell`/`metal` partials decay at their listed fractions of R.
- Interpretations (kept consistent for every row):
  - each layer's source+chain is peak-normalised before its envelope, so the gain column is relative peak level;
  - a bare source with no envelope and no gain joins the next layer (`saw 330 + saw 495 + saw 660 -> lp ... (each -4)`);
  - a standalone `+ rev T m` layer is a reverb on the sum of the other layers; tails are cut at the row's Dur;
  - variant time factor stretches Dur, envelopes, glides, sweeps and offsets; pitch factor scales oscillator,
    filter and resonator frequencies (not tremolo rates);
  - `saw/sq/tri` are additive band-limited, FM is rendered at 4x and brick-walled at 18 kHz;
  - final chain: DC block 20 Hz, tanh soft clip (drive 1.2), peak-normalise, trim below -70 dBFS, 2 ms fade-in
    (skipped for click-onset), 6 ms fade-out, peak-normalise again.
- Loops: sustain only (sweeps held at their end value, envelope at sustain), every oscillator / tremolo rate rounded to a
  whole number of cycles per loop, filters applied circularly, start rotated to an upward zero crossing.
  `lyra_vmm` 148.5 Hz becomes 148 Hz in the loop (1 Hz beat instead of 1.5 Hz).

## Stage ambience beds and event sources (spec section 8)

- **Beds** (spec 8.1, bus AMB): `<id>_01.wav`, 10 s seamless loops. The Peak cell is `rms -24`: the bed is normalised to that
  RMS (allowed -26..-22 dBFS) and its peak must stay at or below -3 dBFS. Write `{0/0/1/0}` as the envelope of every bed
  layer (a layer with no envelope and no gain joins the next one). How the loop stays seamless: whole cycles per loop, noise
  generated at the loop length, circular filters, event sources wrapped around the end, reverb run on three tiles, then a
  120 ms crossfade of the head with the next period. The run fails when the wrap step is larger than the file's own
  99.9th-percentile sample step (+64 LSB).
- **Event sources** (spec 8.0): `drops R F` bubbles, `crackle R` spatter, `chirp R F` birds, `chime R F` wind chimes,
  `flap R F` feathers / wings. R = events per second; times come from the layer's seeded RNG (deterministic).
- Quick listen-free QA used for section 8: band energy (sub < 90 Hz, body 90 Hz-2.5 kHz, air > 3 kHz) per hazard so each
  has all three layers, and the second difference at the bed's wrap compared with the rest of the loop (no click).
