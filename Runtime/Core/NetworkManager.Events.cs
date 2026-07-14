// RTMPE SDK — Runtime/Core/NetworkManager.Events.cs
//
// Public events raised on the Unity main thread.
// Part of the NetworkManager partial class — see NetworkManager.cs for the
// canonical class declaration, base type, and Unity attributes.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using RTMPE.Threading;
using RTMPE.Transport;
using RTMPE.Crypto;
using RTMPE.Crypto.Internal;
using RTMPE.Protocol;
using RTMPE.Rooms;
using RTMPE.Rpc;
using RTMPE.Sync;
using RTMPE.Infrastructure.Compression;

namespace RTMPE.Core
{
    public sealed partial class NetworkManager
    {
        // ── Events ─────────────────────────────────────────────────────────────

        /// <summary>Fired when the connection state changes (previous state, new state).</summary>
        public event Action<NetworkState, NetworkState> OnStateChanged;

        /// <summary>Fired when the connection reaches <see cref="NetworkState.Connected"/>.</summary>
        public event Action OnConnected;

        /// <summary>Fired when the connection drops for any reason.</summary>
        public event Action<DisconnectReason> OnDisconnected;

        /// <summary>Fired when the connection attempt fails (timeout or transport error).</summary>
        public event Action<string> OnConnectionFailed;

        /// <summary>
        /// Fired when the bounded reconnect loop kicked off by <see cref="Reconnect"/>
        /// exhausts <see cref="NetworkSettings.maxReconnectAttempts"/> without
        /// reaching <see cref="NetworkState.Connected"/>.  Argument is the
        /// number of attempts actually made.  When this fires the manager has
        /// already transitioned back to <see cref="NetworkState.Disconnected"/>
        /// and cleared all session state — the application MUST fall back to
        /// <see cref="Connect(string)"/> with credentials to recover.
        /// </summary>
        public event Action<int> OnReconnectFailed;

        /// <summary>Fired when the local player successfully joins a room.</summary>
        [Obsolete("Use NetworkManager.Rooms.OnRoomJoined or Rooms.OnRoomCreated instead.")]
        public event Action<ulong> OnJoinedRoom;

        /// <summary>Fired when the local player leaves a room.</summary>
        [Obsolete("Use NetworkManager.Rooms.OnRoomLeft instead.")]
        public event Action<ulong> OnLeftRoom;

        /// <summary>
        /// Fired when a <see cref="PacketType.Data"/> or <see cref="PacketType.StateSync"/>
        /// packet is received. Argument is the full raw packet (header + payload).
        /// </summary>
        public event Action<byte[]> OnDataReceived;

        /// <summary>Fired on each successful heartbeat with the measured RTT in ms.</summary>
        public event Action<float> OnRttUpdated;

        /// <summary>
        /// Fired when the server acknowledges a reliable Data packet.
        /// Reserved for future reliable-delivery retransmit suppression.
        /// </summary>
        public event Action OnDataAcknowledged;

        /// <summary>
        /// Fired when, after a successful <see cref="Reconnect"/>, the SDK
        /// begins an automatic rejoin of <see cref="LastRoomId"/>.  Apps can
        /// subscribe to update UI (e.g. "Reconnecting to room…").  The follow-up
        /// outcome is observable through the existing
        /// <see cref="RoomManager.OnRoomJoined"/> / <see cref="RoomManager.OnRoomError"/>.
        /// Not fired when <see cref="NetworkSettings.autoRejoinLastRoomOnReconnect"/>
        /// is disabled or when no last-room snapshot is available.
        /// </summary>
        public event Action<string> OnAutoRejoinAttempt;

    }
}
