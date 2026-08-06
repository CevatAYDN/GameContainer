using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Nexus.Demo
{
    /// <summary>Shop screen view - shows IAP products and virtual currency purchases.</summary>
    [Mediator(typeof(ShopMediator))]
    public class ShopScreen : ScreenView
    {
        [SerializeField] private Text coinsText;
        [SerializeField] private Text gemsText;
        [SerializeField] private Button buyCoins100Button;
        [SerializeField] private Button buyCoins500Button;
        [SerializeField] private Button buyGems10Button;
        [SerializeField] private Button buyGems50Button;
        [SerializeField] private Button removeAdsButton;
        [SerializeField] private Button closeButton;

        public Button BuyCoins100Button => buyCoins100Button;
        public Button BuyCoins500Button => buyCoins500Button;
        public Button BuyGems10Button => buyGems10Button;
        public Button BuyGems50Button => buyGems50Button;
        public Button RemoveAdsButton => removeAdsButton;
        public Button CloseButton => closeButton;

        public void UpdateCurrency(int coins, int gems)
        {
            if (coinsText) coinsText.text = $"Coins: {coins}";
            if (gemsText) gemsText.text = $"Gems: {gems}";
        }
    }

    /// <summary>Shop mediator - fires buy/purchase signals; commands own the economy logic.</summary>
    public class ShopMediator : Mediator<ShopScreen>
    {
        [Inject] private IDemoUIModel _uiModel;

        private UnityEngine.Events.UnityAction _buyCoins100ClickHandler;
        private UnityEngine.Events.UnityAction _buyCoins500ClickHandler;
        private UnityEngine.Events.UnityAction _buyGems10ClickHandler;
        private UnityEngine.Events.UnityAction _buyGems50ClickHandler;
        private UnityEngine.Events.UnityAction _removeAdsClickHandler;
        private UnityEngine.Events.UnityAction _closeClickHandler;

        protected override void OnBind()
        {
            base.OnBind();

            View.UpdateCurrency(_uiModel.TotalCoins.Value, _uiModel.TotalGems.Value);
            TrackObservable(_uiModel.TotalCoins, (_, v) => View.UpdateCurrency(v, _uiModel.TotalGems.Value));
            TrackObservable(_uiModel.TotalGems, (_, v) => View.UpdateCurrency(_uiModel.TotalCoins.Value, v));

            // Virtual currency purchases (using in-game gems)
            _buyCoins100ClickHandler ??= () => SignalBus.Fire(DemoUISignal.BuyVirtualCurrency("Coins", 100, 10));
            _buyCoins500ClickHandler ??= () => SignalBus.Fire(DemoUISignal.BuyVirtualCurrency("Coins", 500, 45));
            _buyGems10ClickHandler ??= () => SignalBus.Fire(DemoUISignal.BuyVirtualCurrency("Gems", 10, 50));
            _buyGems50ClickHandler ??= () => SignalBus.Fire(DemoUISignal.BuyVirtualCurrency("Gems", 50, 200));
            View.BuyCoins100Button.onClick.AddListener(_buyCoins100ClickHandler);
            View.BuyCoins500Button.onClick.AddListener(_buyCoins500ClickHandler);
            View.BuyGems10Button.onClick.AddListener(_buyGems10ClickHandler);
            View.BuyGems50Button.onClick.AddListener(_buyGems50ClickHandler);

            // Real IAP purchase (mock)
            _removeAdsClickHandler ??= () => SignalBus.Fire(DemoUISignal.PurchaseRequested("remove_ads"));
            View.RemoveAdsButton.onClick.AddListener(_removeAdsClickHandler);

            _closeClickHandler ??= () => SignalBus.Fire(DemoUISignal.ShowMainMenu());
            View.CloseButton.onClick.AddListener(_closeClickHandler);
        }

        protected override void OnUnbind()
        {
            if (_buyCoins100ClickHandler != null) View.BuyCoins100Button.onClick.RemoveListener(_buyCoins100ClickHandler);
            if (_buyCoins500ClickHandler != null) View.BuyCoins500Button.onClick.RemoveListener(_buyCoins500ClickHandler);
            if (_buyGems10ClickHandler != null) View.BuyGems10Button.onClick.RemoveListener(_buyGems10ClickHandler);
            if (_buyGems50ClickHandler != null) View.BuyGems50Button.onClick.RemoveListener(_buyGems50ClickHandler);
            if (_removeAdsClickHandler != null) View.RemoveAdsButton.onClick.RemoveListener(_removeAdsClickHandler);
            if (_closeClickHandler != null) View.CloseButton.onClick.RemoveListener(_closeClickHandler);
            base.OnUnbind();
        }
    }
}
