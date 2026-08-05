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
        // Lock-free read copy for composite trigger lookup on the dispatch hot path: rebuilt
        // under _compositeLock on registration, read without locking by SignalBus. Each rebuild
        // allocates fresh lists so an in-flight dispatch iterating a previous snapshot is never
        // mutated by a concurrent registration (list instances are never modified in place
        // after publish). Eliminates the per-Fire() lock + per-Fire() list copy that the old
        // TryGetCompositeTriggers snapshot incurred on the hot path.
        private volatile Dictionary<Type, List<CompositeTriggerState>> _compositeTriggersReadCopy = new();
        private readonly List<CompositeTriggerState> _allCompositeTriggers = new();
        private readonly object _handlerReadLock = new();
        private readonly object _compositeLock = new();
        private bool _handlersSnapshotDirty = true;

        // Snapshots for lock-free reads
        private Dictionary<Type, List<CommandHandlerInfo>> _commandHandlersSnapshot = new();
        private volatile Dictionary<Type, List<CommandHandlerInfo>> _commandHandlersReadCopy = new();
        private Dictionary<Type, IReadOnlyList<CommandHandlerInfo>> _registeredHandlersSnapshot = new();

        // REFACTOR PLAN §1.1: the lock-free read copies are rebuilt LAZILY on first dispatch
        // access after a mutation, so registration never pays O(N) dictionary allocation
        // (N commands previously cost ≈N²/2 entry copies at startup even though dispatch
        // never ran during registration). Volatile so a dispatch that observes dirty == false
        // is guaranteed to see the fully-published snapshot (the rebuild writes the snapshot
        // BEFORE clearing this flag). Starts false: the initial empty snapshot is already
        // correct, so the very first Fire with zero registrations stays lock-free.
        private volatile bool _handlersReadCopyDirty = false;

        // Precomputed cache: does this signal type have at least one async handler?
        private readonly Dictionary<Type, bool> _hasAsyncHandler = new();
        private volatile Dictionary<Type, bool> _hasAsyncHandlerReadCopy = new();

        // Audit fix 4.2: cached comparison delegates for the registration-time priority sorts.
        // A lambda written inline at the Sort call site relies on the compiler's delegate
        // caching; the explicit static makes the zero-allocation guarantee self-documenting
        // and immune to accidental capture introduction later.
        private static readonly Comparison<CommandHandlerInfo> s_priorityDescHandlers =
            static (a, b) => b.Priority.CompareTo(a.Priority);
        private static readonly Comparison<CompositeTriggerState> s_priorityDescTriggers =
            static (a, b) => b.Priority.CompareTo(a.Priority);

        // Caches for generic command fallback and cross-context
        private static readonly ConcurrentDictionary<(Type commandType, Type signalType), Action<object, object>> s_signalSetterCache = new();
        private static readonly ConcurrentDictionary<(Type commandType, Type signalType), Action<object, object>> s_genericSyncDispatchCache = new();
        private static readonly ConcurrentDictionary<(Type commandType, Type signalType), Func<object, object, CancellationToken, ValueTask>> s_genericAsyncDispatchCache = new();
        private static readonly ConcurrentDictionary<Type, CrossContextAttribute> s_crossContextCache = new();

        // Per-type cross-context cache: the [CrossContext] attribute is fixed at the type level,
        // so the dispatch hot path can read it from a generic-static slot instead of paying a
        // ConcurrentDictionary lookup per Fire(). The attribute is immutable type metadata, so this
        // cache needs no clearing (ClearStaticCaches still clears the dictionary for API parity).
        private static class CrossContextCache<T> where T : struct
        {
            public static readonly CrossContextAttribute Value = typeof(T).GetCustomAttribute<CrossContextAttribute>();
        }

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

                // C1 fix: the mixed-mode guard must check EVERY existing handler, not just
                // list[0]. In Concurrent mode the list is never sorted (insertion order is
                // preserved), so list[0] is always the first-registered handler — but in
                // Sequential/Exclusive mode the list IS sorted by descending priority, so
                // list[0] is the highest-priority handler. A guard keyed on list[0] alone
                // would let a mixed-mode registration slip through if the first handler's
                // mode happened to match while a later handler's mode differed. Checking the
                // whole list makes the invariant explicit: every handler for a signal shares
                // one ExecutionMode.
                if (list.Count > 0)
                {
                    var firstMode = list[0].Mode;
                    for (int i = 1; i < list.Count; i++)
                    {
                        if (list[i].Mode != firstMode)
                        {
                            throw new InvalidOperationException($"Mixed-mode dispatch error: Signal {signalType.Name} already registered with mode {firstMode}, cannot add handler with mode {mode}.");
                        }
                    }
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
                    list.Sort(s_priorityDescHandlers);
                }

                // REFACTOR PLAN §1.1: registration is now O(1) in allocation. The lock-free
                // read copies (_commandHandlersReadCopy / _hasAsyncHandlerReadCopy) are rebuilt
                // lazily on first dispatch access (see EnsureReadCopies) instead of on every
                // RegisterCommand, so startup with N commands pays ONE rebuild instead of N.
                _handlersReadCopyDirty = true;
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
            _handlersReadCopyDirty = true;
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
                    list.Sort(s_priorityDescTriggers);
                }

                RebuildCompositeReadCopy();
            }

            _container.Bind(commandType, isSingleton: false);
        }

        /// <summary>Checks if a signal type has any async command handlers registered.</summary>
        public bool HasAsyncCommandHandlers(Type signalType)
        {
            EnsureReadCopies();
            return _hasAsyncHandlerReadCopy.TryGetValue(signalType, out var flag) && flag;
        }

        /// <summary>Gets the read-copy of command handlers for a signal type (lock-free dispatch).
        /// Audit fix 5.3: exposed as <see cref="IReadOnlyList{T}"/> — the published snapshot is
        /// shared between concurrent dispatches and must never be mutated through the alias.</summary>
        public bool TryGetHandlers(Type signalType, out IReadOnlyList<CommandHandlerInfo> handlers)
        {
            EnsureReadCopies();
            if (_commandHandlersReadCopy.TryGetValue(signalType, out var list))
            {
                handlers = list;
                return true;
            }
            handlers = null;
            return false;
        }

        /// <summary>
        /// Lazily rebuilds the volatile lock-free read copies after a mutation. The common
        /// (steady-state dispatch) case is a single volatile bool read and NEVER touches a
        /// lock; only when a registration/unregistration races a dispatch does the read path
        /// briefly take <c>_handlerReadLock</c> (short critical section, registration-time
        /// cost only — dispatch itself never mutates the live tables). The double-check under
        /// the lock ensures concurrent dispatches racing the first access never build two
        /// snapshots or read a half-published one. Each per-type list is copied fresh because
        /// registration mutates the live lists in place; after a snapshot is published, its
        /// list instances are never mutated again, so lock-free dispatch iterators are safe.
        /// </summary>
        private void EnsureReadCopies()
        {
            if (!_handlersReadCopyDirty) return;
            lock (_handlerReadLock)
            {
                if (!_handlersReadCopyDirty) return;
                var copy = new Dictionary<Type, List<CommandHandlerInfo>>(_commandHandlers.Count);
                foreach (var kvp in _commandHandlers)
                    copy[kvp.Key] = new List<CommandHandlerInfo>(kvp.Value);
                _commandHandlersReadCopy = copy;
                _hasAsyncHandlerReadCopy = new Dictionary<Type, bool>(_hasAsyncHandler);
                _handlersReadCopyDirty = false;
            }
        }

        /// <summary>
        /// Builds fresh per-type list copies into the volatile read copy. Called under
        /// <c>_compositeLock</c> on every composite registration so dispatch iterators (which
        /// read the read copy WITHOUT the lock) always walk immutable list instances.
        /// </summary>
        private void RebuildCompositeReadCopy()
        {
            var copy = new Dictionary<Type, List<CompositeTriggerState>>(_compositeTriggersBySignal.Count);
            foreach (var kvp in _compositeTriggersBySignal)
                copy[kvp.Key] = new List<CompositeTriggerState>(kvp.Value);
            _compositeTriggersReadCopy = copy;
        }

        /// <summary>
        /// Gets the composite triggers registered for a signal type. LOCK-FREE on the dispatch
        /// hot path: returns the immutable per-type snapshot published on registration (lists are
        /// never mutated in place after publish, so an iterating dispatch is safe while a
        /// concurrent registration rebuilds the snapshot). Returns false when the signal has no
        /// composite triggers (no allocation, no lock).
        ///
        /// The returned snapshot is SHARED between concurrent dispatches — the read-only surface
        /// is deliberate: callers must never mutate it (a Clear/Add would corrupt every other
        /// reader of the same read copy).
        /// </summary>
        public bool TryGetCompositeTriggers(Type signalType, out IReadOnlyList<CompositeTriggerState> triggers)
        {
            if (_compositeTriggersReadCopy.TryGetValue(signalType, out var list))
            {
                triggers = list;
                return true;
            }
            triggers = null;
            return false;
        }

        /// <summary>Gets all composite triggers (for iteration).</summary>
        public IReadOnlyList<CompositeTriggerState> AllCompositeTriggers => _allCompositeTriggers;

        /// <summary>Checks if a signal type has a [CrossContext] attribute (cached).</summary>
        public CrossContextAttribute GetCachedCrossContext(Type type)
            => s_crossContextCache.GetOrAdd(type, static t => t.GetCustomAttribute<CrossContextAttribute>());

        /// <summary>
        /// Generic form of <see cref="GetCachedCrossContext(Type)"/> used by the dispatch hot path:
        /// resolves the attribute from a per-type generic static slot, so the common no-attribute
        /// case costs a single static field read instead of a ConcurrentDictionary lookup.
        /// </summary>
        public CrossContextAttribute GetCachedCrossContext<T>() where T : struct
            => CrossContextCache<T>.Value;

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
                _handlersReadCopyDirty = true;
            }
            lock (_compositeLock)
            {
                _compositeTriggersBySignal.Clear();
                _compositeTriggersReadCopy = new Dictionary<Type, List<CompositeTriggerState>>();
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
