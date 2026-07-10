using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;

namespace Nexus.Tests
{
    [TestFixture]
    public class WindowManagerAssetProviderTests
    {
        private class MockUIAssetProvider : IUIAssetProvider
        {
            public int InstantiateCount;
            public int ReleaseCount;
            public GameObject LastInstantiatedInstance;

            public Task<GameObject> InstantiateWindowAsync(string windowName, Transform parent)
            {
                InstantiateCount++;
                var go = new GameObject(windowName);
                if (parent != null)
                {
                    go.transform.SetParent(parent, false);
                }
                LastInstantiatedInstance = go;
                return Task.FromResult(go);
            }

            public void ReleaseWindow(GameObject windowInstance)
            {
                ReleaseCount++;
                if (windowInstance != null)
                {
                    UnityEngine.Object.DestroyImmediate(windowInstance);
                }
            }
        }

        [Test]
        public async Task WindowManager_UsesCustomUIAssetProvider()
        {
            // Setup DI Context
            var context = new Context();
            var mockProvider = new MockUIAssetProvider();
            
            // Bind mock provider
            context.Container.BindInstance<IUIAssetProvider>(mockProvider);

            var windowManager = new WindowManager();
            context.Container.BindInstance<IWindowManager>(windowManager);
            
            // Inject dependencies
            context.Container.Inject(windowManager);
            
            await windowManager.InitializeAsync(System.Threading.CancellationToken.None);

            // Open window
            var win = await windowManager.OpenWindowAsync("MockWindow", UILayer.Screen);
            
            Assert.IsNotNull(win);
            Assert.AreEqual("MockWindow", win.name);
            Assert.AreEqual(1, mockProvider.InstantiateCount);
            Assert.AreEqual(win, mockProvider.LastInstantiatedInstance);

            // Close window
            await windowManager.CloseWindowAsync("MockWindow");
            
            Assert.AreEqual(1, mockProvider.ReleaseCount);
            
            // Clean up
            context.Dispose();
        }
    }
}
