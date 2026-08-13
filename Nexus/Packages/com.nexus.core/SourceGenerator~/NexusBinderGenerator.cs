// ------------------------------------------------------------------------------
// Nexus AOT Binder — Roslyn Source Generator
//
// Compile-time counterpart of the editor-time NexusCodeGenerator: discovers
// [Inject]/[OptionalInject]/[Construct] types, single-public-ctor factories and
// generic-only ICommand<T>/IAsyncCommand<T> implementations in the CURRENT
// compilation plus every game-relevant referenced assembly, and emits the same
// NexusGeneratedBinder shape the editor generator produces (RegisterConstructorFactory
// / RegisterInjector / RegisterClearer / RegisterGeneric*Dispatcher / PreserveMembers).
//
// The gates proven by the harness CG1 test are preserved exactly:
//   • visibility gate  — non-visible types (private/internal nested) are never referenced;
//   • compiler-generated types (`<>c__DisplayClass*`) are skipped;
//   • value-type ctor params skip the factory; unnameable/open-generic types skip;
//   • all-or-nothing injectors — a single non-emittable injectable member (non-visible
//     member type) drops the WHOLE injector for the type so the reflection path serves it
//     with identical semantics instead of a partial AOT injector;
//   • injectors/clearers are emitted ONLY for types in the CURRENT compilation. Roslyn
//     metadata symbols (referenced assemblies) expose only public/protected members, so a
//     referenced type may carry private/internal [Inject] members this generator cannot see —
//     and a registered injector REPLACES the reflection path, so emitting a partial one would
//     silently break those members. Each Unity asmdef runs its own generator instance (its own
//     current compilation), so its own types get complete injectors; referenced types are
//     served by their own compilation's binder, by the editor-time generator, or by the
//     reflection path. Ctor factories and command dispatchers stay safe for referenced types
//     (their signatures are fully visible and every parameter type is visibility-gated).
//   • [PostConstruct] is a runtime guarantee: Injector.Inject runs [PostConstruct] methods
//     after ANY custom (generated) injector, so generated binders cannot skip them.
//   • WithParameter precedence is a runtime guarantee (Injector.CreateInstance skips the
//     factory when overrides are present) — the generated factory resolves from the
//     container, so explicit constructor arguments always win.
//
// Compatibility: compiled against Microsoft.CodeAnalysis.CSharp 4.10.0 (Unity 6000.5's
// Roslyn) as a netstandard2.0 assembly shipping inside the package; the same source is
// compiled directly into tools/nexus-benchmark (Roslyn from the .NET SDK) and driven by
// the CG2 harness test, so the shipped generator cannot drift from what is proven there.
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
        private const string ICommand = "Nexus.Core.ICommand<>";
        private const string IAsyncCommand = "Nexus.Core.IAsyncCommand<>";

        private static readonly string[] FrameworkPrefixes =
        {
            "System", "Microsoft", "Unity", "mscorlib", "mono", "nunit", "NUnit", "netstandard"
        };

        private static readonly DiagnosticDescriptor s_valueTypeMemberError = new DiagnosticDescriptor(
            id: "NEXUS_SG001",
            title: "Value-type injection member is not supported",
            messageFormat: "Field/property '{0}' on '{1}' has [Inject]/[OptionalInject] but is a value type ({2}). Nexus injection only supports reference-type dependencies.",
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

        private static bool IsGameAssembly(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < FrameworkPrefixes.Length; i++)
            {
                if (name.StartsWith(FrameworkPrefixes[i], StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (name.IndexOf(".editor", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (name.IndexOf("tests", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
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

        private static string Name(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

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

        private static string InjectName(AttributeData injectAttr)
        {
            if (injectAttr == null) return null;
            foreach (var kvp in injectAttr.NamedArguments)
            {
                if (kvp.Key == "Name" && kvp.Value.Value is string s && !string.IsNullOrEmpty(s)) return s;
            }
            return null;
        }

        private sealed class MemberSet
        {
            public readonly List<IFieldSymbol> Fields = new List<IFieldSymbol>();
            public readonly List<IPropertySymbol> Properties = new List<IPropertySymbol>();
            public readonly List<IMethodSymbol> Methods = new List<IMethodSymbol>();
        }

        /// <summary>Base-first chain walk with per-level declared members (mirrors the runtime
        /// MetadataCache and the editor GetMemberSet): private base-class members surface here
        /// even though the leaf type's reflection would not return them.</summary>
        private static MemberSet GetMemberSet(INamedTypeSymbol type)
        {
            var chain = new List<INamedTypeSymbol>();
            for (var cur = type; cur != null && cur.SpecialType != SpecialType.System_Object; cur = cur.BaseType)
                chain.Add(cur);
            chain.Reverse();

            var set = new MemberSet();
            var seenPropNames = new HashSet<string>();
            for (int level = 0; level < chain.Count; level++)
            {
                foreach (var member in chain[level].GetMembers())
                {
                    if (member is IFieldSymbol f && !f.IsStatic && !f.IsImplicitlyDeclared && (HasAttr(f, InjectAttribute) || HasAttr(f, OptionalInjectAttribute)))
                        set.Fields.Add(f);
                    else if (member is IPropertySymbol p && !p.IsStatic && p.SetMethod != null && seenPropNames.Add(p.Name) && (HasAttr(p, InjectAttribute) || HasAttr(p, OptionalInjectAttribute)))
                        set.Properties.Add(p);
                    else if (member is IMethodSymbol m && !m.IsStatic && m.MethodKind == MethodKind.Ordinary && HasAttr(m, InjectAttribute))
                        set.Methods.Add(m);
                }
            }
            return set;
        }

        // ─── Emission ─────────────────────────────────────────────────────────

        private void Emit(SourceProductionContext spc, Compilation compilation)
        {
            var injectTypes = new List<INamedTypeSymbol>();
            var ctorFactoryTypes = new List<IMethodSymbol>();
            var genericCommandPairs = new List<(INamedTypeSymbol Command, INamedTypeSymbol Signal, bool IsAsync)>();
            // A type can surface twice (current compilation AND a referenced assembly of the same
            // identity, e.g. a harness that re-references its own stub) — duplicate registrations
            // are harmless but duplicate emitted locals/fields are not (CS0128/CS0102).
            var seenInject = new HashSet<string>(StringComparer.Ordinal);
            var seenCtor = new HashSet<string>(StringComparer.Ordinal);
            var seenCommands = new HashSet<string>(StringComparer.Ordinal);

            foreach (var type in WalkAllTypes(compilation.Assembly.GlobalNamespace))
                Visit(type, fromCurrentCompilation: true);
            foreach (var asm in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                if (!IsGameAssembly(asm.Name)) continue;
                if (string.Equals(asm.Name, compilation.AssemblyName, StringComparison.Ordinal)) continue;
                foreach (var type in WalkAllTypes(asm.GlobalNamespace))
                    Visit(type, fromCurrentCompilation: false);
            }

            void Visit(INamedTypeSymbol type, bool fromCurrentCompilation)
            {
                if (type.IsValueType || type.TypeKind == TypeKind.Interface || type.TypeKind == TypeKind.Enum || type.TypeKind == TypeKind.Delegate) return;
                if (type.IsAbstract || type.IsGenericType) return; // open generics cannot be named
                if (IsCompilerGenerated(type) || !IsVisible(type)) return;

                // Constructor factory candidates (mirror of the editor scan): exactly one
                // [Inject]/[Construct]-marked public ctor, or a single public ctor.
                var publicCtors = type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).ToList();
                if (publicCtors.Count > 0)
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

                // Generic-only command dispatchers: ICommand<T>/IAsyncCommand<T> implementations.
                foreach (var iface in type.AllInterfaces)
                {
                    if (!iface.IsGenericType) continue;
                    var def = iface.OriginalDefinition;
                    if (def.ContainingNamespace?.ToDisplayString() != "Nexus.Core") continue;
                    if (def.Name == "ICommand" || def.Name == "IAsyncCommand")
                    {
                        var signal = (INamedTypeSymbol)iface.TypeArguments[0];
                        string cmdKey = Name(type) + "|" + Name(signal) + "|" + def.Name;
                        if (IsVisible(signal) && seenCommands.Add(cmdKey)) genericCommandPairs.Add((type, signal, def.Name == "IAsyncCommand"));
                        break;
                    }
                }

                var members = GetMemberSet(type);
                bool hasInject = members.Fields.Count > 0 || members.Properties.Count > 0 || members.Methods.Count > 0;
                // Injectors/clearers ONLY for current-compilation types — see the contract in
                // the file header (metadata member visibility is incomplete).
                if (hasInject && fromCurrentCompilation && seenInject.Add(Name(type)))
                    injectTypes.Add(type);
            }

            // ── Ctor factories ──
            var ctorFactorySb = new StringBuilder();
            foreach (var ctor in ctorFactoryTypes)
            {
                var type = ctor.ContainingType;
                bool skip = false;
                var argList = new List<string>(ctor.Parameters.Length);
                for (int i = 0; i < ctor.Parameters.Length; i++)
                {
                    var p = ctor.Parameters[i];
                    if (p.Type.IsValueType || !IsEmittable(p.Type)) { skip = true; break; }
                    bool optional = HasAttr(p, OptionalInjectAttribute);
                    string pTypeName = Name(p.Type);
                    string paramName = InjectName(GetAttr(p, InjectAttribute));
                    if (optional)
                    {
                        argList.Add($"di.TryResolve<{pTypeName}>()");
                    }
                    else
                    {
                        string nameArg = string.IsNullOrEmpty(paramName) ? "" : $", \"{paramName}\"";
                        argList.Add($"di.ResolveConstructorParameter<{pTypeName}>({i}, \"{Name(type)}\", \"{pTypeName}\"{nameArg})");
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

            // ── Injectors + clearers (all-or-nothing per type) ──
            var cacheSb = new StringBuilder();
            var initSb = new StringBuilder();
            foreach (var type in injectTypes)
            {
                var members = GetMemberSet(type);
                string fullName = Name(type);
                string typeSafeName = SafeIdentifier(fullName);

                bool emittable = true;
                foreach (var f in members.Fields)
                {
                    if (f.Type.IsValueType)
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(s_valueTypeMemberError, Location.None, f.Name, fullName, f.Type.ToDisplayString()));
                        emittable = false;
                        break;
                    }
                    if (!IsEmittable(f.Type) || !IsEmittable(f.ContainingType)) { emittable = false; break; }
                }
                if (emittable)
                {
                    foreach (var p in members.Properties)
                    {
                        if (p.Type.IsValueType)
                        {
                            spc.ReportDiagnostic(Diagnostic.Create(s_valueTypeMemberError, Location.None, p.Name, fullName, p.Type.ToDisplayString()));
                            emittable = false;
                            break;
                        }
                        if (!IsEmittable(p.Type) || !IsEmittable(p.ContainingType)) { emittable = false; break; }
                    }
                }
                if (emittable)
                {
                    foreach (var m in members.Methods)
                    {
                        if (!IsEmittable(m.ContainingType)) { emittable = false; break; }
                        foreach (var prm in m.Parameters)
                        {
                            if (prm.Type.IsValueType) continue;
                            if (!IsEmittable(prm.Type)) { emittable = false; break; }
                        }
                        if (!emittable) break;
                    }
                }
                if (!emittable) continue; // reflection path serves the type (identical semantics)

                initSb.AppendLine($"            NexusDI.RegisterInjector<{fullName}>((instance, di) =>");
                initSb.AppendLine("            {");

                foreach (var f in members.Fields)
                {
                    bool fOptional = HasAttr(f, OptionalInjectAttribute);
                    string fTypeName = Name(f.Type);
                    string fResolve = fOptional ? $"di.TryResolve<{fTypeName}>()" : $"di.Resolve<{fTypeName}>()";
                    if (f.DeclaredAccessibility == Accessibility.Public)
                    {
                        initSb.AppendLine($"                instance.{f.Name} = {fResolve};");
                    }
                    else
                    {
                        string declaringName = Name(f.ContainingType);
                        string cacheFieldName = $"s_f_{typeSafeName}_{SafeIdentifier(declaringName)}_{f.Name}";
                        cacheSb.AppendLine($"        private static readonly System.Reflection.FieldInfo {cacheFieldName} = typeof({declaringName}).GetField(\"{f.Name}\", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);");
                        initSb.AppendLine($"                {cacheFieldName}.SetValue(instance, {fResolve});");
                    }
                }

                foreach (var p in members.Properties)
                {
                    bool pOptional = HasAttr(p, OptionalInjectAttribute);
                    string pTypeName = Name(p.Type);
                    string pResolve = pOptional ? $"di.TryResolve<{pTypeName}>()" : $"di.Resolve<{pTypeName}>()";
                    if (p.SetMethod.DeclaredAccessibility == Accessibility.Public)
                    {
                        initSb.AppendLine($"                instance.{p.Name} = {pResolve};");
                    }
                    else
                    {
                        string declaringName = Name(p.ContainingType);
                        string cachePropName = $"s_p_{typeSafeName}_{SafeIdentifier(declaringName)}_{p.Name}";
                        cacheSb.AppendLine($"        private static readonly System.Reflection.PropertyInfo {cachePropName} = typeof({declaringName}).GetProperty(\"{p.Name}\", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);");
                        initSb.AppendLine($"                {cachePropName}.SetValue(instance, {pResolve});");
                    }
                }

                foreach (var m in members.Methods)
                {
                    bool hasValueTypeParams = false;
                    var paramList = new List<string>();
                    foreach (var param in m.Parameters)
                    {
                        if (param.Type.IsValueType) { hasValueTypeParams = true; break; }
                        bool paramOptional = HasAttr(param, OptionalInjectAttribute);
                        string paramTypeName = Name(param.Type);
                        paramList.Add(paramOptional ? $"di.TryResolve<{paramTypeName}>()" : $"di.Resolve<{paramTypeName}>()");
                    }
                    if (hasValueTypeParams) continue;

                    string declaringName = Name(m.ContainingType);
                    string cacheMethodName = $"s_m_{typeSafeName}_{SafeIdentifier(declaringName)}_{m.Name}";
                    var paramTypesString = string.Join(", ", m.Parameters.Select(param => $"typeof({Name(param.Type)})"));
                    cacheSb.AppendLine($"        private static readonly System.Reflection.MethodInfo {cacheMethodName} = typeof({declaringName}).GetMethod(\"{m.Name}\", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new System.Type[] {{ {paramTypesString} }}, null);");
                    initSb.AppendLine($"                if ({cacheMethodName} == null) throw new InvalidOperationException(\"[Nexus SG] Failed to bind injector method {fullName}.{m.Name}.\");");
                    initSb.AppendLine($"                {cacheMethodName}.Invoke(instance, new object[] {{ {string.Join(", ", paramList)} }});");
                }

                initSb.AppendLine("            });");

                var clearFields = members.Fields.Where(f => !f.Type.IsValueType).ToList();
                var clearProps = members.Properties.Where(p => !p.Type.IsValueType).ToList();
                if (clearFields.Count > 0 || clearProps.Count > 0)
                {
                    initSb.AppendLine($"            NexusDI.RegisterClearer<{fullName}>(instance =>");
                    initSb.AppendLine("            {");
                    foreach (var f in clearFields)
                    {
                        if (f.DeclaredAccessibility == Accessibility.Public)
                        {
                            initSb.AppendLine($"                instance.{f.Name} = null;");
                        }
                        else
                        {
                            string declaringName = Name(f.ContainingType);
                            string cacheFieldName = $"s_f_{typeSafeName}_{SafeIdentifier(declaringName)}_{f.Name}";
                            initSb.AppendLine($"                {cacheFieldName}.SetValue(instance, null);");
                        }
                    }
                    foreach (var p in clearProps)
                    {
                        if (p.SetMethod.DeclaredAccessibility == Accessibility.Public)
                        {
                            initSb.AppendLine($"                instance.{p.Name} = null;");
                        }
                        else
                        {
                            string declaringName = Name(p.ContainingType);
                            string cachePropName = $"s_p_{typeSafeName}_{SafeIdentifier(declaringName)}_{p.Name}";
                            initSb.AppendLine($"                {cachePropName}.SetValue(instance, null);");
                        }
                    }
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
            sb.AppendLine();
            sb.AppendLine("        /// <summary>Forces IL2CPP to keep injected members alive (compiled-in preservation).</summary>");
            sb.AppendLine("        public static void PreserveMembers()");
            sb.AppendLine("        {");
            sb.AppendLine("            #pragma warning disable 0162, 0169, 0414, 0219");
            sb.AppendLine("            if (false)");
            sb.AppendLine("            {");
            foreach (var type in injectTypes)
            {
                var members = GetMemberSet(type);
                string fullName = Name(type);
                string typeSafeName = SafeIdentifier(fullName);
                foreach (var f in members.Fields)
                {
                    if (f.DeclaredAccessibility == Accessibility.Public && IsEmittable(f.ContainingType) && IsEmittable(f.Type))
                        sb.AppendLine($"                var _f_{typeSafeName}_{f.Name} = default({fullName}).{f.Name};");
                    else if (IsEmittable(f.ContainingType))
                        sb.AppendLine($"                var _f_{typeSafeName}_{f.Name} = typeof({Name(f.ContainingType)}).GetField(\"{f.Name}\", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);");
                }
                foreach (var p in members.Properties)
                {
                    if (p.GetMethod != null && p.GetMethod.DeclaredAccessibility == Accessibility.Public && IsEmittable(p.ContainingType) && IsEmittable(p.Type))
                    {
                        sb.AppendLine($"                var _p_{typeSafeName}_{p.Name} = default({fullName}).{p.Name};");
                        sb.AppendLine($"                _ = _p_{typeSafeName}_{p.Name}; // Suppress CS0219 warning");
                    }
                    else if (IsEmittable(p.ContainingType))
                    {
                        sb.AppendLine($"                var _p_{typeSafeName}_{p.Name} = typeof({Name(p.ContainingType)}).GetProperty(\"{p.Name}\", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);");
                    }
                }
            }
            sb.AppendLine("            }");
            sb.AppendLine("            #pragma warning restore 0162, 0169, 0414, 0219");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            if (sb.Length > 0)
                spc.AddSource("NexusGeneratedBinder.g.cs", sb.ToString());
        }

        private static AttributeData GetAttr(ISymbol symbol, string metadataName)
        {
            HasAttr(symbol, metadataName, out var attr);
            return attr;
        }
    }
}
