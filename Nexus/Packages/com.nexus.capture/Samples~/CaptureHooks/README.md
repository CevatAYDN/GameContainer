# Capture Hooks Sample

This sample shows a reference implementation of `ICaptureHooks` (`ExampleCaptureHooks.cs`) for Nexus Capture Mode.

## What it shows

- **Game Name**: Returns `Application.productName` for capture metadata and recording folder names.
- **God Mode**: Sets a static flag demonstrating how Capture Mode toggles invulnerability during takes.
- **Level Jumping**: Reads `SceneManager.sceneCountInBuildSettings` and loads scenes by build settings index.
- **Presets**: Provides a "Restart level" preset that reloads the currently active scene.
- **Automatic Registration**: Uses `[UnityEditor.InitializeOnLoadMethod]` to register hooks on domain reload, ensuring the hooks survive entering Play Mode whether domain reload is enabled or disabled.

## How to import

1. In the Unity Editor, open **Window > Package Manager**.
2. Select **Nexus Capture** in the packages list.
3. Expand the **Samples** section and click **Import** next to **Capture Hooks**.
4. Unity copies the sample files into `Assets/Samples/Nexus Capture/<version>/Capture Hooks/`.

## What to change for your game

Do not use this sample class directly in production. Instead, adapt it for your game:
- Replace `GodMode` with your game's actual runtime invulnerability or debug flag (e.g. `PlayerHealth.Invulnerable = on;`).
- Replace `LevelCount` and `JumpToLevel` with your project's level manager, scene loading system, or state machine transitions.
- Add scenario presets relevant to your game in `Presets` and `ApplyPreset` (e.g. "Boss fight", "Spawn horde", "Unlock all upgrades").

## Editor-only asmdef requirement

The sample assembly definition (`Nexus.Capture.Samples.CaptureHooks.asmdef`) is configured with `"includePlatforms": ["Editor"]`.

Because `com.nexus.capture` runtime code carries `defineConstraints: ["UNITY_EDITOR"]`, Capture Mode exists exclusively in the Unity Editor and is omitted from player builds. Any imported sample code without an editor-only assembly definition would compile into `Assembly-CSharp` and break player builds. The asmdef must stay editor-only.

## Development workflow

Each game writes its own hook class in an editor-only assembly under an issue in its own repository. Never modify framework package files directly.
