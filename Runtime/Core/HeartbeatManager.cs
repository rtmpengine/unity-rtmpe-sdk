// RTMPE SDK — Runtime/Core/HeartbeatManager.cs
//
// Sends periodic Heartbeat packets and monitors HeartbeatAck replies.
//
// Rules (matching gateway timeout defaults):
//   • Sends a Heartbeat every _intervalMs milliseconds (default 5 000 ms).
//   • Records the send timestamp to compute RTT when the Ack arrives.
//   • Fires OnRttUpdated(rttMs) whenever an Ack is received.
//   • OnHeartbeatTimeout() fires — and the caller (NetworkManager) disconnects
//     — only once BOTH the consecutive-miss counter has reached its threshold
//     AND no AEAD-authenticated Ack has been seen for the liveness-grace span.
//     The miss counter is the fast detector; the grace span is the authority
//     that distinguishes a dead link from a client that briefly could not
//     drain the Ack off its main thread. See HeartbeatLivenessPolicy.
//
// Threading:
//   HeartbeatManager uses System.Diagnostics.Stopwatch (monotonic, thread-safe)
//   for RTT measurement and is driven by the Unity main thread via the Update
//   coroutine wired in by NetworkManager.

using System;
using System.Diagnostics;
using RTMPE.Protocol;

namespace RTMPE.Core
{
    /// <summary>
    /// Manages keep-alive heartbeat packets for an active RTMPE session.
    /// Drive by calling <see cref="Tick"/> every Unity Update frame.
    /// </summary>
    public sealed class HeartbeatManager
    {
        // ── Configuration ─────────────────────────────────────────────────────
        private readonly int _intervalMs;
        private const    int MaxMissedAcks = 3;

        // Maximum span (ms) the session may go without an authenticated ack
        // before the link is declared dead.  The miss counter reaching its
        // threshold is necessary but not sufficient — this span is the gate
        // that lets a transient local stall, which still delivers a real ack
        // inside the window, ride out a burst of misses instead of forcing a
        // reconnect.  Floored at the legacy three-strikes window so it can only
        // ever be configured MORE tolerant than the historical behaviour.
        private readonly int _livenessGraceMs;

        // Acks arriving more than this multiple of _intervalMs after the
        // most-recent send are treated as ghosts from a previous session and
        // dropped without computing RTT.  Two intervals comfortably covers a
        // legitimate three-strikes timeout (which would have already fired
        // OnHeartbeatTimeout) while excluding cross-reconnect leakage.
        private const    int StaleAckIntervalMultiplier = 2;

        // ── State ─────────────────────────────────────────────────────────────
        private bool    _running;
        private long    _lastSendTick;    // Stopwatch ticks at the time of the most-recent send (for interval tracking)
        private long    _pendingSendTick; // Stopwatch ticks at the time of the FIRST send for the current heartbeat cycle (for RTT)
        private int     _missedAcks;
        private bool    _awaitingAck;
        // Stopwatch ms of the most-recent AUTHENTICATED ack — the witness the
        // liveness grace measures against.  Advanced only by OnAckReceived,
        // which the receive path reaches only after AEAD validation, so a
        // forged or unauthenticated frame can never refresh it and mask a real
        // outage.  Seeded to the session's own start (0) so a connection that
        // never receives a single ack still times out on schedule.
        private long    _lastValidAckMs;
        // Monotone cycle identifier — increments once per logical heartbeat
        // cycle (initial send), held constant across retransmits within the
        // same cycle, and observable via <see cref="CurrentCycleId"/> for
        // diagnostics and integration tests.  An ack is consumed by the
        // cycle that was outstanding at receive time; subsequent acks for
        // the same cycle (e.g. retransmit duplicate) are ignored — the
        // boolean witness `_awaitingAck` enforces single-shot acceptance,
        // and the cycle counter makes the witness inspectable.
        private uint    _cycleId;

        private readonly PacketBuilder _builder;
        private readonly Stopwatch     _clock;

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>
        /// Invoked when a <c>HeartbeatAck</c> is received with the measured RTT.
        /// Raised on the main thread (triggered from <see cref="OnAckReceived"/>).
        /// </summary>
        public event Action<float> OnRttUpdated;

        /// <summary>
        /// Invoked when the link is judged dead: <see cref="MaxMissedAcks"/>
        /// consecutive heartbeats have gone unacknowledged AND no authenticated
        /// ack has arrived for the whole liveness-grace span. The caller should
        /// disconnect and attempt reconnect.
        /// </summary>
        public event Action OnHeartbeatTimeout;

        /// <summary>
        /// Invoked when a successful ack measures an RTT above
        /// <see cref="RttSpikeThresholdMs"/>.  The argument is the offending
        /// RTT in milliseconds.  Subscribers can use this to log network-
        /// quality regressions, surface a UI indicator, or feed an adaptive
        /// quality controller without polling <see cref="OnRttUpdated"/> on
        /// every ack.  Spikes are surfaced ONCE per occurrence — the
        /// manager fires this event only on the offending sample, not for
        /// the trailing samples that may also exceed the threshold within
        /// the same network event.
        /// </summary>
        public event Action<float> OnRttSpikeDetected;

        /// <summary>
        /// RTT (in ms) above which <see cref="OnRttSpikeDetected"/> fires.
        /// Default 1000 ms — well above any healthy LAN / wired regional
        /// link, just under the threshold where RUDP retransmits would
        /// already be visible to gameplay code.  Settable so deployments
        /// targeting cellular networks (where 500 ms+ is normal) can raise
        /// it; clamped to a positive minimum so a misconfiguration cannot
        /// fire on every ack.
        /// </summary>
        public int RttSpikeThresholdMs
        {
            get => _rttSpikeThresholdMs;
            set => _rttSpikeThresholdMs = value < 1 ? 1 : value;
        }
        private int _rttSpikeThresholdMs = 1_000;

        // ── Construction ──────────────────────────────────────────────────────

        /// <param name="intervalMs">Milliseconds between consecutive heartbeat sends (default 5 000).</param>
        /// <param name="sharedBuilder">
        /// Optional shared <see cref="PacketBuilder"/> whose sequence counter is shared
        /// with the rest of the connection.  When <see langword="null"/> (default) a new
        /// private builder is created — useful in unit tests that do not need shared state.
        /// Pass the NetworkManager's <c>_packetBuilder</c> field in production so that
        /// heartbeat packets and data packets draw from the same monotone counter,
        /// preventing sequence-number collisions that could cause nonce reuse once AEAD
        /// is integrated.
        /// </param>
        /// <param name="livenessGraceMs">
        /// Maximum span (ms) with no authenticated ack before the session is
        /// declared lost.  A value &lt;= 0 selects the default — twice the legacy
        /// three-strikes window — and any supplied value is floored at that
        /// legacy window, so configuration can only widen tolerance, never make
        /// the link drop sooner than it historically would.
        /// </param>
        public HeartbeatManager(int intervalMs = 5_000, PacketBuilder sharedBuilder = null, int livenessGraceMs = 0)
        {
            if (intervalMs < 100)
                throw new ArgumentOutOfRangeException(nameof(intervalMs), "Heartbeat interval must be >= 100 ms.");

            _intervalMs = intervalMs;
            _builder    = sharedBuilder ?? new PacketBuilder();  // Use the shared counter when provided
            _clock      = new Stopwatch();

            // The legacy detector declared the link dead after MaxMissedAcks
            // intervals of silence.  Default the grace to twice that so a stall
            // which recovers and delivers a real ack within the doubled window
            // is forgiven; floor any explicit value at the legacy window so a
            // misconfiguration cannot make liveness stricter than it ever was.
            int legacyWindowMs = intervalMs * MaxMissedAcks;
            _livenessGraceMs   = livenessGraceMs <= 0
                ? legacyWindowMs * 2
                : Math.Max(livenessGraceMs, legacyWindowMs);
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        /// <summary>
        /// Begin the heartbeat loop. No-op if already running.
        /// </summary>
        /// <remarks>
        /// All bookkeeping is reset unconditionally on (re-)Start so a
        /// previously-Stopped manager that is being re-driven across a
        /// reconnect cycle observes a clean slate (no carried-over miss
        /// count, no stale RTT anchor, no spurious "still awaiting an Ack
        /// from the previous session" race).  Keeping the early-return
        /// guard while still in the running state prevents the same fields
        /// from being clobbered if Start is called twice in succession.
        /// </remarks>
        public void Start()
        {
            if (_running) return;
            _running         = true;
            _missedAcks      = 0;
            _awaitingAck     = false;
            _lastSendTick    = 0;
            _pendingSendTick = 0;
            // Anchor liveness to the fresh session's own t=0 (the clock is
            // restarted just below).  A session that goes on to receive no ack
            // at all measures its grace span from here, so it still times out;
            // the first authenticated ack moves the anchor forward.
            _lastValidAckMs  = 0;
            // Cycle id is reset only on (re-)Start so an external observer
            // that latched the previous cycle id and queries
            // <see cref="CurrentCycleId"/> after Stop sees a stable zero
            // until the next cycle actually begins — avoiding ambiguous
            // "cycle 7 from a previous session" reads while the manager
            // is dormant.
            _cycleId         = 0;
            _clock.Restart();
        }

        /// <summary>True while the heartbeat loop is active.</summary>
        public bool IsRunning => _running;

        /// <summary>
        /// Most-recently-issued heartbeat cycle identifier.  Zero before the
        /// first send; otherwise increments once per cycle (initial send),
        /// held constant across retransmits within the same cycle.  Surfaced
        /// for telemetry hooks and integration tests that need to assert
        /// per-cycle ack semantics.
        /// </summary>
        public uint CurrentCycleId => _cycleId;

        /// <summary>True between the initial send of a cycle and the
        /// successful (non-stale) ack that resolves it; false otherwise.</summary>
        public bool IsAwaitingAck => _awaitingAck;

        /// <summary>
        /// Effective liveness-grace span (ms): the longest run without an
        /// authenticated ack the session tolerates before timing out.  Resolved
        /// once at construction (default or floored) and surfaced for telemetry
        /// and tests that assert the floor/default resolution.
        /// </summary>
        public int LivenessGraceMs => _livenessGraceMs;

        /// <summary>
        /// Stop the heartbeat loop. No-op if not running.
        /// </summary>
        public void Stop()
        {
            _running = false;
            _clock.Stop();
        }

        // ── Tick (called per Unity Update frame) ──────────────────────────────

        /// <summary>
        /// Called once per Unity Update frame.
        /// Sends a heartbeat if the interval has elapsed, and checks for timeout.
        /// </summary>
        /// <param name="sendCallback">
        /// Delegate called with the raw heartbeat packet bytes to transmit.
        /// </param>
        public void Tick(Action<byte[]> sendCallback)
        {
            if (!_running || sendCallback == null) return;

            long nowMs = _clock.ElapsedMilliseconds;

            if (!_awaitingAck && nowMs - _lastSendTick >= _intervalMs)
            {
                // Send heartbeat — record BOTH the interval-tracking tick and the
                // per-cycle pending tick used for RTT.  _pendingSendTick must not
                // be overwritten on retransmits so that a late Ack from the original
                // send produces a correct RTT (total elapsed since first attempt).
                var packet = _builder.BuildHeartbeat();
                sendCallback(packet);
                _lastSendTick    = nowMs;
                _pendingSendTick = nowMs;  // start of this heartbeat cycle
                _awaitingAck     = true;
                _cycleId        += 1;      // unsigned wrap is acceptable; only relative ordering matters
            }
            else if (_awaitingAck && nowMs - _lastSendTick >= _intervalMs)
            {
                // Interval elapsed again while waiting for the previous Ack — count as a miss.
                _missedAcks++;
                // Tear down only when the miss counter AND the authenticated-ack
                // grace span agree the link is dead.  The counter on its own no
                // longer disconnects: a missed cycle whose ack was merely dropped
                // or delayed by a saturated main thread is forgiven as long as a
                // real ack landed inside the grace window.
                if (HeartbeatLivenessPolicy.ShouldDisconnect(
                        _missedAcks, MaxMissedAcks, nowMs - _lastValidAckMs, _livenessGraceMs))
                {
                    _running = false;
                    OnHeartbeatTimeout?.Invoke();
                    return;
                }
                // Try again next interval (reset the send timer without requiring an Ack).
                // _pendingSendTick is intentionally NOT reset here — we keep the original
                // send time so that if the Ack for the first heartbeat eventually arrives
                // after a retransmit, the RTT reflects the full round-trip elapsed time.
                var packet = _builder.BuildHeartbeat();
                sendCallback(packet);
                _lastSendTick = nowMs;
            }
        }

        // ── Ack handler ───────────────────────────────────────────────────────

        /// <summary>
        /// Call when a <see cref="PacketType.HeartbeatAck"/> packet is received.
        /// Resets the miss counter and fires <see cref="OnRttUpdated"/>.
        /// </summary>
        public void OnAckReceived()
        {
            if (!_running) return;
            if (!_awaitingAck) return; // Ignore spurious/duplicate ACKs

            // Ghost-RTT guard.  Without it, an Ack delivered after a
            // Stop/Start reconnect cycle (or arriving very late from the
            // previous session) would compute RTT against an uninitialised
            // or stale _pendingSendTick — producing wildly inflated RTT
            // spikes that cosmetically corrupt server health dashboards and
            // confuse any adaptive client logic keyed on RTT.  Pin the
            // invariant: RTT is only computed when an ack arrives within a
            // small multiple of the interval after the most-recent send.
            long nowMs = _clock.ElapsedMilliseconds;
            // Invariant: _awaitingAck implies _pendingSendTick > 0, because
            // Tick sets both atomically.  Reaching this point with a zero or
            // negative tick is therefore an internal book-keeping bug.  In
            // release builds we still bail out defensively so a corrupted
            // tick cannot poison RTT, but the assert surfaces the regression
            // in development builds.
            UnityEngine.Debug.Assert(_pendingSendTick > 0,
                "HeartbeatManager: _awaitingAck was true but _pendingSendTick is non-positive — Tick must set both before clearing _awaitingAck.");
            if (_pendingSendTick <= 0) return;
            long ageMs = nowMs - _pendingSendTick;
            if (ageMs < 0)
            {
                // Negative age is a clock-anomaly path — Stopwatch
                // ElapsedMilliseconds is documented as monotonic, so a
                // negative ageMs implies a clock-source swap or counter
                // overflow.  Drop the ack without firing OnRttUpdated and
                // without altering miss accounting: a clock anomaly is
                // not evidence that the channel delivered anything from
                // the gateway, so resetting _missedAcks here would
                // reward an attacker / hardware-clock fault with cycle-
                // accounting drift.  Clearing _awaitingAck is still
                // correct so the next Tick can issue a fresh heartbeat.
                _awaitingAck = false;
                return;
            }

            // Past the clock-anomaly guard this is a genuine, AEAD-authenticated
            // ack — proof the gateway is still answering.  Advance the liveness
            // anchor here so BOTH a stale-but-real ack and a fresh one refresh
            // it; only the negative-age path above is excluded, because a clock
            // fault is not evidence the channel delivered anything.
            _lastValidAckMs = nowMs;

            if (ageMs > (long)_intervalMs * StaleAckIntervalMultiplier)
            {
                // Staleness path: the ack arrived AFTER the pendingSend
                // window closed.  Reset _missedAcks alongside
                // _awaitingAck because a ghost ack IS evidence that the
                // channel just delivered something from the gateway —
                // whatever caused the previous miss(es) is unlikely to
                // still hold.  Without the reset, misses accumulated
                // during a transient outage carry into the next cycle,
                // surfacing OnHeartbeatTimeout after a single miss
                // instead of the configured threshold and producing
                // spurious disconnects on healthy links (e.g. Wi-Fi →
                // cellular hand-off where the staleness gate trips
                // exactly once).
                _missedAcks  = 0;
                _awaitingAck = false;
                return;
            }

            // Use _pendingSendTick (set at the start of this heartbeat cycle, never
            // overwritten on retransmits) so RTT reflects the true elapsed time from
            // the first send attempt — not just the most-recent retransmit interval.
            float rttMs = ageMs;

            _missedAcks  = 0;
            _awaitingAck = false;

            OnRttUpdated?.Invoke(rttMs);

            // Anomaly hook: an RTT above the configured spike threshold
            // typically indicates a network event (Wi-Fi → cellular hand-
            // off, ISP congestion, queue-bloat).  Surface it once per
            // affected sample so dashboards see the spike without
            // listeners having to filter every OnRttUpdated event
            // themselves.  Per-cycle one-shot semantics: the next ack on
            // the next cycle gets its own spike check.
            if (rttMs > _rttSpikeThresholdMs)
                OnRttSpikeDetected?.Invoke(rttMs);
        }
    }
}
