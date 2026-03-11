// RTMPE SDK — Runtime/Infrastructure/Transport/NetworkTransport.cs
//
// Abstract base for all network transports.
// Current implementations: UdpTransport (Week 8), KcpTransport (Week 10+).
//
// All methods are invoked from the RTMPE background network thread and must
// be internally thread-safe. Do NOT call Unity APIs from implementations.

using System;

namespace RTMPE.Transport
{
    /// <summary>
    /// Abstract base class for RTMPE network transports.
    /// Provides a unified socket interface across UDP (Week 8) and KCP (Week 10+).
    /// </summary>
    public abstract class NetworkTransport : IDisposable
    {
        /// <summary>True while the underlying socket is open and ready for I/O.</summary>
        public abstract bool IsConnected { get; }

        /// <summary>
        /// Open the socket and connect to the configured remote endpoint.
        /// Called once from the network background thread before the I/O loop begins.
        /// </summary>
        /// <exception cref="System.Net.Sockets.SocketException">If the socket cannot be created or bound.</exception>
        /// <exception cref="InvalidOperationException">If the transport has been disposed.</exception>
        public abstract void Connect();

        /// <summary>
        /// Close the socket. Safe to call multiple times; subsequent calls are no-ops.
        /// Called from the network background thread when the loop exits.
        /// </summary>
        public abstract void Disconnect();

        /// <summary>
        /// Send all bytes in <paramref name="data"/> to the remote endpoint.
        /// The array is owned by the caller; the implementation must not retain a reference.
        /// </summary>
        /// <exception cref="InvalidOperationException">If not connected.</exception>
        public abstract void Send(byte[] data);

        /// <summary>
        /// Non-blocking receive into <paramref name="buffer"/>.
        /// Returns the number of bytes written, or 0 if no datagram is available.
        /// Must NOT block — return 0 immediately when the socket has no data.
        /// </summary>
        public abstract int Receive(byte[] buffer);

        /// <summary>
        /// Non-blocking readability poll.
        /// Returns <see langword="true"/> if at least one datagram is waiting to be read.
        /// <paramref name="microSeconds"/> sets the timeout (0 = instant; non-blocking).
        /// </summary>
        public abstract bool Poll(int microSeconds);

        /// <inheritdoc/>
        public abstract void Dispose();
    }
}
