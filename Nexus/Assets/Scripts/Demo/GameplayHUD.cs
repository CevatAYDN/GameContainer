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

        private UnityEngine.Events.UnityAction _killEnemyClickHandler;
        private UnityEngine.Events.UnityAction _collectCoinClickHandler;
        private UnityEngine.Events.UnityAction _completeLevelClickHandler;
        private UnityEngine.Events.UnityAction _gameOverClickHandler;
        private UnityEngine.Events.UnityAction _pauseClickHandler;

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
            _killEnemyClickHandler ??= () => SignalBus.Fire(DemoGameplaySignal.EnemyKilled(10));
            _collectCoinClickHandler ??= () => SignalBus.Fire(DemoGameplaySignal.CoinCollected(5));
            _completeLevelClickHandler ??= () => SignalBus.Fire(DemoGameplaySignal.LevelCompleted(_gameplayModel.CurrentLevel.Value));
            _gameOverClickHandler ??= () => SignalBus.Fire(DemoGameplaySignal.GameOver("test"));
            _pauseClickHandler ??= () => SignalBus.Fire(DemoUISignal.ShowMainMenu());
            View.KillEnemyButton.onClick.AddListener(_killEnemyClickHandler);
            View.CollectCoinButton.onClick.AddListener(_collectCoinClickHandler);
            View.CompleteLevelButton.onClick.AddListener(_completeLevelClickHandler);
            View.GameOverButton.onClick.AddListener(_gameOverClickHandler);
            View.PauseButton.onClick.AddListener(_pauseClickHandler);
        }

        protected override void OnUnbind()
        {
            if (_killEnemyClickHandler != null) View.KillEnemyButton.onClick.RemoveListener(_killEnemyClickHandler);
            if (_collectCoinClickHandler != null) View.CollectCoinButton.onClick.RemoveListener(_collectCoinClickHandler);
            if (_completeLevelClickHandler != null) View.CompleteLevelButton.onClick.RemoveListener(_completeLevelClickHandler);
            if (_gameOverClickHandler != null) View.GameOverButton.onClick.RemoveListener(_gameOverClickHandler);
            if (_pauseClickHandler != null) View.PauseButton.onClick.RemoveListener(_pauseClickHandler);
            base.OnUnbind();
        }
    }
}
