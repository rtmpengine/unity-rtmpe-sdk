// RTMPE SDK — Runtime/Core/ObjectIdMath.cs
//
// Pure-arithmetic helpers used by SpawnManager to compose locally-allocated
// 64-bit network object ids.  Kept in its own file (no UnityEngine usage)
// so that xunit-based unit tests can compile-link this exact source without
// pulling in the rest of the SpawnManager surface.  The bit-mixing function
// is the single source of truth for the (session_id, counter) → object_id
// contract — divergence between this file and SpawnManager.GenerateObjectId
// would silently re-introduce the 32-bit truncation collision class the
// helper was created to close.

namespace RTMPE.Core
{
    /// <summary>
    /// Pure-static composition rules for 64-bit network object identifiers.
    /// </summary>
    public static class ObjectIdMath
    {
        /// <summary>
        /// Compose a 64-bit object id from a 64-bit gateway session id and a
        /// per-session monotonic counter.  High 32 bits = avalanche-mixed
        /// digest of the FULL session id; low 32 bits = counter (truncated to
        /// u32 — caller's responsibility to reset before wrap).
        /// </summary>
        /// <remarks>
        /// The high half mixes EVERY input byte of <paramref name="sessionId"/>
        /// so that two distinct sessions whose low halves coincide map to
        /// different digests with 1-in-2^32 probability.  Reconnects that
        /// reuse the gateway-allocated low half therefore cannot replay
        /// object-id space against the prior session.
        /// </remarks>
        public static ulong Compose(ulong sessionId, ulong counter)
        {
            ulong digest = MixSessionId(sessionId);
            return (digest << 32) | (counter & 0xFFFFFFFFUL);
        }

        /// <summary>
        /// Avalanche-mix a 64-bit session id down to a 32-bit digest using a
        /// fold-then-SplitMix64 finalizer.  Pure, allocation-free, deterministic.
        /// </summary>
        public static uint MixSessionId(ulong sessionId)
        {
            ulong z = sessionId;
            z ^= z >> 32;                       // fold high half into low half
            z ^= z >> 30; z *= 0xBF58476D1CE4E5B9UL;
            z ^= z >> 27; z *= 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (uint)(z ^ (z >> 32));
        }
    }
}
