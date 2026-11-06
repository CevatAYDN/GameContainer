using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Tests.Runtime
{
    /// <summary>UIManager across its own lifetime: an open that outlives it, its asset provider, the shared canvas (issue #185).</summary>
    [TestFixture]
    public class UIManagerLifetimeTests
    {
        public class ProvidedScreen : ScreenView { }

        /// <summary>Hands out instances when the test says so, and records what comes back.</summary>
        private sealed class ManualAssetProvider : IUIAssetProvider
        {
            public readonly List<GameObject> Released = new();
            public TaskCompletionSource<GameObject> Pending;
            private Transform _parent;

            public Task<GameObject> InstantiateWindowAsync(string windowName, Transform parent)
            {
                _parent = parent;
                Pending = new TaskCompletionSource<GameObject>();
                return Pending.Task;
            }

            public GameObject Create()
            {
                var instance = new GameObject(nameof(ProvidedScreen));
                instance.transform.SetParent(_parent, false);
                instance.AddComponent<ProvidedScreen>();
                return instance;
            }

            public void ReleaseWindow(GameObject windowInstance)
            {
                Released.Add(windowInstance);
                if (windowInstance != null) Object.Destroy(windowInstance);
            }
        }

        private ManualAssetProvider _provider;
        private UIManager _ui;

        [TearDown]
        public void TearDown()
        {
            Tests.ScreenStackTests.Cleanup();
            var canvas = GameObject.Find("[Nexus_UICanvas]");
            if (canvas != null) Object.DestroyImmediate(canvas);
        }

        // A context's UIManager loading its screens through the manual provider, so the screens' views have a context.
        private IEnumerator StartManager()
        {
            _provider = new ManualAssetProvider();
            var root = Tests.ScreenStackTests.CreateRoot("App", null, b =>
            {
                b.BindInstance<IUIAssetProvider>(_provider);
                b.BindService<IUIManager, UIManager>();
            });
            yield return Tests.ScreenStackTests.WaitUntil(() => root.IsInitialized, "the context did not start");
            _ui = (UIManager)root.Context.Container.Resolve<IUIManager>();
        }

        private IEnumerator Open(GameObject instance, Task<ProvidedScreen> open)
        {
            _provider.Pending.SetResult(instance);
            yield return Await(open);
        }

        private static IEnumerator Await(Task task)
        {
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True, "the task did not finish in 5 s");
        }

        private void AssertReleasedOnly(GameObject instance)
        {
            Assert.AreEqual(1, _provider.Released.Count);
            Assert.IsTrue(ReferenceEquals(instance, _provider.Released[0]));
        }

        // An open still loading its asset when the manager was disposed registered the screen afterwards, and nothing
        // destroyed it: an orphan on the shared canvas (the second review's B1).
        [UnityTest]
        public IEnumerator AnOpenThatFinishesAfterDispose_HandsTheScreenBackToTheProvider()
        {
            yield return StartManager();
            var open = _ui.OpenScreenAsync<ProvidedScreen>();
            _ui.Dispose();
            var instance = _provider.Create();
            yield return Open(instance, open);

            Assert.IsNull(open.Result);
            AssertReleasedOnly(instance);
            Assert.AreEqual(0, _ui.OpenScreenCount);
        }

        // IUIAssetProvider.ReleaseWindow was never called, so an Addressables provider leaked every instance.
        [UnityTest]
        public IEnumerator ScreensFromTheProvider_GoBackThroughIt_WhenTheManagerIsDisposed()
        {
            yield return StartManager();
            var open = _ui.OpenScreenAsync<ProvidedScreen>();
            var instance = _provider.Create();
            yield return Open(instance, open);
            Assert.IsTrue(open.Result != null);

            _ui.Dispose();

            AssertReleasedOnly(instance);
        }

        // A pooled screen destroyed outside the manager was dropped without going back through the provider.
        [UnityTest]
        public IEnumerator APooledProviderScreenDestroyedOutsideTheManager_GoesBackThroughTheProvider()
        {
            yield return StartManager();
            var first = _provider.Create();
            yield return Open(first, _ui.OpenScreenAsync<ProvidedScreen>());
            yield return Await(_ui.CloseScreenAsync<ProvidedScreen>());
            Object.DestroyImmediate(first);

            var reopen = _ui.OpenScreenAsync<ProvidedScreen>();
            var second = _provider.Create();
            yield return Open(second, reopen);

            Assert.IsTrue(reopen.Result != null && ReferenceEquals(second, reopen.Result.gameObject));
            AssertReleasedOnly(first);
        }

        // UnregisterScreenPrefab had no test (#198): after it the screen loads through the asset provider again, not
        // from the prefab that was registered.
        [UnityTest]
        public IEnumerator AnUnregisteredPrefab_IsNotUsed_AndTheScreenLoadsThroughTheProvider()
        {
            yield return StartManager();
            var prefab = new GameObject("RegisteredPrefab");
            prefab.SetActive(false); // a loose screen in the scene would look for its context
            prefab.AddComponent<ProvidedScreen>();
            try
            {
                _ui.RegisterScreenPrefab<ProvidedScreen>(prefab);
                _ui.UnregisterScreenPrefab<ProvidedScreen>();

                var open = _ui.OpenScreenAsync<ProvidedScreen>();
                Assert.IsNotNull(_provider.Pending, "the open used the unregistered prefab instead of the provider");
                var instance = _provider.Create();
                yield return Open(instance, open);

                Assert.IsTrue(open.Result != null && ReferenceEquals(instance, open.Result.gameObject));
            }
            finally
            {
                Object.Destroy(prefab);
            }
        }

        // The DontDestroyOnLoad canvas was never destroyed, though the comments said teardown cleaned it up. The last
        // manager to go destroys the canvas a manager created, and leaves one the scene holds.
        [UnityTest]
        public IEnumerator TheLastManagerDestroysTheCanvasAManagerCreated_AndLeavesASceneCanvas()
        {
            var first = new UIManager();
            var second = new UIManager();
            first.InitializeAsync(default).GetAwaiter().GetResult();
            second.InitializeAsync(default).GetAwaiter().GetResult();
            var created = GameObject.Find("[Nexus_UICanvas]");
            Assert.IsNotNull(created);

            first.Dispose();
            yield return null;
            Assert.IsTrue(created != null, "the second manager still uses the canvas");

            second.Dispose();
            yield return null;
            Assert.IsTrue(created == null, "the last manager destroyed the canvas it created");

            var sceneCanvas = new GameObject("[Nexus_UICanvas]");
            var third = new UIManager();
            third.InitializeAsync(default).GetAwaiter().GetResult();
            third.Dispose();
            yield return null;
            Assert.IsTrue(sceneCanvas != null, "a canvas the scene holds is the scene's");
        }
    }
}
