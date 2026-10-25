> **Who this is for:** teams moving from Zenject/Extenject (level 2). A concept mapping table and a step-by-step migration.

# Zenject → Nexus Migration Guide

This guide gives the concept mapping and a step-by-step path for moving a project that uses Zenject/Extenject to `com.nexus.core`. Nexus is not a *replacement* for Zenject but an MVCS core that *complements* it with its signal/command/observable/editor package — most of the migration work lies in the binding syntax and the difference in the signal system.

---

## 1. Concept Mapping Table

| Zenject / Extenject | Nexus | Note |
|---|---|---|
| `ProjectContext` / `SceneContext` | `Root` MonoBehaviour + `NexusRuntime` | Root is the context entry point added to the scene (`GameObject/Nexus/Scene Root`) |
| `DiContainer` | `NexusDI` (`Context.Container`) | Same role; a `Context` owns a `NexusDI` |
| `Container.Bind<T>().AsSingle()` | `BindFluent<T>().AsSingle()` *(or `Bind<T>(Lifetime.Scoped)`)* | The fluent chain is the closest match to Zenject syntax; `AsSingle`/`AsCached` map to Scoped |
| `Container.Bind<T>().AsTransient()` | `BindFluent<T>().AsTransient()` *(or `Bind<T>(Lifetime.Transient)`)* | |
| `Container.BindInstance(x)` | `container.BindInstance(x)` | Exactly the same name |
| `BindInterfacesAndSelfTo<T>()` | `BindInterfacesAndSelfTo<T>()` | Same name; singleton parity guaranteed |
| `Container.BindFactory<T, TFactory>()` / `PlaceholderFactory<T>` | `builder.BindTypedFactory<T>()` → `[Inject] IFactory<T>`; with a parameter: `BindTypedFactory<TParam, T>()` → `IFactory<TParam, T>` | As in Zenject, the container creates and injects; the instances are not owned by the container |
| `Container.InstantiatePrefab(prefab)` / `InstantiatePrefabForComponent<T>(prefab)` | `context.Instantiate(prefab, parent)` / `context.Instantiate<T>(prefab, parent)` | The clone's whole hierarchy is injected before Awake; `InjectGameObject` is for objects already in the scene |
| `[Inject]` constructor/field | `[Inject]` constructor/field/property (same) | **Nexus supports constructor injection**: a ctor marked `[Inject]` or `[Construct]` is selected automatically; an unmarked single public ctor is used too |
| `[Optional]` | `[OptionalInject]` | Example: `EconomyService.SaveThrottler` |
| `IInitializable` / `IStartable` | `IStartable` / `IAsyncStartable` / `NexusService.InitializeAsync(ct)` | `IAsyncStartable` carries a cancellation token — a step beyond Zenject |
| `SignalBus.Fire<T>()` (Zenject Signals) | `SignalBus.Fire<T>(signal)` (Nexus) | In Nexus a signal must be a **struct** (`where T : struct`) |
| `SignalBus.Subscribe<T>(handler)` | `SignalBus.Subscribe<T>(handler)` (for observing; returns an `ISignalSubscription`) or `builder.BindCommand<TSignal, TCommand>()` + `ICommand<T>` | In Nexus, handlers that change state are command classes (the command pattern) |
| `[Inject] SignalBus` | `[Inject] SignalBus` | Same |
| `ZenjectBinding` (scene binding) | `NexusBinding` MonoBehaviour | Same idea: zero-code binding in the scene |
| `IViewModel` / `Model` | `ObservableProperty<T>` / `SecureObservableInt` (and `SecureObservableLong`/`Float`/`String`/`BigDouble`) | Reactive models are built into Nexus; no UniRx needed |
| `ITickable` / `IFixedTickable` | `ITickable` / `IFixedTickable` / `ILateTickable` | Same names |
| `IInitializable.Initialize()` | `IStartable.Start()` / `IAsyncStartable.StartAsync(ct)` | |
| `Disposable` / `IDisposable` | `IDisposable` + `OnDispose()` | Context teardown is non-blocking |

---

## 2. Step-by-Step Migration

### Step 1 — Take an inventory
List every `ZenjectBinding` and every `Container.Bind` call; map them with the table above. The **number** of bindings must be preserved — a single `BindInterfacesAndSelfTo` produces the same singleton in Nexus, do not duplicate it.

### Step 2 — Root setup
Instead of `ProjectContext`: add a `Root` to the scene (`GameObject/Nexus/Scene Root`). The Root's `Awake` creates the root context automatically (`InitializeContext`), and its `Start` starts it.

### Step 3 — Translate the binding syntax
```csharp
// Zenject
Container.Bind<IPlayerData>().To<PlayerData>().AsSingle();
Container.Bind<IMusicPlayer>().To<MusicPlayer>().AsTransient();
Container.BindInstance(_settings);

// Nexus (inside the ContextBuilder) — the closest syntax is the fluent chain:
builder.BindFluent<IPlayerData>().To<PlayerData>().AsSingle();      // AsSingle equivalent (Scoped)
builder.BindFluent<IMusicPlayer>().To<MusicPlayer>().AsTransient(); // AsTransient equivalent
builder.BindInstance(_settings);
// The native equivalents behave the same:
//   builder.Bind<IPlayerData, PlayerData>(Lifetime.Scoped);
//   builder.Bind<IMusicPlayer, MusicPlayer>(Lifetime.Transient);
// The isSingleton overloads (Bind<T>(isSingleton: true/false)) exist only on the container (NexusDI); on the builder use Lifetime.
// WithParameter is the equivalent of Zenject's WithArguments/WithParameter:
//   builder.BindFluent<ScoreService>().WithParameter<int>(100);
```
> **Constructor injection:** Nexus **supports** constructor injection — your constructor from
> Zenject can stay as it is. A constructor marked `[Inject]` or `[Construct]` is selected
> automatically; with no marker, if the class has a single public constructor, that one is used.
> Parameters are satisfied in this order: an explicit override (`WithParameter<TParam>` / Zenject
> `WithArguments`), a registered binding, and finally the C# **optional parameter default** (e.g.
> value-type parameters such as `int initialSize = 4` are injected with their defaults).
> ```csharp
> // Zenject:
> public PlayerService(IPlayerData data) { ... }
> // Nexus — stays as it is:
> public PlayerService(IPlayerData data) { ... }
> ```
> **Careful:** With several constructors, DI cannot know which one to use — mark the intended
> one with `[Inject]`/`[Construct]`; with several unmarked ctors, the parameterless one is selected.
> For the AOT-optimal style see `CANONICAL-PATTERNS.md` (public auto-property injection).

### Step 4 — Translate the signals
```csharp
// Zenject: SignalBus.Subscribe<LevelUpSignal>(OnLevelUp); SignalBus.Fire(new LevelUpSignal(5));
// Nexus:
public struct LevelUpSignal { public int NewLevel; public LevelUpSignal(int level) { NewLevel = level; } }

// the handler becomes a command class:
public class ApplyLevelUp : ICommand<LevelUpSignal>
{
    [Inject] private IProgressionService _progression;
    public void Execute(LevelUpSignal signal) => _progression.SetLevel(signal.NewLevel);
}

// registration:
builder.BindCommand<LevelUpSignal, ApplyLevelUp>();
// or automatic discovery with [RegisterCommand(typeof(LevelUpSignal))]
```
> **Key difference:** Zenject signal handlers were method subscriptions; in Nexus `SignalBus.Subscribe<T>(handler)` exists for observing, but a handler that changes state = an `ICommand<T>` class. For your existing method-based logic you can write an `ActionCommand<T>` adapter (an `ICommand<T>` that wraps the method), but moving to command classes cleans up over time.

### Step 5 — Translate the reactive models
```csharp
// UniRx + Zenject: private readonly ReactiveProperty<int> _hp = new(100);
// Nexus:
public ObservableProperty<int> Health { get; } = new(100);
// View subscription: Health.OnChanged(OnHealthChanged); // RemoveOnChanged in OnUnbind
```
> **Hygiene rule (locked by `WizardTemplateSyncTests` in the wizard's mediator template):** subscriptions are removed with a named handler + `RemoveOnChanged`; `ClearOnChanged` deletes other subscribers, and an anonymous lambda cannot be removed.

### Step 6 — Move the services
An `INexusService` implementation + `builder.BindService<TInterface, TImpl>()`. `InitializeAsync(ct)` takes a cancellation token — no hang on teardown. It is the preferred path instead of Zenject's synchronous `IInitializable` start.

### Step 7 — Move the views/mediators
The `View` + `Mediator` bases: derive your view MonoBehaviour from `View` and mark it `[Mediator(typeof(TMediator))]`; derive the mediator from `Mediator<TView>` (the `ViewBinder` belongs to the Context and binds the view itself). The `OnBind`/`OnUnbind` pair is the Nexus equivalent of the subscribe/unsubscribe flow done by hand in Zenject — in `OnUnbind`, remove **your own** subscription.

### Step 8 — Verify teardown
In Zenject `Dispose` was called synchronously; Nexus does a non-blocking teardown through `OnDispose`. Do not use `async void`; do not call `.Wait()/.Result` inside teardown (sync-over-async deadlock).

---

## 3. Migration Checklist

- [ ] Every Zenject binding has a Nexus equivalent (equal count)
- [ ] Constructor injection mapping verified (`[Inject]`/`[Construct]` marked or single ctor) — do not move ctors to fields needlessly
- [ ] Every signal handler that changes state is an `ICommand<T>` (or adapter); every `Subscribe` subscription is disposed — no subscription leak
- [ ] Every `OnChanged` subscription is removed with `RemoveOnChanged`
- [ ] No `async void`; no sync-over-async in teardown
- [ ] `Window > Nexus > Code Health Analyzer` is clean (0 issues); in CI the same analysis runs with `-executeMethod Nexus.Editor.NexusArchitectureAnalyzer.RunHeadless`
- [ ] `BuildValidation.RunSilent()` passes
- [ ] The EditMode test suite passes (Nexus's own numbers: the repository's `docs/RELEASE_GATES.md`)

---

## 4. The 3 Most Common Migration Mistakes

1. **Duplicating `BindInterfacesAndSelfTo`** → thinking the same type yields several singletons; in Nexus one call is enough, parity is guaranteed.
2. **Using `ClearOnChanged`** → deletes other views' subscriptions; always `RemoveOnChanged(handler)`.
3. **Making signals classes** → Nexus enforces `where T : struct`; convert them to structs (value type = zero GC + no boxing).

---

## 5. Automatic Migration Tool (next step)

There is currently no official automatic converter; however, the headless mode of `NexusArchitectureAnalyzer` can be added to CI as a post-migration architecture gate. Because the Zenject → Nexus binding syntax difference is largely mechanical (table 1), a regex-based converter script in large projects (every `Container.Bind<X>().To<Y>().AsSingle()` → `BindFluent<X>().To<Y>().AsSingle()`) saves time; moving step by step and running the tests at every stage is safer.
