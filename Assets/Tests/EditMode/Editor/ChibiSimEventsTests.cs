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
    }
}
