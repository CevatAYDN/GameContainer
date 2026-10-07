using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using UnityEngine;

namespace Nexus.Samples.CodeFirst
{
    public readonly struct ScoreAdded
    {
        public readonly int Amount;
        public ScoreAdded(int amount) => Amount = amount;
    }

    public sealed class ScoreModel : IReactiveModel
    {
        public ObservableProperty<int> Score { get; } = new(0);
        public ValueTask OnBind(CancellationToken ct) => default;
    }

    public sealed class AddScore : ICommand<ScoreAdded>
    {
        private readonly ScoreModel _model;
        public AddScore(ScoreModel model) => _model = model;
        public void Execute(ScoreAdded signal) => _model.Score.Value += signal.Amount;
    }

    /// <summary>Attach to an empty GameObject. No Root, scene asset or assembly scanning needed.</summary>
    public sealed class ScoreBootstrap : MonoBehaviour
    {
        private readonly CancellationTokenSource _startup = new();
        private Context _context;

        private async void Start()
        {
            try
            {
                _context = await ContextFactory.StartAsync("Gameplay", builder =>
                {
                    builder.BindReactiveModel<ScoreModel>();
                    builder.BindSignal<ScoreAdded>().To<AddScore>();
                }, ct: _startup.Token);
                _context.Prewarm<ScoreAdded>(4);
                _context.SignalBus.Fire(new ScoreAdded(1));
                Debug.Log($"Nexus score: {_context.Resolve<ScoreModel>().Score.Value}");
            }
            catch (OperationCanceledException) { } // Owner destroyed during startup.
            catch (Exception error) { Debug.LogException(error); }
        }

        private void OnDestroy()
        {
            try { _startup.Cancel(); }
            finally
            {
                try { _context?.Dispose(); }
                finally { _startup.Dispose(); }
            }
        }
    }
}
