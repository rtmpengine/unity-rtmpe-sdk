// Regression coverage for the Medium-severity audit fixes:
//
//   - SessionKeyStore.ResetAllForSession bundles every per-session reset in
//     a single contiguous step so the "all-valid or all-reset" invariant
//     can be enforced from one reviewable site.
//   - RpcReplayBuffer drops are observable via DroppedCount and the cap
//     enums are stable parts of the public contract used by the warning
//     emitters.

using NUnit.Framework;
using RTMPE.Core.Aead;
using RTMPE.Core.Rpc;
using RTMPE.Crypto;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("MediumRemediation/SessionKeyStoreReset")]
    public class SessionKeyStore_ResetAllForSession
    {
        // Verifies that ResetAllForSession returns every per-session field
        // to its no-session sentinel value in a single call.  The ordering
        // is asserted in spirit through the post-condition: every read
        // accessor must reflect the reset state simultaneously.
        [Test]
        public void ResetsEveryPerSessionField()
        {
            var store = new SessionKeyStore();

            // Drive every field away from its no-session default so the
            // reset has something to undo.  The replay window is exercised
            // through Ensure → Admit so the bitmap moves off zero.
            store.InstallCryptoId(0xDEADBEEFu);
            Assert.AreEqual(0xDEADBEEFu, store.CryptoId,
                "test setup: InstallCryptoId did not stick");

            store.IncrementOutboundNonceCounter();
            store.IncrementOutboundNonceCounter();

            store.AdvanceLastInboundAppSequenceMonotonic(42L);
            Assert.AreEqual(42L, store.ReadLastInboundAppSequence(),
                "test setup: app-sequence advance did not stick");

            store.EnsureReplayWindow();
            Assert.IsNotNull(store.ReplayWindow,
                "test setup: replay window not allocated");
            store.ReplayWindow.Admit(7u);

            // Install a real session key so DisposeKeys has something to
            // zero — the actual key bytes do not matter, the matter under
            // test is the reset, not key derivation.
            var keys = new SessionKeys(new byte[32], new byte[32]);
            store.InstallSessionKeys(keys);
            Assert.IsTrue(store.IsReady,
                "test setup: SessionKeys did not install");

            // Single bundled reset.
            store.ResetAllForSession();

            Assert.IsFalse(store.IsReady,
                "ResetAllForSession must dispose session keys");
            Assert.IsNull(store.SessionKeys,
                "SessionKeys reference must be null after reset");
            Assert.AreEqual(0u, store.CryptoId,
                "CryptoId must reset to zero");
            // Outbound nonce counter — first IncrementOutboundNonceCounter
            // after a reset returns 0, which means the backing counter
            // observably reset to -1.
            Assert.AreEqual(0L, store.IncrementOutboundNonceCounter(),
                "Outbound nonce counter must reset so the next claim is 0");
            Assert.AreEqual(-1L, store.ReadLastInboundAppSequence(),
                "Last inbound app sequence must reset to -1");
            // Replay window buffer is retained but its bitmap must be
            // reset — re-admitting the previously seen counter must
            // succeed because the window has forgotten it.
            Assert.IsNotNull(store.ReplayWindow,
                "Replay window allocation should be retained across reset");
            Assert.IsTrue(store.ReplayWindow.Admit(7u),
                "Replay window state must reset so previously seen counters re-admit");
        }

        [Test]
        public void IsIdempotent()
        {
            var store = new SessionKeyStore();
            store.IncrementOutboundNonceCounter();
            store.AdvanceLastInboundAppSequenceMonotonic(99L);
            store.InstallCryptoId(123u);

            store.ResetAllForSession();
            store.ResetAllForSession();

            Assert.IsFalse(store.IsReady);
            Assert.AreEqual(0u, store.CryptoId);
            Assert.AreEqual(-1L, store.ReadLastInboundAppSequence());
        }
    }

    [TestFixture]
    [Category("MediumRemediation/RpcReplayBufferObservability")]
    public class RpcReplayBuffer_DroppedCountObservability
    {
        // Verifies DroppedCount is incremented for each cap rejection so
        // an application monitor can alert on a sustained drop rate.  The
        // production code path also emits a rate-limited Debug.LogWarning
        // for every drop (via NetworkManager.ShouldWarn) — that warning is
        // exercised by the integration suite; this fixture targets the
        // counter alone.
        [Test]
        public void DropsCountTowardsTotal_PerCap()
        {
            var buf = new RpcReplayBuffer();

            // (1) Per-payload cap.
            var oversize = new byte[RpcReplayBuffer.MaxPayloadBytes + 1];
            Assert.AreEqual(
                RpcReplayBuffer.EnqueueResult.DroppedPayloadTooLarge,
                buf.TryEnqueue(oversize));
            Assert.AreEqual(1L, buf.DroppedCount);

            // (2) Slot cap — fill to the limit, then one more rejects.
            for (int i = 0; i < RpcReplayBuffer.MaxPendingDuringReplay; i++)
            {
                Assert.AreEqual(
                    RpcReplayBuffer.EnqueueResult.Ok,
                    buf.TryEnqueue(new byte[1]));
            }
            Assert.AreEqual(
                RpcReplayBuffer.EnqueueResult.DroppedSlotCapReached,
                buf.TryEnqueue(new byte[1]));
            Assert.AreEqual(2L, buf.DroppedCount,
                "DroppedCount must increment per rejected enqueue regardless of cap");
        }

        [Test]
        public void DroppedCount_IsAtomicallyReadable()
        {
            var buf = new RpcReplayBuffer();
            // No drops yet — counter must read clean.
            Assert.AreEqual(0L, buf.DroppedCount);

            // One synthetic drop for completeness.
            buf.TryEnqueue(new byte[RpcReplayBuffer.MaxPayloadBytes + 1]);
            Assert.AreEqual(1L, buf.DroppedCount);
        }

        // Covers the cumulative-bytes cap, which the slot-cap and per-payload
        // tests above never reach (each payload is well under the per-payload
        // cap and the slot count cap rejects before cumulative does).
        [Test]
        public void CumulativeBytesCapIsEnforced()
        {
            var buf = new RpcReplayBuffer();

            // Fill with max-sized payloads until we are within one byte of the
            // cumulative cap.  Payloads are 64 KiB each → 64 fills exactly
            // 4 MiB; the next 1-byte enqueue is the one that overflows.
            int maxPayloads = RpcReplayBuffer.MaxCumulativeBytes / RpcReplayBuffer.MaxPayloadBytes;
            for (int i = 0; i < maxPayloads; i++)
            {
                Assert.AreEqual(
                    RpcReplayBuffer.EnqueueResult.Ok,
                    buf.TryEnqueue(new byte[RpcReplayBuffer.MaxPayloadBytes]),
                    $"payload #{i} should fit within the cumulative cap");
            }

            Assert.AreEqual(
                RpcReplayBuffer.EnqueueResult.DroppedCumulativeTooLarge,
                buf.TryEnqueue(new byte[1]),
                "cumulative cap should reject the very next enqueue");
            Assert.AreEqual(1L, buf.DroppedCount,
                "cumulative-cap rejection must count toward DroppedCount");
        }
    }
}
