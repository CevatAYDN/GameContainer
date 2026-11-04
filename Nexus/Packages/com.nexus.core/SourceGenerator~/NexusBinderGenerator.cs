// ------------------------------------------------------------------------------
// Nexus AOT Binder — Roslyn Source Generator
//
// Compile-time counterpart of the editor-time NexusCodeGenerator: discovers
// [Inject]/[OptionalInject]/[Construct] types, single-public-ctor factories and
// generic-only ICommand<T>/IAsyncCommand<T> implementations in the CURRENT
// compilation plus every game-relevant referenced assembly (the Nexus packages'
// own assemblies excluded: each preserves its types itself, #110), and emits the same
// NexusGeneratedBinder shape the editor generator produces (RegisterConstructorFactory
// / RegisterInjector / RegisterClearer / RegisterGeneric*Dispatcher).
// It runs only in a game's runtime assembly: Unity applies Assets/csc.rsp to every assembly
// without a csc.rsp of its own, and a Nexus package, editor or test compilation gets no
// binder and no diagnostic (#264).
//
// Semantics (#191): the generated path gives the reflection path's answers, proven by the harness
// CG10 test, which runs every case through reflection, the editor binder and this generator:
//   • members are selected as NexusDI's MetadataCache selects them: base-first, a property at the
//     first level that carries the attribute and once per name, a method once per root definition
//     (a virtual [Inject] method runs once, through its override);
//   • every member and parameter resolves through TryResolve with its [Inject(Name)]; an unbound
//     required one goes to the runtime (ReportMissingMember / ReportMissingParameter: strict throw,
//     or log and ReInjectAll), an unbound optional one keeps its value; a LazyInjection<T> member
//     gets the runtime's lazy wrapper;
//   • an [OptionalInject] or null-default constructor parameter is TryResolve; a type with any other
//     default keeps the reflection path, which passes the declared default;
//   • each injector carries its member signature: the runtime drops an injector generated for other
//     members than the type has;
//   • a static, get-only or value-type [Inject] member is a compile error (NEXUS_SG001, NEXUS_SG003),
//     as the reflection path throws for it.
//
// Gates kept from the CG1/CG2 tests:
//   • visibility gate  — non-visible types (private/internal nested) are never referenced;
//   • compiler-generated types (`<>c__DisplayClass*`) are skipped;
//   • value-type ctor params skip the factory; unnameable/open-generic types skip;
//   • all-or-nothing injectors — a single member the binder cannot name drops the WHOLE injector for
//     the type so the reflection path serves it instead of a partial AOT injector;
//   • injectors/clearers are emitted ONLY for types in the CURRENT compilation: each Unity asmdef runs
//     its own generator instance, so its own types get complete injectors. Their base classes may come
//     from a referenced assembly: the compilation imports every metadata member (private ones
//     included), so a private base member is set through its cached FieldInfo/PropertyInfo. Ctor
//     factories and command dispatchers stay safe for referenced types (their signatures are fully
//     visible and every parameter type is visibility-gated).
//   • [PostConstruct] is a runtime guarantee: Injector.Inject runs [PostConstruct] methods
//     after ANY custom (generated) injector, so generated binders cannot skip them.
//   • WithParameter precedence is a runtime guarantee (Injector.CreateInstance skips the
//     factory when overrides are present) — the generated factory resolves from the
//     container, so explicit constructor arguments always win.
//
// Compatibility: compiled against Microsoft.CodeAnalysis.CSharp 4.10.0 (Unity 6000.6's
// Roslyn) as a netstandard2.0 assembly shipping inside the package; the same source is
// compiled directly into tools/nexus-benchmark (Roslyn from the .NET SDK) and driven by
// the CG2 and CG10 harness tests, so the shipped generator cannot drift from what is proven there.
// ------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Nexus.Generator
{
    [Generator]
    public sealed class NexusBinderGenerator : IIncrementalGenerator
    {
        private const string InjectAttribute = "Nexus.Core.InjectAttribute";
        private const string OptionalInjectAttribute = "Nexus.Core.OptionalInjectAttribute";
        private const string ConstructAttribute = "Nexus.Core.ConstructAttribute";
        private const string CompilerGeneratedAttribute = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";
        private const string LazyInjection = "Nexus.Core.LazyInjection<T>";

        // Mirror of NexusAssemblyPolicy's name lists and rules, which this netstandard2.0 analyzer cannot reference; the
        // harness (CG7) fails when the two disagree on a game, framework, tool, editor or test assembly name (#191).
        private static readonly string[] FrameworkNames =
        {
            "System", "Microsoft", "Unity", "UnityEngine", "UnityEditor", "UnityPipeline", "mscorlib", "Mono", "monodoc", "nunit",
            "netstandard", "StandardSocketsHttpHandler", "SystemWebTestShim"
        };

        private static readonly string[] ThirdPartyNames =
        {
            "Newtonsoft", "Grpc", "ExCSS", "log4net", "TextMateSharp", "JetBrains", "Onigwrap", "unityplastic", "Codice", "Plastic",
            "MCPForUnity", "Bee", "BeeBuildProgramCommon", "NiceIO", "GLTFast", "Google.Protobuf", "I18N", "AndroidPlayerBuildProgram",
            "PlayerBuildProgram", "PlayerBuildProgramLibrary", "ScriptCompilation", "ScriptCompilationBuildProgram",
            "WinPlayerBuildProgram", "BuildProgram"
        };

        // Mirror of NexusAssemblyPolicy.IsNexusPackageAssembly, which this netstandard2.0 analyzer cannot reference;
        // the harness (CG7) fails when the two disagree on a package or game assembly name.
        private static readonly string[] NexusPackagePrefixes = { "com.nexus.", "Nexus.Ads." };

        private static readonly DiagnosticDescriptor s_valueTypeMemberError = new DiagnosticDescriptor(
            id: "NEXUS_SG001",
            title: "Value-type injection member is not supported",
            messageFormat: "'{0}' has [Inject]/[OptionalInject] but is a value type ({1}). Nexus injection only supports reference-type dependencies.",
            category: "Nexus.AOT",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor s_internalError = new DiagnosticDescriptor(
            id: "NEXUS_SG002",
            title: "Nexus AOT binder generation failed",
            messageFormat: "{0}",
            category: "Nexus.AOT",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor s_unsettableMemberError = new DiagnosticDescriptor(
            id: "NEXUS_SG003",
            title: "[Inject] member cannot be set",
            messageFormat: "'{0}' has [Inject]/[OptionalInject] but {1}",
            category: "Nexus.AOT",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterSourceOutput(context.CompilationProvider, (spc, compilation) =>
            {
                try
                {
                    Emit(spc, compilation);
                }
                catch (Exception ex)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(s_internalError, Location.None, ex.Message));
                }
            });
        }

        // ─── Discovery ────────────────────────────────────────────────────────

        internal static bool IsGameAssembly(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (MatchesName(name, FrameworkNames) || MatchesName(name, ThirdPartyNames)) return false;
            if (name.EndsWith(".testing", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.EndsWith("-Editor", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-Editor-firstpass", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var segment in name.Split('.'))
            {
                if (segment.Equals("tests", StringComparison.OrdinalIgnoreCase) || segment.EndsWith("Tests", StringComparison.Ordinal)) return false;
                if (segment.Equals("editor", StringComparison.OrdinalIgnoreCase)) return false;
                if (segment.Length > 6 && segment.StartsWith("Editor", StringComparison.Ordinal)
                    && (char.IsUpper(segment[6]) || char.IsDigit(segment[6]))) return false;
            }
            return true;
        }

        private static bool MatchesName(string name, string[] entries)
        {
            foreach (var entry in entries)
            {
                if (name.StartsWith(entry, StringComparison.OrdinalIgnoreCase) && (name.Length == entry.Length || name[entry.Length] == '.'))
                    return true;
            }
            return false;
        }

        /// <summary>The Nexus packages' own assemblies: a game's binder registers none of their types (#110).</summary>
        internal static bool IsNexusPackageAssembly(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < NexusPackagePrefixes.Length; i++)
            {
                if (name.StartsWith(NexusPackagePrefixes[i], StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static IEnumerable<INamedTypeSymbol> WalkAllTypes(INamespaceSymbol ns)
        {
            foreach (var nested in ns.GetNamespaceMembers())
            {
                foreach (var t in WalkAllTypes(nested)) yield return t;
            }
            foreach (var t in ns.GetTypeMembers())
            {
                yield return t;
                foreach (var n in WalkNestedTypes(t)) yield return n;
            }
        }

        private static IEnumerable<INamedTypeSymbol> WalkNestedTypes(INamedTypeSymbol type)
        {
            foreach (var nested in type.GetTypeMembers())
            {
                yield return nested;
                foreach (var n in WalkNestedTypes(nested)) yield return n;
            }
        }

        private static bool HasAttr(ISymbol symbol, string metadataName, out AttributeData attr)
        {
            foreach (var a in symbol.GetAttributes())
            {
                if (a.AttributeClass != null && a.AttributeClass.ToDisplayString() == metadataName)
                {
                    attr = a;
                    return true;
                }
            }
            attr = null;
            return false;
        }

        private static bool HasAttr(ISymbol symbol, string metadataName)
            => HasAttr(symbol, metadataName, out _);

        private static bool HasInject(ISymbol symbol) => HasAttr(symbol, InjectAttribute) || HasAttr(symbol, OptionalInjectAttribute);

        private static bool IsOptional(ISymbol symbol) => HasAttr(symbol, OptionalInjectAttribute);

        private static bool IsCompilerGenerated(ITypeSymbol type) => HasAttr(type, CompilerGeneratedAttribute);

        private static bool IsVisible(ITypeSymbol type)
        {
            if (type == null) return false;
            if (type is IArrayTypeSymbol arr) return IsVisible(arr.ElementType);
            // A constructed generic is only referenceable when EVERY type argument is
            // referenceable (List<PrivateThing> is CS0122 from the generated file even
            // though List<T> itself is public).
            if (type is INamedTypeSymbol named && named.IsGenericType)
            {
                foreach (var arg in named.TypeArguments)
                {
                    if (!IsVisible(arg)) return false;
                }
            }
            if (type.DeclaredAccessibility != Accessibility.Public) return false;
            for (INamedTypeSymbol cur = type as INamedTypeSymbol; cur != null && cur.ContainingType != null; cur = cur.ContainingType)
            {
                if (cur.ContainingType.DeclaredAccessibility != Accessibility.Public) return false;
            }
            return true;
        }

        private static bool IsEmittable(ITypeSymbol type) => type != null && IsVisible(type);

        /// <summary>A MonoBehaviour, ScriptableObject or other UnityEngine.Object: Unity creates it, so a constructor factory
        /// (`new T()`) is never valid. The editor scan skipped them and this one did not (#249).</summary>
        private static bool IsUnityObject(ITypeSymbol type)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (t.ToDisplayString() == "UnityEngine.Object") return true;
            return false;
        }

        private static string Name(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        /// <summary>A type's name in the binder's header list of types the reflection path serves.</summary>
        private static string DisplayName(ITypeSymbol type) => type.ToDisplayString();

        /// <summary>The name System.Type.FullName gives a non-generic type, so the runtime's messages read the same on both paths.</summary>
        private static string ReflectionName(ITypeSymbol type)
        {
            if (!(type is INamedTypeSymbol named) || named.IsGenericType) return type.ToDisplayString();
            string name = named.MetadataName;
            for (var cur = named.ContainingType; cur != null; cur = cur.ContainingType) name = cur.MetadataName + "+" + name;
            var ns = named.ContainingNamespace;
            return ns == null || ns.IsGlobalNamespace ? name : ns.ToDisplayString() + "." + name;
        }

        private static string SafeIdentifier(string name)
        {
            var sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            }
            return sb.ToString();
        }

        /// <summary>A C# string literal for any text (binding names, member names, the member signature).</summary>
        private static string Literal(string text)
        {
            var sb = new StringBuilder(text.Length + 2).Append('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\0': sb.Append("\\0"); break;
                    default:
                        if (char.IsControl(c)) sb.Append("\\x").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static string InjectName(ISymbol symbol)
        {
            if (!HasAttr(symbol, InjectAttribute, out var injectAttr)) return null;
            // [Inject("name")] sets the same Name as [Inject(Name = "name")]; only the named form was read (#240). A named
            // argument is applied after the constructor, so it wins, as InjectAttribute.Name does at run time.
            string name = injectAttr.ConstructorArguments.Length == 1 ? injectAttr.ConstructorArguments[0].Value as string : null;
            foreach (var kvp in injectAttr.NamedArguments)
            {
                if (kvp.Key == "Name") name = kvp.Value.Value as string;
            }
            return string.IsNullOrEmpty(name) ? null : name;
        }

        /// <summary>The argument list of a TryResolve call: empty for the default binding, the name literal for a named one.</summary>
        private static string NameArgument(string name) => string.IsNullOrEmpty(name) ? "" : Literal(name);

        private static ITypeSymbol LazyArgument(ITypeSymbol type)
            => type is INamedTypeSymbol named && named.IsGenericType && named.OriginalDefinition.ToDisplayString() == LazyInjection
                ? named.TypeArguments[0] : null;

        private static IMethodSymbol RootDefinition(IMethodSymbol method)
        {
            while (method.OverriddenMethod != null) method = method.OverriddenMethod;
            return method.OriginalDefinition;
        }

        private sealed class MemberSet
        {
            public readonly List<IFieldSymbol> Fields = new List<IFieldSymbol>();
            public readonly List<IPropertySymbol> Properties = new List<IPropertySymbol>();
            public readonly List<IMethodSymbol> Methods = new List<IMethodSymbol>();
            public bool IsEmpty => Fields.Count == 0 && Properties.Count == 0 && Methods.Count == 0;
        }

        /// <summary>The injectable instance members, selected as NexusDI's MetadataCache selects them: base-first with
        /// per-level declared members (private base-class members included), a property at the first level that carries
        /// the attribute and once per name, a method once per root definition.</summary>
        private static MemberSet GetMemberSet(INamedTypeSymbol type)
        {
            var chain = new List<INamedTypeSymbol>();
            for (var cur = type; cur != null && cur.SpecialType != SpecialType.System_Object; cur = cur.BaseType)
                chain.Add(cur);
            chain.Reverse();

            var set = new MemberSet();
            var seenPropNames = new HashSet<string>();
            var seenMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            foreach (var level in chain)
            {
                foreach (var member in level.GetMembers())
                {
                    if (member.IsStatic) continue;
                    if (member is IFieldSymbol f && HasInject(f))
                        set.Fields.Add(f);
                    else if (member is IPropertySymbol p && !p.IsIndexer && HasInject(p) && seenPropNames.Add(p.Name))
                        set.Properties.Add(p);
                    else if (member is IMethodSymbol m
                        && (m.MethodKind == MethodKind.Ordinary || m.MethodKind == MethodKind.ExplicitInterfaceImplementation)
                        && HasAttr(m, InjectAttribute) && seenMethods.Add(RootDefinition(m)))
                        set.Methods.Add(m);
                }
            }
            return set;
        }

        /// <summary>The member signature NexusDI compares before a generated injector's first run
        /// (<c>InjectableMetadata.MemberSignature</c>): the same entries, in the same order.</summary>
        private static string MemberSignature(MemberSet set)
        {
            var entries = new List<string>();
            foreach (var f in set.Fields) entries.Add("f:" + f.ContainingType.MetadataName + "." + f.Name + SignatureFlags(IsOptional(f), InjectName(f)));
            foreach (var p in set.Properties) entries.Add("p:" + p.ContainingType.MetadataName + "." + p.Name + SignatureFlags(IsOptional(p), InjectName(p)));
            foreach (var m in set.Methods)
                entries.Add("m:" + m.ContainingType.MetadataName + "." + m.Name + "("
                    + string.Join(",", m.Parameters.Select(x => "p" + SignatureFlags(IsOptional(x), InjectName(x)))) + ")");
            entries.Sort(StringComparer.Ordinal);
            return string.Join(";", entries);
        }

        private static string SignatureFlags(bool optional, string name)
            => (optional ? "?" : "") + (string.IsNullOrEmpty(name) ? "" : "=" + name);

        private static Location LocationOf(ISymbol symbol) => symbol.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;

        private static bool IsSource(ISymbol symbol) => symbol.Locations.Any(l => l.IsInSource);

        // ─── Emission ─────────────────────────────────────────────────────────

        private void Emit(SourceProductionContext spc, Compilation compilation)
        {
            // The rule the referenced assemblies are filtered by: the generator emitted a binder into each Nexus package and
            // each editor and test assembly a project-wide Assets/csc.rsp reaches, and failed a test fixture's compile (#264).
            if (!IsGameAssembly(compilation.AssemblyName) || IsNexusPackageAssembly(compilation.AssemblyName)) return;

            // Private members of a referenced base class are needed to inject a current-compilation type as the
            // reflection path does; a compilation hides them unless it imports every metadata member.
            if (compilation is CSharpCompilation csharp)
                compilation = csharp.WithOptions(csharp.Options.WithMetadataImportOptions(MetadataImportOptions.All));

            var injectTypes = new List<INamedTypeSymbol>();
            var ctorFactoryTypes = new List<IMethodSymbol>();
            var genericCommandPairs = new List<(INamedTypeSymbol Command, INamedTypeSymbol Signal, bool IsAsync)>();
            // Types the binder leaves to the reflection path, with each reason, written at the top of the binder.
            var fallback = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            void Fallback(INamedTypeSymbol type, string reason)
            {
                string key = DisplayName(type);
                if (!fallback.TryGetValue(key, out var reasons)) fallback[key] = reasons = new SortedSet<string>(StringComparer.Ordinal);
                reasons.Add(reason);
            }
            // A member reported once, however many derived types share it.
            var reported = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            // A type can surface twice (current compilation AND a referenced assembly of the same
            // identity, e.g. a harness that re-references its own stub) — duplicate registrations
            // are harmless but duplicate emitted locals/fields are not (CS0128/CS0102).
            var seenInject = new HashSet<string>(StringComparer.Ordinal);
            var seenCtor = new HashSet<string>(StringComparer.Ordinal);
            var seenCommands = new HashSet<string>(StringComparer.Ordinal);

            // A compilation that does not reference the Nexus runtime gets no binder: the binder would not compile there.
            string nexusRuntime = compilation.GetTypeByMetadataName(InjectAttribute)?.ContainingAssembly.Name;
            if (nexusRuntime == null) return;

            foreach (var type in WalkAllTypes(compilation.Assembly.GlobalNamespace))
                Visit(type, fromCurrentCompilation: true);
            foreach (var asm in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                // A package type named in a game's binder breaks the game's compile the day the package removes it (#110).
                if (!IsGameAssembly(asm.Name) || IsNexusPackageAssembly(asm.Name)) continue;
                if (string.Equals(asm.Name, compilation.AssemblyName, StringComparison.Ordinal)) continue;
                // Only an assembly that uses Nexus holds its injectables, commands and factories; the factories of
                // Newtonsoft, DOTween or an SDK would only bloat the binder and break it the day that DLL changes (#191).
                if (!string.Equals(asm.Name, nexusRuntime, StringComparison.Ordinal)
                    && !asm.Modules.Any(m => m.ReferencedAssemblies.Any(r => string.Equals(r.Name, nexusRuntime, StringComparison.Ordinal))))
                    continue;
                foreach (var type in WalkAllTypes(asm.GlobalNamespace))
                    Visit(type, fromCurrentCompilation: false);
            }

            void Report(DiagnosticDescriptor descriptor, ISymbol member, string memberName, string detail)
            {
                if (reported.Add(member))
                    spc.ReportDiagnostic(Diagnostic.Create(descriptor, LocationOf(member), memberName, detail));
            }

            // The members no path can set (the reflection path throws for them): false when the type has one.
            bool Validate(MemberSet members)
            {
                bool valid = true;
                foreach (var f in members.Fields)
                {
                    if (!f.Type.IsValueType) continue;
                    valid = false;
                    if (IsSource(f)) Report(s_valueTypeMemberError, f, DisplayName(f.ContainingType) + "." + f.Name, f.Type.ToDisplayString());
                }
                foreach (var p in members.Properties)
                {
                    if (p.SetMethod == null)
                    {
                        valid = false;
                        if (IsSource(p)) Report(s_unsettableMemberError, p, DisplayName(p.ContainingType) + "." + p.Name, "is get-only: add a setter (it may be private)");
                    }
                    else if (p.Type.IsValueType)
                    {
                        valid = false;
                        if (IsSource(p)) Report(s_valueTypeMemberError, p, DisplayName(p.ContainingType) + "." + p.Name, p.Type.ToDisplayString());
                    }
                }
                foreach (var m in members.Methods)
                    foreach (var prm in m.Parameters)
                    {
                        if (!prm.Type.IsValueType) continue;
                        valid = false;
                        if (IsSource(m)) Report(s_valueTypeMemberError, prm, DisplayName(m.ContainingType) + "." + m.Name + "(" + prm.Name + ")", prm.Type.ToDisplayString());
                    }
                return valid;
            }

            void Visit(INamedTypeSymbol type, bool fromCurrentCompilation)
            {
                if (fromCurrentCompilation)
                {
                    foreach (var member in type.GetMembers())
                    {
                        if (member.IsStatic && (member is IFieldSymbol || member is IPropertySymbol || member is IMethodSymbol) && HasInject(member))
                            Report(s_unsettableMemberError, member, DisplayName(type) + "." + member.Name, "is static: Nexus injects instance members only");
                    }
                }
                if (type.IsValueType || type.TypeKind == TypeKind.Interface || type.TypeKind == TypeKind.Enum || type.TypeKind == TypeKind.Delegate) return;
                if (type.IsAbstract || IsCompilerGenerated(type)) return;

                MemberSet members = fromCurrentCompilation ? GetMemberSet(type) : null;
                bool valid = members == null || Validate(members);
                if (type.IsGenericType)
                {
                    if (members != null && !members.IsEmpty) Fallback(type, "an open generic type: the binder names closed types only");
                    return;
                }

                // Generic-only command dispatchers: every ICommand<T>/IAsyncCommand<T> the type implements, as the editor
                // generator registers them; one the binder cannot name dispatches through MethodInfo.Invoke.
                foreach (var iface in type.AllInterfaces)
                {
                    if (!iface.IsGenericType) continue;
                    var def = iface.OriginalDefinition;
                    if (def.ContainingNamespace?.ToDisplayString() != "Nexus.Core") continue;
                    if (def.Name != "ICommand" && def.Name != "IAsyncCommand") continue;
                    var signal = (INamedTypeSymbol)iface.TypeArguments[0];
                    if (!IsVisible(type) || !IsVisible(signal))
                    {
                        Fallback(type, $"its {def.Name}<{signal.Name}> is dispatched through MethodInfo.Invoke: the binder cannot name the command or the signal");
                        continue;
                    }
                    string cmdKey = Name(type) + "|" + Name(signal) + "|" + def.Name;
                    if (seenCommands.Add(cmdKey)) genericCommandPairs.Add((type, signal, def.Name == "IAsyncCommand"));
                }

                if (!IsVisible(type))
                {
                    if (members != null && !members.IsEmpty) Fallback(type, "not visible from the binder (internal or private)");
                    return;
                }

                // Constructor factory candidates (mirror of the editor scan): exactly one
                // [Inject]/[Construct]-marked public ctor, or a single public ctor.
                var publicCtors = type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).ToList();
                if (publicCtors.Count > 0 && !IsUnityObject(type))
                {
                    var marked = publicCtors.Where(c => HasAttr(c, InjectAttribute) || HasAttr(c, ConstructAttribute)).ToList();
                    if (marked.Count == 1)
                    {
                        if (seenCtor.Add(Name(type))) ctorFactoryTypes.Add(marked[0]);
                    }
                    else if (marked.Count == 0 && publicCtors.Count == 1)
                    {
                        if (seenCtor.Add(Name(type))) ctorFactoryTypes.Add(publicCtors[0]);
                    }
                }


                // Injectors/clearers ONLY for current-compilation types — see the contract in the file header.
                if (members != null && !members.IsEmpty && valid && seenInject.Add(Name(type)))
                    injectTypes.Add(type);
            }

            // ── Ctor factories: an [OptionalInject] or null-default parameter is TryResolve, a required one
            //    ResolveConstructorParameter; any other default keeps the reflection path (#191) ──
            var ctorFactorySb = new StringBuilder();
            foreach (var ctor in ctorFactoryTypes)
            {
                var type = ctor.ContainingType;
                bool skip = false;
                var argList = new List<string>(ctor.Parameters.Length);
                for (int i = 0; i < ctor.Parameters.Length; i++)
                {
                    var p = ctor.Parameters[i];
                    // As the editor generator: only an injectable type is listed, most such classes are never bound.
                    if (p.Type.IsValueType || !IsEmittable(p.Type))
                    {
                        if (!GetMemberSet(type).IsEmpty)
                            Fallback(type, p.Type.IsValueType
                                ? $"constructor parameter '{p.Name}' is a value type, which only an explicit value supplies"
                                : $"constructor parameter '{p.Name}' has a type the binder cannot name");
                        skip = true;
                        break;
                    }
                    if (p.HasExplicitDefaultValue && p.ExplicitDefaultValue != null)
                    {
                        Fallback(type, $"constructor parameter '{p.Name}' has a default value the binder does not repeat");
                        skip = true;
                        break;
                    }
                    string pTypeName = Name(p.Type);
                    string paramName = InjectName(p);
                    if (IsOptional(p) || p.HasExplicitDefaultValue)
                    {
                        argList.Add($"di.TryResolve<{pTypeName}>({NameArgument(paramName)})");
                    }
                    else
                    {
                        string nameArg = string.IsNullOrEmpty(paramName) ? "" : ", " + Literal(paramName);
                        argList.Add($"di.ResolveConstructorParameter<{pTypeName}>({i}, {Literal(ReflectionName(type))}, {Literal(ReflectionName(p.Type))}{nameArg})");
                    }
                }
                if (skip) continue;
                ctorFactorySb.AppendLine($"            NexusDI.RegisterConstructorFactory<{Name(type)}>((di) => new {Name(type)}({string.Join(", ", argList)}));");
            }

            // ── Generic-only command dispatchers ──
            var dispatcherSb = new StringBuilder();
            foreach (var (commandType, signal, isAsync) in genericCommandPairs)
            {
                string commandName = Name(commandType);
                string signalName = Name(signal);
                if (isAsync)
                {
                    dispatcherSb.AppendLine($"            CommandRegistry.RegisterGenericAsyncDispatcher(typeof({commandName}), typeof({signalName}),");
                    dispatcherSb.AppendLine($"                (cmd, sig, ct) => ((IAsyncCommand<{signalName}>)cmd).ExecuteAsync(({signalName})sig, ct));");
                }
                else
                {
                    dispatcherSb.AppendLine($"            CommandRegistry.RegisterGenericSyncDispatcher(typeof({commandName}), typeof({signalName}),");
                    dispatcherSb.AppendLine($"                (cmd, sig) => ((ICommand<{signalName}>)cmd).Execute(({signalName})sig));");
                }
            }

            // ── Injectors + clearers (all-or-nothing per type). Every member resolves through TryResolve; an unbound
            //    required member goes to the runtime, so the generated path gives the reflection path's answer ──
            var cacheSb = new StringBuilder();
            var initSb = new StringBuilder();
            foreach (var type in injectTypes)
            {
                var members = GetMemberSet(type);
                string fullName = Name(type);
                string typeSafeName = SafeIdentifier(fullName);

                bool emittable = members.Fields.All(f => IsEmittable(f.ContainingType) && IsEmittable(f.Type))
                    && members.Properties.All(p => IsEmittable(p.ContainingType) && IsEmittable(p.Type))
                    && members.Methods.All(m => IsEmittable(m.ContainingType) && m.Parameters.All(x => IsEmittable(x.Type)));
                if (!emittable)
                {
                    Fallback(type, "an [Inject] member's declaring or member type is not visible from the binder");
                    continue;
                }

                var cacheNames = new HashSet<string>(StringComparer.Ordinal);
                string Target(INamedTypeSymbol declaring)
                    => SymbolEqualityComparer.Default.Equals(declaring, type) ? "instance" : $"(({Name(declaring)})instance)";
                string Cache(string kind, string reflectionType, ISymbol member, string lookup)
                {
                    string declaringName = Name(member.ContainingType);
                    string cacheName = $"s_{kind}_{typeSafeName}_{SafeIdentifier(declaringName)}_{SafeIdentifier(member.Name)}";
                    if (cacheNames.Add(cacheName))
                        cacheSb.AppendLine($"        private static readonly System.Reflection.{reflectionType} {cacheName} = typeof({declaringName}).{lookup}({Literal(member.Name)}, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);");
                    return cacheName;
                }
                // Public members are set directly; a private or protected member, a readonly field and a non-public or
                // init-only setter go through the cached FieldInfo/PropertyInfo.
                string Get(ISymbol member)
                {
                    if (member is IFieldSymbol f)
                        return f.DeclaredAccessibility == Accessibility.Public ? $"{Target(f.ContainingType)}.{f.Name}" : $"{Cache("f", "FieldInfo", f, "GetField")}.GetValue(instance)";
                    var p = (IPropertySymbol)member;
                    return p.GetMethod != null && p.GetMethod.DeclaredAccessibility == Accessibility.Public
                        ? $"{Target(p.ContainingType)}.{p.Name}" : $"{Cache("p", "PropertyInfo", p, "GetProperty")}.GetValue(instance)";
                }
                string Set(ISymbol member, string value)
                {
                    if (member is IFieldSymbol f)
                        return f.DeclaredAccessibility == Accessibility.Public && !f.IsReadOnly
                            ? $"{Target(f.ContainingType)}.{f.Name} = {value};" : $"{Cache("f", "FieldInfo", f, "GetField")}.SetValue(instance, {value});";
                    var p = (IPropertySymbol)member;
                    return p.SetMethod.DeclaredAccessibility == Accessibility.Public && !p.SetMethod.IsInitOnly
                        ? $"{Target(p.ContainingType)}.{p.Name} = {value};" : $"{Cache("p", "PropertyInfo", p, "GetProperty")}.SetValue(instance, {value});";
                }
                void EmitMember(ISymbol member, ITypeSymbol memberType)
                {
                    string name = InjectName(member);
                    var lazyArgument = LazyArgument(memberType);
                    if (lazyArgument != null)
                    {
                        // the reflection path assigns a LazyInjection<T> only to a member that holds none
                        initSb.AppendLine($"                if ({Get(member)} == null) {Set(member, $"di.CreateLazyInjection<{Name(lazyArgument)}>({NameArgument(name)})")}");
                        return;
                    }
                    initSb.AppendLine("                {");
                    initSb.AppendLine($"                    var value = di.TryResolve<{Name(memberType)}>({NameArgument(name)});");
                    initSb.AppendLine($"                    if (value != null) {Set(member, "value")}");
                    if (!IsOptional(member))
                        initSb.AppendLine($"                    else di.ReportMissingMember(instance, typeof({Name(member.ContainingType)}), {Literal(member.Name)});");
                    initSb.AppendLine("                }");
                }

                initSb.AppendLine($"            NexusDI.RegisterInjector<{fullName}>((instance, di) =>");
                initSb.AppendLine("            {");
                foreach (var f in members.Fields) EmitMember(f, f.Type);
                foreach (var p in members.Properties) EmitMember(p, p.Type);

                // Methods go through the cached MethodInfo of their declaring level: Invoke dispatches a virtual method to
                // its override, as the reflection path does. An unbound required parameter is reported and the method is
                // not invoked (ReInjectAll invokes it later).
                for (int methodIndex = 0; methodIndex < members.Methods.Count; methodIndex++)
                {
                    var m = members.Methods[methodIndex];
                    string declaringName = Name(m.ContainingType);
                    string cacheName = $"s_m_{typeSafeName}_{SafeIdentifier(declaringName)}_{SafeIdentifier(m.Name)}_{methodIndex}";
                    string parameterTypes = string.Join(", ", m.Parameters.Select(x => $"typeof({Name(x.Type)})"));
                    cacheSb.AppendLine($"        private static readonly System.Reflection.MethodInfo {cacheName} = typeof({declaringName}).GetMethod({Literal(m.Name)}, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly, null, new System.Type[] {{ {parameterTypes} }}, null);");
                    initSb.AppendLine("                {");
                    initSb.AppendLine($"                    if ({cacheName} == null) throw new InvalidOperationException({Literal($"[Nexus SG] Failed to bind injector method {DisplayName(type)}.{m.Name}.")});");
                    bool anyRequired = m.Parameters.Any(x => !IsOptional(x));
                    if (anyRequired) initSb.AppendLine("                    bool ready = true;");
                    for (int i = 0; i < m.Parameters.Length; i++)
                    {
                        var prm = m.Parameters[i];
                        initSb.AppendLine($"                    var a{i} = di.TryResolve<{Name(prm.Type)}>({NameArgument(InjectName(prm))});");
                        if (!IsOptional(prm))
                            initSb.AppendLine($"                    if (a{i} == null && !di.ReportMissingParameter(instance, {cacheName}, {i})) ready = false;");
                    }
                    string args = m.Parameters.Length == 0 ? "System.Array.Empty<object>()" : "new object[] { " + string.Join(", ", m.Parameters.Select((x, i) => "a" + i)) + " }";
                    initSb.AppendLine($"                    {(anyRequired ? "if (ready) " : "")}{cacheName}.Invoke(instance, {args});");
                    initSb.AppendLine("                }");
                }
                initSb.AppendLine($"            }}, {Literal(MemberSignature(members))});");

                if (members.Fields.Count > 0 || members.Properties.Count > 0)
                {
                    initSb.AppendLine($"            NexusDI.RegisterClearer<{fullName}>(instance =>");
                    initSb.AppendLine("            {");
                    foreach (var f in members.Fields) initSb.AppendLine($"                {Set(f, "null")}");
                    foreach (var p in members.Properties) initSb.AppendLine($"                {Set(p, "null")}");
                    initSb.AppendLine("            });");
                }
            }

            // ── Assemble ──
            var sb = new StringBuilder();
            sb.AppendLine("//------------------------------------------------------------------------------");
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//     This code was generated by the Nexus AOT Binder Roslyn Source Generator");
            sb.AppendLine("//     (Nexus.Generator.NexusBinderGenerator). Changes will be lost on regeneration.");
            sb.AppendLine("//     If you also run the editor-time generator (Nexus/Generate AOT Binder),");
            sb.AppendLine("//     delete its output file — the two must not both exist in one assembly.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("//------------------------------------------------------------------------------");
            if (fallback.Count > 0)
            {
                sb.AppendLine("// The reflection path serves these types (the binder does not name them):");
                foreach (var entry in fallback) sb.AppendLine($"//   {entry.Key}: {string.Join("; ", entry.Value)}");
            }
            sb.AppendLine("#pragma warning disable CS0618");
            sb.AppendLine("using System;");
            sb.AppendLine("using Nexus.Core;");
            sb.AppendLine();
            sb.AppendLine("namespace Nexus.Generated");
            sb.AppendLine("{");
            sb.AppendLine("    public static class NexusGeneratedBinder");
            sb.AppendLine("    {");
            sb.Append(cacheSb.ToString());
            sb.AppendLine();
            sb.AppendLine("        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]");
            sb.AppendLine("        public static void Initialize()");
            sb.AppendLine("        {");
            sb.Append(ctorFactorySb.ToString());
            sb.Append(initSb.ToString());
            sb.Append(dispatcherSb.ToString());
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            spc.AddSource("NexusGeneratedBinder.g.cs", sb.ToString());
        }
    }
}
