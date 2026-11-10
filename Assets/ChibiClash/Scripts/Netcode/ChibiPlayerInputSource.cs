using System;
using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Feeds the deterministic sim from the ORIGINAL game's input devices: a <see cref="FighterInput"/>
    /// (normally <see cref="HumanInput"/>: rebindable Bindings + gamepad + P2 numpad) plus a gesture source
    /// (camera tracker or the simulated gesture keys). Input side only: no gameplay decisions are made here.
    ///
    /// Render frames: <see cref="PollRenderFrame"/> ticks the device once and LATCHES one-shot presses
    /// (Jump, Light, Heavy, Special, Dash, Skill1-3, Ult) until the next <see cref="SampleTick"/>, so a tap shorter than
    /// one 60 Hz tick is never lost. Held state (MoveX, Up, Down, Block, and the optional held level of Heavy / Special /
    /// Skill1-3 used by charge and hold moves, rules v18) keeps the newest polled value; Block is also latched
    /// (damage-conversion.md §7) so a shield tap released before the next sample still yields one tick of Shield.
    ///
    /// SampleTick with no PollRenderFrame since the previous sample (catch-up ticks in one render frame) REUSES the
    /// last polled held values and does not re-poll: re-polling would tick the tracker twice in one frame and
    /// re-read the same frame's wasPressedThisFrame edges. Latches were cleared by the previous sample, so a press
    /// is delivered on exactly one tick.
    ///
    /// Protocol 3 (CH-3): bit 15 is the Special (B) button; the retired PoseVerified hint is no longer produced.
    /// </summary>
    public sealed class ChibiPlayerInputSource : IChibiInputSource
    {
        /// <summary>Buttons whose held level is sent while held (charge / hold moves read it, movesets README 1.2).</summary>
        public const ChibiButtons HeldLevelButtons =
            ChibiButtons.Heavy | ChibiButtons.Special | ChibiButtons.Skill1 | ChibiButtons.Skill2 | ChibiButtons.Skill3;

        readonly FighterInput _device;
        readonly Func<Gesture> _gesture;
        readonly Func<float> _deltaTime;
        readonly Func<ChibiButtons> _held;
        readonly GestureTracker _tracker;   // owned and ticked by this source when built by ForPlayer; else null

        // Held state (latest poll).
        float _moveX;
        bool _down, _up, _block;
        ChibiButtons _heldLevel;
        // One-shot latches, OR-ed across render frames until the next SampleTick.
        ChibiButtons _latched;
        // True if Block was down in any render frame since the last SampleTick (damage-conversion.md §7).
        bool _shieldLatch;

        /// <summary>Build a source from explicit dependencies (DI / tests).</summary>
        /// <param name="device">Device ticked once per render frame with <c>self = null</c>.</param>
        /// <param name="gesture">Current gesture, read at sample time.</param>
        /// <param name="deltaTime">Render-frame delta time passed to the device (and the owned tracker, if any).</param>
        /// <param name="held">
        /// Optional held level of the charge buttons (only <see cref="HeldLevelButtons"/> bits are used), polled once per
        /// render frame. Null = presses only (a charge move then releases on the tick after its press).
        /// </param>
        public ChibiPlayerInputSource(FighterInput device, Func<Gesture> gesture, Func<float> deltaTime,
                                      Func<ChibiButtons> held = null)
            : this(device, gesture, deltaTime, held, null) { }

        ChibiPlayerInputSource(FighterInput device, Func<Gesture> gesture, Func<float> deltaTime, Func<ChibiButtons> held,
                               GestureTracker tracker)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _gesture = gesture ?? (() => Gesture.None);
            _deltaTime = deltaTime ?? (() => 0f);
            _held = held;
            _tracker = tracker;
        }

        /// <summary>The device this source polls (read-only use, e.g. HUD display).</summary>
        public FighterInput Device => _device;

        /// <summary>The gesture tracker this source owns and ticks, or null when built via the DI constructor.</summary>
        public GestureTracker Tracker => _tracker;

        /// <summary>
        /// Same device and gesture source the original game gives player <paramref name="player"/>
        /// (MatchController.SpawnFighter, human branch): <c>new HumanInput(player)</c>, a SimulatedPoseSource driven
        /// by <see cref="FighterInput.SimulatedGesture"/>/<see cref="FighterInput.Slouching"/>, wrapped in an
        /// AutoPoseSource that switches to the real camera for P1 when the webcam setting is on and the pose model
        /// is running. Gesture = tracker.Stable when the telemetry is valid, else None.
        /// The tracker is ticked inside <see cref="PollRenderFrame"/> with Time.unscaledDeltaTime, as Fighter does.
        /// Held levels of Heavy / Special / Skill1-3 come from the same bindings and pad buttons HumanInput presses.
        /// </summary>
        /// <param name="player">0-based player index.</param>
        public static ChibiPlayerInputSource ForPlayer(int player)
        {
            var input = new HumanInput(player);
            var sim = new SimulatedPoseSource("P" + (player + 1) + " simulated", () => input.SimulatedGesture, () => input.Slouching);
            // Looked up once (including inactive: the sim test match deactivates the GameManager).
            var gm = UnityEngine.Object.FindAnyObjectByType<GameManager>(FindObjectsInactive.Include);
            Func<bool> wantCamera = () => gm != null && gm.Save != null && gm.Save.settings.useWebcam;
            var tracker = new GestureTracker(new AutoPoseSource(player, sim, wantCamera));
            // A gesture the TelemetryValidator rejects is sent as None: the original ignores it for blocking, poses and
            // the ult (F:261, F:373, F:413). Input-side UX filter only, never a security gate (security review rule 2).
            return new ChibiPlayerInputSource(
                input,
                () => tracker.TelemetryValid ? tracker.Stable : Gesture.None,
                () => Time.unscaledDeltaTime,
                () => HeldLevel(player),
                tracker);
        }

        /// <summary>Held level of the charge buttons for <paramref name="player"/>: bindings, P2 numpad and pad N (HumanInput layout).</summary>
        static ChibiButtons HeldLevel(int player)
        {
            ChibiButtons b = ChibiButtons.None;
            if (Bindings.Held(player, BindAction.Heavy)) b |= ChibiButtons.Heavy;
            if (Bindings.Held(player, BindAction.Special)) b |= ChibiButtons.Special;
            if (Bindings.Held(player, BindAction.Skill1)) b |= ChibiButtons.Skill1;
            if (Bindings.Held(player, BindAction.Skill2)) b |= ChibiButtons.Skill2;
            if (Bindings.Held(player, BindAction.Skill3)) b |= ChibiButtons.Skill3;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (player == 1 && kb != null)
            {
                if (kb.numpad2Key.isPressed) b |= ChibiButtons.Heavy;
                if (kb.numpadPlusKey.isPressed) b |= ChibiButtons.Special;
                if (kb.numpad4Key.isPressed) b |= ChibiButtons.Skill1;
                if (kb.numpad5Key.isPressed) b |= ChibiButtons.Skill2;
                if (kb.numpad6Key.isPressed) b |= ChibiButtons.Skill3;
            }
            var pads = UnityEngine.InputSystem.Gamepad.all;
            var gp = player < pads.Count ? pads[player] : null;
            if (gp != null)
            {
                if (gp.buttonNorth.isPressed) b |= ChibiButtons.Heavy;
                if (gp.buttonEast.isPressed) b |= ChibiButtons.Special;
                if (gp.leftShoulder.isPressed) b |= ChibiButtons.Skill1;
                if (gp.rightShoulder.isPressed) b |= ChibiButtons.Skill2;
                if (gp.leftTrigger.isPressed) b |= ChibiButtons.Skill3;
            }
            return b;
        }

        /// <summary>Poll the device once for this render frame, tick the owned tracker, and latch one-shot presses.</summary>
        public void PollRenderFrame()
        {
            float dt = _deltaTime();
            _device.Tick(null, dt);
            _tracker?.Tick(dt);

            _moveX = _device.MoveX;
            _down = _device.Down;
            _up = _device.Up;
            _block = _device.Block;
            if (_block) _shieldLatch = true;
            _heldLevel = _held != null ? _held() & HeldLevelButtons : ChibiButtons.None;

            if (_device.Jump) _latched |= ChibiButtons.Jump;
            if (_device.Light) _latched |= ChibiButtons.Light;
            if (_device.Heavy) _latched |= ChibiButtons.Heavy;
            if (_device.Special) _latched |= ChibiButtons.Special;
            if (_device.Dash) _latched |= ChibiButtons.Dash;
            if (_device.Skill[0]) _latched |= ChibiButtons.Skill1;
            if (_device.Skill[1]) _latched |= ChibiButtons.Skill2;
            if (_device.Skill[2]) _latched |= ChibiButtons.Skill3;
            if (_device.Ult) _latched |= ChibiButtons.Ultimate;
        }

        /// <summary>
        /// Build this tick's packet from latched presses, the latest held state and the current gesture, then clear
        /// the latches. Shield = Block held now OR seen down since the last sample (damage-conversion.md §7), so a
        /// sub-tick tap gives exactly one tick of Shield. Direction: Down wins over Up (aim up never jumps, CH-3).
        /// </summary>
        public ChibiInputPacket SampleTick()
        {
            ChibiButtons b = _latched | _heldLevel;
            _latched = ChibiButtons.None;
            if (_block || _shieldLatch) b |= ChibiButtons.Shield;
            _shieldLatch = false;

            int x = _moveX > 0f ? 1 : (_moveX < 0f ? -1 : 0);
            int y = _down ? -1 : (_up ? 1 : 0);
            return ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(x, y), b, _gesture());
        }
    }
}
