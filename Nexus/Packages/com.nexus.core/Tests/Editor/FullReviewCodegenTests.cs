using System;
using System.Collections.Generic;
using System.IO;

namespace Nexus.Editor.Tests
{
    // Called by BinderConditionTests in Unity and by the host audit runner.
    // No NUnit attributes here: each behavior is registered only once per runner.
    public sealed class FullReviewCodegenTests
    {
        private sealed class DevOnlyTool { }
        private sealed class SplitTool { }

        private static BinderConditions Read(Dictionary<string, string> files)
        {
            var defines = new[] { "UNITY_EDITOR" };
            var assemblies = new Dictionary<string, (string[] Files, string[] Defines)>
            {
                [typeof(FullReviewCodegenTests).Assembly.GetName().Name] = (new List<string>(files.Keys).ToArray(), defines),
                ["Binder"] = (Array.Empty<string>(), defines),
            };
            return new BinderConditions(assemblies, "Binder",
                path => files.TryGetValue(path, out var text) ? text : throw new IOException("missing fixture"));
        }

        private static BinderConditions Read(string source)
            => Read(new Dictionary<string, string> { ["a.cs"] = source });

        public void CommentsAndMultilineLiterals_CannotRemoveAnEditorOnlyTypeGuard()
        {
            foreach (string noise in new[]
            {
                "/* a brace in documentation: }\n*/",
                "/*\n#if NOT_A_REAL_DIRECTIVE\n}\n#endif\n*/",
                "public class Noise { const string Text = \"}}// not a comment\"; }",
                "public class Noise { const string Text = @\"first line\n}\n#if NOT_A_REAL_DIRECTIVE\nlast line\"; }",
            })
            {
                var conditions = Read("namespace Nexus.Editor.Tests\n{\n" + noise
                    + "\n#if UNITY_EDITOR\npublic class DevOnlyTool { }\n#endif\n}\n");
                string actual = conditions.For(typeof(DevOnlyTool));
                Require(actual == "UNITY_EDITOR", "comment/literal lost the player guard: " + noise + "; actual=" + (actual ?? "<unconditional>"));
            }
        }

        public void AmbiguousNestedShortNames_DoNotBecomeAnUnconditionalPlayerReference()
        {
            var conditions = Read(
                "namespace Nexus.Editor.Tests\n{\npublic class First\n{\n#if UNITY_EDITOR\npublic class DevOnlyTool { }\n#endif\n}\n"
                + "public class Second { public class DevOnlyTool { } }\n}\n");
            Require(conditions.For(typeof(DevOnlyTool)) == BinderConditions.Never,
                "ambiguous short names must not manufacture an unconditional reference");
            Require(conditions.Problems[typeof(DevOnlyTool)].Contains("ambiguous"), "fallback must explain the ambiguity");
        }

        public void ConditionalPartialMembers_KeepTheInjectorAndConstructorOnReflection()
        {
            var conditions = Read(new Dictionary<string, string>
            {
                ["Split.cs"] = "namespace Nexus.Editor.Tests { public partial class SplitTool { } }",
                ["Split.Tools.cs"] = "#if UNITY_EDITOR\nnamespace Nexus.Editor.Tests\n{\npublic partial class SplitTool\n{\n"
                    + "[Inject] public ISignalBus Signals;\n}\n}\n#endif",
            });
            Require(conditions.For(typeof(SplitTool)) == null, "the unconditional part still makes the type available");
            string problem = conditions.MemberProblem(typeof(SplitTool));
            Require(problem != null && problem.Contains("partial"), "conditional partial members must not be emitted unconditionally");
        }

        public void MissingSourceDeclaration_IsUnknown_NotUnconditional()
        {
            var conditions = Read("namespace Nexus.Editor.Tests { public class ProviderTool { } }");
            Require(conditions.For(typeof(DevOnlyTool)) == BinderConditions.Never,
                "missing declaration must remain unknown instead of guessing unconditional availability");
            Require(conditions.Problems[typeof(DevOnlyTool)].Contains("declaration"), "fallback must explain the missing declaration");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
