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

        public SaveThrottler()
        {
            _throttleSeconds = 2f;
        }

        public SaveThrottler(IPlayerPrefsService prefs, ITickService tickService, TimeSpan throttleTime)
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
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogWarning($"[SaveThrottler] Save execution failed: {ex.Message}");
            }
        }
    }
}
