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
       /// <para>Default: <see cref="DefaultSenderVerifier"/>, which
        /// accepts ONLY the local session ID — peers cannot be admitted
        /// until the integrator either wires a roster-aware delegate
        /// here, switches to <see cref="RoomAnchoredSenderVerifier"/>,
        /// or opts in to a server-attested verifier via
        /// <see cref="SetServerAttestedSenderVerifier"/>.  AEAD only
        /// authenticates the gateway as relay, NOT the originating peer,
        /// so a permissive default would let any room member impersonate
        /// any other.</para>
        /// </summary>
        public static Func<ulong, bool> SenderVerifier { get; set; } = DefaultSenderVerifier;

        /// <summary>
        /// Optional callback returning the local session ID used by the
        /// self-only default verifier.  Wired by NetworkManager so the
        /// SDK can recognise its own outbound RPCs that the gateway
        /// echoes back to it.  When this is null or returns zero the
        /// default verifier rejects every sender.
        /// </summary>
        public static Func<ulong> SelfSessionIdProvider { get; set; }

        /// <summary>
        /// Conservative default sender policy.
        ///
       /// <para>Accepts ONLY the local session ID (as reported by
        /// <see cref="SelfSessionIdProvider"/>); every other senderId is
        /// rejected.  This is the only safe default: AEAD authenticates
        /// the gateway as relay but does not bind a packet to its
        /// originating peer, so a peer in the room can otherwise stamp
        /// any senderId it likes onto an RPC and impersonate any other
        /// room member.</para>
        ///
       /// <para>Integrators that need peer RPCs MUST opt in explicitly
        /// by either:</para>
        /// <list type="bullet">
        /// <item><description>installing a roster-aware delegate on
        ///   <see cref="SenderVerifier"/>;</description></item>
        /// <item><description>switching to
        ///   <see cref="RoomAnchoredSenderVerifier"/> with the room
        ///   hooks wired; or</description></item>
        /// <item><description>calling
        ///   <see cref="SetServerAttestedSenderVerifier"/> if (and only
        ///   if) the gateway is known to attest senderIds out-of-band.</description></item>
        /// </list>
        /// </summary>
        internal static bool DefaultSenderVerifier(ulong senderId)
        {
            if (senderId == 0UL) return false;
            var provider = SelfSessionIdProvider;
            if (provider == null) return false;
            ulong self;
            try { self = provider(); }
            catch (Exception)
            {
                return false;
            }
            return self != 0UL && senderId == self;
        }

        /// <summary>
        /// Opt in to a server-attested sender policy.  Use ONLY when the
        /// gateway is known to bind <c>senderId</c> into the AEAD additional
        /// data (so the SDK can trust the value as server-stamped rather
        /// than peer-supplied).  The supplied <paramref name="verifier"/>
        /// receives every non-zero senderId; return <see langword="true"/>
        /// to admit, <see langword="false"/> to drop.
        ///
       /// <para>This is an explicit opt-out from the default self-only
        /// policy; pass <see langword="null"/> to revert to the default.
        /// The name is intentionally verbose so call sites cannot be
        /// mistaken for an innocuous "accept anything" toggle during
        /// review.</para>
        /// </summary>
        /// <param name="verifier">Delegate that gates non-zero senderIds,
        /// or <see langword="null"/> to revert to <see cref="DefaultSenderVerifier"/>.</param>
        public static void SetServerAttestedSenderVerifier(Func<ulong, bool> verifier)
        {
            if (verifier == null)
            {
                SenderVerifier = DefaultSenderVerifier;
                return;
            }
            SenderVerifier = senderId =>
            {
                if (senderId == 0UL) return false;
                return verifier(senderId);
            };
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

        // One-time advisory used by the lobby / single-player fallback inside
        // RoomAnchoredSenderVerifier to surface the gap that no roster anchor
        // is in effect.  Spammy per-packet warnings are unacceptable on the
        // hot path.
        private static int _permissiveLegacyWarned;

        // Lobby / single-player fallback.  Accepts any non-zero senderId and
        // emits a one-time advisory.  Used ONLY by RoomAnchoredSenderVerifier
        // when the local SDK is outside a room — pre-room flows have no peer
        // roster to anchor against and the gateway is the only legitimate
        // counterparty.  Not exposed as a default policy because in-room
        // peer environments require explicit roster anchoring or self-only
        // rejection (see DefaultSenderVerifier).
        private static bool PermissiveLegacySenderVerifier(ulong senderId)
        {
            if (senderId == 0UL) return false;
            if (System.Threading.Interlocked.CompareExchange(
                    ref _permissiveLegacyWarned, 1, 0) == 0)
            {
                UnityEngine.Debug.LogWarning(
                    "[RTMPE] EnhancedRpcVerifier accepting RPC outside any room " +
                    "without a roster anchor.  This is the lobby / single-player " +
                    "fallback only; in-room peer traffic must use a roster-aware " +
                    "or server-attested verifier.");
            }
            return true;
        }

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
                // anchor against a roster — accept any non-zero sender so
                // SDK consumers do not break in pre-room flows.  Within
                // such flows the gateway is the only legitimate counter-
                // party and there is no peer roster to anchor against.
                return PermissiveLegacySenderVerifier(senderId);
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
            SelfSessionIdProvider  = null;
            System.Threading.Interlocked.Exchange(ref _permissiveLegacyWarned,     0);
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
            if (hook == null) return true;
            // Verifier hooks must fail-closed: a buggy integrator delegate
            // (NRE on a stale roster reference, Dictionary mutation in
            // flight, …) must drop the packet rather than abort the parse
            // boundary.  Unhandled propagation here would tear down the
            // EnhancedRpcPacketParser and silently consume the rest of the
            // inbound buffer.
            try { return hook(senderId); }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError(
                    "[RTMPE] EnhancedRpcVerifier.SenderVerifier threw " +
                    $"{ex.GetType().Name}: {ex.Message}.  Treating as deny — " +
                    "fix the verifier delegate to fail-closed.");
                return false;
            }
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
            if (hook == null) return true;
            // Same fail-closed discipline as IsSenderAcceptable above.
            try { return hook(objectId); }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError(
                    "[RTMPE] EnhancedRpcVerifier.ObjectExistsVerifier threw " +
                    $"{ex.GetType().Name}: {ex.Message}.  Treating as deny.");
                return false;
            }
        }
    }
}
