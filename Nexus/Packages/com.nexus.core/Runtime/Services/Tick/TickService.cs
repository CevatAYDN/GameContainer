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

        private ITickable[] _tickableSnapshot;
        private IFixedTickable[] _fixedTickableSnapshot;
        private ILateTickable[] _lateTickableSnapshot;

        // Register is deferred (dirty flag): N registrations in one frame produce exactly
        // ONE snapshot rebuild, so spawn storms can't allocate one array per call.
        // Unregister stays immediate — a just-removed tickable (often a destroyed object)
        // must never receive another tick, and its removal is allocation-free anyway.
        private bool _tickablesDirty;
        private bool _fixedTickablesDirty;
        private bool _lateTickablesDirty;

        private readonly object _lock = new();
        private TickDriver _driver;
        private GameObject _driverObject;

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

        public bool IsPaused { get; set; }

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            if (_driverObject != null)
            {
                return default;
            }

            _driverObject = new GameObject("[Nexus_TickDriver]");
            UnityEngine.Object.DontDestroyOnLoad(_driverObject);
            _driver = _driverObject.AddComponent<TickDriver>();

            _driver.OnUpdate = OnTick;
            _driver.OnFixedUpdate = OnFixedTick;
            _driver.OnLateUpdate = OnLateTick;

            return default;
        }

        public void RegisterTickable(ITickable tickable)
        {
            if (tickable == null) return;
            lock (_lock)
            {
                if (!_tickables.Contains(tickable))
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
                if (_tickables.Remove(tickable))
                {
                    _tickableSnapshot = _tickables.Count > 0 ? _tickables.ToArray() : null;
                    _tickablesDirty = false; // snapshot is already current — avoid a redundant rebuild.
                }
            }
        }

        public void RegisterFixedTickable(IFixedTickable fixedTickable)
        {
            if (fixedTickable == null) return;
            lock (_lock)
            {
                if (!_fixedTickables.Contains(fixedTickable))
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
                if (_fixedTickables.Remove(fixedTickable))
                {
                    _fixedTickableSnapshot = _fixedTickables.Count > 0 ? _fixedTickables.ToArray() : null;
                    _fixedTickablesDirty = false; // snapshot is already current — avoid a redundant rebuild.
                }
            }
        }

        public void RegisterLateTickable(ILateTickable lateTickable)
        {
            if (lateTickable == null) return;
            lock (_lock)
            {
                if (!_lateTickables.Contains(lateTickable))
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
                if (_lateTickables.Remove(lateTickable))
                {
                    _lateTickableSnapshot = _lateTickables.Count > 0 ? _lateTickables.ToArray() : null;
                    _lateTickablesDirty = false; // snapshot is already current — avoid a redundant rebuild.
                }
            }
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

                for (int i = 0; i < snapshot.Length; i++)
                {
                    try
                    {
                        snapshot[i]?.Tick(deltaTime);
                    }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogException(ex);
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

                for (int i = 0; i < snapshot.Length; i++)
                {
                    try
                    {
                        snapshot[i]?.FixedTick(fixedDeltaTime);
                    }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogException(ex);
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

                for (int i = 0; i < snapshot.Length; i++)
                {
                    try
                    {
                        snapshot[i]?.LateTick(deltaTime);
                    }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogException(ex);
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
                _tickableSnapshot = null;
                _fixedTickableSnapshot = null;
                _lateTickableSnapshot = null;
                _tickablesDirty = false;
                _fixedTickablesDirty = false;
                _lateTickablesDirty = false;
            }

            if (_driverObject != null)
            {
                UnityEngine.Object.Destroy(_driverObject);
                _driverObject = null;
            }
        }
    }
}
