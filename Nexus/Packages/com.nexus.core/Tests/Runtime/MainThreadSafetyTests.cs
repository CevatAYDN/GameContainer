using System;
using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Tests.Runtime
{
    [TestFixture]
    public class MainThreadSafetyTests
    {
        // Issue #34: SafeDestroy called Object.Destroy on whatever thread it ran on; from a worker
        // (a dispose off the main thread) Unity threw UnityException.
        [UnityTest]
        public IEnumerator SafeDestroy_FromAWorkerThread_DestroysOnTheMainThread()
        {
            var target = new GameObject("issue-34");
            Exception thrown = null;
            var worker = Task.Run(() =>
            {
                try { SafeDestroyUtility.SafeDestroy(target); }
                catch (Exception ex) { thrown = ex; }
            });
            while (!worker.IsCompleted) yield return null;
            // the posted destroy runs on the main thread; Destroy completes at the end of that frame
            yield return null;
            yield return null;

            Assert.IsNull(thrown, thrown?.ToString());
            Assert.IsTrue(target == null, "the object was destroyed");
        }

        public sealed class OpenScreen : ScreenView { }

        // Issue #205: UIManager kept its own destroy helper without the #34 post, and read screen.gameObject on the
        // disposing thread, so a dispose from a worker threw UnityException and left the screen alive.
        [UnityTest]
        public IEnumerator UIManagerDispose_FromAWorkerThread_DestroysItsScreensOnTheMainThread()
        {
            Tests.ScreenStackTests.Cleanup();
            var shelf = new GameObject("Shelf");
            try
            {
                var app = Tests.ScreenStackTests.CreateRoot("App", null, b =>
                {
                    b.BindInstance<IUIAssetProvider>(new ResourcesUIAssetProvider());
                    b.BindService<IUIManager, UIManager>();
                });
                yield return Tests.ScreenStackTests.WaitUntil(() => app.IsInitialized, "the context did not start");

                // An active prefab under an inactive shelf, so the prefab itself never registers as a view.
                shelf.SetActive(false);
                var prefab = new GameObject(nameof(OpenScreen));
                prefab.transform.SetParent(shelf.transform);
                prefab.AddComponent<OpenScreen>();
                var ui = (UIManager)app.Context.Container.Resolve<IUIManager>();
                ui.RegisterScreenPrefab<OpenScreen>(prefab);
                var open = ui.OpenScreenAsync<OpenScreen>();
                yield return Tests.ScreenStackTests.WaitUntil(() => open.IsCompleted, "the screen did not open");
                var screen = open.Result;
                Assert.That(screen != null, Is.True, "the screen opened");

                Exception thrown = null;
                var worker = Task.Run(() =>
                {
                    try { ui.Dispose(); }
                    catch (Exception ex) { thrown = ex; }
                });
                while (!worker.IsCompleted) yield return null;
                yield return null;
                yield return null;

                Assert.IsNull(thrown, thrown?.ToString());
                Assert.IsTrue(screen == null, "the screen was destroyed");
            }
            finally
            {
                Tests.ScreenStackTests.Cleanup();
                var canvas = GameObject.Find("[Nexus_UICanvas]");
                if (canvas != null) UnityEngine.Object.DestroyImmediate(canvas);
                UnityEngine.Object.DestroyImmediate(shelf);
            }
        }
    }
}
