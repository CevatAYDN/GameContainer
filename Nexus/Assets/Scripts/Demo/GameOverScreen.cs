using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Nexus.Demo
{
    /// <summary>Game over screen view.</summary>
    [Mediator(typeof(GameOverMediator))]
    public class GameOverScreen : View
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

        protected override void OnBind()
        {
            base.OnBind();

            View.SetStats(_gameplayModel.CurrentLevel.Value, _gameplayModel.CoinsCollectedThisRun.Value, _gameplayModel.EnemiesKilled.Value);

            View.RetryButton.onClick.AddListener(() => SignalBus.Fire(DemoGameplaySignal.GameStarted()));
            View.MainMenuButton.onClick.AddListener(() => SignalBus.Fire(DemoUISignal.ShowMainMenu()));
            View.WatchAdButton.onClick.AddListener(() => SignalBus.Fire(DemoUISignal.PlayAdRequested()));
        }

        protected override void OnUnbind()
        {
            View.RetryButton.onClick.RemoveAllListeners();
            View.MainMenuButton.onClick.RemoveAllListeners();
            View.WatchAdButton.onClick.RemoveAllListeners();
            base.OnUnbind();
        }
    }
}