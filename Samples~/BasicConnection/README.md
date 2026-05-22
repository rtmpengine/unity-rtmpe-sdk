# Basic Connection Sample

Demonstrates the minimal connect / disconnect lifecycle for the RTMPE SDK.
The sample ships a single `MonoBehaviour` (`ConnectionTest`); you add it —
together with a `NetworkManager` component — to a scene of your own.

## Prerequisites

- Unity 2022.3 LTS or newer (also supports 2023 LTS and Unity 6 / 6000.0+).
- An RTMPE gateway you can reach (local Docker stack, dev cluster, or
  production). Default settings target `127.0.0.1:7777`.
- An API key issued by the RTMPE dashboard (or any value if your gateway
  is configured for open-access development).
- A `NetworkSettings` asset (optional but recommended). Create one via
  **Assets → Create → RTMPE → Settings** and fill in `apiKeyPskHex`
  to match your gateway's PSK.

## Contents

| File | Purpose |
| --- | --- |
| `Scripts/ConnectionTest.cs` | `MonoBehaviour` that connects on Start, displays live status with `OnGUI`, and disconnects on `OnDestroy`. |

> The sample intentionally does **not** ship a `.unity` scene — you wire it
> into a scene of your own. `NetworkManager.Instance` returns the
> `NetworkManager` component placed in the scene; if none exists it returns
> `null` and logs a warning. The scene **must** therefore contain a
> `NetworkManager` (see step 4 below). `NetworkManager` itself calls
> `DontDestroyOnLoad`, so a single instance persists across scene loads.

## Quick start

1. Open the Unity Package Manager (**Window → Package Manager**).
2. Select **RTMPE SDK** → **Samples** → **Basic Connection** → **Import**.
   Unity copies the sample to `Assets/Samples/RTMPE SDK/<version>/Basic Connection/`.
3. Open or create any scene (`File → New Scene → Empty`).
4. Create an empty GameObject and add **both** of these components to it:
   - the `NetworkManager` component (**Component → RTMPE → NetworkManager**)
     — **required**; `ConnectionTest` does nothing without a `NetworkManager`
     in the scene;
   - the **Connection Test** component
     (`Add Component → Scripts → RTMPE.Samples.BasicConnection → Connection Test`).
5. In the Inspector, set:
   - **Api Key** — your RTMPE API key (or leave the placeholder if your
     gateway is open).
   - **Connect On Start** — leave enabled.
   - **Reconnect Delay** — `5` (seconds, `0` disables auto-reconnect).
6. (Optional, but recommended.) Open **Edit → Project Settings → RTMPE**
   and assign your `NetworkSettings` asset. Set `Server Host`, `Server Port`,
   and `Api Key Psk Hex` to match your gateway.
7. Press **Play**. The on-screen overlay shows the live state machine,
   round-trip-time, and any disconnect reason.

## What you should see

- Status line moves from **Idle** through **Connecting…** to **Connected!**
  (the live `NetworkState` is also shown as it transitions).
- An RTT line appears once heartbeats are flowing.
- Stopping play (or calling `TryDisconnect()`) yields a clean
  `Disconnected — reason: …` log entry.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| `apiKey not set` warning | The Inspector field is empty. Fill it in. |
| Stuck on "Connecting…" | Gateway unreachable. Check `Server Host` / `Server Port` and firewall. |
| Stuck connecting, then `Connection failed` | `apiKeyPskHex` does not match the PSK configured on your gateway. |
| `Connection failed` with a signature error | `pinnedServerPublicKeyHex` is set and does not match the gateway's Ed25519 key. Clear the field for development. |

## Manual smoke test

1. Run a local gateway (for example via the project's `docker-compose.yml`).
2. Follow the **Quick start** above.
3. Verify the status line reaches **Connected** within 5 seconds.
4. Stop play, kill the gateway, press Play again, and verify the script
   surfaces a `ConnectionFailed` reason and (if `reconnectDelay > 0`)
   schedules an automatic retry.
