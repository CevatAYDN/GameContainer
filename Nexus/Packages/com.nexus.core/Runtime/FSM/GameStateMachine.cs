using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
            return ChangeStateAsync(typeof(TState), args);
        }

        public async Task ChangeStateAsync(Type stateType, object args = null)
        {
            if (!_states.TryGetValue(stateType, out var nextState))
            {
                Debug.LogError($"[GameStateMachine] State {stateType.Name} is not registered!");
                return;
            }

            if (_currentState == nextState) return;

            _stateCts?.Cancel();
            _stateCts?.Dispose();
            _stateCts = new CancellationTokenSource();

            if (_currentState != null)
            {
                try
                {
                    await _currentState.OnExitAsync(_stateCts.Token);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }

            _currentState = nextState;

            try
            {
                await _currentState.OnEnterAsync(args, _stateCts.Token);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
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
