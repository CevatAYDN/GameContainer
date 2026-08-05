using NUnit.Framework;
using System.Threading.Tasks;
using UnityEngine;
using Nexus.Core.Services;

// These tests intentionally exercise the legacy WindowManager API.
#pragma warning disable CS0618

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class WindowManagerTests
    {
        private WindowManager _windowManager;
        private GameObject _manualCanvas;

        [SetUp]
        public void SetUp()
        {
            // Create manual canvas to avoid DontDestroyOnLoad in EditMode
            _manualCanvas = new GameObject("[Nexus_UICanvas]");
            
            // Set up basic canvas structure
            var canvas = _manualCanvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            // Create layer roots
            var hud = new GameObject("HUD");
            hud.transform.SetParent(_manualCanvas.transform);
            
            var screen = new GameObject("Screen");
            screen.transform.SetParent(_manualCanvas.transform);
            
            var popup = new GameObject("Popup");
            popup.transform.SetParent(_manualCanvas.transform);
            
            var modal = new GameObject("Modal");
            modal.transform.SetParent(_manualCanvas.transform);

            // Skip InitializeAsync to avoid DontDestroyOnLoad
            _windowManager = new WindowManager();
        }

        [TearDown]
        public void TearDown()
        {
            // Manual cleanup without calling Dispose (which uses Destroy)
            if (_manualCanvas != null)
            {
                Object.DestroyImmediate(_manualCanvas);
                _manualCanvas = null;
            }
        }

        [Test]
        public void WindowManager_Initialization_CreatesCanvasAndLayerRoots()
        {
            var canvas = GameObject.Find("[Nexus_UICanvas]");
            Assert.IsNotNull(canvas);
            Assert.IsNotNull(canvas.transform.Find("HUD"));
            Assert.IsNotNull(canvas.transform.Find("Screen"));
            Assert.IsNotNull(canvas.transform.Find("Popup"));
            Assert.IsNotNull(canvas.transform.Find("Modal"));
        }

        [Test]
        public void IsWindowOpen_LockFreeRead_NoFalseNegativeUnderContention()
        {
            // A4b regression: IsWindowOpen now reads a lock-free volatile snapshot
            // (_activeWindowsRead) that is refreshed under the lock after every
            // mutation. It never takes the semaphore, so it can neither block the
            // main thread nor return a false negative when another thread holds the
            // window lock — the old Wait(0)/Wait(50) paths could do both.
            var lockField = typeof(WindowManager).GetField("_windowLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var semaphore = (System.Threading.SemaphoreSlim)lockField.GetValue(_windowManager);

            var activeField = typeof(WindowManager).GetField("_activeWindows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var active = (System.Collections.Generic.Dictionary<string, GameObject>)activeField.GetValue(_windowManager);

            var readField = typeof(WindowManager).GetField("_activeWindowsRead", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var fakeWindow = new GameObject("FakeWindow");
            try
            {
                active["FakeWindow"] = fakeWindow;
                // Refresh the lock-free read snapshot the way a committed mutation would.
                readField.SetValue(_windowManager, new System.Collections.Generic.Dictionary<string, GameObject>(active));

                // Hold the lock on this thread: the query must still answer immediately
                // (no wait, no false negative) because it reads the snapshot, not the
                // semaphore-guarded dictionary.
                semaphore.Wait();
                try
                {
                    bool isOpen = _windowManager.IsWindowOpen("FakeWindow");
                    Assert.IsTrue(isOpen,
                        "IsWindowOpen must read the lock-free snapshot and never block on the window lock.");
                }
                finally
                {
                    semaphore.Release();
                }
            }
            finally
            {
                Object.DestroyImmediate(fakeWindow);
                active.Remove("FakeWindow");
            }
        }
    }
}
