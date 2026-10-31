> **For AI Agents:** This document is the source of truth for contribution standards, verification rules, and automated test enforcement. Work in this repository is committed directly on `main`, with no branches or pull requests (the repository's `CLAUDE.md`). Before committing:
> 1. Complete the [Review Checklist](#-review-checklist-for-ai-agents) below.
> 2. Run all NUnit tests: EditMode `PluginRefactorValidationTests` and `InfrastructureValidationTests`.
> 3. Update `CHANGELOG.md` under `[Unreleased]`.
>
> **Out of scope:** Non-framework project files outside `GameContainer/Nexus/Packages/com.nexus.core`.

# Contributing to Nexus Core

Thank you for contributing to Nexus Core! Please follow these guidelines when submitting bug fixes, features, refactors, or documentation updates.

---

## 🛠️ Development & Coding Rules

### 1. Cross-Cutting Plugin Rules
All editor plugin changes must follow the 4 cross-cutting patterns:
- **`OnUpdate()` Override**: Do NOT use `_view.schedule` or custom update timers. Override `INexusEditorPlugin.OnUpdate()`.
- **State Reset in `OnDisable()`**: Reset all flags, queues, and debounces when the plugin is hidden.
- **Reflection Caching**: Cache `MethodInfo` lookups and assembly scan lists (`[DidReloadScripts]`).
- **No Empty Catches**: Log `ReflectionTypeLoadException.LoaderExceptions` using `Debug.LogWarning`.

### 2. Localization Policy
- Nexus supports **English (`en`)** and **Turkish (`tr`)**.
- Use `NexusLang.Get("key")` for all user-facing UI text.
- Do NOT hardcode string interpolations for count words (e.g. `$"{count} models"`).

### 3. Automated Testing Requirements
- Every P1/P2 fix or new plugin MUST include unit test coverage under `Tests/Editor/`.
- Verify that both `InfrastructureValidationTests` and `PluginRefactorValidationTests` pass cleanly in Unity Test Runner.

### 4. Synchronous-Blocking Exemptions (NEXUS003)
The `NexusArchitectureAnalyzer` rule NEXUS003 flags synchronous blocking calls in runtime
code: `Thread.Sleep` and sync-over-async `GetAwaiter().GetResult()` (which still blocks the
thread). Prefer a real `await Task.Delay(...)`. A blocking site is acceptable only when
awaiting is genuinely impossible — e.g. a synchronous `PlayerPrefs`-style API whose write
path also runs on the sync quit handler. In that case keep the call, append a trailing
`// NEXUS003-exempt: <reason>` comment to the SAME line stating why awaiting is impossible,
and use the `CancellationToken`-aware overload where one is available. Anything not marked
exempt is reported as an Error by the analyzer.

---

### 5. Wizard template changes

The Setup Wizard's eight generated files are single-source templates (`NexusSetupWizard` `*Template` properties) whose canonical copies live in the development project under `Nexus/Assets/Scripts/Game/Samples`. `WizardTemplateSyncTests` enforces four layers: the template mapping equals the canonical files byte for byte, the real write path reproduces those bytes in a temporary directory, the generated sources compile with Unity's own Roslyn, and each template's SHA-256 matches the "Template content hashes" table in `CHANGELOG.md`. To change a template:

1. Change the wizard template and the canonical sample file in the same commit, with identical content.
2. Run `WizardTemplateSyncTests`; the hash test's failure message prints the new SHA-256.
3. Update the "Template content hashes" table in `CHANGELOG.md` and state the reason for the change in the release notes.

### 6. Concurrency style guide

Nexus runs on Unity's main thread but exposes thread-safe surfaces (`FireThreadSafe`, network
rollback, worker-thread services). Shared state picks ONE documented pattern per structure; mixing
patterns in one type is the most common source of subtle bugs.

| Rule | When to use | Example |
|------|-------------|---------|
| **R1: Publish-through-snapshot** | Read-heavy state read on the hot path, mutated rarely | The command and subscription read copies `CommandRegistry._commandHandlersReadCopy` and `SubscriptionRegistry._subscriptionsReadCopy` |
| **R2: Lock + plain collection** | Write-heavy or compound (check-then-mutate) operations | `NexusDI._singletonLock` around singleton construction and the `List<object> _resolvedSingletonOrder`; `EconomyService._balanceLock` around balance mutations. The bindings themselves are a `ConcurrentDictionary<Type, Binding> _bindings` read without a lock |
| **R3: Interlocked counters / CAS flags** | Lock-free simple counters or one-shot claims | `NexusRuntime.Metrics` totals, `NexusRuntime.s_monitoringInitialized`, `HybridQueue` drain guards |
| **R4: Never invoke callbacks under a lock** | Any event/plugin/sink invocation | `NexusTrace` sink writes (snapshotted under the lock, invoked outside it); `NexusRuntime` context events (invoked after the lock is released) |

Anti-patterns to avoid:

- **`lock (concurrentCollection)`**: locking a `ConcurrentDictionary`/`ConcurrentQueue` itself is
  legal but misleading, because the collection's own synchronization is internal. Use a dedicated
  `private readonly object _xxxLock = new()` and document it (e.g. `EconomyService._balanceLock`).
- **Check-then-set on a `volatile` flag**: two threads can both pass the check. Use
  `Interlocked.CompareExchange` (claim) or a single `lock` scope.
- **Double reset on pooled objects**: decide whether reset happens on pop or on return, not both.
  If both are deliberate (defensive `Mediator.Reset`), the reset MUST be idempotent and the double
  call MUST be documented on the method.

### 7. Lifecycle ownership

- `ContextData` is **caller-owned** (scene/asset) unless created at runtime by
  `NexusRuntime.CreatePureContextAsync`, which marks it owned (`Context.OwnsContextData()`) and
  destroys it on dispose. Never destroy caller-provided assets.
- `INexusService` lifecycle (`InitializeAsync`/`OnDispose`) is owned by the owning `Context`;
  `NexusDI.Dispose` skips `INexusService` instances to avoid a double dispose. Non-service
  interfaces (e.g. `IPlayerPrefsService`) keep their own `IDisposable` contract; document which one
  a type uses.

## 📋 Review Checklist for AI Agents

When reviewing or self-auditing a change, validate every check item:

### Code Quality & Architecture
- [ ] No `_view.schedule`, `_root.schedule`, or `EditorApplication.update` calls in plugins.
- [ ] `OnUpdate()` properly overridden if periodic polling is needed.
- [ ] `OnDisable()` resets all instance flags, counters, queues, and debounces.
- [ ] No empty `catch {}` — log `ReflectionTypeLoadException.LoaderExceptions` via `Debug.LogWarning`.
- [ ] Reflection lookups cached on hot paths (`MethodInfo` dictionary).
- [ ] No hardcoded user-facing English strings in UI components.
- [ ] Stat displays use `NexusEditorStyles.CreateStatTile(...)`.

### Concurrency & Thread Safety
- [ ] `volatile` keyword used on fields read/written across threads without a lock (e.g. `Root.IsInitialized`, `LazyInjection._value`/`_resolved`).
- [ ] `Task.Run` or `async` continuations that call Unity APIs or `RestoreSaveData` marshal back to the captured `SynchronizationContext`.
- [ ] Lock scope is minimised: no I/O, `PlayerPrefs`, or network calls inside `lock` blocks.
- [ ] Signals dispatched via `FireThreadSafe`/`HybridQueue` not by direct `Fire<T>` when the caller may be off the main thread.
- [ ] `ThreadStatic` fields (e.g. `NexusDI.s_resolution`) are documented with the sync/async decision rationale.
- [ ] Disposal races prevented: `Dispose()` sets a `_disposed` flag before signalling waiters or running cleanup.

### Persistence & Anti-Cheat
- [ ] `EncryptedStorageService`: imported cloud payloads are HMAC-validated (`TryReadCurrentFormat`) before touching the local file.
- [ ] `Save()` retains dirty keys on write failure (caller retries) instead of clearing them unconditionally.
- [ ] `SecureObservable*` reads and writes are documented as memory-scan deterrence, not cryptographic guarantees.
- [ ] Persistent writes stage a temporary file and swap it in with `File.Replace` (atomic) where the platform supports it; where it does not, the fallback logs a warning (`GameSaveManager`: non-atomic delete-then-move; `EncryptedStorageService`: the previous file kept as `.bak` until the move completes).

### Automated Testing
- [ ] At least 1 NUnit test per P1/P2 fix added under `Tests/Editor/`.
- [ ] Test covers complete lifecycle (`CreateView`, `OnEnable`, `OnUpdate`, `OnDisable`).
- [ ] `InfrastructureValidationTests` passes cleanly.

### Documentation & Versioning
- [ ] `CHANGELOG.md` updated under `[Unreleased]` following Keep a Changelog.
- [ ] `LOCALIZATION_KEYS.md` updated if new i18n keys were added.
- [ ] Public API methods have XML doc comments.
- [ ] Cross-references in related documentation updated.

---

## 📝 Submitting Changes

Work in this repository (the owner and AI agents) is committed directly on `main`; the repository has
no other branch. An outside contributor forks the repository and opens a pull request from the fork,
which lands on `main` as commits. Either way:

1. **Format Commit Messages**: Use [Conventional Commits](https://www.conventionalcommits.org/):
   - `fix(tracer): replace ScrollView rebuild with ListView virtualization`
   - `perf(dashboard): cache assembly catalog and debounce search input`
   - `docs(readme): update language policy to en and tr`
2. **Verify NUnit Tests**: run them in the Unity Test Runner, or from the repository root with the
   commands in the repository's `CLAUDE.md`:
   ```powershell
   & "C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe" -batchmode -nographics -projectPath Nexus -runTests -testPlatform EditMode -testResults artifacts\editmode.xml -logFile Nexus\artifacts\editmode.log
   & "C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe" -batchmode -projectPath Nexus -runTests -testPlatform PlayMode -testResults artifacts\playmode.xml -logFile Nexus\artifacts\playmode.log
   ```

---

## 🔗 Related Documentation
- 📖 [README.md](../README.md) — Framework index, decision flows, and file map
- 📖 [PLUGIN_DEVELOPMENT.md](PLUGIN_DEVELOPMENT.md) — Plugin development & edge cases
- 📜 [CHANGELOG.md](../CHANGELOG.md) — Release notes and changelog
- 📋 Past review and audit reports are archived in the repository under `docs/archive/` (not shipped with the package); consult them before re-flagging hardening items as new findings
