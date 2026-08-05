using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.Profiling;
using Unity.Profiling;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    public interface ITickService
    {
        float TimeScale { get; set; }
        bool IsPaused { get; set; }

        void RegisterTickable(ITickable tickable);
        void UnregisterTickable(ITickable tickable);

        void RegisterFixedTickable(IFixedTickable fixedTickable);
        void UnregisterFixedTickable(IFixedTickable fixedTickable);

        void RegisterLateTickable(ILateTickable lateTickable);
        void UnregisterLateTickable(ILateTickable lateTickable);
    }

    [Preserve]
    public class TickService : NexusService<ITickService>, ITickService
    {
        private class TickDriver : MonoBehaviour
        {
            public Action<float> OnUpdate;
            public Action<float> OnFixedUpdate;
            public Action<float> OnLateUpdate;

            private void Update() => OnUpdate?.Invoke(Time.deltaTime);
            private void FixedUpdate() => OnFixedUpdate?.Invoke(Time.fixedDeltaTime);
            private void LateUpdate() => OnLateUpdate?.Invoke(Time.deltaTime);
        }

        private readonly List<ITickable> _tickables = new();
        private readonly List<IFixedTickable> _fixedTickables = new();
        private readonly List<ILateTickable> _lateTickables = new();

        // Audit fix 3.9: O(1) dedup sets. RegisterTickable previously ran List.Contains
        // (O(N)) per call — a spawn/despawn storm of 1000 register calls over 500 live
        // tickables cost 500k comparisons in one frame.
        private readonly HashSet<ITickable> _tickableSet = new();
        private readonly HashSet<IFixedTickable> _fixedTickableSet = new();
        private readonly HashSet<ILateTickable> _lateTickableSet = new();

        private ITickable[] _tickableSnapshot;
        private IFixedTickable[] _fixedTickableSnapshot;
        private ILateTickable[] _lateTickableSnapshot;

        // Register AND Unregister are both deferred (dirty flag): N mutations in one frame
        // produce exactly ONE snapshot rebuild at the next tick, so spawn/despawn storms
        // allocate one array per frame instead of one per call (audit fix 3.10 — Unregister
        // previously called ToArray() eagerly per removal). A removed tickable can still
        // appear in the CURRENT in-flight snapshot, but the per-frame loop already skips
        // null/destroyed entries, and the next tick rebuilds without it — the same
        // guarantee the eager rebuild provided (it never affected the in-flight iteration
        // either, since OnTick captures the snapshot reference before iterating).
        private bool _tickablesDirty;
        private bool _fixedTickablesDirty;
        private bool _lateTickablesDirty;

        // Audit fix 3.4: destroyed tickables used to stay in the snapshot FOREVER (the loop
        // skipped them but never removed them — 100 dead objects = 6000 wasted null checks
        // per second at 60 fps, plus the snapshot array kept the managed shells alive).
        // The per-frame loop now counts dead entries and compacts the backing list once the
        // waste crosses an amortization threshold.
        private const int DeadSweepThreshold = 8;

        private readonly object _lock = new();
        private TickDriver _driver;
        private GameObject _driverObject;
        private volatile bool _isPaused;

        // Zero-allocation profiler markers (same pattern as SignalBus). ProfilerMarker is a
        // cheap struct no-op when the profiler is not attached, so these are unconditional —
        // no #if wrapper needed and the production GC guarantee is untouched.
        private static readonly ProfilerMarker s_UpdateMarker = new("Nexus.TickService.Update");
        private static readonly ProfilerMarker s_FixedUpdateMarker = new("Nexus.TickService.FixedUpdate");
        private static readonly ProfilerMarker s_LateUpdateMarker = new("Nexus.TickService.LateUpdate");

        public float TimeScale
        {
            get => Time.timeScale;
            set => Time.timeScale = Mathf.Max(0f, value);
        }

        public bool IsPaused
        {
            get => _isPaused;
            set => _isPaused = value;
        }

        private static GameObject s_sharedDriverObject;
        private static TickDriver s_sharedDriver;
        private static int s_activeDriverCount;
        private static readonly object s_driverLock = new();

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            lock (s_driverLock)
            {
                if (s_sharedDriverObject == null)
                {
                    s_sharedDriverObject = new GameObject("[Nexus_TickDriver]");
                    UnityEngine.Object.DontDestroyOnLoad(s_sharedDriverObject);
                    s_sharedDriver = s_sharedDriverObject.AddComponent<TickDriver>();
                }
                s_activeDriverCount++;
                _driver = s_sharedDriver;
                _driverObject = s_sharedDriverObject;
                _driver.OnUpdate += OnTick;
                _driver.OnFixedUpdate += OnFixedTick;
                _driver.OnLateUpdate += OnLateTick;
            }

            return default;
        }

        public void RegisterTickable(ITickable tickable)
        {
            if (tickable == null) return;
            lock (_lock)
            {
                if (_tickableSet.Add(tickable))
                {
                    _tickables.Add(tickable);
                    _tickablesDirty = true;
                }
            }
        }

        public void UnregisterTickable(ITickable tickable)
        {
            if (tickable == null) return;
            lock (_lock)
            {
                if (_tickableSet.Remove(tickable) && _tickables.Remove(tickable))
                {
                    // Audit fix 3.10: dirty-flag only — no eager ToArray per removal.
                    _tickablesDirty = true;
                }
            }
        }

        public void RegisterFixedTickable(IFixedTickable fixedTickable)
        {
            if (fixedTickable == null) return;
            lock (_lock)
            {
                if (_fixedTickableSet.Add(fixedTickable))
                {
                    _fixedTickables.Add(fixedTickable);
                    _fixedTickablesDirty = true;
                }
            }
        }

        public void UnregisterFixedTickable(IFixedTickable fixedTickable)
        {
            if (fixedTickable == null) return;
            lock (_lock)
            {
                if (_fixedTickableSet.Remove(fixedTickable) && _fixedTickables.Remove(fixedTickable))
                {
                    _fixedTickablesDirty = true;
                }
            }
        }

        public void RegisterLateTickable(ILateTickable lateTickable)
        {
            if (lateTickable == null) return;
            lock (_lock)
            {
                if (_lateTickableSet.Add(lateTickable))
                {
                    _lateTickables.Add(lateTickable);
                    _lateTickablesDirty = true;
                }
            }
        }

        public void UnregisterLateTickable(ILateTickable lateTickable)
        {
            if (lateTickable == null) return;
            lock (_lock)
            {
                if (_lateTickableSet.Remove(lateTickable) && _lateTickables.Remove(lateTickable))
                {
                    _lateTickablesDirty = true;
                }
            }
        }

        /// <summary>
        /// Audit fix 3.4: removes null/destroyed entries from a tickable list (and its dedup
        /// set). Returns the number removed. Call under _lock; amortized — only invoked once
        /// the dead-entry count crosses <see cref="DeadSweepThreshold"/> or 25% of the snapshot.
        /// </summary>
        private static int SweepDead<T>(List<T> list, HashSet<T> set) where T : class
        {
            int removed = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var t = list[i];
                if (t == null || (t is UnityEngine.Object uo && uo == false))
                {
                    list.RemoveAt(i);
                    set.Remove(t);
                    removed++;
                }
            }
            return removed;
        }

        internal void OnTick(float deltaTime)
        {
            if (IsPaused) return;
            s_UpdateMarker.Begin();
            try
            {
                ITickable[] snapshot;
                lock (_lock)
                {
                    if (_tickablesDirty)
                    {
                        _tickableSnapshot = _tickables.Count > 0 ? _tickables.ToArray() : null;
                        _tickablesDirty = false;
                    }
                    snapshot = _tickableSnapshot;
                }
                if (snapshot == null) return;

                int deadCount = 0;
                for (int i = 0; i < snapshot.Length; i++)
                {
                    try
                    {
                        var tickable = snapshot[i];
                        // Unity "fake null": a destroyed MonoBehaviour is not C# null, so we
                        // check via both reference null and the Unity-specific cast-to-bool.
                        if (tickable == null || (tickable is UnityEngine.Object uo && uo == false))
                        {
                            deadCount++;
                            continue;
                        }
                        tickable.Tick(deltaTime);
                    }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogException(ex);
                    }
                }

                // Audit fix 3.4: amortized compaction of accumulated dead entries.
                if (deadCount >= DeadSweepThreshold || (deadCount > 0 && deadCount * 4 >= snapshot.Length))
                {
                    lock (_lock)
                    {
                        if (SweepDead(_tickables, _tickableSet) > 0)
                            _tickablesDirty = true;
                    }
                }
            }
            finally
            {
                s_UpdateMarker.End();
            }
        }

        internal void OnFixedTick(float fixedDeltaTime)
        {
            if (IsPaused) return;
            s_FixedUpdateMarker.Begin();
            try
            {
                IFixedTickable[] snapshot;
                lock (_lock)
                {
                    if (_fixedTickablesDirty)
                    {
                        _fixedTickableSnapshot = _fixedTickables.Count > 0 ? _fixedTickables.ToArray() : null;
                        _fixedTickablesDirty = false;
                    }
                    snapshot = _fixedTickableSnapshot;
                }
                if (snapshot == null) return;

                int deadCount = 0;
                for (int i = 0; i < snapshot.Length; i++)
                {
                    try
                    {
                        var tickable = snapshot[i];
                        if (tickable == null || (tickable is UnityEngine.Object uo && uo == false))
                        {
                            deadCount++;
                            continue;
                        }
                        tickable.FixedTick(fixedDeltaTime);
                    }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogException(ex);
                    }
                }

                if (deadCount >= DeadSweepThreshold || (deadCount > 0 && deadCount * 4 >= snapshot.Length))
                {
                    lock (_lock)
                    {
                        if (SweepDead(_fixedTickables, _fixedTickableSet) > 0)
                            _fixedTickablesDirty = true;
                    }
                }
            }
            finally
            {
                s_FixedUpdateMarker.End();
            }
        }

        internal void OnLateTick(float deltaTime)
        {
            if (IsPaused) return;
            s_LateUpdateMarker.Begin();
            try
            {
                ILateTickable[] snapshot;
                lock (_lock)
                {
                    if (_lateTickablesDirty)
                    {
                        _lateTickableSnapshot = _lateTickables.Count > 0 ? _lateTickables.ToArray() : null;
                        _lateTickablesDirty = false;
                    }
                    snapshot = _lateTickableSnapshot;
                }
                if (snapshot == null) return;

                int deadCount = 0;
                for (int i = 0; i < snapshot.Length; i++)
                {
                    try
                    {
                        var tickable = snapshot[i];
                        if (tickable == null || (tickable is UnityEngine.Object uo && uo == false))
                        {
                            deadCount++;
                            continue;
                        }
                        tickable.LateTick(deltaTime);
                    }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogException(ex);
                    }
                }

                if (deadCount >= DeadSweepThreshold || (deadCount > 0 && deadCount * 4 >= snapshot.Length))
                {
                    lock (_lock)
                    {
                        if (SweepDead(_lateTickables, _lateTickableSet) > 0)
                            _lateTickablesDirty = true;
                    }
                }
            }
            finally
            {
                s_LateUpdateMarker.End();
            }
        }

        public override void Dispose()
        {
            lock (_lock)
            {
                _tickables.Clear();
                _fixedTickables.Clear();
                _lateTickables.Clear();
                _tickableSet.Clear();
                _fixedTickableSet.Clear();
                _lateTickableSet.Clear();
                _tickableSnapshot = null;
                _fixedTickableSnapshot = null;
                _lateTickableSnapshot = null;
                _tickablesDirty = false;
                _fixedTickablesDirty = false;
                _lateTickablesDirty = false;
            }

            lock (s_driverLock)
            {
                if (_driver != null)
                {
                    _driver.OnUpdate -= OnTick;
                    _driver.OnFixedUpdate -= OnFixedTick;
                    _driver.OnLateUpdate -= OnLateTick;
                    _driver = null;
                    _driverObject = null;
                    s_activeDriverCount = Math.Max(0, s_activeDriverCount - 1);
                    if (s_activeDriverCount == 0 && s_sharedDriverObject != null)
                    {
                        SafeDestroyUtility.SafeDestroy(s_sharedDriverObject);
                        s_sharedDriverObject = null;
                        s_sharedDriver = null;
                    }
                }
            }
        }
    }
}
