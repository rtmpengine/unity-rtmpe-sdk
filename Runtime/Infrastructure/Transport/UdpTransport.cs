// RTMPE SDK — Runtime/Infrastructure/Transport/UdpTransport.cs
//
// Non-blocking UDP socket transport.
//
// Design notes:
// • Blocking = false + Poll(0) avoids any blocking system call on the hot path.
// • SendTo / ReceiveFrom are used (not Connect+Send/Receive) to avoid the implicit
//   UDP "connection" state that can trigger ICMP port-unreachable errors on some OSes.
// • SocketError.WouldBlock / ConnectionReset are silently swallowed per RFC 1122;
//   the receive loop simply returns 0 bytes and retries next iteration.
// • An IDisposable _disposed guard prevents double-dispose races on shutdown.
//
// Concurrency model:
//  The socket lifetime is racy by design: Disconnect() may be called from any
//  thread (typically the main thread on shutdown) while the network background
//  thread is parked inside Poll() or ReceiveFrom().  Rather than serialise the
//  hot syscall paths under a lock — which would defeat the non-blocking design
//  and risk deadlocks if Dispose() ran on the same thread that holds the lock —
//  we treat disposal as racing with the next syscall and tolerate it:
//
//    1. _socket is read into a local variable once per call ("snapshot").  Any
//       subsequent Disconnect() that nulls the field cannot turn the local
//       reference into null mid-syscall, so the NullReferenceException class
//       of bug is eliminated.
//    2. ObjectDisposedException and the "racing close" SocketError variants are
//       caught and converted into a benign return (false / 0).  The caller
//       loop checks _running on the next iteration and exits cleanly.
//
// This is the conventional .NET idiom for closing a socket from a thread other
// than the one parked in the syscall — the same pattern used by Kestrel,
// SignalR and the BCL's own SocketAsyncEventArgs reference implementations.

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

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
        private readonly TimeSpan _dnsTimeout;
        private readonly int    _maxDatagramSize;

        /// <summary>
        /// Conservative MTU-safe upper bound for outgoing datagrams.  1200 B
        /// fits inside the IPv6 minimum link MTU (1280 B) after IPv6 + UDP
        /// header overhead and survives PPPoE + IPsec ESP tunnels that shrink
        /// the safe envelope below the 1500 B Ethernet baseline.  Sends larger
        /// than this are rejected up front rather than fragmenting at the IP
        /// layer — UDP fragmentation is the canonical cause of mysterious
        /// one-way packet loss on mobile / carrier-grade NAT, and a 1400 B
        /// cap that worked on plain Ethernet silently broke under those
        /// real-world transports.
        /// </summary>
        public const int DefaultMaxDatagramSize = 1200;

        /// <summary>Default DNS resolution timeout. Captive portals can hold the OS
        /// resolver for tens of seconds; 3s is enough for a healthy network and
        /// short enough to fail fast off it.</summary>
        public static readonly TimeSpan DefaultDnsTimeout = TimeSpan.FromSeconds(3);

        // Cached resolved address for the connection lifetime.  DNS is resolved
        // once at Connect() and reused across socket reconstructions.  Cache is
        // cleared on Dispose() but otherwise persists (UDP DNS TTL is irrelevant
        // for an already-bound session).
        private IPAddress    _cachedAddress;
        private AddressFamily _cachedFamily;

        // ── Runtime state ──────────────────────────────────────────────────────
        // _socket is volatile so that a Disconnect() on one thread is immediately
        // visible to readers on the network thread without an explicit fence.
        // Readers must still snapshot the field locally — see class header.
        //
       // _remoteEndPoint, _localEndPoint and _socketFamily are written by the
        // main-thread Connect() and read by the network thread inside the
        // syscalls below.  EndPoint is a reference type, so torn reads cannot
        // produce a partially-initialised object — but reordering across the
        // _socket publish is still possible on weak memory models (ARM /
        // IL2CPP).  Volatile.Read / Volatile.Write below pair with
        // Volatile.Write(_socket, …) inside Connect() so a thread that observes
        // the new socket also observes the matching endpoint state.
        private volatile Socket _socket;
        private EndPoint        _remoteEndPoint;
        private int             _socketFamilyRaw = (int)AddressFamily.InterNetwork; // reflects the active socket
        private volatile bool   _disposed;
        // Populated by Connect() after the socket is bound.
        // Reflects the actual outgoing source IP (discovered via a routing probe),
        // not 0.0.0.0 that would result from Bind(IPAddress.Any, 0).
        private System.Net.IPEndPoint _localEndPoint;

        private AddressFamily SocketFamily
        {
            get => (AddressFamily)Volatile.Read(ref _socketFamilyRaw);
            set => Volatile.Write(ref _socketFamilyRaw, (int)value);
        }

        // ── Properties ─────────────────────────────────────────────────────────
        /// <inheritdoc/>
        /// <remarks>
        /// Advisory only.  The two volatile reads (<c>_socket</c> and
        /// <c>_disposed</c>) are individually atomic but not composed atomically,
        /// so a concurrent <see cref="Disconnect"/> or <see cref="Dispose"/> can
        /// race between them.  Callers must not use this property as a
        /// synchronisation gate; use the exception-safe snapshot idiom in the hot
        /// send/receive paths instead.
        /// </remarks>
        public override bool IsConnected => _socket != null && !_disposed;

        /// <summary>
        /// The local source endpoint (IP + ephemeral port) the OS assigned when
        /// the socket was bound. Populated after <see cref="Connect"/> is called.
        /// The IP reflects the actual outgoing interface (not 0.0.0.0).
        /// Returns <see langword="null"/> before <see cref="Connect"/>.
        /// </summary>
        public override System.Net.IPEndPoint LocalEndPoint => Volatile.Read(ref _localEndPoint);

        // ── Construction ───────────────────────────────────────────────────────

        /// <param name="host">Remote hostname or IP address (e.g. "127.0.0.1").</param>
        /// <param name="port">Remote UDP port (1–65535).</param>
        /// <param name="sendBufferBytes">SO_SNDBUF size in bytes (default 4 KiB).</param>
        /// <param name="receiveBufferBytes">SO_RCVBUF size in bytes (default 4 KiB).</param>
        /// <param name="dnsTimeout">
        /// Maximum time to wait for DNS resolution. <see langword="null"/> uses
        /// <see cref="DefaultDnsTimeout"/> (3 seconds).  When the timeout
        /// elapses, <see cref="Connect"/> throws <see cref="TimeoutException"/>
        /// rather than blocking the caller indefinitely.
        /// </param>
        public UdpTransport(
            string host,
            int    port,
            int    sendBufferBytes    = 4_096,
            int    receiveBufferBytes = 4_096,
            TimeSpan? dnsTimeout      = null,
            int    maxDatagramSize    = DefaultMaxDatagramSize)
        {
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("Host must not be null or whitespace.", nameof(host));
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), "Port must be in range 1–65535.");
            var effectiveTimeout = dnsTimeout ?? DefaultDnsTimeout;
            if (effectiveTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(dnsTimeout), "DNS timeout must be positive.");
            if (maxDatagramSize <= 0 || maxDatagramSize > 65_507)
                throw new ArgumentOutOfRangeException(nameof(maxDatagramSize),
                    "maxDatagramSize must be in range 1–65507 (UDP payload limit).");

            _host               = host;
            _port               = port;
            _sendBufferBytes    = sendBufferBytes;
            _receiveBufferBytes = receiveBufferBytes;
            _dnsTimeout         = effectiveTimeout;
            _maxDatagramSize    = maxDatagramSize;
        }

        /// <summary>
        /// Effective per-call upper bound on outgoing datagram payload size.
        /// </summary>
        public int MaxDatagramSize => _maxDatagramSize;

        // ── NetworkTransport ───────────────────────────────────────────────────

        /// <inheritdoc/>
        public override void Connect()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UdpTransport));

            // Resolve once per UdpTransport lifetime.  Reusing the cached IP across
            // reconnects keeps the captive-portal stall (where Dns.GetHostAddresses
            // can block 5–30s) bounded to the very first Connect of this instance.
            // The cache is cleared on Dispose so a freshly constructed transport
            // always re-resolves.
            IPAddress resolved = _cachedAddress;
            AddressFamily family = _cachedFamily;

            if (resolved == null)
            {
                IPAddress[] addresses;
                try
                {
                    addresses = ResolveHostAddresses(_host, _dnsTimeout);
                }
                catch (AggregateException ae) when (ae.InnerException != null)
                {
                    // Async DNS task surfaces failures wrapped in AggregateException;
                    // unwrap to preserve the SocketException type that callers expect.
                    throw ae.InnerException;
                }

                // Prefer IPv4, but fall back to IPv6 if no IPv4 address is available.
                // Previous code threw InvalidOperationException on IPv6-only hosts.
                family = AddressFamily.InterNetwork;
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

                _cachedAddress = resolved;
                _cachedFamily  = family;
            }

            // Volatile.Write so that a thread that subsequently observes the
            // freshly-published _socket also observes the matching remote
            // endpoint state — paired with Volatile.Read in Send / Receive.
            Volatile.Write(ref _remoteEndPoint, new IPEndPoint(resolved, _port));

            // Construct, configure and bind under a Dispose-on-failure guard.
            // Any exception thrown by the property setters (setsockopt) or by
            // Bind() must not leak the underlying OS file descriptor.  The
            // "transfer of ownership" pattern — assign to local, commit by
            // nulling the local — is the standard idiom for two-phase
            // construction of disposable resources in .NET.
            Socket pending = null;
            try
            {
                pending = new Socket(family, SocketType.Dgram, ProtocolType.Udp)
                {
                    SendBufferSize    = _sendBufferBytes,
                    ReceiveBufferSize = _receiveBufferBytes,
                    Blocking          = false
                };

                // Bind to any local address/port — the OS assigns an ephemeral source port.
                var bindAny = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
                pending.Bind(new IPEndPoint(bindAny, 0));

                // Commit ownership atomically.  Once _socket holds the reference
                // the finally block must not dispose it.  Family is published
                // BEFORE the socket itself so a reader that observes the new
                // socket also sees the matching family — Volatile.Write on
                // _socket then provides the release fence.
                SocketFamily = family;
                _socket      = pending;
                pending      = null;
            }
            finally
            {
                // If we did not reach the commit point above, pending still owns
                // the half-initialised socket and must be disposed.  Otherwise
                // pending was nulled and this is a no-op.
                pending?.Dispose();
            }

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
                    Volatile.Write(ref _localEndPoint, new IPEndPoint(probeLocal.Address, boundPort));
                }
                else
                {
                    // Should not happen on any supported platform, but guard defensively.
                    UnityEngine.Debug.LogWarning(
                        "[RTMPE] UdpTransport: routing probe returned a null LocalEndPoint " +
                        "after connect — falling back to loopback as source IP. " +
                        "HandshakeInit AAD will use loopback; the handshake will fail " +
                        "when connecting to a non-loopback server.");
                    Volatile.Write(ref _localEndPoint, new IPEndPoint(loopback, boundPort));
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
                Volatile.Write(ref _localEndPoint, new IPEndPoint(loopback, boundPort));
            }
        }

        /// <inheritdoc/>
        public override void Disconnect()
        {
            // Dispose() calls Close() internally; calling both is redundant and may throw.
            // Disconnect is idempotent — concurrent callers race only to null the
            // field; whoever wins disposes, the loser sees null and returns.
            // The network thread parked in ReceiveFrom/Poll on the doomed socket
            // unblocks with ObjectDisposedException, which Receive/Poll catch
            // and convert into a benign zero-return.
            var s = System.Threading.Interlocked.Exchange(ref _socket, null);
            s?.Dispose();
        }

        /// <inheritdoc/>
        public override void Send(byte[] data)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UdpTransport));
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            // Reject oversize datagrams synchronously at the call site.  Without
            // this check the kernel surfaces EMSGSIZE asynchronously on the I/O
            // thread, which is reported through OnError far away from the bad
            // caller — much harder to diagnose.
            if (data.Length > _maxDatagramSize)
                throw new ArgumentException(
                    $"Datagram length {data.Length} exceeds MaxDatagramSize ({_maxDatagramSize}). " +
                    "Fragment at the application layer instead of relying on IP fragmentation.",
                    nameof(data));

            // Snapshot the field; if Disconnect() races with us, the local
            // reference keeps the socket alive for the duration of the syscall
            // (Dispose() releases the OS handle but the GC root is still held).
            var s = _socket;
            if (s == null)
                throw new InvalidOperationException("Transport is not connected. Call Connect() first.");
            var remote = Volatile.Read(ref _remoteEndPoint);

            try
            {
                // `Socket.SendTo` for UDP returns the number of bytes accepted by the
                // kernel.  For datagram sockets this is either the full payload
                // length or a SocketException is thrown (EMSGSIZE for oversize,
                // ENOBUFS for send-buffer exhaustion, etc.).  Microsoft's contract
                // does not formally permit a partial return, but this check is
                // cheap and catches platform quirks (e.g. Mono/IL2CPP edge cases)
                // before the symptom manifests as mysteriously dropped packets.
                int sent = s.SendTo(data, remote);
                if (sent != data.Length)
                {
                    throw new SocketException((int)SocketError.MessageSize);
                }
            }
            catch (ObjectDisposedException)
            {
                // Disconnect() raced with us. Treat as a transport-closed signal —
                // the calling I/O loop is responsible for noticing and exiting.
                throw new InvalidOperationException("Transport was disconnected during Send.");
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
            if (_disposed)
                throw new ObjectDisposedException(nameof(UdpTransport));
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    $"offset={offset}, count={count}, buffer.Length={buffer.Length}");
            if (count > _maxDatagramSize)
                throw new ArgumentException(
                    $"Datagram length {count} exceeds MaxDatagramSize ({_maxDatagramSize}). " +
                    "Fragment at the application layer instead of relying on IP fragmentation.",
                    nameof(count));

            var s = _socket;
            if (s == null)
                throw new InvalidOperationException("Transport is not connected. Call Connect() first.");
            var remote = Volatile.Read(ref _remoteEndPoint);

            try
            {
                // See the note in Send(byte[]) for why the return value is asserted.
                int sent = s.SendTo(buffer, offset, count, SocketFlags.None, remote);
                if (sent != count)
                {
                    throw new SocketException((int)SocketError.MessageSize);
                }
            }
            catch (ObjectDisposedException)
            {
                throw new InvalidOperationException("Transport was disconnected during Send.");
            }
        }

        /// <inheritdoc/>
        public override int Receive(byte[] buffer)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UdpTransport));

            // Snapshot — see class header for the rationale.
            var s = _socket;
            if (s == null) return 0;

            try
            {
                // The EndPoint type passed to ReceiveFrom must match the
                // socket's address family.  Using IPAddress.Any (IPv4) on an IPv6
                // socket throws ArgumentException and crashes the receive loop.
                EndPoint ep = SocketFamily == AddressFamily.InterNetworkV6
                    ? new IPEndPoint(IPAddress.IPv6Any, 0)
                    : new IPEndPoint(IPAddress.Any,     0);
                int count = s.ReceiveFrom(buffer, ref ep);

                // Source pinning: drop datagrams whose source endpoint does
                // not match the registered remote.  AEAD will already reject
                // forged ciphertext, but Poly1305 verification is the most
                // expensive part of the receive path; an off-path attacker
                // who blasts random datagrams at the client port can pin a
                // mobile CPU at 100% while the AEAD layer faithfully rejects
                // every one.  Filtering by source first turns that
                // amplification vector into a benign no-op.
                var expected = Volatile.Read(ref _remoteEndPoint) as IPEndPoint;
                if (expected != null && ep is IPEndPoint actual
                    && !EndpointMatches(expected, actual))
                {
                    // Drop and tell the caller "no data" — the receive loop
                    // checks _running on the next iteration so a hostile
                    // flood does not starve graceful shutdown.
                    return 0;
                }

                return count;
            }
            catch (ObjectDisposedException)
            {
                // Disconnect() ran while we were parked in ReceiveFrom.  This is
                // the expected shutdown path — return 0 so the I/O loop sees
                // "no data" and notices _running == false on the next iteration.
                return 0;
            }
            catch (SocketException ex)
                when (ex.SocketErrorCode == SocketError.WouldBlock          // No data ready (Linux / macOS)
                   || ex.SocketErrorCode == SocketError.ConnectionReset     // ICMP port-unreachable (Windows)
                   || ex.SocketErrorCode == SocketError.ConnectionRefused   // ICMP port-unreachable (Linux)
                   || ex.SocketErrorCode == SocketError.MessageSize         // Oversized datagram — drop, keep receiving
                   || ex.SocketErrorCode == SocketError.NetworkReset        // Transient route change
                   || ex.SocketErrorCode == SocketError.HostUnreachable     // ICMP host-unreachable
                   || ex.SocketErrorCode == SocketError.NetworkUnreachable  // ICMP network-unreachable
                   || ex.SocketErrorCode == SocketError.OperationAborted    // Socket closed by another thread mid-syscall
                   || ex.SocketErrorCode == SocketError.Interrupted)        // EINTR — close-induced wake-up on POSIX
            {
                // All of these are benign / transient at the UDP layer: we
                // lose one datagram but the receive loop must keep running.
                // OperationAborted/Interrupted cover the case where Disconnect()
                // closes the socket without disposing the wrapper — the kernel
                // wakes ReceiveFrom with WSA_OPERATION_ABORTED (Windows) or
                // EBADF/EINTR (POSIX) and we treat it as a clean shutdown.
                return 0;
            }
        }

        /// <inheritdoc/>
        public override bool Poll(int microSeconds)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UdpTransport));
            var s = _socket;
            if (s == null) return false;
            try
            {
                return s.Poll(microSeconds, SelectMode.SelectRead);
            }
            catch (ObjectDisposedException)
            {
                // Disconnect() raced with the poll. Returning false makes the
                // caller skip Receive() this iteration and check _running.
                return false;
            }
            catch (SocketException ex)
                when (ex.SocketErrorCode == SocketError.OperationAborted
                   || ex.SocketErrorCode == SocketError.Interrupted
                   || ex.SocketErrorCode == SocketError.NotSocket)
            {
                // Same shutdown story as Receive() — kernel woke us because the
                // descriptor was closed.  Treat as "no data; check _running".
                return false;
            }
        }

        /// <inheritdoc/>
        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Drop the cached address so a freshly constructed transport always
            // re-resolves against the current network state.
            _cachedAddress = null;
            Disconnect();
        }

        /// <summary>
        /// Equality predicate for inbound source-IP pinning.  Compares port
        /// and address bytes directly; <see cref="IPEndPoint.Equals(object)"/>
        /// performs the same comparison but allocates a boxing
        /// <see cref="object"/> reference because the override is on the base
        /// type — calling it on the receive hot path is a small but real
        /// allocation per datagram, so we open-code it here.
        /// </summary>
        private static bool EndpointMatches(IPEndPoint expected, IPEndPoint actual)
        {
            if (expected.Port != actual.Port) return false;
            // IPAddress.Equals on the same family is a fast bytewise
            // comparison; cross-family endpoints (IPv4 vs IPv6) are never
            // equal under our routing model so the check is sufficient.
            return expected.Address.Equals(actual.Address);
        }

        // ── Bounded DNS resolution ──────────────────────────────────────────────

        /// <summary>
        /// Resolve <paramref name="host"/> via the async resolver with a hard
        /// upper bound on total wall-clock time.  The legacy synchronous
        /// <see cref="Dns.GetHostAddresses(string)"/> can block the calling
        /// thread for 5–30 seconds on captive portals or misconfigured DNS;
        /// because the network thread also drives the I/O loop, that stall
        /// directly translates into a frozen client.
        /// </summary>
        /// <exception cref="TimeoutException">
        /// Thrown when DNS does not return within <paramref name="timeout"/>.
        /// </exception>
        private static IPAddress[] ResolveHostAddresses(string host, TimeSpan timeout)
        {
            // A fast-path for literal IPs avoids a system DNS call entirely —
            // important on offline / firewalled machines where even loopback
            // resolution would time out unnecessarily.
            if (IPAddress.TryParse(host, out var literal))
                return new[] { literal };

            // Dns.GetHostAddressesAsync ignores its CancellationToken parameter
            // on .NET Standard 2.1 (the cancel hook landed in .NET 6).  We
            // therefore enforce the bound with Task.Wait(timeout): if it fires
            // first we throw TimeoutException; the underlying resolver task
            // continues on the thread pool and will be reaped once the OS
            // resolver call returns or the process exits.  This is acceptable
            // because the caller never sees the leaked task and the OS cap on
            // concurrent in-flight resolver calls is effectively unlimited.
            var resolveTask = Dns.GetHostAddressesAsync(host);
            if (!resolveTask.Wait(timeout))
            {
                throw new TimeoutException(
                    $"DNS resolution for '{host}' did not complete within {timeout.TotalMilliseconds:0} ms.");
            }
            return resolveTask.Result;
        }
    }
}
