using System;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;

namespace Nexus.Demo
{
    /// <summary>Handles UI signals - screen transitions, ad/IAP requests.</summary>
    public class DemoUICommand : ICommand<DemoUISignal>
    {
        [Inject] private IDemoUIModel _uiModel;
#pragma warning disable CS0618 // WindowManager is kept for backward compatibility demo
        [Inject] private WindowManager _windowManager;
#pragma warning restore CS0618
        [Inject] private AdService _adService;
        [Inject] private IapService _iapService;
        [Inject] private EconomyService _economy;
        [Inject] private FeedbackService _feedback;
        [Inject] private ISignalBus _signalBus;

        public void Execute(DemoUISignal signal)
        {
            switch (signal.Type)
            {
                case DemoUISignalType.ShowMainMenu:
                    _uiModel.SetScreen("MainMenu");
                    _ = OpenWindowAsync("MainMenuScreen", UILayer.Screen);
                    break;

                case DemoUISignalType.ShowGameplayHUD:
                    _uiModel.SetScreen("GameplayHUD");
                    _ = OpenWindowAsync("GameplayHUD", UILayer.HUD);
                    break;

                case DemoUISignalType.ShowGameOver:
                    _uiModel.SetScreen("GameOver");
                    _ = OpenWindowAsync("GameOverScreen", UILayer.Popup);
                    break;

                case DemoUISignalType.ShowShop:
                    _uiModel.SetScreen("Shop");
                    _ = OpenWindowAsync("ShopScreen", UILayer.Screen);
                    break;

                case DemoUISignalType.UpdateCurrency:
                    RefreshCurrency();
                    break;

                case DemoUISignalType.PlayAdRequested:
                    HandleAdRequest();
                    break;

                case DemoUISignalType.PurchaseRequested:
                    HandlePurchaseRequest(signal.ProductId);
                    break;

                case DemoUISignalType.BuyVirtualCurrency:
                    HandleVirtualCurrencyPurchase(signal.Currency, signal.Amount, signal.Cost);
                    break;
            }
        }

        /// <summary>Opens a window on the specified layer, surfacing failures via the logger.</summary>
        private async System.Threading.Tasks.Task OpenWindowAsync(string windowName, UILayer layer)
        {
            try
            {
                await _windowManager.OpenWindowAsync(windowName, layer);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Demo] Failed to open window '{windowName}': {ex.Message}");
            }
        }

        private void HandleVirtualCurrencyPurchase(string currency, int amount, int cost)
        {
            if (string.IsNullOrEmpty(currency)) return;

            if (_economy.CanAfford("Gems", cost))
            {
                _economy.Spend("Gems", cost, $"shop_{currency.ToLower()}_{amount}");
                _economy.Earn(currency, amount, $"shop_buy_{currency.ToLower()}_{amount}");
                _signalBus.Fire(DemoUISignal.UpdateCurrency());
                _feedback.Play(FeedbackPreset.SuccessFanfare);
            }
            else
            {
                _feedback.Play(FeedbackPreset.ErrorFailure);
            }
        }

        /// <summary>Pulls the current balances from the economy (single source of truth) into the UI model.</summary>
        private void RefreshCurrency()
        {
            // Clamp to the int-based UI model instead of checked((int)...): an economy
            // balance past int.MaxValue must never throw OverflowException in the UI.
            _uiModel.TotalCoins.Value = (int)Math.Min(Math.Max(_economy.GetBalance("Coins"), 0L), int.MaxValue);
            _uiModel.TotalGems.Value = (int)Math.Min(Math.Max(_economy.GetBalance("Gems"), 0L), int.MaxValue);
        }

        private void HandleAdRequest()
        {
            if (_adService.IsInterstitialAvailable("gameover"))
            {
                _adService.ShowInterstitial("gameover", () =>
                {
                    // Reward player for watching ad
                    _economy.Earn("Coins", 50, "ad_reward");
                    _signalBus.Fire(DemoUISignal.UpdateCurrency());
                    _feedback.Play(FeedbackPreset.SuccessFanfare);
                });
            }
            else
            {
                _feedback.Play(FeedbackPreset.WarningAlert);
                Debug.Log("[Demo] Ad not ready - cooldown active");
            }
        }

        private void HandlePurchaseRequest(string productId)
        {
            if (string.IsNullOrEmpty(productId)) return;

            _iapService.PurchaseProduct(productId, (success, message) =>
            {
                if (success)
                {
                    _feedback.Play(FeedbackPreset.SuccessFanfare);
                    _signalBus.Fire(DemoUISignal.UpdateCurrency());
                }
                else
                {
                    _feedback.Play(FeedbackPreset.ErrorFailure);
                    Debug.Log($"[Demo] Purchase failed: {message}");
                }
            });
        }
    }
}