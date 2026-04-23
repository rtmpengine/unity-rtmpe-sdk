// RTMPE SDK — Runtime/Sync/NetworkTransform.cs
//
// MonoBehaviour component that synchronises a GameObject's position, rotation,
// and optionally scale over the RTMPE network, with optional client-side
// prediction (CSP) for the owning client.
//
// Design decisions:
//   • Extends NetworkBehaviour — inherits NetworkObjectId, IsOwner,
//     IsSpawned, and the OnNetworkSpawn / OnNetworkDespawn callbacks.
//   • Owner-only sending: only the authoritative owner sends transform updates.
//     All other clients receive server-broadcast StateDelta payloads via the
//     HandleStateSyncPacket handler in NetworkManager.
//   • Two thresholds guard against send spam:
//     - _positionThreshold (0.01 world units): Vector3.Distance check
//     - _rotationThreshold (0.1 degrees):      Quaternion.Angle check
//   • MarkClean() records the last-sent transform so the next Update() compares
//     against that baseline, not the object's initial spawn position.
//   • GetState() / ApplyState() provide a type-safe boundary between Unity
//     transform fields and the serialisation layer, enabling unit testing.
//   • _syncScale defaults to false because scale rarely changes at runtime.
//
// Client-side prediction (CSP) — optional, disabled by default:
//   When _enablePrediction is true the owner client:
//     1. Calls GatherInput() each 30 Hz tick, stamps the tick, pushes it to
//        _inputBuffer.  Game code moves the character immediately (prediction).
//     2. Sends the resulting transform to the server as usual.
//     3. When the server broadcasts back an authoritative StateDelta for this
//        object, ApplyReconciliation() fires (routed by NetworkManager):
//          • Error > _snapThreshold  →  snap directly to server position.
//          • Error > _lerpThreshold  →  start a 3-frame smooth lerp toward
//            the server position (_reconcileFramesLeft drives the blend).
//          • Error <= _lerpThreshold →  accept prediction as-is (no visual pop).
//     4. AcknowledgeUpTo(LocalTick - 1) trims the buffer each reconciliation.
//
//   Non-owning clients are unaffected by the prediction fields — they use the
//   NetworkTransformInterpolator for smooth playback.
//
// Threading: all methods run on the Unity main thread.

using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Sync
{
    /// <summary>
    /// Synchronises a <see cref="GameObject"/>'s Transform over the RTMPE network.
    /// Attach this component to any networked prefab together with a
    /// <see cref="NetworkObjectRegistry"/> identifier.
    /// </summary>
    [AddComponentMenu("RTMPE/Network Transform")]
    public class NetworkTransform : NetworkBehaviour
    {
        // ── Inspector — Sync axes ──────────────────────────────────────────────

        [Header("Sync Axes")]
        [Tooltip("Sync world-space position.")]
        [SerializeField] private bool _syncPosition = true;

        [Tooltip("Sync world-space rotation.")]
        [SerializeField] private bool _syncRotation = true;

        [Tooltip("Sync local-space scale. Disabled by default (scale rarely changes).")]
        [SerializeField] private bool _syncScale = false;

        // ── Inspector — Send thresholds ────────────────────────────────────────

        [Header("Send Thresholds")]
        [Tooltip("Minimum position change in world units before an update is sent.")]
        [SerializeField] private float _positionThreshold = 0.01f;

        [Tooltip("Minimum rotation change in degrees before an update is sent.")]
        [SerializeField] private float _rotationThreshold = 0.1f;

        [Tooltip("Minimum local-scale change per axis before an update is sent. " +
                 "Only evaluated when _syncScale is enabled.")]
        [SerializeField] private float _scaleThreshold = 0.001f;

        // ── Inspector — Client-side prediction ────────────────────────────────

        [Header("Client-Side Prediction")]
        [Tooltip("Enable client-side prediction and server reconciliation for the owning client. " +
                 "Requires game code to override GatherInput() on the NetworkBehaviour subclass.")]
        [SerializeField] private bool _enablePrediction = false;

        [Tooltip("Position error (world units) below which the prediction is accepted as-is.")]
        [SerializeField] private float _lerpThreshold = 0.1f;

        [Tooltip("Position error (world units) above which the object snaps immediately to the " +
                 "server position rather than lerping.")]
        [SerializeField] private float _snapThreshold = 2.0f;

        // ── Last-sent baseline ─────────────────────────────────────────────────

        private Vector3    _lastPosition;
        private Quaternion _lastRotation;
        private Vector3    _lastScale;

        // ── CSP state ──────────────────────────────────────────────────────────

        // Input ring buffer: stores unacknowledged InputPayloads for rollback.
        private readonly InputBuffer _inputBuffer = new InputBuffer();

        // Reconciliation lerp target and remaining-frames counter.
        // When _reconcileFramesLeft > 0, Update() blends toward _reconciledTarget.
        private Vector3 _reconciledTarget;
        private int     _reconcileFramesLeft;

        // Guards input collection to exactly one push per LocalTick.
        // Update() runs at frame rate (e.g. 60 Hz) but LocalTick advances at 30 Hz;
        // without this guard the buffer would accumulate two entries per tick at 60 fps.
        private uint _lastInputTick = uint.MaxValue;

        // Lerp fraction per frame: blend 1/3 of the remaining error each of
        // the 3 frames so the correction is visually smooth.
        private const float ReconcileLerpFraction = 1f / 3f;

        // ── Change-detection properties ────────────────────────────────────────

        /// <summary>
        /// True when the object has moved more than <c>_positionThreshold</c>
        /// world units since the last <see cref="MarkClean"/> call.
        /// </summary>
        public bool HasPositionChanged
            => Vector3.Distance(transform.position, _lastPosition) > _positionThreshold;

        /// <summary>
        /// True when the object has rotated more than <c>_rotationThreshold</c>
        /// degrees since the last <see cref="MarkClean"/> call.
        /// </summary>
        public bool HasRotationChanged
            => Quaternion.Angle(transform.rotation, _lastRotation) > _rotationThreshold;

        /// <summary>
        /// True when <c>_syncScale</c> is enabled and the object's local scale
        /// has changed by more than <c>_scaleThreshold</c> per axis since the
        /// last <see cref="MarkClean"/> call.
        /// </summary>
        public bool HasScaleChanged
            => _syncScale && Vector3.Distance(transform.localScale, _lastScale) > _scaleThreshold;

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Capture the current transform into a <see cref="TransformState"/> snapshot.
        /// </summary>
        public TransformState GetState() => new TransformState
        {
            Position = transform.position,
            Rotation = transform.rotation,
            Scale    = transform.localScale,
        };

        /// <summary>
        /// Apply a received <see cref="TransformState"/> to this object's transform.
        /// Only axes with the corresponding sync flag enabled are written.
        /// </summary>
        public void ApplyState(TransformState state)
        {
            if (_syncPosition) transform.position   = state.Position;
            if (_syncRotation) transform.rotation   = state.Rotation;
            if (_syncScale)    transform.localScale = state.Scale;
        }

        /// <summary>
        /// Record the current transform as the new "last-sent" baseline.
        /// Call this after sending an update so the next <see cref="Update"/>
        /// compares against the just-sent values, not the initial spawn position.
        /// </summary>
        public void MarkClean()
        {
            _lastPosition = transform.position;
            _lastRotation = transform.rotation;
            _lastScale    = transform.localScale;
        }

        // ── Internal CSP API (called by NetworkManager) ───────────────────────

        /// <summary>
        /// Apply a server-authoritative <see cref="TransformState"/> to the owning
        /// client's prediction.  Called by <c>NetworkManager.HandleStateSyncPacket</c>
        /// when a <c>StateDelta</c> for this object is received by its owner.
        ///
        /// <para>When <c>_enablePrediction</c> is false this is a no-op —
        /// non-prediction owners do not reconcile.</para>
        /// </summary>
        internal void ApplyReconciliation(TransformState serverState)
        {
            if (!_enablePrediction) return;

            // Acknowledge all buffered inputs up to the current tick minus one.
            // The server has now confirmed state reflecting those inputs.
            var nm = NetworkManager.Instance;
            if (nm != null && nm.LocalTick > 0)
                _inputBuffer.AcknowledgeUpTo(nm.LocalTick - 1);

            float error = Vector3.Distance(serverState.Position, transform.position);

            if (error <= _lerpThreshold)
            {
                // Prediction was close enough — accept it, no visual correction.
                return;
            }

            if (error >= _snapThreshold)
            {
                // Large error — snap immediately to avoid sustained visual drift.
                if (_syncPosition) transform.position = serverState.Position;
                if (_syncRotation) transform.rotation = serverState.Rotation;
                _reconcileFramesLeft = 0;
                // Update baseline so the next frame does not register a spurious
                // threshold violation and send the snapped position back to the server.
                MarkClean();
                return;
            }

            // Medium error — smooth lerp over 3 frames.
            _reconciledTarget    = serverState.Position;
            _reconcileFramesLeft = 3;
        }

        // ── Unity lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// After the object is spawned, record the current transform baseline
        /// and reset prediction state so the first change-detection comparison
        /// is against the spawn position.
        /// </summary>
        protected override void OnNetworkSpawn()
        {
            MarkClean();
            _inputBuffer.Clear();
            _reconcileFramesLeft = 0;
            _lastInputTick       = uint.MaxValue;
        }

        /// <summary>
        /// Each frame:
        ///   • Owner with prediction enabled: gather input, push to buffer.
        ///   • Owner (with or without prediction): if transform changed beyond
        ///     thresholds, transmit an update and mark clean.
        ///   • Reconciliation lerp: if pending, blend toward server target.
        /// </summary>
        private void Update()
        {
            if (!IsOwner || !IsSpawned) return;

            // ── CSP input collection (once per 30 Hz tick) ───────────────────
            // Update() runs at frame rate; LocalTick advances at 30 Hz from
            // NetworkManager.Update().  Only push when the tick has changed so
            // the buffer holds at most one entry per tick regardless of frame rate.
            if (_enablePrediction)
            {
                var nm = NetworkManager.Instance;
                if (nm != null && nm.LocalTick != _lastInputTick)
                {
                    _lastInputTick = nm.LocalTick;
                    var input = CollectInput(nm.LocalTick);
                    _inputBuffer.Push(input);
                }
            }

            // ── Transform send ────────────────────────────────────────────────
            if (HasPositionChanged || HasRotationChanged || HasScaleChanged)
            {
                SendTransformUpdate();
                MarkClean();
            }

            // ── Reconciliation lerp ───────────────────────────────────────────
            if (_reconcileFramesLeft > 0)
            {
                transform.position = Vector3.Lerp(
                    transform.position,
                    _reconciledTarget,
                    ReconcileLerpFraction);
                _reconcileFramesLeft--;
            }
        }

        // ── Private helpers ────────────────────────────────────────────────────

        private void SendTransformUpdate()
        {
            var manager = NetworkManager.Instance;
            if (manager == null) return;

            var payload = TransformPacketBuilder.BuildUpdatePayload(NetworkObjectId, GetState());
            manager.SendData(payload);
        }
    }
}
