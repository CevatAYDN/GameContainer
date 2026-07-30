# Canonical Patterns

> **For AI Agents:** This document declares the single recommended ("canonical") way to use each Nexus Core API surface. Alternatives exist for migration but should not appear in new code.

---

## 🥇 Canonical Command Registration

**Use `BindCommand<>` / `BindAsyncCommand<>` (fluent API) — NOT `[SignalHandler]` attribute.**

```csharp
// ✅ CANONICAL — compile-time checked, 0-GC, AOT-safe
public class GameplayLifecycle : IContextLifecycle
{
    public void OnConfigure(IContextBuilder builder)
    {
        builder.BindCommand<ScoreChangedSignal, UpdateScoreCommand>();
        builder.BindAsyncCommand<SaveRequestedSignal, SaveGameCommand>();
    }
}

// ❌ NON-CANONICAL — attribute-based discovery (legacy)
[SignalHandler(typeof(ScoreChangedSignal))]
public class UpdateScoreCommand : ICommand<ScoreChangedSignal> { ... }
```

**Rationale:** Fluent API provides compile-time safety, explicit dependency ordering, and is validated by `ContextBuilder.Validate()`. Attribute-based discovery was kept for migration but may miss required interface checks.

---

## 🥇 Canonical Composite Registration

**Use `[CompositeSignalHandler]` attribute — the only supported path.**

```csharp
// ✅ CANONICAL — composite triggers span multiple signal types
[CompositeSignalHandler(typeof(SignalA), typeof(SignalB))]
public class MyCompositeCommand : ICompositeCommand
{
    public void Execute(CompositeContext signals)
    {
        if (signals.TryGet<SignalA>(out var sigA)) { /* use sigA */ }
        if (signals.TryGet<SignalB>(out var sigB)) { /* use sigB */ }
    }
}
```

**Note:** `ExecutionMode.Composite` cannot be passed to `BindCommand<>` / `BindAsyncCommand<>` — passing it will throw an `ArgumentException`.

---

## 🥇 Canonical Dependency Injection

**Use `[Inject] public T Property { get; set; }` — public auto-properties only.**

```csharp
// ✅ CANONICAL — public auto-property for AOT codegen compatibility
[Inject] public IScoreModel ScoreModel { get; set; }

// ❌ AVOID — field injection (breaks AOT codegen)
[Inject] private IScoreModel _scoreModel;
```

**Rationale:** Nexus AOT code generator emits IL that sets public properties. Private field injection falls back to reflection at runtime, defeating IL2CPP optimization and adding GC pressure.

---

## 🥇 Canonical Model Safety (for Concurrent Commands)

**Recommended:** Use read-only model interfaces (e.g. `IReadOnlyPlayerModel`) for dependencies injected into `ExecutionMode.Concurrent` commands.

```csharp
public interface IReadOnlyPlayerModel
{
    int Health { get; }  // Read-only
    ObservableProperty<int> Score { get; } // Reactive (safe)
}

// Registered with ExecutionMode.Concurrent:
public class LogPlayerStateCommand : ICommand<SomeSignal>
{
    [Inject] public IReadOnlyPlayerModel Player { get; set; }

    public void Execute(SomeSignal signal)
    {
        // Concurrent-safe: only reads, no writes
    }
}

// Registration:
builder.BindCommand<SomeSignal, LogPlayerStateCommand>(mode: ExecutionMode.Concurrent);
```

**Rationale:** Concurrent commands run in parallel; injecting writeable model interfaces could
cause race conditions. The `BuildValidation` system emits warnings for concurrent commands
that inject interfaces ending in `Model` with settable properties or mutation methods.

> **Note:** This is a *recommended practice* — the validation rule is a warning, not an error.

---

## 🥇 Canonical Signal Definition

**Use `public struct` — never `class`.**

```csharp
// ✅ CANONICAL — value type, zero GC per fire
public struct ScoreChangedSignal
{
    public int NewScore;
    public int ComboMultiplier;
}

// ❌ AVOID — reference type allocates on every fire
public class ScoreChangedSignal { ... }
```

---

## 🥇 Canonical Context Lifecycle

**Prefer a single `IContextLifecycle` per context, using `OnConfigure` + `OnInitializeAsync`.**

```csharp
public class GameplayLifecycle : IContextLifecycle
{
    public void OnConfigure(IContextBuilder builder)
    {
        // Bind everything here
        builder.BindModel<IPlayerModel, PlayerModel>();
        builder.BindCommand<DamageSignal, DamageCommand>();
        builder.BindService<IAudioService, AudioService>();
    }

    public ValueTask OnInitializeAsync(CancellationToken ct)
    {
        // Async init (load assets, connect services)
        return default;
    }

    public ValueTask OnStartAsync(CancellationToken ct) => default;

    public void OnDispose() { }
}
```

---

## 🥇 Canonical Execution Order

1. **Interceptors** (plugin pipeline)
2. **Cross-Context Broadcast** (if `[CrossContext]` is present)
3. **Commands** (mutate model state)
4. **Subscriptions** (observers read final state)
5. **Composite Triggers** (fan-in check)

This guarantees mediators/views always read post-command state.

---

**Last updated:** 2026-07-30  
**Code version:** 0.4.0
