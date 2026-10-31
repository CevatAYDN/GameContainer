using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Core.Services;
using UnityEngine;

namespace NexusBench
{
    /// <summary>
    /// The screen stack's history and back chain without the engine (#76, ADR-0003). SS1-SS3 are Gigawatt's
    /// UiRegressionSuite cases (back order, the root, one entry per route, a blocking screen); the rest are the ADR's
    /// two more (one handler per press, step order and the root behaviours) and the visibility rule.
    /// </summary>
    public static class ScreenStackSuite
    {
        private sealed class World { }
        private sealed class Build { }
        private sealed class Settings { }
        private sealed class Privacy { }
        private sealed class Inspector { }
        private sealed class Sale { }
        private sealed class Recovery { }
        private sealed class Camp { }
        private sealed class Dialog { }
        private sealed class Hud { }

        private sealed class FakeScreen : IStackScreen
        {
            public UILayer Layer { get; set; } = UILayer.Screen;
            public bool BlocksBack { get; set; }
            public Func<bool> BackHandler = () => false;
            public Action Shown;
            public TaskCompletionSource<bool> ShowTransition, HideTransition;
            public bool IsShown;
            public object Args;
            public int Shows, Hides, Backs;

            public bool OnBack() { Backs++; return BackHandler(); }
            public ValueTask Show(object args)
            {
                IsShown = true; Args = args; Shows++; Shown?.Invoke();
                return ShowTransition == null ? default : new ValueTask(ShowTransition.Task);
            }
            public ValueTask Hide()
            {
                IsShown = false; Hides++;
                return HideTransition == null ? default : new ValueTask(HideTransition.Task);
            }
        }

        internal sealed class FakeInput : IInputService
        {
            public bool CancelDown;
            public Vector2 MoveInput => default;
            public bool IsInputActive => true;
            public void SetVirtualJoystickInput(Vector2 direction) { }
            public void SetInputProvider(IInputProvider provider) { }
            public bool GetButton(string actionName) => false;
            public bool GetButtonDown(string actionName) => CancelDown && actionName == "Cancel";
            public bool GetButtonUp(string actionName) => false;
            public void UpdateInput(float deltaTime) { }
        }

        public static int Run()
        {
            int failures = 0;
            Console.WriteLine("\n===============================================================================");
            Console.WriteLine("[ScreenStack] SCREEN HISTORY AND BACK CHAIN SUITE (#76)");
            Console.WriteLine("===============================================================================");

            failures += AssertPass("SS1. Back_ReturnsInOrder_KeepsEachRoutesArgs_AndStopsAtTheRoot", BackOrder);
            failures += AssertPass("SS2. TheRouteOnTop_IsOneEntry_ItsNewArgsReplaceTheTop_AndAScreenDeeperIsPushedAgain", OneEntryPerRoute);
            failures += AssertPass("SS3. ABlockingScreen_SurvivesBack", BlockingScreen);
            failures += AssertPass("SS4. OnePress_HasOneHandler", OnePressOneHandler);
            failures += AssertPass("SS5. Steps_RunInTheirOrder_AroundTheScreens_ThenTheRootBehaviour", StepOrderAndRoot);
            failures += AssertPass("SS6. AScreen_IsShownWhileItHoldsTheLastEntryOfItsLayer", Visibility);
            failures += AssertPass("SS7. AStepRemovedDuringAPress_SkipsNothingAndRepeatsNothing", StepsRemovedDuringAPress);
            failures += AssertPass("SS8. AScreenThatChangesTheHistoryWhenShown_LeavesItConsistent", ReentrantShow);
            failures += AssertPass("SS9. AChildContextsChain_RunsFirstInItsParents_WithoutItsOwnRootBehaviour", ChildChain);
            failures += AssertPass("SS10. ACallFromAShowHandler_KeepsTheTransitionsOfTheCallThatIsRunning", NestedCallTransitions);

            if (failures == 0)
                Console.WriteLine("\n[ScreenStack] ALL SCREEN STACK TESTS PASSED ✓");
            else
                Console.WriteLine($"\n[ScreenStack] {failures} SCREEN STACK TEST(S) FAILED ✗");
            return failures;
        }

        private static int AssertPass(string testName, Func<bool> testFunc)
        {
            try
            {
                bool passed = testFunc();
                Console.WriteLine($"[ScreenStack] {(passed ? "PASS" : "FAIL")}  {testName}");
                return passed ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenStack] FAIL  {testName}: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        private static bool Check(bool ok, string what)
        {
            if (!ok) Console.WriteLine("      " + what);
            return ok;
        }

        private static (ScreenHistory history, Dictionary<Type, FakeScreen> screens) NewHistory(params Type[] types)
        {
            var history = new ScreenHistory();
            var screens = new Dictionary<Type, FakeScreen>();
            foreach (var type in types)
            {
                screens[type] = new FakeScreen();
                history.Register(type, screens[type]);
            }
            return (history, screens);
        }

        private static bool BackOrder()
        {
            var (h, _) = NewHistory(typeof(World), typeof(Build), typeof(Settings), typeof(Privacy), typeof(Inspector), typeof(Sale));
            h.ReplaceRoot(typeof(World), null);
            h.Push(typeof(Build), "pc_basic");
            h.Push(typeof(Settings), null);
            h.Push(typeof(Privacy), null);
            bool ok = Check(h.Back() == BackResult.Popped && h.Current.Value.Screen == typeof(Settings), "Back from Privacy did not return to Settings")
                && Check(h.Back() == BackResult.Popped && h.Current.Value == new ScreenRoute(typeof(Build), "pc_basic"), "Back did not return to Build with its pc_basic target")
                && Check(h.Back() == BackResult.Popped && h.Current.Value.Screen == typeof(World), "Back from Build did not return to World")
                && Check(h.Back() == BackResult.AtRoot && h.Current.Value.Screen == typeof(World) && h.Depth == 1, "Back at the root changed the history");
            h.Push(typeof(Inspector), "a");
            h.Push(typeof(Sale), "a");
            ok &= Check(h.Back() == BackResult.Popped && h.Current.Value == new ScreenRoute(typeof(Inspector), "a"), "Back from Sale did not restore the same inspector");
            var empty = new ScreenHistory();
            return ok & Check(empty.Back() == BackResult.AtRoot && empty.Current.Value.Screen == null, "Back on an empty history was not AtRoot");
        }

        private static bool OneEntryPerRoute()
        {
            var (h, s) = NewHistory(typeof(World), typeof(Settings), typeof(Inspector), typeof(Sale));
            h.ReplaceRoot(typeof(World), null);
            h.Push(typeof(Settings), null);
            h.Push(typeof(Settings), null);
            bool ok = Check(h.Depth == 2 && s[typeof(Settings)].Shows == 1, "the same route twice made two entries or showed the screen twice")
                && Check(h.Back() == BackResult.Popped && h.Current.Value.Screen == typeof(World), "Back after the same route twice did not return to World");
            h.Push(typeof(Inspector), "a");
            h.Push(typeof(Inspector), "b");
            ok &= Check(h.Depth == 2 && h.Current.Value == new ScreenRoute(typeof(Inspector), "b"), "new arguments on the top screen did not replace its entry")
                && Check(s[typeof(Inspector)].Shows == 2 && (string)s[typeof(Inspector)].Args == "b", "the top screen was not shown again with its new arguments");
            h.Push(typeof(Sale), "b");
            h.Push(typeof(Inspector), "c");
            ok &= Check(h.Depth == 4, "a screen deeper in the history was not pushed again")
                && Check(h.Back() == BackResult.Popped && h.Back() == BackResult.Popped && h.Current.Value == new ScreenRoute(typeof(Inspector), "b"), "Back did not return to the earlier entry of the same screen");
            h.Push(typeof(Sale), "b");
            h.Push(typeof(Inspector), "d");
            h.Close(typeof(Inspector));
            return ok & Check(h.Depth == 2 && h.Current.Value == new ScreenRoute(typeof(Sale), "b") && !h.IsShown(typeof(Inspector)), "Close did not take out every entry of the screen");
        }

        private static bool BlockingScreen()
        {
            var (h, s) = NewHistory(typeof(World), typeof(Recovery));
            s[typeof(Recovery)].BlocksBack = true;
            h.ReplaceRoot(typeof(World), null);
            h.Push(typeof(Recovery), null);
            bool ok = Check(h.Back() == BackResult.Blocked && h.Depth == 2 && h.Current.Value.Screen == typeof(Recovery), "Back closed the blocking screen over World");
            h.ReplaceRoot(typeof(Recovery), null);
            ok &= Check(h.Back() == BackResult.Blocked && h.Current.Value.Screen == typeof(Recovery), "Back closed the blocking root screen");
            s[typeof(Recovery)].BackHandler = () => true;
            return ok & Check(h.Back() == BackResult.HandledByScreen && s[typeof(Recovery)].Backs == 3, "a blocking screen did not hear the press first");
        }

        // Wickling's case: a dialog over the Camp; one press reaches the dialog and nothing else.
        private static bool OnePressOneHandler()
        {
            var (h, s) = NewHistory(typeof(Camp), typeof(Dialog));
            var dialog = s[typeof(Dialog)];
            dialog.Layer = UILayer.Popup;
            h.ReplaceRoot(typeof(Camp), null);
            h.Push(typeof(Dialog), null);
            int after = 0;
            var chain = new BackChain(h.Back, () => 0, null, null);
            chain.AddStep(() => { after++; return true; }, Nexus.Core.Services.BackOrder.AfterScreens);
            dialog.BackHandler = () => true;
            bool ok = Check(chain.Press() && dialog.Backs == 1 && s[typeof(Camp)].Backs == 0 && after == 0 && h.Depth == 2, "a press the dialog handled reached another handler or changed the history");
            dialog.BackHandler = () => false;
            ok &= Check(chain.Press() && h.Depth == 1 && s[typeof(Camp)].Backs == 0 && after == 0 && !dialog.IsShown, "a press that popped the dialog reached the Camp or the step");
            return ok & Check(chain.Press() && after == 1 && s[typeof(Camp)].Backs == 1, "a press at the root did not reach the AfterScreens step once");
        }

        private static bool StepOrderAndRoot()
        {
            var (h, _) = NewHistory(typeof(World), typeof(Settings));
            h.ReplaceRoot(typeof(World), null);
            double now = 0;
            int moved = 0, quit = 0, hints = 0;
            var chain = new BackChain(h.Back, () => now, () => moved++, () => quit++);
            chain.PressAgainHintRequested += () => hints++;
            var order = new List<string>();
            var first = chain.AddStep(() => { order.Add("before1"); return false; }, Nexus.Core.Services.BackOrder.BeforeScreens);
            var second = chain.AddStep(() => { order.Add("before2"); return false; }, Nexus.Core.Services.BackOrder.BeforeScreens);
            var afterStep = chain.AddStep(() => { order.Add("after"); return false; }, Nexus.Core.Services.BackOrder.AfterScreens);
            bool ok = Check(!chain.Press() && string.Join(",", order) == "before2,before1,after", "steps ran as " + string.Join(",", order));

            order.Clear();
            h.Push(typeof(Settings), null);
            ok &= Check(chain.Press() && string.Join(",", order) == "before2,before1" && h.Depth == 1, "with a screen to pop, the AfterScreens step ran or nothing popped");
            first.Dispose(); second.Dispose(); afterStep.Dispose();

            chain.AtRoot = RootBack.MoveTaskToBack;
            ok &= Check(chain.Press() && moved == 1 && quit == 0, "MoveTaskToBack did not move the task to the back");

            chain.AtRoot = RootBack.PressAgainToQuit;
            ok &= Check(chain.Press() && hints == 1 && quit == 0, "the first press at the root did not ask for the hint");
            now = 1.9;
            ok &= Check(chain.Press() && quit == 1, "a second press within the window did not quit");
            now = 10;
            chain.Press();
            now = 12.5;
            ok &= Check(chain.Press() && quit == 1 && hints == 3, "a second press after the window quit instead of asking again");
            h.Push(typeof(Settings), null);
            chain.Press();
            ok &= Check(chain.Press() && quit == 1 && hints == 4, "a press that popped a screen did not end the pending press-again");

            chain.AtRoot = RootBack.Nothing;
            return ok & Check(!chain.Press() && moved == 1 && quit == 1, "RootBack.Nothing handled the press");
        }

        private static bool Visibility()
        {
            var (h, s) = NewHistory(typeof(World), typeof(Hud), typeof(Settings), typeof(Privacy), typeof(Dialog));
            s[typeof(Hud)].Layer = UILayer.HUD;
            s[typeof(Dialog)].Layer = UILayer.Modal;
            h.ReplaceRoot(typeof(Hud), null);
            h.Push(typeof(World), null);
            h.Push(typeof(Settings), null);
            bool ok = Check(s[typeof(Hud)].IsShown && !s[typeof(World)].IsShown && s[typeof(Settings)].IsShown, "the screen below on the same layer was not hidden, or the HUD below was");
            h.Push(typeof(Dialog), null);
            ok &= Check(s[typeof(Settings)].IsShown && s[typeof(Dialog)].IsShown && h.IsShown(typeof(Settings)), "a modal over a screen hid the screen");
            h.Close(typeof(Settings));
            ok &= Check(s[typeof(World)].IsShown && !s[typeof(Settings)].IsShown && s[typeof(Dialog)].IsShown, "closing the top of a layer did not show the one beneath");
            h.Push(typeof(Privacy), null);
            h.Push(typeof(Dialog), "again");
            ok &= Check((string)s[typeof(Dialog)].Args == "again" && s[typeof(Dialog)].IsShown, "the modal pushed again was not shown with its new arguments");
            h.Push(typeof(Settings), null);
            int calls = ScreenCalls(s);
            h.Close(typeof(Privacy));
            ok &= Check(ScreenCalls(s) == calls && s[typeof(Settings)].IsShown, "closing a buried entry showed or hid a screen");
            h.ReplaceRoot(typeof(World), null);
            ok &= Check(s[typeof(World)].IsShown && !s[typeof(Hud)].IsShown && !s[typeof(Settings)].IsShown && !s[typeof(Dialog)].IsShown, "ReplaceRoot left another screen shown");
            h.Unregister(typeof(World));
            return ok & Check(h.Depth == 0 && h.Current.Value.Screen == null, "a removed screen stayed in the history");
        }

        private static int ScreenCalls(Dictionary<Type, FakeScreen> screens)
        {
            int calls = 0;
            foreach (var screen in screens.Values) calls += screen.Shows + screen.Hides;
            return calls;
        }

        private static bool StepsRemovedDuringAPress()
        {
            var chain = new BackChain(null, () => 0, null, null);
            var ran = new List<string>();
            IDisposable a = null, c = null;
            a = chain.AddStep(() => { ran.Add("a"); return false; }, Nexus.Core.Services.BackOrder.BeforeScreens);
            chain.AddStep(() => { ran.Add("b"); return false; }, Nexus.Core.Services.BackOrder.BeforeScreens);
            c = chain.AddStep(() => { ran.Add("c"); c.Dispose(); a.Dispose(); return false; }, Nexus.Core.Services.BackOrder.BeforeScreens);
            chain.Press();
            bool ok = Check(string.Join(",", ran) == "c,b", "steps ran as " + string.Join(",", ran));
            ran.Clear();
            chain.Press();
            return ok & Check(string.Join(",", ran) == "b", "after removal steps ran as " + string.Join(",", ran));
        }

        // Two Roots, the child's chain a BeforeScreens step of the parent's (the router pairs them so): a press reaches one
        // handler, the child's screens before the parent's, and only the parent's root behaviour runs.
        private static bool ChildChain()
        {
            var (app, a) = NewHistory(typeof(World), typeof(Settings));
            var (shop, s) = NewHistory(typeof(Camp), typeof(Dialog));
            int appRoot = 0, shopRoot = 0;
            var parent = new BackChain(app.Back, () => 0, () => appRoot++, null) { AtRoot = RootBack.MoveTaskToBack };
            var child = new BackChain(shop.Back, () => 0, () => shopRoot++, null) { AtRoot = RootBack.MoveTaskToBack };
            parent.AddStep(child.PressBelowRoot, Nexus.Core.Services.BackOrder.BeforeScreens);
            app.ReplaceRoot(typeof(World), null);
            app.Push(typeof(Settings), null);
            shop.ReplaceRoot(typeof(Camp), null);
            shop.Push(typeof(Dialog), null);
            bool ok = Check(parent.Press() && shop.Depth == 1 && app.Depth == 2, "the first press did not pop only the child's dialog");
            ok &= Check(parent.Press() && shop.Depth == 1 && app.Depth == 1 && s[typeof(Camp)].Backs == 1, "with the child at its root, the press did not reach the parent's screen");
            ok &= Check(parent.Press() && appRoot == 1, "nothing handled the press and the parent's root behaviour did not run");
            return ok & Check(shopRoot == 0 && a[typeof(World)].Backs == 1, "the child's root behaviour ran");
        }

        private static bool ReentrantShow()
        {
            var (h, s) = NewHistory(typeof(World), typeof(Settings), typeof(Privacy));
            h.ReplaceRoot(typeof(World), null);
            int seenDepth = -1, nested = 0;
            s[typeof(Settings)].Shown = () =>
            {
                if (seenDepth >= 0) return;
                seenDepth = h.Depth;
                int calls = ScreenCalls(s);
                h.Push(typeof(Privacy), null);
                nested = ScreenCalls(s) - calls;
            };
            h.Push(typeof(Settings), null);
            return Check(seenDepth == 2, "the screen was shown before the history held it")
                & Check(nested == 0, nested + " screen call(s) ran inside another screen's Show")
                & Check(h.Depth == 3 && h.Current.Value.Screen == typeof(Privacy), "the push from the show handler was lost")
                & Check(s[typeof(Privacy)].IsShown && !s[typeof(Settings)].IsShown && !s[typeof(World)].IsShown, "the screens shown do not match the history")
                & Check(h.Back() == BackResult.Popped && s[typeof(Settings)].IsShown && !s[typeof(Privacy)].IsShown, "Back after the re-entrant push did not show Settings again");
        }

        // ScreenStack's calls: a sync one changes the history then drops the transitions; an async one drops, changes and
        // takes them. World's hide starts before Settings' show handler pushes Privacy; the outer push waits for it.
        private static bool NestedCallTransitions()
        {
            bool ok = true;
            foreach (bool nestedAsync in new[] { false, true })
            {
                var (h, s) = NewHistory(typeof(World), typeof(Settings), typeof(Privacy));
                h.ReplaceRoot(typeof(World), null);
                var worldHidden = s[typeof(World)].HideTransition = new TaskCompletionSource<bool>();
                var privacyShown = s[typeof(Privacy)].ShowTransition = new TaskCompletionSource<bool>();
                bool nestedComplete = false;
                s[typeof(Settings)].Shown = () =>
                {
                    if (s[typeof(Settings)].Shows > 1) return;
                    h.DropTransitions();
                    h.Push(typeof(Privacy), null);
                    var nested = h.TakeTransitions();
                    if (!nestedAsync) h.DropTransitions();
                    nestedComplete = nested.IsCompleted;
                };
                h.DropTransitions();
                h.Push(typeof(Settings), null);
                var outer = h.TakeTransitions();
                string call = nestedAsync ? "an async" : "a sync";
                ok &= Check(nestedComplete, call + " call from the handler did not get a completed task");
                privacyShown.SetResult(true);
                ok &= Check(!outer.IsCompleted, "with " + call + " call from the handler, the outer task did not wait for World's hide");
                worldHidden.SetResult(true);
                ok &= Check(outer.IsCompleted, "with " + call + " call from the handler, the outer task did not complete with both transitions");
            }
            return ok;
        }
    }
}
