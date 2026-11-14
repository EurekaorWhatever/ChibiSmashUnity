// Story type: Logic (gesture controls in the deterministic sim, port step 15) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Source spec: docs/architecture/netcode/gesture-conversion.md; security rules: docs/architecture/netcode/security-gesture-review.md
// section 2 rules 1-6. Original: Fighter.cs F:169-197 (Update order), F:258-266 (CrossArms block, Ult press), F:371-392
// (poses), F:396-434 (ult window), Pose.cs:174-197 (StableTime / JustStarted).
//
// Tick convention: one ChibiSimulation.Step = one real tick = one game step here (no hitstop or cinematic is pending
// unless a test says so). "Tick k" = the k-th Step counted from 0 after the fixture.
using System;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>Gesture-driven input (rules v15): ult window, CrossArms block, Super Jump, Pose Fireball, anti-cheat rules.</summary>
    public class ChibiGestureParityTests
    {
        const uint Seed = 1234;
        const int Settle = 90;
        static readonly ChibiInputPacket N = default;

        // ================================================================== fixture

        /// <summary>Both fighters landed on Sky Dojo, F[0] at x0 facing +1, F[1] at x1 facing -1, idle, meters empty.</summary>
        static ChibiSimState Arena(FighterClass c0, FighterClass c1, int x0 = -3000, int x1 = 3000)
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, new ChibiMatchConfig { Seed = Seed, Class0 = c0, Class1 = c1, Stage = 0 });
            for (int t = 0; t < Settle; t++) ChibiSimulation.Step(s, N, N);
            Place(s.F[0], x0, 1);
            Place(s.F[1], x1, -1);
            for (int t = 0; t < 3; t++) ChibiSimulation.Step(s, N, N);
            return s;
        }

        static void Place(ChibiFighterSim f, int x, int facing)
        {
            f.X = x; f.Y = 0; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Grounded = true; f.Facing = facing;
            f.Stun = 0; f.Invuln = 0;
        }

        static ChibiInputPacket G(Gesture g, ChibiButtons b = ChibiButtons.None) => ChibiInputPacket.Make(ChibiDir.Neutral, b, g);
        static ChibiInputPacket B(ChibiButtons b) => ChibiInputPacket.Make(ChibiDir.Neutral, b);

        static void Step(ChibiSimState s, ChibiInputPacket a, ChibiInputPacket b = default) => ChibiSimulation.Step(s, a, b);

        static Gesture UltGestureOf(FighterClass c) => (Gesture)ChibiSimConst.UltGesture[(int)c];

        /// <summary>
        /// Fill the meter and open the gesture window by fixture injection, in the state the v15 Ult press left it (180
        /// real ticks, Preparing). Since rules v22 (UL-1) the Ult press fires the ult directly and nothing in play opens
        /// the window; the window code stays for a future webcam-ult option, so these tests keep it correct.
        /// </summary>
        static void OpenWindow(ChibiSimState s, int who = 0)
        {
            var f = s.F[who];
            f.Ult = ChibiSimConst.UltMax;
            f.UltWindowTicks = ChibiSimConst.UltWindowTicks;
            f.UltPrompt = (int)ChibiUltPrompt.Preparing;
            f.UltPromptTicks = 0;
        }

        static int CountProj(ChibiSimState s, ChibiProjKind k)
        {
            int n = 0;
            foreach (var p in s.Proj) if (p.Active && p.Kind == k) n++;
            return n;
        }

        // ================================================================== class data

        [Test]
        public void UltGestureTable_MatchesClassData_ForEveryClass()
        {
            foreach (FighterClass c in Enum.GetValues(typeof(FighterClass)))
                Assert.AreEqual(ClassDef.Get(c).ultGesture, UltGestureOf(c), $"{c}: sim ult gesture != ClassData.ultGesture");
        }

        [Test]
        public void RulesVersion_IsAtLeast15()
        {
            Assert.GreaterOrEqual(ChibiMatchConfig.RulesVersion, 15);
        }

        // ================================================================== Ult button (rules v22, UL-1)

        [TestCase(FighterClass.Melee)] [TestCase(FighterClass.Heavy)] [TestCase(FighterClass.Ranged)]
        [TestCase(FighterClass.Magic)] [TestCase(FighterClass.Polearm)]
        public void UltPress_FullMeter_FiresOnTheSameStep_NoWindow(FighterClass c)
        {
            var s = Arena(c, FighterClass.Melee, -3000, 12000);
            s.F[0].Ult = ChibiSimConst.UltMax;
            Step(s, B(ChibiButtons.Ultimate));
            var f = s.F[0];
            Assert.AreEqual(1, f.UltsUsed, $"{c}: the key press alone fires the ult (no gesture needed)");
            Assert.AreEqual(0, f.Ult, "meter spent");
            Assert.AreEqual(0, f.UltWindowTicks, "no gesture window opens");
            Assert.AreEqual((int)ChibiUltPrompt.Hidden, f.UltPrompt, "no gesture 'move detected' panel for a key press");
            Assert.AreEqual(0, s.CinematicFighter, "ult cinematic started");
        }

        [Test]
        public void UltPress_MeterNotFull_NothingHappens()
        {
            var s = Arena(FighterClass.Melee, FighterClass.Melee);
            s.F[0].Ult = ChibiSimConst.UltMax - 1;
            Step(s, B(ChibiButtons.Ultimate));
            Assert.AreEqual(0, s.F[0].UltWindowTicks);
            Assert.AreEqual(0, s.F[0].UltsUsed);
            Assert.AreEqual((int)ChibiUltPrompt.Hidden, s.F[0].UltPrompt);
        }

        [Test]
        public void UltPress_WhileDashing_FiresAndEndsTheDash()
        {
            var s = Arena(FighterClass.Heavy, FighterClass.Melee, -3000, 12000);
            Step(s, B(ChibiButtons.Dash));
            Step(s, N);
            Assert.Greater(s.F[0].DashTicks, 0, "precondition: dashing");
            s.F[0].Ult = ChibiSimConst.UltMax;
            Step(s, B(ChibiButtons.Ultimate));
            Assert.AreEqual(1, s.F[0].UltsUsed);
            Assert.AreEqual(0, s.F[0].DashTicks, "the ult start interrupts the dash");
            Assert.AreEqual(5, s.F[0].MoveSlot, "the Heavy ult program is running");
        }

        [Test]
        public void UltPress_WhileStunned_DoesNotFire()
        {
            var s = Arena(FighterClass.Melee, FighterClass.Melee, -3000, 12000);
            s.F[0].Ult = ChibiSimConst.UltMax;
            s.F[0].Stun = 30;
            Step(s, B(ChibiButtons.Ultimate));
            Assert.AreEqual(0, s.F[0].UltsUsed, "the button needs the fighter able to act");
        }

        [Test]
        public void UltPress_DuringHitstop_FiresOnFirstUnfrozenStep()
        {
            var s = Arena(FighterClass.Melee, FighterClass.Melee, -3000, 12000);
            s.F[0].Ult = ChibiSimConst.UltMax;
            s.HitstopRealTicks = 3;
            int g0 = s.GameTicks;
            Step(s, B(ChibiButtons.Ultimate));
            for (int k = 0; k < 10 && s.F[0].UltsUsed == 0; k++) Step(s, N);
            Assert.AreEqual(1, s.F[0].UltsUsed, "the latched press fires once game steps resume");
            Assert.Greater(s.GameTicks, g0);
        }

        // ================================================================== dormant gesture window (F:396-434)

        [TestCase(FighterClass.Melee)] [TestCase(FighterClass.Heavy)] [TestCase(FighterClass.Ranged)]
        [TestCase(FighterClass.Magic)] [TestCase(FighterClass.Polearm)]
        public void ClassGesture_HeldNineRealTicks_FiresOnNinthTick(FighterClass c)
        {
            var s = Arena(c, FighterClass.Melee, -3000, 12000);
            OpenWindow(s);
            var g = G(UltGestureOf(c));
            for (int k = 1; k <= ChibiSimConst.UltGestureHoldTicks - 1; k++)
            {
                Step(s, g);
                Assert.AreEqual(0, s.F[0].UltsUsed, $"{c}: fired after only {k} held ticks (needs StableTime >= 0.15 s)");
            }
            Step(s, g);
            var f = s.F[0];
            Assert.AreEqual(1, f.UltsUsed, $"{c}: fires on the 9th held tick");
            Assert.AreEqual(0, f.Ult, "meter spent");
            Assert.AreEqual(0, f.UltWindowTicks, "window closed");
            Assert.AreEqual((int)ChibiUltPrompt.Detected, f.UltPrompt);
            Assert.AreEqual(0, s.CinematicFighter, "ult cinematic started");
        }

        [Test]
        public void ClassGesture_AlreadyHeldWhenWindowOpens_FiresOnFirstWindowTick()
        {
            // StableTime is a property of the gesture, not of the window (F:412): a long-held gesture fires on the first window tick.
            var s = Arena(FighterClass.Ranged, FighterClass.Melee, -3000, 12000);
            var g = G(Gesture.PunchForward);
            for (int k = 0; k < 20; k++) Step(s, g);   // also fires one Pose Fireball (onset) before the window exists
            OpenWindow(s);
            Step(s, g);
            Assert.AreEqual(1, s.F[0].UltsUsed);
        }

        [Test]
        public void WrongGesture_WindowTimesOutOnTick180_FailedThenRetryWait90()
        {
            var s = Arena(FighterClass.Heavy, FighterClass.Melee, -3000, 12000);
            OpenWindow(s);
            var wrong = G(Gesture.CrossArms);    // Heavy needs RaiseHands
            for (int k = 1; k < ChibiSimConst.UltWindowTicks; k++) Step(s, wrong);
            Assert.Greater(s.F[0].UltWindowTicks, 0, "still open on tick 179");
            Step(s, wrong);
            var f = s.F[0];
            Assert.AreEqual(0, f.UltWindowTicks, "timed out on tick 180");
            Assert.AreEqual((int)ChibiUltPrompt.Failed, f.UltPrompt);
            Assert.AreEqual(ChibiSimConst.UltRetryTicks, f.UltRetryTicks, "1.5 s retry wait");
            Assert.AreEqual(ChibiSimConst.UltMax, f.Ult, "meter kept after a failed window");
            Assert.AreEqual(0, f.UltsUsed);
        }

        [Test]
        public void RetryWait_CountsDownTo0_AndNeverBlocksTheButton()
        {
            var s = Arena(FighterClass.Heavy, FighterClass.Melee, -3000, 12000);
            OpenWindow(s);
            for (int k = 1; k <= ChibiSimConst.UltWindowTicks; k++) Step(s, N);
            Assert.AreEqual(ChibiSimConst.UltRetryTicks, s.F[0].UltRetryTicks);
            Step(s, B(ChibiButtons.Ultimate));             // rules v22: the button fires even inside the retry wait
            Assert.AreEqual(1, s.F[0].UltsUsed);
            for (int k = 1; k < ChibiSimConst.UltRetryTicks; k++) Step(s, N);
            Assert.AreEqual(0, s.F[0].UltRetryTicks, "the wait reaches 0 on real tick 90");
        }

        [Test]
        public void PromptDetected_HidesAfter120RealTicks()
        {
            var s = Arena(FighterClass.Melee, FighterClass.Melee, -3000, 12000);
            OpenWindow(s);
            for (int k = 0; k < ChibiSimConst.UltGestureHoldTicks; k++) Step(s, G(Gesture.CrossArms));
            Assert.AreEqual((int)ChibiUltPrompt.Detected, s.F[0].UltPrompt);
            for (int k = 1; k < ChibiSimConst.PromptDetectedTicks; k++) Step(s, N);
            Assert.AreEqual((int)ChibiUltPrompt.Detected, s.F[0].UltPrompt, "still shown on real tick 119");
            Step(s, N);
            Assert.AreEqual((int)ChibiUltPrompt.Hidden, s.F[0].UltPrompt, "hidden on real tick 120");
        }

        [Test]
        public void GestureFire_WhileStunned_FiresAndClearsStun()
        {
            var s = Arena(FighterClass.Melee, FighterClass.Melee, -3000, 12000);
            OpenWindow(s);
            s.F[0].Stun = 200;   // fixture injection: stunned while the window is open
            for (int k = 0; k < ChibiSimConst.UltGestureHoldTicks; k++) Step(s, G(Gesture.CrossArms));
            Assert.AreEqual(1, s.F[0].UltsUsed, "the ult fires through hitstun (F:417 needs only InputEnabled and alive)");
            Assert.AreEqual(0, s.F[0].Stun, "FireUltimate clears hitstun (F:958)");
        }

        [Test]
        public void GestureFire_WhileHeldByGrab_FiresAndStaysHeld()
        {
            // F[0] Melee opens the window, then F[1] Heavy grabs it (Skill1 at 1000 apart, multiphase test R5 distance).
            var s = Arena(FighterClass.Melee, FighterClass.Heavy, 0, 1000);
            OpenWindow(s);
            Step(s, N, B(ChibiButtons.Skill1));
            for (int k = 0; k < 3 && !s.F[0].Held; k++) Step(s, N);
            Assert.IsTrue(s.F[0].Held, "precondition: F[0] is held by the grab");
            int fired = -1;
            for (int k = 0; k < 12 && fired < 0; k++)
            {
                Step(s, G(Gesture.CrossArms));
                if (s.F[0].UltsUsed == 1) fired = k;
            }
            Assert.GreaterOrEqual(fired, 0, "the ult fires while held");
            Assert.IsTrue(s.F[0].Held, "like the original, firing does not break the opponent's hold");
            Assert.Greater(s.F[0].AuraTicks, 0, "Sword Aura started");
            Assert.GreaterOrEqual(s.F[0].Invuln, ChibiSimConst.UltCommonInvuln - 1, "common ult invuln");
        }

        [Test]
        public void UltButtonAndGestureSameTick_FiresOnce()
        {
            var s = Arena(FighterClass.Magic, FighterClass.Melee, -3000, 12000);
            OpenWindow(s);
            var both = G(Gesture.CrossArms, ChibiButtons.Ultimate);
            var gest = G(Gesture.CrossArms);
            for (int k = 0; k < 40; k++) Step(s, k % 2 == 0 ? both : gest);   // Ult edge every other tick
            Assert.AreEqual(1, s.F[0].UltsUsed, "one meter = one ult (security rule 3)");
        }

        [Test]
        public void GestureHold_CountsRealTicksThroughHitstop()
        {
            var s = Arena(FighterClass.Melee, FighterClass.Melee, -3000, 12000);
            OpenWindow(s);
            s.HitstopRealTicks = 30;   // frozen real ticks: no game step for a while
            int g0 = s.GameTicks;
            for (int k = 0; k < ChibiSimConst.UltGestureHoldTicks; k++) Step(s, G(Gesture.CrossArms));
            Assert.AreEqual(ChibiSimConst.UltGestureHoldTicks, s.F[0].GestureTicks, "StableTime is unscaled (F:172)");
            Assert.IsTrue(s.F[0].UltFirePending || s.F[0].UltsUsed == 1, "matched on a real tick inside the hitstop");
            Assert.AreEqual(0, s.F[0].UltWindowTicks, "the window closed on the real tick it matched");
            for (int k = 0; k < 40 && s.F[0].UltsUsed == 0; k++) Step(s, N);
            Assert.AreEqual(1, s.F[0].UltsUsed, "fires at the next game step (D10)");
            Assert.Greater(s.GameTicks, g0);
        }

        // ================================================================== CrossArms block (F:261)

        [Test]
        public void CrossArms_NoWindow_BlocksLikeShield()
        {
            var s = Arena(FighterClass.Polearm, FighterClass.Melee);
            Step(s, G(Gesture.CrossArms));
            Assert.IsTrue(s.F[0].Shielding, "CrossArms blocks");
            var t = Arena(FighterClass.Polearm, FighterClass.Melee);
            Step(t, B(ChibiButtons.Shield));
            // Only the raw-input bookkeeping differs (gesture bits vs Shield bit); everything gameplay must match.
            t.F[0].PrevInput = s.F[0].PrevInput;
            t.F[0].GestureTicks = s.F[0].GestureTicks;
            Assert.AreEqual(t.F[0].ComputeHashForTest(), s.F[0].ComputeHashForTest(), "identical to holding the Shield button");
        }

        [Test]
        public void CrossArms_WindowOpen_DoesNotBlock()
        {
            var s = Arena(FighterClass.Heavy, FighterClass.Melee);   // Heavy ult gesture is RaiseHands
            OpenWindow(s);
            Step(s, G(Gesture.CrossArms));
            Assert.IsFalse(s.F[0].Shielding, "no CrossArms block while the ult window is open");
        }

        // ================================================================== poses (F:371-392)

        [Test]
        public void RaiseHands_Onset_SuperJumpVy19_AirJumpRefilled_Cd180()
        {
            var s = Arena(FighterClass.Melee, FighterClass.Melee);
            s.F[0].AirJumps = 0;
            Step(s, G(Gesture.RaiseHands));
            var f = s.F[0];
            Assert.AreEqual(ChibiSimConst.SuperJumpVel - ChibiSimConst.Gravity, f.Vy, "Vy 19 u/s then one tick of gravity");
            Assert.AreEqual(1, f.AirJumps, "air jump refilled");
            Assert.AreEqual(ChibiSimConst.SuperJumpCd, f.SuperJumpCd);
        }

        [Test]
        public void RaiseHands_HeldNotRetriggered_SecondOnsetInsideCdIgnored()
        {
            var s = Arena(FighterClass.Melee, FighterClass.Melee);
            for (int k = 0; k < 60; k++) Step(s, G(Gesture.RaiseHands));   // held: one onset only
            Assert.AreEqual(ChibiSimConst.SuperJumpCd - 59, s.F[0].SuperJumpCd);
            Step(s, N);
            int cd = s.F[0].SuperJumpCd;
            int vyBefore = s.F[0].Vy;
            Step(s, G(Gesture.RaiseHands));                                // new onset, still on cooldown
            Assert.AreEqual(cd - 1, s.F[0].SuperJumpCd, "cooldown not restarted");
            Assert.Less(s.F[0].Vy, ChibiSimConst.SuperJumpVel - ChibiSimConst.Gravity, "no second super jump");
            Assert.LessOrEqual(s.F[0].Vy, vyBefore);
        }

        [Test]
        public void PunchForward_Onset_PoseFireballSpawned_Lock15_Cd240()
        {
            var s = Arena(FighterClass.Heavy, FighterClass.Melee, -3000, 12000);
            int since = s.F[0].SinceAttackTicks;
            Step(s, G(Gesture.PunchForward));
            var f = s.F[0];
            Assert.AreEqual(1, CountProj(s, ChibiProjKind.PoseFireball));
            ChibiProjectileSim p = null;
            foreach (var q in s.Proj) if (q.Active && q.Kind == ChibiProjKind.PoseFireball) p = q;
            Assert.AreEqual(0, p.Owner);
            Assert.AreEqual(ChibiSimConst.PoseShotMult, p.Mult);
            Assert.AreEqual(ChibiSimConst.PoseShotR, p.Radius);
            Assert.AreEqual(ChibiSimConst.PoseShotVel, p.Vx, "13 u/s toward Facing +1");
            Assert.AreEqual(ChibiSimConst.PoseShotLife, p.Life);
            Assert.AreEqual(-3000 + ChibiSimConst.PoseShotOx, p.X, "Center + (0.6 F, 0.1)");
            Assert.AreEqual(ChibiSimConst.PoseShotOy, p.Y);
            Assert.AreEqual(ChibiSimConst.PoseSlot, f.MoveSlot, "0.25 s actionLock");
            Assert.AreEqual(ChibiSimConst.PoseShotCd, f.PoseShotCd);
            Assert.AreEqual(since + 1, f.SinceAttackTicks, "not a normal or skill: Patience clock untouched");
        }

        [Test]
        public void PoseFireball_Lock_LightOnTick14Lost_LightOnTick15Starts()
        {
            var s = Arena(FighterClass.Heavy, FighterClass.Melee, -3000, 12000);
            Step(s, G(Gesture.PunchForward));                      // tick 0
            for (int k = 1; k <= 13; k++) Step(s, N);
            var lost = new ChibiSimState(); lost.CopyFrom(s);
            Step(lost, B(ChibiButtons.Light));                     // tick 14
            Assert.AreNotEqual(0, lost.F[0].MoveSlot, "Light on tick 14 is lost (still locked)");
            Step(s, N);                                            // tick 14
            Step(s, B(ChibiButtons.Light));                        // tick 15
            Assert.AreEqual(0, s.F[0].MoveSlot, "Light on tick 15 starts (lock 0.25 s = 15 ticks)");
        }

        [Test]
        public void PunchForward_WhileAttacking_NoFireball()
        {
            var s = Arena(FighterClass.Heavy, FighterClass.Melee, -3000, 12000);
            Step(s, B(ChibiButtons.Heavy));
            Step(s, G(Gesture.PunchForward));
            Assert.AreEqual(0, CountProj(s, ChibiProjKind.PoseFireball), "needs actionLock <= 0 (F:383)");
            Assert.AreEqual(0, s.F[0].PoseShotCd, "cooldown not spent on a refused pose");
        }

        [Test]
        public void Poses_DisabledWhileUltWindowOpen()
        {
            var s = Arena(FighterClass.Melee, FighterClass.Melee, -3000, 12000);   // Melee ult = CrossArms
            OpenWindow(s);
            Step(s, G(Gesture.RaiseHands));
            Step(s, G(Gesture.PunchForward));
            Assert.AreEqual(0, s.F[0].SuperJumpCd, "no Super Jump during the window (F:373)");
            Assert.AreEqual(0, CountProj(s, ChibiProjKind.PoseFireball), "no Pose Fireball during the window");
        }

        [Test]
        public void Poses_IgnoredWhileStunned()
        {
            var s = Arena(FighterClass.Melee, FighterClass.Melee, -3000, 12000);
            s.F[0].Stun = 30;
            Step(s, G(Gesture.RaiseHands));
            Assert.AreEqual(0, s.F[0].SuperJumpCd, "HandlePoses runs only when canAct (F:179-183)");
        }

        // ================================================================== security (security-gesture-review.md section 2)

        // Rules v18 / protocol 3 (CH-3), INTENDED change: bit 15 is no longer the cosmetic PoseVerified hint but the
        // Special (B) button, a real gameplay input. The old "bit 15 never changes the simulation" and "bit 15 cleared
        // without a gesture" tests encoded the retired meaning and were replaced by these two.

        [Test]
        public void SpecialBit_IsIndependentOfTheGestureBits()
        {
            // Arrange / Act: Special with gesture None must survive sanitising (it is no longer tied to a pose).
            var bare = new ChibiInputPacket { Raw = (ushort)ChibiButtons.Special }.Sanitized();
            var withGesture = G(Gesture.RaiseHands, ChibiButtons.Special).Sanitized();

            // Assert
            Assert.AreEqual((ushort)ChibiButtons.Special, bare.Raw);
            Assert.IsTrue(withGesture.Has(ChibiButtons.Special));
            Assert.AreEqual(Gesture.RaiseHands, withGesture.Gesture);
        }

        [Test]
        public void SpecialPress_StartsAMove_AGestureDoesNotSetSpecial()
        {
            // Arrange: a gesture alone (no Special) must start no Special move; a Special press must start one.
            var a = Arena(FighterClass.Ranged, FighterClass.Heavy);
            var b = Arena(FighterClass.Ranged, FighterClass.Heavy);
            for (int t = 0; t < 30; t++) { Step(a, N, N); Step(b, N, N); }

            // Act
            Step(a, G(Gesture.CrossArms), N);
            Step(b, new ChibiInputPacket { Raw = (ushort)ChibiButtons.Special }, N);

            // Assert
            Assert.AreEqual(-1, a.F[0].MoveSlot, "a gesture is not a Special press");
            Assert.GreaterOrEqual(b.F[0].MoveSlot, 7, "Special (bit 15) starts a new-moveset slot (>= 7)");
        }

        [Test]
        public void GestureFuzz_EveryTick_NoMoreActionsThanCooldownsAllow()
        {
            // Rules 3-5: a macro flipping gesture bits every tick gains nothing over the cooldowns and the meter.
            var s = Arena(FighterClass.Polearm, FighterClass.Melee, -3000, 12000);
            s.F[1].Invuln = 1 << 20;
            var rng = new Random(99);
            int superJumps = 0, fireballs = 0;
            const int ticks = 1200;
            for (int t = 0; t < ticks; t++)
            {
                int cdJ = s.F[0].SuperJumpCd, cdF = s.F[0].PoseShotCd;
                var g = (Gesture)rng.Next(0, 4);
                Step(s, G(g, rng.Next(0, 3) == 0 ? ChibiButtons.Ultimate : ChibiButtons.None));
                if (s.F[0].SuperJumpCd > cdJ) superJumps++;
                if (s.F[0].PoseShotCd > cdF) fireballs++;
                // Keep it on stage so ring-outs do not reset anything.
                if (Math.Abs(s.F[0].X) > 6000 || s.F[0].Y > 6000) { s.F[0].X = 0; s.F[0].Y = 0; s.F[0].Vy = 0; }
            }
            Assert.LessOrEqual(superJumps, ticks / ChibiSimConst.SuperJumpCd + 1, "Super Jump limited by its 3 s cooldown");
            Assert.LessOrEqual(fireballs, ticks / ChibiSimConst.PoseShotCd + 1, "Pose Fireball limited by its 4 s cooldown");
            Assert.AreEqual(0, s.F[0].UltsUsed, "no ult without a full meter");
        }

        [Test]
        public void GestureMatch_RollbackRestoreMidWindow_SameChecksum()
        {
            var s = Arena(FighterClass.Heavy, FighterClass.Ranged, -3000, 3000);
            OpenWindow(s);
            var script = new ChibiInputPacket[40];
            for (int k = 0; k < script.Length; k++)
                script[k] = k < 4 ? G(Gesture.PunchForward) : (k < 20 ? G(Gesture.RaiseHands) : B(ChibiButtons.Light));
            var snap = new ChibiSimState(); snap.CopyFrom(s);
            foreach (var p in script) Step(s, p);
            uint straight = s.ComputeChecksum();
            var again = new ChibiSimState(); again.CopyFrom(snap);
            for (int k = 0; k < 10; k++) Step(again, script[k]);
            var mid = new ChibiSimState(); mid.CopyFrom(again);
            again.CopyFrom(mid);
            for (int k = 10; k < script.Length; k++) Step(again, script[k]);
            Assert.AreEqual(straight, again.ComputeChecksum());
            Assert.AreEqual(1, again.F[0].UltsUsed, "precondition: the Heavy ult fired through the gesture");
        }
    }

    /// <summary>Test-only per-fighter hash (ChibiFighterSim.Hash is public; this wraps it for single-fighter comparisons).</summary>
    static class ChibiFighterHashExt
    {
        public static uint ComputeHashForTest(this ChibiFighterSim f)
        {
            uint h = 2166136261u;
            f.Hash(ref h);
            return h;
        }
    }
}
