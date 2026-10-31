using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Nexus.Tests
{
    /// <summary>
    /// Issue #76 (ADR-0003): the UI Toolkit screen stack on the engine. Screens register with their own context's stack
    /// when they bind, show and hide by display and class, wait for their transitions, and one back press has one handler
    /// across a parent and a child context.
    /// </summary>
    [TestFixture]
    [Category("RequiresPlayMode")]
    public sealed class ScreenStackTests
    {
        public abstract class CountingScreen : ToolkitScreen
        {
            public int Backs;

            protected override bool OnBack()
            {
                Backs++;
                return false;
            }
        }

        public sealed class MenuScreen : CountingScreen { }
        public sealed class SettingsScreen : CountingScreen { }
        public sealed class ShopScreen : CountingScreen { }
        public sealed class ShopDetailScreen : CountingScreen { }

        /// <summary>Gigawatt's shape: the screen is a subtree of a document on another GameObject.</summary>
        public sealed class SubtreeScreen : CountingScreen
        {
            public UIDocument Document;

            protected override VisualElement ResolveRoot() => Document != null ? Document.rootVisualElement : null;
        }

        internal sealed class Lifecycle : IContextLifecycle
        {
            private readonly Action<IContextBuilder> _configure;

            public Lifecycle(Action<IContextBuilder> configure) => _configure = configure;

            public void OnConfigure(IContextBuilder builder) => _configure(builder);
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        private PanelSettings _panel;

        [SetUp]
        public void SetUp()
        {
            Root.ClearRegistry();
            _panel = ScriptableObject.CreateInstance<PanelSettings>();
        }

        [TearDown]
        public void TearDown()
        {
            Cleanup();
            UnityEngine.Object.DestroyImmediate(_panel);
        }

        internal static void Cleanup()
        {
            foreach (var root in UnityEngine.Object.FindObjectsByType<Root>(FindObjectsInactive.Include))
                UnityEngine.Object.DestroyImmediate(root.gameObject);
            Root.ClearRegistry();
        }

        internal static Root CreateRoot(string scopeTag, Root parent, Action<IContextBuilder> configure)
        {
            var go = new GameObject(scopeTag);
            go.SetActive(false);
            var root = go.AddComponent<Root>();
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.ScopeTag = scopeTag;
            data.AssemblyScopes = Array.Empty<string>();
            root.SetUp(data, parent);
            root.RegisterLifecycle(new Lifecycle(configure));
            go.SetActive(true);
            return root;
        }

        internal static void BindStack(IContextBuilder builder) => builder.BindServiceInterfacesAndSelfTo<ScreenStack>();

        internal static T AddScreen<T>(Root root, PanelSettings panel) where T : ToolkitScreen
        {
            var go = new GameObject(typeof(T).Name);
            go.SetActive(false);
            go.transform.SetParent(root.transform);
            go.AddComponent<UIDocument>().panelSettings = panel;
            var screen = go.AddComponent<T>();
            go.SetActive(true);
            return screen;
        }

        internal static IEnumerator WaitUntil(Func<bool> condition, string what)
        {
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(condition(), Is.True, what);
        }

        private static bool Displayed(ToolkitScreen screen) =>
            screen.Root.style.display.value == DisplayStyle.Flex && screen.Root.ClassListContains(ToolkitScreen.ShownClass);

        private static bool Hidden(ToolkitScreen screen) =>
            screen.Root.style.display.value == DisplayStyle.None && !screen.Root.ClassListContains(ToolkitScreen.ShownClass);

        private static void SetTransition(ToolkitScreen screen, int milliseconds)
        {
            screen.Root.style.transitionProperty = new List<StylePropertyName> { new StylePropertyName("opacity") };
            screen.Root.style.transitionDuration = new List<TimeValue> { new TimeValue(milliseconds, TimeUnit.Millisecond) };
        }

        [UnityTest]
        public IEnumerator AScreen_IsShownByDisplayAndClass_ExactlyWhileItHoldsTheTopOfItsLayer()
        {
            var app = CreateRoot("App", null, BindStack);
            yield return WaitUntil(() => app.IsInitialized, "the context did not start");
            var menu = AddScreen<MenuScreen>(app, _panel);
            var settings = AddScreen<SettingsScreen>(app, _panel);
            var stack = app.Context.Container.Resolve<IScreenStack>();
            Assert.That((Hidden(menu), Hidden(settings)), Is.EqualTo((true, true)), "a screen outside the history is hidden");

            stack.ReplaceRoot<MenuScreen>();
            stack.Push<SettingsScreen>("audio");
            Assert.That((Hidden(menu), Displayed(settings), settings.Args, stack.Depth), Is.EqualTo((true, true, (object)"audio", 2)));

            Assert.That(stack.Back(), Is.EqualTo(BackResult.Popped));
            Assert.That((Displayed(menu), Hidden(settings)), Is.EqualTo((true, true)));

            stack.Close<MenuScreen>();
            Assert.That((Hidden(menu), stack.Depth), Is.EqualTo((true, 0)));
        }

        [UnityTest]
        public IEnumerator TheBackKeyDriver_IsDestroyedWithItsContext()
        {
            var app = CreateRoot("App", null, BindStack);
            yield return WaitUntil(() => app.IsInitialized, "the context did not start");
            Assert.That(GameObject.Find("[Nexus_BackKey]"), Is.Not.Null);

            UnityEngine.Object.DestroyImmediate(app.gameObject);
            yield return null;
            Assert.That(GameObject.Find("[Nexus_BackKey]"), Is.Null, "the driver outlived its context and keeps polling");
        }

        [UnityTest]
        public IEnumerator AChildContextsScreens_TakeAPressBeforeTheParents_AndOnlyTheTopRouterHasARootBehaviour()
        {
            var app = CreateRoot("App", null, BindStack);
            var shop = CreateRoot("Shop", app, BindStack);
            yield return WaitUntil(() => app.IsInitialized && shop.IsInitialized, "the two contexts did not start");
            var menu = AddScreen<MenuScreen>(app, _panel);
            var settings = AddScreen<SettingsScreen>(app, _panel);
            var list = AddScreen<ShopScreen>(shop, _panel);
            var detail = AddScreen<ShopDetailScreen>(shop, _panel);
            var appStack = app.Context.Container.Resolve<ScreenStack>();
            var shopStack = shop.Context.Container.Resolve<ScreenStack>();

            Assert.That((appStack.ReadsBackKey, shopStack.ReadsBackKey), Is.EqualTo((true, false)));
            Assert.Throws<InvalidOperationException>(() => appStack.Push<ShopScreen>(), "a screen registers with its own context's stack only");

            appStack.ReplaceRoot<MenuScreen>();
            appStack.Push<SettingsScreen>();
            shopStack.ReplaceRoot<ShopScreen>();
            shopStack.Push<ShopDetailScreen>();
            int appHints = 0, shopHints = 0;
            appStack.AtRoot = RootBack.PressAgainToQuit;
            appStack.PressAgainHintRequested += () => appHints++;
            shopStack.AtRoot = RootBack.PressAgainToQuit;
            shopStack.PressAgainHintRequested += () => shopHints++;
            IBackRouter router = appStack;

            Assert.That(router.Press(), Is.True);
            Assert.That((shopStack.Depth, appStack.Depth, detail.Backs, settings.Backs), Is.EqualTo((1, 2, 1, 0)), "the child's top screen takes the first press");

            Assert.That(router.Press(), Is.True);
            Assert.That((shopStack.Depth, appStack.Depth, list.Backs, settings.Backs), Is.EqualTo((1, 1, 1, 1)), "the child at its root passes the press to the parent's screens");

            Assert.That(router.Press(), Is.True);
            Assert.That((menu.Backs, appHints, shopHints), Is.EqualTo((1, 1, 0)), "only the router that reads the key runs its root behaviour");
        }

        [UnityTest]
        public IEnumerator ASecondStackInAnUnrelatedContext_LogsAnError_AndDoesNotReadTheKey()
        {
            LogAssert.Expect(LogType.Error, new Regex(@"^\[Nexus\] ScreenStack of context '(A|B)' does not read the back key"));
            var a = CreateRoot("A", null, BindStack);
            var b = CreateRoot("B", null, BindStack);
            yield return WaitUntil(() => a.IsInitialized && b.IsInitialized, "the two contexts did not start");

            bool aReads = a.Context.Container.Resolve<ScreenStack>().ReadsBackKey;
            bool bReads = b.Context.Container.Resolve<ScreenStack>().ReadsBackKey;
            Assert.That(aReads != bReads, Is.True, "exactly one of the two stacks reads the key");
        }

        [UnityTest]
        public IEnumerator ATransition_DelaysTheTask_AndAHideKeepsTheRootDisplayedUntilItEnds()
        {
            var app = CreateRoot("App", null, BindStack);
            yield return WaitUntil(() => app.IsInitialized, "the context did not start");
            AddScreen<MenuScreen>(app, _panel);
            var faded = AddScreen<SettingsScreen>(app, _panel);
            SetTransition(faded, 300);
            yield return null; // the panel resolves the inline transition
            yield return null;
            var stack = app.Context.Container.Resolve<IScreenStack>();
            stack.ReplaceRoot<MenuScreen>();

            float start = Time.realtimeSinceStartup;
            var push = stack.PushAsync<SettingsScreen>().AsTask();
            Assert.That((push.IsCompleted, faded.Root.style.display.value), Is.EqualTo((false, DisplayStyle.Flex)));
            yield return WaitUntil(() => push.IsCompleted, "the push did not finish in 5 s");
            Assert.That(Time.realtimeSinceStartup - start, Is.GreaterThanOrEqualTo(0.25f), "the task completed before the transition ended");
            Assert.That(Displayed(faded), Is.True);

            var back = stack.BackAsync().AsTask();
            Assert.That((back.IsCompleted, faded.Root.style.display.value, faded.Root.ClassListContains(ToolkitScreen.ShownClass)),
                Is.EqualTo((false, DisplayStyle.Flex, false)), "the hide animates before display: none");
            yield return WaitUntil(() => back.IsCompleted, "the back did not finish in 5 s");
            Assert.That((back.Result, Hidden(faded)), Is.EqualTo((BackResult.Popped, true)));
        }

        [UnityTest]
        public IEnumerator WithoutATransition_ShownAgainWithOtherArgs_OrUnderReducedMotion_TheTaskIsCompleteAtOnce()
        {
            var app = CreateRoot("App", null, BindStack);
            yield return WaitUntil(() => app.IsInitialized, "the context did not start");
            AddScreen<MenuScreen>(app, _panel);
            var plain = AddScreen<SettingsScreen>(app, _panel);
            var faded = AddScreen<ShopScreen>(app, _panel);
            SetTransition(faded, 300);
            yield return null;
            yield return null;
            var stack = app.Context.Container.Resolve<IScreenStack>();
            stack.ReplaceRoot<MenuScreen>();

            Assert.That(stack.PushAsync<SettingsScreen>().IsCompleted, Is.True, "a root without a transition");
            Assert.That(Displayed(plain), Is.True);

            var first = stack.PushAsync<ShopScreen>("first").AsTask();
            yield return WaitUntil(() => first.IsCompleted, "the push did not finish in 5 s");
            Assert.That((stack.PushAsync<ShopScreen>("second").IsCompleted, faded.Args), Is.EqualTo((true, (object)"second")),
                "the screen on top, shown again with other arguments");

            stack.ReducedMotion = true;
            Assert.That(stack.BackAsync().IsCompleted, Is.True, "reduced motion");
            Assert.That(Hidden(faded), Is.True);
        }

        [UnityTest]
        public IEnumerator ADeactivatedScreen_LeavesItsContextsHistory_AndComesBackHidden()
        {
            var app = CreateRoot("App", null, BindStack);
            yield return WaitUntil(() => app.IsInitialized, "the context did not start");
            var menu = AddScreen<MenuScreen>(app, _panel);
            var settings = AddScreen<SettingsScreen>(app, _panel);
            var stack = app.Context.Container.Resolve<IScreenStack>();
            stack.ReplaceRoot<MenuScreen>();
            stack.Push<SettingsScreen>();

            settings.gameObject.SetActive(false);
            Assert.That((stack.Depth, stack.Current.Value.Screen, Displayed(menu)), Is.EqualTo((1, typeof(MenuScreen), true)));

            settings.gameObject.SetActive(true);
            yield return null;
            Assert.That((stack.Depth, Hidden(settings)), Is.EqualTo((1, true)), "back in its context, outside the history");
            stack.Push<SettingsScreen>();
            Assert.That(Displayed(settings), Is.True, "registered again");
        }

        [UnityTest]
        public IEnumerator AScreenWhoseComponentIsDisabled_LeavesTheHistory_AndItsDocumentIsHidden()
        {
            var app = CreateRoot("App", null, BindStack);
            yield return WaitUntil(() => app.IsInitialized, "the context did not start");
            var menu = AddScreen<MenuScreen>(app, _panel);
            var settings = AddScreen<SettingsScreen>(app, _panel);
            var stack = app.Context.Container.Resolve<IScreenStack>();
            stack.ReplaceRoot<MenuScreen>();
            stack.Push<SettingsScreen>();

            settings.enabled = false;
            Assert.That((stack.Depth, Displayed(menu), Hidden(settings)), Is.EqualTo((1, true, true)));
        }

        [UnityTest]
        public IEnumerator AScreenWhoseDocumentIsEnabledAfterItBinds_IsHiddenOnTheNextFrame()
        {
            var app = CreateRoot("App", null, BindStack);
            yield return WaitUntil(() => app.IsInitialized, "the context did not start");
            var documentObject = new GameObject("Document");
            documentObject.SetActive(false);
            var document = documentObject.AddComponent<UIDocument>();
            document.panelSettings = _panel;
            var go = new GameObject(nameof(SubtreeScreen));
            go.SetActive(false);
            go.transform.SetParent(app.transform);
            var screen = go.AddComponent<SubtreeScreen>();
            screen.Document = document;
            go.SetActive(true); // binds while its document is not enabled yet
            documentObject.SetActive(true);
            yield return null;
            Assert.That(Hidden(screen), Is.True);
            UnityEngine.Object.DestroyImmediate(documentObject);
        }
    }

    /// <summary>
    /// Issue #76 (ADR-0003): the back key reaches the stack through its own driver, so it still closes what a paused game
    /// shows: <see cref="TickService"/> stops ticking while paused.
    /// </summary>
    [TestFixture]
    [Category("RequiresPlayMode")]
    public sealed class ScreenStackBackKeyTests : InputTestFixture
    {
        private PanelSettings _panel;

        public override void Setup()
        {
            base.Setup();
            Root.ClearRegistry();
            _panel = ScriptableObject.CreateInstance<PanelSettings>();
        }

        public override void TearDown()
        {
            ScreenStackTests.Cleanup();
            UnityEngine.Object.DestroyImmediate(_panel);
            base.TearDown();
        }

        [UnityTest]
        public IEnumerator TheBackKey_ClosesTheTopScreenOnce_WhileTheTickServiceIsPaused()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            var app = ScreenStackTests.CreateRoot("App", null, builder =>
            {
                builder.BindService<ITickService, TickService>();
                builder.BindService<IInputService, InputService>();
                ScreenStackTests.BindStack(builder);
            });
            yield return ScreenStackTests.WaitUntil(() => app.IsInitialized, "the context did not start");
            ScreenStackTests.AddScreen<ScreenStackTests.MenuScreen>(app, _panel);
            var settings = ScreenStackTests.AddScreen<ScreenStackTests.SettingsScreen>(app, _panel);
            var stack = app.Context.Container.Resolve<IScreenStack>();
            stack.ReplaceRoot<ScreenStackTests.MenuScreen>();
            stack.Push<ScreenStackTests.SettingsScreen>();
            app.Context.Container.Resolve<ITickService>().IsPaused = true;

            Press(keyboard.escapeKey);
            yield return null;
            yield return null;
            Release(keyboard.escapeKey);
            yield return null;
            Assert.That((settings.Backs, stack.Depth), Is.EqualTo((1, 1)));
        }
    }
}
