using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace Nexus.Demo
{
    /// <summary>Gameplay model interface for DI.</summary>
    public interface IDemoGameplayModel
    {
        ObservableProperty<int> CurrentLevel { get; }
        ObservableProperty<int> EnemiesKilled { get; }
        ObservableProperty<int> CoinsCollectedThisRun { get; }
        ObservableProperty<bool> IsGameActive { get; }

        void InitializeDemo();
        void OnEnemyKilled(int coinReward);
        void OnCoinCollected(int amount);
        void OnLevelCompleted();
        void OnGameOver(string reason);
    }

    /// <summary>Gameplay state model - reactive via ObservableProperty.</summary>
    public class DemoGameplayModel : IDemoGameplayModel, IReactiveModel
    {
        public ValueTask OnBind(CancellationToken ct) => default;

        public ObservableProperty<int> CurrentLevel { get; } = new(1);
        public ObservableProperty<int> EnemiesKilled { get; } = new(0);
        public ObservableProperty<int> CoinsCollectedThisRun { get; } = new(0);
        public ObservableProperty<bool> IsGameActive { get; } = new(false);

        public void InitializeDemo()
        {
            CurrentLevel.Value = 1;
            EnemiesKilled.Value = 0;
            CoinsCollectedThisRun.Value = 0;
            IsGameActive.Value = true;
        }

        public void OnEnemyKilled(int coinReward)
        {
            if (!IsGameActive.Value) return;
            EnemiesKilled.Value++;
            CoinsCollectedThisRun.Value += coinReward;
        }

        public void OnCoinCollected(int amount)
        {
            if (!IsGameActive.Value) return;
            CoinsCollectedThisRun.Value += amount;
        }

        public void OnLevelCompleted()
        {
            if (!IsGameActive.Value) return;
            CurrentLevel.Value++;
            EnemiesKilled.Value = 0;
        }

        public void OnGameOver(string reason)
        {
            IsGameActive.Value = false;
        }
    }
}