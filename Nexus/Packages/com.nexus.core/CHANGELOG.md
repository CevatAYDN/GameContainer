# Changelog

All notable changes to the Nexus Core package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Security
- **Async in-flight overflow throws in ALL builds (A9)** — `CommandExecutor`'s `MaxInFlightAsyncCommands` guard previously logged and silently dropped the command in Release while the caller's async chain completed "successfully"; it now throws `NexusAsyncOverflowException` unconditionally (same principle as the A8 reentrancy guard), and the guard is unified into a single `EnterAsyncInFlight()` used by all four async dispatch paths — including composite async commands, which previously bypassed the cap entirely.
- **NetworkMonitor latency history is thread-safe (A9)** — `s_latencyHistory` and the shared `ConnectionStatus` were written from network/background threads and read from the game/editor thread on a plain `Dictionary` (the same BUG-17 race `PerformanceMonitor` already fixed). All access is now guarded by a dedicated `s_historyLock`.
- **Reentrancy guard throws in ALL builds (A8)** — `SignalBus` no longer silently returns on `MaxStackDepth` overflow in Release/development-less builds; the `#if UNITY_EDITOR || DEVELOPMENT_BUILD` guard was removed from both the sync and async dispatch paths, so a runaway signal chain now always throws `NexusReentrancyException` (skill checklist §7) instead of hiding state corruption.
- **Atomic save writes everywhere (A8)** — `GameSaveManager` now publishes saves with a single overwrite-rename (`File.Replace`, fallback `File.Move`) instead of the delete-then-move pair that left a crash window where the only good save was already deleted (skill checklist §4.1).
- **Non-blocking teardown (A8)** — `Context` now implements `IAsyncDisposable` (`DisposeAsync()`); the synchronous `Dispose()` path no longer blocks the Unity main thread on `IAsyncDisposable` singletons — async disposal is scheduled on the thread pool with error capture (skill checklist §3.1). Callers that can await should prefer `await context.DisposeAsync()`.
- **Hardware-tick offline anti-cheat (A8)** — `OfflineTimeCalculator` now stores a monotonic hardware tick (`Environment.TickCount64`) alongside the wall clock and clamps offline rewards to the hardware-measured elapsed time, closing the forward-clock hole (skill checklist §4.1). On reboot (monotonic reset) it falls back to wall-clock-only validation, still capped.
- **Captive-dependency validation (A8)** — `ContextBuilder.Validate()` now reports `DiValidationIssueType.CaptiveDependency` when a singleton service captures a transient (non-singleton, non-factory) dependency in its constructor or `[Inject]` members (skill checklist §2.2).

### Changed
- **NetworkMonitor `CurrentStatus` returns a defensive snapshot (A9)** — the property previously exposed the live shared `ConnectionStatus` instance (readers could observe — or mutate — concurrent updates); it now returns a copy built under the lock, and `OnConnectionStatusChanged` delivers a snapshot too. This is a deliberate contract change: consumers must not hold the reference expecting live updates. Snapshot construction lives in one `SnapshotStatus()` helper.
- **ObjectPoolService — dropped obsolete API (A8)** — the `GetInstanceID()` reflection hack and the `#pragma warning disable 0619` suppressions were removed in favor of the Unity 6 `Object.GetEntityId()` API (skill checklist §5.1 zero-warning policy); also fixed a `CS0649` in `NexusBinding._customTargets`.
- **AssemblyCatalog (editor) — one deep module owns "which assemblies do tools scan?"** — the `GetLoadedAssemblies() → filter → GetTypes()` loop was previously re-implemented ~24× across 11 editor files (NexusWindow, BuildValidation ×10, NexusEditorDataProvider, NexusCodeGenerator ×2, Dashboard/Explorer/GameManager/Graph/TypeAnalyzer/Wizard plugins) with **four divergent filter predicates**, so tools disagreed about what code they could see. New `Editor/Core/AssemblyCatalog.cs` centralises iteration, the predicates (framework / third-party / test / editor classification) and the safe `GetTypesSafe` enumeration (a `ReflectionTypeLoadException` yields the partially-loaded types + one warning instead of throwing). All 11 call sites route through it. **Behavior notes:** test assemblies and third-party assemblies (Newtonsoft, Grpc, ExCSS, log4net, TextMateSharp, Onigwrap, Plastic/Codice, MCPForUnity, JetBrains) are now *consistently* excluded from every scan — previously only BuildValidation excluded them, so dashboard/GameManager type counts and Explorer/TypeAnalyzer search reach shrink slightly; `UnityEditor.*` assemblies are also excluded from NexusWindow plugin discovery (plugins never lived there).
- **NexusFieldInspector (editor) — one module owns the type→UI Toolkit field mapping** — the near-identical switch trees in `ExplorerPlugin.CreateSignalFieldUI` and `HierarchyPlugin.CreateFieldUI` (which had already drifted: read-only support, undo hooks, fallback labels) are replaced by `Editor/Core/NexusFieldInspector.cs` (`CreateField` + `EnumerateMembers`). Hierarchy keeps its undo hook and live binding-tracker wiring via callbacks; Explorer keeps its localized unsupported-type fallback.
- **RecoveryEngine — one decision tree, two thin entry points** — the ~70-line failure-decision policy previously duplicated in `HandleErrorWithDecision` / `HandleErrorWithDecisionAsync` is now a single `BuildPlan` with thin sync/async wrappers, so retry/recovery semantics cannot drift between the two paths.
- **Test harness routes through production registration** — `SignalBus.RegisterCommandType` is now the shared registration path for both the runtime assembly scan (`Context`) and the test harness (`NexusTestContext`), so attribute parsing and sync/async classification can never drift between production and tests. `NexusTestContext` now exposes the production `IContextBuilder` (instead of re-declaring a parallel `Bind` surface) and no longer pre-binds command types (the registry binds them itself).
- **DashboardSections** — removed `DashboardPlugin.CreateSectionTitle`, a shallow passthrough to `DashboardSections.CreateSectionTitle`; call sites now call the module directly.

### Added
- **StrangeIoC-style binder & named injection** — `NexusBinder<TKey,TValue>` / `IBinder<TKey,TValue>` general-purpose fluent binder for non-MVCS catalogs (`Bind(key).To/.ToName/.ToFactory/.To<T>()`); named DI (`[Inject(Name=...)]` + `Bind<T>(name)`/`Resolve<T>(name)`, strict no-fallback — an unregistered name throws instead of silently using the default); lifecycle hooks (`[PostConstruct(Order)]`, `[Deconstruct(Order)]`, `[Construct]` alias); polymorphic shared-singleton binding (`BindMultiple<T1,T2[,T3],TImpl>()`); and race-safe one-shot commands (`BindCommandOnce`/`BindAsyncCommandOnce`/`.Once()`). Proven by `tools/nexus-benchmark` `BinderSuite` (B1–B8, DI1–DI6, P1–P3, DC1, CT1, ON1–ON3, ONC1–ONC2, PM1–PM3) and `Tests/Editor/StrangeStyleCapabilitiesTests.cs`.
- **CommandExecutor & RecoveryEngine** — extracted from `SignalBus` so dispatch execution and recovery strategies live in testable modules (the old `CommandExecutionPipeline` orphan was dropped).
- **UICanvasSystem** — canvas root creation, per-layer transforms, and modal interactivity policy moved into a dedicated module, making `WindowManager` a pure window-lifecycle orchestrator (ADR-0001).
- **CompiledAccessorEmitter** — shared IL emitter for compiled field/property setter/getter delegates with reflection fallback (AOT/IL2CPP-safe); backs `NexusDI` injection/clearing.
- **Shared observer core** — `ObservableProperty`/`ObservableList` observer machinery merged into one core (`SecureObservableCore`) with registration dedupe; supports the zero-GC observer contract.
- **docs/REVIEW_FINDINGS_A1_B8.md** — permanent record of the 2026-08-01 review findings (A1–A6 + B1–B8) with per-finding fix evidence, closing the gap where the findings list previously existed only in a commit message.
- **docs/INTEGRATION.md** — integration guide covering UPM (Git URL), npm (not applicable), and git submodule integration paths, including consumer-project verification steps.
- **package.json** — declared `com.unity.ugui` (2.0.0) in `dependencies` (runtime asmdef references `UnityEngine.UI`; previously undeclared, which could break consumers without UGUI).
- **IFloatingTextService & FloatingTextService** — 0-GC pooled floating text manager for World-Space to Screen UI numbers (`+$500`, `+$1.2M`, `-25 HP`).
- **IEncryptedStorageService Cloud Sync** — `ExportEncryptedSaveData()` & `ImportEncryptedSaveData(base64)` for syncing AES-256 encrypted saves to Firebase, PlayFab, or Cloud Save.
- **OfflineTimeCalculator** — safe offline duration calculator with anti-cheat detection against device clock tampering.
- **IInputService & InputService** — 0-GC Virtual Joystick and Desktop Keyboard/Touch input service with `PlayerMoveSignal` struct signal.
- **PlaySfxWithRandomPitch** — `IAudioService` extension for playing SFX with randomized pitch variations to prevent repetitive robotic sound feel.
- **BigDouble & SecureObservableBigDouble** — lightweight 0-GC struct for Idle & Incremental games supporting numbers up to $10^{308}+$ with auto-normalization, Idle suffixes (K, M, B, T, aa, ab...), RAM obfuscation, and AES-256 encrypted storage (`GetBigDouble`/`SetBigDouble`).
- **INetworkAdapter** — official abstraction interface connecting 3rd-party multiplayer network frameworks (Photon Fusion, Netcode for GameObjects, Mirror, FishNet) to Nexus SignalBus with latency tracking and `NetworkMonitor` integration.
- **AOT / IL2CPP Compilation Guards** — `NexusDI.cs` now explicitly bypasses `Expression.Compile()` on AOT platforms (`ENABLE_IL2CPP`, `UNITY_AOT`, `UNITY_IOS`, `UNITY_WEBGL`), eliminating try-catch JIT exception overhead on iOS/WebGL/Consoles.
- **SignalBus Direct HybridQueue Caching** — `SignalBus` caches `HybridQueue` reference on creation, ensuring 0-lock, thread-safe background thread signal enqueueing (`FireThreadSafe<T>`).

### Security
- **SecureObservableInt/Long/Float/String** — memory obfuscation upgraded from single-XOR to **dual independent keys** with integrity canary (`_guard`). A memory scanner must locate three separate fields to reconstruct the plaintext. Keys are regenerated on every write. Integrity canary detects tampering on read. (Previously: single key stored adjacent to value — GameGuardian/CheatEngine read both fields.)
- **EncryptedStorageService** — HMAC-SHA256 output is now stored in **full 32 bytes** (previously truncated to 16, reducing effective security). New on-disk format (v2): `[VERSION:1] [IV:16] [HMAC:32] [ciphertext:N]`. Legacy v1 files (16-byte HMAC) are detected and migrated to v2 on first read. Format version byte enables forward migration.

### Fixed
- **EncryptedStorageService** — replaced `File.Move(src, dst, overwrite)` (`.NET Core 3.0+`-only, absent from Unity's .NET Standard 2.1 reference profile) with `File.Replace` for atomic overwrite; first-save path falls back to `File.Move` when the destination does not exist (no `FileNotFoundException` on first write).
- **EncryptedStorageService** — `SaveKeyToDisk` retry backoff now uses `Thread.Yield()` instead of `Thread.Sleep(10)` (no main-thread stall); `ExportEncryptedSaveData` file I/O moved outside the shared lock (B1 parity).
- **Storage restore marshaling** — `GameSaveManager.LoadAsync` restores model state through the captured main-thread `SynchronizationContext` (null-safe fallback), so a save restore can never touch Unity APIs from a worker thread.
- **SignalBus.Dispose** — added a `_disposed` guard preventing double-dispose through the `Context.Dispose → Container.Dispose` chain.
- **WindowManager.Dispose** — clears `_pendingOpenWindows` so async openers stuck in the instantiation await cannot leave orphaned pending entries.
- **NexusDI** — concurrent singleton construction now waits for the in-progress builder (10 s deadline, exception-safe marker cleanup) instead of throwing a spurious "circular dependency"; `_disposed`/`Binding.Instance` are `volatile`.
- **EconomyService** — `Spend`/`Earn`/`SetBalance` release the `_balances` lock before `SaveBalance` and network validation (I/O and async calls cannot stall the main thread); `GetObservableBalance` is lock-free for existing entries.
- **Root.IsInitialized / LazyInjection** — cross-thread fields are now `volatile` for correct visibility on the ARM memory model; sync-in-async dispatch respects `CancellationToken`.
- **TracerPlugin** — `_selectedEventId` is reset in `OnDisable` (matching the `ClearTraces` contract); **FSMPlugin** silent catch in `OnStateChanged` unsubscribe now logs via `NexusRuntime.Logger.LogWarning`.
- **NexusWindow.SwitchToPlugin** — now calls `OnEnable` on the target plugin (previously only called `OnDisable` on the old one); **NexusSetupWizard** logs a warning instead of silently creating an EventSystem without an InputModule; **Dashboard/GraphPlugin** scheduled items are paused/nullified on disable.
- **BuildValidation / CodeGenerator** — protected `link.xml` from accidental overwrite; `BuildTypeScriptCache` scans nested types so `[SignalHandler]` commands defined as inner classes are findable by the async-call-graph cycle detector; remaining silent `ReflectionTypeLoadException` catches replaced with warnings.
- **Context.Dispose** — resolved `INexusService` singletons are now disposed even when no `ContextBuilder` was configured (bare test contexts that bind services directly through the container previously orphaned them). Service lifecycle is Context-owned per the `NexusDI.Dispose` contract, so a missing builder no longer skips cleanup.
- **PerformanceMonitor** — removed dead `s_lastFrameTime`/`s_frameCount` fields.
- **AdService** — `ShowInterstitial` no longer invokes `onComplete` callback inside the `_lock`, preventing potential deadlock when the callback re-enters the service (e.g. showing another ad). Critical path restructured to `lock → check → unlock → callback`.
- **ResourcesUIAssetProvider** — replaced busy-wait loop (`while(!request.isDone) await Task.Yield()`) with direct `await request`, eliminating unnecessary per-frame re-scheduling. Added null asset handling after load.
- **GameStateMachine.Dispose** — `_stateCts` write/read now uses `Interlocked.Exchange` for thread safety. `Tick()` reads `_currentState` into a local copy before invocation to prevent null-ref during concurrent Dispose.
- **ObjectPoolService** — `ClearPool()` and `ClearAllPools()` now call `OnDespawned()` on active instances before destroying them, matching the lifecycle contract established by `Despawn()`. Previously active instances were destroyed without notification.
- **6 Editor plugins** migrated from `_view.schedule.Execute().Every()` to `OnUpdate()`:
  - `FSMPlugin` — 300ms schedule → `OnUpdate()` with 300ms throttle
  - `ContextInspectorPlugin` — 500ms schedule removed (OnUpdate already handled refresh)
  - `CasualServicesPlugin` — 500ms schedule → `OnUpdate()` with 500ms throttle
  - `NetworkDashboardPlugin` — 500ms schedule → `OnUpdate()` with 500ms throttle
  - `PerformanceDashboardPlugin` — field declaration cleanup (already used OnUpdate)
  - `GraphPlugin` — 100ms highlight-drain schedule → `OnUpdate()` with 100ms throttle
- **ExplorerPlugin** — `EditorApplication.playModeStateChanged` subscription moved from `CreateView()`-only into the paired `CreateView`/`OnDisable` lifecycle (dedupe `-=`+`+=` on show, `-=` on hide). Previously the subscription was never removed on tab switch; `NexusWindow` calls `CreateView` on every tab show but `OnEnable` only once at window open, so an `OnEnable`-based subscription would be lost after the first tab switch.
- **GameManagerPlugin** — `OnDisable()` now unsubscribes `playModeStateChanged` **before** `base.OnDisable()` (was after), so the event handler can never fire against a half-torn-down plugin during tab switch.
- **NexusWindow** — `CreateGUI()` callback/schedule registration is now guarded by `_uiCallbacksRegistered`: `RefreshDiscovery()` (Ctrl+F5) and `SetLocale()` re-run `CreateGUI()` on the same root, and `root.Clear()` does not remove callbacks/schedules — previously each re-run stacked a duplicate `OnScheduledUpdate` (200ms) and duplicate `KeyDownEvent`/`ContextClickEvent` handlers. Flag reset in `OnDisable()` so re-open re-registers.
- **CommandExecutionPipeline.cs** — removed (dead code, zero references across Runtime + Editor + Tests)
- **SignalBus.Dispose** — `_inFlightAsyncCommands` is now read via `Volatile.Read()` instead of unsynchronized field access, preventing a race condition when Dispose() is called concurrently with in-flight async commands.
- **CommandPool** — `Cleanup()` (reflection-based `ClearInjectedReferences`) is now skipped for command types with zero `[Inject]` fields, reducing CPU overhead on every `Return()` for simple commands.
- **NexusDI** — `s_setterCompileWarnings` dictionary bounded at 1024 entries to prevent unbounded memory growth in long-running editor sessions with many assemblies.

### Changed (Editor)
- **Editor plugin pattern enforcement** — all 15 plugins now comply with `INexusEditorPlugin.OnUpdate()` contract. No plugin uses `_view.schedule` for recurring updates.
- `IEconomyService.GetObservableBalance` return type changed from `ObservableProperty<long>` to `SecureObservableLong` (the new XOR-masked anti-cheat wrapper). The `Value`/`OnChanged`/`SetWithoutNotify` surface is identical, but code that typed the result as `ObservableProperty<long>` must be updated.
- `IProgressionService.CurrentLevel` / `MaxUnlockedLevel` changed from `ObservableProperty<int>` to `SecureObservableInt` (XOR-masked anti-cheat wrapper, matching `EconomyService`). The `Value`/`OnChanged`/`SetWithoutNotify` surface is identical.

### Security
- Added `SecureObservableLong` (XOR-masked RAM obfuscation, mirroring `SecureObservableInt`) and migrated `EconomyService` balances to it — currency values are no longer stored as plain `long` in memory, closing the anti-cheat gap flagged for RAM scanners (GameGuardian/CheatEngine).
- Added `SecureObservableFloat` (XOR-masked RAM obfuscation for IEEE-754 bit patterns, mirroring `SecureObservableInt`/`SecureObservableLong`) and migrated `AdService` interstitial cooldown + last-show timestamp to it — a memory scan can no longer zero the cooldown or backdate the timer to spam interstitials.
- `IapService` editor/dev mock ownership is now guarded by a **salted, rotating-mask checksum** over `_mockOwnedProducts` — a RAM scan (GameGuardian/CheatEngine) can append a fake product ID to the HashSet, but `IsProductOwned` recomputes the checksum before every read, detects the mismatch, wipes the tampered set and denies the forged ownership (fail-closed). Hardening beyond the original session-stable hash: the checksum is folded with a per-instance salt (`_mockOwnedSalt`) and XOR-masked by `_mockOwnedMask` which rotates (`*31+17`) on every successful verify — so value-scans fail across instances and snapshot-replay of an observed (checksum, mask) pair is detected on the next read. Still deterrence, not a security boundary: the real ownership gate is the release `#else` path that never trusts the mock set.
- `ProgressionService` level data migrated to `SecureObservableInt` — `CurrentLevel`/`MaxUnlockedLevel` are no longer plain `int` in RAM.
- `EconomyService.Earn` now clamps at `long.MaxValue` instead of overflowing to a negative balance.
- `ProgressionService.CalculateUpgradeCost` clamps NaN/Infinity/overflow at `long.MaxValue` (the old unchecked `double`→`long` cast wrapped to `long.MinValue` at extreme levels) and never returns a cost below the base cost (Linear curves with `multiplier < 1` previously went negative).
- `EconomyService.Spend` is now reconciled with the network validator: a server-rejected spend rolls the locally-deducted amount back (bounded at `long.MaxValue`), preventing client/server balance desync.

### Added
- `SecureObservableString` (per-char XOR-masked RAM obfuscation, mirroring `SecureObservableInt`/`Long`/`Float`) — player usernames, session tokens and other string state no longer sit in plain RAM where GameGuardian/CheatEngine string scans can read or edit them. Same reactive API (`OnChanged`, `SetWithoutNotify`, implicit `string`).
- `ViewBinder` pool telemetry — `PoolPopCount`/`PoolReturnCount`/`PoolResetCount`/`PoolLeakWarnings`/`ActiveMediatorCount` counters plus a leak warning when a mediator is returned to the pool while still tracked as active (zombie-binding signal for double-unregister).
- `docs/SERVICE_AUDIT.md` — full line-by-line audit of all 13 core services (hot-path allocations, O(N²) risks, concurrency, anti-cheat), documenting the two fixes below plus 11 clean/stub verdicts.
- `docs/REVIEW_VALIDATION.md` — verification & resolution tracker for the 31-Jul detailed code-review report's 4 action items (status, evidence file:line, tests per item).
- `RecoveryTests.CommandTimeout_CancelsHangingCommand_DoesNotBlockRetryLoop` — proves a `[CommandTimeout]`-annotated hanging async command is cancelled via the linked CTS and never re-enters the retry loop (no infinite retry even with `Retry(10)`).
- `ViewBindingTests.MediatorBase_ResetCalledOnPoolReturnAndPop` — proves the `Mediator<TView>` base implements `IResettable` and both pool directions invoke the `OnReset` hook.
- `EncryptedStorageAndAntiCheatTests` — `IapService_MockOwnedIntegrity_TamperDetectedAndSetCleared` + `IapService_MockOwned_NormalFlowUnaffected` (mock-ownership checksum fail-closed behavior).
- `NetcodeTests` — named-argument consistency guards (reflection check + compile-time named-argument calls) that catch a `Prune(confirmedTick:)`-style parameter rename at build/test time; replay consistency stress tests (200-tick deterministic rollback, future-prediction pruning, repeated-rollback idempotency); and a `Prune` steady-state zero-allocation measurement (500 cycles × 1000 signals, asserts ~0 bytes).
- `GameStateMachine` transition history — fixed-size 32-slot ring buffer of `StateTransitionRecord` (timestamp, from/to state type names, args type summary, `Success`/`Superseded`/`Failed` status, duration ms), a `OnStateChanged` event on the concrete class (non-breaking: the `IGameStateMachine` interface is untouched), and `NexusTrace.StateTransition` causal tracing (NEXUS_DEBUG-only).
- `FSMPlugin` transition log is now event-driven: concrete machines are subscribed to `OnStateChanged` (real-time, nothing missed), while custom `IGameStateMachine` implementations keep the polling diff fallback.
- `TickService` profiler markers — `Nexus.TickService.Update/FixedUpdate/LateUpdate` via unconditional static `ProfilerMarker`s (same pattern as `SignalBus`; zero-allocation in all builds).

### Fixed
- `FSMTransitionHistoryTests.FSM_SupersededTransitionIsRecordedAndSkipped` test expectation corrected: the machine records EVERY transition attempt, so the supersession scenario yields 3 records (initial `null→SlowExit` Success, preempted A Superseded, B Success), not 2 — the test previously asserted the wrong count and shifted indices.
- `ViewBinder.GetMediator` now calls `(mediator as IResettable)?.Reset()` before re-injecting a pooled mediator — pooled reuse hygiene is now two-way (return-to-pool resets via `ClearInjectedReferences`, pop-from-pool resets defensively) so stale private state from a previous view session can never leak into a new binding.
- `Mediator<TView>` now implements `IResettable` (with `Reset()` + protected virtual `OnReset()` hook) so pooled-reuse hygiene is **mandatory for all mediators**, not opt-in: `Reset()` disposes surviving subscriptions, nulls `View`/`SignalBus`, and invokes `OnReset()` for derived private state. Idempotent and safe on a freshly created mediator.
- `HapticService` Android hot path is now allocation-minimal: the six `VibrationEffect` objects (one per `HapticType`) are pre-created at init and cached, eliminating the per-trigger `createOneShot` `AndroidJavaObject` + boxed-args allocation that previously violated the service's 0-GC claim. `OnDispose` releases the cached effects.
- `AudioService` SFX pool is now bounded (`MaxSfxPoolSize = 32`): the old `GetAvailableSfxSource` grew the pool unboundedly (new GameObject + interpolated name per allocation) and degraded to effectively O(N²) under SFX bursts. When exhausted, the oldest channel is stolen instead of allocating. `PlaySfx` also swaps inverted pitch ranges before `Random.Range` (mirroring `FeedbackService.PlayCustom`) so a `pitchMin > pitchMax` call can no longer throw.
- `AudioService.BgmStateMultiplier` docs corrected: the transient ducking scalar is never auto-reset by the service — the caller (e.g. the state machine) must restore 1.0 on returning to the main menu.
- `NetworkSignalHistory` prune paths rewritten from backwards `RemoveAt` loops (O(N²)) to single-pass in-place compaction — `Prune` and `RemoveSignalsAfter` are now O(N) and zero-allocation, preserving the 0-GC steady-state guarantee.
- `Prune(int tick)` parameter renamed to `Prune(int confirmedTick)` across `INetworkSignalHistory`, `NetworkSignalHistory<T>`, `INetworkModelSnapshotHandler` and `NetworkModelSnapshotHandler<TState>` for naming consistency with `PruneHistory(int confirmedTick)`. Positional callers are unaffected; only consumers using the named argument `Prune(tick: …)` would need to update.
- `Root.Start` now guards the post-await main-thread invariant: if lifecycle initialization resumes on a worker thread (user code calling `ConfigureAwait(false)`/`Task.Run` in an environment without a `SynchronizationContext`), the root logs a clear error and disposes the context deterministically instead of touching Unity API from a non-main thread.
- `GameStateMachine.ChangeStateAsync` race — concurrent transitions now serialize via a monotonic sequence: a superseded transition aborts at its next await instead of clobbering `_currentState` or running `OnEnterAsync` (previously two states could end up active). Cancellation sources are disposed by their owning transition in `finally`, so a state holding the old token can no longer hit `ObjectDisposedException`.
- `LocalizationService.FormatRTLIfNeeded` reverses by grapheme cluster (`StringInfo.ParseCombiningCharacters`) instead of raw UTF-16 code units — surrogate pairs (emoji) and combining marks survive RTL reversal.
- `TickService` register paths are deferred to a dirty flag: N registrations in one frame produce exactly one snapshot rebuild instead of one `ToArray()` per call (spawn/despawn storms). Unregister stays immediate so destroyed tickables never tick again.
- `NexusRuntime.Reset()` deadlock fix using snapshot-then-dispose pattern to dispose contexts outside locks during Play Mode transitions.
- Pure context lifecycle duplication — `CreatePureContextAsync` now routes through `Context.InitializeLifecycleAsync`.
- `SignalBus.BroadcastCrossContext` global registry access now abstracts cleanly via `IContextResolver`.
- Plugin discovery failures (`ReflectionTypeLoadException`) now render diagnostic details directly in the `NexusWindow` UI.
- `TracerPlugin` live trace state leaks — `_hasLiveEvents` and queues now reset cleanly in `OnDisable`.
- `DashboardPlugin` action card double-click bug — removed duplicate `MouseDownEvent` callback.
- `DashboardPlugin.QuickFind` keystroke lag — implemented static `s_typedCatalog` scanning cache with 200ms input debounce.
- `GameManagerPlugin.FireTestSignal` reflection overhead — added `s_fireMethodCache` for `MethodInfo` caching.
- `TracerPlugin` CS0411 generic callback type inference compile error fixed by specifying `EventCallback<MouseDownEvent>` explicitly.

### Fixed (Runtime adversarial review)
- PerformanceMonitor `s_samples` queue is now bounded (2000 samples) and history is a FIFO `Queue<float>` — previously the sample queue grew forever between `ClearHistory` drains, leaking managed heap (observed climbing to ~800 MB).
- `PerformanceMonitor.UpdateFrameMetrics` throttled to ~10 Hz (6-frame cadence) — per-frame sampling created ~180 allocations/sec of GC churn that spiked FPS every few seconds.
- `PerformanceMonitor` history is a bounded FIFO `Queue<float>` (O(1) dequeue, capped at `MaxHistorySize`) that never leaks regardless of recording state; only the sample queue and event notifications are gated on recording.
- `NexusDI` field/property injection and clearing now use compiled Expression-tree setters with a reflection fallback (AOT/IL2CPP-safe); the repeated setter-or-reflection branches were consolidated into shared helpers.
- `NexusDI` setter compile failures are logged once per type (was a silent `catch {}`) instead of being swallowed.
- `NexusVisualization.UpdateSparkline` reuses its bar children across refreshes instead of `Clear()`+re-adding hundreds of `VisualElement`s every 0.5 s while playing.
- `PerformanceDashboardPlugin` stat-row cache is cleared when the view/container is rebuilt or the plugin is disabled, preventing stale detached rows from leaving the summary card empty after a window reopen.
- Generic-only recovery fallback commands (`ICommand<TSignal>` / `IAsyncCommand<TSignal>` without the non-generic interface) were silently skipped by the object-based dispatch paths. They now execute via cached reflection dispatchers.
- Sync recovery error handler no longer dispatches async-only fallback types (which would throw and re-enter the same strategy decision, recursing forever) — they are rejected and treated as `Skip`.
- `RegisterCompositeCommand` now rejects duplicate and null signal types up front (a duplicate would set the same mask bit twice and the trigger could never fire).
- `NexusService<T>` double-dispose — `NexusDI.Dispose`/`DisposeAsync` now skip `INexusService` singletons (their lifecycle is owned by the Context, which calls `OnDispose()` exactly once, including lazy services resolved outside the eager `ServiceTypes` list).
- Lazy services first resolved during `OnStartAsync` now still receive `InitializeAsync` (second lazy drain after `OnStartAsync`).
- `CommandPool` double-return guard — an instance already in the pool is discarded instead of being pooled twice.
- `ContextBuilder.Validate()` now validates concrete implementations (`Bind<TInterface, TImplementation>`), previously only interface keys were checked, and no longer flags `LazyInjection<T>` fields (constructed directly by the injector).
- `SignalBus` handler/subscription read-copies are `volatile` with deep-copied snapshots; `FireAsyncAndForget` no longer surfaces `OperationCanceledException` during teardown; dead subscription nodes are swept immediately when not dispatching.
- `WindowManager.IsWindowOpen`/`GetWindow` now use a bounded 50 ms wait instead of `Wait(0)` — a briefly-held semaphore (background async open/close) no longer yields a false "window closed" that could trigger duplicate opens.
- `ObjectPoolService.DespawnAfter` now guards timers with a per-instance spawn-session generation — an instance manually despawned and re-spawned before the timer fires is no longer yanked out of the scene by the stale timer.
- `EncryptedStorageService.SaveKeyToDisk` retries the delete+move swap up to 3 times on `IOException` (Windows file-handle propagation: antivirus/indexer), instead of failing a save when the destination handle lingers.
- `CommandPoolManager.GetCommand` no longer allocates a closure on every call — the pool factory delegate is cached once per manager (hot-path allocation-free; also avoids the `GetOrAdd<TArg>` overload that is unavailable on .NET Standard 2.0).

### Changed
- Refactored 5 critical editor plugins (`Dashboard`, `Tracer`, `GameManager`, `Wizard`, `ContextInspector`) to override `OnUpdate` instead of creating custom UI timers (`_view.schedule` / `_root.schedule`).
- Standardized `OnDisable` state cleanup across all editor plugins.
- Replaced empty `catch {}` reflection blocks with `ReflectionTypeLoadException` warning logs.
- Consolidated duplicate stat card implementations into `NexusEditorStyles.CreateStatTile`.
- `TracerPlugin` log list converted from `ScrollView` rebuild to virtualized `ListView` with `makeItem`/`bindItem` pooling.
- Fixed `TracerPlugin` `ListView.fixedItemHeight` to `28f` for safe label padding.

### Added
- `IContextResolver` interface for safe cross-context signal dispatch.
- `NexusEditorStyles.CreateStatTile` helper method for unified metric card rendering.
- `NexusVisualization` — extracted sparkline, gauge, data-table, and stat-row helpers from `NexusEditorStyles`.
- `DashboardSections` — reusable UI-building helpers extracted from `DashboardPlugin`.
- `WizardTabs` — `IWizardTab` interface + 5 tab implementations extracted from `WizardPlugin`.
- Form validation (empty-field check + disabled Generate button) in `CreateRootTab`, `ViewMediatorGenTab`, and `SignalCommandGenTab`.
- `PluginRefactorValidationTests` under `Tests/Editor/` validating plugin lifecycles, virtualized item heights, and scheduler rules.
- `Editor/Locales/en.json` and `tr.json` localization catalogs.

### Changed (Runtime)
- `PerformanceMonitor` built-in metric updates (`UpdateFrameMetrics`/`UpdateMemoryMetrics`/`UpdateGCMetrics`) now early-out when not recording, and the initial frame metric is no longer silently dropped by the throttle guard.
- `PerformanceDashboardPlugin.OnDisable` now resets `_recording`, the stat-row cache, and the stats container reference (CONTRIBUTING state-reset rule).

### Changed (Runtime)
- `NexusDI` — added `EditorResolvedSingletons`, `GetEditorSingletonSnapshot()`, `GetEditorTypeMappings()` safe accessors for editor tools (replaces fragile reflection).
- `Context` — exposed `Builder` accessor property for editor inspection; `InitializeReactiveModelsAsync` dropped its unused `signalBus` parameter.
- `NexusEditorDataProvider` — replaced 4 `GetField`/`GetProperty` reflection calls with direct accessor calls.
- `ExplorerPlugin` — replaced `GetField("_bindings")` reflection with `GetEditorSingletonSnapshot()` call.
- `WizardPlugin` — decomposed monolithic ~1025-line class into coordinator (295 lines) + 5 `IWizardTab` implementations in `WizardTabs.cs`.
- Empty `catch {}` blocks in `NexusEditorDataProvider` and `ExplorerPlugin` now log via `Debug.LogWarning` instead of swallowing silently.

### Removed
- `NexusEditorStyles` — 5 visualization methods deprecated (`[Obsolete]` pointing to `NexusVisualization`).
- `DashboardSections.BuildStatusSection()` and `BuildHealthSection()` (unused — `DashboardPlugin` keeps inline versions).
- `WizardPlugin` — ~730 lines of dead `Build*Tab()` methods and ~90 lines of dead action/validation helpers (migrated to `WizardTabs.cs`).
- `WizardPlugin.FindBootstrapManifest()` — became unreachable after dead code removal.
- Unused language support for `ja`, `zh`, and `ko` from `NexusLang.cs` to focus exclusively on English (`en`) and Turkish (`tr`).
- Dead code `_renderedItems` collection in `TracerPlugin.cs`.
- Custom `_view.schedule` and `_root.schedule` timer calls in all editor plugins.

### Deprecated
- `_view.schedule` / `_root.schedule` pattern in editor plugins — plugins must override `INexusEditorPlugin.OnUpdate()` instead.

## [0.3.2] - 2026-06-15

### Added
- Causal tracing sink (`INexusTraceSink`) for background signal logging.
- `BuildValidation` silent rules engine for CI pipeline verification.
- `ObservableProperty<T>` for zero-GC reactive model updates.
