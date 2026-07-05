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

        [SetUp]
        public async Task SetUp()
        {
            _windowManager = new WindowManager();
            await _windowManager.InitializeAsync(default);
        }

        [TearDown]
        public void TearDown()
        {
            _windowManager.Dispose();
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
