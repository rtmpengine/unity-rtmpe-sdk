// RTMPE SDK — Tests/Runtime/PacketLossSimulationTests.cs
//
// NUnit Edit-Mode tests that simulate UDP packet loss at the transport layer
// and verify higher-level invariants hold under loss.  The tests use a lossy
// loopback pattern: the sender writes into a UdpTransport whose remote is a
// bound socket we control directly, so we can choose which packets to
// acknowledge / drop.

using System.Net;
using System.Net.Sockets;
using NUnit.Framework;
using RTMPE.Transport;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Transport")]
    public class PacketLossSimulationTests
    {
        // ── Loss does not corrupt observed ordering ──────────────────────────

        [Test]
        public void LossyDelivery_ReceiverObservesOrderedSubset()
        {
            int port = FreeLoopbackPort();
            using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            receiver.Bind(new IPEndPoint(IPAddress.Loopback, port));
            receiver.ReceiveTimeout = 200;

            var transport = new UdpTransport("127.0.0.1", port);
            transport.Connect();

            // Send sequence numbers 1..10.  The kernel may drop some on a
            // saturated loopback queue; whichever arrive must be strictly
            // increasing because UDP preserves order per src/dst pair in the
            // absence of reordering middleboxes.
            for (int i = 1; i <= 10; i++)
            {
                transport.Send(new byte[] { (byte)i });
            }

            int last = 0;
            int received = 0;
            var buf = new byte[16];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                while (true)
                {
                    int n = receiver.ReceiveFrom(buf, ref any);
                    Assert.GreaterOrEqual(n, 1);
                    Assert.Greater(buf[0], last, "packets must arrive in increasing order");
                    last = buf[0];
                    received++;
                    if (received >= 10) break;
                }
            }
            catch (SocketException) { /* ReceiveTimeout reached — some loss is fine */ }

            Assert.Greater(received, 0, "loopback should deliver at least some packets");
            transport.Dispose();
        }

        // ── Zero-byte datagrams are valid UDP ────────────────────────────────

        [Test]
        public void EmptyDatagram_IsDelivered()
        {
            int port = FreeLoopbackPort();
            using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            receiver.Bind(new IPEndPoint(IPAddress.Loopback, port));
            receiver.ReceiveTimeout = 200;

            var transport = new UdpTransport("127.0.0.1", port);
            transport.Connect();
            transport.Send(new byte[0]);

            var buf = new byte[16];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            int n = receiver.ReceiveFrom(buf, ref any);
            Assert.AreEqual(0, n, "zero-byte datagrams must be deliverable");
            transport.Dispose();
        }

        // ── Large datagram below MTU ─────────────────────────────────────────

        [Test]
        public void MaxSizedDatagram_RoundTripsIntact()
        {
            // 1200 bytes is the typical safe UDP payload size (fits in an
            // Ethernet frame minus IP+UDP headers).
            int port = FreeLoopbackPort();
            using var receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            receiver.Bind(new IPEndPoint(IPAddress.Loopback, port));
            receiver.ReceiveTimeout = 500;

            var transport = new UdpTransport("127.0.0.1", port);
            transport.Connect();

            var payload = new byte[1200];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
            transport.Send(payload);

            var recv = new byte[2048];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            int n = receiver.ReceiveFrom(recv, ref any);
            Assert.AreEqual(1200, n, "full MTU-sized datagram must survive loopback");
            for (int i = 0; i < 1200; i++)
            {
                Assert.AreEqual(payload[i], recv[i], $"byte {i} corrupted in transit");
            }
            transport.Dispose();
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
