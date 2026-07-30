> **For AI Agents:** This document is the source of truth for selecting and configuring Nexus Core architecture patterns across different game genres (Casual, Mid-core, Hardcore RPG, Simulation, Strategy RTS).
> Refer to [README.md](../README.md#glossary) and [ARCHITECTURE.md](ARCHITECTURE.md) for core concepts.

# Nexus Game Architecture Patterns Guide

This guide describes recommended Nexus Core architectural setups, context topologies, signal budgets, and performance parameters for 5 distinct game genres.

---

## 🎮 1. Casual Games (e.g. Puzzle / Match-3)

- **Topology:** Single Root Context (`GameplayContext`).
- **Characteristics:** UI-heavy, single scene, low signal frequency (< 10 sigs/sec).
- **Setup Pattern:**
  - `GameplayModel`: Reactive score, move count, and board state.
  - `Sequential` signal dispatch for user input & move resolution.
- **Performance Budget:** 60 FPS, < 1KB GC/frame, < 50 draw calls.

---

## ⚔️ 2. Mid-Core Games (e.g. Card Battler / PvP Arena)

- **Topology:** Multi-Context Hierarchy:
  - `GlobalContext` (Audio, Ads, Analytics, Monetization services)
  - `MetaContext` (Inventory, Shop, PlayerProfile models)
  - `MatchContext` (Turn state, DeckModel, HandView)
- **Characteristics:** Mid signal frequency (10–50 sigs/sec), cross-context dispatch via `IContextResolver`.
- **Setup Pattern:**
  - `GlobalContext` registered as parent of `MatchContext`.
  - Commands use `ExecutionMode.Sequential` for turn execution and `ExecutionMode.Concurrent` for async network syncing.

---

## 🐉 3. Hardcore RPGs (e.g. Action RPG / Dungeon Crawler)

- **Topology:** Scoped Sub-Contexts (WorldContext, CombatContext, DialogueContext).
- **Characteristics:** High signal frequency (100–500 sigs/sec), 20+ reactive models, 100+ signals.
- **Setup Pattern:**
  - Struct signals (`public struct DamageSignal`) for 0-GC steady state.
  - Pooling enabled via `CommandPoolManager` for high-frequency combat commands.
  - `CausalTracing` enabled in debug builds to trace complex status-effect cascades.

---

## 🏗️ 4. Simulation Games (e.g. City Builder / Resource Sim)

- **Topology:** Dual Context:
  - `SimulationContext` (Pure C# programmatic context created via `NexusRuntime.CreatePureContextAsync`)
  - `UIContext` (Scene-anchored View mediators)
- **Characteristics:** Tick-based deterministic update, persistent state save/load.
- **Setup Pattern:**
  - `SimulationContext` operates independently of MonoBehaviour lifecycle for headless testing and fast-forward simulation.

---

## ⚔️ 5. Strategy / RTS Games

- **Topology:** Spatial Sub-Contexts with DOTS Bridge (`NexusDOTSBridge`).
- **Characteristics:** High-performance update ticks, thousands of entities, 60+ FPS target.
- **Setup Pattern:**
  - Struct signals for high-level squad commands.
  - Integration with `com.nexus.core.dots` for jobified unit pathfinding and combat calculation.

---

## ⏳ 6. Idle Arcade Games (e.g. Clicker / Idle Tycoon)

- **Topology:** Dual Context (Simulation + UI):
  - `IdleContext` (Pure C# context via `CreatePureContextAsync`) — tick-based simulation, headless
  - `UIContext` (Scene-anchored Root) — HUD, shop, prestige UI mediators
- **Characteristics:** Low signal frequency (< 5 sigs/sec user input), **persistent tick simulation**, offline earnings calculation, heavy UI with upgrade trees.
- **Setup Pattern:**
  - `EconomyService` (Coins, Gems, Energy) for multi-currency passive income.
  - `ProgressionService` for linear/exponential upgrade cost curves.
  - `TickService` + `ITickable` for passive income per-second simulation.
  - `EncryptedStorageService` for offline earnings persistence.
  - `WindowManager` (HUD → Shop → Prestige modal) layered UI navigation.
  - `Sequential` mode for discrete upgrades; `Composite` triggers for multi-resource milestones.
- **Performance Budget:** 60 FPS, < 500B GC/frame, < 30 draw calls (UI-heavy).
- **Offline Earnings Example:**
  ```csharp
  public class OfflineEarningsCommand : ICommand<TickSignal>
  {
      [Inject] public IEconomyService Economy { get; set; }
      [Inject] public IEncryptedStorageService Save { get; set; }

      public void Execute(TickSignal signal)
      {
          // Passive income per second
          Economy.AddCurrency("Coins", CalculateIncomePerSec() * signal.DeltaTime);
      }

      public void CalculateOfflineReward()
      {
          double elapsed = (DateTime.UtcNow - _lastSaveTime).TotalSeconds;
          Economy.AddCurrency("Coins", (long)(CalculateIncomePerSec() * elapsed));
      }
  }
  ```

---

## 🔗 Related Documentation
- 📖 [README.md](../README.md) — Main framework index and decision flows
- 🏛️ [ARCHITECTURE.md](ARCHITECTURE.md) — Runtime architecture & sequence diagrams
