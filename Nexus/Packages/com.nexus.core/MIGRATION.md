# Migration Guide

This guide helps you migrate between versions of Nexus Core. Follow the instructions for the version you're upgrading from.

## Table of Contents

- [Version 0.2.0 → 0.3.0](#version-020--030)
- [Version 0.1.0 → 0.2.0](#version-010--020)
- [General Migration Tips](#general-migration-tips)

---

## Version 0.2.0 → 0.3.0

### Breaking Changes

#### 1. IReactiveModel Signature Change

**Before:**
```csharp
public class PlayerModel : IReactiveModel
{
    public void OnBind(IContext context)
    {
        // Initialization logic
    }
}
```

**After:**
```csharp
public class PlayerModel : IReactiveModel
{
    public ValueTask OnBind(CancellationToken ct)
    {
        // Initialization logic (can be async)
        return default;
    }
}
```

**Migration Steps:**
1. Update all `IReactiveModel` implementations
2. Change return type from `void` to `ValueTask`
3. Change parameter from `IContext` to `CancellationToken`
4. Add `return default;` at the end of synchronous implementations
5. For async initialization, use `await` and return `ValueTask`

#### 2. Service Interface Updates

**IPlayerPrefsService - Added GetBool/SetBool:**

**Before:**
```csharp
public class MockPlayerPrefs : IPlayerPrefsService
{
    public void SetString(string key, string value) { }
    public string GetString(string key, string defaultValue = "") => defaultValue;
    public void SetInt(string key, int value) { }
    public int GetInt(string key, int defaultValue = 0) => defaultValue;
    public void SetFloat(string key, float value) { }
    public float GetFloat(string key, float defaultValue = 0f) => defaultValue;
    public bool HasKey(string key) => false;
    public void DeleteKey(string key) { }
    public void DeleteAll() { }
    public void Save() { }
}
```

**After:**
```csharp
public class MockPlayerPrefs : IPlayerPrefsService
{
    public void SetString(string key, string value) { }
    public string GetString(string key, string defaultValue = "") => defaultValue;
    public void SetInt(string key, int value) { }
    public int GetInt(string key, int defaultValue = 0) => defaultValue;
    public void SetFloat(string key, float value) { }
    public float GetFloat(string key, float defaultValue = 0f) => defaultValue;
    public bool GetBool(string key, bool defaultValue = false) => defaultValue; // NEW
    public void SetBool(string key, bool value) { } // NEW
    public bool HasKey(string key) => false;
    public void DeleteKey(string key) { }
    public void DeleteAll() { }
    public void Save() { }
}
```

**IAudioService - Added PlaySfxAtPosition:**

**Before:**
```csharp
public class MockAudio : IAudioService
{
    public void PlaySfx(AudioClip clip, float volume = 1f, float pitchMin = 1f, float pitchMax = 1f) { }
    // ... other methods
}
```

**After:**
```csharp
public class MockAudio : IAudioService
{
    public void PlaySfx(AudioClip clip, float volume = 1f, float pitchMin = 1f, float pitchMax = 1f) { }
    public void PlaySfxAtPosition(AudioClip clip, Vector3 position, float volume = 1f) { } // NEW
    // ... other methods
}
```

**IHapticService - Added IsEnabled Property:**

**Before:**
```csharp
public class MockHaptic : IHapticService
{
    public void Vibrate(HapticType type = HapticType.Light) { }
}
```

**After:**
```csharp
public class MockHaptic : IHapticService
{
    public bool IsEnabled { get; set; } = true; // NEW
    public void Vibrate(HapticType type = HapticType.Light) { }
}
```

**Migration Steps:**
1. Update all custom service implementations
2. Add the new methods/properties to your mocks
3. Implement the new functionality (can be no-op for mocks)
4. Update test fixtures that use these services

#### 3. MockContext Location Change

**Before:**
```csharp
// Local mock context in test files
private class MockContext : IContext
{
    // Implementation
}
```

**After:**
```csharp
// Use centralized MockContext from Nexus.Core
using Nexus.Core;

var context = new MockContext();
```

**Migration Steps:**
1. Remove local MockContext implementations from test files
2. Add `using Nexus.Core;` to test files
3. Use the centralized `MockContext` class
4. Update any custom behavior to use the centralized implementation

### New Features

#### 1. Async Signal Timeout

You can now use timeout-based async signal firing:

```csharp
try
{
    await signalBus.FireAsyncWithTimeout(new PlayerActionSignal(), timeoutMilliseconds: 5000);
}
catch (OperationCanceledException)
{
    // Handle timeout
}
```

#### 2. Enhanced Services

New services are available:

```csharp
// Localization with RTL support
[Inject] private ILocalizationService _localization;
_localization.SetLanguage("ar");
string rtlText = _localization.FormatRTLIfNeeded("مرحبا");

// Feedback presets
[Inject] private FeedbackService _feedback;
_feedback.Play(FeedbackPreset.SuccessFanfare);

// Save throttling
[Inject] private ISaveThrottler _saveThrottler;
_saveThrottler.TryRequestSave(() => SaveGame());
```

#### 3. Editor Plugins

New editor tools available:
- Type Dependency Analyzer
- Enhanced Dashboard
- Network Signal Tracer
- Game Manager Inspector

Access via `Window > Nexus > [Plugin Name]`

### Deprecated Features

None in this version.

---

## Version 0.1.0 → 0.2.0

### Breaking Changes

#### 1. Signal Handler Protection

**Before:**
```csharp
// Could call Fire() on signals with async handlers
signalBus.Fire(new MySignal());
```

**After:**
```csharp
// Must use FireAsync() for signals with async handlers
await signalBus.FireAsync(new MySignal());

// Or fire and forget
await signalBus.FireAsyncAndForget(new MySignal());
```

**Migration Steps:**
1. Identify all signals with async handlers
2. Replace `Fire()` with `FireAsync()` or `FireAsyncAndForget()`
3. Add `await` where results are needed
4. Use `FireAsyncAndForget()` for fire-and-forget scenarios

#### 2. Command Interface Mutual Exclusivity

**Before:**
```csharp
// Could implement both ICommand and IAsyncCommand
public class MyCommand : ICommand<MySignal>, IAsyncCommand<MySignal>
{
    public void Execute(MySignal signal) { }
    public ValueTask ExecuteAsync(MySignal signal, CancellationToken ct) => default;
}
```

**After:**
```csharp
// Must implement only one interface
public class MyCommand : ICommand<MySignal>
{
    public void Execute(MySignal signal) { }
}

// OR
public class MyAsyncCommand : IAsyncCommand<MySignal>
{
    public ValueTask ExecuteAsync(MySignal signal, CancellationToken ct) => default;
}
```

**Migration Steps:**
1. Review all command classes
2. Remove duplicate interface implementations
3. Choose sync or async based on use case
4. Update registrations if needed

#### 3. FireAsyncAndForget Return Type

**Before:**
```csharp
// Returned async void (fire-and-forget)
signalBus.FireAsyncAndForget(new MySignal());
```

**After:**
```csharp
// Returns ValueTask (still fire-and-forget behavior)
await signalBus.FireAsyncAndForget(new MySignal());
```

**Migration Steps:**
1. Add `await` to existing `FireAsyncAndForget()` calls
2. Behavior remains the same (fire-and-forget)
3. Better exception observability with new signature

### New Features

#### 1. AOT Code Generation

Enable automatic binder generation:

```csharp
// In Editor
Menu.SetChecked("Nexus/Auto-Generate AOT on Script Reload", true);
```

Generated files:
- `NexusGeneratedBinder.g.cs` - Reflection-free injectors
- `link.xml` - IL2CPP code stripping prevention

#### 2. Circular Dependency Detection

```csharp
// Now throws InvalidOperationException for circular dependencies
public class ServiceA
{
    [Inject] private ServiceB _b;
}

public class ServiceB
{
    [Inject] private ServiceA _a; // Throws during resolution
}
```

#### 3. Enhanced Memory Management

- ArrayPool integration for concurrent execution
- Static field/property caching in generated binder
- IDisposable singleton tracking

---

## General Migration Tips

### 1. Backup Your Project

Before upgrading:
1. Create a git branch: `git checkout -b backup-before-nexus-upgrade`
2. Commit current state: `git commit -am "Backup before Nexus upgrade"`
3. Test upgrade in a separate branch

### 2. Update Incrementally

For major version jumps:
1. Upgrade one version at a time
2. Test after each upgrade
3. Fix breaking changes before proceeding
4. Document any custom modifications

### 3. Regenerate AOT Binder

After upgrading:
1. Open `Nexus > Generate AOT Binder`
2. Review generated files
3. Commit generated files to version control
4. Update `.gitignore` if needed

### 4. Run Tests

After migration:
1. Run all unit tests
2. Run integration tests
3. Test critical game flows
4. Verify editor tools work correctly

### 5. Check Build Settings

For platform-specific builds:
1. Test iOS/Android builds (AOT)
2. Test WebGL builds (IL2CPP)
3. Verify link.xml is included
4. Check for code stripping issues

### 6. Review Performance

After upgrade:
1. Profile memory allocation
2. Check GC allocation rates
3. Verify signal dispatch performance
4. Monitor DI resolution times

### 7. Update Documentation

After successful migration:
1. Update internal documentation
2. Note any breaking changes
3. Document custom workarounds
4. Share knowledge with team

---

## Need Help?

If you encounter issues during migration:

1. **Check the Troubleshooting Guide** - [TROUBLESHOOTING.md](TROUBLESHOOTING.md)
2. **Review GitHub Issues** - [GitHub Issues](https://github.com/CevatAYDN/Pixel-Flow-Clone/issues)
3. **Ask in Discussions** - [GitHub Discussions](https://github.com/CevatAYDN/Pixel-Flow-Clone/discussions)
4. **Check Sample Projects** - [Samples~/Counter](Samples~/Counter/README.md)

---

**Last Updated:** 2026-07-07
**Nexus Core Version:** 0.3.0
