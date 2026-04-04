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

        // ── State ─────────────────────────────────────────────────────────────
        private bool    _running;
        private long    _lastSendTick;   // Stopwatch ticks at the time of the last send
        private long    _lastAckTick;    // Stopwatch ticks at the time of the last Ack
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
        public HeartbeatManager(int intervalMs = 5_000)
        {
            if (intervalMs < 100)
                throw new ArgumentOutOfRangeException(nameof(intervalMs), "Heartbeat interval must be >= 100 ms.");

            _intervalMs = intervalMs;
            _builder    = new PacketBuilder();
            _clock      = new Stopwatch();
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        /// <summary>
        /// Begin the heartbeat loop. No-op if already running.
        /// </summary>
        public void Start()
        {
            if (_running) return;
            _running     = true;
            _missedAcks  = 0;
            _awaitingAck = false;
            _lastSendTick = 0;
            _clock.Restart();
        }

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
                // Send heartbeat
                var packet = _builder.BuildHeartbeat();
                sendCallback(packet);
                _lastSendTick = nowMs;
                _awaitingAck  = true;
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

            long nowMs  = _clock.ElapsedMilliseconds;
            float rttMs = nowMs - _lastSendTick;

            _missedAcks  = 0;
            _awaitingAck = false;

            OnRttUpdated?.Invoke(rttMs);
        }
    }
}
