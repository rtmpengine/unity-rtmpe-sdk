// RTMPE SDK — Runtime/Sync/NetworkRigidbody2D.cs
//
// MonoBehaviour component that synchronises a GameObject's 2-D Rigidbody2D
// physics state (position, rotation, velocity, angular velocity, sleep) over
// the RTMPE network.
//
// ── Architecture ──────────────────────────────────────────────────────────────
//
//  Mirrors NetworkRigidbody exactly but targets Rigidbody2D instead of Rigidbody.
//  Key 2-D differences:
//    • Position is Vector2 (XY plane); Z is never touched.
//    • Rotation is a single float in degrees (not a Quaternion).
//    • AngularVelocity is a single float in degrees/second.
//    • MovePosition(Vector2) / MoveRotation(float) are used for non-kinematic
//      correction (Rigidbody2D's equivalents of the 3-D MovePosition/Rotation).
//
//  Owner (authoritative physics):
//    FixedUpdate() captures Rigidbody2D state and sends PhysicsSync2D payloads
//    as PacketType.StateSync (0x40) when enabled fields change beyond thresholds.
//
//  Non-owner (remote simulation):
//    ApplyRemoteState() is called by NetworkManager when a 2-D physics packet
//    arrives.  FixedUpdate() applies dead reckoning, position/rotation correction,
//    and velocity blending each physics step.
//
// ── Threading ─────────────────────────────────────────────────────────────────
//
//  All methods run on the Unity main thread.  No locks needed.
//
// ── Angle conventions ─────────────────────────────────────────────────────────
//
//  Rigidbody2D.rotation returns degrees in the range (-180, 180].
//  Rigidbody2D.angularVelocity returns degrees/second.
//  Both values are transmitted as-is (no conversion to radians).
//  Quaternion.Angle is NOT used for rotation-change detection; Mathf.Abs of
//  the delta angle (with DeltaAngle normalisation) is used instead.

using System.Buffers;

using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync.Internal;

namespace RTMPE.Sync
{
    /// <summary>
    /// Synchronises a 2-D <see cref="UnityEngine.Rigidbody2D"/> over the RTMPE network.
    /// <para>
    /// Attach alongside a <see cref="NetworkBehaviour"/> subclass on any prefab
    /// that is spawned via <c>SpawnManager</c> and driven by Unity 2-D physics.
    /// </para>
    /// </summary>
    [AddComponentMenu("RTMPE/Network Rigidbody 2D")]
    [RequireComponent(typeof(Rigidbody2D))]
    public class NetworkRigidbody2D : NetworkBehaviour
    {
        // ── Inspector — Sync toggles ───────────────────────────────────────────

        [Header("Sync Fields")]
        [Tooltip("Synchronise world-space 2-D position.")]
        [SerializeField] private bool _syncPosition = true;

        [Tooltip("Synchronise Z-axis rotation (degrees).")]
        [SerializeField] private bool _syncRotation = true;

        [Tooltip("Synchronise linear velocity so remote bodies continue moving between packets.")]
        [SerializeField] private bool _syncVelocity = true;

        [Tooltip("Synchronise angular velocity (deg/s) so remote bodies continue spinning between packets.")]
        [SerializeField] private bool _syncAngularVelocity = true;

        [Tooltip("Synchronise sleep state so remote bodies idle when the owner body is asleep.")]
        [SerializeField] private bool _syncSleepState = true;

        [Tooltip("Synchronise the RigidbodyConstraints2D bitmask so runtime constraint changes " +
                 "(e.g. freezing the X axis during a 2-D climb) propagate to remote bodies.")]
        [SerializeField] private bool _syncConstraints = true;

        // ── Inspector — Send thresholds ────────────────────────────────────────

        [Header("Send Thresholds")]
        [Tooltip("Minimum 2-D position change in world units since last send.")]
        [SerializeField] private float _positionThreshold = 0.01f;

        [Tooltip("Minimum rotation change in degrees since last send.")]
        [SerializeField] private float _rotationThreshold = 0.1f;

        [Tooltip("Minimum linear velocity change (magnitude) in units/second since last send.")]
        [SerializeField] private float _velocityThreshold = 0.05f;

        [Tooltip("Minimum angular velocity change in degrees/second since last send.")]
        [SerializeField] private float _angularVelocityThreshold = 1.0f;

        // ── Inspector — Remote body ────────────────────────────────────────────

        [Header("Remote Body Behaviour")]
        [Tooltip("When true, the Rigidbody2D on non-owner clients is set to kinematic on spawn.")]
        [SerializeField] private bool _makeRemoteKinematic = false;

        [Tooltip("Position error in world units above which the remote body snaps " +
                 "immediately to the authoritative position.")]
        [SerializeField] private float _snapThreshold = 3.0f;

        [Tooltip("Speed at which the remote body lerps toward the authoritative position.")]
        [SerializeField] [Range(1f, 50f)] private float _positionCorrectionSpeed = 10f;

        [Tooltip("Speed at which the remote body lerps toward the authoritative rotation.")]
        [SerializeField] [Range(1f, 50f)] private float _rotationCorrectionSpeed = 10f;

        // ── Inspector — Owner reconciliation ───────────────────────────────────

        [Header("Owner Reconciliation")]
        [Tooltip("Enable defensive owner-side reconciliation against the server-broadcast state. " +
                 "When the local position diverges from the server-confirmed position by more than " +
                 "_ownerReconcileSnapThreshold, the body snaps to the server position.  Below the " +
                 "threshold the local prediction is preserved (no visual pop).  Disable for " +
                 "trusted-client modes; enable when an authoritative server simulates physics.")]
        [SerializeField] private bool _enableOwnerReconciliation = false;

        [Tooltip("Position error threshold (world units) above which the owner snaps to the " +
                 "server-confirmed position.  Set high enough to avoid fighting normal " +
                 "client-side prediction noise.")]
        [SerializeField] [Range(0.5f, 20f)] private float _ownerReconcileSnapThreshold = 3.0f;

        // ── Inspector — Dead reckoning ─────────────────────────────────────────

        [Header("Dead Reckoning")]
        [Tooltip("Extrapolate the remote body's position using the last received velocity " +
                 "while no new packet has arrived.")]
        [SerializeField] private bool _enableDeadReckoning = true;

        [Tooltip("Seconds after the last packet after which dead reckoning stops.")]
        [SerializeField] [Range(0.1f, 2.0f)] private float _deadReckoningTimeout = 0.5f;

        // ── Inspector — Send rate ──────────────────────────────────────────────

        [Header("Send Rate")]
        [Tooltip("Physics-state updates sent per second by the owner client.")]
        [SerializeField] [Range(1, 30)] private int _sendRateHz = 20;

        // ── Runtime state ──────────────────────────────────────────────────────

        private Rigidbody2D _rb;

        // Owner side.
        private PhysicsState2D _lastSentState;
        private bool           _hasSentOnce;
        private bool           _lastSleepState;
        private byte           _lastSentConstraints;
        private float          _sendAccum;

        // Non-owner side.
        private PhysicsState2D _receivedState;
        private float          _lastReceiveTime;
        private bool           _hasReceivedState;
        private byte           _appliedConstraints;
        private bool           _hasReceivedConstraints;

        // Receive-side hardening: token-bucket rate limit and per-tick
        // delta-cap reference state.  Mirrors the 3-D component.
        private float _rateBucketTokens;
        private float _rateBucketLastTime;
        private uint  _appliedSequence;
        private bool  _hasAppliedPosition;

        // ── Unity lifecycle ────────────────────────────────────────────────────

        protected override void OnNetworkSpawn()
        {
            _rb = GetComponent<Rigidbody2D>();
            if (_rb == null)
            {
                Debug.LogError("[RTMPE] NetworkRigidbody2D.OnNetworkSpawn: " +
                               "no Rigidbody2D found on this GameObject.", this);
                return;
            }

            if (!IsOwner && _makeRemoteKinematic)
                _rb.bodyType = RigidbodyType2D.Kinematic;

            if (IsOwner)
            {
                _lastSentState       = GetState();
                _lastSleepState      = _rb.IsSleeping();
                _lastSentConstraints = _lastSentState.ConstraintMask;
                _hasSentOnce         = false;
            }

            _appliedConstraints     = (byte)(int)_rb.constraints;
            _hasReceivedConstraints = false;

            // Pre-fill the rate-limit bucket; see NetworkRigidbody for rationale.
            var settings0 = NetworkManager.Instance?.Settings;
            float capacity0 = settings0 != null ? Mathf.Max(0f, settings0.maxPhysicsPacketsPerSecond) : 0f;
            _rateBucketTokens   = capacity0;
            _rateBucketLastTime = Time.fixedTime;
            _hasAppliedPosition = false;
            _appliedSequence    = 0;

            _sendAccum = 0f;
        }

        protected override void OnNetworkDespawn()
        {
            _hasReceivedState = false;
            _hasSentOnce      = false;
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

            var  current  = GetState();
            byte dataMask = BuildChangedMask(current);

            if (!_hasSentOnce)
            {
                dataMask     = BuildFullMask();
                _hasSentOnce = true;
            }

            if (dataMask == 0x00) return;

            var manager = NetworkManager.Instance;
            if (manager == null) return;

            // Pooled 2-D physics send path (GC Round 2, 2026-05-02).
            int size   = PhysicsPacketBuilder.ComputePayloadSize(dataMask, twoDee: true);
            var pool   = ArrayPool<byte>.Shared;
            var buffer = pool.Rent(size);
            try
            {
                int written = PhysicsPacketBuilder.Build2DPayloadInto(
                    buffer, 0, NetworkObjectId, current, dataMask);
                manager.SendStateSync(buffer, written);
            }
            finally
            {
                pool.Return(buffer);
            }

            _lastSentState       = current;
            _lastSleepState      = current.IsSleeping;
            _lastSentConstraints = current.ConstraintMask;
        }

        private byte BuildChangedMask(PhysicsState2D current)
        {
            byte mask = 0;

            if (_syncPosition &&
                (current.Position - _lastSentState.Position).magnitude > _positionThreshold)
                mask |= PhysicsPacketBuilder.ChangedPosition;

            if (_syncRotation &&
                Mathf.Abs(Mathf.DeltaAngle(current.Rotation, _lastSentState.Rotation)) > _rotationThreshold)
                mask |= PhysicsPacketBuilder.ChangedRotation;

            if (_syncVelocity &&
                (current.Velocity - _lastSentState.Velocity).magnitude > _velocityThreshold)
                mask |= PhysicsPacketBuilder.ChangedVelocity;

            if (_syncAngularVelocity &&
                Mathf.Abs(current.AngularVelocity - _lastSentState.AngularVelocity) > _angularVelocityThreshold)
                mask |= PhysicsPacketBuilder.ChangedAngularVelocity;

            if (_syncSleepState && current.IsSleeping != _lastSleepState)
                mask |= PhysicsPacketBuilder.ChangedSleep;

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
            // Apply the authoritative constraint bitmask once per change.
            if (_syncConstraints && _hasReceivedConstraints
                && _receivedState.ConstraintMask != _appliedConstraints)
            {
                _rb.constraints     = (RigidbodyConstraints2D)_receivedState.ConstraintMask;
                _appliedConstraints = _receivedState.ConstraintMask;
            }

            // ── Sleep handling ────────────────────────────────────────────────
            if (_syncSleepState && _receivedState.IsSleeping)
            {
                if (!_rb.IsSleeping()) _rb.Sleep();
                return;
            }
            if (_rb.IsSleeping()) _rb.WakeUp();

            float timeSincePacket = Time.fixedTime - _lastReceiveTime;

            // ── Dead reckoning ─────────────────────────────────────────────────
            Vector2 targetPos = _receivedState.Position;
            if (_enableDeadReckoning && timeSincePacket < _deadReckoningTimeout && _syncVelocity)
                targetPos = _receivedState.Position + _receivedState.Velocity * timeSincePacket;

            // Frame-rate-independent smoothing.  The naive `dt * rate` lerp
            // coefficient produces a visibly-different time-to-converge at
            // every Project-Settings physics step (50 / 60 / 120 Hz), and at
            // 30 Hz it can exceed 1 — which silently degenerates into an
            // instant snap.  The exponential form `1 - exp(-rate * dt)`
            // converges to the same proportion of the remaining error per
            // unit of wall-clock time regardless of dt, matching the
            // discipline already in use in the 3-D companion.
            float posLerpT = 1f - Mathf.Exp(-_positionCorrectionSpeed * Time.fixedDeltaTime);
            float rotLerpT = 1f - Mathf.Exp(-_rotationCorrectionSpeed * Time.fixedDeltaTime);

            // ── Position correction ───────────────────────────────────────────
            if (_syncPosition)
            {
                float posError = (targetPos - _rb.position).magnitude;
                if (posError > _snapThreshold)
                {
                    if (_makeRemoteKinematic)
                        _rb.position = targetPos;
                    else
                        _rb.MovePosition(targetPos);
                }
                else
                {
                    Vector2 corrected = Vector2.Lerp(_rb.position, targetPos, posLerpT);
                    if (_makeRemoteKinematic)
                        _rb.position = corrected;
                    else
                        _rb.MovePosition(corrected);
                }
            }

            // ── Rotation correction ────────────────────────────────────────────
            // Use Mathf.LerpAngle to take the shortest arc through zero degrees.
            if (_syncRotation)
            {
                float correctedAngle = Mathf.LerpAngle(
                    _rb.rotation, _receivedState.Rotation, rotLerpT);
                if (_makeRemoteKinematic)
                    _rb.rotation = correctedAngle;
                else
                    _rb.MoveRotation(correctedAngle);
            }

            // ── Velocity blending (non-kinematic only) ─────────────────────────
            if (!_makeRemoteKinematic)
            {
                if (_syncVelocity)
                    _rb.SetLinearVelocity(Vector2.Lerp(
                        _rb.GetLinearVelocity(), _receivedState.Velocity, posLerpT));

                if (_syncAngularVelocity)
                    _rb.angularVelocity = Mathf.Lerp(
                        _rb.angularVelocity, _receivedState.AngularVelocity, rotLerpT);
            }
        }

        // Componentwise finiteness predicate used by the inbound gate.  Mirrors
        // the 3-D component's helper.  NaN/Inf comparison short-circuits the
        // plausibility caps that follow, so any non-finite value must be
        // rejected before those caps run.
        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        // ── Internal API (called by NetworkManager) ────────────────────────────

        /// <summary>
        /// Apply an incoming 2-D physics-state update from a remote owner.
        /// Called by <c>NetworkManager.HandlePhysicsSync2DPacket</c> on non-owner clients.
        /// </summary>
        internal void ApplyRemoteState(PhysicsState2D incoming, byte changedMask)
        {
            // Componentwise finiteness gate.  Mirrors the 3-D component: the
            // plausibility caps further down compare with `>` operators which
            // short-circuit to false for NaN — letting NaN propagate into
            // Rigidbody2D.position / linearVelocity puts Box2D into an
            // unrecoverable state on most Unity versions (body disappears,
            // joints detach).  Reject the entire packet rather than persist
            // a corrupt sub-field.
            if ((changedMask & PhysicsPacketBuilder.ChangedPosition) != 0
                && (!IsFinite(incoming.Position.x) || !IsFinite(incoming.Position.y)))
                return;

            if ((changedMask & PhysicsPacketBuilder.ChangedRotation) != 0
                && !IsFinite(incoming.Rotation))
                return;

            if ((changedMask & PhysicsPacketBuilder.ChangedVelocity) != 0
                && (!IsFinite(incoming.Velocity.x) || !IsFinite(incoming.Velocity.y)))
                return;

            if ((changedMask & PhysicsPacketBuilder.ChangedAngularVelocity) != 0
                && !IsFinite(incoming.AngularVelocity))
                return;

            var settings = NetworkManager.Instance?.Settings;

            // Per-object inbound rate limit (token-bucket).  See NetworkRigidbody
            // for the threat model — same defence applies in 2-D.
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

            // Plausibility caps on velocity, angular velocity, and per-tick
            // position delta.  In 2-D, AngularVelocity is a single float in
            // degrees/second so we compare the absolute value.
            if (settings != null)
            {
                if (settings.maxLinearVelocity > 0f
                    && (changedMask & PhysicsPacketBuilder.ChangedVelocity) != 0
                    && incoming.Velocity.sqrMagnitude
                       > settings.maxLinearVelocity * settings.maxLinearVelocity)
                    return;

                if (settings.maxAngularVelocity > 0f
                    && (changedMask & PhysicsPacketBuilder.ChangedAngularVelocity) != 0
                    && Mathf.Abs(incoming.AngularVelocity) > settings.maxAngularVelocity)
                    return;

                if (settings.maxPositionDeltaPerTick > 0f
                    && (changedMask & PhysicsPacketBuilder.ChangedPosition) != 0
                    && _hasAppliedPosition
                    && (incoming.Position - _receivedState.Position).sqrMagnitude
                       > settings.maxPositionDeltaPerTick * settings.maxPositionDeltaPerTick)
                    return;
            }

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
        }

        /// <summary>
        /// Defensive 2-D owner reconciliation.  Mirrors
        /// <see cref="NetworkRigidbody.ApplyReconciliation"/>: applies a snap
        /// when the local position diverges from the server-confirmed position
        /// beyond <see cref="_ownerReconcileSnapThreshold"/>, and rejects
        /// non-finite payloads.
        /// </summary>
        internal void ApplyReconciliation(PhysicsState2D serverState, byte changedMask)
        {
            if (!_enableOwnerReconciliation || _rb == null || !IsOwner) return;

            if (_syncPosition && (changedMask & PhysicsPacketBuilder.ChangedPosition) != 0)
            {
                Vector2 sp = serverState.Position;
                if (float.IsNaN(sp.x) || float.IsInfinity(sp.x) ||
                    float.IsNaN(sp.y) || float.IsInfinity(sp.y))
                {
                    Debug.LogWarning(
                        "[RTMPE] NetworkRigidbody2D.ApplyReconciliation: rejected non-finite " +
                        $"server position {sp} — keeping local state.", this);
                    return;
                }

                float err = Vector2.Distance(_rb.position, sp);
                if (err > _ownerReconcileSnapThreshold)
                {
                    // Snap applied immediately (no FixedUpdate deferral).  2D
                    // physics ticks synchronously with FixedUpdate on the same
                    // thread, so a direct write here does not produce the
                    // LCM-beat artifact that the 3D path avoids with its
                    // deferred pending-snap buffer.  If multiple reconciliation
                    // packets arrive in a single frame, each one overwrites the
                    // previous position (newer-wins); this is intentional —
                    // stale intermediate states would produce a stutter even if
                    // applied, and the latest state is always the most accurate.
                    if (_makeRemoteKinematic) _rb.position = sp;
                    else                      _rb.MovePosition(sp);
                }
            }

            if (_syncRotation && (changedMask & PhysicsPacketBuilder.ChangedRotation) != 0)
            {
                float sr = serverState.Rotation;
                if (float.IsNaN(sr) || float.IsInfinity(sr)) return;

                float angleErr = Mathf.Abs(Mathf.DeltaAngle(_rb.rotation, sr));
                if (angleErr > 30f)
                {
                    if (_makeRemoteKinematic) _rb.rotation = sr;
                    else                      _rb.MoveRotation(sr);
                }
            }
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Capture the current <see cref="Rigidbody2D"/> state into a
        /// <see cref="PhysicsState2D"/> snapshot.
        /// </summary>
        public PhysicsState2D GetState()
        {
            if (_rb == null)
                return default;
            return new PhysicsState2D
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
