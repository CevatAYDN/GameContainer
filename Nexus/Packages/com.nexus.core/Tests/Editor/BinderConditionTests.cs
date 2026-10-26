using System.Collections.Generic;
using NUnit.Framework;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// Issue #85: the binder is generated from the Editor's assemblies, so a public type declared under
    /// <c>#if DEVELOPMENT_BUILD || UNITY_EDITOR</c> got a factory and an injector that a release player could not
    /// compile (CS0234). The binder repeats the type's own condition. Each test hands fake source files for the
    /// types below; the binder lives in another assembly called "Binder".
    /// </summary>
    public sealed class BinderConditionTests
    {
        public sealed class DevOnlyTool { }
        public sealed class SplitTool { }
        public sealed class EditorBranchTool { }
        public sealed class ProviderTool { }

        private static BinderConditions Read(Dictionary<string, string> files, string[] typeDefines, string[] binderDefines)
        {
            var assemblies = new Dictionary<string, (string[] Files, string[] Defines)>
            {
                [typeof(BinderConditionTests).Assembly.GetName().Name] = (new List<string>(files.Keys).ToArray(), typeDefines),
                ["Binder"] = (new string[0], binderDefines),
            };
            return new BinderConditions(assemblies, "Binder", path => files[path]);
        }

        private static BinderConditions Read(string source, params string[] defines)
            => Read(new Dictionary<string, string> { ["a.cs"] = source }, defines, defines);

        [Test]
        public void ADevelopmentOnlyTypeKeepsItsIfWhereverTheBinderNamesIt()
        {
            var conditions = Read(
                "namespace Nexus.Editor.Tests\n{\n#if DEVELOPMENT_BUILD || UNITY_EDITOR // tools\n    public sealed class DevOnlyTool { }\n#endif\n}\n",
                "UNITY_EDITOR");

            Assert.That(conditions.For(typeof(DevOnlyTool)), Is.EqualTo("DEVELOPMENT_BUILD || UNITY_EDITOR"));
            Assert.That(conditions.For(typeof(List<DevOnlyTool>)), Is.EqualTo("DEVELOPMENT_BUILD || UNITY_EDITOR"));
            Assert.That(conditions.For(typeof(DevOnlyTool), typeof(string), typeof(DevOnlyTool)), Is.EqualTo("DEVELOPMENT_BUILD || UNITY_EDITOR"));
        }

        [Test]
        public void APartialTypeWithAnUnconditionalPartIsNamedUnconditionally()
        {
            var conditions = Read(new Dictionary<string, string>
            {
                ["Split.cs"] = "namespace Nexus.Editor.Tests { public partial class SplitTool { } }",
                ["Split.Tools.cs"] = "#if UNITY_EDITOR\nnamespace Nexus.Editor.Tests { public partial class SplitTool { } }\n#endif",
            }, new[] { "UNITY_EDITOR" }, new[] { "UNITY_EDITOR" });

            Assert.That(conditions.For(typeof(SplitTool)), Is.Null);
        }

        [Test]
        public void AnElifBranchExcludesTheBranchesBeforeIt()
        {
            var conditions = Read(
                "namespace Nexus.Editor.Tests\n{\n#if DEVELOPMENT_BUILD\n    class Other { }\n#elif UNITY_EDITOR\n    public class EditorBranchTool { }\n#endif\n}\n",
                "UNITY_EDITOR");

            Assert.That(conditions.For(typeof(EditorBranchTool)), Is.EqualTo("!DEVELOPMENT_BUILD && UNITY_EDITOR"));
        }

        [Test]
        public void ASymbolOnlyTheTypesAssemblyDefinesIsNotCopiedIntoTheBinder()
        {
            // versionDefines are per assembly: copied into the binder's assembly, NEXUS_LEVELPLAY would be false there.
            var typeDefines = new[] { "UNITY_EDITOR", "NEXUS_LEVELPLAY" };
            var binderDefines = new[] { "UNITY_EDITOR" };
            var conditions = Read(new Dictionary<string, string>
            {
                ["a.cs"] = "#if NEXUS_LEVELPLAY\nnamespace Nexus.Editor.Tests { public class ProviderTool { } }\n#endif\n"
                    + "#if NEXUS_LEVELPLAY && (UNITY_EDITOR || DEVELOPMENT_BUILD)\nnamespace Nexus.Editor.Tests { public class DevOnlyTool { } }\n#endif",
            }, typeDefines, binderDefines);

            Assert.That(conditions.For(typeof(ProviderTool)), Is.Null);
            Assert.That(conditions.For(typeof(DevOnlyTool)), Is.EqualTo("UNITY_EDITOR || DEVELOPMENT_BUILD"));
        }
    }
}
