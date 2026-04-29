# FlatBuffers C# Runtime — Vendored Source

**Upstream:** https://github.com/google/flatbuffers
**Version:** 25.12.19 (commit tag `v25.12.19`)
**License:** Apache License 2.0 (see `LICENSE.txt` in this directory)
**Source path:** `net/FlatBuffers/` in the upstream repo

## Why vendored

Unity does not consume `dotnet` NuGet packages directly, and the `Google.FlatBuffers` package on NuGet pulls in `System.Memory` references that conflict with Unity's `mscorlib`. Vendoring the 9 hand-picked source files lets the SDK ship with a single self-contained runtime that:

- Builds in IL2CPP and Mono backends without modification
- Has no transitive package dependencies
- Stays version-locked to the `flatc 25.12.19` compiler that emitted the bindings under `../Generated/`

## Files

| File | Purpose |
|---|---|
| `ByteBuffer.cs` | Backing store for read/write of FB messages (1093 lines) |
| `ByteBufferUtil.cs` | Size-prefix helpers (39 lines) |
| `FlatBufferBuilder.cs` | Fluent encoder for outbound messages (1038 lines) |
| `FlatBufferConstants.cs` | Magic numbers, version checks (37 lines) |
| `FlatBufferVerify.cs` | Untrusted-input validator — required for any client-controlled payload |
| `IFlatbufferObject.cs` | Marker interface implemented by every generated type |
| `Offset.cs` | Strongly-typed table offsets |
| `Struct.cs` | Base for inline (no-vtable) structs (Vec3, Quaternion) |
| `Table.cs` | Base for vtable-indexed tables (PlayerState, InputPayload, …) |

Total: ~125 KB of source; zero new package references.

## Local hardening

The vendored copy is **not byte-identical** with upstream `25.12.19`. The following
files contain receive-path security hardening that must be carried forward whenever
this directory is re-vendored:

- `Table.cs`
  - `__string` now caps length at `MaxStringBytes = 4096`, decodes with a
    strict UTF-8 decoder that throws on malformed input, and rejects
    embedded NUL bytes.
  - `__vector_len` now caps length at `MaxVectorElements = 65536` and
    rejects negative wire values.
  - `__vector_as_array<T>` and `__vector_as_span<T>` no longer throw
    `NotSupportedException` on big-endian hosts; they fall back to an
    endian-aware element-by-element reader (`ReadTypedArrayBigEndian<T>`)
    so a single received packet cannot crash a BE-target build.
- `ByteBuffer.cs`
  - `AssertOffsetAndLength` performs an unconditional in-buffer range
    check even under `BYTEBUFFER_NO_BOUNDS_CHECK`. The build flag now
    only opts out of strict alignment / multiple-of-T validation; it no
    longer turns the reader into an arbitrary-read primitive when fed
    adversarial input.
  - `GetStringUTF8Strict` is a new accessor invoked from `Table.__string`.

When upgrading, re-apply these changes by hand or rebase the upstream patch
against this directory before the vendored copy is overwritten.

## Updating

To upgrade the runtime, re-run from the repo root:

```bash
# (1) pull the new tag into the Go module cache (re-uses the existing path)
cd /tmp && go mod download github.com/google/flatbuffers@v<NEW_VERSION>+incompatible

# (2) re-vendor the 9 files into this directory
FB_SRC="$HOME/go/pkg/mod/github.com/google/flatbuffers@v<NEW_VERSION>+incompatible/net/FlatBuffers"
FB_DST="clients/unity-sdk/Packages/com.rtmpe.sdk/Runtime/Infrastructure/Serialization/FlatBuffers"
for f in ByteBuffer.cs ByteBufferUtil.cs FlatBufferBuilder.cs FlatBufferConstants.cs \
         FlatBufferVerify.cs IFlatbufferObject.cs Offset.cs Struct.cs Table.cs; do
  cp "$FB_SRC/$f" "$FB_DST/$f"
done

# (3) re-vendor the LICENSE
cp "$HOME/go/pkg/mod/github.com/google/flatbuffers@v<NEW_VERSION>+incompatible/LICENSE" \
   "$FB_DST/LICENSE.txt"

# (4) regenerate the bindings under Runtime/Infrastructure/Serialization/Generated/
make generate-contracts
```

The Rust crate (`modules/gateway/Cargo.toml`) and the Go module
(`shared/contracts/generated/go/go.mod`) must be bumped to the same version
so all three runtimes stay byte-compatible on the wire.

## IL2CPP / AOT note

The vendored runtime uses `unsafe` only inside guarded `#if` blocks (Span<byte>
fast-path); the default Unity build path is fully managed.  Generated bindings
under `../Generated/` reference these types via direct calls, NOT reflection,
so no `link.xml` preservation is required for FlatBuffers itself.  If a future
schema introduces `[union]` types the IL2CPP linker may strip the union
discriminator dispatch — re-evaluate then.
