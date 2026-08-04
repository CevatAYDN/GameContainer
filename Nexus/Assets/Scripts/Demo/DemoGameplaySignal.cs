using System;
using Nexus.Core;

namespace Nexus.Demo
{
    /// <summary>Types of gameplay events in the demo.</summary>
    public enum DemoGameplaySignalType
    {
        GameStarted,
        EnemyKilled,
        CoinCollected,
        LevelCompleted,
        GameOver
    }

    /// <summary>
    /// Core gameplay signal - carries event type and optional payload.
    /// </summary>
    public readonly struct DemoGameplaySignal
    {
        public readonly DemoGameplaySignalType Type;
        public readonly int Amount;
        public readonly string Payload;

        public DemoGameplaySignal(DemoGameplaySignalType type, int amount = 0, string payload = null)
        {
            Type = type;
            Amount = amount;
            Payload = payload;
        }

        public static DemoGameplaySignal GameStarted() => new(DemoGameplaySignalType.GameStarted);
        public static DemoGameplaySignal EnemyKilled(int coins = 10) => new(DemoGameplaySignalType.EnemyKilled, coins);
        public static DemoGameplaySignal CoinCollected(int amount = 1) => new(DemoGameplaySignalType.CoinCollected, amount);
        public static DemoGameplaySignal LevelCompleted(int level) => new(DemoGameplaySignalType.LevelCompleted, level);
        public static DemoGameplaySignal GameOver(string reason) => new(DemoGameplaySignalType.GameOver, 0, reason);
    }
}