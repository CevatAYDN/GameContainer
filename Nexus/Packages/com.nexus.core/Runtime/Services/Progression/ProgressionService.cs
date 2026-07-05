using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    public enum CurveType
    {
        Linear,
        Exponential,
        Polynomial
    }

    public interface IProgressionService
    {
        ObservableProperty<int> CurrentLevel { get; }
        ObservableProperty<int> MaxUnlockedLevel { get; }

        void CompleteCurrentLevel();
        void SetLevel(int levelIndex);
        long CalculateUpgradeCost(long baseCost, int level, float multiplier = 1.15f, CurveType curveType = CurveType.Exponential);
    }

    [Preserve]
    public class ProgressionService : IProgressionService, INexusService, IDisposable
    {
        [Inject] public IPlayerPrefsService PlayerPrefsService { get; set; }

        private const string KeyCurrentLevel = "NT_Prog_CurrentLevel";
        private const string KeyMaxLevel = "NT_Prog_MaxLevel";

        public ObservableProperty<int> CurrentLevel { get; } = new(1);
        public ObservableProperty<int> MaxUnlockedLevel { get; } = new(1);

        public ValueTask InitializeAsync(CancellationToken ct)
        {
            if (PlayerPrefsService != null)
            {
                CurrentLevel.Value = PlayerPrefsService.GetInt(KeyCurrentLevel, 1);
                MaxUnlockedLevel.Value = PlayerPrefsService.GetInt(KeyMaxLevel, 1);
            }

            CurrentLevel.OnChanged((oldVal, newVal) => PlayerPrefsService?.SetInt(KeyCurrentLevel, newVal));
            MaxUnlockedLevel.OnChanged((oldVal, newVal) => PlayerPrefsService?.SetInt(KeyMaxLevel, newVal));

            return default;
        }

        public void CompleteCurrentLevel()
        {
            int nextLevel = CurrentLevel.Value + 1;
            CurrentLevel.Value = nextLevel;
            if (nextLevel > MaxUnlockedLevel.Value)
            {
                MaxUnlockedLevel.Value = nextLevel;
            }
        }

        public void SetLevel(int levelIndex)
        {
            CurrentLevel.Value = Math.Max(1, levelIndex);
            if (CurrentLevel.Value > MaxUnlockedLevel.Value)
            {
                MaxUnlockedLevel.Value = CurrentLevel.Value;
            }
        }

        public long CalculateUpgradeCost(long baseCost, int level, float multiplier = 1.15f, CurveType curveType = CurveType.Exponential)
        {
            if (level <= 1) return baseCost;

            return curveType switch
            {
                CurveType.Linear => (long)(baseCost * (1 + (level - 1) * (multiplier - 1))),
                CurveType.Exponential => (long)(baseCost * Math.Pow(multiplier, level - 1)),
                CurveType.Polynomial => (long)(baseCost * Math.Pow(level, multiplier)),
                _ => baseCost
            };
        }

        public void OnDispose() => Dispose();

        public void Dispose()
        {
            CurrentLevel.ClearOnChanged();
            MaxUnlockedLevel.ClearOnChanged();
        }
    }
}
