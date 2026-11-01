using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nexus.Core;

namespace Nexus.Editor
{
    /// <summary>
    /// The Architecture tab's model (#193): the game's signals and the commands that handle them, and its services,
    /// models, views and mediators with what Nexus injects into them. Built once by reflection; holds no UI.
    /// A signal is the <c>TSignal</c> of an <see cref="ICommand{TSignal}"/> or <see cref="IAsyncCommand{TSignal}"/>
    /// implementation, a signal of an <see cref="ICompositeCommand{TFirst, TSecond}"/> implementation, or a type a
    /// <see cref="SignalHandlerAttribute"/>, <see cref="CompositeSignalHandlerAttribute"/>, <see cref="RegisterCommandAttribute"/>
    /// or <see cref="RegisterCompositeCommandAttribute"/> names; composites are commands as well (#253). A signal that only has <c>Subscribe</c> listeners, or a command bound in code to a signal it does not name,
    /// is not visible before a context runs; <see cref="Bound"/> reads what the running contexts registered.
    /// </summary>
    internal sealed class ArchitectureMap
    {
        internal enum Kind { Signal, Command, Service, Model, View, Mediator }

        /// <summary>Where an edge between a signal and a command comes from.</summary>
        internal enum Source
        {
            /// <summary>The command implements <c>ICommand&lt;TSignal&gt;</c> or <c>ICompositeCommand&lt;TFirst, TSecond&gt;</c>
            /// (mode <see cref="ExecutionMode.Composite"/>): it can handle the signal, and the game binds it in code.</summary>
            Implements,
            /// <summary>A <see cref="SignalHandlerAttribute"/> binds it, with its mode and priority.</summary>
            Attribute,
            /// <summary>A <see cref="CompositeSignalHandlerAttribute"/>: the command runs once every listed signal has arrived.</summary>
            Composite,
            /// <summary>A <see cref="RegisterCommandAttribute"/> binds it, with its mode and priority.</summary>
            Registered,
            /// <summary>A <see cref="RegisterCompositeCommandAttribute"/>: the command runs once every listed signal has arrived.</summary>
            RegisteredComposite,
        }

        internal readonly struct Handler
        {
            internal Handler(Type signal, Type command, Source source, ExecutionMode mode, int priority, bool oneShot, bool isAsync)
            {
                Signal = signal;
                Command = command;
                Source = source;
                Mode = mode;
                Priority = priority;
                OneShot = oneShot;
                IsAsync = isAsync;
            }

            internal Type Signal { get; }
            internal Type Command { get; }
            internal Source Source { get; }
            internal ExecutionMode Mode { get; }
            internal int Priority { get; }
            internal bool OneShot { get; }
            internal bool IsAsync { get; }
        }

        /// <summary>One value Nexus injects: <see cref="Owner"/> receives a <see cref="Target"/> through <see cref="Member"/>.</summary>
        internal readonly struct Dependency
        {
            internal Dependency(Type owner, string member, Type target, bool optional)
            {
                Owner = owner;
                Member = member;
                Target = target;
                Optional = optional;
            }

            internal Type Owner { get; }
            /// <summary>"field _economy", "property Economy", "constructor economy" or "method Construct(economy)".</summary>
            internal string Member { get; }
            internal Type Target { get; }
            internal bool Optional { get; }
        }

        internal sealed class Node
        {
            internal Node(Type type, Kind kind)
            {
                Type = type;
                Kind = kind;
            }

            internal Type Type { get; }
            internal Kind Kind { get; }
            internal string Assembly => AssemblyCatalog.GetSimpleName(Type.Assembly);
            /// <summary>A signal: the commands that handle it. A command: the signals it handles.</summary>
            internal List<Handler> Handlers { get; } = new();
            internal List<Dependency> Dependencies { get; } = new();
            /// <summary>The members that receive this type, or an interface or base class of it.</summary>
            internal List<Dependency> Dependents { get; } = new();
            /// <summary>A service: the interfaces it implements. A view: its mediators. A mediator: its view.</summary>
            internal List<Type> Related { get; } = new();
        }

        private static ArchitectureMap s_current;

        private readonly Dictionary<Type, Node> _byType = new();
        private readonly List<Node> _nodes = new();
        private readonly List<string> _assemblies = new();

        /// <summary>The map of the game's assemblies (every assembly that references Nexus, without the Nexus packages and
        /// tests), built on first use after each script reload.</summary>
        internal static ArchitectureMap Current => s_current ??= Build(GameAssemblies());

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded() => s_current = null;

        /// <summary>Drops the map; the next <see cref="Current"/> scans again.</summary>
        internal static void Invalidate() => s_current = null;

        /// <summary>Every node, by kind in <see cref="Kind"/> order, then by name.</summary>
        internal IReadOnlyList<Node> Nodes => _nodes;

        /// <summary>The assemblies that declare a node, sorted.</summary>
        internal IReadOnlyList<string> Assemblies => _assemblies;

        internal bool TryGet(Type type, out Node node) => _byType.TryGetValue(type, out node);

        internal int Count(Kind kind)
        {
            int count = 0;
            foreach (var node in _nodes)
                if (node.Kind == kind) count++;
            return count;
        }

        private static IEnumerable<Assembly> GameAssemblies()
        {
            foreach (var assembly in AssemblyCatalog.NexusReferencingAssemblies())
                if (!AssemblyCatalog.IsNexusPackageAssembly(AssemblyCatalog.GetSimpleName(assembly)))
                    yield return assembly;
        }

        internal static ArchitectureMap Build(IEnumerable<Assembly> assemblies)
        {
            var map = new ArchitectureMap();
            var classes = new List<Type>();
            foreach (var assembly in assemblies)
            {
                foreach (var type in AssemblyCatalog.GetTypesSafe(assembly))
                {
                    if (type == null || !type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition) continue;
                    if (type.IsDefined(typeof(CompilerGeneratedAttribute), false)) continue;
                    var kind = Classify(type);
                    if (kind == null) continue;
                    map.Add(type, kind.Value);
                    classes.Add(type);
                }
            }

            foreach (var type in classes)
            {
                var node = map._byType[type];
                switch (node.Kind)
                {
                    case Kind.Command: map.AddHandlers(type); break;
                    case Kind.Service: AddServiceInterfaces(node); break;
                    case Kind.View: map.AddViewMediator(node); break;
                    case Kind.Mediator: map.AddMediatorView(node); break;
                }
                AddDependencies(node);
            }

            map.IndexDependents();
            map.Finish();
            return map;
        }

        /// <summary>The handlers the running contexts registered for <paramref name="signal"/>, with each context's scope tag.</summary>
        internal static List<(string Scope, CommandHandlerInfo Info)> Bound(Type signal, IReadOnlyList<IContext> contexts)
        {
            var bound = new List<(string, CommandHandlerInfo)>();
            if (contexts == null) return bound;
            foreach (var context in contexts)
            {
                var handlers = context?.SignalBus?.RegisteredHandlers;
                if (handlers == null || !handlers.TryGetValue(signal, out var infos)) continue;
                foreach (var info in infos)
                    bound.Add((context.ScopeTag ?? string.Empty, info));
            }
            return bound;
        }

        /// <summary>The signal types the running contexts bound that this map does not list (a command bound in code to a
        /// signal it does not name), sorted by name.</summary>
        internal List<Type> SignalsBoundOnlyAtRuntime(IReadOnlyList<IContext> contexts)
        {
            var seen = new HashSet<Type>();
            var result = new List<Type>();
            if (contexts == null) return result;
            foreach (var context in contexts)
            {
                var handlers = context?.SignalBus?.RegisteredHandlers;
                if (handlers == null) continue;
                foreach (var signal in handlers.Keys)
                    if (!_byType.ContainsKey(signal) && seen.Add(signal)) result.Add(signal);
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return result;
        }

        // One kind per class; a command that is also a service shows as a command.
        private static Kind? Classify(Type type)
        {
            if (typeof(ICommand).IsAssignableFrom(type) || typeof(IAsyncCommand).IsAssignableFrom(type)
                || typeof(ICompositeCommand).IsAssignableFrom(type) || typeof(IAsyncCompositeCommand).IsAssignableFrom(type)
                || HandlesGenerically(type))
                return Kind.Command;
            if (MediatedView(type) != null) return Kind.Mediator;
            if (typeof(View).IsAssignableFrom(type)) return Kind.View;
            if (typeof(IReactiveModel).IsAssignableFrom(type)) return Kind.Model;
            if (typeof(INexusService).IsAssignableFrom(type)) return Kind.Service;
            return null;
        }

        private static bool HandlesGenerically(Type type)
        {
            foreach (var iface in type.GetInterfaces())
            {
                if (!iface.IsGenericType) continue;
                var definition = iface.GetGenericTypeDefinition();
                if (definition == typeof(ICommand<>) || definition == typeof(IAsyncCommand<>) || definition == typeof(ICompositeCommand<,>)) return true;
            }
            return false;
        }

        private static bool IsAsyncCommand(Type type)
        {
            if (typeof(IAsyncCommand).IsAssignableFrom(type) || typeof(IAsyncCompositeCommand).IsAssignableFrom(type)) return true;
            foreach (var iface in type.GetInterfaces())
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IAsyncCommand<>)) return true;
            return false;
        }

        // The TView of the first Mediator<TView> base class, or null.
        private static Type MediatedView(Type type)
        {
            for (var current = type.BaseType; current != null && current != typeof(object); current = current.BaseType)
                if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Mediator<>))
                    return current.GetGenericArguments()[0];
            return null;
        }

        private Node Add(Type type, Kind kind)
        {
            var node = new Node(type, kind);
            _byType[type] = node;
            _nodes.Add(node);
            return node;
        }

        private void AddHandlers(Type command)
        {
            bool isAsync = IsAsyncCommand(command);
            var declared = new HashSet<Type>();

            foreach (var attribute in command.GetCustomAttributes<SignalHandlerAttribute>(false))
            {
                if (attribute.SignalType == null) continue;
                declared.Add(attribute.SignalType);
                AddHandler(new Handler(attribute.SignalType, command, Source.Attribute, attribute.Mode, attribute.Priority,
                    attribute.OneShot, attribute.IsAsync ?? isAsync));
            }

            // The runtime's scan reads [RegisterCommand] as a [SignalHandler] with its flags, and a [RegisterCompositeCommand]
            // in place of a [CompositeSignalHandler] (Context.ScanAssembliesAndRegister).
            foreach (var attribute in command.GetCustomAttributes<RegisterCommandAttribute>(false))
            {
                if (attribute.SignalType == null) continue;
                declared.Add(attribute.SignalType);
                AddHandler(new Handler(attribute.SignalType, command, Source.Registered, attribute.Mode, attribute.Priority,
                    attribute.OneShot, attribute.IsAsync || isAsync));
            }

            var registeredComposite = command.GetCustomAttribute<RegisterCompositeCommandAttribute>(false);
            var composite = command.GetCustomAttribute<CompositeSignalHandlerAttribute>(false);
            if (registeredComposite?.SignalTypes != null)
            {
                foreach (var signal in registeredComposite.SignalTypes)
                {
                    if (signal == null) continue;
                    declared.Add(signal);
                    AddHandler(new Handler(signal, command, Source.RegisteredComposite, ExecutionMode.Composite, registeredComposite.Priority,
                        registeredComposite.OneShot, registeredComposite.IsAsync || isAsync));
                }
            }
            else if (composite?.SignalTypes != null)
            {
                foreach (var signal in composite.SignalTypes)
                {
                    if (signal == null) continue;
                    declared.Add(signal);
                    AddHandler(new Handler(signal, command, Source.Composite, ExecutionMode.Composite, composite.Priority,
                        composite.OneShot, composite.IsAsync ?? isAsync));
                }
            }

            // An attribute already names the pair with its mode; the interface adds nothing to it.
            foreach (var iface in command.GetInterfaces())
            {
                if (!iface.IsGenericType) continue;
                var definition = iface.GetGenericTypeDefinition();
                if (definition == typeof(ICompositeCommand<,>))
                {
                    foreach (var part in iface.GetGenericArguments())
                        if (declared.Add(part))
                            AddHandler(new Handler(part, command, Source.Implements, ExecutionMode.Composite, 0, false, false));
                    continue;
                }
                if (definition != typeof(ICommand<>) && definition != typeof(IAsyncCommand<>)) continue;
                var signal = iface.GetGenericArguments()[0];
                if (declared.Add(signal))
                    AddHandler(new Handler(signal, command, Source.Implements, ExecutionMode.Sequential, 0, false,
                        definition == typeof(IAsyncCommand<>)));
            }
        }

        private void AddHandler(Handler handler)
        {
            if (!_byType.TryGetValue(handler.Signal, out var signal)) signal = Add(handler.Signal, Kind.Signal);
            signal.Handlers.Add(handler);
            _byType[handler.Command].Handlers.Add(handler);
        }

        private static void AddServiceInterfaces(Node service)
        {
            foreach (var iface in service.Type.GetInterfaces())
            {
                if (iface == typeof(INexusService) || iface.IsGenericType) continue;
                if (AssemblyCatalog.IsFrameworkAssembly(AssemblyCatalog.GetSimpleName(iface.Assembly))) continue;
                service.Related.Add(iface);
            }
        }

        private void AddViewMediator(Node view)
        {
            var attribute = view.Type.GetCustomAttribute<MediatorAttribute>(true);
            if (attribute?.MediatorType != null && !view.Related.Contains(attribute.MediatorType))
                view.Related.Add(attribute.MediatorType);
        }

        private void AddMediatorView(Node mediator)
        {
            var viewType = MediatedView(mediator.Type);
            mediator.Related.Add(viewType);
            if (_byType.TryGetValue(viewType, out var view) && !view.Related.Contains(mediator.Type))
                view.Related.Add(mediator.Type);
        }

        private static void AddDependencies(Node node)
        {
            var type = node.Type;
            var members = InjectScanner.ScanMembers(type);
            foreach (var field in members.Fields)
                node.Dependencies.Add(new Dependency(type, "field " + field.Name, field.FieldType, field.IsDefined(typeof(OptionalInjectAttribute), false)));
            foreach (var property in members.Properties)
                node.Dependencies.Add(new Dependency(type, "property " + property.Name, property.PropertyType, property.IsDefined(typeof(OptionalInjectAttribute), false)));
            foreach (var method in members.Methods)
                foreach (var parameter in method.GetParameters())
                    node.Dependencies.Add(new Dependency(type, $"method {method.Name}({parameter.Name})", parameter.ParameterType, parameter.IsOptional));

            // Unity creates components and assets itself; their constructors are never Nexus's to call.
            if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return;
            var constructor = InjectScanner.FindInjectionConstructor(type, out _);
            if (constructor == null) return;
            foreach (var parameter in constructor.GetParameters())
                node.Dependencies.Add(new Dependency(type, "constructor " + parameter.Name, parameter.ParameterType, parameter.IsOptional));
        }

        // A member typed as an interface or base class depends on every node that is one.
        private void IndexDependents()
        {
            foreach (var owner in _nodes)
            {
                foreach (var dependency in owner.Dependencies)
                {
                    var target = dependency.Target;
                    if (target == typeof(object)) continue;
                    foreach (var node in _nodes)
                        if (node.Kind != Kind.Signal && target.IsAssignableFrom(node.Type))
                            node.Dependents.Add(dependency);
                }
            }
        }

        private void Finish()
        {
            _nodes.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : string.CompareOrdinal(a.Type.Name, b.Type.Name));
            var assemblies = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var node in _nodes)
            {
                assemblies.Add(node.Assembly ?? string.Empty);
                node.Handlers.Sort((a, b) => node.Kind == Kind.Signal
                    ? b.Priority != a.Priority ? b.Priority.CompareTo(a.Priority) : string.CompareOrdinal(a.Command.Name, b.Command.Name)
                    : string.CompareOrdinal(a.Signal.Name, b.Signal.Name));
                node.Dependents.Sort((a, b) => string.CompareOrdinal(a.Owner.Name, b.Owner.Name));
                node.Related.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            }
            _assemblies.AddRange(assemblies);
        }
    }
}
