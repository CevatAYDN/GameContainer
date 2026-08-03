using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core
{
    /// <summary>
    /// Handles command registration, validation, and handler metadata.
    /// Separated from dispatch logic to keep the registry focused on registration-time concerns.
    /// Registration semantics mirror <see cref="SignalBus.RegisterCommand"/> /
    /// <see cref="SignalBus.RegisterCompositeCommand"/> exactly, so the benchmark harness can
    /// prove the extraction preserves behavior.
    /// </summary>
    public sealed class CommandRegistry : IDisposable
    {
        private readonly NexusDI _container;
        private readonly Dictionary<Type, List<CommandHandlerInfo>> _commandHandlers = new();
        private readonly Dictionary<Type, List<CompositeTriggerState>> _compositeTriggersBySignal = new();
        private readonly List<CompositeTriggerState> _allCompositeTriggers = new();
        private readonly object _handlerReadLock = new();
        private readonly object _compositeLock = new();
        private bool _handlersSnapshotDirty = true;

        // Snapshots for lock-free reads
        private Dictionary<Type, List<CommandHandlerInfo>> _commandHandlersSnapshot = new();
        private volatile Dictionary<Type, List<CommandHandlerInfo>> _commandHandlersReadCopy = new();
        private Dictionary<Type, IReadOnlyList<CommandHandlerInfo>> _registeredHandlersSnapshot = new();

        // Precomputed cache: does this signal type have at least one async handler?
        private readonly Dictionary<Type, bool> _hasAsyncHandler = new();
        private volatile Dictionary<Type, bool> _hasAsyncHandlerReadCopy = new();

        // Caches for generic command fallback and cross-context
        private static readonly ConcurrentDictionary<(Type commandType, Type signalType), Action<object, object>> s_signalSetterCache = new();
        private static readonly ConcurrentDictionary<(Type commandType, Type signalType), Action<object, object>> s_genericSyncDispatchCache = new();
        private static readonly ConcurrentDictionary<(Type commandType, Type signalType), Func<object, object, CancellationToken, ValueTask>> s_genericAsyncDispatchCache = new();
        private static readonly ConcurrentDictionary<Type, CrossContextAttribute> s_crossContextCache = new();

        public CommandRegistry(NexusDI container)
        {
            _container = container;
        }

        /// <summary>Gets a snapshot of the registered signal→handler maps (lazily rebuilt on first access).</summary>
        public IReadOnlyDictionary<Type, List<CommandHandlerInfo>> CommandHandlers
        {
            get
            {
                lock (_handlerReadLock)
                {
                    RebuildHandlerSnapshotsIfDirty();
                    return _commandHandlersSnapshot;
                }
            }
        }

        /// <summary>Gets all registered signal→handler mappings as read-only lists.</summary>
        public IReadOnlyDictionary<Type, IReadOnlyList<CommandHandlerInfo>> RegisteredHandlers
        {
            get
            {
                lock (_handlerReadLock)
                {
                    RebuildHandlerSnapshotsIfDirty();
                    return _registeredHandlersSnapshot;
                }
            }
        }

        /// <summary>Registers a synchronous or async command for a signal type.</summary>
        /// <param name="oneShot">When true the handler fires once then is unregistered.</param>
        public void RegisterCommand(Type signalType, Type commandType, ExecutionMode mode, int priority, bool isAsync, bool oneShot = false)
        {
            var genericSyncType = typeof(ICommand<>).MakeGenericType(signalType);
            var genericAsyncType = typeof(IAsyncCommand<>).MakeGenericType(signalType);
            bool implementsGenericSync = genericSyncType.IsAssignableFrom(commandType);
            bool implementsGenericAsync = genericAsyncType.IsAssignableFrom(commandType);

            if (!implementsGenericSync && !implementsGenericAsync)
            {
                throw new InvalidOperationException($"Command type {commandType.Name} registered for signal {signalType.Name} must implement either ICommand<{signalType.Name}> or IAsyncCommand<{signalType.Name}>.");
            }

            if (implementsGenericAsync && implementsGenericSync)
            {
                throw new InvalidOperationException($"Command type {commandType.Name} cannot implement both ICommand and IAsyncCommand interfaces.");
            }
            if (implementsGenericAsync && !isAsync)
            {
                throw new InvalidOperationException($"Command type {commandType.Name} implements IAsyncCommand but is being registered as sync. It must be registered as async (isAsync: true).");
            }

            var timeoutAttr = commandType.GetCustomAttribute<CommandTimeoutAttribute>();
            int timeoutMs = timeoutAttr != null ? timeoutAttr.Milliseconds : 0;

            lock (_handlerReadLock)
            {
                if (!_commandHandlers.TryGetValue(signalType, out var list))
                {
                    list = new List<CommandHandlerInfo>();
                    _commandHandlers[signalType] = list;
                }

                if (list.Count > 0 && list[0].Mode != mode)
                {
                    throw new InvalidOperationException($"Mixed-mode dispatch error: Signal {signalType.Name} already registered with mode {list[0].Mode}, cannot add handler with mode {mode}.");
                }

                if (mode == ExecutionMode.Exclusive && list.Count > 0)
                {
                    throw new InvalidOperationException($"Exclusive execution mode violation: Signal {signalType.Name} already has a handler registered.");
                }

                if (mode != ExecutionMode.Concurrent)
                {
                    foreach (var handler in list)
                    {
                        if (handler.Priority == priority)
                        {
                            throw new InvalidOperationException($"Duplicate priority {priority} for signal {signalType.Name}.");
                        }
                    }
                }

                list.Add(new CommandHandlerInfo(commandType, mode, priority, isAsync, timeoutMs, oneShot));
                _handlersSnapshotDirty = true;

                if (isAsync)
                {
                    _hasAsyncHandler[signalType] = true;
                }
                else if (!_hasAsyncHandler.ContainsKey(signalType))
                {
                    _hasAsyncHandler[signalType] = false;
                }

                if (mode != ExecutionMode.Concurrent)
                {
                    list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                }

                _commandHandlersReadCopy = new Dictionary<Type, List<CommandHandlerInfo>>(_commandHandlers.Count);
                foreach (var kvp in _commandHandlers)
                    _commandHandlersReadCopy[kvp.Key] = new List<CommandHandlerInfo>(kvp.Value);
                _hasAsyncHandlerReadCopy = new Dictionary<Type, bool>(_hasAsyncHandler);
            }

            _container.Bind(commandType, isSingleton: false);
        }

        /// <summary>
        /// Removes a previously registered command handler (used by one-shot commands after
        /// their single execution). Rebuilds the lock-free read copy so subsequent fires no
        /// longer see the handler. No-op when the handler is not registered.
        /// </summary>
        public void UnregisterCommand(Type signalType, Type commandType)
        {
            lock (_handlerReadLock)
            {
                if (!_commandHandlers.TryGetValue(signalType, out var list))
                    return;

                int removed = list.RemoveAll(h => h.CommandType == commandType);
                if (removed == 0) return;

                RebuildHandlerReadCopies(signalType, list);
            }
        }

        /// <summary>
        /// Rebuilds the volatile read-copy snapshots after a handler-list mutation under
        /// <see cref="_handlerReadLock"/>. Removes the signal entirely when its list is empty,
        /// otherwise recomputes the async flag for the remaining handlers.
        /// </summary>
        private void RebuildHandlerReadCopies(Type signalType, List<CommandHandlerInfo> list)
        {
            if (list.Count == 0)
            {
                _commandHandlers.Remove(signalType);
                _hasAsyncHandler.Remove(signalType);
            }
            else
            {
                // Recompute the async flag for the remaining handlers.
                bool anyAsync = false;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].IsAsync) { anyAsync = true; break; }
                }
                _hasAsyncHandler[signalType] = anyAsync;
            }

            _handlersSnapshotDirty = true;
            _commandHandlersReadCopy = new Dictionary<Type, List<CommandHandlerInfo>>(_commandHandlers.Count);
            foreach (var kvp in _commandHandlers)
                _commandHandlersReadCopy[kvp.Key] = new List<CommandHandlerInfo>(kvp.Value);
            _hasAsyncHandlerReadCopy = new Dictionary<Type, bool>(_hasAsyncHandler);
        }

        /// <summary>
        /// Atomically claims a one-shot command handler so it can fire at most once even under
        /// concurrent Fire calls. Returns true only to the fire that wins the claim (that caller
        /// must execute the command); false means the handler was already claimed by a concurrent
        /// fire, is not registered as one-shot, or is not registered at all.
        /// </summary>
        public bool TryClaimOneShot(Type signalType, Type commandType)
        {
            lock (_handlerReadLock)
            {
                if (!_commandHandlers.TryGetValue(signalType, out var list))
                    return false;

                // Only one-shot handlers are claimable; persistent handlers are untouched.
                int removed = list.RemoveAll(h => h.CommandType == commandType && h.IsOneShot);
                if (removed == 0) return false;

                RebuildHandlerReadCopies(signalType, list);
                return true;
            }
        }

        /// <summary>Registers a composite command that triggers on multiple signals.</summary>
        public void RegisterCompositeCommand(Type[] signalTypes, Type commandType, bool oneShot, int priority, bool isAsync)
        {
            if (signalTypes == null || signalTypes.Length == 0)
                throw new ArgumentException("Composite command requires at least one signal type.", nameof(signalTypes));
            if (signalTypes.Length > 64 || signalTypes.Length == 0)
                throw new ArgumentException($"Composite command requires between 1 and 64 signal types. Received {signalTypes.Length}.", nameof(signalTypes));

            for (int i = 0; i < signalTypes.Length; i++)
            {
                if (signalTypes[i] == null)
                    throw new ArgumentException("Composite signal types cannot be null.", nameof(signalTypes));
                for (int j = i + 1; j < signalTypes.Length; j++)
                {
                    if (signalTypes[i] == signalTypes[j])
                    {
                        throw new ArgumentException($"Composite command requires unique signal types; '{signalTypes[i].Name}' appears more than once.", nameof(signalTypes));
                    }
                }
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (ImplementsGenericInterface(commandType, typeof(ICommand<>)) || ImplementsGenericInterface(commandType, typeof(IAsyncCommand<>)))
            {
                NexusRuntime.Logger?.LogWarning($"[Nexus] Composite command '{commandType.Name}' implements a single-signal generic command interface (ICommand<T>/IAsyncCommand<T>), which is not supported for composites. Implement ICompositeCommand / IAsyncCompositeCommand to receive all trigger payloads, or non-generic ICommand / IAsyncCommand if no payload is needed.");
            }
#endif

            var state = new CompositeTriggerState(commandType, signalTypes, oneShot, priority);

            lock (_compositeLock)
            {
                _allCompositeTriggers.Add(state);
                foreach (var sigType in signalTypes)
                {
                    if (!_compositeTriggersBySignal.TryGetValue(sigType, out var list))
                    {
                        list = new List<CompositeTriggerState>();
                        _compositeTriggersBySignal[sigType] = list;
                    }
                    list.Add(state);
                    list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                }
            }

            _container.Bind(commandType, isSingleton: false);
        }

        /// <summary>Checks if a signal type has any async command handlers registered.</summary>
        public bool HasAsyncCommandHandlers(Type signalType)
        {
            return _hasAsyncHandlerReadCopy.TryGetValue(signalType, out var flag) && flag;
        }

        /// <summary>Gets the read-copy of command handlers for a signal type (lock-free dispatch).</summary>
        public bool TryGetHandlers(Type signalType, out List<CommandHandlerInfo> handlers)
        {
            return _commandHandlersReadCopy.TryGetValue(signalType, out handlers);
        }

        /// <summary>
        /// Gets a snapshot of the composite triggers registered for a signal type, taken under the
        /// composite lock so a dispatching bus can iterate safely while registration adds triggers.
        /// Returns false when the signal has no composite triggers (no allocation on the hot path).
        /// </summary>
        public bool TryGetCompositeTriggers(Type signalType, out List<CompositeTriggerState> triggers)
        {
            lock (_compositeLock)
            {
                if (!_compositeTriggersBySignal.TryGetValue(signalType, out var list))
                {
                    triggers = null;
                    return false;
                }
                triggers = new List<CompositeTriggerState>(list);
                return true;
            }
        }

        /// <summary>Gets all composite triggers (for iteration).</summary>
        public IReadOnlyList<CompositeTriggerState> AllCompositeTriggers => _allCompositeTriggers;

        /// <summary>Checks if a signal type has a [CrossContext] attribute (cached).</summary>
        public CrossContextAttribute GetCachedCrossContext(Type type)
            => s_crossContextCache.GetOrAdd(type, static t => t.GetCustomAttribute<CrossContextAttribute>());

        /// <summary>
        /// Returns a cached dispatcher that invokes <see cref="ICommand{TSignal}"/>.Execute on a
        /// generic-only command, or null if the command type does not implement that interface.
        /// Uses Expression-compiled delegates to avoid per-call object[] allocation on the hot path.
        /// Falls back to reflection on IL2CPP/AOT platforms where Expression.Compile() is unavailable.
        /// </summary>
        public Action<object, object> GetGenericSyncDispatcher(Type commandType, Type signalType)
        {
            var key = (commandType, signalType);
            if (s_genericSyncDispatchCache.TryGetValue(key, out var cached)) return cached;

            var genericInterface = typeof(ICommand<>).MakeGenericType(signalType);
            if (!genericInterface.IsAssignableFrom(commandType)) return null;

            var method = genericInterface.GetMethod("Execute");
            if (method == null) return null;

#if ENABLE_IL2CPP || UNITY_AOT || UNITY_IOS || UNITY_WEBGL
            // IL2CPP/AOT: Expression.Compile() uses System.Reflection.Emit.DynamicMethod which is
            // unavailable on these platforms (throws NotSupportedException). Fall back to reflection.
            // This allocates an object[] per call but avoids the crash.
            Action<object, object> dispatcher = (cmd, sig) => method.Invoke(cmd, new object[] { sig });
#else
            // Compile an Expression-tree delegate: (object cmd, object sig) => ((ICommand<T>)cmd).Execute((T)sig)
            // This eliminates the object[] allocation that method.Invoke(cmd, new[] { sig }) causes on every dispatch.
            var cmdParam = System.Linq.Expressions.Expression.Parameter(typeof(object), "cmd");
            var sigParam = System.Linq.Expressions.Expression.Parameter(typeof(object), "sig");
            var castCmd  = System.Linq.Expressions.Expression.Convert(cmdParam, genericInterface);
            var castSig  = System.Linq.Expressions.Expression.Convert(sigParam, signalType);
            var call     = System.Linq.Expressions.Expression.Call(castCmd, method, castSig);
            var lambda   = System.Linq.Expressions.Expression.Lambda<Action<object, object>>(call, cmdParam, sigParam);
            Action<object, object> dispatcher = lambda.Compile();
#endif

            s_genericSyncDispatchCache.TryAdd(key, dispatcher);
            return dispatcher;
        }

        /// <summary>
        /// Returns a cached dispatcher that invokes <see cref="IAsyncCommand{TSignal}"/>.ExecuteAsync on
        /// a generic-only async command, or null if the command type does not implement that interface.
        /// Uses Expression-compiled delegates to avoid per-call object[] allocation on the hot path.
        /// Falls back to reflection on IL2CPP/AOT platforms where Expression.Compile() is unavailable.
        /// </summary>
        public Func<object, object, CancellationToken, ValueTask> GetGenericAsyncDispatcher(Type commandType, Type signalType)
        {
            var key = (commandType, signalType);
            if (s_genericAsyncDispatchCache.TryGetValue(key, out var cached)) return cached;

            var genericInterface = typeof(IAsyncCommand<>).MakeGenericType(signalType);
            if (!genericInterface.IsAssignableFrom(commandType)) return null;

            var method = genericInterface.GetMethod("ExecuteAsync");
            if (method == null) return null;

#if ENABLE_IL2CPP || UNITY_AOT || UNITY_IOS || UNITY_WEBGL
            // IL2CPP/AOT: reflection fallback (see GetGenericSyncDispatcher for rationale)
            Func<object, object, CancellationToken, ValueTask> dispatcher = (cmd, sig, ct) =>
            {
                var result = method.Invoke(cmd, new object[] { sig, ct });
                return result is ValueTask vt ? vt : default;
            };
#else
            // Compile: (object cmd, object sig, CancellationToken ct) => ((IAsyncCommand<T>)cmd).ExecuteAsync((T)sig, ct)
            var cmdParam = System.Linq.Expressions.Expression.Parameter(typeof(object), "cmd");
            var sigParam = System.Linq.Expressions.Expression.Parameter(typeof(object), "sig");
            var ctParam  = System.Linq.Expressions.Expression.Parameter(typeof(CancellationToken), "ct");
            var castCmd  = System.Linq.Expressions.Expression.Convert(cmdParam, genericInterface);
            var castSig  = System.Linq.Expressions.Expression.Convert(sigParam, signalType);
            var call     = System.Linq.Expressions.Expression.Call(castCmd, method, castSig, ctParam);
            var lambda   = System.Linq.Expressions.Expression.Lambda<Func<object, object, CancellationToken, ValueTask>>(call, cmdParam, sigParam, ctParam);
            Func<object, object, CancellationToken, ValueTask> dispatcher = lambda.Compile();
#endif

            s_genericAsyncDispatchCache.TryAdd(key, dispatcher);
            return dispatcher;
        }

        /// <summary>Gets or creates a signal setter delegate.</summary>
        public Action<object, object> GetSignalSetter(Type commandType, Type signalType)
            => s_signalSetterCache.GetOrAdd((commandType, signalType), CreateSignalSetter);

        private static bool ImplementsGenericInterface(Type type, Type genericInterface)
        {
            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == genericInterface)
                    return true;
            }
            return false;
        }

        private void RebuildHandlerSnapshotsIfDirty()
        {
            if (!_handlersSnapshotDirty && _commandHandlersSnapshot != null) return;
            _handlersSnapshotDirty = false;

            _commandHandlersSnapshot = new Dictionary<Type, List<CommandHandlerInfo>>(_commandHandlers.Count);
            var dict = new Dictionary<Type, IReadOnlyList<CommandHandlerInfo>>(_commandHandlers.Count);
            foreach (var kvp in _commandHandlers)
            {
                var listCopy = new List<CommandHandlerInfo>(kvp.Value);
                _commandHandlersSnapshot[kvp.Key] = listCopy;
                dict[kvp.Key] = listCopy;
            }
            _registeredHandlersSnapshot = dict;
        }

        /// <summary>
        /// Builds a cached setter that assigns the signal payload to the matching field or property
        /// on a non-generic command instance. Matches SignalBus's InjectSignal semantics: fields by
        /// exact type OR (type-compatible AND named <c>_signal</c>/<c>signal</c>), then writable
        /// properties by exact type. Falls back to a no-op setter when nothing matches.
        /// </summary>
        private static Action<object, object> CreateSignalSetter((Type commandType, Type signalType) key)
        {
            var cmdType = key.commandType;
            var sigType = key.signalType;
            Action<object, object> newSetter = null;
            MemberInfo foundMember = null;

            var fields = cmdType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var field in fields)
            {
                // Exact type match, or a type-compatible field named _signal/signal.
                // Type-level equivalent of SignalBus's runtime check
                // (field.FieldType.IsInstanceOfType(signal)): the field type must be able to
                // hold the signal. A reference-typed field of an unrelated type must NOT match.
                if (field.FieldType == sigType ||
                    (field.FieldType.IsAssignableFrom(sigType) &&
                     (field.Name.Equals("_signal", StringComparison.OrdinalIgnoreCase) ||
                      field.Name.Equals("signal", StringComparison.OrdinalIgnoreCase))))
                {
                    foundMember = field;
                    break;
                }
            }

            if (foundMember == null)
            {
                var properties = cmdType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var prop in properties)
                {
                    if (prop.PropertyType == sigType && prop.CanWrite)
                    {
                        foundMember = prop;
                        break;
                    }
                }
            }

            if (foundMember != null)
            {
                try
                {
                    var targetParam = System.Linq.Expressions.Expression.Parameter(typeof(object), "target");
                    var valParam = System.Linq.Expressions.Expression.Parameter(typeof(object), "val");
                    var castTarget = System.Linq.Expressions.Expression.Convert(targetParam, cmdType);

                    System.Linq.Expressions.MemberExpression memberExpr = foundMember is FieldInfo fInfo
                        ? System.Linq.Expressions.Expression.Field(castTarget, fInfo)
                        : System.Linq.Expressions.Expression.Property(castTarget, (PropertyInfo)foundMember);

                    var memberType = foundMember is FieldInfo fi ? fi.FieldType : ((PropertyInfo)foundMember).PropertyType;
                    var castVal = System.Linq.Expressions.Expression.Convert(valParam, memberType);
                    var assign = System.Linq.Expressions.Expression.Assign(memberExpr, castVal);

                    var lambda = System.Linq.Expressions.Expression.Lambda<Action<object, object>>(assign, targetParam, valParam);
                    newSetter = lambda.Compile();
                }
                catch
                {
                    // Fallback to reflection if Expression compilation is restricted on certain AOT platforms
                    if (foundMember is FieldInfo f) newSetter = (target, val) => f.SetValue(target, val);
                    else if (foundMember is PropertyInfo p) newSetter = (target, val) => p.SetValue(target, val);
                }
            }
            else
            {
                newSetter = (target, val) => { };
            }
            return newSetter;
        }

        /// <summary>Clears all registered command and composite state.</summary>
        public void Dispose()
        {
            lock (_handlerReadLock)
            {
                _commandHandlers.Clear();
                _commandHandlersSnapshot?.Clear();
                _commandHandlersReadCopy?.Clear();
                _registeredHandlersSnapshot?.Clear();
                _hasAsyncHandler.Clear();
                _hasAsyncHandlerReadCopy?.Clear();
            }
            lock (_compositeLock)
            {
                _compositeTriggersBySignal.Clear();
                _allCompositeTriggers.Clear();
            }
        }

        /// <summary>
        /// Clears the process-lifetime reflection caches (signal setters, generic dispatchers,
        /// cross-context attributes). Called by <see cref="SignalBus.ClearStaticCaches"/> so the
        /// whole runtime tears down shared caches in one place.
        /// </summary>
        internal static void ClearStaticCaches()
        {
            s_signalSetterCache.Clear();
            s_genericSyncDispatchCache.Clear();
            s_genericAsyncDispatchCache.Clear();
            s_crossContextCache.Clear();
        }
    }
}
