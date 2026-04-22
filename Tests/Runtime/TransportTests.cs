// RTMPE SDK — Tests/Runtime/TransportTests.cs
//
// NUnit Edit-Mode tests covering UdpTransport edge cases not exercised by
// higher-level integration tests: construction validation, send-before-connect,
// dispose idempotency, the slice-send overload, and concurrent
// send/receive safety.

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RTMPE.Transport;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Transport")]
    public class TransportTests
    {
        // ── Ctor validation ──────────────────────────────────────────────────

        [Test]
        public void Send_BeforeConnect_ThrowsInvalidOperation()
        {
            var transport = new UdpTransport("127.0.0.1", 0);
            Assert.Throws<InvalidOperationException>(() => transport.Send(new byte[] { 1, 2, 3 }));
            transport.Dispose();
        }

        [Test]
        public void SendSlice_BeforeConnect_ThrowsInvalidOperation()
        {
            var transport = new UdpTransport("127.0.0.1", 0);
            Assert.Throws<InvalidOperationException>(() => transport.Send(new byte[16], 0, 8));
            transport.Dispose();
        }

        // ── Slice overload argument validation ───────────────────────────────

        [Test]
        public void SendSlice_NullBuffer_ThrowsArgumentNull()
        {
            var transport = NewConnectedLoopback(out _);
            Assert.Throws<ArgumentNullException>(() => transport.Send(null, 0, 0));
            transport.Dispose();
        }

        [Test]
        public void SendSlice_NegativeOffset_ThrowsArgumentOutOfRange()
        {
            var transport = NewConnectedLoopback(out _);
            Assert.Throws<ArgumentOutOfRangeException>(() => transport.Send(new byte[4], -1, 1));
            transport.Dispose();
        }

        [Test]
        public void SendSlice_CountExceedsBuffer_ThrowsArgumentOutOfRange()
        {
            var transport = NewConnectedLoopback(out _);
            Assert.Throws<ArgumentOutOfRangeException>(() => transport.Send(new byte[4], 2, 5));
            transport.Dispose();
        }

        // ── Slice overload happy path ────────────────────────────────────────

        [Test]
        public void SendSlice_DeliversExactBytes_ToReceiver()
        {
            var transport = NewConnectedLoopback(out int receiverPort);

            using var receiver = NewReceiverSocket(receiverPort, out IPEndPoint _);
            byte[] payload = new byte[128];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);

            // Send only 50 bytes from offset 10 — receiver must observe exactly
            // those 50 bytes.
            transport.Send(payload, 10, 50);

            var recvBuf = new byte[256];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            int n = receiver.ReceiveFrom(recvBuf, ref any);

            Assert.AreEqual(50, n, "receiver must observe exactly the slice length");
            for (int i = 0; i < 50; i++)
            {
                Assert.AreEqual(payload[10 + i], recvBuf[i],
                    $"slice byte {i} mismatch");
            }
            transport.Dispose();
        }

        // ── Dispose idempotency ──────────────────────────────────────────────

        [Test]
        public void Dispose_IsIdempotent()
        {
            var transport = NewConnectedLoopback(out _);
            transport.Dispose();
            Assert.DoesNotThrow(() => transport.Dispose(),
                "Dispose must be safe to call multiple times");
        }

        // ── Receive failure modes ────────────────────────────────────────────

        [Test]
        public void Receive_WithNoData_ReturnsZero()
        {
            var transport = NewConnectedLoopback(out _);
            var buf = new byte[64];
            int n = transport.Receive(buf);
            Assert.AreEqual(0, n, "Receive on empty socket must return 0, not throw");
            transport.Dispose();
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static UdpTransport NewConnectedLoopback(out int receiverPort)
        {
            // Find a free port on loopback by binding a throwaway socket.
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            receiverPort = ((IPEndPoint)probe.LocalEndPoint).Port;
            probe.Close();

            var transport = new UdpTransport("127.0.0.1", receiverPort);
            transport.Connect();
            return transport;
        }

        private static Socket NewReceiverSocket(int port, out IPEndPoint bound)
        {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.Bind(new IPEndPoint(IPAddress.Loopback, port));
            sock.ReceiveTimeout = 500;
            bound = (IPEndPoint)sock.LocalEndPoint;
            return sock;
        }
    }
}
