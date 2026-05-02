// RTMPE SDK — Runtime/Core/Aead/SessionKeyStore.cs
//
// Cohesive bundle of all per-session AEAD crypto state used by NetworkManager
// to encrypt outbound packets and authenticate / replay-protect inbound ones.
//
// Design rationale:
//   The five per-session AEAD fields — SessionKeys, ReplayWindow, CryptoId,
//   OutboundNonceCounter, LastInboundAppSequence — form a single logical
//   lifecycle: either ALL are valid (active session) or ALL are reset (no
//   session). Mixing the two states is the failure mode that produces AEAD
//   nonce reuse, the most catastrophic crypto error possible. Bundling them
//   into one object that can only be transitioned through Install* / Clear
//   makes that invariant explicit and independently reviewable.
//
// Threading model:
//   • SessionKeys / ReplayWindow / CryptoId are written only on the Unity
//     main thread during handshake completion (OnChallenge → OnSessionAck);
//     read concurrently from the receive path (background thread → main
//     thread dispatch).
//   • OutboundNonceCounter mutations route through Interlocked.Increment
//     (claim) and Interlocked.Exchange (reset); never written directly.
//   • LastInboundAppSequence mutations use a Volatile.Read +
//     Interlocked.CompareExchange CAS loop; the property surface uses
//     Interlocked.Read so 64-bit atomicity holds on 32-bit ARM/IL2CPP too.

using System.Threading;

using RTMPE.Crypto;
using RTMPE.Crypto.Internal;

namespace RTMPE.Core.Aead
{
    internal sealed class SessionKeyStore
    {
        // ── Backing fields ────────────────────────────────────────────────
        private SessionKeys  _sessionKeys;
        private ReplayWindow _replayWindow;
        private uint         _cryptoId;
        private long         _outboundNonceCounter   = -1L;
        private long         _lastInboundAppSequence = -1L;

        // ── Read accessors ────────────────────────────────────────────────
        /// <summary>
        /// Active session keys, or <see langword="null"/> when no session is
        /// established. Callers that need to encrypt / decrypt must guard
        /// every access on <see cref="IsReady"/> or the explicit null check.
        /// </summary>
        public SessionKeys SessionKeys => _sessionKeys;

        /// <summary>
        /// Sliding-bitmap anti-replay window for inbound AEAD-authenticated
        /// packets. Allocated once via <see cref="EnsureReplayWindow"/> and
        /// then re-used across reconnects (state cleared in-place by
        /// <see cref="Clear"/>) so the buffer does not churn the GC.
        /// </summary>
        public ReplayWindow ReplayWindow => _replayWindow;

        /// <summary>
        /// Crypto-instance identifier folded into the 12-byte AEAD nonce
        /// (low 4 bytes, little-endian). Assigned by the gateway in the
        /// <c>SessionAck</c> packet; zero before <see cref="InstallCryptoId"/>.
        /// </summary>
        public uint CryptoId => _cryptoId;

        /// <summary>
        /// True when <see cref="SessionKeys"/> is non-null. Callers must use
        /// this — not the raw <c>SessionKeys != null</c> check — so a future
        /// audit can find every session-readiness gate from a single
        /// reference search.
        /// </summary>
        public bool IsReady => _sessionKeys != null;

        // ── Outbound nonce counter ────────────────────────────────────────
        /// <summary>
        /// Atomically claims the next outbound AEAD nonce counter and
        /// returns the new value. Starts at <c>-1</c> so the first call
        /// yields <c>0</c>, matching the gateway's <c>NonceGenerator</c>
        /// initial state.
        /// </summary>
        public long IncrementOutboundNonceCounter() =>
            Interlocked.Increment(ref _outboundNonceCounter);

        /// <summary>
        /// Resets the outbound nonce counter to its initial <c>-1</c> state
        /// so the next <see cref="IncrementOutboundNonceCounter"/> yields
        /// zero on the new session.
        /// </summary>
        public void ResetOutboundNonceCounter() =>
            Interlocked.Exchange(ref _outboundNonceCounter, -1L);

        // ── Last inbound application sequence (monotonic) ────────────────
        /// <summary>
        /// Reads the current high-water value of the inbound application
        /// sequence using <see cref="Interlocked.Read(ref long)"/>, which is
        /// atomic on 32-bit architectures (where a plain field read is not).
        /// </summary>
        public long ReadLastInboundAppSequence() =>
            Interlocked.Read(ref _lastInboundAppSequence);

        /// <summary>
        /// Resets the monotonic high-water mark to <c>-1</c> so the next
        /// session begins sequence tracking from scratch.
        /// </summary>
        public void ResetLastInboundAppSequence() =>
            Interlocked.Exchange(ref _lastInboundAppSequence, -1L);

        /// <summary>
        /// CAS-advances the inbound app-sequence high-water mark to
        /// <paramref name="newSeq"/> if and only if it is strictly greater
        /// than the currently observed value. No-op otherwise.
        /// </summary>
        /// <remarks>
        /// Uses a <see cref="Volatile.Read(ref long)"/> +
        /// <see cref="Interlocked.CompareExchange(ref long, long, long)"/>
        /// CAS loop. The receive path under UDP reorder may attempt to advance
        /// with a value below the current high-water mark; those attempts are
        /// no-ops to keep the sequence observable as strictly monotonic.
        /// </remarks>
        public void AdvanceLastInboundAppSequenceMonotonic(long newSeq)
        {
            long observed;
            do { observed = Volatile.Read(ref _lastInboundAppSequence); }
            while (newSeq > observed
                && Interlocked.CompareExchange(
                       ref _lastInboundAppSequence, newSeq, observed) != observed);
        }

        // ── Lifecycle: install ────────────────────────────────────────────
        /// <summary>
        /// Installs the freshly-derived session keys produced by
        /// <c>HandshakeHandler.DeriveSessionKeys</c>. Called from
        /// <c>OnChallenge</c> on the Unity main thread.
        /// </summary>
        public void InstallSessionKeys(SessionKeys keys)
        {
            _sessionKeys = keys;
        }

        /// <summary>
        /// Allocates the inbound replay window if it is not already
        /// allocated. Reuses the existing buffer across sessions when one
        /// already exists; <see cref="Clear"/> only resets state in-place.
        /// </summary>
        public void EnsureReplayWindow()
        {
            if (_replayWindow == null)
                _replayWindow = new ReplayWindow();
        }

        /// <summary>
        /// Records the gateway-assigned crypto identifier delivered in the
        /// <c>SessionAck</c> packet. Called from <c>OnSessionAck</c> on the
        /// Unity main thread.
        /// </summary>
        public void InstallCryptoId(uint cryptoId)
        {
            _cryptoId = cryptoId;
        }

        // ── Lifecycle: clear (granular) ───────────────────────────────────
        /// <summary>
        /// Disposes the active <see cref="SessionKeys"/> and nulls the
        /// reference. <see cref="SessionKeys.Dispose"/> zeros the AEAD key
        /// material BEFORE the reference is dropped, so a GC scan of
        /// compacted heap memory cannot recover usable keys.
        /// </summary>
        public void DisposeKeys()
        {
            _sessionKeys?.Dispose();
            _sessionKeys = null;
        }

        /// <summary>
        /// Resets the inbound replay window's bitmap state in place. The
        /// allocated buffer is retained for re-use across reconnects so the
        /// reset path does not churn the GC. Pairs with
        /// <c>ClearSessionData</c>'s <c>_inboundReplayWindow?.Reset()</c>.
        /// </summary>
        public void ResetReplayWindow()
        {
            _replayWindow?.Reset();
        }

        /// <summary>
        /// Nulls the inbound replay window so the receive path's strict
        /// null-reject is the only way an AEAD frame can be observed before
        /// the new session's keys are installed. Pairs with
        /// <c>StartReconnectAttempt</c>'s <c>_inboundReplayWindow = null</c>
        /// — that path requires the field to be null (not merely reset) so
        /// <see cref="EnsureReplayWindow"/> re-allocates fresh on the next
        /// <c>OnChallenge</c>.
        /// </summary>
        public void DropReplayWindow()
        {
            _replayWindow = null;
        }

        /// <summary>
        /// Resets the crypto identifier to zero. Used by
        /// <c>ClearSessionData</c>; the next session installs a fresh
        /// identifier through <see cref="InstallCryptoId"/>.
        /// </summary>
        public void ResetCryptoId()
        {
            _cryptoId = 0;
        }

        // ── Lifecycle: clear (atomic bundle) ──────────────────────────────
        /// <summary>
        /// Resets every per-session AEAD field to its no-session state in a
        /// single contiguous step.  Use this in preference to calling the
        /// granular reset methods directly so the all-valid-or-all-reset
        /// invariant declared at the class header is enforced from one
        /// reviewable site.
        /// </summary>
        /// <remarks>
        /// Order matters: <see cref="DisposeKeys"/> runs first so the
        /// AEAD pipeline (every encrypt / decrypt path is gated on
        /// <see cref="IsReady"/>) is disabled before any sequence /
        /// counter / replay-window state is touched.  No subsequent packet
        /// processing can observe a partially-reset bundle because any
        /// AEAD operation rejects on the null <see cref="SessionKeys"/>.
        /// The replay-window buffer is reset in place rather than dropped
        /// so the next session reuses the allocation.
        /// </remarks>
        public void ResetAllForSession()
        {
            DisposeKeys();
            ResetCryptoId();
            ResetOutboundNonceCounter();
            ResetLastInboundAppSequence();
            ResetReplayWindow();
        }
    }
}
