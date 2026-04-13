// RTMPE SDK — Runtime/Sync/NetworkTransform.cs
//
// MonoBehaviour component that synchronises a GameObject's position, rotation,
// and optionally scale over the RTMPE network.
//
// Design decisions:
//   • Extends NetworkBehaviour (Week 15) — inherits NetworkObjectId, IsOwner,
//     IsSpawned, and the OnNetworkSpawn / OnNetworkDespawn callbacks.
//   • Owner-only sending: only the authoritative owner sends transform updates.
//     All other clients receive server-broadcast StateDelta payloads via
//     the OnDataReceived event on NetworkManager (handled externally by the
//     state-sync subsystem — Week 23+).
//   • Two thresholds guard against send spam:
//     - _positionThreshold (0.01 world units): Vector3.Distance check
//     - _rotationThreshold (0.1 degrees):      Quaternion.Angle check
//     Sub-threshold moves are suppressed; only meaningful changes are sent.
//   • MarkClean() stores the last-sent transform so the next Update() compares
//     against that baseline, not the object's original spawn position.
//   • GetState() / ApplyState() provide a type-safe boundary between Unity
//     transform fields and the serialisation layer (TransformPacketBuilder /
//     TransformPacketParser), enabling unit testing without a running server.
//   • _syncScale defaults to false because scale rarely changes at runtime
//     and omitting it saves 12 bytes per update packet at 30 Hz.
//
// Threading: all methods run on the Unity main thread. NetworkManager.SendData
// posts the packet to the NetworkThread via a thread-safe queue.

using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Sync
{
    /// <summary>
    /// Synchronises a <see cref="GameObject"/>'s Transform over the RTMPE network.
    /// Attach this component to any networked prefab together with a
    /// <c>NetworkObject</c> identifier.
    /// </summary>
    [AddComponentMenu("RTMPE/Network Transform")]
    public class NetworkTransform : NetworkBehaviour
    {
        // ── Inspector configuration ────────────────────────────────────────────

        [Header("Sync Axes")]
        [Tooltip("Sync world-space position.")]
        [SerializeField] private bool _syncPosition = true;

        [Tooltip("Sync world-space rotation.")]
        [SerializeField] private bool _syncRotation = true;

        [Tooltip("Sync local-space scale. Disabled by default (scale rarely changes).")]
        [SerializeField] private bool _syncScale = false;

        [Header("Send Thresholds")]
        [Tooltip("Minimum position change in world units before an update is sent.")]
        [SerializeField] private float _positionThreshold = 0.01f;

        [Tooltip("Minimum rotation change in degrees before an update is sent.")]
        [SerializeField] private float _rotationThreshold = 0.1f;

        // ── Last-sent baseline ─────────────────────────────────────────────────

        private Vector3    _lastPosition;
        private Quaternion _lastRotation;
        private Vector3    _lastScale;

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

        // ── Unity lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// After the object is spawned, record the current transform baseline
        /// so the first change-detection comparison is against the spawn position,
        /// not the default zero values.
        /// </summary>
        protected override void OnNetworkSpawn()
        {
            MarkClean();
        }

        /// <summary>
        /// Each frame: if this client owns the object and it has moved or rotated
        /// beyond the send threshold, transmit a transform update and mark clean.
        /// Non-owning clients never send (the server is the source of truth for
        /// remote object positions, received via StateDelta broadcasts).
        /// </summary>
        private void Update()
        {
            if (!IsOwner || !IsSpawned) return;

            if (HasPositionChanged || HasRotationChanged)
            {
                SendTransformUpdate();
                MarkClean();
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
