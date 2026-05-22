// RTMPE SDK — Runtime/Sync/NetworkVariableList.cs
//
// Synchronised List<T> for network-replicated collections (inventory items,
// active buffs, kill feeds, …).  Modelled on Unity Netcode's NetworkList<T>
// and Photon's PunRPC-driven list pattern, with the RTMPE specifics:
//  • Operates inside the existing 30 Hz NetworkVariable flush loop.
//  • Wire format encodes a delta log (Add / Insert / RemoveAt / Set / Clear)
//    for steady-state efficiency and a periodic full-sync for safety against
//    a missed delta.  Late-joiner snapshot uses the FullSync op exclusively.
//  • Per-element serialisation is delegated to a subclass hook
//    (WriteElement / ReadElement) so the same delta machinery covers
//    primitive ints, floats, vectors, strings, and (in future) any
//    INetworkSerializable.
//
// Wire format of a single payload (within the NetworkVariable value frame
// [value_len:2 LE][bytes]):
//
//  [op_count : 1 u8]
//  op record (per op_count):
//    [op : 1 u8]
//    followed by per-op fields (see ListOp).
//
//  ListOp.Add        : [elem_bytes]                           (always tail-append)
//  ListOp.Insert     : [index:2 LE ushort][elem_bytes]
//  ListOp.RemoveAt   : [index:2 LE ushort]
//  ListOp.Set        : [index:2 LE ushort][elem_bytes]
//  ListOp.Clear      : (no fields)
//  ListOp.FullSync   : [count:2 LE ushort][elem_bytes × count] (replaces the list)
//
// elem_bytes layout depends on the subclass:
//  NetworkVariableListInt      : 4-byte LE i32
//  NetworkVariableListFloat    : 4-byte LE f32
//  NetworkVariableListVector3  : 12-byte LE (x,y,z)
//  NetworkVariableListString   : 2-byte LE ushort len + UTF-8 bytes
//
// Failure handling:
//  • Out-of-range Insert / RemoveAt / Set indices are dropped at apply time
//    with a warning; the rest of the payload continues to apply.  This makes
//    the receiver tolerant to a delta that was authored against a slightly
//    newer state without crashing the gameplay layer.
//  • An Add or Insert that would push the list past its configured size
//    ceiling is dropped op-locally, so a stream of delta payloads cannot
//    grow the receiver's list without bound.
//  • The op log applies atomically.  A malformed element, a truncated
//    payload, or an unknown op reverts the list to its pre-payload contents,
//    and change notifications are dispatched only once the whole payload has
//    applied — a subscriber never observes an op the payload then discards.
//  • op_count is a single byte — at most 255 ops per flush.  When more
//    mutations queue up between two flushes, the surplus is split across
//    subsequent flushes.  When the local op log exceeds a soft cap
//    (configurable via FullSyncOpThreshold) we promote to a full-sync to
//    bound the per-flush bandwidth.
//
// Performance:
//  • Mutation methods record a single struct in _pendingOps without
//    allocating per call; the change list is reused across flushes.
//  • Serialize iterates the change log into the BinaryWriter and resets
//    the log on success.
//
// Thread safety: same as NetworkVariable<T> — Unity main-thread only for
// mutation; the network thread reads incoming bytes and dispatches to
// ApplyVariableUpdate on the main thread via MainThreadDispatcher.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Sync
{
    /// <summary>Operation kinds in the NetworkVariableList delta log.</summary>
    internal enum ListOp : byte
    {
        Add      = 0x01,
        Insert   = 0x02,
        RemoveAt = 0x03,
        Set      = 0x04,
        Clear    = 0x05,
        FullSync = 0x06,
    }

    /// <summary>
    /// Abstract base for all NetworkVariableList&lt;T&gt; specialisations.
    /// Concrete subclasses (one per element type) provide
    /// <see cref="WriteElement"/> and <see cref="ReadElement"/>.
    /// </summary>
    /// <typeparam name="T">
    /// Element type.  No constraint at this layer; concrete subclasses
    /// constrain to the supported wire types.
    /// </typeparam>
    public abstract class NetworkVariableList<T> : NetworkVariableBase
    {
        // ── Local state ─────────────────────────────────────────────────────────

        private readonly List<T> _items = new List<T>();

        // Pending op log.  Cleared after a successful Serialize.  When this list
        // grows past FullSyncOpThreshold we collapse it into a single FullSync
        // op to bound per-flush bandwidth and op-count overflow risk.
        private readonly List<PendingOp> _pendingOps = new List<PendingOp>();

        // Reusable scratch for the atomic apply path in Deserialize.
        // _rollbackBuffer captures the pre-payload contents so a payload that
        // fails partway is reverted in full; _deferredChanges holds the change
        // notifications until the whole payload has been confirmed applied, so
        // a subscriber never observes an op that the payload later rolled back.
        // Both reuse their backing storage across calls — no per-payload alloc.
        private readonly List<T> _rollbackBuffer = new List<T>();
        private readonly List<NetworkVariableListChangeEvent<T>> _deferredChanges =
            new List<NetworkVariableListChangeEvent<T>>();

        /// <summary>
        /// When the local change log exceeds this threshold the next flush is
        /// promoted to a FullSync.  255 (op_count's max) is the absolute hard
        /// cap; the soft default of 32 is well below that and matches the
        /// typical per-tick mutation budget for an inventory or buff list.
        /// </summary>
        public int FullSyncOpThreshold { get; set; } = 32;

        // Apply-side cap to defend against malicious / malformed payloads.
        // No legitimate sender should ever emit more than op_count's max value.
        private const int MaxOpsPerPayload = 255;

        // ── Events ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Fires once per applied op (locally and on remote receivers).
        /// Useful for UI list widgets that prefer to react to deltas instead of
        /// rebuilding from scratch each tick.  The event delivers the list
        /// AFTER the op has been applied so callers always observe a
        /// consistent state.
        /// </summary>
        public event Action<NetworkVariableListChangeEvent<T>> OnListChanged;

        // ── Construction ───────────────────────────────────────────────────────

        protected NetworkVariableList(NetworkBehaviour owner, ushort variableId)
            : base(owner, variableId) { }

        // ── List-style API ────────────────────────────────────────────────────

        /// <summary>Number of elements currently in the list.</summary>
        public int Count => _items.Count;

        /// <summary>Read or replace an element at <paramref name="index"/>.</summary>
        public T this[int index]
        {
            get => _items[index];
            set
            {
                if ((uint)index >= (uint)_items.Count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                T previous = _items[index];
                _items[index] = value;
                EnqueueOp(new PendingOp(ListOp.Set, index, value));
                IsDirty = true;
                Raise(new NetworkVariableListChangeEvent<T>(
                    NetworkListChangeKind.Set, index, value, previous));
            }
        }

        /// <summary>Append an item to the end of the list.</summary>
        public void Add(T item)
        {
            int index = _items.Count;
            _items.Add(item);
            EnqueueOp(new PendingOp(ListOp.Add, index, item));
            IsDirty = true;
            Raise(new NetworkVariableListChangeEvent<T>(
                NetworkListChangeKind.Add, index, item, default));
        }

        /// <summary>Insert <paramref name="item"/> at <paramref name="index"/>.</summary>
        public void Insert(int index, T item)
        {
            if ((uint)index > (uint)_items.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            _items.Insert(index, item);
            EnqueueOp(new PendingOp(ListOp.Insert, index, item));
            IsDirty = true;
            Raise(new NetworkVariableListChangeEvent<T>(
                NetworkListChangeKind.Insert, index, item, default));
        }

        /// <summary>Remove the element at <paramref name="index"/>.</summary>
        public void RemoveAt(int index)
        {
            if ((uint)index >= (uint)_items.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            T removed = _items[index];
            _items.RemoveAt(index);
            EnqueueOp(new PendingOp(ListOp.RemoveAt, index, default));
            IsDirty = true;
            Raise(new NetworkVariableListChangeEvent<T>(
                NetworkListChangeKind.RemoveAt, index, default, removed));
        }

        /// <summary>
        /// Remove the first occurrence of <paramref name="item"/>.
        /// Returns <see langword="true"/> when the item was found and removed.
        /// </summary>
        public bool Remove(T item)
        {
            int idx = _items.IndexOf(item);
            if (idx < 0) return false;
            RemoveAt(idx);
            return true;
        }

        /// <summary>Empty the list.</summary>
        public void Clear()
        {
            if (_items.Count == 0 && _pendingOps.Count == 0) return;

            _items.Clear();
            // Clear collapses any prior queued ops — the receiver only needs
            // the final empty state.  A subsequent Add still queues normally.
            _pendingOps.Clear();
            EnqueueOp(new PendingOp(ListOp.Clear, 0, default));
            IsDirty = true;
            Raise(new NetworkVariableListChangeEvent<T>(
                NetworkListChangeKind.Clear, -1, default, default));
        }

        /// <summary>True when <paramref name="item"/> is in the list.</summary>
        public bool Contains(T item) => _items.IndexOf(item) >= 0;

        /// <summary>Index of the first occurrence of <paramref name="item"/>, or -1.</summary>
        public int IndexOf(T item) => _items.IndexOf(item);

        /// <summary>
        /// Iterate the list contents in insertion order.  Allocates a single
        /// enumerator per foreach loop; mutating the list during enumeration
        /// will invalidate the enumerator (same contract as <see cref="List{T}"/>).
        /// </summary>
        public List<T>.Enumerator GetEnumerator() => _items.GetEnumerator();

        // ── Op log helpers ─────────────────────────────────────────────────────

        private readonly struct PendingOp
        {
            public readonly ListOp Op;
            public readonly int    Index;
            public readonly T      Value;
            public PendingOp(ListOp op, int index, T value)
            {
                Op    = op;
                Index = index;
                Value = value;
            }
        }

        private void EnqueueOp(PendingOp op)
        {
            // Soft promotion to FullSync when the queue grows large enough.
            // Mass mutations (e.g. shuffling an inventory) collapse to a
            // single FullSync rather than 100+ tiny deltas.
            if (_pendingOps.Count >= FullSyncOpThreshold)
            {
                _pendingOps.Clear();
                _pendingOps.Add(new PendingOp(ListOp.FullSync, _items.Count, default));
                return;
            }
            _pendingOps.Add(op);
        }

        private void Raise(NetworkVariableListChangeEvent<T> evt)
        {
            try { OnListChanged?.Invoke(evt); }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[RTMPE] NetworkVariableList<{typeof(T).Name}>.OnListChanged subscriber threw " +
                    $"{ex.GetType().Name}: {ex.Message}.");
            }
        }

        // ── Resync handling ────────────────────────────────────────────────────

        /// <summary>
        /// Override the base resync hook so that a late joiner gets a single
        /// FullSync op instead of an empty delta-log.  The base class only sets
        /// IsDirty — without this override we would emit a 1-byte payload with
        /// op_count = 0, which carries no state.
        /// </summary>
        internal override void MarkDirtyForResync()
        {
            // Replace any pending ops with a single FullSync — a late joiner
            // only needs the current state, not the historical mutations.
            _pendingOps.Clear();
            _pendingOps.Add(new PendingOp(ListOp.FullSync, _items.Count, default));
            base.MarkDirtyForResync();
        }

        // ── Subclass extension points ──────────────────────────────────────────

        /// <summary>Encode <paramref name="value"/> to <paramref name="writer"/>.</summary>
        protected abstract void WriteElement(BinaryWriter writer, T value);

        /// <summary>Decode the next element from <paramref name="reader"/>.</summary>
        protected abstract T ReadElement(BinaryReader reader);

        // ── Wire serialisation ─────────────────────────────────────────────────

        public override void Serialize(BinaryWriter writer)
        {
            // Re-validate the ops queue — Clear() collapses earlier ops, but a
            // hostile or buggy subclass override could still leave it empty.
            if (_pendingOps.Count == 0)
            {
                writer.Write((byte)0);
                return;
            }

            // If any FullSync was queued, emit ONLY that op.  Receivers see the
            // current state immediately and earlier deltas would be redundant.
            for (int i = 0; i < _pendingOps.Count; i++)
            {
                if (_pendingOps[i].Op == ListOp.FullSync)
                {
                    WriteFullSync(writer);
                    _pendingOps.Clear();
                    return;
                }
            }

            // If the queued op count exceeds the wire's single-byte cap (255),
            // collapse the entire log into a single FullSync.  This bounds
            // every payload to one tick of work and guarantees the receiver
            // converges to the authoritative state without partial-delta
            // ordering ambiguity.
            if (_pendingOps.Count > MaxOpsPerPayload)
            {
                Debug.LogWarning(
                    $"[RTMPE] NetworkVariableList<{typeof(T).Name}>: pending op log " +
                    $"({_pendingOps.Count}) exceeds wire cap ({MaxOpsPerPayload}); " +
                    "promoting this flush to a FullSync.");
                WriteFullSync(writer);
                _pendingOps.Clear();
                return;
            }

            int n = _pendingOps.Count;
            writer.Write((byte)n);
            for (int i = 0; i < n; i++)
            {
                var op = _pendingOps[i];
                writer.Write((byte)op.Op);
                switch (op.Op)
                {
                    case ListOp.Add:
                        WriteElement(writer, op.Value);
                        break;
                    case ListOp.Insert:
                    case ListOp.Set:
                        writer.Write((ushort)op.Index);
                        WriteElement(writer, op.Value);
                        break;
                    case ListOp.RemoveAt:
                        writer.Write((ushort)op.Index);
                        break;
                    case ListOp.Clear:
                        // no payload
                        break;
                }
            }

            _pendingOps.Clear();
        }

        public override void Deserialize(BinaryReader reader)
        {
            byte opCount = reader.ReadByte();
            if (opCount == 0) return;

            // The configured ceiling bounds the list against an attacker who
            // streams unbounded Add/Insert deltas; resolved once per payload so
            // the per-op checks below do not repeat the settings lookup.
            int maxListSize = ResolveMaxListSize();

            // The op log applies atomically.  A malformed element, an exhausted
            // stream, or an unknown op must leave the list exactly as it was
            // before this payload — so the pre-payload contents are captured
            // here and restored on any failure, and change notifications are
            // queued rather than dispatched until the whole payload has been
            // confirmed applied.
            _rollbackBuffer.Clear();
            _rollbackBuffer.AddRange(_items);
            _deferredChanges.Clear();

            try
            {
                ApplyOpLog(reader, opCount, maxListSize);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[RTMPE] NetworkVariableList<{typeof(T).Name}>.Deserialize: payload " +
                    $"rejected ({ex.GetType().Name}: {ex.Message}).  Restoring the " +
                    "pre-payload contents so owner and receiver stay converged.");
                _items.Clear();
                _items.AddRange(_rollbackBuffer);
                _rollbackBuffer.Clear();
                _deferredChanges.Clear();
                return;
            }

            // Payload applied cleanly — release the snapshot and dispatch the
            // queued notifications now that the list is in a consistent state.
            // The queued events are moved into a local before dispatch and the
            // shared field is cleared first: a subscriber that synchronously
            // re-enters Deserialize on this same instance then operates on a
            // fresh _deferredChanges and cannot corrupt this loop's iteration.
            _rollbackBuffer.Clear();
            if (_deferredChanges.Count > 0)
            {
                var pending = _deferredChanges.ToArray();
                _deferredChanges.Clear();
                for (int i = 0; i < pending.Length; i++)
                    Raise(pending[i]);
            }
        }

        // Apply every op in the payload to _items, queueing change events into
        // _deferredChanges.  Throws on a corrupt payload (truncation, unknown
        // op, oversized FullSync) so the caller can revert atomically; benign
        // out-of-range Insert/RemoveAt/Set indices are skipped op-locally,
        // matching the receiver-tolerance contract in the file header.
        private void ApplyOpLog(BinaryReader reader, byte opCount, int maxListSize)
        {
            for (int i = 0; i < opCount; i++)
            {
                if (reader.BaseStream.Position >= reader.BaseStream.Length)
                    throw new EndOfStreamException(
                        $"op log truncated after {i} of {opCount} ops");

                ListOp op = (ListOp)reader.ReadByte();
                switch (op)
                {
                    case ListOp.Add:
                    {
                        T val = ReadElement(reader);
                        // Cumulative ceiling: an Add that would push the list
                        // past its configured maximum is skipped, so a stream
                        // of Add deltas cannot grow the receiver without bound.
                        if (_items.Count >= maxListSize)
                        {
                            Debug.LogWarning(
                                $"[RTMPE] NetworkVariableList<{typeof(T).Name}>: Add would " +
                                $"exceed the configured max {maxListSize}.  Dropping op.");
                            break;
                        }
                        int idx = _items.Count;
                        _items.Add(val);
                        _deferredChanges.Add(new NetworkVariableListChangeEvent<T>(
                            NetworkListChangeKind.Add, idx, val, default));
                        break;
                    }
                    case ListOp.Insert:
                    {
                        ushort idx = reader.ReadUInt16();
                        T val = ReadElement(reader);
                        if (idx > _items.Count)
                        {
                            Debug.LogWarning(
                                $"[RTMPE] NetworkVariableList<{typeof(T).Name}>: Insert index " +
                                $"{idx} exceeds Count {_items.Count}.  Dropping op.");
                            break;
                        }
                        if (_items.Count >= maxListSize)
                        {
                            Debug.LogWarning(
                                $"[RTMPE] NetworkVariableList<{typeof(T).Name}>: Insert would " +
                                $"exceed the configured max {maxListSize}.  Dropping op.");
                            break;
                        }
                        _items.Insert(idx, val);
                        _deferredChanges.Add(new NetworkVariableListChangeEvent<T>(
                            NetworkListChangeKind.Insert, idx, val, default));
                        break;
                    }
                    case ListOp.RemoveAt:
                    {
                        ushort idx = reader.ReadUInt16();
                        if (idx >= _items.Count)
                        {
                            Debug.LogWarning(
                                $"[RTMPE] NetworkVariableList<{typeof(T).Name}>: RemoveAt index " +
                                $"{idx} out of range (Count={_items.Count}).  Dropping op.");
                            break;
                        }
                        T removed = _items[idx];
                        _items.RemoveAt(idx);
                        _deferredChanges.Add(new NetworkVariableListChangeEvent<T>(
                            NetworkListChangeKind.RemoveAt, idx, default, removed));
                        break;
                    }
                    case ListOp.Set:
                    {
                        ushort idx = reader.ReadUInt16();
                        T val = ReadElement(reader);
                        if (idx >= _items.Count)
                        {
                            Debug.LogWarning(
                                $"[RTMPE] NetworkVariableList<{typeof(T).Name}>: Set index " +
                                $"{idx} out of range (Count={_items.Count}).  Dropping op.");
                            break;
                        }
                        T previous = _items[idx];
                        _items[idx] = val;
                        _deferredChanges.Add(new NetworkVariableListChangeEvent<T>(
                            NetworkListChangeKind.Set, idx, val, previous));
                        break;
                    }
                    case ListOp.Clear:
                    {
                        _items.Clear();
                        _deferredChanges.Add(new NetworkVariableListChangeEvent<T>(
                            NetworkListChangeKind.Clear, -1, default, default));
                        break;
                    }
                    case ListOp.FullSync:
                    {
                        ushort count = reader.ReadUInt16();

                        // Defence against an attacker-controlled wire field:
                        // pre-allocating capacity for the full uint16 range
                        // would commit ~512 KB per variable per tick at
                        // 16-byte elements.  A FullSync above the configured
                        // ceiling is rejected outright — the atomic restore in
                        // Deserialize keeps owner and receiver converged.
                        if (count > maxListSize)
                            throw new InvalidDataException(
                                $"FullSync count {count} exceeds configured max {maxListSize}");

                        _items.Clear();
                        _items.Capacity = Math.Max(_items.Capacity, count);
                        for (int k = 0; k < count; k++)
                        {
                            if (reader.BaseStream.Position >= reader.BaseStream.Length)
                                throw new EndOfStreamException(
                                    $"FullSync truncated after {k} of {count} elements");
                            _items.Add(ReadElement(reader));
                        }
                        _deferredChanges.Add(new NetworkVariableListChangeEvent<T>(
                            NetworkListChangeKind.FullSync, -1, default, default));
                        break;
                    }
                    default:
                        throw new InvalidDataException(
                            $"unknown op 0x{(byte)op:X2} at index {i} of {opCount}");
                }
            }
        }

        private void WriteFullSync(BinaryWriter writer)
        {
            // The wire format encodes the count as a 2-byte unsigned integer,
            // so lists larger than 65535 cannot be serialized as a FullSync.
            // Silent truncation would desync owner and remote — log a hard
            // error and emit a zero-length sync so receivers see "list cleared"
            // rather than a corrupted partial mirror.
            if (_items.Count > ushort.MaxValue)
            {
                Debug.LogError(
                    $"[RTMPE] NetworkVariableList<{typeof(T).Name}>: list size {_items.Count} " +
                    $"exceeds wire cap {ushort.MaxValue}.  FullSync aborted; remote receivers " +
                    "will see an empty list until the size drops below the cap.");
                writer.Write((byte)1);
                writer.Write((byte)ListOp.FullSync);
                writer.Write((ushort)0);
                return;
            }
            writer.Write((byte)1);                  // op_count = 1
            writer.Write((byte)ListOp.FullSync);
            int count = _items.Count;
            writer.Write((ushort)count);
            for (int i = 0; i < count; i++)
            {
                WriteElement(writer, _items[i]);
            }
        }

        // Test seam: per-instance override of the FullSync size cap so unit
        // tests can exercise the gate without standing up a full
        // NetworkManager + NetworkSettings asset.  Negative leaves the
        // setting-driven default in effect.  The field is always present so
        // ResolveMaxListSize stays branch-stable across builds; only the
        // mutator is excluded from the shipped Player assembly.
        private int _testMaxListSize = -1;

#if UNITY_INCLUDE_TESTS
        internal void SetMaxListSizeForTest(int max) => _testMaxListSize = max;
#endif // UNITY_INCLUDE_TESTS

        private int ResolveMaxListSize()
        {
            if (_testMaxListSize >= 0) return _testMaxListSize;

            // Look up the live setting; fall back to a conservative default
            // when no NetworkManager is present (e.g. server-side unit tests).
            var settings = RTMPE.Core.NetworkManager.Instance?.Settings;
            if (settings != null && settings.maxNetworkVariableListSize > 0)
                return settings.maxNetworkVariableListSize;
            return 1024;
        }
    }

    /// <summary>Kind of change reported by <see cref="NetworkVariableList{T}.OnListChanged"/>.</summary>
    public enum NetworkListChangeKind : byte
    {
        Add      = 1,
        Insert   = 2,
        RemoveAt = 3,
        Set      = 4,
        Clear    = 5,
        FullSync = 6,
    }

    /// <summary>
    /// Event payload describing a single mutation applied to a
    /// <see cref="NetworkVariableList{T}"/>.  <see cref="Index"/> is meaningful
    /// only for Add/Insert/RemoveAt/Set; it is <c>-1</c> for Clear and FullSync.
    /// </summary>
    public readonly struct NetworkVariableListChangeEvent<T>
    {
        public readonly NetworkListChangeKind Kind;
        public readonly int Index;
        public readonly T   NewValue;
        public readonly T   PreviousValue;

        public NetworkVariableListChangeEvent(
            NetworkListChangeKind kind, int index, T newValue, T previousValue)
        {
            Kind          = kind;
            Index         = index;
            NewValue      = newValue;
            PreviousValue = previousValue;
        }
    }

    // ── Concrete subclasses ────────────────────────────────────────────────────

    /// <summary>Synchronised list of 32-bit signed integers.</summary>
    public sealed class NetworkVariableListInt : NetworkVariableList<int>
    {
        public NetworkVariableListInt(NetworkBehaviour owner, ushort variableId)
            : base(owner, variableId) { }

        protected override void WriteElement(BinaryWriter writer, int value)
            => writer.Write(value);
        protected override int ReadElement(BinaryReader reader)
            => reader.ReadInt32();
    }

    /// <summary>Synchronised list of 32-bit IEEE-754 floats.</summary>
    public sealed class NetworkVariableListFloat : NetworkVariableList<float>
    {
        public NetworkVariableListFloat(NetworkBehaviour owner, ushort variableId)
            : base(owner, variableId) { }

        protected override void WriteElement(BinaryWriter writer, float value)
            => writer.Write(value);
        protected override float ReadElement(BinaryReader reader)
            => reader.ReadSingle();
    }

    /// <summary>Synchronised list of <see cref="Vector3"/>.</summary>
    public sealed class NetworkVariableListVector3 : NetworkVariableList<Vector3>
    {
        public NetworkVariableListVector3(NetworkBehaviour owner, ushort variableId)
            : base(owner, variableId) { }

        protected override void WriteElement(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        protected override Vector3 ReadElement(BinaryReader reader)
            => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    /// <summary>
    /// Synchronised list of <see cref="string"/> values.  Each element is
    /// encoded as a 2-byte LE length prefix followed by UTF-8 bytes; null
    /// is normalised to <see cref="string.Empty"/> on write.
    /// </summary>
    public sealed class NetworkVariableListString : NetworkVariableList<string>
    {
        // Strict UTF-8 codec — the lax form silently substitutes U+FFFD for
        // malformed sequences, which lets a hostile peer smuggle bytes that
        // survive the decode but mutate downstream string-equality
        // invariants (kill-feed names compared against reserved sentinels,
        // chat-channel keys, etc.).  Symmetric with NetworkVariableString
        // and the RPC stack.
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        public NetworkVariableListString(NetworkBehaviour owner, ushort variableId)
            : base(owner, variableId) { }

        protected override void WriteElement(BinaryWriter writer, string value)
        {
            string s = value ?? string.Empty;
            byte[] bytes = StrictUtf8.GetBytes(s);
            if (bytes.Length > ushort.MaxValue)
                throw new ArgumentException(
                    $"NetworkVariableListString element is {bytes.Length} UTF-8 bytes — " +
                    $"exceeds {ushort.MaxValue}-byte wire limit.",
                    nameof(value));
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        protected override string ReadElement(BinaryReader reader)
        {
            ushort len = reader.ReadUInt16();
            if (len == 0) return string.Empty;
            byte[] bytes = reader.ReadBytes(len);
            // BinaryReader.ReadBytes returns FEWER than the requested count
            // when the underlying stream ends early — no exception is raised.
            // Without this guard a truncated FullSync element would decode as
            // a short string and leave the receiver's list permanently out of
            // sync with the owner.  Surface a clean EndOfStreamException so
            // the caller's existing element-failure handling drops the whole
            // payload (already wired in the FullSync apply path).
            if (bytes.Length != len)
                throw new EndOfStreamException(
                    $"NetworkVariableListString.ReadElement: declared {len} UTF-8 bytes, " +
                    $"only {bytes.Length} available — payload truncated.");
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new EndOfStreamException(
                    "NetworkVariableListString.ReadElement: malformed UTF-8 in element payload.",
                    ex);
            }
        }
    }
}
