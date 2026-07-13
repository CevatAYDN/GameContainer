# Canonical Nexus Patterns

> **Golden Rules**
> 1. Bind commands **explicitly** in `OnConfigure` — never rely on auto-discovery in shipping code.
> 2. Keep the lifecycle to the minimum stages your game needs.
> 3. One signal → one canonical handler path.

## P2-B: Canonical command registration

### Canonical path — explicit `BindCommand` (recommended)

Declare every command in the lifecycle `OnConfigure`. This is the single,
searchable, AOT-safe registration path:

```csharp
// Samples~/Counter/CounterLifecycle.cs
public void OnConfigure(IContextBuilder builder)
{
    // Sequential (default)
    builder.BindCommand<CounterSignal, CounterIncrementCommand>();

    // Concurrent I/O
    builder.BindCommand<CounterLoadSignal, CounterLoadCommand>(ExecutionMode.Concurrent);

    // Exclusive single-writer
    builder.BindCommand<CounterPersistSignal, CounterPersistCommand>(ExecutionMode.Exclusive);
}
```

### Alternative — `[SignalHandler]` auto-discovery (prototyping only)

For rapid prototyping you may let Nexus discover the handler via attribute:

```csharp
[SignalHandler(typeof(DamageSignal))]
public class DamageCommand : ICommand<DamageSignal> { /* ... */ }
```

Use this only for sketches. Prefer explicit `BindCommand` once the feature is real,
because explicit bindings are greppable and unambiguous in code review.

### Fan-in composites — `[CompositeSignalHandler]`

A composite waits for **all** of its signals before firing (fan-in):

```csharp
// Samples~/Counter/CounterCompositeCommand.cs
[CompositeSignalHandler(typeof(CounterAckSignal), typeof(CounterDataSignal))]
public class CounterCompositeCommand : ICommand
{
    public void Execute() { /* both ack + data received */ }
}
```

No `BindCommand` call is needed — the attribute registers it automatically.

## P2-A: Minimal lifecycle (don't be overwhelmed)

A simple game needs only two stages — `OnConfigure` to wire everything, and
`OnDispose` to release native/Unity resources:

```csharp
public class GameLifecycle : IContextLifecycle
{
    public void OnConfigure(IContextBuilder builder)
    {
        builder.BindReactiveModel<ICounterModel, CounterModel>();
        builder.BindCommand<CounterSignal, CounterIncrementCommand>();
    }

    public ValueTask OnInitializeAsync(CancellationToken ct) => default; // optional
    public ValueTask OnStartAsync(CancellationToken ct) => default;     // optional
    public void OnDispose() { /* release resources */ }
}
```

The full 4-stage lifecycle is `OnConfigure` → `OnInitializeAsync` → `OnStartAsync`
→ `OnDispose` (see `IContextLifecycle`). Add the async stages only when you have
real async init/start work.
