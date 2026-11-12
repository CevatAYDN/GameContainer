# Nexus Capture (`com.nexus.capture`)

> **Who this is for:** developers shooting promo videos (TikTok, Shorts, Reels) directly inside the game during Play Mode without manual scene setup.

Nexus Capture provides an editor-only Capture Mode that controls game playback, pauses and steps frames, hides user interface layers, navigates via a free camera, suppresses advertisements, and records constant-framerate MP4 video through Unity Recorder.

Nothing in this package reaches a player build. Every Capture runtime and test assembly carries `defineConstraints: UNITY_EDITOR` (`com.nexus.capture.asmdef`, `com.nexus.capture.tests.asmdef`), and the editor assembly is editor-only (`com.nexus.capture.editor.asmdef`). The marker components `CaptureKeepVisible` and `CaptureHide` live in `com.nexus.core` (`Nexus.Capture`) as empty components without logic so scenes and prefabs carry them without missing scripts and without runtime game code ever referencing Capture.

Capture Mode operates only while in Play Mode. Calling `CaptureModeService.Open()` outside Play Mode logs a warning and does nothing. On close and upon exiting Play Mode, all modifications (time scale, fixed delta time, UI visibility, camera, ad suppression, and god mode) are idempotently restored.

## Install & Dependencies

Add this package by git URL, pinned to the same 40-character commit as `com.nexus.core`:
`https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.capture#<commit>`

Declared dependencies from `package.json`:
- `com.nexus.core`: `0.6.0`
- `com.unity.inputsystem`: `1.20.0`
- `com.unity.recorder`: `5.1.8`

Optional version defines (`versionDefines` in `com.nexus.capture.asmdef`):
- `com.unity.cinemachine` `6.6.0` -> `NEXUS_CINEMACHINE`
- `com.unity.render-pipelines.universal` `17.6.0` -> `NEXUS_URP`
- `com.nexus.ads` `0.1.0` -> `NEXUS_ADS`

## Opening

- Menu item: **Nexus > Capture Mode** (priority 45).
- Nexus window tab: **Capture** (category Live, order 26).
- Keyboard shortcut: **F9** (toggles open/closed).

Outside Play Mode, the window tab shows a hint explaining the Play Mode requirement, a button to create or select `Assets/Settings/CaptureSettings.asset`, and a summary of active shortcuts. In Play Mode, the tab provides full live controls for recording, slow motion, pause, frame stepping, UI visibility, free camera, and registered game hooks.

## Shortcuts

Default keyboard shortcuts configured in `Runtime/CaptureSettings.cs` and read by `Runtime/CaptureDriver.cs`:

| Action | Default Key | Setting Property |
|---|---|---|
| Toggle Capture Mode | `F9` | `ToggleKey` |
| Toggle Recording | `F10` | `RecordKey` |
| Toggle UI Visibility | `H` | `HideUiKey` |
| Slower Slow-Motion Step | `[` (`LeftBracket`) | `SlowerKey` |
| Faster Slow-Motion Step | `]` (`RightBracket`) | `FasterKey` |
| Toggle Pause | `P` | `PauseKey` |
| Step Single Frame | `O` | `StepKey` |
| Toggle Free Camera | `C` | `FreeCameraKey` |
| Toggle God Mode | `G` | `GodModeKey` |
| Apply Preset 1–9 | `1`–`9` (`Digit1`..`Digit9`) | Hook Presets |

`F9` toggles Capture Mode open or closed at any time during Play Mode. All other shortcuts are ignored while Capture Mode is closed.

## Slow Motion, Pause, and Frame Step

- **Slow Motion**: `CaptureModeService.Slower()` and `Faster()` step through `SlowMotionSteps` (defaults: `1f`, `0.5f`, `0.25f`, `0.1f`). `Time.fixedDeltaTime` is scaled proportionally alongside `Time.timeScale` so physics simulations remain stable at reduced speeds.
- **Pause**: `P` sets `Time.timeScale = 0f;` while preserving the target playback speed.
- **Frame Step**: while paused, `O` advances one frame by briefly restoring `Time.timeScale = Speed;` and re-pausing on the subsequent `Update()` frame.
- **Restoration**: Closing Capture Mode restores the original `Time.timeScale` and `Time.fixedDeltaTime` captured upon opening.

## UI Hiding & Markers

`CaptureModeService.ToggleUi()` (`H`) hides active game interfaces without disrupting Capture's own tools:
- Disables all enabled root `Canvas` components.
- Sets all `UIDocument` elements to `style.display = DisplayStyle.None`.
- Deactivates GameObjects carrying `CaptureHide`.
- Restores only the elements that Capture Mode hid; pre-disabled canvases and hidden elements remain disabled when UI is shown again.

Two marker components in `com.nexus.core` (`Nexus.Capture`) control visibility:
- `CaptureKeepVisible`: Placed on a Canvas or UIDocument GameObject that must stay visible during capture takes (e.g., custom prompts or debug readouts).
- `CaptureHide`: Placed on world GameObjects (such as HUD markers or world-space reticles) that should deactivate while UI is hidden.

While Capture Mode is open, a lightweight UI Toolkit overlay appears in the Game view displaying current playback speed and pause status. The overlay automatically hides while recording is active so it never appears in the output video.

## Free Camera

Pressing `C` toggles an unconstrained free camera:
- Requires a camera tagged `MainCamera` (`Camera.main`).
- **Cinemachine Path** (`#if NEXUS_CINEMACHINE`, Cinemachine 6.6.0+): When `Camera.main` has a `CinemachineBrain`, adds a `CinemachineCamera` with priority set higher than all existing cameras (`maxPriority + 1`), adopts the brain's current lens, and clears follow/look targets.
- **Fallback Camera Path**: Instantiates a camera copying properties from `Camera.main` and disables the main camera component. Under `#if NEXUS_URP` (`com.unity.render-pipelines.universal` 17.6.0+), copies `UniversalAdditionalCameraData` settings (post-processing, anti-aliasing, shadow rendering, color/depth options, volume layer mask) and makes the free camera its own volume trigger.
- **Controls** (`FreeCameraMover.cs`):
  - **Right Mouse Button (Hold)**: Look rotation clamped to ±89° pitch, sensitivity `0.1f`, sharpness `20f`.
  - **W / A / S / D**: Local translation (forward, left, backward, right).
  - **Q / E**: World vertical translation (down / up).
  - **Left / Right Shift**: 4x speed multiplier.
  - **Mouse Scroll Wheel**: Adjusts base movement speed between `0.5f` and `50f` (default `5f`, step `±1f`).
  - Movement and look damping use exponential decay against `Time.unscaledDeltaTime`, ensuring responsive and smooth camera motion even when playback is slowed or paused.

On close or toggle off, the free camera GameObject is destroyed and the main camera component is re-enabled.

## Recording

Capture Mode drives Unity Recorder 5.1.8 (`Editor/Recording/CaptureRecorder.cs`):
- **Trigger**: Toggle via `F10` or the Record button in the Nexus window tab.
- **Resolution**: Configured in `CaptureSettings.asset` under `ResolutionPresets`. Defaults are `Vertical` (1080x1920) and `Horizontal` (1920x1080).
- **Frame Rate**: constant, `FrameRate` in `CaptureSettings` (default 60; `FrameRatePlayback.Constant`, `CapFrameRate = true`).
- **Encoding**: MP4 (`CoreEncoderSettings.OutputCodec.MP4`, High quality) with audio preserved (`PreserveAudio = true`).
- **Output Path**: `<projectRoot>/<outputFolder>/<GameName>/<yyyyMMdd_HHmmss>.mp4`:
  - `<outputFolder>`: defaults to `"Recordings"` (configured in settings).
  - `<GameName>`: `ICaptureHooks.GameName`, falling back to `Application.productName`, sanitized for filenames.
  - `<yyyyMMdd_HHmmss>`: capture start timestamp.
- **.gitignore Advice**: Add `/Recordings/` to your project's `.gitignore` to keep large video assets out of version control.
- **Lifecycle**: Recording automatically stops when Capture Mode is closed or when exiting Play Mode. A failed recorder start is logged with `NexusLog.WarnAlways` and leaves Capture Mode in an unrecorded state.

## Ad Protection

Under `#if NEXUS_ADS` (`com.nexus.ads` 0.1.0+):
- Opening Capture Mode suppresses all ad formats via `AdController.SetSuppressed(true)` on `AdsBootstrap.Instance.Controller`.
- `CaptureAdSuppression.Tick()` tracks late-starting controllers so ads initialized during an active capture take are immediately suppressed.
- Preserves external suppression: only lifts suppression on close (`SetSuppressed(false)`) if Capture Mode originally applied it.
- Suppression is cleared when closing Capture Mode or exiting Play Mode.

## Game Hooks

Games provide custom capture integration by implementing `ICaptureHooks` (`Nexus.Capture`):

```csharp
public interface ICaptureHooks
{
    string GameName { get; }
    void SetGodMode(bool on);
    int LevelCount { get; }
    void JumpToLevel(int index);
    IReadOnlyList<string> Presets { get; }
    void ApplyPreset(int index);
}
```

Register the hook implementation in an editor-only assembly:

```csharp
public sealed class MyGameCaptureHooks : ICaptureHooks
{
    public string GameName => Application.productName;
    public void SetGodMode(bool on) => PlayerHealth.Invulnerable = on;
    public int LevelCount => SceneManager.sceneCountInBuildSettings;
    public void JumpToLevel(int index) => SceneManager.LoadScene(index);
    public IReadOnlyList<string> Presets => new[] { "Restart level", "Max Resources" };
    public void ApplyPreset(int index) { /* apply scenario */ }

    // Survives domain reload across Play Mode sessions
    [UnityEditor.InitializeOnLoadMethod]
    private static void Register() => CaptureModeService.RegisterHooks(new MyGameCaptureHooks());
}
```

A complete reference sample is provided in `Samples~/CaptureHooks` (`ExampleCaptureHooks.cs`). When no hooks are registered, hook-dependent shortcuts and controls remain disabled without throwing errors.

## What is Not Done Yet

- **Owner verification**: Physical checks of recorded video output quality and free-camera smoothness remain to be verified by the repository owner in the Unity Editor.
- **Cinemachine path**: The Cinemachine 6.6.0+ path is compile-proven only (`Nexus/artifacts/cinemachine-20261112/`); no test or live scene has run it yet.
