using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// Editor-side architectural-deepening tests (Phase 6: INexusEditorPlugin).
    /// Lives in the editor test assembly because it depends on the editor-only
    /// <see cref="NexusEditorPlugin"/> base class.
    /// </summary>
    [TestFixture]
    public class ArchitecturalDeepeningEditorTests
    {
        [Test]
        public void NexusEditorPlugin_DefaultCategory_IsCatOther()
        {
            // The base class should return "cat_other" as the default category.
            var plugin = new TestEditorPlugin();
            Assert.AreEqual("cat_other", plugin.Category);
            Assert.AreEqual(new Color(0.6f, 0.6f, 0.6f), plugin.IconColor);
        }

        [Test]
        public void NexusEditorPlugin_CustomCategory_OverridesDefault()
        {
            var plugin = new CustomCategoryPlugin();
            Assert.AreEqual("cat_diagnostics", plugin.Category);
            Assert.AreEqual(new Color(1f, 0.3f, 0.3f), plugin.IconColor);
        }

        [Test]
        public void NexusWindow_SidebarGroupsByCategory()
        {
            var window = ScriptableObject.CreateInstance<NexusWindow>();
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(NexusWindow).GetMethod("CreateGUI", flags).Invoke(window, null);
                var plugins = (List<INexusEditorPlugin>)typeof(NexusWindow).GetField("_plugins", flags).GetValue(window);
                Assert.IsNotEmpty(plugins);
                var groups = plugins.GroupBy(p => p.Category).OrderBy(g => g.Min(p => p.Order)).ToArray();
                var sidebar = (VisualElement)typeof(NexusWindow).GetField("_sidebar", flags).GetValue(window);
                var headers = sidebar.Query<Label>(className: "nexus-category-header").ToList();
                CollectionAssert.AreEqual(groups.Select(g => NexusLang.Get(g.Key).ToUpper()).ToArray(), headers.Select(h => h.text).ToArray());
                var expectedButtons = groups.SelectMany(g => g.OrderBy(p => p.Order)).Select(p => "Tab_" + p.Id).ToArray();
                var buttons = sidebar.Query<Button>(className: "nexus-sidebar-btn").ToList();
                CollectionAssert.AreEqual(expectedButtons, buttons.Select(b => b.name).ToArray());
            }
            finally { Object.DestroyImmediate(window); }
        }
    }

    // ─── Test editor plugins ───

    public class TestEditorPlugin : NexusEditorPlugin
    {
        public override string Id => "TestPlugin";
        public override string DisplayName => "Test Plugin";
        public override int Order => 999;
        public override VisualElement CreateView() => new();
    }

    public class CustomCategoryPlugin : NexusEditorPlugin
    {
        public override string Id => "CustomPlugin";
        public override string DisplayName => "Custom Plugin";
        public override int Order => 100;
        public override string Category => "cat_diagnostics";
        public override Color IconColor => new(1f, 0.3f, 0.3f);
        public override VisualElement CreateView() => new();
    }
}
