> **Who this is for:** levels 1–3 and code reviewers. One recommended form per API; alternatives exist for migration only.

# Canonical Patterns

> **For AI Agents:** This document declares the single recommended ("canonical") way to use each Nexus Core API surface. Alternatives exist for migration but should not appear in new code.

---

## 🥇 Canonical Command Registration

**Use `BindCommand<TSignal, TCommand>()` / `BindAsyncCommand<TSignal, TCommand>()` — NOT the `[SignalHandler]` attribute.**

```csharp
// ✅ CANONICAL — compile-time checked, AOT-safe
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

**Several commands on one signal.** Every command on a signal has the same `ExecutionMode`, except `Detached` ones; `Exclusive` allows one command. Under `Sequential` each command needs its own `priority` (higher runs first): binding a second command with a priority the signal already has throws `InvalidOperationException` ("Duplicate priority"). `Concurrent` commands may share a priority.

```csharp
builder.BindCommand<LevelWonSignal, GrantRewardCommand>(priority: 20); // runs first
builder.BindCommand<LevelWonSignal, SaveProgressCommand>(priority: 10);
```

**A command that must not hold up the signal.** `FireAsync` awaits a signal's commands before its subscribers, and the context awaits `ContextStartedSignal` before it counts as started, so a network call there delays both. Bind it `ExecutionMode.Detached`: `FireAsync` starts it before the other commands and goes on without it. It runs on the context's lifetime token (a `FireAsyncWithTimeout` timeout does not cancel it, the context's disposal does), and a failure goes through recovery and the log like any command's. Only an `IAsyncCommand` can be detached; it may join a signal of any mode but `Exclusive`, and its priority does not order it.

```csharp
builder.BindCommand<ContextStartedSignal, ApplySavedSettingsCommand>();                              // awaited
builder.BindAsyncCommand<ContextStartedSignal, FetchRemoteTuningCommand>(ExecutionMode.Detached); // not awaited
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

## 🥇 Canonical Derived State

**Derive a value from properties with `Select` / `CombineLatest` and track it in the mediator — NOT with one hand-written handler per source.**

```csharp
public sealed class ShopMediator : Mediator<ShopView>
{
    [Inject] private IEconomyService _economy;
    [Inject] private IShopModel _shop;

    protected override void OnBind()
    {
        var canBuy = _economy.GetObservableBalance("coins").CombineLatest(_shop.Price, (coins, price) => coins >= price);
        View.SetBuyEnabled(canBuy.Value);
        TrackObservable(canBuy, (_, can) => View.SetBuyEnabled(can)); // disposed with the mediator on unbind
    }
}
```

A `DerivedProperty<T>` recomputes on each change of a source and notifies only when its result changes. Until it is disposed the sources keep it alive, so one created outside a mediator is disposed by its owner. A change reaching it allocates nothing after creation unless the selector does (issue #269).

---

## 🥇 Canonical Signal Definition

**Use `public struct` — never `class`.**

```csharp
// ✅ CANONICAL — value type
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

A lifecycle reaches the container through `[Inject]` members. They are set after the services
have initialized and before `OnInitializeAsync`; inside `OnConfigure` they are still null, because
the bindings are being made there. `ISignalPublisher` fires signals; `ISignalSubscriber` subscribes.

```csharp
public class GameplayLifecycle : MonoBehaviour, IContextLifecycle
{
    [SerializeField] private AudioClip _music;

    [Inject] public IAudioService Audio { get; set; }
    [Inject] public ISignalPublisher Signals { get; set; }

    public void OnConfigure(IContextBuilder builder)
    {
        // Bind everything here; Audio and Signals are still null
        builder.BindModel<IPlayerModel, PlayerModel>();
        builder.BindCommand<DamageSignal, DamageCommand>();
        builder.BindService<IAudioService, AudioService>();
    }

    public ValueTask OnInitializeAsync(CancellationToken ct)
    {
        Audio.PlayBgm(_music); // services are initialized and injected here
        return default;
    }

    public ValueTask OnStartAsync(CancellationToken ct)
    {
        Signals.Fire(new GameStartedSignal());
        return default;
    }

    public void OnDispose() { }
}
```

**Started, pause and resume.** When the start-up has run (services, every `OnStartAsync`, the startables), the context fires `ContextStartedSignal`; a view that registered before the services were ready redraws on it. Once its context has started, `Root` forwards `OnApplicationPause` as `ApplicationPauseSignal` (`IsPaused` true on pause, false on resume); a call that arrives before the start (Android sends one) is dropped. Subscribe to it with `Subscribe`, not `SubscribeAsync`: Android may freeze the process as soon as the pause call returns, so a save must finish inside the handler. `EncryptedStorageService` does its own pause save, on the main thread and without a `Root` (issue #203). In the same way `Root` forwards `OnApplicationQuit` as `ApplicationQuitSignal` and `Application.lowMemory` as `LowMemorySignal`, synchronously and only after the start. Android and iOS normally end a backgrounded app without a quit call, so a save that must survive belongs on the pause; the quit signal covers `Application.Quit`, a closed desktop window and the editor leaving play mode. Each of these signals is fired only when something subscribes to it.

**Reading a parent's service while configuring** (issue #157). A value that decides a binding, such as a remote-config parameter, cannot come through `[Inject]`: `OnConfigure` runs while the bindings are made, and the lifecycle's `[Inject]` members are set only after the services have initialized. A lifecycle on a `Root`'s GameObject, where `Root` collects it, reads the value through its context's parent instead. `Root` sets `Context` before it calls `OnConfigure`.

```csharp
public sealed class ShopLifecycle : MonoBehaviour, IContextLifecycle
{
    public void OnConfigure(IContextBuilder builder)
    {
        Root root = GetComponent<Root>();
        // [Inject] members are still null here; a parent that has not started counts as none (below)
        IRemoteConfigService config = root.ParentRoot != null && root.ParentRoot.IsInitialized
            ? root.Context.Parent?.TryResolve<IRemoteConfigService>()
            : null;
        if (config == null)
            NexusLog.WarnAlways("[Shop] No started parent with IRemoteConfigService; binding the default shop.");

        if (config != null && config.GetBool("new_shop", false)) builder.BindService<IShopService, NewShopService>();
        else builder.BindService<IShopService, ShopService>();
    }

    public ValueTask OnInitializeAsync(CancellationToken ct) => default;
    public ValueTask OnStartAsync(CancellationToken ct) => default;
    public void OnDispose() { }
}
```

- `TryResolve` answers null in two cases: the context has no parent, or the parent does not bind the type. A scene opened in the editor without its boot scene has no parent, because `Root` found no Global Root to link to. The lifecycle then binds its default. The warning goes through `NexusLog.WarnAlways` because that default changes what runs.
- The parent's services have run `InitializeAsync` only when the parent root finished starting before this scene loaded. That is the boot-scene flow: the game loads the next scene after the boot root started.
  - `Root` configures its context in `Awake`, configuring its parent's context first when the parent has not configured yet.
  - The parent's services initialize in the parent's `Start`. The child waits for `ParentRoot.IsInitialized` only in its own `Start`, after its `OnConfigure` has run.
  - So in the parent's own scene, or in a scene loaded while the parent is still starting, `TryResolve` returns the service before its `InitializeAsync` ran: the resolve constructs it, and the parent initializes the same instance later. The `IsInitialized` check above treats such a parent as none.

---

## 🥇 Canonical Async

**Nexus's asynchronous entry points return `ValueTask`; inside them, await Unity's `Awaitable` for frames and time.**

`IAsyncCommand<TSignal>.ExecuteAsync`, `IContextLifecycle.OnInitializeAsync` / `OnStartAsync` and `NexusService.InitializeAsync` return `ValueTask`. An `async ValueTask` method awaits any type that has an awaiter, so it awaits an `Awaitable`, a `Task` or a UniTask the same way. The package has no UniTask or `Awaitable` adapter because none is needed (issue #271).

```csharp
public sealed class ShowRewardCommand : IAsyncCommand<RewardEarnedSignal>
{
    [Inject] private IRewardView _view;

    public async ValueTask ExecuteAsync(RewardEarnedSignal signal, CancellationToken ct)
    {
        _view.Show(signal.Amount);
        await Awaitable.WaitForSecondsAsync(1.5f, ct); // the token Nexus passes ends the wait with the context
        _view.Hide();
    }
}
```

- **Frames and time:** `Awaitable.NextFrameAsync(ct)`, `Awaitable.WaitForSecondsAsync(seconds, ct)`, `Awaitable.EndOfFrameAsync(ct)`, `Awaitable.FixedUpdateAsync(ct)`; an `AsyncOperation` (a scene load) through `Awaitable.FromAsyncOperation(op, ct)`.
- **Threads:** `await Awaitable.BackgroundThreadAsync()` moves the rest of the method to a thread-pool thread and `await Awaitable.MainThreadAsync()` back. Fire signals and touch models and views on the main thread (`FireThreadSafe` is the exception).
- **Cancellation:** pass the `CancellationToken` Nexus hands the method. The context cancels it when it ends, so a wait left running stops instead of touching disposed objects.
- **Nexus's pause does not stop an `Awaitable`.** `TickService.IsPaused` and `HoldPause` stop `ITickable`s, not Unity's frame or time awaiters; a wait that must hold during a pause checks `IsPaused` itself.
- **Await a `ValueTask` once.** Do not store it and await it again, and do not call `.Result` on one that has not completed.
- **UniTask** works the same way inside these methods (`await UniTask.Delay(..., cancellationToken: ct)`); a project that uses it adds the package itself.

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

**Rationale:** `NexusService<T>` is what the Nexus code generator (`NexusSetupWizard`, `Wizard/ScriptGenerator.cs`) and every runtime service template emit. It auto-injects `IContext` and `ISignalBus` (so derived services never need manual DI lookups), delegates `OnDispose()` to `Dispose()` so `IDisposable`-style cleanup works through the context's single `OnDispose` call, and derives from the `[Preserve]`d base. The linker keeps the 16 runtime services through the package's `link.xml`, which preserves the whole `Nexus.Core.Services` namespace and reaches the linker through `NexusPackagesLinkXml` (#110); a game's own services are listed in its generated `link.xml`. The service **interface** stays a plain contract (`IPlayerPersistenceService`) — it does not extend `INexusService`; only the class derives from the base, which supplies the interface. Register with `BindService<TInterface, TImplementation>()` (or `BindServiceInterfacesAndSelfTo<TImplementation>()`); the `where TImplementation : class, INexusService` constraint is satisfied through the base.

A service the game constructs itself (its constructor takes game values) is bound with `BindServiceInstance<TInterface>(instance)`: it gets its `[Inject]` members and `InitializeAsync`, and is disposed with the context's other singletons in reverse creation order, as a `BindService` service is; an instance the game built counts as created when it is injected, so it ends before the services it was given. `BindInstance` only stores an object and logs a warning when it is given a service; `BindModelInstance` injects the model and runs `OnBind` for an `IReactiveModel`.

---

## 🥇 Canonical UI Manager

**One screen API per UI technology: new UI is UI Toolkit through `ScreenStack` (`IScreenStack`); uGUI screens use
`UIManager` (`IUIManager`). A screen belongs to one of the two, never both.**

```csharp
// ✅ CANONICAL for new UI — UI Toolkit screens in the context's ScreenStack (docs/SCREEN_STACK.md)
public class AppLifecycle : IContextLifecycle
{
    public void OnConfigure(IContextBuilder builder)
    {
        builder.BindService<IInputService, InputService>();     // the back button
        builder.BindServiceInterfacesAndSelfTo<ScreenStack>(); // IScreenStack + IBackRouter
    }
}

public class OpenSettingsCommand : ICommand<OpenSettingsSignal>
{
    [Inject] public IScreenStack Screens { get; set; }
    public void Execute(OpenSettingsSignal signal) => Screens.Push<SettingsScreen>();
}

// A screen is a ToolkitScreen beside a UIDocument under the context's Root; it registers itself when it binds.
public sealed class SettingsScreen : ToolkitScreen
{
    public override UILayer Layer => UILayer.Modal;
}
```

```csharp
// ✅ CANONICAL for uGUI — type-safe ScreenView screens, opened by type
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

**Rationale:** Since 2026-10-31 the UI the packages add is UI Toolkit (the owner's decision, #137), and the
screen stack (#76, ADR-0003) gives it one history and one back-button chain per context. `UIManager` stays for games
whose screens are uGUI. It is the type-safe uGUI screen manager — open by type
(`OpenScreenAsync<TScreen>`), pooled instances, `UICanvasSystem` layer policy, and
`ScreenOpenedSignal`/`ScreenClosedSignal`. The legacy string-keyed `WindowManager` was
**removed** (2026-08-06); its migration analyzer rule was retired with it (the id NEXUS004 now
belongs to the leftover debug trace logging rule), so new code has no alternative to diverge to.
`UILayer` and `IUIWindowLifecycle` live in their own files under
`Runtime/Services/UI/` and are shared by `UIManager`/`ScreenView`/`UICanvasSystem` and the
screen stack. uGUI screens are opened/closed only through `UIManager`, UI Toolkit screens only
through `IScreenStack`; scene-anchored views that are not screens stay plain `View`s.

---

## 🥇 Canonical Execution Order

1. **Signal filters** (`ISignalFilter<T>`; a filter returning `false` cancels the signal)
2. **Interceptors** (plugin pipeline)
3. **Cross-Context Broadcast** (if `[CrossContext]` is present)
4. **Commands** (mutate model state)
5. **Subscriptions** (observers read final state)
6. **Composite Triggers** (fan-in check)

This guarantees mediators/views always read post-command state.
