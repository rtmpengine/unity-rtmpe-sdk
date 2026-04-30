// RTMPE SDK — Runtime/Core/InputPayload.cs
//
// One frame of player input, tick-stamped and wire-serialisable.
//
// Wire format (13 bytes, all little-endian):
//  [0..3]   tick   : u32  — monotone client tick counter
//  [4..7]   move_x : f32  — horizontal movement input, clamped to [-1, 1]
//  [8..11]  move_y : f32  — vertical   movement input, clamped to [-1, 1]
//  [12]     flags  : u8   — bit 0 = Jump, bits 1-7 reserved
//
// No UnityEngine dependency so this file compiles in both Unity and plain
// .NET xunit projects without stubs.

using System;

namespace RTMPE.Core
{
    /// <summary>
    /// A single frame of player input — tick-stamped and serialisable to wire bytes.
    /// </summary>
    public struct InputPayload
    {
        // ── Fields ─────────────────────────────────────────────────────────────

        /// <summary>Monotone client tick at which this input was captured.</summary>
        public uint  Tick;

        /// <summary>Horizontal movement input, clamped to [-1, 1].</summary>
        public float MoveX;

        /// <summary>Vertical movement input (forward/back), clamped to [-1, 1].</summary>
        public float MoveY;

        /// <summary>True when the jump button is pressed this frame.</summary>
        public bool  Jump;

        // ── Wire layout ────────────────────────────────────────────────────────

        /// <summary>Wire size in bytes of a serialised <see cref="InputPayload"/>.</summary>
        public const int WireSize = 13;

        private const byte FlagJump = 0x01;

        // ── Serialisation ──────────────────────────────────────────────────────

        /// <summary>
        /// Write this payload into <paramref name="buf"/> at <paramref name="offset"/>.
        /// Caller must ensure <c>buf.Length - offset &gt;= <see cref="WireSize"/></c>.
        /// Throws <see cref="InvalidOperationException"/> when
        /// <see cref="MoveX"/> or <see cref="MoveY"/> is NaN or ±Infinity —
        /// the receive-side parser already rejects such values, so the
        /// sender contract must reject too in order to keep the local
        /// CSP buffer (which never round-trips through <c>ReadFrom</c>)
        /// from carrying non-finite inputs into <c>ApplyInput</c> on
        /// reconciliation replay.
        /// </summary>
        public void WriteTo(byte[] buf, int offset)
        {
            if (buf == null) throw new ArgumentNullException(nameof(buf));
            // Sender-side finiteness gate.  A custom controller that
            // produces NaN MoveX (e.g. division by zero in a deadzone
            // computation) would otherwise be enqueued into the local
            // InputBuffer un-validated.  On the next reconciliation,
            // ReplayUnackedInputs hands the payload to user
            // ApplyInput which propagates NaN into transform.position;
            // the same payload is also sent to the gateway, where the
            // peer parser throws and tears the channel down.  Surfacing
            // the misuse at the sender boundary keeps the CSP simulation
            // domain finite and prevents a single controller bug from
            // promoting into a session-killing protocol error.
            if (float.IsNaN(MoveX) || float.IsInfinity(MoveX))
                throw new InvalidOperationException("InputPayload.MoveX is not finite");
            if (float.IsNaN(MoveY) || float.IsInfinity(MoveY))
                throw new InvalidOperationException("InputPayload.MoveY is not finite");
            WriteU32LE(buf, offset,      Tick);
            WriteF32LE(buf, offset + 4,  MoveX);
            WriteF32LE(buf, offset + 8,  MoveY);
            buf[offset + 12] = Jump ? FlagJump : (byte)0;
        }

        /// <summary>
        /// Read a payload from <paramref name="buf"/> starting at <paramref name="offset"/>.
        /// Caller must ensure <c>buf.Length - offset &gt;= <see cref="WireSize"/></c>.
        /// </summary>
        public static InputPayload ReadFrom(byte[] buf, int offset)
        {
            if (buf == null) throw new ArgumentNullException(nameof(buf));
            float moveX = ReadF32LE(buf, offset + 4);
            float moveY = ReadF32LE(buf, offset + 8);
            // Reject NaN / ±Inf at the parser boundary. These values would
            // otherwise propagate into Unity transforms / physics and pin
            // the simulation in an unrecoverable state.
            if (float.IsNaN(moveX) || float.IsInfinity(moveX))
            {
                throw new InvalidOperationException("InputPayload.MoveX is not finite");
            }
            if (float.IsNaN(moveY) || float.IsInfinity(moveY))
            {
                throw new InvalidOperationException("InputPayload.MoveY is not finite");
            }
            return new InputPayload
            {
                Tick  = ReadU32LE(buf, offset),
                MoveX = moveX,
                MoveY = moveY,
                Jump  = (buf[offset + 12] & FlagJump) != 0,
            };
        }

        // ── Private wire helpers ───────────────────────────────────────────────

        private static void WriteU32LE(byte[] b, int o, uint v)
        {
            b[o]     = (byte) v;
            b[o + 1] = (byte)(v >>  8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        private static uint ReadU32LE(byte[] b, int o)
            => b[o]
            | ((uint)b[o + 1] <<  8)
            | ((uint)b[o + 2] << 16)
            | ((uint)b[o + 3] << 24);

        private static void WriteF32LE(byte[] b, int o, float v)
        {
            int bits = BitConverter.SingleToInt32Bits(v);
            b[o]     = (byte) bits;
            b[o + 1] = (byte)(bits >>  8);
            b[o + 2] = (byte)(bits >> 16);
            b[o + 3] = (byte)(bits >> 24);
        }

        private static float ReadF32LE(byte[] b, int o)
        {
            int bits = b[o]
                     | (b[o + 1] <<  8)
                     | (b[o + 2] << 16)
                     | (b[o + 3] << 24);
            return BitConverter.Int32BitsToSingle(bits);
        }
    }
}
