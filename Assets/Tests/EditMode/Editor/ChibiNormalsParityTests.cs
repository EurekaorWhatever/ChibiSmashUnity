// Story type: Logic (normal attacks: timing, combo, knockback, stun, reload, projectiles) -- evidence: automated EditMode
// unit test -- gate: BLOCKING
// Source spec: docs/architecture/netcode/normals-conversion.md (section 0 ground truth, section 1 timing formulas,
// section 2 per-class table, sections 3-4 combo / reload, section 9 code contract, section 10 reference values +
// tolerances). Original: Fighter.cs (F:), CombatObjects.cs (CO:), ClassData.cs, CombatAuthority.cs.
//
// User decisions applied on top of the doc:
//   D-N1 = A: NO input buffer. A Light/Heavy press while busy (attack lock, dash, hitstun, reload) is LOST; a normal only
//          starts from a press made on a free tick (F:318-333, doc section 0 row 13, contract item 11).
//
// Tick convention (doc section 10): the press is in the input of Step #0 ("tick 0"); "on tick k" = observed at the END
// of Step #k. Observables only: victim Hp drop tick, victim Stun, victim X over a window, attacker MoveSlot, projectile
// Active/X/Y via state.Proj. Vx / Vy are never read or asserted. SetCd is a secondary assert (reload test only).
//
// Arena: Sky Dojo (stage 0, HalfWidth 8000, BlastX 17000). Melee columns X 6500 / 7300 are platform-free (platform B
// spans 2500..5900, stage-conversion.md section 1). Projectile tests fire from X -7500 (outside platform A, -5900..-2500)
// with the victim parked on top platform C (0, 5550), above the shot line, so nothing is hit and the full life is seen.
//
// Attacker passives: base-speed tests use passives WITHOUT attack-speed effects (Melee P1 lifesteal, Heavy P1 move speed,
// Polearm P1 patience, Ranged P0 crit, Magic P0 hex-on-hit). Melee P0 = Swift Blade x1.15, Heavy P0 = Crusher x0.85
// (ChibiCombatStats.cs, doc section 0 row 15). Victim: Melee P1 (no ward / armour / dodge effects).
using System;
using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Light / heavy normal-attack parity between the deterministic 60 Hz simulation and the original Fighter.cs.
    /// Reference values and tolerances: normals-conversion.md section 10.
    /// </summary>
    public class ChibiNormalsParityTests
    {
        const uint Seed = 12345;
        const int SettleTicks = 120;            // spawn drop from Y 775 is long finished (stage tests)
        const int SkyDojo = 0;

        // ---- tolerances (doc section 10)
        const double PosTol = 1;                // +-1 milli on knockback displacement and bolt X

        // ---- arena (see header)
        const int MeleeAX = 6500, MeleeVX = 7300;     // victim 800 milli in front: inside every class's MeleeBox
        const int ShotAX = 6300, ShotVX = 7500;       // ranged/magic kb tests: victim in the shot line
        const int FarAX = -7500;                      // projectile-life tests
        const int PlatC_X = 0, PlatC_Top = 5550;      // Sky Dojo top platform (stage table)

        // ---- passives
        const int MeleeBase = 1, HeavyBase = 1, PolearmBase = 1, RangedBase = 0, MagicBase = 0;
        const int SwiftBlade = 0, Crusher = 0;
        const int VictimPassive = 1;

        // ---- slots (contract item 3)
        const int LightSlot = 0, HeavySlot = 1, NoMove = -1;

        // ---- timing at base speed (doc section 1 table, section 10 rows 1-4)
        // Rules v22: accept tick = k_strike + 1 + floor((k_lock - k_strike - 1) x 3/4) (recovery trimmed by a quarter).
        const int LightStrike = 6, LightLock = 13;     // 24*240/1000 = 5.76 -> 6 ; k_lock 15 -> 6 + 1 + floor(8 x 3/4) = 13
        const int HeavyStrike = 11, HeavyLock = 22;    // 10.08 -> 11 ; k_lock 26 -> 11 + 1 + floor(14 x 3/4) = 22

        // ---- stun ticks (doc section 2: ceil(stun_s * 60); F:471-472)
        const int LightStun = 17, FinisherStun = 27, HeavyStun = 30;

        // ---- knockback displacement (doc section 10 rows "Melee light/heavy kb", "Finisher", "Ranged light", "Magic heavy")
        // Rules v20: the normals' kb rows are the launch angle / tier of the percent formula (percent-knockback.md 4.4).
        const int MeleeLightKbX = 37500, MeleeLightKbY = 30000, MeleeHeavyKbX = 120000, MeleeHeavyKbY = 90000,
                  FinisherKb = 105000, ShotLightKbX = 22500, ShotLightKbY = 18000, ShotHeavyKbX = 72000, ShotHeavyKbY = 54000;

        // ---- combo (doc section 3)
        const int ComboWindowTicks = 55;        // elapsed > 0.9 s resets; j = 54 chains, j = 55 resets

        // ---- reload (doc section 4 / section 10 row "Reload")
        const int ReloadTicks = 90;             // 1.5 s from the finisher STRIKE tick (F:474)

        // ---- projectiles (doc section 0 rows 7-9, section 9 items 6 + 10)
        const int ArrowOffX = 500, ArrowOffY = 725, ArrowLife = 78;            // (0.5, 0.05) + centre 675 ; 1.3 s
        const int ArrowLightStep = 300, ArrowHeavyStep = 350;                  // 18 / 21 u/s
        const int BoltOffX = 600, BoltOffY = 925, BoltLife = 72;               // (0.6, 0.25) ; 1.2 s
        const int BoltSpeedSub = 210000;                                       // 14 u/s = 233.33 milli/t
        const int AuraOffX = 700, AuraOffY = 675, AuraLife = 54, AuraStep = 250; // (0.7, 0) ; 0.9 s ; 15 u/s

        static readonly ChibiInputPacket Neutral = default;

        // ------------------------------------------------------------------ helpers

        static int BasePassive(FighterClass c)
        {
            switch (c)
            {
                case FighterClass.Melee: return MeleeBase;
                case FighterClass.Heavy: return HeavyBase;
                case FighterClass.Polearm: return PolearmBase;
                case FighterClass.Ranged: return RangedBase;
                default: return MagicBase;
            }
        }

        /// <summary>Fresh Sky Dojo match, settled with neutral input.</summary>
        static ChibiSimState NewMatch(FighterClass attacker, int passive)
        {
            var cfg = new ChibiMatchConfig
            {
                Seed = Seed, Class0 = attacker, Passive0 = passive,
                Class1 = FighterClass.Melee, Passive1 = VictimPassive, Stage = SkyDojo,
            };
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, cfg);
            for (int t = 0; t < SettleTicks; t++) ChibiSimulation.Step(s, default, default);
            return s;
        }

        /// <summary>Puts a fighter at rest, grounded on a surface with top <paramref name="y"/>, free to act.</summary>
        static void Put(ChibiFighterSim f, int x, int y, int facing)
        {
            f.X = x; f.Y = y; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0;
            f.Grounded = true; f.AirJumps = 1; f.Facing = facing; f.Stun = 0; f.Invuln = 0;
        }

        static void Settle(ChibiSimState s)
        {
            for (int t = 0; t < 3; t++) ChibiSimulation.Step(s, Neutral, Neutral);
        }

        /// <summary>Attacker at <paramref name="ax"/> facing +1, victim grounded at <paramref name="vx"/> facing -1.</summary>
        static ChibiSimState Arena(FighterClass c, int passive, int ax, int vx)
        {
            var s = NewMatch(c, passive);
            Put(s.F[0], ax, 0, 1);
            Put(s.F[1], vx, 0, -1);
            Settle(s);
            return s;
        }

        /// <summary>Attacker at X -7500 facing +1; victim parked on platform C, out of the shot line.</summary>
        static ChibiSimState FarArena(FighterClass c, int passive)
        {
            var s = NewMatch(c, passive);
            Put(s.F[0], FarAX, 0, 1);
            Put(s.F[1], PlatC_X, PlatC_Top, -1);
            Settle(s);
            return s;
        }

        static Func<int, ChibiButtons> On(ChibiButtons b, params int[] ticks) =>
            t => Array.IndexOf(ticks, t) >= 0 ? b : ChibiButtons.None;

        static Func<int, ChibiButtons> Or(Func<int, ChibiButtons> a, Func<int, ChibiButtons> b) => t => a(t) | b(t);

        static Trace LastTrace;

        /// <summary>Rules v20 oracle per hit of the last HitStuns run: light (2.5, 2) u/s stun 17; the finisher (index
        /// <paramref name="finisherIdx"/>, -1 = none) (7, 7) u/s stun 27.</summary>
        static List<int> ExpectedComboStuns(FighterClass cls, int finisherIdx)
        {
            var tr = LastTrace;
            var e = new List<int>();
            for (int i = 0; i < tr.Hits.Count; i++)
            {
                int h = tr.Hits[i], before = h > 0 ? tr.VictimPct[h - 1] : 0;
                bool fin = i == finisherIdx;
                e.Add(ChibiPercentTestKit.Expected(tr.VictimPct[h], tr.VictimPct[h] - before, tr.VictimWeight, (int)cls,
                    fin ? FinisherKb : MeleeLightKbX, fin ? FinisherKb : MeleeLightKbY, 1, fin ? FinisherStun : LightStun).Stun);
            }
            return e;
        }

        /// <summary>Rules v20 oracle per hit of the last HitStuns run; kinds: 0 light, 1 heavy, 2 finisher.</summary>
        static List<int> ExpectedStunsOf(FighterClass cls, params int[] kinds)
        {
            var tr = LastTrace;
            var e = new List<int>();
            for (int i = 0; i < tr.Hits.Count && i < kinds.Length; i++)
            {
                int h = tr.Hits[i], before = h > 0 ? tr.VictimPct[h - 1] : 0, k = kinds[i];
                int kx = k == 0 ? MeleeLightKbX : k == 1 ? MeleeHeavyKbX : FinisherKb;
                int ky = k == 0 ? MeleeLightKbY : k == 1 ? MeleeHeavyKbY : FinisherKb;
                int st = k == 0 ? LightStun : k == 1 ? HeavyStun : FinisherStun;
                e.Add(ChibiPercentTestKit.Expected(tr.VictimPct[h], tr.VictimPct[h] - before, tr.VictimWeight, (int)cls, kx, ky, 1, st).Stun);
            }
            return e;
        }

        /// <summary>Per-tick observations, all sampled at the END of each tick.</summary>
        sealed class Trace
        {
            public int[] VictimX, VictimStun, AttackerSlot, VictimPct, VictimVx;
            public int VictimWeight;
            public readonly List<int> Hits = new List<int>();     // ticks on which the victim's Hp dropped
        }

        /// <summary>
        /// Steps <paramref name="ticks"/> GAME steps (D10, real ticks of hitstop consumed inside) with P1 pressing <paramref name="btn"/>(t). <paramref name="before"/> runs
        /// before each Step (used to re-stand the victim so every chain hit connects).
        /// </summary>
        static Trace Run(ChibiSimState s, int ticks, Func<int, ChibiButtons> btn, Action<int> before = null)
        {
            var tr = new Trace { VictimX = new int[ticks], VictimStun = new int[ticks], AttackerSlot = new int[ticks],
                                 VictimPct = new int[ticks], VictimVx = new int[ticks], VictimWeight = s.F[1].Weight };
            for (int t = 0; t < ticks; t++)
            {
                before?.Invoke(t);
                int hp = s.F[1].HpLike();
                ChibiTestTime.StepGame(s, ChibiInputPacket.Make(ChibiDir.Neutral, btn(t)), Neutral);   // counts game steps (D10)
                tr.VictimX[t] = s.F[1].X;
                tr.VictimStun[t] = s.F[1].Stun;
                tr.AttackerSlot[t] = s.F[0].MoveSlot;
                tr.VictimPct[t] = s.F[1].PctM;
                tr.VictimVx[t] = s.F[1].Vx;
                if (s.F[1].HpLike() < hp) tr.Hits.Add(t);
            }
            return tr;
        }

        /// <summary>Before-hook that re-stands the victim at its melee spot on each listed press tick.</summary>
        static Action<int> RestandVictimOn(ChibiSimState s, int vx, params int[] ticks) =>
            t => { if (Array.IndexOf(ticks, t) >= 0) Put(s.F[1], vx, 0, -1); };

        static ChibiProjectileSim OwnedProj(ChibiSimState s)
        {
            foreach (var p in s.Proj)
                if (p != null && p.Active && p.Owner == 0) return p;
            return null;
        }

        static void AssertSlotNeverStarts(Trace tr, int from, int to, string why)
        {
            for (int t = from; t <= to; t++)
                Assert.AreEqual(NoMove, tr.AttackerSlot[t], $"{why}: MoveSlot on tick {t} (outside the rules-v22 attack buffer)");
        }

        // ------------------------------------------------------------------ strike tick (doc section 1a, section 10 rows 1-2)

        [TestCase(FighterClass.Melee)]
        [TestCase(FighterClass.Heavy)]
        [TestCase(FighterClass.Polearm)]
        public void Light_HitLandsOnOriginalTick(FighterClass cls)
        {
            // Arrange
            var s = Arena(cls, BasePassive(cls), MeleeAX, MeleeVX);

            // Act
            var tr = Run(s, 40, On(ChibiButtons.Light, 0));

            // Assert: a single MeleeBox check on frame 6 (F:465, Active = 1)
            CollectionAssert.AreEqual(new[] { LightStrike }, tr.Hits, $"{cls} light: Hp-drop ticks");
        }

        [TestCase(FighterClass.Melee)]
        [TestCase(FighterClass.Heavy)]
        [TestCase(FighterClass.Polearm)]
        public void Heavy_HitLandsOnOriginalTick(FighterClass cls)
        {
            var s = Arena(cls, BasePassive(cls), MeleeAX, MeleeVX);

            var tr = Run(s, 50, On(ChibiButtons.Heavy, 0));

            CollectionAssert.AreEqual(new[] { HeavyStrike }, tr.Hits, $"{cls} heavy: Hp-drop ticks");
        }

        // ------------------------------------------------------------------ lock length (doc section 1b, section 10 rows 3-4)

        [TestCase(FighterClass.Melee)]
        [TestCase(FighterClass.Heavy)]
        [TestCase(FighterClass.Polearm)]
        [TestCase(FighterClass.Ranged)]
        [TestCase(FighterClass.Magic)]
        public void Light_LockLength_NextPressAcceptedAtTickN_IgnoredAtNMinus1(FighterClass cls)
        {
            AssertLock(cls, BasePassive(cls), 0, ChibiButtons.Light, LightLock);
        }

        [TestCase(FighterClass.Melee)]
        [TestCase(FighterClass.Heavy)]
        [TestCase(FighterClass.Polearm)]
        [TestCase(FighterClass.Ranged)]
        [TestCase(FighterClass.Magic)]
        public void Heavy_LockLength_NextPressAcceptedAtTickN_IgnoredAtNMinus1(FighterClass cls)
        {
            AssertLock(cls, BasePassive(cls), 0, ChibiButtons.Heavy, HeavyLock);
        }

        /// <summary>
        /// First normal on tick 0; the move is over at the end of tick N-1. Rules v22 attack buffer: a Light on tick N-1 or
        /// as early as N - <see cref="ChibiSimConst.AttackBufferTicks"/> starts slot 0 exactly on tick N; one tick earlier it
        /// is lost (no move on N-1..N+10). A Light on tick N (fresh run) starts slot 0 on tick N.
        /// </summary>
        static void AssertLock(FighterClass cls, int passive, int sluggishTicks, ChibiButtons first, int n)
        {
            int earliest = n - ChibiSimConst.AttackBufferTicks;
            foreach (int press in new[] { n - 1, earliest })
            {
                var s = FarArena(cls, passive);
                if (sluggishTicks > 0) s.F[0].SluggishTicks = sluggishTicks;
                var tr = Run(s, n + 2, Or(On(first, 0), On(ChibiButtons.Light, press)));
                Assert.AreEqual(first == ChibiButtons.Light ? LightSlot : HeavySlot, tr.AttackerSlot[0], "first press starts its move on tick 0");
                Assert.AreEqual(NoMove, tr.AttackerSlot[n - 1], $"move over at end of tick {n - 1}");
                Assert.AreEqual(LightSlot, tr.AttackerSlot[n], $"Light buffered on tick {press} must start exactly on tick {n}");
            }

            // Lost one tick before the buffer window
            var s2 = FarArena(cls, passive);
            if (sluggishTicks > 0) s2.F[0].SluggishTicks = sluggishTicks;
            var tr2 = Run(s2, n + 12, Or(On(first, 0), On(ChibiButtons.Light, earliest - 1)));
            AssertSlotNeverStarts(tr2, n - 1, n + 10, $"press on tick {earliest - 1} was lost");

            // Accepted at N
            var s3 = FarArena(cls, passive);
            if (sluggishTicks > 0) s3.F[0].SluggishTicks = sluggishTicks;
            var tr3 = Run(s3, n + 2, Or(On(first, 0), On(ChibiButtons.Light, n)));
            Assert.AreEqual(LightSlot, tr3.AttackerSlot[n], $"Light on tick {n} must start a move");
        }

        // ------------------------------------------------------------------ rules v22 attack buffer (replaces D-N1)

        [Test]
        public void PressEarlyInTheAttack_IsLost_OutsideTheBuffer()
        {
            // counts game steps (D10). Tick 5 is 8 ticks before the lock end (13): outside the 6-tick buffer.
            var s = Arena(FighterClass.Melee, MeleeBase, MeleeAX, MeleeVX);

            var tr = Run(s, 31, On(ChibiButtons.Light, 0, 5));

            AssertSlotNeverStarts(tr, LightLock, 30, "Light on tick 5 early in the lock");
            CollectionAssert.AreEqual(new[] { LightStrike }, tr.Hits, "only the tick-0 light may hit");
        }

        [Test]
        public void PressLateInTheAttack_IsBuffered_StartsOnTheFirstFreeTick()
        {
            var s = Arena(FighterClass.Melee, MeleeBase, MeleeAX, MeleeVX);

            var tr = Run(s, 20, On(ChibiButtons.Light, 0, 10));

            Assert.AreEqual(NoMove, tr.AttackerSlot[LightLock - 1], "first light over at the end of its lock");
            Assert.AreEqual(LightSlot, tr.AttackerSlot[LightLock], "the buffered tick-10 light starts on tick 13");
        }

        [Test]
        public void BufferedPress_IsDroppedByHitstun()
        {
            var s = FarArena(FighterClass.Melee, MeleeBase);

            var tr = Run(s, 30, On(ChibiButtons.Light, 0, LightLock - 2), t => { if (t == LightLock - 1) s.F[0].Stun = 5; });

            for (int t = LightLock; t < 30; t++)
                Assert.AreNotEqual(LightSlot, tr.AttackerSlot[t], $"a buffered light survived hitstun (tick {t})");
        }

        [Test]
        public void PressDuringDash_IsLost()
        {
            var s = FarArena(FighterClass.Melee, MeleeBase);
            const int lightTick = 3;

            var tr = Run(s, lightTick, On(ChibiButtons.Dash, 0));
            Assert.Greater(s.F[0].DashTicks, 0, "precondition: still dashing when Light is pressed");
            tr = Run(s, 30, On(ChibiButtons.Light, 0));       // tick 0 of this run = tick 3 of the dash

            for (int t = 0; t < 30; t++)
                Assert.AreNotEqual(LightSlot, tr.AttackerSlot[t], $"Light pressed during dash fired on dash tick {t + lightTick}");
        }

        [Test]
        public void PressDuringHitstun_IsLost()
        {
            var s = FarArena(FighterClass.Melee, MeleeBase);
            s.F[0].Stun = 10;

            var tr = Run(s, 30, On(ChibiButtons.Light, 2));

            for (int t = 0; t < 30; t++)
                Assert.AreNotEqual(LightSlot, tr.AttackerSlot[t], $"Light pressed in hitstun fired on tick {t}");
        }

        [Test]
        public void LightAndHeavySamePress_LightWins()
        {
            var s = FarArena(FighterClass.Melee, MeleeBase);

            var tr = Run(s, 2, On(ChibiButtons.Light | ChibiButtons.Heavy, 0));

            Assert.AreEqual(LightSlot, tr.AttackerSlot[0], "F:318-333: Light is checked before Heavy");
        }

        // ------------------------------------------------------------------ attack speed (doc section 1 table, section 10 rows 5-7)

        // cls, passive, sluggishTicks, button, expected hit tick, expected accept tick
        [TestCase(FighterClass.Melee, SwiftBlade, 0, ChibiButtons.Light, 6, 11)]
        [TestCase(FighterClass.Melee, SwiftBlade, 0, ChibiButtons.Heavy, 9, 19)]
        [TestCase(FighterClass.Heavy, Crusher, 0, ChibiButtons.Light, 7, 14)]
        [TestCase(FighterClass.Heavy, Crusher, 0, ChibiButtons.Heavy, 12, 25)]
        [TestCase(FighterClass.Heavy, Crusher, 1000, ChibiButtons.Heavy, 14, 30)]   // Crusher + Hex: 722 500 ppm, multiplicative
        public void AttackSpeedModifier_SwiftBlade_ChangesTiming(FighterClass cls, int passive, int sluggish, ChibiButtons btn, int hitTick, int acceptTick)
        {
            // Hit tick
            var s = Arena(cls, passive, MeleeAX, MeleeVX);
            if (sluggish > 0) s.F[0].SluggishTicks = sluggish;
            var tr = Run(s, 45, On(btn, 0));
            CollectionAssert.AreEqual(new[] { hitTick }, tr.Hits, $"{cls} P{passive} hex={sluggish > 0} {btn}: Hp-drop ticks");

            // Lock
            AssertLock(cls, passive, sluggish, btn, acceptTick);
        }

        // ------------------------------------------------------------------ knockback + stun (doc section 2, section 10 kb rows)

        [Test]
        public void Knockback_VictimDisplacementAndStun_Light()
        {
            // counts game steps (D10)
            var s = Arena(FighterClass.Melee, MeleeBase, MeleeAX, MeleeVX);
            var tr = Run(s, 60, On(ChibiButtons.Light, 0));
            AssertKnockback(tr, LightStrike, LightStun, MeleeLightKbX, MeleeLightKbY, FighterClass.Melee);
        }

        [Test]
        public void Knockback_VictimDisplacementAndStun_Heavy()
        {
            // counts game steps (D10)
            var s = Arena(FighterClass.Melee, MeleeBase, MeleeAX, MeleeVX);
            var tr = Run(s, 60, On(ChibiButtons.Heavy, 0));
            AssertKnockback(tr, HeavyStrike, HeavyStun, MeleeHeavyKbX, MeleeHeavyKbY, FighterClass.Melee);
        }

        [Test]
        public void Knockback_VictimDisplacementAndStun_Finisher()
        {
            // counts game steps (D10)
            // Melee 4th light; lights on 0/15/30/45, victim re-stood before each so every hit connects from rest.
            int[] presses = { 0, LightLock, 2 * LightLock, 3 * LightLock };
            var s = Arena(FighterClass.Melee, MeleeBase, MeleeAX, MeleeVX);
            var tr = Run(s, 100, On(ChibiButtons.Light, presses), RestandVictimOn(s, MeleeVX, presses));
            AssertKnockback(tr, presses[3] + LightStrike, FinisherStun, FinisherKb, FinisherKb, FighterClass.Melee);
        }

        [Test]
        public void Knockback_RangedLight_HalvedDisplacementAndStun()
        {
            // counts game steps (D10)
            var s = Arena(FighterClass.Ranged, RangedBase, ShotAX, ShotVX);
            var tr = Run(s, 60, On(ChibiButtons.Light, 0));
            Assert.AreEqual(1, tr.Hits.Count, "arrow must hit the victim once");
            AssertKnockback(tr, tr.Hits[0], LightStun, ShotLightKbX, ShotLightKbY, FighterClass.Ranged);
        }

        [Test]
        public void Knockback_MagicHeavy_HalvedDisplacementAndStun()
        {
            // counts game steps (D10)
            var s = Arena(FighterClass.Magic, MagicBase, ShotAX, ShotVX);
            var tr = Run(s, 70, On(ChibiButtons.Heavy, 0));
            Assert.AreEqual(1, tr.Hits.Count, "bolt must hit the victim once");
            AssertKnockback(tr, tr.Hits[0], HeavyStun, ShotHeavyKbX, ShotHeavyKbY, FighterClass.Magic);
        }

        /// <summary>
        /// Rules v20: hit on tick <paramref name="h"/>; the victim's Stun and launch Vx equal the spec oracle (legacy stun as
        /// the weak-hit floor, KB x 0.4 for strong hits); still stunned at end of h+stun-1, free (0) at end of h+stun; the
        /// launch carries it away (X grows) and the per-tick decay keeps the travel at or below the undecayed launch.
        /// </summary>
        static void AssertKnockback(Trace tr, int h, int legacyStun, int kbX, int kbY, FighterClass attacker)
        {
            CollectionAssert.Contains(tr.Hits, h, $"victim must be hit on tick {h}");
            int before = h > 0 ? tr.VictimPct[h - 1] : 0;
            var l = ChibiPercentTestKit.Expected(tr.VictimPct[h], tr.VictimPct[h] - before, tr.VictimWeight, (int)attacker,
                                                 kbX, kbY, 1, legacyStun);
            int stun = l.Stun;
            Assert.AreEqual(stun, tr.VictimStun[h], $"victim Stun at end of hit tick {h}");
            Assert.AreEqual(l.Vx, tr.VictimVx[h], $"victim launch Vx at end of hit tick {h}");
            Assert.Greater(tr.VictimStun[h + stun - 1], 0, $"victim still in hitstun on tick {h + stun - 1}");
            Assert.AreEqual(0, tr.VictimStun[h + stun], $"victim acts on tick {h + stun}");
            int window = stun - 1;
            double disp = tr.VictimX[h + window] - tr.VictimX[h];
            Assert.Greater(disp, 0, $"X(H+{window}) - X(H): launched away");
            Assert.LessOrEqual(disp, (double)l.Vx * window / 900 + PosTol, $"X(H+{window}) - X(H): decay never speeds it up");
        }

        // ------------------------------------------------------------------ combo (doc section 3, section 10 combo rows)

        /// <summary>Lights (or given buttons) on <paramref name="presses"/>; returns the victim Stun of each hit in order.</summary>
        static List<int> HitStuns(FighterClass cls, int[] presses, ChibiButtons[] buttons, int ticks)
        {
            var s = Arena(cls, BasePassive(cls), MeleeAX, MeleeVX);
            Func<int, ChibiButtons> btn = t => { int i = Array.IndexOf(presses, t); return i >= 0 ? buttons[i] : ChibiButtons.None; };
            var tr = Run(s, ticks, btn, RestandVictimOn(s, MeleeVX, presses));
            var stuns = new List<int>();
            foreach (int h in tr.Hits) stuns.Add(tr.VictimStun[h]);
            LastTrace = tr;
            return stuns;
        }

        static ChibiButtons[] Lights(int n)
        {
            var b = new ChibiButtons[n];
            for (int i = 0; i < n; i++) b[i] = ChibiButtons.Light;
            return b;
        }

        // patternLength (ClassData / ChibiCombatStats.PatternLength): Melee 4, Heavy 5, Polearm 4
        [TestCase(FighterClass.Melee, 4)]
        [TestCase(FighterClass.Heavy, 5)]
        [TestCase(FighterClass.Polearm, 4)]
        public void Combo_FinisherOnLastPatternHit_PerClass(FighterClass cls, int patternLength)
        {
            // counts game steps (D10)
            int[] presses = new int[patternLength];
            for (int i = 0; i < patternLength; i++) presses[i] = i * LightLock;

            var stuns = HitStuns(cls, presses, Lights(patternLength), patternLength * LightLock + 40);

            CollectionAssert.AreEqual(ExpectedComboStuns(cls, patternLength - 1), stuns,
                $"{cls}: only light #{patternLength} is the finisher (v20: finisher kb (7, 7) is a strong hit, stun KB x 0.4)");
        }

        [TestCase(ComboWindowTicks - 1, FinisherStun)]  // 54 ticks after the previous start: still chained
        [TestCase(ComboWindowTicks, LightStun)]         // 55: elapsed > 0.9 s, chain reset, 4th light is not a finisher
        public void Combo_ResetsAfterWindow(int gap, int expectedFourthStun)
        {
            // counts game steps (D10)
            int[] presses = { 0, LightLock, 2 * LightLock, 2 * LightLock + gap };

            var stuns = HitStuns(FighterClass.Melee, presses, Lights(4), presses[3] + 40);

            bool chained = expectedFourthStun == FinisherStun;
            CollectionAssert.AreEqual(ExpectedComboStuns(FighterClass.Melee, chained ? 3 : -1), stuns, $"4th light {gap} ticks after the 3rd");
        }

        [Test]
        public void Combo_HeavyResetsChain_FinisherIsSeventhPress()
        {
            // counts game steps (D10)
            // L, L, H, L, L, L, L -- heavy lock 26 (doc section 10 "Heavy resets chain")
            int[] presses = { 0, 15, 30, 56, 71, 86, 101 };
            var b = new[] { ChibiButtons.Light, ChibiButtons.Light, ChibiButtons.Heavy, ChibiButtons.Light,
                            ChibiButtons.Light, ChibiButtons.Light, ChibiButtons.Light };

            var stuns = HitStuns(FighterClass.Melee, presses, b, 150);

            // Rules v20: stuns from the spec oracle (light / heavy / finisher kb rows, legacy stun as the weak-hit floor).
            CollectionAssert.AreEqual(ExpectedStunsOf(FighterClass.Melee, 0, 0, 1, 0, 0, 0, 2), stuns);
        }

        // ------------------------------------------------------------------ projectiles (doc section 0 rows 7-9, 14; section 10)

        [TestCase(ChibiButtons.Light, 6, ArrowLightStep)]
        [TestCase(ChibiButtons.Heavy, 11, ArrowHeavyStep)]
        public void RangedLight_ArrowSpawnTravelLifetime(ChibiButtons btn, int spawnTick, int step)
        {
            var s = FarArena(FighterClass.Ranged, RangedBase);
            int ax = s.F[0].X, ay = s.F[0].Y;

            AssertStraightShot(s, btn, spawnTick, ax + ArrowOffX, ay + ArrowOffY, ArrowLife, n => ax + ArrowOffX + n * step, 0);
        }

        [Test]
        public void MagicLight_BoltSpawnTravelLifetime()
        {
            var s = FarArena(FighterClass.Magic, MagicBase);
            int ax = s.F[0].X, ay = s.F[0].Y;

            AssertStraightShot(s, ChibiButtons.Light, LightStrike, ax + BoltOffX, ay + BoltOffY, BoltLife,
                n => ax + BoltOffX + (int)((long)n * BoltSpeedSub / 900), PosTol);
        }

        [Test]
        public void SwordAura_WaveSpawnsOnHeavyToo()
        {
            var s = FarArena(FighterClass.Melee, MeleeBase);
            s.F[0].AuraTicks = 600;
            s.F[0].AuraWaveCredit = ChibiSimConst.AuraWaveGapTicks;   // rules v27: as when the ult starts the aura
            int ax = s.F[0].X, ay = s.F[0].Y;

            AssertStraightShot(s, ChibiButtons.Heavy, HeavyStrike, ax + AuraOffX, ay + AuraOffY, AuraLife, n => ax + AuraOffX + n * AuraStep, 0);
        }

        /// <summary>
        /// No owned projectile before <paramref name="spawn"/>; at end of spawn tick it sits at (x0, y0) unmoved; at end of
        /// spawn+n (1 &lt;= n &lt; life) X = xAfter(n), Y = y0; inactive at end of spawn+life (expires after its last move).
        /// </summary>
        static void AssertStraightShot(ChibiSimState s, ChibiButtons btn, int spawn, int x0, int y0, int life, Func<int, int> xAfter, double tol)
        {
            for (int t = 0; t <= spawn + life; t++)
            {
                ChibiSimulation.Step(s, ChibiInputPacket.Make(ChibiDir.Neutral, t == 0 ? btn : ChibiButtons.None), Neutral);
                var p = OwnedProj(s);
                if (t < spawn) { Assert.IsNull(p, $"no projectile before tick {spawn} (seen on tick {t})"); continue; }
                if (t == spawn + life) { Assert.IsNull(p, $"projectile must be gone at end of tick {t} (life {life})"); continue; }
                Assert.IsNotNull(p, $"projectile must be alive on tick {t}");
                int n = t - spawn;
                Assert.AreEqual(n == 0 ? x0 : xAfter(n), p.X, tol, $"projectile X on tick {t} ({n} moves)");
                Assert.AreEqual(y0, p.Y, $"projectile Y on tick {t}");
            }
        }

        // ------------------------------------------------------------------ reload (doc section 4, section 10 "Reload")

        static readonly int[] ChainPresses = { 0, LightLock, 2 * LightLock, 3 * LightLock };   // finisher on 39, strike 45 (v22)
        const int FinisherStrikeTick = 3 * LightLock + LightStrike;                            // 45 (v22)
        const int ReloadAcceptTick = FinisherStrikeTick + ReloadTicks;                          // 135 (v22)

        [TestCase(FighterClass.Ranged)]
        [TestCase(FighterClass.Magic)]
        public void Reload_StartsOnFinisherShot_PressAtEndMinus1Ignored_AtEndAccepted(FighterClass cls)
        {
            // Ignored (and lost) on ReloadAcceptTick - 1
            var s = FarArena(cls, BasePassive(cls));
            int[] withIgnored = { 0, LightLock, 2 * LightLock, 3 * LightLock, ReloadAcceptTick - 1 };
            var tr = Run(s, ReloadAcceptTick + 12, On(ChibiButtons.Light, withIgnored), t =>
            {
                if (t == FinisherStrikeTick) Assert.AreEqual(0, s.F[0].SetCd, "secondary: reload not set before the strike tick");
                if (t == FinisherStrikeTick + 1) Assert.AreEqual(ReloadTicks, s.F[0].SetCd, "secondary: SetCd = 90 set on the strike tick");
            });
            foreach (int p in ChainPresses) Assert.AreEqual(LightSlot, tr.AttackerSlot[p], $"chain light on tick {p} accepted");
            AssertSlotNeverStarts(tr, ReloadAcceptTick - 1, ReloadAcceptTick + 10, $"Light on tick {ReloadAcceptTick - 1} during reload");

            // Accepted on ReloadAcceptTick
            s = FarArena(cls, BasePassive(cls));
            int[] withAccepted = { 0, LightLock, 2 * LightLock, 3 * LightLock, ReloadAcceptTick };
            tr = Run(s, ReloadAcceptTick + 2, On(ChibiButtons.Light, withAccepted));
            Assert.AreEqual(LightSlot, tr.AttackerSlot[ReloadAcceptTick], $"Light on tick {ReloadAcceptTick} must start a move");
        }

        [TestCase(FighterClass.Ranged)]
        [TestCase(FighterClass.Magic)]
        public void Reload_DoesNotBlockSkills(FighterClass cls)
        {
            const int skillTick = 60;      // v22: finisher on 39, normal lock over on 52, reload until 135
            var s = FarArena(cls, BasePassive(cls));

            var tr = Run(s, skillTick + 2, Or(On(ChibiButtons.Light, ChainPresses), On(ChibiButtons.Skill1, skillTick)));

            int slot = tr.AttackerSlot[skillTick];
            Assert.IsTrue(slot != NoMove && slot != LightSlot && slot != HeavySlot,
                $"Skill1 on tick {skillTick} during reload must start a skill move (MoveSlot was {slot})");
        }
    }
}
