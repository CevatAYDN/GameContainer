using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Nexus.Core;
using Nexus.Core.Services;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Nexus.Tests
{
    /// <summary>
    /// Issue #270: the development menu. Its buttons and toggles run the game's delegates, a removed entry leaves it, a key or
    /// three fingers open and close it, and a release player gets none of it.
    /// </summary>
    public sealed class DebugMenuTests : InputTestFixture
    {
        private static readonly MethodInfo OverlayUpdate =
            typeof(DebugMenuOverlay).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance);

        private DebugMenu _menu;

        [TearDown]
        public void DisposeMenu()
        {
            _menu?.Dispose();
            _menu = null;
            NexusBuild.OverrideForTests(null);
        }

        private DebugMenu StartMenu()
        {
            _menu = new DebugMenu();
            _menu.InitializeAsync(default).GetAwaiter().GetResult();
            return _menu;
        }

        // The overlay reads the touches in its Update; called right after the input, which BeginTouch has already processed.
        private static void RunFrame(DebugMenu menu) => OverlayUpdate.Invoke(menu.Overlay, null);

        // In a [UnityTest] the fixture only queues a key event, so the press reaches the overlay in the next frame's Update.
        private IEnumerator OpenWithKey()
        {
            var keyboard = Keyboard.current ?? InputSystem.AddDevice<Keyboard>();
            Press(keyboard.backquoteKey);
            yield return null;
            Release(keyboard.backquoteKey);
        }

        private static T Find<T>(DebugMenu menu, Func<T, bool> match) where T : VisualElement =>
            menu.Overlay.Root.Query<T>().ToList().FirstOrDefault(match);

        private static void Submit(Button button)
        {
            using (var submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = button;
                button.SendEvent(submit);
            }
        }

        [UnityTest]
        public IEnumerator Backquote_OpensTheMenuOnItsTheme_AndAButtonRunsItsAction()
        {
            var menu = StartMenu();
            int ran = 0;
            menu.AddButton("Economy", "Give 1000 coins", () => ran++);

            yield return OpenWithKey();
            Assert.IsTrue(menu.Overlay.IsOpen);
            Assert.IsNotNull(menu.Overlay.Panel.themeStyleSheet, "Resources/NexusDebugMenu.tss did not load");
            yield return null;

            Submit(Find<Button>(menu, b => b.text == "Give 1000 coins"));
            Assert.AreEqual(1, ran);

            Submit(Find<Button>(menu, b => b.text == "Close"));
            Assert.IsFalse(menu.Overlay.IsOpen);
        }

        [UnityTest]
        public IEnumerator ThreeFingers_OpenAndCloseTheMenu_TwoDoNot()
        {
            var menu = StartMenu();
            InputSystem.AddDevice<Touchscreen>();

            BeginTouch(1, new Vector2(100, 100));
            BeginTouch(2, new Vector2(200, 100));
            RunFrame(menu);
            Assert.IsFalse(menu.Overlay.IsOpen, "two fingers opened the menu");

            BeginTouch(3, new Vector2(300, 100));
            RunFrame(menu);
            Assert.IsTrue(menu.Overlay.IsOpen);
            yield return null;
            RunFrame(menu);
            Assert.IsTrue(menu.Overlay.IsOpen, "fingers held on the screen toggled the menu again");

            for (int id = 1; id <= 3; id++) EndTouch(id, new Vector2(100 * id, 100));
            RunFrame(menu);
            for (int id = 4; id <= 6; id++) BeginTouch(id, new Vector2(100 * (id - 3), 200));
            RunFrame(menu);
            Assert.IsFalse(menu.Overlay.IsOpen);
        }

        [UnityTest]
        public IEnumerator AToggle_ShowsItsGetter_PassesTheChoiceToItsSetter_AndFollowsAButton()
        {
            var menu = StartMenu();
            bool godMode = false;
            menu.AddToggle("Cheats", "God mode", () => godMode, value => godMode = value);
            menu.AddButton("Cheats", "Mortal again", () => godMode = false);

            yield return OpenWithKey();
            var toggle = Find<Toggle>(menu, t => t.label == "God mode");
            Assert.IsFalse(toggle.value);

            toggle.value = true;
            Assert.IsTrue(godMode);

            Submit(Find<Button>(menu, b => b.text == "Mortal again"));
            Assert.IsFalse(toggle.value, "the toggle kept showing a state its getter no longer returns");
        }

        [UnityTest]
        public IEnumerator ARemovedEntry_LeavesTheMenu_WithItsEmptySection()
        {
            var menu = StartMenu();
            menu.AddButton("Economy", "Give 1000 coins", () => { });
            var win = menu.AddButton("Levels", "Win the level", () => { });

            yield return OpenWithKey();
            Assert.IsNotNull(Find<Button>(menu, b => b.text == "Win the level"));

            win.Dispose();
            yield return null; // the open menu draws again on its next frame

            Assert.IsNull(Find<Button>(menu, b => b.text == "Win the level"));
            Assert.IsNull(Find<Label>(menu, l => l.text == "Levels"), "the section of no entry stayed");
            Assert.IsNotNull(Find<Button>(menu, b => b.text == "Give 1000 coins"));
        }

        [UnityTest]
        public IEnumerator AThrowingButton_IsLogged_AndTheMenuStaysOpen()
        {
            NexusLog.Reset(); // no logger: the exception and the formatted line both reach the console (#314)
            var menu = StartMenu();
            menu.AddButton("Economy", "Broken", () => throw new InvalidOperationException("debug menu test"));

            yield return OpenWithKey();
            LogAssert.Expect(LogType.Exception, new Regex("debug menu test"));
            LogAssert.Expect(LogType.Error, new Regex(@"\[DebugMenu\]\[Broken\] debug menu test"));
            Submit(Find<Button>(menu, b => b.text == "Broken"));

            Assert.IsTrue(menu.Overlay.IsOpen);
        }

        [Test]
        public void AReleasePlayer_CreatesNothing_AndStoresNothing()
        {
            NexusBuild.OverrideForTests(false);
            int documents = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Include).Length;

            var menu = StartMenu();
            var handle = menu.AddButton("Economy", "Give 1000 coins", () => { });
            handle.Dispose();

            Assert.IsNull(menu.Overlay);
            Assert.IsNull(GameObject.Find("NexusDebugMenu"));
            Assert.AreEqual(0, menu.Entries.Count);
            Assert.AreEqual(documents, UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Include).Length);
        }
    }
}
