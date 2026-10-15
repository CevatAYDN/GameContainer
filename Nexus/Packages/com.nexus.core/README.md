> **For AI agents:** this file is the index of the package. Before generating game code read
> [AI_GAME_DEVELOPER_GUIDE.md](docs/AI_GAME_DEVELOPER_GUIDE.md), [CANONICAL-PATTERNS.md](docs/CANONICAL-PATTERNS.md)
> and [GAME_PATTERNS.md](docs/GAME_PATTERNS.md); before changing the framework read
> [ARCHITECTURE.md](docs/ARCHITECTURE.md), [CONTRIBUTING.md](docs/CONTRIBUTING.md) and
> [PLUGIN_DEVELOPMENT.md](docs/PLUGIN_DEVELOPMENT.md). Verification commands and release gates
> are in the repository's `CLAUDE.md` and `docs/RELEASE_GATES.md`.

# Nexus Core

[![Unity](https://img.shields.io/badge/Unity-6000.0-blue.svg)](https://unity.com/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Version](https://img.shields.io/badge/Version-0.4.0-orange.svg)](package.json)

Nexus Core is an MVCS (Model, View, Command, Service) architecture framework for Unity 6. One
package provides a dependency-injection container, a signal bus with pooled commands, reactive
models, a view/mediator layer, a service suite for mobile and PC games, AOT code generation and
an editor tool suite. Version: `package.json`; changes: [CHANGELOG.md](CHANGELOG.md).

## Install

Window → Package Manager → **+** → *Install package from git URL*:

```text
https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.core#<commit>
```

Pin a commit or release tag in production. Unity resolves the declared dependencies
(`com.unity.ugui`, `com.unity.profiling.core`, `com.unity.inputsystem`). Minimum Unity is
6000.0; the package is verified on 6000.5.6f1.

## Start: pick your level

| Level | You | Read, in this order |
|---|---|---|
| 0 | Never used dependency injection or a signal bus | [10_MIN_QUICKSTART.md](docs/10_MIN_QUICKSTART.md) ([TR](docs/10_MIN_QUICKSTART_TR.md)), then the **Counter Example** sample |
| 1 | Shipping a feature in a new Nexus project | [walkthrough.md](walkthrough.md), [CANONICAL-PATTERNS.md](docs/CANONICAL-PATTERNS.md) |
| 2 | Adding Nexus to an existing game | [GETTING_STARTED.md](docs/GETTING_STARTED.md) ([TR](docs/GETTING_STARTED_TR.md)), [INTEGRATION_PATHS.md](docs/INTEGRATION_PATHS.md) ([TR](docs/INTEGRATION_PATHS_TR.md)); from Zenject: [ZENJECT_MIGRATION.md](docs/ZENJECT_MIGRATION.md) |
| 3 | Studio: release builds, performance, tooling | [ARCHITECTURE.md](docs/ARCHITECTURE.md), [HARDENING.md](docs/HARDENING.md), [PERFORMANCE_COMPARISON.md](docs/PERFORMANCE_COMPARISON.md), [ADAPTERS.md](docs/ADAPTERS.md), [PLUGIN_DEVELOPMENT.md](docs/PLUGIN_DEVELOPMENT.md), the Dashboard's build-readiness card |
| AI agent | Generating game code on Nexus | [AI_GAME_DEVELOPER_GUIDE.md](docs/AI_GAME_DEVELOPER_GUIDE.md), [GAME_PATTERNS.md](docs/GAME_PATTERNS.md) |

Turkish readers: [THE_BIG_NEXUS_HOWTO.md](docs/THE_BIG_NEXUS_HOWTO.md) tells the same story as
the walkthrough as a narrative; the `*_TR.md` files are translations of their English sources.

Samples (Package Manager → Nexus → Samples): **Code-first Startup** (attach `ScoreBootstrap` to
a GameObject and press Play), **Counter Example** ([README](Samples~/Counter/README.md), the
full view/mediator tour) and **Nexus Starter Package** (scene scaffold). The Setup Wizard in the
Nexus Dashboard generates a starter project additively.

## What is in the package

**Dependency injection** (`NexusDI`, `IContextBuilder`): constructor, field, property and method
injection with `[Inject]`, `[Construct]`, `[OptionalInject]`, `[PostConstruct]`, `[Deconstruct]`;
`Lifetime.Singleton` / `Scoped` / `Transient`; child scopes (`CreateChildScope`) with reverse-order
disposal; named bindings; lazy services; fluent chain (`BindFluent<T>().To<TImpl>().AsScoped()
.WithParameter(...)`); `BindInterfacesAndSelfTo`, `BindMultiple`, cross-boundary bindings;
`ValidateBindings()` reports missing, ambiguous, captive and cyclic bindings without constructing
objects. Typed factories (`BindTypedFactory<TValue>()`, `BindTypedFactory<TParam, TValue>()`) give a
class an `IFactory<...>` that constructs and injects new instances on demand; `Construct<T>()` builds
an unbound type through the container.

**Signals and commands** (`SignalBus`): `struct` signals; `ICommand<T>` / `IAsyncCommand<T>`
handlers pooled per signal; execution modes `Sequential`, `Concurrent`, `Exclusive`, `Composite`
(fan-in of several signals); priorities; one-shot bindings; `ISignalFilter<T>` middleware by
`ref`; cross-context broadcast; `Fire`, `FireAsync`, `FireThreadSafe`, `FireNextFrame`,
`FireAsyncWithTimeout`; `Subscribe<T>` for plain handlers; recovery strategies (retry, fallback,
abort) and `CommandFailedSignal`; loading-time `Context.Prewarm<TSignal>()`.

**Models and views**: `ObservableProperty<T>` and `ObservableList<T>`; `SecureObservableProperty`
(memory obfuscation with a tamper canary); `IReactiveModel`; `View` and `Mediator<TView>` bound by
`[Mediator(typeof(...))]` with pooled mediators and automatic unsubscribe on unbind; `ScreenView`
screens managed by `UIManager`.

**Lifecycle**: `IContextLifecycle` (`OnConfigure`, `OnInitializeAsync`, `OnStartAsync`,
`OnDispose`), `IPostContextLifecycle`, `IStartable` / `IAsyncStartable` / `IStoppable` /
`IAsyncStoppable`, `ITickable` / `IFixedTickable` / `ILateTickable`; `Root` scene anchor,
`NexusLifetimeScope` child scopes, `NexusBehaviour` and `NexusBinding` for scene objects, one
persistent global root; `ContextFactory.StartAsync` for scan-free code-first startup;
`context.Instantiate(prefab, parent)` and `container.InjectGameObject(go)` inject a prefab clone
(inactive children included) before Unity runs its Awake.

**AOT and IL2CPP**: the editor generator (Nexus → Generate AOT Binder) and the Roslyn source
generator (`SourceGenerator~/`) emit injectors, constructor factories, typed command dispatchers
and `link.xml`. `NexusBinderBuildGuard` stops a player build with a stale binder,
`NexusBinderSelfHeal` recovers a binder that no longer compiles, and a stale generated injector
falls back to reflection at runtime with a warning.

**Editor suite** (Window → Nexus → Dashboard, `Ctrl+Shift+N`), 16 plugins: Dashboard,
GameManager, Hierarchy, Explorer, Graph, TypeAnalyzer, ContextInspector, FSM, Tracer,
ErrorDashboard, PerformanceDashboard, NetworkDashboard, Wizard (plus the Setup Wizard),
CasualServices, Help. The Dashboard's build-readiness card shows the AOT binder state,
`link.xml`, the active build target and its scripting defines, generated injectors that fell back
to reflection, and DI validation issues of the active contexts, with one-click check and
generate. Also custom inspectors, a scene-view overlay of context boundaries, a Unity
Profiler module, build validation rules, an architecture analyzer with a headless gate, and
hot-reload hooks. Editor text is localized in English and Turkish.

**Testing**: `NexusTestHarness`, `NexusTestContext`, `MockContext` and `GcAllocationProbe`
(calibrated against a positive control) for EditMode and PlayMode tests.

**Not in the package** (so that no one looks for it): UniTask or Awaitable integration (the API is
`ValueTask`); real advertising, store or analytics SDKs (adapters and mocks only, see
[ADAPTERS.md](docs/ADAPTERS.md)); server authority for the economy and storage services.

## Services

Every service below lives in namespace `Nexus.Core.Services`, derives from `NexusService`, is
bound with `builder.BindService<...>()` and receives `InitializeAsync` / `Dispose` from its context.

| Service | Folder under `Runtime/Services` | Purpose |
|---|---|---|
| `AdService` | `Ads` | Interstitial / rewarded / banner flow over an `IAdNetworkAdapter` from `AdAdapterFactory` (mock included); `RequestReward` with `AdOutcome`, `ReadyChanged`, privacy options through optional adapter capabilities |
| `AnalyticsService` | `Analytics` | Event tracking abstraction with pooled parameter dictionaries and user properties |
| `AudioService` | `Audio` | BGM playlist crossfade, pooled 2D/3D SFX sources, pitch variation, volume channels |
| `EconomyService` | `Economy` | Multi-currency ledger with `long` and `BigDouble` balances, `CanAfford`, reactive balances, optional backend validator |
| `FeedbackService` | `Feedback` | Combined audio + haptic presets |
| `FloatingTextService` | `UI` | Pooled world-space floating numbers |
| `HapticService` | `Haptics` | Android JNI and iOS Taptic haptics with desktop preview |
| `IapService` | `IAP` | Store catalog, purchase and restore over `IapAdapterFactory` (mock included); `Purchase` with `PurchaseOutcome`, localized prices, `IsStoreAvailable` and `StoreChanged` through optional adapter capabilities |
| `InputService` | `Input` | Input System movement/actions behind `IInputService`; a custom `IInputProvider` is authoritative |
| `LocalizationService` | `Localization` | Language tables, RTL formatting, fallback language |
| `LoggerService` | `Logging` | Default `ILoggerService` |
| `ObjectPoolService` | `Pool` | GameObject/component pools with prewarm, `IPoolable` callbacks and timed despawn |
| `ProgressionService` | `Progression` | Level index, max unlocked level, upgrade cost formulas |
| `ResourcesAssetLoadService` | `Assets` | Default `IAssetLoadService` over `Resources`; an Addressables adapter is described in [ADDRESSABLES_ADAPTER.md](docs/ADDRESSABLES_ADAPTER.md) |
| `TickService` | `Tick` | Drives `ITickable` / `IFixedTickable` / `ILateTickable`, time scale, global pause |
| `UIManager` | `UI` | Typed `ScreenView` stack over `HUD`, `Screen`, `Popup`, `Modal` layers with async open/close |

Storage is not a `NexusService`: `EncryptedStorageService` (`Nexus.Core.Services`)
implements `IPlayerPrefsService` with AES-256, key-bound HMAC-SHA256, device-bound seeds and
atomic writes; `GameSaveManager` (`Nexus.Core.Extensions`) coordinates save providers with a
`SaveThrottler`.

## Performance scope

| Path | Allocation contract |
|---|---|
| Generic synchronous dispatch, command pools, warmed DI resolution | Host regressions verify zero allocation for covered paths after warmup |
| Typed two-signal synchronous composite | Value payloads and pooled commands; 0 B per completion after warmup |
| Immutable arbitrary-arity or async composite | Retained snapshots; payload boxing and snapshot allocation are intentional |
| Startup, registration, async operations, editor diagnostics, `NEXUS_DEBUG` tracing | May allocate; outside the hot-path contract |

Measurements and their limits: [HARDENING.md](docs/HARDENING.md). The pinned core DI comparison
with VContainer and Zenject, on host and in a Unity Mono player, is in
[PERFORMANCE_COMPARISON.md](docs/PERFORMANCE_COMPARISON.md); it does not establish device or
IL2CPP performance, and no universal winner is claimed.

## Module map

| Path | Responsibility |
|---|---|
| `Runtime/Core` | `NexusRuntime` (context registry), `Context`, `ContextBuilder`, `NexusDI` and validation, `SignalBus`, `CommandExecutor`, `CommandPool`, `RecoveryEngine`, tracing clock, metrics |
| `Runtime/Core/Lifecycle` | Lifecycle interfaces and the `ContextLifecycleOrchestrator` |
| `Runtime/Interfaces`, `Runtime/Attributes` | Public contracts, attributes, execution modes, exceptions |
| `Runtime/Lifecycle` | `View`, `Mediator<TView>`, `ViewBinder`, `ViewRegistration` |
| `Runtime/Models` | Observable properties and lists, secure observables, `ModelData` |
| `Runtime/Components` | `NexusBehaviour`, `NexusBinding`, `NexusLifetimeScope` |
| `Runtime/Services` | The services above, `ProviderFactory<TAdapter>`, storage and save throttling |
| `Runtime/Netcode`, `Runtime/DOTS` | Network signal bus with rollback journal; optional Collections/Jobs bridge (`com.nexus.core.dots`) |
| `Runtime/FSM`, `Runtime/Queue`, `Runtime/Recovery`, `Runtime/Plugins`, `Runtime/Tracing`, `Runtime/Debug`, `Runtime/Data`, `Runtime/Extensions` | State machine, hybrid queue, recovery strategies, runtime plugin system, causal tracing, debug HUD, `BigDouble`, save manager and scene helpers |
| `Runtime/Testing` | Test harness assembly (`com.nexus.core.testing`) |
| `Editor/Core` | `NexusWindow` host, plugin base class, styles, localization, template provider, hierarchy menus |
| `Editor/Plugins` | The 16 plugins |
| `Editor/CodeGen` | Binder generator, build guard, self-heal, editor settings asset |
| `Editor/Validation`, `Editor/Analyzer`, `Editor/Inspector`, `Editor/Overlay`, `Editor/Profiling`, `Editor/LiveReload` | Build validation rules, architecture analyzer, custom inspectors, scene overlay, profiler module, hot reload |
| `SourceGenerator~` | Roslyn source generator project; built DLL ships in `SourceGenerators~` |
| `Tests/Editor`, `Tests/Runtime`, `Tests/DOTS` | EditMode, PlayMode and optional DOTS tests |

## Glossary

| Term | Definition |
|---|---|
| **Context** | A scoped owner of a DI container, a signal bus and a lifecycle. Anchored by a `Root` in a scene or created code-first |
| **Signal** | A `struct` carrying event data, fired through `SignalBus.Fire(signal)` |
| **Command** | A pooled handler for one signal type, `ICommand<TSignal>` or `IAsyncCommand<TSignal>` |
| **Model** | State holder; reactive models expose `ObservableProperty<T>` and implement `IReactiveModel` |
| **Service** | A long-lived `NexusService` with async initialization and disposal |
| **View** | A MonoBehaviour that draws and exposes events; it knows nothing about models |
| **Mediator** | Connects one view to signals and models; subscriptions are released on unbind |
| **Scope tag** | The name of a context (`ContextData.ScopeTag`) used for lifecycle discovery and cross-context broadcast |
| **Plugin** | An editor tab derived from `NexusEditorPlugin` hosted by `NexusWindow` |

## Anti-patterns

- Hard-coded editor strings instead of `NexusLang.Get(...)`: breaks the English/Turkish editor localization.
- Custom schedulers (`schedule.Execute`, `EditorApplication.update`) in plugins instead of `OnUpdate()`: keep running while the tab is hidden.
- Empty `catch {}` around assembly or type loading: hides broken assemblies; log the loader exceptions.
- Uncached reflection on a hot path: allocates every call; cache `MethodInfo` and delegates.
- Plugin state kept across `OnDisable` / `OnEnable`: reset flags, counters and queues.
- Rebuilding a `ScrollView` every tick: use `ListView` with `makeItem` / `bindItem`.
- Subscribing with anonymous lambdas that are never removed, or `ClearOnChanged()` on a shared property: wipes other subscribers; use named handlers and `RemoveOnChanged`.

## Documentation

| Document | Content |
|---|---|
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Runtime pipeline, lifecycle sequences, hardening contracts |
| [CANONICAL-PATTERNS.md](docs/CANONICAL-PATTERNS.md) | The one recommended way to use each API |
| [THE_BIG_NEXUS_HOWTO.md](docs/THE_BIG_NEXUS_HOWTO.md) | Why each pattern exists and when to use it (Turkish) |
| [GAME_PATTERNS.md](docs/GAME_PATTERNS.md) | Context topologies and budgets per genre |
| [AI_GAME_DEVELOPER_GUIDE.md](docs/AI_GAME_DEVELOPER_GUIDE.md) | Rules for generating game code on Nexus |
| [ZENJECT_MIGRATION.md](docs/ZENJECT_MIGRATION.md) | Concept mapping and migration steps from Zenject/Extenject (Turkish) |
| [STRANGE_COMPARISON.md](docs/STRANGE_COMPARISON.md) | Feature-by-feature comparison with StrangeIoC (Turkish) |
| [ADDRESSABLES_ADAPTER.md](docs/ADDRESSABLES_ADAPTER.md) | Implementing `IAssetLoadService` over Addressables without a package dependency |
| [ADAPTERS.md](docs/ADAPTERS.md) | Registering real ad and store SDKs behind `AdService` / `IapService`, and what a shipped game needed beyond them |
| [PERFORMANCE_COMPARISON.md](docs/PERFORMANCE_COMPARISON.md) | Pinned DI comparison with VContainer and Zenject |
| [HARDENING.md](docs/HARDENING.md) | Verification evidence and measured host performance |
| [STABILITY.md](docs/STABILITY.md) | Pre-1.0 stability policy |
| [PLUGIN_DEVELOPMENT.md](docs/PLUGIN_DEVELOPMENT.md) | Writing editor plugins |
| [LOCALIZATION_KEYS.md](docs/LOCALIZATION_KEYS.md) | Editor localization keys and parity rules |
| [CONTRIBUTING.md](docs/CONTRIBUTING.md) | Coding rules, test requirements, wizard template procedure, PR checklist |
| [TROUBLESHOOTING.md](TROUBLESHOOTING.md) | Symptoms, causes and fixes |
| [MIGRATION.md](MIGRATION.md), [BREAKING_CHANGES.md](BREAKING_CHANGES.md) | Version-to-version migration and the breaking-change policy |
| [ADOPTION.md](ADOPTION.md) | Fork-and-own guidance for studios |
| [CHANGELOG.md](CHANGELOG.md) | The only changelog |

License: MIT ([LICENSE](LICENSE)).
