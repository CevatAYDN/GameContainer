using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Nexus.Demo
{
    /// <summary>Shop screen view - shows IAP products and virtual currency purchases.</summary>
    [Mediator(typeof(ShopMediator))]
    public class ShopScreen : View
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

        protected override void OnBind()
        {
            base.OnBind();

            View.UpdateCurrency(_uiModel.TotalCoins.Value, _uiModel.TotalGems.Value);
            TrackObservable(_uiModel.TotalCoins, (_, v) => View.UpdateCurrency(v, _uiModel.TotalGems.Value));
            TrackObservable(_uiModel.TotalGems, (_, v) => View.UpdateCurrency(_uiModel.TotalCoins.Value, v));

            // Virtual currency purchases (using in-game gems)
            View.BuyCoins100Button.onClick.AddListener(() => SignalBus.Fire(DemoUISignal.BuyVirtualCurrency("Coins", 100, 10)));
            View.BuyCoins500Button.onClick.AddListener(() => SignalBus.Fire(DemoUISignal.BuyVirtualCurrency("Coins", 500, 45)));
            View.BuyGems10Button.onClick.AddListener(() => SignalBus.Fire(DemoUISignal.BuyVirtualCurrency("Gems", 10, 50)));
            View.BuyGems50Button.onClick.AddListener(() => SignalBus.Fire(DemoUISignal.BuyVirtualCurrency("Gems", 50, 200)));

            // Real IAP purchase (mock)
            View.RemoveAdsButton.onClick.AddListener(() => SignalBus.Fire(DemoUISignal.PurchaseRequested("remove_ads")));

            View.CloseButton.onClick.AddListener(() => SignalBus.Fire(DemoUISignal.ShowMainMenu()));
        }

        protected override void OnUnbind()
        {
            View.BuyCoins100Button.onClick.RemoveAllListeners();
            View.BuyCoins500Button.onClick.RemoveAllListeners();
            View.BuyGems10Button.onClick.RemoveAllListeners();
            View.BuyGems50Button.onClick.RemoveAllListeners();
            View.RemoveAdsButton.onClick.RemoveAllListeners();
            View.CloseButton.onClick.RemoveAllListeners();
            base.OnUnbind();
        }
    }
}