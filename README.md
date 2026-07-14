# RTMPE SDK for Unity

Real-Time Multiplayer Engine — Unity 6 (6000.0 LTS or later) / .NET Standard 2.1 client SDK.

> **Current version: `1.9.6`** — RPC routing & seat-liveness release.
> Enhanced RPCs resolve to any NetworkBehaviour (not only the anchor) ·
> seat-liveness lease + guarded reaper · Roslyn authoring toolchain ·
> ownership-transfer / reconnect hardening · security-audit remediations.

## Requirements

| Requirement      | Version                               |
| ---------------- | ------------------------------------- |
| Unity            | 6000.0 (Unity 6) LTS or later         |
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
   https://github.com/rtmpengine/unity-rtmpe-sdk.git
   ```

Or add manually to your project's `Packages/manifest.json`:

```json
"com.rtmpe.sdk": "https://github.com/rtmpengine/unity-rtmpe-sdk.git"
```

## Quick Start

The SDK does not connect to `127.0.0.1:7777` by default — every
`NetworkManager` requires a `NetworkSettings` asset that names the gateway
host, port, and PSK.  Without it, `Connect()` runs on the loopback
defaults under Strict pinning with no pin, so the first call fails with a
logged reason and an `OnConnectionFailed` callback — a pin/PSK error or a
connection timeout, not a silent hang.  Create the asset first, then wire
it up:

1. **Create the settings asset.** In the **Project** panel, right-click an
   `Assets/` folder and choose **Create → RTMPE → Settings**.
   Name the result (for example `RTMPESettings_Dev.asset`).
2. **Configure the asset.** Select it and fill in the Inspector fields.
   Copy `Server Host`, `Server Port`, and `Pinned Server Public Key Hex` from
   the RTMPE developer dashboard.  Set `Api Key Psk Hex` to the operator-supplied
   PSK (it matches `GATEWAY_API_KEY_ENCRYPTION_KEY_HEX` and is not shown on the
   dashboard — obtain it from your gateway operator).  See the [Getting Started
   guide §2](Documentation~/getting-started.md#step-2--create-the-networksettings-asset)
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
    // Prefab registrations persist across reconnects; an object pool (if used)
    // is rebuilt on every Connect()/Reconnect(), so install it here.
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

Two additional, **larger demos** — `BasicMovement` and `SimpleFPS` —
live under the repository's `Samples/` folder, outside the UPM package
(so they are *not* listed in the Package Manager Samples panel). Each is
a set of scripts plus a scene rather than a self-contained Unity project:
copy its `Scripts/` and `Scenes/` into a project that already has the SDK
installed.

## Documentation

Full documentation lives in [`Documentation~/index.md`](Documentation~/index.md).

- [Getting Started](Documentation~/getting-started.md)
- [Architecture](Documentation~/architecture.md)
- [Protocol Reference](Documentation~/protocol.md)
- [API Reference](Documentation~/api/index.md)
- [Performance Tuning](Documentation~/performance-tuning.md)
- [Troubleshooting](Documentation~/troubleshooting.md)

## License

MIT — see [LICENSE](https://github.com/rtmpengine/unity-rtmpe-sdk/blob/main/LICENSE.md).
