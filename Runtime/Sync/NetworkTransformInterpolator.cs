// RTMPE SDK — Runtime/Sync/NetworkTransformInterpolator.cs
//
// Buffered interpolation for smooth movement of non-owner networked objects.
//
// Design decisions:
//   • Extends MonoBehaviour so Update() fires automatically on the main thread.
//   • Owner-only suppression: AddState() and Update() are both valid on any
//     client, but by convention only non-owner clients call AddState().  The
//     owning client uses NetworkTransform.Update() to SEND; the interpolator
//     is wired to RECEIVE via NetworkManager.OnDataReceived.
//   • Timestamping uses Time.timeAsDouble (local monotonic clock) at the moment
//     of receipt — no network time-synchronisation required at this stage.
//     The render cursor is Time.timeAsDouble - _interpolationDelay, giving a
//     stable 100 ms window in which to always find a from/to state pair.
//   • Timestamping uses Time.timeAsDouble (local monotonic clock) for
//     high-resolution, non-rewinding time values suitable for sub-frame math.
//   • AddState() accepts a typed TransformState snapshot so that position,
//     rotation, and scale are always transported together without extra copies.
//   • Division-by-zero is guarded when two states share the same timestamp
//     (packets decoded in the same frame): the from-state is returned as-is.
//   • Quaternion.Slerp is used directly for rotation; Unity already selects
//     the shortest arc internally.
//   • No per-frame heap allocations: internal storage is a pre-allocated List<T>
//     used as a ring buffer; Update() accesses elements by index.
//   • Settings are [SerializeField] fields on the component (Unity convention),
//     keeping Inspector integration straightforward.
//   • Default buffer size of 10 provides ~7 states of margin over the minimum
//     2-state requirement at 30 Hz + 100 ms delay (~720 B total).
//   • Scale interpolation is opt-in (_interpolateScale = false by default)
//     matching the NetworkTransform default of not syncing scale.
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
    [AddComponentMenu("RTMPE/Network Transform Interpolator")]
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

        // ── Properties (test-visible) ──────────────────────────────────────────

        /// <summary>
        /// Number of states currently held in the delay buffer.
        /// Useful for Inspector debug display and unit tests.
        /// </summary>
        public int BufferCount => _buffer.Count;

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
        ///   <item>The buffer has fewer than 2 states.</item>
        ///   <item><paramref name="renderTime"/> is before the oldest buffered state.</item>
        ///   <item><paramref name="renderTime"/> is after the newest buffered state.</item>
        /// </list>
        /// The caller (e.g. <see cref="Update"/>) should be a no-op on false.
        /// </returns>
        public bool TryInterpolate(double renderTime, out TransformState result)
        {
            result = default;

            // Need at least two states to define an interpolation segment.
            if (_buffer.Count < 2) return false;

            // With the ring buffer, states are stored in logical order starting at
            // _head.  A helper that reads logical index i maps to physical:
            //   physical = (_head + i) % _buffer.Count.
            // Walk forward (oldest → newest) to find the bracketing pair.
            int count = _buffer.Count;
            int fromIndex = -1;
            for (int i = 0; i < count - 1; i++)
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

            int physFrom = (_head + fromIndex)     % count;
            int physTo   = (_head + fromIndex + 1) % count;
            TimestampedState from = _buffer[physFrom];
            TimestampedState to   = _buffer[physTo];

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
            int   bufferSize         = 10,
            float interpolationDelay = 0.1f,
            bool  interpolateScale   = false)
        {
            _bufferSize          = bufferSize;
            _interpolationDelay  = interpolationDelay;
            _interpolateScale    = interpolateScale;
            // Reset ring-buffer state for a consistent starting condition.
            _buffer.Clear();
            _head = 0;
        }
    }
}
