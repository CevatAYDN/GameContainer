using System.IO;
using System.Linq;
using Nexus.Core;
using NUnit.Framework;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// The generated binder used to name every class of every assembly the name policy did not recognise, so a precompiled
    /// SDK (an ads plugin, an editor-only resolver DLL) put unreferenceable types into it and the project stopped compiling.
    /// The binder scans only assemblies that use Nexus and that its own assembly can reference; link.xml keeps every
    /// Nexus-referencing assembly. The build guard's dry run must never write.
    /// </summary>
    public sealed class BinderScopeTests
    {
        private static string NexusRuntime => typeof(NexusDI).Assembly.GetName().Name;

        [Test]
        public void NexusReferencingAssembliesContainTheRuntimeAndOnlyAssembliesThatUseIt()
        {
            var assemblies = AssemblyCatalog.NexusReferencingAssemblies().ToList();
            Assert.That(assemblies.Select(AssemblyCatalog.GetSimpleName), Does.Contain(NexusRuntime));
            foreach (var assembly in assemblies)
            {
                string name = AssemblyCatalog.GetSimpleName(assembly);
                bool usesNexus = name == NexusRuntime || assembly.GetReferencedAssemblies().Any(r => r.Name == NexusRuntime);
                Assert.That(usesNexus, Is.True, name + " does not reference Nexus but would be preserved in link.xml");
                Assert.That(AssemblyCatalog.IsEditorAssembly(name), Is.False, name);
                Assert.That(AssemblyCatalog.IsTestAssembly(name), Is.False, name);
            }
        }

        [Test]
        public void BinderAssembliesAreASubsetOfNexusReferencingAssemblies()
        {
            string binderFolder = NexusEditorSettings.GetOrCreateSettings().BinderOutputPath;
            var universe = AssemblyCatalog.NexusReferencingAssemblies().ToHashSet();
            var binder = AssemblyCatalog.BinderAssemblies(binderFolder).ToList();
            Assert.That(binder, Is.Not.Empty);
            Assert.That(binder.Select(AssemblyCatalog.GetSimpleName), Does.Contain(NexusRuntime));
            Assert.That(binder.All(universe.Contains), Is.True, "the binder may only name assemblies link.xml also preserves");
        }

        [Test]
        public void IsBinderCurrentWritesNothing()
        {
            var settings = NexusEditorSettings.GetOrCreateSettings();
            string binderFile = Path.Combine(settings.BinderOutputPath, "NexusGeneratedBinder.g.cs");
            string linkFile = Path.Combine(settings.LinkXmlOutputPath, "link.xml");
            string binderBefore = File.Exists(binderFile) ? File.ReadAllText(binderFile) : null;
            string linkBefore = File.Exists(linkFile) ? File.ReadAllText(linkFile) : null;

            NexusCodeGenerator.IsBinderCurrent();

            Assert.That(File.Exists(binderFile) ? File.ReadAllText(binderFile) : null, Is.EqualTo(binderBefore));
            Assert.That(File.Exists(linkFile) ? File.ReadAllText(linkFile) : null, Is.EqualTo(linkBefore));
        }
    }
}
