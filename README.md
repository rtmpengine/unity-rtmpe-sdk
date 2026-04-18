# RTMPE SDK for Unity

Real-Time Multiplayer Engine — Unity 6 / .NET Standard 2.1 client SDK.

## Requirements

| Requirement      | Version        |
| ---------------- | -------------- |
| Unity            | 6000.0 LTS+    |
| .NET Standard    | 2.1            |
| RTMPE Gateway    | ≥ 3.0.0        |
| Backend protocol | v3 (MAGIC 0x5254) |

## Installation (UPM)

1. Open **Window → Package Manager**.
2. Click **+** → **Add package from git URL…**
3. Paste:
   ```
   https://github.com/Faisalzz1/RTMPE.git?path=clients/unity-sdk/Packages/com.rtmpe.sdk
   ```

Or add manually to your project's `Packages/manifest.json`:

```json
"com.rtmpe.sdk": "https://github.com/Faisalzz1/RTMPE.git?path=clients/unity-sdk/Packages/com.rtmpe.sdk"
```

## Quick Start

```csharp
// 1. Add NetworkManager to a GameObject in your first scene
// 2. Assign an RTMPESettings asset in the Inspector (or leave blank for local defaults)
// 3. Connect
NetworkManager.Instance.Connect("your-api-key");
```

> Full usage examples are available via **Window → Package Manager → RTMPE SDK → Samples**.

## Samples

Import samples from **Window → Package Manager → RTMPE SDK → Samples**:

| Sample           | Description                                    |
| ---------------- | ---------------------------------------------- |
| BasicConnection  | Minimal connect / disconnect loop              |

## Documentation

Full documentation lives in `Documentation~/index.md` and at `/docs` in the repository.

## License

MIT — see [LICENSE](https://github.com/Faisalzz1/RTMPE/blob/main/LICENSE).
