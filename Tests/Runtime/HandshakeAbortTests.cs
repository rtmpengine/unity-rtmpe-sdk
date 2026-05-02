// RTMPE SDK — Tests/Runtime/HandshakeAbortTests.cs
//
// Regression tests for handshake cancellation during Connecting state
// (SDK_REVIEW_REPORT.md §7 — "rage-quit" scenario):
//
//   A client in the middle of a handshake (NetworkState.Connecting) may
//   disconnect before any ack arrives — e.g. the user quits the matchmaking
//   screen before the server responds.  Three production bugs are guarded:
//
//   1. Disconnect() during Connecting must reach Disconnected immediately
//      (not get stuck because the guard only checks Disconnected/Disconnecting).
//
//   2. OnDisconnected must fire exactly once with DisconnectReason.ClientRequest
//      and OnConnected must never fire.
//
//   3. A late HandshakeAck packet that arrives after Disconnect() must be
//      silently ignored by the OnHandshakeAck guard — no second state
//      transition, no second OnDisconnected event.

using System.Threading;
using NUnit.Framework;
using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Handshake")]
    [Category("Abort")]
    public class HandshakeAbortTests
    {
        private GameObject     _nmGo;
        private NetworkManager _nm;

        [SetUp]
        public void SetUp()
        {
            foreach (var existing in UnityEngine.Object.FindObjectsByType<NetworkManager>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                UnityEngine.Object.DestroyImmediate(existing.gameObject);

            _nmGo = new GameObject("NM_HandshakeAbort");
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

        [Test]
        [Description("Disconnect() while in Connecting state must immediately transition to Disconnected.")]
        public void Disconnect_WhileConnecting_TransitionsToDisconnected()
        {
            _nm.ForceConnectingStateForTests();
            Assert.AreEqual(NetworkState.Connecting, _nm.State,
                "precondition: ForceConnectingStateForTests must set state to Connecting");

            _nm.Disconnect();

            Assert.AreEqual(NetworkState.Disconnected, _nm.State,
                "Disconnect() during Connecting must reach Disconnected.");
        }

        [Test]
        [Description("Disconnect() during Connecting must fire OnDisconnected exactly once with ClientRequest.")]
        public void Disconnect_WhileConnecting_FiresOnDisconnectedOnce()
        {
            int firedCount = 0;
            DisconnectReason observedReason = DisconnectReason.Unknown;
            _nm.OnDisconnected += r =>
            {
                Interlocked.Increment(ref firedCount);
                observedReason = r;
            };

            _nm.ForceConnectingStateForTests();
            _nm.Disconnect();

            Assert.AreEqual(1, firedCount,
                "OnDisconnected must fire exactly once when Disconnect() interrupts a handshake.");
            Assert.AreEqual(DisconnectReason.ClientRequest, observedReason,
                "DisconnectReason must be ClientRequest for a user-initiated abort.");
        }

        [Test]
        [Description("OnConnected must not fire when Disconnect() is called during the Connecting state.")]
        public void Disconnect_WhileConnecting_OnConnectedNotFired()
        {
            int onConnectedCount = 0;
            _nm.OnConnected += () => Interlocked.Increment(ref onConnectedCount);

            _nm.ForceConnectingStateForTests();
            _nm.Disconnect();

            Assert.AreEqual(0, onConnectedCount,
                "OnConnected must never fire when Disconnect() aborts the handshake before Connected.");
        }

        [Test]
        [Description("A late HandshakeAck arriving after Disconnect() must be silently ignored — " +
                     "state stays Disconnected and OnDisconnected does not fire a second time.")]
        public void HandshakeAck_ArrivalAfterDisconnect_IsIgnored()
        {
            int disconnectedCount = 0;
            _nm.OnDisconnected += _ => Interlocked.Increment(ref disconnectedCount);

            _nm.ForceConnectingStateForTests();
            _nm.Disconnect();

            Assert.AreEqual(NetworkState.Disconnected, _nm.State,
                "precondition: must be Disconnected before the late ack arrives");
            Assert.AreEqual(1, disconnectedCount,
                "precondition: OnDisconnected must have fired once during Disconnect()");

            // Simulate the late-arriving legacy HandshakeAck packet.
            // OnHandshakeAck guard: if (_state != Connecting) return — must fire here.
            _nm.SimulateLegacyHandshakeAckForTests();

            Assert.AreEqual(NetworkState.Disconnected, _nm.State,
                "State must remain Disconnected — the late HandshakeAck guard must reject it.");
            Assert.AreEqual(1, disconnectedCount,
                "OnDisconnected must not fire a second time for the late HandshakeAck.");
        }
    }
}
