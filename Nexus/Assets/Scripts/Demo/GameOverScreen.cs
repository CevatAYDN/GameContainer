using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Nexus.Demo
{
    /// <summary>Game over screen view.</summary>
    [Mediator(typeof(GameOverMediator))]
    public class GameOverScreen : ScreenView
    {
        [SerializeField] private Text finalLevelText;
        [SerializeField] private Text coinsEarnedText;
        [SerializeField] private Text enemiesKilledText;
        [SerializeField] private Button retryButton;
        [SerializeField] private Button mainMenuButton;
        [SerializeField] private Button watchAdButton;

        public Button RetryButton => retryButton;
        public Button MainMenuButton => mainMenuButton;
        public Button WatchAdButton => watchAdButton;

        public void SetStats(int level, int coins, int enemies)
        {
            if (finalLevelText) finalLevelText.text = $"Reached Level: {level}";
            if (coinsEarnedText) coinsEarnedText.text = $"Coins Earned: {coins}";
            if (enemiesKilledText) enemiesKilledText.text = $"Enemies Defeated: {enemies}";
        }
    }

    /// <summary>Game over mediator - fires signals for retry, menu, and ad watching.</summary>
    public class GameOverMediator : Mediator<GameOverScreen>
    {
        [Inject] private IDemoGameplayModel _gameplayModel;

        private UnityEngine.Events.UnityAction _retryClickHandler;
        private UnityEngine.Events.UnityAction _mainMenuClickHandler;
        private UnityEngine.Events.UnityAction _watchAdClickHandler;

        protected override void OnBind()
        {
            base.OnBind();

            View.SetStats(_gameplayModel.CurrentLevel.Value, _gameplayModel.CoinsCollectedThisRun.Value, _gameplayModel.EnemiesKilled.Value);

            _retryClickHandler ??= () => SignalBus.Fire(DemoGameplaySignal.GameStarted());
            _mainMenuClickHandler ??= () => SignalBus.Fire(DemoUISignal.ShowMainMenu());
            _watchAdClickHandler ??= () => SignalBus.Fire(DemoUISignal.PlayAdRequested());
            View.RetryButton.onClick.AddListener(_retryClickHandler);
            View.MainMenuButton.onClick.AddListener(_mainMenuClickHandler);
            View.WatchAdButton.onClick.AddListener(_watchAdClickHandler);
        }

        protected override void OnUnbind()
        {
            if (_retryClickHandler != null) View.RetryButton.onClick.RemoveListener(_retryClickHandler);
            if (_mainMenuClickHandler != null) View.MainMenuButton.onClick.RemoveListener(_mainMenuClickHandler);
            if (_watchAdClickHandler != null) View.WatchAdButton.onClick.RemoveListener(_watchAdClickHandler);
            base.OnUnbind();
        }
    }
}
