# 🚀 Nexus Starter Template

The minimal scaffold template to get a fresh project running quickly.

For the recommended onboarding sample, import the **Counter Example** sample.

## What's included

| File | Purpose |
|------|---------|
| `NexusStartLifecycle.cs` | Context lifecycle — binds model + signal + command |
| `NexusStartSignal.cs` | A signal struct (`NexusStartSignal`) |
| `NexusStartModel.cs` | Reactive model with `ObservableProperty<int>` |
| `NexusStartCommand.cs` | Command that increments the model |
| `NexusStartView.cs` | Unity UI View (Button + Text) |
| `NexusStartMediator.cs` | Mediator wiring view ↔ model ↔ signal bus |

## Setup

1. Import this sample via **Window > Package Manager → Nexus Observable Architecture → Samples**
2. Create a **Canvas** with a **Button** and a **Text** element
3. Add `NexusStartView` to your Canvas (assign Button & Text in Inspector)
4. Create an empty GameObject → add `Root` component
5. Create a **ContextData** asset (Create → Nexus → Context Data), set **Scope Tag** to `NexusStart` and keep **Enable Auto Discovery** on: the context finds `NexusStartLifecycle` by its `{ScopeTag}Lifecycle` name
6. Assign ContextData to Root → Press **Play**

## What you'll see

Click the button → counter increments. The View updates via the reactive model.

Check **Window > Nexus > Dashboard** to inspect the live signal flow.
