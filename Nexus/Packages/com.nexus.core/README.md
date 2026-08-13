> **For AI Agents:** This document is the source of truth for the Nexus Core architecture, module organization, and developer workflows. Before generating game code or framework modifications:
> 1. Read [AI_GAME_DEVELOPER_GUIDE.md](docs/AI_GAME_DEVELOPER_GUIDE.md) and [GAME_PATTERNS.md](docs/GAME_PATTERNS.md) for game creation rules
> 2. Read [ARCHITECTURE.md](docs/ARCHITECTURE.md), [PLUGIN_DEVELOPMENT.md](docs/PLUGIN_DEVELOPMENT.md), and [CONTRIBUTING.md](docs/CONTRIBUTING.md) for framework core details
> 3. Check the [Anti-Patterns](#anti-patterns-do-not-do) section below
> 4. Run tests: Unity EditMode Test Runner (`PluginRefactorValidationTests`, `InfrastructureValidationTests`)
> 5. Validate build: `BuildValidation.RunSilent()`
>
> **Out of scope:** Third-party package implementations, custom game gameplay code.

# Nexus Core

[![Unity](https://img.shields.io/badge/Unity-6000.0-blue.svg)](https://unity.com/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Version](https://img.shields.io/badge/Version-0.4.0-orange.svg)](package.json)

**Nexus Core** is a modern, high-performance MVCS (Model-View-Controller-Service) architecture framework for Unity 6. It provides observable reactive models, dependency injection, signal-based communication, and comprehensive editor tools with zero-GC allocation in steady-state operations.

---

## 🚀 Quick Start

| 🇬🇧 English | 🇹🇷 Türkçe |
|------------|----------|
| [10-Minute Quickstart Guide](docs/10_MIN_QUICKSTART.md) | [10 Dakikada Hızlı Başlangıç](docs/10_MIN_QUICKSTART_TR.md) |

**Prefer a hands-on example?** Install the [Counter Sample](Samples~/Counter/README.md) via Package Manager → Nexus → Samples. It is the canonical onboarding sample; `NexusStarter` is a scaffold template for greenfield bootstrapping.

---

## ⭐ Strategic Architecture Features

Nexus Core includes 5 strategic architectural capabilities:

1. **Attribute-Based Command Auto-Discovery (`[RegisterCommand]`)**: Decorate command classes with `[RegisterCommand(typeof(MySignal))]` for automatic signal binding.
2. **Convention-Based Binding (`BindInterfacesAndSelfTo<T>()`)**: Automatically bind concrete classes under all user interfaces AND their concrete type sharing one singleton.
3. **Flexible Domain Lifecycles (`IStartable`, `IAsyncStartable`, `IStoppable`, `IAsyncStoppable`)**: Provide startup and teardown lifecycle hooks for non-service domain objects.
4. **Scene & Prefab Auto-Injection (`NexusBinding`)**: Attach `NexusBinding` MonoBehaviour to GameObjects or Prefabs for zero-code scene injection.
5. **Zero-GC Hot Paths**: All capabilities execute with zero-GC steady-state allocation guarantees.

---

## ⚡ Framework Comparison Matrix

| Feature / Metric | **Nexus Core** | Zenject / Extenject | VContainer | UniRx / R3 | StrangeIoC |
|---|:---:|:---:|:---:|:---:|:---:|
| **Steady-State GC Allocations** | **0 Bytes** | High | Low | Low/Moderate | High |
| **AOT / IL2CPP Binder Generator** | ✅ Built-in | ❌ Reflection | ✅ CodeGen | ❌ N/A | ❌ Reflection |
| **SignalBus & Command Pipeline** | ✅ 4 Execution Modes | ✅ Basic | ❌ Missing | ❌ N/A | ✅ Basic |
| **Observable Reactive Models** | ✅ `ObservableProperty` | ❌ Requires UniRx | ❌ Missing | ✅ Stream-based | ❌ Missing |
| **Out-of-the-Box Engine Services** | ✅ 14 Core Services | ❌ Missing | ❌ Missing | ❌ N/A | ❌ Missing |
| **RAM Anti-Cheat & Storage Encryption** | ✅ Built-in AES-256 | ❌ Missing | ❌ Missing | ❌ N/A | ❌ Missing |
| **Live Editor Play-Mode Dashboard** | ✅ 16 Plugins | ❌ Basic Inspector | ❌ Basic Diagnostic | ❌ N/A | ❌ Missing |
| **Build Validation & Diagnostics** | ✅ Pre-build Rules | ❌ Missing | ❌ Missing | ❌ N/A | ❌ Missing |

---

## 📖 Glossary

| Term | Definition | Concrete Example |
|---|---|---|
| **Context** | A scoped container for models, signals, commands, services. Has its own DI container and lifecycle. | `Context ctx = new Context(parentCtx, ctxData);` |
| **ReactiveModel** | Observable data holder implementing `IReactiveModel`. State changes notify views. | `class PlayerHealth : IReactiveModel { ... }` |
| **Signal** | A struct/class carrying event data. Fired via `SignalBus.Fire(signal)`. | `public struct DamageSignal { public int Amount; }` |
| **Command** | A handler for a signal. Implements `ICommand<TSignal>`. | `class ApplyDamage : ICommand<DamageSignal> { ... }` |
| **Service** | Long-lived singleton service implementing `INexusService`. | `class AudioService : INexusService { ... }` |
| **View** | UI/MonoBehaviour bound to a context via a Mediator. | `class HudView : View { ... }` |
| **Pure Context** | A context without a Unity MonoBehaviour anchor, created programmatically. | `NexusRuntime.CreatePureContextAsync("TestContext")` |
| **Root Context** | A context anchored to a `Root` MonoBehaviour component in a scene. | Auto-created when `Root` component enables. |
| **Plugin** | Editor tool extending `NexusWindow`. Implements `INexusEditorPlugin`. | `class TracerPlugin : NexusEditorPlugin { ... }` |

---

## 🗺️ File Map

| Path | Responsibility | Lines | Key Exports / API |
|---|---|---|---|
| `Runtime/Core/NexusRuntime.cs` | Global context registry, Reset, lifecycle orchestration | ~600 | `ActiveContexts`, `Reset()`, `CreatePureContextAsync()` |
| `Runtime/Core/Context.cs` | Single context instance, DI container, signal bus binding | ~650 | `Configure()`, `InitializeLifecycleAsync()` |
| `Runtime/Core/SignalBus.cs` | Signal dispatch, command execution pipelines | ~1845 | `Fire<T>()`, `BroadcastCrossContext()` |
| `Runtime/Core/Root.cs` | Scene-anchored context entry point | ~400 | `OnEnable`, `RootContext` |
| `Editor/Core/NexusWindow.cs` | Host shell for editor suite, tab management | ~676 | `SwitchToPlugin()`, `Instance` |
| `Editor/Core/INexusEditorPlugin.cs` | Plugin interface contract and base class | ~56 | `INexusEditorPlugin`, `NexusEditorPlugin` |
| `Editor/Plugins/*.cs` | 16 integrated editor plugin implementations | varies | `DashboardPlugin`, `TracerPlugin`, `GameManagerPlugin` |
| `Editor/Core/NexusEditorStyles.cs` | Shared UI Toolkit styles, colors, stat tiles | ~650 | `CreateStatTile()`, `CreateStatusDot()` |
| `Editor/Core/NexusLang.cs` | Framework localization manager (en + tr) | ~770 | `Get(key)`, `CurrentLocale` |
| `Editor/Validation/BuildValidation.cs` | Pre-build checks and architectural rules engine | ~1141 | `RunSilent()`, `LastResults` |
| `Editor/CodeGen/NexusCodeGenerator.cs` | Code generator engine for Wizard plugin | ~400 | `GenerateBinder()` |
| `Tests/Editor/PluginRefactorValidationTests.cs` | NUnit tests for editor plugin lifecycles | ~60 | `Tracer_ListView_ConfiguredWithItemHeight` |
| `Tests/Editor/InfrastructureValidationTests.cs` | NUnit tests for build wiring and AOT binder | ~60 | `ArchitectureValidation_Passes` |

---

## 🚀 Decision Flows

### Scenario 1: "I want to add a new reactive model"
1. Define class: `public class PlayerHealthModel : IReactiveModel { public readonly ObservableProperty<int> Health = new(100); public ValueTask OnBind(CancellationToken ct) => default; }`
2. Registration: Handled automatically by `Context` assembly scan or bind via `builder.BindReactiveModel<PlayerHealthModel>()`.
3. Injection: Inject where needed: `public IncrementCommand(PlayerHealthModel health) { _health = health; }`
4. Verification: Write unit test asserting property change listeners fire correctly.
**Reference:** [ARCHITECTURE.md §ReactiveModel](docs/ARCHITECTURE.md#key-components)

### Scenario 2: "I want to add a new signal + command"
1. Define signal struct: `public struct PlayerScoredSignal { public int Points; }`
2. Define command:
   ```csharp
   public class HandlePlayerScored : ICommand<PlayerScoredSignal>
   {
       [Inject] public ScoreModel Score { get; set; } // Canonical AOT-optimal style
       public void Execute(PlayerScoredSignal signal) => Score.CurrentScore.Value += signal.Points;
   }
   ```
3. Register command in context: `builder.BindCommand<PlayerScoredSignal, HandlePlayerScored>(ExecutionMode.Sequential);`
4. Dispatch signal: `context.SignalBus.Fire(new PlayerScoredSignal { Points = 10 });`
**Reference:** [ARCHITECTURE.md §SignalBus](docs/ARCHITECTURE.md#key-components)

### Scenario 3: "I want to add a new editor plugin"
1. Create file `Editor/Plugins/MyNewPlugin.cs` extending `NexusEditorPlugin`.
2. Set properties: `Id`, `DisplayName`, `Order`.
3. **MUST** override `OnUpdate()` — **DO NOT** use `_view.schedule` or `EditorApplication.update`.
4. **MUST** reset state in `OnDisable()` (flags, counters, queues).
5. **MUST** use `NexusLang.Get(...)` for all user-facing text.
6. **MUST** use the shared `NexusEditorStyles` stat helpers for metric cards and summary tiles, not bespoke stat components.
7. **MUST** add NUnit lifecycle test in `Tests/Editor/PluginRefactorValidationTests.cs`.
**Reference:** [PLUGIN_DEVELOPMENT.md](docs/PLUGIN_DEVELOPMENT.md)

### Scenario 4: "I want to add a new localization key"
1. Pick namespace prefix: `dash_` (Dashboard), `tracer_` (Tracer), `gm_` (GameManager), `wizard_`, `ci_` (ContextInspector).
2. Add default English entry in `NexusLang.cs` `AddDefaults()`.
3. Add Turkish override entry in `Editor/Locales/tr.json`.
4. Consume in C# code: `NexusLang.Get("gm_new_key_name")`.
**Reference:** [LOCALIZATION_KEYS.md](docs/LOCALIZATION_KEYS.md)

---

## 🚫 Anti-Patterns (DO NOT DO)

### ❌ Hardcoded User-Facing Strings
```csharp
// BAD:
filterBar.Add(new Button { text = "OK" });

// GOOD:
filterBar.Add(new Button { text = NexusLang.Get("tracer_status_ok") });
```
*Why:* Hardcoded strings bypass `NexusLang` and break English/Turkish localization.

### ❌ Custom UI Schedulers in Plugins
```csharp
// BAD:
_refreshSchedule = _view.schedule.Execute(RefreshStats).Every(1000);

// GOOD:
public override void OnUpdate() => RefreshStats();
```
*Why:* Custom schedules continue running when tab is hidden, causing background CPU consumption. `OnUpdate` is window-managed.

### ❌ Empty `catch {}` Blocks
```csharp
// BAD:
try { assembly.GetTypes(); } catch { }

// GOOD:
try { assembly.GetTypes(); } 
catch (ReflectionTypeLoadException ex) { 
    foreach (var le in ex.LoaderExceptions.Where(e => e != null))
        Debug.LogWarning($"[Nexus] Type load failed: {le.Message}");
}
catch (Exception ex) {
    Debug.LogWarning($"[Nexus] Assembly scan failed: {ex.Message}");
}
```
*Why:* Silent exception swallowing hides broken assemblies from developers during debugging.

### ❌ Hot-Path Reflection Without Caching
```csharp
// BAD:
private void FireSignal(Type signalType) {
    var method = GetType().GetMethod("Fire").MakeGenericMethod(signalType); // Executed every frame
    method.Invoke(this, new[] { Activator.CreateInstance(signalType) });
}

// GOOD:
private static readonly Dictionary<Type, MethodInfo> s_fireCache = new();
private void FireSignal(Type signalType) {
    if (!s_fireCache.TryGetValue(signalType, out var method)) {
        method = GetType().GetMethod("Fire").MakeGenericMethod(signalType);
        s_fireCache[signalType] = method;
    }
    method.Invoke(this, new[] { Activator.CreateInstance(signalType) });
}
```
*Why:* Uncached reflection lookups on hot paths cause major GC allocations and frame rate drops.

### ❌ State Leak Across `OnDisable`/`OnEnable`
```csharp
// BAD:
public override void OnDisable() { 
    base.OnDisable();
    // Flags, counters, and queues NOT reset
}

// GOOD:
public override void OnDisable() { 
    _hasLiveEvents = false;
    _eventCounter = 0;
    while (_queue.TryDequeue(out _)) { }
    base.OnDisable();
}
```
*Why:* Retaining stale queue or flag state across tab switches causes inconsistent behavior on re-enable.

### ❌ Full `ScrollView` Rebuild on Update Tick
```csharp
// BAD:
_scrollView.Clear();
foreach (var item in items) _scrollView.Add(new ItemElement(item)); // Allocates 200 elements per tick

// GOOD:
_listView = new ListView { fixedItemHeight = 28f };
_listView.makeItem = () => new ItemElement();
_listView.bindItem = (el, i) => ((ItemElement)el).Bind(items[i]);
_listView.itemsSource = items;
_listView.Rebuild();
```
*Why:* Allocating hundreds of `VisualElement` nodes every update tick creates heavy GC pressure and loses scroll position.

---

## 🛠️ Editor Plugins (Nexus Suite)

Access all tools via **Window > Nexus > Dashboard** (`Ctrl+Shift+N` / `Cmd+Shift+N`).

1. **Overview**: `Dashboard` (System status & QuickFind), `GameManager` (Central hub for models, signals & live metrics).
2. **Architecture**: `Hierarchy` (Context tree), `Explorer` (Signal wiring), `Graph` (Visual graph), `TypeAnalyzer` (Assembly inspector), `ContextInspector` (Root inspector), `FSM` (State machine manager).
3. **Diagnostics**: `Tracer` (Virtualized causal trace log), `ErrorDashboard` (Build & runtime log hub), `PerformanceDashboard` (FPS & GC profiler).
4. **Tools & Services**: `Wizard` (Boilerplate code generator), `CasualServices` (Economy & window debug helper), `Help` (Documentation viewer).

---

---

## 🛡️ Security & Adversarial Audit Fixes (v0.4.1+)

Nexus Core underwent a comprehensive adversarial code audit (2026) following the **Zero-Complacency Adversarial Audit Protocol**. All critical and high-severity findings were resolved:

### Critical Fixes
- **Reentrancy Counter Drift** (`SignalBus.cs`): `enteredDepth` flag prevents negative counter drift on overflow
- **Singleton Construction Race** (`NexusDI.cs`): `ManualResetEventSlim` replaces spin-wait for atomic publication
- **Unsubscribe-Dispatch TOCTOU** (`SubscriptionRegistry.cs`): `SubscriptionNode.Reset()` preserves `Next` for safe iteration
- **Exception Loss in Recovery** (`RecoveryEngine.cs`): Both strategy + original exceptions collected in `ErrorCollection`
- **PostContext Builder Mismatch** (`Context.cs`): `_configuredBuilder` tracks exact builder used during `Configure()`
- **Metrics Rate Race** (`NexusRuntime.cs`): `_lastSampleTime` read inside lock for ARM weak memory safety
- **Logger Cache Race** (`NexusRuntime.cs`): Lock-based cache replaces `Volatile.Read/Write`
- **Decorator Allocation** (`CommandExecutor.cs`): generic-sync dispatch keeps the zero-closure call pattern — an inline lambda there hoists a closure display class (~56 B/dispatch, proven by the harness + IL dump); decorator chains compose on demand
- **LazyInjection Race** (`NexusDI.cs`): Double-checked locking for thread-safe lazy instance creation
- **Composite Payload Sharing** (`SignalBus.cs`): Per-trigger boxing eliminates shared reference risk
- **Fallback Infinite Loop** (`RecoveryEngine.cs`): `_fallbackDepth` counter (max 3) + negative priority
- **One-Shot Lost Retry** (`CommandRegistry.cs`): Claim marks handler but keeps in list until success
- **Trace Buffer Resize Race** (`NexusRuntime.cs`): Versioned buffer swap with `Volatile` for lock-free readers

### Security Hardening
- **DI Validation in ALL Builds**: `ContextBuilder.Validate()` runs in production (not just editor)
- **Captive Dependency Detection**: `DiValidationIssueType.CaptiveDependency` reported for singleton→transient capture
- **Reentrancy Guard Throws Everywhere**: `NexusReentrancyException` on stack overflow in Debug + Release
- **Async Overflow Guard Throws Everywhere**: `NexusAsyncOverflowException` unified in `EnterAsyncInFlight()`
- **Atomic Save Writes**: `EncryptedStorageService` uses `File.Replace` for crash-safe saves
- **Hardware-Tick Anti-Cheat**: `OfflineTimeCalculator` clamps to a hardware monotonic tick (`Stopwatch.GetTimestamp()`-derived ms since boot)

### Zero-GC Improvements
- **Decorator Chain Composition**: per-plugin decorator lists flattened into one reversed chain preserving execution order (still composed per execution when decorators are present)
- **Logger Cache Locking**: Thread-safe logger access during context lifecycle changes
- **LazyInjection Double-Check**: Double-checked locking for lazy instance creation
- **Fallback Depth Limit**: `MaxFallbackDepth = 3` prevents infinite fallback recursion

---

## 🔗 Related Documentation
- 📖 [PLUGIN_DEVELOPMENT.md](docs/PLUGIN_DEVELOPMENT.md) — Writing editor plugins and lifecycle rules
- 🏛️ [ARCHITECTURE.md](docs/ARCHITECTURE.md) — Core runtime architecture & diagrams
- 🌐 [LOCALIZATION_KEYS.md](docs/LOCALIZATION_KEYS.md) — Localization key catalog & guidelines
- 🤝 [CONTRIBUTING.md](docs/CONTRIBUTING.md) — Contribution guidelines and PR checklist
- 📜 [CHANGELOG.md](CHANGELOG.md) — Framework change history

---

**Last updated:** 2026-07-24  
**Code version:** 0.4.0  
**Maintainers:** Nexus Core Team  
**Re-review trigger:** Any change to `Runtime/Core/`, `Editor/Core/`, or `Editor/Plugins/`.
