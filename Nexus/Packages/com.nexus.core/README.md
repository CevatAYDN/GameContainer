# Nexus Core

[![Unity](https://img.shields.io/badge/Unity-6000.0-blue.svg)](https://unity.com/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Version](https://img.shields.io/badge/Version-0.3.2-orange.svg)](package.json)

**Nexus Core** is a modern, high-performance MVCS (Model-View-Controller-Service) architecture framework for Unity 6. It provides observable models, dependency injection, signal-based communication, and comprehensive service lifecycle management with zero-GC allocation in steady-state operations.

## 🌟 Key Features

- **0-GC Allocation**: Steady-state allocation-free signal dispatch and property updates
- **AOT/IL2CPP Ready**: Code generation bypasses reflection for console and WebGL builds
- **4 Execution Modes**: Sequential, Concurrent, Exclusive, and Composite signal dispatch
- **Causal Tracing**: Production-ready signal flow tracing and debugging
- **Build Validation**: CI/CD-friendly assembly validation and compile-time checks
- **Reactive Models**: `ObservableProperty<T>` with automatic change notifications
- **Service Lifecycle**: Automatic initialization and disposal of service singletons
- **Game Manager Editor**: Visual tools for dependency exploration and runtime inspection
- **Network Support**: Built-in netcode support for multiplayer scenarios
- **DOTS Bridge**: Seamless integration with Unity ECS

## 📦 Installation

### Via Unity Package Manager

1. Open Unity Package Manager (Window > Package Manager)
2. Click the `+` button and select "Add package from git URL"
3. Enter: `https://gitlab.com/beehivegame/GameContainer.git?path=Nexus/Packages/com.nexus.core`

### Via Packages/manifest.json

Add to your `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.nexus.core": "https://gitlab.com/beehivegame/GameContainer.git?path=Nexus/Packages/com.nexus.core#0.3.2"
  }
}
```

## 🚀 Quick Start

### 1. Create Your First Signal

Define a struct signal (value type for zero-GC):

```csharp
public struct CounterSignal
{
    public int Value;
    public CounterSignal(int value) => Value = value;
}
```

### 2. Create a Command

```csharp
public class IncrementCommand : ICommand<CounterSignal>
{
    [Inject] private CounterModel _model;
    
    public void Execute(CounterSignal signal)
    {
        _model.Count.Value += signal.Value;
    }
}
```

### 3. Create a Reactive Model

```csharp
public class CounterModel : IReactiveModel
{
    public readonly ObservableProperty<int> Count = new(0);
    
    public ValueTask OnBind(CancellationToken ct)
    {
        // Initialize after dependency injection
        return default;
    }
}
```

### 4. Configure the Context

```csharp
public class GameLifecycle : IContextLifecycle
{
    public void OnConfigure(IContextBuilder builder)
    {
        // Bind model
        builder.BindReactiveModel<CounterModel>();
        
        // Bind command
        builder.BindCommand<CounterSignal, IncrementCommand>(
            ExecutionMode.Sequential, 
            priority: 0
        );
    }
    
    public ValueTask OnInitializeAsync(CancellationToken ct)
    {
        Debug.Log("Game initialized");
        return default;
    }
    
    public ValueTask OnStartAsync(CancellationToken ct)
    {
        Debug.Log("Game started");
        return default;
    }
    
    public void OnDispose()
    {
        Debug.Log("Game disposed");
    }
}
```

### 5. Create a Root GameObject

1. Create an empty GameObject in your scene
2. Add the `Root` component
3. Assign your `ContextData` ScriptableObject with the lifecycle
4. Add your assemblies to scan (optional)

### 6. Fire Signals

```csharp
// From anywhere in your game
var context = NexusRuntime.CurrentContext;
context.SignalBus.Fire(new CounterSignal(1));

// Or fire asynchronously
await context.SignalBus.FireAsync(new CounterSignal(1));

// Or fire and forget
await context.SignalBus.FireAsyncAndForget(new CounterSignal(1));
```

## 🏗️ Architecture

### MVCS Pattern

Nexus Core implements the Model-View-Controller-Service pattern:

- **Model**: Data containers with `ObservableProperty<T>` for reactive updates
- **View**: UI components implementing `IView`
- **Controller/Command**: Business logic executed in response to signals
- **Service**: Global singletons for cross-cutting concerns (audio, localization, etc.)

### Dependency Injection

NexusDI provides constructor, field, property, and method injection:

```csharp
public class PlayerController
{
    [Inject] private PlayerModel _model;
    [Inject] private IAudioService _audio;
    
    public PlayerController(IInputService input)
    {
        // Constructor injection
    }
    
    [Inject]
    public void SetAnalytics(IAnalyticsService analytics)
    {
        // Method injection
    }
}
```

### Signal Bus

The SignalBus provides type-safe, observable event dispatch:

```csharp
// Subscribe to signals
var subscription = signalBus.Subscribe<CounterSignal>(signal =>
{
    Debug.Log($"Counter changed: {signal.Value}");
});

// Unsubscribe when done
subscription.Dispose();
```

### Execution Order Guarantee

When a signal is fired, Nexus guarantees the following execution order:

```
Signal Fired
  │
  ├─ 1. Plugin Interceptors (may cancel dispatch)
  ├─ 2. Cross-Context Broadcast
  ├─ 3. Commands (mutate model state — execute in priority order)
  └─ 4. Subscriptions (observe final state — read post-command model)
```

**This means**: Mediator subscription handlers always observe the **final** model state after all commands have executed. You never need workaround signals like "XCompletedSignal" — simply subscribe to the original signal and read the model directly.

```csharp
// Command modifies state FIRST
public class IncrementCommand : ICommand<CounterSignal>
{
    [Inject] private CounterModel _model;
    public void Execute(CounterSignal signal) => _model.Count.Value += signal.Value;
}

// Subscription observes state AFTER command
signalBus.Subscribe<CounterSignal>(signal =>
{
    // _model.Count.Value is already updated here
    Debug.Log($"New count: {model.Count.Value}");
});
```

### Execution Modes

Commands can execute in different modes:

- **Sequential**: Commands execute one-by-one in priority order (default)
- **Concurrent**: Commands execute in parallel (for independent operations)
- **Exclusive**: Guarantees a single handler runs at a time
- **Composite**: Fires only after all required signals are received (fan-in)

```csharp
builder.BindCommand<CounterSignal, IncrementCommand>(
    ExecutionMode.Concurrent, 
    priority: 10
);
```

## 🔧 Services

Nexus Core includes built-in services for common game systems:

### Audio Service

```csharp
[Inject] private IAudioService _audio;

_audio.PlaySfx(clickClip, volume: 0.8f);
_audio.PlayBgm(backgroundMusic, loop: true);
```

### Localization Service

```csharp
[Inject] private ILocalizationService _localization;

_localization.SetLanguage("tr");
string localizedText = _localization.GetText("menu_start");
```

### Tick Service

```csharp
[Inject] private ITickService _tickService;

// Register for update callbacks
_tickService.RegisterTickable(this);
```

### Storage Service

```csharp
[Inject] private IPlayerPrefsService _prefs;

_prefs.SetInt("high_score", 1000);
int score = _prefs.GetInt("high_score", 0);
```

### Feedback Service

```csharp
[Inject] private FeedbackService _feedback;

_feedback.Play(FeedbackPreset.LightClick);
```

## 🧪 Testing

Nexus Core provides testing utilities:

```csharp
[TestFixture]
public class CounterTests
{
    [Test]
    public async Task IncrementCommand_IncreasesCount()
    {
        var context = await NexusTestHarness.CreateContextAsync();
        var model = context.Resolve<CounterModel>();
        
        context.SignalBus.Fire(new CounterSignal(5));
        
        Assert.AreEqual(5, model.Count.Value);
    }
}
```

## 🎨 Editor Tools

### Nexus Window

Access via `Window > Nexus > Dashboard`:

- **Dashboard**: Overview of active contexts, services, and metrics
- **Explorer**: Visual dependency graph and type analysis
- **Tracer**: Real-time signal flow visualization
- **Game Manager**: Runtime model inspection and debugging
- **Hierarchy**: Quick context creation and management

### Code Generation

The AOT binder (injectors + `link.xml`) is regenerated **automatically before every build** via the `NexusBuildPreProcessor` hook, so a stale binder never ships in an IL2CPP build. You can also trigger it manually or on script reload:

1. Build-time: runs automatically in `OnPreprocessBuild` (no action needed).
2. On script reload: toggle `Nexus > Auto-Generate AOT on Script Reload`.
3. Manual: `Nexus > Generate AOT Binder` (also available as the `⚡ CodeGen` button in the Nexus window).

Opt out of build-time generation with the `NEXUS_DISABLE_AUTOGEN=1` environment variable (mirrors `NEXUS_DISABLE_VALIDATION`).

Generated files (paths are configurable in `Nexus > Editor Settings`; defaults shown):
- `Assets/Scripts/Nexus/NexusGeneratedBinder.g.cs` - Injectors
- `Assets/Scripts/Nexus/link.xml` - IL2CPP preservation

### Build Validation

Automatic validation catches common issues:

- Value type injection errors
- Circular dependencies
- Missing lifecycle implementations
- Command interface conflicts

## 📊 Performance

### Memory Allocation

- **Signal Dispatch**: 0 allocations (steady-state)
- **Property Updates**: 0 allocations (steady-state)
- **Command Execution**: 0 allocations (with object pooling)
- **AOT Mode**: Reflection-free injection

### Benchmarks

Measured in `Nexus.Tests` (editor/Mono, PlayMode, v0.3.1, after JIT warmup). IL2CPP/Release builds are significantly faster.

- **Signal Fire**: ~9.5µs hot path / ~11.8µs with 1 subscriber (measured; a >25µs / >30µs regression trips the P2-C benchmark tests)
- **Property Change**: ~30ns per subscriber *(not yet measured)*
- **DI Resolve**: ~100ns (cached), ~500ns (first resolve) *(not yet measured)*

## 🔒 AOT/IL2CPP Support

Nexus Core is fully AOT-compatible:

1. The AOT binder regenerates automatically before each build (see Code Generation).
2. Build for target platform (iOS, Android, WebGL, etc.)
3. Generated `link.xml` prevents code stripping

## 🌐 Network Support

For multiplayer scenarios:

```csharp
public struct PlayerPositionSignal : INetworkSignal
{
    public int PlayerId;
    public Vector3 Position;
}

// Automatically replicated to all clients
context.SignalBus.Fire(new PlayerPositionSignal { ... });
```

## 📚 Documentation

- [Migration Guide](MIGRATION.md) - Version upgrade guide
- [Troubleshooting Guide](TROUBLESHOOTING.md) - Common issues and solutions
- [API Reference](https://gitlab.com/beehivegame/GameContainer/blob/main/README.md) - Full API documentation
- [Samples](Samples~/Counter/README.md) - Example implementations

## 🤝 Contributing

Contributions are welcome! Please follow these guidelines:

1. Fork the repository
2. Create a feature branch
3. Add tests for new functionality
4. Ensure all tests pass
5. Submit a merge request

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 📞 Support

- **Issues**: [GitLab Issues](https://gitlab.com/beehivegame/GameContainer/-/issues)
- **Merge Requests**: [GitLab Merge Requests](https://gitlab.com/beehivegame/GameContainer/-/merge_requests)

## 🔄 Continuous Integration

Every push and pull request to `main` runs [GitHub Actions CI](../../../.github/workflows/ci.yml):

- **EditMode tests** — including `BuildWiringTests`, which regenerates the AOT binder (`NexusGeneratedBinder.g.cs` + `link.xml`) before asserting the build wiring is correct.
- **PlayMode tests** — the full runtime suite (signal dispatch, commands, models, services).
- **Architecture Validation** — runs as part of the test/build pipeline.
- **Doc/code consistency guard** — fails the build if the README still references stale execution-mode names or if its version badge drifts from `package.json`.

The AOT binder regenerates automatically before each Unity build (see *Code Generation* above), so a stale binder never ships in an IL2CPP build. CI runs on Unity `6000.5.0f1` (Unity 6).

---

## 🙏 Acknowledgments

- Inspired by StrangeIOC and Zenject
- Built for Unity 6 and modern C#
- Optimized for mobile and console platforms

---

**Built with ❤️ for the Unity community**
