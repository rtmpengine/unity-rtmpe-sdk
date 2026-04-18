// RTMPE SDK — Tests/Runtime/NetworkVariableTests.cs
//
// NUnit Edit-Mode tests for NetworkVariable<T> and NetworkVariableTypes.
//
// Test philosophy:
//   • Each test targets one observable behaviour (one reason to fail).
//   • Shared setup: a NetworkManager singleton + one GameObject with a
//     minimal stub NetworkBehaviour.  Both are destroyed in TearDown.
//   • Round-trip serialisation tests use a MemoryStream + BinaryWriter/Reader
//     to verify the byte-layer without a real network connection.
//   • P-1 fix: concrete subtypes (NetworkVariableInt, etc.) are used throughout.
//     The plan's "new NetworkVariable<int>()" is invalid (abstract class).
//   • P-2 fix: Initialize called with correct types (ulong, string).
//   • P-3 fix: StubNetworkBehaviour defined locally (ConcreteNetworkBehaviour
//     in NetworkBehaviourTests.cs is private sealed).
//   • P-4 fix: SetUp creates NetworkManager so NetworkBehaviour.IsOwner works.
//   • P-5 fix: class placed in namespace RTMPE.Tests.
//   • P-8 fix: round-trip serialisation tests added for all concrete types.
//
// Fixtures:
//   Category "NetworkVariable" — value semantics, dirty tracking, events.
//   Category "NetworkVariableSerialization" — BinaryWriter/Reader round-trips.

using System.IO;
using NUnit.Framework;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync;

namespace RTMPE.Tests
{
    // ── Minimal stub — defined locally because ConcreteNetworkBehaviour ──────
    // in NetworkBehaviourTests is private sealed and not accessible here.
    // Only needs a no-op body; NetworkBehaviour is abstract with no abstract methods.
    // internal (not private) so both test fixtures in this file can reference it.
    // The name StubNB is unique in the RTMPE.Tests assembly (assembly-internal only).
    internal sealed class StubNB : NetworkBehaviour { }

    // ═════════════════════════════════════════════════════════════════════════
    // VALUE SEMANTICS, DIRTY TRACKING, EVENTS
    // ═════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("NetworkVariable")]
    public class NetworkVariableTests
    {
        // ── Shared state ──────────────────────────────────────────────────────

        private GameObject     _nmGo;
        private NetworkManager _manager;
        private GameObject     _ownerGo;
        private StubNB         _owner;

        // ── SetUp / TearDown ──────────────────────────────────────────────────

        [SetUp]
        public void SetUp()
        {
            // NetworkManager singleton required by NetworkBehaviour.IsOwner.
            _nmGo    = new GameObject("TestNetworkManager");
            _manager = _nmGo.AddComponent<NetworkManager>();

            _ownerGo = new GameObject("OwnerObject");
            _owner   = _ownerGo.AddComponent<StubNB>();

            // P-2 fix: Initialize(ulong, string) — second arg is string.
            _owner.Initialize(1UL, "test-owner");
        }

        [TearDown]
        public void TearDown()
        {
            if (_ownerGo != null) { Object.DestroyImmediate(_ownerGo); _ownerGo = null; }
            if (_nmGo    != null) { Object.DestroyImmediate(_nmGo);    _nmGo    = null; }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // Create a NetworkVariableInt with varid=0 and given initial value.
        private NetworkVariableInt  MakeInt(int initial = 0)
            => new NetworkVariableInt(_owner, 0, initial);

        private NetworkVariableFloat MakeFloat(float initial = 0f)
            => new NetworkVariableFloat(_owner, 0, initial);

        private NetworkVariableBool MakeBool(bool initial = false)
            => new NetworkVariableBool(_owner, 0, initial);

        private NetworkVariableString MakeString(string initial = "")
            => new NetworkVariableString(_owner, 0, initial);

        // ── 1: IsDirty false at construction ─────────────────────────────────

        [Test]
        [Description("A freshly created NetworkVariable is not dirty.")]
        public void IsDirty_AtConstruction_IsFalse()
        {
            var v = MakeInt(42);

            Assert.IsFalse(v.IsDirty);
        }

        // ── 2: VariableId stored correctly ───────────────────────────────────

        [Test]
        [Description("VariableId is the value passed to the constructor.")]
        public void VariableId_StoredCorrectly()
        {
            var v = new NetworkVariableInt(_owner, 7, 0);

            Assert.AreEqual((ushort)7, v.VariableId);
        }

        // ── 3: Value initialised ──────────────────────────────────────────────

        [Test]
        [Description("Value property returns the initial value passed to the constructor.")]
        public void Value_Initial_MatchesConstructorArg()
        {
            var v = MakeInt(99);

            Assert.AreEqual(99, v.Value);
        }

        // ── 4: OnValueChanged fires on change ─────────────────────────────────

        [Test]
        [Description("OnValueChanged fires exactly once when Value is set to a new value.")]
        public void Value_SetNewValue_FiresOnValueChangedOnce()
        {
            var v = MakeInt(0);
            int callCount = 0;
            v.OnValueChanged += (_, __) => callCount++;

            v.Value = 10;

            Assert.AreEqual(1, callCount);
        }

        // ── 5: OnValueChanged does NOT fire for same value ────────────────────

        [Test]
        [Description("Setting Value to its current value is a no-op: no event, no dirty.")]
        public void Value_SetSameValue_DoesNotFireEvent()
        {
            var v = MakeInt(5);
            int callCount = 0;
            v.OnValueChanged += (_, __) => callCount++;

            v.Value = 5; // same

            Assert.AreEqual(0, callCount);
            Assert.IsFalse(v.IsDirty);
        }

        // ── 6: IsDirty set after change ───────────────────────────────────────

        [Test]
        [Description("Setting Value to a new value marks IsDirty = true.")]
        public void Value_SetNewValue_MarksDirty()
        {
            var v = MakeInt(0);

            v.Value = 1;

            Assert.IsTrue(v.IsDirty);
        }

        // ── 7: MarkClean clears IsDirty ───────────────────────────────────────

        [Test]
        [Description("MarkClean() resets IsDirty to false.")]
        public void MarkClean_AfterDirty_ResetsDirty()
        {
            var v = MakeInt(0);
            v.Value = 1;
            Assert.IsTrue(v.IsDirty, "Pre-condition: must be dirty after value change.");

            v.MarkClean();

            Assert.IsFalse(v.IsDirty);
        }

        // ── 8: Old and new values passed to callback ──────────────────────────

        [Test]
        [Description("Callback receives (oldValue, newValue) in the correct order.")]
        public void OnValueChanged_PassesOldAndNewValue()
        {
            var v = MakeInt(10);
            int receivedOld = -1, receivedNew = -1;
            v.OnValueChanged += (old, nw) => { receivedOld = old; receivedNew = nw; };

            v.Value = 20;

            Assert.AreEqual(10, receivedOld, "old value must be 10");
            Assert.AreEqual(20, receivedNew, "new value must be 20");
        }

        // ── 9: Value readable inside callback ─────────────────────────────────

        [Test]
        [Description("Value is already updated when OnValueChanged fires (callback can read Value).")]
        public void OnValueChanged_ValueUpdatedBeforeCallback()
        {
            var v = MakeInt(0);
            int valueInsideCallback = -1;
            v.OnValueChanged += (_, __) => valueInsideCallback = v.Value;

            v.Value = 42;

            Assert.AreEqual(42, valueInsideCallback);
        }

        // ── 10: SetValueWithoutNotify does not fire event ─────────────────────

        [Test]
        [Description("SetValueWithoutNotify updates Value without firing OnValueChanged.")]
        public void SetValueWithoutNotify_DoesNotFireEvent()
        {
            var v = MakeInt(0);
            int callCount = 0;
            v.OnValueChanged += (_, __) => callCount++;

            v.SetValueWithoutNotify(99);

            Assert.AreEqual(0, callCount, "No event must fire.");
            Assert.AreEqual(99, v.Value, "Value must be updated.");
        }

        // ── 11: SetValueWithoutNotify does not mark dirty ─────────────────────

        [Test]
        [Description("SetValueWithoutNotify does not set IsDirty (receive-side use case).")]
        public void SetValueWithoutNotify_DoesNotMarkDirty()
        {
            var v = MakeInt(0);

            v.SetValueWithoutNotify(50);

            Assert.IsFalse(v.IsDirty);
        }

        // ── 12: Multiple consecutive changes accumulate dirty ─────────────────

        [Test]
        [Description("Multiple changes keep IsDirty = true until MarkClean is called.")]
        public void Value_MultipleChanges_StaysDirtyUntilMarkClean()
        {
            var v = MakeInt(0);
            v.Value = 1;
            v.Value = 2;
            v.Value = 3;
            Assert.IsTrue(v.IsDirty, "Still dirty after 3 changes.");

            v.MarkClean();
            Assert.IsFalse(v.IsDirty);
        }

        // ── 13: Float NaN equality (.NET IEquatable semantics) ──────────────

        [Test]
        [Description("Setting float.NaN once fires the event (0f→NaN). " +
                     "A second NaN assignment is a no-op because IEquatable<float>.Equals(NaN,NaN) " +
                     "returns true in .NET/Mono (NaN is treated as equal to itself by Equals).")]
        public void NetworkVariableFloat_FirstNaN_Fires_SecondNaN_IsNoop()
        {
            var v = MakeFloat(0f);
            int callCount = 0;
            v.OnValueChanged += (_, __) => callCount++;

            v.Value = float.NaN; // 0f → NaN: fires once
            v.Value = float.NaN; // NaN → NaN: no-op (IEquatable<float>.Equals(NaN,NaN)==true)

            Assert.AreEqual(1, callCount,
                "Second NaN assignment must be a no-op: IEquatable<float>.Equals(NaN,NaN)==true.");
        }

        // ── 14: Bool change from false → true ─────────────────────────────────

        [Test]
        [Description("NetworkVariableBool fires event when toggling false → true.")]
        public void NetworkVariableBool_Toggle_FiresEvent()
        {
            var v = MakeBool(false);
            bool received = false;
            v.OnValueChanged += (_, nw) => received = nw;

            v.Value = true;

            Assert.IsTrue(received);
            Assert.AreEqual(true, v.Value);
        }

        // ── 15: String null normalised to empty ───────────────────────────────

        [Test]
        [Description("Assigning null to NetworkVariableString.Value stores \"\" (P-6 fix).")]
        public void NetworkVariableString_NullValue_TreatedAsEmptyString()
        {
            var v = MakeString("hello");

            v.Value = null; // should normalise to ""

            Assert.AreEqual(string.Empty, v.Value, "null must be stored as \"\".");
        }

        // ── 16: String event fires for null-as-empty change ───────────────────

        [Test]
        [Description("Assigning null fires OnValueChanged with newValue = \"\" when old != \"\".")]
        public void NetworkVariableString_NullChange_FiresEventWithEmptyString()
        {
            var v = MakeString("hello");
            string eventNewValue = "NOT_SET";
            v.OnValueChanged += (_, nw) => eventNewValue = nw;

            v.Value = null;

            Assert.AreEqual(string.Empty, eventNewValue);
        }

        // ── 17: String no-event when already empty and assigned null ──────────

        [Test]
        [Description("Assigning null when value is already \"\" is a no-op (no event).")]
        public void NetworkVariableString_NullOnEmpty_NoEvent()
        {
            var v = MakeString("");  // already empty
            int callCount = 0;
            v.OnValueChanged += (_, __) => callCount++;

            v.Value = null; // normalises to "" — same as current

            Assert.AreEqual(0, callCount);
        }

        // ── 18: Vector3 no event for same value ───────────────────────────────

        [Test]
        [Description("Setting Vector3 to its current value does not fire the event.")]
        public void NetworkVariableVector3_SameValue_NoEvent()
        {
            var initial = new Vector3(1f, 2f, 3f);
            var v = new NetworkVariableVector3(_owner, 0, initial);
            int callCount = 0;
            v.OnValueChanged += (_, __) => callCount++;

            v.Value = initial; // exact same struct

            Assert.AreEqual(0, callCount);
        }

        // ── 19: Vector3 event fires on change ─────────────────────────────────

        [Test]
        [Description("Changing Vector3 fires the event with correct old/new values.")]
        public void NetworkVariableVector3_Change_FiresEvent()
        {
            var oldVec = new Vector3(0f, 0f, 0f);
            var newVec = new Vector3(1f, 2f, 3f);
            var v = new NetworkVariableVector3(_owner, 0, oldVec);
            Vector3 receivedOld = Vector3.one * -1f, receivedNew = Vector3.one * -1f;
            v.OnValueChanged += (old, nw) => { receivedOld = old; receivedNew = nw; };

            v.Value = newVec;

            Assert.AreEqual(oldVec, receivedOld);
            Assert.AreEqual(newVec, receivedNew);
        }

        // ── 20: NetworkVariableString multiple events ─────────────────────────

        [Test]
        [Description("NetworkVariableString fires correctly for two sequential changes.")]
        public void NetworkVariableString_TwoChanges_FiresTwice()
        {
            var v = MakeString("a");
            int callCount = 0;
            v.OnValueChanged += (_, __) => callCount++;

            v.Value = "b";
            v.Value = "c";

            Assert.AreEqual(2, callCount);
            Assert.AreEqual("c", v.Value);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // SERIALISATION ROUND-TRIPS (BinaryWriter / BinaryReader)
    // ═════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("NetworkVariableSerialization")]
    public class NetworkVariableSerializationTests
    {
        private GameObject     _nmGo;
        private NetworkManager _manager;
        private GameObject     _ownerGo;
        private StubNB         _owner;

        [SetUp]
        public void SetUp()
        {
            _nmGo    = new GameObject("TestNetworkManager");
            _manager = _nmGo.AddComponent<NetworkManager>();

            _ownerGo = new GameObject("OwnerObject");
            _owner   = _ownerGo.AddComponent<StubNB>();
            _owner.Initialize(1UL, "test-owner");
        }

        [TearDown]
        public void TearDown()
        {
            if (_ownerGo != null) { Object.DestroyImmediate(_ownerGo); _ownerGo = null; }
            if (_nmGo    != null) { Object.DestroyImmediate(_nmGo);    _nmGo    = null; }
        }

        // ── Helper: serialize v, create a new instance, deserialize into it ──

        private static void RoundTrip<TVar>(TVar src, TVar dst)
            where TVar : NetworkVariableBase
        {
            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            src.Serialize(writer);
            writer.Flush();

            ms.Position = 0;
            using var reader = new BinaryReader(ms);
            dst.Deserialize(reader);
        }

        // ── 1: int round-trip ─────────────────────────────────────────────────

        [Test]
        [Description("NetworkVariableInt: Serialize then Deserialize recovers the original value.")]
        public void NetworkVariableInt_RoundTrip()
        {
            var src = new NetworkVariableInt(_owner, 0, -12345678);
            var dst = new NetworkVariableInt(_owner, 0, 0);

            RoundTrip(src, dst);

            Assert.AreEqual(-12345678, dst.Value);
        }

        // ── 2: int round-trip does not fire event or dirty ────────────────────

        [Test]
        [Description("Deserialize via SetValueWithoutNotify does not fire event or mark dirty.")]
        public void NetworkVariableInt_Deserialize_NoEventNoDirty()
        {
            var src = new NetworkVariableInt(_owner, 0, 42);
            var dst = new NetworkVariableInt(_owner, 0, 0);
            int eventCount = 0;
            dst.OnValueChanged += (_, __) => eventCount++;

            RoundTrip(src, dst);

            Assert.AreEqual(0, eventCount, "No event should fire on Deserialize.");
            Assert.IsFalse(dst.IsDirty,    "Deserialize must not mark dirty.");
        }

        // ── 3: float round-trip ───────────────────────────────────────────────

        [Test]
        [Description("NetworkVariableFloat: round-trip preserves IEEE 754 bit pattern.")]
        public void NetworkVariableFloat_RoundTrip()
        {
            const float value = -3.1415927f;
            var src = new NetworkVariableFloat(_owner, 0, value);
            var dst = new NetworkVariableFloat(_owner, 0, 0f);

            RoundTrip(src, dst);

            Assert.AreEqual(value, dst.Value, 0f, "Exact IEEE 754 equality expected after round-trip.");
        }

        // ── 4: bool true round-trip ───────────────────────────────────────────

        [Test]
        [Description("NetworkVariableBool(true): Serialize writes 0x01, Deserialize reads true.")]
        public void NetworkVariableBool_True_RoundTrip()
        {
            var src = new NetworkVariableBool(_owner, 0, true);
            var dst = new NetworkVariableBool(_owner, 0, false);

            RoundTrip(src, dst);

            Assert.AreEqual(true, dst.Value);
        }

        // ── 5: bool false round-trip ──────────────────────────────────────────

        [Test]
        [Description("NetworkVariableBool(false): Serialize writes 0x00, Deserialize reads false.")]
        public void NetworkVariableBool_False_RoundTrip()
        {
            var src = new NetworkVariableBool(_owner, 0, false);
            var dst = new NetworkVariableBool(_owner, 0, true);

            RoundTrip(src, dst);

            Assert.AreEqual(false, dst.Value);
        }

        // ── 6: Vector3 round-trip ─────────────────────────────────────────────

        [Test]
        [Description("NetworkVariableVector3: all three components survive the round-trip.")]
        public void NetworkVariableVector3_RoundTrip()
        {
            var value = new Vector3(1.5f, -2.25f, 3.75f);
            var src   = new NetworkVariableVector3(_owner, 0, value);
            var dst   = new NetworkVariableVector3(_owner, 0, Vector3.zero);

            RoundTrip(src, dst);

            Assert.AreEqual(value.x, dst.Value.x, 0f, "X");
            Assert.AreEqual(value.y, dst.Value.y, 0f, "Y");
            Assert.AreEqual(value.z, dst.Value.z, 0f, "Z");
        }

        // ── 7: Quaternion round-trip ──────────────────────────────────────────

        [Test]
        [Description("NetworkVariableQuaternion: all four components (XYZW) survive the round-trip.")]
        public void NetworkVariableQuaternion_RoundTrip()
        {
            // A 90° rotation around Y: Quaternion.Euler(0, 90, 0).
            var value = Quaternion.Euler(0f, 90f, 0f);
            var src   = new NetworkVariableQuaternion(_owner, 0, value);
            var dst   = new NetworkVariableQuaternion(_owner, 0, Quaternion.identity);

            RoundTrip(src, dst);

            Assert.AreEqual(value.x, dst.Value.x, 0.0001f, "X");
            Assert.AreEqual(value.y, dst.Value.y, 0.0001f, "Y");
            Assert.AreEqual(value.z, dst.Value.z, 0.0001f, "Z");
            Assert.AreEqual(value.w, dst.Value.w, 0.0001f, "W");
        }

        // ── 8: String round-trip ──────────────────────────────────────────────

        [Test]
        [Description("NetworkVariableString: Unicode + ASCII string survives the round-trip.")]
        public void NetworkVariableString_RoundTrip()
        {
            const string text = "Hello, 世界! \u0041\u0042";
            var src = new NetworkVariableString(_owner, 0, text);
            var dst = new NetworkVariableString(_owner, 0, "");

            RoundTrip(src, dst);

            Assert.AreEqual(text, dst.Value);
        }

        // ── 9: String null source serialises as empty ─────────────────────────

        [Test]
        [Description("A NetworkVariableString with Value=\"\" serialises and deserialises as \"\".")]
        public void NetworkVariableString_Empty_RoundTrip()
        {
            var src = new NetworkVariableString(_owner, 0, "");
            var dst = new NetworkVariableString(_owner, 0, "initial");

            RoundTrip(src, dst);

            Assert.AreEqual(string.Empty, dst.Value);
        }

        // ── 10: int serialises 4 bytes ────────────────────────────────────────

        [Test]
        [Description("NetworkVariableInt.Serialize writes exactly 4 bytes.")]
        public void NetworkVariableInt_Serialize_Writes4Bytes()
        {
            var v = new NetworkVariableInt(_owner, 0, 1);

            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            v.Serialize(writer);
            writer.Flush();

            Assert.AreEqual(4L, ms.Length, "int must occupy exactly 4 bytes.");
        }

        // ── 11: bool serialises 1 byte ────────────────────────────────────────

        [Test]
        [Description("NetworkVariableBool.Serialize writes exactly 1 byte.")]
        public void NetworkVariableBool_Serialize_Writes1Byte()
        {
            var v = new NetworkVariableBool(_owner, 0, true);

            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            v.Serialize(writer);
            writer.Flush();

            Assert.AreEqual(1L, ms.Length, "bool must occupy exactly 1 byte.");
        }

        // ── 12: Vector3 serialises 12 bytes ───────────────────────────────────

        [Test]
        [Description("NetworkVariableVector3.Serialize writes exactly 12 bytes (3 × float).")]
        public void NetworkVariableVector3_Serialize_Writes12Bytes()
        {
            var v = new NetworkVariableVector3(_owner, 0, Vector3.one);

            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            v.Serialize(writer);
            writer.Flush();

            Assert.AreEqual(12L, ms.Length, "Vector3 must occupy exactly 12 bytes.");
        }

        // ── 13: Quaternion serialises 16 bytes ────────────────────────────────

        [Test]
        [Description("NetworkVariableQuaternion.Serialize writes exactly 16 bytes (4 × float).")]
        public void NetworkVariableQuaternion_Serialize_Writes16Bytes()
        {
            var v = new NetworkVariableQuaternion(_owner, 0, Quaternion.identity);

            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            v.Serialize(writer);
            writer.Flush();

            Assert.AreEqual(16L, ms.Length, "Quaternion must occupy exactly 16 bytes.");
        }

        // ── 14: SerializeWithId — wire layout for int ─────────────────────────

        [Test]
        [Description("SerializeWithId for NetworkVariableInt emits exactly 8 bytes: " +
                     "[var_id:2][value_len:2][int_bytes:4], and the var_id and value_len fields " +
                     "have the correct little-endian values.")]
        public void SerializeWithId_Int_WireLayout()
        {
            var v = new NetworkVariableInt(_owner, 5, 0x01020304);

            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            v.SerializeWithId(writer);
            writer.Flush();

            Assert.AreEqual(8L, ms.Length,
                "Wire frame: 2 (var_id) + 2 (value_len) + 4 (int) = 8 bytes");

            ms.Position = 0;
            using var reader = new BinaryReader(ms);
            Assert.AreEqual((ushort)5, reader.ReadUInt16(), "var_id must be 5");
            Assert.AreEqual((ushort)4, reader.ReadUInt16(), "value_len must be 4");
        }

        // ── 15: SerializeWithId — long string does not throw ──────────────────

        [Test]
        [Description("SerializeWithId must not throw NotSupportedException for a 300-character " +
                     "string. Regression: a fixed-size 64-byte MemoryStream was overflowed.")]
        public void SerializeWithId_LongString_DoesNotThrow()
        {
            var longValue = new string('A', 300);
            var v = new NetworkVariableString(_owner, 0, longValue);

            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            Assert.DoesNotThrow(
                () => v.SerializeWithId(writer),
                "SerializeWithId must not throw for a 300-character string.");
        }

        // ── 16: SerializeWithId — long string round-trip ──────────────────────

        [Test]
        [Description("A 300-character string serialised with SerializeWithId round-trips " +
                     "correctly: wire format is [var_id:2][value_len:2][utf8_prefix:2][utf8_bytes:300] " +
                     "= 306 bytes total; Deserialize recovers the original string.")]
        public void SerializeWithId_LongString_RoundTrip()
        {
            var longValue = new string('Z', 300);
            var src = new NetworkVariableString(_owner, 3, longValue);
            var dst = new NetworkVariableString(_owner, 3, "");

            // --- Serialise ---
            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            src.SerializeWithId(writer);
            writer.Flush();

            // Total expected: 2 (var_id) + 2 (value_len) + 2 (ushort prefix in Serialize) + 300 (ASCII) = 306
            Assert.AreEqual(306L, ms.Length, "Total wire size must be 306 bytes.");

            // --- Deserialise ---
            ms.Position = 0;
            using var reader = new BinaryReader(ms);

            ushort varId    = reader.ReadUInt16();
            ushort valueLen = reader.ReadUInt16();
            Assert.AreEqual((ushort)3,   varId,    "var_id must be 3");
            Assert.AreEqual((ushort)302, valueLen, "value_len = 2 (ushort prefix) + 300 (content)");

            // Deserialize reads the inner format: [ushort len][utf8 bytes]
            byte[] valueBytes = reader.ReadBytes(valueLen);
            using var valueMs     = new MemoryStream(valueBytes);
            using var valueReader = new BinaryReader(valueMs);
            dst.Deserialize(valueReader);

            Assert.AreEqual(longValue, dst.Value, "Round-tripped value must match the original.");
        }
    }
}
