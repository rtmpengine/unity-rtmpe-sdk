// RTMPE SDK — Runtime/Crypto/ReplayWindow.cs
//
// Sliding-bitmap anti-replay window for AEAD-authenticated inbound packets.
//
// Construction mirrors RFC 4303 §3.4.3 (IPsec ESP) and the BoringSSL DTLS
// implementation: a fixed-width bitmap tracks which sequence numbers within
// the window [highest-WindowSize+1 .. highest] have already been observed.
// Sequence numbers below the window or repeats within it are rejected.
//
// Invariants:
//  • Capacity is fixed at 1024 entries — covers >30 s of jitter at 30 Hz
//    while keeping the bitmap to a single 16×u64 array.
//  • The window is keyed externally on (session_id, channel); a fresh
//    session must construct a fresh window so a counter reused across
//    sessions does not falsely register as a replay.
//  • Operations are NOT thread-safe.  The SDK applies the window from the
//    main-thread packet dispatcher (ProcessPacket) so concurrent admission
//    is impossible by construction; introducing a lock would only mask a
//    threading-model violation elsewhere.

namespace RTMPE.Crypto.Internal
{
    /// <summary>
    /// Fixed-size sliding-bitmap anti-replay window for monotonic 32-bit
    /// AEAD nonce counters.  Admit returns <see langword="false"/> when the
    /// counter is a duplicate or falls outside the trailing window.
    /// </summary>
    /// <remarks>
    /// THREAD SAFETY: This type is NOT thread-safe.  All callers must invoke
    /// Admit/Reset from the same thread (the SDK's main-thread packet
    /// dispatcher).  Calling Admit concurrently would produce torn reads of
    /// the bitmap and corrupt the highest-seen counter, opening a replay
    /// window large enough to admit duplicates.  Adding a lock here would
    /// hide a threading-model violation in the caller; the documented
    /// invariant is enforced by the dispatcher's main-thread contract, not
    /// by this type.  Callers that introduce a non-main-thread admission
    /// path (e.g. a worker pool) MUST add their own external synchronisation.
    /// </remarks>
    internal sealed class ReplayWindow
    {
        /// <summary>Number of entries the window covers — see RFC 4303 §3.4.3 for sizing rationale.</summary>
        internal const int WindowSize = 1024;

        // 1024 bits = 16 × ulong.  Index 0 corresponds to the lowest sequence
        // currently inside the window.  Bit positions advance with the
        // window head.
        private readonly ulong[] _bitmap = new ulong[WindowSize / 64];

        // _highest is the largest sequence ever admitted; _hasAny tracks
        // whether the window has seen any traffic so we don't conflate the
        // initial counter (0) with "no traffic yet".
        private uint _highest;
        private bool _hasAny;

        /// <summary>
        /// Admit <paramref name="counter"/>.  Returns <see langword="true"/>
        /// when the counter is fresh (not previously seen and within the
        /// trailing window of the highest accepted counter).
        /// </summary>
        public bool Admit(uint counter)
        {
            if (!_hasAny)
            {
                _hasAny  = true;
                _highest = counter;
                SetBit(0);
                return true;
            }

            if (counter > _highest)
            {
                ulong shift = (ulong)counter - _highest;
                ShiftWindowForward(shift);
                _highest = counter;
                SetBit(0);
                return true;
            }

            ulong distance = (ulong)_highest - counter;
            if (distance >= WindowSize) return false;          // below window
            int bitIndex = (int)distance;
            if (TestBit(bitIndex)) return false;               // duplicate
            SetBit(bitIndex);
            return true;
        }

        /// <summary>
        /// Reset the window so a fresh session starts from a clean slate.
        /// Called whenever <c>ClearSessionData</c> tears down the SDK's
        /// session state — a stale highest-counter would falsely reject
        /// the first inbound packet of the next session.
        /// </summary>
        public void Reset()
        {
            for (int i = 0; i < _bitmap.Length; i++) _bitmap[i] = 0UL;
            _highest = 0;
            _hasAny  = false;
        }

        // Distance is the offset from the new highest to this position
        // (0 = current highest).  The bitmap is therefore indexed
        // backwards from "newest" to "oldest" — bit 0 is _highest, bit
        // WindowSize-1 is _highest-WindowSize+1.
        private bool TestBit(int distance)
        {
            int word   = distance >> 6;     // /64
            int offset = distance & 63;
            return ((_bitmap[word] >> offset) & 1UL) != 0UL;
        }

        private void SetBit(int distance)
        {
            int word   = distance >> 6;
            int offset = distance & 63;
            _bitmap[word] |= 1UL << offset;
        }

        // Shift "older" entries up by `shift` positions when the window
        // head advances.  Anything that falls off the high end (older than
        // window-size from the new highest) is forgotten — those counters
        // are then rejected on principle by the distance check in Admit.
        private void ShiftWindowForward(ulong shift)
        {
            if (shift >= WindowSize)
            {
                for (int i = 0; i < _bitmap.Length; i++) _bitmap[i] = 0UL;
                return;
            }

            int wordShift = (int)(shift >> 6);
            int bitShift  = (int)(shift & 63);

            // Shift the bitmap from the most-significant word down so we do
            // not overwrite words we still need to read.
            if (wordShift > 0)
            {
                for (int i = _bitmap.Length - 1; i >= 0; i--)
                {
                    int src = i - wordShift;
                    _bitmap[i] = src >= 0 ? _bitmap[src] : 0UL;
                }
            }

            if (bitShift > 0)
            {
                ulong carry = 0UL;
                for (int i = 0; i < _bitmap.Length; i++)
                {
                    ulong word = _bitmap[i];
                    _bitmap[i] = (word << bitShift) | carry;
                    carry      = word >> (64 - bitShift);
                }
            }
        }
    }
}
