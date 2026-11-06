using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Editor
{
    public static class NexusCodeGenerator
    {
        private const string AutoGenKey = "Nexus.AutoGenerateAOTBinder";
        
        public static bool AutoGenerateEnabled
        {
            get => EditorPrefs.GetBool(AutoGenKey, false);
            set => EditorPrefs.SetBool(AutoGenKey, value);
        }

        [MenuItem("Nexus/Auto-Generate AOT on Script Reload", false, 61)]
        private static void ToggleAutoGenerate()
        {
            AutoGenerateEnabled = !AutoGenerateEnabled;
            Menu.SetChecked("Nexus/Auto-Generate AOT on Script Reload", AutoGenerateEnabled);
            Debug.Log($"[Nexus] Auto-Generate AOT Binder on script reload: {(AutoGenerateEnabled ? "ENABLED" : "DISABLED")}");
        }

        [MenuItem("Nexus/Auto-Generate AOT on Script Reload", true, 61)]
        private static bool ToggleAutoGenerateValidate()
        {
            Menu.SetChecked("Nexus/Auto-Generate AOT on Script Reload", AutoGenerateEnabled);
            return true;
        }

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            if (!AutoGenerateEnabled) return;
            try
            {
                GenerateBinder();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Nexus] Auto-generate AOT binder failed: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }

        /// <summary>A type's injectable members, selected exactly as NexusDI's MetadataCache selects them; computed once
        /// per type and shared by every pass (discovery, validation, injector, clearer).</summary>
        private struct MemberSet
        {
            public FieldInfo[] Fields;
            public PropertyInfo[] Properties;
            public MethodInfo[] Methods;
        }

        private static MemberSet GetMemberSet(Type type, Dictionary<Type, MemberSet> cache)
        {
            if (!cache.TryGetValue(type, out var set))
            {
                // Base-first chain walk with DeclaredOnly, like the runtime: reflection on the leaf type never returns
                // PRIVATE members declared on base classes. A property is kept at the first level that carries the
                // attribute and once per name; a method once per base definition, so a virtual [Inject] method runs once,
                // through its override (#191). Get-only properties stay in the set: validation rejects them.
                var chain = new List<Type>();
                for (var cur = type; cur != null && cur != typeof(object); cur = cur.BaseType)
                    chain.Add(cur);
                chain.Reverse();

                const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                var fields = new List<FieldInfo>();
                var properties = new List<PropertyInfo>();
                var methods = new List<MethodInfo>();
                var seenPropNames = new HashSet<string>();
                var seenMethods = new HashSet<MethodInfo>();
                foreach (var level in chain)
                {
                    foreach (var f in level.GetFields(Declared))
                        if (HasInject(f)) fields.Add(f);
                    foreach (var p in level.GetProperties(Declared))
                        if (HasInject(p) && seenPropNames.Add(p.Name)) properties.Add(p);
                    foreach (var m in level.GetMethods(Declared))
                        if (HasInject(m) && seenMethods.Add(m.GetBaseDefinition())) methods.Add(m);
                }
                set = new MemberSet
                {
                    Fields = fields.ToArray(),
                    Properties = properties.ToArray(),
                    Methods = methods.ToArray()
                };
                cache[type] = set;
            }
            return set;
        }

        /// <summary>
        /// The member signature NexusDI compares before a generated injector's first run
        /// (<c>InjectableMetadata.MemberSignature</c>): the same entries, in the same order.
        /// </summary>
        private static string MemberSignature(MemberSet set)
        {
            var entries = new List<string>();
            foreach (var f in set.Fields) entries.Add("f:" + f.DeclaringType.Name + "." + f.Name + SignatureFlags(IsOptional(f), InjectName(f)));
            foreach (var p in set.Properties) entries.Add("p:" + p.DeclaringType.Name + "." + p.Name + SignatureFlags(IsOptional(p), InjectName(p)));
            foreach (var m in set.Methods)
                entries.Add("m:" + m.DeclaringType.Name + "." + m.Name + "("
                    + string.Join(",", m.GetParameters().Select(x => "p" + SignatureFlags(IsOptional(x), InjectName(x)))) + ")");
            entries.Sort(StringComparer.Ordinal);
            return string.Join(";", entries);
        }

        private static string SignatureFlags(bool optional, string name)
            => (optional ? "?" : "") + (string.IsNullOrEmpty(name) ? "" : "=" + name);

        /// <summary>
        /// E-C1 fix: converts a Type to a compilable C# reference name. The previous
        /// FullName.Replace("+", ".") produced INVALID C# for generic types (the raw
        /// backtick arity marker leaked and type arguments were dropped → CS0246/CS0305
        /// in the generated binder). Handles nested, generic and array types correctly.
        /// Returns null for open generic definitions (ContainsGenericParameters) — those
        /// can never be named in generated code and are skipped by the caller.
        /// </summary>
        private static string GetCSharpTypeName(Type type)
        {
            if (type == null) return null;
            if (type.ContainsGenericParameters) return null;
            if (type.IsArray)
            {
                var elem = GetCSharpTypeName(type.GetElementType());
                return elem != null ? elem + "[]" : null;
            }

            var sb = new StringBuilder();
            if (type.IsNested)
            {
                var parent = GetCSharpTypeName(type.DeclaringType);
                if (parent == null) return null;
                sb.Append(parent);
                sb.Append('.');
            }
            else if (!string.IsNullOrEmpty(type.Namespace))
            {
                sb.Append(type.Namespace);
                sb.Append('.');
            }

            var name = type.Name;
            int tick = name.IndexOf('`');
            if (tick > 0) name = name.Substring(0, tick);
            sb.Append(name);

            if (type.IsGenericType)
            {
                var args = type.GetGenericArguments();
                sb.Append('<');
                for (int i = 0; i < args.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    var argName = GetCSharpTypeName(args[i]);
                    if (argName == null) return null;
                    sb.Append(argName);
                }
                sb.Append('>');
            }
            return sb.ToString();
        }

        // Attribute lookups as NexusDI's MetadataCache makes them (GetCustomAttribute inherits through overrides).
        private static bool HasInject(FieldInfo f)
            => f.GetCustomAttribute<InjectAttribute>() != null || f.GetCustomAttribute<OptionalInjectAttribute>() != null;

        private static bool HasInject(PropertyInfo p)
            => p.GetCustomAttribute<InjectAttribute>() != null || p.GetCustomAttribute<OptionalInjectAttribute>() != null;

        private static bool HasInject(MethodInfo m)
            => m.GetCustomAttribute<InjectAttribute>() != null;

        private static bool IsOptional(MemberInfo member) => member.GetCustomAttribute<OptionalInjectAttribute>() != null;
        private static bool IsOptional(ParameterInfo parameter) => parameter.GetCustomAttribute<OptionalInjectAttribute>() != null;
        private static string InjectName(MemberInfo member) => member.GetCustomAttribute<InjectAttribute>()?.Name;
        private static string InjectName(ParameterInfo parameter) => parameter.GetCustomAttribute<InjectAttribute>()?.Name;

        private static bool IsLazy(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(LazyInjection<>);

        /// <summary>An init-only setter (<c>init;</c>) carries the IsExternalInit modifier and cannot be called from C# after construction.</summary>
        private static bool IsInitOnly(MethodInfo setter)
            => setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

        /// <summary>A C# string literal for any text (binding names, member names, the member signature).</summary>
        internal static string Literal(string text)
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

        /// <summary>The argument list of a TryResolve call: empty for the default binding, the name literal for a named one.</summary>
        private static string NameArgument(string name) => string.IsNullOrEmpty(name) ? "" : Literal(name);

        /// <summary>A type's name in the binder's header list of types the reflection path serves.</summary>
        private static string DisplayName(Type type) => type.FullName.Replace('+', '.');

        /// <summary>The reason both generators give at the top of the binder for a type [assembly: BinderExclude] keeps out (#158).</summary>
        internal const string BinderExcludedReason = "excluded by [assembly: BinderExclude]";

        private static string[] BinderExcludedPrefixes(Assembly assembly)
            => assembly.GetCustomAttributes<BinderExcludeAttribute>().SelectMany(a => a.Prefixes).Where(p => !string.IsNullOrEmpty(p)).ToArray();

        private static bool IsBinderExcluded(Type type, string[] prefixes)
        {
            if (prefixes.Length == 0) return false;
            string name = DisplayName(type);
            foreach (var prefix in prefixes)
                if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>True when a type can be referenced from the generated binder file.</summary>
        private static bool ImplementsSignalFilter(Type type)
        {
            foreach (var iface in type.GetInterfaces())
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(ISignalFilter<>)) return true;
            return false;
        }

        private static bool IsEmittableType(Type type)
        {
            if (type == null || !type.IsVisible || type.IsNestedPrivate || type.IsNestedAssembly) return false;
            if (type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false)) return false;
            if (GetCSharpTypeName(type) == null) return false;

            var asmName = AssemblyCatalog.GetSimpleName(type.Assembly);
            if (AssemblyCatalog.IsFrameworkAssembly(asmName)
                || AssemblyCatalog.IsThirdPartyAssembly(asmName)
                || AssemblyCatalog.IsEditorAssembly(asmName)
                || AssemblyCatalog.IsTestAssembly(asmName))
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// True when the binder can name an [Inject] member's type (#245). <see cref="IsEmittableType"/> keeps framework and
        /// third-party types out of what the binder registers; a member's type only has to be visible and come from an
        /// assembly the binder's assembly references: the .NET class library, an engine module or a game assembly, and so
        /// each generic argument and array element. A Unity package or third-party type keeps the reflection path
        /// (<see cref="IsReferencedMemberType"/>): the binder's assembly may not reference it.
        /// </summary>
        private static bool IsVisibleMemberType(Type type) => type != null && type.IsVisible && GetCSharpTypeName(type) != null;

        private static bool IsReferencedMemberType(Type type)
        {
            if (type.IsArray) return IsReferencedMemberType(type.GetElementType());
            if (type.IsGenericType)
                foreach (var arg in type.GetGenericArguments())
                    if (!IsReferencedMemberType(arg)) return false;
            string asm = AssemblyCatalog.GetSimpleName(type.Assembly);
            return asm == "mscorlib" || asm == "netstandard" || asm == "System" || asm == "System.Core" || asm == "System.Private.CoreLib"
                || asm == "UnityEngine" || (asm != null && asm.StartsWith("UnityEngine.", StringComparison.Ordinal) && asm.EndsWith("Module", StringComparison.Ordinal))
                || IsEmittableType(type);
        }

        /// <summary>
        /// True when a constructor factory (<c>new T(...)</c>) may be emitted for the type.
        /// UnityEngine.Object types (MonoBehaviour, ScriptableObject, Component...) are created by
        /// Unity and must never be constructed with <c>new</c>.
        /// </summary>
        internal static bool CanEmitConstructorFactory(Type type)
            => !typeof(UnityEngine.Object).IsAssignableFrom(type);

        /// <summary>E-C1: sanitizes a C# type name into a valid identifier prefix for cache fields.</summary>
        private static string GetSafeIdentifierName(string csharpTypeName)
        {
            var sb = new StringBuilder(csharpTypeName.Length);
            foreach (char c in csharpTypeName)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            return sb.ToString();
        }

        [MenuItem("Nexus/Generate AOT Binder", false, 60)]
        public static void GenerateBinder() => GenerateBinderCore(write: true);

        /// <summary>
        /// True when the binder and link.xml on disk are exactly what generation would write now. Nothing is written.
        /// A stale binder keeps injecting the members a type had when it was generated and a stale link.xml lets IL2CPP
        /// strip the rest, so player builds check this first (<see cref="NexusBinderBuildGuard"/>).
        /// </summary>
        public static bool IsBinderCurrent() => GenerateBinderCore(write: false);

        /// <summary>
        /// What switches the Roslyn source generator on for the binder's assembly, or null (#242). The generator emits
        /// Nexus.Generated.NexusGeneratedBinder into that compilation, so an editor binder file beside it is a second
        /// declaration (CS0101). It is on when the csc.rsp that governs the assembly has an -analyzer line naming
        /// Nexus.SourceGenerator.dll, or when that DLL is imported with the RoslynAnalyzer label. The csc.rsp beside the
        /// nearest .asmdef above the binder folder governs when that folder has one; otherwise Assets/csc.rsp does, which
        /// Unity 6000.6 applies to every assembly without a csc.rsp of its own (#264).
        /// </summary>
        internal static string SourceGeneratorSwitch(string binderFolder)
        {
            const string Dll = "Nexus.SourceGenerator.dll";
            string assets = Path.GetFullPath(Application.dataPath).TrimEnd('/', '\\');
            string rsp = Path.Combine(assets, "csc.rsp");
            for (var dir = new DirectoryInfo(Path.GetFullPath(binderFolder)); dir != null; dir = dir.Parent)
            {
                if (dir.Exists && dir.GetFiles("*.asmdef").Length > 0)
                {
                    string own = Path.Combine(dir.FullName, "csc.rsp");
                    if (File.Exists(own)) rsp = own;
                    break;
                }
                if (string.Equals(dir.FullName.TrimEnd('/', '\\'), assets, StringComparison.OrdinalIgnoreCase)) break;
            }
            if (File.Exists(rsp))
                foreach (string line in File.ReadAllLines(rsp))
                {
                    string option = line.Trim();
                    if ((option.StartsWith("-analyzer:", StringComparison.OrdinalIgnoreCase) || option.StartsWith("/analyzer:", StringComparison.OrdinalIgnoreCase)
                            || option.StartsWith("-a:", StringComparison.OrdinalIgnoreCase) || option.StartsWith("/a:", StringComparison.OrdinalIgnoreCase))
                        && option.IndexOf(Dll, StringComparison.OrdinalIgnoreCase) >= 0)
                        return rsp;
                }
            foreach (string guid in AssetDatabase.FindAssets("l:RoslynAnalyzer"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.Equals(Path.GetFileName(path), Dll, StringComparison.OrdinalIgnoreCase)) return path;
            }
            return null;
        }

        /// <summary>True when a generated binder exists at the configured output path, i.e. the project opted into AOT codegen.</summary>
        public static bool BinderExists()
            => File.Exists(Path.Combine(NexusEditorSettings.GetOrCreateSettings().BinderOutputPath, "NexusGeneratedBinder.g.cs"));

        private static bool GenerateBinderCore(bool write)
        {
            if (write) Debug.Log("[Nexus] Generating AOT Binder...");
            // Output settings decide which assembly compiles the binder, so they are needed before scanning.
            var settings = NexusEditorSettings.GetOrCreateSettings();
            var injectTypes = new List<Type>();
            // Every injectable type (visible or not) + network signals: link.xml preservation
            // covers the full set so IL2CPP keeps the reflection path alive even for types the
            // generated binder cannot reference.
            var linkXmlTypes = new List<Type>();
            var networkSignalTypes = new List<Type>();
            // (command, signal, isAsync) triples for generic-only command dispatchers.
            var genericCommandPairs = new List<(Type Command, Type Signal, bool IsAsync)>();
            // Constructor factory candidates: (type, ctor). Mirrors NexusDI's runtime ctor
            // selection — exactly one [Inject]/[Construct]-marked public ctor, or a single public
            // ctor on an injectable/DI type. Every other shape keeps the reflection path.
            var ctorFactoryTypes = new List<(Type Type, ConstructorInfo Ctor)>();
            // Shared across all passes below so each type's members reflect exactly once.
            var memberCache = new Dictionary<Type, MemberSet>();
            // Issue #85: a type declared under #if UNITY_EDITOR / DEVELOPMENT_BUILD is named only under that #if.
            var conditions = new BinderConditions(AssemblyCatalog.SourceAssemblies(settings.BinderOutputPath, out string binderAssembly), binderAssembly);
            // [Inject] members no path can set (#191): a static member, a get-only property, a value type. Generation stops
            // and names every one; the reflection path throws for the same members when it injects their type.
            var invalid = new SortedSet<string>(StringComparer.Ordinal);
            // Types the binder leaves to the reflection path, with each reason, written at the top of the binder.
            var fallback = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            void Fallback(Type type, string reason)
            {
                string key = DisplayName(type);
                if (!fallback.TryGetValue(key, out var reasons)) fallback[key] = reasons = new SortedSet<string>(StringComparer.Ordinal);
                reasons.Add(reason);
            }
            // Public types whose constructor factory was not emitted: link.xml keeps their constructors for the reflection path.
            var reflectionConstructed = new List<Type>();
            void Validate(MemberSet set)
            {
                foreach (var f in set.Fields)
                    if (f.FieldType.IsValueType) invalid.Add($"{f.DeclaringType.FullName}.{f.Name}: a value type ({f.FieldType.Name})");
                foreach (var p in set.Properties)
                {
                    if (!p.CanWrite) invalid.Add($"{p.DeclaringType.FullName}.{p.Name}: get-only, add a setter (it may be private)");
                    else if (p.PropertyType.IsValueType) invalid.Add($"{p.DeclaringType.FullName}.{p.Name}: a value type ({p.PropertyType.Name})");
                }
                foreach (var m in set.Methods)
                    foreach (var prm in m.GetParameters())
                        if (prm.ParameterType.IsValueType) invalid.Add($"{m.DeclaringType.FullName}.{m.Name}({prm.Name}): a value type ({prm.ParameterType.Name})");
            }
            string InjectCondition(Type type)
            {
                var set = GetMemberSet(type, memberCache);
                var named = new List<Type> { type };
                foreach (var f in set.Fields)
                    if (HasInject(f)) { named.Add(f.DeclaringType); named.Add(f.FieldType); }
                foreach (var p in set.Properties)
                    if (HasInject(p)) { named.Add(p.DeclaringType); named.Add(p.PropertyType); }
                foreach (var m in set.Methods)
                {
                    if (!HasInject(m)) continue;
                    named.Add(m.DeclaringType);
                    foreach (var prm in m.GetParameters()) named.Add(prm.ParameterType);
                }
                return conditions.For(named);
            }

            // Gather all types containing [Inject] and all INetworkSignal implementations from every assembly that
            // references Nexus (link.xml must preserve all of them). Binder code is emitted only for the assemblies the
            // binder's own assembly can reference; the rest keep the reflection path, which link.xml keeps alive.
            var emittableAssemblies = new HashSet<Assembly>(AssemblyCatalog.BinderAssemblies(settings.BinderOutputPath));
            foreach (var assembly in AssemblyCatalog.NexusReferencingAssemblies())
            {
                // The Nexus packages' types take the reflection path and each package's own link.xml preserves them:
                // a package type named here would break the game's compile the day the package removes it (#110).
                if (AssemblyCatalog.IsNexusPackageAssembly(AssemblyCatalog.GetSimpleName(assembly))) continue;
                bool emittable = emittableAssemblies.Contains(assembly);
                // [assembly: BinderExclude] (#158): the binder names no type under the assembly's prefixes; an injectable one
                // keeps link.xml and the reflection path, and the binder's header lists it.
                string[] excludedPrefixes = BinderExcludedPrefixes(assembly);
                foreach (var type in AssemblyCatalog.GetTypesSafe(assembly))
                {
                    bool excluded = IsBinderExcluded(type, excludedPrefixes);
                    foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                        if (!(member is Type) && (member.IsDefined(typeof(InjectAttribute), false) || member.IsDefined(typeof(OptionalInjectAttribute), false)))
                            invalid.Add($"{type.FullName}.{member.Name}: static, Nexus injects instance members only");

                    if (type.IsValueType && typeof(Nexus.Netcode.INetworkSignal).IsAssignableFrom(type))
                    {
                        networkSignalTypes.Add(type);
                    }

                    // Generic-only commands (ICommand<T>/IAsyncCommand<T>): on IL2CPP the
                    // runtime dispatcher falls back to MethodInfo.Invoke with a per-call
                    // object[] allocation. Emitting strongly-typed dispatcher registrations
                    // keeps the AOT dispatch path allocation-free.
                    if (emittable && !excluded && type.IsClass && !type.IsAbstract && !type.IsGenericTypeDefinition)
                    {
                        foreach (var iface in type.GetInterfaces())
                        {
                            if (!iface.IsGenericType) continue;
                            var genericDef = iface.GetGenericTypeDefinition();
                            if (genericDef == typeof(ICommand<>))
                                genericCommandPairs.Add((type, iface.GetGenericArguments()[0], false));
                            else if (genericDef == typeof(IAsyncCommand<>))
                                genericCommandPairs.Add((type, iface.GetGenericArguments()[0], true));
                        }
                    }

                    if (type.IsClass && !type.IsAbstract)
                    {
                        // Binder-emission visibility gate. Compiler-generated closure/display
                        // types (`<>c__DisplayClass*`, `<>c`) carry '<'/'>' in their names, which
                        // are NEVER valid C# identifiers (emitting them produced CS1001 in the
                        // generated binder), and non-visible types (private/internal nested) cannot
                        // be referenced from the generated file (CS0122). Both are skipped from
                        // EMISSION, but injectable ones still go into link.xml below so IL2CPP
                        // preserves the reflection path that serves them.
                        bool codegenVisible = type.IsVisible
                            && !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false);

                        ConstructorInfo markedCtor = null;
                        int markedCtorCount = 0;
                        var publicCtors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                        if (publicCtors.Length > 0)
                        {
                            for (int c = 0; c < publicCtors.Length; c++)
                            {
                                if (publicCtors[c].IsDefined(typeof(InjectAttribute), false)
                                    || publicCtors[c].IsDefined(typeof(ConstructAttribute), false))
                                {
                                    markedCtorCount++;
                                    markedCtor = publicCtors[c];
                                }
                            }
                        }

                        // Chain walk (base-first) so a [Inject] on a PRIVATE base-class member
                        // marks the leaf type injectable — the reflection path injects it, so the
                        // type must at least be preserved in link.xml (the emitter below then
                        // decides whether the binder can replicate it).
                        var members = GetMemberSet(type, memberCache);
                        bool hasInject = members.Fields.Length > 0 || members.Properties.Length > 0 || members.Methods.Length > 0;
                        // An [Inject] member or constructor under an #if of its own (#191): a binder line naming it would
                        // break the player compile, or set a member the player does not have.
                        string memberProblem = emittable && codegenVisible && !excluded && IsEmittableType(type) ? conditions.MemberProblem(type) : null;

                        if (hasInject)
                        {
                            Validate(members);
                            linkXmlTypes.Add(type);
                            if (emittable && codegenVisible && !excluded && IsEmittableType(type))
                            {
                                if (memberProblem == null) injectTypes.Add(type);
                                else Fallback(type, memberProblem);
                            }
                            else if (emittable && !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
                                Fallback(type, excluded ? BinderExcludedReason : type.ContainsGenericParameters
                                    ? "an open generic type: the binder names closed types only"
                                    : "not visible from the binder (internal or private)");
                        }
                        // Created only through Activator: a lifecycle found by name convention and an
                        // AddSignalFilter<TSignal, TFilter> type that is not bound. Nothing references
                        // them statically, so Medium/High stripping removed them (issue #37).
                        else if (typeof(IContextLifecycle).IsAssignableFrom(type) || ImplementsSignalFilter(type))
                        {
                            linkXmlTypes.Add(type);
                        }

                        // Constructor factory emission: emittable types that may be constructed with
                        // `new` (CanEmitConstructorFactory) and have one [Inject]/[Construct]-marked
                        // public ctor or a single public ctor. Whether such a type is ever bound in
                        // DI is only known at runtime, so an unused factory is simply inert.
                        if (emittable && codegenVisible && !excluded && IsEmittableType(type) && !type.IsGenericTypeDefinition
                            && CanEmitConstructorFactory(type))
                        {
                            ConstructorInfo ctorToRegister = null;
                            if (markedCtorCount == 1)
                            {
                                ctorToRegister = markedCtor;
                            }
                            else if (markedCtorCount == 0)
                            {
                                if (publicCtors.Length == 1)
                                    ctorToRegister = publicCtors[0];
                            }

                            if (ctorToRegister != null && memberProblem == null)
                                ctorFactoryTypes.Add((type, ctorToRegister));
                            else if (ctorToRegister != null)
                            {
                                Fallback(type, memberProblem);
                                if (!linkXmlTypes.Contains(type)) reflectionConstructed.Add(type);
                            }
                        }
                    }
                }
            }

            var cacheSb = new StringBuilder();
            var initSb = new StringBuilder();
            var dispatcherSb = new StringBuilder();

            if (invalid.Count > 0)
                throw new InvalidOperationException("[Nexus CodeGen Error] These [Inject] members cannot be injected, so no binder was written:"
                    + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", invalid));

            // NetworkSignalBus dispatch is intentionally not emitted here.  The runtime
            // exposes only generic Fire<T> APIs, so a generated assignment to a
            // NetworkSignalBus.CustomDispatcher member would make every project that
            // declares an INetworkSignal fail to compile (the member does not exist).
            // Network signal types are still included in link.xml below so IL2CPP can
            // preserve their serialized fields.

            // Constructor factories: zero-reflection instantiation on IL2CPP, with the reflection path's answers (#191):
            // an [OptionalInject] or null-default parameter is TryResolve (null without a log), a required one
            // di.ResolveConstructorParameter<T> (strict throw, or warn and pass null), a named one its named binding.
            // A parameter with any other default keeps the reflection path, which passes the declared default; the
            // binder's header lists the type and link.xml keeps its constructors.
            var ctorFactorySb = new StringBuilder();
            foreach (var (type, ctor) in ctorFactoryTypes)
            {
                string fullName = GetCSharpTypeName(type);
                if (fullName == null) continue;
                var parameters = ctor.GetParameters();
                var argList = new List<string>(parameters.Length);
                bool skipType = false;
                for (int i = 0; i < parameters.Length; i++)
                {
                    var p = parameters[i];
                    // A value-type parameter takes an explicit value (WithParameter), which a factory does not; a parameter
                    // type the binder cannot name is CS0122. Most such classes are never bound, so only an injectable
                    // type, which link.xml keeps whole, is listed.
                    string pTypeName = p.ParameterType.IsValueType ? null : GetCSharpTypeName(p.ParameterType);
                    if (pTypeName == null || !p.ParameterType.IsVisible)
                    {
                        if (linkXmlTypes.Contains(type))
                            Fallback(type, p.ParameterType.IsValueType
                                ? $"constructor parameter '{p.Name}' is a value type, which only an explicit value supplies"
                                : $"constructor parameter '{p.Name}' has a type the binder cannot name");
                        skipType = true;
                        break;
                    }
                    if (p.HasDefaultValue && p.DefaultValue != null)
                    {
                        Fallback(type, $"constructor parameter '{p.Name}' has a default value the binder does not repeat");
                        if (!linkXmlTypes.Contains(type)) reflectionConstructed.Add(type);
                        skipType = true;
                        break;
                    }
                    string paramName = p.GetCustomAttribute<InjectAttribute>()?.Name;
                    if (p.IsDefined(typeof(OptionalInjectAttribute), false) || p.HasDefaultValue)
                        argList.Add($"di.TryResolve<{pTypeName}>({NameArgument(paramName)})");
                    else
                    {
                        string nameArg = string.IsNullOrEmpty(paramName) ? "" : ", " + Literal(paramName);
                        argList.Add($"di.ResolveConstructorParameter<{pTypeName}>({i}, {Literal(type.FullName)}, {Literal(p.ParameterType.FullName)}{nameArg})");
                    }
                }
                if (skipType) continue;
                string factoryCondition = conditions.For(new[] { type }.Concat(parameters.Select(p => p.ParameterType)));
                if (factoryCondition == BinderConditions.Never)
                {
                    // a type whose #if could not be known: the reflection path constructs it, link.xml keeps the constructor
                    if (!linkXmlTypes.Contains(type)) reflectionConstructed.Add(type);
                    continue;
                }
                int factoryStart = ctorFactorySb.Length;
                // the parameters this factory is written for (InjectableMetadata.ConstructorSignature): NexusDI drops a stale one (#290)
                string ctorSignature = string.Join(",", parameters.Select(p => p.ParameterType.Name + SignatureFlags(IsOptional(p) || p.HasDefaultValue, InjectName(p))));
                ctorFactorySb.AppendLine($"            NexusDI.RegisterConstructorFactory<{fullName}>((di) => new {fullName}({string.Join(", ", argList)}), {Literal(ctorSignature)});");
                WrapInCondition(ctorFactorySb, factoryStart, factoryCondition);
            }

            // Generic-only command dispatchers: strongly-typed delegates registered into
            // CommandRegistry's dispatcher caches so IL2CPP builds never hit the
            // MethodInfo.Invoke fallback (per-dispatch object[] allocation).
            foreach (var (commandType, signalType, isAsync) in genericCommandPairs)
            {
                // typeof() on non-visible types is CS0122 from the generated binder; such
                // commands keep the runtime MethodInfo.Invoke fallback instead.
                string commandName = commandType.IsVisible ? GetCSharpTypeName(commandType) : null;
                string signalName = signalType.IsVisible ? GetCSharpTypeName(signalType) : null;
                if (commandName == null || signalName == null)
                {
                    Fallback(commandType, $"its {(isAsync ? "IAsyncCommand" : "ICommand")}<{signalType.Name}> is dispatched through MethodInfo.Invoke: the binder cannot name the command or the signal");
                    continue;
                }

                int dispatcherStart = dispatcherSb.Length;
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
                WrapInCondition(dispatcherSb, dispatcherStart, conditions.For(commandType, signalType));
            }

            // Injectors and clearers (#191). Every member resolves through TryResolve; an unbound required member goes to the
            // runtime (ReportMissingMember / ReportMissingParameter), so the generated path gives the reflection path's answer
            // (strict throw, or log and ReInjectAll) and an unbound optional member keeps its value. Each injector carries the
            // member signature it was generated for: the runtime drops an injector whose type has other members since.
            // All-or-nothing per type: a member the binder cannot name leaves the whole type to the reflection path.
            foreach (var type in injectTypes)
            {
                string fullName = GetCSharpTypeName(type);
                string typeSafeName = GetSafeIdentifierName(fullName);
                var set = GetMemberSet(type, memberCache);

                // The framework and third-party exclusion applies to the declaring types; a member's type only has to be
                // nameable, so a System.Random or IReadOnlyList<IFoo> member took the reflection path, called not visible (#245).
                var memberTypes = set.Fields.Select(f => f.FieldType).Concat(set.Properties.Select(p => p.PropertyType))
                    .Concat(set.Methods.SelectMany(m => m.GetParameters().Select(x => x.ParameterType))).ToList();
                bool emittable = set.Fields.All(f => IsEmittableType(f.DeclaringType)) && set.Properties.All(p => IsEmittableType(p.DeclaringType))
                    && set.Methods.All(m => IsEmittableType(m.DeclaringType)) && memberTypes.All(IsVisibleMemberType);
                if (!emittable)
                {
                    Fallback(type, "an [Inject] member's declaring or member type is not visible from the binder");
                    continue;
                }
                if (!memberTypes.All(IsReferencedMemberType))
                {
                    Fallback(type, "an [Inject] member's type is in a Unity package or third-party assembly the binder's assembly may not reference");
                    continue;
                }

                int cacheStart = cacheSb.Length, initStart = initSb.Length;
                var cacheNames = new HashSet<string>(StringComparer.Ordinal);
                string Target(Type declaring) => declaring == type ? "instance" : $"(({GetCSharpTypeName(declaring)})instance)";
                string Cache(string kind, string reflectionType, MemberInfo member, string lookup)
                {
                    string declaringName = GetCSharpTypeName(member.DeclaringType);
                    string cacheName = $"s_{kind}_{typeSafeName}_{GetSafeIdentifierName(declaringName)}_{GetSafeIdentifierName(member.Name)}";
                    if (cacheNames.Add(cacheName))
                        cacheSb.AppendLine($"        private static readonly System.Reflection.{reflectionType} {cacheName} = typeof({declaringName}).{lookup}({Literal(member.Name)}, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);");
                    return cacheName;
                }
                // Public members are set directly; a private or protected member, a readonly field and a non-public or
                // init-only setter go through the cached FieldInfo/PropertyInfo.
                string Get(MemberInfo member)
                {
                    if (member is FieldInfo f)
                        return f.IsPublic ? $"{Target(f.DeclaringType)}.{f.Name}" : $"{Cache("f", "FieldInfo", f, "GetField")}.GetValue(instance)";
                    var p = (PropertyInfo)member;
                    return p.GetMethod != null && p.GetMethod.IsPublic ? $"{Target(p.DeclaringType)}.{p.Name}" : $"{Cache("p", "PropertyInfo", p, "GetProperty")}.GetValue(instance)";
                }
                string Set(MemberInfo member, string value)
                {
                    if (member is FieldInfo f)
                        return f.IsPublic && !f.IsInitOnly ? $"{Target(f.DeclaringType)}.{f.Name} = {value};" : $"{Cache("f", "FieldInfo", f, "GetField")}.SetValue(instance, {value});";
                    var p = (PropertyInfo)member;
                    var setter = p.GetSetMethod(true);
                    return setter.IsPublic && !IsInitOnly(setter) ? $"{Target(p.DeclaringType)}.{p.Name} = {value};" : $"{Cache("p", "PropertyInfo", p, "GetProperty")}.SetValue(instance, {value});";
                }
                void EmitMember(MemberInfo member, Type memberType)
                {
                    string name = InjectName(member);
                    if (IsLazy(memberType))
                    {
                        // the reflection path assigns a LazyInjection<T> only to a member that holds none
                        initSb.AppendLine($"                if ({Get(member)} == null) {Set(member, $"di.CreateLazyInjection<{GetCSharpTypeName(memberType.GetGenericArguments()[0])}>({NameArgument(name)})")}");
                        return;
                    }
                    initSb.AppendLine("                {");
                    initSb.AppendLine($"                    var value = di.TryResolve<{GetCSharpTypeName(memberType)}>({NameArgument(name)});");
                    initSb.AppendLine($"                    if (value != null) {Set(member, "value")}");
                    if (!IsOptional(member))
                        initSb.AppendLine($"                    else di.ReportMissingMember(instance, typeof({GetCSharpTypeName(member.DeclaringType)}), {Literal(member.Name)});");
                    initSb.AppendLine("                }");
                }

                initSb.AppendLine($"            NexusDI.RegisterInjector<{fullName}>((instance, di) =>");
                initSb.AppendLine("            {");
                foreach (var f in set.Fields) EmitMember(f, f.FieldType);
                foreach (var p in set.Properties) EmitMember(p, p.PropertyType);

                // Methods go through the cached MethodInfo of their declaring level (DeclaredOnly, explicit parameter types):
                // Invoke dispatches a virtual method to its override, as the reflection path does. Parameters resolve in
                // order; an unbound required one is reported and the method is not invoked (ReInjectAll invokes it later).
                for (int methodIndex = 0; methodIndex < set.Methods.Length; methodIndex++)
                {
                    var m = set.Methods[methodIndex];
                    var prms = m.GetParameters();
                    string declaringName = GetCSharpTypeName(m.DeclaringType);
                    string cacheName = $"s_m_{typeSafeName}_{GetSafeIdentifierName(declaringName)}_{GetSafeIdentifierName(m.Name)}_{methodIndex}";
                    string parameterTypes = string.Join(", ", prms.Select(x => $"typeof({GetCSharpTypeName(x.ParameterType)})"));
                    cacheSb.AppendLine($"        private static readonly System.Reflection.MethodInfo {cacheName} = typeof({declaringName}).GetMethod({Literal(m.Name)}, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly, null, new System.Type[] {{ {parameterTypes} }}, null);");
                    initSb.AppendLine("                {");
                    initSb.AppendLine($"                    if ({cacheName} == null) throw new InvalidOperationException({Literal($"[Nexus CodeGen] Failed to bind injector method {fullName}.{m.Name}.")});");
                    bool anyRequired = prms.Any(x => !IsOptional(x));
                    if (anyRequired) initSb.AppendLine("                    bool ready = true;");
                    for (int i = 0; i < prms.Length; i++)
                    {
                        initSb.AppendLine($"                    var a{i} = di.TryResolve<{GetCSharpTypeName(prms[i].ParameterType)}>({NameArgument(InjectName(prms[i]))});");
                        if (!IsOptional(prms[i]))
                            initSb.AppendLine($"                    if (a{i} == null && !di.ReportMissingParameter(instance, {cacheName}, {i})) ready = false;");
                    }
                    string args = prms.Length == 0 ? "System.Array.Empty<object>()" : "new object[] { " + string.Join(", ", prms.Select((x, i) => "a" + i)) + " }";
                    initSb.AppendLine($"                    {(anyRequired ? "if (ready) " : "")}{cacheName}.Invoke(instance, {args});");
                    initSb.AppendLine("                }");
                }
                initSb.AppendLine($"            }}, {Literal(MemberSignature(set))});");

                // Clearer (0 GC pooling reuse): every injected field and property, as the reflection clearer does.
                if (set.Fields.Length > 0 || set.Properties.Length > 0)
                {
                    initSb.AppendLine($"            NexusDI.RegisterClearer<{fullName}>(instance =>");
                    initSb.AppendLine("            {");
                    foreach (var f in set.Fields) initSb.AppendLine($"                {Set(f, "null")}");
                    foreach (var p in set.Properties) initSb.AppendLine($"                {Set(p, "null")}");
                    initSb.AppendLine("            });");
                }

                string injectCondition = InjectCondition(type);
                WrapInCondition(cacheSb, cacheStart, injectCondition);
                WrapInCondition(initSb, initStart, injectCondition);
            }

            // Assemble the final file
            var sb = new StringBuilder();
            sb.AppendLine("#define NEXUS_GENERATED_BINDER");
            sb.AppendLine("//------------------------------------------------------------------------------");
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//     This code was generated by Nexus AOT Binder Code Generator.");

            // Read package version from package.json
            string packageVersion = "unknown";
            try
            {
                string packageJsonPath = "Packages/com.nexus.core/package.json";
                if (!File.Exists(packageJsonPath))
                {
                    // Search in project Packages folder
                    packageJsonPath = Path.Combine(Application.dataPath, "../Packages/com.nexus.core/package.json");
                }
                if (File.Exists(packageJsonPath))
                {
                    string json = File.ReadAllText(packageJsonPath);
                    int idx = json.IndexOf("\"version\":");
                    if (idx >= 0)
                    {
                        int start = json.IndexOf("\"", idx + 10);
                        int end = json.IndexOf("\"", start + 1);
                        packageVersion = json.Substring(start + 1, end - start - 1);
                    }
                }
            }
            catch
            {
                packageVersion = "unknown";
            }

            sb.AppendLine($"//     Package Version: {packageVersion}");
            sb.AppendLine("//     Changes to this file may cause incorrect behavior and will be lost if");
            sb.AppendLine("//     the code is regenerated.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("//------------------------------------------------------------------------------");
            // Types whose #if could not be known (#191): no binder line names them, so nothing the player lacks is named.
            foreach (var problem in conditions.Problems)
                Fallback(problem.Key, "every binder line that names it is left out: " + problem.Value);
            if (write)
                foreach (var warning in conditions.Warnings)
                    Debug.LogWarning($"[Nexus] Binder conditions: {warning}. The types this hides keep the reflection path; the binder lists them at its top.");
            if (fallback.Count > 0)
            {
                sb.AppendLine("// The reflection path serves these types (the binder does not name them):");
                foreach (var entry in fallback) sb.AppendLine($"//   {entry.Key}: {string.Join("; ", entry.Value)}");
            }
            sb.AppendLine();
            sb.AppendLine("#pragma warning disable CS0618");
            sb.AppendLine("using System;");
            sb.AppendLine("using Nexus.Core;");
            sb.AppendLine("using Nexus.Netcode;");
            sb.AppendLine();
            sb.AppendLine("namespace Nexus.Generated");
            sb.AppendLine("{");
            sb.AppendLine("    public static class NexusGeneratedBinder");
            sb.AppendLine("    {");
            
            // Write static cached fields
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

            // Output settings (Issue 18)
            string binderFolder = settings.BinderOutputPath;
            string linkXmlFolder = settings.LinkXmlOutputPath;

            // Delete old file if the path has changed
            string defaultFolder = "Assets/Scripts/Nexus";
            string defaultFile = Path.Combine(defaultFolder, "NexusGeneratedBinder.g.cs");
            if (write && binderFolder != defaultFolder && File.Exists(defaultFile))
            {
                File.Delete(defaultFile);
                if (File.Exists(defaultFile + ".meta"))
                    File.Delete(defaultFile + ".meta");
            }

            // Create target folders
            if (write && !Directory.Exists(binderFolder))
                Directory.CreateDirectory(binderFolder);
            if (write && !Directory.Exists(linkXmlFolder))
                Directory.CreateDirectory(linkXmlFolder);

            bool changed = false;

            // Preflight the preservation file before touching the generated binder.  A
            // curated/non-auto link.xml must fail atomically; otherwise a failed codegen
            // could leave a new binder paired with stale IL2CPP stripping rules.
            string destLinkXmlFile = Path.Combine(linkXmlFolder, "link.xml");
            if (write && File.Exists(destLinkXmlFile) && !File.ReadAllText(destLinkXmlFile).Contains("<auto-generated>"))
            {
                throw new InvalidOperationException(
                    $"[Nexus CodeGen] Overwrite blocked: the file at '{destLinkXmlFile}' " +
                    "does not contain the <auto-generated> tag. Move/merge the curated " +
                    "rules, then regenerate the Nexus AOT link.xml before building.");
            }

            // Write binder file with overwrite guard
            string destBinderFile = Path.Combine(binderFolder, "NexusGeneratedBinder.g.cs");
            string newBinderContent = sb.ToString();

            if (string.IsNullOrEmpty(newBinderContent))
            {
                throw new InvalidOperationException("[Nexus CodeGen] Code generation aborted: generated binder content is empty.");
            }

            // With the source generator on, the compiler writes the binder: only link.xml is generated here, which preserves
            // the types the binder cannot name. The build guard wrote the file again after Mode B deleted it, and the player
            // build failed with two NexusGeneratedBinder classes (CS0101, #242).
            string sourceGenerator = SourceGeneratorSwitch(binderFolder);
            bool binderCurrent;
            if (sourceGenerator != null)
            {
                binderCurrent = !File.Exists(destBinderFile);
                if (write && !binderCurrent)
                    throw new InvalidOperationException($"[Nexus CodeGen] The source generator writes the binder ({sourceGenerator} switches it on), and '{destBinderFile}' " +
                        "declares it a second time (CS0101). Delete the file: with the source generator on, generation and the build write only link.xml.");
                if (write) Debug.Log($"[Nexus] The source generator writes the AOT binder ({sourceGenerator} switches it on); only link.xml is generated.");
            }
            else
            {
                // Write an empty binder even with no inject types, so stale generated injectors are cleared.
                if (write && injectTypes.Count == 0)
                {
                    Debug.LogWarning("[Nexus] AOT Binder generation: no injectable types discovered. An empty binder was written to clear any previously generated injectors.");
                }

                if (write && File.Exists(destBinderFile))
                {
                    string existingContent = File.ReadAllText(destBinderFile);
                    if (!existingContent.Contains("<auto-generated>"))
                    {
                        throw new InvalidOperationException($"[Nexus CodeGen] Overwrite blocked: the file at '{destBinderFile}' does not contain the <auto-generated> tag. It may have been manually modified by the user.");
                    }
                }

                binderCurrent = File.Exists(destBinderFile) && File.ReadAllText(destBinderFile) == newBinderContent;
                if (write && !binderCurrent)
                {
                    File.WriteAllText(destBinderFile, newBinderContent);
                    changed = true;
                }
                if (write) EnsureGitIgnore(binderFolder, "NexusGeneratedBinder.g.cs");
            }

            // Write link.xml (Issue 4). Iterates linkXmlTypes — injectables that were excluded
            // from binder emission (compiler-generated/non-visible) are still preserved here so
            // their reflection-path injection survives IL2CPP stripping.
            var typesByAssembly = new Dictionary<string, List<Type>>();
            foreach (var type in linkXmlTypes)
            {
                var asmName = type.Assembly.GetName().Name;
                if (!typesByAssembly.TryGetValue(asmName, out var list))
                {
                    list = new List<Type>();
                    typesByAssembly[asmName] = list;
                }
                list.Add(type);
            }
            foreach (var type in networkSignalTypes.Concat(reflectionConstructed))
            {
                var asmName = type.Assembly.GetName().Name;
                if (!typesByAssembly.TryGetValue(asmName, out var list))
                {
                    list = new List<Type>();
                    typesByAssembly[asmName] = list;
                }
                if (!list.Contains(type))
                    list.Add(type);
            }

            var xmlSb = new StringBuilder();
            xmlSb.AppendLine("<!-- <auto-generated> -->");
            xmlSb.AppendLine("<linker>");
            foreach (var kvp in typesByAssembly)
            {
                // ignoreIfMissing: the editor loads assemblies a player lacks (editor-only, define- or platform-gated), and
                // one link.xml serves every build target; the linker fails on a named assembly it cannot find (#241).
                xmlSb.AppendLine($"  <assembly fullname=\"{kvp.Key}\" ignoreIfMissing=\"true\">");
                foreach (var type in kvp.Value)
                {
                    xmlSb.AppendLine(reflectionConstructed.Contains(type)
                        ? $"    <type fullname=\"{type.FullName}\" preserve=\"nothing\"><method name=\".ctor\" /></type>"
                        : $"    <type fullname=\"{type.FullName}\" preserve=\"all\" />");
                }
                xmlSb.AppendLine("  </assembly>");
            }
            xmlSb.AppendLine("</linker>");

            string newLinkXmlContent = xmlSb.ToString();
            if (!write)
                return binderCurrent && File.Exists(destLinkXmlFile) && File.ReadAllText(destLinkXmlFile) == newLinkXmlContent;

            // Overwrite guard: a hand-curated link.xml is never silently replaced.
            // Failing loudly is important here.  Silently retaining stale preservation
            // rules lets an IL2CPP build continue with stripped injected members.
            if (File.Exists(destLinkXmlFile))
            {
                string existingLinkXml = File.ReadAllText(destLinkXmlFile);
                if (!existingLinkXml.Contains("<auto-generated>"))
                {
                    throw new InvalidOperationException(
                        $"[Nexus CodeGen] Overwrite blocked: the file at '{destLinkXmlFile}' " +
                        "does not contain the <auto-generated> tag. Move/merge the curated " +
                        "rules, then regenerate the Nexus AOT link.xml before building.");
                }

                if (existingLinkXml != newLinkXmlContent)
                {
                    File.WriteAllText(destLinkXmlFile, newLinkXmlContent);
                    changed = true;
                }
            }
            else
            {
                File.WriteAllText(destLinkXmlFile, newLinkXmlContent);
                changed = true;
            }
            EnsureGitIgnore(linkXmlFolder, "link.xml");

            if (changed)
            {
                AssetDatabase.Refresh();
                if (sourceGenerator == null)
                    Debug.Log($"[Nexus] AOT Binder successfully generated at {destBinderFile}"
                        + (fallback.Count > 0 ? $"; {fallback.Count} type(s) keep the reflection path, listed at the top of the file" : ""));
                Debug.Log($"[Nexus] AOT link.xml successfully generated at {destLinkXmlFile}");
            }
            else
            {
                Debug.Log("[Nexus] AOT Binder generation completed with no file changes.");
            }
            return true;
        }

        /// <summary>Puts what was appended since <paramref name="start"/> under <c>#if condition</c>; null leaves it as is.</summary>
        private static void WrapInCondition(StringBuilder sb, int start, string condition)
        {
            if (condition == BinderConditions.Never) { sb.Length = start; return; }
            if (condition == null || sb.Length == start) return;
            sb.Insert(start, "#if " + condition + Environment.NewLine);
            sb.AppendLine("#endif");
        }

        private static void EnsureGitIgnore(string folder, string fileNameToIgnore)
        {
            string gitIgnorePath = Path.Combine(folder, ".gitignore");
            if (!File.Exists(gitIgnorePath))
            {
                File.WriteAllText(gitIgnorePath, fileNameToIgnore + "\n");
            }
            else
            {
                string content = File.ReadAllText(gitIgnorePath);
                if (!content.Contains(fileNameToIgnore))
                {
                    File.AppendAllText(gitIgnorePath, "\n" + fileNameToIgnore + "\n");
                }
            }
        }
    }
}
