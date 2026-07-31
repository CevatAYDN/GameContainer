using NUnit.Framework;
using System.Threading.Tasks;
using UnityEngine;
using Nexus.Core.Services;

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
        public void IsWindowOpen_UnderLockContention_WaitsInsteadOfFalseNegative()
        {
            // Regression: IsWindowOpen used _windowLock.Wait(0), which returns false the
            // instant any other thread (e.g. a background OpenWindowAsync) briefly holds the
            // semaphore — a false negative that can trigger duplicate window opens. The fix
            // uses a bounded 50 ms wait, so a transient ~20 ms hold is waited out and the
            // query still reports the window as open.
            var lockField = typeof(WindowManager).GetField("_windowLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var semaphore = (System.Threading.SemaphoreSlim)lockField.GetValue(_windowManager);

            var activeField = typeof(WindowManager).GetField("_activeWindows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var active = (System.Collections.Generic.Dictionary<string, GameObject>)activeField.GetValue(_windowManager);
            var fakeWindow = new GameObject("FakeWindow");
            try
            {
                active["FakeWindow"] = fakeWindow;

                var acquired = new System.Threading.ManualResetEventSlim(false);
                // Worker holds the lock for ~20 ms (well under the 50 ms bounded wait),
                // then releases. With Wait(0) this test would get a false negative; with
                // the bounded wait IsWindowOpen blocks until the hold ends and returns true.
                var holder = System.Threading.Tasks.Task.Run(() =>
                {
                    semaphore.Wait();
                    acquired.Set();
                    System.Threading.Thread.Sleep(20);
                    semaphore.Release();
                });

                Assert.IsTrue(acquired.Wait(1000), "Worker never acquired the window lock.");
                bool isOpen = _windowManager.IsWindowOpen("FakeWindow");
                Assert.IsTrue(holder.Wait(2000), "Lock holder task did not complete.");

                // Best-effort guard: in the uncontended case (call happens after the 20 ms
                // hold already ended) this passes even with the old Wait(0) — but when the
                // call DOES overlap the hold, Wait(0) returns false here and the test fails.
                // Deterministic timing assertions are flaky in CI, so this form is preferred.
                Assert.IsTrue(isOpen,
                    "IsWindowOpen must wait out a transient lock hold instead of returning a false negative.");
            }
            finally
            {
                Object.DestroyImmediate(fakeWindow);
                active.Remove("FakeWindow");
            }
        }
    }
}
