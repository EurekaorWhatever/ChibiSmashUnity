// Story type: Logic (CPU positioning and defence fixes, review 2026-11-16 batch 2, items 10-15) -- evidence: automated
// EditMode unit test -- gate: BLOCKING
// Source: production review 2026-11-16 (ChibiCpuInputSource): descend to a lower opponent, drop the shield after the
// threat, edge-guard depth, under-stage recovery, air jump timing / no Jump in hitstun, walk-cancel aware punish.
using System;
using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>ChibiCpuInputSource: follows an opponent down, shield discipline, recovery timing, punish reads.</summary>
    public class ChibiCpuPositioningTests
    {
        static readonly FighterClass[] Classes =
            { FighterClass.Melee, FighterClass.Heavy, FighterClass.Ranged, FighterClass.Magic, FighterClass.Polearm };

        static ChibiMatchConfig Cfg(FighterClass c0, FighterClass c1, uint seed, int stage, bool hazardsOff = true) =>
            new ChibiMatchConfig { Seed = seed, Class0 = c0, Class1 = c1, Stage = stage, HazardsOff = hazardsOff };

        /// <summary>P0 = scripted (idle by default) or a CPU, P1 = the CPU under test; records the CPU's packets.</summary>
        sealed class Rig
        {
            public readonly ChibiLocalSession Session;
            public readonly List<ChibiInputPacket> Sent = new List<ChibiInputPacket>();

            public Rig(ChibiMatchConfig cfg, int difficulty, IChibiInputSource p0 = null)
            {
                Session = new ChibiLocalSession(cfg, p0 ?? new ChibiScriptedInputSource(_ => default),
                                                new ChibiCpuInputSource(1, difficulty, cfg.Seed));
                Session.OnFrame += (f, a, b) => Sent.Add(b);
            }

            public ChibiSimState S => Session.State;
            public ChibiFighterSim Cpu => S.F[1];
            public ChibiFighterSim Opp => S.F[0];
            public bool Tick() => Session.Tick();
            public void Run(int ticks) { for (int t = 0; t < ticks && Session.Tick(); t++) { } }
            public ChibiInputPacket Last => Sent[Sent.Count - 1];
        }

        /// <summary>Puts a fighter at rest, free to act (no move, stun, dash, invulnerability).</summary>
        static void Free(ChibiFighterSim f, int x, int y, bool grounded, int facing)
        {
            f.X = x; f.Y = y; f.XRem = 0; f.YRem = 0; f.Vx = 0; f.Vy = 0; f.Grounded = grounded; f.Facing = facing;
            f.Stun = 0; f.MoveSlot = -1; f.MoveLockEnd = 0; f.DashTicks = 0; f.Invuln = 0; f.AirJumps = 1; f.AirUsedMask = 0;
            f.ClearLaunch();
        }

        // ================================================================== item 10: follow the opponent down (Ocean)

        [TestCaseSource(nameof(Classes))]
        public void Ocean_OpponentOnLowerTier_CpuGoesDownAndFights(FighterClass c)
        {
            var st = ChibiStageTable.Get(ChibiStageTable.Ocean);
            int fought = 0;
            for (int side = -1; side <= 1; side += 2)
            {
                var r = new Rig(Cfg(FighterClass.Heavy, c, 7100u + (uint)c * 3 + (uint)(side + 1), ChibiStageTable.Ocean), 3);
                r.Run(90);
                var tier = st.Platforms[side < 0 ? 0 : 1];
                Free(r.Opp, tier.Cx, tier.Top, true, -side);
                Free(r.Cpu, side * 2000, 0, true, side);
                r.S.HitstopRealTicks = 0;
                bool reached = false;
                for (int t = 0; t < 900 && r.Tick(); t++)
                {
                    var f = r.Cpu;
                    if (f.Grounded && f.Y == tier.Top && Math.Abs(f.X - tier.Cx) <= tier.HalfW) reached = true;
                    if (r.Opp.PctM > 0) break;
                }
                Assert.AreEqual(0, r.Cpu.KosTaken, $"{c} side {side}: the CPU never fell out going down");
                Assert.IsTrue(reached || r.Opp.PctM > 0, $"{c} side {side}: the CPU reached the tier (at {r.Cpu.X}, {r.Cpu.Y})");
                if (r.Opp.PctM > 0) fought++;
            }
            Assert.AreEqual(2, fought, $"{c}: the CPU damaged the opponent standing on the lower tier on both sides");
        }

        [TestCaseSource(nameof(Classes))]
        public void Ocean_CpuOnTierWithOpponent_StaysAndFights_ThenReturnsWhenOpponentLeaves(FighterClass c)
        {
            var st = ChibiStageTable.Get(ChibiStageTable.Ocean);
            var tier = st.Platforms[1];
            var r = new Rig(Cfg(FighterClass.Heavy, c, 7300u + (uint)c, ChibiStageTable.Ocean), 3);
            r.Run(90);
            Free(r.Opp, tier.Cx + 900, tier.Top, true, -1);
            Free(r.Cpu, tier.Cx - 900, tier.Top, true, 1);
            r.S.HitstopRealTicks = 0;
            int maxY = int.MinValue;
            for (int t = 0; t < 300 && r.Tick() && r.Opp.PctM == 0; t++) maxY = Math.Max(maxY, r.Cpu.Y);
            Assert.Greater(r.Opp.PctM, 0, $"{c}: the CPU fought on the tier");
            Assert.Less(maxY, st.MainBottom, $"{c}: the CPU did not recover up to MAIN while the opponent stood beside it (max Y {maxY})");

            // The opponent leaves (back on MAIN): the CPU climbs back up without being KO'd.
            Free(r.Opp, 0, 0, true, 1);
            r.Opp.PctM = 0;
            bool home = false;
            for (int t = 0; t < 600 && r.Tick(); t++)
                if (r.Cpu.InPlay && r.Cpu.Grounded && r.Cpu.Y >= 0) { home = true; break; }
            Assert.IsTrue(home, $"{c}: back on the main stage once the opponent left (at {r.Cpu.X}, {r.Cpu.Y})");
            Assert.AreEqual(0, r.Cpu.KosTaken, $"{c}: never KO'd");
        }

        // ================================================================== item 11: drop the shield after the threat

        [TestCase(1)] [TestCase(2)] [TestCase(3)]
        public void Shield_ReleasedOnceTheOpponentsMoveIsOver(int difficulty)
        {
            int longest = 0, shieldedDuringMove = 0;
            for (int k = 0; k < 6; k++)
            {
                var jab = ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(0, 0), ChibiButtons.Light).Raw;
                var p0 = new ChibiScriptedInputSource(t => t % 45 == 0 ? new ChibiInputPacket { Raw = jab } : default);
                var r = new Rig(Cfg(FighterClass.Melee, Classes[k % Classes.Length], 7500u + (uint)k, ChibiStageTable.SkyDojo), difficulty, p0);
                r.Run(90);
                Free(r.Opp, -500, 0, true, 1);
                Free(r.Cpu, 500, 0, true, -1);
                r.S.HitstopRealTicks = 0;
                int run = 0;
                for (int t = 0; t < 900; t++)
                {
                    // No threat: no move, or a jab past its active ticks (it shoots nothing).
                    var o = r.Opp;
                    bool oppIdle = o.MoveSlot < 0 || o.MoveTick >= o.MoveStartup + o.MoveActive;
                    if (!r.Tick()) break;
                    bool sh = r.Last.Has(ChibiButtons.Shield);
                    if (sh && !oppIdle) shieldedDuringMove++;
                    run = sh && oppIdle ? run + 1 : 0;
                    longest = Math.Max(longest, run);
                }
            }
            Assert.Greater(shieldedDuringMove, 0, "precondition: the CPU shielded during an opponent move");
            Assert.LessOrEqual(longest, 2, $"difficulty {difficulty}: shield held {longest} ticks after the opponent's active ticks ended");
        }

        // ================================================================== item 14: no Jump in hitstun, no early air jump

        [TestCaseSource(nameof(Classes))]
        public void OffStage_InHitstun_SendsNoJump(FighterClass c)
        {
            var st = ChibiStageTable.Get(ChibiStageTable.SkyDojo);
            var r = new Rig(Cfg(c, c, 7700u + (uint)c, ChibiStageTable.SkyDojo), 3);
            r.Run(90);
            Free(r.Cpu, st.HalfWidth + 2000, -500, false, -1);
            r.Cpu.Vy = -40000;
            r.Cpu.Stun = 30;
            r.S.HitstopRealTicks = 0;
            int checkedTicks = 0;
            while (r.Cpu.Stun > 0 && r.Cpu.InPlay)
            {
                if (!r.Tick()) break;
                Assert.IsFalse(r.Last.Has(ChibiButtons.Jump), $"{c}: Jump sent in hitstun (stun was > 0 at sample time)");
                checkedTicks++;
            }
            Assert.Greater(checkedTicks, 20, "precondition: the stun lasted");
        }

        [TestCaseSource(nameof(Classes))]
        public void JustOutsideTheLedge_AboveStageLevel_KeepsTheAirJump_AndLands(FighterClass c)
        {
            var st = ChibiStageTable.Get(ChibiStageTable.SkyDojo);
            for (int side = -1; side <= 1; side += 2)
            {
                var r = new Rig(Cfg(c, c, 7900u + (uint)c, ChibiStageTable.SkyDojo), 3);
                r.Run(90);
                Free(r.Cpu, side * (st.HalfWidth + 300), 450, false, -side);
                r.S.HitstopRealTicks = 0;
                bool landed = false;
                for (int t = 0; t < 300; t++)
                {
                    int y = r.Cpu.Y;                                     // the CPU samples the state at the start of the tick
                    if (!r.Tick()) break;
                    if (r.Last.Has(ChibiButtons.Jump))
                        Assert.Less(y, -200, $"{c} side {side}: air jump pressed above stage level (feet {y})");
                    if (r.Cpu.Grounded && r.Cpu.Y == 0) { landed = true; break; }
                }
                Assert.IsTrue(landed, $"{c} side {side}: landed on the main stage (at {r.Cpu.X}, {r.Cpu.Y})");
                Assert.AreEqual(0, r.Cpu.KosTaken, $"{c} side {side}: not KO'd");
            }
        }

        // ================================================================== item 13: deep under a wide stage

        /// <summary>
        /// Starts under Sky Dojo's underside, facing the centre, both sides: the old CPU spent its air jump into the
        /// underside and only drifted, and died from every (3000, -3000) start (probe 2026-11-17). The new one air-dashes
        /// out and keeps the jump until clear. Known gaps (follow-up): Kaia from 3000+ under, and any class facing away
        /// from the stage (air facing is locked, so the up special carries it outward). (5500, -1800) is a guard case
        /// that already recovered.
        /// </summary>
        [TestCase(FighterClass.Heavy, 3000, -3000)] [TestCase(FighterClass.Ranged, 3000, -3000)]
        [TestCase(FighterClass.Magic, 3000, -3000)] [TestCase(FighterClass.Polearm, 3000, -3000)]
        [TestCase(FighterClass.Heavy, 5000, -3000)] [TestCase(FighterClass.Polearm, 5000, -3000)]
        [TestCase(FighterClass.Melee, 5500, -1800)] [TestCase(FighterClass.Ranged, 5500, -1800)]
        public void UnderTheStage_FacingTheCentre_RecoversWithoutKo(FighterClass c, int x, int y)
        {
            int fail = 0;
            string where = "";
            for (int side = -1; side <= 1; side += 2)
                {
                    int face = -side;                                    // facing the centre
                    var r = new Rig(Cfg(c, c, 8100u + (uint)c, ChibiStageTable.SkyDojo), 3);
                    r.Run(90);
                    Free(r.Cpu, side * x, y, false, face);
                    r.Cpu.Vy = -20000;
                    r.S.HitstopRealTicks = 0;
                    bool home = false;
                    for (int t = 0; t < 600 && r.Tick(); t++)
                    {
                        if (r.Cpu.KosTaken > 0) break;
                        if (r.Cpu.Grounded && r.Cpu.Y >= 0) { home = true; break; }
                    }
                    if (!home) { fail++; where += $"(side {side}, facing {face}: {r.Cpu.X}, {r.Cpu.Y}, KOs {r.Cpu.KosTaken}) "; }
                }
            Assert.AreEqual(0, fail, $"{c} from ({x}, {y}): back on the stage from every start. Failed: {where}");
        }

        // ================================================================== item 12: edge-guard a lower recovery

        /// <summary>
        /// Guard for the deferred item 12: an opponent recovering from 2200 below is not chased (the deeper hop killed Taro,
        /// whose up special carries it outward when facing away), and the CPU never dies near the ledge.
        /// </summary>
        [Test]
        public void EdgeGuard_OpponentWellBelowTheLedge_CpuStaysOn_NeverDies()
        {
            var st = ChibiStageTable.Get(ChibiStageTable.SkyDojo);
            int wentOut = 0;
            for (int k = 0; k < 20; k++)
            {
                var c = Classes[k % Classes.Length];
                var cfg = Cfg(FighterClass.Heavy, c, 8300u + (uint)k, ChibiStageTable.SkyDojo);
                var r = new Rig(cfg, 3);                                 // idle opponent: any KO is the CPU's own
                r.Run(90);
                r.Opp.PctM = 0;                                          // the warm-up may have hit it: pin from a clean state
                Free(r.Opp, st.HalfWidth + 1500, -2200, false, -1);
                Free(r.Cpu, st.HalfWidth - 1200, 0, true, 1);
                r.S.HitstopRealTicks = 0;
                // The opponent hangs 2200 below the ledge level (a slow recovery) until it is hit or 120 ticks pass.
                bool outside = false, pinned = true;
                for (int t = 0; t < 600; t++)
                {
                    if (pinned && (t >= 120 || r.Opp.Stun > 0 || r.Opp.PctM > 0)) pinned = false;
                    if (pinned) { Free(r.Opp, st.HalfWidth + 1500, -2200, false, -1); r.Opp.AirJumps = 0; }
                    if (!r.Tick()) break;
                    if (t < 120 && r.Cpu.InPlay && Math.Abs(r.Cpu.X) > st.HalfWidth) outside = true;
                }
                if (outside) wentOut++;
                Assert.AreEqual(0, r.Cpu.KosTaken, $"trial {k} ({c}): the edge-guarding CPU was KO'd");
            }
            Assert.AreEqual(0, wentOut, "the CPU did not go out after an opponent recovering from 2200 below");
        }

        // ================================================================== item 15: punish read knows walk cancels

        static ChibiFighterSim MoveOut(int slot, int lockLeft, bool hit, bool grounded = true)
        {
            var s = new ChibiLocalSession(Cfg(FighterClass.Melee, FighterClass.Melee, 1u, ChibiStageTable.SkyDojo),
                                          new ChibiScriptedInputSource(_ => default), new ChibiScriptedInputSource(_ => default)).State;
            var f = s.F[0];
            Free(f, 0, grounded ? 0 : 2000, grounded, 1);
            f.MoveSlot = slot;
            f.MoveStartup = 4; f.MoveActive = 3;
            f.MoveTick = 20;
            f.MoveLockEnd = f.MoveTick + lockLeft;
            f.MoveHit = hit;
            return f;
        }

        [Test]
        public void Vulnerable_AccountsForWalkCancel()
        {
            Assert.IsFalse(ChibiCpuInputSource.Vulnerable(MoveOut(0, 14, false)), "light: cancels 6 early, 8 ticks left is no punish");
            Assert.IsTrue(ChibiCpuInputSource.Vulnerable(MoveOut(0, 30, false)), "light with 30 left: still punishable");
            Assert.IsTrue(ChibiCpuInputSource.Vulnerable(MoveOut(1, 14, false)), "heavy whiff: no cancel, 14 left is punishable");
            Assert.IsFalse(ChibiCpuInputSource.Vulnerable(MoveOut(1, 14, true)), "heavy that hit: cancels 6 early");
            Assert.IsTrue(ChibiCpuInputSource.Vulnerable(MoveOut(0, 14, false, grounded: false)), "airborne: no walk cancel");
        }
    }
}
