# Stability Policy

> **Status:** Pre-1.0 (v0.4.0)  
> Nexus is under active development. While the core architecture is stable, API surfaces may change between minor versions until 1.0.

---

## 📋 Pre-1.0 API Stability Commitments

| Guarantee | Status |
|-----------|--------|
| **Breaking changes documented** | ✅ `BREAKING_CHANGES.md` |
| **Migration guides provided** | ✅ `MIGRATION.md` |
| **SemVer respected** | ✅ Patch = bugfixes only |
| **Internal refactor without API changes** | ✅ Allowed at any time |
| **Public API frozen until 1.0** | ❌ May change |

---

## 🔄 Versioning Rules (Pre-1.0)

| Version Bump | What changes |
|:------------:|-------------|
| **Patch** (0.4.0 → 0.4.1) | Bug fixes, performance, docs — no public API changes |
| **Minor** (0.4.0 → 0.5.0) | New features, API additions, minor breaking changes |
| **Major** (0.x → 1.0) | Stabilization — API freeze begins |

---

## ✅ What You Can Rely On (Stable)

- `ISignalBus.Fire<T>()` and `FireAsync<T>()` signatures
- `ICommand<T>` and `IAsyncCommand<T>` interfaces
- `IContextLifecycle` lifecycle contract (`OnConfigure` / `OnInitializeAsync` / `OnStartAsync` / `OnDispose`)
- `NexusDI` injection via `[Inject]` attribute
- `ObservableProperty<T>` reactive system
- `Root` scene component behavior
- Command execution order: Commands → Subscriptions → Composites

---

## ⚠️ May Change (Experimental)

- `[SignalHandler]` attribute auto-discovery (canonical path is `BindCommand<>`)
- `NexusDOTSBridge` DOTS integration
- `NetworkSignalBus` and replay system
- `[CommandTimeout]` implementation details
- `IRecoveryStrategy` API specifics
- Non-generic `ICommand`/`IAsyncCommand` fallback paths (used for recovery composites)

---

## 🗺️ Path to 1.0

1. All 13 core services reach production validation
2. Performance benchmarks stabilize
3. Breaking changes rate drops to zero for 3 consecutive releases
4. Community adoption demonstrates API surface correctness

---

**Last updated:** 2026-07-30  
**Code version:** 0.4.0
