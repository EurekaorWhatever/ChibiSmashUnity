using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Non-blocking UDP socket connected to one remote endpoint (datagrams from anyone else are dropped by the OS).
    /// NAT traversal / matchmaking is out of scope: exchange endpoints through your lobby service, then both
    /// peers call this with the other's address.
    /// </summary>
    public sealed class ChibiUdpTransport : IChibiTransport, IDisposable
    {
        readonly Socket _sock;

        public ChibiUdpTransport(int localPort, IPEndPoint remote)
        {
            _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _sock.Bind(new IPEndPoint(IPAddress.Any, localPort));
            _sock.Connect(remote);
            _sock.Blocking = false;
        }

        public void Send(byte[] data, int length)
        {
            try { _sock.Send(data, 0, length, SocketFlags.None); }
            catch (SocketException) { /* would-block / unreachable: UDP is lossy by contract, redundancy covers it */ }
        }

        public int Receive(byte[] buffer)
        {
            try
            {
                if (_sock.Available <= 0) return 0;
                return _sock.Receive(buffer, 0, buffer.Length, SocketFlags.None);
            }
            catch (SocketException) { return 0; }
        }

        public void Dispose() => _sock.Close();
    }

    /// <summary>
    /// In-process link between two peers with configurable one-way latency (in ticks), jitter and loss.
    /// Jitter can reorder datagrams, exactly like real UDP. Call Advance() once per simulated tick.
    /// </summary>
    public sealed class ChibiLoopbackLink
    {
        sealed class Datagram { public int DeliverAt; public byte[] Data; }

        sealed class Endpoint : IChibiTransport
        {
            readonly ChibiLoopbackLink _link;
            readonly List<Datagram> _inbox = new List<Datagram>();
            Endpoint _peer;

            public Endpoint(ChibiLoopbackLink link) { _link = link; }
            public void Connect(Endpoint peer) { _peer = peer; }

            public void Send(byte[] data, int length)
            {
                if (_link._now < _link._blackoutEnd) return;          // burst loss: the whole link is down
                if (_link._rng.Range(100) < _link.LossPercent) return;
                var copy = new byte[length];
                Buffer.BlockCopy(data, 0, copy, 0, length);
                int delay = _link.LatencyTicks + (_link.JitterTicks > 0 ? _link._rng.Range(_link.JitterTicks + 1) : 0);
                _peer._inbox.Add(new Datagram { DeliverAt = _link._now + delay, Data = copy });
            }

            public int Receive(byte[] buffer)
            {
                int best = -1;
                for (int i = 0; i < _inbox.Count; i++)
                    if (_inbox[i].DeliverAt <= _link._now && (best < 0 || _inbox[i].DeliverAt < _inbox[best].DeliverAt)) best = i;
                if (best < 0) return 0;
                var d = _inbox[best];
                _inbox.RemoveAt(best);
                int n = Math.Min(d.Data.Length, buffer.Length);
                Buffer.BlockCopy(d.Data, 0, buffer, 0, n);
                return n;
            }
        }

        public int LatencyTicks, JitterTicks, LossPercent;
        public readonly IChibiTransport A, B;
        int _now;
        int _blackoutEnd;
        ChibiRng _rng = new ChibiRng(12345);

        public ChibiLoopbackLink(int latencyTicks, int jitterTicks, int lossPercent)
        {
            LatencyTicks = latencyTicks; JitterTicks = jitterTicks; LossPercent = lossPercent;
            var a = new Endpoint(this);
            var b = new Endpoint(this);
            a.Connect(b); b.Connect(a);
            A = a; B = b;
        }

        public void Advance() => _now++;

        /// <summary>
        /// Burst loss: drop every datagram sent in either direction during the next <paramref name="ticks"/> ticks
        /// (a Wi-Fi dropout). Independent per-packet loss (LossPercent) almost never produces such a run.
        /// </summary>
        /// <example><code>link.DropAllFor(24);   // 400 ms outage</code></example>
        public void DropAllFor(int ticks) => _blackoutEnd = _now + ticks;
    }

    /// <summary>Feeds inputs from a function of the frame number. Used by tests, bots and replays.</summary>
    public sealed class ChibiScriptedInputSource : IChibiInputSource
    {
        readonly Func<int, ChibiInputPacket> _script;
        int _n;
        public ChibiScriptedInputSource(Func<int, ChibiInputPacket> script) { _script = script; }
        public void PollRenderFrame() { }
        public ChibiInputPacket SampleTick() => _script(_n++);
    }

    /// <summary>
    /// Keyboard/Input System source. Held state is read at tick time; press events seen in render frames are
    /// latched so a tap shorter than one 16.7 ms tick is never lost.
    /// WASD move, Space jump, J light, K heavy, L shield, U/I/O skills, Q ultimate.
    /// </summary>
    public sealed class ChibiKeyboardInputSource : IChibiInputSource
    {
        ChibiButtons _latched;

        public void PollRenderFrame()
        {
            var k = Keyboard.current;
            if (k == null) return;
            if (k.jKey.wasPressedThisFrame) _latched |= ChibiButtons.Light;
            if (k.kKey.wasPressedThisFrame) _latched |= ChibiButtons.Heavy;
            if (k.spaceKey.wasPressedThisFrame) _latched |= ChibiButtons.Jump;
            if (k.uKey.wasPressedThisFrame) _latched |= ChibiButtons.Skill1;
            if (k.iKey.wasPressedThisFrame) _latched |= ChibiButtons.Skill2;
            if (k.oKey.wasPressedThisFrame) _latched |= ChibiButtons.Skill3;
            if (k.qKey.wasPressedThisFrame) _latched |= ChibiButtons.Ultimate;
        }

        public ChibiInputPacket SampleTick()
        {
            var k = Keyboard.current;
            ChibiButtons b = _latched;
            _latched = ChibiButtons.None;
            int x = 0, y = 0;
            if (k != null)
            {
                if (k.aKey.isPressed) x--;
                if (k.dKey.isPressed) x++;
                if (k.sKey.isPressed) y--;
                if (k.wKey.isPressed) y++;
                if (k.jKey.isPressed) b |= ChibiButtons.Light;
                if (k.kKey.isPressed) b |= ChibiButtons.Heavy;
                if (k.spaceKey.isPressed) b |= ChibiButtons.Jump;
                if (k.lKey.isPressed) b |= ChibiButtons.Shield;
                if (k.uKey.isPressed) b |= ChibiButtons.Skill1;
                if (k.iKey.isPressed) b |= ChibiButtons.Skill2;
                if (k.oKey.isPressed) b |= ChibiButtons.Skill3;
                if (k.qKey.isPressed) b |= ChibiButtons.Ultimate;
            }
            return ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(x, y), b);
        }
    }

    /// <summary>
    /// Headless end-to-end harness: two peers over a lossy, laggy, reordering link, random scripted inputs.
    /// Call from an editor script or a test to validate determinism and rollback after any change.
    /// </summary>
    public static class ChibiNetSelfTest
    {
        public static string Run(int ticks, int latencyTicks, int jitterTicks, int lossPercent, FighterClass c0, FighterClass c1, int passive0, int passive1)
        {
            var cfg = new ChibiMatchConfig { Seed = 0xC0FFEE, Class0 = c0, Class1 = c1, Passive0 = passive0, Passive1 = passive1 };
            var link = new ChibiLoopbackLink(latencyTicks, jitterTicks, lossPercent);
            var a = new ChibiRollbackSession(0, cfg, link.A, new ChibiScriptedInputSource(f => Bot(1, f)));
            var b = new ChibiRollbackSession(1, cfg, link.B, new ChibiScriptedInputSource(f => Bot(2, f)));
            var recA = new ChibiReplayRecorder(cfg);
            var recB = new ChibiReplayRecorder(cfg);
            a.OnFrameConfirmed += recA.Add;
            b.OnFrameConfirmed += recB.Add;
            string desync = null;
            a.OnDesync += (f, l, r) => desync = "A desync at frame " + f;
            b.OnDesync += (f, l, r) => desync = "B desync at frame " + f;

            for (int t = 0; t < ticks; t++)
            {
                link.Advance();
                a.Tick();
                b.Tick();
            }

            int common = Math.Min(recA.FrameCount, recB.FrameCount);
            bool inputsMatch = true;
            for (int i = 0; i < common; i++)
                if (recA.P0[i] != recB.P0[i] || recA.P1[i] != recB.P1[i]) { inputsMatch = false; break; }

            // determinism: replaying A's confirmed inputs must land exactly where B's replay does
            uint ha = ChibiReplay.Simulate(cfg, recA.P0.GetRange(0, common), recA.P1.GetRange(0, common)).ComputeChecksum();
            uint hb = ChibiReplay.Simulate(cfg, recB.P0.GetRange(0, common), recB.P1.GetRange(0, common)).ComputeChecksum();

            bool replayOk;
            using (var ms = new System.IO.MemoryStream())
            {
                ChibiReplay.Write(ms, cfg, recA.P0, recA.P1);
                int bytes = (int)ms.Length;
                ms.Position = 0;
                replayOk = ChibiReplay.Verify(ms);
                return string.Format(
                    "{0} vs {1} | latency {2}±{3} ticks, loss {4}% | frames A={5} B={6} confirmed={7} | rollbacks A={8} B={9} (max depth {10}/{11}, resim {12}/{13}) | stalls A={14} B={15} | checksums compared A={16} B={17} | " +
                    "inputsMatch={18} replayHash {19}={20} replayVerify={21} replayBytes={22} | hpA {23}/{24} hpB {25}/{26} over={27} | {28}",
                    c0, c1, latencyTicks, jitterTicks, lossPercent, a.Frame, b.Frame, common, a.RollbackCount, b.RollbackCount,
                    a.MaxRollbackDepth, b.MaxRollbackDepth, a.ResimulatedFrames, b.ResimulatedFrames, a.StalledTicks, b.StalledTicks,
                    a.ChecksumsCompared, b.ChecksumsCompared, inputsMatch, ha.ToString("X8"), hb.ToString("X8"), replayOk, bytes,
                    a.State.F[0].Hp, a.State.F[1].Hp, b.State.F[0].Hp, b.State.F[1].Hp, a.State.MatchOver, desync ?? "no desync");
            }
        }

        // Deterministic pseudo-random "player": holds a direction for a while and mashes buttons.
        static ChibiInputPacket Bot(int seed, int frame)
        {
            uint h = (uint)(frame / 7) * 2654435761u + (uint)seed * 40503u;
            h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
            int x = (int)(h % 3) - 1;
            int y = (h >> 3) % 8 == 0 ? -1 : 0;
            uint g = (uint)frame * 2246822519u + (uint)seed * 3266489917u;
            g ^= g >> 15; g *= 2654435761u; g ^= g >> 13;
            ChibiButtons b = ChibiButtons.None;
            if (g % 9 == 0) b |= ChibiButtons.Light;
            if (g % 23 == 1) b |= ChibiButtons.Heavy;
            if (g % 31 == 2) b |= ChibiButtons.Jump;
            if (g % 41 == 3) b |= ChibiButtons.Skill1;
            if (g % 47 == 4) b |= ChibiButtons.Skill2;
            if (g % 53 == 5) b |= ChibiButtons.Skill3;
            if (g % 61 == 6) b |= ChibiButtons.Shield;
            if (g % 29 == 7) b |= ChibiButtons.Ultimate;
            return ChibiInputPacket.Make(ChibiInputPacket.DirFromAxes(x, y), b);
        }
    }
}
