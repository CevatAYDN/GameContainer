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
        // XOR-masked reactive properties: level data must not sit in plain RAM where
        // GameGuardian / CheatEngine scans can find and edit it.
        SecureObservableInt CurrentLevel { get; }
        SecureObservableInt MaxUnlockedLevel { get; }

        void CompleteCurrentLevel();
        void SetLevel(int levelIndex);
        long CalculateUpgradeCost(long baseCost, int level, float multiplier = 1.15f, CurveType curveType = CurveType.Exponential);
    }

    [Preserve]
    public class ProgressionService : NexusService<IProgressionService>, IProgressionService
    {
        [Inject] public IPlayerPrefsService PlayerPrefsService { get; set; }

        private const string KeyCurrentLevel = "NT_Prog_CurrentLevel";
        private const string KeyMaxLevel = "NT_Prog_MaxLevel";

        public SecureObservableInt CurrentLevel { get; } = new(1);
        public SecureObservableInt MaxUnlockedLevel { get; } = new(1);

        public override ValueTask InitializeAsync(CancellationToken ct)
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

            double rawCost = curveType switch
            {
                CurveType.Linear => baseCost * (1 + (level - 1) * (multiplier - 1)),
                CurveType.Exponential => baseCost * Math.Pow(multiplier, level - 1),
                CurveType.Polynomial => baseCost * Math.Pow(level, multiplier),
                _ => baseCost
            };

            // Clamp NaN / Infinity / overflow so extreme levels never wrap around to
            // long.MinValue (the unchecked double->long cast does exactly that), and
            // never produce a negative cost (Linear curves can go negative when
            // multiplier < 1). A maxed cost is far safer than a broken one.
            if (double.IsNaN(rawCost) || double.IsInfinity(rawCost) || rawCost >= long.MaxValue)
            {
                return long.MaxValue;
            }

            return (long)Math.Max(Math.Max(baseCost, rawCost), 1L);
        }

        public override void Dispose()
        {
            CurrentLevel.ClearOnChanged();
            MaxUnlockedLevel.ClearOnChanged();
        }
    }
}
