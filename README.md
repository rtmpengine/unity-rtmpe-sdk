# RTMPE SDK for Unity

Real-Time Multiplayer Engine — Unity 2022.3 LTS+ / .NET Standard 2.1 client SDK.

> **Current version: `1.1.0`** — gameplay-readiness release.
> Late-join state snapshot · pluggable transport · auto room re-join ·
> scene-transition pruning · object pooling.
> See the [CHANGELOG](CHANGELOG.md) for the full list of changes.

## Requirements

| Requirement      | Version                               |
| ---------------- | ------------------------------------- |
| Unity            | 2022.3 LTS, 2023 LTS, or 6000.0 LTS+  |
| .NET Standard    | 2.1                                   |
| RTMPE Gateway    | ≥ 3.0.0                               |
| Backend protocol | v3 (MAGIC 0x5254)                     |

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

The SDK does not connect to `127.0.0.1:7777` by default — every
`NetworkManager` requires a `NetworkSettings` asset that names the gateway
host, port, and PSK.  Skipping this step silently leaves the manager on
the loopback fallback and the very first `Connect()` call times out with
no diagnostic.  Create the asset first, then wire it up:

1. **Create the settings asset.** In the **Project** panel, right-click an
   `Assets/` folder and choose **Create → RTMPE → Settings**.
   Name the result (for example `RTMPESettings_Dev.asset`).
2. **Configure the asset.** Select it and fill in the Inspector fields:
   `Server Host`, `Server Port`, and `Api Key Psk Hex` (copy these from
   the RTMPE developer dashboard).  See the [Getting Started guide
   §2](Documentation~/getting-started.md#step-2--create-the-networksettings-asset)
   for the full field reference.
3. **Add the NetworkManager.** Create an empty GameObject in your boot
   scene, name it `[RTMPE] NetworkManager`, and add the `NetworkManager`
   component (**Component → RTMPE → NetworkManager**).
4. **Bind the asset.** Drag the `RTMPESettings_Dev.asset` you created in
   step 1 onto the `Settings` field of the NetworkManager Inspector.
5. **Connect from code:**

```csharp
using RTMPE.Core;

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

> **Tip:** **Window → RTMPE → Setup Wizard** walks through every step
> above — including creating and binding the `NetworkSettings` asset —
> and stores the API key in the OS credential vault for you.

Full walkthrough — including reconnect, late-join snapshots, and object
pooling — in the [Getting Started guide](Documentation~/getting-started.md).

## Samples

Import samples from **Window → Package Manager → RTMPE SDK → Samples**:

| Sample          | Description                                       |
| --------------- | ------------------------------------------------- |
| BasicConnection | Minimal connect / disconnect loop (UPM-importable via the Package Manager → Samples panel) |

Two additional, **larger demos** live alongside the SDK in the
**repository root** (not inside the package, so they are *not* shown in
the Package Manager Samples panel):
`clients/unity-sdk/Samples/BasicMovement/` and
`clients/unity-sdk/Samples/SimpleFPS/`.  Open them as standalone Unity
projects (or import them manually) — see the top-level README for
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
