// RTMPE SDK — Runtime/Core/Rpc/OwnershipTransfer.cs
//
// Pure static handler for the OwnershipTransfer RPC pair (request + response).
//
// Threat model — why this is its own file:
//   The authorisation predicate (<see cref="IsAuthorized"/>) decides whether
//   an inbound TransferOwnership grant is honoured.  Five legitimate paths
//   exist: master-release, initial-assignment, self-initiated, host-
//   authorised, and server-relayed — any wrongly-accepted grant is an
//   ownership-flip
//   vulnerability against any spawned object in the room.  Isolating these
//   methods into a side-effect-free static class lets a security review
//   read this file end-to-end without paging through 6,000 lines of
//   NetworkManager state, and makes the four authorisation paths
//   independently unit-testable.
//
// Threading:
//   All methods are pure — no instance state, no side effects beyond the
//   <see cref="SpawnManager.Ownership"/> grant + <see cref="UnityEngine.Debug"/>
//   warning. All call sites run on the Unity main thread (RPC dispatch path).
//
// Backward compatibility:
//   <c>NetworkManager.IsOwnershipTransferAuthorized</c> stays as an internal
//   instance passthrough so <c>Tier0SecurityTests</c> can keep calling it
//   directly through a NetworkManager fixture.

using System;

using UnityEngine;

using RTMPE.Rooms;
using RTMPE.Rpc;

namespace RTMPE.Core.Rpc
{
    internal static class OwnershipTransfer
    {
        /// <summary>
        /// Handle an inbound OwnershipTransfer RPC. Parses the payload,
        /// runs the four-path authorisation predicate, and applies the
        /// grant via <see cref="OwnershipManager.ApplyOwnershipGrant"/>
        /// when authorised. Drops the packet silently (logged) on any
        /// payload defect or authorisation failure.
        /// </summary>
        /// <param name="request">RPC envelope as parsed by the SDK dispatcher.</param>
        /// <param name="spawnManager">Live SpawnManager. Null = no-op.</param>
        /// <param name="localPlayerId">SDK's <c>_localPlayerId</c> (u64). Used for self-initiated path.</param>
        /// <param name="localPlayerStringId">SDK's <c>_localPlayerStringId</c>. Used for self-initiated + master-release paths.</param>
        /// <param name="isMasterClient">Live <c>NetworkManager.IsMasterClient</c> verdict.</param>
        /// <param name="roomManager">Live RoomManager. Null = no roster check possible.</param>
        public static void HandleRpc(
            RpcRequest request,
            SpawnManager spawnManager,
            ulong localPlayerId,
            string localPlayerStringId,
            bool isMasterClient,
            RoomManager roomManager)
        {
            if (spawnManager == null) return;
            if (request.Payload == null || request.Payload.Length < 10) return;

            var p = request.Payload;
            ulong objectId =
                  (ulong)p[0]
                | ((ulong)p[1] << 8)
                | ((ulong)p[2] << 16)
                | ((ulong)p[3] << 24)
                | ((ulong)p[4] << 32)
                | ((ulong)p[5] << 40)
                | ((ulong)p[6] << 48)
                | ((ulong)p[7] << 56);
            ushort ownerLen = (ushort)(p[8] | (p[9] << 8));
            if (request.Payload.Length < 10 + ownerLen) return;

            // Strict UTF-8 decoder — consistent with SpawnPacketParser and JwtValidator
            // throughout the SDK.  The lenient System.Text.Encoding.UTF8 decoder
            // silently replaces invalid bytes with U+FFFD, so two distinct on-wire byte
            // sequences could decode to the same string, defeating the roster-membership
            // identity check below (SDKR-01).  A strict decoder throws on any malformed
            // byte so the packet is treated as a protocol violation and dropped.
            string newOwner;
            if (ownerLen > 0)
            {
                try
                {
                    newOwner = new System.Text.UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false,
                        throwOnInvalidBytes: true
                    ).GetString(request.Payload, 10, ownerLen);
                }
                catch (System.Exception)
                {
                    return; // malformed UTF-8 — treat as protocol violation, drop silently
                }
            }
            else
            {
                newOwner = string.Empty;
            }

            if (!IsAuthorized(
                    objectId, newOwner, request.SenderId,
                    spawnManager, localPlayerId, localPlayerStringId,
                    isMasterClient, roomManager))
            {
                Debug.LogWarning(
                    $"[RTMPE] TransferOwnership rejected: sender {LogRedaction.Redact(request.SenderId)} " +
                    $"is not authorised to transfer object {objectId} to new owner.");
                return;
            }

            // A grant authorised by anything other than our own echoed request
            // is server-attested and applied directly: the master-client and
            // initial-assignment branches are attested by replicated room state,
            // and a transfer initiated by another player (senderId != ours) was
            // authorised by the gateway before it reached us.  Only our OWN
            // request echoed back (senderId == ours) is withheld from direct
            // apply so ApplyOwnershipGrant can additionally match it against the
            // (objectId, newOwner) tuple we actually issued — the originator's
            // defence against a peer crafting a grant stamped with our id.
            bool serverAttested = request.SenderId != localPlayerId
                || isMasterClient
                || string.IsNullOrEmpty(spawnManager.Registry.Get(objectId)?.OwnerPlayerId);
            spawnManager.Ownership.ApplyOwnershipGrant(objectId, newOwner, serverAttested);
        }

        /// <summary>
        /// Authorisation predicate for an inbound TransferOwnership grant.
        /// Returns <see langword="false"/> on any condition the SDK cannot
        /// positively justify so a captured-and-replayed (or peer-forged)
        /// grant cannot flip an arbitrary object.
        ///
        /// <para>The five legitimate paths are:</para>
        /// <list type="number">
        ///  <item>Master-release: empty <paramref name="newOwner"/> AND (local is current owner OR local is master).</item>
        ///  <item>Initial assignment: target had no owner AND <paramref name="newOwner"/> is a roster member.</item>
        ///  <item>Self-initiated: <paramref name="senderId"/> == localPlayerId AND local is current owner.</item>
        ///  <item>Host-authorised: local is master client AND <paramref name="newOwner"/> is a roster member.</item>
        ///  <item>Server-relayed: <paramref name="senderId"/> is a non-zero id other than ours (another player's transfer, gateway-authorised before it is relayed) AND <paramref name="newOwner"/> is a roster member.</item>
        /// </list>
        /// </summary>
        public static bool IsAuthorized(
            ulong objectId,
            string newOwner,
            ulong senderId,
            SpawnManager spawnManager,
            ulong localPlayerId,
            string localPlayerStringId,
            bool isMasterClient,
            RoomManager roomManager)
        {
            if (spawnManager == null) return false;

            var target = spawnManager.Registry.Get(objectId);
            if (target == null) return false;

            // Empty new-owner is the wire encoding for "release ownership".
            // We allow it only when the local player is the current owner
            // (we released it) or the local player is the master client.
            if (string.IsNullOrEmpty(newOwner))
            {
                bool localIsOwner = !string.IsNullOrEmpty(localPlayerStringId)
                                 && target.OwnerPlayerId == localPlayerStringId;
                return localIsOwner || isMasterClient;
            }

            // Initial assignment from "no owner" to a roster member is
            // permitted — the gateway authoritatively assigns ownership at
            // spawn time and may emit a separate Transfer to bind it.
            bool initialAssignment = string.IsNullOrEmpty(target.OwnerPlayerId)
                                  && IsRosterMember(roomManager, newOwner);

            // Self-initiated transfer: the local player asked the gateway
            // to relay this grant on their behalf.  We tolerate the gateway
            // echoing the request back via the same RPC channel.
            bool selfInitiated = senderId != 0UL
                              && senderId == localPlayerId
                              && !string.IsNullOrEmpty(localPlayerStringId)
                              && target.OwnerPlayerId == localPlayerStringId;

            // Host-authorised reassignment: the master client may grant
            // ownership to any roster member.  The current client only
            // applies the change if it can verify both the new owner and
            // that the local view of the master client is unchanged from
            // the gateway's broadcast.
            bool hostAuthorised = isMasterClient && IsRosterMember(roomManager, newOwner);

            // Server-relayed transfer: a grant initiated by ANOTHER player
            // (a non-zero wire senderId that is not our own id) reaches us only
            // after the gateway has authorised it — it validates the transferor
            // against the object's recorded owner using the authenticated
            // session id and confirms newOwner is a room member before relaying
            // (forwarder.rs object_authority_ok + transfer_new_owner_ok), and our
            // inbound is gateway-authenticated with replay protection, so a peer
            // can neither forge nor replay a grant onto us.  The transferee and
            // uninvolved members cannot map the remote sender to the owner
            // locally (the roster carries no numeric id), so the four
            // self-verifiable paths above reject a legitimate remote transfer —
            // which is exactly what leaves ownership diverged across the room.
            // Honouring a gateway-attested transfer to a live roster member lets
            // every client converge to the server's decision.
            bool serverRelayed = senderId != 0UL
                              && senderId != localPlayerId
                              && IsRosterMember(roomManager, newOwner);

            return initialAssignment || selfInitiated || hostAuthorised || serverRelayed;
        }

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="playerId"/> is
        /// present in <see cref="RoomManager.CurrentRoom"/>'s roster.  Empty
        /// or null id always returns <see langword="false"/>.
        /// </summary>
        public static bool IsRosterMember(RoomManager roomManager, string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return false;
            var room = roomManager?.CurrentRoom;
            if (room == null) return false;
            var roster = room.Players;
            if (roster == null) return false;
            for (int i = 0; i < roster.Length; i++)
            {
                if (roster[i] != null && roster[i].PlayerId == playerId) return true;
            }
            return false;
        }

        /// <summary>
        /// Process the server's response to a client-initiated TransferOwnership
        /// RPC.  On success the server has already broadcast the grant via
        /// <see cref="HandleRpc"/>.  Forgery guard: an attacker who guesses or
        /// replays a request_id can otherwise inject a fake "success" the SDK
        /// would log as a legitimate transfer; the OwnershipManager tracks
        /// the ids it actually issued and drops anything outside that set.
        /// </summary>
        public static void HandleResponse(RpcResponse response, SpawnManager spawnManager)
        {
            var ownership = spawnManager?.Ownership;
            if (ownership != null && !ownership.TryAcknowledgeResponse(response.RequestId))
                return;

            if (!response.Success)
            {
                Debug.LogWarning(
                    $"[RTMPE] Ownership transfer request {response.RequestId} " +
                    $"rejected by server (error code: {response.ErrorCode}).");
            }
        }
    }
}
