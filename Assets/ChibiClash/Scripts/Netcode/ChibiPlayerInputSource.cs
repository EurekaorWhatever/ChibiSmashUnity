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
    /// (Jump, Light, Heavy, Dash, Skill1-3, Ult) until the next <see cref="SampleTick"/>, so a tap shorter than
    /// one 60 Hz tick is never lost. Held state (MoveX, Down, Block) keeps the newest polled value.
    ///
    /// SampleTick with no PollRenderFrame since the previous sample (catch-up ticks in one render frame) REUSES the
    /// last polled held values and does not re-poll: re-polling would tick the tracker twice in one frame and
    /// re-read the same frame's wasPressedThisFrame edges. Latches were cleared by the previous sample, so a press
    /// is delivered on exactly one tick.
    /// </summary>
    public sealed class ChibiPlayerInputSource : IChibiInputSource
    {
        readonly FighterInput _device;
        readonly Func<Gesture> _gesture;
        readonly Func<bool> _poseVerified;
        readonly Func<float> _deltaTime;
        readonly GestureTracker _tracker;   // owned and ticked by this source when built by ForPlayer; else null

        // Held state (latest poll).
        float _moveX;
        bool _down, _block;
        // One-shot latches, OR-ed across render frames until the next SampleTick.
        ChibiButtons _latched;

        /// <summary>
        /// Build a source from explicit dependencies (DI / tests).
        /// </summary>
        /// <param name="device">Device ticked once per render frame with <c>self = null</c>.</param>
        /// <param name="gesture">Current gesture, read at sample time.</param>
        /// <param name="poseVerified">True when the gesture came from a live, telemetry-valid camera pose.</param>
        /// <param name="deltaTime">Render-frame delta time passed to the device (and the owned tracker, if any).</param>
        public ChibiPlayerInputSource(FighterInput device, Func<Gesture> gesture, Func<bool> poseVerified, Func<float> deltaTime)
            : this(device, gesture, poseVerified, deltaTime, null) { }

        ChibiPlayerInputSource(FighterInput device, Func<Gesture> gesture, Func<bool> poseVerified, Func<float> deltaTime,
                               GestureTracker tracker)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _gesture = gesture ?? (() => Gesture.None);
            _poseVerified = poseVerified ?? (() => false);
            _deltaTime = deltaTime ?? (() => 0f);
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
        /// is running. Gesture = tracker.Stable (what Fighter reads); verified = tracker.IsLive &amp;&amp; TelemetryValid.
        /// The tracker is ticked inside <see cref="PollRenderFrame"/> with Time.unscaledDeltaTime, as Fighter does.
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
            return new ChibiPlayerInputSource(
                input,
                () => tracker.Stable,
                () => tracker.IsLive && tracker.TelemetryValid,
                () => Time.unscaledDeltaTime,
                tracker);
        }

        /// <summary>Poll the device once for this render frame, tick the owned tracker, and latch one-shot presses.</summary>
        public void PollRenderFrame()
        {
            float dt = _deltaTime();
            _device.Tick(null, dt);
            _tracker?.Tick(dt);

            _moveX = _device.MoveX;
            _down = _device.Down;
            _block = _device.Block;

            if (_device.Jump) _latched |= ChibiButtons.Jump;
            if (_device.Light) _latched |= ChibiButtons.Light;
            if (_device.Heavy) _latched |= ChibiButtons.Heavy;
            if (_device.Dash) _latched |= ChibiButtons.Dash;
            if (_device.Skill[0]) _latched |= ChibiButtons.Skill1;
            if (_device.Skill[1]) _latched |= ChibiButtons.Skill2;
            if (_device.Skill[2]) _latched |= ChibiButtons.Skill3;
            if (_device.Ult) _latched |= ChibiButtons.Ultimate;
        }

        /// <summary>
        /// Build this tick's packet from latched presses, the latest held state and the current gesture, then clear
        /// the latches. PoseVerified is set only when the gesture is not None and comes from a verified pose.
        /// </summary>
        public ChibiInputPacket SampleTick()
        {
            ChibiButtons b = _latched;
            _latched = ChibiButtons.None;
            if (_block) b |= ChibiButtons.Shield;

            Gesture g = _gesture();
            if (g != Gesture.None && _poseVerified()) b |= ChibiButtons.PoseVerified;

            int x = _moveX > 0f ? 1 : (_moveX < 0f ? -1 : 0);
            return ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(x, _down ? -1 : 0), b, g);
        }
    }
}
