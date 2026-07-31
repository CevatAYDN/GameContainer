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

    [Preserve]
    public class GameStateMachine : IGameStateMachine, ITickable, IDisposable
    {
        private readonly Dictionary<Type, IGameState> _states = new();
        private IGameState _currentState;
        private Type _errorStateType;
        private CancellationTokenSource _stateCts;

        // Monotonic sequence used to serialize concurrent ChangeStateAsync calls.
        // A transition records its sequence on entry; after every await it bails out
        // if a NEWER transition has superseded it — so two transitions can never
        // both write _currentState or run OnEnterAsync at the same time.
        private long _transitionSequence;

        public IGameState CurrentState => _currentState;

        /// <summary>Editor/introspection: state types registered via <see cref="RegisterState{TState}"/>.</summary>
        public IReadOnlyCollection<Type> RegisteredStateTypes => _states.Keys;

        /// <summary>Editor/introspection: the fallback state type, or null if none is set.</summary>
        public Type ErrorStateType => _errorStateType;

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
                        return;
                    }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogException(ex);
                    }
                }

                // A newer transition may have superseded us while we awaited OnExitAsync.
                if (mySequence != _transitionSequence) return;

                _currentState = nextState;

                try
                {
                    await _currentState.OnEnterAsync(args, token);
                }
                catch (OperationCanceledException)
                {
                    // Superseded or externally cancelled mid-enter. _currentState already
                    // points at nextState; a superseding transition overwrites it itself.
                    return;
                }
                catch (Exception ex)
                {
                    // If a newer transition superseded us while we were inside OnEnterAsync,
                    // it owns the machine now — a stale error-state fallback here would
                    // clobber its committed _currentState. Abort silently.
                    if (mySequence != _transitionSequence) return;

                    NexusRuntime.Logger?.LogException(ex);
                    // Attempt to transition to the consumer-registered error state for safe recovery.
                    if (_errorStateType != null && _states.TryGetValue(_errorStateType, out var errorState))
                    {
                        _currentState = errorState;
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
                    }
                }
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
