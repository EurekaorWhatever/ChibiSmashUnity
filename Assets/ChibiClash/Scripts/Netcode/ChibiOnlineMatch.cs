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

        readonly ChibiChannelMux _mux;
        readonly ChibiHandshake _handshake;
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
        public ChibiOnlineResult Result { get; private set; }
        /// <summary>True once <see cref="Result"/> is final.</summary>
        public bool HasResult => Result.Outcome != ChibiMatchOutcome.None;
        /// <summary>Sim index of this peer (host 0, joiner 1).</summary>
        public int LocalIndex => _handshake.LocalIndex;
        public float TimeDilation => Phase == ChibiOnlinePhase.Playing && Session != null ? Session.TimeDilation : 1f;
        /// <summary>Datagrams the mux dropped (diagnostics).</summary>
        public int MuxDropped => _mux.Dropped;

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
        }

        /// <summary>Host side (sim player 0). The host decides seed, stage and rollback settings.</summary>
        /// <example><code>var m = ChibiOnlineMatch.Host(transport, input, FighterClass.Melee, 0, stage: 0, seed, ChibiRollbackSettings.Default);</code></example>
        /// <param name="hazardsOff">Rules v20 (ST-5): the host's hazards switch, sent in the handshake Start message.</param>
        public static ChibiOnlineMatch Host(IChibiTransport transport, IChibiInputSource input, FighterClass myClass, int myPassive,
                                            int stage, uint seed, ChibiRollbackSettings settings,
                                            int handshakeTimeoutTicks = ChibiHandshake.DefaultTimeoutTicks, string localName = "",
                                            bool hazardsOff = false)
            => new ChibiOnlineMatch(transport,
                c => ChibiHandshake.ForHost(c, myClass, myPassive, stage, seed, settings, handshakeTimeoutTicks, localName,
                                            hazardsOff), input);

        /// <summary>Joiner side (sim player 1).</summary>
        /// <example><code>var m = ChibiOnlineMatch.Join(transport, input, FighterClass.Magic, 1);</code></example>
        /// <param name="expectStage">SEC-001: room stage the joiner agreed to (-1 = any).</param>
        /// <param name="expectHazardsOff">SEC-001: room hazards switch the joiner agreed to (null = any).</param>
        public static ChibiOnlineMatch Join(IChibiTransport transport, IChibiInputSource input, FighterClass myClass, int myPassive,
                                            int handshakeTimeoutTicks = ChibiHandshake.DefaultTimeoutTicks, string localName = "",
                                            int expectStage = -1, bool? expectHazardsOff = null)
            => new ChibiOnlineMatch(transport, c => ChibiHandshake.ForJoiner(c, myClass, myPassive, handshakeTimeoutTicks, localName,
                                                                             expectStage, expectHazardsOff), input);

        // ================================================================== tick

        /// <summary>One 60 Hz tick. Returns true when the simulation advanced this tick.</summary>
        public bool Tick()
        {
            // A real transport (Unity Transport / Relay) is serviced around the tick: receive first, flush sends last.
            var pumped = _mux.Raw as IChibiPumpedTransport;
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
            Session = new ChibiRollbackSession(_handshake.LocalIndex, cfg, _mux.Session, _input, _handshake.Settings);
            Replay = new ChibiReplayRecorder(cfg);
            Session.OnFrameConfirmed += Replay.Add;
            Session.OnDesync += (f, l, r) => _desyncFlag = true;
            Session.OnDisconnect += reason => _disconnect = reason;
            Phase = ChibiOnlinePhase.Playing;
            OnStarted?.Invoke(this);
        }

        bool TickPlaying()
        {
            if (_link != null && _link.LinkState == ChibiLinkState.Lost) Session.NotifyDisconnected(ChibiDisconnectReason.TransportLost);
            while (_handshake.Handle(out var leave))
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
                _handshake.SendLeave(ChibiLeaveReason.Desync);
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
                    _handshake.SendLeave(ChibiLeaveReason.Stalled);   // so the other side ends as no contest too
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
            while (_handshake.Handle(out _)) { }
            if (--_linger <= 0 || Session.Disconnected)
            {
                _handshake.SendLeave(ChibiLeaveReason.MatchComplete);
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
                    _handshake.SendLeave(ChibiLeaveReason.Forfeit);
                    Abort(ChibiHandshakeState.PeerLeft);
                    break;
                case ChibiOnlinePhase.Playing:
                    _handshake.SendLeave(ChibiLeaveReason.Forfeit);
                    Finish(ChibiMatchOutcome.Loss, ChibiEndCause.LocalForfeit, 1 - LocalIndex, close: true);
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
                HandshakeState = why, RejectReason = _handshake.RejectReason,
            };
            Phase = ChibiOnlinePhase.Closed;
            OnResult?.Invoke(Result);
        }

        void Finish(ChibiMatchOutcome outcome, ChibiEndCause cause, int winner, bool close)
        {
            Result = new ChibiOnlineResult { Outcome = outcome, Cause = cause, Winner = winner, Frame = Session.Frame };
            if (close) Phase = ChibiOnlinePhase.Closed;
            else { Phase = ChibiOnlinePhase.Lingering; _linger = LingerTicks; }
            OnResult?.Invoke(Result);
        }

        /// <summary>Dispose the wrapped transport if it is disposable (Relay/UDP drivers hold native memory).</summary>
        public void Dispose() => (_mux.Raw as IDisposable)?.Dispose();
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
