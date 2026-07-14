// RTMPE SDK — Runtime/Rooms/RoomInfo.cs
//
// Immutable snapshot of a room's state.
// Mirrors the GetRoomResponse / RoomSummary messages in room.proto.

using System;
using System.Collections.Generic;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Read-only snapshot of a room.
    /// Populated from CreateRoom response, JoinRoom response, or ListRooms.
    /// </summary>
    public sealed class RoomInfo
    {
        /// <summary>Server-assigned room UUID.</summary>
        public string RoomId { get; }

        /// <summary>6-character human-readable join code (e.g. "XKCD42").</summary>
        public string RoomCode { get; }

        /// <summary>Display name of the room.</summary>
        public string Name { get; }

        /// <summary>Room lifecycle state: "waiting", "playing", or "finished".</summary>
        public string State { get; }

        /// <summary>Current number of players in the room.</summary>
        public int PlayerCount { get; }

        /// <summary>Maximum allowed players (1–100).</summary>
        public int MaxPlayers { get; }

        /// <summary>Whether the room is publicly listed.</summary>
        public bool IsPublic { get; }

        /// <summary>Player roster snapshot. May be empty for list responses.</summary>
        public PlayerInfo[] Players { get; }

        /// <summary>
        /// The room's <c>custom_properties</c> JSONB column mirrored as a
        /// read-only map.  Always non-null; an empty map is the default state.
        /// Callers MUST NOT mutate the underlying collection — the snapshot is
        /// immutable by contract.
        /// </summary>
        public IReadOnlyDictionary<string, PropertyValue> Properties { get; }

        /// <summary>
        /// Monotonic version counter advanced by the server on every accepted
        /// <c>RoomPropertyUpdate</c>.  Used for optimistic-concurrency control
        /// when the local player issues an update.
        /// </summary>
        public int PropertiesVersion { get; }

        /// <summary>
        /// PlayerId of the current master client (room host), or empty string
        /// when the roster has no host set.  Derived from <see cref="Players"/>
        /// so no dedicated database column is required — the server
        /// guarantees exactly one host per non-empty room via the <c>is_host</c>
        /// column on <c>room_players</c>.
        /// </summary>
        /// <remarks>
        /// Implemented as a manual scan rather than
        /// <c>Players.FirstOrDefault(p =&gt; p?.IsHost == true)</c> because
        /// LINQ's enumerator + closure pair allocates ~80 B per getter
        /// invocation and the property is on the hot path
        /// (<c>NetworkManager.IsMasterClient</c> reads it from UI / gameplay
        /// code that may poll every frame).
        /// </remarks>
        public string MasterId
        {
            get
            {
                var players = Players;
                if (players == null) return string.Empty;
                for (int i = 0; i < players.Length; i++)
                {
                    var p = players[i];
                    if (p != null && p.IsHost) return p.PlayerId;
                }
                return string.Empty;
            }
        }

        /// <summary>
        /// The authoritative scene currently loaded by the room, read from the
        /// reserved <c>__scene</c> custom property.  Empty string when no
        /// scene has been set.  Clients MUST treat this as read-only — the
        /// room host drives scene changes via
        /// <see cref="RoomManager.SetRoomProperties"/> (or the higher-level
        /// <c>NetworkSceneManager</c> façade).
        /// </summary>
        public string CurrentScene
        {
            get
            {
                if (Properties != null
                    && Properties.TryGetValue(ReservedPropertyKeys.Scene, out var v)
                    && v.Type == PropertyType.String)
                {
                    return v.AsString();
                }
                return string.Empty;
            }
        }

        public RoomInfo(
            string roomId,
            string roomCode,
            string name,
            string state,
            int    playerCount,
            int    maxPlayers,
            bool   isPublic,
            PlayerInfo[] players = null,
            IReadOnlyDictionary<string, PropertyValue> properties = null,
            int    propertiesVersion = 0)
        {
            RoomId            = roomId ?? string.Empty;
            RoomCode          = roomCode ?? string.Empty;
            Name              = name ?? string.Empty;
            State             = state ?? string.Empty;
            PlayerCount       = playerCount;
            MaxPlayers        = maxPlayers;
            IsPublic          = isPublic;
            // Defensive copy of the player roster.  The constructor caller
            // (RoomManager / RoomPacketParser) builds the array imperatively
            // during parse; without this copy a future caller that re-uses
            // its scratch array across packets would silently mutate the
            // RoomInfo snapshot — and the snapshot is supposed to be
            // immutable for its entire lifetime.  An empty roster reuses
            // the canonical Array.Empty&lt;T&gt;() singleton to avoid the copy.
            Players           = (players != null && players.Length > 0)
                ? (PlayerInfo[])players.Clone()
                : Array.Empty<PlayerInfo>();
            // Defensive copy: an IReadOnlyDictionary surface does not prevent
            // the caller from holding a reference to the underlying mutable
            // Dictionary and mutating it after construction.  Copying here
            // guarantees the snapshot is truly immutable for the SDK's
            // lifetime contract.  Skipped for the canonical EmptyProperties
            // singleton and for already-copied readonly dictionaries to avoid
            // redundant allocation.
            Properties        = FreezeProperties(properties);
            PropertiesVersion = propertiesVersion;
        }

        /// <summary>
        /// Returns an immutable snapshot of <paramref name="source"/>.  Reuses
        /// the shared empty singleton when the input is null or empty, and
        /// defensively copies all other inputs so the returned reference is
        /// safe against external mutation of the caller's dictionary.
        /// </summary>
        internal static IReadOnlyDictionary<string, PropertyValue> FreezeProperties(
            IReadOnlyDictionary<string, PropertyValue> source)
        {
            if (source == null || source.Count == 0) return EmptyProperties;
            var copy = new Dictionary<string, PropertyValue>(source.Count);
            foreach (var kv in source) copy[kv.Key] = kv.Value;
            return copy;
        }

        /// <summary>
        /// Returns a new <see cref="RoomInfo"/> identical to this one but
        /// with the supplied <paramref name="properties"/> and
        /// <paramref name="version"/>.  Used by the RoomManager to apply
        /// <c>room_properties_updated</c> broadcasts without mutating the
        /// previous snapshot.
        /// </summary>
        public RoomInfo WithProperties(IReadOnlyDictionary<string, PropertyValue> properties, int version)
            => new RoomInfo(
                RoomId, RoomCode, Name, State, PlayerCount, MaxPlayers,
                IsPublic, Players, properties, version);

        /// <summary>
        /// Returns a new <see cref="RoomInfo"/> with the supplied roster.
        /// Provided for symmetry with <see cref="WithProperties"/> so the
        /// RoomManager can apply player-level changes without duplicating
        /// the rest of the snapshot.
        /// </summary>
        public RoomInfo WithPlayers(PlayerInfo[] players)
            => new RoomInfo(
                RoomId, RoomCode, Name, State, PlayerCount, MaxPlayers,
                IsPublic, players, Properties, PropertiesVersion);

        /// <summary>
        /// Returns a new <see cref="RoomInfo"/> whose roster is replaced by
        /// <paramref name="players"/> and whose <see cref="PlayerCount"/> is
        /// re-derived to match the new roster length.  This is the membership
        /// variant of <see cref="WithPlayers"/>: where <see cref="WithPlayers"/>
        /// preserves the count for size-preserving edits (a host-flag flip or a
        /// per-player property swap), <see cref="WithRoster"/> is used when a
        /// player joins or leaves, so the count and the roster move in lockstep
        /// and never drift — matching the construction-time invariant that
        /// <see cref="PlayerCount"/> equals <see cref="Players"/>.Length for a
        /// fully-populated room snapshot.
        /// </summary>
        public RoomInfo WithRoster(PlayerInfo[] players)
            => new RoomInfo(
                RoomId, RoomCode, Name, State,
                players?.Length ?? 0, MaxPlayers,
                IsPublic, players, Properties, PropertiesVersion);

        private static readonly IReadOnlyDictionary<string, PropertyValue> EmptyProperties
            = new Dictionary<string, PropertyValue>(0);

        public override string ToString()
            => $"Room({RoomId}, \"{Name}\", {PlayerCount}/{MaxPlayers}, {State})";
    }
}
