// Story type: Logic (CPU opponent uses the rules v18 movesets) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Source spec: design/gdd/movesets/README.md section 1 (input -> move), per-character files design/gdd/movesets/*.md,
// docs/architecture/netcode/cpu-ai-conversion.md (CPU as a deterministic input source).
using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>ChibiCpuInputSource moveset layer: determinism per character, move variety, dummy, recovery, press edges.</summary>
    public class ChibiCpuMovesetTests
    {
        /// <summary>Distinct new-move slots each character's CPU must start in a 2-minute AI-vs-AI training match
        /// (observed 13-19 at the time of writing; the floor leaves room for tuning without letting the layer go dead).</summary>
        const int MinDistinctNewMoves = 8;
        const int SampleTicks = 2 * 60 * ChibiSimConst.TickRate;

        static readonly FighterClass[] Classes =
            { FighterClass.Melee, FighterClass.Heavy, FighterClass.Ranged, FighterClass.Magic, FighterClass.Polearm };

        static IEnumerable<FighterClass> AllClasses() => Classes;

        static FighterClass Opponent(FighterClass c) => Classes[((int)c + 1) % Classes.Length];

        static ChibiMatchConfig Cfg(FighterClass c0, FighterClass c1, uint seed, bool training = false, int stage = 0) =>
            new ChibiMatchConfig { Seed = seed, Class0 = c0, Class1 = c1, Stage = stage, Training = training };

        /// <summary>CPU vs CPU session recording both input streams.</summary>
        sealed class Duel
        {
            public readonly ChibiLocalSession Session;
            public readonly List<ChibiInputPacket> P0 = new List<ChibiInputPacket>(), P1 = new List<ChibiInputPacket>();

            public Duel(ChibiMatchConfig cfg, int diff0, int diff1)
            {
                Session = new ChibiLocalSession(cfg, new ChibiCpuInputSource(0, diff0, cfg.Seed), new ChibiCpuInputSource(1, diff1, cfg.Seed));
                Session.OnFrame += (f, a, b) => { P0.Add(a); P1.Add(b); };
            }

            public ChibiSimState S => Session.State;
        }

        static bool JustRingedOut(ChibiFighterSim f, ChibiStageDef st) =>
            f.X == st.RespawnX && f.Y == st.RespawnY && f.Invuln == ChibiSimConst.RespawnInvuln;

        static string SlotNames(FighterClass c, IEnumerable<int> slots)
        {
            var names = new List<string>();
            foreach (int s in slots) names.Add(ChibiMoveInfo.Get((int)c, s).Id);
            return string.Join(", ", names);
        }

        // ================================================================== determinism

        [TestCaseSource(nameof(AllClasses))]
        public void SameSeed_SameInputStream_3000Ticks(FighterClass c)
        {
            var cfg = Cfg(c, Opponent(c), 4100u + (uint)c, training: true);   // KOs revive: a fixed 3000-tick sample
            var a = new Duel(cfg, 3, 3);
            var b = new Duel(cfg, 3, 3);
            for (int t = 0; t < 3000; t++) { a.Session.Tick(); b.Session.Tick(); }

            Assert.AreEqual(3000, a.P0.Count, "precondition: 3000 ticks sampled");
            CollectionAssert.AreEqual(a.P0, b.P0, $"{c}: same seed, same CPU input stream");
            CollectionAssert.AreEqual(a.P1, b.P1, $"{Opponent(c)}: same seed, same CPU input stream");
            Assert.AreEqual(a.S.ComputeChecksum(), b.S.ComputeChecksum(), "same inputs, same final state");

            int moveset = 0;
            foreach (var p in a.P0) if (p.Has(ChibiButtons.Special) || p.MoveY != 0) moveset++;
            Assert.Greater(moveset, 0, "precondition: the stream uses the moveset inputs (Special / up / down)");
        }

        [Test]
        public void CpuStream_DependsOnSeed_NotOnSimRng()
        {
            var a = new Duel(Cfg(FighterClass.Magic, FighterClass.Ranged, 1), 3, 3);
            var b = new Duel(Cfg(FighterClass.Magic, FighterClass.Ranged, 2), 3, 3);
            for (int t = 0; t < 1500; t++) { a.Session.Tick(); b.Session.Tick(); }
            CollectionAssert.AreNotEqual(a.P0, b.P0);
        }

        // ================================================================== move variety

        [TestCaseSource(nameof(AllClasses))]
        public void AiVsAi_StartsManyDistinctNewMoves(FighterClass c)
        {
            // Training config: a KO revives instead of ending the match, so every character gets the same sample length.
            var cfg = Cfg(c, Opponent(c), 5200u + (uint)c, training: true, stage: (int)c % 3);
            var d = new Duel(cfg, 3, 3);
            var used = new SortedSet<int>();
            for (int t = 0; t < SampleTicks && d.Session.Tick(); t++)
            {
                var f = d.S.F[0];
                if (f.MoveSlot >= ChibiMovesetData.FirstNewSlot) used.Add(f.MoveSlot);
            }
            Assert.GreaterOrEqual(used.Count, MinDistinctNewMoves,
                $"{c} started {used.Count}/{ChibiMoveInfo.Count((int)c)} moveset rows: {SlotNames(c, used)}");
        }

        [TestCaseSource(nameof(AllClasses))]
        public void AiVsAi_UsesEveryMoveFamily(FighterClass c)
        {
            // Union over the five pairings: tilts, smashes, aerials and specials all appear (README 1.2 rules 3, 5, 6, 8).
            var used = new HashSet<int>();
            foreach (var o in Classes)
            {
                var d = new Duel(Cfg(c, o, 6300u + (uint)c * 10 + (uint)o, training: true, stage: (int)o % 3), 3, 3);
                for (int t = 0; t < SampleTicks && d.Session.Tick(); t++)
                    if (d.S.F[0].MoveSlot >= ChibiMovesetData.FirstNewSlot) used.Add(d.S.F[0].MoveSlot);
            }
            int cls = (int)c;
            bool Any(int button, int[] dirs, bool air)
            {
                foreach (int dir in dirs)
                {
                    int sl = ChibiMovesetData.SlotFor(cls, button, dir, air);
                    if (sl >= 0 && used.Contains(sl)) return true;
                }
                return false;
            }
            int[] tiltDirs = { 1, 3, 4 }, aerialDirs = { 0, 1, 2, 3, 4 }, specialDirs = { 0, 1, 3, 4 };
            Assert.IsTrue(Any(0, tiltDirs, false), $"{c}: a tilt (L + F/U/D on the ground)");
            Assert.IsTrue(Any(1, tiltDirs, false), $"{c}: a smash (H + F/U/D on the ground)");
            Assert.IsTrue(Any(0, aerialDirs, true), $"{c}: an aerial (L in the air)");
            Assert.IsTrue(Any(2, specialDirs, false) || Any(2, specialDirs, true), $"{c}: a special (B)");
        }

        // ================================================================== difficulty 0

        [TestCaseSource(nameof(AllClasses))]
        public void Difficulty0_EmitsOnlyNeutral_WhileBeatenUp(FighterClass c)
        {
            // P0 is a hard CPU that attacks (and knocks off) the dummy: the dummy still never presses or moves.
            var d = new Duel(Cfg(FighterClass.Heavy, c, 900u + (uint)c, training: true), 3, 0);
            d.S.F[1].Ult = ChibiSimConst.UltMax;
            for (int t = 0; t < 2400 && d.Session.Tick(); t++) { }
            foreach (var p in d.P1) Assert.AreEqual(0, p.Raw, "difficulty 0 is the still training dummy");
            Assert.Greater(d.S.F[0].HitsLanded, 0, "precondition: the dummy was attacked");
        }

        // ================================================================== recovery

        static IEnumerable<TestCaseData> RecoveryStarts()
        {
            foreach (var c in Classes)
            {
                yield return new TestCaseData(c, 1500, -1000).SetName($"OffStage_JumpSpent_UsesUpSpecialAndGetsBack_{c}_Below");
                yield return new TestCaseData(c, 2500, 1000).SetName($"OffStage_JumpSpent_UsesUpSpecialAndGetsBack_{c}_Above");
            }
        }

        [TestCaseSource(nameof(RecoveryStarts))]
        public void OffStage_JumpSpent_UsesUpSpecialAndGetsBack(FighterClass c, int pastEdge, int y)
        {
            var cfg = Cfg(FighterClass.Heavy, c, 31u + (uint)c);
            var cpu = new ChibiCpuInputSource(1, 2, cfg.Seed);
            var s = new ChibiLocalSession(cfg, new ChibiScriptedInputSource(_ => default), cpu);
            for (int t = 0; t < 90; t++) s.Tick();
            var st = ChibiStageTable.Get(cfg.Stage);
            var p0 = s.State.F[0];
            p0.X = -st.HalfWidth + 1000; p0.Y = 0; p0.Vx = 0; p0.Vy = 0; p0.Grounded = true;
            // Knocked past the edge (below or above the stage top), air jump spent, facing the stage (knockback leaves the
            // victim facing its attacker; air facing is locked, OD-1). Every class's up special can make it back from these
            // starts (checked per class against its row: Skyhook / Rocket Hammer / Staff Vault rise with self_vy, Grapple
            // Arrow tethers at 60 deg, Blink teleports). Deeper starts hugging the side wall are not recoverable for every
            // class: from (1000, -1500) Ren's grapple pull always starts below the stage corner and drags into the underside.
            var f = s.State.F[1];
            s.State.HitstopRealTicks = 0; s.State.CinematicRealTicks = 0;
            f.X = st.HalfWidth + pastEdge; f.Y = y; f.Vx = 0; f.Vy = -30000; f.XRem = 0; f.YRem = 0;
            f.Grounded = false; f.AirJumps = 0; f.Facing = -1; f.Stun = 0; f.MoveSlot = -1; f.MoveLockEnd = 0;
            f.DashTicks = 0; f.DashCd = ChibiSimConst.DashCooldown; f.AirUsedMask = 0; f.SpearOut = false;
            for (int k = 0; k < f.TechCd.Length; k++) f.TechCd[k] = 0;

            int up = ChibiMovesetData.SlotFor((int)c, 2, 3, true);
            Assert.GreaterOrEqual(up, 0, "precondition: every character has an air up special");
            bool usedUp = false, ringOut = false, landed = false;
            for (int t = 0; t < 300 && !landed && !ringOut; t++)
            {
                s.Tick();
                if (f.MoveSlot == up) usedUp = true;
                if (JustRingedOut(f, st)) ringOut = true;
                if (f.Grounded && System.Math.Abs(f.X) <= st.HalfWidth) landed = true;
            }
            if (y < 0) Assert.IsTrue(usedUp, $"{c}: below the stage top with the air jump spent, the CPU uses its up special");
            Assert.IsFalse(ringOut, $"{c}: and does not fall out");
            Assert.IsTrue(landed, $"{c}: and lands back on the stage");
        }

        // ================================================================== press edges

        [TestCaseSource(nameof(AllClasses))]
        public void Presses_ReleaseBetweenPresses_HoldsOnlyChargeButtons(FighterClass c)
        {
            var d = new Duel(Cfg(c, Opponent(c), 77u + (uint)c, training: true), 3, 3);
            for (int t = 0; t < 4000 && d.Session.Tick(); t++) { }
            const ushort tapOnly = (ushort)(ChibiButtons.Light | ChibiButtons.Jump | ChibiButtons.Skill1 | ChibiButtons.Skill2 |
                                            ChibiButtons.Ultimate | ChibiButtons.Dash);
            ChibiButtons[] holdable = { ChibiButtons.Heavy, ChibiButtons.Special, ChibiButtons.Skill3 };
            foreach (var stream in new[] { d.P0, d.P1 })
            {
                for (int i = 1; i < stream.Count; i++)
                    Assert.AreEqual(0, stream[i - 1].Raw & stream[i].Raw & tapOnly, $"tick {i}: a tap button held two ticks would merge two presses");
                foreach (var b in holdable)
                {
                    int run = 0;
                    foreach (var p in stream)
                    {
                        run = p.Has(b) ? run + 1 : 0;
                        Assert.LessOrEqual(run, ChibiCpuInputSource.MaxHoldTicks + 1, $"{b}: a deliberate charge hold is bounded");
                    }
                }
            }
        }
    }
}
