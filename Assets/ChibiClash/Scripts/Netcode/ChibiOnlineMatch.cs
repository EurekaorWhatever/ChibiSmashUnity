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
        /// <summary>Team play: the drop vote did not complete or our side of a split was the minority (no contest for us).</summary>
        DropVoteFailed,
        /// <summary>Team play: a peer sent something no honest peer can (see ChibiDisconnectReason.ProtocolViolation). No contest.</summary>
        ProtocolViolation,
    }

    /// <summary>Final result of an online match, from the local player's point of view.</summary>
    public struct ChibiOnlineResult
    {
        public ChibiMatchOutcome Outcome;
        public ChibiEndCause Cause;
        /// <summary>Sim index of the winner, -1 for draw / no contest / aborted.</summary>
        public int Winner;
        /// <summary>Simulated frame when the result was decided.</summary>
        public int Frame;
        /// <summary>Handshake detail when <see cref="Cause"/> is HandshakeFailed.</summary>
        public ChibiHandshakeState HandshakeState;
        public ChibiRejectReason RejectReason;
        /// <summary>FFA / team play: our placement from the confirmed state (ChibiSimState.Placement; 0 = not decided).</summary>
        public int Placement;
        /// <summary>
        /// Review S1 (telemetry): roster slot that triggered a no contest / abort (desync peer, Leave(Desync) sender, protocol
        /// violator, accused-and-accuser of a failed vote, a leaver that never played, the handshake's problem link); -1 = none.
        /// </summary>
        public int CulpritSlot;

        public override string ToString() =>
            Outcome + " (" + Cause + (Cause == ChibiEndCause.HandshakeFailed ? ": " + HandshakeState + "/" + RejectReason : "") + ") @" + Frame;
    }

    /// <summary>
    /// One online match from handshake to result, for 1v1 (one link) and team play (a full mesh of up to three links,
    /// protocol 6). Owns one <see cref="ChibiChannelMux"/> per link (security BLOCKER 6: a flooding peer can only fill its
    /// own queues), the <see cref="ChibiHandshake"/> and the <see cref="ChibiRollbackSession"/>.
    ///
    /// Results are read from the CONFIRMED state only: Win when the winning team is ours, Loss otherwise, Draw on -1;
    /// FFA also reports our <see cref="ChibiOnlineResult.Placement"/>. Leavers:
    ///   * 1v1 (unchanged): a peer that leaves or times out after playing gives us the win; one that never sent a real input
    ///     frame gives a no contest (NET-03); Stalled is a no contest for both.
    ///   * Team play: a leaver is voted out and eliminated at the agreed drop frame (the session injects
    ///     <see cref="ChibiInputPacket.DroppedRaw"/>); the match continues and its end is scored from the confirmed state.
    ///     A leaver that never delivered a real input frame (drop frame at or below InputDelay on every survivor) ends the
    ///     match as a no contest for everyone. A failed vote / lost majority is a no contest for us.
    ///   * Any desync with any peer: Leave(Desync) to everyone and a no contest for everyone.
    ///   * Leave(MatchComplete) decides nothing (SEC-003). Our own forfeit is a Loss.
    /// </summary>
    public sealed class ChibiOnlineMatch : IDisposable
    {
        /// <summary>Ticks we keep exchanging inputs after a confirmed end so the peers confirm it too (1.5 s).</summary>
        public const int LingerTicks = 90;

        readonly IChibiTransport[] _raw;
        readonly ChibiChannelMux[] _mux;
        readonly IChibiLinkStatus[] _link;
        readonly ChibiHandshake _handshake;
        readonly IChibiInputSource _input;
        readonly int _count;
        int _linger;
        bool _desyncFlag;
        bool _neverStartedDrop;
        int _neverStartedSlot = -1;
        int _desyncFrom = -1;
        ChibiDisconnectReason? _disconnect;

        public ChibiOnlinePhase Phase { get; private set; } = ChibiOnlinePhase.Handshaking;
        public ChibiRollbackSession Session { get; private set; }
        public ChibiReplayRecorder Replay { get; private set; }
        public ChibiHandshake Handshake => _handshake;
        public ChibiOnlineResult Result { get; private set; }
        public bool HasResult => Result.Outcome != ChibiMatchOutcome.None;
        /// <summary>Our roster slot (sim player index).</summary>
        public int LocalIndex => _handshake.LocalIndex;
        /// <summary>Roster size (2..4).</summary>
        public int Count => _count;
        public float TimeDilation => Phase == ChibiOnlinePhase.Playing && Session != null ? Session.TimeDilation : 1f;
        /// <summary>Datagrams dropped by every link's mux (full queues, unknown magic, oversize).</summary>
        public int MuxDropped
        {
            get { int n = 0; foreach (var m in _mux) if (m != null) n += m.Dropped; return n; }
        }

        public event Action<ChibiOnlineMatch> OnStarted;
        public event Action<ChibiOnlineResult> OnResult;
        public event Action<int> OnAdvanced;

        ChibiOnlineMatch(IChibiTransport[] raw, Func<IChibiTransport[], ChibiHandshake> handshake, IChibiInputSource input)
        {
            _input = input ?? throw new ArgumentNullException(nameof(input));
            _count = raw.Length;
            _raw = raw;
            _mux = new ChibiChannelMux[_count];
            _link = new IChibiLinkStatus[_count];
            var controls = new IChibiTransport[_count];
            for (int s = 0; s < _count; s++)
            {
                if (raw[s] == null) continue;
                _mux[s] = new ChibiChannelMux(raw[s]);
                _link[s] = raw[s] as IChibiLinkStatus;
                controls[s] = _mux[s].Control;
            }
            _handshake = handshake(controls);
        }

        /// <summary>1v1 host (slot 0) over one link.</summary>
        public static ChibiOnlineMatch Host(IChibiTransport transport, IChibiInputSource input, FighterClass myClass, int myPassive,
                                            int stage, uint seed, ChibiRollbackSettings settings,
                                            int handshakeTimeoutTicks = ChibiHandshake.DefaultTimeoutTicks, string localName = "",
                                            bool hazardsOff = false)
            => new ChibiOnlineMatch(new[] { null, transport ?? throw new ArgumentNullException(nameof(transport)) },
                c => ChibiHandshake.ForHost(c[1], myClass, myPassive, stage, seed, settings, handshakeTimeoutTicks, localName,
                                            hazardsOff), input);

        /// <summary>1v1 joiner (slot 1) over one link.</summary>
        public static ChibiOnlineMatch Join(IChibiTransport transport, IChibiInputSource input, FighterClass myClass, int myPassive,
                                            int handshakeTimeoutTicks = ChibiHandshake.DefaultTimeoutTicks, string localName = "",
                                            int expectStage = -1, bool? expectHazardsOff = null)
            => new ChibiOnlineMatch(new[] { transport ?? throw new ArgumentNullException(nameof(transport)), null },
                c => ChibiHandshake.ForJoiner(c[0], myClass, myPassive, handshakeTimeoutTicks, localName,
                                              expectStage, expectHazardsOff), input);

        /// <summary>
        /// Team-play host (slot 0). <paramref name="links"/>[j] is the raw link bound to roster slot j (null at 0); its
        /// length is the roster size. <paramref name="playerHashes"/>[j] is the lobby's player in slot j (null = unchecked).
        /// </summary>
        public static ChibiOnlineMatch MeshHost(IChibiTransport[] links, IChibiInputSource input, int format, uint[] playerHashes,
                                                FighterClass myClass, int myPassive, int stage, uint seed, ChibiRollbackSettings settings,
                                                int handshakeTimeoutTicks = ChibiHandshake.DefaultTimeoutTicks, string localName = "",
                                                bool hazardsOff = false, byte[][] pairKeys = null)
            => new ChibiOnlineMatch(Authenticate(links, pairKeys, 0),
                c => ChibiHandshake.ForMeshHost(c, format, playerHashes, myClass, myPassive, stage, seed, settings,
                                                handshakeTimeoutTicks, localName, hazardsOff), input);

        /// <summary>
        /// Team-play joiner in roster slot <paramref name="mySlot"/>: <paramref name="links"/>[0] is the host link,
        /// links[k] the guest link to slot k (null at <paramref name="mySlot"/>).
        /// </summary>
        public static ChibiOnlineMatch MeshJoin(IChibiTransport[] links, int mySlot, IChibiInputSource input, int format,
                                                uint[] playerHashes, FighterClass myClass, int myPassive,
                                                int handshakeTimeoutTicks = ChibiHandshake.DefaultTimeoutTicks, string localName = "",
                                                int expectStage = -1, bool? expectHazardsOff = null, byte[][] pairKeys = null)
            => new ChibiOnlineMatch(Authenticate(links, pairKeys, mySlot),
                c => ChibiHandshake.ForMeshJoiner(c, mySlot, format, playerHashes, myClass, myPassive, handshakeTimeoutTicks,
                                                  localName, expectStage, expectHazardsOff), input);

        /// <summary>
        /// Review N1: wrap every mesh link in a <see cref="ChibiAuthLink"/> keyed with that pair's key
        /// (<paramref name="pairKeys"/>[slot], from <see cref="ChibiX25519.PairKey"/>). Null keys = untagged (tests, LAN
        /// tools); production team play always passes keys (ChibiTeamMatchmaker).
        /// </summary>
        static IChibiTransport[] Authenticate(IChibiTransport[] links, byte[][] pairKeys, int mySlot)
        {
            if (links == null) throw new ArgumentNullException(nameof(links));
            if (pairKeys == null) return links;
            if (pairKeys.Length != links.Length) throw new ArgumentException("one pair key per slot", nameof(pairKeys));
            var wrapped = new IChibiTransport[links.Length];
            for (int s = 0; s < links.Length; s++)
                wrapped[s] = s == mySlot || links[s] == null ? links[s] : new ChibiAuthLink(links[s], pairKeys[s], mySlot, s);
            return wrapped;
        }

        /// <summary>Datagrams dropped by the link authenticators (bad or missing tag), all links.</summary>
        public int AuthRejected
        {
            get { int n = 0; foreach (var r in _raw) if (r is ChibiAuthLink a) n += a.Rejected; return n; }
        }

        // ================================================================== tick

        /// <summary>One 60 Hz tick. Returns true when the simulation advanced.</summary>
        public bool Tick()
        {
            // A real transport (Unity Transport / Relay) is serviced around the tick: receive first, flush sends last.
            for (int s = 0; s < _count; s++) (_raw[s] as IChibiPumpedTransport)?.Pump();
            for (int s = 0; s < _count; s++) _mux[s]?.Pump();
            bool advanced;
            switch (Phase)
            {
                case ChibiOnlinePhase.Handshaking: TickHandshake(); advanced = false; break;
                case ChibiOnlinePhase.Playing: advanced = TickPlaying(); break;
                case ChibiOnlinePhase.Lingering: advanced = TickLingering(); break;
                default: advanced = false; break;
            }
            for (int s = 0; s < _count; s++) (_raw[s] as IChibiPumpedTransport)?.Flush();
            return advanced;
        }

        bool LinkLost(int s) => _link[s] != null && _link[s].LinkState == ChibiLinkState.Lost;

        void TickHandshake()
        {
            for (int s = 0; s < _count; s++)
                if (LinkLost(s)) { Abort(ChibiHandshakeState.TimedOut); return; }
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
            var cfg = _handshake.Config;
            var sessionLinks = new IChibiTransport[_count];
            for (int s = 0; s < _count; s++) sessionLinks[s] = _mux[s]?.Session;
            Session = new ChibiRollbackSession(_handshake.LocalIndex, cfg, sessionLinks, _input, _handshake.Settings);
            Replay = new ChibiReplayRecorder(cfg);
            if (_count == 2) Session.OnFrameConfirmed += Replay.Add;
            else Session.OnFrameConfirmedAll += Replay.Add;
            Session.OnDesync += (f, l, r) => _desyncFlag = true;
            Session.OnDisconnect += reason => _disconnect = reason;
            // NET-03 per peer, decided identically on every survivor: the agreed drop frame is the minimum over the
            // survivors' votes, so "at or below InputDelay" means no survivor ever had a real input frame from the leaver.
            Session.OnPeerDropped += (slot, frame) =>
            {
                if (frame > Session.Settings.InputDelay) return;
                _neverStartedDrop = true;
                if (_neverStartedSlot < 0) _neverStartedSlot = slot;
            };
            Phase = ChibiOnlinePhase.Playing;
            OnStarted?.Invoke(this);
        }

        void PeerGone(int slot, ChibiDisconnectReason reason)
        {
            if (_count == 2) Session.NotifyDisconnected(reason);
            else Session.NotifyPeerDisconnected(slot, reason);
        }

        bool TickPlaying()
        {
            for (int s = 0; s < _count; s++)
                if (LinkLost(s)) PeerGone(s, ChibiDisconnectReason.TransportLost);
            while (_handshake.Handle(out int from, out var leave))
            {
                switch (leave)
                {
                    case ChibiLeaveReason.Forfeit: PeerGone(from, ChibiDisconnectReason.RemoteLeft); break;
                    case ChibiLeaveReason.Desync: _desyncFlag = true; if (_desyncFrom < 0) _desyncFrom = from; break;
                    // SEC-003: the peer's word that the match is complete decides nothing. If it really is, the confirmed
                    // state below shows it; if not, whatever disconnect follows is scored as a disconnect.
                    case ChibiLeaveReason.MatchComplete: break;
                    // 1v1: the peer ended the match as "too slow to play": end it the same way here (no contest). Team
                    // play: that peer is leaving; it is voted out like any leaver and the rest play on.
                    case ChibiLeaveReason.Stalled: PeerGone(from, _count == 2 ? ChibiDisconnectReason.Stalled : ChibiDisconnectReason.RemoteLeft); break;
                }
            }

            bool advanced = Session.Tick();
            if (advanced) OnAdvanced?.Invoke(Session.Frame);

            if (_desyncFlag)
            {
                _handshake.SendLeave(ChibiLeaveReason.Desync);   // every peer: one desync is a no contest for all
                Finish(ChibiMatchOutcome.NoContest, ChibiEndCause.Desync, -1, close: true,
                       culprit: Session.DesyncPeer >= 0 ? Session.DesyncPeer : _desyncFrom);
            }
            else if (_neverStartedDrop)
            {
                Finish(ChibiMatchOutcome.NoContest, ChibiEndCause.PeerNeverStarted, -1, close: true, culprit: _neverStartedSlot);
            }
            else if (Session.ConfirmedState is ChibiSimState confirmed && confirmed.MatchOver)
            {
                // The end is in the CONFIRMED state: no late input can roll it back any more. Winner is a TEAM id.
                int w = confirmed.Winner;
                int myTeam = confirmed.Team[LocalIndex];
                var outcome = w < 0 ? ChibiMatchOutcome.Draw : (w == myTeam ? ChibiMatchOutcome.Win : ChibiMatchOutcome.Loss);
                Finish(outcome, ChibiEndCause.MatchOver, w, close: false, placement: confirmed.Placement[LocalIndex]);
            }
            else if (_disconnect.HasValue)
            {
                if (_count > 2)
                    // Team play reaches here only when the drop vote failed or we lost the majority: no contest for us.
                    Finish(ChibiMatchOutcome.NoContest,
                           _disconnect.Value == ChibiDisconnectReason.ProtocolViolation ? ChibiEndCause.ProtocolViolation : ChibiEndCause.DropVoteFailed,
                           -1, close: true, culprit: Session.CulpritSlot);
                else if (!Session.HasRemoteInput)   // NET-03: the peer never played a tick with us (stale datagrams do not count)
                    Finish(ChibiMatchOutcome.NoContest, ChibiEndCause.PeerNeverStarted, -1, close: true, culprit: 1 - LocalIndex);
                else if (_disconnect.Value == ChibiDisconnectReason.Stalled)
                {
                    _handshake.SendLeave(ChibiLeaveReason.Stalled);   // so the other side ends as no contest too
                    Finish(ChibiMatchOutcome.NoContest, ChibiEndCause.OpponentStalled, -1, close: true, culprit: 1 - LocalIndex);
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
            bool advanced = Session.Tick();          // keep sending inputs + acks so the peers confirm their end too
            while (_handshake.Handle(out _, out _)) { }
            if (--_linger <= 0 || Session.Disconnected)
            {
                _handshake.SendLeave(ChibiLeaveReason.MatchComplete);
                Phase = ChibiOnlinePhase.Closed;
            }
            return advanced;
        }

        // ================================================================== local actions

        /// <summary>
        /// Quit now. While handshaking: cancel. While playing: tell every peer and record a Loss (1v1: the opponent wins;
        /// team play: we are voted out and the others play on). While lingering: just close.
        /// </summary>
        public void Forfeit()
        {
            switch (Phase)
            {
                case ChibiOnlinePhase.Handshaking:
                    _handshake.SendLeave(ChibiLeaveReason.Forfeit);
                    Abort(ChibiHandshakeState.PeerLeft);
                    break;
                case ChibiOnlinePhase.Playing:
                    _handshake.SendLeave(ChibiLeaveReason.Forfeit);
                    Finish(ChibiMatchOutcome.Loss, ChibiEndCause.LocalForfeit, _count == 2 ? 1 - LocalIndex : -1, close: true);
                    break;
                case ChibiOnlinePhase.Lingering:
                    _handshake.SendLeave(ChibiLeaveReason.MatchComplete);
                    Phase = ChibiOnlinePhase.Closed;
                    break;
            }
        }

        void Abort(ChibiHandshakeState why)
        {
            Result = new ChibiOnlineResult
            {
                Outcome = ChibiMatchOutcome.Aborted, Cause = ChibiEndCause.HandshakeFailed, Winner = -1,
                HandshakeState = why, RejectReason = _handshake.RejectReason, CulpritSlot = _handshake.ProblemSlot,
            };
            Phase = ChibiOnlinePhase.Closed;
            OnResult?.Invoke(Result);
        }

        void Finish(ChibiMatchOutcome outcome, ChibiEndCause cause, int winner, bool close, int placement = 0, int culprit = -1)
        {
            Result = new ChibiOnlineResult
            {
                Outcome = outcome, Cause = cause, Winner = winner, Frame = Session.Frame, Placement = placement, CulpritSlot = culprit,
            };
            if (close) Phase = ChibiOnlinePhase.Closed;
            else { Phase = ChibiOnlinePhase.Lingering; _linger = LingerTicks; }
            OnResult?.Invoke(Result);
        }

        /// <summary>Dispose every link that is disposable.</summary>
        public void Dispose()
        {
            foreach (var r in _raw) (r as IDisposable)?.Dispose();
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
