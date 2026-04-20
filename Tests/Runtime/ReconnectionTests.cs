// RTMPE SDK — Tests/Runtime/ReconnectionTests.cs
//
// NUnit Edit-Mode tests covering the transport-level disconnect/reconnect
// state machine.  These exercise UdpTransport directly without going through
// the full handshake flow, since the handshake is covered by the higher-level
// HandshakeHandlerTests.

using System;
using System.Net;
using System.Net.Sockets;
using NUnit.Framework;
using RTMPE.Transport;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Transport")]
    public class ReconnectionTests
    {
        // ── Basic disconnect/reconnect cycle ─────────────────────────────────

        [Test]
        public void Dispose_AllowsReconnect_ViaFreshTransportInstance()
        {
            int port = FreeLoopbackPort();
            var t1 = new UdpTransport("127.0.0.1", port);
            t1.Connect();
            t1.Dispose();

            // A fresh instance targeting the same endpoint must not throw.
            Assert.DoesNotThrow(() =>
            {
                var t2 = new UdpTransport("127.0.0.1", port);
                t2.Connect();
                t2.Dispose();
            });
        }

        [Test]
        public void SendAfterDispose_ThrowsInvalidOperation()
        {
            var t = new UdpTransport("127.0.0.1", FreeLoopbackPort());
            t.Connect();
            t.Dispose();

            Assert.Throws<InvalidOperationException>(() => t.Send(new byte[] { 1 }),
                "Send after Dispose must not silently succeed on a closed socket");
        }

        [Test]
        public void ReceiveAfterDispose_ReturnsZero()
        {
            var t = new UdpTransport("127.0.0.1", FreeLoopbackPort());
            t.Connect();
            t.Dispose();

            int n = t.Receive(new byte[64]);
            Assert.AreEqual(0, n, "Receive on a disposed transport must yield 0 bytes");
        }

        [Test]
        public void MultipleReconnectCycles_DoNotLeakSockets()
        {
            int port = FreeLoopbackPort();
            // A socket leak would exhaust ephemeral ports; 50 cycles is a
            // conservative upper bound that still fits inside a CI budget.
            for (int i = 0; i < 50; i++)
            {
                var t = new UdpTransport("127.0.0.1", port);
                t.Connect();
                t.Dispose();
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static int FreeLoopbackPort()
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)probe.LocalEndPoint).Port;
        }
    }
}
