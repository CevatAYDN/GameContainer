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
        Task ChangeStateAsync<TState>(object args = null) where TState : class, IGameState;
        Task ChangeStateAsync(Type stateType, object args = null);
        Task ChangeStateAsync(Type stateType, CancellationToken ct, object args = null);
    }

    [Preserve]
    public class GameStateMachine : IGameStateMachine, ITickable, IDisposable
    {
        private readonly Dictionary<Type, IGameState> _states = new();
        private IGameState _currentState;
        private CancellationTokenSource _stateCts;

        public IGameState CurrentState => _currentState;

        public void RegisterState<TState>(TState state) where TState : class, IGameState
        {
            if (state == null) return;
            _states[typeof(TState)] = state;
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
                NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.LogError($"[GameStateMachine] State {stateType.Name} is not registered!");
                return;
            }

            if (_currentState == nextState) return;

            _stateCts?.Cancel();
            _stateCts?.Dispose();
            _stateCts = ct != CancellationToken.None 
                ? CancellationTokenSource.CreateLinkedTokenSource(ct) 
                : new CancellationTokenSource();

            var token = _stateCts.Token;
            token.ThrowIfCancellationRequested();

            if (_currentState != null)
            {
                try
                {
                    await _currentState.OnExitAsync(token);
                }
                catch (Exception ex)
                {
                    NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.LogException(ex);
                }
            }

            _currentState = nextState;

            try
            {
                await _currentState.OnEnterAsync(args, token);
            }
            catch (Exception ex)
            {
                NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.LogException(ex);
                // Fallback to null state to avoid remaining in a corrupted/failed state
                _currentState = null;
            }
        }

        public void Tick(float deltaTime)
        {
            _currentState?.OnTick(deltaTime);
        }

        public void Dispose()
        {
            _stateCts?.Cancel();
            _stateCts?.Dispose();
            _states.Clear();
            _currentState = null;
        }
    }
}
