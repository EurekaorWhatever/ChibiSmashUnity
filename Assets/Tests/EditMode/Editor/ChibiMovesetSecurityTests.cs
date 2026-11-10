// Story type: Logic (moveset abuse resistance, rules v19) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Source: security-engineer review of the rules v18 movesets (SEC-101 .. SEC-106), design/gdd/movesets/README.md.
using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;
using static ChibiClash.Tests.MovesetKit;

namespace ChibiClash.Tests
{
    /// <summary>Data limits, cooldown / airtime gates under macros, reflect growth cap, stale-state clearing, input fuzz.</summary>
    public class ChibiMovesetSecurityTests
    {
        // ================================================================== SEC-105 data limits

        [Test]
        public void DataLimits_EveryRowFitsTheSimState()
        {
            foreach (var c in Classes)
            {
                int cls = (int)c, zones = 0;
                int rows = ChibiMoveTable.Count(cls) - ChibiMovesetData.FirstNewSlot;
                Assert.LessOrEqual(rows, ChibiMovesetConst.MaxRowsPerClass, c + ": AirUsedMask bit (slot - 7) must stay below 31");
                Assert.AreEqual(ChibiMoveInfo.Count(cls), rows, c + ": metadata row per sim row");
                for (int slot = ChibiMovesetData.FirstNewSlot; slot < ChibiMoveTable.Count(cls); slot++)
                {
                    var m = ChibiMoveTable.Get(cls, slot);
                    var e = m.Ext;
                    string id = ChibiMoveInfo.Get(cls, slot).Id;
                    Assert.IsNotNull(e, id + ": ext");
                    Assert.That(e.TechIdx, Is.InRange(-1, new ChibiFighterSim().TechCd.Length - 1), id + ": TechIdx inside TechCd");
                    if (m.Cooldown > 0) Assert.GreaterOrEqual(e.TechIdx, 0, id + ": a cooldown needs a technique slot");
                    if (e.Has(ChibiMoveExt.FFan))
                    {
                        Assert.IsTrue(e.FanVx != null && e.FanVx.Length > 0 && e.FanVx.Length == e.FanVy.Length, id + ": fan arrays");
                        int shots = m.Rehit <= 0 ? 1 : (m.Active - 1) / m.Rehit + 1;
                        Assert.GreaterOrEqual(e.FanVx.Length, shots, id + ": one velocity per shot");
                    }
                    if (e.Has(ChibiMoveExt.FMulti)) Assert.Greater(m.Rehit, 0, id + ": multi needs a rehit period");
                    if (e.Has(ChibiMoveExt.FCharge)) Assert.Greater(e.ChargeMax, 0, id + ": charge MAX");
                    if (e.Has(ChibiMoveExt.FZone))
                    {
                        zones++;
                        Assert.That(e.ZoneKind, Is.InRange(1, 3), id + ": zone kind");
                        if (e.ZoneKind == 3) Assert.Greater(e.ZoneA, 0, id + ": periodic period");
                    }
                    if (e.Has(ChibiMoveExt.FChargeLevel))
                    {
                        Assert.That(slot, Is.InRange(e.ChargeGroupBase, e.ChargeGroupBase + e.ChargeGroupCount - 1), id + ": in its group");
                        Assert.LessOrEqual(e.ChargeGroupBase + e.ChargeGroupCount, ChibiMoveTable.Count(cls), id + ": group inside the table");
                    }
                }
                Assert.LessOrEqual(zones, ChibiMovesetConst.ZonePool / 2, c + ": zone rows <= ZonePool / 2 (pool shared by two fighters)");
            }
        }

        // ================================================================== SEC-101 Jade Stance cooldown

        [Test]
        public void JadeStance_HasACooldown_SharedWithJadeCounter()
        {
            // Arrange
            var s = Arena(FighterClass.Polearm, FighterClass.Heavy, 0, 6000);
            var stance = Row(FighterClass.Polearm, "taro_dspec");
            Assert.AreEqual(240, stance.Cooldown, "SEC-101: cd 240");

            // Act: stance, wait for it to end, then try the stance and Jade Counter again inside the cooldown.
            Game(s, P(ChibiDir.S, ChibiButtons.Special));
            Assert.AreEqual("taro_dspec", IdOf(s.F[0]));
            Idle(s, stance.Startup + stance.Active + stance.Recovery + 1);
            Game(s, P(ChibiDir.S, ChibiButtons.Special));
            int again = s.F[0].MoveSlot;
            Idle(s, 1);
            Game(s, P(ChibiDir.S, ChibiButtons.Skill2));
            int counter = s.F[0].MoveSlot;

            // Assert
            Assert.AreEqual(-1, again, "stance on cooldown: the press is dropped");
            Assert.AreEqual(-1, counter, "Jade Counter shares the cooldown: nothing fires");
        }

        // ================================================================== macros vs gates

        [Test]
        public void Macro_SpecialUpEveryOtherTick_OneAirOnceStartPerAirtime()
        {
            // Arrange: Kaia high above the centre platform; a macro presses Special + Up on every other tick (an edge each time).
            var s = Arena(FighterClass.Melee, FighterClass.Heavy, 0, 7000);
            Place(s.F[0], 0, 9000, 1);
            int skyhook = Slot(FighterClass.Melee, "kaia_uspec");
            int startsThisAirtime = 0, maxPerAirtime = 0, airtimes = 0, prev = -1;

            // Act
            for (int n = 0; n < 900; n++)
            {
                Game(s, n % 2 == 0 ? P(ChibiDir.N, ChibiButtons.Special) : P(ChibiDir.N));
                var f = s.F[0];
                if (f.MoveSlot == skyhook && prev != skyhook && !f.Grounded) startsThisAirtime++;
                if (f.Grounded) { if (startsThisAirtime > 0) airtimes++; startsThisAirtime = 0; }
                if (startsThisAirtime > maxPerAirtime) maxPerAirtime = startsThisAirtime;
                prev = f.MoveSlot;
            }

            // Assert
            Assert.AreEqual(1, maxPerAirtime, "aironce: at most one airborne Skyhook per airtime");
            Assert.GreaterOrEqual(airtimes, 1, "precondition: at least one airtime with a Skyhook");
        }

        [Test]
        public void Macro_SkillUpEveryOtherTick_OneStartPerCooldown()
        {
            // Arrange: Kaia Parry Riposte (S2+U, cd 240) pressed on every other tick for 1200 ticks.
            var s = Arena(FighterClass.Melee, FighterClass.Heavy, 0, 7000);
            int riposte = Slot(FighterClass.Melee, "kaia_riposte");
            int cd = Row(FighterClass.Melee, "kaia_riposte").Cooldown;
            var starts = new List<int>();
            int prev = -1;

            // Act
            for (int n = 0; n < 1200; n++)
            {
                Game(s, n % 2 == 0 ? P(ChibiDir.N, ChibiButtons.Skill2) : P(ChibiDir.N));
                if (s.F[0].MoveSlot == riposte && prev != riposte) starts.Add(n);
                prev = s.F[0].MoveSlot;
            }

            // Assert
            Assert.GreaterOrEqual(starts.Count, 4, "precondition: the technique fires when ready");
            for (int k = 1; k < starts.Count; k++)
                Assert.GreaterOrEqual(starts[k] - starts[k - 1], cd, "never twice inside one cooldown");
        }

        // ================================================================== SEC-104 reflect growth

        [TestCase(0, 1250)]
        [TestCase(1, 1250)]
        [TestCase(2, 1000)]
        [TestCase(5, 1000)]
        public void Reflect_OnlyTheFirstTwoReflectionsScaleTheMult(int reflectsSoFar, int expectedPermilleOfBefore)
        {
            // Arrange: Lyra in Prism Reflection, an enemy shot that was already reflected reflectsSoFar times.
            var s = Arena(FighterClass.Magic, FighterClass.Heavy, 0, 6000);
            Game(s, P(ChibiDir.S, ChibiButtons.Skill2));
            Idle(s, Row(FighterClass.Magic, "lyra_prism").Startup + 1);
            ChibiProjectileSim shot = null;
            foreach (var p in s.Proj) if (!p.Active) { shot = p; break; }
            shot.Active = true; shot.Owner = 1; shot.X = s.F[0].X + 300; shot.Y = s.F[0].Y + 675; shot.Vx = -9000; shot.Vy = 0;
            shot.XRem = shot.YRem = 0; shot.Life = 20; shot.Mult = 800; shot.KbX = 60000; shot.KbY = 30000; shot.Stun = 20;
            shot.SpawnFrame = s.GameTicks - 1; shot.Kind = ChibiProjKind.Arrow; shot.Flags = 0; shot.Radius = 300;
            shot.ResetMoveset();
            shot.Reflects = reflectsSoFar;

            // Act
            Game(s, N, N);

            // Assert
            Assert.AreEqual(0, shot.Owner, "reflected");
            Assert.AreEqual(800 * expectedPermilleOfBefore / 1000, shot.Mult);
            Assert.AreEqual(reflectsSoFar + 1, shot.Reflects);
        }

        // ================================================================== SEC-105 teleport floor clamp

        [Test]
        public void Teleport_NeverLandsBelowTheKillLine()
        {
            // Arrange: Lyra off-stage near the kill line, the captured direction forced downward (fixture: no input gives it).
            var s = Arena(FighterClass.Magic, FighterClass.Heavy, 0, 6000);
            var st = ChibiStageTable.Get(0);
            var row = Row(FighterClass.Magic, "lyra_uspec");
            Place(s.F[0], 12000, st.KillY + 2600, 1);
            Game(s, P(ChibiDir.N, ChibiButtons.Special));
            s.F[0].MoveDir = 1 + 3 * 0;                              // packed (dx 0, dy -1)

            // Act
            for (int k = 0; k <= row.Startup; k++) { s.F[0].Y = st.KillY + 2600; s.F[0].Vy = 0; Game(s, N, N); }

            // Assert
            Assert.GreaterOrEqual(s.F[0].Y, st.KillY + ChibiMovesetConst.TeleportMargin - 50, "clamped above the kill line");
            Assert.AreEqual(s.F[0].Stats.MaxHp * ChibiCombatStats.HpScale, s.F[0].HpU, "no ring-out penalty");
        }

        // ================================================================== SEC-106 stale move state

        [Test]
        public void Interrupt_ClearsAChargeInProgress()
        {
            // Arrange: Kaia charging a forward smash.
            var s = Arena(FighterClass.Melee, FighterClass.Heavy, 0, 6000);
            Game(s, P(ChibiDir.E, ChibiButtons.Heavy));
            for (int k = 0; k < 10; k++) Game(s, P(ChibiDir.Neutral, ChibiButtons.Heavy));
            Assert.IsTrue(s.F[0].Charging, "precondition");
            ChibiProjectileSim shot = null;
            foreach (var p in s.Proj) if (!p.Active) { shot = p; break; }
            shot.Active = true; shot.Owner = 1; shot.X = s.F[0].X; shot.Y = s.F[0].Y + 675; shot.Vx = 0; shot.Vy = 0;
            shot.Life = 5; shot.Mult = 300; shot.KbX = 30000; shot.KbY = 30000; shot.Stun = 20; shot.Radius = 600;
            shot.SpawnFrame = s.GameTicks - 1; shot.Kind = ChibiProjKind.Arrow; shot.Flags = 0; shot.ResetMoveset();

            // Act
            Game(s, P(ChibiDir.Neutral, ChibiButtons.Heavy));

            // Assert
            Assert.AreEqual(-1, s.F[0].MoveSlot, "stunned out of the charge");
            Assert.IsFalse(s.F[0].Charging, "Interrupt clears Charging");
        }

        [Test]
        public void AnyMoveStart_ClearsPullHoldAndPendingFlags()
        {
            // Arrange: stale runtime flags as a buggy path could leave them.
            var s = Arena(FighterClass.Melee, FighterClass.Heavy, 0, 6000);
            var f = s.F[0];
            f.PullTicks = 9; f.PullSpeed = 270000; f.AnchorX = 5000; f.AnchorY = 3000;
            f.HoldExt = 4; f.CounterStrike = 1; f.LandHitPending = 1; f.Charging = true;

            // Act: an old-kit move (neutral Light, slot 0).
            Game(s, P(ChibiDir.Neutral, ChibiButtons.Light));

            // Assert
            Assert.AreEqual(0, f.MoveSlot);
            Assert.AreEqual(0, f.PullTicks); Assert.AreEqual(0, f.HoldExt); Assert.AreEqual(0, f.CounterStrike);
            Assert.AreEqual(0, f.LandHitPending); Assert.IsFalse(f.Charging);
        }

        // ================================================================== input fuzz

        [Test]
        public void EveryInputValue_TwoSims_NoExceptionEqualChecksums()
        {
            // Arrange: training (no match end), both fighters fed every 16-bit value once (illegal directions included).
            var cfg = new ChibiMatchConfig { Seed = 65536, Class0 = FighterClass.Ranged, Class1 = FighterClass.Polearm, Training = true };
            var a = new ChibiSimState(); ChibiSimulation.Init(a, cfg);
            var b = new ChibiSimState(); ChibiSimulation.Init(b, cfg);

            // Act / Assert
            for (int raw = 0; raw < 65536; raw++)
            {
                var p0 = new ChibiInputPacket { Raw = (ushort)raw };
                var p1 = new ChibiInputPacket { Raw = (ushort)((raw * 40503 + 12345) & 0xFFFF) };
                ChibiSimulation.Step(a, p0, p1);
                ChibiSimulation.Step(b, p0, p1);
                if ((raw & 4095) == 4095) Assert.AreEqual(a.ComputeChecksum(), b.ComputeChecksum(), "diverged at input " + raw);
            }
            Assert.AreEqual(a.ComputeChecksum(), b.ComputeChecksum());
            Assert.AreEqual(0, a.DroppedShots, "projectile pool");
        }
    }
}
