using NUnit.Framework;
using System.Threading.Tasks;
using UnityEngine;
using Nexus.Core.Services;

namespace Nexus.Editor.Tests
{
    /// <summary>Minimal concrete ScreenView used to exercise UIManager open/close/pooling.</summary>
    public class TestScreenView : ScreenView
    {
        public int OpenCount;
        public int CloseCount;
        public object LastArgs;

        protected override void OnScreenOpened(object args)
        {
            OpenCount++;
            LastArgs = args;
        }

        protected override void OnScreenClosed()
        {
            CloseCount++;
        }
    }

    [TestFixture]
    public class UIManagerTests
    {
        private UIManager _uiManager;
        private GameObject _manualCanvas;
        private GameObject _prefab;

        [SetUp]
        public void SetUp()
        {
            // Create manual canvas to avoid DontDestroyOnLoad in EditMode
            _manualCanvas = new GameObject("[Nexus_UICanvas]");
            var canvas = _manualCanvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            // Layer roots the UICanvasSystem will discover
            foreach (var name in new[] { "HUD", "Screen", "Popup", "Modal" })
            {
                var layer = new GameObject(name);
                layer.transform.SetParent(_manualCanvas.transform);
            }

            _uiManager = new UIManager();
            _uiManager.InitializeAsync(default).GetAwaiter().GetResult();

            _prefab = new GameObject("TestScreenView");
            _prefab.AddComponent<TestScreenView>();
            _uiManager.RegisterScreenPrefab<TestScreenView>(_prefab);
        }

        [TearDown]
        public void TearDown()
        {
            if (_uiManager != null)
                _uiManager.Dispose();
            if (_manualCanvas != null)
                Object.DestroyImmediate(_manualCanvas);
            if (_prefab != null)
                Object.DestroyImmediate(_prefab);
            _manualCanvas = null;
            _prefab = null;
        }

        [Test]
        public void OpenScreen_InstantiatesAndRunsLifecycle()
        {
            var screen = _uiManager.OpenScreenAsync<TestScreenView>("payload").GetAwaiter().GetResult();

            Assert.IsNotNull(screen);
            Assert.IsTrue(_uiManager.IsScreenOpen<TestScreenView>());
            Assert.AreEqual(1, screen.OpenCount);
            Assert.AreEqual("payload", screen.LastArgs);
            Assert.AreEqual(1, _uiManager.OpenScreenCount);
        }

        [Test]
        public void OpenScreen_TwiceReturnsSameInstance()
        {
            var first = _uiManager.OpenScreenAsync<TestScreenView>().GetAwaiter().GetResult();
            var second = _uiManager.OpenScreenAsync<TestScreenView>("again").GetAwaiter().GetResult();

            Assert.AreSame(first, second);
            Assert.AreEqual(1, _uiManager.OpenScreenCount);
            // Re-open does not re-run the open lifecycle
            Assert.AreEqual(1, second.OpenCount);
        }

        [Test]
        public void CloseScreen_PoolsAndRemovesFromActive()
        {
            var screen = _uiManager.OpenScreenAsync<TestScreenView>().GetAwaiter().GetResult();
            _uiManager.CloseScreenAsync<TestScreenView>().GetAwaiter().GetResult();

            Assert.IsFalse(_uiManager.IsScreenOpen<TestScreenView>());
            Assert.AreEqual(0, _uiManager.OpenScreenCount);
            Assert.AreEqual(1, screen.CloseCount);
            Assert.IsFalse(screen.gameObject.activeInHierarchy);
        }

        [Test]
        public void Reopen_ReusesPooledInstance()
        {
            var first = _uiManager.OpenScreenAsync<TestScreenView>().GetAwaiter().GetResult();
            _uiManager.CloseScreenAsync<TestScreenView>().GetAwaiter().GetResult();

            var second = _uiManager.OpenScreenAsync<TestScreenView>().GetAwaiter().GetResult();

            Assert.AreSame(first, second, "Pooled instance should be reused instead of a fresh instantiation");
            Assert.AreEqual(2, second.OpenCount);
        }

        [Test]
        public void CloseAll_ClosesEveryScreen()
        {
            _uiManager.OpenScreenAsync<TestScreenView>().GetAwaiter().GetResult();

            _uiManager.CloseAllAsync().GetAwaiter().GetResult();

            Assert.AreEqual(0, _uiManager.OpenScreenCount);
            Assert.IsFalse(_uiManager.IsScreenOpen<TestScreenView>());
        }

        [Test]
        public void OpenScreen_FiresScreenOpenedEvent_WithArgs()
        {
            object firedArgs = null;
            var screen = _uiManager.OpenScreenAsync<TestScreenView>().GetAwaiter().GetResult();
            screen.ScreenOpened += args => firedArgs = args;

            // Close pools the instance; the pooled reopen re-runs the open lifecycle
            // and raises ScreenOpened with the new payload.
            _uiManager.CloseScreenAsync<TestScreenView>().GetAwaiter().GetResult();
            _uiManager.OpenScreenAsync<TestScreenView>("reopen").GetAwaiter().GetResult();

            Assert.AreEqual("reopen", firedArgs);
        }

        [Test]
        public void CloseScreen_FiresScreenClosedEvent()
        {
            bool fired = false;
            var screen = _uiManager.OpenScreenAsync<TestScreenView>().GetAwaiter().GetResult();
            screen.ScreenClosed += () => fired = true;

            _uiManager.CloseScreenAsync<TestScreenView>().GetAwaiter().GetResult();

            Assert.IsTrue(fired);
        }

        [Test]
        public void OpenScreen_OnSpecifiedLayer_ParentsToLayerRoot()
        {
            var screen = _uiManager.OpenScreenAsync<TestScreenView>(layer: UILayer.HUD).GetAwaiter().GetResult();

            Assert.IsNotNull(screen);
            Assert.AreEqual("HUD", screen.transform.parent.name);
        }
    }
}
