// RTMPE SDK — Runtime/Sync/NetworkRigidbody.cs
//
// MonoBehaviour component that synchronises a GameObject's 3-D Rigidbody
// physics state (position, rotation, velocity, angular velocity, sleep) over
// the RTMPE network.
//
// ── Architecture ──────────────────────────────────────────────────────────────
//
//   Owner (authoritative physics):
//     FixedUpdate() captures the Rigidbody state each physics step.
//     When any enabled field exceeds its send threshold AND the configured
//     send-rate interval has elapsed, a PhysicsSync payload is built and
//     transmitted as PacketType.StateSync (0x40) via the Sync Engine, which
//     broadcasts it to all room members at 30 Hz.
//
//   Non-owner (remote simulation):
//     ApplyRemoteState() is called by NetworkManager when a physics-sync packet
//     arrives for this object.  The received state is stored and applied each
//     FixedUpdate():
//       • MovePosition / MoveRotation smooth the body toward the authoritative
//         position, respecting physics constraints (no teleport through colliders).
//       • The received linear velocity is blended onto the Rigidbody so the
//         physics engine continues simulating between packets (no rubber-banding).
//       • Dead reckoning extrapolates the expected position using the last known
//         velocity for up to _deadReckoningTimeout seconds while no new packet
//         arrives, preventing objects from stopping mid-air between ticks.
//       • If the position error exceeds _snapThreshold, MovePosition snaps
//         immediately instead of lerping, correcting large desync without
//         prolonged visual drift.
//       • When IsSleeping is received, the remote body is put to sleep to stop
//         physics noise on stationary objects.
//
//   Owner reconciliation:
//     When the Sync Engine broadcasts the owner's own state back (because all
//     room members receive each tick), ApplyReconciliation() can optionally
//     apply a server-corrected position.  Currently a no-op placeholder — the
//     owner's local physics simulation is authoritative.
//
// ── Threading ─────────────────────────────────────────────────────────────────
//
//   All methods run on the Unity main thread.  ApplyRemoteState() is called from
//   NetworkManager (main thread) and stores state into _receivedState, which
//   FixedUpdate() reads on the next physics step (also main thread).  No lock is
//   required because both sites execute on the same thread.
//
// ── Design decisions ──────────────────────────────────────────────────────────
//
//   • Uses FixedUpdate (physics timestep) for both capture and application so
//     that Rigidbody.velocity and .angularVelocity reflect the physics engine's
//     actual state, not a mid-frame snapshot.
//   • MovePosition / MoveRotation are preferred over direct position / rotation
//     assignment on non-kinematic bodies: they participate in collision detection
//     within the physics step, preventing tunnelling.
//   • _makeRemoteKinematic: when true, the Rigidbody on non-owner clients is set
//     to kinematic on spawn, and position/rotation are applied directly.  This is
//     appropriate when the owner fully controls the body (e.g. a player character)
//     and remote clients should never simulate physics for it.
//   • Send rate defaults to 20 Hz (below the 30 Hz Sync Engine tick) to reduce
//     bandwidth while still producing smooth results with dead reckoning.
//   • Change detection uses per-field thresholds to suppress sends for micro-
//     movements (floating-point noise, sleep vibrations) that would waste bandwidth.
//   • Sleep state is only included in the payload when it changes, reducing the
//     common-case payload size for active objects.

using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Sync
{
    /// <summary>
    /// Synchronises a 3-D <see cref="UnityEngine.Rigidbody"/> over the RTMPE network.
    /// <para>
    /// Attach alongside a <see cref="NetworkBehaviour"/> subclass on any prefab
    /// that is spawned via <c>SpawnManager</c> and driven by Unity physics.
    /// </para>
    /// <para>
    /// The owner client simulates physics and sends state updates.
    /// Non-owner clients receive updates and smoothly correct their local
    /// Rigidbody using velocity blending, dead reckoning, and position lerp.
    /// </para>
    /// </summary>
    [AddComponentMenu("RTMPE/Network Rigidbody")]
    [RequireComponent(typeof(Rigidbody))]
    public class NetworkRigidbody : NetworkBehaviour
    {
        // ── Inspector — Sync toggles ───────────────────────────────────────────

        [Header("Sync Fields")]
        [Tooltip("Synchronise world-space position.")]
        [SerializeField] private bool _syncPosition = true;

        [Tooltip("Synchronise world-space rotation.")]
        [SerializeField] private bool _syncRotation = true;

        [Tooltip("Synchronise linear velocity so remote bodies continue moving between packets.")]
        [SerializeField] private bool _syncVelocity = true;

        [Tooltip("Synchronise angular velocity so remote bodies continue spinning between packets.")]
        [SerializeField] private bool _syncAngularVelocity = true;

        [Tooltip("Synchronise sleep state so remote bodies idle when the owner body is asleep.")]
        [SerializeField] private bool _syncSleepState = true;

        // ── Inspector — Send thresholds ────────────────────────────────────────

        [Header("Send Thresholds")]
        [Tooltip("Minimum position change in world units since last send before an update is sent.")]
        [SerializeField] private float _positionThreshold = 0.01f;

        [Tooltip("Minimum rotation change in degrees since last send before an update is sent.")]
        [SerializeField] private float _rotationThreshold = 0.1f;

        [Tooltip("Minimum linear velocity change (magnitude) in units/second before an update is sent.")]
        [SerializeField] private float _velocityThreshold = 0.05f;

        [Tooltip("Minimum angular velocity change (magnitude) in radians/second before an update is sent.")]
        [SerializeField] private float _angularVelocityThreshold = 0.05f;

        // ── Inspector — Remote body ────────────────────────────────────────────

        [Header("Remote Body Behaviour")]
        [Tooltip("When true, the Rigidbody on non-owner clients is set kinematic on spawn. " +
                 "Position and rotation are then applied directly without MovePosition/MoveRotation. " +
                 "Use for player characters and objects whose physics are fully owner-authoritative.")]
        [SerializeField] private bool _makeRemoteKinematic = false;

        [Tooltip("Position error in world units above which the remote body snaps immediately " +
                 "to the authoritative position instead of lerping.")]
        [SerializeField] private float _snapThreshold = 3.0f;

        [Tooltip("Speed at which the remote body lerps toward the authoritative position. " +
                 "Higher values produce tighter following at the cost of visible correction steps.")]
        [SerializeField] [Range(1f, 50f)] private float _positionCorrectionSpeed = 10f;

        [Tooltip("Speed at which the remote body slerps toward the authoritative rotation.")]
        [SerializeField] [Range(1f, 50f)] private float _rotationCorrectionSpeed = 10f;

        // ── Inspector — Owner reconciliation ───────────────────────────────────

        [Header("Owner Reconciliation")]
        [Tooltip("When true, the OWNER reconciles its local physics against the server-broadcast " +
                 "state.  Defensive snap-on-divergence: when the local position diverges from the " +
                 "server-confirmed position by more than _ownerReconcileSnapThreshold, the body " +
                 "snaps to the server position.  Below the threshold the local prediction is kept " +
                 "(no visual pop).  Disable for trusted-client deployments where the owner is fully " +
                 "authoritative; enable when an authoritative server simulates physics and emits " +
                 "corrections.")]
        [SerializeField] private bool _enableOwnerReconciliation = false;

        [Tooltip("Position error threshold (world units) above which the owner snaps to the " +
                 "server-confirmed position.  Set high enough to avoid fighting normal " +
                 "client-side prediction noise.")]
        [SerializeField] [Range(0.5f, 20f)] private float _ownerReconcileSnapThreshold = 3.0f;

        // ── Inspector — Dead reckoning ─────────────────────────────────────────

        [Header("Dead Reckoning")]
        [Tooltip("When true, the remote body's expected position is extrapolated using the " +
                 "last received velocity while no new packet has arrived.  Prevents objects " +
                 "from snapping to their last confirmed position between ticks.")]
        [SerializeField] private bool _enableDeadReckoning = true;

        [Tooltip("Seconds after the last packet after which dead reckoning stops. " +
                 "After this timeout the object is held at its last extrapolated position " +
                 "until a new packet arrives.")]
        [SerializeField] [Range(0.1f, 2.0f)] private float _deadReckoningTimeout = 0.5f;

        // ── Inspector — Send rate ──────────────────────────────────────────────

        [Header("Send Rate")]
        [Tooltip("How many times per second the owner sends a physics-state update. " +
                 "Values above 30 are clamped by the Sync Engine tick rate. " +
                 "20 Hz is the recommended default (balances bandwidth vs. smoothness).")]
        [SerializeField] [Range(1, 30)] private int _sendRateHz = 20;

        // ── Runtime state ──────────────────────────────────────────────────────

        private Rigidbody _rb;

        // Owner side: baseline for change detection.
        private PhysicsState _lastSentState;
        // Tracks whether any state has been sent yet (skips threshold on first send).
        private bool _hasSentOnce;
        // Tracks whether sleep state changed since last send (forces a send when it does).
        private bool _lastSleepState;
        // Accumulator for send rate limiting.
        private float _sendAccum;

        // Non-owner side: latest state received from the network.
        private PhysicsState _receivedState;
        // Timestamp (Time.fixedTime) when the last packet was received.
        private float _lastReceiveTime;
        // True once at least one state has been received.
        private bool _hasReceivedState;

        // ── Unity lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// Cache the Rigidbody reference and apply kinematic mode to remote bodies.
        /// Called by the SDK after <c>NetworkBehaviour.SetSpawned(true)</c>.
        /// </summary>
        protected override void OnNetworkSpawn()
        {
            _rb = GetComponent<Rigidbody>();
            if (_rb == null)
            {
                Debug.LogError("[RTMPE] NetworkRigidbody.OnNetworkSpawn: " +
                               "no Rigidbody found on this GameObject.", this);
                return;
            }

            if (!IsOwner && _makeRemoteKinematic)
                _rb.isKinematic = true;

            if (IsOwner)
            {
                // Capture spawn state as the send baseline so the first FixedUpdate
                // compares against the actual spawn transform, not default zeroes.
                _lastSentState = GetState();
                _lastSleepState = _rb.IsSleeping();
                _hasSentOnce = false;
            }

            _sendAccum = 0f;
        }

        /// <summary>
        /// Restore kinematic mode when the object leaves the network.
        /// </summary>
        protected override void OnNetworkDespawn()
        {
            _hasReceivedState = false;
            _hasSentOnce = false;
        }

        private void FixedUpdate()
        {
            if (!IsSpawned || _rb == null) return;

            if (IsOwner)
                OwnerFixedUpdate();
            else
                RemoteFixedUpdate();
        }

        // ── Owner update ───────────────────────────────────────────────────────

        private void OwnerFixedUpdate()
        {
            _sendAccum += Time.fixedDeltaTime;
            float sendInterval = 1f / _sendRateHz;
            if (_sendAccum < sendInterval) return;
            _sendAccum -= sendInterval;

            var current = GetState();

            // Build the data-field mask from fields that have changed beyond thresholds.
            byte dataMask = BuildChangedMask(current);

            // On the very first send, transmit all enabled fields regardless of thresholds
            // so remote clients receive an initial full state snapshot.
            if (!_hasSentOnce)
            {
                dataMask = BuildFullMask();
                _hasSentOnce = true;
            }

            if (dataMask == 0x00) return; // nothing to send

            var manager = NetworkManager.Instance;
            if (manager == null) return;

            var payload = PhysicsPacketBuilder.BuildPayload(NetworkObjectId, current, dataMask);
            manager.SendStateSync(payload);

            _lastSentState  = current;
            _lastSleepState = current.IsSleeping;
        }

        private byte BuildChangedMask(PhysicsState current)
        {
            byte mask = 0;

            if (_syncPosition && Vector3.Distance(current.Position, _lastSentState.Position) > _positionThreshold)
                mask |= PhysicsPacketBuilder.ChangedPosition;

            if (_syncRotation && Quaternion.Angle(current.Rotation, _lastSentState.Rotation) > _rotationThreshold)
                mask |= PhysicsPacketBuilder.ChangedRotation;

            if (_syncVelocity && (current.Velocity - _lastSentState.Velocity).magnitude > _velocityThreshold)
                mask |= PhysicsPacketBuilder.ChangedVelocity;

            if (_syncAngularVelocity && (current.AngularVelocity - _lastSentState.AngularVelocity).magnitude > _angularVelocityThreshold)
                mask |= PhysicsPacketBuilder.ChangedAngularVelocity;

            // Sleep state: only include when it has changed since last send.
            if (_syncSleepState && current.IsSleeping != _lastSleepState)
                mask |= PhysicsPacketBuilder.ChangedSleep;

            return mask;
        }

        private byte BuildFullMask()
        {
            byte mask = 0;
            if (_syncPosition)        mask |= PhysicsPacketBuilder.ChangedPosition;
            if (_syncRotation)        mask |= PhysicsPacketBuilder.ChangedRotation;
            if (_syncVelocity)        mask |= PhysicsPacketBuilder.ChangedVelocity;
            if (_syncAngularVelocity) mask |= PhysicsPacketBuilder.ChangedAngularVelocity;
            if (_syncSleepState)      mask |= PhysicsPacketBuilder.ChangedSleep;
            return mask;
        }

        // ── Non-owner update ───────────────────────────────────────────────────

        private void RemoteFixedUpdate()
        {
            if (!_hasReceivedState) return;

            // ── Sleep handling ────────────────────────────────────────────────
            if (_syncSleepState && _receivedState.IsSleeping)
            {
                if (!_rb.IsSleeping()) _rb.Sleep();
                return; // sleeping body needs no correction
            }
            if (_rb.IsSleeping()) _rb.WakeUp();

            float timeSincePacket = Time.fixedTime - _lastReceiveTime;

            // ── Dead reckoning: project expected position forward ─────────────
            Vector3 targetPos = _receivedState.Position;
            if (_enableDeadReckoning && timeSincePacket < _deadReckoningTimeout && _syncVelocity)
                targetPos = _receivedState.Position + _receivedState.Velocity * timeSincePacket;

            // ── Position correction ───────────────────────────────────────────
            if (_syncPosition)
            {
                float posError = Vector3.Distance(_rb.position, targetPos);
                if (posError > _snapThreshold)
                {
                    // Large error: snap to authoritative position.
                    if (_makeRemoteKinematic)
                        _rb.position = targetPos;
                    else
                        _rb.MovePosition(targetPos);
                }
                else
                {
                    // Small error: lerp smoothly toward the projected position.
                    Vector3 corrected = Vector3.Lerp(
                        _rb.position, targetPos,
                        Time.fixedDeltaTime * _positionCorrectionSpeed);
                    if (_makeRemoteKinematic)
                        _rb.position = corrected;
                    else
                        _rb.MovePosition(corrected);
                }
            }

            // ── Rotation correction ────────────────────────────────────────────
            if (_syncRotation)
            {
                Quaternion corrected = Quaternion.Slerp(
                    _rb.rotation, _receivedState.Rotation,
                    Time.fixedDeltaTime * _rotationCorrectionSpeed);
                if (_makeRemoteKinematic)
                    _rb.rotation = corrected;
                else
                    _rb.MoveRotation(corrected);
            }

            // ── Velocity blending (non-kinematic only) ─────────────────────────
            // Blending velocity (not snapping) avoids visual lurching when packets
            // arrive slightly out of order.  The physics engine continues to simulate
            // with this velocity between packets, producing natural-looking movement.
            if (!_makeRemoteKinematic)
            {
                if (_syncVelocity)
                    _rb.linearVelocity = Vector3.Lerp(
                        _rb.linearVelocity, _receivedState.Velocity,
                        Time.fixedDeltaTime * _positionCorrectionSpeed);

                if (_syncAngularVelocity)
                    _rb.angularVelocity = Vector3.Lerp(
                        _rb.angularVelocity, _receivedState.AngularVelocity,
                        Time.fixedDeltaTime * _rotationCorrectionSpeed);
            }
        }

        // ── Internal API (called by NetworkManager) ────────────────────────────

        /// <summary>
        /// Apply an incoming physics-state update from a remote owner.
        /// Called by <c>NetworkManager.HandlePhysicsSyncPacket</c> on non-owner clients.
        /// </summary>
        /// <param name="incoming">Decoded physics snapshot.</param>
        /// <param name="changedMask">Bit-mask indicating which fields are valid.</param>
        internal void ApplyRemoteState(PhysicsState incoming, byte changedMask)
        {
            // Merge the received fields with the current known state so that fields
            // absent from this packet retain their last known values.
            if ((changedMask & PhysicsPacketBuilder.ChangedPosition) != 0)
                _receivedState.Position = incoming.Position;

            if ((changedMask & PhysicsPacketBuilder.ChangedRotation) != 0)
                _receivedState.Rotation = incoming.Rotation;

            if ((changedMask & PhysicsPacketBuilder.ChangedVelocity) != 0)
                _receivedState.Velocity = incoming.Velocity;

            if ((changedMask & PhysicsPacketBuilder.ChangedAngularVelocity) != 0)
                _receivedState.AngularVelocity = incoming.AngularVelocity;

            if ((changedMask & PhysicsPacketBuilder.ChangedSleep) != 0)
                _receivedState.IsSleeping = incoming.IsSleeping;

            _lastReceiveTime  = Time.fixedTime;
            _hasReceivedState = true;
        }

        /// <summary>
        /// Called by <c>NetworkManager</c> when the Sync Engine broadcasts the
        /// owner's own physics state back to all room members.
        ///
        /// <para>When <see cref="_enableOwnerReconciliation"/> is <c>true</c>,
        /// applies a defensive snap if the local body has diverged from the
        /// server-confirmed position by more than
        /// <see cref="_ownerReconcileSnapThreshold"/> world units.  Below the
        /// threshold the local prediction is kept intact, avoiding visual pops
        /// from normal physics noise.</para>
        ///
        /// <para>When <see cref="_enableOwnerReconciliation"/> is <c>false</c>
        /// (the default), this is a no-op — the owner's local physics
        /// simulation is treated as authoritative.  Set to true when an
        /// authoritative server simulates physics (anti-cheat / competitive
        /// modes) and broadcasts corrections back.</para>
        ///
        /// <para>Defensively rejects NaN/Inf positions and non-unit quaternions
        /// from the server payload — a bug or hostile signal will not be
        /// allowed to corrupt local <c>Rigidbody</c> state.</para>
        /// </summary>
        internal void ApplyReconciliation(PhysicsState serverState, byte changedMask)
        {
            if (!_enableOwnerReconciliation || _rb == null || !IsOwner) return;

            // Position snap on large divergence.
            if (_syncPosition && (changedMask & PhysicsPacketBuilder.ChangedPosition) != 0)
            {
                Vector3 sp = serverState.Position;
                if (!IsFiniteVector(sp))
                {
                    Debug.LogWarning(
                        "[RTMPE] NetworkRigidbody.ApplyReconciliation: rejected non-finite " +
                        $"server position {sp} — keeping local state.", this);
                    return;
                }

                float err = Vector3.Distance(_rb.position, sp);
                if (err > _ownerReconcileSnapThreshold)
                {
                    if (_makeRemoteKinematic) _rb.position = sp;
                    else                      _rb.MovePosition(sp);
                }
            }

            // Rotation snap on large divergence (uses the same snap threshold as
            // position, scaled to degrees via Quaternion.Angle).
            if (_syncRotation && (changedMask & PhysicsPacketBuilder.ChangedRotation) != 0)
            {
                Quaternion sr = serverState.Rotation;
                float magSq = sr.x * sr.x + sr.y * sr.y + sr.z * sr.z + sr.w * sr.w;
                if (magSq < 0.81f || magSq > 1.21f) return; // [0.9², 1.1²] band

                float angleErr = Quaternion.Angle(_rb.rotation, sr);
                if (angleErr > 30f) // 30° divergence → snap
                {
                    if (_makeRemoteKinematic) _rb.rotation = sr;
                    else                      _rb.MoveRotation(sr);
                }
            }
        }

        private static bool IsFiniteVector(Vector3 v)
            => !float.IsNaN(v.x) && !float.IsInfinity(v.x)
            && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
            && !float.IsNaN(v.z) && !float.IsInfinity(v.z);

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Capture the current <see cref="Rigidbody"/> state into a
        /// <see cref="PhysicsState"/> snapshot.  Called from
        /// <see cref="FixedUpdate"/> on the owner client.
        /// </summary>
        public PhysicsState GetState()
        {
            if (_rb == null)
                return default;
            return new PhysicsState
            {
                Position        = _rb.position,
                Rotation        = _rb.rotation,
                Velocity        = _rb.linearVelocity,
                AngularVelocity = _rb.angularVelocity,
                IsSleeping      = _rb.IsSleeping(),
            };
        }
    }
}
