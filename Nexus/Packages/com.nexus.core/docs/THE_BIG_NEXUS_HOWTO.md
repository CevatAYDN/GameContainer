> **Who this is for:** level 1 developer. Tells the same story as `walkthrough.md`, as cause and effect.

# The Big Nexus How-To

> Why first, how second. This guide tells Nexus not as a *list of rules* but as a
> *story*: **why** each pattern exists, which problem it
> solves, and when to use it and when not to. For the rules
> (the canonical list) see [CANONICAL-PATTERNS.md](./CANONICAL-PATTERNS.md).
> Here the goal is understanding.

---

## Part 1 — Why is there a "framework"?

When you write a small game, everything fits inside one MonoBehaviour: `Update()`
reads the input, increases the money, updates a text, plays a sound. At 500 lines you still
manage. But when the third screen, the second game mode and the first network sync
arrive, the same file becomes 3000 lines. Now:

- To change one line you have to scan five places (where was this
  touched?).
- Nobody can answer "what gets cleaned up when this screen closes?" at a
  glance.
- To write a test you have to open the scene, because the logic is buried in Unity.

Nexus's answer is a single idea: **split the responsibilities apart and put
disciplined, searchable connections between them.** Each part does one job;
the parts do not know each other directly; every connection between them is visible
in the codebase (greppable).

The first framework to do this was StrangeIoC (and its ancestor Robotlegs). Nexus speaks the same
language — Context, Signal, Command, Mediator — but changes three things at the root:

1. **Performance:** signals are `struct`s, commands and mediators are pooled,
   a cached resolve measured 0 B (the package README's [Performance scope](../README.md#performance-scope) names each check).
2. **Discipline:** there are two registration paths (explicit `BindCommand` and the prototype `[SignalHandler]`)
   but only one is canonical, and this guide tells you which to use when,
   with the reasons.
3. **Capability:** the general Binder, named injection and
   `[PostConstruct]` known from Strange are added to Nexus *natively* — below in Part 7.

---

## Part 2 — First meeting: Signal and Command

The most basic split is between **carrying an event** and **doing work**.

A button was pressed. The way to announce it is a **Signal**: a small struct that says "this happened"
and carries data.

```csharp
public readonly struct CoinsAddedSignal
{
    public readonly int Amount;
    public CoinsAddedSignal(int amount) => Amount = amount;
}
```

A Signal *does* nothing. It is only a message. The thing that *receives the message and
does the work* is the **Command**:

```csharp
public class AddCoinsCommand : ICommand<CoinsAddedSignal>
{
    [Inject] public PlayerWallet Wallet { get; set; }

    public void Execute(CoinsAddedSignal signal)
    {
        Wallet.Add(signal.Amount);
    }
}
```

Why this split? Because the two sides do not know each other. The button knows nothing about
`Wallet`; the wallet knows nothing about the button. The only link between them is a
message both know. Changing one does not break the other.

You make the link — explicitly, inside `OnConfigure`:

```csharp
public void OnConfigure(IContextBuilder builder)
{
    builder.BindCommand<CoinsAddedSignal, AddCoinsCommand>();
}
```

Firing the Signal is this:

```csharp
_signalBus.Fire(new CoinsAddedSignal(10));
```

That is the whole loop. It looks simple because it is — the power is in
combining these small parts.

---

## Part 3 — Why a Command, why not a direct method call?

An honest question: why is `button.OnClick += () => wallet.Add(10)` not enough?

There are three reasons:

1. **Visibility.** The `BindCommand` line says "this signal triggers this command" and
   can be searched in the codebase. `OnClick +=` lambdas scatter across the project; they are
   not easy for anyone to find.
2. **Several listeners.** To bind another command to the same signal you do not
   change the calling side; you just add one line. The caller does *not have to*
   know who is listening.
3. **Timing control.** With `ExecutionMode` you decide whether commands run one after another, in parallel,
   or exclusively (Exclusive). A direct call has no
   such policy layer.

> **When not to use it:** communication between two tightly coupled MonoBehaviours that
> have a single consumer and are not game-specific. There a plain
> `GetComponent<T>()` + method call needs less ceremony. Signals are for
> *cross* communication; not for the neighbour.

---

## Part 4 — Mediator: keeping the View clean

Now the visual layer. Think of a UI panel: it shows the coin counter.

The **View** deals only with visuals: it knows how to draw the number, not what it
means.

```csharp
[Mediator(typeof(WalletHudMediator))]
public class WalletHudView : View
{
    public Text AmountLabel;

    public void SetAmount(int amount) => AmountLabel.text = amount.ToString("N0");
}
```

The **Mediator** is the translator between the View and the game: it listens to signals and updates the View,
and turns user actions coming from the View into signals.

```csharp
public class WalletHudMediator : Mediator<WalletHudView>
{
    protected override void OnBind()
    {
        // The mediator's Subscribe tracks the subscription; it is disposed automatically on Unbind.
        Subscribe<CoinsAddedSignal>(OnCoinsAdded);
    }

    private void OnCoinsAdded(CoinsAddedSignal s) => View.SetAmount(s.Amount);
}
```

Why two classes? Because **the View is reusable, the Mediator is not** (it is
game-specific) and **the Mediator is testable, the View is not** (it is a Unity object).
You do not break the mediator to change the visuals; you do not break the
View to change the logic.

Mediators are pooled in Nexus (up to 64 per type) and the `OnUnbind`/`OnReset`
contract guarantees pool hygiene. This is the performance-aware equivalent of Strange's
`OnRemove()`.

---

## Part 5 — Context: the owner of it all

Signal, Command, Mediator — they all live inside a **Context**. A Context is
an independent region of the application (or of a scene): its own DI container,
its own SignalBus, its own lifecycle.

The setup contract sits on a MonoBehaviour: `IContextLifecycle` components added to the Root GameObject
are discovered automatically. (If a plain class is used, the naming
convention applies: `{ScopeTag}Lifecycle` + a filled-in `ScopeTag` in the ContextData.)

```csharp
public class GameLifecycle : MonoBehaviour, IContextLifecycle
{
    public void OnConfigure(IContextBuilder builder)
    {
        // Dependencies: services, models, commands, binders.
        builder.BindService<IPlayerWallet, PlayerWalletService>();
        builder.BindCommand<CoinsAddedSignal, AddCoinsCommand>();
        // The mediator is not registered: the [Mediator] attribute on the view is enough; the ViewBinder pools it.
    }

    public async ValueTask OnInitializeAsync(CancellationToken ct) { /* async preparation */ }
    public async ValueTask OnStartAsync(CancellationToken ct) { /* start the game */ }
    public void OnDispose() { /* release resources */ }
}
```

The lifecycle has four stages and runs **asynchronously**:

```
OnConfigure ─▶ OnInitializeAsync ─▶ OnStartAsync ─▶ (game) ─▶ OnDispose
```

Why asynchronous? Because loading screens, network connections and asset loads
need `await`. A synchronous setup blocks this work; Nexus gives you control over
holding the start.

---

## Part 6 — Injection: who provides the dependencies?

Remember the Command's `[Inject] public PlayerWallet Wallet` line?
Who created `PlayerWallet`, who provided it? **The container.**

In `OnConfigure` you register something with `Bind...`:

```csharp
builder.BindInstance<PlayerWallet>(new PlayerWallet());
builder.BindService<IPlayerWallet, PlayerWalletService>();
```

And you receive it with `[Inject]` wherever it is needed. The container resolves the type;
if it is a singleton it gives the same instance every time, if it is transient it creates
a new one every time.

The point of injection is not mystery but **handing over responsibility**: instead of a class
*finding* its dependencies itself (new, GetComponent, static singleton),
it *requests* them. That makes the class testable, because you can replace its dependencies
in a test.

---

## Part 7 — Capabilities from Strange (added in 0.5.0)

Nexus now offers three capabilities that Strange users know, natively.

### 7.1 General Binder — `Bind(anything).To(anything)`

The MVCS container works *for types*. But what if you want to bind a **table**?
A unit catalog, a config registry, a theme table, a multi-loader —
in other words a "this key → this value" mapping?

That is `NexusBinder<TKey, TValue>`. You register it in the context:

```csharp
// OnConfigure
builder.BindBinder<UnitType, UnitDefinition>();
```

You inject it anywhere:

```csharp
[Inject] public IBinder<UnitType, UnitDefinition> Units { get; set; }
```

You fill it and read it:

```csharp
Units.Bind(UnitType.Warrior).To(new UnitDefinition("Warrior", 120));
Units.Bind(UnitType.Warrior).ToName("elite").To(new UnitDefinition("Elite", 200));
Units.Bind(UnitType.Archer).ToFactory(() => new UnitDefinition($"Archer_{n++}", 70));
Units.Bind(UnitType.Wizard).To<WizardDefinition>(); // resolved from the container

var def = Units.Get(UnitType.Warrior);          // default
var elite = Units.Get(UnitType.Warrior, "elite"); // named
```

`ToFactory` gives a fresh instance on every `Get`; `To<T>()` resolves the concrete type from the DI
container; the read paths are a dictionary lookup, 0 B after warmup (harness Z1).

**When to use it:** *data* mappings that fall outside the MVCS patterns.
**When not to use it:** to glue on behaviour that should already live as a
Command/Service/Model.

### 7.2 Named injection — `[Inject(Name = "...")]`

Want more than one dependency of the same type? Strange had
`[Inject(NamedInjections.PRIMARY)]`; in Nexus:

```csharp
builder.BindInstance<string>("gameName", "Idle Project");
builder.Bind<BStorage>("primary");
builder.BindInstance<BStorage>("secondary", new BStorage("backup"));
```

```csharp
[Inject(Name = "gameName")] public string GameName { get; set; }
```

#### Named resolution is strict

A name that is explicitly requested but not registered **does not silently fall back to the default binding**
— it throws in `Resolve` so that a typo is not masked. The contract:

| Call | Name registered | Result |
|---|---|---|
| `Resolve<T>(name)` | no | `InvalidOperationException` (strict) |
| `TryResolve<T>(name)` | no | `null` |
| `IsRegistered(type, name)` | no (no default either) | `false` |
| `Resolve<T>("")` or `Resolve<T>(null)` | — | always falls back to the default path |

```csharp
// "primary" is registered, "primray" is not:
builder.Bind<BStorage>("primary");

var ok = ctx.Container.Resolve<BStorage>("primary");          // ✓ works
var nullTry = ctx.Container.TryResolve<BStorage>("primray");  // null (optional)
try { ctx.Container.Resolve<BStorage>("primray"); }
catch (InvalidOperationException) { /* typo caught — strict behaviour */ }
```

This is the natural structure for variants such as "primary/secondary", and a misspelled
`[Inject(Name = "...")]` under strict injection is caught **not at registration time** but when
that dependency is resolved — so a typo only blows up the first time that type is
injected. For optional named dependencies use `[OptionalInject]` together
with `TryResolve(type, name)`; an empty name always follows the default path.
This prevents an unregistered name from silently falling back to the default and hiding the error
(an asymmetry fixed in code review).

### 7.3 `[PostConstruct]` — injection is done, get ready

A method that runs **after** injection has completed:

```csharp
public class CatalogService
{
    [Inject] public IBinder<UnitType, UnitDefinition> Units { get; set; }

    [PostConstruct]
    private void OnConstructed()
    {
        // Units is ready here — do not put setup logic in the constructor,
        // because injection may not have happened yet.
        Units.Bind(UnitType.Warrior).To(new UnitDefinition("Warrior", 120));
    }
}
```

Several methods are ordered with `Order`:

```csharp
[PostConstruct(Order = 0)] private void First() { }
[PostConstruct(Order = 10)] private void Second() { }
```

**Why is it needed?** The constructor runs *before* injection — the `[Inject]`
fields are still null. `[PostConstruct]` is the only place that gives you the guarantee
"my dependencies are ready".

### 7.4 `[Deconstruct]` — clean up before dying

When the container disposes singletons, the destroyed object's
`[Deconstruct]`-marked methods run **first**, in `Order` order
(before `IDisposable.Dispose`). This lets you release external resources (listeners, subscriptions,
handles) while the injections are still alive:

```csharp
public class MatchSession
{
    [Inject] public IPlayerRegistry Registry { get; set; }

    [Deconstruct]
    private void LeaveRoom()
    {
        // Registry is still non-null — cleanup can use the dependencies.
        Registry.Remove(this);
    }
}
```

It is symmetric with `[PostConstruct]`: one prepares at birth, the other cleans up at death.

### 7.5 `[Construct]` — the preferred constructor

If a class has several constructors and you want to mark explicitly which one DI
uses (Strange's `[Construct]`
alias):

```csharp
public class MatchSession
{
    public MatchSession() { /* parameterless — the default */ }

    [Construct]
    public MatchSession(IPlayerRegistry registry) { /* DI selects this one */ }
}
```

Nexus also accepts `[Inject]` on a constructor; both spellings select the same
thing. If there is neither and there are several constructors, the parameterless one is
preferred.

### 7.6 `.Once()` — a one-shot command

The Nexus equivalent of Strange's `Bind(GameEvent.HIT).To<Cmd>().Once()` pattern:
the command fires once, runs and **unregisters itself**. A second fire
silently does nothing — the handler is really removed, not guarded:

```csharp
builder.BindCommandOnce<StartSignal, StartCommand>();
builder.BindAsyncCommandOnce<BootstrapSignal, BootstrapCommand>();

// The fluent form does the same:
builder.BindSignal<StartSignal>().Once().To<StartCommand>();
```

**Concurrency guarantee:** the one-shot handler is claimed atomically **before** it
runs (`TryClaimOneShot`). If several threads that see the same read-copy snapshot
fire at the same time, the winner runs and the losers lose the claim and skip — the command runs
**exactly once**. Async one-shots are also
claimed before the command starts running, and the claim is held until the command
finishes, so a second fire that arrives while the command is still awaiting cannot run it
again (the sync, async-sequential and async-concurrent paths all carry the same
contract).

**Throw semantics:** if the command throws, the claim is released and the error
reaches the caller; the one-shot **stays registered** and the next fire retries
it. A one-shot is unregistered only after a successful run —
a failed first attempt does not lose a one-time setup for good.

**When to use it:** application start, one-time setup, the user's
first interaction, a one-time reward. Later triggers do not require
re-registration — every `Fire` uses a fresh pooled instance and then the
registration is deleted.

### 7.7 Polymorphic binding — one concrete, many interfaces

The Nexus equivalent of Strange's `Bind<IHittable>().Bind<IUpdateable>().To<Romulan>()`
pattern: one concrete class, registered under several interfaces, and
**all keys share the same singleton instance**:

```csharp
builder.BindMultiple<IBUnit, IAttackable, IUpdatable, CombatUnit>();

// All three interfaces return the SAME CombatUnit instance.
var unit = ctx.Resolve<IBUnit>();
var attackable = ctx.Resolve<IAttackable>(); // same reference as unit
```

**When to use it:** a class plays several roles and consumers need to see each
role through a separate interface. If you did separate `Bind`s, each
interface would produce a different singleton; `BindMultiple` prevents that.

---

## Part 8 — Two registration paths and the canonical choice

Nexus binds commands in two ways. Knowing the difference is choosing the right one:

| Path | Code | When |
|---|---|---|
| Explicit `BindCommand` | `builder.BindCommand<S, C>();` | **Real features.** Searchable, AOT-safe, uncontroversial in review. |
| Attribute `[SignalHandler]` | `[SignalHandler(typeof(S))] class C : ICommand<S>` | **Prototype only.** Fast but scattered. |

The decision rule is one sentence: *switch to explicit binding the moment the feature becomes real.*

This "dual path" is deliberate: teams coming from Strange want to try things quickly,
and production discipline wants explicit registration. Nexus offers both and says
which is canonical.

---

## Part 9 — The performance contract (why it is so fast)

Nexus's advantage over other frameworks is not in the concepts but in the implementation:

- **Struct signals:** `SignalBus.Fire` runs without boxing (boxing-free).
- **Command pool:** `CommandPoolManager` recycles command instances;
  the `Get`/`Return` cycle allocates ~0 bytes in steady state.
- **Mediator pool:** recycling up to 64 per type.
- **AOT binder:** resolvers generated at compile time instead of reflection.
- **Measured contract:** the `tools/nexus-benchmark` harness verifies these
  numbers on every run (e.g. 5000 signal dispatches 0 bytes, 5000
  `Get`/`Return` in the command pool ≤ 128 bytes).

You can run the harness yourself:

```bash
cd GameContainer/tools/nexus-benchmark
dotnet run -c Release
```

---

## Part 10 — Quick decision guide

| Your question | Answer |
|---|---|
| I need to announce "this happened" | `Signal` (struct) |
| I need to do work in response to a message | `Command` + `BindCommand` |
| I need to update the UI | `View` (visual) + `Mediator` (translator) |
| I need a persistent single instance (service) | `BindService<TInterface, TImpl>()` |
| I need in-game state (model) | `BindReactiveModel<TInterface, TImpl>()` |
| I need a non-type key→value table | `BindBinder<TKey, TValue>()` |
| More than one dependency of the same type | `Bind<T>(name)` + `[Inject(Name = "...")]` |
| Preparation after injection | `[PostConstruct]` |
| Cleanup before disposal | `[Deconstruct]` |
| Choosing one of several ctors | `[Construct]` |
| A one-shot command | `BindCommandOnce` / `.Once()` |
| One concrete → many interfaces | `BindMultiple<T1, T2, ..., TImpl>()` |
| Just a try/prototype | `[SignalHandler]` |
| Order/parallelism/exclusivity policy | `ExecutionMode` |

---

## Afterword — Philosophy

Strange gave you "a Binder that binds everything to everything". Nexus carries the same spirit
but adds one rule: **binding is disciplined.** Every connection is either visible explicitly
in `OnConfigure` or justified in the canonical document. This is the
balance point between Strange's flexibility and the control a production game needs.

Even though the concepts are the same, what makes Nexus different is not the concepts — it is **pooling,
struct signals, the async lifecycle and the measured performance contract**.
This guide gave you the *why*; the code already tells you the *how*.
