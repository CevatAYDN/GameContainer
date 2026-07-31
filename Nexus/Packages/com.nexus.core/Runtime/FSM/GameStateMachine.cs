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
        private IGameState _currentState;
        private Type _errorStateType;
        private CancellationTokenSource _stateCts;

        // Monotonic sequence used to serialize concurrent ChangeStateAsync calls.
        // A transition records its sequence on entry; after every await it bails out
        // if a NEWER transition has superseded it — so two transitions can never
        // both write _currentState or run OnEnterAsync at the same time.
        private long _transitionSequence;

        // Fixed-size transition history ring buffer. No per-transition allocation.
        private readonly StateTransitionRecord[] _transitionHistory = new StateTransitionRecord[TransitionHistoryCapacity];
        private int _transitionHead;   // next write slot
        private int _transitionCount;  // records written so far (capped at capacity)

        public IGameState CurrentState => _currentState;

        /// <summary>Fires once per transition attempt with a full <see cref="StateTransitionRecord"/> snapshot.</summary>
        public event Action<StateTransitionRecord> OnStateChanged;

        /// <summary>Editor/introspection: state types registered via <see cref="RegisterState{TState}"/>.</summary>
        public IReadOnlyCollection<Type> RegisteredStateTypes => _states.Keys;

        /// <summary>Editor/introspection: the fallback state type, or null if none is set.</summary>
        public Type ErrorStateType => _errorStateType;

        /// <summary>Number of transitions recorded so far (max <see cref="TransitionHistoryCapacity"/>).</summary>
        public int TransitionCount => _transitionCount;

        /// <summary>Recent transitions in chronological order (allocates a list; editor/testing only).</summary>
        public IReadOnlyList<StateTransitionRecord> GetRecentTransitions()
        {
            var result = new List<StateTransitionRecord>(_transitionCount);
            for (int i = 0; i < _transitionCount; i++)
            {
                int idx = (_transitionHead - _transitionCount + i + TransitionHistoryCapacity) % TransitionHistoryCapacity;
                result.Add(_transitionHistory[idx]);
            }
            return result;
        }

        public void RegisterState<TState>(TState state) where TState : class, IGameState
        {
            if (state == null) return;
            _states[typeof(TState)] = state;
        }

        public void SetErrorState<TState>() where TState : class, IGameState
        {
            _errorStateType = typeof(TState);
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
            if (!_states.TryGetValue(stateType, out var nextState))
            {
                NexusRuntime.Logger?.LogError($"[GameStateMachine] State {stateType.Name} is not registered!");
                return;
            }

            if (_currentState == nextState) return;

            string fromName = _currentState?.GetType().Name;
            string argsSummary = args?.GetType().Name; // type name only — no ToString() surprises
            double startTime = UnityEngine.Time.realtimeSinceStartupAsDouble;

            // Preempt any in-flight transition. The superseded transition observes the
            // cancellation at its next await point and abandons its own transition
            // (see the sequence check after every await below). We deliberately do NOT
            // dispose its source here — the superseded flow disposes its OWN source in
            // its finally block, so a state still holding a reference to the old token
            // can never hit ObjectDisposedException.
            var superseded = _stateCts;
            _stateCts = null;
            superseded?.Cancel();

            long mySequence = ++_transitionSequence;
            var myCts = ct != CancellationToken.None
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : new CancellationTokenSource();
            _stateCts = myCts;
            var token = myCts.Token;

            if (token.IsCancellationRequested)
            {
                myCts.Cancel();
                myCts.Dispose();
                return;
            }

            var status = StateTransitionStatus.Success;
            string toName = nextState.GetType().Name;

            try
            {
                if (_currentState != null)
                {
                    try
                    {
                        await _currentState.OnExitAsync(token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Superseded or externally cancelled — abort without touching state.
                        status = StateTransitionStatus.Superseded;
                        RecordTransition(fromName, toName, argsSummary, status, startTime);
                        return;
                    }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogException(ex);
                    }
                }

                // A newer transition may have superseded us while we awaited OnExitAsync.
                if (mySequence != _transitionSequence)
                {
                    RecordTransition(fromName, toName, argsSummary, StateTransitionStatus.Superseded, startTime);
                    return;
                }

                _currentState = nextState;

                try
                {
                    await _currentState.OnEnterAsync(args, token);
                }
                catch (OperationCanceledException)
                {
                    // Superseded or externally cancelled mid-enter. _currentState already
                    // points at nextState; a superseding transition overwrites it itself.
                    RecordTransition(fromName, toName, argsSummary, StateTransitionStatus.Superseded, startTime);
                    return;
                }
                catch (Exception ex)
                {
                    // If a newer transition superseded us while we were inside OnEnterAsync,
                    // it owns the machine now — a stale error-state fallback here would
                    // clobber its committed _currentState. Abort silently.
                    if (mySequence != _transitionSequence)
                    {
                        RecordTransition(fromName, toName, argsSummary, StateTransitionStatus.Superseded, startTime);
                        return;
                    }

                    NexusRuntime.Logger?.LogException(ex);
                    status = StateTransitionStatus.Failed;
                    // Attempt to transition to the consumer-registered error state for safe recovery.
                    if (_errorStateType != null && _states.TryGetValue(_errorStateType, out var errorState))
                    {
                        _currentState = errorState;
                        toName = errorState.GetType().Name;
                        // Pass exception information to the error state.
                        try
                        {
                            await _currentState.OnEnterAsync(ex, token);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception innerEx)
                        {
                            NexusRuntime.Logger?.LogException(innerEx);
                        }
                    }
                    else
                    {
                        // Fallback to null state if no error state is registered.
                        _currentState = null;
                        toName = null;
                    }
                }

                // A state that ignores cancellation may return from OnEnterAsync normally
                // after a newer transition already superseded us mid-enter. The machine
                // state is correct (the newer transition owns _currentState), but our
                // history record and trace event must not claim a Success.
                if (mySequence != _transitionSequence)
                {
                    RecordTransition(fromName, toName, argsSummary, StateTransitionStatus.Superseded, startTime);
                    return;
                }

                // Committed — record the success (or failed-with-error-state) transition.
                RecordTransition(fromName, toName, argsSummary, status, startTime);
            }
            finally
            {
                // Only the newest transition clears the shared slot; superseded transitions
                // dispose their own source here, after all of their state code has returned.
                if (mySequence == _transitionSequence) _stateCts = null;
                myCts.Cancel();
                myCts.Dispose();
            }
        }

        /// <summary>
        /// Appends a transition record to the fixed-size ring buffer (no allocation), fires
        /// <see cref="OnStateChanged"/>, and pushes a causal trace event (NEXUS_DEBUG-only).
        /// </summary>
        private void RecordTransition(string fromName, string toName, string argsSummary,
            StateTransitionStatus status, double startTime)
        {
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            var record = new StateTransitionRecord(now, fromName, toName, argsSummary, status, (now - startTime) * 1000.0);

            _transitionHistory[_transitionHead] = record;
            _transitionHead = (_transitionHead + 1) % TransitionHistoryCapacity;
            if (_transitionCount < TransitionHistoryCapacity) _transitionCount++;

            // Causal tracing integration (compiled away without NEXUS_DEBUG).
            int traceId = NexusTrace.BeginEvent(TraceEventType.StateTransition, toName ?? fromName ?? "null");
            NexusTrace.EndEvent(traceId,
                status == StateTransitionStatus.Success ? TraceStatus.OK :
                status == StateTransitionStatus.Failed ? TraceStatus.Failed : TraceStatus.Cancelled);

            OnStateChanged?.Invoke(record);
        }

        public void Tick(float deltaTime)
        {
            _currentState?.OnTick(deltaTime);
        }

        public void Dispose()
        {
            // Invalidate any in-flight transition; it aborts at its next checkpoint and
            // disposes its own source in its finally block (CTS.Dispose is idempotent,
            // so double-disposal with the shared slot below is safe).
            _transitionSequence++;
            var cts = _stateCts;
            _stateCts = null;
            cts?.Cancel();
            cts?.Dispose();
            _states.Clear();
            _currentState = null;
        }
    }
}
