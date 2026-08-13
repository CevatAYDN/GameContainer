using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Nexus.Core;
using Nexus.Editor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace NexusBench
{
    // ─── Dedicated codegen fixtures ───
    // These live in the harness assembly so the (stubbed) AssemblyCatalog scan — which mirrors
    // the real editor predicate — discovers them exactly like game code inside Unity.

    public sealed class CodeGenDep { }

    /// <summary>Single public ctor → the generator must emit a constructor factory; readonly ctor state.</summary>
    public sealed class CodeGenTarget
    {
        public readonly CodeGenDep Dep;
        public CodeGenTarget(CodeGenDep dep) { Dep = dep; }
    }

    /// <summary>Two public ctors, one [Construct]-marked → the generator must pick the marked one.</summary>
    public sealed class CodeGenMulti
    {
        public readonly CodeGenDep Dep;
        public bool ParameterlessUsed;
        public CodeGenMulti() { ParameterlessUsed = true; }

        [Construct]
        public CodeGenMulti(CodeGenDep dep) { Dep = dep; }
    }

    /// <summary>[Inject] field → the generator must emit an injector.</summary>
    public sealed class CodeGenInjected
    {
        [Inject] public CodeGenDep Dep;
    }

    /// <summary>[Inject] field + [PostConstruct]: a generated injector REPLACES the reflection
    /// member injection, and Injector.Inject must still run [PostConstruct] afterwards —
    /// regression for the silent-skip gap that would break on the AOT path.</summary>
    public sealed class CodeGenPostConstruct
    {
        [Inject] public CodeGenDep Dep;
        public bool PostRan;

        [PostConstruct]
        public void OnPostConstruct() => PostRan = Dep != null;
    }

    /// <summary>Single public ctor (factory-eligible) so the WithParameter override-vs-factory
    /// precedence can be proven through the ACTUAL generated binder output.</summary>
    public sealed class CodeGenOverride
    {
        public readonly CodeGenDep Dep;
        public CodeGenOverride(CodeGenDep dep) { Dep = dep; }
    }

    /// <summary>Value-type ctor param → the generator must SKIP the factory (reflection handles it).</summary>
    public sealed class CodeGenSkippedValueParam
    {
        public readonly int Count;
        public readonly CodeGenDep Dep;
        public CodeGenSkippedValueParam(int count, CodeGenDep dep) { Count = count; Dep = dep; }
    }

    /// <summary>Injection-hierarchy fixtures. CodeGenVisibleBase carries a PRIVATE [Inject]
    /// field (the chain walk must surface it and the generated injector must handle it via
    /// cached FieldInfo on the base type). CodeGenNonEmittableMember's [Inject] member type is
    /// INTERNAL (non-visible) — not referenceable from the generated binder (CS0122) — so the
    /// WHOLE injector must be skipped (all-or-nothing) and the reflection path must still
    /// inject the member. A public type cannot derive from a private nested base (CS0060), so
    /// the non-emittable case is exercised through the member type instead.</summary>
    public static class CodeGenHierarchy
    {
        public class CodeGenVisibleBase
        {
            [Inject] private CodeGenDep _dep;
            public CodeGenDep ReadDep() => _dep;
        }

        public sealed class CodeGenDerivedWithVisibleBase : CodeGenVisibleBase { }

        internal sealed class InternalDep { }

        public sealed class CodeGenEmittableMember
        {
            [Inject] private CodeGenDep _dep;
            public bool HasDep => _dep != null;
        }

        public sealed class CodeGenNonEmittableMember
        {
            [Inject] private InternalDep _dep;
            public bool HasDep => _dep != null;
        }
    }

    /// <summary>
    /// Runs the REAL NexusCodeGenerator (editor code compiled into the harness with UnityEditor
    /// stubs), then compiles the emitted binder with Roslyn and boots it. This closes the gap
    /// the roadmap flags as an open acceptance gate ("Unity IL2CPP build doğrulaması"): the CF
    /// tests only prove the runtime factory mechanism, while this suite proves the codegen's
    /// OUTPUT is valid, compiling C# that actually wires NexusDI end to end.
    /// Runs LAST (after ServiceGraphSuite): the generated binder registers typed command
    /// dispatchers and injectors for every harness type, and CodeGenSuite resets them in
    /// teardown, so no other suite can observe the registration.
    /// </summary>
    public static class CodeGenSuite
    {
        private static int _failures;

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine("[CodeGen] REAL NexusCodeGenerator → EMITTED BINDER → ROSLYN COMPILE → BOOT");
            Console.WriteLine("===============================================================================");

            Test_Codegen_Emits_Compiles_And_Boots();
            Test_Generator_Emits_Compiles_And_Boots();

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "[CodeGen] ALL CODEGEN TESTS PASSED ✓"
                : $"[CodeGen] {_failures} CODEGEN TEST(S) FAILED ✗");
            return _failures;
        }

        // ─── CG2: Roslyn Source Generator path ─────────────────────────────────
        // Source fixtures live in the GENERATOR'S CURRENT COMPILATION (the stub source tree),
        // which is the primary path in Unity (game asmdef = current compilation); the harness
        // assembly is a referenced "game assembly" (the secondary path). The same content gates
        // proven by CG1 (factory/[Construct]/injector/value-param skip/visibility/all-or-nothing/
        // WithParameter precedence) must hold for the generator's output, and that output must
        // compile and boot end to end.

        private const string GeneratorStubSource = @"
using Nexus.Core;
using NexusBench;

namespace NexusBench.CG2Source
{
    public sealed class SrcFixture
    {
        [Inject] public CodeGenDep Dep;
        public bool HasDep => Dep != null;
    }

    public sealed class SrcCtorTarget
    {
        public readonly CodeGenDep Dep;
        public SrcCtorTarget(CodeGenDep dep) { Dep = dep; }
    }

    public sealed class SrcValueCtor
    {
        public readonly int Count;
        public readonly CodeGenDep Dep;
        public SrcValueCtor(int count, CodeGenDep dep) { Count = count; Dep = dep; }
    }

    public sealed class SrcOverrideTarget
    {
        public readonly CodeGenDep Dep;
        public SrcOverrideTarget(CodeGenDep dep) { Dep = dep; }
    }

    // Chain-walk fixture IN THE CURRENT COMPILATION: the SG sees all members for source
    // types, so a public base with a private [Inject] field must produce an injector that
    // injects it (parity with the editor generator / CG1).
    public static class SrcHierarchy
    {
        public class SrcVisibleBase
        {
            [Inject] private CodeGenDep _dep;
            public CodeGenDep ReadDep() => _dep;
        }

        public sealed class SrcDerived : SrcVisibleBase { }
    }

    // [PostConstruct] must run after the GENERATED injector (runtime guarantee).
    public sealed class SrcPostConstruct
    {
        [Inject] public CodeGenDep Dep;
        public bool PostRan;

        [PostConstruct]
        public void OnPostConstruct() => PostRan = Dep != null;
    }
}";

        private static void Test_Generator_Emits_Compiles_And_Boots()
        {
            bool ok = false;
            string detail;
            NexusDI container = null;
            // Dedicated scratch dir for the stub assembly (persisted so it loads via LoadFrom).
            string cg2Dir = Path.Combine(Path.GetTempPath(), "nexus-sg-cg2");
            try
            {
                // Clear stale factories/injectors so the generated binder is authoritative.
                NexusRuntime.Reset();

                // 1) Compile + load the stub source assembly FIRST so the emitted binder can
                //    reference its types when it is compiled afterwards. Byte-loaded assemblies
                //    (Assembly.Load) have an empty Location and are NOT resolvable by name from
                //    other byte-loaded assemblies, so the stub is persisted to disk and loaded
                //    via LoadFrom — then the AppDomain scan picks it up as a file reference and
                //    the runtime name-based resolution finds it too.
                Directory.CreateDirectory(cg2Dir);
                string[] stubErrors = CompileSource(GeneratorStubSource, "NexusBench.CodeGenSourceStub", out Assembly stubAssembly, out byte[] stubPe);
                if (stubErrors.Length == 0 && stubAssembly != null)
                {
                    string stubPath = Path.Combine(cg2Dir, "NexusBench.CodeGenSourceStub.dll");
                    File.WriteAllBytes(stubPath, stubPe);
                    stubAssembly = Assembly.LoadFrom(stubPath);
                }
                if (stubErrors.Length > 0 || stubAssembly == null)
                {
                    detail = "stub source FAILED to compile: " + string.Join(" | ", stubErrors.Take(3));
                    Report("CG2. SourceGenerator_Emits_Compiles_And_Boots", false, detail);
                    return;
                }

                // 2) Drive the REAL shipping generator over a synthetic compilation whose CURRENT
                //    assembly is the stub (source path) and which references the harness assembly
                //    (referenced game-assembly path).
                var references = new List<MetadataReference>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.IsDynamic) continue;
                    try
                    {
                        string location = asm.Location;
                        if (string.IsNullOrEmpty(location)) continue;
                        references.Add(MetadataReference.CreateFromFile(location));
                    }
                    catch { }
                }
                var stubTree = CSharpSyntaxTree.ParseText(GeneratorStubSource);
                var compilation = CSharpCompilation.Create(
                    "NexusBench.CodeGenSourceStub",
                    new[] { stubTree },
                    references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                var generator = new Nexus.Generator.NexusBinderGenerator();
                CSharpGeneratorDriver.Create(generator)
                    .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var genDiags);

                var genTree = outputCompilation.SyntaxTrees.FirstOrDefault(t => t.FilePath.EndsWith("NexusGeneratedBinder.g.cs"));
                if (genTree == null)
                {
                    detail = "generator produced no NexusGeneratedBinder.g.cs (driver diagnostics: " +
                             string.Join(" | ", genDiags.Select(d => d.ToString()).Take(3)) + ")";
                    Report("CG2. SourceGenerator_Emits_Compiles_And_Boots", false, detail);
                    return;
                }
                string source = genTree.ToString();

                // 3) Content gates for the GENERATOR's emitted binder. The SG emits fully-qualified
                //    names (with a `global::` prefix), so registrations are matched by
                //    "RegisterX<[global::]Type". PreserveMembers legitimately references the
                //    containing type of a skipped injector via typeof(), so the prefix matters.
                //    Injectors exist ONLY for current-compilation types (complete member
                //    visibility); referenced (metadata) types get ctor factories + dispatchers
                //    only — emitting a partial injector would silently break invisible members.
                bool srcFactory = HasRegistration(source, "RegisterConstructorFactory", "NexusBench.CG2Source.SrcCtorTarget");
                bool srcInjector = HasRegistration(source, "RegisterInjector", "NexusBench.CG2Source.SrcFixture");
                bool srcValueSkipped = !HasRegistration(source, "RegisterConstructorFactory", "NexusBench.CG2Source.SrcValueCtor");
                //    Source chain-walk: a public base with a PRIVATE [Inject] field in the current
                //    compilation must produce an injector (SG sees all members for source types).
                bool srcChainInjector = HasRegistration(source, "RegisterInjector", "NexusBench.CG2Source.SrcHierarchy.SrcDerived");
                bool srcPostInjector = HasRegistration(source, "RegisterInjector", "NexusBench.CG2Source.SrcPostConstruct");
                //    Referenced game-assembly path: factories yes, injectors NO (metadata contract).
                bool harnessFactory = HasRegistration(source, "RegisterConstructorFactory", "NexusBench.CodeGenTarget");
                bool harnessMarked = HasRegistration(source, "RegisterConstructorFactory", "NexusBench.CodeGenMulti");
                bool harnessValueSkipped = !HasRegistration(source, "RegisterConstructorFactory", "NexusBench.CodeGenSkippedValueParam");
                bool harnessInjectorAbsent = !HasRegistration(source, "RegisterInjector", "NexusBench.CodeGenInjected");
                bool metadataInjectorAbsent = !HasRegistration(source, "RegisterInjector", "NexusBench.CodeGenHierarchy.CodeGenDerivedWithVisibleBase");
                bool nonEmittableInjectorAbsent = !HasRegistration(source, "RegisterInjector", "NexusBench.CodeGenHierarchy.CodeGenNonEmittableMember");

                // 4) Compile + boot the generated binder, resolve through it end to end (the stub
                //    is file-loaded, so it is already among the AppDomain-wide references).
                string[] compileErrors = CompileSource(source, "NexusGeneratedBinderHarness", out Assembly binderAssembly, out _);
                if (compileErrors.Length > 0 || binderAssembly == null)
                {
                    detail = "generator output FAILED to compile: " +
                             string.Join(" | ", compileErrors.Take(3)) +
                             $" (total {compileErrors.Length} errors)";
                    Report("CG2. SourceGenerator_Emits_Compiles_And_Boots", false, detail);
                    return;
                }

                Type binderType = binderAssembly.GetType("Nexus.Generated.NexusGeneratedBinder");
                if (binderType == null)
                {
                    detail = "generator output compiled but Nexus.Generated.NexusGeneratedBinder type not found";
                    Report("CG2. SourceGenerator_Emits_Compiles_And_Boots", false, detail);
                    return;
                }
                binderType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);

                // The CG2Source fixtures only exist in the runtime-compiled stub assembly, so
                // bind/resolve them through the non-generic API + reflection (no static types).
                Type srcCtorT = stubAssembly.GetType("NexusBench.CG2Source.SrcCtorTarget");
                Type srcFixtureT = stubAssembly.GetType("NexusBench.CG2Source.SrcFixture");
                Type srcOverrideT = stubAssembly.GetType("NexusBench.CG2Source.SrcOverrideTarget");
                Type srcDerivedT = stubAssembly.GetType("NexusBench.CG2Source.SrcHierarchy+SrcDerived"); // nested: '+' in metadata name
                Type srcPostT = stubAssembly.GetType("NexusBench.CG2Source.SrcPostConstruct");
                if (srcCtorT == null || srcFixtureT == null || srcOverrideT == null || srcDerivedT == null || srcPostT == null)
                {
                    detail = "stub assembly is missing CG2Source fixture types";
                    Report("CG2. SourceGenerator_Emits_Compiles_And_Boots", false, detail);
                    return;
                }

                container = new NexusDI();
                container.Bind<CodeGenDep>(Lifetime.Scoped);
                container.Bind(srcFixtureT, Lifetime.Transient);
                container.Bind(srcCtorT, Lifetime.Transient);
                container.Bind(srcOverrideT, Lifetime.Transient);
                container.Bind(srcDerivedT, Lifetime.Transient);
                container.Bind(srcPostT, Lifetime.Transient);
                container.Bind<CodeGenTarget>(Lifetime.Transient);
                container.Bind<CodeGenMulti>(Lifetime.Transient);
                container.Bind<CodeGenInjected>(Lifetime.Transient);
                container.Bind<CodeGenPostConstruct>(Lifetime.Transient);
                container.Bind<CodeGenHierarchy.CodeGenDerivedWithVisibleBase>(Lifetime.Transient);
                container.Bind<CodeGenHierarchy.CodeGenEmittableMember>(Lifetime.Transient);
                container.Bind<CodeGenHierarchy.CodeGenNonEmittableMember>(Lifetime.Transient);
                container.Bind<CodeGenHierarchy.InternalDep>(Lifetime.Scoped);

                var srcCtor = container.Resolve(srcCtorT);       // generated factory (source path)
                var srcFixture = container.Resolve(srcFixtureT); // generated injector (source path)
                var shared = container.Resolve<CodeGenDep>();
                var manual = new CodeGenDep();

                // Fluent WithParameter override (fluent chain is generic — drive it via reflection)
                // must beat the generated factory for the source-path type.
                var diType = typeof(NexusDI);
                object fluentBinder = diType.GetMethod("BindFluent").MakeGenericMethod(srcOverrideT).Invoke(container, null);
                var fluentType = fluentBinder.GetType();
                fluentType.GetMethod("To").MakeGenericMethod(srcOverrideT).Invoke(fluentBinder, null);
                fluentType.GetMethod("AsTransient").Invoke(fluentBinder, null);
                fluentType.GetMethod("WithParameter", new[] { typeof(Type), typeof(object) })
                    .Invoke(fluentBinder, new object[] { typeof(CodeGenDep), manual });
                var srcOverridden = container.Resolve(srcOverrideT); // override beats generated factory

                var target = container.Resolve<CodeGenTarget>();                               // generated factory (referenced path)
                var multi = container.Resolve<CodeGenMulti>();                                 // generated [Construct] factory
                var injected = container.Resolve<CodeGenInjected>();                           // reflection injector (no SG injector for metadata)
                var visibleBase = container.Resolve<CodeGenHierarchy.CodeGenDerivedWithVisibleBase>();
                var emittableMember = container.Resolve<CodeGenHierarchy.CodeGenEmittableMember>();
                var nonEmittableMember = container.Resolve<CodeGenHierarchy.CodeGenNonEmittableMember>(); // reflection injector

                // Source-path fixtures: generated chain-walk injector (base private field) and
                // generated injector + [PostConstruct] (must still run after the AOT injector).
                var srcDerived = container.Resolve(srcDerivedT);
                var srcPost = container.Resolve(srcPostT);
                var srcCtorDep = (CodeGenDep)srcCtorT.GetField("Dep").GetValue(srcCtor);
                var srcFixtureDep = (CodeGenDep)srcFixtureT.GetField("Dep").GetValue(srcFixture);
                var srcOverrideDep = (CodeGenDep)srcOverrideT.GetField("Dep").GetValue(srcOverridden);
                bool srcFixtureInjected = (bool)srcFixtureT.GetProperty("HasDep").GetValue(srcFixture);
                bool srcDerivedInjected = srcDerivedT.GetMethod("ReadDep").Invoke(srcDerived, null) != null;
                bool srcPostRan = (bool)srcPostT.GetField("PostRan").GetValue(srcPost);
                bool srcPostInjected = srcPostT.GetField("Dep").GetValue(srcPost) != null;

                ok = srcFactory && srcInjector && srcValueSkipped
                     && srcChainInjector && srcPostInjector
                     && harnessFactory && harnessMarked && harnessValueSkipped
                     && harnessInjectorAbsent && metadataInjectorAbsent && nonEmittableInjectorAbsent
                     && srcCtorDep != null && ReferenceEquals(srcCtorDep, shared)
                     && srcFixtureInjected && ReferenceEquals(srcFixtureDep, shared)
                     && ReferenceEquals(srcOverrideDep, manual)
                     && srcDerivedInjected && srcPostRan && srcPostInjected
                     && target.Dep != null && !multi.ParameterlessUsed && injected.Dep != null
                     && visibleBase.ReadDep() != null && emittableMember.HasDep && nonEmittableMember.HasDep;
                detail = $"srcFactory={srcFactory} srcInjector={srcInjector} srcValueSkipped={srcValueSkipped} " +
                         $"srcChainInjector={srcChainInjector} srcPostInjector={srcPostInjector} " +
                         $"harnessFactory={harnessFactory} harnessMarked={harnessMarked} harnessValueSkipped={harnessValueSkipped} " +
                         $"harnessInjectorAbsent={harnessInjectorAbsent} metadataInjectorAbsent={metadataInjectorAbsent} " +
                         $"nonEmittableInjectorAbsent={nonEmittableInjectorAbsent} " +
                         $"srcCtor={srcCtorDep != null} srcInjected={srcFixtureInjected} " +
                         $"srcOverrideWins={ReferenceEquals(srcOverrideDep, manual)} " +
                         $"srcChainInjected={srcDerivedInjected} srcPostRan={srcPostRan} srcPostInjected={srcPostInjected} " +
                         $"targetReadonly={target.Dep != null} markedCtorWon={!multi.ParameterlessUsed} injected={injected.Dep != null} " +
                         $"visibleBaseInjected={visibleBase.ReadDep() != null} emittableInjected={emittableMember.HasDep} " +
                         $"nonEmittableInjected={nonEmittableMember.HasDep}";
            }
            catch (Exception ex)
            {
                string inner = ex.InnerException != null
                    ? $" → {ex.InnerException.GetType().Name}: {ex.InnerException.Message}"
                    : string.Empty;
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}{inner}";
            }
            finally
            {
                try { container?.Dispose(); } catch (Exception ex) { Console.WriteLine($"[CodeGen] container.Dispose teardown: {ex.Message}"); }
                container = null;
                try { NexusRuntime.Reset(); } catch (Exception ex) { Console.WriteLine($"[CodeGen] NexusRuntime.Reset teardown: {ex.Message}"); }
                try { if (Directory.Exists(cg2Dir)) Directory.Delete(cg2Dir, true); } catch { }
            }
            Report("CG2. SourceGenerator_Emits_Compiles_And_Boots", ok, detail);
        }

        private static void Test_Codegen_Emits_Compiles_And_Boots()
        {
            string rootDir = NexusEditorSettings.OutputRoot;
            string binderPath = Path.Combine(rootDir, "NexusGeneratedBinder.g.cs");
            bool ok = false;
            string detail;
            NexusDI container = null;
            try
            {
                if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
                // Clear any stale factories/injectors so the generated binder is authoritative.
                NexusRuntime.Reset();

                NexusCodeGenerator.GenerateBinder();

                if (!File.Exists(binderPath))
                {
                    detail = $"generated binder file missing at {binderPath}";
                    Report("CG1. Codegen_Emits_Compiles_And_Boots", false, detail);
                    return;
                }

                string source = File.ReadAllText(binderPath);
                bool hasTargetFactory = source.Contains("RegisterConstructorFactory<NexusBench.CodeGenTarget>");
                bool hasMarkedFactory = source.Contains("RegisterConstructorFactory<NexusBench.CodeGenMulti>");
                bool hasInjector = source.Contains("RegisterInjector<NexusBench.CodeGenInjected>");
                bool skippedValueParam = !source.Contains("RegisterConstructorFactory<NexusBench.CodeGenSkippedValueParam>");
                // Base-class chain walk: a public base with a private [Inject] field IS emittable
                // (visible declaring+member types) so the derived injector must exist and inject it;
                // a PRIVATE nested base is NOT referenceable, so the derived injector must be
                // skipped entirely (all-or-nothing) and the reflection path serves it.
                bool visibleBaseInjector = source.Contains("RegisterInjector<NexusBench.CodeGenHierarchy.CodeGenDerivedWithVisibleBase>");
                bool emittableMemberInjector = source.Contains("RegisterInjector<NexusBench.CodeGenHierarchy.CodeGenEmittableMember>");
                bool nonEmittableInjectorAbsent = !source.Contains("RegisterInjector<NexusBench.CodeGenHierarchy.CodeGenNonEmittableMember>");
                bool postConstructInjector = source.Contains("RegisterInjector<NexusBench.CodeGenPostConstruct>");

                string[] compileErrors = CompileGeneratedBinder(source, out Assembly binderAssembly);
                if (compileErrors.Length > 0)
                {
                    detail = "emitted binder FAILED to compile: " +
                             string.Join(" | ", compileErrors.Take(3)) +
                             $" (total {compileErrors.Length} errors)";
                    Report("CG1. Codegen_Emits_Compiles_And_Boots", false, detail);
                    return;
                }

                // Boot the generated binder: registers factories/injectors/dispatchers into the
                // SAME NexusDI statics the harness uses (default ALC, type identity is shared).
                Type binderType = binderAssembly.GetType("Nexus.Generated.NexusGeneratedBinder");
                if (binderType == null)
                {
                    detail = "emitted binder compiled but Nexus.Generated.NexusGeneratedBinder type not found";
                    Report("CG1. Codegen_Emits_Compiles_And_Boots", false, detail);
                    return;
                }
                binderType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);

                container = new NexusDI();
                container.Bind<CodeGenDep>(Lifetime.Scoped);
                container.Bind<CodeGenTarget>(Lifetime.Transient);
                container.Bind<CodeGenMulti>(Lifetime.Transient);
                container.Bind<CodeGenInjected>(Lifetime.Transient);
                container.Bind<CodeGenPostConstruct>(Lifetime.Transient);
                container.Bind<CodeGenHierarchy.CodeGenDerivedWithVisibleBase>(Lifetime.Transient);
                container.Bind<CodeGenHierarchy.CodeGenEmittableMember>(Lifetime.Transient);
                container.Bind<CodeGenHierarchy.CodeGenNonEmittableMember>(Lifetime.Transient);
                container.Bind<CodeGenHierarchy.InternalDep>(Lifetime.Scoped);

                var target = container.Resolve<CodeGenTarget>();      // generated factory
                var multi = container.Resolve<CodeGenMulti>();        // generated [Construct] factory
                var injected = container.Resolve<CodeGenInjected>();  // generated injector
                var postConstruct = container.Resolve<CodeGenPostConstruct>(); // generated injector + [PostConstruct]
                var shared = container.Resolve<CodeGenDep>();
                var visibleBase = container.Resolve<CodeGenHierarchy.CodeGenDerivedWithVisibleBase>();  // generated injector, base member
                var emittableMember = container.Resolve<CodeGenHierarchy.CodeGenEmittableMember>();      // generated injector
                var nonEmittableMember = container.Resolve<CodeGenHierarchy.CodeGenNonEmittableMember>(); // reflection injector (all-or-nothing skip)

                // WithParameter override must beat the generated factory (regression for the
                // silent-drop bug) — proves the fix through the REAL emitted binder.
                var manual = new CodeGenDep();
                container.BindFluent<CodeGenOverride>().To<CodeGenOverride>().AsTransient().WithParameter<CodeGenDep>(manual);
                var overridden = container.Resolve<CodeGenOverride>();

                ok = hasTargetFactory && hasMarkedFactory && hasInjector && skippedValueParam
                     && visibleBaseInjector && emittableMemberInjector && nonEmittableInjectorAbsent
                     && postConstructInjector
                     && target.Dep != null && ReferenceEquals(target.Dep, shared)
                     && multi.Dep != null && !multi.ParameterlessUsed
                     && injected.Dep != null
                     && postConstruct.PostRan
                     && visibleBase.ReadDep() != null && emittableMember.HasDep && nonEmittableMember.HasDep
                     && ReferenceEquals(overridden.Dep, manual);
                detail = $"emittedFactory={hasTargetFactory} markedCtorFactory={hasMarkedFactory} injector={hasInjector} " +
                         $"valueParamSkipped={skippedValueParam} visibleBaseInjector={visibleBaseInjector} " +
                         $"emittableMemberInjector={emittableMemberInjector} nonEmittableInjectorAbsent={nonEmittableInjectorAbsent} " +
                         $"postConstructInjector={postConstructInjector} " +
                         $"targetReadonly={target.Dep != null} markedCtorWon={!multi.ParameterlessUsed} injected={injected.Dep != null} " +
                         $"postConstructRan={postConstruct.PostRan} " +
                         $"visibleBaseInjected={visibleBase.ReadDep() != null} emittableInjected={emittableMember.HasDep} " +
                         $"nonEmittableInjected={nonEmittableMember.HasDep} " +
                         $"withParamOverrideWins={ReferenceEquals(overridden.Dep, manual)}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { container?.Dispose(); } catch (Exception ex) { Console.WriteLine($"[CodeGen] container.Dispose teardown: {ex.Message}"); }
                container = null;
                try { NexusRuntime.Reset(); } catch (Exception ex) { Console.WriteLine($"[CodeGen] NexusRuntime.Reset teardown: {ex.Message}"); }
                // Keep the emitted binder on failure so the failing output can be inspected.
                if (ok)
                {
                    try { if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true); } catch { }
                }
            }
            Report("CG1. Codegen_Emits_Compiles_And_Boots", ok, detail);
        }

        /// <summary>
        /// Compiles the emitted binder source with the Roslyn that ships inside the .NET SDK
        /// (no NuGet restore needed). References = every assembly already loaded in the harness,
        /// so the binder sees the exact same System + NexusBenchmark surface it would see in
        /// Unity's compilation. Returns the error diagnostics when compilation fails.
        /// </summary>
        private static string[] CompileGeneratedBinder(string source, out Assembly assembly)
            => CompileSource(source, "NexusGeneratedBinderHarness", out assembly, out _);

        /// <summary>Compiles a source string to an in-memory assembly (byte-loaded: empty
        /// Location), returning the emitted PE so callers can build explicit references to it.
        /// <paramref name="extraReferences"/> are appended after the AppDomain-wide scan.</summary>
        private static string[] CompileSource(string source, string assemblyName, out Assembly assembly, out byte[] peBytes, params MetadataReference[] extraReferences)
        {
            assembly = null;
            peBytes = null;
            var references = new List<MetadataReference>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                try
                {
                    string location = asm.Location;
                    if (string.IsNullOrEmpty(location)) continue;
                    references.Add(MetadataReference.CreateFromFile(location));
                }
                catch { }
            }
            if (extraReferences != null) references.AddRange(extraReferences);

            var syntaxTree = CSharpSyntaxTree.ParseText(source);
            var compilation = CSharpCompilation.Create(
                assemblyName,
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using (var pe = new MemoryStream())
            {
                var emit = compilation.Emit(pe);
                if (!emit.Success)
                {
                    return emit.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => d.ToString())
                        .ToArray();
                }
                pe.Position = 0;
                peBytes = pe.ToArray();
                assembly = Assembly.Load(peBytes);
            }
            return Array.Empty<string>();
        }

        /// <summary>True when the emitted binder contains a registration of the form
        /// <c>Kind&lt;[global::]Type</c> (the SG emits fully-qualified names with a `global::`
        /// prefix; the editor generator emits plain full names).</summary>
        private static bool HasRegistration(string source, string kind, string typeName)
            => source.Contains($"{kind}<global::{typeName}") || source.Contains($"{kind}<{typeName}");

        private static void Report(string name, bool ok, string detail)
        {
            Console.WriteLine($"[CodeGen] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("CodeGen", name, ok, detail);
            if (!ok) _failures++;
        }
    }
}
