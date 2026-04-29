// RTMPE SDK — Runtime/Rpc/EnhancedRpcVerifier.cs
//
// Trust model for inbound Enhanced RPC packets:
//
//  Field        Trust source                  Verification on receive
//  ─────────    ───────────────────────────   ─────────────────────────
//  AEAD tag     gateway-attested              transport pipeline
//  methodId     wire-supplied                 looked up against the
//                                             receiving object's
//                                             [RtmpeRpc] map; missing
//                                             ⇒ drop
//  senderId     wire-supplied (hostile)       structural reject (zero)
//                                             + optional membership
//                                             callback (SenderVerifier)
//  requestId    wire-supplied                 opaque correlation token,
//                                             not security-relevant
//  objectId     wire-supplied (hostile)       must resolve to a live
//                                             entry in the spawn
//                                             registry; verified at
//                                             dispatch time by
//                                             NetworkManager
//  target       wire-supplied (hostile)       must be a defined value
//                                             of the RpcTarget enum;
//                                             undefined values ⇒ drop
//  params       wire-supplied (hostile)       per-type bounds checks in
//                                             RpcSerializer; INetwork-
//                                             Serializable type names
//                                             must resolve via the
//                                             explicit RpcTypeRegistry
//
// AEAD authenticates the gateway as the relay, NOT the originating peer.
// A malicious peer in the same room can craft any senderId/objectId/target
// it likes; the gateway only attests "I delivered this payload to you", not
// "this payload was honestly authored".  Treat every wire-derived field as
// hostile until verified.
//
// Extension hooks:
//  • SenderVerifier  — integrators set this delegate to gate inbound
//                      senderId values against their own room/session
//                      roster.  Default: accept any non-zero senderId
//                      (zero is structurally rejected because it is the
//                      SDK's "uninitialised session" sentinel).
//  • ObjectExistsVerifier — optional sanity hook.  NetworkManager
//                      already gates dispatch on the spawn registry,
//                      so the default returns true (no extra check
//                      beyond the registry lookup the dispatch path
//                      already performs).  Provided so security-
//                      sensitive games can layer additional checks
//                      (e.g. "is this object in the sender's interest
//                      set?") without monkey-patching NetworkManager.
//
// The verifier is intentionally a static, allocation-free policy object.
// Inbound RPC dispatch is on the hot path and must not allocate per
// packet.

using System;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Pluggable verification policy applied to every inbound Enhanced
    /// RPC packet before it reaches a <c>NetworkBehaviour</c>.
    ///
   /// <para>The defaults are conservative — structurally-malformed
    /// packets are dropped, but membership-style checks no-op until the
    /// integrator wires up a roster source.  Wire <see cref="SenderVerifier"/>
    /// to your room manager during application bootstrap.</para>
    /// </summary>
    public static class EnhancedRpcVerifier
    {
        /// <summary>
        /// Application-supplied membership check.  Returns
        /// <see langword="true"/> when <paramref name="senderId"/>
        /// (gateway session ID) is currently a recognised peer of the
        /// local session.
        ///
       /// <para>Default: <see langword="null"/> (no integrator hook
        /// configured).  When null the parser still drops zero
        /// senderIds but otherwise accepts the value — strict roster
        /// enforcement requires the integrator to install this delegate.
        /// Document deployment guidance: in untrusted-peer environments,
        /// configuring this hook is mandatory.</para>
        /// </summary>
        public static Func<ulong, bool> SenderVerifier { get; set; } = DefaultSenderVerifier;

        // One-time advisory so production deployments that never wire a
        // roster-aware verifier surface the gap exactly once at the first
        // non-self RPC.  Spammy per-packet warnings are unacceptable on the
        // hot path; a single warning is enough to be actionable in CI logs
        // without drowning the console.
        private static int _defaultVerifierWarned;

        /// <summary>
        /// Conservative default: rejects the SDK's "uninitialised session"
        /// sentinel (zero) and accepts every other gateway session ID, but
        /// emits a one-time warning so integrators discover the gap before
        /// shipping.  Replace with a roster-aware delegate during bootstrap
        /// in any production build that hosts untrusted peers.
        /// </summary>
        internal static bool DefaultSenderVerifier(ulong senderId)
        {
            if (senderId == 0UL) return false;
            if (System.Threading.Interlocked.CompareExchange(
                    ref _defaultVerifierWarned, 1, 0) == 0)
            {
                UnityEngine.Debug.LogWarning(
                    "[RTMPE] EnhancedRpcVerifier.SenderVerifier is using the " +
                    "permissive default policy (accept any non-zero sender). " +
                    "Production deployments with untrusted peers MUST install " +
                    "a roster-aware verifier so spoofed senderIds are rejected. " +
                    "Set EnhancedRpcVerifier.SenderVerifier during application " +
                    "bootstrap.");
            }
            return true;
        }

        /// <summary>
        /// Optional secondary hook for object-id verification beyond the
        /// spawn-registry lookup that NetworkManager already performs.
        /// Default: <see langword="null"/> (no additional check).  Use
        /// to enforce game-specific invariants such as "object must be
        /// owned by the sender" or "object must be in the sender's
        /// interest set".
        /// </summary>
        public static Func<ulong, bool> ObjectExistsVerifier { get; set; }

        // ── Roster-anchored sender verification ────────────────────────────
        //
        // The roster anchor is a triple of optional callbacks.  When all are
        // supplied AND the local SDK is currently joined to a room, inbound
        // RPCs are accepted only when the wire-supplied senderId belongs to
        // the active room roster (or equals the local session ID).  When
        // any callback is missing, or the SDK is not currently in a room,
        // we fall back to the permissive default (non-zero accepted, with a
        // one-time warning) so single-player / lobby-browser flows still
        // work.  Wiring is performed by NetworkManager at construction time
        // and torn down on Cleanup() / ClearSessionData() to avoid a stale
        // closure outliving the manager that captured it.

        /// <summary>Callback that returns <see langword="true"/> when the
        /// local SDK is currently joined to a room.  When this is null OR
        /// returns false, the roster anchor is bypassed and the permissive
        /// default policy applies.</summary>
        public static Func<bool> IsRoomJoined { get; set; }

        /// <summary>Callback that returns the local session ID (the value
        /// stamped into outbound senderIds by this client).  Used by the
        /// roster-anchored verifier to admit self-originated RPCs even when
        /// no broader roster source has been wired.</summary>
        public static Func<ulong> LocalSessionIdProvider { get; set; }

        /// <summary>Callback that returns <see langword="true"/> when the
        /// supplied <paramref name="sessionId"/> is a current member of the
        /// active room.  Integrators that maintain a session-ID roster
        /// (built from gateway events not currently exposed by the open-
        /// source RoomManager) install this hook to extend acceptance
        /// beyond self.  Without this hook, a roster-anchored session
        /// accepts only its own session id while in a room.</summary>
        public static Func<ulong, bool> IsRosterMemberSession { get; set; }

        // Once-per-AppDomain warning emitted when a roster-anchored verifier
        // is in force but has no IsRosterMemberSession callback wired.  The
        // resulting "self-only" policy is conservative but may surprise
        // integrators who expected peer RPCs to flow; surface the gap once.
        private static int _rosterAnchorSelfOnlyWarned;

        /// <summary>
        /// Roster-anchored verifier suitable for assignment to
        /// <see cref="SenderVerifier"/>.  Behaviour:
        /// <list type="bullet">
        /// <item><description>If <see cref="IsRoomJoined"/> is null or returns false,
        /// fall through to <see cref="DefaultSenderVerifier"/> (permissive).</description></item>
        /// <item><description>If the senderId equals the local session id (per
        /// <see cref="LocalSessionIdProvider"/>), accept.</description></item>
        /// <item><description>If <see cref="IsRosterMemberSession"/> is wired, defer
        /// to it; otherwise emit a one-time advisory and reject (self-only).</description></item>
        /// </list>
        /// Allocation-free; safe to call from the receive hot path.
        /// </summary>
        public static bool RoomAnchoredSenderVerifier(ulong senderId)
        {
            if (senderId == 0UL) return false;

            var inRoom = IsRoomJoined;
            if (inRoom == null || !inRoom())
            {
                // Outside a room (lobby / browse / single-player) we cannot
                // anchor against a roster — defer to the permissive default
                // so SDK consumers do not break in pre-room flows.
                return DefaultSenderVerifier(senderId);
            }

            var localProvider = LocalSessionIdProvider;
            ulong localId = localProvider != null ? localProvider() : 0UL;
            if (localId != 0UL && senderId == localId) return true;

            var rosterCheck = IsRosterMemberSession;
            if (rosterCheck != null) return rosterCheck(senderId);

            // No session-ID roster available — surface the gap once and
            // reject every non-self senderId.  This is the correct
            // conservative posture in untrusted-peer environments because a
            // roster anchor that admits anyone offers no improvement over
            // the permissive default.
            if (System.Threading.Interlocked.CompareExchange(
                    ref _rosterAnchorSelfOnlyWarned, 1, 0) == 0)
            {
                UnityEngine.Debug.LogWarning(
                    "[RTMPE] EnhancedRpcVerifier roster anchor active but " +
                    "IsRosterMemberSession is not wired — accepting only " +
                    "self-originated RPCs while in a room.  Wire " +
                    "IsRosterMemberSession to admit peer RPCs.");
            }
            return false;
        }

        /// <summary>
        /// Reset every hook to its default <see langword="null"/> state.
        /// Called automatically on Play-Mode entry so a second run does
        /// not inherit hook delegates that captured stale Domain
        /// references.  Public so tests and integrators can reset
        /// between scenarios.
        /// </summary>
        public static void Reset()
        {
            SenderVerifier         = DefaultSenderVerifier;
            ObjectExistsVerifier   = null;
            IsRoomJoined           = null;
            LocalSessionIdProvider = null;
            IsRosterMemberSession  = null;
            System.Threading.Interlocked.Exchange(ref _defaultVerifierWarned,    0);
            System.Threading.Interlocked.Exchange(ref _rosterAnchorSelfOnlyWarned, 0);
        }

        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlayModeEnter() => Reset();

        /// <summary>
        /// Validate the wire-derived target byte against the
        /// <see cref="RpcTarget"/> enum.  Undefined values indicate a
        /// malformed or hostile sender and the packet must be dropped.
        /// </summary>
        public static bool IsTargetDefined(byte targetByte)
            => Enum.IsDefined(typeof(RpcTarget), targetByte);

        /// <summary>
        /// Apply the configured sender policy.  Zero is always rejected
        /// (it is the SDK's pre-authentication sentinel); non-zero
        /// values are passed through <see cref="SenderVerifier"/> when
        /// the integrator has installed one, otherwise accepted.
        /// </summary>
        public static bool IsSenderAcceptable(ulong senderId)
        {
            if (senderId == 0UL) return false;
            var hook = SenderVerifier;
            // The default verifier is non-null (see initialiser); a caller
            // that explicitly assigns null falls back to "structural-only"
            // semantics — zero rejected, every non-zero accepted.
            return hook == null || hook(senderId);
        }

        /// <summary>
        /// Apply the optional object-id policy.  Returns
        /// <see langword="true"/> when no hook is configured (the
        /// SpawnManager registry lookup performed by
        /// <c>NetworkManager.OnEnhancedRpcRequest</c> remains the
        /// authoritative existence check).
        /// </summary>
        public static bool IsObjectAcceptable(ulong objectId)
        {
            var hook = ObjectExistsVerifier;
            return hook == null || hook(objectId);
        }
    }
}
