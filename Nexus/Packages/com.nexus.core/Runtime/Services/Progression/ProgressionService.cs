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
        // Optional write-coalescer: without it, every level change triggers a synchronous
        // PlayerPrefs.Save() (frame hitches on mobile). Mirrors EconomyService's pattern.
        [OptionalInject] public ISaveThrottler SaveThrottler { get; set; }

        private const string KeyCurrentLevel = "NT_Prog_CurrentLevel";
        private const string KeyMaxLevel = "NT_Prog_MaxLevel";

        // Owner id for the shared SaveThrottler (see EconomyService.SaveOwner): the
        // progression pending save must live in its own slot so economy writes cannot
        // clobber it and vice versa.
        private const string SaveOwner = "progression";

        public SecureObservableInt CurrentLevel { get; } = new(1);
        public SecureObservableInt MaxUnlockedLevel { get; } = new(1);

        // Serializes the read-modify-write chains (CompleteCurrentLevel / SetLevel):
        // ObservableProperty makes a SINGLE set atomic, but these methods read, compute,
        // then write TWO properties — two concurrent callers could otherwise compute the
        // same next level from one shared read (lost update) or leave CurrentLevel above
        // MaxUnlockedLevel (audit 6.3). Level calls are user-triggered and rare, so a lock
        // is free on the hot path (which is read-only).
        // Note: without a SaveThrottler, PersistNow (PlayerPrefs.Save) runs synchronously
        // INSIDE this lock via the OnChanged → SchedulePersist chain. That is intentional:
        // moving I/O out would split the two-property read-modify-write. Level changes are
        // rare, so the brief disk write under the lock is acceptable — do not "optimize"
        // this without reintroducing the cross-property invariant.
        private readonly object _levelLock = new();

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            if (PlayerPrefsService != null)
            {
                CurrentLevel.Value = PlayerPrefsService.GetInt(KeyCurrentLevel, 1);
                MaxUnlockedLevel.Value = PlayerPrefsService.GetInt(KeyMaxLevel, 1);
            }

            CurrentLevel.OnChanged((oldVal, newVal) => SchedulePersist());
            MaxUnlockedLevel.OnChanged((oldVal, newVal) => SchedulePersist());

            return default;
        }

        /// <summary>Batch-persists both level keys, throttled when a SaveThrottler is bound.</summary>
        private void SchedulePersist()
        {
            if (SaveThrottler != null) SaveThrottler.TryRequestSave(SaveOwner, PersistNow);
            else PersistNow();
        }

        private void PersistNow()
        {
            if (PlayerPrefsService == null) return;
            PlayerPrefsService.SetInt(KeyCurrentLevel, CurrentLevel.Value);
            PlayerPrefsService.SetInt(KeyMaxLevel, MaxUnlockedLevel.Value);
            PlayerPrefsService.Save();
        }

        public void CompleteCurrentLevel()
        {
            lock (_levelLock)
            {
                int nextLevel = CurrentLevel.Value + 1;
                CurrentLevel.Value = nextLevel;
                if (nextLevel > MaxUnlockedLevel.Value)
                {
                    MaxUnlockedLevel.Value = nextLevel;
                }
            }
        }

        public void SetLevel(int levelIndex)
        {
            lock (_levelLock)
            {
                int nextLevel = Math.Max(1, levelIndex);
                CurrentLevel.Value = nextLevel;
                if (nextLevel > MaxUnlockedLevel.Value)
                {
                    MaxUnlockedLevel.Value = nextLevel;
                }
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
            // Flush any pending throttled save BEFORE clearing observers so the final
            // level values survive teardown even if SaveThrottler disposes after this
            // service. Mirrors EconomyService.Dispose() for the same reason.
            if (SaveThrottler != null)
            {
                try { SaveThrottler.ForceSave(SaveOwner, PersistNow); }
                catch (Exception ex) { NexusRuntime.Logger?.LogWarning($"[Progression] Final persist failed on dispose: {ex.Message}"); }
            }
            CurrentLevel.ClearOnChanged();
            MaxUnlockedLevel.ClearOnChanged();
        }
    }
}
