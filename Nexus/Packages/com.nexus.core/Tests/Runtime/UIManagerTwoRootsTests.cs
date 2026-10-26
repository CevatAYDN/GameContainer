using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Tests
{
    /// <summary>
    /// Issue #67: UIManager instantiates screens under the shared [Nexus_UICanvas], which has no Root, so a screen's
    /// view looked for the scene's single Root and gave up with "Multiple Root instances found" when an app Root and a
    /// scene Root both existed; its mediator never bound (ChipCrisis kept its own UI stack for this reason).
    /// </summary>
    [TestFixture]
    [Category("RequiresPlayMode")]
    public sealed class UIManagerTwoRootsTests
    {
        public sealed class OwnedScreen : ScreenView
        {
            public IContext BoundContext => Context;
        }

        private sealed class UiLifecycle : IContextLifecycle
        {
            public void OnConfigure(IContextBuilder builder)
            {
                builder.BindInstance<IUIAssetProvider>(new ResourcesUIAssetProvider());
                builder.BindService<IUIManager, UIManager>();
            }

            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        private GameObject _shelf;

        [SetUp]
        public void SetUp() => Root.ClearRegistry();

        [TearDown]
        public void TearDown()
        {
            foreach (var root in UnityEngine.Object.FindObjectsByType<Root>(FindObjectsInactive.Include))
                UnityEngine.Object.DestroyImmediate(root.gameObject);
            var canvas = GameObject.Find("[Nexus_UICanvas]");
            if (canvas != null) UnityEngine.Object.DestroyImmediate(canvas);
            if (_shelf != null) UnityEngine.Object.DestroyImmediate(_shelf);
            Root.ClearRegistry();
        }

        private static Root CreateRoot(string scopeTag, IContextLifecycle lifecycle)
        {
            var go = new GameObject(scopeTag);
            go.SetActive(false);
            var root = go.AddComponent<Root>();
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.ScopeTag = scopeTag;
            data.AssemblyScopes = Array.Empty<string>();
            root.SetUp(data);
            if (lifecycle != null) root.RegisterLifecycle(lifecycle);
            go.SetActive(true);
            return root;
        }

        private static IEnumerator Await(Task task)
        {
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True, "the task did not finish in 5 s");
        }

        [UnityTest]
        public IEnumerator AScreenOpenedByAContextsUIManagerBindsToThatContextWhileTheSceneHoldsTwoRoots()
        {
            var app = CreateRoot("App", null);
            var shop = CreateRoot("Shop", new UiLifecycle());
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!(app.IsInitialized && shop.IsInitialized) && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(app.IsInitialized && shop.IsInitialized, Is.True, "the two contexts did not start");

            // An active prefab, kept under an inactive shelf so the prefab itself never registers.
            _shelf = new GameObject("Shelf");
            _shelf.SetActive(false);
            var prefab = new GameObject(nameof(OwnedScreen));
            prefab.transform.SetParent(_shelf.transform);
            prefab.AddComponent<OwnedScreen>();

            var ui = shop.Context.Container.Resolve<IUIManager>();
            ui.RegisterScreenPrefab<OwnedScreen>(prefab);

            var open = ui.OpenScreenAsync<OwnedScreen>();
            yield return Await(open);
            Assert.That(open.Result, Is.Not.Null);
            Assert.That(open.Result.BoundContext, Is.SameAs(shop.Context));

            // Closed into the pool and opened again: the re-enabled view binds to the same context.
            var close = ui.CloseScreenAsync<OwnedScreen>();
            yield return Await(close);
            var reopen = ui.OpenScreenAsync<OwnedScreen>();
            yield return Await(reopen);
            Assert.That(reopen.Result, Is.SameAs(open.Result));
            Assert.That(reopen.Result.BoundContext, Is.SameAs(shop.Context));
        }
    }
}
