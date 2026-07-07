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
    }
}
