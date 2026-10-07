# Add Nexus to an existing Unity 6 project

Nexus is a UPM package; the repository's demo project is not required. Choose one path.

## Install

In **Window → Package Manager → + → Install package from git URL**, use:

```text
https://github.com/CevatAYDN/GameContainer.git?path=/Nexus/Packages/com.nexus.core
```

For a local checkout, use **Install package from disk** and select
`Nexus/Packages/com.nexus.core/package.json`. Use a tested release tag or commit (`#<revision>`)
when pinning a production git dependency. Unity resolves the package's uGUI/profiling dependencies.
This package targets Unity 6; older Unity versions are not promised by this guide.

## Beginner: run the example

Select Nexus in Package Manager, expand **Samples**, and import **Counter Example**.
Follow the sample README. The sample includes the complete MVCS tour; no files from the
repository's `Assets` directory are prerequisites. For a generated scene, open Nexus Dashboard's
Setup Wizard. It preserves existing starter files, settings and scenes and creates a new scene additively.

## Code-first: one startup call

Import **Code-first Startup** from Package Manager, attach `ScoreBootstrap` to an empty
GameObject and enter Play Mode. This is the complete script; keep its file name
`ScoreBootstrap.cs` so Unity can attach the component.

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using UnityEngine;

namespace Nexus.Samples.CodeFirst
{
    public readonly struct ScoreAdded
    {
        public readonly int Amount;
        public ScoreAdded(int amount) => Amount = amount;
    }

    public sealed class ScoreModel : IReactiveModel
    {
        public ObservableProperty<int> Score { get; } = new(0);
        public ValueTask OnBind(CancellationToken ct) => default;
    }

    public sealed class AddScore : ICommand<ScoreAdded>
    {
        private readonly ScoreModel _model;
        public AddScore(ScoreModel model) => _model = model;
        public void Execute(ScoreAdded signal) => _model.Score.Value += signal.Amount;
    }

    /// <summary>Attach to an empty GameObject. No Root, scene asset or assembly scanning needed.</summary>
    public sealed class ScoreBootstrap : MonoBehaviour
    {
        private readonly CancellationTokenSource _startup = new();
        private Context _context;

        private async void Start()
        {
            try
            {
                _context = await ContextFactory.StartAsync("Gameplay", builder =>
                {
                    builder.BindReactiveModel<ScoreModel>();
                    builder.BindSignal<ScoreAdded>().To<AddScore>();
                }, ct: _startup.Token);
                _context.Prewarm<ScoreAdded>(4);
                _context.SignalBus.Fire(new ScoreAdded(1));
                Debug.Log($"Nexus score: {_context.Resolve<ScoreModel>().Score.Value}");
            }
            catch (OperationCanceledException) { } // Owner destroyed during startup.
            catch (Exception error) { Debug.LogException(error); }
        }

        private void OnDestroy()
        {
            try { _startup.Cancel(); }
            finally
            {
                try { _context?.Dispose(); }
                finally { _startup.Dispose(); }
            }
        }
    }
}

```

`StartAsync` configures before validation and completes model, service and lifecycle
initialization before returning. Startup failure cleans up; cancellation is accepted
through `ct:`. Unity work must originate on the Unity main thread. Use an owning
field and dispose the context when that owner ends.

## Scene-first and advanced projects

Use a Root + ContextData asset and an IContextLifecycle for scene ownership, parent/child contexts,
auto-discovery, mediators and editor configuration. Follow the [English quickstart](10_MIN_QUICKSTART.md)
or [Türkçe hızlı başlangıç](10_MIN_QUICKSTART_TR.md). Keep configuration in the lifecycle rather
than binding from unrelated MonoBehaviour updates. Bind real analytics/ad/IAP/backend adapters
when the game needs them; stub services are integration seams.

## Performance-sensitive composites

```csharp
public readonly struct MapReady { }
public readonly struct PlayerReady { }
public sealed class BeginPlay : ICompositeCommand<MapReady, PlayerReady>
{
    public void Execute(MapReady map, PlayerReady player) { /* start gameplay */ }
}

var registration = context.SignalBus.BindComposite<MapReady, PlayerReady, BeginPlay>();
context.SignalBus.Fire(new MapReady());
context.SignalBus.Fire(new PlayerReady());
// Dispose registration to stop it, or let the owning context dispose its subscriptions.
```

Both distinct signal types must arrive. Repeated values replace that type's pending value;
completed pairs reset, and `oneShot: true` stops after the first successful completion. A failed pair releases the one-shot claim for a later pair. This synchronous path
uses typed values and pooled commands; registration order determines delivery order. Use legacy
CompositeContext for arbitrary-arity or async composites when immutable retained snapshots are
needed. Debug tracing/decorators have their own cost. Validate on the target device before setting
a frame budget; host 0-GC results are not IL2CPP measurements.
