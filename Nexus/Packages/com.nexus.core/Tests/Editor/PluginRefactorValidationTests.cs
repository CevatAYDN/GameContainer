using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Nexus.Core;
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
        public void ContextTree_ListsEachChildUnderItsParent_AndAContextWhoseParentIsNotRunning_AtTheTop()
        {
            // the Hierarchy tab dropped a context whose parent was not running: it waited under a parent it never drew
            using var root = new Context();
            using var child = new Context(root);
            using var stoppedParent = new Context();
            using var orphan = new Context(stoppedParent);

            var tree = ContextInspectorPlugin.BuildTree(new IContext[] { child, orphan, root });

            CollectionAssert.AreEqual(new (IContext, int)[] { (orphan, 0), (root, 0), (child, 1) },
                tree.Select(e => (e.Context, e.Depth)).ToArray());
        }
    }
}
