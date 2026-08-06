# 🚀 Nexus 10-Minute Quickstart

> **From zero to running code in 10 minutes.**  
> This guide walks you through installing Nexus, setting up your first context, wiring a signal + command, binding a reactive model, and connecting a UI — all in a single Unity scene.

---

## ✅ Before You Begin

- Unity **6000.0+** (Unity 6)
- A new or existing Unity project (any template works)
- Basic familiarity with C# and Unity Editor

---

## 📦 Step 1: Install Nexus (1 min)

1. Open **Window > Package Manager**
2. Click **+** → **Add package from disk...**
3. Browse to `Nexus/Packages/com.nexus.core/package.json`
4. Click **Open**

> **That's it.** No additional setup scripts, no DLLs to copy, no Bootstrapper prefab.

---

## 🧱 Step 2: Create Your First Context (2 min)

A **Context** is Nexus's central container — it holds models, services, commands, and the signal bus.

### 2a. Create a ContextData asset

1. In the Project window, right-click **Create > Nexus > ContextData**
2. Name it `GameContextData`
3. Select it and check **Enable Auto-Discovery**
   - Enables the `{ScopeTag}Lifecycle` name-convention scan (see Step 3c). The `Root` component additionally discovers every `IContextLifecycle` component attached to its GameObject.

### 2b. Add the Root component

1. Create an empty **GameObject** in your scene → name it `GameRoot`
2. Add the `Root` component (**Add Component → Nexus → Root**)
3. Drag `GameContextData` into the **Context Data** field on the Root component

> ✅ **Done.** At runtime, the `Root` component will:
> 1. Create a `Context` with your configuration
> 2. Scan for `IContextLifecycle` classes
> 3. Call `OnConfigure` → `OnInitializeAsync` → `OnStartAsync`

---

## ⚡ Step 3: Add a Signal + Command (2 min)

Signals are immutable structs. Commands handle them.

### 3a. Create the Signal

```csharp
// Signals/ScoreSignal.cs
public readonly struct ScoreSignal
{
    public readonly int Points;
    public ScoreSignal(int points) => Points = points;
}
```

### 3b. Create the Command

```csharp
// Commands/AddScoreCommand.cs
using Nexus.Core;

public class AddScoreCommand : ICommand<ScoreSignal>
{
    [Inject] private ScoreModel _score; // We'll create this next

    public void Execute(ScoreSignal signal)
    {
        _score.Total.Value += signal.Points;
    }
}
```

### 3c. Create the Lifecycle (wiring)

```csharp
// Lifecycle/GameLifecycle.cs
using Nexus.Core;
using UnityEngine;
using System.Threading;
using System.Threading.Tasks;

public class GameLifecycle : MonoBehaviour, IContextLifecycle
{
    public void OnConfigure(IContextBuilder builder)
    {
        builder.BindReactiveModel<ScoreModel>();
        builder.BindSignal<ScoreSignal>().To<AddScoreCommand>();
    }

    public ValueTask OnInitializeAsync(CancellationToken ct) => default;
    public ValueTask OnStartAsync(CancellationToken ct) => default;
    public void OnDispose() { }
}
```

> **Discovery:** attach `GameLifecycle` to the `GameRoot` GameObject (**Add Component**) —
> `Root` finds every `IContextLifecycle` component on it automatically. (A plain class
> also works via the `{ScopeTag}Lifecycle` name convention when the ContextData carries a
> matching `ScopeTag`.)

---

## 📊 Step 4: Add a Reactive Model (1 min)

Models hold observable state. Changes automatically notify subscribers.

```csharp
// Models/ScoreModel.cs
using Nexus.Core;
using System.Threading;
using System.Threading.Tasks;

public class ScoreModel : IReactiveModel
{
    public ObservableProperty<int> Total { get; } = new(0);
    public ObservableProperty<string> Rank { get; } = new("Bronze");

    public ValueTask OnBind(CancellationToken ct) => default;
}
```

> `ObservableProperty<T>` is allocation-free — no GC pressure from property changes.

---

## 🖥️ Step 5: Connect a UI (2 min)

### 5a. Create a Mediator + View

```csharp
// UI/ScoreView.cs
using Nexus.Core;
using UnityEngine;
using UnityEngine.UI;

public class ScoreView : View
{
    public Text scoreText;
    public Button addButton;

    protected override void OnBind()
    {
        var model = Context.Resolve<ScoreModel>();
        model.Total.OnChanged((oldVal, newVal) =>
        {
            scoreText.text = $"Score: {newVal}";
        });

        addButton.onClick.AddListener(() =>
        {
            Context.SignalBus.Fire(new ScoreSignal(10));
        });
    }
}
```

### 5b. Wire it in the scene

1. Add a **Canvas** with a **Text** and a **Button**
2. Add the `ScoreView` component to a GameObject
3. Assign the Text and Button references in the Inspector

---

## ▶️ Step 6: Run It! (1 min)

1. Press **Play** ☝️
2. Click the button — the score text updates
3. Open **Window > Nexus > Dashboard** to inspect:
   - Live signals and commands
   - Reactive model state
   - Causal trace log

> **Congratulations!** You've just built a fully reactive Nexus application.

---

## 📚 What's Next?

| Topic | Guide |
|-------|-------|
| 🏗️ Architecture deep-dive | [ARCHITECTURE.md](ARCHITECTURE.md) |
| 🎮 Game patterns (Idle, RPG, RTS, etc.) | [GAME_PATTERNS.md](GAME_PATTERNS.md) |
| 🔌 All 4 command execution modes | [Counter Sample](../Samples~/Counter/README.md) |
| 🛠️ Editor plugins & tools | [PLUGIN_DEVELOPMENT.md](PLUGIN_DEVELOPMENT.md) |
| 🔍 Localization system | [LOCALIZATION_KEYS.md](LOCALIZATION_KEYS.md) |
| 🤝 Contributing | [CONTRIBUTING.md](CONTRIBUTING.md) |

---

## 🆘 Quick Troubleshooting

| Symptom | Fix |
|---------|-----|
| **"No lifecycle found"** | Make sure your lifecycle class implements `IContextLifecycle` and is **attached to the Root GameObject as a component** (or matches the `{ScopeTag}Lifecycle` convention with a non-empty ContextData `ScopeTag`) |
| **"Type not registered"** | Add `builder.Bind<MyType>()` in `OnConfigure`, or check that the type is in a non-system assembly |
| **Signal not dispatched** | Verify the command implements `ICommand<YourSignal>` and is registered via `builder.BindSignal<>()` |
| **Test won't run** | Open EditMode Test Runner, select `InfrastructureValidationTests` and `PluginRefactorValidationTests` |

---

> **Last updated:** 2026-07-30  
> **Nexus version:** 0.4.0
