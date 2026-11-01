using System.Collections.Generic;
using System.IO;
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
        public class BaseTool { }
        public sealed class DerivedTool : BaseTool { }
        public sealed class NestedBlockTool { }
        public sealed class AfterInnerBlockTool { }

        private static BinderConditions Read(Dictionary<string, string> files, string[] typeDefines, string[] binderDefines)
        {
            var assemblies = new Dictionary<string, (string[] Files, string[] Defines)>
            {
                [typeof(BinderConditionTests).Assembly.GetName().Name] = (new List<string>(files.Keys).ToArray(), typeDefines),
                ["Binder"] = (new string[0], binderDefines),
            };
            return new BinderConditions(assemblies, "Binder", path => files.TryGetValue(path, out var text) && text != null ? text : throw new IOException("locked"));
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
        public void ATypeInNestedNamespaceBlocksKeepsItsIf()
        {
            // #250: the inner block's name replaced the outer one, so the type was looked up as "Editor.Tests" and named
            // unconditionally; a type after a closed inner block kept the inner name the same way
            var nested = Read(
                "namespace Nexus\n{\n    namespace Editor.Tests\n    {\n#if DEVELOPMENT_BUILD || UNITY_EDITOR\n        public sealed class NestedBlockTool { }\n#endif\n    }\n}\n",
                "UNITY_EDITOR");
            var afterInner = Read(
                "namespace Nexus.Editor.Tests\n{\n    namespace Inner\n    {\n        public sealed class Unrelated { string Format => \"}\"; }\n    }\n#if DEVELOPMENT_BUILD || UNITY_EDITOR\n    public sealed class AfterInnerBlockTool { }\n#endif\n}\n",
                "UNITY_EDITOR");

            Assert.That(nested.For(typeof(NestedBlockTool)), Is.EqualTo("DEVELOPMENT_BUILD || UNITY_EDITOR"));
            Assert.That(afterInner.For(typeof(AfterInnerBlockTool)), Is.EqualTo("DEVELOPMENT_BUILD || UNITY_EDITOR"));
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

        // #191: an unreadable file was skipped in silence, so a type declared there under its own #if was named always.
        [Test]
        public void AnUnreadableSourceLeavesTheTypesItMayDeclareOutOfTheBinder()
        {
            var conditions = Read(new Dictionary<string, string>
            {
                ["Provider.cs"] = "namespace Nexus.Editor.Tests { public class ProviderTool { } public partial class SplitTool { } }",
                ["DevOnly.cs"] = null,
            }, new[] { "UNITY_EDITOR" }, new[] { "UNITY_EDITOR" });

            Assert.That(conditions.For(typeof(ProviderTool)), Is.Null);
            Assert.That(conditions.For(typeof(DevOnlyTool)), Is.EqualTo(BinderConditions.Never));
            Assert.That(conditions.For(typeof(List<DevOnlyTool>), typeof(ProviderTool)), Is.EqualTo(BinderConditions.Never));
            Assert.That(conditions.Problems[typeof(DevOnlyTool)], Is.EqualTo("could not read DevOnly.cs"));
            Assert.That(conditions.Warnings, Is.EquivalentTo(new[] { "could not read DevOnly.cs (locked)" }));
            Assert.That(conditions.MemberProblem(typeof(ProviderTool)), Is.Null, "a type declared whole in a readable file keeps its binder lines");
            Assert.That(conditions.MemberProblem(typeof(SplitTool)), Is.EqualTo("could not read DevOnly.cs"), "the unread file may hold a part of a partial type");
        }

        // #191: the binder set an [Inject] member under its own #if through a FieldInfo a release player does not have.
        [Test]
        public void AnInjectMemberUnderAnIfOfABaseTypeKeepsTheDerivedTypeOnTheReflectionPath()
        {
            var conditions = Read(new Dictionary<string, string>
            {
                ["Base.cs"] = "namespace Nexus.Editor.Tests\n{\n    public class BaseTool\n    {\n#if UNITY_EDITOR\n        [Inject] private ISignalBus _signals;\n#endif\n    }\n}\n",
                ["Derived.cs"] = "namespace Nexus.Editor.Tests { public sealed class DerivedTool : BaseTool { } }",
            }, new[] { "UNITY_EDITOR" }, new[] { "UNITY_EDITOR" });

            Assert.That(conditions.MemberProblem(typeof(DerivedTool)), Is.EqualTo("an [Inject] member in Base.cs (line 6) is declared under #if UNITY_EDITOR"));
            Assert.That(conditions.For(typeof(DerivedTool)), Is.Null);
        }

        [Test]
        public void AnIfAroundAWholeTypeOrOnTheTypesOwnDefineIsNoMemberCondition()
        {
            var conditions = Read(new Dictionary<string, string>
            {
                ["DevOnly.cs"] = "#if DEVELOPMENT_BUILD || UNITY_EDITOR\nnamespace Nexus.Editor.Tests\n{\n    public sealed class DevOnlyTool\n    {\n        [Inject] public ISignalBus Signals { get; set; }\n    }\n}\n#endif\n",
                ["Provider.cs"] = "namespace Nexus.Editor.Tests\n{\n    public sealed class ProviderTool\n    {\n#if NEXUS_LEVELPLAY\n        [Inject] public ISignalBus Signals;\n#endif\n    }\n}\n",
            }, new[] { "UNITY_EDITOR", "NEXUS_LEVELPLAY" }, new[] { "UNITY_EDITOR" });

            Assert.That(conditions.MemberProblem(typeof(DevOnlyTool)), Is.Null);
            Assert.That(conditions.For(typeof(DevOnlyTool)), Is.EqualTo("DEVELOPMENT_BUILD || UNITY_EDITOR"));
            Assert.That(conditions.MemberProblem(typeof(ProviderTool)), Is.Null);
        }
    }
}
