using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Nexus.Core.Services
{
    public interface IWindowManager
    {
        void OpenWindow(string windowName, object args = null);
        void CloseWindow(string windowName);
        void CloseAll();
        bool IsWindowOpen(string windowName);
    }

    public class WindowManager : IWindowManager, INexusService
    {
        private readonly Dictionary<string, GameObject> _activeWindows = new Dictionary<string, GameObject>();
        private Transform _canvasRoot;

        public ValueTask InitializeAsync(CancellationToken ct)
        {
            var canvasGo = GameObject.Find("Canvas") ?? new GameObject("Canvas");
            _canvasRoot = canvasGo.transform;
            return default;
        }

        public void OnDispose()
        {
            CloseAll();
        }

        public void OpenWindow(string windowName, object args = null)
        {
            if (_activeWindows.ContainsKey(windowName)) return;

            var prefab = Resources.Load<GameObject>($"UI/Windows/{windowName}");
            if (prefab == null) return;

            var inst = Object.Instantiate(prefab, _canvasRoot);
            inst.name = windowName;
            _activeWindows[windowName] = inst;
        }

        public void CloseWindow(string windowName)
        {
            if (_activeWindows.TryGetValue(windowName, out var go))
            {
                if (go != null) Object.Destroy(go);
                _activeWindows.Remove(windowName);
            }
        }

        public void CloseAll()
        {
            foreach (var go in _activeWindows.Values)
            {
                if (go != null) Object.Destroy(go);
            }
            _activeWindows.Clear();
        }

        public bool IsWindowOpen(string windowName) => _activeWindows.ContainsKey(windowName);
    }
}
