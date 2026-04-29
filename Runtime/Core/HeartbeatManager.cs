// RTMPE SDK — Runtime/Core/HeartbeatManager.cs
//
// Sends periodic Heartbeat packets and monitors HeartbeatAck replies.
//
// Rules (matching gateway timeout defaults):
//   • Sends a Heartbeat every _intervalMs milliseconds (default 5 000 ms).
//   • Records the send timestamp to compute RTT when the Ack arrives.
//   • Fires OnRttUpdated(rttMs) whenever an Ack is received.
//   • Three consecutive missed Acks trigger OnHeartbeatTimeout() — the
//     caller (NetworkManager) is responsible for disconnecting.
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

        private readonly PacketBuilder _builder;
        private readonly Stopwatch     _clock;

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>
        /// Invoked when a <c>HeartbeatAck</c> is received with the measured RTT.
        /// Raised on the main thread (triggered from <see cref="OnAckReceived"/>).
        /// </summary>
        public event Action<float> OnRttUpdated;

        /// <summary>
        /// Invoked when <see cref="MaxMissedAcks"/> consecutive heartbeats go unacknowledged.
        /// The caller should disconnect and attempt reconnect.
        /// </summary>
        public event Action OnHeartbeatTimeout;

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
        public HeartbeatManager(int intervalMs = 5_000, PacketBuilder sharedBuilder = null)
        {
            if (intervalMs < 100)
                throw new ArgumentOutOfRangeException(nameof(intervalMs), "Heartbeat interval must be >= 100 ms.");

            _intervalMs = intervalMs;
            _builder    = sharedBuilder ?? new PacketBuilder();  // Use the shared counter when provided
            _clock      = new Stopwatch();
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
            _clock.Restart();
        }

        /// <summary>True while the heartbeat loop is active.</summary>
        public bool IsRunning => _running;

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
            }
            else if (_awaitingAck && nowMs - _lastSendTick >= _intervalMs)
            {
                // Interval elapsed again while waiting for the previous Ack — count as a miss.
                _missedAcks++;
                if (_missedAcks >= MaxMissedAcks)
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
            if (_pendingSendTick <= 0) return;
            long ageMs = nowMs - _pendingSendTick;
            if (ageMs < 0 || ageMs > (long)_intervalMs * StaleAckIntervalMultiplier)
            {
                // Drop without firing OnRttUpdated.  We still clear
                // _awaitingAck so the next Tick can issue a fresh
                // heartbeat instead of being permanently parked waiting
                // for the ghost.
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
        }
    }
}
