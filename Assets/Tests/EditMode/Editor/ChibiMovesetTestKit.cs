// Story type: Logic (test fixture helper, rules v18 movesets) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Source spec: design/gdd/movesets/README.md (sections 1 and 5), design/gdd/movesets/movesets.csv.
using System;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>Shared fixture helpers for the rules v18 moveset tests (arena setup, inputs, slot lookup by CSV id).</summary>
    static class MovesetKit
    {
        public const uint Seed = 4242;
        /// <summary>Game steps to let both fighters land after Init (spawn height 775 milli).</summary>
        public const int Settle = 40;

        public static readonly FighterClass[] Classes =
            { FighterClass.Melee, FighterClass.Heavy, FighterClass.Ranged, FighterClass.Magic, FighterClass.Polearm };

        public static readonly ChibiInputPacket N = default;

        /// <summary>Both fighters grounded on Sky Dojo at <paramref name="x0"/> (facing +1) and <paramref name="x1"/> (facing -1), idle.</summary>
        public static ChibiSimState Arena(FighterClass c0, FighterClass c1 = FighterClass.Heavy, int x0 = -3000, int x1 = 3000,
                                          int passive0 = 0, int passive1 = 0)
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, new ChibiMatchConfig
                { Seed = Seed, Class0 = c0, Class1 = c1, Passive0 = passive0, Passive1 = passive1, Stage = 0 });
            for (int t = 0; t < Settle; t++) ChibiSimulation.Step(s, N, N);
            Place(s.F[0], x0, 0, 1);
            Place(s.F[1], x1, 0, -1);
            for (int t = 0; t < 3; t++) Game(s, N, N);
            return s;
        }

        /// <summary>Puts a fighter at (x, y) at rest; y &gt; 0 = airborne with Vy 0.</summary>
        public static void Place(ChibiFighterSim f, int x, int y, int facing)
        {
            f.X = x; f.Y = y; f.Vx = 0; f.Vy = 0; f.XRem = 0; f.YRem = 0; f.Grounded = y == 0; f.Facing = facing;
            f.Stun = 0; f.Invuln = 0;
        }

        public static ChibiInputPacket P(ChibiDir dir, ChibiButtons b = ChibiButtons.None) => ChibiInputPacket.Make(dir, b);

        /// <summary>One game step (repeats the packets through hitstop / cinematic real ticks).</summary>
        public static void Game(ChibiSimState s, ChibiInputPacket a, ChibiInputPacket b = default) => ChibiTestTime.StepGame(s, a, b);

        public static void Idle(ChibiSimState s, int steps)
        {
            for (int k = 0; k < steps; k++) Game(s, N, N);
        }

        /// <summary>Slot of the CSV row <paramref name="id"/> in class <paramref name="cls"/> (fails the test when absent).</summary>
        public static int Slot(FighterClass cls, string id)
        {
            int c = (int)cls;
            for (int k = 0; k < ChibiMoveInfo.Count(c); k++)
                if (ChibiMoveInfo.Get(c, ChibiMovesetData.FirstNewSlot + k).Id == id) return ChibiMovesetData.FirstNewSlot + k;
            Assert.Fail("no moveset row " + id + " for " + cls);
            return -1;
        }

        public static ChibiMove Row(FighterClass cls, string id) => ChibiMoveTable.Get((int)cls, Slot(cls, id));

        public static string IdOf(ChibiFighterSim f) =>
            f.MoveSlot >= ChibiMovesetData.FirstNewSlot ? ChibiMoveInfo.Get(f.Class, f.MoveSlot).Id : "slot " + f.MoveSlot;

        /// <summary>Button / direction for an input string of the CSV notation, for a fighter facing +1.</summary>
        public static void Decode(string input, out ChibiButtons button, out ChibiDir dir, out bool dash)
        {
            dash = input.EndsWith("@dash", StringComparison.Ordinal);
            if (dash) input = input.Substring(0, input.Length - 5);
            var parts = input.Split('+');
            switch (parts[0])
            {
                case "L": button = ChibiButtons.Light; break;
                case "H": button = ChibiButtons.Heavy; break;
                case "B": button = ChibiButtons.Special; break;
                case "S1": button = ChibiButtons.Skill1; break;
                case "S2": button = ChibiButtons.Skill2; break;
                case "S3": button = ChibiButtons.Skill3; break;
                default: throw new ArgumentException(input);
            }
            string d = parts.Length > 1 ? parts[1] : "";
            dir = d == "F" ? ChibiDir.E : d == "BK" ? ChibiDir.W : d == "U" ? ChibiDir.N : d == "D" ? ChibiDir.S : ChibiDir.Neutral;
        }

        /// <summary>
        /// Starts row <paramref name="id"/> for F[0] (facing +1) from the state its input needs and returns after the press
        /// step (MoveTick 0). Air rows are started from 4000 milli up.
        /// </summary>
        public static void StartRow(ChibiSimState s, FighterClass cls, string id, string input, bool air, ChibiButtons hold = ChibiButtons.None)
        {
            Decode(input, out var b, out var dir, out bool dash);
            if (air) Place(s.F[0], s.F[0].X, 4000, 1);
            if (dash) { Game(s, P(ChibiDir.E, ChibiButtons.Dash)); Game(s, P(dir == ChibiDir.S ? ChibiDir.S : ChibiDir.E, b | hold)); }
            else Game(s, P(dir, b | hold));
            Assert.AreEqual(id, IdOf(s.F[0]), $"{cls} {input} {(air ? "air" : "ground")} resolves to {id}");
        }
    }
}
