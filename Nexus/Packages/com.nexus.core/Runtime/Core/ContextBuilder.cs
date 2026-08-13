using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    [Preserve]
    public class ContextBuilder : IContextBuilder
    {
        private readonly NexusDI _container;
        private readonly SignalBus _signalBus;
        private readonly List<Type> _reactiveModelTypes = new();
        private readonly List<Type> _serviceTypes = new();

        /// <summary>
        /// When true (default), <see cref="Context.Configure"/> runs DI validation in ALL
        /// build targets — missing dependencies, constructor explosion, and captive
        /// dependencies are surfaced as logged issues at startup. Previously this ran only
        /// under <c>UNITY_EDITOR</c>, so production builds silently skipped every check.
        /// Set to false for projects that intentionally defer bindings past Configure.
        /// </summary>
        public static bool ValidateOnStartup { get; set; } = true;

        /// <summary>
        /// Maximum allowed constructor parameters before the DI validator
        /// flags a constructor-explosion issue (previously a hardcoded magic number 6).
        /// Configurable per-project — set to 0 to disable the check entirely.
        /// </summary>
        public static int MaxConstructorParameters { get; set; } = 6;

        public ContextBuilder(NexusDI container, SignalBus signalBus)
        {
            _container = container;
            _signalBus = signalBus;
        }

        public void BindModel<TInterface, TImplementation>() where TImplementation : class, TInterface
        {
            _container.Bind<TInterface, TImplementation>(isSingleton: true);
        }

        public void BindModel<TImplementation>() where TImplementation : class
        {
            _container.Bind<TImplementation>(isSingleton: true);
        }

        public void BindModelInstance<TInterface>(TInterface instance) where TInterface : class
        {
            _container.BindInstance(instance);
        }

        public void BindReactiveModel<TInterface, TImplementation>()
            where TImplementation : class, TInterface, IReactiveModel
        {
            _container.Bind<TInterface, TImplementation>(isSingleton: true);
            _reactiveModelTypes.Add(typeof(TInterface));
        }

        public void BindReactiveModel<TImplementation>()
            where TImplementation : class, IReactiveModel
        {
            _container.Bind<TImplementation>(isSingleton: true);
            _reactiveModelTypes.Add(typeof(TImplementation));
        }

        public void Bind<TInterface, TImplementation>() where TImplementation : class, TInterface
        {
            _container.Bind<TInterface, TImplementation>(isSingleton: true);
        }

        public void Bind<T>() where T : class
        {
            _container.Bind<T>(isSingleton: true);
        }

        public void BindInstance<T>(T instance) where T : class
        {
            _container.BindInstance(instance);
        }

        /// <summary>
        /// Starts a fluent binding chain (Zenject/VContainer-style):
        /// <c>builder.BindFluent&lt;IFoo&gt;().To&lt;Foo&gt;().AsScoped().AsImplementedInterfaces()</c>.
        /// </summary>
        public NexusDI.FluentTypeBinder<T> BindFluent<T>() where T : class
        {
            return _container.BindFluent<T>();
        }

        /// <summary>Binds with an explicit <see cref="Lifetime"/> (see <see cref="Lifetime"/> for semantics).</summary>
        public void Bind<TInterface, TImplementation>(Lifetime lifetime) where TImplementation : class, TInterface
        {
            _container.Bind<TInterface, TImplementation>(lifetime);
        }

        /// <summary>Binds a self-referencing type with an explicit <see cref="Lifetime"/>.</summary>
        public void Bind<T>(Lifetime lifetime) where T : class
        {
            _container.Bind<T>(lifetime);
        }

        /// <summary>Binds a named implementation (Strange-style named injection).</summary>
        public void Bind<TInterface, TImplementation>(string name) where TImplementation : class, TInterface
        {
            _container.Bind<TInterface, TImplementation>(name, isSingleton: true);
        }

        /// <summary>Binds a named self-referencing type.</summary>
        public void Bind<T>(string name) where T : class
        {
            _container.Bind<T>(name, isSingleton: true);
        }

        /// <summary>Binds a named implementation with an explicit <see cref="Lifetime"/>.</summary>
        public void Bind<TInterface, TImplementation>(string name, Lifetime lifetime) where TImplementation : class, TInterface
        {
            _container.Bind<TInterface, TImplementation>(name, lifetime);
        }

        /// <summary>Binds a named self-referencing type with an explicit <see cref="Lifetime"/>.</summary>
        public void Bind<T>(string name, Lifetime lifetime) where T : class
        {
            _container.Bind<T>(name, lifetime);
        }

        /// <summary>Binds a named instance value.</summary>
        public void BindInstance<T>(string name, T instance) where T : class
        {
            _container.BindInstance(name, instance);
        }

        /// <summary>
        /// Creates and registers a general-purpose <see cref="NexusBinder{TKey,TValue}"/> as a
        /// singleton so it can be injected anywhere (Strange-style generic binder):
        /// <code>[Inject] public IBinder&lt;UnitType, UnitDefinition&gt; Units { get; set; }</code>
        /// </summary>
        public void BindBinder<TKey, TValue>() where TKey : notnull
        {
            _container.BindInstance<IBinder<TKey, TValue>>(new NexusBinder<TKey, TValue>(_container));
        }

        /// <summary>Polymorphic binding: one concrete class under multiple interfaces, shared singleton.</summary>
        public void BindMultiple<TInterface1, TInterface2, TImplementation>()
            where TImplementation : class, TInterface1, TInterface2
        {
            _container.BindMultiple<TInterface1, TInterface2, TImplementation>(isSingleton: true);
        }

        /// <summary>Three-interface polymorphic binding (see the two-interface overload).</summary>
        public void BindMultiple<TInterface1, TInterface2, TInterface3, TImplementation>()
            where TImplementation : class, TInterface1, TInterface2, TInterface3
        {
            _container.BindMultiple<TInterface1, TInterface2, TInterface3, TImplementation>(isSingleton: true);
        }

        /// <summary>
        /// Automatically binds a concrete implementation class under all of its implemented interfaces
        /// (excluding system/framework interfaces) AND under its own concrete type as a shared singleton.
        /// </summary>
        public void BindInterfacesAndSelfTo<TImplementation>(bool isSingleton = true) where TImplementation : class
        {
            BindInterfacesAndSelfTo(typeof(TImplementation), isSingleton);
        }

        /// <summary>Interfaces-and-self binding with an explicit <see cref="Lifetime"/>.</summary>
        public void BindInterfacesAndSelfTo<TImplementation>(Lifetime lifetime) where TImplementation : class
        {
            BindInterfacesAndSelfTo(typeof(TImplementation), lifetime);
        }

        /// <summary>
        /// Scans an assembly and automatically binds matching concrete types using the specified predicate.
        /// </summary>
        public void BindAllClassesMatching(System.Reflection.Assembly assembly, Func<Type, bool> predicate, bool isSingleton = true)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            var types = Services.AssemblyScanService.GetCachedTypes(assembly);
            for (int i = 0; i < types.Length; i++)
            {
                var t = types[i];
                if (t.IsClass && !t.IsAbstract && predicate(t))
                {
                    BindInterfacesAndSelfTo(t, isSingleton);
                }
            }
        }

        /// <summary>Assembly-scan binding with an explicit <see cref="Lifetime"/>.</summary>
        public void BindAllClassesMatching(System.Reflection.Assembly assembly, Func<Type, bool> predicate, Lifetime lifetime)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            var types = Services.AssemblyScanService.GetCachedTypes(assembly);
            for (int i = 0; i < types.Length; i++)
            {
                var t = types[i];
                if (t.IsClass && !t.IsAbstract && predicate(t))
                {
                    BindInterfacesAndSelfTo(t, lifetime);
                }
            }
        }

        private void BindInterfacesAndSelfTo(Type implType, bool isSingleton)
        {
            var interfaces = GetUserDefinedInterfaces(implType);
            _container.BindMultiple(interfaces, implType, isSingleton);
        }

        private void BindInterfacesAndSelfTo(Type implType, Lifetime lifetime)
        {
            var interfaces = GetUserDefinedInterfaces(implType);
            _container.BindMultiple(interfaces, implType, lifetime);
        }

        // User-defined-interface filtering lives in NexusDI.GetUserDefinedInterfaces (one cache,
        // one predicate) so the fluent AsImplementedInterfaces chain and the builder's
        // BindInterfacesAndSelfTo surfaces can never disagree about which interfaces to bind.
        private static Type[] GetUserDefinedInterfaces(Type type) => NexusDI.GetUserDefinedInterfaces(type);

        public void EnableStrictInjection()
        {
            _container.StrictInjection = true;
        }

        /// <summary>
        /// Clears the shared interface-metadata cache. Called by <see cref="NexusRuntime.Reset"/>
        /// so a recompile with Disable Domain Reload can never leave stale Type references.
        /// </summary>
        internal static void ClearCaches()
        {
            NexusDI.ClearCaches();
        }

        // ─── Cross-Boundary Binding ───

        public void BindCrossBoundary<TInterface, TImplementation>()
            where TImplementation : class, TInterface
        {
            _container.BindCrossBoundary<TInterface, TImplementation>();
        }

        public void BindCrossBoundary<T>() where T : class
        {
            _container.BindCrossBoundary<T>();
        }

        public void BindService<TInterface, TImplementation>()
            where TImplementation : class, TInterface, INexusService
        {
            _container.Bind<TInterface, TImplementation>(isSingleton: true);
            _serviceTypes.Add(typeof(TInterface));
        }

        public void BindService<TImplementation>()
            where TImplementation : class, INexusService
        {
            _container.Bind<TImplementation>(isSingleton: true);
            _serviceTypes.Add(typeof(TImplementation));
        }

        /// <summary>
        /// Binds an <see cref="INexusService"/> under ALL of its user-defined interfaces AND its
        /// concrete type as ONE shared singleton, and registers it for eager initialization during
        /// <c>InitializeServicesAsync</c>. This is the combination <see cref="BindService{TInterface,TImplementation}"/>
        /// (eager but interface-key only) and <see cref="BindInterfacesAndSelfTo{TImplementation}"/>
        /// (shared keys but lazy) each miss: services whose <c>InitializeAsync</c> must run at startup
        /// (TickService's driver, AudioService's sources, UIManager's canvas, SaveThrottler's
        /// tick registration) AND that are consumed both by interface and by concrete type.
        /// </summary>
        public void BindServiceInterfacesAndSelfTo<TImplementation>()
            where TImplementation : class, INexusService
        {
            BindInterfacesAndSelfTo(typeof(TImplementation), isSingleton: true);
            _serviceTypes.Add(typeof(TImplementation));
        }

        public void BindLazyService<TInterface, TImplementation>()
            where TImplementation : class, TInterface, INexusService
        {
            _container.Bind<TInterface, TImplementation>(isSingleton: true);
            _container.MarkLazyService(typeof(TInterface));
            _container.MarkLazyService(typeof(TImplementation));
            // Intentionally NOT adding to _serviceTypes — prevents eager construction
            // during InitializeServicesAsync(). Construction happens on first Resolve().
        }

        public void BindLazyService<TImplementation>()
            where TImplementation : class, INexusService
        {
            _container.Bind<TImplementation>(isSingleton: true);
            _container.MarkLazyService(typeof(TImplementation));
            // Intentionally NOT adding to _serviceTypes
        }

        /// <summary>
        /// Registers a synchronous command to handle the specified signal type.
        /// The command is bound as non-singleton (one instance per execution).
        /// </summary>
        /// <typeparam name="TSignal">The signal struct type that triggers the command.</typeparam>
        /// <typeparam name="TCommand">The command class (must implement <see cref="ICommand"/>).</typeparam>
        /// <param name="mode">Execution mode (Sequential, Concurrent, or Exclusive). Composite triggers must be registered via [CompositeSignalHandler] instead.</param>
        /// <param name="priority">Execution priority; <b>higher values run first</b>.</param>
        public void BindCommand<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) 
            where TCommand : class where TSignal : struct
        {
            // Composite registration has its own path (CompositeSignalHandler);
            // passing it here would silently register a normal sequential-like handler.
            if (mode == ExecutionMode.Composite)
            {
                throw new ArgumentException($"ExecutionMode.Composite cannot be used with BindCommand. Use the [CompositeSignalHandler] attribute (or SignalBus.RegisterCompositeCommand) to register composite triggers.", nameof(mode));
            }

            // Validate that the command implements either ICommand or ICommand<TSignal>
            bool isGeneric = typeof(ICommand<TSignal>).IsAssignableFrom(typeof(TCommand));
            bool isNormal = typeof(ICommand).IsAssignableFrom(typeof(TCommand));
            if (!isGeneric && !isNormal)
            {
                throw new ArgumentException($"Command type {typeof(TCommand).Name} must implement either ICommand or ICommand<{typeof(TSignal).Name}>");
            }

            _container.Bind<TCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(TSignal), typeof(TCommand), mode, priority, isAsync: false);
        }

        /// <summary>
        /// Registers an asynchronous command to handle the specified signal type.
        /// The command is bound as non-singleton (one instance per execution).
        /// </summary>
        /// <typeparam name="TSignal">The signal struct type that triggers the command.</typeparam>
        /// <typeparam name="TCommand">The command class.</typeparam>
        /// <param name="mode">Execution mode (Sequential, Concurrent, or Exclusive). Composite triggers must be registered via [CompositeSignalHandler] instead.</param>
        /// <param name="priority">Execution priority; <b>higher values run first</b>.</param>
        public void BindAsyncCommand<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) 
            where TCommand : class where TSignal : struct
        {
            // Composite registration has its own path (CompositeSignalHandler).
            if (mode == ExecutionMode.Composite)
            {
                throw new ArgumentException($"ExecutionMode.Composite cannot be used with BindAsyncCommand. Use the [CompositeSignalHandler] attribute (or SignalBus.RegisterCompositeCommand) to register composite triggers.", nameof(mode));
            }

            // Validate that the command implements either IAsyncCommand or IAsyncCommand<TSignal>
            bool isGeneric = typeof(IAsyncCommand<TSignal>).IsAssignableFrom(typeof(TCommand));
            bool isNormal = typeof(IAsyncCommand).IsAssignableFrom(typeof(TCommand));
            if (!isGeneric && !isNormal)
            {
                throw new ArgumentException($"Command type {typeof(TCommand).Name} must implement either IAsyncCommand or IAsyncCommand<{typeof(TSignal).Name}>");
            }

            _container.Bind<TCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(TSignal), typeof(TCommand), mode, priority, isAsync: true);
        }

        /// <summary>Registers a one-shot command (Strange-style <c>.Once()</c>): fires once then unregisters.</summary>
        public void BindCommandOnce<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0)
            where TCommand : class where TSignal : struct
        {
            if (mode == ExecutionMode.Composite)
                throw new ArgumentException($"ExecutionMode.Composite cannot be used with BindCommandOnce. Use the [CompositeSignalHandler] attribute instead.", nameof(mode));

            bool isGeneric = typeof(ICommand<TSignal>).IsAssignableFrom(typeof(TCommand));
            bool isNormal = typeof(ICommand).IsAssignableFrom(typeof(TCommand));
            if (!isGeneric && !isNormal)
                throw new ArgumentException($"Command type {typeof(TCommand).Name} must implement either ICommand or ICommand<{typeof(TSignal).Name}>");

            _container.Bind<TCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(TSignal), typeof(TCommand), mode, priority, isAsync: false, oneShot: true);
        }

        /// <summary>Registers a one-shot async command: fires once then unregisters.</summary>
        public void BindAsyncCommandOnce<TSignal, TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0)
            where TCommand : class where TSignal : struct
        {
            if (mode == ExecutionMode.Composite)
                throw new ArgumentException($"ExecutionMode.Composite cannot be used with BindAsyncCommandOnce. Use the [CompositeSignalHandler] attribute instead.", nameof(mode));

            bool isGeneric = typeof(IAsyncCommand<TSignal>).IsAssignableFrom(typeof(TCommand));
            bool isNormal = typeof(IAsyncCommand).IsAssignableFrom(typeof(TCommand));
            if (!isGeneric && !isNormal)
                throw new ArgumentException($"Command type {typeof(TCommand).Name} must implement either IAsyncCommand or IAsyncCommand<{typeof(TSignal).Name}>");

            _container.Bind<TCommand>(isSingleton: false);
            _signalBus.RegisterCommand(typeof(TSignal), typeof(TCommand), mode, priority, isAsync: true, oneShot: true);
        }

        public ICommandBindingBuilder<TSignal> BindSignal<TSignal>() where TSignal : struct
        {
            return new CommandBindingBuilder<TSignal>(this);
        }

        /// <summary>Registers a ref-based signal filter by type (see <see cref="SignalBus.AddSignalFilter{TSignal,TFilter}"/>).</summary>
        public void AddSignalFilter<TSignal, TFilter>()
            where TSignal : struct
            where TFilter : class, ISignalFilter<TSignal>
        {
            _signalBus.AddSignalFilter<TSignal, TFilter>();
        }

        [Obsolete("Fire from a lifecycle's OnInitializeAsync/OnStartAsync via ISignalPublisher instead; a builder should only register bindings.", error: false)]
        public void Fire<T>(T signal) where T : struct
        {
            _signalBus.Fire(signal);
        }

        /// <summary>
        /// Validates that all registered types' [Inject] dependencies have matching bindings.
        /// Returns a list of validation issues (empty = all dependencies are satisfiable).
        /// </summary>
        public List<DiValidationIssue> Validate()
        {
            var issues = new List<DiValidationIssue>();

            // What can actually be resolved = binding keys (interfaces).
            var allRegisteredTypes = _container.GetAllRegisteredTypes();

            // Validate concrete implementations too: Bind<TInterface, TImplementation> keys the
            // binding by interface, so the concrete type's [Inject]/ctor dependencies were
            // previously never checked. Resolve-time lookups still use the key set only.
            var typesToValidate = new HashSet<Type>(allRegisteredTypes);
            foreach (var (_, concrete) in _container.GetEditorTypeMappings())
            {
                if (concrete != null) typesToValidate.Add(concrete);
            }

            bool IsDependencyRegistered(Type dependencyType, string bindingName)
                => string.IsNullOrEmpty(bindingName)
                    ? allRegisteredTypes.Contains(dependencyType)
                    : _container.IsRegistered(dependencyType, bindingName);

            static string BindingSuffix(string bindingName)
                => string.IsNullOrEmpty(bindingName) ? string.Empty : $" named '{bindingName}'";

            foreach (var type in typesToValidate)
            {
                NexusDI.InjectableMetadata meta;
                try { meta = NexusDI.GetOrCreateInjectMetadata(type); }
                catch (Exception ex)
                {
                    NexusRuntime.Logger?.LogWarning($"[Nexus] DI validation skipped type '{type.Name}': {ex.Message}");
                    continue;
                }

                // Check constructor parameters
                if (meta.ConstructorParameterTypes != null)
                {
                    if (MaxConstructorParameters > 0 && meta.ConstructorParameterTypes.Length > MaxConstructorParameters)
                    {
                        issues.Add(new DiValidationIssue(
                            type, type,
                            DiValidationIssueType.MissingConstructorDependency,
                            $"[ConstructorExplosion] Constructor of '{type.Name}' has {meta.ConstructorParameterTypes.Length} parameters (> {MaxConstructorParameters} limit), indicating high coupling risk."
                        ));
                    }

                    for (int paramIndex = 0; paramIndex < meta.ConstructorParameterTypes.Length; paramIndex++)
                    {
                        var paramType = meta.ConstructorParameterTypes[paramIndex];
                        var bindingName = meta.ConstructorParameterNames != null && paramIndex < meta.ConstructorParameterNames.Length
                            ? meta.ConstructorParameterNames[paramIndex]
                            : null;
                        // C# optional parameters have a declared default — they are satisfied
                        // without a registration (e.g. CommandPoolManager(NexusDI, int = 4)).
                        if (meta.ConstructorParameterHasDefault != null
                            && paramIndex < meta.ConstructorParameterHasDefault.Length
                            && meta.ConstructorParameterHasDefault[paramIndex])
                            continue;
                        if (!IsDependencyRegistered(paramType, bindingName))
                        {
                            issues.Add(new DiValidationIssue(
                                type, paramType,
                                DiValidationIssueType.MissingConstructorDependency,
                                $"Constructor of '{type.Name}' requires '{paramType.Name}'{BindingSuffix(bindingName)} which is not registered."
                            ));
                        }
                    }
                }

                // Check [Inject] fields
                foreach (var field in meta.Fields)
                {
                    // LazyInjection<T> fields are constructed by the injector directly
                    // (never resolved), so they are always satisfiable — skip them.
                    if (field.Type.IsGenericType && field.Type.GetGenericTypeDefinition() == typeof(LazyInjection<>))
                        continue;
                    if (!field.IsOptional && !IsDependencyRegistered(field.Type, field.Name))
                    {
                        issues.Add(new DiValidationIssue(
                            type, field.Type,
                            DiValidationIssueType.MissingFieldDependency,
                            $"[Inject] field '{type.Name}.{field.Field.Name}' requires '{field.Type.Name}'{BindingSuffix(field.Name)} which is not registered."
                        ));
                    }
                }

                // Check [Inject] properties
                foreach (var prop in meta.Properties)
                {
                    if (prop.Type.IsGenericType && prop.Type.GetGenericTypeDefinition() == typeof(LazyInjection<>))
                        continue;
                    if (!prop.IsOptional && !IsDependencyRegistered(prop.Type, prop.Name))
                    {
                        issues.Add(new DiValidationIssue(
                            type, prop.Type,
                            DiValidationIssueType.MissingPropertyDependency,
                            $"[Inject] property '{type.Name}.{prop.Property.Name}' requires '{prop.Type.Name}'{BindingSuffix(prop.Name)} which is not registered."
                        ));
                    }
                }

                // Check [Inject] method parameters
                foreach (var method in meta.Methods)
                {
                    for (int i = 0; i < method.ParameterTypes.Length; i++)
                    {
                        var bindingName = method.ParameterNames != null && i < method.ParameterNames.Length
                            ? method.ParameterNames[i]
                            : null;
                        if (!method.OptionalParameterMask[i] && !IsDependencyRegistered(method.ParameterTypes[i], bindingName))
                        {
                            var paramName = method.Method.GetParameters()[i].Name;
                            issues.Add(new DiValidationIssue(
                                type, method.ParameterTypes[i],
                                DiValidationIssueType.MissingMethodDependency,
                                $"[Inject] method '{type.Name}.{method.Method.Name}' parameter '{paramName}' requires '{method.ParameterTypes[i].Name}'{BindingSuffix(bindingName)} which is not registered."
                            ));
                        }
                    }
                }
            }

            // Captive-dependency prevention — a singleton service must not capture a
            // transient (non-singleton, non-factory) dependency in its constructor or
            // [Inject] members, or the transient silently becomes a long-lived shared
            // instance with an unclear lifetime.
            // Polymorphic bindings (BindMultiple / BindInterfacesAndSelfTo) share ONE
            // Binding across several interface keys, so dedupe by concrete type to avoid
            // reporting the same captive dependency once per interface.
            var captiveReportedConcretes = new HashSet<Type>();
            foreach (var (interfaceKey, concrete) in _container.GetEditorTypeMappings())
            {
                if (concrete == null || concrete.IsInterface || concrete.IsAbstract) continue;
                if (!_container.IsSingletonBinding(interfaceKey)) continue;
                if (!captiveReportedConcretes.Add(concrete)) continue;

                NexusDI.InjectableMetadata captiveMeta;
                try { captiveMeta = NexusDI.GetOrCreateInjectMetadata(concrete); }
                catch (Exception ex)
                {
                    NexusRuntime.Logger?.LogWarning($"[Nexus] DI validation skipped type '{concrete.Name}': {ex.Message}");
                    continue;
                }

                void ReportCaptive(Type depType, string memberDesc)
                {
                    if (depType == null) return;
                    if (depType.IsGenericType && depType.GetGenericTypeDefinition() == typeof(LazyInjection<>)) return;
                    if (!allRegisteredTypes.Contains(depType)) return; // missing deps are already reported above
                    if (_container.IsSingletonBinding(depType) || _container.IsFactoryBinding(depType)) return;
                    issues.Add(new DiValidationIssue(
                        concrete, depType,
                        DiValidationIssueType.CaptiveDependency,
                        $"[CaptiveDependency] Singleton '{concrete.Name}' ({interfaceKey.Name}) captures transient dependency '{depType.Name}' via {memberDesc}. Bind it as a singleton or through a factory."
                    ));
                }

                if (captiveMeta.ConstructorParameterTypes != null)
                {
                    for (int i = 0; i < captiveMeta.ConstructorParameterTypes.Length; i++)
                        ReportCaptive(captiveMeta.ConstructorParameterTypes[i], $"constructor parameter {i}");
                }
                foreach (var f in captiveMeta.Fields) ReportCaptive(f.Type, $"[Inject] field '{f.Field.Name}'");
                foreach (var p in captiveMeta.Properties) ReportCaptive(p.Type, $"[Inject] property '{p.Property.Name}'");
                foreach (var m in captiveMeta.Methods)
                {
                    for (int i = 0; i < m.ParameterTypes.Length; i++)
                        ReportCaptive(m.ParameterTypes[i], $"[Inject] method '{m.Method.Name}' parameter {i}");
                }
            }

            return issues;
        }

        internal IReadOnlyList<Type> ReactiveModelTypes => _reactiveModelTypes;
        internal IReadOnlyList<Type> ServiceTypes => _serviceTypes;

        internal async ValueTask InitializeReactiveModelsAsync(CancellationToken ct)
        {
            foreach (var modelType in _reactiveModelTypes)
            {
                if (ct.IsCancellationRequested) break;

                var model = _container.Resolve(modelType) as IReactiveModel;
                if (model != null)
                {
                    await model.OnBind(ct);
                }
            }
        }

        internal async ValueTask InitializeServicesAsync(CancellationToken ct)
        {
            foreach (var serviceType in _serviceTypes)
            {
                if (ct.IsCancellationRequested) break;

                var service = _container.Resolve(serviceType) as INexusService;
                if (service != null)
                {
                    await service.InitializeAsync(ct);
                }
            }
        }
    }

    [Preserve]
    internal class CommandBindingBuilder<TSignal> : ICommandBindingBuilder<TSignal> where TSignal : struct
    {
        private readonly ContextBuilder _builder;

        public CommandBindingBuilder(ContextBuilder builder)
        {
            _builder = builder;
        }

        private bool _oneShot;

        public ICommandBindingBuilder<TSignal> Once()
        {
            _oneShot = true;
            return this;
        }

        public ICommandBindingBuilder<TSignal> To<TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) where TCommand : class
        {
            if (_oneShot) { _oneShot = false; _builder.BindCommandOnce<TSignal, TCommand>(mode, priority); }
            else { _builder.BindCommand<TSignal, TCommand>(mode, priority); }
            return this;
        }

        public ICommandBindingBuilder<TSignal> ToAsync<TCommand>(ExecutionMode mode = ExecutionMode.Sequential, int priority = 0) where TCommand : class
        {
            if (_oneShot) { _oneShot = false; _builder.BindAsyncCommandOnce<TSignal, TCommand>(mode, priority); }
            else { _builder.BindAsyncCommand<TSignal, TCommand>(mode, priority); }
            return this;
        }
    }
}
