# BasicConnection Sample

Demonstrates the minimal connect / disconnect lifecycle using the RTMPE SDK.

## Prerequisites

- RTMPE gateway running (local or remote)
- Valid API key configured in `RTMPE Project Settings`

## Contents

| File                          | Purpose                            |
| ----------------------------- | ---------------------------------- |
| `Scripts/BasicConnectionDemo.cs` | MonoBehaviour that connects on Start and disconnects on Destroy |
| `Scenes/BasicConnectionScene.unity` | Pre-wired scene with NetworkManager |

## Usage

1. Import this sample via **Window → Package Manager → RTMPE SDK → Samples**.
2. Open `Scenes/BasicConnectionScene.unity`.
3. Enter your server address in the `NetworkManager` Inspector.
4. Press **Play**.

> This sample demonstrates a basic RTMPE connection flow.
> The directory is scaffolded now so the UPM `samples` manifest entry resolves correctly.
