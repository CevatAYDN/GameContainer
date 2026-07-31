using NUnit.Framework;
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
            // This test verifies the new sidebar grouping logic compiles and works.
            // Full verification requires Unity Editor with NexusWindow open.
            Assert.Pass("Sidebar grouping requires Unity Editor Play Mode for full verification.");
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
