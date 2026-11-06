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
        // Read-only views of XOR-masked properties (level data must not sit in plain RAM where GameGuardian / CheatEngine
        // scans can find and edit it): a level changes through SetLevel and CompleteCurrentLevel, which save it (#231).
        IReadOnlyObservableProperty<int> CurrentLevel { get; }
        IReadOnlyObservableProperty<int> MaxUnlockedLevel { get; }

        void CompleteCurrentLevel();
        void SetLevel(int levelIndex);

        /// <summary>
        /// The cost of an upgrade at <paramref name="level"/> (level 1 costs <paramref name="baseCost"/>): Linear
        /// <c>baseCost * (1 + (level - 1) * (multiplier - 1))</c>, Exponential <c>baseCost * multiplier^(level - 1)</c>,
        /// Polynomial <c>baseCost * level^multiplier</c>. The result rounds to the nearest whole number, halves away from
        /// zero (100 at 1.15 gives 115 at level 2 and 175 at level 5). It is never below <paramref name="baseCost"/>,
        /// so a multiplier below 1 keeps the base cost, and a cost too large for a long is <see cref="long.MaxValue"/>.
        /// </summary>
        long CalculateUpgradeCost(long baseCost, int level, float multiplier = 1.15f, CurveType curveType = CurveType.Exponential);
    }

    [Preserve]
    public class ProgressionService : NexusService<IProgressionService>, IProgressionService
    {
        [Inject] public IPlayerPrefsService PlayerPrefsService { get; set; }
        // Optional: the game's store for levels (SettingsStores.Progression); null uses PlayerPrefsService.
        [OptionalInject] public SettingsStores SettingsStores { get; set; }
        private IPlayerPrefsService Store => SettingsStores?.Progression ?? PlayerPrefsService;
        // Optional write-coalescer: without it, every level change triggers a synchronous
        // PlayerPrefs.Save() (frame hitches on mobile). Mirrors EconomyService's pattern.
        [OptionalInject] public ISaveThrottler SaveThrottler { get; set; }

        private const string KeyCurrentLevel = "NT_Prog_CurrentLevel";
        private const string KeyMaxLevel = "NT_Prog_MaxLevel";

        // Owner id for the shared SaveThrottler (see EconomyService.SaveOwner): the
        // progression pending save must live in its own slot so economy writes cannot
        // clobber it and vice versa.
        private const string SaveOwner = "progression";

        private readonly SecureObservableInt _currentLevel = new(1);
        private readonly SecureObservableInt _maxUnlockedLevel = new(1);
        public IReadOnlyObservableProperty<int> CurrentLevel => _currentLevel;
        public IReadOnlyObservableProperty<int> MaxUnlockedLevel => _maxUnlockedLevel;

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
        private int _updateDepth;

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            var store = Store;
            if (store != null)
            {
                _currentLevel.SetByService(Math.Max(1, store.GetInt(KeyCurrentLevel, 1)));
                _maxUnlockedLevel.SetByService(Math.Max(_currentLevel.Value, store.GetInt(KeyMaxLevel, 1)));
            }

            _currentLevel.OnChanged(OnLevelChanged);
            _maxUnlockedLevel.OnChanged(OnLevelChanged);

            return default;
        }

        // Named handler so Dispose can remove EXACTLY this subscription — anonymous
        // lambdas cannot be unsubscribed, and ClearOnChanged() would wipe every OTHER
        // subscriber of the shared observable (house style: named handler + RemoveOnChanged).
        private void OnLevelChanged(int oldValue, int newValue)
        {
            lock (_levelLock)
            {
                if (_updateDepth == 0) SchedulePersist();
            }
        }

        /// <summary>Batch-persists both level keys, throttled when a SaveThrottler is bound.</summary>
        private void SchedulePersist()
        {
            if (SaveThrottler != null) SaveThrottler.TryRequestSave(SaveOwner, PersistNow);
            else PersistNow();
        }

        private void PersistNow()
        {
            var store = Store;
            if (store == null) return;
            store.SetInt(KeyCurrentLevel, _currentLevel.Value);
            store.SetInt(KeyMaxLevel, _maxUnlockedLevel.Value);
            store.Save();
        }

        public void CompleteCurrentLevel()
        {
            lock (_levelLock)
            {
                if (_currentLevel.Value == int.MaxValue) return;
                SetLevelLocked(_currentLevel.Value + 1);
            }
        }

        public void SetLevel(int levelIndex)
        {
            lock (_levelLock)
            {
                SetLevelLocked(Math.Max(1, levelIndex));
            }
        }

        private void SetLevelLocked(int level)
        {
            if (_currentLevel.Value == level && _maxUnlockedLevel.Value >= level) return;
            _updateDepth++;
            try
            {
                // Publish the unlocked bound first so observers never see current > max.
                // an observer's failure does not stop the second write or the save (#225)
                if (level > _maxUnlockedLevel.Value) _maxUnlockedLevel.SetByService(level);
                _currentLevel.SetByService(level);
            }
            finally
            {
                if (--_updateDepth == 0) SchedulePersist();
            }
        }

        public long CalculateUpgradeCost(long baseCost, int level, float multiplier = 1.15f, CurveType curveType = CurveType.Exponential)
        {
            if (level <= 1) return baseCost;

            double m = multiplier;
            double rawCost = curveType switch
            {
                CurveType.Linear => baseCost * (1 + (level - 1) * (m - 1)),
                CurveType.Exponential => baseCost * Math.Pow(m, level - 1),
                CurveType.Polynomial => baseCost * Math.Pow(level, m),
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

            // Round, not truncate: 1.15f widens to 1.149999976..., so 100 at level 2 truncated to 114 (#301).
            return (long)Math.Max(Math.Max(baseCost, Math.Round(rawCost, MidpointRounding.AwayFromZero)), 1L);
        }

        public override void Dispose()
        {
            // Flush any pending throttled save BEFORE clearing observers so the final
            // level values survive teardown even if SaveThrottler disposes after this
            // service. Mirrors EconomyService.Dispose() for the same reason.
            if (SaveThrottler != null)
            {
                try { SaveThrottler.ForceSave(SaveOwner, PersistNow); }
                catch (Exception ex) { NexusLog.WarnAlways($"[Progression] Final persist failed on dispose: {ex.Message}"); }
            }
            _currentLevel.RemoveOnChanged(OnLevelChanged);
            _maxUnlockedLevel.RemoveOnChanged(OnLevelChanged);
        }
    }
}
