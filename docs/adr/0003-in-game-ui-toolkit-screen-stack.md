# ADR-0003: In-Game UI Toolkit Screen Stack

- **Status:** Accepted for the direction. The API sketch below is accepted with the answers to its three open
  questions ("Answers to the open questions", 2026-10-28; the owner had them decided by the advisor on
  [#76](https://github.com/CevatAYDN/GameContainer/issues/76)). Names may still change in the stack cycle. On
  2026-10-31 the owner moved the stack into v0.6.0 (it no longer waits for its own cycle after the tag), kept decision 2
  (`UIManager` stays beside the stack) and ruled that the framework's existing IMGUI development panels stay as they
  are while everything it adds from then on is UI Toolkit ([#137](https://github.com/CevatAYDN/GameContainer/issues/137)).
- **Date:** 2026-10-28
- **Decision makers:** Project owner. UI Toolkit in the framework was decided on 2026-10-25 (#76, option (a)). On
  2026-10-27 the stack was given its own cycle and re-pin. On 2026-10-28 the owner chose "this ADR and the API sketch
  now, the stack in its own cycle".
- **Supersedes:** [ADR-0001](0001-ui-stack-in-game-ugui-editor-ui-toolkit.md) for in-game UI only. ADR-0001's decision
  that editor screens use UI Toolkit stands.

## Context

The framework's runtime UI is `UIManager` (`IUIManager`). It builds uGUI canvases under `[Nexus_UICanvas]`
(`Runtime/Services/UI/UICanvasSystem.cs`) and opens `ScreenView` prefabs on `UILayer` roots. ADR-0001 fixed in-game UI
to uGUI.

All three games that consume Nexus are UI Toolkit only, and none of them uses `IUIManager`:

- Gigawatt destroys any Canvas at start (`Assets/Scripts/Game/Lifecycle/GigawattRuntimeBootstrap.cs`).
- Wickling's rules forbid uGUI (its `CLAUDE.md`, `docs/CODE_RULES.md`).
- ChipCrisis chose UI Toolkit in its `docs/adr/0007-ui-toolkit.md`. Its `docs/UI_UX_GUIDE.md` adds that `IUIManager`
  opened screens outside its App and Shop Roots (#67, since fixed in `91e1c39`).

Each game built its own screens, navigation and back handling. The table below was read on 2026-10-28 at each game's
committed `HEAD`: Gigawatt `4dfdf88`, Wickling `69f7494`, ChipCrisis `a0c8a72`. ChipCrisis also had uncommitted sheet
work by the owner (a sheet slide); none of it is cited here. Paths are relative to each game's repository.

| | Gigawatt | Wickling | ChipCrisis |
|---|---|---|---|
| Screen shape | One `UIDocument` (`D0GameScreen.uxml`). 23 `View`s each show a subtree under `#screen-root` (`UI/UiNavigationView.cs`) | One `UIDocument` per screen. `UI/ScreenView.cs` (a `View` with `[RequireComponent(typeof(UIDocument))]`) toggles `display` | One `UIDocument` per screen with its `View`, placed by `Assets/_Project/Editor/MapSceneBuilder.cs` |
| Lifetime | Built once, `display` toggled; no pooling, no run-time loading | Same | Same |
| Navigation | `Models/IUiNavigationModel.cs`: `Stack<UiRoute>`, `Navigate(UiRoute route, bool overlay)`, `bool Back()`; `NavigateUiSignal` → `NavigateUiCommand` | `Models/IUiNavigationModel.cs`: `Stack<UiScreen>`, `Navigate`, `bool Back()`; `NavigateSignal` and `BackSignal` commands | No history. Sheets and modals open from their mediators; "something is open" is `Tick.IsPaused` |
| Back source | `Keyboard.current.escapeKey`, plus legacy `Input.GetKeyDown(KeyCode.Escape)` (`UI/MobileScreenLayout.cs`) | `Keyboard.current.escapeKey.wasPressedThisFrame` in every `ScreenView` | `IInputService.GetButtonDown("Cancel")` in `Mediators/AppPauseMediator.cs` |
| Back order | One chain in `MobileScreenLayout`: Recovery ignores it, a route goes back, then world steps (placement, drawer, inspector, heat map), using `FindAnyObjectByType` | Every view polls in `LateUpdate`; no "handled" flag. The Camp skips back while `SetCovered`, which only follows the navigation model | `BackPressedSignal` to every sheet mediator. The Return and MapEnd modals do not subscribe, so back is swallowed |
| At the root | Second press within 2 s quits, with a hint toast | Second press within 2 s quits (`UI/CampMediator.cs`), with a hint label | `moveTaskToBack` |
| Layers | One panel. Tree order, plus `BringToFront` for the shown panel | One `PanelSettings`. `UIDocument.sortingOrder` 0 to 5 (HUD 0, menus 1, FlameOut 2, Pause 3, Result 4, notice 5) | Four `PanelSettings`: World −40, HUD −30, Screen −20, Modal −10 |
| Transitions | Inline styles: enter after 40 ms, leave after 160 ms; `ReducedMotion` skips them | `UI/ScreenFade.cs`: opacity in on the next tick; close is instant | Sheets: a 0.26 s USS slide (`Assets/_Project/UI/Sheet.uss`); modals: none. Nothing is awaited in any game |
| Safe area | `MobileScreenLayout` positions `#screen-root` | `UI/OverlayRoot.cs` pads every root and keeps backdrops at the edges | `Views/SafeAreaPadding.cs` pads on `GeometryChangedEvent` |
| UI input path | `EventSystem` with `InputSystemUIInputModule` | No `EventSystem` (InputForUI) | No `EventSystem` (InputForUI) |
| `PanelSettings` | 390 × 844, scale with screen size, match width | 390 × 844, scale with screen size, match width | 1080 × 1920, match 0.5 |
| Tests | Model and command: `Tests/PlaytestRunner/UiRegressionSuite.cs`. Device `KEYCODE_BACK`: `Tools/ci/gw_ci.py` | Batch play checks: `Editor/WicklingAutoplayDriver.cs` | PlayMode: `Assets/_Project/Tests/PlayMode/BootSceneTests.cs` |

Read from the code, not run:

- **Gigawatt.** The guard that keeps every route except Recovery away while Recovery is shown lives in
  `NavigateUiCommand`. Mediators that call the model directly skip it.
- **Wickling.** `ReminderAsk` and `SaveNotice` read back in their own `LateUpdate` while the navigation model still says
  Camp. One press can reach both the dialog and the Camp.
- **ChipCrisis.** The ad commands also hold the tick pause (`Commands/RequestRewardedAdCommand.cs`,
  `Commands/ShowInterstitialCommand.cs`). `Tick.IsPaused` therefore does not mean "a sheet is open".

## Decision

1. **UI Toolkit is the framework's in-game UI.** A UI Toolkit screen stack is added to `com.nexus.core`, beside
   `UIManager`.
2. **`UIManager` stays as it is.** It is not deprecated by this ADR and its API does not change. No game uses it today.
   A later ADR may deprecate it once the stack has shipped.
3. **The framework takes what two or three games each built:**
   - the navigation history;
   - one back chain, where a press has at most one handler;
   - the root-back behaviour, as a setting;
   - safe-area padding;
   - the show and hide mechanics of a screen.
4. **The games keep the rest:** toasts and hints, loading and title cards, Wickling's tap guard, tablet layouts,
   `PanelSettings` values, the look of every transition (USS), and HUD and world views that are not navigated.
5. **Async.** #76 says "async open and close", but no game awaits a transition or returns a task today. The stack
   changes its state (current screen, history, who gets back) synchronously in every method. That way a caller that
   does not await sees the same order as today's code. The `…Async` variants return a `ValueTask` that completes when
   the screen's transition ends. When there is no transition, it is already complete and allocates nothing. This
   follows `UIManager`'s `OpenScreen` and `OpenScreenAsync` pair.
6. **Layers.** The stack reuses `UILayer` (Background, HUD, Screen, Popup, Modal, Overlay, System) as its order. Its
   summary will stop saying "UGUI". ChipCrisis's four panels map onto Background, HUD, Screen and Modal. Wickling's
   six orders map onto HUD, Screen and Modal. Inside one layer, the screen opened last is on top.
7. **UI input path: not chosen here.** Two setups are in use: Gigawatt's `EventSystem` with `InputSystemUIInputModule`,
   and InputForUI with no `EventSystem` in Wickling and ChipCrisis. ChipCrisis measured about 150 B per touched frame
   under InputForUI on 2311DRK48G: 274 KB over 1811 frames, inside the engine (its `docs/RELEASE_GATES.md`). The stack
   works under both setups. The first step of the stack cycle is the same capture with the other setup: 60 s, a 30 s
   drag, the same phone. The documents recommend a setup only after that.
8. **Back source.** The stack reads back through `IInputService` "Cancel". Its `InputAction` records a press and release
   in one update, which a key's `wasPressedThisFrame` misses. That path is proven on 2311DRK48G: #92, 0 of 10 first
   presses lost. Gigawatt's `MobileScreenLayout` says Android's back does not reach the Input System's keyboard on a
   POCO X6 Pro, and reads the key's `wasPressedThisFrame`. That may be the effect #92 fixed. It is checked when Gigawatt
   moves its back key to `IInputService` (Gigawatt#4), not assumed here.
9. **Contexts.** In all three games, screens are `View`s that the game places under its own `Root`, and the stack
   leaves it that way. A screen registers with the stack of the context it binds to, so with two Roots each context has
   its own stack. This version never creates screens at run time. If a later one does, what it creates carries
   `ScreenOwner`, as `UIManager` does since #67.

## API sketch (for objection before code)

Names and members are proposals. The comments say which game needs each member.

```csharp
namespace Nexus.Core.Services
{
    /// A screen is a Nexus View, as in all three games; its mediator binds as usual. It registers itself with the
    /// IScreenStack of the context it binds to, so no game keeps a screen table.
    public abstract class ToolkitScreen : View
    {
        public virtual UILayer Layer => UILayer.Screen;

        /// Back never closes it: Gigawatt's Recovery, Wickling's FlameOut, ChipCrisis's Return and MapEnd.
        public virtual bool BlocksBack => false;

        /// The element the stack shows and hides. Default: the root of the UIDocument on this GameObject
        /// (Wickling, ChipCrisis). Gigawatt returns a named subtree of its one document.
        protected virtual VisualElement ResolveRoot();

        /// First call of a back press while this screen is on top; true = handled here, nothing is popped.
        /// Wickling's Pause (leave the quit question first), Result (back = continue), ReminderAsk (back = not now).
        protected virtual bool OnBack() => false;

        protected virtual void OnShown(object args) { }
        protected virtual void OnHidden() { }

        public VisualElement Root { get; }
        public object Args { get; }      // Gigawatt's UiRoute.TargetId, or null
        public bool IsShown { get; }
    }

    public readonly struct ScreenRoute
    {
        public readonly Type Screen;
        public readonly object Args;
    }

    public enum BackResult { Popped, HandledByScreen, Blocked, AtRoot }

    public interface IScreenStack : INexusService
    {
        /// What the games' mediators watch today (Gigawatt and Wickling: Navigation.Current). Read-only: only the
        /// stack sets it, so it cannot drift from the history.
        IReadOnlyObservableProperty<ScreenRoute> Current { get; }
        int Depth { get; }

        /// Gigawatt overlay:true, Wickling Navigate(menu), ChipCrisis opening a sheet or a modal.
        /// Pushing the route already on top does nothing; the same screen on top with other arguments takes them.
        /// The screen below on the same layer is hidden; screens on
        /// lower layers stay shown. Push does not look at BlocksBack: a game that refuses routes while a blocking
        /// screen is shown checks IsShown<T>() in its own command, as Gigawatt's NavigateUiCommand does.
        void Push<TScreen>(object args = null) where TScreen : ToolkitScreen;

        /// Gigawatt overlay:false, Wickling Navigate(Camp or Run): the history is cleared.
        void ReplaceRoot<TScreen>(object args = null) where TScreen : ToolkitScreen;

        /// Takes that screen out wherever it is in the history: ChipCrisis's Claim on Return, Wickling's dialogs.
        void Close<TScreen>() where TScreen : ToolkitScreen;

        /// The top screen's OnBack, then a pop. Blocked when the top screen BlocksBack; AtRoot with one entry.
        BackResult Back();

        /// Same state change as above; the task completes when the transition ends (already complete without one).
        ValueTask PushAsync<TScreen>(object args = null) where TScreen : ToolkitScreen;
        ValueTask ReplaceRootAsync<TScreen>(object args = null) where TScreen : ToolkitScreen;
        ValueTask CloseAsync<TScreen>() where TScreen : ToolkitScreen;
        ValueTask<BackResult> BackAsync();

        bool IsShown<TScreen>() where TScreen : ToolkitScreen;

        /// Gigawatt's setting: transitions are skipped and every task completes at once.
        bool ReducedMotion { get; set; }
    }

    public enum RootBack { Nothing, MoveTaskToBack, PressAgainToQuit }

    public enum BackOrder { BeforeScreens, AfterScreens }

    public interface IBackRouter : INexusService
    {
        /// ChipCrisis: MoveTaskToBack. Gigawatt and Wickling: PressAgainToQuit.
        RootBack AtRoot { get; set; }

        /// 2 s in Gigawatt and Wickling.
        float PressAgainSeconds { get; set; }

        /// The first press at the root under PressAgainToQuit; the game shows its own hint.
        event Action PressAgainHintRequested;

        /// A game step outside the stack; true = handled. Gigawatt's placement cancel, drawer, inspector and heat
        /// map are AfterScreens steps. Disposing removes the step.
        IDisposable AddStep(Func<bool> step, BackOrder order);

        /// One press through the chain, for tests and tools (Gigawatt's review driver, Wickling's autoplay).
        bool Press();
    }

    public static class SafeArea
    {
        /// Pads the element by the device's safe-area insets in panel units and keeps them current
        /// (rotation, cutout, panel resize). Dispose to stop.
        public static IDisposable Pad(VisualElement element);
    }
}
```

A press goes through these steps and stops at the first one that handles it:

1. `BeforeScreens` steps, the most recently added first.
2. `IScreenStack.Back()`. `Popped`, `HandledByScreen` and `Blocked` end the press.
3. `AfterScreens` steps, the most recently added first.
4. The `AtRoot` behaviour.

`IBackRouter` reads `IInputService.GetButtonDown("Cancel")` once per frame. A frame without a press allocates nothing.

**Showing and hiding.** To show a screen, the stack sets `display: flex`, then adds the class `nexus-screen--shown` on the
next panel update. To hide it, the stack removes the class and sets `display: none` when the root's transitions end. A
screen whose USS has no transition shows and hides at once, as Wickling's and ChipCrisis's closes do today. How the end
is found is in "As built" below.

### Answers to the open questions (2026-10-28)

The sketch went to #76 with three open questions. The owner had them decided by the advisor; the answers are written
into the sketch above.

- **Push over a blocking screen: a game rule.** `Push` is not refused while a `BlocksBack` screen is on top. Only
  Gigawatt refuses routes there (its `NavigateUiCommand` lets only Recovery through while Recovery is shown), and
  decision 3 takes into the framework what two or three games each built. `IsShown<TScreen>()` gives a game's command
  the check, and `BlocksBack` keeps one meaning: back never closes the screen.
- **The screen below: hidden on the same layer, shown on a lower layer.** Every game fits:
  - Gigawatt shows one route at a time, all on one layer.
  - Wickling's `WicklingRuntimeBootstrap.cs` (at `69f7494`) gives the Camp the HUD's order (0) and its menus order 1,
    so the Camp stays shown under a menu.
  - ChipCrisis's `MapSceneBuilder.cs` (at `6618271`) puts the four sheets on its `ScreenPanel` (order −20), the HUD,
    dock and menu on its `HudPanel` (−30) and the Return and MapEnd modals on its `ModalPanel` (−10). A sheet hides
    nothing on the HUD, and a modal over a sheet leaves the sheet shown.
- **`Current`: a read-only observable.** The core has no read-only interface for `ObservableProperty<T>` (the class is
  sealed, and `Mediator.TrackObservable` takes the class). The stack cycle adds `IReadOnlyObservableProperty<T>`
  (`Value`, `OnChanged`, `RemoveOnChanged`), which `ObservableProperty<T>` implements, and a `TrackObservable` overload
  for it. Both are additions; no existing call changes. A setter outside the stack becomes a compile error instead of
  a history that disagrees with `Current`, and the mediators keep watching `Current` as they do today. An event alone
  was the fallback, in case the helper could not take the interface.

## As built (2026-10-31)

The stack was built in three commits: the read-only observable, the engine-free history and back chain, and the engine
side (`Runtime/Services/UI/Stack/Toolkit/`). Where the sketch left a choice or the build found something, this is what
was chosen and why:

- **One service, both interfaces.** `ScreenStack` implements `IScreenStack` and `IBackRouter` and is bound with
  `builder.BindServiceInterfacesAndSelfTo<ScreenStack>()`, so a context never has a stack without its router or a
  router without its stack.
- **Contexts and the key.** A press must have one handler across contexts too. A `ScreenStack` whose parent context
  resolves an `IBackRouter` joins that router's chain as a `BeforeScreens` step: its own steps and screens, without its
  `AtRoot` behaviour. A child context's screens therefore hear back before the parent's, and only the router at the
  top reads the key and has a root behaviour. A second stack in an unrelated context (no common parent) logs an error
  and does not read the key; the fix is to bind the stack in a common parent context.
- **The key is read by the router's own driver, not by `ITickService`.** `TickService` stops ticking while
  `IsPaused`, and ChipCrisis pauses it while a sheet is open; back must still close that sheet. The router keeps a
  `[Nexus_BackKey]` object whose `Update` polls once per frame, and destroys it when its context is disposed. Without
  a bound `IInputService` it logs a warning, and `IBackRouter.Press()` still runs the chain.
- **The end of a transition is timed, not an event.** `TransitionEndEvent` comes only for a transition that ran: none
  when a transition is removed before it completes (Unity's API documentation of the event), and none when no
  transition was created (`TransitionRunEvent`) because no value changed, as for a root whose opacity already has its
  shown value. Waiting for the event could wait forever.
  The stack waits for the longest duration plus delay in the root's resolved style, on the panel's scheduler (unscaled
  time). A root that has not been styled yet, the frame its document appears, has no transition and shows at once.
  A show or hide that supersedes a running one completes the earlier task at once. The screen on top, shown again
  with other arguments, animates nothing and completes at once. A call made from a screen's handler during another
  call gets a completed task, because its screens are told after it returns, by the call that is running; that
  call's task covers them (harness SS10).
- **Reduced motion** sets the root's inline `transition-duration` to 0 and clears it when turned off, so a duration a
  game set inline on the root is cleared then.
- **Hiding never deactivates.** A screen that is deactivated unbinds, as every `View` does, and leaves its context's
  history; the screen beneath it is shown, and its root is hidden, also when only the screen's component was disabled
  and its document still shows. Enabled again, it registers again, hidden. A screen whose root is not there
  yet when it binds (Gigawatt's shape: a subtree of a document on another GameObject that enables after it) gets its
  state on the next frame. An override of `OnBind` calls `base.OnBind`, which registers it.
- **Compile check (verification plan, item 2).** `UnityStubs.cs` gains no UIElements types: the four files under
  `Toolkit/` join the files the harness leaves out and are compiled by `tools/nexus-compile-check`. The core's
  `package.json` declares `com.unity.modules.uielements` 1.0.0, which it now uses.

## Verification plan for the stack cycle

"Verified" means a result file exists. The cycle produces:

1. **Host harness.** The history and the back chain are engine-free and get a suite. Gigawatt's
   `UiRegressionSuite` cases are the contract:
   - back order;
   - the root returns `AtRoot`;
   - the same route twice is one entry;
   - a blocking screen survives `Back`.

   Two more cases:
   - one press has one handler (Wickling's case above);
   - step order and each root behaviour.

   GCAudit adds a case for the per-frame poll without a press: 0 B. The harness log goes in `Nexus/artifacts/`.
2. **Compile check.** `ToolkitScreen`, the host and `SafeArea` use UIElements types. `tools/nexus-benchmark/UnityStubs.cs`
   has none today, so they either join the files the harness leaves out or the stubs gain those types. The cycle
   decides which. `tools/nexus-compile-check` compiles them against the editor's modules. The cycle also checks whether
   `package.json` needs `com.unity.modules.uielements`.
3. **PlayMode** (`Tests/Runtime`):
   - open, close and back;
   - a context with two Roots, where each screen reaches its own context's stack (#76's done-when);
   - a transition completes the `ValueTask`;
   - a screen without a transition completes at once;
   - one press reaches one handler across a parent and a child context;
   - a deactivated screen leaves the history;
   - the back key closes a screen while `TickService` is paused.
4. **Device** (2311DRK48G):
   - `KEYCODE_BACK` through the chain in a development player: sheet, then modal, then root;
   - the input-path capture from decision 7.

   Result files go under `Nexus/artifacts/`.
5. **Documents:** the core README's UI section, the core CHANGELOG, the `UILayer` summary, and a row in
   `docs/RELEASE_GATES.md`.
6. **Adoption** happens at the re-pin after the stack ships, in each game's repository. The stack items are open in
   Wickling#2 and ChipCrisis#2. Gigawatt#2 is closed, so Gigawatt gets a new issue.

## Consequences

- No API break. `UIManager`, `ScreenView` and `ScreenMediator` stay.
- `com.nexus.core` gains UI Toolkit runtime code next to its uGUI code.
- Seen in the surveys and not decided here:
  - Gigawatt made `MobileScreenLayout` a `View` only to reach the context.
  - Wickling's mediators bind before its services are ready, and rewrite their text on `AppStartedSignal`.

  These are framework questions of their own. They become issues if a game asks.

## References

- Issue #76 and its comments of 2026-10-27 (scope) and 2026-10-28 (the InputForUI measurement).
- Issue #67 (`ScreenOwner`, `91e1c39`) and #92 (`IInputService` back, first press).
- Gigawatt `4dfdf88`, Wickling `69f7494` and ChipCrisis `a0c8a72`: read-only survey of 2026-10-28.
