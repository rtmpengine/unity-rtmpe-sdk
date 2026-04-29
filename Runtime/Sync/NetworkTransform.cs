// RTMPE SDK — Runtime/Sync/NetworkTransform.cs
//
// MonoBehaviour component that synchronises a GameObject's position, rotation,
// and optionally scale over the RTMPE network, with optional client-side
// prediction (CSP) for the owning client.
//
// Design decisions:
//  • Extends NetworkBehaviour — inherits NetworkObjectId, IsOwner,
//    IsSpawned, and the OnNetworkSpawn / OnNetworkDespawn callbacks.
//  • Owner-only sending: only the authoritative owner sends transform updates.
//    All other clients receive server-broadcast StateDelta payloads via the
//    HandleStateSyncPacket handler in NetworkManager.
//  • Two thresholds guard against send spam:
//    - _positionThreshold (0.01 world units): Vector3.Distance check
//    - _rotationThreshold (0.1 degrees):      Quaternion.Angle check
//  • MarkClean() records the last-sent transform so the next Update() compares
//    against that baseline, not the object's initial spawn position.
//  • GetState() / ApplyState() provide a type-safe boundary between Unity
//    transform fields and the serialisation layer, enabling unit testing.
//  • _syncScale defaults to false because scale rarely changes at runtime.
//
// Client-side prediction (CSP) — optional, disabled by default:
//  When _enablePrediction is true the owner client:
//    1. Calls GatherInput() each 30 Hz tick, stamps the tick, pushes it to
//       _inputBuffer.  Game code moves the character immediately (prediction).
//    2. Sends the resulting transform to the server as usual.
//    3. When the server broadcasts back an authoritative StateDelta for this
//       object, ApplyReconciliation() fires (routed by NetworkManager):
//         • Error > _snapThreshold  →  snap directly to server position.
//         • Error > _lerpThreshold  →  start a 100 ms smooth lerp toward
//           the server position (_reconcileTimeLeft drives the blend).
//         • Error <= _lerpThreshold →  accept prediction as-is (no visual pop).
//    4. AcknowledgeUpTo(LocalTick - 1) trims the buffer each reconciliation.
//
//  Non-owning clients are unaffected by the prediction fields — they use the
//  NetworkTransformInterpolator for smooth playback.
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

        [Tooltip("Position error (world units) below which the prediction is accepted as-is. " +
                 "Leave at -1 (default) to inherit NetworkSettings.reconcileLerpThreshold; " +
                 "any non-negative override applies per-instance and ignores the project default.")]
        [SerializeField] private float _lerpThreshold = ReconcileUseProjectDefault;

        [Tooltip("Position error (world units) above which the object snaps immediately to the " +
                 "server position rather than lerping. Leave at -1 (default) to inherit " +
                 "NetworkSettings.reconcileSnapThreshold.")]
        [SerializeField] private float _snapThreshold = ReconcileUseProjectDefault;

        // Sentinel meaning "this Inspector field has not been overridden — read the
        // project-wide default from NetworkSettings on spawn".  -1 is chosen because
        // a negative threshold has no physical meaning (Vector3.Distance is always
        // non-negative, so any genuine use-case value is >= 0); using a sentinel
        // distinct from 0 lets a designer explicitly opt into "never lerp / always
        // snap" by setting the threshold to literal 0.
        internal const float ReconcileUseProjectDefault = -1f;

        // Final, resolved thresholds — the two _*Threshold fields above hold the
        // raw Inspector value (potentially the sentinel); these hold the value
        // actually consulted by ApplyReconciliation each frame.  Resolution
        // happens once on spawn and again on settings changes (rare).
        private float _resolvedLerpThreshold;
        private float _resolvedSnapThreshold;

        // ── Last-sent baseline ─────────────────────────────────────────────────

        private Vector3    _lastPosition;
        private Quaternion _lastRotation;
        private Vector3    _lastScale;

        // ── CSP state ──────────────────────────────────────────────────────────

        // Input ring buffer: stores unacknowledged InputPayloads for rollback.
        private readonly InputBuffer _inputBuffer = new InputBuffer();

        // Reconciliation lerp target, start (captured once at schedule time),
        // and remaining-time accumulator.
        //
       // Why both start-pose and time accumulator: true linear interpolation
        // requires a fixed start position so each frame's blend is
        //  pos = Lerp(_reconcileStart, _reconciledTarget, elapsed / duration)
        // — not a recursive Lerp(transform.position, target, dt/timeLeft) which
        // is mathematically an exponential ease-out and only reaches the target
        // because of the explicit end-frame snap.  Capturing the start pose at
        // schedule time fixes the blend and keeps it framerate-independent at
        // 30 / 60 / 120 / 144 fps.
        private Vector3    _reconciledTarget;
        private Quaternion _reconciledRotationTarget;
        private Vector3    _reconcileStart;
        private Quaternion _reconcileStartRotation;
        private float      _reconcileTimeLeft;

        // Guards input collection to exactly one push per LocalTick.
        // Update() runs at frame rate (e.g. 60 Hz) but LocalTick advances at 30 Hz;
        // without this guard the buffer would accumulate two entries per tick at 60 fps.
        // A separate _hasLastInputTick flag is used instead of a sentinel value so
        // every uint LocalTick (including 0 and uint.MaxValue) is unambiguously valid.
        private uint _lastInputTick;
        private bool _hasLastInputTick;

        // Guards 0x43 input-batch transmission to exactly once per LocalTick.
        // Phase 2.x (2026-04-25) — server-authoritative input pipeline.
        // The batch is built fresh from the input buffer on each transmission
        // and replays every unacknowledged frame, so a missed tick is recovered
        // from the next batch — but a duplicate-per-frame send wastes bandwidth.
        // Companion bool flag mirrors the _hasLastInputTick pattern.
        private uint _lastInputSendTick;
        private bool _hasLastInputSendTick;

        // Reusable scratch array sized for the maximum batch.  Allocated once
        // per NetworkTransform so SendInputBatch() does NOT allocate on the
        // hot path — critical for staying inside the 33 ms tick budget when
        // many transforms send concurrently.  The InputBuffer's Capacity is
        // the upper bound on entries the buffer can ever hand out.
        private readonly InputPayload[] _inputSendScratch = new InputPayload[InputBuffer.Capacity];

        // Total wall-clock time (seconds) allowed for a medium-error reconciliation
        // lerp.  100 ms (3 ticks at 30 Hz) matches the previous 3-frame window at
        // 30 fps while remaining framerate-independent at 60/120/144 fps.
        private const float ReconcileDuration = 0.1f;

        // Reusable scratch for the rollback replay path.  Sized for the full
        // ring buffer so the worst-case "every frame is unacked" rollback
        // (saturated 2 s of input at 30 Hz) does not allocate during the
        // reconciliation hot path.
        private readonly InputPayload[] _replayScratch = new InputPayload[InputBuffer.Capacity];

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
            // Without an explicit confirmedInputTick on the wire, fall back to
            // (LocalTick - 1) as the most-recent input the server can possibly
            // have processed.  This matches the original SDK behaviour and is
            // the worst case for replay (no in-flight inputs to re-simulate)
            // — but the new replay-aware overload still keeps the buffer
            // intact above that watermark so any genuinely-in-flight input is
            // re-applied on top of the snapped state.
            var nm = NetworkManager.Instance;
            uint confirmedInputTick = 0u;
            bool hasConfirmedTick   = false;
            if (nm != null && nm.LocalTick > 0u)
            {
                confirmedInputTick = nm.LocalTick - 1u;
                hasConfirmedTick   = true;
            }
            ApplyReconciliation(serverState, confirmedInputTick, hasConfirmedTick);
        }

        /// <summary>
        /// Reconcile against a server-authoritative <see cref="TransformState"/>
        /// that explicitly carries the highest input tick the server has
        /// applied to this object.  Snaps the transform back to the server
        /// pose at <paramref name="confirmedInputTick"/>, then replays every
        /// buffered input strictly greater than that watermark via
        /// <see cref="NetworkBehaviour.ApplyInput"/> so the local prediction
        /// stays consistent with the keystrokes the player has issued since
        /// the server's snapshot was taken.
        /// </summary>
        internal void ApplyReconciliation(
            TransformState serverState,
            uint           confirmedInputTick,
            bool           hasConfirmedTick)
        {
            if (!_enablePrediction) return;

            var nm = NetworkManager.Instance;

            // Trim the input buffer up to the confirmed watermark.  The
            // server has now produced a state that incorporates every input
            // at or below this tick; anything still in the ring above it
            // remains "in-flight" and is re-simulated by the replay loop
            // further down.
            if (hasConfirmedTick)
                _inputBuffer.AcknowledgeUpTo(confirmedInputTick);

            float error = Vector3.Distance(serverState.Position, transform.position);

            // NaN/Inf positions (crafted packet or physics explosion) must not
            // corrupt transform.position through the reconciliation lerp path.
            if (float.IsNaN(error) || float.IsInfinity(error)) { MarkClean(); return; }

            // ── Server-correction cap & world bounds ─────────────────────────
            // A hostile or compromised server must not be able to teleport the
            // local client to an arbitrary world position.  Two configurable
            // guards are evaluated before any lerp / snap path runs:
            //  1. MaxServerCorrectionDistance: if the server's claimed
            //     position differs from the local prediction by more than
            //     this many world units, reject the correction outright
            //     (with a single warning per occurrence).
            //  2. WorldBounds: if a world AABB is configured and the server
            //     position lies outside it, reject the correction.
            // Both checks are bypassed when their cap is 0 / disabled so
            // back-compat with existing scenes is preserved.
            var settings = nm?.Settings;
            if (settings != null)
            {
                if (settings.maxServerCorrectionDistance > 0f
                    && error > settings.maxServerCorrectionDistance)
                {
                    Debug.LogWarning(
                        "[RTMPE] NetworkTransform.ApplyReconciliation: rejected " +
                        $"server correction of {error:F2}m (cap " +
                        $"{settings.maxServerCorrectionDistance:F2}m) — keeping " +
                        "local prediction.", this);
                    return;
                }

                if (settings.worldBoundsEnabled)
                {
                    Vector3 d = serverState.Position - settings.worldBoundsCenter;
                    Vector3 e = settings.worldBoundsExtents;
                    if (Mathf.Abs(d.x) > e.x
                        || Mathf.Abs(d.y) > e.y
                        || Mathf.Abs(d.z) > e.z)
                    {
                        Debug.LogWarning(
                            "[RTMPE] NetworkTransform.ApplyReconciliation: rejected " +
                            $"server position {serverState.Position} outside world " +
                            "bounds — keeping local prediction.", this);
                        return;
                    }
                }
            }

            if (error <= _resolvedLerpThreshold)
            {
                // Prediction was close enough — accept it, no visual correction.
                return;
            }

            if (error >= _resolvedSnapThreshold)
            {
                // Large error — snap immediately to avoid sustained visual drift.
                if (_syncPosition) transform.position = serverState.Position;
                if (_syncRotation) transform.rotation = serverState.Rotation;
                _reconcileTimeLeft = 0f;

                // ── CSP replay loop ───────────────────────────────────────────
                // After snapping back to the server-authoritative pose, walk
                // every input the player has issued since confirmedInputTick
                // and re-apply it on top of the snapped state.  This is the
                // canonical Quake / Source / Overwatch reconciliation flow:
                // server state is treated as ground-truth at the confirmed
                // tick, the local simulation rewinds to that tick, then
                // fast-forwards through the in-flight inputs to land at a
                // pose that already incorporates this frame's keystrokes.
                //
                // Without the replay step, every snap discards the player's
                // recent input — a noticeable hitch on every server
                // correction.  With it, the only visible artifact is the
                // small position delta produced by network latency, which
                // the lerp threshold absorbs on the next correction.
                if (hasConfirmedTick)
                    ReplayUnackedInputs(confirmedInputTick);

                // Update baseline so the next frame does not register a spurious
                // threshold violation and send the snapped position back to the server.
                MarkClean();
                return;
            }

            // Medium error — smooth linear lerp over ReconcileDuration seconds.
            //
           // Capture the CURRENT pose as the lerp start so the per-frame blend
            // computes a true linear interpolation (Lerp(start, target, t)) with
            // t = elapsed / duration, instead of a recursive Lerp from the
            // moving transform.position which produces an exponential ease-out.
            // Rotation is captured here so the per-frame slerp in Update() can
            // align toward the server-authoritative orientation alongside position.
            _reconciledTarget         = serverState.Position;
            _reconciledRotationTarget = serverState.Rotation;
            _reconcileStart           = transform.position;
            _reconcileStartRotation   = transform.rotation;
            _reconcileTimeLeft        = ReconcileDuration;
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
            _reconcileTimeLeft        = 0f;
            _reconciledRotationTarget = transform.rotation;
            _hasLastInputTick         = false;
            _hasLastInputSendTick     = false;
            // The first transform send after spawn is treated as a teleport;
            // without this reset the velocity cap would clamp legitimate
            // initial motion when the player spawns far from world origin.
            _hasLastSent              = false;
            ResolveReconcileThresholds();
        }

        /// <summary>
        /// Resolve the per-instance lerp/snap thresholds, falling back to
        /// <see cref="NetworkSettings.reconcileLerpThreshold"/> /
        /// <see cref="NetworkSettings.reconcileSnapThreshold"/> when the
        /// Inspector field is left at <see cref="ReconcileUseProjectDefault"/>.
        /// Also enforces snap &gt; lerp by clamping snap upward when a designer
        /// authors them inverted; otherwise the lerp branch (error &lt;= lerp)
        /// would always succeed and the snap branch would be unreachable.
        /// </summary>
        private void ResolveReconcileThresholds()
        {
            // Inspector overrides: any non-negative value wins over the
            // project default.  Negative values (the sentinel) trigger
            // resolution from NetworkSettings.
            float lerp = _lerpThreshold;
            float snap = _snapThreshold;

            var settings = NetworkManager.Instance != null
                ? NetworkManager.Instance.Settings
                : null;

            if (lerp < 0f) lerp = settings != null ? settings.reconcileLerpThreshold : 0.1f;
            if (snap < 0f) snap = settings != null ? settings.reconcileSnapThreshold : 2.0f;

            // Inverted authoring (snap < lerp) would make the snap branch
            // unreachable.  Clamp snap to lerp so the worst-case behaviour is
            // "every error above lerp snaps" — degraded but coherent.
            if (snap < lerp) snap = lerp;

            _resolvedLerpThreshold = lerp;
            _resolvedSnapThreshold = snap;
        }

        /// <summary>
        /// Test seam — re-resolve the thresholds without re-spawning.  Lets a
        /// fixture mutate <see cref="NetworkSettings"/> after the component is
        /// constructed and exercise both the inherit and override paths.
        /// </summary>
        internal void ConfigureReconcileForTest(float lerpThreshold, float snapThreshold)
        {
            _lerpThreshold = lerpThreshold;
            _snapThreshold = snapThreshold;
            ResolveReconcileThresholds();
        }

        /// <summary>Resolved lerp threshold (test seam).</summary>
        internal float ResolvedLerpThreshold => _resolvedLerpThreshold;

        /// <summary>Resolved snap threshold (test seam).</summary>
        internal float ResolvedSnapThreshold => _resolvedSnapThreshold;

        /// <summary>
        /// Per-tick CSP work, driven by NetworkManager's fixed-cadence tick
        /// loop so a long frame still collects (and ships) one input sample
        /// per simulated tick rather than silently dropping the stutter's
        /// worth of keystrokes.  See <see cref="NetworkBehaviour.OnFixedTick"/>
        /// for the contract.
        /// </summary>
        protected override void OnFixedTick(float deltaTime)
        {
            if (!_enablePrediction) return;

            var nm = NetworkManager.Instance;
            if (nm == null) return;

            // The tick driver guarantees exactly one invocation per simulated
            // tick, but the per-instance dedupe is retained as belt-and-braces
            // against re-entrant dispatch (e.g. a future settings reload that
            // restarts the loop mid-frame).
            if (_hasLastInputTick && nm.LocalTick == _lastInputTick) return;
            _lastInputTick    = nm.LocalTick;
            _hasLastInputTick = true;

            var input = CollectInput(nm.LocalTick);
            // Push returns false when the rollback window is saturated
            // (newest rejected to preserve the oldest as a replay anchor).
            // The drop is reflected in _inputBuffer.DroppedInputCount;
            // we deliberately do not log on the hot path because a
            // genuine network stall produces one rejection per tick and
            // the counter alone is enough to surface the condition via
            // the debugger window / telemetry.
            _inputBuffer.Push(input);

            // ── Server-authoritative input send (Phase 2.x) ─────────────
            // Re-ship the unacknowledged buffer once per tick.  This is
            // gated on _enablePrediction because the input buffer is only
            // filled when prediction is on; sending an empty batch every
            // tick from non-predicting owners would just burn bandwidth.
            // Each batch supersedes the prior, so a dropped UDP packet
            // costs at most one tick of latency.
            if (!_hasLastInputSendTick || nm.LocalTick != _lastInputSendTick)
            {
                _lastInputSendTick    = nm.LocalTick;
                _hasLastInputSendTick = true;
                SendInputBatch();
            }
        }

        /// <summary>
        /// Each frame:
        ///  • Owner (with or without prediction): if transform changed beyond
        ///    thresholds, transmit an update and mark clean.
        ///  • Reconciliation lerp: if pending, blend toward server target.
        ///
        /// CSP input collection no longer lives here — it runs from
        /// <see cref="OnFixedTick"/> so the cadence is locked to the
        /// simulation tick, not the visual frame.
        /// </summary>
        private void Update()
        {
            if (!IsOwner || !IsSpawned) return;

            // ── Transform send ────────────────────────────────────────────────
            if (HasPositionChanged || HasRotationChanged || HasScaleChanged)
            {
                SendTransformUpdate();
                MarkClean();
            }

            // ── Reconciliation lerp ───────────────────────────────────────────
            // True linear interpolation from the captured start pose to the
            // server target, parameterised by elapsed wall-clock time over
            // ReconcileDuration.  Framerate-independent at 30 / 60 / 120 / 144 fps
            // because the same elapsed value produces the same blend factor.
            //
           // Blend BOTH position and rotation so a partial mid-air rotation
            // correction does not get left behind when the position lerp
            // completes first.
            if (_reconcileTimeLeft > 0f)
            {
                _reconcileTimeLeft -= Time.deltaTime;
                float elapsed = ReconcileDuration - _reconcileTimeLeft;
                float t       = Mathf.Clamp01(elapsed / ReconcileDuration);

                if (_syncPosition)
                {
                    transform.position = Vector3.Lerp(
                        _reconcileStart,
                        _reconciledTarget,
                        t);
                }
                if (_syncRotation)
                {
                    transform.rotation = Quaternion.Slerp(
                        _reconcileStartRotation,
                        _reconciledRotationTarget,
                        t);
                }
                if (_reconcileTimeLeft <= 0f)
                {
                    // Snap to exact target on completion, then refresh baseline
                    // so the next frame does not echo the corrected state back.
                    if (_syncPosition) transform.position = _reconciledTarget;
                    if (_syncRotation) transform.rotation = _reconciledRotationTarget;
                    _reconcileTimeLeft = 0f;
                    MarkClean();
                }
            }
        }

        // ── Private helpers ────────────────────────────────────────────────────

        /// <summary>
        /// Re-apply every unacknowledged input strictly after
        /// <paramref name="confirmedInputTick"/> on top of the current
        /// (just-snapped) transform.  Inputs are replayed in oldest-first
        /// order so the resulting pose is the same one the predicted
        /// simulation produced before reconciliation arrived, modulo the
        /// server's correction at the confirmed tick.
        /// </summary>
        private void ReplayUnackedInputs(uint confirmedInputTick)
        {
            int n = _inputBuffer.CopyUnacknowledgedAfter(confirmedInputTick, _replayScratch);
            if (n == 0) return;

            // Use the same fixed simulation step that GatherInput / SendInputBatch
            // observe so replay produces a pose identical to the original
            // prediction's deterministic step.  NetworkManager.VariableFlushInterval
            // is the authoritative 30 Hz cadence and is exposed via Settings;
            // when no manager is reachable (edit-mode tests) the literal
            // 1/30 falls back transparently.
            const float DefaultFixedDt = 1f / 30f;
            var nm = NetworkManager.Instance;
            float dt = nm != null ? nm.FixedTickInterval : DefaultFixedDt;

            for (int i = 0; i < n; i++)
                ReplayInput(_replayScratch[i], dt);
        }

        private void SendTransformUpdate()
        {
            var manager = NetworkManager.Instance;
            if (manager == null) return;

            var state    = GetState();
            var settings = manager.Settings;

            // ── Owner velocity cap ────────────────────────────────────────
            // Clamp the broadcast position when the apparent per-second
            // velocity exceeds the project-wide cap.  This is a client-side
            // anti-cheat scaffold; gateway-side reconciliation will refine
            // the policy in a future iteration.  The first send after spawn
            // (or after OwnerTeleportTo) skips the check because there is
            // no previous baseline to derive a velocity from.
            if (_syncPosition && settings != null && settings.maxOwnerVelocityMetersPerSecond > 0f)
            {
                state.Position = ClampOwnerVelocity(state.Position);
            }

            byte[] payload = null;
            if (settings != null && settings.quantizeTransforms)
            {
                payload = TransformPacketBuilder.BuildQuantizedUpdatePayload(NetworkObjectId, state);
                // Quantized builder returns null on a degenerate / non-finite
                // input.  Fall back to the full-precision encoder so the peer
                // still receives a coherent update; the legacy decoder rejects
                // NaN/Inf at parse time.
            }
            payload ??= TransformPacketBuilder.BuildUpdatePayload(NetworkObjectId, state);

            manager.SendData(payload);

            _lastSentPosition = state.Position;
            _lastSentTimeUnscaled = Time.unscaledTimeAsDouble;
            _hasLastSent = true;
        }

        // Owner-velocity cap state.  Initialised on first send (via
        // OnNetworkSpawn or the first SendTransformUpdate); reset by
        // OwnerTeleportTo to mark the next send as a legitimate teleport.
        private Vector3 _lastSentPosition;
        private double  _lastSentTimeUnscaled;
        private bool    _hasLastSent;

        private Vector3 ClampOwnerVelocity(Vector3 candidate)
        {
            // First send: capture baseline, skip the check (no prior sample
            // means no velocity can be derived).
            if (!_hasLastSent) return candidate;

            var settings = NetworkManager.Instance?.Settings;
            float cap = settings != null ? settings.maxOwnerVelocityMetersPerSecond : 0f;
            if (cap <= 0f) return candidate;

            double now = Time.unscaledTimeAsDouble;
            float dt = (float)(now - _lastSentTimeUnscaled);
            if (dt <= 0f) return candidate;

            Vector3 delta = candidate - _lastSentPosition;
            float distance = delta.magnitude;
            float maxDistance = cap * dt;
            if (distance <= maxDistance) return candidate;

            // Clamp by linear interpolation along the requested displacement.
            // A genuine teleport (respawn, scripted cinematic) must call
            // OwnerTeleportTo to skip this gate; without that escape hatch
            // legitimate level transitions would be visibly throttled.
            float t = distance > 0f ? maxDistance / distance : 0f;
            return _lastSentPosition + delta * t;
        }

        /// <summary>
        /// Reset the owner-velocity baseline so the next normal send is treated
        /// as a teleport rather than an instantaneous high-speed move.  Call
        /// this from gameplay code that legitimately repositions the owner
        /// (respawn, fast travel, scripted cinematic).  Does NOT itself send a
        /// packet — the next change-detection update emits the new pose.
        /// </summary>
        public void OwnerTeleportTo(Vector3 worldPosition)
        {
            transform.position    = worldPosition;
            _lastSentPosition     = worldPosition;
            _lastSentTimeUnscaled = Time.unscaledTimeAsDouble;
            _hasLastSent          = false;
            MarkClean();
        }

        /// <summary>Test seam — exposes the velocity-clamp helper without an Update tick.</summary>
        internal Vector3 ClampOwnerVelocityForTest(Vector3 candidate) => ClampOwnerVelocity(candidate);

        /// <summary>Test seam — primes the velocity-cap baseline.</summary>
        internal void PrimeVelocityBaselineForTest(Vector3 position, double timeUnscaled)
        {
            _lastSentPosition     = position;
            _lastSentTimeUnscaled = timeUnscaled;
            _hasLastSent          = true;
        }

        /// <summary>
        /// Phase 2.x (2026-04-25) — server-authoritative input send.
        ///
       /// Snapshots the unacknowledged input ring buffer into the
        /// pre-allocated scratch array, builds a 0x43 batch payload, and
        /// hands it to <see cref="NetworkManager.SendInput"/> for unreliable
        /// UDP transmission.  Called at most once per LocalTick from
        /// <see cref="Update"/>.
        ///
       /// Bandwidth: at 30 Hz with the default 64-entry buffer, one full
        /// batch is 2 + 13×64 = 834 bytes per object.  In steady state the
        /// server acknowledges within 2-3 ticks, so the typical batch holds
        /// 2-3 entries (~30-50 bytes).
        /// </summary>
        private void SendInputBatch()
        {
            var manager = NetworkManager.Instance;
            if (manager == null) return;

            int count = _inputBuffer.CopyUnacknowledgedTo(_inputSendScratch);
            if (count == 0) return;

            var payload = InputPacketBuilder.BuildBatchPayload(_inputSendScratch, count);
            manager.SendInput(payload);
        }
    }
}
