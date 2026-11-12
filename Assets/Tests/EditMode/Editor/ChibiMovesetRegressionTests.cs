// Story type: Logic (existing-move regression, rules v18) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Source spec: decision CH-2 ("keep each class's current kit on its current inputs"), design/gdd/movesets/README.md 1.2.
using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Neutral inputs keep the old moves bit for bit. A state-aware bot plays every class pairing with only the inputs
    /// whose meaning rules v18 did not change (neutral Light while grounded, neutral Heavy, neutral skills, Shield, the
    /// ult gesture; no direction, no jump, no dash, no Special), and a fingerprint of the v17-era gameplay fields is
    /// compared with the value recorded by running this exact file against the rules v17 build (git HEAD 73de00f).
    /// The fingerprint reads only fields that existed in v17, so new hashed fields cannot hide a behaviour change.
    /// </summary>
    public class ChibiMovesetRegressionTests
    {
        const int Ticks = 1500;

        /// <summary>
        /// Rules v20 re-pin, ON PURPOSE (percent-knockback.md, GL-1..GL-3): every landed hit now launches by the percent
        /// formula and the fingerprint reads PctM instead of the retired HpU, so the v17 trace (git 73de00f: 0x66970CB8u,
        /// 0x6FF11880u, 0x2CC7FBFBu, 0xC5BD08EDu, 0xC6965F65u) cannot hold any more. v18/v19 kept it bit for bit, which
        /// proved the moveset port left neutral inputs alone; these values pin the v20 trace so a later change to the
        /// neutral kit still shows up here. Recorded by running this file against the rules v20 build outside Unity.
        /// </summary>
        static readonly uint[] GoldenV20 = { 0x89FC7AFFu, 0x833EAAF2u, 0x382E95DCu, 0x46F72735u, 0xD56F4841u };

        static ChibiInputPacket Bot(ChibiSimState s, int i, int n, int seed)
        {
            unchecked
            {
                uint h = (uint)(n / 3) * 2654435761u + (uint)seed * 40503u + (uint)i * 977u;
                h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
                var f = s.F[i];
                ChibiButtons b = ChibiButtons.None;
                if (n % 2 == 0)
                {
                    if (h % 4 == 0 && f.Grounded) b |= ChibiButtons.Light;
                    else if (h % 7 == 1) b |= ChibiButtons.Heavy;
                    else if (h % 11 == 2) b |= ChibiButtons.Skill1;
                    else if (h % 13 == 3) b |= ChibiButtons.Skill2;
                    else if (h % 17 == 4) b |= ChibiButtons.Skill3;
                    else if (h % 19 == 5) b |= ChibiButtons.Ultimate;
                }
                if ((h >> 8) % 9 == 0) b |= ChibiButtons.Shield;
                var g = f.UltWindowTicks > 0 ? (Gesture)ChibiSimConst.UltGesture[f.Class] : Gesture.None;
                return ChibiInputPacket.Make(ChibiDir.Neutral, b, g);
            }
        }

        static void Mix(ref uint h, int v)
        {
            unchecked { h ^= (uint)v; h *= 16777619u; h ^= h >> 15; }
        }

        /// <summary>Fingerprint of v17 gameplay fields only (positions, velocities, HP, move, stun, meter, cooldowns, shots).</summary>
        static uint Fingerprint(ChibiSimState s, uint h)
        {
            for (int i = 0; i < 2; i++)
            {
                var f = s.F[i];
                Mix(ref h, f.X); Mix(ref h, f.Y); Mix(ref h, f.Vx); Mix(ref h, f.Vy); Mix(ref h, f.PctM); Mix(ref h, f.Facing);
                Mix(ref h, f.MoveSlot); Mix(ref h, f.MoveTick); Mix(ref h, f.MoveMult); Mix(ref h, f.Stun); Mix(ref h, f.Invuln);
                Mix(ref h, f.Ult); Mix(ref h, f.Cd[0]); Mix(ref h, f.Cd[1]); Mix(ref h, f.Cd[2]); Mix(ref h, f.ComboIdx);
                Mix(ref h, f.GuardU); Mix(ref h, f.Shielding ? 1 : 0); Mix(ref h, f.Grounded ? 1 : 0); Mix(ref h, f.SetCd);
            }
            foreach (var p in s.Proj)
            {
                if (!p.Active) continue;
                Mix(ref h, p.X); Mix(ref h, p.Y); Mix(ref h, p.Vx); Mix(ref h, p.Vy); Mix(ref h, p.Life); Mix(ref h, (int)p.Kind);
            }
            Mix(ref h, s.GameTicks); Mix(ref h, s.Rng.State == 0 ? 0 : (int)s.Rng.State); Mix(ref h, s.MatchOver ? 1 : 0);
            return h;
        }

        static uint Play(int c0)
        {
            var cfg = new ChibiMatchConfig
            {
                Seed = (uint)(777 + c0), Class0 = (FighterClass)c0, Class1 = (FighterClass)((c0 + 2) % 5), Stage = c0 % 3,
                Training = true,
            };
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, cfg);
            uint h = 2166136261u;
            for (int n = 0; n < Ticks; n++)
            {
                var a = Bot(s, 0, n, 1);
                var b = Bot(s, 1, n, 2);
                ChibiSimulation.Step(s, a, b);
                if (n % 30 == 29) h = Fingerprint(s, h);
            }
            return h;
        }

        static IEnumerable<TestCaseData> Pairings()
        {
            for (int c = 0; c < 5; c++)
                yield return new TestCaseData(c).SetName($"NeutralInputs_{(FighterClass)c}_vs_{(FighterClass)((c + 2) % 5)}_MatchThePinnedTraceBitForBit");
        }

        [TestCaseSource(nameof(Pairings))]
        public void NeutralInputs_MatchThePinnedTraceBitForBit(int c0)
        {
            // Arrange / Act
            uint h = Play(c0);

            // Assert
            Assert.AreEqual(GoldenV20[c0], h, $"pinned v20 fingerprint changed: now 0x{h:X8}u (neutral inputs must keep their moves)");
        }
    }
}
