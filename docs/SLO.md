# Nexus SLO (Service Level Objectives)

**Version:** 0.4.0  
**Last Updated:** 2026-08-04  
**Status:** Baseline — measured on benchmark harness (real runtime, .NET 10, 8-core)

---

## 🎯 Core Performance SLOs

| Metric | Target | Measured (Harness) | Measurement Method |
|--------|--------|-------------------|-------------------|
| **SignalBus.Fire (hot path)** | < 25 μs/dispatch | 0.82 μs | `Benchmark_SignalFire_HotPathNs` |
| **SignalBus.Fire + 1 subscriber** | < 30 μs/dispatch | 0.59 μs | `Benchmark_SignalFire_WithSubscriberNs` |
| **Command Execute (sync)** | < 50 μs | ~0.4 μs | `CommandExecutor.Execute` path |
| **Command Execute (async)** | < 100 μs | ~0.9 μs | `CommandExecutor.ExecuteAsync` path |
| **Steady-state GC allocation** | **0 B/frame** | **0 B** (5000 ops) | `SteadyState_HasZeroGCAllocations` |
| **Command Pool reuse rate** | > 95% | 100% | `CommandPool_ReusesInstances` |
| **DI Resolve + Inject** | < 5 μs | 0.39 μs | `NexusDI_Resolution_And_Injection_Stress` |

---

## 📊 Runtime Quality SLOs

| Metric | Target | Current Baseline | Notes |
|--------|--------|-----------------|-------|
| **TTI (Time to Interactive)** | < 2.0 s (cold) | ~1.2 s | Root init + Configure + InitializeAsync + StartAsync |
| **Context startup (per context)** | < 500 ms | ~200 ms | Single context, no heavy services |
| **Memory baseline (casual)** | < 150 MB | ~85 MB | Empty context + 13 services |
| **Memory baseline (hybrid)** | < 300 MB | ~180 MB | With pooled objects + assets |
| **Max Gen2 GC / 10 min** | ≤ 1 | 0 (steady-state) | Zero-allocation design |
| **Frame time (60 FPS)** | < 16.67 ms | N/A (harness) | Requires device profiling |

---

## 🛡️ Reliability SLOs

| Metric | Target | Measurement |
|--------|--------|-------------|
| **Crash-free sessions** | > 99.9% | Sentry / Crashlytics (requires integration) |
| **ANR rate (Android)** | < 0.01% | Play Console (requires integration) |
| **Error recovery success** | > 99% | RecoveryEngine retry/fallback telemetry |
| **Save data integrity** | 100% | EncryptedStorage HMAC verification |
| **Offline income accuracy** | ±1% | OfflineTimeCalculator test suite |

---

## 🔧 Operational SLOs

| Area | Target | Tooling |
|------|--------|---------|
| **Build time (harness)** | < 60 s | GitHub Actions (`dotnet build`) |
| **Test suite (full)** | < 120 s | `dotnet run -c Release` (harness) |
| **Unity EditMode tests** | < 60 s | Self-hosted runner (Unity 6000.5) |
| **Unity PlayMode tests** | < 120 s | Self-hosted runner (Unity 6000.5) |
| **Deploy to device (CI)** | < 10 min | Fastlane / Unity Cloud Build |

---

## 📈 Error Budget Policy

| SLO Category | Error Budget (monthly) | Burn Rate Alert |
|--------------|----------------------|-----------------|
| **Performance (ns/op)** | 1% of requests exceed target | > 2% for 5 min → alert |
| **GC Allocation** | 0 bytes steady-state | Any allocation > 0 → alert |
| **Crash Rate** | 0.1% (99.9% crash-free) | > 0.05% → page |
| **ANR Rate** | 0.01% | > 0.005% → alert |

---

## 🧪 Measurement Infrastructure

### Benchmark Harness (Current)
- **Location:** `tools/nexus-benchmark/`
- **Runtime:** .NET 10 (cross-platform, no Unity dependency)
- **Output:** JSON report with per-test metrics + coverage
- **Runs on:** GitHub Actions (ubuntu-latest, free tier compatible)

### Unity Tests (Planned)
- **EditMode:** Architecture validation, DI, binding, registry
- **PlayMode:** Root lifecycle, scene loading, services integration
- **Runner:** Self-hosted (Unity 6000.5.6f1 + Android/iOS build support)

### Device Soak (Future)
- **Project:** `tools/nexus-device-test/` (Unity build)
- **Duration:** 24h continuous gameplay loop
- **Metrics:** Memory, GC, FPS, battery, thermal, network
- **Chaos:** Network toggle, background/foreground, time change, kill -9

---

## 📋 SLO Review Cadence

| Cadence | Activity | Owner |
|---------|----------|-------|
| **Per PR** | Harness benchmarks must not regress | CI Gate |
| **Weekly** | Review error budget burn | Lead Engineer |
| **Monthly** | Update baselines, adjust targets | Architecture Review |
| **Release** | Full SLO report in CHANGELOG | Release Manager |

---

## 🚀 Next Steps (To Reach v1.0 SLOs)

- [ ] **Integrate Sentry/Crashlytics** → Crash-free sessions, ANR tracking
- [ ] **Self-hosted Unity runner** → EditMode/PlayMode CI gate
- [ ] **Device soak harness** → 24h memory/GC/FPS baselines on low-end devices
- [ ] **Grafana/Datadog dashboard** → Live ns/op, GC, memory, error rates
- [ ] **Unity Profiler automation** → Frame time, GC.Alloc, texture/audio memory

---

## 📝 Notes

1. **Current baselines** measured on benchmark harness (.NET 10, desktop). Unity IL2CPP on mobile will differ — device profiling required.
2. **Zero-GC steady-state** is a *hard guarantee* of the architecture (verified by harness). Any regression = blocker.
3. **TTI < 2s** assumes minimal services. Heavy scenes (asset bundles, large addressables) add load time.
4. **Memory targets** are for *casual/hybrid-casual* scope. AAA scope requires separate budgeting.

---

*This document is the single source of truth for Nexus performance and reliability commitments. Update with each release in `CHANGELOG.md`.*