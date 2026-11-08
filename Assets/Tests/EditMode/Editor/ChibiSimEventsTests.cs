using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>ChibiSimEvents: each real state change yields exactly one presentation event.</summary>
    public class ChibiSimEventsTests
    {
        const uint Seed = 12345;
        const int CloseX = 500;          // fighters 1.0 world unit apart: inside Melee light reach
        const int RunTicks = 60;

        static ChibiMatchConfig Cfg() => new ChibiMatchConfig
        {
            Seed = Seed, Class0 = FighterClass.Melee, Class1 = FighterClass.Heavy, Passive0 = 0, Passive1 = 0
        };

        static ChibiSimState NewState()
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, Cfg());
            return s;
        }

        static ChibiSimState Copy(ChibiSimState s)
        {
            var c = new ChibiSimState();
            c.CopyFrom(s);
            return c;
        }

        static int Count(List<ChibiSimEvent> ev, ChibiSimEventKind k, int index = -1)
        {
            int n = 0;
            foreach (var e in ev) if (e.Kind == k && (index < 0 || e.Index == index)) n++;
            return n;
        }

        [Test]
        public void NoChange_EmitsNothing()
        {
            var a = NewState();
            var ev = new List<ChibiSimEvent>();
            ChibiSimEvents.Diff(a, Copy(a), ev);
            Assert.AreEqual(0, ev.Count);
        }

        [Test]
        public void HpDecrease_EmitsOneDamagedOnce()
        {
            const int Amount = 37;
            var a = NewState();
            var b = Copy(a);
            b.F[1].Hp -= Amount;
            var ev = new List<ChibiSimEvent>();
            ChibiSimEvents.Diff(a, b, ev);
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.Damaged));
            Assert.AreEqual(1, ev[0].Index);
            Assert.AreEqual(Amount, ev[0].Value);

            ev.Clear();
            ChibiSimEvents.Diff(b, Copy(b), ev);   // next frame, nothing new
            Assert.AreEqual(0, ev.Count);
        }

        [Test]
        public void MoveSlotToLight_EmitsOneMoveStarted()
        {
            var a = NewState();
            var b = Copy(a);
            b.F[0].MoveSlot = 0;
            b.F[0].MoveTick = 0;
            var ev = new List<ChibiSimEvent>();
            ChibiSimEvents.Diff(a, b, ev);
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.MoveStarted));
            Assert.AreEqual(0, ev[0].Index);
            Assert.AreEqual(0, ev[0].Value);

            var c = Copy(b);
            c.F[0].MoveTick = 1;                  // same move continuing
            ev.Clear();
            ChibiSimEvents.Diff(b, c, ev);
            Assert.AreEqual(0, Count(ev, ChibiSimEventKind.MoveStarted));
        }

        [Test]
        public void ShieldOnThenOff_EmitsTwoShieldChanged()
        {
            var a = NewState();
            var b = Copy(a);
            b.F[0].Shielding = true;
            var c = Copy(b);
            c.F[0].Shielding = false;
            var ev = new List<ChibiSimEvent>();
            ChibiSimEvents.Diff(a, b, ev);
            ChibiSimEvents.Diff(b, c, ev);
            Assert.AreEqual(2, Count(ev, ChibiSimEventKind.ShieldChanged));
            Assert.IsTrue(ev[0].On);
            Assert.IsFalse(ev[1].On);
        }

        [Test]
        public void ProjectileSpawnThenGone_EmitsOnceEach()
        {
            const int Slot = 3;
            var a = NewState();
            var b = Copy(a);
            b.Proj[Slot].Active = true;
            b.Proj[Slot].Owner = 1;
            b.Proj[Slot].Life = 10;
            var c = Copy(b);
            c.Proj[Slot].Life = 9;               // still flying
            var d = Copy(c);
            d.Proj[Slot].Active = false;

            var ev = new List<ChibiSimEvent>();
            ChibiSimEvents.Diff(a, b, ev);
            ChibiSimEvents.Diff(b, c, ev);
            ChibiSimEvents.Diff(c, d, ev);
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.ProjectileSpawned, Slot));
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.ProjectileGone, Slot));
            Assert.AreEqual(2, ev.Count);
            Assert.AreEqual(1, ev[0].Value);
        }

        [Test]
        public void ProjectileLifeRaisedSameSpawnFrame_EmitsNothing()
        {
            // Arrange: a returning spear's Life floor (projectile-conversion.md return rule, CO:73 Life = max(Life, 0.5))
            // raises Life on the SAME projectile. Same SpawnFrame/Owner/Kind = same projectile, so no gone/spawn (A6).
            const int Slot = 3, Owner = 1, Spawn = 12;
            const int OldLife = 20, NewLife = 30;
            var prev = NewState();
            var p = prev.Proj[Slot];
            p.Active = true; p.Owner = Owner; p.Kind = ChibiProjKind.Spear;
            p.SpawnFrame = Spawn; p.Life = OldLife;
            var cur = Copy(prev);
            cur.Proj[Slot].Life = NewLife;
            var ev = new List<ChibiSimEvent>();

            // Act
            ChibiSimEvents.Diff(prev, cur, ev);

            // Assert
            Assert.AreEqual(0, Count(ev, ChibiSimEventKind.ProjectileGone, Slot));
            Assert.AreEqual(0, Count(ev, ChibiSimEventKind.ProjectileSpawned, Slot));
        }

        [Test]
        public void ProjectileSlotReusedSameOwnerLessLife_EmitsGoneThenSpawned()
        {
            // Arrange: slot 2 holds owner 0's arrow (spawned on tick 10). In one step it despawns and owner 0 spawns a
            // new projectile into the same slot (tick 40) with LESS life than the old one had (A6, the lead -> arrow 0
            // reuse case). Owner and Kind are unchanged; only SpawnFrame, Life and position differ.
            const int Slot = 2, Owner = 0;
            const int OldSpawn = 10, NewSpawn = 40;
            const int OldLife = 50, NewLife = 27;
            const int OldX = 4000, NewX = 500;
            var prev = NewState();
            var p = prev.Proj[Slot];
            p.Active = true; p.Owner = Owner; p.Kind = ChibiProjKind.Arrow;
            p.SpawnFrame = OldSpawn; p.Life = OldLife; p.X = OldX;
            var cur = Copy(prev);
            var c = cur.Proj[Slot];
            c.SpawnFrame = NewSpawn; c.Life = NewLife; c.X = NewX;
            var ev = new List<ChibiSimEvent>();

            // Act
            ChibiSimEvents.Diff(prev, cur, ev);

            // Assert: the old projectile left and a new one arrived, in that order.
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.ProjectileGone, Slot));
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.ProjectileSpawned, Slot));
            Assert.AreEqual(2, ev.Count);
            Assert.AreEqual(ChibiSimEventKind.ProjectileGone, ev[0].Kind);
            Assert.AreEqual(ChibiSimEventKind.ProjectileSpawned, ev[1].Kind);
            Assert.AreEqual(Owner, ev[1].Value);
        }

        [Test]
        public void RealSimLightPress_StartsMoveAndDamagesP2()
        {
            var s = NewState();
            s.F[0].X = -CloseX;
            s.F[1].X = CloseX;
            var shown = Copy(s);
            var ev = new List<ChibiSimEvent>();
            var light = ChibiInputPacket.Make(ChibiDir.Neutral, ChibiButtons.Light);
            var idle = default(ChibiInputPacket);

            for (int t = 0; t < RunTicks; t++)
            {
                ChibiSimulation.Step(s, t == 0 ? light : idle, idle);
                ChibiSimEvents.Diff(shown, s, ev);
                shown.CopyFrom(s);
            }

            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.MoveStarted, 0));
            Assert.AreEqual(0, Count(ev, ChibiSimEventKind.MoveStarted, 1));
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.Damaged, 1));
            Assert.AreEqual(0, Count(ev, ChibiSimEventKind.Damaged, 0));
        }

        [Test]
        public void DashTicksRiseFromZero_EmitsOneDashStarted()
        {
            // Arrange
            var prev = NewState();
            prev.F[0].DashTicks = 0;
            var cur = Copy(prev);
            cur.F[0].DashTicks = 8;
            cur.F[0].DashDir = 1;
            var ev = new List<ChibiSimEvent>();

            // Act
            ChibiSimEvents.Diff(prev, cur, ev);

            // Assert
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.DashStarted));
            var dash = ev.Find(x => x.Kind == ChibiSimEventKind.DashStarted);
            Assert.AreEqual(0, dash.Index);
            Assert.AreEqual(1, dash.Value);

            // Act: dash continues counting down
            prev = Copy(cur);
            cur.F[0].DashTicks = 7;
            ev.Clear();
            ChibiSimEvents.Diff(prev, cur, ev);

            // Assert
            Assert.AreEqual(0, Count(ev, ChibiSimEventKind.DashStarted));
        }

        [Test]
        public void CinematicRealTicksRise_EmitsOneCinematicStarted()
        {
            // Arrange
            const int CinematicTicks = 48;   // 0.8 s at 60 Hz (MatchController.Cinematic)
            var prev = NewState();
            prev.CinematicRealTicks = 0;
            prev.CinematicFighter = -1;
            var cur = Copy(prev);
            cur.CinematicRealTicks = CinematicTicks;
            cur.CinematicFighter = 1;
            var ev = new List<ChibiSimEvent>();

            // Act
            ChibiSimEvents.Diff(prev, cur, ev);

            // Assert
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.CinematicStarted));
            Assert.AreEqual(1, ev.Find(x => x.Kind == ChibiSimEventKind.CinematicStarted).Index);

            // Act: next frozen real tick, the cinematic counts down
            prev = Copy(cur);
            cur.CinematicRealTicks = CinematicTicks - 1;
            ev.Clear();
            ChibiSimEvents.Diff(prev, cur, ev);

            // Assert
            Assert.AreEqual(0, Count(ev, ChibiSimEventKind.CinematicStarted));
        }
    
        // ---------------------------------------------------------------- step 12: program move frames / phases

        const int Heavy = 1;          // Cfg(): F[1] is Heavy (slot 2 Grab, slot 3 Tornado)
        const int GrabSlot = 2, TornadoSlot = 3;

        static ChibiSimEvent First(List<ChibiSimEvent> ev, ChibiSimEventKind k)
        {
            return ev.Find(x => x.Kind == k);
        }

        [Test]
        public void ProgramMoveStart_EmitsProgramFrameZero()
        {
            // Arrange: Tornado pressed; frame 0 ran inline on the press tick so MoveTick is already 1 (frame-0 rule).
            var prev = NewState();
            var cur = Copy(prev);
            cur.F[Heavy].MoveSlot = TornadoSlot;
            cur.F[Heavy].MoveTick = 1;
            var ev = new List<ChibiSimEvent>();

            // Act
            ChibiSimEvents.Diff(prev, cur, ev);

            // Assert
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.ProgramFrame, Heavy));
            var e = First(ev, ChibiSimEventKind.ProgramFrame);
            Assert.AreEqual(TornadoSlot, e.Value);
            Assert.AreEqual(0, e.Value2);
            Assert.AreEqual(ChibiSimEventKind.MoveStarted, ev[0].Kind);   // MoveStarted first, then its frame 0
        }

        [Test]
        public void ProgramMoveAdvance_EmitsOneFramePerStepAndNoneWhenFrozen()
        {
            // Arrange
            var prev = NewState();
            prev.F[Heavy].MoveSlot = TornadoSlot;
            prev.F[Heavy].MoveTick = 12;
            var cur = Copy(prev);
            cur.F[Heavy].MoveTick = 13;   // frame 12 ran
            var ev = new List<ChibiSimEvent>();

            // Act
            ChibiSimEvents.Diff(prev, cur, ev);

            // Assert
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.ProgramFrame));
            Assert.AreEqual(12, First(ev, ChibiSimEventKind.ProgramFrame).Value2);

            // Act: hitstop, MoveTick does not advance
            ev.Clear();
            ChibiSimEvents.Diff(cur, Copy(cur), ev);

            // Assert
            Assert.AreEqual(0, Count(ev, ChibiSimEventKind.ProgramFrame));
        }

        [Test]
        public void ProgramMoveMultiStepDiff_EmitsEveryFrameInOrder()
        {
            // Arrange: a diff spanning three steps (rollback re-sim) must not skip a frame-keyed effect.
            var prev = NewState();
            prev.F[Heavy].MoveSlot = TornadoSlot;
            prev.F[Heavy].MoveTick = 11;
            var cur = Copy(prev);
            cur.F[Heavy].MoveTick = 14;
            var ev = new List<ChibiSimEvent>();

            // Act
            ChibiSimEvents.Diff(prev, cur, ev);

            // Assert
            var frames = ev.FindAll(x => x.Kind == ChibiSimEventKind.ProgramFrame);
            Assert.AreEqual(3, frames.Count);
            Assert.AreEqual(11, frames[0].Value2);
            Assert.AreEqual(12, frames[1].Value2);
            Assert.AreEqual(13, frames[2].Value2);
        }

        [Test]
        public void NormalMove_EmitsNoProgramFrame()
        {
            // Arrange: row moves (Light) are not programs.
            var prev = NewState();
            var cur = Copy(prev);
            cur.F[0].MoveSlot = 0;
            cur.F[0].MoveTick = 1;
            var ev = new List<ChibiSimEvent>();

            // Act
            ChibiSimEvents.Diff(prev, cur, ev);

            // Assert
            Assert.AreEqual(0, Count(ev, ChibiSimEventKind.ProgramFrame));
            Assert.AreEqual(0, Count(ev, ChibiSimEventKind.MovePhaseChanged));
        }

        [Test]
        public void GrabConnects_EmitsOneMovePhaseChanged()
        {
            // Arrange: grab press tick that connects (TryGrab sets MovePhase 1 on the press tick).
            var prev = NewState();
            var cur = Copy(prev);
            cur.F[Heavy].MoveSlot = GrabSlot;
            cur.F[Heavy].MoveTick = 1;
            cur.F[Heavy].MovePhase = 1;
            var ev = new List<ChibiSimEvent>();

            // Act
            ChibiSimEvents.Diff(prev, cur, ev);

            // Assert
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.MovePhaseChanged, Heavy));
            var e = First(ev, ChibiSimEventKind.MovePhaseChanged);
            Assert.AreEqual(1, e.Value);
            Assert.AreEqual(GrabSlot, e.Value2);
            Assert.IsTrue(First(ev, ChibiSimEventKind.ProgramFrame).On);

            // Act: next step, phase unchanged
            var next = Copy(cur);
            next.F[Heavy].MoveTick = 2;
            ev.Clear();
            ChibiSimEvents.Diff(cur, next, ev);

            // Assert
            Assert.AreEqual(0, Count(ev, ChibiSimEventKind.MovePhaseChanged));
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.ProgramFrame));
        }

        [Test]
        public void IsProgram_MatchesSimProgramTable()
        {
            // multiphase-conversion.md section 2: the seven program moves (class, slot).
            Assert.IsTrue(ChibiSimEvents.IsProgram((int)FighterClass.Melee, 4));
            Assert.IsTrue(ChibiSimEvents.IsProgram((int)FighterClass.Heavy, 2));
            Assert.IsTrue(ChibiSimEvents.IsProgram((int)FighterClass.Heavy, 3));
            Assert.IsTrue(ChibiSimEvents.IsProgram((int)FighterClass.Ranged, 2));
            Assert.IsTrue(ChibiSimEvents.IsProgram((int)FighterClass.Ranged, 4));
            Assert.IsTrue(ChibiSimEvents.IsProgram((int)FighterClass.Polearm, 2));
            Assert.IsTrue(ChibiSimEvents.IsProgram((int)FighterClass.Polearm, 3));
            Assert.IsFalse(ChibiSimEvents.IsProgram((int)FighterClass.Magic, 2));
            Assert.IsFalse(ChibiSimEvents.IsProgram((int)FighterClass.Melee, 0));
            // Since step 14 (ultimates-conversion.md) Heavy slot 5 (Smash) is a program move; Melee slot 5 (Sword Aura) is not.
            Assert.IsTrue(ChibiSimEvents.IsProgram((int)FighterClass.Heavy, 5));
            Assert.IsFalse(ChibiSimEvents.IsProgram((int)FighterClass.Melee, 5));
        }

        [Test]
        public void RealSimTornado_EmitsFrameZeroThenConsecutiveFrames()
        {
            // Arrange: drive the real sim so the event mirror cannot drift from ChibiSimulation's program layout.
            const int Settle = 120, Watch = 30;
            var s = NewState();
            var idle = new ChibiInputPacket();
            for (int t = 0; t < Settle; t++) ChibiSimulation.Step(s, idle, idle);   // land, round start
            var shown = Copy(s);
            var ev = new List<ChibiSimEvent>();
            var press = ChibiInputPacket.Make(ChibiDir.Neutral, ChibiButtons.Skill2);   // Heavy Skill2 = Tornado
            int expect = 0, frames = 0;

            // Act / Assert
            for (int t = 0; t < Watch; t++)
            {
                ChibiSimulation.Step(s, idle, t == 0 ? press : idle);
                ev.Clear();
                ChibiSimEvents.Diff(shown, s, ev);
                shown.CopyFrom(s);
                foreach (var e in ev)
                {
                    if (e.Kind != ChibiSimEventKind.ProgramFrame || e.Index != Heavy) continue;
                    Assert.AreEqual(TornadoSlot, e.Value);
                    Assert.AreEqual(expect, e.Value2);
                    expect++; frames++;
                }
            }
            Assert.Greater(frames, 0, "Tornado press produced no ProgramFrame events");
        }

        // ---- Step 13: Mage minions + Thunder Cloud (minion-cloud-conversion.md, TD amendments 5 and 6) ----

        const int MinionSlot = 3, CloudSlot = 2;
        const int OldSpawn = 10, NewSpawn = 20;   // SpawnTick values: the identity of a pool slot's occupant

        static void LiveMinion(ChibiSimState s, int slot, int owner, int spawnTick)
        {
            var m = s.Minions[slot];
            m.Active = true; m.Owner = owner; m.SpawnTick = spawnTick;
            m.Life = ChibiSimConst.MinionLife; m.Facing = 1;
        }

        static void LiveCloud(ChibiSimState s, int slot, int owner, int spawnTick)
        {
            var c = s.Clouds[slot];
            c.Active = true; c.Owner = owner; c.SpawnTick = spawnTick;
            c.Life = ChibiSimConst.CloudLife; c.StrikeTicks = ChibiSimConst.CloudFirstStrike; c.BoltTicks = 0;
        }

        [Test]
        public void Minion_SpawnMoveDespawn_EmitsOneSpawnedAndOneGoneWithOwner()
        {
            var a = NewState();
            var b = Copy(a);
            LiveMinion(b, MinionSlot, 1, OldSpawn);
            var c = Copy(b);
            c.Minions[MinionSlot].X += 100; c.Minions[MinionSlot].Life--;   // same minion, moved
            var d = Copy(c);
            d.Minions[MinionSlot].Active = false;

            var ev = new List<ChibiSimEvent>();
            ChibiSimEvents.Diff(a, b, ev);
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.MinionSpawned, MinionSlot));
            Assert.AreEqual(1, First(ev, ChibiSimEventKind.MinionSpawned).Value, "Value = owner");
            ev.Clear();
            ChibiSimEvents.Diff(b, c, ev);
            Assert.AreEqual(0, Count(ev, ChibiSimEventKind.MinionSpawned) + Count(ev, ChibiSimEventKind.MinionGone));
            ev.Clear();
            ChibiSimEvents.Diff(c, d, ev);
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.MinionGone, MinionSlot));
            Assert.AreEqual(1, First(ev, ChibiSimEventKind.MinionGone).Value, "Value = owner");
            Assert.AreEqual(0, Count(ev, ChibiSimEventKind.MinionSpawned));
        }

        [Test]
        public void Minion_SlotReusedInOneStep_EmitsGoneThenSpawned()
        {
            var prev = NewState();
            LiveMinion(prev, MinionSlot, 0, OldSpawn);
            var cur = Copy(prev);
            LiveMinion(cur, MinionSlot, 1, NewSpawn);   // Active in both states: only SpawnTick/Owner tell them apart

            var ev = new List<ChibiSimEvent>();
            ChibiSimEvents.Diff(prev, cur, ev);
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.MinionGone, MinionSlot));
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.MinionSpawned, MinionSlot));
            Assert.AreEqual(0, First(ev, ChibiSimEventKind.MinionGone).Value, "Gone carries the old owner");
            Assert.AreEqual(1, First(ev, ChibiSimEventKind.MinionSpawned).Value, "Spawned carries the new owner");
            Assert.Less(ev.FindIndex(e => e.Kind == ChibiSimEventKind.MinionGone),
                        ev.FindIndex(e => e.Kind == ChibiSimEventKind.MinionSpawned), "Gone before Spawned");
        }

        [Test]
        public void Minion_SameSpawnTickDifferentLife_IsSameMinion()
        {
            var prev = NewState();
            LiveMinion(prev, MinionSlot, 0, OldSpawn);
            var cur = Copy(prev);
            cur.Minions[MinionSlot].Life = 1; cur.Minions[MinionSlot].Facing = -1;

            var ev = new List<ChibiSimEvent>();
            ChibiSimEvents.Diff(prev, cur, ev);
            Assert.AreEqual(0, Count(ev, ChibiSimEventKind.MinionGone) + Count(ev, ChibiSimEventKind.MinionSpawned));
            Assert.IsTrue(ChibiSimEvents.SameMinion(prev.Minions[MinionSlot], cur.Minions[MinionSlot]));
        }

        [Test]
        public void Cloud_SpawnDespawnAndReuse_EmitGoneThenSpawnedWithOwners()
        {
            var a = NewState();
            var b = Copy(a);
            LiveCloud(b, CloudSlot, 0, OldSpawn);
            var c = Copy(b);
            LiveCloud(c, CloudSlot, 1, NewSpawn);   // reused within one step
            var d = Copy(c);
            d.Clouds[CloudSlot].Active = false;

            var ev = new List<ChibiSimEvent>();
            ChibiSimEvents.Diff(a, b, ev);
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.CloudSpawned, CloudSlot));
            Assert.AreEqual(0, First(ev, ChibiSimEventKind.CloudSpawned).Value);
            ev.Clear();
            ChibiSimEvents.Diff(b, c, ev);
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.CloudGone, CloudSlot));
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.CloudSpawned, CloudSlot));
            Assert.AreEqual(0, First(ev, ChibiSimEventKind.CloudGone).Value, "Gone carries the old owner");
            Assert.AreEqual(1, First(ev, ChibiSimEventKind.CloudSpawned).Value, "Spawned carries the new owner");
            Assert.Less(ev.FindIndex(e => e.Kind == ChibiSimEventKind.CloudGone),
                        ev.FindIndex(e => e.Kind == ChibiSimEventKind.CloudSpawned), "Gone before Spawned");
            ev.Clear();
            ChibiSimEvents.Diff(c, d, ev);
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.CloudGone, CloudSlot));
            Assert.AreEqual(1, First(ev, ChibiSimEventKind.CloudGone).Value);
        }

        [Test]
        public void Cloud_StrikeFiresOnceWhenBoltTicksBecomesCloudBolt()
        {
            var a = NewState();
            LiveCloud(a, CloudSlot, 1, OldSpawn);
            a.Clouds[CloudSlot].StrikeTicks = 1;
            var b = Copy(a);
            b.Clouds[CloudSlot].StrikeTicks = ChibiSimConst.CloudPeriod;
            b.Clouds[CloudSlot].BoltTicks = ChibiSimConst.CloudBolt;     // strike step
            var c = Copy(b);
            c.Clouds[CloudSlot].BoltTicks = ChibiSimConst.CloudBolt - 1; // countdown: bolt visible, no new strike
            var d = Copy(c);
            d.Clouds[CloudSlot].BoltTicks = 0;

            var ev = new List<ChibiSimEvent>();
            ChibiSimEvents.Diff(a, b, ev);
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.CloudStrike, CloudSlot));
            Assert.AreEqual(1, First(ev, ChibiSimEventKind.CloudStrike).Value, "Value = owner");
            ev.Clear();
            ChibiSimEvents.Diff(b, c, ev);
            Assert.AreEqual(0, Count(ev, ChibiSimEventKind.CloudStrike));
            ev.Clear();
            ChibiSimEvents.Diff(c, d, ev);
            Assert.AreEqual(0, Count(ev, ChibiSimEventKind.CloudStrike));
            ev.Clear();
            ChibiSimEvents.Diff(b, b, ev);   // frozen tick: identical states never re-emit the strike
            Assert.AreEqual(0, Count(ev, ChibiSimEventKind.CloudStrike));
        }

        [Test]
        public void Cloud_ReusedSlotAlreadyStriking_EmitsStrikeForTheNewCloud()
        {
            var prev = NewState();
            LiveCloud(prev, CloudSlot, 0, OldSpawn);
            prev.Clouds[CloudSlot].BoltTicks = ChibiSimConst.CloudBolt;
            var cur = Copy(prev);
            LiveCloud(cur, CloudSlot, 1, NewSpawn);
            cur.Clouds[CloudSlot].BoltTicks = ChibiSimConst.CloudBolt;   // equal BoltTicks, but a different cloud

            var ev = new List<ChibiSimEvent>();
            ChibiSimEvents.Diff(prev, cur, ev);
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.CloudStrike, CloudSlot));
            Assert.AreEqual(1, First(ev, ChibiSimEventKind.CloudStrike).Value);
        }

        // ---- Step 14: Ultimates (ultimates-conversion.md). Slot 5 is a program for Heavy (PSmash), Ranged (PArrows)
        // and Polearm (PKick); Sword Aura / Fire Ring start on the press tick with MoveSlot -1. ----

        const int UltSlot = 5, Ulter = 0;   // UltState(): F[0] is the ult user
        const int UltSettle = 120, UltWatch = 240, KickNear = 2000;

        static ChibiSimState UltState(FighterClass cls, bool nearOpponent)
        {
            var s = new ChibiSimState();
            ChibiSimulation.Init(s, new ChibiMatchConfig
            {
                Seed = Seed, Class0 = cls, Class1 = FighterClass.Heavy, Passive0 = 0, Passive1 = 0
            });
            var idle = new ChibiInputPacket();
            for (int t = 0; t < UltSettle; t++) ChibiSimulation.Step(s, idle, idle);   // land, round start
            if (nearOpponent) { s.F[1].X = s.F[Ulter].X + KickNear; s.F[1].Y = s.F[Ulter].Y; }
            s.F[Ulter].Ult = ChibiSimConst.UltMax;
            return s;
        }

        /// <summary>Presses Ultimate on tick 0, diffs every tick and returns all events for UltWatch ticks.</summary>
        static List<ChibiSimEvent> RunUlt(ChibiSimState s)
        {
            var all = new List<ChibiSimEvent>();
            var ev = new List<ChibiSimEvent>();
            var shown = Copy(s);
            var idle = new ChibiInputPacket();
            var press = ChibiInputPacket.Make(ChibiDir.Neutral, ChibiButtons.Ultimate);
            for (int t = 0; t < UltWatch; t++)
            {
                ChibiSimulation.Step(s, t == 0 ? press : idle, idle);
                ev.Clear();
                ChibiSimEvents.Diff(shown, s, ev);
                shown.CopyFrom(s);
                all.AddRange(ev);
            }
            return all;
        }

        [Test]
        public void IsProgram_UltSlotIsProgramForSmashArrowsKickOnly()
        {
            Assert.IsTrue(ChibiSimEvents.IsProgram((int)FighterClass.Heavy, UltSlot));
            Assert.IsTrue(ChibiSimEvents.IsProgram((int)FighterClass.Ranged, UltSlot));
            Assert.IsTrue(ChibiSimEvents.IsProgram((int)FighterClass.Polearm, UltSlot));
            Assert.IsFalse(ChibiSimEvents.IsProgram((int)FighterClass.Melee, UltSlot));
            Assert.IsFalse(ChibiSimEvents.IsProgram((int)FighterClass.Magic, UltSlot));
        }

        [Test]
        public void RealSimPolearmKick_InRange_EmitsConsecutiveFramesThroughRelease()
        {
            // Arrange
            var s = UltState(FighterClass.Polearm, nearOpponent: true);

            // Act
            var ev = RunUlt(s);

            // Assert: frames 0..9 cover the 9 lerp frames and the frame-9 hold, each exactly once and in order.
            int expect = 0;
            foreach (var e in ev)
            {
                if (e.Kind != ChibiSimEventKind.ProgramFrame || e.Index != Ulter) continue;
                Assert.AreEqual(UltSlot, e.Value);
                Assert.AreEqual(expect, e.Value2);
                expect++;
            }
            Assert.GreaterOrEqual(expect, ChibiSimConst.KickReleaseFrame + 1, "Kick ProgramFrames stopped before the release frame");
        }

        [Test]
        public void RealSimPolearmKick_InRange_ReportsHoldingThenReleasedPhases()
        {
            var s = UltState(FighterClass.Polearm, nearOpponent: true);
            var ev = RunUlt(s);
            var phases = new List<int>();
            foreach (var e in ev)
                if (e.Kind == ChibiSimEventKind.MovePhaseChanged && e.Index == Ulter && e.Value2 == UltSlot) phases.Add(e.Value);
            // Lerp (2) on the press tick, Holding (3) at frame 9, Released (4) at frame 50.
            CollectionAssert.AreEqual(new[] { 2, 3, 4 }, phases);
        }

        [Test]
        public void RealSimSmash_EmitsSlamPhaseChangeOnce()
        {
            var s = UltState(FighterClass.Heavy, nearOpponent: false);
            var ev = RunUlt(s);
            int slams = 0;
            foreach (var e in ev)
                if (e.Kind == ChibiSimEventKind.MovePhaseChanged && e.Index == Ulter && e.Value2 == UltSlot && e.Value == 1) slams++;
            Assert.AreEqual(1, slams);
        }

        [Test]
        public void RealSimSwordAura_EmitsCinematicButNoProgramFrame()
        {
            var s = UltState(FighterClass.Melee, nearOpponent: false);
            var ev = RunUlt(s);
            Assert.Greater(s.F[Ulter].AuraTicks, 0, "Sword Aura did not fire");
            Assert.AreEqual(0, Count(ev, ChibiSimEventKind.ProgramFrame, Ulter));
            Assert.AreEqual(1, Count(ev, ChibiSimEventKind.CinematicStarted, Ulter));
        }
}
}
