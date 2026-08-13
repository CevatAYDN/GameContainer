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

        /// <summary>
        /// Returns true if the AOT binder has injectable types registered (non-empty binder will be generated).
        /// Used by the Dashboard plugin to warn users when AOT generation is disabled.
        /// </summary>
        public static bool HasInjectableTypes
        {
            get
            {
                return ScanForInjectables(AssemblyCatalog.RuntimeAssemblies(), stopOnFirstMatch: true);
            }
        }


        private static bool ScanForInjectables(IEnumerable<Assembly> assemblies, bool stopOnFirstMatch)
        {
            bool found = false;
            foreach (var assembly in assemblies)
            {
                foreach (var type in AssemblyCatalog.GetTypesSafe(assembly))
                {
                    if (!type.IsClass || type.IsAbstract)
                        continue;

                    if (HasInjectableMembers(type, stopOnFirstMatch))
                    {
                        if (stopOnFirstMatch)
                            return true;
                        found = true;
                    }
                }
            }

            return found;
        }

        private static bool HasInjectableMembers(Type type, bool stopOnFirstMatch)
        {
            var fields = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            foreach (var f in fields)
            {
                if (f.IsDefined(typeof(InjectAttribute), false) || f.IsDefined(typeof(OptionalInjectAttribute), false))
                    return true;
            }

            var properties = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            foreach (var p in properties)
            {
                if (p.IsDefined(typeof(InjectAttribute), false) || p.IsDefined(typeof(OptionalInjectAttribute), false))
                    return true;
            }

            if (!stopOnFirstMatch)
            {
                var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                foreach (var m in methods)
                {
                    if (m.IsDefined(typeof(InjectAttribute), false))
                        return true;
                }
            }

            return false;
        }

        [MenuItem("Nexus/Auto-Generate AOT on Script Reload")]
        private static void ToggleAutoGenerate()
        {
            AutoGenerateEnabled = !AutoGenerateEnabled;
            Menu.SetChecked("Nexus/Auto-Generate AOT on Script Reload", AutoGenerateEnabled);
            Debug.Log($"[Nexus] Auto-Generate AOT Binder on script reload: {(AutoGenerateEnabled ? "ENABLED" : "DISABLED")}");
        }

        [MenuItem("Nexus/Auto-Generate AOT on Script Reload", true)]
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

        /// <summary>Per-type reflection results shared across GenerateBinder's passes so
        /// GetFields/GetProperties/GetMethods run ONCE per type instead of once per pass
        /// (discovery, value-type check, injector gen, clearer gen, preserve gen).</summary>
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
                // Walk the inheritance chain base-first with DeclaredOnly — exactly like the
                // runtime Injector's MetadataCache — because reflection on the leaf type never
                // returns PRIVATE members declared on base classes. Without this, a [Inject] on
                // a private base-class field/property was silently never injected on the AOT
                // path while the reflection path injected it (FV3 divergence). Overridden
                // virtual properties are deduped by name so injection runs once per name.
                var chain = new List<Type>();
                for (var cur = type; cur != null && cur != typeof(object); cur = cur.BaseType)
                    chain.Add(cur);
                chain.Reverse();

                const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                var fields = new List<FieldInfo>();
                var properties = new List<PropertyInfo>();
                var methods = new List<MethodInfo>();
                var seenPropNames = new HashSet<string>();
                for (int level = 0; level < chain.Count; level++)
                {
                    fields.AddRange(chain[level].GetFields(Declared));
                    foreach (var p in chain[level].GetProperties(Declared))
                    {
                        if (seenPropNames.Add(p.Name)) properties.Add(p);
                    }
                    methods.AddRange(chain[level].GetMethods(Declared));
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

        private static bool HasInject(FieldInfo f)
            => f.GetCustomAttribute<InjectAttribute>() != null || f.GetCustomAttribute<OptionalInjectAttribute>() != null;

        private static bool HasInject(PropertyInfo p)
            => p.GetCustomAttribute<InjectAttribute>() != null || p.GetCustomAttribute<OptionalInjectAttribute>() != null;

        private static bool HasInject(MethodInfo m)
            => m.GetCustomAttribute<InjectAttribute>() != null;

        /// <summary>True when a type can be referenced from the generated binder file.</summary>
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

        /// <summary>E-C1: sanitizes a C# type name into a valid identifier prefix for cache fields.</summary>
        private static string GetSafeIdentifierName(string csharpTypeName)
        {
            var sb = new StringBuilder(csharpTypeName.Length);
            foreach (char c in csharpTypeName)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            return sb.ToString();
        }

        [MenuItem("Nexus/Generate AOT Binder")]
        public static void GenerateBinder()
        {
            Debug.Log("[Nexus] Generating AOT Binder...");
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

            // Gather all types containing [Inject] and all INetworkSignal implementations
            foreach (var assembly in AssemblyCatalog.RuntimeAssemblies())
            {
                foreach (var type in AssemblyCatalog.GetTypesSafe(assembly))
                {
                    if (type.IsValueType && typeof(Nexus.Netcode.INetworkSignal).IsAssignableFrom(type))
                    {
                        networkSignalTypes.Add(type);
                    }

                    // Generic-only commands (ICommand<T>/IAsyncCommand<T>): on IL2CPP the
                    // runtime dispatcher falls back to MethodInfo.Invoke with a per-call
                    // object[] allocation. Emitting strongly-typed dispatcher registrations
                    // keeps the AOT dispatch path allocation-free.
                    if (type.IsClass && !type.IsAbstract && !type.IsGenericTypeDefinition)
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

                        bool hasInject = false;

                        // Chain walk (base-first) so a [Inject] on a PRIVATE base-class member
                        // marks the leaf type injectable — the reflection path injects it, so the
                        // type must at least be preserved in link.xml (the emitter below then
                        // decides whether the binder can replicate it).
                        var members = GetMemberSet(type, memberCache);
                        foreach (var f in members.Fields)
                        {
                            if (f.IsDefined(typeof(InjectAttribute), false) || f.IsDefined(typeof(OptionalInjectAttribute), false))
                            {
                                hasInject = true;
                                break;
                            }
                        }

                        if (!hasInject)
                        {
                            foreach (var p in members.Properties)
                            {
                                if (p.IsDefined(typeof(InjectAttribute), false) || p.IsDefined(typeof(OptionalInjectAttribute), false))
                                {
                                    hasInject = true;
                                    break;
                                }
                            }
                        }

                        if (!hasInject)
                        {
                            foreach (var m in members.Methods)
                            {
                                if (m.IsDefined(typeof(InjectAttribute), false))
                                {
                                    hasInject = true;
                                    break;
                                }
                            }
                        }

                        if (hasInject)
                        {
                            linkXmlTypes.Add(type);
                            if (codegenVisible && IsEmittableType(type))
                                injectTypes.Add(type);
                        }

                        // Constructor factory emission: ONLY for emittable types that either have an
                        // explicit [Inject]/[Construct] constructor OR are injectable/DI types.
                        if (codegenVisible && IsEmittableType(type) && !type.IsGenericTypeDefinition)
                        {
                            ConstructorInfo ctorToRegister = null;
                            if (markedCtorCount == 1)
                            {
                                ctorToRegister = markedCtor;
                            }
                            else if (markedCtorCount == 0 && (hasInject
                                || typeof(ICommand).IsAssignableFrom(type)
                                || typeof(IAsyncCommand).IsAssignableFrom(type)
                                || typeof(INexusService).IsAssignableFrom(type)))
                            {
                                if (publicCtors.Length == 1)
                                    ctorToRegister = publicCtors[0];
                            }

                            if (ctorToRegister != null)
                            {
                                ctorFactoryTypes.Add((type, ctorToRegister));
                            }
                        }
                    }
                }
            }

            var cacheSb = new StringBuilder();
            var initSb = new StringBuilder();
            var preserveSb = new StringBuilder();
            var dispatcherSb = new StringBuilder();

            // Check value types first (Issue 6)
            foreach (var type in injectTypes)
            {
                var fields = GetMemberSet(type, memberCache).Fields;
                foreach (var f in fields)
                {
                    if ((f.GetCustomAttribute<InjectAttribute>() != null || f.GetCustomAttribute<OptionalInjectAttribute>() != null) && f.FieldType.IsValueType)
                    {
                        throw new InvalidOperationException($"[Nexus CodeGen Error] Field '{f.Name}' in type '{type.FullName}' has [Inject]/[OptionalInject] attribute but is a value type ({f.FieldType.Name}). Injection on value types is not supported because value types are passed by value and injected values will be lost.");
                    }
                }

                var properties = GetMemberSet(type, memberCache).Properties;
                foreach (var p in properties)
                {
                    if ((p.GetCustomAttribute<InjectAttribute>() != null || p.GetCustomAttribute<OptionalInjectAttribute>() != null) && p.PropertyType.IsValueType)
                    {
                        throw new InvalidOperationException($"[Nexus CodeGen Error] Property '{p.Name}' in type '{type.FullName}' has [Inject]/[OptionalInject] attribute but is a value type ({p.PropertyType.Name}). Injection on value types is not supported because value types are passed by value and injected values will be lost.");
                    }
                }
            }

            // NetworkSignalBus dispatch is intentionally not emitted here.  The runtime
            // exposes only generic Fire<T> APIs, so a generated assignment to a
            // NetworkSignalBus.CustomDispatcher member would make every project that
            // declares an INetworkSignal fail to compile (the member does not exist).
            // Network signal types are still included in link.xml below so IL2CPP can
            // preserve their serialized fields.

            // Constructor factories: zero-reflection instantiation on IL2CPP. The lambda calls
            // di.ResolveConstructorParameter<T> which reproduces the reflection path's
            // strict/warn semantics byte-for-byte; [OptionalInject] params use TryResolve, and
            // [Inject(Name=...)] params resolve the named binding.
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
                    if (p.ParameterType.IsValueType) { skipType = true; break; }
                    string pTypeName = GetCSharpTypeName(p.ParameterType);
                    // Non-visible parameter types (private/internal nested) cannot be
                    // referenced from the generated binder (CS0122) — keep the reflection path.
                    if (pTypeName == null || !p.ParameterType.IsVisible) { skipType = true; break; }
                    bool optional = p.IsDefined(typeof(OptionalInjectAttribute), false);
                    var paramInject = p.GetCustomAttribute<InjectAttribute>();
                    string paramName = paramInject?.Name;
                    if (optional)
                    {
                        argList.Add($"di.TryResolve<{pTypeName}>()");
                    }
                    else
                    {
                        string nameArg = string.IsNullOrEmpty(paramName) ? "" : $", \"{paramName}\"";
                        argList.Add($"di.ResolveConstructorParameter<{pTypeName}>({i}, \"{fullName}\", \"{pTypeName}\"{nameArg})");
                    }
                }
                if (skipType) continue;
                ctorFactorySb.AppendLine($"            NexusDI.RegisterConstructorFactory<{fullName}>((di) => new {fullName}({string.Join(", ", argList)}));");
            }

            // Generic-only command dispatchers: strongly-typed delegates registered into
            // CommandRegistry's dispatcher caches so IL2CPP builds never hit the
            // MethodInfo.Invoke fallback (per-dispatch object[] allocation).
            foreach (var (commandType, signalType, isAsync) in genericCommandPairs)
            {
                // typeof() on non-visible types is CS0122 from the generated binder; such
                // commands keep the runtime MethodInfo.Invoke fallback instead.
                if (!commandType.IsVisible || !signalType.IsVisible) continue;
                string commandName = GetCSharpTypeName(commandType);
                string signalName = GetCSharpTypeName(signalType);
                if (commandName == null || signalName == null) continue;

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

            // Generate Injectors and Cache Definitions (Issue 5 & 7). All-or-nothing per type:
            // a single non-emittable injectable member (non-visible or unnameable declaring or
            // member type, through the base chain) drops the WHOLE injector+clearer for the type
            // — the reflection path then serves it with identical semantics. A partial injector
            // would silently diverge (e.g. a private base-class [Inject] field left null on the
            // AOT path while the reflection path injects it).
            foreach (var type in injectTypes)
            {
                // E-C1 fix: use the compilable C# name; skip open-generic types that can
                // never be named (previously emitted `1 backtick arity markers → CS0246).
                string fullName = GetCSharpTypeName(type);
                if (fullName == null)
                {
                    Debug.LogWarning($"[Nexus CodeGen] Skipping open generic type '{type.FullName}' — injectors cannot be generated for unbound generic types.");
                    continue;
                }
                string typeSafeName = GetSafeIdentifierName(fullName);

                var fields = GetMemberSet(type, memberCache).Fields;
                var properties = GetMemberSet(type, memberCache).Properties;
                var methods = GetMemberSet(type, memberCache).Methods;

                bool emittable = true;
                foreach (var f in fields)
                {
                    if (!HasInject(f) || f.FieldType.IsValueType) continue;
                    if (!IsEmittableType(f.DeclaringType) || !IsEmittableType(f.FieldType)) { emittable = false; break; }
                }
                if (emittable)
                {
                    foreach (var p in properties)
                    {
                        if (!HasInject(p) || p.PropertyType.IsValueType || p.GetSetMethod(true) == null) continue;
                        if (!IsEmittableType(p.DeclaringType) || !IsEmittableType(p.PropertyType)) { emittable = false; break; }
                    }
                }
                if (emittable)
                {
                    foreach (var m in methods)
                    {
                        if (!HasInject(m)) continue;
                        if (!IsEmittableType(m.DeclaringType)) { emittable = false; break; }
                        foreach (var prm in m.GetParameters())
                        {
                            if (prm.ParameterType.IsValueType) continue; // method skipped, not the type
                            if (!IsEmittableType(prm.ParameterType)) { emittable = false; break; }
                        }
                        if (!emittable) break;
                    }
                }
                if (!emittable)
                {
                    Debug.LogWarning($"[Nexus CodeGen] Skipping injector/clearer for '{type.FullName}': an injectable member is not referenceable from the generated binder (non-visible/unnameable declaring or member type). The reflection path serves this type with identical semantics.");
                    continue;
                }

                initSb.AppendLine($"            NexusDI.RegisterInjector<{fullName}>((instance, di) =>");
                initSb.AppendLine("            {");

                // Inject Fields
                foreach (var f in fields)
                {
                    bool fOptional = f.GetCustomAttribute<OptionalInjectAttribute>() != null;
                    if (!HasInject(f) || f.FieldType.IsValueType) continue;
                    {
                        // Optional members resolve through TryResolve (null when unbound) so an
                        // absent optional dependency never throws at boot on the AOT path.
                        string fTypeName = GetCSharpTypeName(f.FieldType);
                        string fResolve = fOptional
                            ? $"di.TryResolve<{fTypeName}>()"
                            : $"di.Resolve<{fTypeName}>()";
                        if (f.IsPublic)
                        {
                            initSb.AppendLine($"                instance.{f.Name} = {fResolve};");
                        }
                        else
                        {
                            // Cache FieldInfo from the DECLARING type (base-class private fields
                            // are not visible through the leaf type's GetField) and key the cache
                            // field by declaring type so shadowed names cannot collide.
                            string declaringName = GetCSharpTypeName(f.DeclaringType);
                            string cacheFieldName = $"s_f_{typeSafeName}_{GetSafeIdentifierName(declaringName)}_{f.Name}";
                            cacheSb.AppendLine($"        private static readonly System.Reflection.FieldInfo {cacheFieldName} = typeof({declaringName}).GetField(\"{f.Name}\", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);");
                            initSb.AppendLine($"                {cacheFieldName}.SetValue(instance, {fResolve});");
                        }
                    }
                }

                // Inject Properties
                foreach (var p in properties)
                {
                    bool pOptional = p.GetCustomAttribute<OptionalInjectAttribute>() != null;
                    if (!HasInject(p) || p.PropertyType.IsValueType) continue;
                    var setMethod = p.GetSetMethod(true);
                    if (setMethod != null)
                    {
                        string pTypeName = GetCSharpTypeName(p.PropertyType);
                        string pResolve = pOptional
                            ? $"di.TryResolve<{pTypeName}>()"
                            : $"di.Resolve<{pTypeName}>()";
                        if (setMethod.IsPublic)
                        {
                            initSb.AppendLine($"                instance.{p.Name} = {pResolve};");
                        }
                        else
                        {
                            string declaringName = GetCSharpTypeName(p.DeclaringType);
                            string cachePropName = $"s_p_{typeSafeName}_{GetSafeIdentifierName(declaringName)}_{p.Name}";
                            cacheSb.AppendLine($"        private static readonly System.Reflection.PropertyInfo {cachePropName} = typeof({declaringName}).GetProperty(\"{p.Name}\", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);");
                            initSb.AppendLine($"                {cachePropName}.SetValue(instance, {pResolve});");
                        }
                    }
                }

                // Inject Methods (method-level OptionalInject is impossible per AttributeUsage;
                // per-PARAM [OptionalInject] below is the meaningful case → TryResolve).
                // All methods go through the cached MethodInfo + Invoke path (declaring type +
                // explicit parameter types): the runtime itself invokes methods via
                // MethodInfo.Invoke, and this removes overload-ambiguity and base-class
                // visibility issues that direct calls would have.
                foreach (var m in methods)
                {
                    if (!HasInject(m)) continue;
                    bool hasValueTypeParams = false;
                    var paramList = new List<string>();
                    foreach (var param in m.GetParameters())
                    {
                        if (param.ParameterType.IsValueType)
                        {
                            hasValueTypeParams = true;
                            break;
                        }
                        // [OptionalInject] on a parameter → TryResolve (null when unbound).
                        bool paramOptional = param.GetCustomAttribute<OptionalInjectAttribute>() != null;
                        string paramTypeName = GetCSharpTypeName(param.ParameterType);
                        paramList.Add(paramOptional
                            ? $"di.TryResolve<{paramTypeName}>()"
                            : $"di.Resolve<{paramTypeName}>()");
                    }
                    if (hasValueTypeParams) continue;

                    string declaringName = GetCSharpTypeName(m.DeclaringType);
                    string cacheMethodName = $"s_m_{typeSafeName}_{GetSafeIdentifierName(declaringName)}_{m.Name}";
                    var paramTypesString = string.Join(", ", m.GetParameters().Select(param => $"typeof({GetCSharpTypeName(param.ParameterType)})"));
                    cacheSb.AppendLine($"        private static readonly System.Reflection.MethodInfo {cacheMethodName} = typeof({declaringName}).GetMethod(\"{m.Name}\", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new System.Type[] {{ {paramTypesString} }}, null);");
                    initSb.AppendLine($"                if ({cacheMethodName} == null) throw new InvalidOperationException(\"[Nexus CodeGen] Failed to bind injector method {fullName}.{m.Name}.\");");
                    initSb.AppendLine($"                {cacheMethodName}.Invoke(instance, new object[] {{ {string.Join(", ", paramList)} }});");
                }

                initSb.AppendLine("            });");

                // Generate AOT Clearers (0 GC Allocation optimization for pooling reuse)
                // [OptionalInject]-only members are cleared too (they may hold a resolved value).
                var clearFields = fields.Where(f => HasInject(f) && !f.FieldType.IsValueType).ToList();
                var clearProps = properties.Where(p => HasInject(p) && !p.PropertyType.IsValueType && p.GetSetMethod(true) != null).ToList();

                if (clearFields.Count > 0 || clearProps.Count > 0)
                {
                    initSb.AppendLine($"            NexusDI.RegisterClearer<{fullName}>(instance =>");
                    initSb.AppendLine("            {");

                    foreach (var f in clearFields)
                    {
                        if (f.IsPublic)
                        {
                            initSb.AppendLine($"                instance.{f.Name} = null;");
                        }
                        else
                        {
                            string declaringName = GetCSharpTypeName(f.DeclaringType);
                            string cacheFieldName = $"s_f_{typeSafeName}_{GetSafeIdentifierName(declaringName)}_{f.Name}";
                            initSb.AppendLine($"                {cacheFieldName}.SetValue(instance, null);");
                        }
                    }

                    foreach (var p in clearProps)
                    {
                        var setMethod = p.GetSetMethod(true);
                        if (setMethod.IsPublic)
                        {
                            initSb.AppendLine($"                instance.{p.Name} = null;");
                        }
                        else
                        {
                            string declaringName = GetCSharpTypeName(p.DeclaringType);
                            string cachePropName = $"s_p_{typeSafeName}_{GetSafeIdentifierName(declaringName)}_{p.Name}";
                            initSb.AppendLine($"                {cachePropName}.SetValue(instance, null);");
                        }
                    }

                    initSb.AppendLine("            });");
                }
            }

            // Generate PreserveMembers (Issue 4)
            preserveSb.AppendLine("        // Forces IL2CPP to preserve members that are injected");
            preserveSb.AppendLine("        public static void PreserveMembers()");
            preserveSb.AppendLine("        {");
            preserveSb.AppendLine("            #pragma warning disable 0162, 0169, 0414, 0219");
            preserveSb.AppendLine("            if (false)");
            preserveSb.AppendLine("            {");

            foreach (var type in injectTypes)
            {
                // E-C1: same naming fix as the injector pass above.
                string fullName = GetCSharpTypeName(type);
                if (fullName == null) continue;
                string typeSafeName = GetSafeIdentifierName(fullName);

                var fields = GetMemberSet(type, memberCache).Fields;
                foreach (var f in fields)
                {
                    if (HasInject(f))
                    {
                        // Members whose declaring or member type is not referenceable are skipped
                        // here too: the reflection path + link.xml preserve them.
                        if (f.IsPublic && IsEmittableType(f.DeclaringType) && IsEmittableType(f.FieldType))
                            preserveSb.AppendLine($"                var _f_{typeSafeName}_{f.Name} = default({fullName}).{f.Name};");
                        else if (IsEmittableType(f.DeclaringType))
                            preserveSb.AppendLine($"                var _f_{typeSafeName}_{f.Name} = typeof({GetCSharpTypeName(f.DeclaringType)}).GetField(\"{f.Name}\", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);");
                    }
                }

                var properties = GetMemberSet(type, memberCache).Properties;
                foreach (var p in properties)
                {
                    if (HasInject(p))
                    {
                        if (p.GetMethod != null && p.GetMethod.IsPublic && IsEmittableType(p.DeclaringType) && IsEmittableType(p.PropertyType))
                        {
                            preserveSb.AppendLine($"                var _p_{typeSafeName}_{p.Name} = default({fullName}).{p.Name};");
                            preserveSb.AppendLine($"                _ = _p_{typeSafeName}_{p.Name}; // Suppress CS0219 warning");
                        }
                        else if (IsEmittableType(p.DeclaringType))
                        {
                            preserveSb.AppendLine($"                var _p_{typeSafeName}_{p.Name} = typeof({GetCSharpTypeName(p.DeclaringType)}).GetProperty(\"{p.Name}\", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);");
                        }
                    }
                }
            }

            preserveSb.AppendLine("            }");
            preserveSb.AppendLine("            #pragma warning restore 0162, 0169, 0414, 0219");
            preserveSb.AppendLine("        }");

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
            sb.AppendLine();
            
            // Write PreserveMembers
            sb.Append(preserveSb.ToString());
            sb.AppendLine("    }");
            sb.AppendLine("}");

            // Load output settings (Issue 18)
            var settings = NexusEditorSettings.GetOrCreateSettings();
            string binderFolder = settings.BinderOutputPath;
            string linkXmlFolder = settings.LinkXmlOutputPath;

            // Delete old file if the path has changed
            string defaultFolder = "Assets/Scripts/Nexus";
            string defaultFile = Path.Combine(defaultFolder, "NexusGeneratedBinder.g.cs");
            if (binderFolder != defaultFolder && File.Exists(defaultFile))
            {
                File.Delete(defaultFile);
                if (File.Exists(defaultFile + ".meta"))
                    File.Delete(defaultFile + ".meta");
            }

            // Create target folders
            if (!Directory.Exists(binderFolder))
                Directory.CreateDirectory(binderFolder);
            if (!Directory.Exists(linkXmlFolder))
                Directory.CreateDirectory(linkXmlFolder);

            bool changed = false;

            // Preflight the preservation file before touching the generated binder.  A
            // curated/non-auto link.xml must fail atomically; otherwise a failed codegen
            // could leave a new binder paired with stale IL2CPP stripping rules.
            string destLinkXmlFile = Path.Combine(linkXmlFolder, "link.xml");
            if (File.Exists(destLinkXmlFile) && !File.ReadAllText(destLinkXmlFile).Contains("<auto-generated>"))
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

            // Write an empty binder even with no inject types, so stale generated injectors are cleared.
            if (injectTypes.Count == 0)
            {
                Debug.LogWarning("[Nexus] AOT Binder generation: no injectable types discovered. An empty binder was written to clear any previously generated injectors.");
            }

            if (File.Exists(destBinderFile))
            {
                string existingContent = File.ReadAllText(destBinderFile);
                if (!existingContent.Contains("<auto-generated>"))
                {
                    throw new InvalidOperationException($"[Nexus CodeGen] Overwrite blocked: the file at '{destBinderFile}' does not contain the <auto-generated> tag. It may have been manually modified by the user.");
                }
            }

            if (!File.Exists(destBinderFile) || File.ReadAllText(destBinderFile) != newBinderContent)
            {
                File.WriteAllText(destBinderFile, newBinderContent);
                changed = true;
            }
            EnsureGitIgnore(binderFolder, "NexusGeneratedBinder.g.cs");

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
            foreach (var type in networkSignalTypes)
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
                xmlSb.AppendLine($"  <assembly fullname=\"{kvp.Key}\">");
                foreach (var type in kvp.Value)
                {
                    xmlSb.AppendLine($"    <type fullname=\"{type.FullName}\" preserve=\"all\" />");
                }
                xmlSb.AppendLine("  </assembly>");
            }
            xmlSb.AppendLine("</linker>");

            string newLinkXmlContent = xmlSb.ToString();

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
                Debug.Log($"[Nexus] AOT Binder successfully generated at {destBinderFile}");
                Debug.Log($"[Nexus] AOT link.xml successfully generated at {destLinkXmlFile}");
            }
            else
            {
                Debug.Log("[Nexus] AOT Binder generation completed with no file changes.");
            }
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
