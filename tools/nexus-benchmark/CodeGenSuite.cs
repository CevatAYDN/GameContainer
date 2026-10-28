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

    /// <summary>Never bound: an [OptionalInject] member of this type must stay null instead of throwing.</summary>
    public sealed class CodeGenUnbound { }

    /// <summary>[Inject] field → the generator must emit an injector; the [OptionalInject] one must use TryResolve.</summary>
    public sealed class CodeGenInjected
    {
        [Inject] public CodeGenDep Dep;
        [OptionalInject] public CodeGenUnbound Missing;
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

    /// <summary>[Inject(Name)] on a field, a property (one optional) and a method parameter: the generated
    /// injector must resolve the named binding as the reflection path does (issue #81).</summary>
    public sealed class CodeGenNamed
    {
        [Inject(Name = "special")] public CodeGenDep Field;
        [Inject(Name = "special")] public CodeGenDep Property { get; private set; }
        [Inject(Name = "special"), OptionalInject] public CodeGenDep Optional { get; set; }
        public CodeGenDep FromMethod;

        [Inject]
        public void Take([Inject(Name = "special")] CodeGenDep dep) => FromMethod = dep;
    }

    /// <summary>Value-type ctor param → the generator must SKIP the factory (reflection handles it).</summary>
    public sealed class CodeGenSkippedValueParam
    {
        public readonly int Count;
        public readonly CodeGenDep Dep;
        public CodeGenSkippedValueParam(int count, CodeGenDep dep) { Count = count; Dep = dep; }
    }

    /// <summary>MonoBehaviour: Unity owns its instantiation, so a constructor factory (`new T()`) is never
    /// valid — but the [Inject] member must still get a generated injector.</summary>
    public sealed class CodeGenUnityBehaviour : UnityEngine.MonoBehaviour
    {
        [Inject] public CodeGenDep Dep;
    }

    /// <summary>ScriptableObject: created via ScriptableObject.CreateInstance, never `new`.</summary>
    public sealed class CodeGenUnityAsset : UnityEngine.ScriptableObject { }

    /// <summary>Mediator with a single public ctor: ViewBinder resolves mediators through DI, so the
    /// constructor factory must stay.</summary>
    public sealed class CodeGenMediator : Mediator<CodeGenDep>
    {
        public readonly CodeGenDep Dep;
        public CodeGenMediator(CodeGenDep dep) { Dep = dep; }
    }

    /// <summary>Created only through Activator (a convention lifecycle, an unbound AddSignalFilter type) and
    /// carrying no [Inject] member: link.xml must still keep them (issue #37).</summary>
    public sealed class CodeGenConventionLifecycle : IContextLifecycle
    {
        public void OnConfigure(IContextBuilder builder) { }
        public System.Threading.Tasks.ValueTask OnInitializeAsync(System.Threading.CancellationToken ct) => default;
        public System.Threading.Tasks.ValueTask OnStartAsync(System.Threading.CancellationToken ct) => default;
        public void OnDispose() { }
    }

    public struct CodeGenFilteredSignal { }

    public sealed class CodeGenUnboundFilter : ISignalFilter<CodeGenFilteredSignal>
    {
        public bool OnFilter(ref CodeGenFilteredSignal signal) => true;
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
            Test_Codegen_Skips_Factories_For_UnityObjects();
            Test_ConstructorFactoryPolicy_Decisions();
            Test_LinkXml_Keeps_ActivatorCreatedTypes();
            Test_PackageAssemblies_Recognized_And_Preserved();
            // Last: it loads an assembly named like a package, which every later scan would see.
            Test_Binders_Register_No_Package_Assembly_Type();

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
                NexusDI.ClearGeneratedRegistrations();

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
                var references = LoadedAssemblyReferences();
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
                try { NexusRuntime.Reset(); NexusDI.ClearGeneratedRegistrations(); } catch (Exception ex) { Console.WriteLine($"[CodeGen] NexusRuntime.Reset teardown: {ex.Message}"); }
                try { if (Directory.Exists(cg2Dir)) Directory.Delete(cg2Dir, true); } catch { }
            }
            Report("CG2. SourceGenerator_Emits_Compiles_And_Boots", ok, detail);
        }

        // ─── CG3/CG4: constructor-factory gate ─────────────────────────────────
        // A constructor factory is `new T(...)`, which is only valid for plain C# types:
        // UnityEngine.Object types (MonoBehaviour, ScriptableObject...) are instantiated by Unity.
        // The generator must emit no factory for them, while plain DI types and mediators (ViewBinder
        // resolves mediators through DI) keep theirs and Unity objects keep their [Inject] injectors.
        // The packages' own types get no registration at all (CG6).

        private static void Test_Codegen_Skips_Factories_For_UnityObjects()
        {
            string rootDir = NexusEditorSettings.OutputRoot;
            string binderPath = Path.Combine(rootDir, "NexusGeneratedBinder.g.cs");
            bool ok = false;
            string detail;
            try
            {
                if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
                NexusCodeGenerator.GenerateBinder();
                string source = File.ReadAllText(binderPath);

                bool behaviourSkipped = !source.Contains("RegisterConstructorFactory<NexusBench.CodeGenUnityBehaviour>");
                bool assetSkipped = !source.Contains("RegisterConstructorFactory<NexusBench.CodeGenUnityAsset>");
                bool behaviourInjector = source.Contains("RegisterInjector<NexusBench.CodeGenUnityBehaviour>");
                bool plainFactory = source.Contains("RegisterConstructorFactory<NexusBench.CodeGenTarget>");
                bool mediatorFactory = source.Contains("RegisterConstructorFactory<NexusBench.CodeGenMediator>");

                ok = behaviourSkipped && assetSkipped && behaviourInjector && plainFactory && mediatorFactory;
                detail = $"behaviourFactorySkipped={behaviourSkipped} assetFactorySkipped={assetSkipped} " +
                         $"behaviourInjectorKept={behaviourInjector} plainFactoryKept={plainFactory} mediatorFactoryKept={mediatorFactory}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true); } catch { }
            }
            Report("CG3. Codegen_Skips_Factories_For_UnityObjects", ok, detail);
        }

        private static void Test_LinkXml_Keeps_ActivatorCreatedTypes()
        {
            string rootDir = NexusEditorSettings.OutputRoot;
            bool ok = false;
            string detail;
            try
            {
                if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
                NexusCodeGenerator.GenerateBinder();
                string linkXml = File.ReadAllText(Path.Combine(rootDir, "link.xml"));
                bool lifecycleKept = linkXml.Contains("fullname=\"NexusBench.CodeGenConventionLifecycle\"");
                bool filterKept = linkXml.Contains("fullname=\"NexusBench.CodeGenUnboundFilter\"");
                ok = lifecycleKept && filterKept;
                detail = $"convention lifecycle kept={lifecycleKept}, unbound signal filter kept={filterKept} (pre-fix: neither, they carry no [Inject])";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true); } catch { }
            }
            Report("CG5. LinkXml_Keeps_ActivatorCreatedTypes", ok, detail);
        }

        // ─── CG6: a game's binder registers no type of a Nexus package (#110) ───
        // A package type registered by a game's binder breaks that game's compile when the package removes it
        // (FloatingTextService #91, AdMobAdapter #14). The harness runtime compiles as NexusBenchmark, which no
        // package-name rule matches, so the rule is proven on a fixture assembly that carries a package's NAME:
        // neither generator may register, preserve or list its types, while the game assembly keeps its own.

        private const string PackageFixtureAssembly = "com.nexus.harnessfixture";

        private const string PackageFixtureSource = @"
using Nexus.Core;
using NexusBench;

namespace NexusBench.PackageFixture
{
    public sealed class PackageInjected
    {
        [Inject] public CodeGenDep Dep;
    }

    public sealed class PackageCtorService
    {
        public readonly CodeGenDep Dep;
        public PackageCtorService(CodeGenDep dep) { Dep = dep; }
    }

    public struct PackageSignal { }

    public sealed class PackageCommand : ICommand<PackageSignal>
    {
        public void Execute(PackageSignal signal) { }
    }

    public struct PackageNetworkSignal : Nexus.Netcode.INetworkSignal { }
}";

        private static void Test_Binders_Register_No_Package_Assembly_Type()
        {
            const string name = "CG6. Binders_Register_No_Package_Assembly_Type";
            const string fixtureNamespace = "NexusBench.PackageFixture.";
            string rootDir = NexusEditorSettings.OutputRoot;
            bool ok = false;
            string detail;
            try
            {
                // Persisted and file-loaded (as in CG2) so the source generator's compilation can reference it.
                string[] errors = CompileSource(PackageFixtureSource, PackageFixtureAssembly, out _, out byte[] pe);
                if (errors.Length > 0)
                {
                    Report(name, false, "package fixture FAILED to compile: " + string.Join(" | ", errors.Take(3)));
                    return;
                }
                string fixtureDir = Path.Combine(Path.GetTempPath(), "nexus-cg6");
                Directory.CreateDirectory(fixtureDir);
                string fixturePath = Path.Combine(fixtureDir, PackageFixtureAssembly + ".dll");
                File.WriteAllBytes(fixturePath, pe);
                Assembly.LoadFrom(fixturePath);

                if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
                NexusCodeGenerator.GenerateBinder();
                string binder = File.ReadAllText(Path.Combine(rootDir, "NexusGeneratedBinder.g.cs"));
                string linkXml = File.ReadAllText(Path.Combine(rootDir, "link.xml"));
                bool editorBinderClean = !binder.Contains(fixtureNamespace);
                bool editorLinkClean = !linkXml.Contains(fixtureNamespace) && !linkXml.Contains($"fullname=\"{PackageFixtureAssembly}\"");
                bool editorGameKept = binder.Contains("RegisterConstructorFactory<NexusBench.CodeGenTarget>");

                var compilation = CSharpCompilation.Create(
                    "NexusBench.CG6Game",
                    new[] { CSharpSyntaxTree.ParseText("namespace NexusBench.CG6Game { public sealed class Marker { } }") },
                    LoadedAssemblyReferences(),
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                CSharpGeneratorDriver.Create(new Nexus.Generator.NexusBinderGenerator())
                    .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
                string generated = output.SyntaxTrees.FirstOrDefault(t => t.FilePath.EndsWith("NexusGeneratedBinder.g.cs"))?.ToString() ?? string.Empty;
                bool generatorClean = generated.Length > 0 && !generated.Contains(fixtureNamespace);
                bool generatorGameKept = HasRegistration(generated, "RegisterConstructorFactory", "NexusBench.CodeGenTarget");

                ok = editorBinderClean && editorLinkClean && editorGameKept && generatorClean && generatorGameKept;
                detail = $"editorBinderClean={editorBinderClean} editorLinkXmlClean={editorLinkClean} editorGameKept={editorGameKept} " +
                         $"sourceGeneratorClean={generatorClean} sourceGeneratorGameKept={generatorGameKept}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true); } catch { }
            }
            Report(name, ok, detail);
        }

        // ─── CG7: every Nexus package assembly is recognized and preserves itself (#110) ───
        // Read from the repository's asmdefs, so a new package is covered the day it is added. A runtime assembly
        // the two package predicates (editor and source generator) miss would be registered by games again; one
        // that references the core without an <assembly> element in its package's link.xml would lose its
        // reflection-constructed types to IL2CPP stripping above the default level (the editor's NexusPackagesLinkXml
        // hands those files to the linker); one compiled only under a define needs ignoreIfMissing, or a project
        // without that SDK fails to build. A package link.xml that does not parse is skipped by the editor, so it fails here.

        private static void Test_PackageAssemblies_Recognized_And_Preserved()
        {
            const string name = "CG7. PackageAssemblies_Recognized_And_Preserved";
            bool ok = false;
            string detail;
            try
            {
                string packages = new[] { "../../Nexus/Packages", "Nexus/Packages" }.FirstOrDefault(Directory.Exists);
                if (packages == null)
                {
                    Report(name, false, "Nexus/Packages not found from the working directory");
                    return;
                }
                string coreGuid = File.ReadAllLines(Path.Combine(packages, "com.nexus.core/Runtime/com.nexus.core.asmdef.meta"))
                    .First(l => l.StartsWith("guid: ", StringComparison.Ordinal)).Substring(6).Trim();

                var problems = new List<string>();
                foreach (string linkXml in Directory.GetFiles(packages, "link.xml", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (System.Xml.Linq.XDocument.Load(linkXml).Root?.Name != "linker") problems.Add($"{linkXml}: the root is not <linker>");
                    }
                    catch (Exception ex)
                    {
                        problems.Add($"{linkXml}: {ex.Message}");
                    }
                }

                int checkedAssemblies = 0;
                foreach (string asmdef in Directory.GetFiles(packages, "*.asmdef", SearchOption.AllDirectories))
                {
                    using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(asmdef));
                    var root = json.RootElement;
                    string assembly = root.GetProperty("name").GetString();
                    if (NexusAssemblyPolicy.IsTestAssembly(assembly) || NexusAssemblyPolicy.IsEditorAssembly(assembly)) continue;
                    checkedAssemblies++;

                    if (!NexusAssemblyPolicy.IsNexusPackageAssembly(assembly) || !Nexus.Generator.NexusBinderGenerator.IsNexusPackageAssembly(assembly))
                        problems.Add($"{assembly}: not recognized as a package assembly");

                    bool referencesCore = assembly == "com.nexus.core"
                        || (root.TryGetProperty("references", out var refs)
                            && refs.EnumerateArray().Any(r => r.GetString() == "com.nexus.core" || r.GetString() == "GUID:" + coreGuid));
                    if (!referencesCore) continue;

                    string package = Path.Combine(packages, Path.GetRelativePath(packages, asmdef).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0]);
                    string element = Directory.GetFiles(package, "link.xml", SearchOption.AllDirectories)
                        .SelectMany(File.ReadAllLines)
                        .FirstOrDefault(l => l.Contains($"<assembly fullname=\"{assembly}\""));
                    bool gated = root.TryGetProperty("defineConstraints", out var defines) && defines.GetArrayLength() > 0;
                    if (element == null) problems.Add($"{assembly}: no <assembly> element in a link.xml of its package");
                    else if (gated && !element.Contains("ignoreIfMissing=\"true\"")) problems.Add($"{assembly}: compiled under a define, but its link.xml element has no ignoreIfMissing");
                }

                // Game assemblies, the harness runtime and empty names stay game code for both predicates.
                string[] gameNames = { "Assembly-CSharp", "Gigawatt", "Wickling.Monetization", "ChipCrisis.Game", "NexusBenchmark", "Nexus.Starter", string.Empty, null };
                foreach (string game in gameNames)
                    if (NexusAssemblyPolicy.IsNexusPackageAssembly(game) || Nexus.Generator.NexusBinderGenerator.IsNexusPackageAssembly(game))
                        problems.Add($"'{game}': taken for a package assembly");

                ok = checkedAssemblies > 0 && problems.Count == 0;
                detail = $"{checkedAssemblies} package runtime assemblies checked; " + (problems.Count == 0 ? "no problems" : string.Join(" | ", problems));
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            Report(name, ok, detail);
        }

        private static void Test_ConstructorFactoryPolicy_Decisions()
        {
            bool ok = false;
            string detail;
            try
            {
                bool behaviourSkipped = !NexusCodeGenerator.CanEmitConstructorFactory(typeof(CodeGenUnityBehaviour));
                bool assetSkipped = !NexusCodeGenerator.CanEmitConstructorFactory(typeof(CodeGenUnityAsset));
                bool plainKept = NexusCodeGenerator.CanEmitConstructorFactory(typeof(CodeGenTarget));
                bool mediatorKept = NexusCodeGenerator.CanEmitConstructorFactory(typeof(CodeGenMediator));

                ok = behaviourSkipped && assetSkipped && plainKept && mediatorKept;
                detail = $"behaviourSkipped={behaviourSkipped} assetSkipped={assetSkipped} plainKept={plainKept} " +
                         $"mediatorKept={mediatorKept}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }
            Report("CG4. ConstructorFactoryPolicy_Decisions", ok, detail);
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
                NexusDI.ClearGeneratedRegistrations();

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
                bool namedEmitted = source.Contains("instance.Field = di.Resolve<NexusBench.CodeGenDep>(\"special\")")
                                    && source.Contains("di.TryResolve<NexusBench.CodeGenDep>(\"special\")");
                // [OptionalInject] mirrors the runtime: TryResolve (unbound stays null, never throws), and the clearer nulls it.
                bool optionalEmitted = source.Contains("instance.Missing = di.TryResolve<NexusBench.CodeGenUnbound>();")
                                       && source.Contains("instance.Missing = null;")
                                       && !source.Contains("di.Resolve<NexusBench.CodeGenUnbound>");

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
                var special = new CodeGenDep();
                container.BindInstance<CodeGenDep>("special", special);
                container.Bind<CodeGenNamed>(Lifetime.Transient);

                var target = container.Resolve<CodeGenTarget>();      // generated factory
                var multi = container.Resolve<CodeGenMulti>();        // generated [Construct] factory
                var injected = container.Resolve<CodeGenInjected>();  // generated injector
                var postConstruct = container.Resolve<CodeGenPostConstruct>(); // generated injector + [PostConstruct]
                var shared = container.Resolve<CodeGenDep>();
                var visibleBase = container.Resolve<CodeGenHierarchy.CodeGenDerivedWithVisibleBase>();  // generated injector, base member
                var emittableMember = container.Resolve<CodeGenHierarchy.CodeGenEmittableMember>();      // generated injector
                var nonEmittableMember = container.Resolve<CodeGenHierarchy.CodeGenNonEmittableMember>(); // reflection injector (all-or-nothing skip)
                var named = container.Resolve<CodeGenNamed>();                                          // generated injector, named members
                bool namedResolved = ReferenceEquals(named.Field, special) && ReferenceEquals(named.Property, special)
                                     && ReferenceEquals(named.Optional, special) && ReferenceEquals(named.FromMethod, special);

                // WithParameter override must beat the generated factory (regression for the
                // silent-drop bug) — proves the fix through the REAL emitted binder.
                var manual = new CodeGenDep();
                container.BindFluent<CodeGenOverride>().To<CodeGenOverride>().AsTransient().WithParameter<CodeGenDep>(manual);
                var overridden = container.Resolve<CodeGenOverride>();

                ok = hasTargetFactory && hasMarkedFactory && hasInjector && skippedValueParam
                     && visibleBaseInjector && emittableMemberInjector && nonEmittableInjectorAbsent
                     && postConstructInjector && namedEmitted && namedResolved && optionalEmitted
                     && target.Dep != null && ReferenceEquals(target.Dep, shared)
                     && multi.Dep != null && !multi.ParameterlessUsed
                     && injected.Dep != null && injected.Missing == null
                     && postConstruct.PostRan
                     && visibleBase.ReadDep() != null && emittableMember.HasDep && nonEmittableMember.HasDep
                     && ReferenceEquals(overridden.Dep, manual);
                detail = $"emittedFactory={hasTargetFactory} markedCtorFactory={hasMarkedFactory} injector={hasInjector} " +
                         $"valueParamSkipped={skippedValueParam} visibleBaseInjector={visibleBaseInjector} " +
                         $"emittableMemberInjector={emittableMemberInjector} nonEmittableInjectorAbsent={nonEmittableInjectorAbsent} " +
                         $"postConstructInjector={postConstructInjector} namedEmitted={namedEmitted} namedResolved={namedResolved} " +
                         $"optionalEmitted={optionalEmitted} optionalUnboundNull={injected.Missing == null} " +
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
                try { NexusRuntime.Reset(); NexusDI.ClearGeneratedRegistrations(); } catch (Exception ex) { Console.WriteLine($"[CodeGen] NexusRuntime.Reset teardown: {ex.Message}"); }
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
            var references = LoadedAssemblyReferences();
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

        /// <summary>Every file-backed assembly loaded in the harness: the surface the binder sees in Unity's compilation.
        /// Byte-loaded assemblies have no Location and are skipped.</summary>
        private static List<MetadataReference> LoadedAssemblyReferences()
        {
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
            return references;
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
