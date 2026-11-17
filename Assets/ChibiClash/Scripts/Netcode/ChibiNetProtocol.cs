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
            while (true)
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
        /// <summary>Joiner -> host: my versions and my fighter.</summary>
        Hello = 1,
        /// <summary>Host -> joiner: the complete match config + rollback settings + their hash.</summary>
        Start = 2,
        /// <summary>Joiner -> host: I accept the Start with this hash (both sides then simulate).</summary>
        Ready = 3,
        /// <summary>Either way: refusing to play (version/config mismatch). Carries a <see cref="ChibiRejectReason"/>.</summary>
        Reject = 4,
        /// <summary>Either way: I am leaving the match. Carries a <see cref="ChibiLeaveReason"/>.</summary>
        Leave = 5,
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
        /// <summary>A field outside its legal range (bad class, passive, stage or settings).</summary>
        InvalidConfig = 4,
    }

    /// <summary>Why a peer left a running match.</summary>
    public enum ChibiLeaveReason : byte
    {
        /// <summary>Player quit or closed the game mid-match: the other player wins (decision Q4).</summary>
        Forfeit = 0,
        /// <summary>The match reached its confirmed end; this peer is closing the connection.</summary>
        MatchComplete = 1,
        /// <summary>This peer detected a desync: the match is a no contest.</summary>
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
        /// <summary>
        /// Hello / Start, write side: the sender's chosen player name (cosmetic, optional, NOT part of the match hash).
        /// Only <see cref="ChibiControlCodec.NameAlphabet"/> characters are sent; anything else is dropped.
        /// </summary>
        public string Name = "";
        /// <summary>Hello / Start, read side: received name characters (reused buffer, no allocation per message).</summary>
        public readonly char[] NameChars = new char[ChibiControlCodec.NameMaxChars];
        /// <summary>Number of valid characters in <see cref="NameChars"/> (0 = the peer sent no name, e.g. an older build).</summary>
        public int NameLength;
        /// <summary>The received name as a string ("" when none). Allocates: call once, not per tick.</summary>
        public string ReceivedName() => NameLength == 0 ? "" : new string(NameChars, 0, NameLength);
    }

    /// <summary>
    /// Wire format of control datagrams (bits, LSB first, via <see cref="BitBuffer"/>):
    ///   8 magic 0xC9, 4 type, then
    ///   Hello : 8 protocol, 16 rules, 4 class, 1 passive, [name]
    ///   Start : 8 protocol, 16 rules, 32 seed, 4 class0, 1 passive0, 4 class1, 1 passive1, 4 stage, 1 hazardsOff (protocol 4),
    ///           4 inputDelay, 4 maxPrediction, 16 disconnectTicks, 32 matchHash, [name]
    ///   Ready : 32 matchHash
    ///   Reject/Leave : 8 reason
    ///   [name] (optional trailer, cosmetic): 5 length (0..24), then 6 bits per character, an index into
    ///          NameAlphabet. A datagram that ends before the trailer means "no name" (older builds), so adding
    ///          it did not change the protocol version. A length above 24 is malformed.
    /// Largest (Start + 24-char name) = 139 + 5 + 144 = 288 bits = 36 bytes.
    /// </summary>
    public static class ChibiControlCodec
    {
        public const byte Magic = 0xC9;
        /// <summary>Buffer size for control datagrams.</summary>
        public const int MaxBytes = 48;
        const int ClassBits = 4, StageBits = 4, NameLengthBits = 5, NameCharBits = 6;
        /// <summary>Longest name on the wire (equals ChibiSocialRules.DisplayMax).</summary>
        public const int NameMaxChars = 24;
        /// <summary>
        /// The only characters a name can carry (exactly 64 = 6 bits). No angle brackets, '=', spaces or control
        /// characters, so a hostile peer cannot inject rich-text tags into the HUD through its name.
        /// </summary>
        public const string NameAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_#";

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
                    break;
                case ChibiControlType.Start:
                    b.Write((uint)m.Protocol, 8);
                    b.Write((uint)m.Rules, 16);
                    b.Write(m.Config.Seed, 32);
                    b.Write((uint)m.Config.Class0, ClassBits);
                    b.Write((uint)m.Config.Passive0, 1);
                    b.Write((uint)m.Config.Class1, ClassBits);
                    b.Write((uint)m.Config.Passive1, 1);
                    b.Write((uint)m.Config.Stage, StageBits);
                    b.Write(m.Config.HazardsOff ? 1u : 0u, 1);   // protocol 4 (rules v21, ST-5)
                    b.Write((uint)m.Settings.InputDelay, 4);
                    b.Write((uint)m.Settings.MaxPrediction, 4);
                    b.Write((uint)m.Settings.DisconnectTicks, 16);
                    b.Write(m.MatchHash, 32);
                    WriteName(b, m.Name);
                    break;
                case ChibiControlType.Ready:
                    b.Write(m.MatchHash, 32);
                    break;
                case ChibiControlType.Reject:
                case ChibiControlType.Leave:
                    b.Write(m.Reason, 8);
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
                    break;
                case ChibiControlType.Start:
                    m.Protocol = (int)b.Read(8);
                    m.Rules = (int)b.Read(16);
                    m.Config.Seed = b.Read(32);
                    m.Config.Class0 = (FighterClass)b.Read(ClassBits);
                    m.Config.Passive0 = (int)b.Read(1);
                    m.Config.Class1 = (FighterClass)b.Read(ClassBits);
                    m.Config.Passive1 = (int)b.Read(1);
                    m.Config.Stage = (int)b.Read(StageBits);
                    m.Config.HazardsOff = b.Read(1) != 0;          // protocol 4 (rules v21, ST-5)
                    m.Settings.InputDelay = (int)b.Read(4);
                    m.Settings.MaxPrediction = (int)b.Read(4);
                    m.Settings.DisconnectTicks = (int)b.Read(16);
                    m.MatchHash = b.Read(32);
                    if (b.Overrun || !ReadName(b, m)) return false;
                    break;
                case ChibiControlType.Ready:
                    m.MatchHash = b.Read(32);
                    break;
                case ChibiControlType.Reject:
                case ChibiControlType.Leave:
                    m.Reason = (byte)b.Read(8);
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

        /// <summary>Legal fighter class value (wire values are 4 bits; only 0..4 exist).</summary>
        public static bool ValidClass(FighterClass c) => c >= FighterClass.Melee && c <= FighterClass.Polearm;
    }

    // =====================================================================================================
    // Handshake
    // =====================================================================================================

    /// <summary>Progress of a <see cref="ChibiHandshake"/>.</summary>
    public enum ChibiHandshakeState : byte
    {
        /// <summary>Exchanging Hello / Start / Ready.</summary>
        Negotiating,
        /// <summary>Agreed: <see cref="ChibiHandshake.Config"/> and <see cref="ChibiHandshake.Settings"/> are final. Start simulating.</summary>
        Agreed,
        /// <summary>One side refused (see <see cref="ChibiHandshake.RejectReason"/>).</summary>
        Rejected,
        /// <summary>No agreement within the timeout.</summary>
        TimedOut,
        /// <summary>The peer sent Leave before the match started (cancelled).</summary>
        PeerLeft,
    }

    /// <summary>
    /// Agrees on the match before any input flows. The HOST is the config authority (player index 0): it picks the
    /// seed, stage and rollback settings and adds the joiner's fighter from Hello. Both sides then prove they would
    /// simulate the same match by comparing <see cref="ChibiControlCodec.MatchHash"/>, which covers RulesVersion and
    /// every gameplay constant table, so two builds with different balance data are refused here instead of
    /// desyncing at frame 30 (rollback-gap-report.md 2.3, risks A1-A4).
    ///
    ///   joiner                     host
    ///   Hello (every 10 ticks) --->                       validate versions, complete config
    ///                          <--- Start (on each Hello / every 10 ticks until Ready)
    ///   validate, hash; Agreed
    ///   Ready (on every Start) --->                       hash equal: Agreed
    ///
    /// Every message is idempotent and resent, so it works over the lossy unreliable channel. The joiner keeps
    /// answering Start with Ready after it agreed (call <see cref="Handle"/> during the match too). Pure C#, no allocation per tick.
    /// </summary>
    public sealed class ChibiHandshake
    {
        /// <summary>Resend interval for Hello / Start (ticks).</summary>
        public const int ResendTicks = 10;
        /// <summary>Give up after this long without agreement (10 s: covers Relay connect + a slow peer).</summary>
        public const int DefaultTimeoutTicks = 600;

        readonly bool _isHost;
        readonly IChibiTransport _control;
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
        bool _haveJoiner;

        // joiner side
        readonly FighterClass _myClass;
        readonly int _myPassive;
        /// <summary>SEC-001: the stage the joiner agreed to in the room (-1 = any, e.g. quick play).</summary>
        readonly int _expectStage = -1;
        /// <summary>SEC-001: the hazards switch the joiner agreed to in the room (null = any).</summary>
        readonly bool? _expectHazardsOff;

        ChibiMatchConfig _cfg;
        ChibiRollbackSettings _settings;
        uint _hash;

        public ChibiHandshakeState State { get; private set; } = ChibiHandshakeState.Negotiating;
        /// <summary>Agreed config (valid once <see cref="State"/> is Agreed).</summary>
        public ChibiMatchConfig Config => _cfg;
        /// <summary>Agreed rollback settings (valid once Agreed).</summary>
        public ChibiRollbackSettings Settings => _settings;
        /// <summary>Agreed match hash (valid once Agreed).</summary>
        public uint MatchHash => _hash;
        /// <summary>Sim player index: host 0, joiner 1.</summary>
        public int LocalIndex => _isHost ? 0 : 1;
        /// <summary>Why it was rejected (local or remote refusal).</summary>
        public ChibiRejectReason RejectReason { get; private set; }
        /// <summary>True when the PEER refused us (false when we refused them).</summary>
        public bool RejectedByPeer { get; private set; }
        /// <summary>Our player name as sent to the peer (cosmetic; "" = none).</summary>
        public string LocalName { get; }
        /// <summary>
        /// The peer's player name from its Hello / Start ("" when it sent none). Untrusted but already limited to
        /// <see cref="ChibiControlCodec.NameAlphabet"/>; it never affects the simulation or the match hash.
        /// </summary>
        public string PeerName { get; private set; } = "";

        /// <summary>Host (sim player 0): decides seed, stage and settings.</summary>
        /// <example><code>var hs = ChibiHandshake.ForHost(mux.Control, FighterClass.Melee, 0, stage: 1, seed, ChibiRollbackSettings.Default);</code></example>
        /// <param name="hazardsOff">Rules v20 (ST-5): stage hazards off for this match (host's choice; travels in Start).</param>
        public static ChibiHandshake ForHost(IChibiTransport control, FighterClass myClass, int myPassive, int stage, uint seed,
                                             ChibiRollbackSettings settings, int timeoutTicks = DefaultTimeoutTicks,
                                             string localName = "", bool hazardsOff = false)
            => new ChibiHandshake(true, control, myClass, myPassive, stage, seed, settings, timeoutTicks, localName, hazardsOff);

        /// <summary>Joiner (sim player 1): sends its fighter, accepts the host's config if it hashes the same locally.</summary>
        /// <example><code>var hs = ChibiHandshake.ForJoiner(mux.Control, FighterClass.Magic, 1);</code></example>
        /// <param name="expectStage">SEC-001: the room's stage the joiner agreed to; a Start with another stage is refused
        /// (InvalidConfig). -1 = accept any stage (quick play, where the host's pick is used).</param>
        /// <param name="expectHazardsOff">SEC-001: the room's hazards switch; a Start that differs is refused. null = any.</param>
        public static ChibiHandshake ForJoiner(IChibiTransport control, FighterClass myClass, int myPassive,
                                               int timeoutTicks = DefaultTimeoutTicks, string localName = "",
                                               int expectStage = -1, bool? expectHazardsOff = null)
            => new ChibiHandshake(false, control, myClass, myPassive, 0, 0, default, timeoutTicks, localName, false,
                                  expectStage, expectHazardsOff);

        ChibiHandshake(bool isHost, IChibiTransport control, FighterClass myClass, int myPassive, int stage, uint seed,
                       ChibiRollbackSettings settings, int timeoutTicks, string localName, bool hazardsOff,
                       int expectStage = -1, bool? expectHazardsOff = null)
        {
            _hazardsOff = hazardsOff;
            _expectStage = expectStage;
            _expectHazardsOff = expectHazardsOff;
            _isHost = isHost;
            LocalName = localName ?? "";
            _control = control ?? throw new ArgumentNullException(nameof(control));
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
                if (!_isHost) SendHello();
                else if (_haveJoiner) SendStart();
            }
            if (++_ticks > _timeoutTicks) State = ChibiHandshakeState.TimedOut;
        }

        /// <summary>
        /// Read and act on every waiting control message. During a match the caller routes Leave messages itself, so
        /// this returns the first Leave it sees (or false) and leaves the rest queued for the next call.
        /// </summary>
        /// <param name="leaveReason">Reason of a received Leave.</param>
        public bool Handle(out ChibiLeaveReason leaveReason)
        {
            leaveReason = ChibiLeaveReason.Forfeit;
            while (true)
            {
                int n = _control.Receive(_raw);
                if (n <= 0) return false;
                _rx.LoadFrom(_raw, n);
                if (!ChibiControlCodec.TryRead(_rx, _in)) continue;
                if (_in.Type == ChibiControlType.Leave) { leaveReason = (ChibiLeaveReason)_in.Reason; return true; }
                if (_isHost) OnHostMessage(_in); else OnJoinerMessage(_in);
            }
        }

        /// <summary><see cref="Handle(out ChibiLeaveReason)"/> for the negotiating phase (a Leave there aborts the handshake).</summary>
        public void Handle()
        {
            if (Handle(out _) && State == ChibiHandshakeState.Negotiating) State = ChibiHandshakeState.PeerLeft;
        }

        // ------------------------------------------------------------------ host

        void OnHostMessage(ChibiControlMessage m)
        {
            switch (m.Type)
            {
                case ChibiControlType.Hello:
                    if (State != ChibiHandshakeState.Negotiating) return;   // late duplicate
                    if (m.Protocol != ChibiPacketCodec.ProtocolVersion) { Reject(ChibiRejectReason.ProtocolMismatch); return; }
                    if (m.Rules != ChibiMatchConfig.RulesVersion) { Reject(ChibiRejectReason.RulesMismatch); return; }
                    if (!ChibiControlCodec.ValidClass(m.Class)) { Reject(ChibiRejectReason.InvalidConfig); return; }
                    _cfg = new ChibiMatchConfig
                    {
                        Seed = _seed, Class0 = _hostClass, Passive0 = _hostPassive, Class1 = m.Class, Passive1 = m.Passive,
                        Stage = _stage, Intro = true,   // online matches always play the 2.2 s intro, never training (rules v16)
                        HazardsOff = _hazardsOff,      // rules v21: the host decides (ST-5), sent in Start
                    };
                    _hash = ChibiControlCodec.MatchHash(_cfg, _settings);
                    _haveJoiner = true;
                    PeerName = m.ReceivedName();
                    SendStart();
                    break;
                case ChibiControlType.Ready:
                    if (State != ChibiHandshakeState.Negotiating || !_haveJoiner) return;
                    if (m.MatchHash != _hash) { Reject(ChibiRejectReason.ContentMismatch); return; }
                    State = ChibiHandshakeState.Agreed;
                    break;
                case ChibiControlType.Reject:
                    PeerRejected(m);
                    break;
            }
        }

        // ------------------------------------------------------------------ joiner

        void OnJoinerMessage(ChibiControlMessage m)
        {
            switch (m.Type)
            {
                case ChibiControlType.Start:
                    if (State == ChibiHandshakeState.Agreed)
                    {
                        if (m.MatchHash == _hash) SendReady();   // host missed our Ready: answer again
                        return;
                    }
                    if (State != ChibiHandshakeState.Negotiating) return;
                    if (m.Protocol != ChibiPacketCodec.ProtocolVersion) { Reject(ChibiRejectReason.ProtocolMismatch); return; }
                    if (m.Rules != ChibiMatchConfig.RulesVersion) { Reject(ChibiRejectReason.RulesMismatch); return; }
                    if (!ValidStart(m)) { Reject(ChibiRejectReason.InvalidConfig); return; }
                    // Online flags are fixed by protocol, not sent: intro on, training off (rules v16).
                    var cfg = m.Config;
                    cfg.Intro = true;
                    cfg.Training = false;
                    // Recompute with OUR constant tables: equal only if both builds would simulate the same match.
                    if (ChibiControlCodec.MatchHash(cfg, m.Settings) != m.MatchHash) { Reject(ChibiRejectReason.ContentMismatch); return; }
                    _cfg = cfg;
                    _settings = m.Settings;
                    _hash = m.MatchHash;
                    PeerName = m.ReceivedName();
                    State = ChibiHandshakeState.Agreed;
                    SendReady();
                    break;
                case ChibiControlType.Reject:
                    PeerRejected(m);
                    break;
            }
        }

        bool ValidStart(ChibiControlMessage m)
        {
            if (!ChibiControlCodec.ValidClass(m.Config.Class0) || !ChibiControlCodec.ValidClass(m.Config.Class1)) return false;
            if (m.Config.Class1 != _myClass || m.Config.Passive1 != _myPassive) return false;   // host must echo our pick
            if (m.Config.Stage < 0 || m.Config.Stage >= ChibiStageTable.Count) return false;   // rules v21: stages 0..7
            // SEC-001: in a room the joiner agreed to a stage and a hazards switch; the host cannot swap them in Start.
            if (_expectStage >= 0 && m.Config.Stage != _expectStage) return false;
            if (_expectHazardsOff.HasValue && m.Config.HazardsOff != _expectHazardsOff.Value) return false;
            try { m.Settings.Validate(); } catch (ArgumentOutOfRangeException) { return false; }
            return true;
        }

        // ------------------------------------------------------------------ send helpers

        void Reject(ChibiRejectReason r)
        {
            RejectReason = r;
            RejectedByPeer = false;
            State = ChibiHandshakeState.Rejected;
            _out.Type = ChibiControlType.Reject;
            _out.Reason = (byte)r;
            for (int i = 0; i < 3; i++) Send();   // no resend loop after rejecting: send a few copies
        }

        void PeerRejected(ChibiControlMessage m)
        {
            if (State == ChibiHandshakeState.Agreed) return;
            RejectReason = (ChibiRejectReason)m.Reason;
            RejectedByPeer = true;
            State = ChibiHandshakeState.Rejected;
        }

        void SendHello()
        {
            _out.Type = ChibiControlType.Hello;
            _out.Protocol = ChibiPacketCodec.ProtocolVersion;
            _out.Rules = ChibiMatchConfig.RulesVersion;
            _out.Class = _myClass;
            _out.Passive = _myPassive;
            _out.Name = LocalName;
            Send();
        }

        void SendStart()
        {
            _out.Type = ChibiControlType.Start;
            _out.Protocol = ChibiPacketCodec.ProtocolVersion;
            _out.Rules = ChibiMatchConfig.RulesVersion;
            _out.Config = _cfg;
            _out.Settings = _settings;
            _out.MatchHash = _hash;
            _out.Name = LocalName;
            Send();
        }

        void SendReady()
        {
            _out.Type = ChibiControlType.Ready;
            _out.MatchHash = _hash;
            Send();
        }

        /// <summary>Send a Leave (a few redundant copies; the channel is unreliable).</summary>
        public void SendLeave(ChibiLeaveReason reason, int copies = 3)
        {
            _out.Type = ChibiControlType.Leave;
            _out.Reason = (byte)reason;
            for (int i = 0; i < copies; i++) Send();
        }

        void Send()
        {
            _tx.Clear();
            ChibiControlCodec.Write(_tx, _out);
            _control.Send(_tx.Bytes, _tx.ByteLength);
        }
    }
}
