// RTMPE SDK — Runtime/Infrastructure/Transport/UdpTransport.cs
//
// Non-blocking UDP socket transport.
//
// Design notes:
//  • Blocking = false + Poll(0) avoids any blocking system call on the hot path.
//  • SendTo / ReceiveFrom are used (not Connect+Send/Receive) to avoid the implicit
//    UDP "connection" state that can trigger ICMP port-unreachable errors on some OSes.
//  • SocketError.WouldBlock / ConnectionReset are silently swallowed per RFC 1122;
//    the receive loop simply returns 0 bytes and retries next iteration.
//  • An IDisposable _disposed guard prevents double-dispose races on shutdown.

using System;
using System.Net;
using System.Net.Sockets;

namespace RTMPE.Transport
{
    /// <summary>
    /// Non-blocking UDP socket transport.
    /// Thread-safe for concurrent calls to <see cref="Send"/> and <see cref="Receive"/>
    /// from a single network thread (not designed for multi-producer/multi-consumer).
    /// </summary>
    public sealed class UdpTransport : NetworkTransport
    {
        // ── Configuration (immutable after construction) ───────────────────────
        private readonly string _host;
        private readonly int    _port;
        private readonly int    _sendBufferBytes;
        private readonly int    _receiveBufferBytes;

        // ── Runtime state ──────────────────────────────────────────────────────
        private Socket   _socket;
        private EndPoint _remoteEndPoint;
        private bool     _disposed;
        // Populated by Connect() after the socket is bound.
        // Reflects the actual outgoing source IP (discovered via a routing probe),
        // not 0.0.0.0 that would result from Bind(IPAddress.Any, 0).
        private System.Net.IPEndPoint _localEndPoint;

        // ── Properties ─────────────────────────────────────────────────────────
        /// <inheritdoc/>
        public override bool IsConnected => _socket != null && !_disposed;

        /// <summary>
        /// The local source endpoint (IP + ephemeral port) the OS assigned when
        /// the socket was bound. Populated after <see cref="Connect"/> is called.
        /// The IP reflects the actual outgoing interface (not 0.0.0.0).
        /// Returns <see langword="null"/> before <see cref="Connect"/>.
        /// </summary>
        public override System.Net.IPEndPoint LocalEndPoint => _localEndPoint;

        // ── Construction ───────────────────────────────────────────────────────

        /// <param name="host">Remote hostname or IP address (e.g. "127.0.0.1").</param>
        /// <param name="port">Remote UDP port (1–65535).</param>
        /// <param name="sendBufferBytes">SO_SNDBUF size in bytes (default 4 KiB).</param>
        /// <param name="receiveBufferBytes">SO_RCVBUF size in bytes (default 4 KiB).</param>
        public UdpTransport(
            string host,
            int    port,
            int    sendBufferBytes    = 4_096,
            int    receiveBufferBytes = 4_096)
        {
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("Host must not be null or whitespace.", nameof(host));
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), "Port must be in range 1–65535.");

            _host               = host;
            _port               = port;
            _sendBufferBytes    = sendBufferBytes;
            _receiveBufferBytes = receiveBufferBytes;
        }

        // ── NetworkTransport ───────────────────────────────────────────────────

        /// <inheritdoc/>
        public override void Connect()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UdpTransport));

            var addresses = Dns.GetHostAddresses(_host);

            // Filter for IPv4: the socket is AddressFamily.InterNetwork (IPv4-only).
            // addresses[0] may be an IPv6 address on dual-stack systems when _host is a
            // hostname like "localhost" that resolves to ::1 first. Using an IPv6 endpoint
            // with an IPv4 socket causes an AddressFamilyNotSupported SocketException.
            IPAddress ipv4 = null;
            foreach (var addr in addresses)
            {
                if (addr.AddressFamily == AddressFamily.InterNetwork)
                {
                    ipv4 = addr;
                    break;
                }
            }

            if (ipv4 == null)
                throw new InvalidOperationException(
                    $"No IPv4 address found for host '{_host}'. " +
                    $"Resolved {addresses.Length} address(es), none with AddressFamily.InterNetwork.");

            _remoteEndPoint = new IPEndPoint(ipv4, _port);

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                SendBufferSize    = _sendBufferBytes,
                ReceiveBufferSize = _receiveBufferBytes,
                Blocking          = false
            };

            // Bind to any local address/port — the OS assigns an ephemeral source port.
            _socket.Bind(new IPEndPoint(IPAddress.Any, 0));

            // Discover the actual outgoing source IP via a temporary routing probe.
            // Socket.Connect for UDP just records the destination and triggers the
            // kernel routing table lookup without sending any data. Reading
            // LocalEndPoint after connect gives the real outgoing interface IP
            // (not 0.0.0.0 that Bind(Any) would produce).
            int boundPort = ((IPEndPoint)_socket.LocalEndPoint).Port;
            try
            {
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                probe.Connect(_remoteEndPoint);
                var probeLocal = probe.LocalEndPoint as IPEndPoint;
                _localEndPoint = probeLocal != null
                    ? new IPEndPoint(probeLocal.Address, boundPort)
                    : new IPEndPoint(IPAddress.Loopback, boundPort);
            }
            catch
            {
                // Fallback: use loopback IP (works for localhost dev/test).
                _localEndPoint = new IPEndPoint(IPAddress.Loopback, boundPort);
            }
        }

        /// <inheritdoc/>
        public override void Disconnect()
        {
            _socket?.Close();
            _socket?.Dispose();
            _socket = null;
        }

        /// <inheritdoc/>
        public override void Send(byte[] data)
        {
            if (_socket == null)
                throw new InvalidOperationException("Transport is not connected. Call Connect() first.");

            _socket.SendTo(data, _remoteEndPoint);
        }

        /// <inheritdoc/>
        public override int Receive(byte[] buffer)
        {
            if (_socket == null) return 0;

            try
            {
                EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                return _socket.ReceiveFrom(buffer, ref ep);
            }
            catch (SocketException ex)
                when (ex.SocketErrorCode == SocketError.WouldBlock       // No data ready (Linux / macOS)
                   || ex.SocketErrorCode == SocketError.ConnectionReset) // ICMP port-unreachable (Windows)
            {
                return 0;
            }
        }

        /// <inheritdoc/>
        public override bool Poll(int microSeconds)
        {
            if (_socket == null) return false;
            return _socket.Poll(microSeconds, SelectMode.SelectRead);
        }

        /// <inheritdoc/>
        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }
    }
}
