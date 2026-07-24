> **For AI Agents:** This document is the source of truth for the Nexus Core runtime architecture, container lifecycles, signal dispatch pipelines, and editor host interactions. Before making architectural changes:
> 1. Read [README.md](../README.md#glossary) for domain terminology.
> 2. Inspect [Context.cs](../../Runtime/Core/Context.cs) and [SignalBus.cs](../../Runtime/Core/SignalBus.cs).
> 3. Run EditMode infrastructure tests: `InfrastructureValidationTests`.
>
> **Out of scope:** Third-party package integrations.

# Nexus Architecture Guide

This document describes the high-level architecture, runtime pipeline, component contracts, and lifecycle sequences of the **Nexus Core** framework for Unity 6.

---

## 🏛️ System Architecture Diagram

```
┌───────────────────────────────────────────────────────────┐
│                  Editor Window (NexusWindow)              │
│  ┌──────────┐ ┌──────────┐ ┌───────────┐ ┌───────────┐   │
│  │Dashboard │ │ Tracer   │ │GameManager│ │ Wizard    │   │
│  └─────┬────┘ └─────┬────┘ └─────┬─────┘ └─────┬─────┘   │
│        │            │            │             │         │
│        └────────────┴────────────┴─────────────┘         │
│                          │                               │
│                    ┌─────▼─────┐                         │
│                    │  OnUpdate │ (~200ms window tick)    │
│                    └─────┬─────┘                         │
└──────────────────────────┼───────────────────────────────┘
                           │
┌──────────────────────────▼───────────────────────────────┐
│                     Runtime (Play Mode)                  │
│                                                          │
│  ┌─────────────┐    ┌─────────────┐    ┌──────────────┐  │
│  │    Root     │───▶│   Context   │───▶│  SignalBus   │  │
│  │ (Scene MB)  │    │(DI+Lifecycle│    │ (Dispatch)   │  │
│  └─────────────┘    └──────┬──────┘    └──────┬───────┘  │
│                            │                  │          │
│                     ┌──────▼──────┐    ┌──────▼───────┐  │
│                     │  Services   │    │   Commands   │  │
│                     └─────────────┘    └──────────────┘  │
│                                                          │
│                  ┌──────────────────┐                    │
│                  │ IContextResolver │ (cross-context)    │
│                  └──────────────────┘                    │
└──────────────────────────────────────────────────────────┘
```

---

## 🔄 Lifecycle Sequence

```
CreateView()          OnEnable()           OnUpdate() (~200ms tick)
    │                     │                        │
    ▼                     ▼                        ▼
Build UI Tree         Subscribe events           Read state
Subscribe callbacks   Register listeners         Render changes
Cache references      Load static caches        (Zero-GC steady state)

OnDisable()               Dispose()
    │                        │
    ▼                        ▼
Unsubscribe events    Stop schedulers
Reset flags           Clear collections
Drain queues          Dispose View / Container
Call base             Call base
```

---

## 🔑 Key Components

### 1. `Context` & `IContextLifecycle`
The central dependency container and signal lifecycle manager. Root and sub-contexts configure models, commands, and services via `IContextBuilder`.
- All context types route through `Context.InitializeLifecycleAsync()`.
- **Decision Rationale:** Unified lifecycle initialization guarantees that pure contexts (used in tests) and root contexts (anchored to scene GameObjects) follow the exact same ordering of DI binding, model initialization, and service startup.

### 2. `SignalBus` & `IContextResolver`
Handles signal registration and command execution.
- **Dispatch Modes**:
  - `Sequential`: Executes commands in registration order synchronously.
  - `Concurrent`: Executes commands asynchronously via tasks.
  - `Exclusive`: Ensures only one command instance executes for the signal type.
  - `Composite`: Triggers when multiple dependent signals are satisfied.
- **Cross-Context Routing**: `IContextResolver` provides clean context lookup without exposing internal global registries.

### 3. `ObservableProperty<T>`
Type-safe reactive property wrapper providing allocation-free change notifications to UI views and mediators.

### 4. `NexusEditorPlugin` & `NexusWindow`
Editor tools implement `INexusEditorPlugin`. The host window drives tab updates via `OnUpdate()` cadence (~200ms interval) without custom background timers.

---

## 🚫 Architectural Anti-Patterns

### ❌ Bypassing `IContextResolver` for Cross-Context Signals
```csharp
// BAD: Direct static access to internal context list
NexusRuntime.ActiveContexts[0].SignalBus.Fire(signal);

// GOOD: Route via IContextResolver abstraction
_contextResolver.ResolveContext("Gameplay").SignalBus.Fire(signal);
```
*Why:* Direct index access creates brittle dependencies and fails during unit testing or multi-context scene reloads.

### ❌ Non-Idempotent `OnDisable` Cleanups
```csharp
// BAD: Assumes OnEnable was called first
public override void OnDisable()
{
    _subscription.Unsubscribe(); // Crashes with NullReferenceException if OnEnable failed
}

// GOOD: Defensive null-checks
public override void OnDisable()
{
    if (_subscription != null)
    {
        _subscription.Unsubscribe();
        _subscription = null;
    }
    base.OnDisable();
}
```
*Why:* Unity Editor can invoke `OnDisable()` during domain reloads or window resets even if `OnEnable()` was interrupted.

---

## 🔗 Related Documentation
- 📖 [README.md](../README.md) — Main framework index and decision flows
- 📖 [PLUGIN_DEVELOPMENT.md](PLUGIN_DEVELOPMENT.md) — Plugin development and edge cases
- 📜 [CHANGELOG.md](../CHANGELOG.md) — Version history

---

**Last updated:** 2026-07-24  
**Code version:** 0.4.0  
**Maintainers:** Nexus Core Team  
**Re-review trigger:** Any change to `Runtime/Core/Context.cs` or `Runtime/Core/SignalBus.cs`.
