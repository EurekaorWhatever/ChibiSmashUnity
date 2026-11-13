// Story type: Logic (fight-view sheet animation timing) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Source: fight view shows moveset-sheet clips only; review 2026-11-13 items 3, 4 and 7 (victory hop in sheet mode,
// Hurt() restarting looping Guard / Idle on shielded or armoured hits, timing exercised with an injected dt).
using NUnit.Framework;
using UnityEngine;

namespace ChibiClash.Tests
{
    /// <summary>
    /// FighterVisual in fight mode (EnableSheetOnly) driven on an explicit clock (Tick with dt / now), so clip looping,
    /// one-shot completion, the return to the state clip, the strike-frame remap, Hurt() and the victory hop are all
    /// exercised in EditMode, where Time.deltaTime is 0. One real character (its Resources sheets) per test.
    /// </summary>
    public class FighterVisualSheetTests
    {
        const FighterClass Cls = FighterClass.Melee;
        const float Dt = 0.1f;

        GameObject _go;
        FighterVisual _v;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("FighterVisualSheetTests");
            _v = _go.AddComponent<FighterVisual>();
            _v.Build(ClassDef.Get(Cls), Skin.Default(Cls), 10, WeaponDef.Get(Cls, 0));
            _v.EnableSheetOnly();
            Assert.IsTrue(_v.SheetOnly, "precondition: " + _v.CharacterId + " has drawn art and sheet clips");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
        }

        void TickIdle(float dt, float now = 0f) => _v.Tick(Vector2.zero, true, false, FightFlags.None, dt, now);
        void TickGuard(float dt) => _v.Tick(Vector2.zero, true, true, FightFlags.None, dt, 0f);
        void TickHitstun(float dt) => _v.Tick(Vector2.zero, true, false, FightFlags.Hitstun, dt, 0f);

        SpriteClip Sheet(string name) => SpriteAnimLibrary.GetSheet(_v.CharacterId, name);

        [Test]
        public void IdleLoop_PastItsLength_WrapsToTheFirstFrames()
        {
            // Arrange
            string idleName = _v.StateClip(FightAnimState.Idle);
            var idle = Sheet(idleName);
            Assert.IsTrue(idle.loop || FightClips.LoopsClip(idleName), "precondition: idle loops");
            int n = idle.frames.Length;

            // Act: half a frame past the end of the clip, then one more frame
            TickIdle((n + 0.5f) / idle.fps);
            var wrapped = _v.BodyRenderer.sprite;
            TickIdle(1f / idle.fps);
            var next = _v.BodyRenderer.sprite;

            // Assert
            Assert.AreEqual(idleName, _v.ClipName);
            Assert.AreSame(idle.frames[0], wrapped, "loop did not wrap to frame 0");
            Assert.AreSame(idle.frames[1 % n], next);
        }

        [Test]
        public void MoveClip_WithPhases_StrikeFrameLandsOnFirstActiveFrame()
        {
            // Arrange
            string moveId = FightClips.CharKey(_v.CharacterId) + "_" + FightClips.GenericRow;
            var clip = Sheet(SpriteAnimLibrary.MoveClipName(moveId));
            Assert.IsNotNull(clip, "precondition: " + moveId + " sheet");
            Assert.IsTrue(clip.HasPhases, "precondition: clips.txt gives strike / recover frames");
            const float startup = 0.3f, active = 0.1f, recovery = 0.2f;

            // Act
            _v.PlayMoveClip(moveId, "swing", startup, active, recovery);
            TickIdle(startup);

            // Assert: exactly the strike frame at the first active frame (not a linear stretch over the whole move)
            Assert.AreEqual(clip.name, _v.ClipName);
            Assert.AreSame(clip.frames[clip.strikeFrame], _v.BodyRenderer.sprite);
        }

        [Test]
        public void MoveClip_AfterItsDuration_ReturnsToTheStateClip()
        {
            // Arrange
            string moveId = FightClips.CharKey(_v.CharacterId) + "_" + FightClips.GenericRow;
            string moveClip = SpriteAnimLibrary.MoveClipName(moveId);
            string idleName = _v.StateClip(FightAnimState.Idle);
            _v.PlayMoveClip(moveId, "swing", 0.1f, 0.1f, 0.2f);

            // Act
            TickIdle(Dt);
            string during = _v.ClipName;
            TickIdle(0.35f);   // 0.45 s > 0.4 s move

            // Assert
            Assert.AreEqual(moveClip, during, "the move clip plays while the move lasts");
            Assert.AreEqual(idleName, _v.ClipName, "one-shot finished: back to the state clip");
            Assert.AreEqual(idleName, _v.BodyRenderer.sprite.texture.name);
        }

        [Test]
        public void Hurt_ShieldedHitWhileGuarding_DoesNotRestartGuardLoop()
        {
            // Arrange
            TickGuard(Dt);
            TickGuard(Dt);
            float before = _v.ClipTime;

            // Act
            _v.Hurt();
            TickGuard(Dt);

            // Assert
            Assert.AreEqual(FightAnimState.Guard, _v.FightState);
            Assert.AreEqual(_v.StateClip(FightAnimState.Guard), _v.ClipName);
            Assert.AreEqual(before + Dt, _v.ClipTime, 1e-4f, "guard loop restarted on a shielded hit");
        }

        [Test]
        public void Hurt_ArmouredHitWhileIdle_DoesNotRestartIdleLoop()
        {
            // Arrange
            TickIdle(Dt);
            TickIdle(Dt);
            float before = _v.ClipTime;

            // Act: hit event without hitstun (armour)
            _v.Hurt();
            TickIdle(Dt);

            // Assert
            Assert.AreEqual(FightAnimState.Idle, _v.FightState);
            Assert.AreEqual(before + Dt, _v.ClipTime, 1e-4f, "idle loop restarted on an armoured hit");
        }

        [Test]
        public void Hurt_NewHitDuringHitstun_RestartsTheHitClip()
        {
            // Arrange
            TickHitstun(Dt);
            TickHitstun(Dt);
            Assert.Greater(_v.ClipTime, Dt * 1.5f, "precondition: hit clip already running");

            // Act
            _v.Hurt();
            TickHitstun(Dt);

            // Assert
            Assert.AreEqual(FightAnimState.Hitstun, _v.FightState);
            Assert.AreEqual(_v.StateClip(FightAnimState.Hitstun), _v.ClipName);
            Assert.AreEqual(Dt, _v.ClipTime, 1e-4f, "a new hit in hitstun replays the hit clip from its start");
        }

        [Test]
        public void Victory_SheetMode_WinnerHopsAboveTheIdlePose()
        {
            // Arrange: the hop peaks at |sin(now * rate)| = 1; now = pi / 12 is a peak for the 6 rad/s hop
            const float peak = Mathf.PI / 12f;
            TickIdle(Dt, peak);
            float idleY = _v.BodyRenderer.transform.position.y;

            // Act
            _v.SetVictory(true);
            _v.Tick(Vector2.zero, true, false, FightFlags.None, Dt, peak);
            float hopY = _v.BodyRenderer.transform.position.y;
            Vector3 hopScale = _v.BodyRenderer.transform.lossyScale;
            _v.Tick(Vector2.zero, true, false, FightFlags.None, Dt, 0f);   // bottom of the hop
            float groundY = _v.BodyRenderer.transform.position.y;

            // Assert
            Assert.AreEqual(FightAnimState.Victory, _v.FightState);
            Assert.Greater(hopY - idleY, 0.1f, "the winner's sheet pose did not hop");
            Assert.AreEqual(idleY, groundY, 1e-4f, "the hop must not accumulate between frames");
            Assert.AreNotEqual(_v.BodyRenderer.transform.lossyScale.y, hopScale.y, "no squash / stretch over the hop");
        }
    }
}
