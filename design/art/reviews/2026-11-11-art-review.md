# Art Review: Stages, Move Animations, UI Evidence (2026-11-11)

Reviewer: art-director. This review is read-only: no game files were changed.
Source of truth: `design/art/maps/*.jpg` (shared style: 2D side view, anime cel shading, bold clean ink,
vibrant high contrast, layered parallax, readable platforms. AVOID: blurry, muddy, cluttered),
`design/art/character-sheets/*-costume.png` and `*-moveset.png`.

**Review method and its limits.** Images were inspected at display scale. Stage art was seen at about 1:1.9
and the 7680x256 move strips at about 1:3.8. No shell or PIL was available in this session, so no crops or
zooms were made. Coordinates are **source pixels** (display coordinates x 1.92 for stages). Pixel-level edge
quality on the move strips (1 px halos, bitten outlines) was **not** verified. A follow-up pass with
2x-4x crops is needed before sign-off.

Severity: **BLOCKER** = visibly broken or unprofessional at 1080p in a match. **MAJOR** = noticeably below
the reference bar. **MINOR** = polish.

---

## BLOCKER (6)

### B1. `Stages/Volcano/Backdrop.png`: ghosts of the removed platforms and pillars
- **What:** The inpaint left blurred orange "after-images" of L1 to L4 and of MAIN. Examples: a rectangular blur
  block under MAIN at about x 1400-2150, y 1600-1800, and soft blobs at the L-platform sites in the light shaft.
  Both fire pillars also left smeared vertical columns. These end in **hard-edged rectangles** at the lava line,
  at about x 670-1020 and x 2930-3245, y 1830-1920. The lava shoreline ink stroke is chopped at both pillar
  sites.
- **Why it blocks:** The pillars are a hazard, so for most of the match they are *absent*. Their smeared ghosts
  sit centre-left and centre-right in full view. This is exactly the "blurry, muddy" look the sheet forbids.
- **Fix:** Re-inpaint with the mask = pillar silhouette + glow halo, dilated 24 px. Use LaMa or another
  structure-aware inpaint, not Gaussian or Telea. Fill the cave wall by cloning wall texture from x 0-600 and
  x 3300-3840, and fill the shaft region from the shaft gradient. Redraw the lava shoreline as one continuous
  3 px ink stroke (#2a0d0d) with a 2 px pale-yellow inner line across both gaps. Re-scatter 10-15 ember
  diamonds over the patched area so ember density matches. For the platform ghosts, regenerate the light
  shaft as a clean linear gradient inside its trapezoid, because it is a flat graphic shape and needs no
  inpaint.

### B2. `Stages/Volcano/Haz_PillarLeft.png` (check `Haz_PillarRight.png` too): background baked into the hazard
- **What:** The pillar sprite contains a dark cave-rock shard (maroon/brown polygon on the left flank,
  roughly y 330-1050 of the sprite), crack lines, a horizontal lava vein crossing the tip, loose ember diamonds
  and a brown blurred halo. When the pillar rises, a piece of cave wall rises with it.
- **Fix:** Re-matte to the fire triangle only. Draw the pillar polygon (apex plus the two base corners and the
  base flare) and zero alpha outside it. Inside it, keep pixels whose hue is 15-55 degrees with S > 0.45 to
  drop the maroon rock. Then erode alpha 1 px and re-add a 2 px warm ink stroke (#5a1a0a) to match the source
  outline weight. Keep the soft outer glow as a *separate* additive sprite instead of baking it in.

### B3. `Stages/BirdLair/Backdrop.png`: smear left by the bird removal, centre sky
- **What:** A wide blurred band at about x 1100-2730, y 290-620. A leftover dark **beak/head triangle** remains
  at about (1920, 300), with dark specks at about (1830-2010, 790) and an orange feather smudge at about
  (2690, 360). Wind streaks crossing the area are cut off mid-stroke. This is the centre top of the screen, the
  most-seen area of the stage, and nothing covers it.
- **Fix:** Mask = bird silhouette from the source sheet, dilated 20 px. The sky is a smooth radial gradient
  (teal top to gold sun), so do not inpaint it. Fit a 2nd-order 2D polynomial per channel to the unmasked sky
  pixels and paint that into the mask, then add 1 px of noise so no banding appears. Redraw the 3-4 cut wind
  streaks as complete 3 px white strokes with tapered ends. Delete the triangle and the specks.

### B4. `Stages/Ocean/Haz_Whale.png` + `Stages/Ocean/Backdrop.png`: whale cut incompletely
- **What:** The whale sprite has **no pectoral fin**. The fin, the throat pleats and a dark blurred body ghost
  stay baked in the backdrop at about x 860-1830, y 0-660. When the whale descends, a disembodied fin and pleat
  lines hang in the water. The top tail fluke is clipped flat at the sprite's top edge (y 0). The whale also
  has only a 1 px cyan rim and no ink outline, while every platform and pillar uses a 3-4 px black ink.
- **Fix:** Re-cut the whale from the source with the fin and the full pleat lines included. Mirror the lower
  fluke to rebuild the missing upper fluke tip. Add a 3 px ink stroke (#061a33) outside the existing cyan rim.
  Repaint the backdrop region as the light-ray pattern: the rays are straight bands, so redraw them as
  polygons with the same two blues. Do not inpaint.

### B5. `Stages/Mountain/Plat_MAIN.png` + `Stages/Mountain/Backdrop.png`: the platform is split across two layers
- **What:** `Plat_MAIN.png` holds only the top lip and the upper band. Its lower edge is a wavy cut, and the
  left and right underside outline strokes (about x 100-440 and x 2800-3150, y 380-590) dangle with **no fill**.
  The underbelly comes from a blurred grey ghost of the platform in `Backdrop.png` (about x 1130-2700,
  y 1450-1850). The result is a crisp top over a soft grey mass. Blurry patches also remain where A, B and C
  were removed (about x 380-1000 / y 1030-1240, x 2880-3300 / y 1030-1150). There is a smoke-like smear beside
  the pagoda (x 1300-1400, y 560-640) and a smeared cloud edge (x 900-980, y 540-590).
- **Why it blocks:** If the backdrop scrolls at any parallax factor other than the platform's, the underbelly
  visibly separates from its top. Even static, the soft/hard mismatch reads as broken at 1080p.
- **Fix:** Re-cut `Plat_MAIN` as the **whole** silhouette, top lip to zigzag bottom, with the bottom ink
  outline included. Then re-inpaint the backdrop under it with the mask dilated 16 px. Most of that area is
  fog/waterfall base, so fill it from the Foreground fog gradient. Repaint the A, B and C patches by cloning
  the adjacent cliff texture and continuing the cliff ink lines through. Clean the pagoda and cloud smears.

### B6. `Animations/Bruna/Move_bruna_dair.png`: file did not decode
- **What:** The 7680x256 PNG could not be decoded by the image reader. Every other sheet in the folder
  decoded. It may be mid-write by the artist fixing facing flips.
- **Fix:** Once that work lands, open it in Unity and an image viewer. If it is corrupt, regenerate it with
  `tools/art/sheet_anims.py --only bruna` and re-save as 8-bit RGBA PNG.

---

## MAJOR (11)

### M1. All `Move_*.png`: the recovery pops back to the anticipation pose
- `sheet_anims.py: move_frames()`: frames 10-12 are `transform(base0, ...)`, the *anticipation* pose, so at
  the end of every 2-3 pose move the figure snaps from the strike pose back to the wind-up pose. In 2-pose
  moves the anticipation-to-strike change is also a single-frame pop with no in-between.
- **Fix:** Make the last frame the `Idle_Sheet` frame 0 pose, and cross-fade strike to idle over frames 10-12
  (alpha 0.66/0.33). Insert one smear frame between anticipation and strike: the strike figure with a
  directional motion blur of 10-14 px along the strike vector at 60% alpha, behind the sharp figure.

### M2. Strike frame does not line up with the hitbox (anim timing vs frame data)
- The strike is fixed at frame 4-5 of 12 (33-40%), and `FighterVisual.PlayMoveClip` stretches the clip
  linearly over the whole move. From the sheet data: Kaia jab startup is 2 of 23 frames (9%), so the visual
  hit lands about 6 frames *after* the hitbox. Kaia fsmash is 14 of 56 (25%), so it lands early and the
  wind-up looks rushed.
- **Fix:** This is code, so it goes to the technical-artist / gameplay programmer. Use a piecewise time remap:
  clip frames 0-3 over Startup, 4-6 over Active, 7-11 over Recovery, using the move's frame data.

### M3. All `Move_*.png` + `Idle_Sheet.png`: soft upscale
- The source poses on the moveset sheets are about 60-90 px tall. They are upscaled about 2.5-3x with
  Lanczos plus a light unsharp (`to_layer`). At 1080p the fighters display roughly 1:1, next to stage ink that
  was drawn at 4K, so the fighters look soft and JPEG-mushy beside the stage.
- **Fix:** Before segmentation, upscale each panel crop 4x with an anime super-resolution model
  (Real-ESRGAN `x4plus_anime_6B`, or waifu2x cunet noise 1), then downscale to target with INTER_AREA. Reduce
  the in-house unsharp to radius 1.0 / amount 0.3 to avoid ink halos. Re-check 2 frames per character at 2x
  crop.

### M4. `Animations/Taro/Move_taro_uair.png`: effect detached from the body
- The jade arc sits **behind and to the left** of the figure with a visible gap, not above it, so the move reads
  as a back-air.
- **Fix:** Check `split_poses` for this panel: the arc was probably assigned to the wrong pose. Anchor the arc
  so its inner end overlaps the striking foot by 8-12 px. If the sheet's arc points up, rotate it to point up.

### M5. Afterimage frame (frame 4, every move) reads as a grey smudge
- `tint(ghost, 0.55)` at 0.35-0.4 alpha pushes the ghost toward pale blue-white. On light stages it is a fog
  patch, and on Volcano and Ocean a grey smear. Both are muddy.
- **Fix:** Tint the ghost toward the character's VFX colour (Kaia cyan, Bruna orange, Ren blue, Taro jade,
  Lyra violet) and blend it additively at 0.25. Or drop it and use the smear frame from M1.

### M6. Weak effects on some specials, e.g. `Animations/Lyra/Move_lyra_nspec.png`
- The effect is a 1-2 px purple streak, invisible at game scale, while the moveset sheet shows a bold VFX
  shape. (Lyra's scale is being fixed separately; this point is only about the effect.)
- **Fix:** Effects thinner than 6 px at final size should be redrawn as bold cel shapes: a 2-tone fill plus a
  white core, matching the sheet. Or replace them with an in-engine VFX sprite. Audit every `Move_*` whose
  effect alpha covers < 2% of the frame.

### M7. Mipmaps disabled on all stage and animation textures
- `enableMipMap: 0` on every `Stages/**` and `Animations/**` PNG (only `Characters/*.png` have mips).
  3840x2160 stage layers drawn at 1080p, and further shrunk when the camera zooms out, are minified 2-4x
  with bilinear filtering only. The result is shimmering, aliased ink lines while the camera moves.
- **Fix (technical-artist):** Enable mipmaps + trilinear (`filterMode: 2`) on `Stages/**`, or ship 1920-wide
  layers. Re-check the cream platform lips for shimmer during zoom.

### M8. `Stages/Mountain/Foreground.png`: fog strip carries the platform's stencil
- The fog has a hard-edged polygon matching the removed MAIN platform's zigzag underside (about x 1420-2500,
  y 190-350). Fog with a geometric edge reads as an error.
- **Fix:** Rebuild the strip as soft cloud masses: 3 octaves of Perlin noise thresholded into rounded shapes,
  1-2 px pale-blue ink rim on the top edge to match the map sheet's cel clouds, alpha 0.85 at the bottom
  fading to 0 at the top. The top 25% of the strip must stay at alpha < 0.3 so nothing on the lowest playable
  band is hidden.

### M9. `Stages/BirdLair/Backdrop.png`: platform ghosts and double outlines
- Brown smears remain behind MAIN and the L/R plateaus (about y 1300-1650). Thin dark vertical slivers sit
  just outside the left edges of the L and R plateaus in the composite (`_preview.png` about x 470 and
  x 1100), which reads as a doubled outline.
- **Fix:** Re-inpaint with the mask dilated 12 px past the plateau outlines and fill from the sunset sky
  gradient (same polynomial fill as B3) plus the cloud-bank tops.

### M10. No in-match evidence of the five new stages at 1080p
- The `production/qa/evidence/2026-11-10-*` match shots show the legacy Neon City and Sky Dojo stages, and
  they are 734x293, too small to judge edges or HUD type.
- **Fix:** Capture one 1920x1080 shot per new stage with two fighters mid-move, plus one hazard-active shot
  each for Volcano, BirdLair and Ocean. Visual sign-off cannot be given on previews alone.

### M11. `Stages/Plain/Foreground.png` (verify in-game): pale edge fade
- In `_preview.png` both grass bands fade to a cream wash over the outer ~200 px left and right. It reads as
  a fog vignette that the map sheet does not have, and it desaturates the frame edges.
- **Fix:** Remove the horizontal alpha/colour ramp, or make the strip 1.2x the view width so its edges never
  show.

---

## MINOR (8)

- **m1. `Stages/Mountain/Mid.png` (3840x2149) and `Far.png` (3840x2158)** are not 2160 tall. This can
  misalign layers by up to 11 px or cause non-integer scaling. Pad to 3840x2160 exactly. `Far.png` is also
  blurred, which softens its ink to 1 px. Atmospheric recession is right, but do it with -20% saturation and
  +15% lightness, not blur, so the ink style holds.
- **m2. `Stages/Volcano/Plat_L1..L4.png`, `Plat_MAIN.png`:** ember diamonds from the backdrop are baked onto the
  platforms (for example a diamond on L4's top lip). If embers animate later, these stay stuck. Paint them out
  with a local clone.
- **m3. `Stages/Ocean/Backdrop.png`:** green and pink smudges where the coral decor was lifted, at about
  (2500, 960) and (720, 1390). Mostly behind platforms, but visible at the platform ends. Clean them with a
  clone from the adjacent rock.
- **m4. `Stages/*/_preview.png`:** red labels and lines are debug overlays. Make sure `_preview.png` is never
  loaded as a runtime backdrop and is excluded from the build (move it out of `Resources/` or add an
  editor-only folder).
- **m5. Evidence `2026-11-10-match.png` / `moves-2.png`:** a debug key-hint strip in small grey text crosses the
  bottom of the playfield, and a "PI CAMERA" panel overlaps the stage. Hide both behind a dev toggle for
  release captures.
- **m6. Evidence `2026-11-10-moves-2.png`:** Taro reads noticeably paler and less saturated than Bruna at
  game scale. Verify at 1080p. If confirmed, adjust levels on Taro's sheets (+10% saturation, black point to
  the ink) so the cast matches.
- **m7. `Stages/Volcano/Haz_Ember.png`:** a soft glow diamond with no ink. It matches the source's ember
  style, so it is acceptable. Make sure it is drawn additively so it does not grey out over the dark walls.
- **m8. Move strips, frames 1-3:** the anticipation is a 5-7 degree lean plus squash only, which is subtle at
  game scale. Double it (10-14 degrees, sy 0.88) on smashes only, so heavy moves read as heavy.

---

## Genuinely good: keep these

- **Stage composition matches the map sheet**: palette, landmark shapes (Mountain waterfall and pagoda,
  Volcano light shaft and magma orb, Ocean arches and jellyfish, BirdLair nest and eggs) and KO layout are
  faithful. Plain and Ocean backdrops are largely clean.
- **Platform readability is excellent.** The cream top lip with a black ink line under it makes every
  walkable surface instantly readable against every backdrop. Keep this treatment on all future platforms.
- **Platform sprites** (Mountain A-D, Volcano L1-L4 and MAIN, Ocean MAIN and T1-T3, BirdLair L/R) have crisp,
  consistent ink weight and good cel shading. The Ocean glyph glow and Volcano lava veins are lovely.
- **Move sheets keep the user's character designs intact**, and each fighter's VFX colour is consistent
  (Kaia cyan, Bruna orange, Ren blue, Taro jade, Lyra violet). Kaia, Bruna and Ren fsmash read clearly at a
  glance.
- **Pipeline choices to keep:** one scale per character, a shared feet pivot, the 32 px edge fade for
  effects that run past the frame, and `alphaIsTransparency: 1` on every PNG (no white fringes).
- **UI:** home and character-select screens have a strong, coherent identity: red/blue diagonal slashes, bold
  italic display type, halftone dots and large character art. The HUD plates (portrait, name, damage %,
  stock/energy bars) are clear and on-brand. Keep this as the UI style reference.

## Not reviewed in this pass
`Stages/BirdLair/Haz_BirdSwipe.png`, `Haz_BirdSilhouette.png`, `Stages/Volcano/Haz_PillarRight.png`,
`Characters/*.png` cutouts individually, and pixel-level edges of most `Move_*` sheets. These need a crop-based
follow-up, together with re-checking B1-B6 after the fixes.
