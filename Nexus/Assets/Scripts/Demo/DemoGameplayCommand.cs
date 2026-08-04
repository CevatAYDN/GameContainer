using System;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;

namespace Nexus.Demo
{
    /// <summary>Handles gameplay signals and updates models/services.</summary>
    public class DemoGameplayCommand : ICommand<DemoGameplaySignal>
    {
        [Inject] private IDemoGameplayModel _gameplayModel;
        [Inject] private IDemoUIModel _uiModel;
        [Inject] private EconomyService _economy;
        [Inject] private ProgressionService _progression;
        [Inject] private FeedbackService _feedback;
        [Inject] private ISignalBus _signalBus;

        public void Execute(DemoGameplaySignal signal)
        {
            switch (signal.Type)
            {
                case DemoGameplaySignalType.GameStarted:
                    _gameplayModel.InitializeDemo();
                    _signalBus.Fire(DemoUISignal.ShowGameplayHUD());
                    break;

                case DemoGameplaySignalType.EnemyKilled:
                    _gameplayModel.OnEnemyKilled(signal.Amount);
                    _economy.Earn("Coins", signal.Amount, "enemy_kill");
                    _feedback.Play(FeedbackPreset.CoinCollect);
                    _signalBus.Fire(DemoUISignal.UpdateCurrency());
                    break;

                case DemoGameplaySignalType.CoinCollected:
                    _gameplayModel.OnCoinCollected(signal.Amount);
                    _economy.Earn("Coins", signal.Amount, "coin_pickup");
                    _signalBus.Fire(DemoUISignal.UpdateCurrency());
                    break;

                case DemoGameplaySignalType.LevelCompleted:
                    _gameplayModel.OnLevelCompleted();
                    _progression.CompleteCurrentLevel();
                    _uiModel.UpdateHighScore(_gameplayModel.CurrentLevel.Value);
                    _feedback.Play(FeedbackPreset.SuccessFanfare);
                    break;

                case DemoGameplaySignalType.GameOver:
                    _gameplayModel.OnGameOver(signal.Payload);
                    _signalBus.Fire(DemoUISignal.ShowGameOver());
                    _feedback.Play(FeedbackPreset.ErrorFailure);
                    break;
            }
        }
    }
}