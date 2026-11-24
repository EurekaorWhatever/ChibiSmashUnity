using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Host side of the protocol 6 handshake for 2-4 players over the star (four-player-design.md 2.3). Every guest runs
    /// the ordinary joiner (<see cref="ChibiHandshake.ForJoiner"/>) on its link to the host.
    ///
    ///   guest g                     host
    ///   Hello (every 10 ticks) ---> record class / passive / name of seat g
    ///                               once EVERY seat said Hello: config (seed, stage, mode, teams, 4 seats) + hash
    ///                          <--- Start (your slot = g) + Roster (every name), every 10 ticks until Ready
    ///   validate own seat, hash
    ///   Ready ------------------->  hash equal: seat g ready. All seats ready = Agreed.
    ///
    /// The host is the config authority (seed, stage, teams); every guest checks its own seat and the hash, which covers
    /// RulesVersion and every gameplay table. Pure C#, no allocation per tick (a Hello allocates its name string once).
    /// </summary>
    public sealed class ChibiGroupHandshake
    {
        readonly IChibiTransport[] _controls;   // by slot; [0] unused
        readonly int _players;
        readonly BitBuffer _tx = new BitBuffer(ChibiControlCodec.MaxBytes);
        readonly BitBuffer _rx = new BitBuffer(ChibiControlCodec.MaxBytes);
        readonly ChibiControlMessage _out = new ChibiControlMessage(), _in = new ChibiControlMessage();
        readonly byte[] _raw = new byte[ChibiChannelMux.MaxDatagram];
        readonly int _timeoutTicks;
        readonly FighterClass _hostClass;
        readonly int _hostPassive, _stage, _teamOf;
        readonly ChibiMatchMode _mode;
        readonly bool _hazardsOff;
        readonly uint _seed;
        readonly ChibiRollbackSettings _settings;
        readonly bool[] _hello = new bool[ChibiSimState.MaxFighters];
        readonly bool[] _ready = new bool[ChibiSimState.MaxFighters];
        readonly FighterClass[] _class = new FighterClass[ChibiSimState.MaxFighters];
        readonly int[] _passive = new int[ChibiSimState.MaxFighters];
        readonly string[] _names = { "", "", "", "" };   // by seat
        readonly string[] _seatKeys;                    // SEC-003: published key per seat (null = no binding)
        readonly int[] _seatOf = new int[ChibiSimState.MaxFighters];   // connection -> seat (0 = not yet)
        readonly int[] _connOf = new int[ChibiSimState.MaxFighters];   // seat -> connection (0 = not yet)
        bool _configured;
        int _ticks;
        ChibiMatchConfig _cfg;
        uint _hash;

        /// <summary>Progress (same states as the 1v1 handshake).</summary>
        public ChibiHandshakeState State { get; private set; } = ChibiHandshakeState.Negotiating;
        /// <summary>Agreed config (valid once Agreed).</summary>
        public ChibiMatchConfig Config => _cfg;
        /// <summary>Agreed rollback settings.</summary>
        public ChibiRollbackSettings Settings => _settings;
        /// <summary>Agreed match hash.</summary>
        public uint MatchHash => _hash;
        /// <summary>The host is always slot 0.</summary>
        public int LocalIndex => 0;
        /// <summary>Players in the match (2..4).</summary>
        public int Players => _players;
        /// <summary>Why it was rejected.</summary>
        public ChibiRejectReason RejectReason { get; private set; }
        /// <summary>Slot whose refusal or bad Hello ended the handshake (-1 = none).</summary>
        public int RejectedSlot { get; private set; } = -1;
        /// <summary>Our player name.</summary>
        public string LocalName { get; }
        /// <summary>Name of a seat ("" = unknown). Untrusted, alphabet-filtered: draw through ChibiSocialRules.PeerDisplayName.</summary>
        public string PlayerName(int slot) => slot == 0 ? LocalName : (slot > 0 && slot < _players ? _names[slot] : "");

        /// <summary>
        /// Sim slot of the guest on connection <paramref name="connection"/> (1..players-1): its room seat when seat keys
        /// were given (SEC-003 / L2), else the connection index. 0 until its Hello was accepted.
        /// </summary>
        public int SlotOf(int connection) => connection > 0 && connection < _players ? _seatOf[connection] : 0;

        /// <summary>
        /// Host of a 2-4 player match. <paramref name="controls"/>[g] is the control channel of guest slot g (1..players-1).
        /// </summary>
        /// <example><code>var hs = new ChibiGroupHandshake(new[] { null, c1, c2, c3 }, 4, FighterClass.Melee, 0, 3, seed, settings);</code></example>
        public ChibiGroupHandshake(IChibiTransport[] controls, int players, FighterClass myClass, int myPassive, int stage,
                                   uint seed, ChibiRollbackSettings settings, ChibiMatchMode mode = ChibiMatchMode.FreeForAll,
                                   int teamOf = 0, int timeoutTicks = ChibiHandshake.DefaultTimeoutTicks, string localName = "",
                                   bool hazardsOff = false, string[] seatKeys = null)
        {
            // SEC-003: in a room each seat's published key; a Hello is accepted only for an untaken seat whose key matches.
            if (seatKeys != null)
            {
                _seatKeys = new string[ChibiSimState.MaxFighters];
                for (int s = 0; s < _seatKeys.Length; s++) _seatKeys[s] = s < seatKeys.Length ? ChibiRoomRules.CleanSeatKey(seatKeys[s]) : "";
            }
            if (players < 2 || players > ChibiSimState.MaxFighters) throw new ArgumentOutOfRangeException(nameof(players));
            if (controls == null || controls.Length < players) throw new ArgumentException("one control per seat", nameof(controls));
            for (int g = 1; g < players; g++) if (controls[g] == null) throw new ArgumentNullException(nameof(controls), "seat " + g);
            if (!ChibiControlCodec.ValidClass(myClass)) throw new ArgumentOutOfRangeException(nameof(myClass));
            settings.Validate();
            _controls = controls;
            _players = players;
            _hostClass = myClass;
            _hostPassive = myPassive == 0 ? 0 : 1;
            _stage = stage;
            _seed = seed;
            _settings = settings;
            _mode = mode;
            _teamOf = mode == ChibiMatchMode.Team ? teamOf & ((1 << players) - 1) : 0;
            _timeoutTicks = timeoutTicks;
            _hazardsOff = hazardsOff;
            LocalName = localName ?? "";
        }

        /// <summary>One 60 Hz tick: read every guest's control messages, resend Start / Roster, check the timeout.</summary>
        public void Tick()
        {
            Handle();
            if (State != ChibiHandshakeState.Negotiating) return;
            if (_configured && _ticks % ChibiHandshake.ResendTicks == 0)
                for (int g = 1; g < _players; g++) if (!_ready[g]) { SendStart(g); SendRoster(g); }
            if (++_ticks > _timeoutTicks) State = ChibiHandshakeState.TimedOut;
        }

        /// <summary>Negotiating phase: read every channel; a Leave from any guest cancels the handshake.</summary>
        public void Handle()
        {
            for (int g = 1; g < _players; g++)
                if (Handle(g, out _) && State == ChibiHandshakeState.Negotiating) State = ChibiHandshakeState.PeerLeft;
        }

        /// <summary>
        /// Read guest <paramref name="slot"/>'s control channel. Returns true at the first Leave (its reason out), leaving the
        /// rest queued. During the match this keeps answering late Ready / Hello duplicates.
        /// </summary>
        public bool Handle(int slot, out ChibiLeaveReason leaveReason)
        {
            leaveReason = ChibiLeaveReason.Forfeit;
            if (slot <= 0 || slot >= _players) return false;
            var c = _controls[slot];
            while (true)
            {
                int n = c.Receive(_raw);
                if (n <= 0) return false;
                _rx.LoadFrom(_raw, n);
                if (!ChibiControlCodec.TryRead(_rx, _in)) continue;
                if (_in.Type == ChibiControlType.Leave) { leaveReason = (ChibiLeaveReason)_in.Reason; return true; }
                OnMessage(slot, _in);
            }
        }

        void OnMessage(int g, ChibiControlMessage m)
        {
            switch (m.Type)
            {
                case ChibiControlType.Hello:
                    if (State != ChibiHandshakeState.Negotiating || _hello[g]) return;   // late duplicate
                    if (m.Protocol != ChibiPacketCodec.ProtocolVersion) { Reject(g, ChibiRejectReason.ProtocolMismatch); return; }
                    if (m.Rules != ChibiMatchConfig.RulesVersion) { Reject(g, ChibiRejectReason.RulesMismatch); return; }
                    if (!ChibiControlCodec.ValidClass(m.Class)) { Reject(g, ChibiRejectReason.InvalidConfig); return; }
                    int seat = g;
                    if (_seatKeys != null)
                    {
                        // SEC-003 / L2: the connection takes the seat whose key its secret hashes to (each seat once);
                        // anything else (a stranger, a player who left, a second connection of one player) is refused.
                        string key = ChibiRoomRules.SeatKey(m.Secret);
                        seat = 0;
                        for (int s = 1; s < _players && key.Length > 0; s++) if (_seatKeys[s] == key && _connOf[s] == 0) { seat = s; break; }
                        if (seat == 0) { Reject(g, ChibiRejectReason.InvalidConfig); return; }
                    }
                    _hello[g] = true;
                    _seatOf[g] = seat;
                    _connOf[seat] = g;
                    _class[seat] = m.Class;
                    _passive[seat] = m.Passive == 0 ? 0 : 1;
                    _names[seat] = m.ReceivedName();
                    TryConfigure();
                    break;
                case ChibiControlType.Ready:
                    if (!_configured) return;
                    if (m.MatchHash != _hash) { if (State == ChibiHandshakeState.Negotiating) Reject(g, ChibiRejectReason.ContentMismatch); return; }
                    if (_ready[g]) return;
                    _ready[g] = true;
                    SendRoster(g);
                    if (State != ChibiHandshakeState.Negotiating) return;
                    bool all = true;
                    for (int s = 1; s < _players; s++) all &= _ready[s];
                    if (all) State = ChibiHandshakeState.Agreed;
                    break;
                case ChibiControlType.Reject:
                    if (State == ChibiHandshakeState.Agreed) return;
                    RejectReason = (ChibiRejectReason)m.Reason;
                    RejectedSlot = g;
                    State = ChibiHandshakeState.Rejected;
                    break;
            }
        }

        void TryConfigure()
        {
            for (int g = 1; g < _players; g++) if (!_hello[g]) return;
            _cfg = new ChibiMatchConfig
            {
                Seed = _seed, Stage = _stage, HazardsOff = _hazardsOff,
                Intro = true,   // online matches always play the intro, never training (rules v16)
                PlayerCount = _players == 2 ? 0 : _players, Mode = _mode, TeamOf = _teamOf,
                Class0 = _hostClass, Passive0 = _hostPassive,
                Class1 = _class[1], Passive1 = _passive[1],
                Class2 = _players > 2 ? _class[2] : FighterClass.Melee, Passive2 = _players > 2 ? _passive[2] : 0,
                Class3 = _players > 3 ? _class[3] : FighterClass.Melee, Passive3 = _players > 3 ? _passive[3] : 0,
            };
            _hash = ChibiControlCodec.MatchHash(_cfg, _settings);
            _configured = true;
            for (int g = 1; g < _players; g++) { SendStart(g); SendRoster(g); }
        }

        void Reject(int g, ChibiRejectReason r)
        {
            RejectReason = r;
            RejectedSlot = g;
            State = ChibiHandshakeState.Rejected;
            _out.Type = ChibiControlType.Reject;
            _out.Reason = (byte)r;
            for (int s = 1; s < _players; s++) for (int i = 0; i < 3; i++) Send(s);   // everyone: the match will not start
        }

        void SendStart(int g)
        {
            _out.Type = ChibiControlType.Start;
            _out.Protocol = ChibiPacketCodec.ProtocolVersion;
            _out.Rules = ChibiMatchConfig.RulesVersion;
            _out.Config = _cfg;
            _out.Settings = _settings;
            _out.MatchHash = _hash;
            _out.Name = LocalName;
            _out.Slot = _seatOf[g];
            Send(g);
        }

        void SendRoster(int g)
        {
            if (!_configured) return;
            _out.Type = ChibiControlType.Roster;
            _out.MatchHash = _hash;
            _out.RosterCount = _players;
            for (int s = 0; s < _players; s++) _out.RosterOut[s] = PlayerName(s);
            Send(g);
        }

        /// <summary>Send a Leave to every guest (a few redundant copies; the channel is unreliable).</summary>
        public void SendLeave(ChibiLeaveReason reason, int copies = 3)
        {
            for (int g = 1; g < _players; g++) SendLeaveTo(g, reason, copies);
        }

        /// <summary>Send a Leave to one guest.</summary>
        public void SendLeaveTo(int slot, ChibiLeaveReason reason, int copies = 3)
        {
            if (slot <= 0 || slot >= _players) return;
            _out.Type = ChibiControlType.Leave;
            _out.Reason = (byte)reason;
            for (int i = 0; i < copies; i++) Send(slot);
        }

        void Send(int g)
        {
            _tx.Clear();
            ChibiControlCodec.Write(_tx, _out);
            _controls[g].Send(_tx.Bytes, _tx.ByteLength);
        }
    }
}
