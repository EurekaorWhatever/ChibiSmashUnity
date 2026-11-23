using System;
using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Review N1 (protocol 6): per-pair authenticated mesh links. HMAC-SHA256 correctness, tag checks (flipped bit, wrong
    /// direction, wrong key), impersonators refused on Hello and on inputs, and no allocation per datagram.
    /// </summary>
    public class ChibiLinkAuthTests
    {
        // ================================================================== fixtures

        /// <summary>Allocation-free in-memory pipe end (fixed ring of fixed buffers).</summary>
        sealed class RingEnd : IChibiTransport
        {
            const int Slots = 32;
            readonly byte[][] _buf = new byte[Slots][];
            readonly int[] _len = new int[Slots];
            int _head, _count;
            public RingEnd Peer;
            public RingEnd() { for (int i = 0; i < Slots; i++) _buf[i] = new byte[512]; }
            public void Send(byte[] data, int length) => Peer.Push(data, length);
            public void Push(byte[] data, int length)
            {
                if (_count == Slots) return;
                int i = (_head + _count++) % Slots;
                Buffer.BlockCopy(data, 0, _buf[i], 0, length);
                _len[i] = length;
            }
            public int Receive(byte[] buffer)
            {
                if (_count == 0) return 0;
                int n = _len[_head];
                Buffer.BlockCopy(_buf[_head], 0, buffer, 0, n);
                _head = (_head + 1) % Slots; _count--;
                return n;
            }
            /// <summary>Flip one bit of the oldest queued datagram (a tampering relay).</summary>
            public void Tamper(int byteIndex) => _buf[_head][byteIndex] ^= 0x10;
            public int Queued => _count;
        }

        static (RingEnd a, RingEnd b) Pipe()
        {
            var a = new RingEnd(); var b = new RingEnd();
            a.Peer = b; b.Peer = a;
            return (a, b);
        }

        static byte[] Key(int i, int j, byte salt = 0)
        {
            var k = new byte[32];
            for (int n = 0; n < 32; n++) k[n] = (byte)(Math.Min(i, j) * 31 + Math.Max(i, j) * 7 + n + salt);
            return k;
        }

        static byte[][] KeysFor(int me, int count, int forgedSlot = -1)
        {
            var keys = new byte[count][];
            for (int j = 0; j < count; j++) if (j != me) keys[j] = Key(me, j, me == forgedSlot ? (byte)99 : (byte)0);
            return keys;
        }

        static string Hex(byte[] b, int n)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }

        // ================================================================== HMAC

        [Test]
        public void Hmac_Rfc4231TestCase2()
        {
            // key "Jefe", data "what do ya want for nothing?" (the prefix byte is the leading 'w').
            var mac = new ChibiHmacSha256(System.Text.Encoding.ASCII.GetBytes("Jefe"));
            var rest = System.Text.Encoding.ASCII.GetBytes("hat do ya want for nothing?");
            var o = new byte[32];
            mac.Compute((byte)'w', rest, 0, rest.Length, o, 0);
            Assert.AreEqual("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843", Hex(o, 32));
        }

        [Test]
        public void Hmac_MatchesTheBcl_ForManyKeysAndLengths()
        {
            var rng = new ChibiRng(5);
            for (int round = 0; round < 200; round++)
            {
                var key = new byte[1 + rng.Range(100)];
                for (int i = 0; i < key.Length; i++) key[i] = (byte)rng.Range(256);
                var data = new byte[rng.Range(200)];
                for (int i = 0; i < data.Length; i++) data[i] = (byte)rng.Range(256);
                byte prefix = (byte)rng.Range(4);
                var mine = new byte[32];
                new ChibiHmacSha256(key).Compute(prefix, data, 0, data.Length, mine, 0);
                var all = new byte[data.Length + 1];
                all[0] = prefix;
                Buffer.BlockCopy(data, 0, all, 1, data.Length);
                byte[] bcl;
                using (var h = new System.Security.Cryptography.HMACSHA256(key)) bcl = h.ComputeHash(all);
                Assert.AreEqual(Hex(bcl, 32), Hex(mine, 32), "round " + round);
            }
        }

        // ================================================================== tag checks

        [Test]
        public void TaggedDatagram_RoundTrips_FlippedBitAndReflectionAreRejected()
        {
            // Arrange
            var (ra, rb) = Pipe();
            var a = new ChibiAuthLink(ra, Key(0, 1), 0, 1);
            var b = new ChibiAuthLink(rb, Key(0, 1), 1, 0);
            var msg = new byte[] { 0xC7, 1, 2, 3, 4, 5 };
            var got = new byte[256];

            // Act / Assert: clean
            a.Send(msg, msg.Length);
            Assert.AreEqual(msg.Length, b.Receive(got));
            Assert.AreEqual(0xC7, got[0]);

            // flipped bit in transit
            a.Send(msg, msg.Length);
            rb.Tamper(2);
            Assert.AreEqual(0, b.Receive(got));
            Assert.AreEqual(1, b.Rejected);

            // reflected back to its sender: wrong direction byte
            a.Send(msg, msg.Length);
            int n = rb.Receive(got);
            ra.Push(got, n);
            Assert.AreEqual(0, a.Receive(got));
            Assert.AreEqual(1, a.Rejected);

            // untagged / too short
            rb.Push(new byte[] { 0xC7, 1 }, 2);
            Assert.AreEqual(0, b.Receive(got));
            Assert.AreEqual(2, b.Rejected);
        }

        [Test]
        public void SendAndReceive_AllocateNothing()
        {
            var (ra, rb) = Pipe();
            var a = new ChibiAuthLink(ra, Key(0, 2), 0, 2);
            var b = new ChibiAuthLink(rb, Key(0, 2), 2, 0);
            var msg = new byte[60];
            var got = new byte[256];
            a.Send(msg, msg.Length); b.Receive(got);   // warm up

            Assert.That(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    a.Send(msg, msg.Length);
                    b.Receive(got);
                }
            }, Is.Not.AllocatingGCMemory(), "the link tag allocated per datagram");
        }

        // ================================================================== impersonators (mesh)

        sealed class Mesh
        {
            public ChibiLoopbackLink[,] L;
            public ChibiOnlineMatch[] M;
            public int N;
            public ChibiLoopbackLink Link(int i, int j) => i < j ? L[i, j] : L[j, i];
            public IChibiTransport[] RawFor(int i)
            {
                var r = new IChibiTransport[N];
                for (int j = 0; j < N; j++) if (j != i) r[j] = i < j ? L[i, j].A : L[j, i].B;
                return r;
            }
            public void Tick()
            {
                foreach (var m in M) if (m.Phase != ChibiOnlinePhase.Closed) m.Tick();
                foreach (var l in L) l?.Advance();
            }
        }

        static Mesh NewMesh(int forgedSlot)
        {
            const int n = 3;
            var m = new Mesh { N = n, L = new ChibiLoopbackLink[n, n], M = new ChibiOnlineMatch[n] };
            for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) m.L[i, j] = new ChibiLoopbackLink(2, 0, 0);
            var hashes = new uint[] { 11, 22, 33 };
            m.M[0] = ChibiOnlineMatch.MeshHost(m.RawFor(0), new ChibiScriptedInputSource(x => default), ChibiMatchConfig.FormatFfa, hashes,
                                               FighterClass.Melee, 0, 0, 77u, ChibiRollbackSettings.Default,
                                               pairKeys: KeysFor(0, n, forgedSlot));
            for (int i = 1; i < n; i++)
                m.M[i] = ChibiOnlineMatch.MeshJoin(m.RawFor(i), i, new ChibiScriptedInputSource(x => default), ChibiMatchConfig.FormatFfa,
                                                   hashes, FighterClass.Ranged, 0, pairKeys: KeysFor(i, n, forgedSlot));
            return m;
        }

        [Test]
        public void HonestMesh_WithPairKeys_StartsAndPlays()
        {
            var m = NewMesh(-1);
            for (int t = 0; t < 400; t++) m.Tick();
            foreach (var x in m.M)
            {
                Assert.AreEqual(ChibiOnlinePhase.Playing, x.Phase, "slot " + x.LocalIndex + ": " + x.Result);
                Assert.AreEqual(0, x.AuthRejected);
            }
        }

        [Test]
        public void ImpersonatorWithTheJoinCodeButNotTheKey_IsRefusedOnHello()
        {
            // Arrange: whoever sits in slot 2 holds the links (the Relay join codes) but not slot 2's private key.
            var m = NewMesh(forgedSlot: 2);

            // Act
            for (int t = 0; t < 400; t++) m.Tick();

            // Assert: the host never accepted its Hello, so nobody started.
            Assert.Greater(m.M[0].AuthRejected, 0, "the host never saw the forged datagrams");
            Assert.AreNotEqual(ChibiOnlinePhase.Playing, m.M[0].Phase);
            Assert.AreNotEqual(ChibiHandshakeState.Agreed, m.M[0].Handshake.State);
        }

        [Test]
        public void ForgedInputDatagramWithoutTheKey_IsRejectedWithoutEffect()
        {
            // Arrange: an honest mesh in play.
            var m = NewMesh(-1);
            for (int t = 0; t < 400; t++) m.Tick();
            Assert.AreEqual(ChibiOnlinePhase.Playing, m.M[0].Phase, "precondition");
            var lie = new ChibiNetMessage
            {
                Sender = 2, LastFrame = m.M[0].Session.Frame + 3, AckFrame = -1, Count = 1, HasChecksum = true,
                ChecksumFrame = ChibiRollbackSession.ChecksumInterval * 50, Checksum = 0xDEADBEEFu,
            };
            var bb = new BitBuffer(ChibiPacketCodec.MaxPacketBytes);
            ChibiPacketCodec.Write(bb, lie);
            var forged = new byte[bb.ByteLength + ChibiAuthLink.TagBytes];   // well formed, but the tag is zeros
            Buffer.BlockCopy(bb.Bytes, 0, forged, 0, bb.ByteLength);

            // Act: injected on the raw slot-2 link into the host
            m.Link(0, 2).B.Send(forged, forged.Length);
            for (int t = 0; t < 200; t++) m.Tick();

            // Assert
            Assert.Greater(m.M[0].AuthRejected, 0);
            Assert.IsFalse(m.M[0].Session.Desynced);
            Assert.AreEqual(ChibiOnlinePhase.Playing, m.M[0].Phase);
        }
    }
}
