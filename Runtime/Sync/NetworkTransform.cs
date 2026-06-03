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

using System.Buffers;

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
            // Full-state finiteness gate — covers Position, Rotation
            // (including the .w component, which PhysX may surface as NaN
            // after divide-by-zero recovery), and Scale.  The legacy
            // position-only check left rotation and scale unguarded; an
            // internal caller (test harness, custom dispatcher, future
            // server-broadcast path) could persist a NaN quaternion into
            // transform.rotation, after which every Slerp / Quaternion.Angle
            // returns NaN for the lifetime of the GameObject.
            if (!IsFiniteState(serverState)) return;

            if (!_enablePrediction)
            {
                // Passive-reconciliation path.  Non-predicting owners still
                // honour server-authoritative deltas — without this snap an
                // owner that disabled prediction would silently desync from
                // every other peer until the next state-resync, masquerading
                // as authoritative while the rest of the room observed the
                // server's truth.  Snap directly without the replay loop
                // (no input buffer to walk) and without the lerp blend
                // (the owner did not predict, so there is nothing to blend
                // *from*).
                if (_syncPosition) transform.position    = serverState.Position;
                if (_syncRotation) transform.rotation    = serverState.Rotation;
                MarkClean();
                return;
            }

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
            // Replay the in-flight input log on top of the server-confirmed
            // pose BEFORE capturing the lerp target.  Without this step, the
            // lerp settles on the server's stale snapshot at confirmedInputTick
            // and silently discards every keystroke the player issued between
            // that tick and "now" — producing recurring rubber-band / micro-
            // stutter on every server beat under any non-zero RTT.  The snap
            // branch above already calls ReplayUnackedInputs; the medium-error
            // branch must apply the same canonical CSP flow so the visible
            // pose incorporates current-frame input.
            //
            // Sequence:
            //   1. Capture current transform.position as the lerp start.
            //   2. Temporarily fast-forward transform.position from
            //      serverState.Position through the unacked input log.
            //   3. Read the post-replay pose as the lerp target.
            //   4. Restore the transform to the lerp start so the per-frame
            //      Update() blend produces a true linear interpolation
            //      from the player's pre-correction visual pose to the
            //      replay-adjusted target.
            _reconcileStart         = transform.position;
            _reconcileStartRotation = transform.rotation;

            if (_syncPosition) transform.position = serverState.Position;
            if (_syncRotation) transform.rotation = serverState.Rotation;
            if (hasConfirmedTick)
                ReplayUnackedInputs(confirmedInputTick);

            _reconciledTarget         = transform.position;
            _reconciledRotationTarget = transform.rotation;

            // Restore the visible pose so the lerp blends from where the
            // player saw the object to the replayed target.  Without the
            // restore, the medium-error branch would degenerate into a
            // hard snap-then-lerp-back cycle that visually inverts the
            // intended smoothing.
            if (_syncPosition) transform.position = _reconcileStart;
            if (_syncRotation) transform.rotation = _reconcileStartRotation;

            _reconcileTimeLeft = ReconcileDuration;

            // Mark the restored pose as clean so the next frame's change-
            // detection compares against _reconcileStart (the current visible
            // position), not the pre-correction stale baseline.  Without this
            // call the outbound StateSync deltas computed during the lerp
            // reference the old _lastPosition and look like intentional owner
            // movement to the server — causing a rubber-band feedback loop on
            // lossy links (SDKS-02).  The snap branch already calls MarkClean()
            // at the equivalent point (line 398); this mirrors that pattern.
            MarkClean();
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

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Test seam — re-resolve the thresholds without re-spawning.  Lets a
        /// fixture mutate <see cref="NetworkSettings"/> after the component is
        /// constructed and exercise both the inherit and override paths.
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined.
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
#endif // UNITY_INCLUDE_TESTS

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

            // ── Sender finiteness gate ───────────────────────────────────
            // Reject a broadcast whose own components carry NaN/Inf BEFORE
            // they leave this client.  The peer-side parser already rejects
            // the same on receive, but a partially-corrupted local state
            // (e.g. physics-engine glitch on the owner) should not cost
            // bandwidth or surface as a peer-side anomaly when it can be
            // localised to the originator.  Skipping the send keeps the
            // last-known-good position broadcast as the peer baseline until
            // the owner's local state recovers.
            if (!IsFiniteState(state))
                return;

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

            // ── Pooled transform send path (GC Round 2, 2026-05-02) ─────
            // Rent the maximum possible size (full-precision + input tick =
            // 52 B); the quantized builder writes 29 B into the same buffer
            // when enabled.  ArrayPool.Rent may return a buffer larger than the
            // requested size, so we always pass the *exact* written length
            // (PAYLOAD_SIZE_WITH_TICK or QUANTIZED_PAYLOAD_SIZE_WITH_TICK) to
            // SendStateSync so the wire frame's payload_len matches the bytes we
            // actually wrote.  Renting always at the larger size keeps the rent
            // path single-bucket and lets the quantized fallback path reuse the
            // same buffer without a second rent.
            //
            // The send target is PacketType.StateSync (0x40), not
            // PacketType.Data (0x10): the gateway routes StateSync through
            // the NATS state-forward path so the Sync Service can ingest the
            // transform into the authoritative tick engine and broadcast it
            // to every peer in the room.  PacketType.Data was the legacy
            // wiring; the gateway echoes Data packets back to the sender, so
            // a transform sent under that type would never reach other
            // clients and would produce a self-feedback loop on the owner.
            //
            // SDKS-01: the current LocalTick is the client prediction tick that
            // produced this pose; it rides on the uplink so the server can echo
            // it back on the object's StateDelta as the reconciliation
            // watermark.  Trailing-field layout keeps the 48/25-byte prefixes
            // byte-identical, and the gateway accepts both the extended and
            // legacy lengths.
            uint inputTick = manager.LocalTick;
            var pool   = ArrayPool<byte>.Shared;
            var buffer = pool.Rent(TransformPacketBuilder.PAYLOAD_SIZE_WITH_TICK);
            try
            {
                int written = 0;
                if (settings != null && settings.quantizeTransforms)
                {
                    written = TransformPacketBuilder.BuildQuantizedUpdatePayloadInto(
                        buffer, 0, NetworkObjectId, state, inputTick);
                    // Quantized builder returns 0 on a degenerate / non-finite
                    // input.  Fall back to the full-precision encoder so the
                    // peer still receives a coherent update; the legacy
                    // decoder rejects NaN/Inf at parse time.
                }
                if (written == 0)
                {
                    written = TransformPacketBuilder.BuildUpdatePayloadInto(
                        buffer, 0, NetworkObjectId, state, inputTick);
                }

                manager.SendStateSync(buffer, written);
            }
            finally
            {
                pool.Return(buffer);
            }

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
            // Reject teleport requests on objects this peer does not own.
            // The velocity-cap reset that follows is a legitimate escape hatch
            // for the owner (respawn, scripted travel) but a hostile path if
            // any peer can invoke it on any object — a remote player could
            // bypass the anti-cheat clamp on the local owner's transform.
            if (!IsOwner)
            {
                UnityEngine.Debug.LogWarning(
                    $"[RTMPE] NetworkTransform.OwnerTeleportTo on object {NetworkObjectId} " +
                    "ignored: caller does not own this object.  Teleport is owner-only " +
                    "by design; remote peers must use the standard reconciled position " +
                    "stream, not this fast path.");
                return;
            }
            transform.position    = worldPosition;
            _lastSentPosition     = worldPosition;
            _lastSentTimeUnscaled = Time.unscaledTimeAsDouble;
            _hasLastSent          = false;
            MarkClean();
        }

#if UNITY_INCLUDE_TESTS
        /// <summary>Test seam — exposes the velocity-clamp helper without an Update tick.
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined.</summary>
        internal Vector3 ClampOwnerVelocityForTest(Vector3 candidate) => ClampOwnerVelocity(candidate);

        /// <summary>Test seam — primes the velocity-cap baseline.
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined.</summary>
        internal void PrimeVelocityBaselineForTest(Vector3 position, double timeUnscaled)
        {
            _lastSentPosition     = position;
            _lastSentTimeUnscaled = timeUnscaled;
            _hasLastSent          = true;
        }

        /// <summary>
        /// Test seam — enables client-side prediction so the medium-error
        /// reconciliation path (lerp scheduling + <see cref="MarkClean"/>)
        /// can be exercised without the Unity Inspector.
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined.
        /// </summary>
        internal void EnablePredictionForTest()
        {
            _enablePrediction = true;
        }

        /// <summary>
        /// Test seam — exposes the current value of <c>_reconcileTimeLeft</c>
        /// so tests can verify that a medium-error reconciliation actually
        /// scheduled a lerp (non-zero value) rather than snapping or no-oping.
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined.
        /// </summary>
        internal float ReconcileTimeLeftForTest => _reconcileTimeLeft;

        /// <summary>
        /// Test seam — pushes a synthetic input stamped with <paramref name="tick"/>
        /// into the CSP input buffer so tests can verify that reconciliation
        /// trims it against a server-supplied confirmed tick (SDKS-01).
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined.
        /// </summary>
        internal void PushInputForTest(uint tick) =>
            _inputBuffer.Push(new RTMPE.Core.InputPayload { Tick = tick });

        /// <summary>
        /// Test seam — number of unacknowledged inputs still buffered.  Used to
        /// assert the reconciliation watermark trimmed exactly the confirmed
        /// prefix and left the in-flight tail intact.
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined.
        /// </summary>
        internal int UnackedInputCountForTest => _inputBuffer.Count;
#endif // UNITY_INCLUDE_TESTS

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

            // Pooled input batch send path (GC Round 2, 2026-05-02).
            // Rent the exact size; pass the written length back so SendInput
            // wraps only the bytes we wrote.
            int size   = InputPacketBuilder.ComputeBatchPayloadSize(count);
            var pool   = ArrayPool<byte>.Shared;
            var buffer = pool.Rent(size);
            try
            {
                int written = InputPacketBuilder.BuildBatchPayloadInto(
                    buffer, 0, _inputSendScratch, count);
                manager.SendInput(buffer, written);
            }
            finally
            {
                pool.Return(buffer);
            }
        }

        // Componentwise IsFinite over the position, rotation, and scale of a
        // TransformState.  Used by SendTransformUpdate to quench broadcasts
        // produced from a corrupt local pose before they leave the
        // originator.  Quaternion.w is included because PhysX-derived
        // rotations may carry a non-unit quaternion that has a finite x/y/z
        // and a non-finite w after a divide-by-zero recovery.
        // .NET Standard 2.1 has float.IsFinite, but the SDK targets older
        // Unity runtimes where it is not always available.  Spelled out as
        // !IsNaN && !IsInfinity to match the same pattern used elsewhere
        // in this file (see ApplyReconciliation:280-283).
        private static bool IsFiniteState(TransformState s)
        {
            var p = s.Position;
            var r = s.Rotation;
            var c = s.Scale;
            return Finite(p.x) && Finite(p.y) && Finite(p.z)
                && Finite(r.x) && Finite(r.y) && Finite(r.z) && Finite(r.w)
                && Finite(c.x) && Finite(c.y) && Finite(c.z);
        }

        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
