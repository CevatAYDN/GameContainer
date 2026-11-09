using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Nexus.Core;
using Nexus.Editor;

namespace NexusBench
{
    public static partial class CodeGenSuite
    {
        private const string ArrayNamingSource = @"
using System;
using System.Collections.Generic;
using Nexus.Core;
namespace NexusBench.NamingArrays
{
    public sealed class Dependency { }
    public sealed class Target
    {
        public readonly Dependency[,] FromConstructor;
        public Target(Dependency[,] matrix) => FromConstructor = matrix;
        [Inject] public Dependency[,] Matrix;
        [Inject] public Dependency[,,] Cube { get; private set; }
        [Inject] public Dependency[][] Jagged;
        [Inject] public Dependency[][,] Mixed;
        [Inject] public IReadOnlyList<Dependency[,]> List;
        public Dependency[,][] FromMethod;
        [Inject] private void Accept(Dependency[,][] value) => FromMethod = value;
    }
    public static class Proof
    {
        public static void Run()
        {
            using var container = new NexusDI { StrictInjection = true };
            var matrix = new Dependency[2, 3];
            var cube = new Dependency[2, 3, 4];
            var jagged = new Dependency[2][];
            var mixed = new Dependency[2][,];
            var fromMethod = new Dependency[2, 3][];
            IReadOnlyList<Dependency[,]> list = new List<Dependency[,]> { matrix };
            container.BindInstance(matrix);
            container.BindInstance(cube);
            container.BindInstance(jagged);
            container.BindInstance(mixed);
            container.BindInstance(fromMethod);
            container.BindInstance(list);
            container.Bind<Target>(Lifetime.Transient);
            var value = container.Resolve<Target>();
            if (!ReferenceEquals(value.FromConstructor, matrix) || !ReferenceEquals(value.Matrix, matrix)
                || !ReferenceEquals(value.Cube, cube) || !ReferenceEquals(value.Jagged, jagged)
                || !ReferenceEquals(value.Mixed, mixed) || !ReferenceEquals(value.List, list)
                || !ReferenceEquals(value.FromMethod, fromMethod))
                throw new InvalidOperationException(""Array ranks or nesting changed during generated injection."");
            NexusDI.ClearInjectedReferences(value);
            if (value.Matrix != null || value.Cube != null || value.Jagged != null || value.Mixed != null || value.List != null)
                throw new InvalidOperationException(""Generated array clearer left injected references."");
        }
    }
}
";

        private const string KeywordTypeNamingSource = @"
using System;
using Nexus.Core;
namespace NexusBench.NamingTypes.@namespace
{
    public sealed class @class { }
    public sealed class Target
    {
        public readonly @class FromConstructor;
        public Target(@class value) => FromConstructor = value;
        [Inject] public @class Dependency;
    }
    public static class Proof
    {
        public static void Run()
        {
            using var container = new NexusDI { StrictInjection = true };
            var dependency = new @class();
            container.BindInstance(dependency);
            container.Bind<Target>(Lifetime.Transient);
            var value = container.Resolve<Target>();
            if (!ReferenceEquals(value.Dependency, dependency) || !ReferenceEquals(value.FromConstructor, dependency))
                throw new InvalidOperationException(""Keyword-named types were not injected."");
            NexusDI.ClearInjectedReferences(value);
            if (value.Dependency != null) throw new InvalidOperationException(""The generated clearer did not run."");
        }
    }
}
";

        private const string KeywordMemberNamingSource = @"
using System;
using Nexus.Core;
namespace NexusBench.NamingMembers
{
    public sealed class Dependency { }
    public sealed class Target
    {
        public readonly Dependency FromConstructor;
        public Target(Dependency @params) => FromConstructor = @params;
        [Inject] public Dependency @event;
        [Inject] public Dependency @class { get; set; }
        [Inject] public LazyInjection<Dependency> @switch;
        [Inject] public LazyInjection<Dependency> @namespace { get; set; }
        [Inject] private Dependency @private;
        public Dependency Hidden => @private;
        [Inject] public Dependency @protected { get; private set; }
        public Dependency FromMethod;
        [Inject] private void @return(Dependency value) => FromMethod = value;
    }
    public static class Proof
    {
        public static void Run()
        {
            using var container = new NexusDI { StrictInjection = true };
            var dependency = new Dependency();
            container.BindInstance(dependency);
            container.Bind<Target>(Lifetime.Transient);
            var value = container.Resolve<Target>();
            if (!ReferenceEquals(value.FromConstructor, dependency) || !ReferenceEquals(value.@event, dependency)
                || !ReferenceEquals(value.@class, dependency) || !ReferenceEquals(value.@switch?.Value, dependency)
                || !ReferenceEquals(value.@namespace?.Value, dependency) || !ReferenceEquals(value.Hidden, dependency)
                || !ReferenceEquals(value.@protected, dependency) || !ReferenceEquals(value.FromMethod, dependency))
                throw new InvalidOperationException(""Keyword-named members were not injected."");
            NexusDI.ClearInjectedReferences(value);
            if (value.@event != null || value.@class != null || value.@switch != null || value.@namespace != null
                || value.Hidden != null || value.@protected != null)
                throw new InvalidOperationException(""Keyword-named members were not cleared."");
        }
    }
}
";

        private const string CacheCollisionNamingSource = @"
using System;
using Nexus.Core;
namespace NexusBench.NamingCaches.Path
{
    public class Target
    {
        [Inject(""base"")] private Dependency _dependency;
        public Dependency BaseField => _dependency;
        [Inject(""base"")] public Dependency Property { get; private set; }
        public Dependency BaseMethod { get; private set; }
        [Inject] private void Accept([Inject(""base"")] Dependency value) => BaseMethod = value;
    }
}
namespace NexusBench.NamingCaches
{
    public sealed class Dependency { }
    public sealed class Path_Target : Path.Target
    {
        [Inject(""derived"")] private Dependency _dependency;
        public Dependency DerivedField => _dependency;
        [Inject(""derived"")] public Dependency DerivedProperty { get; private set; }
        public Dependency DerivedMethod { get; private set; }
        [Inject] private void Accept([Inject(""derived"")] Dependency value) => DerivedMethod = value;
    }
    public static class Proof
    {
        public static void Run()
        {
            using var container = new NexusDI { StrictInjection = true };
            var baseDependency = new Dependency();
            var derivedDependency = new Dependency();
            container.BindInstance(""base"", baseDependency);
            container.BindInstance(""derived"", derivedDependency);
            container.Bind<Path.Target>(Lifetime.Transient);
            container.Bind<Path_Target>(Lifetime.Transient);
            var parent = container.Resolve<Path.Target>();
            var derived = container.Resolve<Path_Target>();
            if (!ReferenceEquals(parent.BaseField, baseDependency) || !ReferenceEquals(parent.Property, baseDependency)
                || !ReferenceEquals(parent.BaseMethod, baseDependency)
                || !ReferenceEquals(derived.BaseField, baseDependency) || !ReferenceEquals(derived.Property, baseDependency)
                || !ReferenceEquals(derived.BaseMethod, baseDependency)
                || !ReferenceEquals(derived.DerivedField, derivedDependency) || !ReferenceEquals(derived.DerivedProperty, derivedDependency)
                || !ReferenceEquals(derived.DerivedMethod, derivedDependency))
                throw new InvalidOperationException(""Distinct declaring types shared an injection cache."");
            NexusDI.ClearInjectedReferences(parent);
            NexusDI.ClearInjectedReferences(derived);
            if (parent.BaseField != null || parent.Property != null || derived.BaseField != null || derived.Property != null
                || derived.DerivedField != null || derived.DerivedProperty != null)
                throw new InvalidOperationException(""A cache collision left injected references uncleared."");
        }
    }
}
";

        private const string UnicodeLiteralNamingSource = @"
using System;
using Nexus.Core;
namespace NexusBench.NamingLiterals
{
    public sealed class Dependency { }
    public sealed class Target
    {
        public readonly Dependency FromConstructor;
        public Target([Inject(""line\u2028break"")] Dependency value) => FromConstructor = value;
        [Inject(""line\u2028break"")] public Dependency Line;
        [Inject(""paragraph\u2029break"")] public Dependency Paragraph { get; private set; }
        [Inject(""line\u2028break"")] public LazyInjection<Dependency> Lazy;
        public Dependency FromMethod;
        [Inject] private void Accept([Inject(""paragraph\u2029break"")] Dependency value) => FromMethod = value;
    }
    public static class Proof
    {
        public static void Run()
        {
            using var container = new NexusDI { StrictInjection = true };
            var line = new Dependency();
            var paragraph = new Dependency();
            container.BindInstance(""line\u2028break"", line);
            container.BindInstance(""paragraph\u2029break"", paragraph);
            container.Bind<Target>(Lifetime.Transient);
            var value = container.Resolve<Target>();
            if (!ReferenceEquals(value.FromConstructor, line) || !ReferenceEquals(value.Line, line)
                || !ReferenceEquals(value.Lazy?.Value, line) || !ReferenceEquals(value.Paragraph, paragraph)
                || !ReferenceEquals(value.FromMethod, paragraph))
                throw new InvalidOperationException(""Unicode binding names changed during emission."");
            NexusDI.ClearInjectedReferences(value);
            if (value.Line != null || value.Paragraph != null || value.Lazy != null)
                throw new InvalidOperationException(""Unicode-named bindings were not cleared."");
        }
    }
}
";

        private static void Test_Naming_ArrayRanks_CompileAndBoot()
            => RunNamingFixture("CG17. ArrayRanks_Compile_And_Boot", "NexusBench.NamingArrays",
                ArrayNamingSource, "NexusBench.NamingArrays.Target", "NexusBench.NamingArrays.Proof");

        private static void Test_Naming_KeywordTypes_CompileAndBoot()
            => RunNamingFixture("CG18. KeywordTypes_Compile_And_Boot", "NexusBench.NamingTypes",
                KeywordTypeNamingSource, "NexusBench.NamingTypes.namespace.Target", "NexusBench.NamingTypes.namespace.Proof",
                "NexusBench.NamingTypes.@namespace.Target");

        private static void Test_Naming_KeywordMembers_CompileAndBoot()
            => RunNamingFixture("CG19. KeywordMembers_Compile_And_Boot", "NexusBench.NamingMembers",
                KeywordMemberNamingSource, "NexusBench.NamingMembers.Target", "NexusBench.NamingMembers.Proof");

        private static void Test_Naming_CacheCollisions_CompileAndBoot()
            => RunNamingFixture("CG20. DistinctTypeCaches_Compile_And_Boot", "NexusBench.NamingCaches",
                CacheCollisionNamingSource, "NexusBench.NamingCaches.Path_Target", "NexusBench.NamingCaches.Proof",
                null, "NexusBench.NamingCaches.Path.Target");

        private static void Test_Naming_UnicodeSeparators_CompileAndBoot()
            => RunNamingFixture("CG21. UnicodeSeparators_Compile_And_Boot", "NexusBench.NamingLiterals",
                UnicodeLiteralNamingSource, "NexusBench.NamingLiterals.Target", "NexusBench.NamingLiterals.Proof");

        private static void RunNamingFixture(string testName, string assemblyName, string fixtureSource,
            string targetName, string proofName, string referenceName = null, params string[] otherTargetNames)
        {
            var problems = new List<string>();
            var outcomes = new List<string>();
            try
            {
                string[] fixtureErrors = EmitSource(fixtureSource, assemblyName, out byte[] pe);
                if (fixtureErrors.Length != 0)
                    throw new InvalidOperationException("Fixture compilation failed: " + string.Join(" | ", fixtureErrors));
                string directory = Path.Combine(Program.TempRoot, "nexus-naming");
                Directory.CreateDirectory(directory);
                string fixturePath = Path.Combine(directory, assemblyName + ".dll");
                File.WriteAllBytes(fixturePath, pe);
                // Keep file-backed fixtures until process exit, as later binder scans still see their types.
                var fixtures = Assembly.LoadFrom(fixturePath);
                var targets = new[] { targetName }.Concat(otherTargetNames)
                    .Select(n => fixtures.GetType(n, throwOnError: true)).ToArray();
                var proof = fixtures.GetType(proofName, throwOnError: true).GetMethod("Run");

                NexusRuntime.Reset();
                NexusDI.ClearGeneratedRegistrations();
                proof.Invoke(null, null);
                outcomes.Add("reflection boot passed");

                NexusCodeGenerator.GenerateBinder();
                string editor = File.ReadAllText(Path.Combine(NexusEditorSettings.OutputRoot, "NexusGeneratedBinder.g.cs"));
                string generator = GenerateBinderSource(new Nexus.Generator.NexusBinderGenerator(), assemblyName, fixtureSource);
                foreach (var path in new[] { (Name: "editor", Source: editor), (Name: "source-generator", Source: generator) })
                {
                    SaveNamingEvidence(assemblyName, path.Name + ".g.cs", path.Source);
                    NexusRuntime.Reset();
                    NexusDI.ClearGeneratedRegistrations();
                    string[] errors = CompileSource(path.Source, assemblyName + "." + path.Name.Replace('-', '_') + "Binder",
                        out Assembly binder, out _);
                    if (errors.Length != 0)
                    {
                        problems.Add(path.Name + " compilation failed: " + string.Join(" | ", errors.Take(8)));
                        continue;
                    }
                    try
                    {
                        binder.GetType("Nexus.Generated.NexusGeneratedBinder", throwOnError: true)
                            .GetMethod("Initialize").Invoke(null, null);
                        foreach (var target in targets)
                        {
                            if (!NexusDI.HasRegisteredConstructorFactory(target))
                                throw new InvalidOperationException("The generated constructor factory was not registered: " + target.FullName);
                            string emittedName = target.FullName == targetName ? referenceName ?? targetName : target.FullName;
                            if (!HasRegistration(path.Source, "RegisterInjector", emittedName))
                                throw new InvalidOperationException("The generated injector was not emitted: " + target.FullName);
                        }
                        proof.Invoke(null, null);
                        if (NexusDI.GetStaleGeneratedInjectorTypes().Intersect(targets).Any())
                            throw new InvalidOperationException("The target's generated injector fell back to reflection.");
                        outcomes.Add(path.Name + " compiled and booted");
                    }
                    catch (Exception ex)
                    {
                        problems.Add(path.Name + " boot failed: " + ex);
                    }
                }
                SaveNamingEvidence(assemblyName, "fixture.cs", fixtureSource);
            }
            catch (Exception ex)
            {
                problems.Add(ex.ToString());
            }
            finally
            {
                NexusRuntime.Reset();
                NexusDI.ClearGeneratedRegistrations();
            }
            Report(testName, problems.Count == 0, string.Join("; ", outcomes.Concat(problems)));
        }

        private static void SaveNamingEvidence(string assemblyName, string name, string content)
        {
            string root = Environment.GetEnvironmentVariable("NEXUS_CODEGEN_EVIDENCE_DIR");
            if (string.IsNullOrEmpty(root)) return;
            string directory = Path.Combine(root, assemblyName);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, name), content);
        }
    }
}
