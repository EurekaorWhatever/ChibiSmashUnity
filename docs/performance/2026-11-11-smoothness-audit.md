# Smoothness Audit — 2026-11-11 (static, code reading only)

**Author:** performance-analyst · **Build:** working tree on `main` after 93fad90 (uncommitted wave work) ·
**Engine:** Unity 6000.6.7f1, URP 2D · **Method:** code and asset reading only. Unity was not launched, so every
millisecond figure below is an estimate to confirm with the checklist in section 5.

Goal (user): the match should feel as smooth as a modern premium fighter. That means one sim step per 60 Hz tick, every
moving thing interpolated at the display rate, no frame-time spikes during a match, and 0 B of GC allocation per frame.

## 1. Budgets (proposed, technical-director to confirm)

| Budget | 60 Hz display | 120 Hz display | 144 Hz display |
|---|---|---|---|
| Whole frame (CPU main thread, p99) | 16.6 ms | 8.3 ms | 6.9 ms |
| Sim (ticks this frame, avg 1 / 0.5 / 0.42 per frame) | ≤ 1.0 ms per tick | same | same |
| View + FX + camera (ChibiSimView.LateUpdate and its callees) | ≤ 1.5 ms | ≤ 1.5 ms | ≤ 1.5 ms |
| IMGUI HUD (GameManager.OnGUI, all events) | ≤ 2.0 ms | ≤ 1.5 ms | ≤ 1.2 ms |
| GPU (render + post) at 1080p | ≤ 10 ms | ≤ 6 ms | ≤ 5 ms |
| GC allocation during a live match | **0 B / frame** (hit and KO frames may allocate up to 1 KB) | | |
| Frame-time spikes during a match (after the intro) | 0 frames > 2x budget per 3-minute match | | |
| Texture memory, one match (2 fighters + 1 stage) | ≤ 250 MB | | |

## 2. Fixes made in this audit (owned files, compile_check green)

| ID | File | Change | Why |
|---|---|---|---|
| F1 | `Assets/ChibiClash/Scripts/Game/GameManager.cs` Awake | `QualitySettings.vSyncCount = 1`; `Application.targetFrameRate` = display refresh (was a fixed 120) | Quality levels Very Low and Low ship `vSyncCount: 0` (ProjectSettings/QualitySettings.asset lines 32, 85). On those, or when a driver forces vSync off, the fixed 120 tore on 60 Hz and judders on 144 Hz (120 frames over 144 refreshes repeats every 5th image). With vSync on, targetFrameRate is ignored, so on the default Ultra level nothing changes except robustness. |
| F2 | `Netcode/ChibiTickClock.cs` `SnapToRefresh` (new pure helper) + `Netcode/ChibiLocalLoop.cs` `PacedDelta` | Offline only: with vSync on, a measured delta within 0.5 ms of 1..4 refresh periods is snapped to that exact multiple before it enters the accumulator. | The accumulator sits right at a tick boundary at 60 Hz. A ±0.3 ms jitter in `unscaledDeltaTime` gives 0 ticks one frame and 2 the next (a visible hitch even with interpolation, because the sim pair jumps). At 120/144 Hz it wobbles RenderAlpha. Online loops are untouched: rollback time sync must follow wall time. |
| F3 | `Netcode/ChibiLocalLoop.cs` `PacedDelta`, `Begin` | Offline only: the first frame after `Begin` contributes 0 s; any later frame contributes at most 3 ticks (50 ms). | The match-build frame (synchronous `Resources.LoadAll` of ~70 MB of animation strips per fighter, 3840 px stage layers) produced a 5-tick burst at match start. Mid-match hitches (GC, texture upload, window drag) teleported fighters 4-5 ticks and swallowed inputs. Now a hitch above 50 ms becomes a brief slowdown, like frame-locked console fighters. Runs at full speed down to 20 fps. |

Existing tests are unaffected: `TickClock_*` in `ChibiDeterminismTests.cs` call `Advance`/`Run`, whose code did not
change. Suggested new EditMode tests for the test owner: `SnapToRefresh(0.0172f, 1/60f) == 1/60f`,
`SnapToRefresh(0.0333f, 1/60f) == 2/60f`, `SnapToRefresh(0.025f, 1/60f) == 0.025f`, `SnapToRefresh(dt, 0f) == dt`.

## 3. Findings ranked by visible impact (not fixed: other owners)

### P1. Stage motion uses a different interpolation alpha from the fighters (slow-mo, hitstop)
- **Where:** `Netcode/View/ChibiSimView.cs:206` binds the stage with the raw `_loop.RenderAlpha`. Fighters use the
  time-scaled alpha `min(0.999, (RealTicksSinceStep + RenderAlpha) * scale / 1000)` (`ChibiSimView.cs` LateUpdate,
  the `alpha` local). `Game/StageDesign.cs:168` `Behind()` uses the raw one.
- **Visible:** during hitstop, KO slow-motion and cinematics the alpha values differ, so a fighter standing on a Mountain
  moving platform slides against it, and the bird and whale step instead of gliding. At normal speed (scale 1000,
  0 ticks since the step) the two agree, which is why it looks fine in neutral.
- **Fix (owner: ui-programmer / lead, ChibiSimView + StageDesign):** compute the alpha once in `ChibiSimView.LateUpdate`,
  store it in a field (`public float DisplayAlpha { get; private set; }`), and bind `() => DisplayAlpha` instead of
  `_loop.RenderAlpha`. Better still, interpolate the stage from the same pair the fighters use (`_before`, `_seen`)
  rather than the live state minus a step, so a rollback correction moves both identically.

### P2. Parallax and stage art can run one frame behind the camera (LateUpdate order)
- **Where:** `Game/StageDesign.cs` `LateUpdate` → `Parallax()` reads `Camera.main.transform.position`, which
  `ChibiSimView.LateUpdate` → `UpdateCamera()` writes. Neither has `[DefaultExecutionOrder]`, so Unity's order between
  the two components is undefined.
- **Visible:** when the order is wrong, the Far/Mid layers use last frame's camera position. While the camera tracks a
  launch they shimmer by one frame of camera motion, which is very visible on the 3840 px painted backdrops.
- **Fix (owner: lead, StageDesign.cs):** add `[DefaultExecutionOrder(100)]` to `ChibiDesignStage`, or have
  `ChibiSimView` call a public `design.Sync()` right after `UpdateCamera()` and drop the stage's own LateUpdate.

### P3. Camera shake feeds back into the camera's follow position
- **Where:** `Netcode/View/ChibiSimView.cs` `UpdateCamera()`: `basePos = Lerp(t.position, goal, ...)` then
  `t.position = basePos + offset`. Next frame lerps from the shaken position.
- **Visible:** after every heavy hit or KO the camera drifts and wobbles for a few frames after the shake ends, instead of
  snapping back cleanly. Premium fighters keep the follow position and the shake offset separate.
- **Fix (owner: technical-artist fx, camera code in ChibiSimView):** keep a `Vector3 _camBase` field, lerp `_camBase`
  toward the goal, and set `t.position = _camBase + offset`. Initialise `_camBase` from `t.position` in Begin.

### P4. HUD (IMGUI) allocates and redraws heavily every frame
`GameManager.OnGUI` runs at least twice per frame (Layout and Repaint, plus one per input event), and `Hud.Draw` builds
strings every time. Estimated ~2-6 KB of GC per frame during a match, and a CPU cost of 1.5-3 ms that eats most of the
8.3 ms budget at 120 Hz. Top offenders (owner: ui-programmer):

| # | Where | Allocation per OnGUI event | Fix |
|---|---|---|---|
| 1 | `UI/Hud.cs` `Hints` / `KeyHint` (around lines 580-602) | ~25 `Bindings.Label` calls (each runs `Enum.TryParse`) + ~30 string concatenations, always on screen | Build the hint string once on match start and when bindings change; cache it in a static field. |
| 2 | `UI/Hud.cs` `PlayerPanel` skill row (line 191-192) | `key + name + cd.ToString("0.0")` ×3 ×2 players | Cache the key labels per player; cache the cooldown text per tenth of a second (only rebuild when `(int)(cdSec*10)` changes), as `Timer` already does with `clockText`. |
| 3 | `UI/Hud.cs` `PlayerPanel` line 165, 179, 195-196 | `ch.Weapon + " · " + passive`, `"ULT " + n + "%"`, `string.Join` of statuses + `ChibiMatch.Statuses` building fresh strings | Cache the subtitle per fighter at match start; cache "ULT n%" in a 101-entry table (like `WholeText`); rebuild the status line only when the status inputs change. |
| 4 | `UI/Hud.cs` intro banner (lines 87-89) | 6-part concat each event for 3 s | Build once when the match starts. |
| 5 | `UI/Hud.cs` `Outlined` and comic popups | 10 `GUI.Label` calls per big number and 9 per popup (8-way outline). No GC, but each label is a text-mesh build: ~40-60 labels per Repaint for the two percent numbers alone | Draw the outline once into a cached texture per value, or move the percent numbers to UI Toolkit / TextMeshPro with an outline material. Measure first (checklist item 6). |
| 6 | `GameManager.OnGUI` | Layout event pass that Gui does not need | Set `useGUILayout = false` on GameManager (no `GUILayout` / `GUI.Window` in the codebase). Halves OnGUI passes. Verify text fields and menu navigation in Play Mode before keeping it. |

Other allocation notes: `Fx.Text` / `Fx.Comic` allocate a `Popup` class per hit (event-driven, acceptable; make it a
struct pool if hit frames show spikes). `FighterVisual.SetWeaponVisible` allocates through `GetComponentsInChildren`
(already guarded to change-only in ChibiSimView). `ChibiSimView` closures are created once in Begin, not per frame.

### P5. Animation strips: memory and first-use hitches
- **What:** `Resources/Animations/<Char>/` holds 45-49 PNG strips per character, up to **10752 x 256**, ~70 Mpx per
  character. Import: Sprite, compression High Quality (BC7 on Standalone, 1 byte per pixel), no mipmaps, max size 16384.
  `SpriteAnim.Load` (`Fighter/SpriteAnim.cs:198`) does a synchronous `Resources.LoadAll` of the whole folder the first
  time a character is used, and the static `cache` keeps it for the session.
- **Cost:** ~70 MB GPU + the same again in CPU memory during load per character, so ~140 MB per match and up to ~350 MB
  after all five characters have been played. Load of one character: estimated 150-400 ms on an SSD (this is the
  match-start hitch F3 now hides from the sim clock).
- **Fixes (owner: technical-artist anims, tools/art/sheet_anims.py):**
  1. Trim each frame to its opaque bounds and pack all of a character's frames into 2-4 atlases of at most 4096 x 4096
     (Sprite Atlas v2 or the build script). Frames are 256 px tall with large transparent margins, so expect 40-60%
     less memory and one texture bind for the whole fighter (better SRP batching).
  2. Cap max texture size at 8192 (older GPUs and integrated graphics cap at 8192 or 16384; 10752 fails on 8192 cards).
  3. Load the two selected characters during the VS / loading screen (already a loading page), and clear
     `SpriteAnim.cache` entries for characters not in the next match.
- **Stages:** 23 stage PNGs have heights that are not a multiple of 4 (for example `Stages/Ocean/Mid.png` 3840x2151,
  `Stages/BirdLair/Haz_BirdSwipe.png` 3481x1305). Block compression needs multiples of 4. Confirm in the Inspector:
  if any shows RGBA32, it costs 33 MB each instead of 8 MB. Fix in `tools/art/stages/build_stages.py`: pad the canvas
  to a multiple of 4 (keep the pivot) when exporting.

### P6. Physics and settings (low impact, cheap)
- `ProjectSettings/TimeManager.asset` Fixed Timestep 0.02 and Physics2D auto simulation (`m_SimulationMode: 0`) still
  run every frame, although the sim path uses no physics (only the legacy `Fighter.cs` / `CombatObjects.cs` path does).
  At 50 Hz against 60/120/144 Hz rendering it steps 0-1 times per frame. Cost is tiny with no bodies, but the step count
  jitters. Owner: engine-programmer. Fix: `Physics2D.simulationMode = SimulationMode2D.Script` while a sim match runs
  (restore for the legacy path), and the 3D `DynamicsManager` auto simulation off if 3D physics is unused.
- URP (`Assets/Settings/UniversalRP.asset`): MSAA off, render scale 1, HDR on, SRP Batcher on. HDR is needed by the
  launch FX bloom, so keep it. `Renderer2D` light render texture scale 0.5 is fine. GPU cost of `ChibiPostFx` bloom at
  1440p/4K is the only unknown (checklist item 8).
- `ChibiOnlineMatch` / `ChibiP2PPeerLoop` keep the raw delta on purpose. `ChibiTickClock.RenderAlpha` divides by the
  undilated tick, so with time dilation it can reach about 1.1; the view clamps it. No change needed.

### What is already right
- Sim at a fixed 60 Hz on its own accumulator (not FixedUpdate), capped at 5 ticks per frame with backlog drop.
- Fighters, projectiles (including grow radius), minions and clouds are interpolated between `_before` and `_seen`,
  with identity checks to snap on slot reuse and a teleport check for respawns (`ChibiSimView.cs` LateUpdate,
  `UpdateMinionsAndClouds`). Moving platforms, bird and whale are interpolated (lead, `StageDesign.cs`, see P1 for the alpha).
- Camera follows interpolated fighter positions with frame-rate independent exponential smoothing.
- FX use one shared ParticleSystem with `Emit` (no instantiation per burst); the clock text and percent digits are cached.
- Non-interpolated movers that are fine: idle bird and whale, embers and fire flicker are driven by `Time.time` every
  render frame, so they are smooth at any refresh rate. Zones (`UpdateZones`) are static rings with a pulse.

## 4. Priority order for owners

| Rank | Item | Owner | Effort | Expected effect |
|---|---|---|---|---|
| 1 | P1 one alpha for fighters and stage | ui-programmer / lead | 15 min | No sliding on moving platforms in hitstop and slow-mo |
| 2 | P2 LateUpdate order | lead | 5 min | No parallax shimmer while the camera moves |
| 3 | P3 camera base vs shake | technical-artist fx | 15 min | Clean recovery after every heavy hit |
| 4 | P4 HUD string caching + `useGUILayout = false` | ui-programmer | 2-3 h | GC to ~0 B/frame, 1-2 ms back at 120 Hz |
| 5 | P5 atlas + 8192 cap + preload + non-multiple-of-4 stages | technical-artist (anims, stages) | 1 day | -40-60% texture memory, no first-use hitch, works on 8192-limit GPUs |
| 6 | P6 physics off in sim matches | engine-programmer | 15 min | Removes a jittery background step |

## 5. Play Mode profiling checklist (run when the editor is available)

Use a Development Build with Autoconnect Profiler for real numbers; the Editor adds 2-5 ms of overhead.

1. **Frame pacing:** Profiler, CPU module, 60 s of Kaia vs Bruna on Mountain (moving platforms). Record frame time
   avg / p99 / max at 60 Hz and, if available, a 120 or 144 Hz monitor. Pass: p99 within budget, max < 2x budget after
   the intro.
2. **Ticks per frame:** add a temporary `ProfilerCounter` (or log a histogram) of `n` in `ChibiLocalLoop.Update`. Pass at
   60 Hz: n == 1 on 99%+ of frames (F2 working). At 120 Hz: strict alternation 1, 0, 1, 0.
3. **Visual check, moving platforms:** stand on Mountain platform A during a strong hit (hitstop) and during the KO
   slow-motion. Record a 120 fps capture. Pass: the feet never slide relative to the platform (P1).
4. **Parallax:** dash across Volcano while zoomed out; watch the Far layer edges (P2).
5. **GC:** Profiler, Memory/GC Alloc column, filter `GC.Alloc` on the main thread during neutral play, then during a
   combo. Pass: 0 B in neutral. List any callers above 0 with their call stacks (enable Call Stacks for GC.Alloc).
6. **HUD cost:** Profiler hierarchy, `GameManager.OnGUI` total ms and calls per frame, before and after P4.
7. **Load hitch:** Profiler, the frame that enters the match. Record `Resources.LoadAll` and `Texture upload` ms. Then
   play the first use of every move once and look for spikes > 4 ms (late uploads).
8. **GPU:** Profiler GPU module (or RenderDoc / PIX), 1080p and 1440p, Volcano (most layers + fire + bloom). Record
   render and post-process ms. Pass: within the GPU budget row.
9. **Memory:** Memory Profiler snapshot after one match, then after playing all five characters. Record texture totals
   and confirm whether the stage PNGs listed in P5 imported as RGBA32 or BC7.
10. **Online sanity:** one online match after F2/F3: confirm nothing changed there (online loops keep the raw delta).

Record results in `docs/performance/` as a dated report against the budgets in section 1.

## 6. 2026-11-12: animation and texture loading hitches (performance-analyst, static, compile_check green)

User report: "graphics getting weird and delays" in fights. Code and asset reading only, Unity not launched.

### Measured from the assets (Resources/Animations, PNG headers + .meta)

| Character | Sheets (Move_/Idle_Sheet, BC7 + mips, max 4096) | Cutouts (BC7, no mips, max 16384) | GPU (BC7 1 B/px, mips x1.33) | Disk |
|---|---|---|---|---|
| Bruna | 22 (21 x 3840x768, 1 x 3840x1024) | 24 | 83.8 + 23.6 = **107 MiB** | 46 MiB |
| Kaia | 23 | 24 | 87.5 + 23.6 = **111 MiB** | 30 MiB |
| Lyra | 22 | 24 | 83.8 + 23.6 = **107 MiB** | 32 MiB |
| Ren | 25 | 24 | 95.0 + 23.6 = **119 MiB** | 27 MiB |
| Taro | 21 | 24 | 80.0 + 23.6 = **104 MiB** | 27 MiB |

- 45-49 textures per character; all dimensions are multiples of 4, so all BC7 (no RGBA32 fallback).
- One match (2 different fighters): **~215-230 MiB** of animation textures, plus the stage. Over the 250 MB budget
  (section 1) once the 3840x2160 stage layers are added.
- Before this fix the cache never released: after all five characters were played **~548 MiB** stayed resident. On 2-4 GB
  GPUs or integrated graphics that overcommits VRAM; driver paging gives stutter and missing / late textures, a likely
  cause of "graphics getting weird".
- Load path before the fix: nothing loaded at match build. The first `FighterVisual` clip lookup (first LateUpdate of
  the match, and online: a sim that is already Playing) ran a synchronous `Resources.LoadAll` of the whole folder plus
  ~650 `Sprite.Create` calls per character, for both fighters. Estimate 100-300 ms per character, so **0.2-0.6 s**
  frozen on the first visible frames. Offline F3 hid it from the sim clock; online it was not hidden (raw delta, catch-up
  burst + rollbacks).
- `Slice()` sprites are created once per character and cached; `Get` / `MoveClipName` are allocation-free. OK.
- Lazy first-use FX textures built on a fight frame: `ChibiLaunchFx` Streak / Puff / Star (3 x 128x128 RGBA32 built
  pixel by pixel + additive material via `Shader.Find`) on the first launch or KO; `ChibiMoveVfx.Crescent` (128x128) on
  the first moveset slash; `ChibiFxTextures` Ray / Glow / Ring / Line on the first hit spark. Estimate 1-5 ms each.
- Per-frame GC in SpriteAnim, ChibiSimView, ChibiBladeTrails, ChibiHitSparks, ChibiLaunchFx, ChibiPostFx,
  StageAtmosphere*: **none found** in the steady state (pools, cached arrays, closures built once). Event-only:
  `int.ToString` of every damage number (fixed), `Fx.Text` / `Fx.Comic` allocate a `Popup` per hit (CombatObjects.cs,
  not changed), `FighterVisual.SpawnKoGhost` creates GameObjects per KO (not changed, FighterVisual was being edited).
- Stage textures (`StageBuilder.Build`, `AtmosFx` atlas) already load synchronously inside `ChibiSimView.Begin`, i.e.
  inside `ChibiMatch.Create`, behind the wipe. Not mid-fight.

### Fixed

| ID | File | Change |
|---|---|---|
| F4 | `Fighter/SpriteAnim.cs` | `SpriteAnimLibrary.Preload(id, warmGpu)` builds the clip table once and warms the textures; `WarmGpu(id)` blits each sheet into a 4x4 temporary RT so the render thread finishes uploads now (no-op with a null device); `Release(id)` / `ReleaseAllExcept(a, b)` destroy the sliced sprites and `Resources.UnloadAsset` the sheets; `LoadCount` / `IsLoaded` diagnostics. `Get` behaviour unchanged (still lazy-loads if nothing preloaded). |
| F5 | `Game/ChibiMatch.cs` | `PreloadAssets(c0, c1)` called at the top of `Create` and before `BuildView` in `CreateOnline`: both fighters' sheets + GPU warm + the shared FX textures. `Start()` (first frame, old match destroyed) releases characters not in this match. |
| F6 | `Game/GameManager.Online.cs` `OnRoomConnected` | Preloads the local fighter before the handshake, so `CreateOnline` (already Playing) only loads the opponent. |
| F7 | `Game/GameManager.cs` `UpdateWipe` | The wipe clock is pushed forward by the time the mid-wipe action blocked, so the reveal half still plays after the preload instead of the wipe vanishing in one frame. |
| F8 | `Netcode/View/ChibiLaunchFx.cs` | `WarmShared()` builds Streak / Puff / Star / additive material (called by F5). |
| F9 | `Netcode/View/ChibiSimView.cs` | Damage popup text from a cached 0..999 string table (`DamageText`), same strings as `int.ToString`. |

Tests: `Assets/Tests/EditMode/Editor/SpriteAnimLibraryTests.cs` (8 tests). Not yet run in Unity.

### Verify with the Profiler (Play Mode, Deep Profile off)

1. Frame that runs `ChibiMatch.Create`: record ms for `Resources.LoadAll`, `Sprite.Create`, `Graphics.Blit` (warm) and
   `Texture.AwakeFromLoad` / upload markers. Expect the whole cost on that one frame, under the covered wipe.
2. First 5 s of the match and the first use of every move, launch and KO: no frame > 2x budget, and no
   `Resources.LoadAll` or `Texture2D.Apply` sample after the match-build frame. `SpriteAnimLibrary.LoadCount` must not
   change after the build frame.
3. Wipe: the reveal half (0.3 s) plays smoothly after the build frame.
4. Memory Profiler: after one match ~215-230 MiB of `Animations/*` textures; after playing all five characters in a
   row it must stay at two characters' worth (was ~548 MiB).
5. Online: frame of `CreateOnline`, ticks run on the next frame (catch-up size) and rollback count during the intro.
6. GPU warm: compare first-draw spikes with `warmGpu: false` (Profiler GPU / render thread). If the Blit shows no gain on
   D3D11/D3D12, it can be dropped; it costs ~47 tiny blits per character.

### Not fixed (owners)

- Cutouts: fights now draw only sheet clips (`FightClips` / `GetSheet`, gameplay-programmer). The 24 cutouts per
  character (23.6 MiB GPU, ~10 MB disk) are still loaded by `Resources.LoadAll`. If fights never draw them, move them to a
  separate folder or load sheets by name (technical-artist + gameplay-programmer).
- Sheet size: ~85-95 MiB of mipmapped 3840x768 sheets per character. Mips on an orthographic 2D game at fixed zoom are
  33% of that; consider mips off or trimmed atlases (P5, technical-artist).
- First KO / ult enables URP chromatic aberration (`ChibiPostFx` punch from 0 to > 0), which switches the UberPost shader
  variant mid-fight. In the Editor that can compile asynchronously (cyan placeholder for a frame); in builds the first
  PSO creation can hitch. Warm with a ShaderVariantCollection / Unity 6 GraphicsStateCollection or keep the keyword on
  (technical-artist, profile first).
- Async loading (`Resources.LoadAsync` per sheet during the wipe and the READY intro) would remove the build-frame
  freeze entirely; needs a known name list (clips.txt + `Clips`), engine-programmer.

## Follow-up measurements (2026-11-14 and 2026-11-17)

Editor Play Mode, Plain, Kaia vs a Bruna CPU, webcam off, vSync on.

**2026-11-14 (fix A):**
- Main thread average is 6.5-6.8 ms on Mountain, Volcano and Ocean, with no frame over 33 ms.
- Memoizing `Bindings` key parsing took game-side GC from 11.5 to 7.8 KB/frame.

**2026-11-17 (fixes B, C and D, review batch 5).** Game-side GC was measured per call site with allocation call stacks over 241 frames, `profileEditor = false`:

| Source | Before | After | Fix |
|---|---|---|---|
| Pose templates and telemetry clone (`Pose.cs`) | ~280 B/frame (11-14) | **0** | B: `SimulatedPoseSource` fills a reused goal buffer, `TelemetryValidator` copies into its previous frame (`PoseFrame.CopyTo`), HUD and menu skeletons use `TemplateCached` |
| Invite expiry (`ChibiSocialManager`) | 148 B/frame (11-14) | **0** | C: `RemoveExpired` reverse loop instead of `RemoveAll(lambda)` |
| `Hud.Outlined` (HUD numbers) | 2,280 B/frame | **1,352 B/frame** | D: 4 diagonal outline copies instead of 8; screenshots `production/qa/evidence/2026-11-17-batch5-hud-outline-before/after.png` look the same |

- **Total game-side GC:** 10,555 → 7,788 B/frame. Part of that gap is window-to-window variance.
- **Main thread:** about 6.8-6.9 ms (unchanged).
- **Editor frame average** ("GC Allocated In Frame", editor included): it varies by about ±2 KB between identical runs (38.9 vs 42.7 KB), so it cannot show savings this small. Use the per-call-site method above.
- **Regression guards:** `PerfAllocationTests` (EditMode, `Is.Not.AllocatingGCMemory`).
- **Still open:** the other IMGUI label costs (`PlayerPanel`, `Hints`, `CvPanel`), which allocate inside Unity.
