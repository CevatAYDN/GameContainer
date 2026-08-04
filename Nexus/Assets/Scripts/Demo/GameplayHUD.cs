using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Nexus.Demo
{
    /// <summary>Gameplay HUD view - shows during active gameplay.</summary>
    [Mediator(typeof(GameplayHUDMediator))]
    public class GameplayHUD : View
    {
        [SerializeField] private Text levelText;
        [SerializeField] private Text coinsText;
        [SerializeField] private Text enemiesKilledText;
        [SerializeField] private Button pauseButton;
        [SerializeField] private Button killEnemyButton; // For testing
        [SerializeField] private Button collectCoinButton; // For testing
        [SerializeField] private Button completeLevelButton; // For testing
        [SerializeField] private Button gameOverButton; // For testing

        public Button PauseButton => pauseButton;
        public Button KillEnemyButton => killEnemyButton;
        public Button CollectCoinButton => collectCoinButton;
        public Button CompleteLevelButton => completeLevelButton;
        public Button GameOverButton => gameOverButton;

        public void UpdateLevel(int level)
        {
            if (levelText) levelText.text = $"Level: {level}";
        }

        public void UpdateCurrency(int coins)
        {
            if (coinsText) coinsText.text = $"Coins: {coins}";
        }

        public void UpdateEnemiesKilled(int count)
        {
            if (enemiesKilledText) enemiesKilledText.text = $"Enemies: {count}";
        }
    }

    /// <summary>Gameplay HUD mediator - binds gameplay model to UI, fires signals on buttons.</summary>
    public class GameplayHUDMediator : Mediator<GameplayHUD>
    {
        [Inject] private IDemoGameplayModel _gameplayModel;
        [Inject] private IDemoUIModel _uiModel;

        protected override void OnBind()
        {
            base.OnBind();

            // Initial UI update
            View.UpdateLevel(_gameplayModel.CurrentLevel.Value);
            View.UpdateCurrency(_uiModel.TotalCoins.Value);
            View.UpdateEnemiesKilled(_gameplayModel.EnemiesKilled.Value);

            // Subscribe to model changes (auto-cleaned on Unbind)
            TrackObservable(_gameplayModel.CurrentLevel, (_, v) => View.UpdateLevel(v));
            TrackObservable(_uiModel.TotalCoins, (_, v) => View.UpdateCurrency(v));
            TrackObservable(_gameplayModel.EnemiesKilled, (_, v) => View.UpdateEnemiesKilled(v));

            // Test buttons fire signals; commands own the logic (single path).
            View.KillEnemyButton.onClick.AddListener(() => SignalBus.Fire(DemoGameplaySignal.EnemyKilled(10)));
            View.CollectCoinButton.onClick.AddListener(() => SignalBus.Fire(DemoGameplaySignal.CoinCollected(5)));
            View.CompleteLevelButton.onClick.AddListener(() => SignalBus.Fire(DemoGameplaySignal.LevelCompleted(_gameplayModel.CurrentLevel.Value)));
            View.GameOverButton.onClick.AddListener(() => SignalBus.Fire(DemoGameplaySignal.GameOver("test")));
            View.PauseButton.onClick.AddListener(() => SignalBus.Fire(DemoUISignal.ShowMainMenu()));
        }

        protected override void OnUnbind()
        {
            View.KillEnemyButton.onClick.RemoveAllListeners();
            View.CollectCoinButton.onClick.RemoveAllListeners();
            View.CompleteLevelButton.onClick.RemoveAllListeners();
            View.GameOverButton.onClick.RemoveAllListeners();
            View.PauseButton.onClick.RemoveAllListeners();
            base.OnUnbind();
        }
    }
}