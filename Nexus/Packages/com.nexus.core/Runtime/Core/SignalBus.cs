using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    [Preserve]
    public class CommandHandlerInfo
    {
        public Type CommandType { get; }
        public ExecutionMode Mode { get; }
        public int Priority { get; }
        public bool IsAsync { get; }

        public CommandHandlerInfo(Type commandType, ExecutionMode mode, int priority, bool isAsync)
        {
            CommandType = commandType;
            Mode = mode;
            Priority = priority;
            IsAsync = isAsync;
        }
    }

    public class CompositeTriggerState
    {
        public Type CommandType { get; }
        public Type[] RequiredSignals { get; }
        public bool OneShot { get; }
        public int Priority { get; }
        public ulong CurrentMask { get; set; }
        public ulong TargetMask { get; }
        public bool IsCompleted { get; set; }

        public CompositeTriggerState(Type commandType, Type[] requiredSignals, bool oneShot, int priority)
        {
            CommandType = commandType;
            RequiredSignals = requiredSignals;
            OneShot = oneShot;
            Priority = priority;
            TargetMask = (1UL << requiredSignals.Length) - 1;
            CurrentMask = 0;
            IsCompleted = false;
        }
    }

    public readonly struct CommandFailedSignal
    {
        public readonly Exception Exception;
        public readonly Type SourceCommand;
        public readonly object SourceSignal;

        public CommandFailedSignal(Exception exception, Type sourceCommand, object sourceSignal)
        {
            Exception = exception;
            SourceCommand = sourceCommand;
            SourceSignal = sourceSignal;
        }
    }

    [Preserve]
    public class SignalBus : ISignalBus, IDisposable
    {
        private readonly NexusDI _container;
        private readonly CommandPoolManager _poolManager;
        private readonly IContext _context;

        private readonly Dictionary<Type, List<CommandHandlerInfo>> _commandHandlers = new();
        private readonly Dictionary<Type, List<CompositeTriggerState>> _compositeTriggersBySignal = new();
        private readonly List<CompositeTriggerState> _allCompositeTriggers = new();

        private readonly Dictionary<Type, List<object>> _subscriptions = new();
        
        [ThreadStatic]
        private static int s_stackDepth;
        private const int MaxStackDepth = 50;

        private int _inFlightAsyncCommands;
        private const int MaxInFlightAsyncCommands = 100;

#if NEXUS_DEBUG
        private static readonly ProfilerMarker s_DispatchMarker = new ProfilerMarker("Nexus.Signal.Dispatch");
        private static readonly ProfilerMarker s_CommandMarker = new ProfilerMarker("Nexus.Command.Execute");
        private static readonly ProfilerMarker s_DrainMarker = new ProfilerMarker("Nexus.Queue.Drain");
#endif

        public SignalBus(NexusDI container, CommandPoolManager poolManager, IContext context)
        {
            _container = container;
            _poolManager = poolManager;
            _context = context;
        }

        public void RegisterCommand(Type signalType, Type commandType, ExecutionMode mode, int priority, bool isAsync)
        {
            if (!_commandHandlers.TryGetValue(signalType, out var list))
            {
                list = new List<CommandHandlerInfo>();
                _commandHandlers[signalType] = list;
            }

            // Verify Mixed-Mode restriction
            if (list.Count > 0 && list[0].Mode != mode)
            {
                throw new InvalidOperationException($"Mixed-mode dispatch error: Signal {signalType.Name} already registered with mode {list[0].Mode}, cannot add handler with mode {mode}.");
            }

            // Verify Exclusive mode restriction
            if (mode == ExecutionMode.Exclusive && list.Count > 0)
            {
                throw new InvalidOperationException($"Exclusive execution mode violation: Signal {signalType.Name} already has a handler registered.");
            }

            // Verify priority uniqueness for Sequential/Exclusive
            if (mode != ExecutionMode.Concurrent)
            {
                foreach (var handler in list)
                {
                    if (handler.Priority == priority)
                    {
                        // Priority tie break fallback check or Build/Validation error
                        throw new InvalidOperationException($"Duplicate priority {priority} for signal {signalType.Name}.");
                    }
                }
            }

            list.Add(new CommandHandlerInfo(commandType, mode, priority, isAsync));
            
            // Bind command type in DI so CommandPoolManager can resolve it
            _container.Bind(commandType, isSingleton: false);

            // Sort by priority descending
            if (mode != ExecutionMode.Concurrent)
            {
                list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }
        }

        public void RegisterCompositeCommand(Type[] signalTypes, Type commandType, bool oneShot, int priority, bool isAsync)
        {
            var state = new CompositeTriggerState(commandType, signalTypes, oneShot, priority);
            _allCompositeTriggers.Add(state);

            foreach (var sigType in signalTypes)
            {
                if (!_compositeTriggersBySignal.TryGetValue(sigType, out var list))
                {
                    list = new List<CompositeTriggerState>();
                    _compositeTriggersBySignal[sigType] = list;
                }
                list.Add(state);
            }
            
            _container.Bind(commandType, isSingleton: false);
        }

        public void Fire<T>(T signal) where T : struct
        {
            FireInternal(signal, isCrossContextSource: false);
        }

        public async ValueTask FireAsync<T>(T signal) where T : struct
        {
            await FireInternalAsync(signal, isCrossContextSource: false);
        }

        public void FireThreadSafe<T>(T signal) where T : struct
        {
            var hybridQueue = _container.Resolve<HybridQueue>();
            hybridQueue.EnqueueThreadSafe(signal);
        }

        public void FireNextFrame<T>(T signal) where T : struct
        {
            var hybridQueue = _container.Resolve<HybridQueue>();
            hybridQueue.EnqueueNextFrame(signal);
        }

        public ISignalSubscription Subscribe<T>(Action<T> handler) where T : struct
        {
            var type = typeof(T);
            if (!_subscriptions.TryGetValue(type, out var list))
            {
                list = new List<object>();
                _subscriptions[type] = list;
            }

            var sub = new SignalSubscription<T>(handler, _context.LifetimeToken, () =>
            {
                lock (list)
                {
                    list.Remove(handler);
                }
            });

            lock (list)
            {
                list.Add(sub);
            }
            return sub;
        }

        public ISignalSubscription SubscribeAsync<T>(Func<T, CancellationToken, ValueTask> handler) where T : struct
        {
            var type = typeof(T);
            if (!_subscriptions.TryGetValue(type, out var list))
            {
                list = new List<object>();
                _subscriptions[type] = list;
            }

            var sub = new AsyncSignalSubscription<T>(handler, _context.LifetimeToken, () =>
            {
                lock (list)
                {
                    list.Remove(handler);
                }
            });

            lock (list)
            {
                list.Add(sub);
            }
            return sub;
        }

        private void FireInternal<T>(T signal, bool isCrossContextSource) where T : struct
        {
            s_stackDepth++;
            if (s_stackDepth > MaxStackDepth)
            {
                s_stackDepth = 0;
                throw new NexusReentrancyException($"Stack overflow detected. Reentrancy limit of {MaxStackDepth} exceeded for signal {typeof(T).FullName}");
            }

#if NEXUS_DEBUG
            int eventId = NexusTrace.BeginEvent(TraceEventType.Signal, typeof(T).Name);
            s_DispatchMarker.Begin();
#endif
            try
            {
                var type = typeof(T);

                // Run plugins' SignalInterceptors
                object boxedSignal = signal;
                if (_context is Context ctx && ctx.Plugins.Count > 0)
                {
                    foreach (var p in ctx.Plugins)
                    {
                        foreach (var interceptor in p.context.Interceptors)
                        {
                            if (!interceptor.Intercept(ref boxedSignal))
                            {
#if NEXUS_DEBUG
                                NexusTrace.EndEvent(eventId, TraceStatus.Cancelled);
#endif
                                return;
                            }
                        }
                    }
                    signal = (T)boxedSignal;
                }

                // Handle Cross-Context
                if (!isCrossContextSource)
                {
                    var crossContextAttr = type.GetCustomAttribute<CrossContextAttribute>();
                    if (crossContextAttr != null)
                    {
                        BroadcastCrossContext(signal, crossContextAttr.ScopeTag);
                    }
                }

                // Process subscriptions
                if (_subscriptions.TryGetValue(type, out var subs))
                {
                    lock (subs)
                    {
                        for (int i = 0; i < subs.Count; i++)
                        {
                            if (subs[i] is SignalSubscription<T> syncSub)
                            {
                                syncSub.Invoke(signal);
                            }
                        }
                    }
                }

                // Process commands
                if (_commandHandlers.TryGetValue(type, out var handlers))
                {
                    foreach (var handler in handlers)
                    {
                        if (handler.IsAsync)
                        {
                            _ = ExecuteCommandAsync(handler, signal, _context.LifetimeToken);
                        }
                        else
                        {
                            ExecuteCommand(handler, signal);
                        }
                    }
                }

                // Process composite triggers
                ProcessCompositeTriggers(type);
#if NEXUS_DEBUG
                NexusTrace.EndEvent(eventId, TraceStatus.OK);
#endif
            }
            catch (Exception)
            {
#if NEXUS_DEBUG
                NexusTrace.EndEvent(eventId, TraceStatus.Failed);
#endif
                throw;
            }
            finally
            {
#if NEXUS_DEBUG
                s_DispatchMarker.End();
#endif
                s_stackDepth--;
            }
        }

        private async ValueTask FireInternalAsync<T>(T signal, bool isCrossContextSource) where T : struct
        {
            s_stackDepth++;
            if (s_stackDepth > MaxStackDepth)
            {
                s_stackDepth = 0;
                throw new NexusReentrancyException($"Stack overflow detected. Reentrancy limit of {MaxStackDepth} exceeded for signal {typeof(T).FullName}");
            }

            int eventId = NexusTrace.BeginEvent(TraceEventType.Signal, typeof(T).Name);
            try
            {
                var type = typeof(T);

                // Run plugins' SignalInterceptors
                object boxedSignal = signal;
                if (_context is Context ctx && ctx.Plugins.Count > 0)
                {
                    foreach (var p in ctx.Plugins)
                    {
                        foreach (var interceptor in p.context.Interceptors)
                        {
                            if (!interceptor.Intercept(ref boxedSignal))
                            {
                                NexusTrace.EndEvent(eventId, TraceStatus.Cancelled);
                                return;
                            }
                        }
                    }
                    signal = (T)boxedSignal;
                }

                // Handle Cross-Context
                if (!isCrossContextSource)
                {
                    var crossContextAttr = type.GetCustomAttribute<CrossContextAttribute>();
                    if (crossContextAttr != null)
                    {
                        BroadcastCrossContext(signal, crossContextAttr.ScopeTag);
                    }
                }

                // Subscriptions
                if (_subscriptions.TryGetValue(type, out var subs))
                {
                    List<object> subsCopy;
                    lock (subs)
                    {
                        subsCopy = new List<object>(subs);
                    }
                    
                    foreach (var sub in subsCopy)
                    {
                        if (sub is SignalSubscription<T> syncSub)
                        {
                            syncSub.Invoke(signal);
                        }
                        else if (sub is AsyncSignalSubscription<T> asyncSub)
                        {
                            await asyncSub.InvokeAsync(signal, _context.LifetimeToken);
                        }
                    }
                }

                // Process commands
                if (_commandHandlers.TryGetValue(type, out var handlers))
                {
                    if (handlers.Count > 0 && handlers[0].Mode == ExecutionMode.Concurrent)
                    {
                        // Run concurrently
                        var tasks = new ValueTask[handlers.Count];
                        for (int i = 0; i < handlers.Count; i++)
                        {
                            tasks[i] = ExecuteCommandAsync(handlers[i], signal, _context.LifetimeToken);
                        }
                        
                        foreach (var task in tasks)
                        {
                            await task;
                        }
                    }
                    else
                    {
                        // Run sequentially
                        foreach (var handler in handlers)
                        {
                            if (handler.IsAsync)
                            {
                                await ExecuteCommandAsync(handler, signal, _context.LifetimeToken);
                            }
                            else
                            {
                                ExecuteCommand(handler, signal);
                            }
                        }
                    }
                }

                // Process composite triggers
                ProcessCompositeTriggers(type);
                NexusTrace.EndEvent(eventId, TraceStatus.OK);
            }
            catch (Exception)
            {
                NexusTrace.EndEvent(eventId, TraceStatus.Failed);
                throw;
            }
            finally
            {
                s_stackDepth--;
            }
        }

        private void ExecuteCommand(CommandHandlerInfo handler, object signal)
        {
            int retryCount = 0;
            bool shouldRun = true;

            while (shouldRun)
            {
#if NEXUS_DEBUG
                int traceId = NexusTrace.BeginEvent(TraceEventType.Command, handler.CommandType.Name, handler.Mode);
                s_CommandMarker.Begin();
#endif
                object command = null;
                try
                {
                    command = _poolManager.GetCommand(handler.CommandType);
                    _container.Inject(command);
                    InjectSignal(command, signal);

                    if (command is ICommand syncCmd)
                    {
                        ExecuteWithDecorators(syncCmd, () => syncCmd.Execute());
                    }
                    shouldRun = false; // completed successfully
#if NEXUS_DEBUG
                    NexusTrace.EndEvent(traceId, TraceStatus.OK);
#endif
                }
                catch (Exception ex)
                {
#if NEXUS_DEBUG
                    NexusTrace.EndEvent(traceId, TraceStatus.Failed);
#endif
                    var action = HandleCommandErrorWithDecision(ex, handler.CommandType, signal, ref retryCount);
                    if (action != RecoveryAction.Retry)
                    {
                        shouldRun = false;
                    }
                }
                finally
                {
#if NEXUS_DEBUG
                    s_CommandMarker.End();
#endif
                    if (command != null)
                    {
                        _poolManager.ReturnCommand(handler.CommandType, command);
                    }
                }
            }
        }

        private async ValueTask ExecuteCommandAsync(CommandHandlerInfo handler, object signal, CancellationToken ct)
        {
            int retryCount = 0;
            bool shouldRun = true;

            while (shouldRun)
            {
#if NEXUS_DEBUG
                int traceId = NexusTrace.BeginEvent(TraceEventType.Command, handler.CommandType.Name, handler.Mode);
                s_CommandMarker.Begin();
#endif
                object command = null;
                bool inFlightIncremented = false;
                try
                {
                    var count = Interlocked.Increment(ref _inFlightAsyncCommands);
                    if (count > MaxInFlightAsyncCommands)
                    {
                        Interlocked.Decrement(ref _inFlightAsyncCommands);
                        throw new NexusAsyncOverflowException($"Async execution overflow. Max in-flight async commands limit of {MaxInFlightAsyncCommands} exceeded.");
                    }
                    inFlightIncremented = true;

                    command = _poolManager.GetCommand(handler.CommandType);
                    _container.Inject(command);
                    InjectSignal(command, signal);

                    if (command is IAsyncCommand asyncCmd)
                    {
                        await ExecuteWithDecoratorsAsync(asyncCmd, async () => await asyncCmd.ExecuteAsync(ct));
                    }
                    shouldRun = false; // success
#if NEXUS_DEBUG
                    NexusTrace.EndEvent(traceId, TraceStatus.OK);
#endif
                }
                catch (Exception ex)
                {
#if NEXUS_DEBUG
                    NexusTrace.EndEvent(traceId, TraceStatus.Failed);
#endif
                    var action = await HandleCommandErrorWithDecisionAsync(ex, handler.CommandType, signal, retryCount, ct);
                    if (action == RecoveryAction.Retry)
                    {
                        retryCount++;
                    }
                    else
                    {
                        shouldRun = false;
                    }
                }
                finally
                {
#if NEXUS_DEBUG
                    s_CommandMarker.End();
#endif
                    if (inFlightIncremented)
                    {
                        Interlocked.Decrement(ref _inFlightAsyncCommands);
                    }
                    if (command != null)
                    {
                        _poolManager.ReturnCommand(handler.CommandType, command);
                    }
                }
            }
        }

        private void InjectSignal(object command, object signal)
        {
            if (signal == null) return;
            var type = command.GetType();
            var signalType = signal.GetType();
            
            // Check if there is a field of this signal type (or field named _signal)
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (field.FieldType == signalType || (field.Name.Equals("_signal", StringComparison.OrdinalIgnoreCase) && field.FieldType.IsInstanceOfType(signal)))
                {
                    field.SetValue(command, signal);
                    return;
                }
            }

            // Check properties
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                if (prop.PropertyType == signalType && prop.CanWrite)
                {
                    prop.SetValue(command, signal);
                    return;
                }
            }
        }

        private void ProcessCompositeTriggers(Type signalType)
        {
            if (!_compositeTriggersBySignal.TryGetValue(signalType, out var triggers))
                return;

            foreach (var trigger in triggers)
            {
                if (trigger.IsCompleted) continue;

                // Find index of signalType in required signals
                int index = Array.IndexOf(trigger.RequiredSignals, signalType);
                if (index >= 0)
                {
                    trigger.CurrentMask |= (1UL << index);
                    
                    if (trigger.CurrentMask == trigger.TargetMask)
                    {
                        // Trigger the command!
                        ExecuteCompositeCommand(trigger);

                        if (trigger.OneShot)
                        {
                            trigger.IsCompleted = true;
                        }
                        else
                        {
                            // Reset bitmask
                            trigger.CurrentMask = 0;
                        }
                    }
                }
            }
        }

        private void ExecuteCompositeCommand(CompositeTriggerState trigger)
        {
            int traceId = NexusTrace.BeginEvent(TraceEventType.Command, trigger.CommandType.Name, ExecutionMode.Sequential);
            object command = null;
            try
            {
                command = _poolManager.GetCommand(trigger.CommandType);
                _container.Inject(command);

                if (command is ICommand syncCmd)
                {
                    syncCmd.Execute();
                }
                else if (command is IAsyncCommand asyncCmd)
                {
                    _ = asyncCmd.ExecuteAsync(_context.LifetimeToken);
                }
                NexusTrace.EndEvent(traceId, TraceStatus.OK);
            }
            catch (Exception ex)
            {
                NexusTrace.EndEvent(traceId, TraceStatus.Failed);
                int retry = 0;
                HandleCommandErrorWithDecision(ex, trigger.CommandType, null, ref retry);
            }
            finally
            {
                if (command != null)
                {
                    _poolManager.ReturnCommand(trigger.CommandType, command);
                }
            }
        }

        private void BroadcastCrossContext<T>(T signal, string scopeTag) where T : struct
        {
            // Find other contexts through NexusRuntime
            var contexts = NexusRuntime.ActiveContexts;
            for (int i = 0; i < contexts.Count; i++)
            {
                var targetCtx = contexts[i];
                if (targetCtx == _context) continue; // Skip self

                // Check scope tag
                if (!string.IsNullOrEmpty(scopeTag))
                {
                    if (targetCtx is Context concreteCtx && concreteCtx.ScopeTag == scopeTag)
                    {
                        concreteCtx.SignalBusInternal.FireCrossContext(signal);
                    }
                }
                else
                {
                    if (targetCtx is Context concreteCtx)
                    {
                        concreteCtx.SignalBusInternal.FireCrossContext(signal);
                    }
                }
            }
        }

        public void FireCrossContext<T>(T signal) where T : struct
        {
            FireInternal(signal, isCrossContextSource: true);
        }

        private RecoveryAction HandleCommandErrorWithDecision(Exception ex, Type commandType, object signal, ref int retryCount)
        {
            if (ex is NexusReentrancyException || ex is NexusAsyncOverflowException || 
                (ex.InnerException != null && (ex.InnerException is NexusReentrancyException || ex.InnerException is NexusAsyncOverflowException)))
            {
                throw ex;
            }

            var failedSignal = new CommandFailedSignal(ex, commandType, signal);
            
            if (signal is CommandFailedSignal)
            {
                UnityEngine.Debug.LogException(ex);
                return RecoveryAction.Abort;
            }

            UnityEngine.Debug.LogError($"[Nexus] Command {commandType.Name} failed: {ex.Message}\n{ex.StackTrace}");
            
            if (_container.IsRegistered(typeof(IRecoveryStrategy)))
            {
                try
                {
                    var strategy = _container.Resolve<IRecoveryStrategy>();
                    var ctx = new CommandFailureContext(ex, commandType, signal, retryCount);
                    var decision = strategy.OnCommandFailed(ctx);
                    
                    if (decision.Action == RecoveryAction.Skip)
                    {
                        return RecoveryAction.Skip;
                    }
                    if (decision.Action == RecoveryAction.Abort)
                    {
                        throw new InvalidOperationException("Execution aborted by recovery strategy.", ex);
                    }
                    if (decision.Action == RecoveryAction.Fallback)
                    {
                        if (decision.FallbackCommandType != null)
                        {
                            ExecuteCommand(new CommandHandlerInfo(decision.FallbackCommandType, ExecutionMode.Sequential, 0, false), signal);
                        }
                        return RecoveryAction.Fallback;
                    }
                    if (decision.Action == RecoveryAction.Retry)
                    {
                        if (retryCount >= decision.MaxRetries)
                        {
                            UnityEngine.Debug.LogWarning($"[Nexus] Retry limit of {decision.MaxRetries} reached. Forcing Abort.");
                            throw new InvalidOperationException($"Retry limit reached for command {commandType.Name}.", ex);
                        }
                        return RecoveryAction.Retry;
                    }
                }
                catch (Exception strategyEx) when (!(strategyEx is InvalidOperationException && strategyEx.InnerException == ex))
                {
                    UnityEngine.Debug.LogError($"[Nexus] Error recovery strategy failed: {strategyEx.Message}");
                }
            }

            Fire(failedSignal);
            return RecoveryAction.Skip;
        }

        private async ValueTask<RecoveryAction> HandleCommandErrorWithDecisionAsync(Exception ex, Type commandType, object signal, int retryCount, CancellationToken ct)
        {
            if (ex is NexusReentrancyException || ex is NexusAsyncOverflowException || 
                (ex.InnerException != null && (ex.InnerException is NexusReentrancyException || ex.InnerException is NexusAsyncOverflowException)))
            {
                throw ex;
            }

            var failedSignal = new CommandFailedSignal(ex, commandType, signal);
            
            if (signal is CommandFailedSignal)
            {
                UnityEngine.Debug.LogException(ex);
                return RecoveryAction.Abort;
            }

            UnityEngine.Debug.LogError($"[Nexus] Command {commandType.Name} failed: {ex.Message}\n{ex.StackTrace}");
            
            if (_container.IsRegistered(typeof(IRecoveryStrategy)))
            {
                try
                {
                    var strategy = _container.Resolve<IRecoveryStrategy>();
                    var ctx = new CommandFailureContext(ex, commandType, signal, retryCount);
                    var decision = strategy.OnCommandFailed(ctx);
                    
                    if (decision.Action == RecoveryAction.Skip)
                    {
                        return RecoveryAction.Skip;
                    }
                    if (decision.Action == RecoveryAction.Abort)
                    {
                        throw new InvalidOperationException("Execution aborted by recovery strategy.", ex);
                    }
                    if (decision.Action == RecoveryAction.Fallback)
                    {
                        if (decision.FallbackCommandType != null)
                        {
                            var isAsync = typeof(IAsyncCommand).IsAssignableFrom(decision.FallbackCommandType);
                            if (isAsync)
                            {
                                await ExecuteCommandAsync(new CommandHandlerInfo(decision.FallbackCommandType, ExecutionMode.Sequential, 0, true), signal, ct);
                            }
                            else
                            {
                                ExecuteCommand(new CommandHandlerInfo(decision.FallbackCommandType, ExecutionMode.Sequential, 0, false), signal);
                            }
                        }
                        return RecoveryAction.Fallback;
                    }
                    if (decision.Action == RecoveryAction.Retry)
                    {
                        if (retryCount >= decision.MaxRetries)
                        {
                            UnityEngine.Debug.LogWarning($"[Nexus] Retry limit of {decision.MaxRetries} reached. Forcing Abort.");
                            throw new InvalidOperationException($"Retry limit reached for command {commandType.Name}.", ex);
                        }
                        return RecoveryAction.Retry;
                    }
                }
                catch (Exception strategyEx) when (!(strategyEx is InvalidOperationException && strategyEx.InnerException == ex))
                {
                    UnityEngine.Debug.LogError($"[Nexus] Error recovery strategy failed: {strategyEx.Message}");
                }
            }

            Fire(failedSignal);
            return RecoveryAction.Skip;
        }

        private void ExecuteWithDecorators(object command, Action next)
        {
            if (_context is Context ctx && ctx.Plugins.Count > 0)
            {
                Action current = next;
                for (int i = ctx.Plugins.Count - 1; i >= 0; i--)
                {
                    var decorators = ctx.Plugins[i].context.Decorators;
                    for (int j = decorators.Count - 1; j >= 0; j--)
                    {
                        var d = decorators[j];
                        var prev = current;
                        current = () => d.DecorateExecute(command, prev);
                    }
                }
                current();
            }
            else
            {
                next();
            }
        }

        private async ValueTask ExecuteWithDecoratorsAsync(object command, Func<ValueTask> next)
        {
            if (_context is Context ctx && ctx.Plugins.Count > 0)
            {
                Func<ValueTask> current = next;
                for (int i = ctx.Plugins.Count - 1; i >= 0; i--)
                {
                    var decorators = ctx.Plugins[i].context.Decorators;
                    for (int j = decorators.Count - 1; j >= 0; j--)
                    {
                        var d = decorators[j];
                        var prev = current;
                        current = async () => await d.DecorateExecuteAsync(command, prev);
                    }
                }
                await current();
            }
            else
            {
                await next();
            }
        }

        public void Dispose()
        {
            _subscriptions.Clear();
            _commandHandlers.Clear();
            _compositeTriggersBySignal.Clear();
            _allCompositeTriggers.Clear();
        }
    }

    // Concrete signal subscription implementations
    public class SignalSubscription<T> : ISignalSubscription where T : struct
    {
        private readonly Action<T> _handler;
        private readonly Action _onDispose;
        public bool IsActive { get; private set; } = true;
        public CancellationToken Lifetime { get; }
        private CancellationTokenRegistration _registration;

        public SignalSubscription(Action<T> handler, CancellationToken ct, Action onDispose)
        {
            _handler = handler;
            Lifetime = ct;
            _onDispose = onDispose;
            _registration = ct.Register(Dispose);
        }

        public void Invoke(T signal)
        {
            if (IsActive && !Lifetime.IsCancellationRequested)
            {
                _handler(signal);
            }
        }

        public void Dispose()
        {
            if (!IsActive) return;
            IsActive = false;
            _registration.Dispose();
            _onDispose?.Invoke();
        }
    }

    public class AsyncSignalSubscription<T> : ISignalSubscription where T : struct
    {
        private readonly Func<T, CancellationToken, ValueTask> _handler;
        private readonly Action _onDispose;
        public bool IsActive { get; private set; } = true;
        public CancellationToken Lifetime { get; }
        private CancellationTokenRegistration _registration;

        public AsyncSignalSubscription(Func<T, CancellationToken, ValueTask> handler, CancellationToken ct, Action onDispose)
        {
            _handler = handler;
            Lifetime = ct;
            _onDispose = onDispose;
            _registration = ct.Register(Dispose);
        }

        public async ValueTask InvokeAsync(T signal, CancellationToken ct)
        {
            if (IsActive && !Lifetime.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                await _handler(signal, ct);
            }
        }

        public void Dispose()
        {
            if (!IsActive) return;
            IsActive = false;
            _registration.Dispose();
            _onDispose?.Invoke();
        }
    }
}
