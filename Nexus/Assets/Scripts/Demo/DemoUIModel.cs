using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;

namespace Nexus.Demo
{
    /// <summary>UI model interface for DI.</summary>
    public interface IDemoUIModel
    {
        ObservableProperty<string> CurrentScreen { get; }
        ObservableProperty<int> TotalCoins { get; }
        ObservableProperty<int> TotalGems { get; }
        ObservableProperty<int> HighScore { get; }

        void SetScreen(string screenName);
        void AddCoins(int amount);
        void AddGems(int amount);
        void UpdateHighScore(int score);
    }

    /// <summary>UI state model - reactive via ObservableProperty.</summary>
    public class DemoUIModel : IDemoUIModel, IReactiveModel
    {
        public ValueTask OnBind(CancellationToken ct) => default;

        public ObservableProperty<string> CurrentScreen { get; } = new("MainMenu");
        public ObservableProperty<int> TotalCoins { get; } = new(0);
        public ObservableProperty<int> TotalGems { get; } = new(0);
        public ObservableProperty<int> HighScore { get; } = new(0);

        public void SetScreen(string screenName)
        {
            CurrentScreen.Value = screenName;
        }

        public void AddCoins(int amount)
        {
            if (amount > 0) TotalCoins.Value += amount;
        }

        public void AddGems(int amount)
        {
            if (amount > 0) TotalGems.Value += amount;
        }

        public void UpdateHighScore(int score)
        {
            if (score > HighScore.Value) HighScore.Value = score;
        }
    }
}