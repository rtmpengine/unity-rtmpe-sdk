// RTMPE SDK — Tests/Runtime/ServerKeyPinningTests.cs
//
// Unit tests for the SDK's three-mode server-static-key pinning:
//
//  • Strict           — must match operator-supplied pin, else refuse
//  • TrustOnFirstUse  — capture-and-pin on first connect; strict thereafter
//  • InsecureNoPinning — accept any valid signature (warning emitted)
//
// The tests target the pure-logic ServerKeyPinning helper plus an
// in-memory IServerKeyPinStore — no Unity engine dependencies.  The
// HandshakeHandler.ValidateChallenge integration is covered indirectly
// here (PinToEnforce passing through) and directly by the existing
// HandshakeHandlerTests, which exercise the constant-time compare.

using System.Collections.Generic;
using NUnit.Framework;
using RTMPE.Crypto;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Crypto")]
    [Category("ServerPinning")]
    public class ServerKeyPinningTests
    {
        // ── In-memory pin store used as the test-double ────────────────────

        private sealed class InMemoryPinStore : IServerKeyPinStore
        {
            public readonly Dictionary<string, byte[]> Pins = new Dictionary<string, byte[]>();
            public int  SaveCalls;
            public int  ClearCalls;

            public byte[] Load(string endpoint) =>
                Pins.TryGetValue(endpoint, out var v) ? (byte[])v.Clone() : null;

            public void Save(string endpoint, byte[] pin)
            {
                SaveCalls++;
                Pins[endpoint] = (byte[])pin.Clone();
            }

            public void Clear(string endpoint)
            {
                ClearCalls++;
                Pins.Remove(endpoint);
            }
        }

        private static byte[] Pin(byte fill)
        {
            var k = new byte[32];
            for (int i = 0; i < 32; i++) k[i] = fill;
            return k;
        }

        // ── CanonicalEndpoint ──────────────────────────────────────────────

        [Test]
        public void CanonicalEndpoint_LowercasesHost_AndPreservesPort()
        {
            Assert.AreEqual("example.com:7777",
                ServerKeyPinning.CanonicalEndpoint("Example.COM", 7777));
        }

        [Test]
        public void CanonicalEndpoint_TrimsWhitespace()
        {
            Assert.AreEqual("gw.local:1234",
                ServerKeyPinning.CanonicalEndpoint("  gw.local  ", 1234));
        }

        [Test]
        public void CanonicalEndpoint_DistinguishesPorts()
        {
            Assert.AreNotEqual(
                ServerKeyPinning.CanonicalEndpoint("a.example", 7777),
                ServerKeyPinning.CanonicalEndpoint("a.example", 7778));
        }

        // ── Strict mode ────────────────────────────────────────────────────

        [Test]
        public void Strict_WithPin_ProceedsWithThatPin()
        {
            var pin   = Pin(0xAA);
            var store = new InMemoryPinStore();
            var r = ServerKeyPinning.PreparePin(
                ServerPinningMode.Strict, pin, store, "h", 1);
            Assert.AreEqual(PinDecision.ProceedWithPin, r.Decision);
            Assert.AreSame(pin, r.PinToEnforce, "PinToEnforce must be the configured pin");
            Assert.AreEqual(0, store.SaveCalls,  "Strict must never write to the pin store");
        }

        [Test]
        public void Strict_WithoutPin_RefusesFailClosed()
        {
            var store = new InMemoryPinStore();
            var r = ServerKeyPinning.PreparePin(
                ServerPinningMode.Strict, configuredPin: null, store, "h", 1);
            Assert.AreEqual(PinDecision.RefuseStrictNoPin, r.Decision);
            Assert.IsNull(r.PinToEnforce);
        }

        [Test]
        public void Strict_WithMalformedPin_RefusesFailClosed()
        {
            var malformed = new byte[31];   // wrong length
            var r = ServerKeyPinning.PreparePin(
                ServerPinningMode.Strict, malformed, new InMemoryPinStore(), "h", 1);
            Assert.AreEqual(PinDecision.RefuseStrictNoPin, r.Decision);
        }

        [Test]
        public void Strict_DoesNotConsultPinStore()
        {
            var store = new InMemoryPinStore();
            store.Pins["h:1"] = Pin(0xCC); // pre-existing TOFU pin must not leak in

            var r = ServerKeyPinning.PreparePin(
                ServerPinningMode.Strict, configuredPin: null, store, "h", 1);

            Assert.AreEqual(PinDecision.RefuseStrictNoPin, r.Decision,
                "Strict must ignore the TOFU store; missing operator pin = refusal");
        }

        // ── TrustOnFirstUse ────────────────────────────────────────────────

        [Test]
        public void Tofu_FirstConnect_NoPersistedPin_ProceedsWithCapture()
        {
            var store = new InMemoryPinStore();
            var r = ServerKeyPinning.PreparePin(
                ServerPinningMode.TrustOnFirstUse, configuredPin: null, store, "h", 1);
            Assert.AreEqual(PinDecision.ProceedCaptureFirstUse, r.Decision);
            Assert.IsNull(r.PinToEnforce, "Capture flow must not enforce a pin during the call");
        }

        [Test]
        public void Tofu_FirstConnect_PersistFirstUse_WritesPin()
        {
            var store = new InMemoryPinStore();
            var r = ServerKeyPinning.PreparePin(
                ServerPinningMode.TrustOnFirstUse, null, store, "h", 1);

            var observed = Pin(0x11);
            bool wrote = ServerKeyPinning.PersistFirstUse(r, store, observed);
            Assert.IsTrue(wrote);
            Assert.AreEqual(1, store.SaveCalls);
            Assert.IsTrue(BytesEqual(observed, store.Pins["h:1"]));
        }

        [Test]
        public void Tofu_SecondConnect_SameKey_ProceedsWithPersistedPin()
        {
            var store = new InMemoryPinStore();
            var observed = Pin(0x11);

            // First connect — capture
            var r1 = ServerKeyPinning.PreparePin(
                ServerPinningMode.TrustOnFirstUse, null, store, "h", 1);
            ServerKeyPinning.PersistFirstUse(r1, store, observed);

            // Second connect — the resolver must now load the persisted pin
            // and demand strict equality from ValidateChallenge.
            var r2 = ServerKeyPinning.PreparePin(
                ServerPinningMode.TrustOnFirstUse, null, store, "h", 1);
            Assert.AreEqual(PinDecision.ProceedWithPin, r2.Decision);
            Assert.IsTrue(BytesEqual(observed, r2.PinToEnforce));
        }

        [Test]
        public void Tofu_SecondConnect_PersistFirstUse_DoesNotOverwrite()
        {
            var store = new InMemoryPinStore();
            var firstObserved = Pin(0x11);

            var r1 = ServerKeyPinning.PreparePin(
                ServerPinningMode.TrustOnFirstUse, null, store, "h", 1);
            ServerKeyPinning.PersistFirstUse(r1, store, firstObserved);

            // Resolution on the second connect is ProceedWithPin — calling
            // PersistFirstUse with a *different* observed key must be a no-op
            // (overwriting on every connect would silently launder a MITM).
            var r2 = ServerKeyPinning.PreparePin(
                ServerPinningMode.TrustOnFirstUse, null, store, "h", 1);
            int savesBefore = store.SaveCalls;
            bool wrote = ServerKeyPinning.PersistFirstUse(r2, store, Pin(0x99));
            Assert.IsFalse(wrote);
            Assert.AreEqual(savesBefore, store.SaveCalls);
            Assert.IsTrue(BytesEqual(firstObserved, store.Pins["h:1"]),
                "Second-connect persist must NEVER overwrite a captured pin");
        }

        [Test]
        public void Tofu_DifferentEndpoint_RecapturesIndependently()
        {
            var store = new InMemoryPinStore();
            ServerKeyPinning.PersistFirstUse(
                ServerKeyPinning.PreparePin(ServerPinningMode.TrustOnFirstUse, null, store, "a", 1),
                store, Pin(0x11));

            // A fresh endpoint is a fresh TOFU slot — must not silently
            // adopt the pin from a different host:port.
            var r = ServerKeyPinning.PreparePin(
                ServerPinningMode.TrustOnFirstUse, null, store, "b", 1);
            Assert.AreEqual(PinDecision.ProceedCaptureFirstUse, r.Decision);
        }

        [Test]
        public void Tofu_WithConfiguredPin_HonoursConfiguredPin()
        {
            // If the operator both enables TOFU AND embeds a pin, the embedded
            // pin wins — a developer who set a pin should never have it
            // silently ignored.
            var store        = new InMemoryPinStore();
            var configured   = Pin(0x55);
            store.Pins["h:1"] = Pin(0x11); // a pre-existing TOFU pin (different)

            var r = ServerKeyPinning.PreparePin(
                ServerPinningMode.TrustOnFirstUse, configured, store, "h", 1);

            Assert.AreEqual(PinDecision.ProceedWithPin, r.Decision);
            Assert.IsTrue(BytesEqual(configured, r.PinToEnforce));
        }

        // ── InsecureNoPinning ──────────────────────────────────────────────

        [Test]
        public void InsecureNoPinning_ProceedsUnpinned()
        {
            var r = ServerKeyPinning.PreparePin(
                ServerPinningMode.InsecureNoPinning, null, new InMemoryPinStore(), "h", 1);
            Assert.AreEqual(PinDecision.ProceedUnpinned, r.Decision);
            Assert.IsNull(r.PinToEnforce);
        }

        [Test]
        public void Store_Clear_RemovesPinAndReTriggersTofu()
        {
            // Models the ClearPinnedKey() rotation flow on NetworkManager:
            // after Clear() the next TOFU resolution must re-enter the
            // capture branch instead of returning the stale pin.
            var store = new InMemoryPinStore();
            store.Pins["h:1"] = Pin(0x77);

            var endpoint = ServerKeyPinning.CanonicalEndpoint("h", 1);
            store.Clear(endpoint);

            Assert.IsNull(store.Load(endpoint), "Clear must remove the persisted pin");

            var r = ServerKeyPinning.PreparePin(
                ServerPinningMode.TrustOnFirstUse, null, store, "h", 1);
            Assert.AreEqual(PinDecision.ProceedCaptureFirstUse, r.Decision,
                "After Clear(), TOFU should re-enter the capture branch");
        }

        [Test]
        public void InsecureNoPinning_NeverPersists()
        {
            var store = new InMemoryPinStore();
            var r = ServerKeyPinning.PreparePin(
                ServerPinningMode.InsecureNoPinning, null, store, "h", 1);
            ServerKeyPinning.PersistFirstUse(r, store, Pin(0x11));
            Assert.AreEqual(0, store.SaveCalls,
                "InsecureNoPinning must never write a pin (would later masquerade as TOFU).");
        }

        // ── Defensive paths ────────────────────────────────────────────────

        [Test]
        public void UnknownMode_FailsClosed()
        {
            // Cast a deliberately-out-of-range value: defends against a
            // settings asset corrupted to an unknown enum integer.
            var bogus = (ServerPinningMode)99;
            var r = ServerKeyPinning.PreparePin(bogus, null, new InMemoryPinStore(), "h", 1);
            Assert.AreEqual(PinDecision.RefuseStrictNoPin, r.Decision);
        }

        [Test]
        public void PersistFirstUse_RejectsWrongLengthKey()
        {
            var store = new InMemoryPinStore();
            var r = ServerKeyPinning.PreparePin(
                ServerPinningMode.TrustOnFirstUse, null, store, "h", 1);

            Assert.IsFalse(ServerKeyPinning.PersistFirstUse(r, store, new byte[31]));
            Assert.IsFalse(ServerKeyPinning.PersistFirstUse(r, store, null));
            Assert.AreEqual(0, store.SaveCalls);
        }

        [Test]
        public void PersistFirstUse_NoOpForNonCaptureDecisions()
        {
            var store = new InMemoryPinStore();

            var rStrict = new PinResolution(PinDecision.ProceedWithPin, Pin(0x77), "h:1");
            Assert.IsFalse(ServerKeyPinning.PersistFirstUse(rStrict, store, Pin(0x77)));

            var rUnpinned = new PinResolution(PinDecision.ProceedUnpinned, null, "h:1");
            Assert.IsFalse(ServerKeyPinning.PersistFirstUse(rUnpinned, store, Pin(0x77)));

            var rRefuse = new PinResolution(PinDecision.RefuseStrictNoPin, null, "h:1");
            Assert.IsFalse(ServerKeyPinning.PersistFirstUse(rRefuse, store, Pin(0x77)));

            Assert.AreEqual(0, store.SaveCalls);
        }

        // ── Mismatch path is enforced by HandshakeHandler.ValidateChallenge ─
        //
       // PreparePin's role on a TOFU follow-up connect is to load the
        // persisted pin into PinToEnforce.  HandshakeHandler.ValidateChallenge
        // then performs a constant-time compare against the embedded
        // staticPub and returns false on mismatch — covered exhaustively by
        // HandshakeHandlerTests.ConstantTimeEquals_*.  The test below pins
        // down the contract that the resolver actually surfaces the
        // persisted bytes, so a regression where it returned null would be
        // caught here rather than silently downgrading to TOFU-recapture.

        [Test]
        public void Tofu_FollowupConnect_SurfacesPersistedPinForValidator()
        {
            var store = new InMemoryPinStore();
            store.Pins["h:1"] = Pin(0x42);

            var r = ServerKeyPinning.PreparePin(
                ServerPinningMode.TrustOnFirstUse, null, store, "h", 1);

            Assert.AreEqual(PinDecision.ProceedWithPin, r.Decision,
                "A persisted pin must NOT be silently re-captured");
            Assert.IsTrue(BytesEqual(Pin(0x42), r.PinToEnforce));
        }

        // ── ValidateChallenge integration: pin mismatch is enforced ────────
        //
       // ValidateChallenge applies the pin compare BEFORE the Ed25519 verify.
        // We construct a 128-byte payload whose embedded staticPub differs
        // from the supplied pin and assert the function returns false — i.e.
        // a TOFU follow-up that the gateway has had its key rotated cannot
        // slide through to signature verification with the new key.

        [Test]
        public void HandshakeHandler_PinMismatch_RejectsBeforeSignatureCheck()
        {
            var h = new HandshakeHandler();

            var payload = new byte[128];
            // Embedded staticPub at offset 32 is all 0xCC.
            for (int i = 32; i < 64; i++) payload[i] = 0xCC;

            var pin = new byte[32];
            for (int i = 0; i < 32; i++) pin[i] = 0x42;   // disagrees with 0xCC

            bool ok = h.ValidateChallenge(
                payload,
                handshakeInitCiphertext: null,
                HandshakeFlow.Reconnect,
                out _,
                out _,
                pinnedServerStaticPub: pin);

            Assert.IsFalse(ok,
                "ValidateChallenge must refuse when the embedded staticPub disagrees with the pin.");
        }

        [Test]
        public void HandshakeHandler_PinMatches_StillRequiresValidSignature()
        {
            // When the pin matches, ValidateChallenge falls through to the
            // Ed25519 verify.  A bogus 64-byte signature must still be
            // refused — the pin is an additional gate, never a replacement.
            var h = new HandshakeHandler();

            var staticPub = new byte[32];
            for (int i = 0; i < 32; i++) staticPub[i] = 0xCC;

            var payload = new byte[128];
            System.Buffer.BlockCopy(staticPub, 0, payload, 32, 32);
            // Rest of the payload (ephemeral, sig) left zero — Ed25519 verify
            // will reject.

            bool ok = h.ValidateChallenge(
                payload,
                handshakeInitCiphertext: null,
                HandshakeFlow.Reconnect,
                out _,
                out _,
                pinnedServerStaticPub: staticPub);

            Assert.IsFalse(ok,
                "Pin match alone must not authorise — Ed25519 verify is still gating.");
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
