using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.FSM
{
    public interface IGameState
    {
        ValueTask OnEnterAsync(object args, CancellationToken ct);
        ValueTask OnExitAsync(CancellationToken ct);
        void OnTick(float deltaTime);
    }

    public interface IGameStateMachine
    {
        IGameState CurrentState { get; }
        void RegisterState<TState>(TState state) where TState : class, IGameState;

        /// <summary>
        /// Registers the fallback state entered when a state's OnEnterAsync throws.
        /// The state must also be registered via <see cref="RegisterState{TState}"/>.
        /// Consumers decide their own error state; the framework never hardcodes a game type.
        /// If no error state is set, the machine falls back to a null (no) state on failure.
        /// The originating exception is passed to the error state's OnEnterAsync as its args.
        /// </summary>
        void SetErrorState<TState>() where TState : class, IGameState;

        Task ChangeStateAsync<TState>(object args = null) where TState : class, IGameState;
        Task ChangeStateAsync(Type stateType, object args = null);
        Task ChangeStateAsync(Type stateType, CancellationToken ct, object args = null);
    }

    /// <summary>Outcome of a state transition, recorded in the machine's history ring buffer.</summary>
    public enum StateTransitionStatus
    {
        /// <summary>The transition committed and the target state was entered.</summary>
        Success,
        /// <summary>A newer transition preempted this one before it could commit.</summary>
        Superseded,
        /// <summary>The transition threw and the machine fell back to its error state (or none).</summary>
        Failed
    }

    /// <summary>
    /// Immutable snapshot of a single state transition attempt.
    /// Held in <see cref="GameStateMachine.TransitionHistoryCapacity"/>-sized ring buffer and
    /// broadcast via <see cref="GameStateMachine.OnStateChanged"/>.
    /// </summary>
    [Preserve]
    public readonly struct StateTransitionRecord
    {
        /// <summary>Wall-clock timestamp (<c>Time.realtimeSinceStartupAsDouble</c>) at completion.</summary>
        public readonly double Timestamp;
        /// <summary>Type name of the state being exited (null on the first transition).</summary>
        public readonly string FromState;
        /// <summary>Type name of the target state (null if failure fell back to a null state).</summary>
        public readonly string ToState;
        /// <summary>Type name of the transition arguments (never <c>ToString()</c> — cheap and side-effect-free).</summary>
        public readonly string ArgsSummary;
        /// <summary>How the transition ended.</summary>
        public readonly StateTransitionStatus Status;
        /// <summary>Elapsed time of the transition in milliseconds.</summary>
        public readonly double DurationMs;

        public StateTransitionRecord(double timestamp, string fromState, string toState,
            string argsSummary, StateTransitionStatus status, double durationMs)
        {
            Timestamp = timestamp;
            FromState = fromState;
            ToState = toState;
            ArgsSummary = argsSummary;
            Status = status;
            DurationMs = durationMs;
        }
    }

    [Preserve]
    public class GameStateMachine : IGameStateMachine, ITickable, IDisposable
    {
        /// <summary>Capacity of the transition history ring buffer.</summary>
        public const int TransitionHistoryCapacity = 32;

        private readonly Dictionary<Type, IGameState> _states = new();
        // _states is touched from the main thread (RegisterState) and from async transition
        // continuations (state lookups); a plain Dictionary is not safe for concurrent
        // read/write, so every access goes through this lock.
        private readonly object _statesLock = new();
        // EKSİK-5 fix: volatile ensures Tick() (Unity main thread) always observes the
        // latest state written by an async transition continuation — prevents torn reads
        // on platforms without strong memory ordering guarantees.
        // Read and written through Volatile/Interlocked: a transition claims the state it exits with one exchange, so
        // Dispose and a later transition never exit it again (issue #192).
        private IGameState _currentState;
        // The latest transition; the next one waits for it, so the one it supersedes exits what it entered first (issue #192).
        private Task _tail;
        private Type _errorStateType;
        private int _configurationVersion;
        /// <summary>Changes only when state registration or the error-state configuration changes.</summary>
        public int ConfigurationVersion => Volatile.Read(ref _configurationVersion);
        private CancellationTokenSource _stateCts;

        // Monotonic sequence used to serialize concurrent ChangeStateAsync calls.
        // A transition records its sequence on entry; after every await it bails out
        // if a NEWER transition has superseded it — so two transitions can never
        // both write _currentState or run OnEnterAsync at the same time.
        private long _transitionSequence;

        // Fixed-size transition history ring buffer. No per-transition allocation.
        private readonly StateTransitionRecord[] _transitionHistory = new StateTransitionRecord[TransitionHistoryCapacity];
        private readonly object _historyLock = new();
        private int _transitionHead;   // next write slot
        private int _transitionCount;  // records written so far (capped at capacity)

        /// <summary>The committed state; null between a state's exit and the next state's enter.</summary>
        public IGameState CurrentState => Volatile.Read(ref _currentState);

        /// <summary>Fires once per transition attempt with a full <see cref="StateTransitionRecord"/> snapshot.</summary>
        public event Action<StateTransitionRecord> OnStateChanged;

        /// <summary>Editor/introspection: state types registered via <see cref="RegisterState{TState}"/> (snapshot).</summary>
        public IReadOnlyCollection<Type> RegisteredStateTypes
        {
            get { lock (_statesLock) { return new List<Type>(_states.Keys); } }
        }

        /// <summary>Editor/introspection: the fallback state type, or null if none is set.</summary>
        public Type ErrorStateType => _errorStateType;

        /// <summary>Number of transitions recorded so far (max <see cref="TransitionHistoryCapacity"/>).</summary>
        public int TransitionCount => _transitionCount;

        /// <summary>Recent transitions in chronological order (allocates a list; editor/testing only).</summary>
        public IReadOnlyList<StateTransitionRecord> GetRecentTransitions()
        {
            lock (_historyLock)
            {
                var result = new List<StateTransitionRecord>(_transitionCount);
                for (int i = 0; i < _transitionCount; i++)
                {
                    int idx = (_transitionHead - _transitionCount + i + TransitionHistoryCapacity) % TransitionHistoryCapacity;
                    result.Add(_transitionHistory[idx]);
                }
                return result;
            }
        }

        public void RegisterState<TState>(TState state) where TState : class, IGameState
        {
            if (state == null) return;
            lock (_statesLock)
            {
                _states[typeof(TState)] = state;
                Interlocked.Increment(ref _configurationVersion);
            }
        }

        public void SetErrorState<TState>() where TState : class, IGameState
        {
            bool registered;
            lock (_statesLock) registered = _states.ContainsKey(typeof(TState));
            // An unregistered error state left the machine without a state after a failed enter, with only that failure
            // logged (issue #192).
            if (!registered)
                throw new InvalidOperationException($"[GameStateMachine] The error state {typeof(TState).Name} is not registered: call RegisterState before SetErrorState.");
            _errorStateType = typeof(TState);
            Interlocked.Increment(ref _configurationVersion);
        }

        public Task ChangeStateAsync<TState>(object args = null) where TState : class, IGameState
        {
            return ChangeStateAsync(typeof(TState), CancellationToken.None, args);
        }

        public Task ChangeStateAsync(Type stateType, object args = null)
        {
            return ChangeStateAsync(stateType, CancellationToken.None, args);
        }

        public async Task ChangeStateAsync(Type stateType, CancellationToken ct, object args = null)
        {
            IGameState nextState;
            lock (_statesLock)
            {
                _states.TryGetValue(stateType, out nextState);
            }
            if (nextState == null)
            {
                NexusRuntime.Logger?.LogError($"[GameStateMachine] State {stateType.Name} is not registered!");
                return;
            }

            if (CurrentState == nextState && _stateCts == null) return;

            string fromName = CurrentState?.GetType().Name;
            string argsSummary = args?.GetType().Name; // type name only — no ToString() surprises
            double startTime = UnityEngine.Time.realtimeSinceStartupAsDouble;

            // ATOMIC token swap. The old read-null-cancel-assign sequence
            // was not atomic: two concurrent ChangeStateAsync calls could interleave so
            // that the FIRST caller's `_stateCts = myCts` overwrote the SECOND caller's
            // freshly published token — the second transition's token was then lost and
            // could never be cancelled (a runaway transition that still commits state).
            // Interlocked.Exchange publishes the new token and grabs the old one in a
            // single indivisible step; cancelling the superseded source afterwards is
            // safe because the superseded flow disposes its OWN source in its finally.
            long mySequence = System.Threading.Interlocked.Increment(ref _transitionSequence);
            var myCts = ct != CancellationToken.None
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : new CancellationTokenSource();
            var superseded = System.Threading.Interlocked.Exchange(ref _stateCts, myCts);
            // R2026-FIX: Cancel() on a token source that the superseded flow's finally
            // block has ALREADY disposed throws ObjectDisposedException. The window is
            // narrow (Exchange → Cancel vs the old flow's finally → Dispose) but real.
            CancelAndLog(superseded);
            var token = myCts.Token;

            if (token.IsCancellationRequested)
            {
                // R2: never leave a disposed source published in _stateCts. This branch
                // skips the finally that normally CompareExchanges the slot back to null;
                // a stale disposed CTS would make Dispose()'s unguarded Cancel() throw
                // ObjectDisposedException and would permanently bypass the same-state
                // fast path (line 177). Clear our own slot before returning.
                Interlocked.CompareExchange(ref _stateCts, null, myCts);
                CancelAndLog(myCts);
                myCts.Dispose();
                return;
            }

            // Transitions run one after another: the one this supersedes finishes first, exiting the state it entered,
            // so no state is entered and exited at once and each entered state is exited once (issue #192).
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var previous = Interlocked.Exchange(ref _tail, done.Task);

            var status = StateTransitionStatus.Success;
            string toName = nextState.GetType().Name;

            try
            {
                if (previous != null) await previous;

                if (mySequence != Volatile.Read(ref _transitionSequence) || token.IsCancellationRequested)
                {
                    RecordTransition(fromName, toName, argsSummary, StateTransitionStatus.Superseded, startTime);
                    return;
                }

                // Claimed with one exchange: nothing else exits this state again (issue #192).
                var exiting = Interlocked.Exchange(ref _currentState, null);
                if (exiting != null)
                {
                    try
                    {
                        await exiting.OnExitAsync(token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Asked to stop: the state has left all the same, and nothing calls its exit again.
                    }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogException(ex);
                    }
                }

                // A newer transition may have superseded us while we awaited OnExitAsync, and a state that ignores
                // the token could run for an arbitrarily long time after being superseded: do not start the enter.
                // Volatile.Read: _transitionSequence is a long written via Interlocked on other threads.
                if (mySequence != Volatile.Read(ref _transitionSequence) || token.IsCancellationRequested)
                {
                    RecordTransition(fromName, toName, argsSummary, StateTransitionStatus.Superseded, startTime);
                    return;
                }

                try
                {
                    await nextState.OnEnterAsync(args, token);
                }
                catch (OperationCanceledException)
                {
                    // Superseded or cancelled mid-enter: this transition exits the state it entered (issue #192).
                    await ExitSupersededAsync(nextState);
                    RecordTransition(fromName, toName, argsSummary, StateTransitionStatus.Superseded, startTime);
                    return;
                }
                catch (Exception ex)
                {
                    // If a newer transition superseded us while we were inside OnEnterAsync, it owns the machine
                    // now: a stale error-state fallback here would run in its place.
                    if (mySequence != Volatile.Read(ref _transitionSequence))
                    {
                        RecordTransition(fromName, toName, argsSummary, StateTransitionStatus.Superseded, startTime);
                        return;
                    }

                    NexusRuntime.Logger?.LogException(ex);
                    status = StateTransitionStatus.Failed;
                    // Attempt to transition to the consumer-registered error state for safe recovery.
                    IGameState errorState = null;
                    if (_errorStateType != null)
                    {
                        lock (_statesLock) { _states.TryGetValue(_errorStateType, out errorState); }
                    }
                    if (errorState != null)
                    {
                        toName = errorState.GetType().Name;
                        try
                        {
                            await errorState.OnEnterAsync(ex, token);
                            if (mySequence == Volatile.Read(ref _transitionSequence))
                                Volatile.Write(ref _currentState, errorState);
                            else
                                await ExitSupersededAsync(errorState);
                        }
                        catch (OperationCanceledException)
                        {
                            // The machine stays without a state; the error state is exited like any superseded enter.
                            await ExitSupersededAsync(errorState);
                        }
                        catch (Exception innerEx)
                        {
                            NexusRuntime.Logger?.LogException(innerEx);
                        }
                    }
                    else
                    {
                        toName = null;
                    }
                }

                // A state that ignores cancellation may return from OnEnterAsync normally after a newer transition
                // superseded us: it is exited here, and the history does not claim a Success (issue #192).
                if (mySequence != Volatile.Read(ref _transitionSequence))
                {
                    if (status == StateTransitionStatus.Success) await ExitSupersededAsync(nextState);
                    RecordTransition(fromName, toName, argsSummary, StateTransitionStatus.Superseded, startTime);
                    return;
                }

                if (status == StateTransitionStatus.Success)
                {
                    Volatile.Write(ref _currentState, nextState);
                }

                // Committed — record the success (or failed-with-error-state) transition.
                RecordTransition(fromName, toName, argsSummary, status, startTime);
            }
            finally
            {
                // CompareExchange only clears the slot when it still holds OUR source —
                // the old sequence check-then-Exchange was check-then-act and could null
                // out a NEWER transition's freshly published token.
                Interlocked.CompareExchange(ref _stateCts, null, myCts);
                CancelAndLog(myCts);
                myCts.Dispose();
                done.TrySetResult(true);
            }
        }

        // A callback registered on a transition token can throw from Cancel(), after every callback has run. Unguarded,
        // that skipped the disposal and the queue completion (every later transition waited forever), faulted the
        // transition that superseded the running one, and made Dispose throw: it is logged instead (issue #321).
        private static void CancelAndLog(CancellationTokenSource cts)
        {
            try { cts?.Cancel(); }
            catch (ObjectDisposedException) { /* the flow that owns the source already completed and disposed it */ }
            catch (Exception ex) { NexusRuntime.Logger?.LogException(ex); }
        }

        // The transition token is cancelled by now, so the exit gets its own short timeout, as on Dispose (issue #192).
        private static async ValueTask ExitSupersededAsync(IGameState state)
        {
            try
            {
                await ExitWithTimeoutAsync(state);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogException(ex);
            }
        }

        /// <summary>
        /// Appends a transition record to the fixed-size ring buffer (written in place), fires
        /// <see cref="OnStateChanged"/>, and pushes a causal trace event (NEXUS_DEBUG-only).
        /// </summary>
        private void RecordTransition(string fromName, string toName, string argsSummary,
            StateTransitionStatus status, double startTime)
        {
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            var record = new StateTransitionRecord(now, fromName, toName, argsSummary, status, (now - startTime) * 1000.0);

            lock (_historyLock)
            {
                _transitionHistory[_transitionHead] = record;
                _transitionHead = (_transitionHead + 1) % TransitionHistoryCapacity;
                if (_transitionCount < TransitionHistoryCapacity) _transitionCount++;
            }

            // NexusTrace calls are now wrapped in the same NEXUS_DEBUG guard
            // used by every other trace site in the framework. Without the guard, every
            // production build paid two method-call overheads per state transition even
            // though the trace ring-buffer stubs compile to no-ops anyway.
#if NEXUS_DEBUG
            int traceId = NexusTrace.BeginEvent(TraceEventType.StateTransition, toName ?? fromName ?? "null");
            NexusTrace.EndEvent(traceId,
                status == StateTransitionStatus.Success ? TraceStatus.OK :
                status == StateTransitionStatus.Failed ? TraceStatus.Failed : TraceStatus.Cancelled);
#endif

            // Subscriber exceptions must not propagate up into ChangeStateAsync and cause
            // a spurious StateTransitionStatus.Failed. Log them independently.
            // One read: the last subscriber can leave between a null check and a second read (issue #205).
            var stateChanged = OnStateChanged;
            if (stateChanged != null)
            {
                var handlers = stateChanged.GetInvocationList();
                for (int i = 0; i < handlers.Length; i++)
                {
                    try { ((Action<StateTransitionRecord>)handlers[i])(record); }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogError(
                            $"[GameStateMachine] OnStateChanged subscriber '{handlers[i].Method?.Name}' threw: {ex.Message}\n{ex.StackTrace}");
                    }
                }
            }
        }

        public void Tick(float deltaTime)
        {
            var current = CurrentState;
            current?.OnTick(deltaTime);
        }

        public void Dispose()
        {
            System.Threading.Interlocked.Increment(ref _transitionSequence);
            var cts = Interlocked.Exchange(ref _stateCts, null);
            // R2 defense-in-depth: a superseded flow may have disposed its own source in
            // its finally before Dispose() drained the slot — Cancel() on it throws.
            // The transition that owns the source disposes it in its finally; disposing it here made that
            // finally's Cancel throw (issue #192).
            CancelAndLog(cts);

            // Best-effort exit hook: give the current state a chance to clean up
            // (fire-and-forget with error capture and a short timeout, mirroring the
            // ContextLifecycleOrchestrator sync-dispose pattern). A transition in flight finishes first and exits the
            // state it entered itself (issue #192); with none in flight this runs at once.
            var tail = Volatile.Read(ref _tail);
            if (tail == null || tail.IsCompleted)
            {
                var exiting = Interlocked.Exchange(ref _currentState, null);
                if (exiting != null)
                {
                    SafeAsyncRunner.Run(() => ExitWithTimeoutAsync(exiting),
                        $"[GameStateMachine] OnExitAsync during Dispose failed for state '{exiting.GetType().Name}'");
                }
            }
            else
            {
                SafeAsyncRunner.Run(() => ExitAfterAsync(tail), "[GameStateMachine] OnExitAsync during Dispose failed");
            }

            lock (_statesLock) { _states.Clear(); }
        }

        private async ValueTask ExitAfterAsync(Task transition)
        {
            await transition;
            var exiting = Interlocked.Exchange(ref _currentState, null);
            if (exiting != null) await ExitWithTimeoutAsync(exiting);
        }

        private static async ValueTask ExitWithTimeoutAsync(IGameState state)
        {
            // Short timeout so fire-and-forget cleanup can't hang indefinitely.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await state.OnExitAsync(cts.Token);
        }
    }
}
