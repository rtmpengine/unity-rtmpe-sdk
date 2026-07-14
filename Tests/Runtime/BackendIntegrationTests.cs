// RTMPE SDK — Tests/Runtime/BackendIntegrationTests.cs
//
// Regression tests for two SDK ↔ Backend wire contracts called out in
// BACKEND_REVIEW_REPORT.md §"التكامل SDK ↔ Backend":
//
//   1. HeartbeatAck backpressure byte (issue #3 in the report).
//      The gateway places a per-session backpressure level (0–255) in the
//      single-byte HeartbeatAck payload (modules/gateway/src/main.rs:246).
//      The SDK must surface that value via NetworkManager.ServerBackpressure
//      so adaptive send-rate logic and observability dashboards can react.
//
//   2. Disconnect reason byte (issue #4 in the report).
//      The gateway places a typed-reason byte in the Disconnect packet
//      payload (modules/gateway/src/packet/mod.rs::disconnect_reason).
//      The SDK must map that byte to DisconnectReason instead of the
//      generic ServerRequest fallback.  Empty payload from a legacy
//      gateway must continue to fall back to ServerRequest so old
//      deployments keep working.

using System.Threading;
using NUnit.Framework;
using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("BackendIntegration")]
    public class BackendIntegrationTests
    {
        private GameObject     _nmGo;
        private NetworkManager _nm;

        [SetUp]
        public void SetUp()
        {
            foreach (var existing in UnityEngine.Object.FindObjectsByType<NetworkManager>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                UnityEngine.Object.DestroyImmediate(existing.gameObject);

            _nmGo = new GameObject("NM_BackendIntegration");
            _nm   = _nmGo.AddComponent<NetworkManager>();
            NetworkManager.ResetApplicationQuittingForTests();
        }

        [TearDown]
        public void TearDown()
        {
            if (_nmGo != null)
                UnityEngine.Object.DestroyImmediate(_nmGo);
            NetworkManager.ResetApplicationQuittingForTests();
        }

        // ── Issue #3: HeartbeatAck backpressure parsing ─────────────────────

        [Test]
        [Description("Initial ServerBackpressure is 0 before any HeartbeatAck has arrived.")]
        public void ServerBackpressure_BeforeAnyAck_IsZero()
        {
            Assert.AreEqual(0, _nm.ServerBackpressure,
                "Default backpressure must be 0 — anything else implies stale state from a prior session.");
        }

        [Test]
        [Description("HeartbeatAck with a 1-byte payload updates ServerBackpressure to that value.")]
        public void HeartbeatAck_WithBackpressureByte_UpdatesObservableProperty()
        {
            _nm.SimulateHeartbeatAckForTests(new byte[] { 128 });
            Assert.AreEqual(128, _nm.ServerBackpressure,
                "Backpressure value 128 must surface verbatim from a fresh HeartbeatAck.");

            _nm.SimulateHeartbeatAckForTests(new byte[] { 255 });
            Assert.AreEqual(255, _nm.ServerBackpressure,
                "Bucket-empty signal (255) must propagate to the observable property.");

            _nm.SimulateHeartbeatAckForTests(new byte[] { 0 });
            Assert.AreEqual(0, _nm.ServerBackpressure,
                "Recovery to 0 must also propagate — the property tracks the latest sample.");
        }

        [Test]
        [Description("HeartbeatAck with an empty payload (legacy gateway) leaves backpressure unchanged.")]
        public void HeartbeatAck_EmptyPayload_LeavesBackpressureUnchanged()
        {
            // Prime: any non-default value.
            _nm.SimulateHeartbeatAckForTests(new byte[] { 42 });
            Assert.AreEqual(42, _nm.ServerBackpressure, "precondition");

            // Legacy gateway omits the byte; SDK must not zero out the cache
            // or throw — graceful degradation across mixed-version deployments.
            _nm.SimulateHeartbeatAckForTests(System.Array.Empty<byte>());
            Assert.AreEqual(42, _nm.ServerBackpressure,
                "Empty HeartbeatAck must leave the last-known backpressure value intact.");
        }

        [Test]
        [Description("Backpressure boundary values 0 and 255 both round-trip correctly.")]
        public void HeartbeatAck_BackpressureBoundaryValues_RoundTrip()
        {
            _nm.SimulateHeartbeatAckForTests(new byte[] { 0 });
            Assert.AreEqual(0, _nm.ServerBackpressure);

            _nm.SimulateHeartbeatAckForTests(new byte[] { 1 });
            Assert.AreEqual(1, _nm.ServerBackpressure);

            _nm.SimulateHeartbeatAckForTests(new byte[] { 254 });
            Assert.AreEqual(254, _nm.ServerBackpressure);

            _nm.SimulateHeartbeatAckForTests(new byte[] { 255 });
            Assert.AreEqual(255, _nm.ServerBackpressure);
        }

        // ── Issue #4: Disconnect reason byte parsing ────────────────────────

        [Test]
        [Description("Empty Disconnect payload (legacy gateway) maps to ServerRequest.")]
        public void Disconnect_EmptyPayload_LegacyMapsToServerRequest()
        {
            DisconnectReason observed = DisconnectReason.Unknown;
            int firedCount = 0;
            _nm.OnDisconnected += r =>
            {
                Interlocked.Increment(ref firedCount);
                observed = r;
            };

            _nm.SetSessionEstablishedForTests(true);
            _nm.ForceConnectingStateForTests();
            // Reach Connected via internal state machine — SimulateServerDisconnect
            // requires _sessionEstablished true plus a non-Disconnected state.
            // ForceConnectingStateForTests + the established flag is sufficient
            // since OnServerDisconnect doesn't gate on Connected specifically.
            _nm.SimulateServerDisconnectForTests(System.Array.Empty<byte>());

            Assert.AreEqual(1, firedCount, "OnDisconnected must fire exactly once.");
            Assert.AreEqual(DisconnectReason.ServerRequest, observed,
                "Legacy empty-payload Disconnect must default to ServerRequest for backward compatibility.");
        }

        [Test]
        [Description("Disconnect with reason byte 0x02 maps to ServerRequest.")]
        public void Disconnect_ServerRequestByte_MapsToServerRequest()
        {
            DisconnectReason observed = DisconnectReason.Unknown;
            _nm.OnDisconnected += r => observed = r;

            _nm.SetSessionEstablishedForTests(true);
            _nm.ForceConnectingStateForTests();
            _nm.SimulateServerDisconnectForTests(new byte[] { 0x02 });

            Assert.AreEqual(DisconnectReason.ServerRequest, observed,
                "Wire byte 0x02 must map to DisconnectReason.ServerRequest.");
        }

        [Test]
        [Description("Disconnect with reason byte 0x05 maps to Kicked.")]
        public void Disconnect_KickedByte_MapsToKicked()
        {
            DisconnectReason observed = DisconnectReason.Unknown;
            _nm.OnDisconnected += r => observed = r;

            _nm.SetSessionEstablishedForTests(true);
            _nm.ForceConnectingStateForTests();
            _nm.SimulateServerDisconnectForTests(new byte[] { 0x05 });

            Assert.AreEqual(DisconnectReason.Kicked, observed,
                "Wire byte 0x05 must map to DisconnectReason.Kicked.");
        }

        [Test]
        [Description("Disconnect with reason byte 0x07 maps to ProtocolError.")]
        public void Disconnect_ProtocolErrorByte_MapsToProtocolError()
        {
            DisconnectReason observed = DisconnectReason.Unknown;
            _nm.OnDisconnected += r => observed = r;

            _nm.SetSessionEstablishedForTests(true);
            _nm.ForceConnectingStateForTests();
            _nm.SimulateServerDisconnectForTests(new byte[] { 0x07 });

            Assert.AreEqual(DisconnectReason.ProtocolError, observed,
                "Wire byte 0x07 must map to DisconnectReason.ProtocolError.");
        }

        [Test]
        [Description("Disconnect with reason byte 0x00 (Unknown) maps to Unknown.")]
        public void Disconnect_UnknownByte_MapsToUnknown()
        {
            DisconnectReason observed = DisconnectReason.ServerRequest; // sentinel
            _nm.OnDisconnected += r => observed = r;

            _nm.SetSessionEstablishedForTests(true);
            _nm.ForceConnectingStateForTests();
            _nm.SimulateServerDisconnectForTests(new byte[] { 0x00 });

            Assert.AreEqual(DisconnectReason.Unknown, observed,
                "Wire byte 0x00 must map to DisconnectReason.Unknown explicitly.");
        }

        [Test]
        [Description("Unrecognised reason byte falls back to ServerRequest (forward compatibility).")]
        public void Disconnect_UnrecognisedByte_FallsBackToServerRequest()
        {
            DisconnectReason observed = DisconnectReason.Unknown;
            _nm.OnDisconnected += r => observed = r;

            _nm.SetSessionEstablishedForTests(true);
            _nm.ForceConnectingStateForTests();
            // 0xAB is not a defined reason — a future gateway might emit it.
            // The SDK must not throw and must fall back to ServerRequest so
            // old SDK builds keep working against newer gateways.
            _nm.SimulateServerDisconnectForTests(new byte[] { 0xAB });

            Assert.AreEqual(DisconnectReason.ServerRequest, observed,
                "Unknown reason byte must fall back to ServerRequest for forward compatibility.");
        }

        [Test]
        [Description("Disconnect received before session established is silently ignored.")]
        public void Disconnect_BeforeSessionEstablished_IsIgnored()
        {
            int firedCount = 0;
            _nm.OnDisconnected += _ => Interlocked.Increment(ref firedCount);

            // Do NOT mark session established — production code gates on this.
            _nm.ForceConnectingStateForTests();
            _nm.SimulateServerDisconnectForTests(new byte[] { 0x02 });

            Assert.AreEqual(0, firedCount,
                "Disconnect during handshake (session not yet established) must be ignored — " +
                "matches the existing guard at NetworkManager.OnServerDisconnect.");
            Assert.AreNotEqual(NetworkState.Disconnected, _nm.State,
                "State must not transition while the handshake is still in flight.");
        }
    }
}
