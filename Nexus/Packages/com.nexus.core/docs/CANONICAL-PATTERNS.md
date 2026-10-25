> **Who this is for:** levels 1–3 and code reviewers. One recommended form per API; alternatives exist for migration only.

# Canonical Patterns

> **For AI Agents:** This document declares the single recommended ("canonical") way to use each Nexus Core API surface. Alternatives exist for migration but should not appear in new code.

---

## 🥇 Canonical Command Registration

**Use `BindCommand<TSignal, TCommand>()` / `BindAsyncCommand<TSignal, TCommand>()` — NOT the `[SignalHandler]` attribute.**

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

**Rationale:** Builder registration provides compile-time safety, explicit dependency ordering, and is validated by `ContextBuilder.Validate()`. Attribute-based discovery was kept for migration but may miss required interface checks.

`builder.BindSignal<TSignal>().To<TCommand>()` (and `.ToAsync<TCommand>()`) is the same registration written as a chain: it calls `BindCommand` / `BindAsyncCommand` with the same mode and priority. Existing code may keep it; new code, the samples and these documents use `BindCommand<>`. A command that runs once uses `BindCommandOnce<>` / `BindAsyncCommandOnce<>`.

**Several commands on one signal.** Every command on a signal has the same `ExecutionMode`; `Exclusive` allows one command. Under `Sequential` each command needs its own `priority` (higher runs first): binding a second command with a priority the signal already has throws `InvalidOperationException` ("Duplicate priority"). `Concurrent` commands may share a priority.

```csharp
builder.BindCommand<LevelWonSignal, GrantRewardCommand>(priority: 20); // runs first
builder.BindCommand<LevelWonSignal, SaveProgressCommand>(priority: 10);
```

**Commands are pooled.** An instance is reused. When `Execute` returns, or the task from `ExecuteAsync` completes, the pool clears the command's `[Inject]` members. A callback that arrives later (an ad or store answer passed as a delegate, an event handler) must use locals captured before `Execute` returns; awaiting the answer inside `ExecuteAsync` is safe.

```csharp
public class ClaimRewardCommand : ICommand<ClaimRewardSignal>
{
    [Inject] public IAdService Ads { get; set; }
    [Inject] public IWalletModel Wallet { get; set; }

    public void Execute(ClaimRewardSignal signal)
    {
        var wallet = Wallet; // Wallet is null when the ad answers: the command is back in the pool
        Ads.ShowRewarded("double_coins", rewarded => { if (rewarded) wallet.Coins.Value += signal.Amount; });
    }
}
```

---

## 🥇 Canonical Composite Registration

**Use `[CompositeSignalHandler]` for a composite over any number of signal types, sync or async.**
`[RegisterCompositeCommand]` is the same registration under another name, and
`SignalBus.RegisterCompositeCommand(...)` (on the concrete bus, not on `ISignalBus`) is the call both
attributes end in. For a hot synchronous composite of exactly two signal types,
`ISignalBus.BindComposite<TFirst, TSecond, TCommand>()` with an `ICompositeCommand<TFirst, TSecond>`
passes the typed values to pooled commands without boxing; see
[GETTING_STARTED.md](GETTING_STARTED.md#performance-sensitive-composites).

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

**Use `[Inject] public T Property { get; set; }` — public auto-properties (AOT-optimal).**

```csharp
// ✅ CANONICAL — public auto-property: generated binder emits a DIRECT assignment (AOT/IL2CPP-optimal)
[Inject] public IScoreModel ScoreModel { get; set; }

// ⚠️ WORKS BUT NOT CANONICAL — private field injection: generated binder falls back to a
// cached FieldInfo.SetValue (works everywhere incl. IL2CPP, but reflection-based, slower)
[Inject] private IScoreModel _scoreModel;
```

**Rationale:** The Nexus AOT generator emits direct IL/C# assignments for public members.
Private fields/properties are **also injected** — via cached `FieldInfo.SetValue` / `PropertyInfo.SetValue`
with `link.xml` preservation — so they work in every build target; they simply trade
AOT-directness for encapsulation. Public auto-properties are the canonical AOT-optimal style.
**Constructor injection is equally valid** (`[Inject]`/`[Construct]`-marked or single public
constructor); prefer it when you want immutability (`readonly` fields).

---

## 🥇 Canonical Factory

When a class must create instances at runtime (enemies, projectiles, popups), it takes an
`IFactory<...>` instead of the container. Bind the factory in the lifecycle; the container
constructs each instance (constructor parameters, `[Inject]` members, `[PostConstruct]`) and does
not own it.

```csharp
// Lifecycle
builder.BindTypedFactory<EnemyBrain>();                 // IFactory<EnemyBrain>
builder.BindTypedFactory<EnemyKind, EnemyBrain>();      // IFactory<EnemyKind, EnemyBrain>: the argument
                                                        // goes to the constructor parameter of type EnemyKind

// Consumer
public sealed class WaveCommand : ICommand<WaveSignal>
{
    [Inject] public IFactory<EnemyKind, EnemyBrain> Enemies { get; set; }
    public void Execute(WaveSignal signal) => Enemies.Create(signal.Kind).Spawn();
}
```

Use the delegate form (`BindTypedFactory<T>(() => ...)`) when the object is not built by the container,
for example an object from a pool. Two constructor parameters of the same type cannot be told apart
by `BindTypedFactory<TParam1, TParam2, TValue>()`; use the delegate form there too.
`container.Construct<T>()` is the one-off equivalent for a type that is not bound.

---

## 🥇 Canonical Prefab Instantiation

Scene objects that come from prefabs are created through the context so their `[Inject]` members are
set before Unity calls `Awake`:

```csharp
var enemy = Context.Instantiate(enemyPrefab, parent);          // T : Component, returns the clone's component
var vfx = Context.Instantiate(vfxPrefab, position, rotation);  // GameObject overloads exist too
Context.Container.InjectGameObject(existingObject);            // objects already in the scene
```

Every `MonoBehaviour` in the clone's hierarchy is injected, inactive children included. A
`NexusBehaviour` with `AutoInject` keeps injecting itself from its own context, and a `NexusBinding`
on the prefab is marked as handled so nothing is injected twice. The prefab's and the clone's active
state are restored afterwards. `ObjectPoolService` and `UIManager` keep their own instantiation
paths; use them for pooled objects and screens.

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
cause race conditions. `BuildValidation` logs an error and fails validation for a concurrent
command that `[Inject]`s an interface whose name ends in `Model` (and does not start with
`IReadOnly`) with a settable property or a `Set`/`Update`/`Modify`/`Reset`/`Clear` method; the
pre-build check then stops the build unless `NEXUS_VALIDATION_WARN_ONLY` is set.

> **Note:** The check reads `[SignalHandler(..., Mode = ExecutionMode.Concurrent)]` attributes only.
> A command registered in code, as with `BindCommand(...)` above, is not checked; the read-only
> interface is then your own rule.

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

## 🥇 Canonical Service Implementation

**Implement `INexusService` via the `NexusService<T>` base class — NOT by implementing the interface directly.**

```csharp
// ✅ CANONICAL — base class provides [Inject] Context + SignalBus, OnDispose() → Dispose() delegation
public interface IPlayerPersistenceService { ... } // plain contract — does NOT extend INexusService
public class PlayerPersistenceService : NexusService<IPlayerPersistenceService>, IPlayerPersistenceService
{
    public override async ValueTask InitializeAsync(CancellationToken ct) { /* load */ }
    public override void OnDispose() { /* cleanup */ }
}

// ❌ NON-CANONICAL — direct interface implementation (pre-base-class services kept for migration)
public class LegacyService : ILegacyService, INexusService
{
    public ValueTask InitializeAsync(CancellationToken ct) { ... }
    public void OnDispose() { ... }
}
```

**Rationale:** `NexusService<T>` is what the Nexus code generator (`NexusSetupWizard`, `WizardTabs`) and every runtime service template emit. It auto-injects `IContext` and `ISignalBus` (so derived services never need manual DI lookups), delegates `OnDispose()` to `Dispose()` so `IDisposable`-style cleanup works through the context's single `OnDispose` call, and derives from the `[Preserve]`d base (derived services carry `[Preserve]` too — all 16 runtime services do). The service **interface** stays a plain contract (`IPlayerPersistenceService`) — it does not extend `INexusService`; only the class derives from the base, which supplies the interface. Register with `BindService<TInterface, TImplementation>()` (or `BindServiceInterfacesAndSelfTo<TImplementation>()`); the `where TImplementation : class, INexusService` constraint is satisfied through the base.

---

## 🥇 Canonical UI Manager

**Use `UIManager` (`IUIManager`) — the single runtime UI manager. There is no second window API.**

```csharp
// ✅ CANONICAL — type-safe ScreenView screens, opened by type
public class GameplayLifecycle : IContextLifecycle
{
    public void OnConfigure(IContextBuilder builder)
    {
        builder.BindServiceInterfacesAndSelfTo<UIManager>();
        // Optional: UIManager defaults to ResourcesUIAssetProvider when none is bound.
        builder.BindInterfacesAndSelfTo<ResourcesUIAssetProvider>(); // IUIAssetProvider
    }
}

public class OpenSettingsCommand : ICommand<OpenSettingsSignal>
{
    [Inject] public IUIManager UI { get; set; }
    public void Execute(OpenSettingsSignal signal) => UI.OpenScreen<SettingsScreen>(layer: UILayer.Screen);
}

// Screens derive from ScreenView (View + IUIWindowLifecycle) — the mediator binds automatically:
[Mediator(typeof(SettingsMediator))]
public class SettingsScreen : ScreenView { ... }
```

**Rationale:** `UIManager` is the canonical, type-safe screen manager — open by type
(`OpenScreenAsync<TScreen>`), pooled instances, `UICanvasSystem` layer policy, and
`ScreenOpenedSignal`/`ScreenClosedSignal`. The legacy string-keyed `WindowManager` was
**removed** (2026-08-06); its migration analyzer rule was retired with it (the id NEXUS004 now
belongs to the leftover debug trace logging rule), so new code has no alternative to diverge to.
`UILayer` and `IUIWindowLifecycle` live in their own files under
`Runtime/Services/UI/` and are shared by `UIManager`/`ScreenView`/`UICanvasSystem`. Screens
are opened/closed only through `UIManager`; scene-anchored views that are not screens stay
plain `View`s.

---

## 🥇 Canonical Execution Order

1. **Signal filters** (`ISignalFilter<T>`; a filter returning `false` cancels the signal)
2. **Interceptors** (plugin pipeline)
3. **Cross-Context Broadcast** (if `[CrossContext]` is present)
4. **Commands** (mutate model state)
5. **Subscriptions** (observers read final state)
6. **Composite Triggers** (fan-in check)

This guarantees mediators/views always read post-command state.

---

**Code version:** 0.5.0
