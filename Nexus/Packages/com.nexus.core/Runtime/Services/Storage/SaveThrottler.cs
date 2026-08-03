using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Nexus.Core;

namespace Nexus.Core.Services
{
    public interface ITimeProvider
    {
        float Now { get; }
    }

    public class UnityTimeProvider : ITimeProvider
    {
        public float Now => Time.realtimeSinceStartup;
    }

    public class SaveThrottler : ISaveThrottler, INexusService, ITickable
    {
        [Inject] public ITickService TickService { get; set; }
        [Inject] public ITimeProvider TimeProvider { get; set; }

        private readonly float _throttleSeconds = 2f;

        private Action _lastSaveAction;
        private float _lastSaveTime = -999f;
        private bool _pendingSave;

        // M1: consecutive-failure accounting. A failing save must back off (the throttle
        // window gates the next retry) instead of retrying on every request in a tight
        // loop, and must give up after a bound so a permanently broken disk cannot keep
        // the pending flag alive forever.
        private int _consecutiveFailures;
        private const int MaxConsecutiveSaveFailures = 5;

        public SaveThrottler()
        {
            _throttleSeconds = 2f;
        }

        // M8: the IPlayerPrefsService parameter was never used — a dead parameter that
        // implied a dependency this throttler does not have. Removed.
        public SaveThrottler(ITickService tickService, TimeSpan throttleTime)
        {
            TickService = tickService;
            _throttleSeconds = (float)throttleTime.TotalSeconds;
        }

        public float SecondsSinceLastSave =>
            _lastSaveTime < 0f ? 999f : (TimeProvider?.Now ?? Time.realtimeSinceStartup) - _lastSaveTime;

        public ValueTask InitializeAsync(CancellationToken ct)
        {
            TickService?.RegisterTickable(this);
            return default;
        }

        public void OnDispose()
        {
            TickService?.UnregisterTickable(this);
            FlushPending();
            _pendingSave = false;
        }

        public void TryRequestSave(Action saveAction)
        {
            if (saveAction == null) return;
            _lastSaveAction = saveAction;

            if (SecondsSinceLastSave >= _throttleSeconds)
            {
                Flush();
            }
            else
            {
                _pendingSave = true;
            }
        }

        public void Tick(float deltaTime)
        {
            if (_pendingSave && SecondsSinceLastSave >= _throttleSeconds)
            {
                Flush();
            }
        }

        public void Tick()
        {
            if (_pendingSave && SecondsSinceLastSave >= _throttleSeconds)
            {
                Flush();
            }
        }

        private void FlushPending()
        {
            if (_pendingSave) Flush();
        }

        public void ForceSave(Action saveAction)
        {
            if (saveAction == null) return;
            _lastSaveAction = saveAction;
            Flush();
        }

        public void Flush()
        {
            if (_lastSaveAction == null) return;
            try
            {
                _lastSaveAction.Invoke();
                _lastSaveTime = TimeProvider?.Now ?? Time.realtimeSinceStartup;
                _pendingSave = false;
                _consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                // M1: back off after a failed save. Treating the failed attempt as a save
                // moment makes the throttle window gate the next retry (no tight loop), and
                // the retry cap clears the pending flag after repeated failures so a broken
                // disk cannot keep retrying forever. The failure is always logged — never
                // silent.
                _consecutiveFailures++;
                _lastSaveTime = TimeProvider?.Now ?? Time.realtimeSinceStartup;
                _pendingSave = _consecutiveFailures < MaxConsecutiveSaveFailures;
                NexusRuntime.Logger?.LogWarning(
                    $"[SaveThrottler] Save execution failed ({_consecutiveFailures}/{MaxConsecutiveSaveFailures} consecutive): {ex.Message}");
            }
        }
    }
}
