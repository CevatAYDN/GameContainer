using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Services;
using UnityEngine;
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
    public class TickService : ITickService, INexusService, IDisposable
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

        private readonly object _lock = new();
        private TickDriver _driver;
        private GameObject _driverObject;

        public float TimeScale
        {
            get => Time.timeScale;
            set => Time.timeScale = Mathf.Max(0f, value);
        }

        public bool IsPaused { get; set; }

        public ValueTask InitializeAsync(CancellationToken ct)
        {
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
                    _tickableSnapshot = _tickables.ToArray();
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
                    _fixedTickableSnapshot = _fixedTickables.ToArray();
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
                    _lateTickableSnapshot = _lateTickables.ToArray();
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
                }
            }
        }

        private void OnTick(float deltaTime)
        {
            if (IsPaused) return;
            var snapshot = _tickableSnapshot;
            if (snapshot == null) return;

            for (int i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    snapshot[i]?.Tick(deltaTime);
                }
                catch (Exception ex)
                {
                    NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.LogException(ex);
                }
            }
        }

        private void OnFixedTick(float fixedDeltaTime)
        {
            if (IsPaused) return;
            var snapshot = _fixedTickableSnapshot;
            if (snapshot == null) return;

            for (int i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    snapshot[i]?.FixedTick(fixedDeltaTime);
                }
                catch (Exception ex)
                {
                    NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.LogException(ex);
                }
            }
        }

        private void OnLateTick(float deltaTime)
        {
            if (IsPaused) return;
            var snapshot = _lateTickableSnapshot;
            if (snapshot == null) return;

            for (int i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    snapshot[i]?.LateTick(deltaTime);
                }
                catch (Exception ex)
                {
                    NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.LogException(ex);
                }
            }
        }

        public void OnDispose() => Dispose();

        public void Dispose()
        {
            lock (_lock)
            {
                _tickables.Clear();
                _fixedTickables.Clear();
                _lateTickables.Clear();
                _tickableSnapshot = null;
                _fixedTickableSnapshot = null;
                _lateTickableSnapshot = null;
            }

            if (_driverObject != null)
            {
                UnityEngine.Object.Destroy(_driverObject);
                _driverObject = null;
            }
        }
    }
}
