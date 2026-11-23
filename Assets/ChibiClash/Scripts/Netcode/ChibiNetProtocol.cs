using System;

namespace ChibiClash.Netcode
{
    // =====================================================================================================
    // Channel mux: one datagram pipe, two logical channels selected by the first byte (the magic).
    //   0xC7  rollback inputs   (ChibiPacketCodec, unreliable, 60 Hz, repaired by redundancy)
    //   0xC9  control messages  (ChibiControlCodec, rare; reliability by idempotent resend in ChibiHandshake /
    //                            redundant sends in ChibiOnlineMatch)
    // 0xC8 is reserved for the optional gesture telemetry datagram (netcode-architecture.md section 4).
    // =====================================================================================================

    /// <summary>Connection health reported by a real transport (Relay / UDP). Loopback test links don't implement it.</summary>
    public enum ChibiLinkState : byte
    {
        /// <summary>Still connecting (Relay bind / UTP connect handshake in progress).</summary>
        Connecting,
        /// <summary>Datagrams can flow.</summary>
        Connected,
        /// <summary>The connection is gone and will not come back (Relay severed it, connect failed, disposed).</summary>
        Lost,
    }

    /// <summary>Optional transport capability: report link state so the match can end on a hard disconnect at once.</summary>
    public interface IChibiLinkStatus
    {
        /// <summary>Current link state.</summary>
        ChibiLinkState LinkState { get; }
    }

    /// <summary>
    /// Splits one <see cref="IChibiTransport"/> into the rollback channel and the control channel by magic byte.
    /// Each channel has a fixed ring of preallocated buffers, so steady-state traffic allocates nothing.
    /// Both channel views pump the shared transport when their own queue is empty, so the caller can read either
    /// channel first. Not thread-safe.
    /// </summary>
    public sealed class ChibiChannelMux
    {
        /// <summary>Largest datagram accepted from the wire; bigger ones are dropped (nothing legit comes close).</summary>
        public const int MaxDatagram = 256;
        const int QueueSlots = 64;
        /// <summary>Most datagrams read from the link per <see cref="Pump"/> (an honest peer sends 1-2 per tick).</summary>
        public const int MaxPumpPerCall = 2 * QueueSlots;

        sealed class Channel : IChibiTransport
        {
            readonly ChibiChannelMux _mux;
            readonly byte[][] _slots = new byte[QueueSlots][];
            readonly int[] _len = new int[QueueSlots];
            int _head, _count;
            public int Dropped;

            public Channel(ChibiChannelMux mux)
            {
                _mux = mux;
                for (int i = 0; i < QueueSlots; i++) _slots[i] = new byte[MaxDatagram];
            }

            public void Enqueue(byte[] src, int n)
            {
                if (_count == QueueSlots) { Dropped++; return; }   // full: drop newest (rollback redundancy repairs it)
                int slot = (_head + _count) % QueueSlots;
                Buffer.BlockCopy(src, 0, _slots[slot], 0, n);
                _len[slot] = n;
                _count++;
            }

            public void Send(byte[] data, int length) => _mux._raw.Send(data, length);

            public int Receive(byte[] buffer)
            {
                if (_count == 0) _mux.Pump();
                if (_count == 0) return 0;
                int n = Math.Min(_len[_head], buffer.Length);
                Buffer.BlockCopy(_slots[_head], 0, buffer, 0, n);
                _head = (_head + 1) % QueueSlots;
                _count--;
                return n;
            }
        }

        readonly IChibiTransport _raw;
        readonly Channel _session, _control;
        readonly byte[] _scratch = new byte[MaxDatagram + 1];

        /// <summary>Rollback input channel (magic 0xC7). Hand it to <see cref="ChibiRollbackSession"/>.</summary>
        public IChibiTransport Session => _session;
        /// <summary>Control channel (magic 0xC9): handshake, goodbye.</summary>
        public IChibiTransport Control => _control;
        /// <summary>The wrapped transport (for <see cref="IChibiLinkStatus"/> / disposal).</summary>
        public IChibiTransport Raw => _raw;
        /// <summary>Datagrams dropped: unknown magic, oversized, or a full channel queue.</summary>
        public int Dropped => _unknown + _session.Dropped + _control.Dropped;
        int _unknown;

        /// <summary>Wrap <paramref name="raw"/>. The mux does not own it (dispose the transport yourself).</summary>
        /// <example><code>var mux = new ChibiChannelMux(transport); var s = new ChibiRollbackSession(0, cfg, mux.Session, input);</code></example>
        public ChibiChannelMux(IChibiTransport raw)
        {
            _raw = raw ?? throw new ArgumentNullException(nameof(raw));
            _session = new Channel(this);
            _control = new Channel(this);
        }

        /// <summary>Drain everything the transport holds into the channel queues.</summary>
        public void Pump()
        {
            // Security BLOCKER 6: one mux per link (team play builds one per peer), and at most MaxPumpPerCall datagrams
            // read per call, so a flooding peer can neither evict other links' datagrams nor spin this loop forever. What
            // it leaves behind waits in its own transport queue.
            for (int read = 0; read < MaxPumpPerCall; read++)
            {
                int n = _raw.Receive(_scratch);
                if (n <= 0) return;
                if (n > MaxDatagram) { _unknown++; continue; }
                switch (_scratch[0])
                {
                    case ChibiPacketCodec.Magic: _session.Enqueue(_scratch, n); break;
                    case ChibiControlCodec.Magic: _control.Enqueue(_scratch, n); break;
                    default: _unknown++; break;
                }
            }
        }
    }

    // =====================================================================================================
    // Control messages
    // =====================================================================================================

    /// <summary>Control message kinds (4 bits on the wire).</summary>
    public enum ChibiControlType : byte
    {
        /// <summary>Joiner -> host: my versions, my roster slot, format, player hash and fighter.</summary>
        Hello = 1,
        /// <summary>Host -> joiner: the complete match config (full roster) + rollback settings + their hash.</summary>
        Start = 2,
        /// <summary>Joiner -> host: I accept the Start with this hash and every guest link is verified (I simulate now).</summary>
        Ready = 3,
        /// <summary>Either way: refusing to play (version/config mismatch). Carries a <see cref="ChibiRejectReason"/>.</summary>
        Reject = 4,
        /// <summary>Either way: I am leaving the match. Carries a <see cref="ChibiLeaveReason"/>.</summary>
        Leave = 5,
        /// <summary>
        /// Protocol 6, guest &lt;-&gt; guest (team play): "I am roster slot S and agreed to match hash H". Opens and binds a
        /// guest-to-guest link; a slot or hash that does not match is refused.
        /// </summary>
        PeerHello = 6,
    }

    /// <summary>Why a handshake was refused.</summary>
    public enum ChibiRejectReason : byte
    {
        None = 0,
        /// <summary>Different ChibiPacketCodec.ProtocolVersion: the wire formats are incompatible.</summary>
        ProtocolMismatch = 1,
        /// <summary>Different ChibiMatchConfig.RulesVersion: the games would simulate different rules.</summary>
        RulesMismatch = 2,
        /// <summary>Same versions but a different config hash: balance data or constants differ between the builds.</summary>
        ContentMismatch = 3,
        /// <summary>A field outside its legal range, or a roster entry / format that differs from what was agreed in the lobby.</summary>
        InvalidConfig = 4,
    }

    /// <summary>Why a peer left a running match.</summary>
    public enum ChibiLeaveReason : byte
    {
        /// <summary>Player quit or closed the game mid-match: in 1v1 the other player wins (decision Q4); in team play the leaver is dropped.</summary>
        Forfeit = 0,
        /// <summary>The match reached its confirmed end; this peer is closing the connection.</summary>
        MatchComplete = 1,
        /// <summary>This peer detected a desync: the match is a no contest (for everyone, in team play).</summary>
        Desync = 2,
        /// <summary>
        /// This peer was stalled past the stall limit and is ending the match as a no contest (protocol 5). Without it
        /// the slow side would wait out our silence and record a win. Same layout: the reason is already a full byte.
        /// </summary>
        Stalled = 3,
    }

    /// <summary>One decoded control message (a reused instance; fields not used by <see cref="Type"/> are stale).</summary>
    public sealed class ChibiControlMessage
    {
        public ChibiControlType Type;
        public int Protocol, Rules;
        public ChibiMatchConfig Config;
        public ChibiRollbackSettings Settings;
        public uint MatchHash;
        /// <summary>Hello: the joiner's fighter.</summary>
        public FighterClass Class;
        public int Passive;
        public byte Reason;
        /// <summary>Hello / PeerHello: the sender's roster slot (0..3).</summary>
        public int Slot;
        /// <summary>Hello: the format the sender queued for (<see cref="ChibiMatchConfig.Format1v1"/> ...).</summary>
        public int Format;
        /// <summary>Hello: the sender's player hash (<see cref="ChibiControlCodec.PlayerHash"/>; 0 = unknown).</summary>
        public uint PlayerHash;
        /// <summary>Start: player hash per roster slot.</summary>
        public readonly uint[] PlayerHashes = new uint[4];
        /// <summary>PeerHello: the sender already holds our PeerHello (so we need not answer it).</summary>
        public bool HaveYours;
        /// <summary>
        /// Hello / Start / PeerHello, write side: the sender's chosen player name (cosmetic, optional, NOT part of the match
        /// hash). Only <see cref="ChibiControlCodec.NameAlphabet"/> characters are sent; anything else is dropped.
        /// </summary>
        public string Name = "";
        /// <summary>Read side: received name characters (reused buffer, no allocation per message).</summary>
        public readonly char[] NameChars = new char[ChibiControlCodec.NameMaxChars];
        /// <summary>Number of valid characters in <see cref="NameChars"/> (0 = the peer sent no name).</summary>
        public int NameLength;
        /// <summary>The received name as a string ("" when none). Allocates: call once, not per tick.</summary>
        public string ReceivedName() => NameLength == 0 ? "" : new string(NameChars, 0, NameLength);
    }

    /// <summary>
    /// Wire format of control datagrams (bits, LSB first, via <see cref="BitBuffer"/>), protocol 6:
    ///   8 magic 0xC9, 4 type, then
    ///   Hello     : 8 protocol, 16 rules, 4 class, 1 passive, [name], [2 slot, 2 format, 32 playerHash]
    ///               (the roster trailer is optional on read like the name; a sender always writes both)
    ///   Start     : 8 protocol, 16 rules, 32 seed, 2 format, 2 count - 2, then per slot (count times):
    ///               32 playerHash, 4 class, 1 passive, 2 team; then 4 stage, 1 hazardsOff, 4 inputDelay,
    ///               4 maxPrediction, 16 disconnectTicks, 32 matchHash, [name]
    ///   Ready     : 32 matchHash
    ///   Reject/Leave : 8 reason
    ///   PeerHello : 8 protocol, 2 slot, 1 haveYours, 32 matchHash, [name]
    ///   [name] (optional trailer, cosmetic): 5 length (0..24), then 6 bits per character, an index into
    ///          NameAlphabet. A datagram that ends before the trailer means "no name". A length above 24 is malformed.
    /// Largest (4-slot Start + 24-char name) = 12 + 60 + 4 x 39 + 61 + 149 = 438 bits = 55 bytes.
    /// </summary>
    public static class ChibiControlCodec
    {
        public const byte Magic = 0xC9;
        /// <summary>Buffer size for control datagrams (protocol 6: a 4-slot Start with a name is 55 bytes).</summary>
        public const int MaxBytes = 64;
        const int ClassBits = 4, StageBits = 4, NameLengthBits = 5, NameCharBits = 6;
        const int HelloTrailerBits = 2 + 2 + 32;
        /// <summary>Longest name on the wire (equals ChibiSocialRules.DisplayMax).</summary>
        public const int NameMaxChars = 24;
        /// <summary>
        /// The only characters a name can carry (exactly 64 = 6 bits). No angle brackets, '=', spaces or control
        /// characters, so a hostile peer cannot inject rich-text tags into the HUD through its name.
        /// </summary>
        public const string NameAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_#";

        /// <summary>
        /// Team of roster slot <paramref name="slot"/> by join order (protocol 6, fixed by protocol so a host cannot stack
        /// teams): 2v2 = slots 0,1 vs 2,3 (the simulation's default split); FFA = every slot its own team. 1v1 = 0 (the
        /// team fields are not read in 1v1, so a 1v1 config stays exactly the protocol-5 config).
        /// </summary>
        public static int TeamFor(int format, int slot) =>
            format == ChibiMatchConfig.Format2v2 ? slot >> 1 : format == ChibiMatchConfig.FormatFfa ? slot : 0;

        /// <summary>Fighters a format needs online (FFA: 3 or 4 decided in the lobby, so this returns the maximum).</summary>
        public static int MaxCountFor(int format) => format == ChibiMatchConfig.Format1v1 ? 2 : 4;

        /// <summary>
        /// Stable 32-bit hash of a UGS player id (FNV-1a over UTF-16 units; 0 is reserved for "unknown", so it maps to 1).
        /// Not a secret: it binds a roster slot to the player the lobby put there so the host cannot swap people.
        /// </summary>
        /// <example><code>uint h = ChibiControlCodec.PlayerHash(AuthenticationService.Instance.PlayerId);</code></example>
        public static uint PlayerHash(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return 0;
            uint h = 2166136261u;
            foreach (char c in playerId) { h ^= c; h *= 16777619u; }
            return h == 0 ? 1u : h;
        }

        /// <summary>Set class, passive and team of slot <paramref name="i"/> in <paramref name="cfg"/>.</summary>
        public static void SetSlot(ref ChibiMatchConfig cfg, int i, FighterClass cls, int passive, int team)
        {
            switch (i)
            {
                case 0: cfg.Class0 = cls; cfg.Passive0 = passive; cfg.Team0 = team; break;
                case 1: cfg.Class1 = cls; cfg.Passive1 = passive; cfg.Team1 = team; break;
                case 2: cfg.Class2 = cls; cfg.Passive2 = passive; cfg.Team2 = team; break;
                case 3: cfg.Class3 = cls; cfg.Passive3 = passive; cfg.Team3 = team; break;
            }
        }

        static void WriteName(BitBuffer b, string name)
        {
            name = name ?? "";
            int n = 0;
            foreach (char c in name) if (NameAlphabet.IndexOf(c) >= 0 && ++n == NameMaxChars) break;
            b.Write((uint)n, NameLengthBits);
            int written = 0;
            foreach (char c in name)
            {
                if (written == n) break;
                int k = NameAlphabet.IndexOf(c);
                if (k < 0) continue;
                b.Write((uint)k, NameCharBits);
                written++;
            }
        }

        /// <summary>Optional trailer: absent (too few bits left) = no name. Returns false only when malformed.</summary>
        static bool ReadName(BitBuffer b, ChibiControlMessage m)
        {
            m.NameLength = 0;
            if (b.BitsLeft < NameLengthBits) return true;
            int n = (int)b.Read(NameLengthBits);
            if (n > NameMaxChars) return false;
            for (int i = 0; i < n; i++) m.NameChars[i] = NameAlphabet[(int)b.Read(NameCharBits)];
            if (b.Overrun) return false;
            m.NameLength = n;
            return true;
        }

        /// <summary>Serialise <paramref name="m"/>. Throws on overrun (never sends a truncated message).</summary>
        public static void Write(BitBuffer b, ChibiControlMessage m)
        {
            b.Write(Magic, 8);
            b.Write((uint)m.Type, 4);
            switch (m.Type)
            {
                case ChibiControlType.Hello:
                    b.Write((uint)m.Protocol, 8);
                    b.Write((uint)m.Rules, 16);
                    b.Write((uint)m.Class, ClassBits);
                    b.Write((uint)m.Passive, 1);
                    WriteName(b, m.Name);
                    // Protocol 6 roster trailer, after the (always written) name length: optional on read.
                    b.Write((uint)m.Slot & 3u, 2);
                    b.Write((uint)m.Format & 3u, 2);
                    b.Write(m.PlayerHash, 32);
                    break;
                case ChibiControlType.Start:
                {
                    int count = m.Config.EffectiveCount;
                    b.Write((uint)m.Protocol, 8);
                    b.Write((uint)m.Rules, 16);
                    b.Write(m.Config.Seed, 32);
                    b.Write((uint)m.Config.EffectiveFormat, 2);
                    b.Write((uint)(count - 2), 2);
                    for (int i = 0; i < count; i++)
                    {
                        b.Write(m.PlayerHashes[i], 32);
                        b.Write((uint)m.Config.ClassOf(i), ClassBits);
                        b.Write((uint)m.Config.PassiveOf(i), 1);
                        b.Write((uint)m.Config.RawTeam(i) & 3u, 2);
                    }
                    b.Write((uint)m.Config.Stage, StageBits);
                    b.Write(m.Config.HazardsOff ? 1u : 0u, 1);   // protocol 4 (rules v21, ST-5)
                    b.Write((uint)m.Settings.InputDelay, 4);
                    b.Write((uint)m.Settings.MaxPrediction, 4);
                    b.Write((uint)m.Settings.DisconnectTicks, 16);
                    b.Write(m.MatchHash, 32);
                    WriteName(b, m.Name);
                    break;
                }
                case ChibiControlType.Ready:
                    b.Write(m.MatchHash, 32);
                    break;
                case ChibiControlType.Reject:
                case ChibiControlType.Leave:
                    b.Write(m.Reason, 8);
                    break;
                case ChibiControlType.PeerHello:
                    b.Write((uint)m.Protocol, 8);
                    b.Write((uint)m.Slot & 3u, 2);
                    b.WriteBool(m.HaveYours);
                    b.Write(m.MatchHash, 32);
                    WriteName(b, m.Name);
                    break;
                default:
                    throw new InvalidOperationException("ChibiControlCodec.Write: unknown type " + m.Type);
            }
            if (b.Overrun) throw new InvalidOperationException("ChibiControlCodec.Write: message overran the buffer");
        }

        /// <summary>Parse a control datagram. Returns false for anything malformed; never throws on wire data.</summary>
        public static bool TryRead(BitBuffer b, ChibiControlMessage m)
        {
            if (b.Read(8) != Magic) return false;
            m.Type = (ChibiControlType)b.Read(4);
            m.NameLength = 0;
            switch (m.Type)
            {
                case ChibiControlType.Hello:
                    m.Protocol = (int)b.Read(8);
                    m.Rules = (int)b.Read(16);
                    m.Class = (FighterClass)b.Read(ClassBits);
                    m.Passive = (int)b.Read(1);
                    if (b.Overrun || !ReadName(b, m)) return false;
                    // Optional roster trailer (absent: a 1v1 joiner in slot 1 with no known player hash). A mesh host then
                    // refuses it, because the slot / format / hash do not match the link it arrived on.
                    m.Slot = 1; m.Format = ChibiMatchConfig.Format1v1; m.PlayerHash = 0;
                    int left = b.BitsLeft;
                    // All or nothing: fewer than 8 bits left is byte padding (no trailer); 8..35 is a cut datagram.
                    if (left >= 8 && left < HelloTrailerBits) return false;
                    if (left >= HelloTrailerBits)
                    {
                        m.Slot = (int)b.Read(2);
                        m.Format = (int)b.Read(2);
                        m.PlayerHash = b.Read(32);
                    }
                    break;
                case ChibiControlType.Start:
                {
                    m.Protocol = (int)b.Read(8);
                    m.Rules = (int)b.Read(16);
                    m.Config = default;
                    m.Config.Seed = b.Read(32);
                    m.Config.Format = (int)b.Read(2);
                    int count = (int)b.Read(2) + 2;
                    if (count > 4 || m.Config.Format > ChibiMatchConfig.Format2v2) return false;
                    m.Config.Count = count;
                    for (int i = 0; i < 4; i++) m.PlayerHashes[i] = 0;
                    for (int i = 0; i < count; i++)
                    {
                        m.PlayerHashes[i] = b.Read(32);
                        var cls = (FighterClass)b.Read(ClassBits);
                        int passive = (int)b.Read(1);
                        int team = (int)b.Read(2);
                        SetSlot(ref m.Config, i, cls, passive, team);
                    }
                    m.Config.Stage = (int)b.Read(StageBits);
                    m.Config.HazardsOff = b.Read(1) != 0;
                    m.Settings.InputDelay = (int)b.Read(4);
                    m.Settings.MaxPrediction = (int)b.Read(4);
                    m.Settings.DisconnectTicks = (int)b.Read(16);
                    m.MatchHash = b.Read(32);
                    if (b.Overrun || !ReadName(b, m)) return false;
                    break;
                }
                case ChibiControlType.Ready:
                    m.MatchHash = b.Read(32);
                    break;
                case ChibiControlType.Reject:
                case ChibiControlType.Leave:
                    m.Reason = (byte)b.Read(8);
                    break;
                case ChibiControlType.PeerHello:
                    m.Protocol = (int)b.Read(8);
                    m.Slot = (int)b.Read(2);
                    m.HaveYours = b.ReadBool();
                    m.MatchHash = b.Read(32);
                    if (b.Overrun || !ReadName(b, m)) return false;
                    break;
                default:
                    return false;
            }
            return !b.Overrun;
        }

        /// <summary>
        /// The value both peers must agree on before simulating: the config hash (RulesVersion, seed, fighters, stage and
        /// every gameplay constant table) mixed with the wire protocol and the rollback settings.
        /// </summary>
        /// <example><code>uint h = ChibiControlCodec.MatchHash(cfg, ChibiRollbackSettings.Default);</code></example>
        public static uint MatchHash(in ChibiMatchConfig cfg, in ChibiRollbackSettings settings)
        {
            uint h = cfg.ComputeHash();
            ChibiSimState.Mix(ref h, ChibiPacketCodec.ProtocolVersion);
            ChibiSimState.Mix(ref h, settings.InputDelay);
            ChibiSimState.Mix(ref h, settings.MaxPrediction);
            ChibiSimState.Mix(ref h, settings.DisconnectTicks);
            return h;
        }

        /// <summary>
        /// Protocol 6: <see cref="MatchHash(in ChibiMatchConfig, in ChibiRollbackSettings)"/> plus the roster's player hashes
        /// (when at least one is known; each joiner separately checks the entries it knows from the lobby).
        /// </summary>
        public static uint MatchHash(in ChibiMatchConfig cfg, in ChibiRollbackSettings settings, uint[] playerHashes)
        {
            uint h = MatchHash(cfg, settings);
            int n = cfg.EffectiveCount;
            bool any = false;
            for (int i = 0; i < n; i++) any |= playerHashes[i] != 0;
            if (!any) return h;   // no roster identities (tests, offline tools): same value as the plain match hash
            for (int i = 0; i < n; i++) ChibiSimState.Mix(ref h, unchecked((int)playerHashes[i]));
            return h;
        }

        /// <summary>Legal fighter class value (wire values are 4 bits; only 0..4 exist).</summary>
        public static bool ValidClass(FighterClass c) => c >= FighterClass.Melee && c <= FighterClass.Polearm;
    }

    // =====================================================================================================
    // Handshake
    // =====================================================================================================

    /// <summary>Progress of a <see cref="ChibiHandshake"/>.</summary>
    public enum ChibiHandshakeState : byte
    {
        /// <summary>Exchanging Hello / Start / PeerHello / Ready.</summary>
        Negotiating,
        /// <summary>Agreed: <see cref="ChibiHandshake.Config"/> and <see cref="ChibiHandshake.Settings"/> are final. Start simulating.</summary>
        Agreed,
        /// <summary>One side refused (see <see cref="ChibiHandshake.RejectReason"/>).</summary>
        Rejected,
        /// <summary>No agreement within the timeout.</summary>
        TimedOut,
        /// <summary>A peer sent Leave before the match started (cancelled).</summary>
        PeerLeft,
    }

    /// <summary>
    /// Agrees on the match before any input flows, for 2..4 peers in a full mesh (protocol 6). The HOST (roster slot 0) is
    /// the config authority: it picks the seed, stage and rollback settings and adds each joiner's fighter from its Hello.
    /// Every peer then proves it would simulate the same match by comparing <see cref="ChibiControlCodec.MatchHash"/>,
    /// which covers RulesVersion, every gameplay constant table and the roster's player hashes.
    ///
    /// Links are indexed by roster slot (<c>controls[slot]</c>, null at our own slot). The host holds a link to every
    /// joiner; a joiner holds the host link (slot 0) and one link to every other joiner. Each link is bound to its slot:
    /// a Hello or PeerHello claiming another slot is refused (security BLOCKER 1).
    ///
    ///   joiner j                     host                        joiner k (guest link j-k)
    ///   Hello (every 10 ticks) --->                               validate slot / format / player hash
    ///                          <--- Start (all Hellos in; resent until each Ready)
    ///   validate roster entry, format, teams, hash
    ///   PeerHello{j, hash} ----------------------------------->   (and k -> j) slot and hash must match
    ///   all guest links verified: Agreed
    ///   Ready (on every Start) --->                               all Ready: Agreed
    ///
    /// Every message is idempotent and resent, so it works over the lossy unreliable channel. Peers keep answering Start
    /// with Ready and a PeerHello that lacks ours with ours after they agreed (call <see cref="Handle"/> during the match
    /// too). 1v1 is the same exchange with two slots and no guest links. Pure C#, no allocation per tick.
    /// </summary>
    public sealed class ChibiHandshake
    {
        /// <summary>Resend interval for Hello / Start / PeerHello (ticks).</summary>
        public const int ResendTicks = 10;
        /// <summary>Give up after this long without agreement (10 s: covers Relay connect + a slow peer).</summary>
        public const int DefaultTimeoutTicks = 600;
        const int MaxSlots = 4;

        readonly bool _isHost;
        readonly int _mySlot, _format, _count;
        readonly IChibiTransport[] _controls;
        readonly uint[] _expectHashes = new uint[MaxSlots];   // 0 = not checked
        readonly uint _myHash;
        readonly BitBuffer _tx = new BitBuffer(ChibiControlCodec.MaxBytes);
        readonly BitBuffer _rx = new BitBuffer(ChibiControlCodec.MaxBytes);
        readonly ChibiControlMessage _out = new ChibiControlMessage(), _in = new ChibiControlMessage();
        readonly byte[] _raw = new byte[ChibiChannelMux.MaxDatagram];
        readonly int _timeoutTicks;
        int _ticks;

        // host side
        readonly FighterClass _hostClass;
        readonly int _hostPassive, _stage;
        readonly bool _hazardsOff;
        readonly uint _seed;
        readonly bool[] _haveHello = new bool[MaxSlots];
        readonly bool[] _haveReady = new bool[MaxSlots];
        readonly FighterClass[] _helloClass = new FighterClass[MaxSlots];
        readonly int[] _helloPassive = new int[MaxSlots];
        readonly uint[] _helloHash = new uint[MaxSlots];
        bool _started;

        // joiner side
        readonly FighterClass _myClass;
        readonly int _myPassive;
        /// <summary>SEC-001: the stage the joiner agreed to in the room (-1 = any, e.g. quick play).</summary>
        readonly int _expectStage = -1;
        /// <summary>SEC-001: the hazards switch the joiner agreed to in the room (null = any).</summary>
        readonly bool? _expectHazardsOff;
        bool _haveStart;
        readonly bool[] _peerVerified = new bool[MaxSlots];
        readonly bool[] _peerHasOurs = new bool[MaxSlots];

        ChibiMatchConfig _cfg;
        ChibiRollbackSettings _settings;
        readonly uint[] _rosterHashes = new uint[MaxSlots];
        uint _hash;
        readonly string[] _names = new string[MaxSlots];

        public ChibiHandshakeState State { get; private set; } = ChibiHandshakeState.Negotiating;
        /// <summary>Agreed config (valid once <see cref="State"/> is Agreed).</summary>
        public ChibiMatchConfig Config => _cfg;
        /// <summary>Agreed rollback settings (valid once Agreed).</summary>
        public ChibiRollbackSettings Settings => _settings;
        /// <summary>Agreed match hash (valid once Agreed).</summary>
        public uint MatchHash => _hash;
        /// <summary>Sim player index = roster slot: host 0, joiners 1..3.</summary>
        public int LocalIndex => _mySlot;
        /// <summary>Roster size (2..4).</summary>
        public int Count => _count;
        /// <summary>Match format agreed in the lobby.</summary>
        public int Format => _format;
        /// <summary>Why it was rejected (local or remote refusal).</summary>
        public ChibiRejectReason RejectReason { get; private set; }
        /// <summary>True when a PEER refused us (false when we refused them).</summary>
        public bool RejectedByPeer { get; private set; }
        /// <summary>Slot of the link the refusal or leave came from (-1 = none).</summary>
        public int ProblemSlot { get; private set; } = -1;
        /// <summary>Our player name as sent to the peers (cosmetic; "" = none).</summary>
        public string LocalName { get; }
        /// <summary>
        /// 1v1: the other player's name from its Hello / Start ("" when it sent none). Untrusted but already limited to
        /// <see cref="ChibiControlCodec.NameAlphabet"/>; it never affects the simulation or the match hash.
        /// </summary>
        public string PeerName => _count == 2 ? NameOf(1 - _mySlot) : NameOf(_mySlot == 0 ? 1 : 0);
        /// <summary>Name received from roster slot <paramref name="slot"/> ("" when none / not yet).</summary>
        public string NameOf(int slot) => slot >= 0 && slot < MaxSlots && _names[slot] != null ? _names[slot] : "";
        /// <summary>Player hash of roster slot <paramref name="slot"/> in the agreed Start (0 = unknown).</summary>
        public uint PlayerHashOf(int slot) => slot >= 0 && slot < MaxSlots ? _rosterHashes[slot] : 0;

        /// <summary>1v1 host (slot 0): decides seed, stage and settings.</summary>
        /// <example><code>var hs = ChibiHandshake.ForHost(mux.Control, FighterClass.Melee, 0, stage: 1, seed, ChibiRollbackSettings.Default);</code></example>
        /// <param name="hazardsOff">Rules v20 (ST-5): stage hazards off for this match (host's choice; travels in Start).</param>
        public static ChibiHandshake ForHost(IChibiTransport control, FighterClass myClass, int myPassive, int stage, uint seed,
                                             ChibiRollbackSettings settings, int timeoutTicks = DefaultTimeoutTicks,
                                             string localName = "", bool hazardsOff = false)
            => new ChibiHandshake(true, 0, ChibiMatchConfig.Format1v1, 2, new[] { null, control }, null,
                                  myClass, myPassive, stage, seed, settings, timeoutTicks, localName, hazardsOff);

        /// <summary>
        /// Team-play host (slot 0). <paramref name="controls"/>[j] is the control channel of the link bound to slot j
        /// (null at 0); its length is the roster size. <paramref name="playerHashes"/>[j] is the lobby's player in slot j
        /// (null or 0 entries = not checked); the host's own entry goes into Start.
        /// </summary>
        public static ChibiHandshake ForMeshHost(IChibiTransport[] controls, int format, uint[] playerHashes, FighterClass myClass,
                                                 int myPassive, int stage, uint seed, ChibiRollbackSettings settings,
                                                 int timeoutTicks = DefaultTimeoutTicks, string localName = "", bool hazardsOff = false)
            => new ChibiHandshake(true, 0, format, controls?.Length ?? 0, controls, playerHashes,
                                  myClass, myPassive, stage, seed, settings, timeoutTicks, localName, hazardsOff);

        /// <summary>1v1 joiner (slot 1): sends its fighter, accepts the host's config if it hashes the same locally.</summary>
        /// <example><code>var hs = ChibiHandshake.ForJoiner(mux.Control, FighterClass.Magic, 1);</code></example>
        /// <param name="expectStage">SEC-001: the room's stage the joiner agreed to; a Start with another stage is refused
        /// (InvalidConfig). -1 = accept any stage (quick play, where the host's pick is used).</param>
        /// <param name="expectHazardsOff">SEC-001: the room's hazards switch; a Start that differs is refused. null = any.</param>
        public static ChibiHandshake ForJoiner(IChibiTransport control, FighterClass myClass, int myPassive,
                                               int timeoutTicks = DefaultTimeoutTicks, string localName = "",
                                               int expectStage = -1, bool? expectHazardsOff = null)
            => new ChibiHandshake(false, 1, ChibiMatchConfig.Format1v1, 2, new[] { control, null }, null,
                                  myClass, myPassive, 0, 0, default, timeoutTicks, localName, false, expectStage, expectHazardsOff);

        /// <summary>
        /// Team-play joiner in roster slot <paramref name="mySlot"/>. <paramref name="controls"/>[0] is the host link,
        /// <paramref name="controls"/>[k] the guest link to slot k (null at <paramref name="mySlot"/>). The Start must carry
        /// <paramref name="format"/>, controls.Length fighters, our own entry and (when given) the lobby's player hashes.
        /// </summary>
        public static ChibiHandshake ForMeshJoiner(IChibiTransport[] controls, int mySlot, int format, uint[] playerHashes,
                                                   FighterClass myClass, int myPassive, int timeoutTicks = DefaultTimeoutTicks,
                                                   string localName = "", int expectStage = -1, bool? expectHazardsOff = null)
            => new ChibiHandshake(false, mySlot, format, controls?.Length ?? 0, controls, playerHashes,
                                  myClass, myPassive, 0, 0, default, timeoutTicks, localName, false, expectStage, expectHazardsOff);

        ChibiHandshake(bool isHost, int mySlot, int format, int count, IChibiTransport[] controls, uint[] playerHashes,
                       FighterClass myClass, int myPassive, int stage, uint seed, ChibiRollbackSettings settings, int timeoutTicks,
                       string localName, bool hazardsOff, int expectStage = -1, bool? expectHazardsOff = null)
        {
            if (controls == null) throw new ArgumentNullException(nameof(controls));
            if (format < ChibiMatchConfig.Format1v1 || format > ChibiMatchConfig.Format2v2) throw new ArgumentOutOfRangeException(nameof(format));
            int need = format == ChibiMatchConfig.Format1v1 ? 2 : format == ChibiMatchConfig.Format2v2 ? 4 : -1;
            if (count < 2 || count > MaxSlots || (need > 0 && count != need) || (need < 0 && count < 3))
                throw new ArgumentException("roster size " + count + " does not fit format " + format, nameof(controls));
            if (mySlot < 0 || mySlot >= count) throw new ArgumentOutOfRangeException(nameof(mySlot));
            for (int i = 0; i < count; i++)
            {
                bool needLink = i != mySlot;
                if (needLink && controls[i] == null) throw new ArgumentNullException(nameof(controls), "missing link for slot " + i);
            }
            if (playerHashes != null)
                for (int i = 0; i < count && i < playerHashes.Length; i++) _expectHashes[i] = playerHashes[i];
            _isHost = isHost;
            _mySlot = mySlot;
            _format = format;
            _count = count;
            _controls = (IChibiTransport[])controls.Clone();
            _controls[mySlot] = null;
            _myHash = _expectHashes[mySlot];
            _hazardsOff = hazardsOff;
            _expectStage = expectStage;
            _expectHazardsOff = expectHazardsOff;
            LocalName = localName ?? "";
            _timeoutTicks = timeoutTicks;
            if (!ChibiControlCodec.ValidClass(myClass)) throw new ArgumentOutOfRangeException(nameof(myClass));
            if (isHost)
            {
                settings.Validate();
                _hostClass = myClass; _hostPassive = myPassive == 0 ? 0 : 1; _stage = stage; _seed = seed; _settings = settings;
            }
            else { _myClass = myClass; _myPassive = myPassive == 0 ? 0 : 1; }
        }

        /// <summary>One 60 Hz tick: read control messages, resend what is pending, check the timeout.</summary>
        public void Tick()
        {
            Handle();
            if (State != ChibiHandshakeState.Negotiating) return;
            if (_ticks % ResendTicks == 0)
            {
                if (_isHost)
                {
                    if (_started) for (int j = 1; j < _count; j++) if (!_haveReady[j]) SendStart(j);
                }
                else if (!_haveStart) SendHello();
                else for (int k = 1; k < _count; k++) if (k != _mySlot && (!_peerVerified[k] || !_peerHasOurs[k])) SendPeerHello(k);
            }
            if (++_ticks > _timeoutTicks) State = ChibiHandshakeState.TimedOut;
        }

        /// <summary>
        /// Read and act on every waiting control message on every link. During a match the caller routes Leave messages
        /// itself, so this returns the first Leave it sees (and the slot it came from) or false, leaving the rest queued.
        /// </summary>
        public bool Handle(out int fromSlot, out ChibiLeaveReason leaveReason)
        {
            leaveReason = ChibiLeaveReason.Forfeit;
            fromSlot = -1;
            for (int s = 0; s < _count; s++)
            {
                var c = _controls[s];
                if (c == null) continue;
                while (true)
                {
                    int n = c.Receive(_raw);
                    if (n <= 0) break;
                    _rx.LoadFrom(_raw, n);
                    if (!ChibiControlCodec.TryRead(_rx, _in)) continue;
                    if (_in.Type == ChibiControlType.Leave) { leaveReason = (ChibiLeaveReason)_in.Reason; fromSlot = s; return true; }
                    if (_in.Type == ChibiControlType.Reject) { PeerRejected(s, _in); continue; }
                    if (_isHost) OnHostMessage(s, _in);
                    else if (s == 0) OnJoinerHostMessage(_in);
                    else OnGuestLinkMessage(s, _in);
                }
            }
            return false;
        }

        /// <summary>1v1 form of <see cref="Handle(out int, out ChibiLeaveReason)"/>.</summary>
        public bool Handle(out ChibiLeaveReason leaveReason) => Handle(out _, out leaveReason);

        /// <summary><see cref="Handle(out ChibiLeaveReason)"/> for the negotiating phase (a Leave there aborts the handshake).</summary>
        public void Handle()
        {
            if (Handle(out int slot, out _) && State == ChibiHandshakeState.Negotiating)
            {
                ProblemSlot = slot;
                State = ChibiHandshakeState.PeerLeft;
            }
        }

        // ------------------------------------------------------------------ host

        void OnHostMessage(int s, ChibiControlMessage m)
        {
            switch (m.Type)
            {
                case ChibiControlType.Hello:
                    if (_started)
                    {
                        if (State == ChibiHandshakeState.Negotiating && !_haveReady[s]) SendStart(s);   // our Start was lost
                        return;
                    }
                    if (State != ChibiHandshakeState.Negotiating) return;
                    if (m.Protocol != ChibiPacketCodec.ProtocolVersion) { Reject(s, ChibiRejectReason.ProtocolMismatch); return; }
                    if (m.Rules != ChibiMatchConfig.RulesVersion) { Reject(s, ChibiRejectReason.RulesMismatch); return; }
                    if (!ChibiControlCodec.ValidClass(m.Class)) { Reject(s, ChibiRejectReason.InvalidConfig); return; }
                    // BLOCKER 1/2: the link is bound to slot s and the lobby put a known player there.
                    if (m.Slot != s || m.Format != _format) { Reject(s, ChibiRejectReason.InvalidConfig); return; }
                    if (_expectHashes[s] != 0 && m.PlayerHash != _expectHashes[s]) { Reject(s, ChibiRejectReason.InvalidConfig); return; }
                    _haveHello[s] = true;
                    _helloClass[s] = m.Class;
                    _helloPassive[s] = m.Passive;
                    _helloHash[s] = m.PlayerHash;
                    _names[s] = m.ReceivedName();
                    for (int j = 1; j < _count; j++) if (!_haveHello[j]) return;
                    BuildStart();
                    for (int j = 1; j < _count; j++) SendStart(j);
                    break;
                case ChibiControlType.Ready:
                    if (State != ChibiHandshakeState.Negotiating || !_started) return;
                    if (m.MatchHash != _hash) { Reject(s, ChibiRejectReason.ContentMismatch); return; }
                    _haveReady[s] = true;
                    for (int j = 1; j < _count; j++) if (!_haveReady[j]) return;
                    State = ChibiHandshakeState.Agreed;
                    break;
            }
        }

        void BuildStart()
        {
            _cfg = new ChibiMatchConfig
            {
                Seed = _seed, Stage = _stage, Format = _format, Count = _count,
                Intro = true,                  // online matches always play the 2.2 s intro, never training (rules v16)
                HazardsOff = _hazardsOff,      // rules v21: the host decides (ST-5), sent in Start
            };
            ChibiControlCodec.SetSlot(ref _cfg, 0, _hostClass, _hostPassive, ChibiControlCodec.TeamFor(_format, 0));
            _rosterHashes[0] = _expectHashes[0];
            for (int j = 1; j < _count; j++)
            {
                ChibiControlCodec.SetSlot(ref _cfg, j, _helloClass[j], _helloPassive[j], ChibiControlCodec.TeamFor(_format, j));
                _rosterHashes[j] = _helloHash[j];
            }
            _hash = ChibiControlCodec.MatchHash(_cfg, _settings, _rosterHashes);
            _started = true;
        }

        // ------------------------------------------------------------------ joiner

        void OnJoinerHostMessage(ChibiControlMessage m)
        {
            if (m.Type != ChibiControlType.Start) return;
            if (_haveStart)
            {
                if (m.MatchHash == _hash && State == ChibiHandshakeState.Agreed) SendReady();   // host missed our Ready
                return;
            }
            if (State != ChibiHandshakeState.Negotiating) return;
            if (m.Protocol != ChibiPacketCodec.ProtocolVersion) { Reject(0, ChibiRejectReason.ProtocolMismatch); return; }
            if (m.Rules != ChibiMatchConfig.RulesVersion) { Reject(0, ChibiRejectReason.RulesMismatch); return; }
            if (!ValidStart(m)) { Reject(0, ChibiRejectReason.InvalidConfig); return; }
            // Online flags are fixed by protocol, not sent: intro on, training off (rules v16).
            var cfg = m.Config;
            cfg.Intro = true;
            cfg.Training = false;
            // Recompute with OUR constant tables: equal only if both builds would simulate the same match.
            if (ChibiControlCodec.MatchHash(cfg, m.Settings, m.PlayerHashes) != m.MatchHash) { Reject(0, ChibiRejectReason.ContentMismatch); return; }
            _cfg = cfg;
            _settings = m.Settings;
            _hash = m.MatchHash;
            for (int i = 0; i < MaxSlots; i++) _rosterHashes[i] = m.PlayerHashes[i];
            _names[0] = m.ReceivedName();
            _haveStart = true;
            for (int k = 1; k < _count; k++) if (k != _mySlot) SendPeerHello(k);
            TryAgree();
        }

        void OnGuestLinkMessage(int s, ChibiControlMessage m)
        {
            if (m.Type != ChibiControlType.PeerHello) return;
            if (m.Protocol != ChibiPacketCodec.ProtocolVersion)
            {
                if (State == ChibiHandshakeState.Negotiating) Reject(s, ChibiRejectReason.ProtocolMismatch);
                return;
            }
            if (!_haveStart) return;                                   // we cannot judge the hash yet; it is resent
            // BLOCKER 2: the guest link is bound to slot s and both ends must have agreed to the same Start.
            if (m.Slot != s || m.MatchHash != _hash)
            {
                if (State == ChibiHandshakeState.Negotiating) Reject(s, m.Slot != s ? ChibiRejectReason.InvalidConfig : ChibiRejectReason.ContentMismatch);
                return;
            }
            if (!_peerVerified[s]) { _peerVerified[s] = true; _names[s] = m.ReceivedName(); }
            _peerHasOurs[s] = m.HaveYours;
            if (!m.HaveYours) SendPeerHello(s);                        // answer once; their next one carries HaveYours
            TryAgree();
        }

        void TryAgree()
        {
            if (State != ChibiHandshakeState.Negotiating || !_haveStart) return;
            for (int k = 1; k < _count; k++) if (k != _mySlot && !_peerVerified[k]) return;
            State = ChibiHandshakeState.Agreed;
            SendReady();
        }

        bool ValidStart(ChibiControlMessage m)
        {
            var c = m.Config;
            // BLOCKER 2: the format and roster size the lobby agreed to; teams by the fixed join-order rule.
            if (c.Format != _format || c.EffectiveCount != _count || c.Count != _count) return false;
            for (int i = 0; i < _count; i++)
            {
                if (!ChibiControlCodec.ValidClass(c.ClassOf(i))) return false;
                if (_format != ChibiMatchConfig.Format1v1 && c.RawTeam(i) != ChibiControlCodec.TeamFor(_format, i)) return false;
                if (_expectHashes[i] != 0 && m.PlayerHashes[i] != _expectHashes[i]) return false;
            }
            if (c.ClassOf(_mySlot) != _myClass || c.PassiveOf(_mySlot) != _myPassive) return false;   // host must echo our pick
            if (c.Stage < 0 || c.Stage >= ChibiStageTable.Count) return false;   // rules v21: stages 0..7
            // SEC-001: in a room the joiner agreed to a stage and a hazards switch; the host cannot swap them in Start.
            if (_expectStage >= 0 && c.Stage != _expectStage) return false;
            if (_expectHazardsOff.HasValue && c.HazardsOff != _expectHazardsOff.Value) return false;
            try { m.Settings.Validate(); } catch (ArgumentOutOfRangeException) { return false; }
            return true;
        }

        // ------------------------------------------------------------------ send helpers

        void Reject(int slot, ChibiRejectReason r)
        {
            RejectReason = r;
            RejectedByPeer = false;
            ProblemSlot = slot;
            State = ChibiHandshakeState.Rejected;
            _out.Type = ChibiControlType.Reject;
            _out.Reason = (byte)r;
            for (int i = 0; i < 3; i++) Send(slot);   // no resend loop after rejecting: send a few copies
            // Everyone else: the match is off.
            _out.Type = ChibiControlType.Leave;
            _out.Reason = (byte)ChibiLeaveReason.Forfeit;
            for (int s = 0; s < _count; s++) if (s != slot && _controls[s] != null) Send(s);
        }

        void PeerRejected(int slot, ChibiControlMessage m)
        {
            if (State != ChibiHandshakeState.Negotiating) return;
            RejectReason = (ChibiRejectReason)m.Reason;
            RejectedByPeer = true;
            ProblemSlot = slot;
            State = ChibiHandshakeState.Rejected;
        }

        void SendHello()
        {
            _out.Type = ChibiControlType.Hello;
            _out.Protocol = ChibiPacketCodec.ProtocolVersion;
            _out.Rules = ChibiMatchConfig.RulesVersion;
            _out.Class = _myClass;
            _out.Passive = _myPassive;
            _out.Slot = _mySlot;
            _out.Format = _format;
            _out.PlayerHash = _myHash;
            _out.Name = LocalName;
            Send(0);
        }

        void SendStart(int slot)
        {
            _out.Type = ChibiControlType.Start;
            _out.Protocol = ChibiPacketCodec.ProtocolVersion;
            _out.Rules = ChibiMatchConfig.RulesVersion;
            _out.Config = _cfg;
            _out.Settings = _settings;
            _out.MatchHash = _hash;
            for (int i = 0; i < MaxSlots; i++) _out.PlayerHashes[i] = _rosterHashes[i];
            _out.Name = LocalName;
            Send(slot);
        }

        void SendReady()
        {
            _out.Type = ChibiControlType.Ready;
            _out.MatchHash = _hash;
            Send(0);
        }

        void SendPeerHello(int slot)
        {
            _out.Type = ChibiControlType.PeerHello;
            _out.Protocol = ChibiPacketCodec.ProtocolVersion;
            _out.Slot = _mySlot;
            _out.HaveYours = _peerVerified[slot];
            _out.MatchHash = _hash;
            _out.Name = LocalName;
            Send(slot);
        }

        /// <summary>Send a Leave to every peer (a few redundant copies; the channel is unreliable).</summary>
        public void SendLeave(ChibiLeaveReason reason, int copies = 3)
        {
            for (int s = 0; s < _count; s++) if (_controls[s] != null) SendLeave(s, reason, copies);
        }

        /// <summary>Send a Leave to the peer in roster slot <paramref name="slot"/> only.</summary>
        public void SendLeave(int slot, ChibiLeaveReason reason, int copies = 3)
        {
            if (slot < 0 || slot >= _count || _controls[slot] == null) return;
            _out.Type = ChibiControlType.Leave;
            _out.Reason = (byte)reason;
            for (int i = 0; i < copies; i++) Send(slot);
        }

        void Send(int slot)
        {
            _tx.Clear();
            ChibiControlCodec.Write(_tx, _out);
            _controls[slot].Send(_tx.Bytes, _tx.ByteLength);
        }
    }
}
