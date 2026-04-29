// RTMPE SDK — Runtime/Rpc/LegacyRpcVerifier.cs
//
// Pre-dispatch authorisation gate for the legacy MethodId-keyed RPC path
// (Rpc 0x50 without FLAG_ENHANCED_RPC).  AEAD authenticates the gateway as
// the relay, NOT the originating peer — exactly as for Enhanced RPC — so
// every wire-derived senderId / methodId pair must be passed through the
// same verification policy the enhanced parser enforces before the receiver
// invokes a handler.  Without this check a hostile peer can stamp a Ping
// or ApplyDamage with senderId=0 (the SDK's "uninitialised session"
// sentinel) or with the session id of another roster member and the
// receiver dispatches as if the gateway had attested origin.
//
// Trust model summary (mirrors EnhancedRpcVerifier.cs):
//   senderId   — wire-supplied; structurally rejected when zero, otherwise
//                deferred to EnhancedRpcVerifier.IsSenderAcceptable so a
//                roster-anchored verifier already wired by the integrator
//                covers both code paths.
//   methodId   — wire-supplied; per-method overrides may apply additional
//                checks (e.g. TransferOwnership goes through the existing
//                IsOwnershipTransferAuthorized predicate at the dispatch
//                site after this gate accepts).

namespace RTMPE.Rpc
{
    /// <summary>
    /// Authorisation predicate applied to every inbound legacy
    /// MethodId-keyed RPC before the receiver dispatches the matching
    /// handler.  Defers to <see cref="EnhancedRpcVerifier.IsSenderAcceptable"/>
    /// so a single integrator-installed roster source covers both the
    /// Enhanced and the legacy code paths uniformly.
    /// </summary>
    public static class LegacyRpcVerifier
    {
        /// <summary>
        /// Returns <see langword="true"/> when the (senderId, methodId) pair
        /// is admissible under the configured sender policy.  The
        /// <paramref name="methodId"/> is reserved for future per-method
        /// overrides; today every legacy id shares the same uniform sender
        /// gate so the parameter is accepted for forward compatibility and
        /// to make call sites self-documenting.
        /// </summary>
        public static bool IsLegacyRpcAuthorized(ulong senderId, uint methodId)
        {
            // Zero is the SDK's pre-authentication sentinel and never a
            // legitimate origin for a legacy RPC.  EnhancedRpcVerifier
            // already enforces this guard but repeating it locally keeps
            // the contract explicit at the call site.
            if (senderId == 0UL) return false;
            return EnhancedRpcVerifier.IsSenderAcceptable(senderId);
        }
    }
}
