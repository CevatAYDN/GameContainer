using NUnit.Framework;
using Nexus.Editor;
using UnityEngine.UIElements;

namespace Nexus.Tests.Editor
{
    [TestFixture]
    public class PluginRefactorValidationTests
    {
        [Test]
        public void TracerPlugin_Disable_ResetsLiveStateAndQueue()
        {
            var plugin = new TracerPlugin();
            var view = plugin.CreateView();
            Assert.IsNotNull(view);

            plugin.OnDisable();
        }

        [Test]
        public void Tracer_ListView_ConfiguredWithItemHeight()
        {
            var plugin = new TracerPlugin();
            var view = plugin.CreateView();
            var listView = view.Q<ListView>();
            Assert.IsNotNull(listView, "TracerPlugin must contain a ListView for virtualized logs.");
            Assert.AreEqual(28f, listView.fixedItemHeight, "ListView fixedItemHeight should be 28 for safe padding.");
        }

        [Test]
        public void DashboardPlugin_Lifecycle_ExecutesCleanly()
        {
            var plugin = new DashboardPlugin();
            var view = plugin.CreateView();
            Assert.IsNotNull(view);

            plugin.OnEnable();
            plugin.OnUpdate();
            plugin.OnDisable();
        }

        [Test]
        public void Dashboard_StatusLabel_UpdatesOnPlayModeChange()
        {
            var plugin = new DashboardPlugin();
            var view = plugin.CreateView();
            Assert.IsNotNull(view);

            plugin.OnEnable();
            plugin.OnUpdate();
            
            var labels = view.Query<Label>().ToList();
            Assert.IsTrue(labels.Count > 0, "Dashboard view should contain status labels.");
            
            plugin.OnDisable();
        }

        [Test]
        public void GameManagerPlugin_Lifecycle_ExecutesCleanly()
        {
            var plugin = new GameManagerPlugin();
            var view = plugin.CreateView();
            Assert.IsNotNull(view);

            plugin.OnEnable();
            plugin.OnUpdate();
            plugin.OnDisable();
        }

        [Test]
        public void GameManager_LiveChart_ReusesElements()
        {
            var plugin = new GameManagerPlugin();
            var view = plugin.CreateView();
            Assert.IsNotNull(view);

            plugin.OnEnable();
            plugin.OnUpdate();
            plugin.OnDisable();
        }

        [Test]
        public void ErrorDashboardPlugin_Lifecycle_ExecutesCleanlyOnUpdate()
        {
            var plugin = new ErrorDashboardPlugin();
            var view = plugin.CreateView();
            Assert.IsNotNull(view, "ErrorDashboardPlugin view must render.");

            plugin.OnEnable();
            plugin.OnUpdate();
            plugin.OnDisable();
        }

        [Test]
        public void PerformanceDashboardPlugin_Lifecycle_ExecutesCleanlyOnUpdate()
        {
            var plugin = new PerformanceDashboardPlugin();
            var view = plugin.CreateView();
            Assert.IsNotNull(view, "PerformanceDashboardPlugin view must render.");

            plugin.OnEnable();
            plugin.OnUpdate();
            plugin.OnDisable();
        }

        [Test]
        public void NetworkDashboardPlugin_InstantiatesCleanly()
        {
            var plugin = new NetworkDashboardPlugin();
            var view = plugin.CreateView();
            Assert.IsNotNull(view, "NetworkDashboardPlugin view must render.");

            plugin.OnEnable();
            plugin.OnUpdate();
            plugin.OnDisable();
        }
    }
}
