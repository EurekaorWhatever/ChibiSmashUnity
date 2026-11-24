using System;
using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>Lifecycle of an online match.</summary>
    public enum ChibiOnlinePhase : byte
    {
        /// <summary>Agreeing on config / versions (no simulation yet).</summary>
        Handshaking,
        /// <summary>Rollback session running.</summary>
        Playing,
        /// <summary>Result is final; still ticking briefly so the peer can confirm its last frames, then saying goodbye.</summary>
        Lingering,
        /// <summary>Done. The transport may be disposed.</summary>
        Closed,
    }

    /// <summary>What the local player gets.</summary>
    public enum ChibiMatchOutcome : byte
    {
        None,
        Win,
        Loss,
        Draw,
        /// <summary>Nobody wins or loses, no rewards (desync; user decision 2026-11-09).</summary>
        NoContest,
        /// <summary>The match never started (handshake failed or was cancelled).</summary>
        Aborted,
    }

    /// <summary>Why the match ended.</summary>
    public enum ChibiEndCause : byte
    {
        None,
        /// <summary>KO or timeout inside the simulation, confirmed by both peers' inputs.</summary>
        MatchOver,
        /// <summary>Opponent quit / forfeited (Leave Forfeit): local player wins (decision Q4).</summary>
        OpponentLeft,
        /// <summary>Opponent went silent past the disconnect timeout: local player wins (Q4).</summary>
        OpponentTimedOut,
        /// <summary>The transport lost the connection mid-match: local player wins (Q4).</summary>
        ConnectionLost,
        /// <summary>We forfeited.</summary>
        LocalForfeit,
        /// <summary>The two simulations disagreed (checksum), detected here or reported by the peer.</summary>
        Desync,
        /// <summary>No longer produced (SEC-003, protocol 5): an end we cannot confirm ourselves is scored as a disconnect.</summary>
        UnconfirmedEnd,
        /// <summary>Handshake rejected / timed out / cancelled.</summary>
        HandshakeFailed,
        /// <summary>
        /// The session started here but the peer never sent one gameplay packet (e.g. the host never saw our Ready):
        /// no contest, so nobody collects a win for a match that was never played (NET-03).
        /// </summary>
        PeerNeverStarted,
        /// <summary>
        /// The peer kept playing, but so slowly that the match was stalled for more than two thirds of the stall window: no
        /// contest (decision 2026-11-16). A slow link and a deliberate drip-feed look the same, and two honest players
        /// on a bad connection must not both record a win.
        /// </summary>
        OpponentStalled,
        /// <summary>3-4 players, guest: the host left, timed out or lost the link. No contest (star: nobody relays any more).</summary>
        HostLeft,
        /// <summary>3-4 players, guest: the host announced that WE left (our link was too slow or silent). A loss.</summary>
        Dropped,
    }

    /// <summary>Final result of an online match, from the local player's point of view.</summary>
    public struct ChibiOnlineResult
    {
        public ChibiMatchOutcome Outcome;
        public ChibiEndCause Cause;
        /// <summary>Sim index of the winner, -1 for draw / no contest / aborted (3-4 players: the lowest winning slot).</summary>
        public int Winner;
        /// <summary>Bit per winning player once the match is over in the simulation (0 = draw / none).</summary>
        public int WinnerMask;
        /// <summary>Our finish place (1 = first) when the simulation ended the match, else 0.</summary>
        public int Place;
        /// <summary>Simulated frame when the result was decided.</summary>
        public int Frame;
        /// <summary>Handshake detail when <see cref="Cause"/> is HandshakeFailed.</summary>
        public ChibiHandshakeState HandshakeState;
        public ChibiRejectReason RejectReason;

        public override string ToString() =>
            Outcome + " (" + Cause + (Cause == ChibiEndCause.HandshakeFailed ? ": " + HandshakeState + "/" + RejectReason : "") + ") @" + Frame;
    }

    /// <summary>
    /// One online match from connection to goodbye, transport-agnostic and pure C# (headless testable over
    /// <see cref="ChibiLoopbackLink"/>). Call <see cref="Tick"/> at 60 Hz with <see cref="TimeDilation"/> applied.
    ///
    /// Result rules
    ///   * The simulation's end (KO / timer) counts only once its frame is CONFIRMED (both inputs final). A predicted
    ///     KO can still be undone by a rollback, so it never decides a result.
    ///   * Opponent forfeits, goes silent past the timeout or the transport drops: local player wins (decision Q4).
    ///     A peer that only sends old frames again counts as silent (NET-01).
    ///   * A peer that keeps the match stalled for more than two thirds of the stall window: no contest (NET-01, 2026-11-16).
    ///   * Leave(MatchComplete) never decides a result (SEC-003). Either our own CONFIRMED state shows the end (then it
    ///     is a normal MatchOver), or the peer's drop is scored like any other disconnect. It used to give a no contest,
    ///     which let a losing peer erase the loss by claiming "complete" and leaving.
    ///   * A peer that never delivered a single real input frame: no contest (NET-03). Known limit: a joiner can use
    ///     this to dodge a matchup after seeing the host's pick in Start (it costs the host a win, not a loss).
    ///   * Any checksum mismatch (ours or reported by the peer): no contest (decision 2026-11-09). Nobody can tell whose
    ///     simulation is right with two peers, so a cheater cannot convert a desync into a win.
    ///   * Honest limit (P2P, no authority): if the network dies for both players at once, both see "opponent
    ///     disconnected". Ranked play would need a server-side replay check (netcode-architecture.md 7.6).
    ///   * Known limit (SEC-003 remainder): a losing peer can still force a no contest by sending Leave(Desync) or a
    ///     wrong checksum. Without an authority nobody can tell a real desync from a faked one.
    /// </summary>
    public sealed class ChibiOnlineMatch : IDisposable
    {
        /// <summary>Ticks to keep the session alive after a confirmed end so the peer can confirm too (1.5 s).</summary>
        public const int LingerTicks = 90;

        readonly ChibiChannelMux _mux;               // guest / 1v1 host: the one link
        readonly ChibiHandshake _handshake;          // null for a group host
        readonly ChibiGroupHandshake _group;         // host of a 3-4 player (or star 1v1) match, else null
        readonly ChibiChannelMux[] _muxes;           // group host: one per guest slot ([0] null)
        readonly IChibiLinkStatus[] _links;          // group host: per guest slot
        readonly IChibiPumpedTransport _pump;
        readonly IDisposable _owner;
        readonly IChibiInputSource _input;
        readonly IChibiLinkStatus _link;
        int _linger;
        bool _desyncFlag;
        ChibiDisconnectReason? _disconnect;

        public ChibiOnlinePhase Phase { get; private set; } = ChibiOnlinePhase.Handshaking;
        /// <summary>The rollback session once Playing (null while handshaking).</summary>
        public ChibiRollbackSession Session { get; private set; }
        /// <summary>Confirmed inputs of the whole match (null until Playing). Save it on a desync for the bug report.</summary>
        public ChibiReplayRecorder Replay { get; private set; }
        public ChibiHandshake Handshake => _handshake;
        /// <summary>Group host handshake (null unless created by <see cref="HostGroup"/>).</summary>
        public ChibiGroupHandshake Group => _group;
        /// <summary>The agreed config (valid once Playing; the host knows it once every Hello arrived).</summary>
        public ChibiMatchConfig Config => _group != null ? _group.Config : _handshake.Config;
        /// <summary>Match hash (0 until known).</summary>
        public uint MatchHash => _group != null ? _group.MatchHash : _handshake.MatchHash;
        /// <summary>True when a peer refused us (false when we refused, or nobody did).</summary>
        public bool RejectedByPeer => _group == null && _handshake.RejectedByPeer;
        /// <summary>Players in the match once agreed (2 before).</summary>
        public int Players => Session != null ? Session.Players : (_group != null ? _group.Players : 2);
        /// <summary>
        /// Raw received name of a seat ("" = unknown); untrusted, alphabet-filtered. Draw it only through
        /// ChibiSocialRules.PeerDisplayName.
        /// </summary>
        public string PlayerName(int slot) => _group != null ? _group.PlayerName(slot) : _handshake.PlayerName(slot);
        /// <summary>(slot, frame): a player left the running match and was eliminated from that frame (3-4 players).</summary>
        public event Action<int, int> OnPlayerLeft;
        public ChibiOnlineResult Result { get; private set; }
        /// <summary>True once <see cref="Result"/> is final.</summary>
        public bool HasResult => Result.Outcome != ChibiMatchOutcome.None;
        /// <summary>Sim index of this peer (host 0, joiner its slot).</summary>
        public int LocalIndex => _group != null ? 0 : _handshake.LocalIndex;
        public float TimeDilation => Phase == ChibiOnlinePhase.Playing && Session != null ? Session.TimeDilation : 1f;
        /// <summary>Datagrams the mux dropped (diagnostics).</summary>
        public int MuxDropped
        {
            get
            {
                if (_muxes == null) return _mux.Dropped;
                int d = 0;
                foreach (var m in _muxes) if (m != null) d += m.Dropped;
                return d;
            }
        }

        /// <summary>Raised once when the handshake agrees and the session exists.</summary>
        public event Action<ChibiOnlineMatch> OnStarted;
        /// <summary>Raised once when the result becomes final.</summary>
        public event Action<ChibiOnlineResult> OnResult;
        /// <summary>Raised after every Playing/Lingering tick that advanced the simulation.</summary>
        public event Action<int> OnAdvanced;

        ChibiOnlineMatch(IChibiTransport raw, Func<IChibiTransport, ChibiHandshake> handshake, IChibiInputSource input)
        {
            _mux = new ChibiChannelMux(raw);
            _handshake = handshake(_mux.Control);
            _input = input ?? throw new ArgumentNullException(nameof(input));
            _link = raw as IChibiLinkStatus;
            _pump = raw as IChibiPumpedTransport;
            _owner = raw as IDisposable;
        }

        ChibiOnlineMatch(IChibiTransport[] guests, int players, Func<IChibiTransport[], ChibiGroupHandshake> handshake,
                         IChibiInputSource input, IChibiPumpedTransport pump, IDisposable owner)
        {
            if (guests == null || guests.Length < players) throw new ArgumentException("one link per seat", nameof(guests));
            _muxes = new ChibiChannelMux[ChibiSimState.MaxFighters];
            _links = new IChibiLinkStatus[ChibiSimState.MaxFighters];
            var controls = new IChibiTransport[ChibiSimState.MaxFighters];
            for (int g = 1; g < players; g++)
            {
                _muxes[g] = new ChibiChannelMux(guests[g] ?? throw new ArgumentNullException(nameof(guests), "seat " + g));
                _links[g] = guests[g] as IChibiLinkStatus;
                controls[g] = _muxes[g].Control;
            }
            _group = handshake(controls);
            _input = input ?? throw new ArgumentNullException(nameof(input));
            _pump = pump;
            _owner = owner;
        }

        /// <summary>
        /// Host of a 2-4 player match over the star (protocol 6): <paramref name="guests"/>[g] is the link to guest slot g
        /// (1..players-1; [0] unused). <paramref name="pump"/> services a shared driver around each tick; <paramref name="owner"/>
        /// is disposed with the match (both may be null, e.g. loopback links).
        /// </summary>
        /// <example><code>var m = ChibiOnlineMatch.HostGroup(new[] { null, l1, l2, l3 }, 4, input, FighterClass.Melee, 0, 3, seed, settings);</code></example>
        public static ChibiOnlineMatch HostGroup(IChibiTransport[] guests, int players, IChibiInputSource input, FighterClass myClass,
                                                 int myPassive, int stage, uint seed, ChibiRollbackSettings settings,
                                                 ChibiMatchMode mode = ChibiMatchMode.FreeForAll, int teamOf = 0,
                                                 int handshakeTimeoutTicks = ChibiHandshake.DefaultTimeoutTicks, string localName = "",
                                                 bool hazardsOff = false, IChibiPumpedTransport pump = null, IDisposable owner = null,
                                                 string[] seatKeys = null)
            => new ChibiOnlineMatch(guests, players,
                c => new ChibiGroupHandshake(c, players, myClass, myPassive, stage, seed, settings, mode, teamOf,
                                             handshakeTimeoutTicks, localName, hazardsOff, seatKeys), input, pump, owner);

        /// <summary>Rollback settings for a match of <paramref name="players"/> (design 2.2: the star adds a hop for 3-4).</summary>
        public static ChibiRollbackSettings SettingsFor(int players)
        {
            var s = ChibiRollbackSettings.Default;
            if (players > 2) { s.InputDelay = ChibiRollbackSession.GroupInputDelay; s.MaxPrediction = ChibiRollbackSession.GroupMaxPrediction; }
            return s;
        }

        /// <summary>Host side (sim player 0). The host decides seed, stage and rollback settings.</summary>
        /// <example><code>var m = ChibiOnlineMatch.Host(transport, input, FighterClass.Melee, 0, stage: 0, seed, ChibiRollbackSettings.Default);</code></example>
        /// <param name="hazardsOff">Rules v20 (ST-5): the host's hazards switch, sent in the handshake Start message.</param>
        public static ChibiOnlineMatch Host(IChibiTransport transport, IChibiInputSource input, FighterClass myClass, int myPassive,
                                            int stage, uint seed, ChibiRollbackSettings settings,
                                            int handshakeTimeoutTicks = ChibiHandshake.DefaultTimeoutTicks, string localName = "",
                                            bool hazardsOff = false, string expectKey = null)
            => new ChibiOnlineMatch(transport,
                c => ChibiHandshake.ForHost(c, myClass, myPassive, stage, seed, settings, handshakeTimeoutTicks, localName,
                                            hazardsOff, expectKey), input);

        /// <summary>Joiner side (sim player 1).</summary>
        /// <example><code>var m = ChibiOnlineMatch.Join(transport, input, FighterClass.Magic, 1);</code></example>
        /// <param name="expectStage">SEC-001: room stage the joiner agreed to (-1 = any).</param>
        /// <param name="expectHazardsOff">SEC-001: room hazards switch the joiner agreed to (null = any).</param>
        public static ChibiOnlineMatch Join(IChibiTransport transport, IChibiInputSource input, FighterClass myClass, int myPassive,
                                            int handshakeTimeoutTicks = ChibiHandshake.DefaultTimeoutTicks, string localName = "",
                                            int expectStage = -1, bool? expectHazardsOff = null, int expectPlayers = -1,
                                            ulong secret = 0)
            => new ChibiOnlineMatch(transport, c => ChibiHandshake.ForJoiner(c, myClass, myPassive, handshakeTimeoutTicks, localName,
                                                                             expectStage, expectHazardsOff, expectPlayers, secret), input);

        // ================================================================== tick

        /// <summary>One 60 Hz tick. Returns true when the simulation advanced this tick.</summary>
        public bool Tick()
        {
            // A real transport (Unity Transport / Relay) is serviced around the tick: receive first, flush sends last.
            var pumped = _pump;
            pumped?.Pump();
            bool advanced;
            switch (Phase)
            {
                case ChibiOnlinePhase.Handshaking: TickHandshake(); advanced = false; break;
                case ChibiOnlinePhase.Playing: advanced = TickPlaying(); break;
                case ChibiOnlinePhase.Lingering: advanced = TickLingering(); break;
                default: advanced = false; break;
            }
            pumped?.Flush();
            return advanced;
        }

        void TickHandshake()
        {
            if (_link != null && _link.LinkState == ChibiLinkState.Lost)
            {
                Abort(ChibiHandshakeState.TimedOut);
                return;
            }
            if (_group != null)
            {
                for (int g = 1; g < _group.Players; g++)
                    if (_links[g] != null && _links[g].LinkState == ChibiLinkState.Lost)
                    {
                        _group.SendLeave(ChibiLeaveReason.Forfeit);   // the match will not start: release everyone
                        Abort(ChibiHandshakeState.TimedOut);
                        return;
                    }
                _group.Tick();
                switch (_group.State)
                {
                    case ChibiHandshakeState.Agreed: StartSession(); break;
                    case ChibiHandshakeState.Negotiating: break;
                    default: _group.SendLeave(ChibiLeaveReason.Forfeit); Abort(_group.State); break;
                }
                return;
            }
            _handshake.Tick();
            switch (_handshake.State)
            {
                case ChibiHandshakeState.Agreed: StartSession(); break;
                case ChibiHandshakeState.Negotiating: break;
                default: Abort(_handshake.State); break;
            }
        }

        void StartSession()
        {
            if (_group != null)
            {
                var links = new IChibiTransport[ChibiSimState.MaxFighters];
                for (int c = 1; c < _group.Players; c++) links[_group.SlotOf(c)] = _muxes[c].Session;   // L2: slot = room seat
                Session = new ChibiRollbackSession(0, _group.Config, links, _input, _group.Settings);
            }
            else Session = new ChibiRollbackSession(_handshake.LocalIndex, _handshake.Config, _mux.Session, _input, _handshake.Settings);
            var cfg = _group != null ? _group.Config : _handshake.Config;
            if (Session.Players == 2)   // the replay file format holds 2 streams
            {
                Replay = new ChibiReplayRecorder(cfg);
                Session.OnFrameConfirmed += Replay.Add;
            }
            Session.OnDesync += (f, l, r) => _desyncFlag = true;
            Session.OnDisconnect += reason => _disconnect = reason;
            Session.OnPlayerLeft += (s, f) => OnPlayerLeft?.Invoke(s, f);
            Phase = ChibiOnlinePhase.Playing;
            OnStarted?.Invoke(this);
        }

        bool TickPlaying() => Session.Players == 2 ? TickPair() : _group != null ? TickGroupHost() : TickGroupGuest();

        /// <summary>Next Leave from the one opponent of a 1v1 (a group host with one guest reads seat 1).</summary>
        bool HandlePair(out ChibiLeaveReason leave) =>
            _group != null ? _group.Handle(1, out leave) : _handshake.Handle(out leave);

        /// <summary>Host of a star match: a guest that quits, loses its link or times out is eliminated; the match goes on.</summary>
        bool TickGroupHost()
        {
            for (int c = 1; c < _group.Players; c++)
            {
                int slot = _group.SlotOf(c);   // connection c plays this seat (L2)
                if (_links[c] != null && _links[c].LinkState == ChibiLinkState.Lost) Session.DeclareLeft(slot);
                while (_group.Handle(c, out var leave))
                {
                    // Every Leave reason is an unauthenticated claim of ONE guest, so it can only ever affect that guest:
                    //  - MatchComplete decides nothing (M2, SEC-003 rule): an honest guest says it only after the confirmed end.
                    //  - Desync cannot be checked by the host: that guest is dropped, the match goes on (M3 / SEC-002). A real
                    //    desync is found by the host's own checksum comparison (Session.OnDesync).
                    //  - Forfeit / Stalled: that seat is gone.
                    if (leave != ChibiLeaveReason.MatchComplete) Session.DeclareLeft(slot);
                }
            }
            bool advanced = Session.Tick();
            if (advanced) OnAdvanced?.Invoke(Session.Frame);
            if (_desyncFlag)
            {
                _group.SendLeave(ChibiLeaveReason.Desync);
                Finish(ChibiMatchOutcome.NoContest, ChibiEndCause.Desync, -1, close: true);
            }
            else if (Session.ConfirmedState is ChibiSimState confirmed && confirmed.MatchOver) FinishFromState(confirmed);
            return advanced;
        }

        /// <summary>Guest of a 3-4 player star match: the host is the only link, so losing it is a no contest.</summary>
        bool TickGroupGuest()
        {
            if (_link != null && _link.LinkState == ChibiLinkState.Lost) Session.NotifyDisconnected(ChibiDisconnectReason.TransportLost);
            while (_handshake.Handle(out var leave))
            {
                switch (leave)
                {
                    case ChibiLeaveReason.Desync: _desyncFlag = true; break;
                    case ChibiLeaveReason.MatchComplete: break;   // SEC-003: decides nothing
                    default: Session.NotifyDisconnected(ChibiDisconnectReason.RemoteLeft); break;
                }
            }
            bool advanced = Session.Tick();
            if (advanced) OnAdvanced?.Invoke(Session.Frame);
            if (_desyncFlag)
            {
                _handshake.SendLeave(ChibiLeaveReason.Desync);
                Finish(ChibiMatchOutcome.NoContest, ChibiEndCause.Desync, -1, close: true);
            }
            else if (Session.ConfirmedState is ChibiSimState confirmed && confirmed.MatchOver) FinishFromState(confirmed);
            else if (Session.LocallyDropped)
            {
                _handshake.SendLeave(ChibiLeaveReason.Forfeit);
                Finish(ChibiMatchOutcome.Loss, ChibiEndCause.Dropped, -1, close: true);
            }
            else if (_disconnect.HasValue)
            {
                if (!Session.HasInputFrom(0))
                    Finish(ChibiMatchOutcome.NoContest, ChibiEndCause.PeerNeverStarted, -1, close: true);
                else if (_disconnect.Value == ChibiDisconnectReason.Stalled)
                {
                    _handshake.SendLeave(ChibiLeaveReason.Stalled);   // the host eliminates us; we take no result
                    Finish(ChibiMatchOutcome.NoContest, ChibiEndCause.OpponentStalled, -1, close: true);
                }
                else Finish(ChibiMatchOutcome.NoContest, ChibiEndCause.HostLeft, -1, close: true);
            }
            return advanced;
        }

        /// <summary>3-4 players: the result from the CONFIRMED end state (WinnerMask / Place).</summary>
        void FinishFromState(ChibiSimState s)
        {
            int me = LocalIndex, mask = s.WinnerMask;
            var outcome = mask == 0 ? ChibiMatchOutcome.Draw : (mask & (1 << me)) != 0 ? ChibiMatchOutcome.Win : ChibiMatchOutcome.Loss;
            int w = -1;
            for (int i = 0; i < s.PlayerCount; i++) if ((mask & (1 << i)) != 0) { w = i; break; }
            Finish(outcome, ChibiEndCause.MatchOver, w, close: false);
        }

        bool TickPair()
        {
            var link = _group != null ? _links[1] : _link;
            if (link != null && link.LinkState == ChibiLinkState.Lost) Session.NotifyDisconnected(ChibiDisconnectReason.TransportLost);
            while (HandlePair(out var leave))
            {
                switch (leave)
                {
                    case ChibiLeaveReason.Forfeit: Session.NotifyDisconnected(ChibiDisconnectReason.RemoteLeft); break;
                    case ChibiLeaveReason.Desync: _desyncFlag = true; break;
                    // SEC-003: the peer's word that the match is complete decides nothing. If it really is, the confirmed
                    // state below shows it; if not, whatever disconnect follows is scored as a disconnect.
                    case ChibiLeaveReason.MatchComplete: break;
                    // The peer ended the match as "too slow to play": end it the same way here (no contest), instead of
                    // timing the peer out 5 s later and recording a win. Weaker than Leave(Desync), so no new escape.
                    case ChibiLeaveReason.Stalled: Session.NotifyDisconnected(ChibiDisconnectReason.Stalled); break;
                }
            }

            bool advanced = Session.Tick();
            if (advanced) OnAdvanced?.Invoke(Session.Frame);

            if (_desyncFlag)
            {
                SendLeaveAll(ChibiLeaveReason.Desync);
                Finish(ChibiMatchOutcome.NoContest, ChibiEndCause.Desync, -1, close: true);
            }
            else if (Session.ConfirmedState is ChibiSimState confirmed && confirmed.MatchOver)
            {
                // The end is in the CONFIRMED state: no late input can roll it back any more.
                int w = confirmed.Winner;
                var outcome = w < 0 ? ChibiMatchOutcome.Draw : (w == LocalIndex ? ChibiMatchOutcome.Win : ChibiMatchOutcome.Loss);
                Finish(outcome, ChibiEndCause.MatchOver, w, close: false);
            }
            else if (_disconnect.HasValue)
            {
                if (!Session.HasRemoteInput)   // NET-03: the peer never played a tick with us (stale datagrams do not count)
                    Finish(ChibiMatchOutcome.NoContest, ChibiEndCause.PeerNeverStarted, -1, close: true);
                else if (_disconnect.Value == ChibiDisconnectReason.Stalled)
                {
                    SendLeaveAll(ChibiLeaveReason.Stalled);   // so the other side ends as no contest too
                    Finish(ChibiMatchOutcome.NoContest, ChibiEndCause.OpponentStalled, -1, close: true);
                }
                else
                {
                    var cause = _disconnect.Value == ChibiDisconnectReason.RemoteLeft ? ChibiEndCause.OpponentLeft
                              : _disconnect.Value == ChibiDisconnectReason.Timeout ? ChibiEndCause.OpponentTimedOut
                              : ChibiEndCause.ConnectionLost;
                    Finish(ChibiMatchOutcome.Win, cause, LocalIndex, close: true);
                }
            }
            return advanced;
        }

        bool TickLingering()
        {
            bool advanced = Session.Tick();          // keep sending inputs + acks so the peer confirms its end too
            if (_group != null) { for (int g = 1; g < _group.Players; g++) while (_group.Handle(g, out _)) { } }
            else while (_handshake.Handle(out _)) { }
            if (--_linger <= 0 || Session.Disconnected)
            {
                SendLeaveAll(ChibiLeaveReason.MatchComplete);
                Phase = ChibiOnlinePhase.Closed;
            }
            return advanced;
        }

        // ================================================================== local actions

        /// <summary>
        /// Leave now. Before the match: cancels (Aborted). During it: tells the peer and counts as a loss (Q4).
        /// After the result: just closes.
        /// </summary>
        public void Forfeit()
        {
            switch (Phase)
            {
                case ChibiOnlinePhase.Handshaking:
                    SendLeaveAll(ChibiLeaveReason.Forfeit);
                    Abort(ChibiHandshakeState.PeerLeft);
                    break;
                case ChibiOnlinePhase.Playing:
                    SendLeaveAll(ChibiLeaveReason.Forfeit);
                    Finish(ChibiMatchOutcome.Loss, ChibiEndCause.LocalForfeit, Players == 2 ? 1 - LocalIndex : -1, close: true);
                    break;
                case ChibiOnlinePhase.Lingering:
                    SendLeaveAll(ChibiLeaveReason.MatchComplete);
                    Phase = ChibiOnlinePhase.Closed;
                    break;
            }
        }

        void SendLeaveAll(ChibiLeaveReason reason)
        {
            if (_group != null) _group.SendLeave(reason);
            else _handshake.SendLeave(reason);
        }

        void Abort(ChibiHandshakeState why)
        {
            Result = new ChibiOnlineResult
            {
                Outcome = ChibiMatchOutcome.Aborted, Cause = ChibiEndCause.HandshakeFailed, Winner = -1,
                HandshakeState = why, RejectReason = _group != null ? _group.RejectReason : _handshake.RejectReason,
            };
            Phase = ChibiOnlinePhase.Closed;
            OnResult?.Invoke(Result);
        }

        void Finish(ChibiMatchOutcome outcome, ChibiEndCause cause, int winner, bool close)
        {
            var s = cause == ChibiEndCause.MatchOver ? Session.ConfirmedState : null;
            Result = new ChibiOnlineResult
            {
                Outcome = outcome, Cause = cause, Winner = winner, Frame = Session.Frame,
                WinnerMask = s != null ? s.WinnerMask : 0,
                Place = s != null && LocalIndex < ChibiSimState.MaxFighters ? s.Place[LocalIndex] : 0,
            };
            if (close) Phase = ChibiOnlinePhase.Closed;
            else { Phase = ChibiOnlinePhase.Lingering; _linger = LingerTicks; }
            OnResult?.Invoke(Result);
        }

        /// <summary>Dispose the wrapped transport if it is disposable (Relay/UDP drivers hold native memory).</summary>
        public void Dispose()
        {
            if (_owner != null) { _owner.Dispose(); return; }
            if (_muxes != null) { foreach (var m in _muxes) (m?.Raw as IDisposable)?.Dispose(); return; }
            (_mux.Raw as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Unity host for a <see cref="ChibiOnlineMatch"/>: 60 Hz tick clock with rollback time dilation, local input
    /// polling, and the <see cref="IChibiSimFeed"/> the existing ChibiSimView draws from. Destroying it forfeits a
    /// running match (so closing the game counts as leaving) and disposes the transport.
    /// </summary>
    public sealed class ChibiOnlineMatchRunner : MonoBehaviour, IChibiSimFeed
    {
        readonly ChibiTickClock _clock = new ChibiTickClock();
        IChibiInputSource _input;
        Func<float> _dilation;
        Action _tick;

        /// <summary>The match being run (null before Begin).</summary>
        public ChibiOnlineMatch Match { get; private set; }

        /// <inheritdoc/>
        public ChibiSimState State => Match?.Session?.State;
        /// <inheritdoc/>
        public float RenderAlpha => _clock.RenderAlpha;
        /// <inheritdoc/>
        public event Action<int, ChibiInputPacket, ChibiInputPacket> OnFrame;

        /// <summary>Start ticking <paramref name="match"/>; <paramref name="input"/> must be the source the match samples.</summary>
        /// <example><code>runner.Begin(match, input);</code></example>
        public void Begin(ChibiOnlineMatch match, IChibiInputSource input)
        {
            Match = match ?? throw new ArgumentNullException(nameof(match));
            _input = input;
            match.OnAdvanced += f => OnFrame?.Invoke(f - 1, default, default);
            _dilation = () => Match.TimeDilation;
            _tick = () => Match.Tick();
            _clock.Reset();
        }

        void Update()
        {
            if (Match == null || Match.Phase == ChibiOnlinePhase.Closed) return;
            _input?.PollRenderFrame();
            _clock.Run(Time.unscaledDeltaTime, _dilation, _tick);
        }

        void OnDestroy()
        {
            if (Match == null) return;
            if (Match.Phase != ChibiOnlinePhase.Closed) Match.Forfeit();
            Match.Dispose();
        }
    }
}
