# Art Review Pass 2: Five New Stages In-Game (2026-11-12)

Reviewer: art-director. Read-only review: no game files were changed.
Inputs: `production/qa/evidence/2026-11-12-stage-{Mountain,Plain,Volcano,BirdLair,Ocean}.png` (1920x1080),
`design/art/stage-previews/*.png`, `design/art/maps/*.jpg`, pass 1 `2026-11-11-art-review.md`.

**Method and limits.** Each capture was inspected at native 1:1. No shell or image library was available, so
no zoomed crops were possible. Edge defects at 1-2 px scale (halos, bitten ink) are **not** verified.
Coordinates are **capture screen pixels** unless marked "preview". The camera is zoomed about 1.85x, so
large parts of each stage (Ocean whale area, BirdLair centre sky, Volcano lava line) are out of frame.
Those areas were judged from the clean previews. The BirdLair hard backdrop edge (left x 458, bottom y 980)
is the known camera bug, already fixed. It is excluded.

Severity: **BLOCKER** = looks broken or unprofessional at 1080p in a match. **MAJOR** = noticeably below the
reference bar. **MINOR** = polish.

## Pass 1 fixes: confirmed

- **B5 / M8 Mountain:** MAIN is one whole silhouette. The fog is soft and has no platform stencil. Confirmed
  in-game.
- **B1 Volcano:** no orange after-images in the shaft. **B2:** the dormant pillar has no rock shard. The
  active state was not captured.
- **B3 / M9 BirdLair:** the sky and plateaus are clean in the preview. Only the faint wing trace remains
  (item 6).
- **B4 Ocean:** the fin is attached, the fluke is complete and the ink outline is present (preview only).
- **M7 mipmaps:** stills cannot show this. No shimmer is visible in these frames.

## Verdict per stage

| Stage | Verdict | Why |
|---|---|---|
| Mountain | **APPROVED** | Clean, faithful to the sheet, best platform readability of the set. |
| Plain | **APPROVED WITH NOTES** | M11 edge fade is still open (item 3). Grass ink is busy at fighter feet (item 10). |
| Volcano | **NEEDS WORK** | The dormant vent is off-palette (item 1). Ember scale and density are too high at match zoom (item 2). |
| Bird Lair | **APPROVED WITH NOTES** | The wing trace is faint and acceptable for now (item 6). |
| Ocean | **APPROVED WITH NOTES** | The whale marks are out of frame at match zoom (item 7). m3 is not visible. The floating cyan runes on the columns are in the reference, so they are correct. |

## Remaining issues

1. **MAJOR: Volcano dormant vent reads as a grey stone spire. Not acceptable.**
   At about x 380-560 it is the largest single shape in frame, top to bottom. It is the only cool, neutral
   grey in an all-warm stage, so colour contrast pulls the eye to it. It is also soft-gradient with no ink,
   so it reads as placeholder art rather than a volcanic vent. The sheet palette has no grey: Obsidian is
   `#1B1022`.
   **Fix:** Redraw the dormant state as cooled basalt. Use a 2-tone cel fill in obsidian
   (`#1B1022` / `#3A1A2A`) and a 3 px ink outline (`#0D0810`) to match the platforms. Add 3-5 dim cooled-lava
   crack lines (`#6A2A10`, about 30% of the active orange). Add a small dark-red glow at the base only, and
   remove the full-height grey halo. Suggestion for game design, not an art call: brighten the cracks to
   full `#FF9A1F` during the pre-eruption telegraph.

2. **MAJOR: Volcano ember diamonds are too big and too dense at match zoom.**
   At 1.85x, the ember diamonds are 30-60 px tall, as large as a fighter's head, for example at (1330, 640)
   beside Kaia and the "Slash!" callout. About 50 are visible. They have the same luminance as hit sparks and
   compete with fighters and FX. The sheet asks for embers, not clutter.
   **Fix:** Cap ember size so the largest is about 24 px tall at the default match zoom. Cut the count in the
   play band (MAIN lip to L4) by about 40%. Keep the dense, big embers in the far layer and near the lava,
   where they set the mood but do not cover gameplay. Draw them additively at 0.7 alpha (see m7).

3. **MAJOR (carry-over M11): Plain foreground edge fade.**
   The cream fade over the outer ~200 px is still in the preview. It is out of frame in this capture, but it
   will show whenever the camera follows a fighter to either ledge, which is where KOs happen on this stage.
   **Fix:** As in pass 1. Remove the horizontal alpha/colour ramp, or make the strip 1.2x the camera's
   widest view.

4. **MAJOR (UI, owner: ux-designer / ui-programmer): world-space labels collide with the HUD and with each
   other.**
   - The "Pangya123" tag sits behind the timer plate on Mountain (about 1000, 117) and on Plain.
   - The "Thunder Cl..." callout on Ocean is cut off by the P2 plate (about 1110-1240, 165).
   - The "Slash!" callout on Volcano covers the name tag (about 900-1110, 600-630).

   **Fix:** Keep labels out of the HUD band: no label may sit above y 230 at 1080p. When a fighter is under
   that band, show the tag below the fighter's feet. Stack callouts above the tag and never overlap them.
   This is not stage art, but it shows on every stage capture.

5. **MAJOR (carry-over M10): hazard-active captures are still missing.**
   The rebuilt Volcano pillars (B2), the re-cut whale (B4) and `Haz_BirdSwipe` have not been seen in-game.
   **Fix:** Capture one 1920x1080 shot per hazard at peak, plus one Volcano shot during the telegraph.
   Final visual sign-off for Volcano, Ocean and Bird Lair depends on these.

6. **MINOR: Bird Lair wing trace.**
   It is very low contrast, at about preview (870, 320) and (1030, 320), and partly broken up by wind
   streaks. It is out of frame at match zoom. **Acceptable to ship.**
   **Fix when time allows:** Repaint the area with the polynomial sky fill from B3, mask dilated 20 px.

7. **MINOR: Ocean marks at the left arch, where the whale's head was.**
   At match zoom this area is above the top of the frame (preview about 420-500, 100-230), so players see it
   only at maximum zoom-out or while the whale is absent.
   **Fix:** Clone arch marble (`#B9DFE3` and its shadow tone) over the marks, and continue the arch's ink
   line through them. Paint the water behind as straight light-ray polygons (pass 1, B4), not inpaint.

8. **MINOR (carry-over m2): embers baked into the Volcano platform sprites.**
   Static diamonds on MAIN show in-game. One sits right on the walkable lip, at about (1530, 830). Static
   embers on the floor read as pickups.
   **Fix:** As in pass 1: clone them out of `Plat_MAIN` and `Plat_L1..L4`. Fix the one on the lip first.

9. **MINOR (carry-over m3): coral smudges in the Ocean backdrop.**
   Not visible in this capture. Keep it on the list. **Fix:** As in pass 1, clone from the adjacent rock.

10. **MINOR: Plain grass ink behind the fighters' feet.**
    The mid-band grass (y 590-800) uses the same black ink weight and density as the fighters. Taro's legs and
    glaive tip blend into it.
    **Fix:** Recolour the ink on the mid-band blades only, from black to dark green `#1D5A2A`, at about 70%
    opacity. Keep black ink on the near-foreground blades below the ground line. This is atmospheric
    recession done with colour, not blur, as in pass 1 m1.

11. **MINOR (VFX, owner: technical-artist): Lyra's Thunder Cloud is a flat grey blob with no ink.**
    About (775-935, 305-385) on Ocean. It is off-style next to the ink-outlined cel clouds on Mountain and
    Plain.
    **Fix:** Use a 2-tone cel cloud (`#3B3F5C` / `#5D6285`) with a 3 px ink outline and a violet rim
    (Lyra's VFX colour). Add one lightning glyph so it reads as a hazard.

12. **MINOR (carry-over m5): dev overlays are in the captures.**
    The P1 CAMERA panel and the key-hint strip are fine in dev evidence, but they must be off by default in
    release and marketing captures.

## Direction: what "sharp like the Blast Blade teaser" means for fighters

At default zoom, fighters display at about 170-200 px tall, and stage ink is 3-4 px. "Sharp" means the
fighters look as if they were drawn at the same scale with the same pen as the stage.

- **Outline:** one continuous, unbroken dark outline around the silhouette, **3 px at 1080p**. Use a warm
  near-black such as `#1A1220`, not pure black. Gaps in the outline read as "holes" just as much as missing
  fill does.
- **Interior lines:** 1.5-2 px. Small details may drop out. Never draw them heavier than the outline.
- **Edge:** at most 1 px of anti-aliasing, then solid alpha. No soft alpha ramp, no grey or white fringe, and
  no unsharp-mask halo. If sharpening creates a light ring, it is too much.
- **Fill:** flat cel fills, 2-3 tones per material, with hard shadow edges. No gradients, upscale noise or
  JPEG mottling. Posterise the fills after upscaling if needed.
- **Face:** the face is the top read. At this size the head is about 50-60 px. Eyes are solid dark shapes,
  each with one white highlight. Skin and eye whites must be fully opaque. Holes usually mean the matte was
  keyed on colour (white or skin) instead of filled from the silhouette.
- **Motion stays out of the figure:** every frame of the figure is sharp. Speed comes from smear frames and
  the separate additive trails (`ChibiBladeTrails`). Never blur the body.
- **Consistency:** move clips must match the idle and costume art in ink weight, scale and saturation, so
  the switch from idle to a move does not "pop" softer.
- **Acceptance check:**
  1. Crop each character's strike frame at 2x next to a platform lip. The ink weights should be within
     ±1 px and there should be no halo.
  2. At 1x, the face reads.
  3. Filled solid black at 50% scale, the pose is still recognisable.

## Overall verdict

**Shippable after item 1. Four of the five stages can ship now.** Mountain, Plain, Bird Lair and Ocean meet
the reference bar. Their open notes are polish, except item 3, which should land before release.

Volcano holds the set back for one cheap fix: recolour the dormant vent (item 1). Item 2 should land with it.

Before final visual sign-off on the three hazard stages, two more things are needed: hazard-active captures
(item 5) and the label layering fix (item 4). No BLOCKERs remain.
