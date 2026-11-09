// Story type: Integration (online play over Unity Transport) -- evidence: automated EditMode test over real UDP sockets on
// 127.0.0.1 -- gate: BLOCKING. Spec: docs/architecture/netcode/online-rooms.md. Relay uses the same transport class with
// relay parameters; the Relay/Lobby services themselves need a linked Unity Cloud project and are not unit-testable.
using System.Threading;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>Two ChibiOnlineMatch peers over real Unity Transport UDP: handshake, synced play, forfeit, oversize safety.</summary>
    public class ChibiUtpTransportTests
    {
        static ChibiInputPacket Bot(int seed, int n)
        {
            unchecked
            {
                uint h = (uint)(n / 4) * 2654435761u + (uint)seed * 40503u;
                h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
                ChibiButtons b = ChibiButtons.None;
                if (h % 5 == 0) b |= ChibiButtons.Light;
                if (h % 11 == 1) b |= ChibiButtons.Jump;
                if (h % 17 == 3) b |= ChibiButtons.Dash;
                if (h % 23 == 5) b |= ChibiButtons.Skill1;
                return ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes((int)(h % 3) - 1, 0), b, (Gesture)((n / 50) % 4));
            }
        }

        sealed class Pair : System.IDisposable
        {
            public ChibiUtpTransport HostT, JoinT;
            public ChibiOnlineMatch Host, Join;

            public Pair()
            {
                HostT = ChibiUtpTransport.HostDirect(0, loopbackOnly: true);
                Assert.Greater(HostT.LocalPort, 0, "host bound a port");
                JoinT = ChibiUtpTransport.JoinDirect("127.0.0.1", HostT.LocalPort);
                Host = ChibiOnlineMatch.Host(HostT, new ChibiScriptedInputSource(n => Bot(1, n)), FighterClass.Heavy, 1,
                                             stage: 2, seed: 4242, ChibiRollbackSettings.Default);
                Join = ChibiOnlineMatch.Join(JoinT, new ChibiScriptedInputSource(n => Bot(2, n)), FighterClass.Magic, 0);
            }

            /// <summary>Tick both peers; a 1 ms sleep lets the OS deliver loopback datagrams between ticks.</summary>
            public void Tick(int n)
            {
                for (int i = 0; i < n; i++) { Host.Tick(); Join.Tick(); Thread.Sleep(1); }
            }

            public void Dispose() { Host.Dispose(); Join.Dispose(); }
        }

        [Test]
        public void Loopback_HandshakeAgrees_ConfigHasIntroAndBothFighters()
        {
            using (var p = new Pair())
            {
                for (int i = 0; i < 1500 && (p.Host.Phase == ChibiOnlinePhase.Handshaking || p.Join.Phase == ChibiOnlinePhase.Handshaking); i++)
                    p.Tick(1);
                Assert.AreEqual(ChibiOnlinePhase.Playing, p.Host.Phase, "host started (" + p.Host.Result + ")");
                Assert.AreEqual(ChibiOnlinePhase.Playing, p.Join.Phase, "joiner started (" + p.Join.Result + ")");
                Assert.AreEqual(ChibiLinkState.Connected, p.HostT.LinkState);
                Assert.AreEqual(ChibiLinkState.Connected, p.JoinT.LinkState);
                var cfg = p.Join.Handshake.Config;
                Assert.AreEqual(p.Host.Handshake.MatchHash, p.Join.Handshake.MatchHash);
                Assert.IsTrue(cfg.Intro, "online matches play the intro");
                Assert.IsFalse(cfg.Training);
                Assert.AreEqual(FighterClass.Heavy, cfg.Class0);
                Assert.AreEqual(FighterClass.Magic, cfg.Class1);
                Assert.AreEqual(2, cfg.Stage);
                Assert.AreEqual(0, p.Host.LocalIndex);
                Assert.AreEqual(1, p.Join.LocalIndex);
            }
        }

        [Test]
        public void Loopback_PlayTenSeconds_InSync_ConfirmedInputsAgree()
        {
            using (var p = new Pair())
            {
                for (int i = 0; i < 1500 && (p.Host.Phase != ChibiOnlinePhase.Playing || p.Join.Phase != ChibiOnlinePhase.Playing); i++)
                    p.Tick(1);
                Assume.That(p.Host.Phase == ChibiOnlinePhase.Playing && p.Join.Phase == ChibiOnlinePhase.Playing, "handshake");
                p.Tick(600);
                Assert.IsFalse(p.Host.Session.Desynced || p.Join.Session.Desynced, "desync over real UDP");
                Assert.IsFalse(p.Host.Session.Disconnected || p.Join.Session.Disconnected, "disconnect over real UDP");
                Assert.Greater(p.Host.Session.ChecksumsCompared, 5, "checksums compared");
                int common = System.Math.Min(p.Host.Replay.FrameCount, p.Join.Replay.FrameCount);
                Assert.Greater(common, 300, "frames confirmed on both sides");
                for (int i = 0; i < common; i++)
                {
                    Assert.AreEqual(p.Host.Replay.P0[i], p.Join.Replay.P0[i], "P0 input differs at " + i);
                    Assert.AreEqual(p.Host.Replay.P1[i], p.Join.Replay.P1[i], "P1 input differs at " + i);
                }
                Assert.AreEqual(0, p.HostT.Dropped + p.JoinT.Dropped, "no datagram dropped by the transport");
            }
        }

        [Test]
        public void Loopback_HostForfeits_JoinerWinsByOpponentLeft()
        {
            using (var p = new Pair())
            {
                for (int i = 0; i < 1500 && (p.Host.Phase != ChibiOnlinePhase.Playing || p.Join.Phase != ChibiOnlinePhase.Playing); i++)
                    p.Tick(1);
                Assume.That(p.Host.Phase == ChibiOnlinePhase.Playing && p.Join.Phase == ChibiOnlinePhase.Playing, "handshake");
                p.Tick(200);
                p.Host.Forfeit();
                p.HostT.Flush();
                for (int i = 0; i < 300 && !p.Join.HasResult; i++) { p.Join.Tick(); Thread.Sleep(1); }
                Assert.AreEqual(ChibiMatchOutcome.Loss, p.Host.Result.Outcome, "forfeit = loss");
                Assert.AreEqual(ChibiMatchOutcome.Win, p.Join.Result.Outcome, "Q4: the opponent who stays wins (" + p.Join.Result + ")");
                Assert.AreEqual(ChibiEndCause.OpponentLeft, p.Join.Result.Cause);
            }
        }

        [Test]
        public void Loopback_HostDisposedMidMatch_JoinerWinsByLostConnection()
        {
            using (var p = new Pair())
            {
                for (int i = 0; i < 1500 && (p.Host.Phase != ChibiOnlinePhase.Playing || p.Join.Phase != ChibiOnlinePhase.Playing); i++)
                    p.Tick(1);
                Assume.That(p.Host.Phase == ChibiOnlinePhase.Playing && p.Join.Phase == ChibiOnlinePhase.Playing, "handshake");
                p.Tick(120);
                p.HostT.Dispose();   // the host's game closes without a goodbye (crash / cable pulled)
                for (int i = 0; i < 900 && !p.Join.HasResult; i++) { p.Join.Tick(); Thread.Sleep(1); }
                Assert.AreEqual(ChibiMatchOutcome.Win, p.Join.Result.Outcome, "Q4: a vanished opponent loses (" + p.Join.Result + ")");
            }
        }

        [Test]
        public void Send_OversizedDatagram_IsDroppedNeverTruncated()
        {
            using (var p = new Pair())
            {
                for (int i = 0; i < 1500 && (p.HostT.LinkState != ChibiLinkState.Connected || p.JoinT.LinkState != ChibiLinkState.Connected); i++)
                { p.HostT.Pump(); p.JoinT.Pump(); p.HostT.Flush(); p.JoinT.Flush(); Thread.Sleep(1); }
                Assume.That(p.JoinT.LinkState == ChibiLinkState.Connected, "connected");
                var big = new byte[ChibiChannelMux.MaxDatagram + 40];
                for (int i = 0; i < big.Length; i++) big[i] = 0xC7;
                p.JoinT.Send(big, big.Length);
                p.JoinT.Flush();
                var buf = new byte[ChibiChannelMux.MaxDatagram];
                int got = 0;
                for (int i = 0; i < 100 && got == 0; i++) { p.HostT.Pump(); got = p.HostT.Receive(buf); Thread.Sleep(1); }
                Assert.AreEqual(0, got, "an oversized datagram is never delivered cut short");
                Assert.Greater(p.HostT.Dropped, 0, "and it is counted as dropped");
            }
        }
    }
}
