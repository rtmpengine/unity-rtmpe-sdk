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
        private Socket        _socket;
        private EndPoint      _remoteEndPoint;
        private AddressFamily _socketFamily = AddressFamily.InterNetwork; // reflects the active socket
        private volatile bool _disposed;
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

            // Prefer IPv4, but fall back to IPv6 if no IPv4 address is available.
            // Previous code threw InvalidOperationException on IPv6-only hosts.
            IPAddress resolved = null;
            AddressFamily family = AddressFamily.InterNetwork;
            foreach (var addr in addresses)
            {
                if (addr.AddressFamily == AddressFamily.InterNetwork)
                {
                    resolved = addr;
                    break;
                }
            }

            if (resolved == null)
            {
                // No IPv4 — try IPv6.
                foreach (var addr in addresses)
                {
                    if (addr.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        resolved = addr;
                        family   = AddressFamily.InterNetworkV6;
                        break;
                    }
                }
            }

            if (resolved == null)
                throw new InvalidOperationException(
                    $"No usable address found for host '{_host}'. " +
                    $"Resolved {addresses.Length} address(es), none IPv4 or IPv6.");

            _remoteEndPoint = new IPEndPoint(resolved, _port);

            _socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp)
            {
                SendBufferSize    = _sendBufferBytes,
                ReceiveBufferSize = _receiveBufferBytes,
                Blocking          = false
            };

            // Record the address family so Receive() can construct a matching EndPoint.
            _socketFamily = family;

            // Bind to any local address/port — the OS assigns an ephemeral source port.
            var anyAddr = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
            _socket.Bind(new IPEndPoint(anyAddr, 0));

            // Discover the actual outgoing source IP via a temporary routing probe.
            // Socket.Connect for UDP just records the destination and triggers the
            // kernel routing table lookup without sending any data. Reading
            // LocalEndPoint after connect gives the real outgoing interface IP
            // (not 0.0.0.0/[::] that Bind(Any) would produce).
            int boundPort = ((IPEndPoint)_socket.LocalEndPoint).Port;
            var loopback  = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            try
            {
                using var probe = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
                probe.Connect(_remoteEndPoint);
                var probeLocal = probe.LocalEndPoint as IPEndPoint;
                if (probeLocal != null)
                {
                    _localEndPoint = new IPEndPoint(probeLocal.Address, boundPort);
                }
                else
                {
                    // Should not happen on any supported platform, but guard defensively.
                    UnityEngine.Debug.LogWarning(
                        "[RTMPE] UdpTransport: routing probe returned a null LocalEndPoint " +
                        "after connect — falling back to loopback as source IP. " +
                        "HandshakeInit AAD will use loopback; the handshake will fail " +
                        "when connecting to a non-loopback server.");
                    _localEndPoint = new IPEndPoint(loopback, boundPort);
                }
            }
            catch (Exception ex)
            {
                // The routing probe is a best-effort kernel lookup (no data is sent).
                // It can fail on hosts with no default route (isolated test containers,
                // offline CI, certain mobile network transitions).
                // When it does, we fall back to loopback as the source IP, which means
                // HandshakeInit AAD will be [0x04][127][0][0][1][port LE] instead of
                // the real interface IP — the gateway will reject the handshake with
                // an AEAD auth failure.  Log a prominent warning so the failure is
                // diagnosable rather than appearing as a silent handshake timeout.
                UnityEngine.Debug.LogWarning(
                    $"[RTMPE] UdpTransport: routing probe failed " +
                    $"({ex.GetType().Name}: {ex.Message}). " +
                    "Falling back to loopback as source IP. " +
                    "If connecting to a remote server the handshake will fail; " +
                    "this is expected in isolated test environments with no default route.");
                _localEndPoint = new IPEndPoint(loopback, boundPort);
            }
        }

        /// <inheritdoc/>
        public override void Disconnect()
        {
            // Dispose() calls Close() internally; calling both is redundant and may throw.
            _socket?.Dispose();
            _socket = null;
        }

        /// <inheritdoc/>
        public override void Send(byte[] data)
        {
            if (_socket == null)
                throw new InvalidOperationException("Transport is not connected. Call Connect() first.");
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            // `Socket.SendTo` for UDP returns the number of bytes accepted by the
            // kernel.  For datagram sockets this is either the full payload
            // length or a SocketException is thrown (EMSGSIZE for oversize,
            // ENOBUFS for send-buffer exhaustion, etc.).  Microsoft's contract
            // does not formally permit a partial return, but this check is
            // cheap and catches platform quirks (e.g. Mono/IL2CPP edge cases)
            // before the symptom manifests as mysteriously dropped packets.
            int sent = _socket.SendTo(data, _remoteEndPoint);
            if (sent != data.Length)
            {
                throw new System.Net.Sockets.SocketException(
                    (int)System.Net.Sockets.SocketError.MessageSize);
            }
        }

        /// <summary>
        /// Send a slice of a buffer without forcing the caller to allocate a
        /// fresh array.  Preferred in hot paths that build packets into
        /// <see cref="System.Buffers.ArrayPool{T}"/>-rented buffers — the
        /// pool requires the exact rented array to be returned, so callers
        /// cannot afford to slice-copy before Send.
        /// </summary>
        /// <param name="buffer">Source buffer (must not be null).</param>
        /// <param name="offset">Starting index inside <paramref name="buffer"/>.</param>
        /// <param name="count">Number of bytes to send from <paramref name="offset"/>.</param>
        public void Send(byte[] buffer, int offset, int count)
        {
            if (_socket == null)
                throw new InvalidOperationException("Transport is not connected. Call Connect() first.");
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    $"offset={offset}, count={count}, buffer.Length={buffer.Length}");

            // See the note in Send(byte[]) for why the return value is asserted.
            int sent = _socket.SendTo(buffer, offset, count, SocketFlags.None, _remoteEndPoint);
            if (sent != count)
            {
                throw new System.Net.Sockets.SocketException(
                    (int)System.Net.Sockets.SocketError.MessageSize);
            }
        }

        /// <inheritdoc/>
        public override int Receive(byte[] buffer)
        {
            if (_socket == null) return 0;

            try
            {
                // The EndPoint type passed to ReceiveFrom must match the
                // socket's address family.  Using IPAddress.Any (IPv4) on an IPv6
                // socket throws ArgumentException and crashes the receive loop.
                EndPoint ep = _socketFamily == AddressFamily.InterNetworkV6
                    ? new IPEndPoint(IPAddress.IPv6Any, 0)
                    : new IPEndPoint(IPAddress.Any,     0);
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
