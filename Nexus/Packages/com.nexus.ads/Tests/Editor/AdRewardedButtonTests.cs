using NUnit.Framework;
using Nexus.Ads.Unity;
using UnityEngine;
using UnityEngine.UI;

namespace Nexus.Ads.Tests
{
    /// <summary>The rewarded button's not-ready presentation (#440): Hide stays invisible, ShowDisabled stays visible, labelled and not interactable.</summary>
    public sealed class AdRewardedButtonTests
    {
        private GameObject _go;
        private GameObject _label;
        private AdRewardedButton _button;
        private CanvasGroup _group;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("rewarded", typeof(RectTransform), typeof(Button), typeof(CanvasGroup));
            _label = new GameObject("no-ad-label");
            _label.transform.SetParent(_go.transform);
            _group = _go.GetComponent<CanvasGroup>();
            _go.SetActive(false); // wire the label before Awake/OnEnable run
            _button = _go.AddComponent<AdRewardedButton>();
            typeof(AdRewardedButton).GetField("_notReadyLabel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(_button, _label);
            _go.SetActive(true);
            // EditMode never runs Awake/OnEnable and AddComponent runs Reset (ShowDisabled); start like a component saved by an older version.
            typeof(AdRewardedButton).GetMethod("Awake", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(_button, null);
            _button.WhenNotReady = NotReadyBehavior.Hide;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_go);

        [Test]
        public void An_Unchanged_Component_Still_Hides_When_No_Ad_Is_Ready()
        {
            Assert.AreEqual(NotReadyBehavior.Hide, _button.WhenNotReady);
            Assert.AreEqual(0f, _group.alpha);
            Assert.IsFalse(_group.interactable);
            Assert.IsFalse(_group.blocksRaycasts);
            Assert.IsFalse(_label.activeSelf);
        }

        [Test]
        public void ShowDisabled_Keeps_The_Button_Visible_Not_Interactable_And_Labelled_When_No_Ad_Is_Ready()
        {
            _button.WhenNotReady = NotReadyBehavior.ShowDisabled;
            Assert.Greater(_group.alpha, 0f);
            Assert.IsFalse(_group.interactable);
            Assert.IsTrue(_label.activeSelf);
        }

        [Test]
        public void Switching_Back_To_Hide_Hides_The_Button_And_The_Label_Again()
        {
            _button.WhenNotReady = NotReadyBehavior.ShowDisabled;
            _button.WhenNotReady = NotReadyBehavior.Hide;
            Assert.AreEqual(0f, _group.alpha);
            Assert.IsFalse(_label.activeSelf);
        }

        [TestCase(NotReadyBehavior.Hide)]
        [TestCase(NotReadyBehavior.ShowDisabled)]
        public void A_Ready_Placement_Is_Fully_Shown_And_Unlabelled(NotReadyBehavior mode)
        {
            var v = AdRewardedButton.Present(false, true, mode);
            Assert.AreEqual(1f, v.Alpha);
            Assert.IsTrue(v.Interactable);
            Assert.IsTrue(v.BlocksRaycasts);
            Assert.IsFalse(v.ShowNotReadyLabel);
        }

        [TestCase(NotReadyBehavior.Hide, 0f, false)]
        [TestCase(NotReadyBehavior.ShowDisabled, 0.5f, false)]
        public void While_Its_Own_Ad_Is_Showing_It_Is_Never_Interactable_And_Never_Says_No_Ad(NotReadyBehavior mode, float alpha, bool label)
        {
            // even if the facade still reports the placement ready, a show in flight wins (#380 reentrancy)
            var v = AdRewardedButton.Present(true, true, mode);
            Assert.AreEqual(alpha, v.Alpha);
            Assert.IsFalse(v.Interactable);
            Assert.AreEqual(label, v.ShowNotReadyLabel);
        }
    }
}
