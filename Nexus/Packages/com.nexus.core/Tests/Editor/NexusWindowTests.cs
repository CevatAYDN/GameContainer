using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// The window is made with <c>CreateInstance</c> and its <c>CreateGUI</c> is called directly, so no window opens
    /// on the desktop. A console error a tab logs fails the test.
    /// </summary>
    [TestFixture]
    public class NexusWindowTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private NexusWindow _window;

        [SetUp]
        public void SetUp() => _window = ScriptableObject.CreateInstance<NexusWindow>();

        [TearDown]
        public void TearDown()
        {
            if (_window != null) Object.DestroyImmediate(_window);
        }

        private T Field<T>(string name) => (T)typeof(NexusWindow).GetField(name, Private).GetValue(_window);

        private void CreateGUI() => typeof(NexusWindow).GetMethod("CreateGUI", Private).Invoke(_window, null);

        private List<Button> TabButtons() =>
            Field<VisualElement>("_sidebar").Query<Button>(className: NexusEditorStyles.ClassSidebarBtn).ToList();

        [Test]
        public void Window_OpensOnTheFirstTabOfTheFirstSection_AndShowsItsView()
        {
            // the window opened with an empty right pane: the default tab was already "active", so the switch returned early
            CreateGUI();

            var first = TabButtons().First();
            Assert.AreEqual("Tab_Status", first.name);
            Assert.IsTrue(first.ClassListContains("active"), "the first tab is marked open");
            Assert.AreEqual(1, Field<VisualElement>("_contentArea").childCount, "the first tab's view is shown");
        }

        [Test]
        public void Sidebar_ListsTheSectionsInTheirOrder_AndTheShortcutsFollowIt()
        {
            CreateGUI();

            var headers = Field<VisualElement>("_sidebar").Query<Label>(className: "nexus-category-header").ToList();
            CollectionAssert.AreEqual(NexusWindow.Sections.Select(key => NexusLang.Get(key).ToUpper()).ToArray(),
                headers.Select(h => h.text).ToArray(), "every tab sits in one of the sections, in their order");
            // Ctrl+1..9 index _plugins, so it lists the tabs as the sidebar does
            var plugins = Field<List<INexusEditorPlugin>>("_plugins");
            CollectionAssert.AreEqual(plugins.Select(p => "Tab_" + p.Id).ToArray(), TabButtons().Select(b => b.name).ToArray());
        }

        [Test]
        public void EveryTab_BuildsItsView_EachTimeItIsOpened()
        {
            // the second round opens each tab after its OnDisable ran, the order a play-mode change or a language switch uses
            CreateGUI();
            var plugins = Field<List<INexusEditorPlugin>>("_plugins");
            var content = Field<VisualElement>("_contentArea");

            for (int round = 0; round < 2; round++)
            {
                foreach (var plugin in plugins)
                {
                    _window.SwitchToPlugin(plugin.Id);
                    Assert.AreSame(plugin, Field<INexusEditorPlugin>("_activePlugin"), plugin.Id);
                    Assert.AreEqual(1, content.childCount, plugin.Id + " shows its view");
                    plugin.OnUpdate();
                }
            }
        }

        [Test]
        public void TabLifecycle_EnablesOnlyTheShownTab_AndDisablesEveryViewItBuilt()
        {
            // a rebuilt view skipped OnDisable on the old one, and every tab was enabled while one was shown
            var log = new List<string>();
            var plugins = Field<List<INexusEditorPlugin>>("_plugins");
            plugins.Clear();
            plugins.Add(new RecordingPlugin("A", log));
            plugins.Add(new RecordingPlugin("B", log));

            _window.SwitchToPlugin("B"); // before the window has its UI, as Open("B") on a new window
            Assert.IsEmpty(log);
            CreateGUI();
            _window.SwitchToPlugin("A");
            CreateGUI(); // a language switch rebuilds the window
            typeof(NexusWindow).GetMethod("RefreshActivePlugin", Private).Invoke(_window, null); // a play-mode change
            Object.DestroyImmediate(_window);
            _window = null;

            CollectionAssert.AreEqual(new[]
            {
                "B.enable", "B.view", "B.disable",
                "A.enable", "A.view", "A.disable",
                "A.enable", "A.view", "A.disable",
                "A.enable", "A.view", "A.disable",
            }, log);
        }

        [Test]
        public void EveryMenuItemOfThePackages_IsInTheNexusMenu()
        {
            // the owner's decision of 2026-10-31 (#193): one Nexus menu and the GameObject > Nexus create items
            var paths = TypeCache.GetMethodsWithAttribute<MenuItem>()
                .Where(m => m.DeclaringType.Assembly.GetName().Name.StartsWith("com.nexus.")
                            || m.DeclaringType.Assembly.GetName().Name.StartsWith("Nexus."))
                .SelectMany(m => m.GetCustomAttributes<MenuItem>())
                .Select(a => a.menuItem)
                .ToList();

            Assert.IsNotEmpty(paths);
            foreach (var path in paths)
                Assert.IsTrue(path.StartsWith("Nexus/") || path.StartsWith("GameObject/Nexus/"), path + " is outside the Nexus menu");
        }

        // No parameterless constructor, so plugin discovery never adds it to a real window.
        private sealed class RecordingPlugin : NexusEditorPlugin
        {
            private readonly List<string> _log;

            public RecordingPlugin(string id, List<string> log)
            {
                Id = id;
                _log = log;
            }

            public override string Id { get; }
            public override string DisplayName => Id;
            public override int Order => 0;
            public override string Category => "cat_status";
            public override void OnEnable() => _log.Add(Id + ".enable");
            public override void OnDisable() => _log.Add(Id + ".disable");

            public override VisualElement CreateView()
            {
                _log.Add(Id + ".view");
                return new VisualElement();
            }
        }
    }
}
