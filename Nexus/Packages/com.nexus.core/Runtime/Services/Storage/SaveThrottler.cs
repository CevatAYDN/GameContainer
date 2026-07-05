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

        private const float ThrottleSeconds = 2f;

        private Action _lastSaveAction;
        private float _lastSaveTime = -999f;
        private bool _pendingSave;

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

            if (SecondsSinceLastSave >= ThrottleSeconds)
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
            if (_pendingSave && SecondsSinceLastSave >= ThrottleSeconds)
            {
                Flush();
            }
        }

        public void Tick()
        {
            Tick(TimeProvider?.Now ?? Time.realtimeSinceStartup);
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
                _lastSaveTime = Time.realtimeSinceStartup;
                _pendingSave = false;
            }
            catch (Exception ex)
            {
                NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.LogWarning($"[SaveThrottler] Save execution failed: {ex.Message}");
            }
        }
    }
}
