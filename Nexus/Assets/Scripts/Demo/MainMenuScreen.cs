using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Nexus.Demo
{
    /// <summary>Main menu screen view.</summary>
    [Mediator(typeof(MainMenuMediator))]
    public class MainMenuScreen : View
    {
        [SerializeField] private Button playButton;
        [SerializeField] private Button shopButton;
        [SerializeField] private Button settingsButton;
        [SerializeField] private Text coinsText;
        [SerializeField] private Text gemsText;
        [SerializeField] private Text highScoreText;

        public Button PlayButton => playButton;
        public Button ShopButton => shopButton;
        public Button SettingsButton => settingsButton;

        public void UpdateCurrency(int coins, int gems)
        {
            if (coinsText) coinsText.text = $"Coins: {coins}";
            if (gemsText) gemsText.text = $"Gems: {gems}";
        }

        public void UpdateHighScore(int score)
        {
            if (highScoreText) highScoreText.text = $"High Score: Level {score}";
        }
    }

    /// <summary>Main menu mediator - fires signals for button actions and binds model to view.</summary>
    public class MainMenuMediator : Mediator<MainMenuScreen>
    {
        [Inject] private IDemoUIModel _uiModel;

        protected override void OnBind()
        {
            base.OnBind();

            // Initial UI update
            View.UpdateCurrency(_uiModel.TotalCoins.Value, _uiModel.TotalGems.Value);
            View.UpdateHighScore(_uiModel.HighScore.Value);

            // Subscribe to model changes (auto-cleaned on Unbind)
            TrackObservable(_uiModel.TotalCoins, (_, v) => View.UpdateCurrency(v, _uiModel.TotalGems.Value));
            TrackObservable(_uiModel.TotalGems, (_, v) => View.UpdateCurrency(_uiModel.TotalCoins.Value, v));
            TrackObservable(_uiModel.HighScore, (_, v) => View.UpdateHighScore(v));

            // Button handlers fire signals; commands own the logic (single path).
            View.PlayButton.onClick.AddListener(() => SignalBus.Fire(DemoGameplaySignal.GameStarted()));
            View.ShopButton.onClick.AddListener(() => SignalBus.Fire(DemoUISignal.ShowShop()));
        }

        protected override void OnUnbind()
        {
            View.PlayButton.onClick.RemoveAllListeners();
            View.ShopButton.onClick.RemoveAllListeners();
            // Note: SettingsButton currently has no listener (no settings screen yet) —
            // it stays wired for future use via its public getter.
            base.OnUnbind();
        }
    }
}