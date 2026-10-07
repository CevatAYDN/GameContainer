# Choose your integration path

Nexus is an architecture foundation for Unity 6 mobile and PC games. Movement,
combat, cameras and level design belong to your game. Installing the package does
not require a .NET SDK. Start with the [installation guide](GETTING_STARTED.md).

| Your situation | Start here | Next step |
|---|---|---|
| New to Unity architecture packages | Package Manager → Samples → Code-first Startup; attach `ScoreBootstrap` | Change the score payload, then adapt the model and command to your feature |
| Prefer scenes and Inspector | Setup Wizard in the Nexus window | Inspect the counter scene and View/Mediator flow |
| Already have a game | `ContextFactory.StartAsync` for one feature | Add inventory, quests or economy incrementally |
| Large team / multiple scenes | Separate application and scene contexts | Use explicit registrations, named bindings, AOT generation and automated checks |

## Your first working feature

Code-first Startup puts a model, signal, command, startup, cancellation and disposal
in one file. Enter Play Mode and expect `Nexus score: 1` in Console. Await startup
before using the context. Dispose it when its owner ends, as the sample demonstrates.

Flow: `ScoreAdded` → `AddScore` → `ScoreModel.Score`. When adding UI, a mediator
observes model changes and updates the view. Models should not contain UI or
GameObject references. Dispose subscriptions when their screen closes.

## Add Nexus to an existing project

1. Preserve your project with version control and install the UPM package.
2. Choose one feature. Code-first startup needs no scene asset or assembly scan.
3. Register its model and commands explicitly in the startup callback.
4. Publish signals from your existing MonoBehaviour through the returned context.
5. Check state, cancellation and disposal before adding the next feature.

Use constructors for command dependencies. Register explicit options with
`builder.BindFluent<MyCommand>().WithParameter(options)` before registering its
signal handler; command registration preserves local transient options.

`builder.BindInstance(instance)` transfers cleanup ownership to the context. Do not
transfer an `IDisposable` owned by another system this way. The low-level DI API
supports `container.BindInstance(instance, disposeWithContainer: false)`; do not
register the same object again with owned lifetime during context configuration.
A context-owned adapter can provide a clear boundary to existing services.

The `IDependencyAdapter` bridge supports coexistence with another DI system. It
does not resolve named bindings; register named dependencies in Nexus.

## Prepare during loading

In the Code-first example, after startup and before the first gameplay signal:

```csharp
_context.Prewarm<ScoreAdded>(4);
_context.SignalBus.Fire(new ScoreAdded(1));
```

Prewarm never executes commands, calls subscribers or consumes one-shots. It runs
constructors/factories, which should not change gameplay state. It ensures a number
of **idle** commands; a larger default initial capacity is retained. Requests above
the maximum pool capacity fail explicitly. Repeated calls do not create redundant
instances. Choose capacity for nesting/concurrency; warming every type unnecessarily
increases memory use and startup work.

For typed composite commands, prepare the pool directly with
`context.PoolManager.Prewarm<MyCompositeCommand>(4)`. Profile async, tracing,
interceptors/decorators and immutable composites separately.

## Catch configuration errors before gameplay

`ContextFactory.StartAsync` validates registrations at startup. For low-level DI,
call `container.ValidateBindings()` after registration and inspect its result.
Validation does not execute constructors or factories.

| Issue | Action |
|---|---|
| Missing dependency / named binding | Register it in the constructing binding's owning scope or its parent |
| Circular dependency | Break the reported chain; separate data and service responsibilities |
| Captive dependency | Cache the dependency or manage its lifetime explicitly through a factory |
| Invalid binding | Supply an assignable implementation, usable constructor and compatible parameter values |

Factories, supplied/already-created instances and external adapters are opaque
boundaries. Test their internals yourself. `LazyInjection<T>` resolves when accessed;
startup eager-cycle analysis does not follow lazy accesses or cycles inside factories.

## Performance decisions

- Resolve model/service references at startup rather than every frame.
- Use struct signals and generic synchronous commands for frequent events.
- Measure debug/tracing enabled and disabled separately.
- On Mono, suitable constructors compile once and avoid argument arrays for transient
  graphs. Compilation itself is startup work. Open generics, optional/value parameters
  and explicit parameter overrides retain their authoritative fallback.
- IL2CPP/AOT never emits dynamic IL. Use the AOT binder/source generator and test a real player.
- Measure startup, memory, and frame p95/p99 on target devices.

Register production provider adapters for ads, IAP and analytics. Mock providers
do not prove a working payment or advertising integration. Network signals and
rollback alone do not establish a multiplayer backend.

See [verification scope](HARDENING.md), [game patterns](GAME_PATTERNS.md),
[architecture](ARCHITECTURE.md), and [troubleshooting](../TROUBLESHOOTING.md).
