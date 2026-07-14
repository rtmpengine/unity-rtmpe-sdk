// RTMPE SDK — Tests/Runtime/TransformQuantizationTests.cs
//
// NUnit Edit-Mode tests for TransformQuantization (Runtime/Sync/TransformQuantization.cs).
//
// Coverage focus: the V3-audit fix #3 saturating clamp on TryWriteHalf.
// Pre-fix, an input float in the narrow window (HalfMaxFinite, HalfMaxFinite +
// 1 ULP] could carry into the binary16 exponent during the round-half-to-even
// step in FloatToHalfBits and emit ±Inf — which ReadHalf folds to zero, so a
// transform component would silently teleport to the origin.
//
// The fix saturates the input to ±HalfMaxFinite BEFORE the bit-level
// conversion so the encoder is total over the finite domain: every in-range
// float lands on its representable binary16 neighbour, every out-of-range
// float lands on ±HalfMaxFinite, and ±Inf is no longer reachable from a
// non-±Inf input.

using NUnit.Framework;
using RTMPE.Sync;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("TransformQuantization")]
    public class TransformQuantizationTests
    {
        // ── HalfMaxFinite contract ────────────────────────────────────────────

        [Test]
        [Description("HalfMaxFinite matches the IEEE 754 binary16 max-finite magnitude (65504).")]
        public void HalfMaxFinite_MatchesIeeeSpec()
        {
            // Per IEEE 754-2008 §3.6, the largest finite binary16 value is
            // (2 - 2^-10) * 2^15 = 65504.
            Assert.AreEqual(65504f, TransformQuantization.HalfMaxFinite);
        }

        // ── In-range values: identity round-trip (within precision) ───────────

        [Test]
        [Description("Round-trip of zero is exact.")]
        public void Half_Zero_RoundTripsExactly()
        {
            var buf = new byte[2];
            Assert.IsTrue(TransformQuantization.TryWriteHalf(buf, 0, 0f));
            Assert.AreEqual(0f, TransformQuantization.ReadHalf(buf, 0));
        }

        [Test]
        [Description("Round-trip of small finite values is within binary16 precision.")]
        public void Half_SmallValues_RoundTripsApproximately()
        {
            var buf = new byte[2];
            // 1.0, 100.0, 1234.5 all sit comfortably inside binary16's
            // representable range; round-trip error is bounded by 1 ULP.
            float[] cases = { 1f, 100f, 1234.5f, -42.25f };
            foreach (var v in cases)
            {
                Assert.IsTrue(TransformQuantization.TryWriteHalf(buf, 0, v));
                float decoded = TransformQuantization.ReadHalf(buf, 0);
                // Relative tolerance ~0.1% — half-precision is 10-bit mantissa
                // so the worst-case relative error is 2^-10 ≈ 0.001.
                float tol = System.Math.Max(0.002f, System.Math.Abs(v) * 0.002f);
                Assert.AreEqual(v, decoded, tol,
                    $"Round-trip of {v} should land within {tol}.");
            }
        }

        [Test]
        [Description("Round-trip of HalfMaxFinite itself is exact.")]
        public void Half_AtMaxFinite_RoundTripsExactly()
        {
            var buf = new byte[2];
            Assert.IsTrue(TransformQuantization.TryWriteHalf(
                buf, 0, TransformQuantization.HalfMaxFinite));
            Assert.AreEqual(TransformQuantization.HalfMaxFinite,
                TransformQuantization.ReadHalf(buf, 0));
        }

        [Test]
        [Description("Round-trip of -HalfMaxFinite is exact.")]
        public void Half_AtNegMaxFinite_RoundTripsExactly()
        {
            var buf = new byte[2];
            Assert.IsTrue(TransformQuantization.TryWriteHalf(
                buf, 0, -TransformQuantization.HalfMaxFinite));
            Assert.AreEqual(-TransformQuantization.HalfMaxFinite,
                TransformQuantization.ReadHalf(buf, 0));
        }

        // ── Saturating clamp (the fix) ────────────────────────────────────────

        [Test]
        [Description(
            "An input slightly above HalfMaxFinite is saturated to +HalfMaxFinite, " +
            "NOT silently teleported to zero via an Inf round-trip.")]
        public void Half_AboveMaxFinite_ClampsToMax_NotInf_NotZero()
        {
            var buf = new byte[2];
            // 65505 falls inside (HalfMaxFinite, next-representable] and
            // pre-fix could round up to half-precision +Inf.
            Assert.IsTrue(TransformQuantization.TryWriteHalf(buf, 0, 65505f));
            float decoded = TransformQuantization.ReadHalf(buf, 0);

            Assert.AreEqual(TransformQuantization.HalfMaxFinite, decoded,
                "Just above HalfMaxFinite must saturate to HalfMaxFinite, " +
                "not collapse to zero via Inf round-trip.");
            Assert.AreNotEqual(0f, decoded,
                "Pre-fix regression sentinel: a value just above the half-float " +
                "max must not decode to zero.");
        }

        [Test]
        [Description(
            "An input far above HalfMaxFinite is saturated, sign preserved.")]
        public void Half_FarAboveMaxFinite_ClampsToMax_SignPreserved()
        {
            var buf = new byte[2];
            // World-space coordinate beyond half-float range (e.g., a very
            // large open-world map).  Pre-fix: would teleport to origin.
            // Post-fix: saturates to +HalfMaxFinite, preserving direction.
            Assert.IsTrue(TransformQuantization.TryWriteHalf(buf, 0, 100000f));
            float decoded = TransformQuantization.ReadHalf(buf, 0);

            Assert.AreEqual(TransformQuantization.HalfMaxFinite, decoded);
        }

        [Test]
        [Description(
            "An input far below -HalfMaxFinite is saturated to -HalfMaxFinite.")]
        public void Half_FarBelowNegMaxFinite_ClampsToNegMax_SignPreserved()
        {
            var buf = new byte[2];
            Assert.IsTrue(TransformQuantization.TryWriteHalf(buf, 0, -100000f));
            float decoded = TransformQuantization.ReadHalf(buf, 0);

            Assert.AreEqual(-TransformQuantization.HalfMaxFinite, decoded);
        }

        // ── Non-finite rejection (pre-existing contract) ──────────────────────

        [Test]
        [Description("NaN is rejected (TryWriteHalf returns false). Pre-existing contract.")]
        public void Half_Nan_IsRejected()
        {
            var buf = new byte[2];
            Assert.IsFalse(TransformQuantization.TryWriteHalf(buf, 0, float.NaN));
        }

        [Test]
        [Description("+Inf is rejected. Pre-existing contract.")]
        public void Half_PosInf_IsRejected()
        {
            var buf = new byte[2];
            Assert.IsFalse(TransformQuantization.TryWriteHalf(
                buf, 0, float.PositiveInfinity));
        }

        [Test]
        [Description("-Inf is rejected. Pre-existing contract.")]
        public void Half_NegInf_IsRejected()
        {
            var buf = new byte[2];
            Assert.IsFalse(TransformQuantization.TryWriteHalf(
                buf, 0, float.NegativeInfinity));
        }

        // ── Bounds-checking on buf / offset ───────────────────────────────────

        [Test]
        [Description("Null buffer is rejected.")]
        public void Half_NullBuffer_Rejected()
        {
            Assert.IsFalse(TransformQuantization.TryWriteHalf(null, 0, 1f));
        }

        [Test]
        [Description("Negative offset is rejected.")]
        public void Half_NegativeOffset_Rejected()
        {
            var buf = new byte[2];
            Assert.IsFalse(TransformQuantization.TryWriteHalf(buf, -1, 1f));
        }

        [Test]
        [Description("Offset past end-of-buffer is rejected.")]
        public void Half_OffsetPastEnd_Rejected()
        {
            var buf = new byte[2];
            // HalfSize = 2 bytes; offset 1 leaves only 1 byte → reject.
            Assert.IsFalse(TransformQuantization.TryWriteHalf(buf, 1, 1f));
        }

        // ── Encoder is total over the finite domain (post-fix) ────────────────

        [Test]
        [Description(
            "The encoder never produces a half-float that decodes to NaN or " +
            "Inf for any finite (non-NaN, non-Inf) input — a property that " +
            "fix #3 made total over the finite domain.")]
        public void Half_EncoderIsTotalOverFiniteDomain()
        {
            var buf = new byte[2];
            // Sweep across the binary16 saturation region with deliberate
            // round-half-to-even hazard inputs.
            float[] hazards =
            {
                65504f,                  // exact max-finite
                65505f,                  // first value above max — pre-fix Inf risk
                65510f, 65515f, 65519f,  // narrow window where rounding could overflow
                65520f, 65535f,          // boundary at 2^16
                70000f, 100000f,         // far overflow
                float.MaxValue,          // largest finite float
                -65504f, -65505f, -65520f, -100000f, float.MinValue
            };

            foreach (var v in hazards)
            {
                bool ok = TransformQuantization.TryWriteHalf(buf, 0, v);
                Assert.IsTrue(ok, $"TryWriteHalf must accept finite {v}.");

                float decoded = TransformQuantization.ReadHalf(buf, 0);
                // ReadHalf folds NaN/Inf to zero defensively; the property
                // we want is that the decoded value is *finite and non-zero*
                // for non-zero finite inputs.  Saturated cases return
                // ±HalfMaxFinite (non-zero), in-range cases return their
                // representable neighbour (non-zero), so the floor is the
                // sign-preserving HalfMaxFinite for any finite input whose
                // sign matches v.
                Assert.IsFalse(float.IsNaN(decoded),
                    $"Decoded value for input {v} must not be NaN.");
                Assert.IsFalse(float.IsInfinity(decoded),
                    $"Decoded value for input {v} must not be Inf — fix #3 " +
                    "regression sentinel.");
                if (v != 0f)
                {
                    Assert.AreNotEqual(0f, decoded,
                        $"Decoded value for non-zero input {v} must not be " +
                        "zero (pre-fix Inf→0 teleport regression sentinel).");
                    Assert.AreEqual(System.Math.Sign(v), System.Math.Sign(decoded),
                        $"Decoded value for input {v} must preserve sign.");
                }
            }
        }
    }
}
