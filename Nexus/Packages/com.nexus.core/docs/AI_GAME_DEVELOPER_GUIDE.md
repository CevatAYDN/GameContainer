> **CRITICAL INSTRUCTIONS FOR AI AGENTS BUILDING GAMES ON NEXUS CORE:**
> This document is your primary source of truth when generating game logic, models, signals, commands, services, views, and mediators for any Unity game built on the **Nexus Core** framework.
> 
> **MANDATORY RULES FOR ALL AI CODE GENERATION:**
> 1. **Signals:** EVERY signal MUST be a value type (`public struct MySignal`). NEVER use reference types (`class`) for signals.
> 2. **Dependency Injection:** Inject dependencies into Models, Services, Commands, and Mediators using `[Inject] public IMyService MyService { get; set; }`. Auto-injected properties MUST be `public` auto-properties with `{ get; set; }`.
> 3. **Mediators:** Inherit from `Mediator<TView>`. DO NOT redeclare `[Inject] public ISignalBus SignalBus { get; set; }` in derived mediators; `SignalBus` is already provided by `Mediator<TView>`.
> 4. **Commands:** Implement `ICommand<TSignal>` (sync) or `IAsyncCommand<TSignal>` (async ValueTask with `CancellationToken`).
> 5. **Models:** Implement `IReactiveModel`. Expose reactive state using `ObservableProperty<T>`.

# Nexus Core — AI Game Developer Guide & Rulebook

This guide equips AI agents and human developers with complete, self-contained rules, code templates, and architectural contracts needed to build any Unity 6 game using the **Nexus Core MVCS framework**.

---

## 🏛️ MVCS Architecture Overview

```
                        ┌────────────────────────┐
                        │   SignalBus (Events)   │
                        └───────────┬────────────┘
                                    │
            ┌───────────────────────┼───────────────────────┐
            │                       │                       │
            ▼                       ▼                       ▼
   ┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐
   │    Commands     │     │ Reactive Models │     │    Services     │
   │ (Game Operations)     │ (State & Data)  │     │(Audio, Ads, Save)
   └────────┬────────┘     └────────┬────────┘     └────────┬────────┘
            │                       │                       │
            └───────────────────────┼───────────────────────┘
                                    │
                                    ▼
                         ┌────────────────────┐
                         │      Mediators     │
                         └──────────┬─────────┘
                                    │
                                    ▼
                         ┌────────────────────┐
                         │  UI & Game Views   │
                         └────────────────────┘
```

---

## 📋 5 Golden Rules of Nexus Game Development

### Rule 1: Struct Signals
All signals represent game events and MUST be defined as `public struct`:
```csharp
// GOOD: struct signal
public struct ScoreUpdatedSignal
{
    public int NewScore;
    public int ComboMultiplier;
}

// BAD: Reference type causes GC allocation on every fire
public class ScoreUpdatedSignal { ... }
```

### Rule 2: Public Property Injection
Dependencies are injected by the `NexusDI` container. The **AOT-optimal canonical** style is
`public` auto-properties marked with `[Inject]` (the generated binder emits a direct assignment):
```csharp
// GOOD (canonical): Public auto-property — direct AOT assignment
[Inject] public IScoreModel ScoreModel { get; set; }

// WORKS BUT NOT CANONICAL: Private field injection — generated binder falls back to a cached
// FieldInfo.SetValue (works incl. IL2CPP, but reflection-based). Constructor injection is
// equally valid for immutability: public Ctor(IScoreModel model) { ... } ([Inject]/[Construct]).
[Inject] private IScoreModel _scoreModel;
```

### Rule 3: Clean Mediator Sub-classing
Derive your mediator from `Mediator<TView>`:
```csharp
// GOOD: Inherits View and SignalBus automatically
public class ScoreHUDMediator : Mediator<ScoreHUDView>
{
    [Inject] public IScoreModel ScoreModel { get; set; }

    protected override void OnBind()
    {
        ScoreModel.Score.OnChanged(HandleScoreChanged);
    }

    protected override void OnUnbind()
    {
        ScoreModel.Score.RemoveOnChanged(HandleScoreChanged);
    }

    private void HandleScoreChanged(int oldScore, int newScore)
    {
        View.SetScoreText(newScore);
    }
}

// BAD: Redeclaring SignalBus triggers CS0108 warning
public class ScoreHUDMediator : Mediator<ScoreHUDView>
{
    [Inject] public ISignalBus SignalBus { get; set; } // DO NOT DO THIS!
}
```

### Rule 4: Standard Command Handlers
Commands execute business logic in response to signals:
```csharp
// Synchronous Command
public class AddScoreCommand : ICommand<ScoreUpdatedSignal>
{
    [Inject] public IScoreModel ScoreModel { get; set; }

    public void Execute(ScoreUpdatedSignal signal)
    {
        ScoreModel.Score.Value += signal.NewScore * signal.ComboMultiplier;
    }
}

// Asynchronous Command
public class SaveGameCommand : IAsyncCommand<SaveRequestedSignal>
{
    [Inject] public ISaveService SaveService { get; set; }

    public async ValueTask ExecuteAsync(SaveRequestedSignal signal, CancellationToken ct)
    {
        await SaveService.SavePlayerDataAsync(ct);
    }
}
```

### Rule 5: Reactive Models with `ObservableProperty<T>`
Models hold game state and notify mediators without manual event delegates:
```csharp
public interface IScoreModel : IReactiveModel
{
    ObservableProperty<int> Score { get; }
    ObservableProperty<int> HighScore { get; }
}

public class ScoreModel : IScoreModel
{
    public ObservableProperty<int> Score { get; } = new(0);
    public ObservableProperty<int> HighScore { get; } = new(0);

    // IReactiveModel: called once after injection (BindReactiveModel registers it).
    public ValueTask OnBind(CancellationToken ct) => default;
}
```

**Exception: state that changes every frame.** Positions, stacks and other simulation state that changes every frame stay in plain fields, and the view reads them once per frame (from an `ITickable` or on a per-frame signal). An `ObservableProperty` there would raise an event per field per frame. Use observables for state that changes on events (score, currency, settings).

---

## 🚀 Complete Game Creation Recipe

When creating a new feature (e.g. Health & Damage System):

### Step 1: Define Signals
```csharp
namespace MyGame.Signals
{
    public struct TakeDamageSignal
    {
        public int DamageAmount;
    }

    public struct PlayerDiedSignal { }
}
```

### Step 2: Define Reactive Model Interface & Class
```csharp
namespace MyGame.Models
{
    public interface IHealthModel : IReactiveModel
    {
        ObservableProperty<int> CurrentHealth { get; }
        ObservableProperty<int> MaxHealth { get; }
    }

    public class HealthModel : IHealthModel
    {
        public ObservableProperty<int> CurrentHealth { get; } = new(100);
        public ObservableProperty<int> MaxHealth { get; } = new(100);

        public ValueTask OnBind(CancellationToken ct) => default;
    }
}
```

### Step 3: Implement Command
```csharp
namespace MyGame.Commands
{
    public class TakeDamageCommand : ICommand<TakeDamageSignal>
    {
        [Inject] public IHealthModel HealthModel { get; set; }
        [Inject] public ISignalBus SignalBus { get; set; }

        public void Execute(TakeDamageSignal signal)
        {
            HealthModel.CurrentHealth.Value = Math.Max(0, HealthModel.CurrentHealth.Value - signal.DamageAmount);
            if (HealthModel.CurrentHealth.Value == 0)
            {
                SignalBus.Fire(new PlayerDiedSignal());
            }
        }
    }
}
```

### Step 4: Implement View & Mediator
```csharp
namespace MyGame.Views
{
    [Mediator(typeof(HealthBarMediator))]
    public class HealthBarView : View
    {
        [SerializeField] private Image _healthFill;

        public void SetHealthRatio(float ratio)
        {
            if (_healthFill != null) _healthFill.fillAmount = ratio;
        }
    }

    public class HealthBarMediator : Mediator<HealthBarView>
    {
        [Inject] public IHealthModel HealthModel { get; set; }

        protected override void OnBind()
        {
            HealthModel.CurrentHealth.OnChanged(OnHealthChanged);
        }

        protected override void OnUnbind()
        {
            HealthModel.CurrentHealth.RemoveOnChanged(OnHealthChanged);
        }

        private void OnHealthChanged(int previousHealth, int currentHealth)
        {
            float ratio = (float)currentHealth / HealthModel.MaxHealth.Value;
            View.SetHealthRatio(ratio);
        }
    }
}
```

### Step 5: Wire in Context Builder
```csharp
namespace MyGame.Contexts
{
    public class GameplayContextLifecycle : IContextLifecycle
    {
        public void OnConfigure(IContextBuilder builder)
        {
            builder.BindReactiveModel<IHealthModel, HealthModel>();
            builder.BindCommand<TakeDamageSignal, TakeDamageCommand>();
            // No mediator registration: HealthBarView's [Mediator] attribute attaches
            // HealthBarMediator when the view registers with the context.
        }

        public ValueTask OnInitializeAsync(CancellationToken ct) => default;
        public ValueTask OnStartAsync(CancellationToken ct) => default;
        public void OnDispose() { }
    }
}
```

---

## 🚫 AI Anti-Pattern Reference Table

| Anti-Pattern | Why it fails | Correct Pattern |
|:---|:---|:---|
| `public class MySignal` | Allocates memory on GC heap every fire. | `public struct MySignal` |
| `[Inject] private IModel _model;` | Works (cached `FieldInfo.SetValue` fallback incl. IL2CPP) but not AOT-direct; encapsulation costs AOT-optimality. | `[Inject] public IModel Model { get; set; }` — direct AOT assignment |
| Redeclaring `SignalBus` in `Mediator` | Causes `CS0108` compiler warning and shadows base member. | Omit `SignalBus` declaration; use inherited property. |
| Direct `GetComponent` in Mediators | Bypasses MVCS decoupling and fails during unit tests. | Inject models or views through Mediator binding. |
| Hardcoded strings for signals | Brittle, breaks refactoring, no compile-time checks. | Use typed struct signals (`SignalBus.Fire(new MySignal())`). |

---

## 🔗 Related Documentation
- 📖 [README.md](../README.md) — Main framework index and decision flows
- 🏛️ [ARCHITECTURE.md](ARCHITECTURE.md) — Core runtime architecture & sequence diagrams
- 🎮 [GAME_PATTERNS.md](GAME_PATTERNS.md) — Architecture patterns for 6 game genres

---

**Code version:** 0.5.0  
**Target AI Models:** Claude 3.5 Sonnet, GPT-4o, Gemini 1.5/3.6 Flash  
