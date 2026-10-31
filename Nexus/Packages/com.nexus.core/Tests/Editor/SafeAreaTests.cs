using NUnit.Framework;
using Nexus.Core.Services;
using UnityEngine;

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// Issue #76 (ADR-0003): <see cref="Screen.safeArea"/> is in pixels with its origin at the bottom left, the panel's
    /// padding in panel units from the top left; the top inset is the space above the safe area, not below it.
    /// </summary>
    [TestFixture]
    public sealed class SafeAreaTests
    {
        [Test]
        public void PanelInsets_ScaleThePixelInsetsToThePanel_WithTheTopMeasuredFromTheScreensTop()
        {
            // Portrait 1080x2400: a 110 px notch at the top, a 63 px gesture bar at the bottom, a panel at half scale.
            var portrait = SafeArea.PanelInsets(new Rect(0, 63, 1080, 2227), new Vector2(1080, 2400), new Vector2(540, 1200));
            Assert.That(portrait, Is.EqualTo(new Vector4(0, 55, 0, 31.5f)));

            // Landscape 2400x1080 with the notch on the left.
            var landscape = SafeArea.PanelInsets(new Rect(100, 0, 2300, 1080), new Vector2(2400, 1080), new Vector2(1200, 540));
            Assert.That(landscape, Is.EqualTo(new Vector4(50, 0, 0, 0)));
        }
    }
}
