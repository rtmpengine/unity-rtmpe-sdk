// RTMPE SDK — Runtime/Sync/TransformQuantization.cs
//
// Bandwidth-oriented codecs for transform deltas:
//
//   • Half-precision floats (IEEE 754 binary16): 16 bits per component instead
//     of 32.  Cuts position/scale wire size in half.  Precision is approximately
//     0.1% relative across the normalised range — adequate for world-space
//     coordinates inside a typical playable area but not for centimetre-scale
//     work outside [-65504, 65504] absolute.
//
//   • Smallest-three quaternion encoding: 32 bits total instead of 128.
//     A unit quaternion has magnitude 1 by definition, so the largest of its
//     four components can be reconstructed from the other three using
//     w = sqrt(1 - x² - y² - z²).  We pack the index of the dropped component
//     in two bits, then quantise the remaining three to 10 bits each over the
//     range [-1/√2, 1/√2] (the maximum absolute value any non-largest
//     component can take in a unit quaternion).  Sign of the dropped component
//     is fixed positive on reconstruction; this is canonical because q and -q
//     represent the same rotation.
//
// Both codecs are pure functions of (byte[], offset, value) and allocate
// nothing on the hot path.  The half-float NaN/Inf path returns false from the
// encoder so a corrupted simulation does not silently propagate as a sentinel
// pattern through the wire.  The decoder maps half-precision NaN/Inf inputs to
// finite zero so a malicious peer cannot drive transform.position to NaN.

using System;
using UnityEngine;

namespace RTMPE.Sync
{
    /// <summary>
    /// Pure static codecs for half-precision floats and smallest-three
    /// quaternions.  Both formats are off-by-default in the wire protocol —
    /// see <see cref="RTMPE.Core.NetworkSettings.quantizeTransforms"/>.
    /// </summary>
    public static class TransformQuantization
    {
        // ── Layout constants ──────────────────────────────────────────────

        /// <summary>Bytes occupied by a single binary16 half-float.</summary>
        public const int HalfSize = 2;

        /// <summary>Bytes occupied by a smallest-three encoded quaternion.</summary>
        public const int QuaternionSize = 4;

        /// <summary>
        /// Largest finite magnitude representable by an IEEE 754 binary16
        /// half-precision float.  Any input to <see cref="TryWriteHalf"/>
        /// whose absolute value exceeds this threshold is saturated to
        /// <c>±HalfMaxFinite</c> before encoding so the wire never carries
        /// a half-precision <c>±Inf</c> bit pattern (which the decoder
        /// folds to zero for non-finite-input safety).  Game code that
        /// needs world-space coordinates beyond this range must disable
        /// transform quantisation (<c>NetworkSettings.quantizeTransforms = false</c>)
        /// and pay the doubled wire size for full-precision floats.
        /// </summary>
        public const float HalfMaxFinite = 65504f;

        // The smallest-three packer needs to know the upper bound any
        // non-largest unit-quaternion component can take.  When one
        // component holds the maximum magnitude, the remaining three sum-of-
        // squares equal 1 - max² ≤ 1/2, so each is bounded by 1/sqrt(2).
        private static readonly float OneOverSqrt2 = 1f / (float)Math.Sqrt(2.0);

        // 10-bit signed range for each packed quaternion component.  A
        // saturating clamp guards against floating-point overshoot at the
        // edges of [-1/sqrt(2), 1/sqrt(2)] caused by accumulated drift in
        // the source quaternion.
        private const int  QuatComponentBits = 10;
        private const int  QuatComponentMax  = (1 << QuatComponentBits) - 1; // 1023
        private const float QuatScale = QuatComponentMax * 0.5f;             // 511.5

        // ── Half-precision float ──────────────────────────────────────────

        /// <summary>
        /// Encode <paramref name="value"/> as a 16-bit IEEE 754 binary16 at
        /// <c>buf[offset]</c>.  Returns <c>false</c> when the input is NaN or
        /// ±Infinity — the caller must decide whether to fall back to a
        /// full-precision encoding or drop the update.
        /// </summary>
        public static bool TryWriteHalf(byte[] buf, int offset, float value)
        {
            if (buf == null) return false;
            if (offset < 0 || offset > buf.Length - HalfSize) return false;

            // NaN/Inf would round-trip into a half-float NaN/Inf and a
            // subsequent renormalisation step would corrupt downstream math.
            // Reject up-front so the encoder is total over its declared
            // domain (finite floats only).
            if (float.IsNaN(value) || float.IsInfinity(value)) return false;

            // Pre-clamp to the half-precision finite range BEFORE bit-level
            // conversion.  FloatToHalfBits already saturates exponents that
            // are obviously out-of-range, but the round-half-to-even step
            // on the normal-binary16 path can still carry into the exponent
            // for inputs in the narrow window (HalfMaxFinite, HalfMaxFinite +
            // 1 ULP] and emit ±Inf — which ReadHalf folds to zero, silently
            // teleporting a transform component to the origin.  Clamping
            // here makes the encoder's output total over the finite domain:
            // any in-range float lands on its representable half-precision
            // neighbour, and any out-of-range float lands on ±HalfMaxFinite,
            // preserving sign and order of magnitude rather than
            // collapsing to zero.  No allocation, no branch on the hot
            // (in-range) path beyond the magnitude compare.
            if (value > HalfMaxFinite)       value = HalfMaxFinite;
            else if (value < -HalfMaxFinite) value = -HalfMaxFinite;

            ushort half = FloatToHalfBits(value);
            buf[offset]     = (byte)(half & 0xFF);
            buf[offset + 1] = (byte)((half >> 8) & 0xFF);
            return true;
        }

        /// <summary>
        /// Decode a 16-bit binary16 at <c>buf[offset]</c>.  NaN/Inf bit
        /// patterns are mapped to 0f so a malicious or corrupted packet cannot
        /// inject a non-finite value into the renderer / physics engine.
        /// </summary>
        public static float ReadHalf(byte[] buf, int offset)
        {
            if (buf == null) throw new ArgumentNullException(nameof(buf));
            if (offset < 0 || offset > buf.Length - HalfSize)
                throw new ArgumentOutOfRangeException(nameof(offset));

            ushort half = (ushort)(buf[offset] | (buf[offset + 1] << 8));
            float v = HalfBitsToFloat(half);
            // The decoder must never hand a NaN/Inf to a transform; the
            // simplest invariant is "out-of-range half = neutral zero".
            if (float.IsNaN(v) || float.IsInfinity(v)) return 0f;
            return v;
        }

        // ── Smallest-three quaternion ─────────────────────────────────────

        /// <summary>
        /// Encode <paramref name="q"/> as a 32-bit smallest-three packed
        /// integer at <c>buf[offset]</c>.  Returns <c>false</c> when the input
        /// is degenerate (zero magnitude, NaN, or Inf); a non-unit quaternion
        /// is renormalised before packing.
        /// </summary>
        public static bool TryWriteSmallestThree(byte[] buf, int offset, Quaternion q)
        {
            if (buf == null) return false;
            if (offset < 0 || offset > buf.Length - QuaternionSize) return false;

            float x = q.x, y = q.y, z = q.z, w = q.w;
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) || float.IsNaN(w)) return false;
            if (float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z) || float.IsInfinity(w)) return false;

            float magSq = x * x + y * y + z * z + w * w;
            if (magSq <= 1e-12f) return false; // zero quaternion has no rotation
            float invMag = 1f / (float)Math.Sqrt(magSq);
            x *= invMag; y *= invMag; z *= invMag; w *= invMag;

            // Find the component with the largest absolute value; it will be
            // reconstructed from the other three.  Ties resolve in component-
            // index order because the canonical-positive sign rule applies
            // identically to all four cases.
            float ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z), aw = Math.Abs(w);
            int dropped = 0;
            float maxA = ax;
            if (ay > maxA) { dropped = 1; maxA = ay; }
            if (az > maxA) { dropped = 2; maxA = az; }
            if (aw > maxA) { dropped = 3; maxA = aw; }

            // Flip the sign of the entire quaternion so the dropped component
            // is non-negative.  q and -q represent the same rotation, so the
            // decoder can reconstruct the dropped component as +sqrt(1 - …).
            float droppedComponent = dropped == 0 ? x : dropped == 1 ? y : dropped == 2 ? z : w;
            if (droppedComponent < 0f) { x = -x; y = -y; z = -z; w = -w; }

            // Read back the three kept components in component order, skipping
            // the dropped one.
            float c0 = 0f, c1 = 0f, c2 = 0f;
            switch (dropped)
            {
                case 0: c0 = y; c1 = z; c2 = w; break;
                case 1: c0 = x; c1 = z; c2 = w; break;
                case 2: c0 = x; c1 = y; c2 = w; break;
                case 3: c0 = x; c1 = y; c2 = z; break;
            }

            uint packed = (uint)(dropped & 0x3);
            packed |= QuantiseSignedToTenBits(c0) << 2;
            packed |= QuantiseSignedToTenBits(c1) << 12;
            packed |= QuantiseSignedToTenBits(c2) << 22;

            buf[offset]     = (byte)(packed & 0xFF);
            buf[offset + 1] = (byte)((packed >> 8)  & 0xFF);
            buf[offset + 2] = (byte)((packed >> 16) & 0xFF);
            buf[offset + 3] = (byte)((packed >> 24) & 0xFF);
            return true;
        }

        /// <summary>
        /// Decode a 32-bit smallest-three packed quaternion at
        /// <c>buf[offset]</c> and return a unit quaternion.  Always succeeds:
        /// the encoded form cannot represent NaN/Inf, and the reconstructed
        /// magnitude is renormalised to compensate for quantisation drift.
        /// </summary>
        public static Quaternion ReadSmallestThree(byte[] buf, int offset)
        {
            if (buf == null) throw new ArgumentNullException(nameof(buf));
            if (offset < 0 || offset > buf.Length - QuaternionSize)
                throw new ArgumentOutOfRangeException(nameof(offset));

            uint packed =
                  (uint)buf[offset]
                | ((uint)buf[offset + 1] << 8)
                | ((uint)buf[offset + 2] << 16)
                | ((uint)buf[offset + 3] << 24);

            int dropped = (int)(packed & 0x3);
            float c0 = DequantiseSignedFromTenBits((packed >> 2)  & 0x3FF);
            float c1 = DequantiseSignedFromTenBits((packed >> 12) & 0x3FF);
            float c2 = DequantiseSignedFromTenBits((packed >> 22) & 0x3FF);

            // Reconstruct the dropped component.  Quantisation can produce a
            // sum > 1 for the three kept components (each rounded slightly
            // upward); clamp the radicand at 0 so sqrt remains real.
            float sumSq = c0 * c0 + c1 * c1 + c2 * c2;
            float dropped2 = 1f - sumSq;
            if (dropped2 < 0f) dropped2 = 0f;
            float droppedV = (float)Math.Sqrt(dropped2);

            float qx = 0f, qy = 0f, qz = 0f, qw = 0f;
            switch (dropped)
            {
                case 0: qx = droppedV; qy = c0; qz = c1; qw = c2; break;
                case 1: qx = c0; qy = droppedV; qz = c1; qw = c2; break;
                case 2: qx = c0; qy = c1; qz = droppedV; qw = c2; break;
                case 3: qx = c0; qy = c1; qz = c2; qw = droppedV; break;
            }

            // Final renormalise to absorb the residual drift introduced by
            // 10-bit quantisation; the result is unit-magnitude within ~1e-4.
            float mag = (float)Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (mag <= 1e-6f) return new Quaternion(0f, 0f, 0f, 1f);
            float inv = 1f / mag;
            return new Quaternion(qx * inv, qy * inv, qz * inv, qw * inv);
        }

        // ── Internals ─────────────────────────────────────────────────────

        // Maps a signed value in [-1/sqrt(2), 1/sqrt(2)] to a 10-bit unsigned
        // integer in [0, 1023].  Saturating-clamps the input first so a
        // numerically-borderline source quaternion never overflows the field.
        private static uint QuantiseSignedToTenBits(float v)
        {
            // Normalise the input to [-1, 1] then to [0, 1].
            float normalised = v / OneOverSqrt2;
            if (normalised < -1f) normalised = -1f;
            else if (normalised > 1f) normalised = 1f;
            float zeroToOne = (normalised + 1f) * 0.5f;
            int q = (int)Math.Round(zeroToOne * QuatComponentMax);
            if (q < 0) q = 0;
            else if (q > QuatComponentMax) q = QuatComponentMax;
            return (uint)q;
        }

        private static float DequantiseSignedFromTenBits(uint q)
        {
            float zeroToOne = q / (float)QuatComponentMax;
            float minusOneToOne = zeroToOne * 2f - 1f;
            return minusOneToOne * OneOverSqrt2;
        }

        // Bit-level binary32 → binary16 conversion following IEEE 754-2008
        // §3.6 round-half-to-even.  Subnormals on the binary16 side are
        // produced for inputs whose exponent falls below the binary16 minimum;
        // overflows saturate to ±Inf.
        private static ushort FloatToHalfBits(float value)
        {
            uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(value));
            uint sign = (bits >> 16) & 0x8000u;
            int exp32 = (int)((bits >> 23) & 0xFFu);
            uint mant = bits & 0x7FFFFFu;

            if (exp32 == 0xFF)
            {
                // NaN or Inf — caller has already screened these out, but
                // keep the bit-level mapping for completeness.
                if (mant != 0u)
                    return (ushort)(sign | 0x7E00u); // canonical qNaN
                return (ushort)(sign | 0x7C00u);     // ±Inf
            }

            int exp16 = exp32 - 127 + 15;
            if (exp16 >= 0x1F)
            {
                // Saturate to half-precision max-finite (±65504).  Returning
                // ±Inf would silently teleport the receiver to the origin —
                // ReadHalf folds Inf to 0 to keep non-finite values out of the
                // transform.  Saturation preserves the sign and the order of
                // magnitude so a position component beyond the half-float cap
                // clamps to the cap instead of collapsing to zero.
                return (ushort)(sign | 0x7BFFu);
            }
            if (exp16 <= 0)
            {
                // Subnormal binary16 or underflow.
                if (exp16 < -10) return (ushort)sign; // signed zero
                mant |= 0x800000u; // restore implicit bit
                int shift = 14 - exp16;
                uint half = mant >> shift;
                // Round-half-to-even on the discarded bits.
                uint roundBits = mant & ((1u << shift) - 1u);
                uint halfwayBit = 1u << (shift - 1);
                if (roundBits > halfwayBit
                    || (roundBits == halfwayBit && (half & 1u) != 0u))
                    half += 1u;
                return (ushort)(sign | half);
            }

            // Normal binary16.
            uint encoded = (uint)(exp16 << 10) | (mant >> 13);
            uint round   = mant & 0x1FFFu;
            uint halfway = 0x1000u;
            if (round > halfway || (round == halfway && (encoded & 1u) != 0u))
            {
                encoded += 1u;
                // The round may carry into the exponent; if exp16 saturates we
                // produce ±Inf, which is already the correct overflow value.
            }
            return (ushort)(sign | encoded);
        }

        private static float HalfBitsToFloat(ushort half)
        {
            uint sign = (uint)(half & 0x8000) << 16;
            uint exp16 = (uint)((half >> 10) & 0x1F);
            uint mant16 = (uint)(half & 0x3FF);

            uint bits;
            if (exp16 == 0)
            {
                if (mant16 == 0)
                {
                    // Signed zero.
                    bits = sign;
                }
                else
                {
                    // Subnormal binary16 → normalise into binary32.
                    int e = -1;
                    do { e++; mant16 <<= 1; } while ((mant16 & 0x400u) == 0u);
                    mant16 &= 0x3FFu;
                    uint exp32 = (uint)(127 - 15 - e);
                    bits = sign | (exp32 << 23) | (mant16 << 13);
                }
            }
            else if (exp16 == 0x1F)
            {
                // ±Inf or NaN — preserved bit pattern; the public ReadHalf
                // entry point folds these to 0 so untrusted input cannot
                // poison transform fields.
                bits = sign | 0x7F800000u | (mant16 << 13);
            }
            else
            {
                uint exp32 = exp16 + (127u - 15u);
                bits = sign | (exp32 << 23) | (mant16 << 13);
            }

            return BitConverter.Int32BitsToSingle(unchecked((int)bits));
        }
    }
}
