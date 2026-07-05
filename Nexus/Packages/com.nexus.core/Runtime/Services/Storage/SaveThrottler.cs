using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Nexus.Core.Services
{
    public class SaveThrottler : ISaveThrottler, INexusService
    {
        private const float ThrottleSeconds = 2f;

        private Action _lastSaveAction;
        private float _lastSaveTime = -999f;
        private bool _pendingSave;

        public float SecondsSinceLastSave =>
            _lastSaveTime < 0f ? 999f : Time.realtimeSinceStartup - _lastSaveTime;

        public ValueTask InitializeAsync(CancellationToken ct) => default;

        public void OnDispose()
        {
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

        public void Tick()
        {
            if (_pendingSave && SecondsSinceLastSave >= ThrottleSeconds)
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
                _lastSaveTime = Time.realtimeSinceStartup;
                _pendingSave = false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SaveThrottler] Save execution failed: {ex.Message}");
            }
        }
    }
}
