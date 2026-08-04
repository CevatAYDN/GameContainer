using System;
using Nexus.Core;

namespace Nexus.Demo
{
    /// <summary>Types of UI events in the demo.</summary>
    public enum DemoUISignalType
    {
        ShowMainMenu,
        ShowGameplayHUD,
        ShowGameOver,
        ShowShop,
        UpdateCurrency,
        PlayAdRequested,
        PurchaseRequested,
        BuyVirtualCurrency
    }

    /// <summary>UI signal for screen transitions and updates.</summary>
    public readonly struct DemoUISignal
    {
        public readonly DemoUISignalType Type;
        public readonly string ScreenName;
        public readonly string Currency;
        public readonly int Amount;
        public readonly int Cost;
        public readonly string ProductId;

        public DemoUISignal(DemoUISignalType type, string screenName = null, int amount = 0, string productId = null, string currency = null, int cost = 0)
        {
            Type = type;
            ScreenName = screenName;
            Amount = amount;
            ProductId = productId;
            Currency = currency;
            Cost = cost;
        }

        public static DemoUISignal ShowMainMenu() => new(DemoUISignalType.ShowMainMenu);
        public static DemoUISignal ShowGameplayHUD() => new(DemoUISignalType.ShowGameplayHUD);
        public static DemoUISignal ShowGameOver() => new(DemoUISignalType.ShowGameOver);
        public static DemoUISignal ShowShop() => new(DemoUISignalType.ShowShop);
        public static DemoUISignal UpdateCurrency() => new(DemoUISignalType.UpdateCurrency);
        public static DemoUISignal PlayAdRequested() => new(DemoUISignalType.PlayAdRequested);
        public static DemoUISignal PurchaseRequested(string productId) => new(DemoUISignalType.PurchaseRequested, productId: productId);
        public static DemoUISignal BuyVirtualCurrency(string currency, int amount, int cost) => new(DemoUISignalType.BuyVirtualCurrency, currency: currency, amount: amount, cost: cost);
    }
}