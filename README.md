# RTMPE SDK for Unity

Real-Time Multiplayer Engine — Unity 6 / .NET Standard 2.1 client SDK.

> **Current version: `1.1.0`** — gameplay-readiness release.
> Late-join state snapshot · pluggable transport · auto room re-join ·
> scene-transition pruning · object pooling.
> See the [CHANGELOG](CHANGELOG.md) for the full list of changes.

## Requirements

| Requirement      | Version           |
| ---------------- | ----------------- |
| Unity            | 6000.0 LTS+       |
| .NET Standard    | 2.1               |
| RTMPE Gateway    | ≥ 3.0.0           |
| Backend protocol | v3 (MAGIC 0x5254) |

**Supported platforms:** Windows, macOS, Linux, Android, iOS. WebGL is
supported via a user-provided WebSocket transport (see
[Architecture §3](Documentation~/architecture.md#3-transport-layer)) — the
default `UdpTransport` cannot run inside the browser sandbox.

## Installation (UPM)

1. Open **Window → Package Manager**.
2. Click **+** → **Add package from git URL…**
3. Paste:
   ```
   https://github.com/Faisalzz1/unity-rtmpe-sdk.git
   ```

Or add manually to your project's `Packages/manifest.json`:

```json
"com.rtmpe.sdk": "https://github.com/Faisalzz1/unity-rtmpe-sdk.git"
```

## Quick Start

```csharp
using RTMPE.Core;

// 1. Add NetworkManager to a GameObject in your first scene.
// 2. Assign an RTMPESettings asset in the Inspector (Create → RTMPE → Settings).
// 3. Connect.
NetworkManager.Instance.Connect("your-api-key");

NetworkManager.Instance.OnConnected += () =>
{
    // Register prefabs (and optionally an object pool) inside OnConnected —
    // a fresh SpawnManager is created on every Connect() / Reconnect().
    NetworkManager.Instance.Spawner.RegisterPrefab(prefabId: 1, prefab: playerPrefab);
    NetworkManager.Instance.Rooms.CreateRoom(new RTMPE.Rooms.CreateRoomOptions
    {
        Name       = "My Room",
        MaxPlayers = 4,
        IsPublic   = true,
    });
};
```

Full walkthrough — including reconnect, late-join snapshots, and object
pooling — in the [Getting Started guide](Documentation~/getting-started.md).

## Samples

Import samples from **Window → Package Manager → RTMPE SDK → Samples**:

| Sample          | Description                                       |
| --------------- | ------------------------------------------------- |
| BasicConnection | Minimal connect / disconnect loop                 |

Additional runnable samples ship in the companion repository
(`Samples/BasicMovement`, `Samples/SimpleFPS`) — see the top-level README for
setup instructions.

## Documentation

Full documentation lives in [`Documentation~/index.md`](Documentation~/index.md).

- [Getting Started](Documentation~/getting-started.md)
- [Architecture](Documentation~/architecture.md)
- [Protocol Reference](Documentation~/protocol.md)
- [API Reference](Documentation~/api/index.md)
- [Performance Tuning](Documentation~/performance-tuning.md)
- [Troubleshooting](Documentation~/troubleshooting.md)

## License

MIT — see [LICENSE](https://github.com/Faisalzz1/unity-rtmpe-sdk/blob/main/LICENSE.md).
