# Mode 3 (Player vs AI, offline): AI as an IChibiInputSource

Status: design note, DRAFT. No project files changed. Paths are relative to `Assets/ChibiClash/Scripts/`.

## 1. What exists today

- **`CpuInput`** (`Fighter/FighterInput.cs:138-228`) is the only state-reading AI. It drives the **live Fighter +
  Rigidbody2D path**. `MatchController.SpawnFighter` creates it (`Game/MatchController.cs:91-92`) and
  `Fighter.Update` calls it (`Fighter/Fighter.cs:173`, `Controls.Tick(this, dt)`). It reads `Fighter` and transform
  floats directly, uses `UnityEngine.Random` (lines 176, 182-183, 196-203, 216-217) and `dt`, and fakes the ult
  gesture through `SimulatedGesture`. It is nondeterministic and cannot be ported as it is. Difficulty comes from
  `SaveData.cpuDifficulty` (`Core/SaveData.cs:77`, set at `Game/GameManager.cs:115`), 0 = training dummy.
- **`ChibiNetSelfTest.Bot`** (`Netcode/ChibiTransports.cs:214-232`) is a hash-of-frame button masher fed through
  `ChibiScriptedInputSource` (`:104-111`). It does drive `ChibiSimulation`, but it never reads state. It is a test
  fixture, not an opponent.
- **Nothing outside `Netcode/` references `ChibiClash.Netcode`** (checked with grep). `ChibiSimulation`, `ChibiP2PPeerLoop`
  and `ChibiReplay` are not wired into any shipped mode. The "RANKED ONLINE" card is disabled (`GameManager.cs:288`).

## 2. Offline topology. Recommendation: (A) direct step

**(A) `ChibiSimulation.Step` with two `IChibiInputSource`s, no session.** The online input path is
`source.SampleTick()` -> `ChibiInputPacket` -> `ChibiSimulation.Step(state, in0, in1)` (`ChibiP2PPeerLoop.cs:148,176-178`).
Rollback is only transport and prediction stacked on top. (A) uses that same path with nothing stacked on it, so the
combat and hitbox code is byte-for-byte the same.
- Exists: `Init/Step`, `ChibiSimState.CopyFrom`, `ChibiReplayRecorder.Add(frame,p0,p1)` (callable directly), and
  `ChibiReplay.Simulate` (this is (A) without sources). The 60 Hz accumulator pattern exists in `ChibiP2PPeerLoop.Update` (`:393-409`).
- Missing: a `ChibiLocalLoop` MonoBehaviour host (about 50 lines, mirrors `ChibiP2PPeerLoop`), `ChibiAiInputSource`, a
  Bindings-aware human source (`ChibiKeyboardInputSource` hardcodes WASD/JKL and ignores `Core/Bindings.cs`),
  and the sim->view render bridge (missing for online too).

**(B) `ChibiRollbackSession` over `ChibiLoopbackLink(0,0,0)`.** Rejected for shipping:
- A session takes **one** local source (`ctor :109`). Getting two players means **two sessions**, as `ChibiNetSelfTest`
  does (`:170-185`). That simulates every frame twice and runs rollback and checksum machinery that does nothing useful offline.
- `InputDelay = 2` is a `const` (`:47`), so the human gets 2 frames of input lag in an offline mode for no reason.
- `session.State` can be a *predicted* frame, so an AI in session B would react to speculative state.
- What (B) is good for: as an **EditMode test**, it shows that the AI source behaves the same under netcode. That harness already
  exists as `ChibiNetSelfTest`. Swap one `Bot` for `ChibiAiInputSource` and it runs.

## 3. `ChibiAiInputSource` design

**State access.** `SampleTick()` takes no arguments, so the host injects a **read-only snapshot**. After each
`Step`, the host does `_view.CopyFrom(_state)`. At `SampleTick` for frame T, the AI therefore sees the state at the start of T,
which is the result of the previous confirmed tick. In (A) every tick is confirmed. The copy is about 2 fighters + 24 projectiles of ints,
which is negligible. The AI never holds the live `ChibiSimState`, because `ChibiFighterSim` is public and mutable.

**Reaction delay (state queue).** Each tick, reduce the view to a small `Percept` struct (opponent X/Y/Vx/Vy,
MoveSlot/MoveTick, Shielding, Stun, nearest incoming projectile dx/dy, and the AI's own Hp/Ult/Cd/Grounded/Facing). Push it
into a ring of `MaxReaction+1` entries. Decisions about the **opponent** read `ring[T - reactionFrames]`. The AI's **own**
state comes from the current entry, since a player knows their own character's state instantly. Add jitter: `reactionFrames = base + rng.Range(jitter+1)`,
redrawn once per decision.

**Determinism.**
- The AI owns its RNG: `new ChibiRng(cfg.Seed ^ 0xA15EED00u ^ (uint)player)`. It must **never touch `state.Rng`**. Reading
  it copies the struct, but any `ref` use would advance the sim stream, and the replay would then diverge, because a replay re-runs
  inputs only and never runs the AI.
- No `UnityEngine.Random`, `System.Random`, `Time.*` or floats. Decisions happen on a fixed tick cadence. Weights are in permille.
- Note: `ChibiReplay` stores *inputs*. A replay reproduces exactly even if the AI were nondeterministic, as long as the AI
  never mutates sim state. AI determinism is still required for golden-input unit tests, bug repros
  ("seed X, difficulty 2, frame 3012") and AI-vs-AI soak tests.

**Input semantics.** The packet holds *state*, and the sim derives press edges from `PrevInput`. To press twice, the AI must
**release for at least 1 tick** between presses. A held Light is one press plus buffer. The AI emits through a tiny "press for N ticks, then release"
helper, so it does not toggle bits by hand.

**Behaviour layer.** Use a small utility selector on a decision cadence (no full BT needed yet). It scores
Approach / Space (ranged) / Poke / Punish (opponent in `MoveRecovery`) / Block (incoming active hitbox or projectile in
range) / Skill (Cd==0) / Ult (`Ult >= UltMax`) / Recover (|X| > StageHalfWidth). The highest score wins. `mistakePermille` swaps in
the 2nd-best option or drops the press. Recovery always takes priority over everything else, so the AI is never stuck offstage.

```csharp
/// <summary>Tuning for one difficulty step. Integers only (permille / ticks) so the AI stays deterministic.</summary>
[Serializable] public struct ChibiAiTuning {
    public int ReactionFrames, ReactionJitter;   // perceive opponent this many ticks late
    public int DecisionInterval;                 // re-plan every N ticks
    public int MistakePermille;                  // pick 2nd-best / drop the press
    public int AggressionPermille;               // bias toward Approach/Poke vs Space/Block
    public int BlockPermille, PunishPermille;    // chance to react to a seen threat / opening
}
public sealed class ChibiAiInputSource : IChibiInputSource {
    public ChibiAiInputSource(int player, ChibiSimState view, in ChibiAiTuning t, uint seed) { /* ring, rng */ }
    public void PollRenderFrame() { }            // AI has no render-rate state
    public ChibiInputPacket SampleTick() { Perceive(); if (--_replan <= 0) Decide(); return Emit(); }
}
// ChibiLocalLoop tick (host owns order; AI reads _view == start of this frame):
var p0 = _src0.SampleTick(); var p1 = _src1.SampleTick();
ChibiSimulation.Step(_state, p0, p1); _rec.Add(_state.Frame - 1, p0, p1); _view.CopyFrom(_state);
```

**Data.** Use a `ChibiAiProfile : ScriptableObject` with one `ChibiAiTuning` per difficulty (0 = dummy, emits neutral). It is converted
to the plain struct at match start, so EditMode tests need no assets. The values are placeholders until systems-designer specifies the
difficulty curve. That decision is not the AI programmer's to make.

## 4. Risks

1. **"Same combat code" is false today.** Players fight on `Fighter` + `Rigidbody2D` + `Time.time` (`Fighter.cs:68,118,130...`,
   `MatchController.cs:87-98`). `ChibiSimulation` is an independent integer reimplementation that nothing renders. Mode 3
   on the sim needs the render bridge and a `MatchController` branch first. Until then, a `ChibiAiInputSource` can only be
   exercised headless.
2. **Feature parity gaps between the two paths.** `FighterInput` has Dash and Down. `ChibiButtons` has no Dash bit. Ult is
   gesture-gated (`UltWindow`, `SimulatedGesture`) in Fighter but a plain button in the sim (gesture id would use reserved bits 12-15).
   Neither stage selection (`ChibiMatchConfig` has no stage, fixed `StageHalfWidth`) nor training revive/hitstop/cinematic
   `Time.timeScale` exist in the sim. Rewards and quests read `Fighter` stats (`MatchController.cs:196-225`). Owners:
   gameplay-programmer and game-designer need to decide.
3. **State visibility.** `IChibiInputSource` carries no state, so the AI relies on host injection. In (B) or any online bot,
   `session.State` may be predicted and confirmed snapshots are not exposed (`_snap` is private). An AI there would act on
   rolled-back futures.
4. **Nondeterminism leaks.** The ones to watch: a ported `CpuInput` bringing `Random.value` along; float distance math; iterating
   `Proj[]` in a non-fixed order; advancing `state.Rng`. Mitigation: AI-vs-AI test that runs the same seed twice and asserts the
   input streams are equal, plus `ChibiReplay.Verify`.
5. **Replay metadata.** The CCRP v1 header has no difficulty or "side is AI" field. Playback is fine, but labelling and repro
   are not. A version bump is needed if wanted.
6. **Exploitability and feel.** With Percept reading exact sim ints, the AI can see frame-1 startup. Reaction delay must
   always be at least 1 tick (clamp), and telegraphing comes from the delay ring, not from cheating.

## Open questions (for the user)
- Should Mode 3 wait for the sim->view bridge, or should a deterministic rewrite of `CpuInput` ship on the Fighter path first?
- Is a Dash bit and a gesture-id field in `ChibiInputPacket` in scope (WireBits 12 -> 16)?
- Where are the AI tuning files stored (`Assets/ChibiClash/Data/AI/`?), and who owns the difficulty values?
