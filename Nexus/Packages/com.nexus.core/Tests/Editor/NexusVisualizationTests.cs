using NUnit.Framework;
using Nexus.Editor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Tests.Editor
{
    [TestFixture]
    public class NexusVisualizationTests
    {
        [Test]
        public void UpdateSparkline_ReusesBarChildren_AcrossRefreshes()
        {
            // Regression: the old implementation called sparkline.Clear() then re-added bars on
            // every 0.5 s refresh, allocating hundreds of VisualElements per refresh while playing.
            var sparkline = NexusVisualization.CreateSparkline(new[] { 1f, 2f, 3f }, 10f, Color.green);
            var firstBar = sparkline[0];
            var secondBar = sparkline[1];

            NexusVisualization.UpdateSparkline(sparkline, new[] { 4f, 5f, 6f }, 10f, Color.green, 120f, 32f);

            Assert.AreEqual(3, sparkline.childCount, "Bar count must match the data length.");
            Assert.AreSame(firstBar, sparkline[0], "Bars must be reused, not recreated.");
            Assert.AreSame(secondBar, sparkline[1], "Bars must be reused, not recreated.");
        }

        [Test]
        public void UpdateSparkline_GrowsAndShrinks_BarCountTracksDataLength()
        {
            var sparkline = NexusVisualization.CreateSparkline(new[] { 1f, 2f, 3f }, 10f, Color.green);
            Assert.AreEqual(3, sparkline.childCount);

            NexusVisualization.UpdateSparkline(sparkline, new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f }, 10f, Color.green, 120f, 32f);
            Assert.AreEqual(7, sparkline.childCount, "Sparkline must grow when data grows.");

            NexusVisualization.UpdateSparkline(sparkline, new[] { 1f, 2f }, 10f, Color.green, 120f, 32f);
            Assert.AreEqual(2, sparkline.childCount, "Sparkline must shrink when data shrinks.");
        }

        [Test]
        public void UpdateSparkline_NullOrEmptyData_ClearsBars()
        {
            var sparkline = NexusVisualization.CreateSparkline(new[] { 1f, 2f, 3f }, 10f, Color.green);
            Assert.AreEqual(3, sparkline.childCount);

            NexusVisualization.UpdateSparkline(sparkline, null, 10f, Color.green, 120f, 32f);
            Assert.AreEqual(0, sparkline.childCount, "Null data must clear all bars.");

            NexusVisualization.UpdateSparkline(sparkline, System.Array.Empty<float>(), 10f, Color.green, 120f, 32f);
            Assert.AreEqual(0, sparkline.childCount, "Empty data must keep the sparkline clear.");
        }

        [Test]
        public void UpdateSparkline_NullSparkline_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
                NexusVisualization.UpdateSparkline(null, new[] { 1f, 2f }, 10f, Color.green, 120f, 32f));
        }
    }
}
