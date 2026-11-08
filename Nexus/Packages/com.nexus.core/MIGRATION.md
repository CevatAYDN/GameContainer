# Migration Guide

This guide helps you migrate between versions of Nexus Core. Follow the instructions for the version you're upgrading from.

## Table of Contents

- [Version 0.6.0 → 0.7.0 (Unreleased)](#version-060--070-unreleased)
- [Version 0.5.0 → 0.6.0](#version-050--060)
- [Version 0.4.0 → 0.5.0](#version-040--050)
  - [Scene integration and large currency update](#scene-integration-and-large-currency-update)
- [Version 0.3.2 → 0.4.0](#version-032--040)
- [Version 0.3.0 → 0.3.1](#version-030--031)
- [Version 0.2.0 → 0.3.0](#version-020--030)
- [Version 0.1.0 → 0.2.0](#version-010--020)
- [General Migration Tips](#general-migration-tips)

---

## Version 0.6.0 → 0.7.0 (Unreleased)

On `main`, not yet released. The [changelog](CHANGELOG.md)'s `[Unreleased]` section lists every change since 0.6.0.

#### Unavailable economy balances are not new zero balances

Issue #339 changes Nexus's `EconomyService` first-load failure behavior. A saved
long or canonical BigDouble value that is unreadable or cannot be decoded now
throws `IOException`, from a read or a mutation that first loads the currency.
The failed load creates no balance and schedules no persistence. Disposal and
another currency's flush therefore preserve the original key and, for an
unreadable canonical big balance, its legacy long recovery key.

Catch this failure at the game's load/UI boundary, hold dependent spending and
earning, and present the game's recovery state. Do not catch it and call
`SetBalance(..., 0)`, delete the keys, or treat the session as a new save.
There is no new interface member for custom `IEconomyService` implementations;
their own persisted-read behavior remains their responsibility.

For a transient read failure, restore access and explicitly call the same read
again on the same service; no fallback zero was cached. `EncryptedStorageService`
caches corrupt/decryption failures as unreadable, so restore those through a
successful `ImportEncryptedSaveData(key, validPayload)`, or reopen the store
after restoring the files, before retrying. The valid payload must belong to
that key and storage seed. Do not rewrite the corruption just to clear the
unreadable state. Normal absent currencies still start at zero, and existing
healthy cached balances remain usable; this is not a continuous disk-health
monitor or a transaction across currencies.

#### Confirm reward persistence before acknowledging delivery

Issue #338 adds `IPlayerPrefsService.TrySave()` and `SaveOrThrow()`. Existing
implementations still compile: the default `TrySave()` returns false, not an
inferred success from void `Save()`. `EncryptedStorageService` implements
confirmation for all pending writes/deletes; custom stores must implement
their own real confirmation before using this path. In-memory test stores and
`UnityPlayerPrefsService` do not claim durable confirmation.

Existing reward callbacks still acknowledge on normal return. A callback that
only called void `Save()` can therefore still consume a receipt after a failed
write. Opt in by confirming the actual game state with `SaveOrThrow()` before
returning; startup replay confirms before `AcknowledgeReward(id)`. Propagate
failure out of the callback. A signal bus or event handler that catches a
listener's exception is not that propagation boundary.
Inside a live callback, copy `AdController.DeliveringReward` to identify that
delivery. `PendingReward` is for startup/replay: a reentrant notification can
replace it before an older callback runs.

Keep the reward state and applied receipt id in one atomically replaced blob,
and reconcile a pending receipt before accepting another reward. Separate
economy/pending-id keys are not a transaction, even if `TrySave()` reports
success after a later retry. The bridge's
[Confirmed Reward Saves](../com.nexus.ads.bridge.nexus/README.md#confirmed-reward-saves)
section gives both callback forms, startup replay and the guarantee boundaries.

#### Per-context unheard-signal diagnostics

Issue #336 adds `void SuppressUnheardSignalHints()` to `IContextBuilder`.
Existing games using Nexus's `ContextBuilder` need no change unless they want
to suppress this hint in an intentionally headless context. A custom class
implementing `IContextBuilder` must add the new method and apply it to its own
context; a wrapper around Nexus's builder can forward the call.

Call `test.Builder.SuppressUnheardSignalHints()` on a `NexusTestContext`, or
call it on the builder supplied to `OnConfigure`. This does not disable the
global logger, change signal delivery or propagate to child contexts.
See [Testing Issues](TROUBLESHOOTING.md#unheard-signal-hints-in-headless-tests).

#### Parent contexts now own descendant teardown

Issue #337: disposing a parent context also disposes its children, deepest first, before its own services.
This applies to both `Root` destruction and code-created contexts, including disabled child Roots.
Child shutdown no longer needs a frame before parent shutdown to flush synchronous pending saves into
parent-owned storage. A child must not outlive its parent; creating one after parent teardown starts
throws `ObjectDisposedException`. Repeated child disposal remains harmless.

Choose `DisposeAsync()` from the start, and use `await parent.DisposeAsync()` when child asynchronous
cleanup must complete before parent services close. If a child already used synchronous `Dispose()`,
a later parent `DisposeAsync()` cannot upgrade it or join the background work it handed off.
`Root.OnDestroy` still uses synchronous, nonblocking `Dispose()`; an async-only cleanup continuation
must not rely on parent services remaining alive. An independently running child teardown is not joined
by synchronous parent disposal and produces a warning. Await the original async disposal call, not a
repeated no-op call. Never await ancestor shutdown from inside a child's own cleanup.

#### Removed members nobody called

Issue #238 removed these public members:
- `NexusDI.WaitForBackgroundDispose`
- `NexusDI.ClearPendingInjection`
- `NexusDI.GetRegisteredSingletons`
- `InjectScanner.HasInjectableMembers`
- `CommandRegistry.UnregisterCommand`
- `CommandExecutor.InFlightAsyncCommands`

The package, the development project, `tools/` and the known games were searched, and none of them names one.
A game that does gets CS1061. The replacements:

| Removed | Use instead |
|---|---|
| `GetRegisteredSingletons` | `NexusDI.GetActiveSingletons()`: this container's singleton instances in creation order, without the parent's and without their binding types |
| `WaitForBackgroundDispose` | `await NexusDI.DisposeAsync()` (the deterministic teardown of `IAsyncDisposable` singletons) |

Issue #251 removed `NexusGeneratedBinder.PreserveMembers` from the generated binder. It never ran, and the generated
`link.xml` preserves the injectable types. Regenerate the binder (**Nexus > Generate AOT Binder**) at the re-pin. A game
that called the method gets CS0117 and removes the call; nothing replaces it.

#### `IAdService.OnAdImpression`

Issue #135 added `event Action<AdImpression> OnAdImpression` to `IAdService`. A class of the game that implements
`IAdService` gets CS0535 and adds the event, in a test fake as `public event Action<AdImpression> OnAdImpression { add { } remove { } }`.
Five test fakes of the known games implement it (issue #287): Gigawatt's `FakeAdAdapter` and `DelayedAds` (`Tests/PlaytestRunner`),
Wickling's `FakeAds` (`Tests/SimRunner`) and RingFlow's two `MockAdService` classes; no sample does. A game that logs ad revenue
subscribes this event instead of `OnImpressionRecorded` and logs the impression's `Currency` instead of a fixed "USD";
`Format` and `Precision` are there too. `OnImpressionRecorded` still fires for every impression.

#### Commands the executor cannot run are refused at registration

Issue #294: a command bound to a signal runs only through `ICommand<TSignal>` or `IAsyncCommand<TSignal>`. A type that
implemented only the non-generic `ICommand` or `IAsyncCommand` was accepted and then logged an error at every fire; the
binding now throws at start (`ArgumentException` from the context builder, `InvalidOperationException` from
`CommandRegistry.RegisterCommand`). Implement the generic interface of the signal. No class of the four known games
implements a non-generic command interface.

Issue #299: `RegisterCompositeCommand` throws `ArgumentException` for a type the composite trigger cannot run (one that
implements only `ICommand<T>` or `IAsyncCommand<T>`) and for an async-only type registered as sync. No known game
registers a composite command.

Issue #313 removed `ErrorEntry.Metadata`; nothing wrote it, and no known game reads it.

#### Regenerate the binder for the constructor check

Issue #290: the editor binder now writes each constructor factory's parameters, and NexusDI drops a factory written for
other parameters (one warning; the type is built by reflection). A binder generated before this change has no parameters
and is not checked: regenerate it (**Nexus > Generate AOT Binder**) at the re-pin.

#### Upgrade costs round

Issue #301: `ProgressionService.CalculateUpgradeCost` rounds to the nearest whole (halves away from zero) instead of
truncating; 100 at a multiplier of 1.15 costs 115 at level 2, not 114. A game that stored costs computed by the old
method, or that computed the cost itself to avoid the truncation (Formatron), checks its numbers at the re-pin.

#### The editor patch moves to 6000.6.8f1

The framework's gates run on Unity 6000.6.8f1 (issue #284), the newest patch of the Supported stream; the `unity` field
stays 6000.6. Open the game in 6000.6.8f1 at the re-pin (gate 11 records each game's editor). The patch ships the same
.NET SDK, built-in packages and Android tools as 6000.6.7f1. Its release notes list a known issue, UUM-159613: with both
**Run In Background** and **Predictive Back Support** on, the back gesture or button closes the app after it returns from
the background. Keep one of the two off.

---

## Version 0.5.0 → 0.6.0

Released 2026-11-02. The [changelog](CHANGELOG.md)'s 0.6.0 section lists every change since 0.5.0. These need a
step in the game:

#### The FSM and the DOTS bridge are packages of their own

`GameStateMachine`, `IGameState`, `IGameStateMachine` and the window's State Machine tab moved to
`com.nexus.fsm`; `NativeSignalQueue<T>` and `DOTSSignalBridge<T>` moved to `com.nexus.dots` (issue #112).
The namespaces (`Nexus.Core.FSM`, `Nexus.DOTS`) are unchanged. A game whose code names these types adds the
package at the same commit as `com.nexus.core`, for example
`"com.nexus.fsm": "https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.fsm#<commit>"`,
and adds `com.nexus.fsm` (or `com.nexus.dots`) to the references of each assembly definition that names them;
an assembly that referenced `com.nexus.core.dots` references `com.nexus.dots` instead.

#### `Prewarm` and `PrewarmAsync` take no parent

`IObjectPoolService.Prewarm(prefab, count, parent)` and `PrewarmAsync(prefab, count, instancesPerFrame, parent)` lost
their last parameter (issue #184): it moved the pool's root, and the inactive instances died with that object. Remove
the argument. To place the spawned objects, pass the parent to `Spawn`.

#### Balances and levels are read-only

`IEconomyService.GetObservableBalance` and `GetObservableBigBalance` return `IReadOnlyObservableProperty<long>` and
`IReadOnlyObservableProperty<BigDouble>`, and `IProgressionService.CurrentLevel` and `MaxUnlockedLevel` return
`IReadOnlyObservableProperty<int>` (issue #231): a value written through the property skipped the services' checks, and a
balance written that way was not saved. Change a balance with `Earn`, `Spend` and `SetBalance` (or their `Big` forms) and a
level with `SetLevel` and `CompleteCurrentLevel`. Code that reads `Value`, calls `OnChanged` or `RemoveOnChanged`, or passes
the property to `TrackObservable` compiles unchanged. A class of the game that implements `IEconomyService` or
`IProgressionService` (a test fake) declares the new return types; `ObservableProperty<T>` and the `SecureObservable` types
implement `IReadOnlyObservableProperty<T>`, so the fields behind them stay.

#### Ownership can be unknown

`IIapService.GetOwnership(productId)` answers `Owned`, `NotOwned` or `Unknown` (issue #183). `IsProductOwned` stays false
while the store has not answered which products the account owns, offline or before its purchase fetch. A game that
hides its own interstitials or ad offers by `IsProductOwned` hides them while `GetOwnership` is not `NotOwned`, as the ads
bridge's entitlement now does. Register every product before `SetStoreAdapter`: a later `RegisterProducts` is an error.

#### Paid consumables nobody requested reach the game

`IIapService` gains `event UnrequestedPurchaseHandler UnrequestedPurchase` (issue #204). A class of the game that implements
`IIapService` (a test fake) adds the event. A game that sells consumables subscribes before `SetStoreAdapter` and grants
each transaction id once: `UnityIapAdapter` no longer consumes a paid consumable nobody handles, and it reports an order
whose confirm failed in an earlier session again.

#### The adapter factories have no `"mock"` provider

`AdAdapterFactory` and `IapAdapterFactory` ship no provider (issue #162): `CreateAdapter("mock")` answers null with a warning,
and without an adapter `IapService` and `AdService` answer as a release player does. A game that asked for `"mock"` in the
editor or in development builds sets the real adapter there too: `UnityIapAdapter` (`com.nexus.iap.unity`; Unity IAP's own
`FakeAppStore` in the editor, the store's test accounts on a device) and the ad family's bridge, whose bootstrap runs the
labelled simulator in the editor. A test passes `MockIapStoreAdapter` or `MockAdNetworkAdapter` from `com.nexus.core.testing`
to `SetStoreAdapter` or `SetNetworkAdapter`, also when it boots the game's real context: Unity IAP 5.4.4's editor store
opens a Buy/Cancel dialog for every purchase, so a purchase made through `UnityIapAdapter` in a batchmode test stays
pending until the context is disposed and then answers `Unavailable` (#267; the define that turns the dialog off is in
[ADAPTERS](docs/ADAPTERS.md)). A switch on `DEVELOPMENT_BUILD` goes with it: Unity 6000.6 deprecates the symbol
(`UAC0009`, #140).

#### A custom `IUIAssetProvider` gets its instances back

`UIManager` now calls `ReleaseWindow` for each instance `InstantiateWindowAsync` returned (issue #185), once, on the main
thread, and the instance can already be destroyed then. Release what the instance holds (an Addressables handle) and
destroy it only if it is alive (`if (instance != null)`). `UICanvasSystem.UpdateLayerInteractivity` is replaced by
`SetModalOpen(bool)` and `ReleaseWithoutDestroy` is gone; code that called them leaves the canvas to `UIManager`.

#### The editor moves to Unity 6000.6

The `unity` field of `com.nexus.core`, `com.nexus.fsm`, `com.nexus.dots`, `com.nexus.firebase` and `com.nexus.iap.unity`
is 6000.6 (issue #139), and `com.nexus.core` depends on `com.unity.ugui` 2.6.0 (#138), which the 6000.6 editor ships. Open
the game in the framework's editor patch, 6000.6.8f1 since issue #284 (gate 11 records each game's editor). After the move, select the
Android Target API Level again in Player Settings: 6000.6.3f1 to 6000.6.6 saved a level 6 lower than the one selected
(UUM-151786, fixed in 6000.6.7f1). A release build below API 36 now fails the ads family's pre-build check (#194).

#### Regenerate the binder after the re-pin

A binder generated before this release calls `RegisterInjector<T>` without the member list (CS7036, #191), and older binders
name types that are gone (`AdMobAdapter`, `FloatingTextService`). Regenerate it once in batch with `-ignorecompilererrors`
(TROUBLESHOOTING, "Stale Generated Binder"), or with **Nexus > Generate AOT Binder** once the project compiles. Never empty the
file by hand: the generator refuses a zero-byte binder.

#### Interfaces with new members

A class of the game that implements one of these interfaces (a test fake, a wrapper) adds the members:

| Interface | New members | Issue |
|---|---|---|
| `IAudioService` | `IsMusicMuted`, `IsSfxMuted` | #71 |
| `IHapticService` | `CreateEffect(ms, amplitude)`, `CreateWaveform(timings, amplitudes)`, `Play(effect)` | #71 |
| `ILocalizationService` | `Culture`, `Format` | #72 |
| `IContextBuilder` | `BindServiceInstance<TInterface>(instance)` | #69 |
| `ITickService` | `HoldPause()` | #77 |
| `IIapService` | `GetOwnership(productId)`, `UnrequestedPurchase` (above) | #183, #204 |
| `IIapStoreInfoAdapter` | `OwnershipKnown` | #183 |
| `IEconomyService` | `Spend(currencyId, amount, out refusal, reason)`, `SpendBig(currencyId, amount, out refusal, reason)` | #196 |
| `IResolver` (so `IContext`) | `Resolve(type)`, `TryResolve(type, name = null)` | #148 |
| `IContextBuilder` (`ITypeBinder`) | `BindFactory<T>(create, lifetime)`, `BindFactory<T>(name, create, lifetime)` | #149 |
| `INetworkModelSnapshotHandler` | `HasSnapshot(tick)` | #170 |
| `ICrashReporter` (`com.nexus.firebase`, which came after 0.5.0) | `SetCustomKey(key, value)`, `Log(message)` | #199 |

#### Tests reference the testing kit

`MockContext`, `NexusTestHarness`, `MockIapStoreAdapter` and `MockAdNetworkAdapter` are in the assembly `com.nexus.core.testing`
(#74, #162). It is not auto-referenced and compiles only in the editor or with `UNITY_INCLUDE_TESTS`: add `com.nexus.core.testing`
to the `references` of each test assembly definition that uses them.

#### Behaviour that changed under the same API

- `IContext` inherits `Resolve` and `TryResolve` from `IResolver`, so `typeof(IContext).GetMethod("TryResolve")` returns null
  and a reflective call returns without resolving anything. Call `TryResolve(type)` or `Resolve(type)` (#148).
- A signal fired with `FireThreadSafe` while `DrainThreadSafe` runs, from a thread-safe handler or another thread, waits for the
  next drain, one frame later (#169). A game that relied on it running in the same drain reads the result a frame later.
- A `[CommandTimeout]` command that times out goes through recovery (#168). `DefaultRecoveryStrategy` retries it up to 3
  times and then aborts with `NexusRecoveryAbortException` (inner `CommandTimeoutException`), so an awaiting `FireAsync`
  caller of a sequential line with a 3 s timeout can wait about 12 s instead of getting an `OperationCanceledException` at
  the first timeout. A strategy that must not retry a timeout reads `CommandFailureContext.IsTimeout`.
- `NetworkSignalBus.RollbackAndResimulate` to a tick that a registered model has no snapshot of (pruned, or the model was
  registered after it) throws `InvalidOperationException` before it changes anything, where it replayed that tick's inputs over
  the current state; `NetworkModelSnapshotHandler.Restore` throws for a missing snapshot (#170). After `PruneHistory(N)` the
  oldest tick a rollback can reach is N + 1.
- The editor has one top-level **Nexus** menu (#193). `Window/Nexus/Dashboard`, `Nexus/Open Dashboard` and the
  `Nexus/Create/*` items are gone: **Nexus > Open Window** (`Ctrl+Shift+N`) opens the window, `GameObject/Nexus/` creates roots,
  and **Assets > Create > Nexus** creates assets.
- `EconomyService` throws `ArgumentOutOfRangeException` for an amount outside the method's range (#196): `Spend`, `Earn`,
  `SpendBig` and `EarnBig` take a positive amount, `CanAfford`, `CanAffordBig`, `SetBalance` and `SetBigBalance` a nonnegative
  one. Code that passed 0 or a negative amount and relied on it being ignored now throws: a price or reward that can be 0 (a
  free item, a bonus from a rate that starts at 0, a designer value set to 0) skips the call. `SpendBig` with a validator that
  is not an `INetworkBigEconomyValidator` throws `NotSupportedException` instead of returning false. A server-refused spend
  fires `SpendRefundedSignal`; a game that grants something for a spend takes it back there.
- `BigDouble` throws `ArgumentOutOfRangeException` for NaN and infinite input, the conversion from `double` included (#196).
  A value built from `Math.Pow` that overflows a double throws instead of becoming 0: build it with `BigDouble.Pow`.
- A player build regenerates the binder and `link.xml` first (`NexusBinderBuildGuard`, #193), also when they exist and are out
  of date, where the guard used to stop the build. With `NEXUS_DISABLE_AUTOGEN=1` nothing is generated and a stale binder stops
  the build unless `NEXUS_SKIP_BINDER_GUARD=1`.
- `BuildValidation.Validate()` runs the project's `IBuildValidationRule` classes (#193): an error from one fails the validation,
  and so a player build. A validation that crashes fails with every error counted, where it left `LastErrorCount` at 0 and
  `LastRunPassed` at the previous run's value. `LastResults` holds the findings after every run, not only after `RunSilent`, and
  no longer holds the "Validation FAILED" summary line.
- The Dashboard tab is now the Status tab (#193): `NexusWindow.Open("Status")`. The **Nexus > Code Health Analyzer** and
  **Nexus > Run System Diagnostic Audit** (`Ctrl+Alt+D`) items are gone; **Check** on the Status tab runs their checks. A CI step
  that ran `Nexus.Editor.NexusArchitectureAnalyzer.RunHeadless` runs `Nexus.Editor.NexusStatus.RunCli`, which also runs
  validation and the binder and package checks, and exits 1 for an error only, not for a NEXUS001 warning.

#### Removed

Code that names one of these stops compiling; the changelog's Removed section gives each reason.

| Removed | Instead |
|---|---|
| `NexusBootstrapManifest` and "Generate Skeleton from Manifest" (#193) | The Setup Wizard's generators |
| The Context Wizard's Clean Deletion tab and the Help tab (#193) | Delete in the Hierarchy (with Undo); the Setup Wizard's document buttons |
| `NexusWindow.ShowWindow()`, `OpenPlugin(id)`, `TypeDependencyAnalyzerWindow` (#193) | `NexusWindow.Open(id)` and `SwitchToPlugin(id)`; the Architecture tab |
| `ExplorerPlugin`, `GraphPlugin` with `SignalGraphView`, `TypeAnalyzerPlugin`, and the Game Manager's type lists and signal test (#193) | The Architecture tab (`NexusWindow.Open("Architecture")`) |
| `HierarchyPlugin` and `GameManagerPlugin` (#193) | The Context Inspector tab (`NexusWindow.Open("ContextInspector")`); the signal and command rates are on the Performance tab |
| `Samples~/DevelopmentAssets/` (#201) | Nothing referenced it |
| `AdMobAdapter`, `AdMobConfig`, `AdMobMainThread` (#14) | The ad family (`com.nexus.ads/MIGRATION.md`), before the re-pin |
| `NexusTemplateProvider` (#41) | The Setup Wizard writes its own templates |
| `ContextLifecycleOrchestrator.ExecuteLifecyclePhasesAsync` (#27) | Start the context through `Root`, `NexusLifetimeScope` or `Context.StartAsync` |
| `IFloatingTextService`, `FloatingTextService` (#91) | A game's own floating text |
| `ProviderFactory.RegisterBuiltIn` and the `"mock"` providers (#162) | The real adapters; the test doubles in `com.nexus.core.testing` |
| `DIBindingRule` (#193) | Nothing: it reported nothing. A rule of the project implements `IBuildValidationRule` |
| `NexusCodeGenerator.HasInjectableTypes` (#193) | Nothing called it |
| `DashboardPlugin` (#193) | `StatusPlugin`, `NexusWindow.Open("Status")` |
| `NexusArchitectureAnalyzer` with `RunHeadless` (#193) | The Status tab; `NexusStatus.RunCli` in batch mode |
| `NexusSystemDiagnostic` (#193) | The Status tab's **Check** |

`ContextData.DisposeTimeoutSeconds` is obsolete (#59): nothing reads it, and the context never waits for async-disposable
singletons. Remove its uses before it goes.

---

## Version 0.4.0 → 0.5.0

Released 2026-10-15. Regenerate the AOT binder after upgrading (**Nexus > Generate AOT Binder**):
the binder header carries the package version and the build guard stops a player build while it is
out of date. In Play Mode the Dashboard's build-readiness card must show live generated registrations.

### Breaking Changes

#### 1. WindowManager Removed — UIManager Is the Single UI Manager

`WindowManager` / `IWindowManager` were deleted. Use `UIManager` (`IUIManager : INexusService`) for type-safe screen management with UI pooling. `UILayer` and `IUIWindowLifecycle` were extracted from the old `WindowManager.cs`.

**Before:**
```csharp
[Inject] private IWindowManager _windows;
_windows.OpenWindowAsync("ShopScreen");
```

**After:**
```csharp
[Inject] private IUIManager _ui;
_ui.OpenScreen<ShopScreen>();
// or, with pooling + await:
await _ui.OpenScreenAsync<ShopScreen>(args, UILayer.Screen, pooled: true);
```

**Migration Steps:**
1. Replace `IWindowManager` injections with `IUIManager`.
2. Replace window-open calls with `OpenScreen<TScreen>()` / `OpenScreenAsync<TScreen>(args, layer, pooled)` where `TScreen : ScreenView`.
3. Register screen prefabs via `RegisterScreenPrefab<TScreen>(prefab)` or your `IUIAssetProvider`.
4. Replace close calls with `CloseScreen<TScreen>()` / `CloseTopScreenAsync()` / `CloseAllAsync()`.

> Architecture note: this migration is recorded in ADR-0001, `docs/adr/0001-ui-stack-in-game-ugui-editor-ui-toolkit.md` in the repository (addenda: WindowManager removal, UIManager canonical, Demo removal). The benchmark's WindowManager W1–W7 proof was migrated to UIManager U1–U7.

#### 2. Demo Scaffolding Removed — Game/Samples Is Canonical

`Assets/Scripts/Demo/` (the demo `ScreenView` screens and their commands/models/signals) was deleted. The single canonical project example is `Assets/Scripts/Game/Samples` — generated by the `NexusSetupWizard` and wired to `Assets/Scenes/NexusStarter.unity` (`GameLifecycle` + `GameView`).

**Migration Steps:**
1. Remove references to `Nexus.Demo` types.
2. Refresh your example from the wizard (`NexusSetupWizard`) — its templates are kept in sync with `Game/Samples`.
3. `DemoCompatibilitySuite` was removed from the benchmark together with the demo.

#### 3. NEXUS004 Now Names a Different Rule

The `NEXUS004` rule that flagged new `WindowManager` references to drive the migration was retired together with `WindowManager`; the id now names the leftover-debug-trace rule (a `Debug.Log` carrying a `[SLOCK]` or `[*-TRACE]` marker in runtime code). The Code Health Analyzer enforces NEXUS001 (hot-path allocations), NEXUS002 (async void), NEXUS003 (sync blocking) and NEXUS004 (leftover debug trace). `NexusArchitectureAnalyzer.RunHeadless` is the batch-mode gate (exit 0 clean, 1 on any issue) for a project's own build pipeline.

#### 4. `NexusDI.ClearCaches()` Keeps Generated Registrations

`ClearCaches()` (and `NexusRuntime.Reset()`, which calls it at `SubsystemRegistration`) no longer removes the generated binder's constructor factories, injectors and clearers; it clears only derived caches. Code that relied on it to switch a type back to reflection injection calls the new `NexusDI.ClearGeneratedRegistrations()`.

**Before:**
```csharp
NexusDI.ClearCaches(); // also dropped RegisterConstructorFactory / RegisterInjector entries
```

**After:**
```csharp
NexusDI.ClearGeneratedRegistrations(); // tests and benchmarks only; a game never needs it
```

#### 5. `IAdService` and `IIapService` Gained Members

Custom implementations of the two service interfaces must add: `IAdService.RequestReward(string, Action<AdOutcome>)`, `ReadyChanged`, `PrivacyOptionsRequired`, `ShowPrivacyOptions(Action)`; `IIapService.Purchase(string, Action<PurchaseOutcome>)`, `GetLocalizedPrice(string)`, `IsStoreAvailable`, `StoreChanged`. Adapters (`IAdNetworkAdapter`, `IIapStoreAdapter`) are unchanged; the capability interfaces `IAdOutcomeAdapter`, `IAdReadinessAdapter`, `IAdConsentAdapter`, `IIapOutcomeAdapter` and `IIapStoreInfoAdapter` are optional (`docs/ADAPTERS.md`).

### Scene integration and large currency update

- NexusBehaviour hooks wait for the owning context: OnNexusAwake after successful
  binding configuration, OnNexusStart after Unity Start plus async context startup.
  Keep configuration hooks short; frame Update code must wait for your Start hook
  when it needs initialized services. Context replacement cleans automatic injected
  fields/properties and tracked subscriptions. Set Context=null to return to automatic
  selection; multiple independent pure scopes require explicit Context assignment.
- NexusBinding waits for configured bindings and chooses the nearest Root even when
  that Root is still pending; it does not consume another scope's registration event.
- Configure Root components on inactive GameObjects before activation. Keep one global
  root in the bootstrap scene. Duplicate global registrations now fail explicitly;
  failed startup releases global/sibling ownership so a replacement can start.
- Input System is a declared UPM dependency. Choose New or Both Active Input Handling
  for built-in device input. A custom provider's zero movement no longer falls through
  to joystick/keyboard input. Disabled legacy backends are not probed every frame;
  unavailable named legacy actions are cached after their first failure.
- BigDouble API promotes a currency's live long balance to one canonical approximate
  ledger; retained long observables project it (truncate/saturate). Existing network
  validators must implement INetworkBigEconomyValidator for Big transactions. Existing
  storage adapters implementing string methods get default numeric round-trip support;
  concrete numeric codecs retain the mantissa;exponent format using invariant R output.
- DOTS producer jobs use AsParallelWriter and immediately AddProducerDependency(handle)
  on the main thread. The bridge completes registered jobs before drain/reinitialize/
  destroy. Direct bridge Enqueue is a main-thread operation; NativeSignalQueue itself
  requires explicit producer completion by its owner.

---

## Version 0.3.2 → 0.4.0

### Breaking Changes

#### 1. NEXUS_DEBUG Now Opt-In

The `NEXUS_DEBUG` scripting define is no longer auto-enabled via the `com.unity.modules.unitywebrequest` version define entry. It is now a genuine opt-in define used only for development builds.

If you rely on the editor Tracer dashboard or causal tracing, enable it manually in `Project Settings → Player → Scripting Define Symbols` (or via `TracerPlugin → Enable` button).

If `NEXUS_DEBUG` is off, the Tracer dashboard falls back to the production Metrics ring buffer and displays an info card with instructions.

#### 2. AES-256 Key Derivation (with automatic legacy migration)

`EncryptedStorageService` now uses the full 32-byte SHA-256 hash as the AES-256 encryption key and derives an independent HMAC key from a salted hash. Legacy AES-128 payloads are migrated automatically on first decrypt and re-encrypted with the new keys.

No action is required for existing save data. If a payload cannot be decrypted with the new keys, the service attempts the legacy path; on success, it rewrites the payload.

#### 3. Priority Semantics Clarified (higher values run first)

Documentation and attributes now state the actual runtime behavior: commands with higher priority values run before lower-priority handlers. No sorting change was made; only docs were corrected.

#### 4. BindCommand Rejects ExecutionMode.Composite

Composite triggers have a dedicated registration path. Passing `ExecutionMode.Composite` to `BindCommand`/`BindAsyncCommand` now throws `ArgumentException`. Use `[CompositeSignalHandler]` (or `SignalBus.RegisterCompositeCommand`) instead.

#### 5. Generic-only ICommand<TSignal> / IAsyncCommand<TSignal> Discovery

Attribute-based scanning now recognizes command classes that only implement the generic `ICommand<T>` / `IAsyncCommand<T>` interfaces. Previously these were silently skipped during `[SignalHandler]` discovery and build validation.

If you have a `[SignalHandler]` type that implements neither interface, the context now logs an error at startup so the misconfiguration is visible.

---

## Version 0.3.0 → 0.3.1

### Breaking Changes

#### 1. IPlayerPrefsService - Added GetLong/SetLong

Any custom implementation of `IPlayerPrefsService` (e.g. test mocks, alternative databases) must implement `GetLong(string key, long defaultValue = 0L)` and `SetLong(string key, long value)`.

**Example implementation for a mock dictionary:**
```csharp
public void SetLong(string key, long value) => _data[key] = value.ToString();
public long GetLong(string key, long defaultValue = 0L) => 
    _data.TryGetValue(key, out var value) && long.TryParse(value, out var parsed) 
        ? parsed 
        : defaultValue;
```

#### 2. EncryptedStorageService - Caching & Deferred Saves

`EncryptedStorageService` now features an in-memory cache and has `AutoSave` set to `false` by default. Under this setup, disk writes are deferred and batched until:
* `Save()` is explicitly called.
* The application loses focus.
* The application quits.

If you have custom tests that rely on immediate disk modification (e.g. manually editing file bytes on disk and checking if they fail HMAC checks), you must set:
```csharp
storage.AutoSave = true;
```
or instantiate a new service instance to bypass the memory cache.

---

## Version 0.2.0 → 0.3.0

### Breaking Changes

#### 1. IReactiveModel Signature Change

**Before:**
```csharp
public class PlayerModel : IReactiveModel
{
    public void OnBind(IContext context)
    {
        // Initialization logic
    }
}
```

**After:**
```csharp
public class PlayerModel : IReactiveModel
{
    public ValueTask OnBind(CancellationToken ct)
    {
        // Initialization logic (can be async)
        return default;
    }
}
```

**Migration Steps:**
1. Update all `IReactiveModel` implementations
2. Change return type from `void` to `ValueTask`
3. Change parameter from `IContext` to `CancellationToken`
4. Add `return default;` at the end of synchronous implementations
5. For async initialization, use `await` and return `ValueTask`

#### 2. Service Interface Updates

**IPlayerPrefsService - Added GetBool/SetBool:**

**Before:**
```csharp
public class MockPlayerPrefs : IPlayerPrefsService
{
    public void SetString(string key, string value) { }
    public string GetString(string key, string defaultValue = "") => defaultValue;
    public void SetInt(string key, int value) { }
    public int GetInt(string key, int defaultValue = 0) => defaultValue;
    public void SetFloat(string key, float value) { }
    public float GetFloat(string key, float defaultValue = 0f) => defaultValue;
    public bool HasKey(string key) => false;
    public void DeleteKey(string key) { }
    public void DeleteAll() { }
    public void Save() { }
}
```

**After:**
```csharp
public class MockPlayerPrefs : IPlayerPrefsService
{
    public void SetString(string key, string value) { }
    public string GetString(string key, string defaultValue = "") => defaultValue;
    public void SetInt(string key, int value) { }
    public int GetInt(string key, int defaultValue = 0) => defaultValue;
    public void SetFloat(string key, float value) { }
    public float GetFloat(string key, float defaultValue = 0f) => defaultValue;
    public bool GetBool(string key, bool defaultValue = false) => defaultValue; // NEW
    public void SetBool(string key, bool value) { } // NEW
    public bool HasKey(string key) => false;
    public void DeleteKey(string key) { }
    public void DeleteAll() { }
    public void Save() { }
}
```

**IAudioService - Added PlaySfxAtPosition:**

**Before:**
```csharp
public class MockAudio : IAudioService
{
    public void PlaySfx(AudioClip clip, float volume = 1f, float pitchMin = 1f, float pitchMax = 1f) { }
    // ... other methods
}
```

**After:**
```csharp
public class MockAudio : IAudioService
{
    public void PlaySfx(AudioClip clip, float volume = 1f, float pitchMin = 1f, float pitchMax = 1f) { }
    public void PlaySfxAtPosition(AudioClip clip, Vector3 position, float volume = 1f) { } // NEW
    // ... other methods
}
```

**IHapticService - Added IsEnabled Property:**

**Before:**
```csharp
public class MockHaptic : IHapticService
{
    public void Vibrate(HapticType type = HapticType.Light) { }
}
```

**After:**
```csharp
public class MockHaptic : IHapticService
{
    public bool IsEnabled { get; set; } = true; // NEW
    public void Vibrate(HapticType type = HapticType.Light) { }
}
```

**Migration Steps:**
1. Update all custom service implementations
2. Add the new methods/properties to your mocks
3. Implement the new functionality (can be no-op for mocks)
4. Update test fixtures that use these services

#### 3. MockContext Location Change

**Before:**
```csharp
// Local mock context in test files
private class MockContext : IContext
{
    // Implementation
}
```

**After:**
```csharp
// Use centralized MockContext from Nexus.Core
using Nexus.Core;

var context = new MockContext();
```

**Migration Steps:**
1. Remove local MockContext implementations from test files
2. Add `using Nexus.Core;` to test files
3. Use the centralized `MockContext` class
4. Update any custom behavior to use the centralized implementation

### New Features

#### 1. Async Signal Timeout

You can now use timeout-based async signal firing:

```csharp
try
{
    await signalBus.FireAsyncWithTimeout(new PlayerActionSignal(), timeoutMilliseconds: 5000);
}
catch (OperationCanceledException)
{
    // Handle timeout
}
```

#### 2. Enhanced Services

New services are available:

```csharp
// Localization with RTL support
[Inject] private ILocalizationService _localization;
_localization.SetLanguage("ar");
string rtlText = _localization.FormatRTLIfNeeded("مرحبا");

// Feedback presets
[Inject] private FeedbackService _feedback;
_feedback.Play(FeedbackPreset.SuccessFanfare);

// Save throttling
[Inject] private ISaveThrottler _saveThrottler;
_saveThrottler.TryRequestSave(() => SaveGame());
```

#### 3. Editor Plugins

New editor tools available:
- Type Dependency Analyzer
- Enhanced Dashboard
- Network Signal Tracer
- Game Manager Inspector

They are tabs of the Nexus window: **Nexus > Open Window** (`Ctrl+Shift+N`). After 0.5.0 the Architecture tab holds the
Type Dependency Analyzer and the Game Manager's type lists, and the Context Inspector tab holds the Game Manager's
running contexts (#193).

### Deprecated Features

None in this version.

---

## Version 0.1.0 → 0.2.0

### Breaking Changes

#### 1. Signal Handler Protection

**Before:**
```csharp
// Could call Fire() on signals with async handlers
signalBus.Fire(new MySignal());
```

**After:**
```csharp
// Must use FireAsync() for signals with async handlers
await signalBus.FireAsync(new MySignal());

// Or fire and forget (returns void)
signalBus.FireAsyncAndForget(new MySignal());
```

**Migration Steps:**
1. Identify all signals with async handlers
2. Replace `Fire()` with `FireAsync()` or `FireAsyncAndForget()`
3. Add `await` where results are needed
4. Use `FireAsyncAndForget()` for fire-and-forget scenarios

#### 2. Command Interface Mutual Exclusivity

**Before:**
```csharp
// Could implement both ICommand and IAsyncCommand
public class MyCommand : ICommand<MySignal>, IAsyncCommand<MySignal>
{
    public void Execute(MySignal signal) { }
    public ValueTask ExecuteAsync(MySignal signal, CancellationToken ct) => default;
}
```

**After:**
```csharp
// Must implement only one interface
public class MyCommand : ICommand<MySignal>
{
    public void Execute(MySignal signal) { }
}

// OR
public class MyAsyncCommand : IAsyncCommand<MySignal>
{
    public ValueTask ExecuteAsync(MySignal signal, CancellationToken ct) => default;
}
```

**Migration Steps:**
1. Review all command classes
2. Remove duplicate interface implementations
3. Choose sync or async based on use case
4. Update registrations if needed

#### 3. FireAsyncAndForget Return Type

**Before:**
```csharp
// Returned async void (fire-and-forget)
signalBus.FireAsyncAndForget(new MySignal());
```

**After (current API):**
```csharp
// Returns void and does not wait; a failure goes to onError,
// or to SignalBus.OnUnhandledException when onError is null
signalBus.FireAsyncAndForget(new MySignal(), onError: ex => Debug.LogException(ex));
```

**Migration Steps:**
1. Call `FireAsyncAndForget()` without `await`: it returns `void` and does not wait for the handlers
2. Pass `onError`, or subscribe to `SignalBus.OnUnhandledException`, to observe failures
3. Use `await FireAsync()` when the caller must wait for completion

### New Features

#### 1. AOT Code Generation

Enable automatic binder generation with the menu item **Nexus > Auto-Generate AOT on Script Reload**
(a toggle, off by default, stored per machine in `EditorPrefs`), or from editor code:

```csharp
NexusCodeGenerator.AutoGenerateEnabled = true;
```

Generated files:
- `NexusGeneratedBinder.g.cs` - Reflection-free injectors
- `link.xml` - IL2CPP code stripping prevention

#### 2. Circular Dependency Detection

```csharp
// Now throws InvalidOperationException for circular dependencies
public class ServiceA
{
    [Inject] private ServiceB _b;
}

public class ServiceB
{
    [Inject] private ServiceA _a; // Throws during resolution
}
```

#### 3. Enhanced Memory Management

- ArrayPool integration for concurrent execution
- Static field/property caching in generated binder
- IDisposable singleton tracking

---

## General Migration Tips

### 1. Backup Your Project

Before upgrading:
1. Create a git branch: `git checkout -b backup-before-nexus-upgrade`
2. Commit current state: `git commit -am "Backup before Nexus upgrade"`
3. Test upgrade in a separate branch

### 2. Update Incrementally

For major version jumps:
1. Upgrade one version at a time
2. Test after each upgrade
3. Fix breaking changes before proceeding
4. Document any custom modifications

### 3. Regenerate AOT Binder

After upgrading:
1. Open `Nexus > Generate AOT Binder`
2. Review generated files
3. The generator writes a `.gitignore` beside `NexusGeneratedBinder.g.cs` and `link.xml` that ignores both; a player build regenerates them in its pre-build step (unless `NEXUS_DISABLE_AUTOGEN=1`)
4. To track them in version control instead, add them once with `git add -f` (the generator re-adds the `.gitignore` entries on every run, and an ignore entry does not affect a file that is already tracked)

### 4. Run Tests

After migration:
1. Run all unit tests
2. Run integration tests
3. Test critical game flows
4. Verify editor tools work correctly

### 5. Check Build Settings

For platform-specific builds:
1. Test iOS/Android builds (AOT)
2. Test WebGL builds (IL2CPP)
3. Verify link.xml is included
4. Check for code stripping issues

### 6. Review Performance

After upgrade:
1. Profile memory allocation
2. Check GC allocation rates
3. Verify signal dispatch performance
4. Monitor DI resolution times

### 7. Update Documentation

After successful migration:
1. Update internal documentation
2. Note any breaking changes
3. Document custom workarounds
4. Share knowledge with team

---

## Need Help?

If you encounter issues during migration:

1. **Check the Troubleshooting Guide** - [TROUBLESHOOTING.md](TROUBLESHOOTING.md)
2. **Review GitHub Issues** - [GitHub Issues](https://github.com/CevatAYDN/GameContainer/issues)
3. **Open an Issue** - [New issue](https://github.com/CevatAYDN/GameContainer/issues/new/choose) (the repository has no Discussions)
4. **Check Sample Projects** - [Samples~/Counter](Samples~/Counter/README.md)
