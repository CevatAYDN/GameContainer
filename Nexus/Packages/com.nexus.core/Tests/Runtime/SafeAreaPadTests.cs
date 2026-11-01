using System.Collections;
using NUnit.Framework;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Nexus.Tests.Runtime
{
    /// <summary>SafeArea.Pad on a live panel; the inset arithmetic is in the EditMode SafeAreaTests (issue #198).</summary>
    [TestFixture]
    public sealed class SafeAreaPadTests
    {
        private PanelSettings _panel;
        private GameObject _host;

        [SetUp]
        public void SetUp()
        {
            _panel = ScriptableObject.CreateInstance<PanelSettings>();
            _panel.themeStyleSheet = ScriptableObject.CreateInstance<ThemeStyleSheet>(); // without one Unity warns "No Theme Style Sheet"
            _host = new GameObject("[SafeAreaPadTests]");
            _host.SetActive(false);
            _host.AddComponent<UIDocument>().panelSettings = _panel;
            _host.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_host);
            Object.DestroyImmediate(_panel.themeStyleSheet);
            Object.DestroyImmediate(_panel);
        }

        private static StyleKeyword[] Paddings(VisualElement element) => new[]
        {
            element.style.paddingLeft.keyword, element.style.paddingTop.keyword, element.style.paddingRight.keyword, element.style.paddingBottom.keyword,
        };

        // Pad had no test (#198): an element padded before it is in a panel gets its insets when it joins one, and Dispose
        // takes the padding away again.
        [UnityTest]
        public IEnumerator Pad_PadsAnElementWhenItJoinsAPanel_AndDisposeClearsThePadding()
        {
            var element = new VisualElement();
            var pad = SafeArea.Pad(element);
            Assert.That(Paddings(element), Is.All.EqualTo(StyleKeyword.Null), "no panel, no insets");

            _host.GetComponent<UIDocument>().rootVisualElement.Add(element);
            float deadline = Time.realtimeSinceStartup + 5f;
            while (element.style.paddingTop.keyword == StyleKeyword.Null && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(Paddings(element), Is.All.EqualTo(StyleKeyword.Undefined), "the element was not padded when it joined the panel");

            pad.Dispose();
            Assert.That(Paddings(element), Is.All.EqualTo(StyleKeyword.Null), "Dispose left the padding");
        }
    }
}
