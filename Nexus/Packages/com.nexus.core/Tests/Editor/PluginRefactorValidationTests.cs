using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Nexus.Editor;
using UnityEngine.UIElements;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class PluginRefactorValidationTests
    {
        [Test]
        public void EveryTabTheEditorJumpsTo_IsATabOfTheWindow()
        {
            // the Dashboard and Game Manager "Context Wizard" buttons switched to a hidden plugin and did nothing
            var shown = typeof(NexusWindow).Assembly.GetTypes()
                .Where(t => typeof(INexusEditorPlugin).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
                .Select(t => ((INexusEditorPlugin)Activator.CreateInstance(t)).Id)
                .ToList();
            string editorRoot = Path.Combine(UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(NexusWindow).Assembly).resolvedPath, "Editor");
            var targets = Directory.GetFiles(editorRoot, "*.cs", SearchOption.AllDirectories)
                .SelectMany(file =>
                {
                    string text = File.ReadAllText(file);
                    return Regex.Matches(text, @"(?:SwitchToPlugin|NexusWindow\.Open)\(""(\w+)""\)").Cast<Match>()
                        .Select(m => (id: m.Groups[1].Value, file: Path.GetFileName(file)));
                })
                .ToList();
            Assert.IsNotEmpty(targets, "the scan found the jump buttons");
            foreach (var (id, file) in targets)
                CollectionAssert.Contains(shown, id, file + " switches to '" + id + "', which is not a tab of the window");
        }

        [Test]
        public void Tracer_ListView_ConfiguredWithItemHeight()
        {
            var plugin = new TracerPlugin();
            var view = plugin.CreateView();
            var listView = view.Q<ListView>();
            Assert.IsNotNull(listView, "TracerPlugin must contain a ListView for virtualized logs.");
            Assert.AreEqual(28f, listView.fixedItemHeight, "ListView fixedItemHeight should be 28 for safe padding.");
        }

        [Test]
        public void Dashboard_BuildReadiness_ReportsTheBinderStateTheGeneratorReports()
        {
            var plugin = new DashboardPlugin();
            var view = plugin.CreateView();
            var pill = view.Q<VisualElement>("readiness-binder");
            Assert.IsNotNull(pill, "the build-readiness card must show the AOT binder row");
            var text = (pill as Label)?.text;
            Assert.IsNotNull(text, "the binder row's value is a label");
            if (NexusCodeGenerator.BinderExists())
                Assert.AreNotEqual(NexusLang.Get("db_rd_binder_missing"), text, "a generated binder exists, the row must not say it is missing");
            else
                Assert.AreEqual(NexusLang.Get("db_rd_binder_missing"), text);
            Assert.IsNotNull(view.Q<VisualElement>("readiness-defines"), "scripting defines of the active target are shown");
            plugin.OnDisable();
        }
    }
}
