> **For AI agents:** this file is the index of the package. Before generating game code read
> [AI_GAME_DEVELOPER_GUIDE.md](docs/AI_GAME_DEVELOPER_GUIDE.md), [CANONICAL-PATTERNS.md](docs/CANONICAL-PATTERNS.md)
> and [GAME_PATTERNS.md](docs/GAME_PATTERNS.md); before changing the framework read
> [ARCHITECTURE.md](docs/ARCHITECTURE.md), [CONTRIBUTING.md](docs/CONTRIBUTING.md) and
> [PLUGIN_DEVELOPMENT.md](docs/PLUGIN_DEVELOPMENT.md). Verification commands and release gates
> are in the repository's `CLAUDE.md` and `docs/RELEASE_GATES.md`.

# Nexus Core

[![Unity](https://img.shields.io/badge/Unity-6000.6-blue.svg)](https://unity.com/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Version](https://img.shields.io/badge/Version-0.5.0-orange.svg)](package.json)

Nexus Core is an MVCS (Model, View, Command, Service) architecture framework for Unity 6. One
package provides a dependency-injection container, a signal bus with pooled commands, reactive
models, a view/mediator layer, a service suite for mobile and PC games, AOT code generation and
an editor tool suite. Version: `package.json`; changes: [CHANGELOG.md](CHANGELOG.md).

## Install

Window → Package Manager → **+** → *Install package from git URL*:

```text
https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.core#<commit>
```

Pin a commit (the full 40-character hash: the Package Manager does not take a short one) or release
tag in production. Unity resolves the declared dependencies
(`com.unity.ugui`, `com.unity.profiling.core`, `com.unity.inputsystem`,
`com.unity.modules.androidjni`). Minimum Unity is 6000.6; the package is verified on 6000.6.7f1.

## Start: pick your level

| Level | You | Read, in this order |
|---|---|---|
| 0 | Never used dependency injection or a signal bus | [10_MIN_QUICKSTART.md](docs/10_MIN_QUICKSTART.md) ([TR](docs/10_MIN_QUICKSTART_TR.md)), then the **Counter Example** sample |
| 1 | Shipping a feature in a new Nexus project | [walkthrough.md](walkthrough.md), [CANONICAL-PATTERNS.md](docs/CANONICAL-PATTERNS.md) |
| 2 | Adding Nexus to an existing game | [GETTING_STARTED.md](docs/GETTING_STARTED.md) ([TR](docs/GETTING_STARTED_TR.md)), [INTEGRATION_PATHS.md](docs/INTEGRATION_PATHS.md) ([TR](docs/INTEGRATION_PATHS_TR.md)); from Zenject: [ZENJECT_MIGRATION.md](docs/ZENJECT_MIGRATION.md) ([TR](docs/ZENJECT_MIGRATION_TR.md)) |
| 3 | Studio: release builds, performance, tooling | [ARCHITECTURE.md](docs/ARCHITECTURE.md), [PERFORMANCE_COMPARISON.md](docs/PERFORMANCE_COMPARISON.md), [ADAPTERS.md](docs/ADAPTERS.md), [PLUGIN_DEVELOPMENT.md](docs/PLUGIN_DEVELOPMENT.md), the Dashboard's build-readiness card |
| AI agent | Generating game code on Nexus | [AI_GAME_DEVELOPER_GUIDE.md](docs/AI_GAME_DEVELOPER_GUIDE.md), [GAME_PATTERNS.md](docs/GAME_PATTERNS.md) |

[THE_BIG_NEXUS_HOWTO.md](docs/THE_BIG_NEXUS_HOWTO.md) ([TR](docs/THE_BIG_NEXUS_HOWTO_TR.md)) tells the same story as
the walkthrough as a narrative; the `*_TR.md` files are translations of their English sources.

Samples (Package Manager → Nexus Observable Architecture → Samples): **Code-first Startup**
(attach `ScoreBootstrap` to a GameObject and press Play), **Counter Example**
([README](Samples~/Counter/README.md), the full view/mediator tour) and **Nexus Starter Package**
(six scripts: lifecycle, signal, model, command, view and mediator; no scene). The Setup Wizard in
the Nexus Dashboard generates a starter project additively.

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

**Models and views**: `ObservableProperty<T>` (its read side `IReadOnlyObservableProperty<T>` for state a service alone
sets) and `ObservableList<T>`; `SecureObservableInt`,
`SecureObservableLong`, `SecureObservableFloat`, `SecureObservableString` and
`SecureObservableBigDouble` (memory obfuscation with a tamper canary); `IReactiveModel`; `View` and `Mediator<TView>` bound by
`[Mediator(typeof(...))]` with pooled mediators and automatic unsubscribe on unbind; `ScreenView`
uGUI screens managed by `UIManager`, and UI Toolkit screens (`ToolkitScreen`) in a `ScreenStack` that
also routes the back button, with `ToolkitPanelInput` for their `EventSystem` input ([SCREEN_STACK.md](docs/SCREEN_STACK.md)).

**Lifecycle**: `IContextLifecycle` (`OnConfigure`, `OnInitializeAsync`, `OnStartAsync`,
`OnDispose`), `IPostContextLifecycle`, `IStartable` / `IAsyncStartable` / `IStoppable` /
`IAsyncStoppable`, `ITickable` / `IFixedTickable` / `ILateTickable`; `Root` scene anchor,
`NexusLifetimeScope` child scopes, `NexusBehaviour` and `NexusBinding` for scene objects, one
persistent global root; `ContextFactory.StartAsync` for scan-free code-first startup;
`context.Instantiate(prefab, parent)` and `container.InjectGameObject(go)` inject a prefab clone
(inactive children included) before Unity runs its Awake.

**AOT and IL2CPP**: the editor generator (Nexus → Generate AOT Binder) and the Roslyn source
generator (`SourceGenerator~/`) emit injectors, constructor factories and typed command
dispatchers; only the editor generator writes `link.xml`. Both cover the game's own types: the Nexus packages' types
take the reflection path and each package preserves them in its own `link.xml` (handed to UnityLinker by
`NexusPackagesLinkXml`; Unity reads only the files under `Assets`), so a package update does not leave a
binder naming a removed type (issue #110). `NexusBinderBuildGuard` stops a player build with a stale binder,
`VersionDefineBuildGuard` stops one in which a Nexus package's optional code is compiled out because the package it
needs is installed below its asmdef's `versionDefines` expression (issue #143; the editor logs it after each compile),
`NexusBinderSelfHeal` recovers a binder that no longer compiles, and a stale generated injector
falls back to reflection at runtime with a warning.

**Editor suite** (Window → Nexus → Dashboard, `Ctrl+Shift+N`): 15 plugins, one tab each:
Dashboard, GameManager, Hierarchy, Explorer, Graph, TypeAnalyzer, ContextInspector, Tracer,
ErrorDashboard, PerformanceDashboard, NetworkDashboard, Setup Wizard (project scaffolding), Context
Wizard (Root, Service, View/Mediator and Signal/Command generators), CasualServices, Help; `com.nexus.fsm` adds a State Machine tab. The Dashboard's build-readiness card shows the AOT binder state,
`link.xml`, the active build target and its scripting defines, generated injectors that fell back
to reflection, and DI validation issues of the active contexts, with one-click check and
generate. Also custom inspectors, a scene-view overlay of context boundaries, a Unity
Profiler module, build validation rules, an architecture analyzer with a headless gate, and
hot-reload hooks. Editor text is localized in English and Turkish.

**Testing**: the `com.nexus.core.testing` assembly holds `NexusTestHarness`, `NexusTestContext`,
`MockContext`, `RecordingSignalBus` (a bus without a context that records what was fired),
`InMemoryPlayerPrefsService` and `GcAllocationProbe` (calibrated against a positive control) for
EditMode and PlayMode tests, and the test doubles `MockAdNetworkAdapter` (its `RewardOutcome` picks what
a rewarded ad ends with) and `MockIapStoreAdapter`, which a test passes to `AdService` / `IapService`.
A game's test assembly lists `com.nexus.core.testing` in its references and needs no `testables`;
the assembly compiles in the editor and for test players (`UNITY_INCLUDE_TESTS`), not into a game's
build. To run the package's own tests in a game, add `"testables": ["com.nexus.core"]` to
`Packages/manifest.json`; the clean consumer gate (`tools/nexus-consumer`, `-ValidatePerformance`)
runs seven of its suites that way.

**Not in the package** (so that no one looks for it): UniTask or Awaitable integration (the API is
`ValueTask`); real advertising, store or analytics SDKs (adapter contracts only, and no simulated store or ad in a player, see
[ADAPTERS.md](docs/ADAPTERS.md)); server authority for the economy and storage services.

## Services

Every service below lives in namespace `Nexus.Core.Services`, derives from `NexusService`, is
bound with `builder.BindService<...>()` and receives `InitializeAsync` / `Dispose` from its context.

| Service | Folder under `Runtime/Services` | Purpose |
|---|---|---|
| `AdService` | `Ads` | Interstitial / rewarded / banner flow over an `IAdNetworkAdapter` from `AdAdapterFactory` (no provider included: without an adapter no ad and a warning in every build; the ad family's `NexusAdsBridge.Connect` in `com.nexus.ads.bridge.nexus` sets its adapter); `RequestReward` / `RequestRewardAsync` with `AdOutcome`, `ReadyChanged` (also when its own interstitial cooldown ends, with an `ITickService` bound), privacy options through optional adapter capabilities |
| `AnalyticsService` | `Analytics` | Stub `IAnalyticsService`: events (with an optional parameter dictionary) and user properties written to the log; replace it with a real backend such as `com.nexus.firebase` before release |
| `AudioService` | `Audio` | BGM crossfade, pooled 2D/3D SFX sources, pitch variation, volume channels, one mute for everything plus separate music and effects mutes (`IsMusicMuted`, `IsSfxMuted`); the root comes from an `IAudioRootProvider`, which gets it back through `ReleaseRoot` on dispose |
| `EconomyService` | `Economy` | Multi-currency ledger with `long` and `BigDouble` balances, `CanAfford`, reactive balances, optional backend validator; balances persist to `SettingsStores.Economy` when a game binds one |
| `FeedbackService` | `Feedback` | Combined audio + haptic presets |
| `HapticService` | `Haptics` | Six `HapticType` presets and a game's own one-shots and waveforms (`CreateEffect`, `CreateWaveform`, `Play`); on Android 8+ each effect is built once and played through cached JNI; iOS uses `Handheld.Vibrate()`; `IsEnabled` persists |
| `IapService` | `IAP` | Store catalog, purchase and restore over `IapAdapterFactory` (no provider included: without an adapter `store_unavailable` and a warning in every build; Unity IAP in the `com.nexus.iap.unity` package); `Purchase` / `PurchaseAsync` with `PurchaseOutcome`, localized prices, `IsStoreAvailable` and `StoreChanged` through optional adapter capabilities |
| `InputService` | `Input` | Input System movement/actions behind `IInputService`; a custom `IInputProvider` is authoritative |
| `LocalizationService` | `Localization` | Language tables (a game's JSON tables through `JsonLocalizationTableProvider`), first-run device language, the language's culture and `Format`, RTL formatting, English fallback |
| `LoggerService` | `Logging` | Default `ILoggerService` |
| `ObjectPoolService` | `Pool` | GameObject/component pools with prewarm, `IPoolable` callbacks and timed despawn |
| `ProgressionService` | `Progression` | Level index, max unlocked level, upgrade cost formulas; levels persist to `SettingsStores.Progression` when a game binds one |
| `ResourcesAssetLoadService` | `Assets` | Default `IAssetLoadService` over `Resources`; an Addressables adapter is described in [ADDRESSABLES_ADAPTER.md](docs/ADDRESSABLES_ADAPTER.md) |
| `ScreenStack` | `UI/Stack` | The UI Toolkit screen history of one context over `ToolkitScreen`s, USS transitions, and the back button's chain (`IBackRouter`: the game's steps, the screens, the root behaviour); bound with `BindServiceInterfacesAndSelfTo<ScreenStack>()` ([SCREEN_STACK.md](docs/SCREEN_STACK.md)) |
| `TickService` | `Tick` | Drives `ITickable` / `IFixedTickable` / `ILateTickable`, time scale, global pause |
| `UIManager` | `UI` | Typed uGUI `ScreenView` stack over the seven `UILayer` layers (`Background`, `HUD`, `Screen`, `Popup`, `Modal`, `Overlay`, `System`) with async open/close |

Localization: a game supplies its tables with
`builder.BindInstance<ILocalizationTableProvider>(JsonLocalizationTableProvider.FromResources("Localization", "en", "tr"))`
(one flat `{"key": "text"}` file per language under `Resources/Localization/`). Binding a
`LocalizationOptions` makes the first run pick the device language when the game has a table for it
(`pt` finds `pt-BR`), else `DefaultLanguage`; the pick is not saved, `SetLanguage` is. `Culture` is the
current language's culture (`LocalizationOptions.Cultures` fixes a game's own separators) and
`Format(key, args)` fills a table text with it. Without options the first run is English, as before.

Storage is not a `NexusService`: `EncryptedStorageService` (`Nexus.Core.Services`)
implements `IPlayerPrefsService` with AES-256, key-bound HMAC-SHA256 and atomic writes. Its seed is
bound to the device by default (`StorageKeyBinding.Device`): Android Auto Backup restores the files to a
new phone, where every value then reads as tampered and reverts to its default. For data a player
must keep on a new phone (purchases, progress, reward ledgers) construct it with
`new EncryptedStorageService(salt, StorageKeyBinding.App)`: the seed is bound to the app and the salt,
the first run takes over the device-bound seed so existing data stays readable, and a copied save
then also opens on another phone. Keep `Device` where a save must not move between phones. `SettingsStores` names, per service (audio, haptics, economy, progression), the
`IPlayerPrefsService` that keeps its state, for a game that keeps it in its own save; a slot left null uses the bound
`IPlayerPrefsService`, and the key names stay the same. `GameSaveManager` (`Nexus.Core.Extensions`) writes one registered
`ISaveDataProvider` to a JSON slot file under the persistent data path and reads it back; it is a
starting point, not a save system: a game keeps its own save format and decides when to save.
`SaveThrottler` (used by `EconomyService` and `ProgressionService`) limits how often a service writes.

Logging: `LoggerService`, and the fallback logger `NexusRuntime.Logger` returns while no `ILoggerService` is
bound, drop `Log` and `LogWarning` in a release player (not the editor, not a development build); `LogError` and
`LogException` always pass. The rule the framework follows (issue #164): a failure, or a configuration that changes
what runs, reaches every build's log through `NexusLog.WarnAlways` or `LogError` (no ad or store adapter set, an
unknown provider name, an adapter's SDK failure); a diagnostic stays in the editor and development builds (a
signal nobody hears, an interstitial on cooldown or not loaded yet, a performance hint). A game follows the same
rule: `NexusLog.WarnAlways` for what a store build's log must show. The framework sites that do not follow it yet
are listed in issue #164.

## Performance scope

| Path | Allocation contract |
|---|---|
| Synchronous `Fire` of a struct signal to its commands and subscribers, no plugin `SignalInterceptor` registered | 0 B after warmup (GCA1, GCA6, `SteadyState_HasZeroGCAllocations` in the harness and in `PerformanceTests`); nested dispatch (stress 2), cross-context routing (stress 4) and a real context under fuzz (F1) 0 B; an interceptor boxes the signal once per fire |
| Command pools | `CommandPoolManager` get/return 0 B after warmup (GCA4, stress 5, `CommandPoolManager_GetReturn_SteadyState_DoesNotAllocate` in the harness and in `PerformanceTests`) |
| Warmed DI resolution | A cached resolve 0 B/op on host in the pinned comparison runs (`after-reset-android-006` and `generated-android-002`, `host-results.json`; [PERFORMANCE_COMPARISON.md](docs/PERFORMANCE_COMPARISON.md)), a measurement, not a check run on every commit; a resolved `LazyInjection<T>` read 0 B (stress 12) |
| Typed two-signal synchronous composite | Value payloads and pooled commands; 0 B per completion after warmup (EditMode `TypedComposite_SteadyStateHasNoManagedAllocation`) |
| `ObservableProperty<T>` value change, `ObservableList<T>` add, remove and clear, with subscribers | 0 B after the first subscription (GCA2, stress 17 and 17b) |
| `TickService` dispatch | 0 B per frame (GCA3; stress 20, 10k frames × 300 tickables) |
| `AdService.Tick` with an `ITickService` bound: a frame with no cooldown running, a frame inside the interstitial cooldown, the frame it ends | 0 B (GCA9, with a `ReadyChanged` handler that counts) |
| `ScreenStack` back-button poll in a frame without a press | 0 B (GCA10: 5000 polls of the back chain with a fake input service) |
| An `EventSystem` frame with `ToolkitPanelInput`, touched or not | 0 B (PlayMode `ToolkitPanelInputTests`: 0 `GC.Alloc` samples over 10 `EventSystem` frames; on the device 0 B per frame over 60 s with a 30 s drag, Android IL2CPP development player, Unity 6000.6.7f1, 2311DRK48G on Android 16, `StackProbe`); without it uGUI 2.6.0 allocates 32 B per panel on every frame |
| `HybridQueue` thread-safe and next-frame enqueue and drain | 0 B after warmup (`HybridQueue_ThreadSafe_ZeroGC`, stress 14) |
| `NetworkSignalHistory` replay, `RemoveSignalsAfter` and `Prune`; `NetworkSignalBus.RollbackAndResimulate` | 0 B (`Netcode_Rollback_And_Replay_ZeroGC`, stress 9; PlayMode `NetcodeTests.Prune_SteadyState_ZeroAllocations`, EditMode `NestedRollback_SteadyStateHasNoManagedAllocation`) |
| `InputService` update with a `PlayerMoveSignal` fire, a pool-warmed `AudioService.PlaySfx`, `LocalizationService.GetString` | 0 B (G1, G2, G3) |
| `NexusBinder` lookups, `SubscriptionRegistry` read paths and a sweep with nothing to sweep | 0 B (binder Z1, registry Z1 and Z2) |
| `EconomyService` `Earn` / `Spend` / `GetBalance` with a `SaveThrottler` bound | 0 B after the currency's first touch (harness GCA5), also with a handler subscribed to the balance (GCA7); `SaveThrottler.Tick` each frame, idle or with saves due, 0 B (GCA8; its save action is a counter, while a game's save action is a storage write, the `EncryptedStorageService` row); without a throttler every mutation persists synchronously and allocates |
| `HapticService.Play` of a built effect, `Vibrate(HapticType)` on Android 8+ | 0 B in the managed lookup (harness G5); on the device the call goes through a prebuilt `VibrationEffect` and a cached JNI method: 0 `GC.Alloc` samples in each of 12 plays of two built effects, the first play of each included (Android IL2CPP development player, Unity 6000.5.6f1, 2311DRK48G on Android 16, `DeviceProbe`); Android 7 and older use `vibrate(long)`, which allocates |
| `EncryptedStorageService`: each key that `Save()` writes to disk | Allocates. A write creates an `Aes`, an encryptor and an `HMACSHA256`, writes through the `FileStream` buffer of `File.WriteAllBytes`, and Mono's file API builds path strings under `File.WriteAllBytes`, `File.Exists` and `File.Replace`. Measured in issue #100 (ChipCrisis `93e68e8`, Android 16 on 2311DRK48G, Unity 6000.5.6f1 IL2CPP development build, Profiler `GC.Alloc` callstacks): the framework frames add up to 452 808 B over 18 key writes in 61 s, about 25 KB per write; no result file in this repository. A game chooses how often it saves |
| Immutable arbitrary-arity or async composite | Retained snapshots; payload boxing and snapshot allocation are intentional |
| Startup, registration (a `Subscribe` allocates its subscription and a new read copy, the sweep after `Unsubscribe` a new read copy), async operations, editor diagnostics, `NEXUS_DEBUG` tracing | May allocate; outside the hot-path contract |

Each row names the check behind it. GCA, G and Z checks, the fuzz suite's F1 and the numbered stress cases
run in the repository's host harness (`tools/nexus-benchmark`, on .NET with Unity APIs stubbed, before every
commit): GCA checks and `SteadyState_HasZeroGCAllocations` fail on any byte, the others above 128 B (F1: 512 B)
over the whole measured loop of 1000 to 40000 iterations, and every one of them measured 0 B on the run that
`docs/RELEASE_GATES.md` records. The named Unity tests run in the package's EditMode and PlayMode suites
and fail on any `GC.Alloc` sample. A host result is not an IL2CPP measurement; the haptics row is the one
measured on a device. The pinned core DI comparison
with VContainer, Zenject and Reflex, on host, in a Unity Mono player and on an Android IL2CPP device, is in
[PERFORMANCE_COMPARISON.md](docs/PERFORMANCE_COMPARISON.md); it measures a three-object constructor graph, not
frame budget or full `Context` startup, and no universal winner is claimed.

## Module map

| Path | Responsibility |
|---|---|
| `Runtime/Core` | `NexusRuntime` (context registry), `Context`, `ContextBuilder`, `NexusDI` and validation, `SignalBus`, `CommandExecutor`, `CommandPool`, `RecoveryEngine`, metrics |
| `Runtime/Core/Lifecycle` | Lifecycle interfaces and the `ContextLifecycleOrchestrator` |
| `Runtime/Interfaces`, `Runtime/Attributes` | Public contracts, attributes, execution modes, exceptions |
| `Runtime/Lifecycle` | `View`, `Mediator<TView>`, `ViewBinder`, `ViewRegistration` |
| `Runtime/Models` | Observable properties and lists, secure observables, `ModelData` |
| `Runtime/Components` | `NexusBehaviour`, `NexusBinding`, `NexusLifetimeScope` |
| `Runtime/Services` | The services above, `ProviderFactory<TAdapter>`, storage and save throttling |
| `Runtime/Netcode` | Network signal bus with rollback journal |
| `Runtime/Queue`, `Runtime/Recovery`, `Runtime/Plugins`, `Runtime/Tracing`, `Runtime/Debug`, `Runtime/Data`, `Runtime/Extensions` | Hybrid queue, recovery strategies, runtime plugin system, causal tracing, debug HUD, `BigDouble`, save manager and scene helpers |
| `Runtime/Testing` | Test harness assembly (`com.nexus.core.testing`) |
| `Editor/Core` | `NexusWindow` host, plugin base class, styles, localization, hierarchy menus |
| `Editor/Plugins` | The 15 plugins, one Dashboard tab each |
| `Editor/CodeGen` | Binder generator, build guard, self-heal, editor settings asset |
| `Editor/Validation`, `Editor/Analyzer`, `Editor/Inspector`, `Editor/Overlay`, `Editor/Profiling`, `Editor/LiveReload` | Build validation rules, architecture analyzer, custom inspectors, scene overlay, profiler module, hot reload |
| `SourceGenerator~` | Roslyn source generator project; built DLL ships in `SourceGenerators~` |
| `Tests/Editor`, `Tests/Runtime` | EditMode and PlayMode tests |

The state machine and the jobs-to-signals bridge are packages of their own since issue #112:
[`com.nexus.fsm`](../com.nexus.fsm/README.md) (`GameStateMachine` and the window's State Machine tab) and
[`com.nexus.dots`](../com.nexus.dots/README.md) (`NativeSignalQueue<T>`, `DOTSSignalBridge<T>`).

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
| [THE_BIG_NEXUS_HOWTO.md](docs/THE_BIG_NEXUS_HOWTO.md) ([TR](docs/THE_BIG_NEXUS_HOWTO_TR.md)) | Why each pattern exists and when to use it |
| [GAME_PATTERNS.md](docs/GAME_PATTERNS.md) | Context topologies and budgets per genre |
| [AI_GAME_DEVELOPER_GUIDE.md](docs/AI_GAME_DEVELOPER_GUIDE.md) | Rules for generating game code on Nexus |
| [ZENJECT_MIGRATION.md](docs/ZENJECT_MIGRATION.md) ([TR](docs/ZENJECT_MIGRATION_TR.md)) | Concept mapping and migration steps from Zenject/Extenject |
| [STRANGE_COMPARISON.md](docs/STRANGE_COMPARISON.md) ([TR](docs/STRANGE_COMPARISON_TR.md)) | Feature-by-feature comparison with StrangeIoC |
| [ADDRESSABLES_ADAPTER.md](docs/ADDRESSABLES_ADAPTER.md) ([TR](docs/ADDRESSABLES_ADAPTER_TR.md)) | Implementing `IAssetLoadService` over Addressables without a package dependency |
| [SCREEN_STACK.md](docs/SCREEN_STACK.md) | The UI Toolkit screen stack, its screens, transitions and the back button |
| [ADAPTERS.md](docs/ADAPTERS.md) | Registering real ad and store SDKs behind `AdService` / `IapService`, and what a shipped game needed beyond them |
| [PERFORMANCE_COMPARISON.md](docs/PERFORMANCE_COMPARISON.md) | Pinned DI comparison with VContainer, Zenject and Reflex (host, Mono player, Android IL2CPP device) |
| [STABILITY.md](docs/STABILITY.md) | Pre-1.0 stability policy |
| [PLUGIN_DEVELOPMENT.md](docs/PLUGIN_DEVELOPMENT.md) | Writing editor plugins |
| [LOCALIZATION_KEYS.md](docs/LOCALIZATION_KEYS.md) | Editor localization keys and parity rules |
| [CONTRIBUTING.md](docs/CONTRIBUTING.md) | Coding rules, concurrency rules, test requirements, wizard template procedure, PR checklist |
| [TROUBLESHOOTING.md](TROUBLESHOOTING.md) | Symptoms, causes and fixes |
| [MIGRATION.md](MIGRATION.md), [BREAKING_CHANGES.md](BREAKING_CHANGES.md) | Version-to-version migration and the breaking-change policy |
| [ADOPTION.md](ADOPTION.md) | Fork-and-own guidance for studios |
| [CHANGELOG.md](CHANGELOG.md) | The changelog of the core and its adapter packages |

License: MIT ([LICENSE](LICENSE)).
