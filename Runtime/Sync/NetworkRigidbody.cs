// RTMPE SDK — Runtime/Sync/NetworkRigidbody.cs
//
// MonoBehaviour component that synchronises a GameObject's 3-D Rigidbody
// physics state (position, rotation, velocity, angular velocity, sleep) over
// the RTMPE network.
//
// ── Architecture ──────────────────────────────────────────────────────────────
//
//  Owner (authoritative physics):
//    FixedUpdate() captures the Rigidbody state each physics step.
//    When any enabled field exceeds its send threshold AND the configured
//    send-rate interval has elapsed, a PhysicsSync payload is built and
//    transmitted as PacketType.StateSync (0x40) via the Sync Engine, which
//    broadcasts it to all room members at 30 Hz.
//
//  Non-owner (remote simulation):
//    ApplyRemoteState() is called by NetworkManager when a physics-sync packet
//    arrives for this object.  The received state is stored and applied each
//    FixedUpdate():
//      • MovePosition / MoveRotation smooth the body toward the authoritative
//        position, respecting physics constraints (no teleport through colliders).
//      • The received linear velocity is blended onto the Rigidbody so the
//        physics engine continues simulating between packets (no rubber-banding).
//      • Dead reckoning extrapolates the expected position using the last known
//        velocity for up to _deadReckoningTimeout seconds while no new packet
//        arrives, preventing objects from stopping mid-air between ticks.
//      • If the position error exceeds _snapThreshold, MovePosition snaps
//        immediately instead of lerping, correcting large desync without
//        prolonged visual drift.
//      • When IsSleeping is received, the remote body is put to sleep to stop
//        physics noise on stationary objects.
//
//  Owner reconciliation:
//    When the Sync Engine broadcasts the owner's own state back (because all
//    room members receive each tick), ApplyReconciliation() can optionally
//    apply a server-corrected position.  Currently a no-op placeholder — the
//    owner's local physics simulation is authoritative.
//
// ── Threading ─────────────────────────────────────────────────────────────────
//
//  All methods run on the Unity main thread.  ApplyRemoteState() is called from
//  NetworkManager (main thread) and stores state into _receivedState, which
//  FixedUpdate() reads on the next physics step (also main thread).  No lock is
//  required because both sites execute on the same thread.
//
// ── Design decisions ──────────────────────────────────────────────────────────
//
//  • Uses FixedUpdate (physics timestep) for both capture and application so
//    that Rigidbody.velocity and .angularVelocity reflect the physics engine's
//    actual state, not a mid-frame snapshot.
//  • MovePosition / MoveRotation are preferred over direct position / rotation
//    assignment on non-kinematic bodies: they participate in collision detection
//    within the physics step, preventing tunnelling.
//  • _makeRemoteKinematic: when true, the Rigidbody on non-owner clients is set
//    to kinematic on spawn, and position/rotation are applied directly.  This is
//    appropriate when the owner fully controls the body (e.g. a player character)
//    and remote clients should never simulate physics for it.
//  • Send rate defaults to 20 Hz (below the 30 Hz Sync Engine tick) to reduce
//    bandwidth while still producing smooth results with dead reckoning.
//  • Change detection uses per-field thresholds to suppress sends for micro-
//    movements (floating-point noise, sleep vibrations) that would waste bandwidth.
//  • Sleep state is only included in the payload when it changes, reducing the
//    common-case payload size for active objects.

using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync.Internal;

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

        [Tooltip("Synchronise the RigidbodyConstraints bitmask so runtime constraint changes " +
                 "(e.g. freezing an axis after a ragdoll lands) propagate to remote bodies.")]
        [SerializeField] private bool _syncConstraints = true;

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

        [Tooltip("Convergence rate (per second) for the linear-velocity blend on remote " +
                 "non-kinematic bodies.  Used as the rate constant inside an exponential " +
                 "smoothing filter so the time-to-converge is identical at any physics step.")]
        [SerializeField] [Range(0.1f, 60f)] private float _velocityBlendRate = 10f;

        [Tooltip("Convergence rate (per second) for the angular-velocity blend on remote " +
                 "non-kinematic bodies.  Same exponential-smoothing semantics as " +
                 "_velocityBlendRate.")]
        [SerializeField] [Range(0.1f, 60f)] private float _angularVelocityBlendRate = 10f;

        [Tooltip("Position-error magnitude (world units) above which a non-kinematic " +
                 "remote body is teleported by direct .position assignment instead of " +
                 "being driven through MovePosition.  MovePosition sweeps the body " +
                 "through colliders for the upcoming physics step; on a large server " +
                 "correction the sweep can clamp short against geometry and leave the " +
                 "body desynced.")]
        [SerializeField] [Range(0.1f, 50f)] private float _snapDistanceThreshold = 1.5f;

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

        [Tooltip("Rotation error threshold (degrees) above which the owner snaps to the " +
                 "server-confirmed rotation.  Defaults to 30°, matching the legacy " +
                 "behaviour; titles with tight reconciliation tolerances (competitive " +
                 "shooters) typically lower this; titles with loose tolerances (casual " +
                 "ragdoll) raise it.  Tune in tandem with the position threshold.")]
        [SerializeField] [Range(1f, 180f)] private float _ownerReconcileRotationSnapDegrees = 30.0f;

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
        // Last constraint mask sent (used to detect change-and-send-once semantics).
        private byte _lastSentConstraints;
        // Accumulator for send rate limiting.
        private float _sendAccum;

        // Non-owner side: latest state received from the network.
        private PhysicsState _receivedState;
        // Timestamp (Time.fixedTime) when the last packet was received.
        private float _lastReceiveTime;
        // True once at least one state has been received.
        private bool _hasReceivedState;
        // Last constraint mask actually applied to the local Rigidbody so we
        // only assign _rb.constraints when it changes (no-op writes are cheap
        // but apt to confuse Unity's PhysX cache).
        private byte _appliedConstraints;
        // True once the receive side has observed at least one ConstraintMask
        // field — until then we never touch _rb.constraints (avoids stamping
        // RigidbodyConstraints.None over an inspector-configured default).
        private bool _hasReceivedConstraints;

        // ── Receive-side hardening ─────────────────────────────────────────────
        //
       // Token-bucket rate limit for inbound physics packets, per object.  A
        // hostile peer or compromised server cannot drive PhysX state changes
        // faster than NetworkSettings.maxPhysicsPacketsPerSecond / second.
        private float _rateBucketTokens;
        private float _rateBucketLastTime;

        // Monotonic local sequence number incremented every time a packet is
        // accepted.  Used for receive-order enforcement: an out-of-order
        // dispatch (re-ordered between threads, replayed by an attacker) can
        // be detected when the inbound packet would carry a stale view.
        // Without a wire-format sequence we fall back to local arrival order.
        private uint _appliedSequence;
        // True once a position has been accepted, so the per-tick delta cap
        // has a reference to compare against.
        private bool _hasAppliedPosition;

        // Stable position anchor refreshed every PositionAnchorInterval accepted
        // packets.  Provides a secondary absolute-distance check that is immune
        // to manipulation of _receivedState.Position via out-of-order or crafted
        // packet sequences.
        private const uint PositionAnchorInterval = 32;
        private UnityEngine.Vector3 _positionAnchor;
        private bool _hasPositionAnchor;

        // ── Owner-reconciliation deferral ──────────────────────────────────────
        //
        // Physics body state must be written aligned with the FixedUpdate
        // cadence; writing in the Update path (HandlePhysicsSyncPacket → this
        // component) with two FixedUpdates per Update at 60 fps physics + 30 fps
        // rendering produces a visible lurch every other physics step as the
        // body teleports mid-Update and then runs one extra integration step
        // before the next render.  Capture the desired correction here and
        // apply it from FixedUpdate so the write lands exactly once per
        // physics tick — the LCM beat goes away.
        private Vector3    _pendingOwnerSnapPosition;
        private bool       _hasPendingOwnerSnapPosition;
        private Quaternion _pendingOwnerSnapRotation;
        private bool       _hasPendingOwnerSnapRotation;

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
                _lastSentState         = GetState();
                _lastSleepState        = _rb.IsSleeping();
                _lastSentConstraints   = _lastSentState.ConstraintMask;
                _hasSentOnce           = false;
            }

            // Receive-side constraint application bookkeeping.  Initialise to
            // the local Rigidbody's current constraints so the "change-detect
            // before assign" guard works correctly for the first received mask.
            _appliedConstraints     = (byte)(int)_rb.constraints;
            _hasReceivedConstraints = false;

            // Pre-fill the rate-limit bucket so the first inbound packet
            // immediately after spawn is admitted; the bucket then refills at
            // maxPhysicsPacketsPerSecond / second.  Capacity is bounded by the
            // configured rate so a sleeping object cannot accumulate burst
            // tokens beyond a single second's worth.
            var settings0 = NetworkManager.Instance?.Settings;
            float capacity0 = settings0 != null ? Mathf.Max(0f, settings0.maxPhysicsPacketsPerSecond) : 0f;
            _rateBucketTokens   = capacity0;
            _rateBucketLastTime = Time.fixedTime;
            _hasAppliedPosition = false;
            _appliedSequence    = 0;
            _hasPositionAnchor  = false;

            _sendAccum = 0f;

            // Pending owner-reconciliation buffer is per-spawn lifecycle: a
            // re-spawn must not carry an unconsumed snap from a previous run.
            _hasPendingOwnerSnapPosition = false;
            _hasPendingOwnerSnapRotation = false;
        }

        /// <summary>
        /// Restore kinematic mode when the object leaves the network.
        /// </summary>
        protected override void OnNetworkDespawn()
        {
            _hasReceivedState = false;
            _hasSentOnce = false;
            _hasPendingOwnerSnapPosition = false;
            _hasPendingOwnerSnapRotation = false;
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
            // ── Drain queued reconciliation writes ────────────────────────────
            // ApplyReconciliation runs on the main thread / Update path when a
            // server-broadcast state arrives; mutating _rb.position directly
            // there causes lurches at the LCM of the Update / FixedUpdate
            // clocks (one teleport plus an extra integration step every Update
            // beat).  Queueing the target and applying it here aligns every
            // owner-side write with PhysX exactly once per physics step.
            if (_hasPendingOwnerSnapPosition)
            {
                _rb.position                 = _pendingOwnerSnapPosition;
                _hasPendingOwnerSnapPosition = false;
            }
            if (_hasPendingOwnerSnapRotation)
            {
                if (_makeRemoteKinematic) _rb.rotation = _pendingOwnerSnapRotation;
                else                      _rb.MoveRotation(_pendingOwnerSnapRotation);
                _hasPendingOwnerSnapRotation = false;
            }

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

            _lastSentState       = current;
            _lastSleepState      = current.IsSleeping;
            _lastSentConstraints = current.ConstraintMask;
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

            // Constraints: only include when the bitmask changed since last send.
            // Static constraint configurations therefore pay zero per-tick bandwidth.
            if (_syncConstraints && current.ConstraintMask != _lastSentConstraints)
                mask |= PhysicsPacketBuilder.ChangedConstraints;

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
            if (_syncConstraints)     mask |= PhysicsPacketBuilder.ChangedConstraints;
            return mask;
        }

        // ── Non-owner update ───────────────────────────────────────────────────

        private void RemoteFixedUpdate()
        {
            if (!_hasReceivedState) return;

            // ── Constraint application ────────────────────────────────────────
            // Apply the authoritative constraint mask once per change.  Done
            // BEFORE sleep/position/rotation logic because freezing an axis
            // affects how MovePosition / MoveRotation behave for the rest of
            // this physics step.
            if (_syncConstraints && _hasReceivedConstraints
                && _receivedState.ConstraintMask != _appliedConstraints)
            {
                _rb.constraints     = (RigidbodyConstraints)_receivedState.ConstraintMask;
                _appliedConstraints = _receivedState.ConstraintMask;
            }

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

            // Frame-rate-independent blend factor.  1 - exp(-rate·dt) yields the
            // same time-to-converge at any physics step (50 / 60 / 120 Hz) where
            // the naive `dt * coefficient` Lerp would silently accelerate as the
            // step shrinks.
            float dt        = Time.fixedDeltaTime;
            float posBlend  = 1f - Mathf.Exp(-_positionCorrectionSpeed * dt);
            float rotBlend  = 1f - Mathf.Exp(-_rotationCorrectionSpeed * dt);
            float velBlend  = 1f - Mathf.Exp(-_velocityBlendRate         * dt);
            float angBlend  = 1f - Mathf.Exp(-_angularVelocityBlendRate  * dt);

            // ── Position correction ───────────────────────────────────────────
            if (_syncPosition)
            {
                float posError = Vector3.Distance(_rb.position, targetPos);
                if (posError > _snapThreshold)
                {
                    // Large error: teleport to the authoritative position.  On a
                    // dynamic body, MovePosition runs a sweep test for the next
                    // physics step and will clamp short against any collider in
                    // the path of a multi-metre correction, leaving the remote
                    // visibly desynced.  Direct .position assignment skips the
                    // sweep so the snap actually lands.
                    ApplySnapPosition(targetPos);
                }
                else
                {
                    // Small error: blend smoothly toward the projected position
                    // using exponential smoothing so the convergence rate is
                    // identical at any physics step.
                    Vector3 corrected = Vector3.Lerp(_rb.position, targetPos, posBlend);
                    if (_makeRemoteKinematic)
                        _rb.position = corrected;
                    else if (Vector3.Distance(_rb.position, corrected) > _snapDistanceThreshold)
                        // Even a "small-error" branch can produce a per-step
                        // motion larger than the sweep budget after a long
                        // hitch; route those through the bypass too.
                        _rb.position = corrected;
                    else
                        _rb.MovePosition(corrected);
                }
            }

            // ── Rotation correction ────────────────────────────────────────────
            if (_syncRotation)
            {
                Quaternion corrected = Quaternion.Slerp(
                    _rb.rotation, _receivedState.Rotation, rotBlend);
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
                    _rb.SetLinearVelocity(Vector3.Lerp(
                        _rb.GetLinearVelocity(), _receivedState.Velocity, velBlend));

                if (_syncAngularVelocity)
                    _rb.angularVelocity = Vector3.Lerp(
                        _rb.angularVelocity, _receivedState.AngularVelocity, angBlend);
            }
        }

        // Teleport the remote body to <paramref name="targetPos"/>, preserving
        // the most recent linear velocity on a non-kinematic body so the
        // physics simulation continues smoothly from the new origin instead of
        // dropping to zero motion the instant the snap lands.  Direct
        // .position assignment bypasses the upcoming-step sweep that
        // MovePosition performs on dynamic bodies; required when the
        // correction distance exceeds the normal interpolation budget.
        private void ApplySnapPosition(Vector3 targetPos)
        {
            if (_makeRemoteKinematic)
            {
                _rb.position = targetPos;
                return;
            }

            // Non-kinematic: assign position directly so PhysX moves the body
            // without a discrete-step sweep.  Linear velocity is preserved
            // from the latest accepted packet so simulation continues
            // naturally; the velocity blend below smooths any divergence.
            _rb.position = targetPos;
            if (_syncVelocity)
                _rb.SetLinearVelocity(_receivedState.Velocity);
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
            var settings = NetworkManager.Instance?.Settings;

            // ── Per-object inbound rate limit ─────────────────────────────────
            // Token-bucket: refill at maxPhysicsPacketsPerSecond per second,
            // capped at one second's worth.  Each accepted packet costs one
            // token.  When the bucket is empty the packet is silently dropped
            // — never resets the bucket via attacker-controlled input, so a
            // flood cannot reset the counter to its own benefit.
            if (settings != null && settings.maxPhysicsPacketsPerSecond > 0f)
            {
                float now = Time.fixedTime;
                float dt  = Mathf.Max(0f, now - _rateBucketLastTime);
                _rateBucketLastTime = now;
                float capacity = settings.maxPhysicsPacketsPerSecond;
                _rateBucketTokens = Mathf.Min(capacity,
                    _rateBucketTokens + dt * settings.maxPhysicsPacketsPerSecond);
                if (_rateBucketTokens < 1f) return;
                _rateBucketTokens -= 1f;
            }

            // ── Componentwise finiteness gate ─────────────────────────────────
            // Reject ANY inbound packet whose vector or quaternion fields
            // carry NaN / +Inf / -Inf in a sync field that this packet
            // claims to update.  PhysX has been observed to enter an
            // unrecoverable state on a single non-finite assignment to
            // Rigidbody.position / .velocity (the body either disappears
            // from the simulation or stops responding to forces); the
            // velocity cap above happens to reject NaN.sqrMagnitude (NaN >
            // anything is false → the comparison passes through), so the
            // explicit IsFinite gate is the only authoritative defence.
            if ((changedMask & PhysicsPacketBuilder.ChangedPosition) != 0
                && !IsFiniteVector(incoming.Position)) return;
            if ((changedMask & PhysicsPacketBuilder.ChangedVelocity) != 0
                && !IsFiniteVector(incoming.Velocity)) return;
            if ((changedMask & PhysicsPacketBuilder.ChangedAngularVelocity) != 0
                && !IsFiniteVector(incoming.AngularVelocity)) return;
            if ((changedMask & PhysicsPacketBuilder.ChangedRotation) != 0
                && !IsFiniteQuaternion(incoming.Rotation)) return;

            // ── Plausibility caps on velocity / angular velocity ──────────────
            if (settings != null)
            {
                if (settings.maxLinearVelocity > 0f
                    && (changedMask & PhysicsPacketBuilder.ChangedVelocity) != 0
                    && incoming.Velocity.sqrMagnitude
                       > settings.maxLinearVelocity * settings.maxLinearVelocity)
                    return;

                if (settings.maxAngularVelocity > 0f
                    && (changedMask & PhysicsPacketBuilder.ChangedAngularVelocity) != 0
                    && incoming.AngularVelocity.sqrMagnitude
                       > settings.maxAngularVelocity * settings.maxAngularVelocity)
                    return;

                if (settings.maxPositionDeltaPerTick > 0f
                    && (changedMask & PhysicsPacketBuilder.ChangedPosition) != 0
                    && _hasAppliedPosition)
                {
                    float capSq = settings.maxPositionDeltaPerTick * settings.maxPositionDeltaPerTick;
                    if ((incoming.Position - _receivedState.Position).sqrMagnitude > capSq)
                        return;

                    // Secondary check against a stable anchor refreshed every
                    // PositionAnchorInterval accepted packets.  Limits the total
                    // distance travellable via a sequence of individually-valid
                    // small steps crafted from out-of-order or replayed packets.
                    if (_hasPositionAnchor)
                    {
                        float anchorCapSq = capSq * PositionAnchorInterval * PositionAnchorInterval;
                        if ((incoming.Position - _positionAnchor).sqrMagnitude > anchorCapSq)
                            return;
                    }
                }
            }

            // Merge the received fields with the current known state so that fields
            // absent from this packet retain their last known values.
            if ((changedMask & PhysicsPacketBuilder.ChangedPosition) != 0)
            {
                _receivedState.Position = incoming.Position;
                _hasAppliedPosition     = true;
            }

            if ((changedMask & PhysicsPacketBuilder.ChangedRotation) != 0)
                _receivedState.Rotation = incoming.Rotation;

            if ((changedMask & PhysicsPacketBuilder.ChangedVelocity) != 0)
                _receivedState.Velocity = incoming.Velocity;

            if ((changedMask & PhysicsPacketBuilder.ChangedAngularVelocity) != 0)
                _receivedState.AngularVelocity = incoming.AngularVelocity;

            if ((changedMask & PhysicsPacketBuilder.ChangedSleep) != 0)
                _receivedState.IsSleeping = incoming.IsSleeping;

            // ConstraintMask: only honoured when AllowDynamicConstraints is true.
            // Bits outside DynamicConstraintsAllowMask are stripped before assignment
            // so a hostile sender cannot toggle constraint bits the host policy
            // disallows (e.g. unfreezing an axis the local design has locked).
            if ((changedMask & PhysicsPacketBuilder.ChangedConstraints) != 0
                && settings != null && settings.allowDynamicConstraints)
            {
                byte allow = (byte)(settings.dynamicConstraintsAllowMask & 0xFF);
                _receivedState.ConstraintMask = (byte)(incoming.ConstraintMask & allow);
                _hasReceivedConstraints       = true;
            }

            _lastReceiveTime  = Time.fixedTime;
            _hasReceivedState = true;
            unchecked { _appliedSequence++; }

            // Refresh the position anchor on the first accepted packet and then
            // every PositionAnchorInterval packets so the secondary delta cap
            // always has a recent, attacker-resistant reference point.
            if ((changedMask & PhysicsPacketBuilder.ChangedPosition) != 0)
            {
                if (!_hasPositionAnchor || (_appliedSequence % PositionAnchorInterval) == 0)
                {
                    _positionAnchor    = _receivedState.Position;
                    _hasPositionAnchor = true;
                }
            }
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

            var settings = NetworkManager.Instance?.Settings;

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

                // ── Server-correction cap & world bounds ─────────────────────
                // Defends against a hostile / compromised server that would
                // otherwise teleport the local Rigidbody to an arbitrary
                // location.  Mirrors NetworkTransform.ApplyReconciliation so
                // the same project-wide tuning governs both transforms and
                // rigidbodies.  Each guard is bypassed when its setting is
                // disabled (0 / false) for back-compat with existing scenes.
                if (settings != null)
                {
                    if (settings.maxServerCorrectionDistance > 0f
                        && err > settings.maxServerCorrectionDistance)
                    {
                        Debug.LogWarning(
                            "[RTMPE] NetworkRigidbody.ApplyReconciliation: rejected " +
                            $"server correction of {err:F2}m (cap " +
                            $"{settings.maxServerCorrectionDistance:F2}m) — keeping " +
                            "local state.", this);
                        return;
                    }

                    if (settings.worldBoundsEnabled)
                    {
                        Vector3 d = sp - settings.worldBoundsCenter;
                        Vector3 e = settings.worldBoundsExtents;
                        if (Mathf.Abs(d.x) > e.x
                            || Mathf.Abs(d.y) > e.y
                            || Mathf.Abs(d.z) > e.z)
                        {
                            Debug.LogWarning(
                                "[RTMPE] NetworkRigidbody.ApplyReconciliation: rejected " +
                                $"server position {sp} outside world bounds — keeping " +
                                "local state.", this);
                            return;
                        }
                    }
                }

                if (err > _ownerReconcileSnapThreshold)
                {
                    // Queue the snap for the next FixedUpdate so the actual
                    // _rb.position write lands on the physics cadence.  A
                    // direct write here would teleport the body mid-Update,
                    // and PhysX's next integration step (≤16 ms later at
                    // 60 fps physics) would then run on top of the snapped
                    // pose, producing a visible velocity-preserving lurch on
                    // every owner correction beat.
                    _pendingOwnerSnapPosition    = sp;
                    _hasPendingOwnerSnapPosition = true;
                }
            }

            // Rotation snap on large divergence (uses the same snap threshold as
            // position, scaled to degrees via Quaternion.Angle).
            if (_syncRotation && (changedMask & PhysicsPacketBuilder.ChangedRotation) != 0)
            {
                Quaternion sr = serverState.Rotation;
                // IsFinite must precede the magnitude band gate: a NaN component
                // makes magSq=NaN, and NaN<0.81 / NaN>1.21 are both false, so the
                // band check would silently pass a corrupt quaternion through.
                if (!IsFiniteQuaternion(sr))
                {
                    Debug.LogWarning(
                        "[RTMPE] NetworkRigidbody.ApplyReconciliation: rejected non-finite " +
                        $"server rotation {sr} — keeping local state.", this);
                    return;
                }
                float magSq = sr.x * sr.x + sr.y * sr.y + sr.z * sr.z + sr.w * sr.w;
                if (magSq < 0.81f || magSq > 1.21f) return; // [0.9², 1.1²] band

                float angleErr = Quaternion.Angle(_rb.rotation, sr);
                if (angleErr > _ownerReconcileRotationSnapDegrees)
                {
                    // Same FixedUpdate-aligned deferral as the position branch
                    // above: a direct write here would land between physics
                    // steps and add a non-deterministic rotational nudge.
                    _pendingOwnerSnapRotation    = sr;
                    _hasPendingOwnerSnapRotation = true;
                }
            }
        }

        private static bool IsFiniteVector(Vector3 v)
            => !float.IsNaN(v.x) && !float.IsInfinity(v.x)
            && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
            && !float.IsNaN(v.z) && !float.IsInfinity(v.z);

        // Quaternion finiteness used by ApplyRemoteState's inbound gate.
        // Includes the w component because PhysX may carry a finite x/y/z
        // alongside a NaN w after a divide-by-zero recovery, which Unity
        // would otherwise persist as a corrupt rotation.
        private static bool IsFiniteQuaternion(Quaternion q)
            => !float.IsNaN(q.x) && !float.IsInfinity(q.x)
            && !float.IsNaN(q.y) && !float.IsInfinity(q.y)
            && !float.IsNaN(q.z) && !float.IsInfinity(q.z)
            && !float.IsNaN(q.w) && !float.IsInfinity(q.w);

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
                Velocity        = _rb.GetLinearVelocity(),
                AngularVelocity = _rb.angularVelocity,
                IsSleeping      = _rb.IsSleeping(),
                ConstraintMask  = (byte)(int)_rb.constraints,
            };
        }
    }
}
