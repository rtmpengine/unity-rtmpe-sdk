// RTMPE SDK — Runtime/Sync/NetworkTransformInterpolator.cs
//
// Buffered interpolation for smooth movement of non-owner networked objects.
//
// Design decisions:
//  • Extends MonoBehaviour so Update() fires automatically on the main thread.
//  • Owner-only suppression: AddState() and Update() are both valid on any
//    client, but by convention only non-owner clients call AddState().  The
//    owning client uses NetworkTransform.Update() to SEND; the interpolator
//    is wired to RECEIVE via NetworkManager.OnDataReceived.
//  • Timestamping uses Time.timeAsDouble (local monotonic clock) at the moment
//    of receipt — no network time-synchronisation required at this stage.
//    The render cursor is Time.timeAsDouble - _interpolationDelay, giving a
//    stable 100 ms window in which to always find a from/to state pair.
//  • Timestamping uses Time.timeAsDouble (local monotonic clock) for
//    high-resolution, non-rewinding time values suitable for sub-frame math.
//  • AddState() accepts a typed TransformState snapshot so that position,
//    rotation, and scale are always transported together without extra copies.
//  • Division-by-zero is guarded when two states share the same timestamp
//    (packets decoded in the same frame): the from-state is returned as-is.
//  • Quaternion.Slerp is used directly for rotation; Unity already selects
//    the shortest arc internally.
//  • No per-frame heap allocations: internal storage is a pre-allocated List<T>
//    used as a ring buffer; Update() accesses elements by index.
//  • Settings are [SerializeField] fields on the component (Unity convention),
//    keeping Inspector integration straightforward.
//  • Default buffer size of 10 provides ~7 states of margin over the minimum
//    2-state requirement at 30 Hz + 100 ms delay (~720 B total).
//  • Scale interpolation is opt-in (_interpolateScale = false by default)
//    matching the NetworkTransform default of not syncing scale.
//
// Threading: all public methods and Update() run on the Unity main thread.
// TryInterpolate(double) is pure logic and testable without a Unity scene.

using System.Collections.Generic;
using UnityEngine;

namespace RTMPE.Sync
{
    /// <summary>
    /// Buffers server-received <see cref="TransformState"/> snapshots and applies
    /// smooth interpolated movement to this <see cref="GameObject"/> each frame.
    ///
   /// Add to any networked prefab alongside <see cref="NetworkTransform"/>.
    /// Call <see cref="AddState"/> whenever a <c>StateDelta</c> is decoded for
    /// this object (typically wired to <c>NetworkManager.OnDataReceived</c>).
    /// </summary>
    /// <remarks>
    /// The <see cref="DefaultExecutionOrder"/> attribute pins this component
    /// to a high (late) execution slot so render-time consumers — Camera
    /// follows, animation IK, gameplay scripts that read transform.position —
    /// see the interpolated pose for the current frame.  Without an explicit
    /// order, Unity's registration-order tie-breaker would let any user
    /// script with a higher script-execution priority observe the previous
    /// frame's pose, producing a one-frame lag artefact that is hard to
    /// reproduce because it depends on import ordering.  The chosen value
    /// (10000) sits comfortably after default user scripts (0) and most
    /// animation systems while leaving headroom (max int32) for downstream
    /// post-processing components.
    /// </remarks>
    [AddComponentMenu("RTMPE/Network Transform Interpolator")]
    [DefaultExecutionOrder(10000)]
    public class NetworkTransformInterpolator : MonoBehaviour
    {
        // ── Inspector configuration ────────────────────────────────────────────

        [Header("Interpolation Settings")]
        [Tooltip("Maximum number of states to keep in the delay buffer.  " +
                 "At 30 Hz with 100 ms delay, ~3 states are in the window; " +
                 "10 adds margin for jitter without meaningful memory cost.")]
        [SerializeField] [Range(2, 64)] private int _bufferSize = 10;

        [Tooltip("How far behind real-time (seconds) to render, ensuring there is always " +
                 "a 'from' and 'to' state available for interpolation.")]
        [SerializeField] [Range(0.05f, 0.5f)] private float _interpolationDelay = 0.1f;

        [Tooltip("Also interpolate local-space scale.  Disabled by default to match " +
                 "NetworkTransform._syncScale = false.")]
        [SerializeField] private bool _interpolateScale = false;

        [Tooltip("Maximum accepted skew (seconds) into the future relative to " +
                 "the local clock.  Defends the interpolator against a hostile " +
                 "or buggy sender that puts double.MaxValue (or any far-future " +
                 "value) into the timestamp field — such a payload would " +
                 "otherwise permanently freeze the buffer because no subsequent " +
                 "timestamp could ever be greater.  10 seconds of forward skew " +
                 "comfortably absorbs every realistic clock drift while still " +
                 "rejecting double.MaxValue and similarly-absurd injections. " +
                 "Expressed as a relative offset rather than an absolute wall " +
                 "so persistent-world / social-VR sessions whose Time.timeAsDouble " +
                 "exceeds 24h continue to interpolate normally.")]
        [SerializeField] private double _maxFutureSkewSeconds = 10.0;

        // ── Buffer state ───────────────────────────────────────────────────────

        /// <summary>
        /// One timestamped snapshot in the delay buffer.
        /// Timestamp is the local <c>Time.timeAsDouble</c> at receive time.
        /// </summary>
        private struct TimestampedState
        {
            public double         Timestamp;
            public TransformState State;
        }

        // Ring-buffer storage: _head is the logical index of the oldest valid entry.
        // Entries are overwritten in-place once the buffer is full, giving O(1)
        // insertions regardless of buffer size.
        private readonly List<TimestampedState> _buffer = new List<TimestampedState>();
        private int _head; // index of the oldest valid entry (logical index 0)

        // Tracks the largest timestamp ever accepted. States with an equal or
        // smaller timestamp are discarded to maintain chronological buffer order
        // despite out-of-order UDP delivery or duplicate packets.
        private double _latestTimestamp = double.MinValue;

        // ── Sender-clock alignment ─────────────────────────────────────────────
        //
        // The receiver-clock AddState path (above) timestamps each snapshot at
        // the moment the packet leaves the network thread.  Under jitter that
        // collapses sender intervals: two snapshots produced 33 ms apart on
        // the server can land 5 ms apart on the receiver, and the interpolation
        // segment between them runs 6× faster than the underlying motion.
        //
        // The sender-tick AddState overload below converts the wire tick into
        // a sender-domain timestamp, then offsets it into receiver wall-clock
        // space using a low-pass filter on the (receiver_now - sender_time)
        // delta.  States separated by N sender ticks remain N × tickInterval
        // apart in the buffer regardless of network jitter.  This is the same
        // pattern Quake3 / Source / Overwatch use for their snapshot streams.
        //
        // The offset is a single double accumulator updated as an exponential
        // moving average: offset := offset + alpha * (sample - offset).  Alpha
        // is chosen so the filter has a ~1 s time constant at 30 Hz (alpha ≈
        // 0.033), which absorbs single-packet jitter without lagging through a
        // genuine clock skew that develops over seconds.
        private double _clockOffset;        // receiver_now - sender_time, EMA
        private bool   _hasClockOffset;     // false until the first tick sample
        private const double ClockOffsetAlpha = 1.0 / 30.0;

        // Highest sender tick observed, for wrap-safe out-of-order rejection.
        // Mirrors the modular arithmetic used by InputBuffer / NetworkVariable
        // so the whole SDK observes one wrap discipline.
        private uint _latestSenderTick;
        private bool _hasSenderTick;

        // Cursor hint for the bracketing-pair search in TryInterpolate.
        // Remote timestamps are monotonic by construction (AddState rejects
        // out-of-order writes; AddStateFromSenderTick filters via wrap-safe
        // sender-tick gating) and the per-frame render time advances
        // monotonically with Time.timeAsDouble — together this makes the
        // search amortised O(1) via a logical-index cursor that only ever
        // advances forward.  Without the cursor the inner loop walks every
        // sample under the lock on every Update; at 5 000 networked objects
        // and a 64-cap ring this is hundreds of thousands of lock-protected
        // index reads per frame.
        //
        // _bracketCursor is a LOGICAL index relative to _head (i.e. 0 = oldest
        // valid entry).  It is reset to 0 whenever the buffer is cleared, the
        // ring head wraps past it (overwrite of the slot it points at), or an
        // out-of-order sample lands at or below the cursor's left edge — any
        // of which would otherwise let the search return a stale pair.
        private int _bracketCursor;
        // Tracks whether a Configure / Clear has invalidated the cursor since
        // the last successful search; used to skip the optimistic "start from
        // _bracketCursor" path until the buffer is repopulated.
        private bool _bracketCursorValid;

        // Lock guarding all ring-buffer mutations and reads (_buffer, _head,
        // _latestTimestamp).  The SDK convention routes packet callbacks through
        // MainThreadDispatcher, so in practice AddState() runs on the Unity main
        // thread; but the PUBLIC API allows any caller, and Update() also reads
        // on the main thread — a misbehaving integration (e.g. custom transport
        // forgetting to marshal) would otherwise corrupt the ring buffer with no
        // diagnostic.  The lock is uncontended in the common case (same thread
        // always), so overhead is one interlocked CAS per call (~20 ns).
        private readonly object _syncRoot = new object();

        // ── Properties (test-visible) ──────────────────────────────────────────

        /// <summary>
        /// Number of states currently held in the delay buffer.
        /// Useful for Inspector debug display and unit tests.
        /// </summary>
        public int BufferCount
        {
            // Read under the same lock as AddState/TryInterpolate so the
            // returned count is coherent with the internal state (not a
            // racing mid-write List.Count that briefly observes the wrong
            // value during Add/Clear).
            get { lock (_syncRoot) return _buffer.Count; }
        }

        /// <summary>
        /// The configured interpolation delay in seconds.
        /// Read-only after construction; set via Inspector or
        /// <see cref="ConfigureForTest"/> (test use only).
        /// </summary>
        public float InterpolationDelaySeconds => _interpolationDelay;

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Enqueue a received server snapshot.
        /// Call this on the main thread when a <c>StateDelta</c> is decoded for
        /// this object ID.
        ///
       /// <paramref name="timestamp"/> should be <c>Time.timeAsDouble</c> at the
        /// moment the packet was received so that the interpolation cursor
        /// (<c>Time.timeAsDouble - _interpolationDelay</c>) can locate a
        /// surrounding pair.
        /// </summary>
        /// <param name="state">Decoded transform snapshot.</param>
        /// <param name="timestamp">
        /// Local monotonic receive time (<c>Time.timeAsDouble</c>).
        /// </param>
        public void AddState(TransformState state, double timestamp)
        {
            // Reject non-finite (NaN, +Inf, -Inf) and absurd far-future
            // timestamps at the entry point.  A double.MaxValue payload would
            // otherwise lock the buffer permanently — any subsequent legitimate
            // timestamp would compare strictly less than _latestTimestamp and
            // be silently dropped.  The interpolator has its own ingress
            // independent of the InputPayload parser and needs the same gate.
            //
            // Skew expressed RELATIVE to the local clock (Time.timeAsDouble),
            // not as an absolute wall — Time.timeAsDouble is process-uptime
            // and never resets within a session, so an absolute 24-hour wall
            // froze every persistent-world / social-VR session past the
            // first day of uptime.  10 seconds of forward skew comfortably
            // absorbs every realistic clock drift while still rejecting
            // double.MaxValue and similar far-future injections.
            if (!double.IsFinite(timestamp)
                || timestamp - UnityEngine.Time.timeAsDouble > _maxFutureSkewSeconds)
                return;

            // Reject non-finite components in the snapshot itself.  The
            // network parser rejects NaN/Inf positions on the receive thread,
            // but a caller that constructs a TransformState in user code (a
            // test harness, a custom dispatcher) can bypass that path —
            // and the downstream Vector3.Lerp / Quaternion.Slerp would then
            // propagate NaN into _lastInterpolatedPose and from there into
            // transform.position / transform.rotation, which Unity persists
            // unchecked.  Quenching at ingress keeps the buffer free of
            // poisoned entries and matches the snapshot's wire-format
            // contract.
            if (!IsFiniteSnapshot(state))
                return;

            lock (_syncRoot)
            {
                // Discard out-of-order and duplicate states — only strictly newer
                // timestamps advance the ring buffer.
                if (timestamp <= _latestTimestamp) return;
                _latestTimestamp = timestamp;

                // Fill the backing list to capacity on initial population, then
                // overwrite the oldest slot in-place — O(1) regardless of buffer size.
                if (_buffer.Count < _bufferSize)
                {
                    _buffer.Add(new TimestampedState { Timestamp = timestamp, State = state });
                    // _head stays 0 while filling; oldest is always index 0 during fill.
                }
                else
                {
                    // Overwrite the oldest slot and advance the ring head.
                    _buffer[_head] = new TimestampedState { Timestamp = timestamp, State = state };
                    _head = (_head + 1) % _bufferSize;
                    // The slot the cursor points at may have just been
                    // overwritten by the new write, and the entire logical
                    // window shifted left by one.  Pull the cursor in by one
                    // step (clamped at zero) so the next search resumes at a
                    // still-valid position rather than walking off the new
                    // oldest entry.
                    if (_bracketCursorValid)
                    {
                        _bracketCursor = _bracketCursor > 0 ? _bracketCursor - 1 : 0;
                    }
                }
            }
        }

        /// <summary>
        /// Enqueue a snapshot stamped by the sender's tick number.  The wire
        /// tick is mapped into receiver wall-clock space via an exponentially-
        /// smoothed clock-offset estimator so jitter on the receive path does
        /// not collapse the inter-snapshot interval the interpolator sees.
        ///
        /// <para>Out-of-order rejection uses 32-bit modular sequence-number
        /// arithmetic (RFC 1982 §3.2): a tick is accepted iff
        /// <c>(int)(senderTick - latestSenderTick) &gt; 0</c> after the first
        /// sample.  This is the same wrap discipline used by
        /// <c>InputBuffer</c> and <c>NetworkVariable</c> so a uint32 wrap that
        /// happens mid-session does not silently freeze the buffer.</para>
        ///
        /// <para><paramref name="receiverNow"/> should be
        /// <c>Time.timeAsDouble</c> at receive time so the clock-offset
        /// estimator stays anchored to the same render-cursor clock that
        /// <see cref="Update"/> reads.</para>
        ///
        /// <para><paramref name="tickIntervalSeconds"/> is the sender's
        /// tick period (<c>1 / tickRate</c>); reading
        /// <c>NetworkSettings.TickInterval</c> on the call site keeps the SDK
        /// to a single source of truth for the tick rate.</para>
        /// </summary>
        public void AddStateFromSenderTick(
            TransformState state,
            uint           senderTick,
            double         receiverNow,
            double         tickIntervalSeconds)
        {
            // Reject pathological tick interval values defensively — a zero
            // or negative interval would map every tick to the same sender
            // time, collapsing the buffer.  A non-finite value is also a
            // protocol bug we do not propagate.
            if (!double.IsFinite(receiverNow)
                || !double.IsFinite(tickIntervalSeconds)
                || tickIntervalSeconds <= 0.0)
                return;

            lock (_syncRoot)
            {
                // ── Wrap-safe out-of-order check ─────────────────────────────
                // Signed-difference comparison treats two unsigned values as
                // "near" on the 32-bit ring when the gap is < 2^31; any
                // realistic gameplay backlog (a few hundred ticks at most) is
                // orders of magnitude below that threshold.
                if (_hasSenderTick && (int)(senderTick - _latestSenderTick) <= 0)
                    return;

                // ── Sender-domain timestamp ──────────────────────────────────
                // Promote tick to double BEFORE multiplying so a tick close
                // to uint.MaxValue does not overflow during the conversion.
                double senderTime = (double)senderTick * tickIntervalSeconds;

                // ── Clock-offset EMA ─────────────────────────────────────────
                // Sample = receiver_now - sender_time.  Initial sample is
                // adopted directly (no warm-up bias); subsequent samples are
                // low-pass filtered with ClockOffsetAlpha so a single jittery
                // packet does not yank the render cursor.
                double sample = receiverNow - senderTime;
                if (!_hasClockOffset)
                {
                    _clockOffset    = sample;
                    _hasClockOffset = true;
                }
                else
                {
                    _clockOffset += ClockOffsetAlpha * (sample - _clockOffset);
                }

                // Stored timestamp lives in the receiver wall-clock domain so
                // the existing TryInterpolate(renderTime) path — which reads
                // Time.timeAsDouble - delay — needs no changes.
                double timestamp = senderTime + _clockOffset;

                // The interpolator's AddState(state, timestamp) path enforces
                // strict monotonic timestamps; the sender-tick path enforces
                // strict monotonic ticks instead.  Persist the high-water
                // tick FIRST so a duplicate timestamp (rare under EMA) does
                // not block subsequent tick-strict-greater states.
                _latestSenderTick = senderTick;
                _hasSenderTick    = true;

                // Bypass the per-timestamp monotonicity guard inside
                // AddState — the sender-tick guard above is the authoritative
                // ordering signal here.  Inline the buffer push so two
                // adjacent ticks whose receiver-domain timestamps round to
                // the same EMA value do not silently drop the second.
                if (!double.IsFinite(timestamp)
                    || timestamp - UnityEngine.Time.timeAsDouble > _maxFutureSkewSeconds)
                    return;

                // Track the highest stored timestamp so a later receiver-clock
                // AddState() call (mixed-mode integration) cannot insert an
                // older state in front of the sender-tick ordering.
                if (timestamp > _latestTimestamp) _latestTimestamp = timestamp;

                if (_buffer.Count < _bufferSize)
                {
                    _buffer.Add(new TimestampedState { Timestamp = timestamp, State = state });
                }
                else
                {
                    _buffer[_head] = new TimestampedState { Timestamp = timestamp, State = state };
                    _head = (_head + 1) % _bufferSize;
                    if (_bracketCursorValid)
                    {
                        _bracketCursor = _bracketCursor > 0 ? _bracketCursor - 1 : 0;
                    }
                }
            }
        }

        /// <summary>
        /// Current sender-clock offset estimate (receiver_now - sender_time).
        /// Exposed for diagnostics and unit tests; converges to a stable value
        /// after a few seconds of streaming snapshots.
        /// </summary>
        internal double ClockOffsetEstimate
        {
            get
            {
                lock (_syncRoot)
                    return _hasClockOffset ? _clockOffset : 0.0;
            }
        }

        /// <summary>
        /// Core interpolation logic — pure function, separated from the Unity
        /// frame callback for deterministic unit testing.
        ///
       /// Searches the delay buffer for the pair of states that bracket
        /// <paramref name="renderTime"/> and returns the linearly/spherically
        /// interpolated result.
        /// </summary>
        /// <param name="renderTime">
        /// The target render time.  Typically <c>Time.timeAsDouble - _interpolationDelay</c>.
        /// </param>
        /// <param name="result">
        /// On success: the interpolated <see cref="TransformState"/>.
        /// Undefined on failure.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when a valid interpolated state was produced.
        /// <see langword="false"/> when:
        /// <list type="bullet">
        ///  <item>The buffer has fewer than 2 states.</item>
        ///  <item><paramref name="renderTime"/> is before the oldest buffered state.</item>
        ///  <item><paramref name="renderTime"/> is after the newest buffered state.</item>
        /// </list>
        /// The caller (e.g. <see cref="Update"/>) should be a no-op on false.
        /// </returns>
        public bool TryInterpolate(double renderTime, out TransformState result)
        {
            result = default;

            // Snapshot the two buffered states we need while holding the lock,
            // then release it before the Vector/Quaternion math.  This keeps
            // the critical section tiny (no floating-point work under the lock)
            // and ensures AddState() is never blocked by per-frame arithmetic.
            TimestampedState from;
            TimestampedState to;
            lock (_syncRoot)
            {
                // Need at least two states to define an interpolation segment.
                if (_buffer.Count < 2) return false;

                // With the ring buffer, states are stored in logical order starting at
                // _head.  Logical index i maps to physical (_head + i) % count.
                //
                // Monotonic remote timestamps make the bracketing-pair search
                // amortised O(1) via a cursor that advances with render time;
                // resetting on reset / older-than-cursor samples preserves
                // correctness when streams restart.  Without this hint the
                // inner loop is O(N) under the lock per frame per object,
                // which under interest-managed broadcast scales to hundreds
                // of thousands of lock-protected reads at fleet scale.
                int count = _buffer.Count;

                // If the cursor is stale (Clear / ConfigureForTest / first
                // call after spawn) start from logical index 0 and rebuild.
                int start = _bracketCursorValid ? _bracketCursor : 0;
                if (start > count - 2) start = 0; // ring shrank under us
                int fromIndex = -1;

                // Adversarial guard: if renderTime is older than the cursor's
                // left edge (a legitimate clock rewind, or a fresh stream
                // arriving with smaller sender-tick timestamps that the EMA
                // mapped behind the cursor), restart at logical 0 so the
                // search can still locate a valid pair.  Without this the
                // forward walk would never revisit the older window.
                int startPhys = (_head + start) % count;
                if (_buffer[startPhys].Timestamp > renderTime) start = 0;

                for (int i = start; i < count - 1; i++)
                {
                    int iA = (_head + i)     % count;
                    int iB = (_head + i + 1) % count;
                    if (_buffer[iA].Timestamp <= renderTime && _buffer[iB].Timestamp >= renderTime)
                    {
                        fromIndex = i;
                        break;
                    }
                }

                // renderTime is outside the buffered range — no interpolation possible.
                if (fromIndex < 0) return false;

                _bracketCursor      = fromIndex;
                _bracketCursorValid = true;

                int physFrom = (_head + fromIndex)     % count;
                int physTo   = (_head + fromIndex + 1) % count;
                from = _buffer[physFrom];
                to   = _buffer[physTo];
            }

            // Guard against division by zero when two states share the same
            // timestamp (e.g. two packets decoded in the same frame).
            double span = to.Timestamp - from.Timestamp;
            float t;
            if (span < double.Epsilon)
                t = 0f; // identical timestamps → return from-state unchanged
            else
                t = Mathf.Clamp01((float)((renderTime - from.Timestamp) / span));

            result = new TransformState
            {
                Position = Vector3.Lerp(from.State.Position, to.State.Position, t),

                // Quaternion.Slerp selects the shortest arc automatically.
                Rotation = Quaternion.Slerp(from.State.Rotation, to.State.Rotation, t),

                // When scale interpolation is disabled, snap to the destination
                // value so callers that apply scale receive a stable result.
                Scale    = _interpolateScale
                    ? Vector3.Lerp(from.State.Scale, to.State.Scale, t)
                    : to.State.Scale,
            };
            return true;
        }

        // ── Unity lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// Each frame: advance the render cursor by <see cref="_interpolationDelay"/>
        /// seconds behind real time and apply the interpolated transform.
        /// No-op when fewer than 2 states are buffered (e.g. at startup).
        /// </summary>
        private void Update()
        {
            // Render cursor: local monotonic time minus the configured delay.
            // Using Time.timeAsDouble gives sub-millisecond precision without
            // the drift risk of float accumulation.
            double renderTime = Time.timeAsDouble - _interpolationDelay;

            if (TryInterpolate(renderTime, out TransformState state))
                ApplyToTransform(state);
        }

        // ── Private helpers ────────────────────────────────────────────────────

        // Apply an interpolated state to this object's Transform components.
        // Matches the axis-gate pattern in NetworkTransform.ApplyState().
        private void ApplyToTransform(TransformState state)
        {
            transform.position = state.Position;
            transform.rotation = state.Rotation;
            if (_interpolateScale) transform.localScale = state.Scale;
        }

        // ── Test seam ─────────────────────────────────────────────────────────
        //
       // ConfigureForTest allows unit tests to set the buffer parameters without
        // going through Unity serialisation.  Accessible via InternalsVisibleTo.

        /// <summary>
        /// Set buffer configuration without Unity Inspector serialisation.
        /// <b>For unit tests only.</b>
        /// </summary>
        /// <param name="bufferSize">Maximum number of buffered states.</param>
        /// <param name="interpolationDelay">Seconds behind real-time to render.</param>
        /// <param name="interpolateScale">Whether scale is interpolated.</param>
        internal void ConfigureForTest(
            int    bufferSize             = 10,
            float  interpolationDelay     = 0.1f,
            bool   interpolateScale       = false,
            double maxFutureSkewSeconds   = 10.0)
        {
            lock (_syncRoot)
            {
                _bufferSize             = bufferSize;
                _interpolationDelay     = interpolationDelay;
                _interpolateScale       = interpolateScale;
                _maxFutureSkewSeconds   = maxFutureSkewSeconds;
                // Reset ring-buffer state for a consistent starting condition.
                _buffer.Clear();
                _head = 0;
                // Reset the high-water timestamp so subsequent AddState calls
                // with small timestamps (test vectors) are not silently dropped.
                _latestTimestamp = double.MinValue;
                // Bracketing-pair cursor is paired to the live buffer; a fresh
                // fixture must restart the search at logical index 0.
                _bracketCursor      = 0;
                _bracketCursorValid = false;
                // Sender-clock estimator state: a fresh fixture must start
                // without any inherited tick / offset bias.
                _hasSenderTick   = false;
                _latestSenderTick = 0u;
                _hasClockOffset  = false;
                _clockOffset     = 0.0;
            }
        }

        // Componentwise IsFinite over the position, rotation, and (when
        // enabled) scale of an inbound TransformState.  Spelled with
        // !IsNaN && !IsInfinity so the SDK compiles on Unity runtimes that
        // do not expose float.IsFinite.
        private bool IsFiniteSnapshot(TransformState s)
        {
            var p = s.Position;
            var r = s.Rotation;
            if (Bad(p.x) || Bad(p.y) || Bad(p.z)) return false;
            if (Bad(r.x) || Bad(r.y) || Bad(r.z) || Bad(r.w)) return false;
            if (_interpolateScale)
            {
                var c = s.Scale;
                if (Bad(c.x) || Bad(c.y) || Bad(c.z)) return false;
            }
            return true;
        }

        private static bool Bad(float v) => float.IsNaN(v) || float.IsInfinity(v);
    }
}
