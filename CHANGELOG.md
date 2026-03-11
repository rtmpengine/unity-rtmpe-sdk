# RTMPE SDK — Changelog

All notable changes to this Unity package are documented here.
Follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format and
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [0.2.0-preview] — 2026-03-09

### Added
- `Runtime/Core/NetworkConstants.cs` — wire-protocol constants mirroring the Rust gateway
  (`PacketProtocol`, `PacketType`, `PacketFlags` with all 18 discriminator values).
- `Runtime/Core/NetworkSettings.cs` — `ScriptableObject` asset for per-project configuration;
  `CreateDefault()` produces a runtime-only instance for zero-config local development.
- `Runtime/Core/NetworkManager.cs` — singleton `MonoBehaviour`; state machine
  (`Disconnected → Connecting → Disconnecting → Disconnected | Connected → InRoom`);
  `OnStateChanged / OnConnected / OnDisconnected / OnConnectionFailed / OnDataReceived` events;
  timeout coroutine; `[DefaultExecutionOrder(-1000)]`; domain-reload-safe static state reset.
- `Runtime/Infrastructure/Transport/NetworkTransport.cs` — abstract base for all transports
  (UDP Week 8, KCP Week 10+); called exclusively from the background I/O thread.
- `Runtime/Infrastructure/Transport/UdpTransport.cs` — non-blocking UDP socket
  (`Blocking = false`, `Poll(0)`, `SocketError.WouldBlock/ConnectionReset` guard).
- `Runtime/Infrastructure/Threading/ThreadSafeQueue.cs` — lock-free `ConcurrentQueue<T>`
  wrapper; drain-loop `Clear()` (.NET Standard 2.1 compatible, no `.Clear()` .NET 5+).
- `Runtime/Infrastructure/Threading/MainThreadDispatcher.cs` — marshals callbacks from the
  network background thread to Unity's main thread via `Update()`; max 200 actions/frame;
  domain-reload-safe static state reset.
- `Runtime/Infrastructure/Threading/NetworkThread.cs` — dedicated background I/O thread
  (`ThreadPriority.AboveNormal`, `IsBackground = true`, ~1 kHz `Thread.Sleep(1)` poll).
- `Editor/NetworkManagerEditor.cs` — `[CustomEditor(typeof(NetworkManager))]` with Play Mode
  runtime diagnostics (State, IsConnected, IsInRoom, LocalPlayerId, CurrentRoomId).
- `Tests/Runtime/ThreadSafeQueueTests.cs` — 11 NUnit tests (FIFO, drain-loop, concurrent
  producer-consumer 10 000 items, reference-type safety).
- `Tests/Runtime/NetworkManagerTests.cs` — 17 NUnit tests (initial state, singleton lifecycle,
  settings defaults, input validation, disconnect guard, send guard, `OnDisconnected` no-op guard).
- `Tests/Runtime/RTMPE.SDK.Tests.asmdef` — NUnit test assembly for Runtime code.
- `Tests/Runtime/ThreadingTests.cs` — 6 contract-guard tests for protocol constants.
- `Tests/Editor/RTMPE.SDK.Editor.Tests.asmdef` — NUnit test assembly for Editor code.
- `Editor/SettingsProvider.cs` — Project Settings pane (`Project/RTMPE`).
- `Samples~/BasicConnection/README.md` — scaffold for BasicConnection sample (Week 10+).
- `Documentation~/index.md` — documentation root.
- `CHANGELOG.md` (this file) at the UPM package root.

### Changed
- `package.json` bumped to `0.2.0-preview`.
- `package.json` `samples` array added (BasicConnection entry registered).
- `package.json` `changelogUrl` updated to point to this file in the repository.
- `package.json` `unityRelease` key removed — not applicable to third-party UPM packages.
- `Editor/NetworkManagerEditor.cs` replaced Day 1-2 stub with full custom inspector.

### Fixed
- `Challenge` packet comment corrected: payload is
  `[ephemeral_pub:32][static_pub:32][ed25519_sig:64]` = 128 B (H4 security fix), not 32 B.
- `README.md` Quick Start corrected: `Connect(apiKey)` not the non-existent `ConnectAsync`.

---

## [0.1.0-preview] — 2026-03-07

### Added
- Initial UPM package scaffold (`package.json`, `Runtime/`, `Editor/`).
- `Runtime/com.rtmpe.sdk.runtime.asmdef` (`RTMPE.SDK.Runtime`).
- `Editor/com.rtmpe.sdk.editor.asmdef` (`RTMPE.SDK.Editor`).
- Empty `Infrastructure/` sub-directories (Transport, Serialization, Threading).
