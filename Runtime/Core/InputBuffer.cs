// RTMPE SDK — Runtime/Core/InputBuffer.cs
//
// Fixed-capacity ring buffer that stores unacknowledged InputPayloads for
// client-side prediction rollback.
//
// Capacity: 64 entries (power of two — enables bitwise AND masking).
// At 30 Hz, 64 entries covers >2 seconds of unacknowledged input — well
// beyond any realistic server round-trip time.  When the buffer is full
// the oldest entry is silently overwritten: dropping the oldest prediction
// is the safest recovery path (a visible pop on severe congestion is
// preferable to unbounded memory growth or a dropped exception).
//
// No UnityEngine dependency — testable from pure .NET xunit projects.

using System;

namespace RTMPE.Core
{
    /// <summary>
    /// Fixed-capacity ring buffer of unacknowledged <see cref="InputPayload"/> entries
    /// for client-side prediction and rollback.
    ///
    /// <para>All operations are O(1) and allocation-free after construction.</para>
    /// </summary>
    public sealed class InputBuffer
    {
        // ── Constants ──────────────────────────────────────────────────────────

        /// <summary>
        /// Maximum number of entries the buffer can hold simultaneously.
        /// Must be a power of two for the bitwise AND mask to work correctly.
        /// </summary>
        public const int Capacity = 64;

        // Bitwise AND mask for O(1) index wrapping without modulo arithmetic.
        private const int Mask = Capacity - 1;

        // ── Storage ────────────────────────────────────────────────────────────

        private readonly InputPayload[] _slots = new InputPayload[Capacity];

        // _head: index of the oldest unacknowledged entry.
        // _count: number of valid unacknowledged entries (0..Capacity inclusive).
        private int _head;
        private int _count;

        // ── Properties ─────────────────────────────────────────────────────────

        /// <summary>Number of unacknowledged entries currently stored.</summary>
        public int Count => _count;

        // ── Operations ─────────────────────────────────────────────────────────

        /// <summary>
        /// Append <paramref name="payload"/> to the back of the buffer.
        ///
        /// <para>When the buffer is full the oldest entry is overwritten and the
        /// head advances by one, preserving the newest <see cref="Capacity"/>
        /// inputs.</para>
        /// </summary>
        public void Push(InputPayload payload)
        {
            int slot = (_head + _count) & Mask;
            _slots[slot] = payload;
            if (_count < Capacity)
            {
                _count++;
            }
            else
            {
                // Buffer full: overwrite oldest entry, advance head.
                _head = (_head + 1) & Mask;
            }
        }

        /// <summary>
        /// Remove all entries whose <see cref="InputPayload.Tick"/> is &lt;=
        /// <paramref name="ackedTick"/>.  No-op when the buffer is empty.
        ///
        /// <para>Complexity: O(k) where k is the number of acknowledged entries.</para>
        /// </summary>
        public void AcknowledgeUpTo(uint ackedTick)
        {
            while (_count > 0 && _slots[_head].Tick <= ackedTick)
            {
                _head  = (_head + 1) & Mask;
                _count--;
            }
        }

        /// <summary>
        /// Copy all unacknowledged entries — oldest first — into
        /// <paramref name="dest"/>.
        ///
        /// <para>The caller must allocate <paramref name="dest"/> with
        /// <c>Length &gt;= <see cref="Count"/></c> (or &gt;= <see cref="Capacity"/>
        /// to be safe against concurrent pushes on the same frame).</para>
        /// </summary>
        /// <returns>Number of entries written into <paramref name="dest"/>.</returns>
        public int CopyUnacknowledgedTo(InputPayload[] dest)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            int written = 0;
            for (int i = 0; i < _count; i++)
                dest[written++] = _slots[(_head + i) & Mask];
            return written;
        }

        /// <summary>Remove all stored entries, resetting head and count to zero.</summary>
        public void Clear()
        {
            _head  = 0;
            _count = 0;
        }
    }
}
