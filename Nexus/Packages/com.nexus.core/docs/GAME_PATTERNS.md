> **Who this is for:** AI agents and architects choosing a context topology per genre.

> **For AI Agents:** This document is the source of truth for selecting and configuring Nexus Core architecture patterns across different game genres (Casual, Mid-core, Hardcore RPG, Simulation, Strategy RTS, Idle Arcade).
> Refer to [README.md](../README.md#glossary) and [ARCHITECTURE.md](ARCHITECTURE.md) for core concepts.

# Nexus Game Architecture Patterns Guide

This guide describes recommended Nexus Core architectural setups, context topologies, signal budgets, and performance parameters for 6 distinct game genres.
A performance budget below is a target the game sets for its steady-state gameplay frames, not a framework
guarantee: what the framework's own paths allocate is in the package README's [Performance scope](../README.md#performance-scope), and a
frame that saves (its `EncryptedStorageService` row) is outside such a budget.

---

## 🎮 1. Casual Games (e.g. Puzzle / Match-3)

- **Topology:** Single Root Context (`GameplayContext`).
- **Characteristics:** UI-heavy, single scene, low signal frequency (< 10 sigs/sec).
- **Setup Pattern:**
  - `GameplayModel`: Reactive score, move count, and board state.
  - `Sequential` signal dispatch for user input & move resolution.
- **Performance Budget (game target):** 60 FPS, < 1KB GC/frame, < 50 draw calls.

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
  - Struct signals (`public struct DamageSignal`), dispatched at 0 B after warmup ([Performance scope](../README.md#performance-scope)).
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

- **Topology:** Spatial Sub-Contexts with the DOTS bridge (`NativeSignalQueue<T>`, `DOTSSignalBridge<T>`).
- **Characteristics:** High-performance update ticks, thousands of entities, 60+ FPS target.
- **Setup Pattern:**
  - Struct signals for high-level squad commands.
  - Jobs (unit pathfinding, combat calculation) write unmanaged signals through `NativeSignalQueue<T>`'s
    parallel writer; `DOTSSignalBridge<T>` drains them into the `SignalBus` on the main thread. Both live in
    the `com.nexus.dots` package (namespace `Nexus.DOTS`), which compiles only when
    `com.unity.collections` is installed.

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
  - `UIManager` (HUD → Shop → Prestige modal) layered UI navigation.
  - `Sequential` mode for discrete upgrades; `Composite` triggers for multi-resource milestones.
- **Performance Budget (game target):** 60 FPS, < 500B GC/frame, < 30 draw calls (UI-heavy); a save frame is outside it ([Performance scope](../README.md#performance-scope), storage row).
- **Offline Earnings Example:**
  ```csharp
  using Nexus.Core;
  using Nexus.Core.Services;

  public struct GameResumedSignal { } // your game's signal, fired when play resumes

  public class OfflineEarningsCommand : ICommand<GameResumedSignal>
  {
      private const long CoinsPerSecond = 5;

      [Inject] public IEconomyService Economy { get; set; }
      [Inject] public IPlayerPrefsService Save { get; set; } // EncryptedStorageService implements it

      public void Execute(GameResumedSignal signal)
      {
          // Seconds since OfflineTimeCalculator.RecordQuitTimestamp(Save) ran on pause/quit:
          // 0 when the clock was set back, capped at 8 hours by default.
          long offlineSeconds = OfflineTimeCalculator.CalculateOfflineSeconds(Save);
          Economy.Earn("Coins", CoinsPerSecond * offlineSeconds, "offline");
      }
  }
  ```

---

## 🔗 Related Documentation
- 📖 [README.md](../README.md) — Main framework index and decision flows
- 🏛️ [ARCHITECTURE.md](ARCHITECTURE.md) — Runtime architecture & sequence diagrams
