# API Stability & the Road to 1.0

> **Status: pre-1.0 (v0.3.1).** The public API may still change. `BREAKING_CHANGES.md`
> and `MIGRATION.md` manage transitions, but **consumers MUST pin a version** (see
> `ADOPTION.md`). 1.0 is declared only when every checkbox below is checked.

## API Freeze Checklist

### 1. Core runtime public surface
- [ ] `NexusRuntime` (context bootstrap / `CurrentContext`)
- [ ] `SignalBus` / `ISignalBus` (dispatch, subscribe, `Fire` / `FireAsync`)
- [ ] `NexusDI` (constructor / field / property / method injection)
- [ ] `ObservableProperty<T>` (reactive model primitive)
- [ ] `IContextLifecycle` / `IContextBuilder` (lifecycle + wiring)

**Already stable:** signal/command dispatch, `ObservableProperty<T>`, DI injection.
**Needs review:** netcode (`INetworkSignal`) and the DOTS bridge — less mature, may move.

### 2. Command model
- [ ] `ICommand<T>` / `IAsyncCommand<T>`
- [ ] `ExecutionMode` enum (Sequential, Concurrent, Exclusive, Composite)
- [ ] `[CommandTimeout]` and recovery strategies (Retry / Fallback / Abort)

**Already stable:** the four execution modes and generic command interfaces.
**Needs review:** default recovery-strategy selection semantics.

### 3. Services contracts (13 built-in)
- [ ] `IWindowManager`
- [ ] `IAudioService`
- [ ] `IHapticService`
- [ ] `IFeedbackService`
- [ ] `IAdService`
- [ ] `IIapService`
- [ ] `IEconomyService`
- [ ] `IProgressionService`
- [ ] `ITickService`
- [ ] `ILocalizationService`
- [ ] `IAnalyticsService`
- [ ] `IPlayerPrefsService`
- [ ] `IEncryptedStorageService`

**Already stable:** the service *catalog* (see README §13). **Needs review:**
per-service method signatures before 1.0.

### 4. Editor tooling contracts
- [ ] `NexusWindow` plugin API
- [ ] `BuildValidation` (architectural rules)
- [ ] `NexusCodeGenerator` AOT binder
- [ ] `NexusEditorSettings` (binder / `link.xml` output paths)

**Already stable:** build-time AOT binder regeneration (`NexusBuildPreProcessor`).

### 5. `Samples~/Counter` as the compatibility contract
- [ ] The Counter sample keeps exercising every building block (model, 4 modes,
      async, composite, services, tracing, recovery).

## Versioning & Release

- Semantic versioning. A change is **breaking** when it alters a public signature
  or observable behavior → record it in `BREAKING_CHANGES.md` and add a
  `MIGRATION.md` entry.
- **Patch** = internal/bugfix, **Minor** = additive/non-breaking, **Major** = breaking.
- 1.0 is declared only when all five checklist areas above are fully checked.

## How to propose an API change

1. Open an issue describing the motivation and the affected surface area.
2. Flag it as breaking or non-breaking.
3. If breaking: provide the `BREAKING_CHANGES.md` + `MIGRATION.md` draft.
4. Link the change to the relevant checklist box above.
5. Wait for the freeze review before landing in a release-tagged version.
