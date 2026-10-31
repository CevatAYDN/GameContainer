# UI Toolkit screen stack

`ScreenStack` keeps the screens of one context in a history, shows the screens the history says are visible, and
routes the device's back button through one chain, so a press has at most one handler. Its screens are
`ToolkitScreen`s: Nexus `View`s whose element is a UI Toolkit `VisualElement`. The design and the reasons behind
each choice are in [ADR-0003](https://github.com/CevatAYDN/GameContainer/blob/main/docs/adr/0003-in-game-ui-toolkit-screen-stack.md).

New game UI is UI Toolkit through this stack. Games with uGUI screens keep `UIManager`; one screen is managed by one
of the two, never both ([CANONICAL-PATTERNS.md](CANONICAL-PATTERNS.md)).

## Binding

```csharp
public sealed class AppLifecycle : IContextLifecycle
{
    public void OnConfigure(IContextBuilder builder)
    {
        builder.BindService<IInputService, InputService>();     // the back key: IInputService "Cancel"
        builder.BindServiceInterfacesAndSelfTo<ScreenStack>(); // IScreenStack and IBackRouter, one service
    }
    // OnInitializeAsync, OnStartAsync, OnDispose as usual
}
```

`BindServiceInterfacesAndSelfTo<ScreenStack>()` binds `IScreenStack` and `IBackRouter` together, so a context never
has a stack without its router. Without a bound `IInputService` the stack logs a warning and does not read the key;
`IBackRouter.Press()` still runs the chain, for tests and tools.

## A screen

```csharp
public sealed class SettingsScreen : ToolkitScreen
{
    public override UILayer Layer => UILayer.Modal;

    protected override void OnShown(object args) { /* fill the view from args */ }
    protected override void OnHidden() { }

    // First call of a back press while this screen is on top; true keeps the screen open.
    protected override bool OnBack() => false;
}
```

Put the component on a GameObject with a `UIDocument`, under the context's `Root`. When the screen binds it registers
with the stack of its own context under its type; there is no screen table to keep. An override of `OnBind` calls
`base.OnBind`, which registers it. A mediator binds to the screen as to any `View`.

| Member | Meaning |
|---|---|
| `Layer` | The screen's `UILayer`, default `Screen`. Inside one layer the screen opened last is shown and the one below it is hidden; screens on lower layers stay shown. The layer does not set the draw order: that is the `UIDocument`'s sort order, which the game sets. |
| `BlocksBack` | Back never closes it (a damaged-save recovery, a run's end). `Push` still opens other screens over it; a game that refuses routes while it is shown checks `IsShown<T>()` in its own command. |
| `OnBack()` | Hears a back press first while the screen is on top; return true when it handled the press. |
| `OnShown(args)`, `OnHidden()` | Called when the history shows or hides the screen. `Args` holds the arguments of the route that showed it last; `IsShown` says whether it is shown. |
| `ResolveRoot()` | The element the stack shows and hides. Default: the root of the `UIDocument` on the same GameObject. Override it when the screen is a part of a document, also one on another GameObject. |
| `Root` | The element `ResolveRoot()` returned, or null until it exists. |

The stack shows and hides `Root` with `display` and the class `nexus-screen--shown` (`ToolkitScreen.ShownClass`),
never by deactivating the GameObject. A screen whose GameObject is deactivated, or whose component is disabled,
unbinds as every `View` does: it leaves its context's history, its root is hidden, and the screen beneath it is shown.
Enabled again, it registers again, hidden.

A screen whose root is not there when it binds (a document on another GameObject that enables later) gets its state on
the first frame its root is there. A `UIDocument` that is disabled drops its tree; the stack completes a running
transition of that root at once and gives the screen's state to the root the document builds when it is enabled again.

## Navigation

`IScreenStack` changes the history at once. Each method has an `...Async` form whose task completes when the
transitions that call started end (at once without one).

| Call | Effect |
|---|---|
| `Push<T>(args)`, `PushAsync<T>(args)` | Opens the screen on top. The route already on top does nothing; the same screen on top with other arguments takes them and animates nothing. |
| `ReplaceRoot<T>(args)`, `ReplaceRootAsync<T>(args)` | Clears the history and opens the screen. |
| `Close<T>()`, `CloseAsync<T>()` | Takes the screen out of the history wherever it is. |
| `Back()`, `BackAsync()` | The top screen's `OnBack`, then its block, then a pop; returns `BackResult` (`Popped`, `HandledByScreen`, `Blocked`, `AtRoot`). With one entry left it does not pop. |
| `IsShown<T>()` | Whether the screen is in the history and visible. |
| `Current`, `Depth` | The route on top as an `IReadOnlyObservableProperty<ScreenRoute>` (a mediator watches it with `TrackObservable`), and the number of entries. |

A call made from a screen's `OnShown` or `OnHidden` during another call gets a completed task; the task of the call
that is running covers its transitions.

## The back button

A press goes through the context's `IBackRouter` and stops at the first of:

1. the steps added with `AddStep(step, BackOrder.BeforeScreens)`, the most recent first;
2. the stack's back: the top screen's `OnBack`, its block, a pop;
3. the steps added with `AddStep(step, BackOrder.AfterScreens)`, the most recent first;
4. the root behaviour, `AtRoot`.

| `RootBack` | At the root |
|---|---|
| `Nothing` (default) | Nothing happens (Unity's default on Android). |
| `MoveTaskToBack` | The app goes to the background (an Android player; nothing elsewhere). |
| `PressAgainToQuit` | The first press raises `PressAgainHintRequested`, where the game shows its own hint; a second press within `PressAgainSeconds` (default 2, unscaled) quits. Any press something else handles in between cancels it. |

A step is a `Func<bool>` that returns true when it handled the press; disposing the returned `IDisposable` removes it.
The router reads the key once per frame from its own driver object, not from `ITickService`, so back still closes a
screen while the game pauses its tick service.

**Contexts.** A stack whose parent context has an `IBackRouter` joins the parent's chain as a `BeforeScreens` step:
a child context's screens hear back before the parent's, and only the router at the top reads the key and has a root
behaviour. A second stack in an unrelated context (no common parent) logs an error and does not read the key; bind
the stack in a context both share instead.

## Transitions

A screen animates with a USS transition on its root. The root's own style is the hidden look; the stack adds
`nexus-screen--shown` on the panel update after a show and removes it at a hide:

```css
.settings { opacity: 0; transition-property: opacity; transition-duration: 0.25s; }
.settings.nexus-screen--shown { opacity: 1; }
```

With the class `settings` on an element of the document, `ResolveRoot()` returns that element
(`document.rootVisualElement.Q(className: "settings")`). A hide sets `display: none` after the transition, and the task
completes when the root's longest `transition-duration` plus `transition-delay` has passed. That time is taken from the
resolved style and counted on the panel's scheduler, in unscaled time, so a pause menu animates while
`Time.timeScale` is 0. A root without a transition shows and hides at once. A show or hide that replaces a running one
completes the earlier task at once.

`ReducedMotion = true` skips every transition and completes every task at once. It sets the root's inline
`transition-duration` to 0 and clears it when turned off, so a duration a game set inline on the root is cleared then.

## Safe area

`SafeArea.Pad(element)` pads an element by the device's safe area (`Screen.safeArea`) in panel units and keeps the
padding current on every geometry change (rotation, cutout, panel resize). Dispose the returned object to stop and clear
the padding.

## Input setup

The stack works with both setups in use: an `EventSystem` with `InputSystemUIInputModule`, and UI Toolkit's own input
(InputForUI) without an `EventSystem`. These documents recommend one only after the device capture that ADR-0003
decision 7 asks for: the managed allocation per touched frame of the `EventSystem` setup, on the phone where InputForUI
was measured.
