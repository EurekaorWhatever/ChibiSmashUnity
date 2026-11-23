using System;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Final security review (gate-stall framing): a cheater that stalls ONE honest peer (lying about holds or withholding
    /// inputs to it only) must never get that honest peer voted out by the bystanders; a false "waiting on" claim ends as a
    /// no contest with nobody blamed; and an honest high-latency mesh is never ejected. Full multi-node meshes over
    /// in-memory links; the cheater is a normal session whose outgoing datagrams to one peer are rewritten.
    /// </summary>
    public class ChibiMeshFramingTests
    {
        const uint Seed = 0xF4A3u;

        static ChibiMatchConfig Cfg(int count) => new ChibiMatchConfig
        {
            Seed = Seed, Format = ChibiMatchConfig.FormatFfa, Count = count,
            Class0 = FighterClass.Melee, Class1 = FighterClass.Heavy, Class2 = FighterClass.Ranged, Class3 = FighterClass.Magic,
            Stage = 0, HazardsOff = true,
        };

        static ChibiInputPacket Bot(int seed, int n)
        {
            unchecked
            {
                uint h = (uint)(n / 5) * 2654435761u + (uint)seed * 40503u;
                h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
                int x = (int)(h % 3) - 1;
                ChibiButtons b = ChibiButtons.None;
                if (h % 7 == 0) b |= ChibiButtons.Light;
                if (h % 11 == 1) b |= ChibiButtons.Jump;
                return ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(x, 0), b);
            }
        }

        /// <summary>The cheater's outgoing pipe to one peer: rewrites (or drops) its gameplay datagrams once armed.</summary>
        sealed class Interposer : IChibiTransport
        {
            public enum Mode { Pass, FreezeHold, Mute, ClaimWaiting, FreezeAndClaim, RandomWait }
            ChibiNetMessage _frozen;
            ChibiRng _rng = new ChibiRng(4242);
            public int Count = 4, Sender;
            readonly IChibiTransport _inner;
            readonly BitBuffer _buf = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
            readonly ChibiNetMessage _m = new ChibiNetMessage();
            public Mode Active = Mode.Pass;
            public int Slot, Value;

            public Interposer(IChibiTransport inner) { _inner = inner; }

            public void Send(byte[] data, int length)
            {
                if (Active == Mode.Pass || data[0] != ChibiPacketCodec.Magic) { _inner.Send(data, length); return; }
                if (Active == Mode.Mute) return;
                _buf.LoadFrom(data, length);
                if (!ChibiPacketCodec.TryRead(_buf, _m)) return;
                if (Active == Mode.FreezeHold) _m.Hold[Slot] = Value;           // "I still hold nothing of slot B"
                if (Active == Mode.ClaimWaiting) _m.WaitMask = Value;           // "I am waiting on someone" (always)
                if (Active == Mode.FreezeAndClaim)
                {
                    // Stop new inputs at the first datagram after arming, keep sending it, claim to wait on Value.
                    if (_frozen == null)
                    {
                        _frozen = new ChibiNetMessage();
                        _buf.Clear(); ChibiPacketCodec.Write(_buf, _m);
                        var copy = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
                        copy.LoadFrom(_buf.Bytes, _buf.ByteLength);
                        ChibiPacketCodec.TryRead(copy, _frozen);
                    }
                    _frozen.WaitMask = Value;
                    _buf.Clear();
                    ChibiPacketCodec.Write(_buf, _frozen);
                    _inner.Send(_buf.Bytes, _buf.ByteLength);
                    return;
                }
                if (Active == Mode.RandomWait) _m.WaitMask = _rng.Range(1 << Count) & ~(1 << Sender);
                _buf.Clear();
                ChibiPacketCodec.Write(_buf, _m);
                _inner.Send(_buf.Bytes, _buf.ByteLength);
            }

            public int Receive(byte[] buffer) => _inner.Receive(buffer);
        }

        sealed class Mesh
        {
            public ChibiRollbackSession[] P;
            public ChibiLoopbackLink[,] L;
            public Interposer[,] Out;   // [from, to]
            public int N;

            public void Tick()
            {
                foreach (var p in P) p.Tick();
                foreach (var l in L) l?.Advance();
            }

            public void Run(int ticks) { for (int t = 0; t < ticks; t++) Tick(); }
        }

        static Mesh NewMesh(int count, int latency, int jitter, int loss)
        {
            var m = new Mesh { N = count, P = new ChibiRollbackSession[count], L = new ChibiLoopbackLink[count, count], Out = new Interposer[count, count] };
            for (int i = 0; i < count; i++) for (int j = i + 1; j < count; j++) m.L[i, j] = new ChibiLoopbackLink(latency, jitter, loss);
            for (int i = 0; i < count; i++)
            {
                var links = new IChibiTransport[count];
                for (int j = 0; j < count; j++)
                {
                    if (j == i) continue;
                    var raw = i < j ? m.L[i, j].A : m.L[j, i].B;
                    links[j] = m.Out[i, j] = new Interposer(raw);
                }
                int seed = 3 * i + 1;
                m.P[i] = new ChibiRollbackSession(i, Cfg(count), links, new ChibiScriptedInputSource(n => Bot(seed, n)), ChibiRollbackSettings.Default);
            }
            return m;
        }

        const int A = 0, B = 1, C = 2;

        static void AssertANeverDropped(Mesh m)
        {
            for (int i = 0; i < m.N; i++)
                Assert.AreEqual(0, m.P[i].DroppedMask & (1 << A), "peer " + i + " dropped the honest peer A");
            Assert.IsFalse(m.P[B].IsPeerLost(A), "bystander B voted the honest peer A out");
        }

        static bool CDroppedOrNoContest(Mesh m) =>
            ((m.P[A].DroppedMask & (1 << C)) != 0 && (m.P[B].DroppedMask & (1 << C)) != 0) || m.P[A].Disconnected || m.P[B].Disconnected;

        [Test]
        public void CheaterFreezesItsHoldOfBToAOnly_AIsNeverDropped()
        {
            // Arrange
            var m = NewMesh(3, latency: 3, jitter: 1, loss: 0);
            m.Run(300);

            // Act: C tells A only that it still holds nothing of B, so A stalls on the confirmation gate.
            var lie = m.Out[C, A];
            lie.Slot = B;
            lie.Value = m.P[C].Settings.InputDelay - 1;
            lie.Active = Interposer.Mode.FreezeHold;
            m.Run(3000);

            // Assert
            AssertANeverDropped(m);
            Assert.IsTrue(CDroppedOrNoContest(m), "the freeze was neither punished nor ended");
        }

        [Test]
        public void CheaterWithholdsInputsFromAOnly_AIsNeverDropped()
        {
            // Arrange
            var m = NewMesh(3, latency: 3, jitter: 1, loss: 0);
            m.Run(300);

            // Act
            m.Out[C, A].Active = Interposer.Mode.Mute;
            m.Run(3000);

            // Assert
            AssertANeverDropped(m);
            Assert.IsTrue(CDroppedOrNoContest(m), "the withholding was neither punished nor ended");
        }

        [Test]
        public void FalseWaitingClaim_ExpiresAsNoContest_NobodyBlamedNorEjected()
        {
            // Arrange
            var m = NewMesh(3, latency: 2, jitter: 0, loss: 0);
            m.Run(300);

            // Act: C keeps playing normally but tells B on every datagram that it is waiting on A.
            m.Out[C, B].Value = 1 << A;
            m.Out[C, B].Active = Interposer.Mode.ClaimWaiting;
            m.Run(ChibiRollbackSession.MaxDisconnectTicks + 200);

            // Assert
            for (int i = 0; i < 3; i++) Assert.AreEqual(0, m.P[i].DroppedMask, "peer " + i + " ejected someone");
            Assert.IsTrue(m.P[B].Disconnected, "the never-ending claim held the pause forever");
            Assert.AreEqual(ChibiDisconnectReason.VoteFailed, m.P[B].DisconnectReason);
            Assert.AreEqual(-1, m.P[B].CulpritSlot);
        }

        [Test]
        public void CollusionFreezeClaimingToWaitOnEachOther_EndsAsNoContest_NoHang_NoHonestDropped()
        {
            // Arrange: 4 peers; C (2) and D (3) are a colluding team.
            var m = NewMesh(4, latency: 2, jitter: 0, loss: 0);
            m.Run(300);

            // Act: both stop new inputs and tell everyone they are waiting on each other.
            for (int to = 0; to < 4; to++)
            {
                if (to != 2) { m.Out[2, to].Value = 1 << 3; m.Out[2, to].Active = Interposer.Mode.FreezeAndClaim; }
                if (to != 3) { m.Out[3, to].Value = 1 << 2; m.Out[3, to].Active = Interposer.Mode.FreezeAndClaim; }
            }
            int limit = ChibiRollbackSettings.Default.DisconnectTicks + ChibiRollbackSession.MaxDisconnectTicks + 200;
            m.Run(limit);

            // Assert: the honest players are out of the hang with a no contest nobody is blamed for, and nobody dropped them.
            for (int i = 0; i < 2; i++)
            {
                Assert.IsTrue(m.P[i].Disconnected, "honest peer " + i + " is still stuck");
                Assert.AreEqual(ChibiDisconnectReason.VoteFailed, m.P[i].DisconnectReason);
                Assert.AreEqual(-1, m.P[i].CulpritSlot);
            }
            for (int i = 0; i < 4; i++) Assert.AreEqual(0, m.P[i].DroppedMask & 0b0011, "peer " + i + " dropped an honest player");
        }

        [TestCase(3)]
        [TestCase(4)]
        public void Fuzz_RandomWaitMasksFromOnePeer_NeverDropOrDesync(int count)
        {
            var m = NewMesh(count, latency: 2, jitter: 1, loss: 5);
            for (int to = 0; to < count; to++)
                if (to != 1) { m.Out[1, to].Count = count; m.Out[1, to].Sender = 1; m.Out[1, to].Active = Interposer.Mode.RandomWait; }

            Assert.DoesNotThrow(() => m.Run(1500));

            for (int i = 0; i < count; i++)
            {
                Assert.AreEqual(0, m.P[i].DroppedMask, "peer " + i + " dropped someone over a wait claim");
                Assert.IsFalse(m.P[i].Desynced);
            }
        }

        [Test]
        public void HonestMesh_500msOneWay_10PercentLoss_NoEjectionNoNoContest()
        {
            // Arrange: four honest peers, 30 ticks (500 ms) one way, 10 % loss on every pair.
            var m = NewMesh(4, latency: 30, jitter: 0, loss: 10);

            // Act
            m.Run(3000);

            // Assert
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(0, m.P[i].DroppedMask, "peer " + i + " ejected someone");
                Assert.IsFalse(m.P[i].Disconnected, "peer " + i + " ended: " + m.P[i].DisconnectReason);
                Assert.IsFalse(m.P[i].Desynced);
                Assert.Greater(m.P[i].Frame, 200, "the mesh did not progress");
            }
        }
    }
}
