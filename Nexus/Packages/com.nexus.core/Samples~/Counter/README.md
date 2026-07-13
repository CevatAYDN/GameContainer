# Nexus Counter Example

This sample is a **complete tour of every Nexus building block**. A single button
click exercises the model, all four command execution modes, the async command
path, a composite (fan-in) trigger, service injection, causal tracing, and the
error-recovery strategy — so you can watch the whole architecture from one action.

## Structure

| File | Building block |
| --- | --- |
| `CounterSignal.cs` | Signal (immutable `struct`) |
| `CounterSignals.cs` | Additional signals for the concurrency / composite demos |
| `ICounterModel.cs` / `CounterModel.cs` | Reactive model (`ObservableProperty<T>` + `IReactiveModel`) |
| `CounterIncrementCommand.cs` | **Sequential** command (`ICommand<TSignal>`) — default mode |
| `CounterLoadCommand.cs` | **Concurrent** command (`ExecutionMode.Concurrent`) |
| `CounterPersistCommand.cs` | **Exclusive** command (`ExecutionMode.Exclusive`) + built-in service injection |
| `CounterAsyncCommand.cs` | **Async** command (`IAsyncCommand<TSignal>`) + `[CommandTimeout]` |
| `CounterCompositeCommand.cs` | **Composite** fan-in (`[CompositeSignalHandler]`) |
| `CounterTelemetryService.cs` | Custom **service** (`INexusService`), bound via `BindService` |
| `CounterTraceSink.cs` | **Causal tracing** sink (`INexusTraceSink` + `NexusTrace`) |
| `CounterRecoveryStrategy.cs` | **Error recovery** (`IRecoveryStrategy`) |
| `CounterView.cs` / `CounterMediator.cs` | **View + Mediator** (`[Mediator]` attribute, `SignalBus.Fire`) |
| `CounterLifecycle.cs` | **Lifecycle** wiring — every binding above lives here |

## How the building blocks are wired

`CounterLifecycle.OnConfigure` is the single wiring surface:

- `builder.BindReactiveModel<ICounterModel, CounterModel>()` — the model is a
  singleton and its `OnBind(CancellationToken)` is called automatically.
- `builder.BindService<ICounterTelemetryService, CounterTelemetryService>()` — a
  custom Nexus service; `InitializeAsync` / `OnDispose` are managed for you.
- `builder.Bind<IPlayerPrefsService, UnityPlayerPrefsService>()` — a built-in
  Nexus service (zero-dependency Storage implementation).
- `builder.Bind<IRecoveryStrategy, CounterRecoveryStrategy>()` — resolved
  automatically by `SignalBus` when a command throws.
- Sequential: `builder.BindSignal<CounterSignal>().To<CounterIncrementCommand>()`.
- Concurrent: `builder.BindCommand<CounterLoadSignal, CounterLoadCommand>(ExecutionMode.Concurrent)`.
- Exclusive: `builder.BindCommand<CounterPersistSignal, CounterPersistCommand>(ExecutionMode.Exclusive)`.
- Async: `builder.BindAsyncCommand<CounterAsyncSignal, CounterAsyncCommand>()`.
- Composite: discovered automatically via `[CompositeSignalHandler]` on
  `CounterCompositeCommand` — no explicit `BindCommand` call needed.

The trace sink is attached in `OnStartAsync` via `NexusTrace.AddSink(...)`.

## How to Setup & Run

1. Open your Unity scene.
2. Create a Canvas with a **Button** (assign to `incrementButton`) and a **Text**
   element (assign to `countText`).
3. Add the `CounterView` component to your canvas or UI hierarchy.
4. Create a new empty GameObject named `CounterRoot` and add the `Root` component
   to it.
5. In the `Root` component, set up your Context configurations (or use the
   **Root Wizard** under `Window/Nexus/Root Wizard`).
6. Enter **Play Mode** and click the button. Watch the Console: you will see the
   Sequential increment, the Concurrent load, the Exclusive persist, the Async
   load, the Composite fan-in trigger, telemetry, and the `[Trace]` causal events
   — every building block firing from one click.
