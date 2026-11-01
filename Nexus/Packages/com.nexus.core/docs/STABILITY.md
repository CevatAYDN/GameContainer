# Stability Policy

> **Status:** Pre-1.0 (v0.6.0)  
> Nexus is under active development. While the core architecture is stable, API surfaces may change between minor versions until 1.0.

---

## 📋 Pre-1.0 API Stability Commitments

| Guarantee | Status |
|-----------|--------|
| **Breaking changes documented** | ✅ `BREAKING_CHANGES.md` |
| **Migration guides provided** | ✅ `MIGRATION.md` |
| **SemVer respected** | ✅ Patch = bug fixes only; the one exception, 0.3.1, added `IPlayerPrefsService.GetLong`/`SetLong` (`BREAKING_CHANGES.md`) |
| **Internal refactor without API changes** | ✅ Allowed at any time |
| **Public API frozen until 1.0** | ❌ May change |

---

## 🔄 Versioning Rules (Pre-1.0)

| Version Bump | What changes |
|:------------:|-------------|
| **Patch** (0.6.0 → 0.6.1) | Bug fixes, performance, docs — no public API changes |
| **Minor** (0.6.0 → 0.7.0) | New features, API additions, minor breaking changes |
| **Major** (0.x → 1.0) | Stabilization — API freeze begins |

---

## ✅ What You Can Rely On (Stable)

- `ISignalBus.Fire<T>()` and `FireAsync<T>()` signatures
- `ICommand<T>` and `IAsyncCommand<T>` interfaces
- `IContextLifecycle` lifecycle contract (`OnConfigure` / `OnInitializeAsync` / `OnStartAsync` / `OnDispose`)
- `NexusDI` injection via `[Inject]` attribute
- `ObservableProperty<T>` reactive system
- `Root` scene component behavior
- Command execution order: Commands → Subscriptions (newest first) → Composites; a subscriber that throws does not stop the others (README "Signals and commands")
- **Service disposal contract** — every `INexusService` singleton gets `OnDispose()` exactly once (never a second `Dispose()`), with the context's other singletons in reverse creation order; a bare `NexusDI` or a child scope ends its services the same way (#197)

---

## ⚠️ May Change (Experimental)

- `[SignalHandler]` attribute auto-discovery (canonical path is `BindCommand<>`)
- DOTS bridge: `NativeSignalQueue<T>` and `DOTSSignalBridge<T>` (`com.nexus.dots` package, compiled when `com.unity.collections` is installed)
- `NetworkSignalBus` and replay system
- `[CommandTimeout]` implementation details
- `IRecoveryStrategy` API specifics
- Non-generic `ICommand`/`IAsyncCommand` fallback paths (used for recovery composites) — generic-only `ICommand<TSignal>`/`IAsyncCommand<TSignal>` fallback dispatch is now implemented and tested, but its exact behavior may still evolve pre-1.0

---

## 🗺️ Path to 1.0

1. All 16 core services reach production validation
2. Performance benchmarks stabilize
3. Breaking changes rate drops to zero for 3 consecutive releases
4. Community adoption demonstrates API surface correctness
