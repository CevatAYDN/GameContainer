using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine.TestTools;
using Nexus.Ads.Unity;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Ads.Tests
{
    /// <summary>Issue #430: the UI Toolkit screen and the settings of the game-drawn consent flow; its engine-free core is tested in FullReviewAdsGameConsentTests.</summary>
    public sealed class GameConsentFlowTests
    {
        private sealed class NoScreen : IConsentScreen
        {
            public void Show(ConsentTexts texts, Action<bool> onChoice) { }
            public void Hide() { }
        }

        private sealed class MemoryStore : IPacingStore
        {
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>();
            public string Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
            public void Set(string key, string value) => _values[key] = value;
        }

        [Test]
        public void AChildDirectedOrUnderAgeBuild_LogsInEveryBuildThatItRefusesWithoutAScreen()
        {
            var answers = new ConsentAnswerStore(new MemoryStore(), 1);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("consent: child-directed or under-age build"));
            GameConsent.CreateFlow(answers, new NoScreen(), () => new ConsentTexts(), _ => { }, noPersonalisation: true);
            Assert.IsTrue(GameConsent.RefusedWithoutScreen);

            GameConsent.CreateFlow(answers, new NoScreen(), () => new ConsentTexts(), _ => { }, noPersonalisation: false);
            Assert.IsFalse(GameConsent.RefusedWithoutScreen, "an ordinary build asks again");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void TheSettings_FillEmptyPartnersAndPolicyAddress_ButNeverOverrideTheGamesOwn()
        {
            var settings = ScriptableObject.CreateInstance<AdsSettings>();
            try
            {
                settings.PrivacyPolicyUrl = "https://example.com/policy";
                settings.ConsentPartners.Add("Meta Audience Network");
                var filled = GameConsent.WithSettings(new ConsentTexts(), settings);
                CollectionAssert.Contains(filled.Partners, "Meta Audience Network");
                Assert.AreEqual("https://example.com/policy", filled.PrivacyPolicyUrl);
                var own = GameConsent.WithSettings(new ConsentTexts { Partners = new[] { "Mine" }, PrivacyPolicyUrl = "https://game.example/p" }, settings);
                CollectionAssert.AreEqual(new[] { "Mine" }, own.Partners);
                Assert.AreEqual("https://game.example/p", own.PrivacyPolicyUrl);
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void TheBuiltInScreen_GivesAcceptAndRejectTheSameWeight_AndPreselectsNeither()
        {
            var texts = new ConsentTexts { Title = "T", Body = "B", Accept = "Accept", Reject = "Reject", Partners = new[] { "P1", "P2" } };
            var chosen = new List<bool>();
            var built = ConsentScreenView.Build(texts, null, chosen.Add);
            Assert.AreEqual("Accept", built.Accept.text);
            Assert.AreEqual("Reject", built.Reject.text);
            Assert.AreEqual(built.Accept.style.flexGrow, built.Reject.style.flexGrow);
            Assert.AreEqual(built.Accept.style.minHeight, built.Reject.style.minHeight);
            Assert.AreEqual(built.Accept.style.fontSize, built.Reject.style.fontSize);
            Assert.AreEqual(built.Accept.style.backgroundColor, built.Reject.style.backgroundColor);
            Assert.AreEqual(built.Accept.style.color, built.Reject.style.color);
            Assert.IsFalse(built.Accept.focusable || built.Reject.focusable, "neither is pre-selected");
            Assert.AreEqual("P1\nP2", built.Root.Q<Label>(ConsentScreenNames.Partners).text);
        }

        [Test]
        public void TheBuiltInScreen_HidesThePrivacyLink_WithoutAWebAddress()
        {
            var texts = new ConsentTexts { PrivacyPolicyLabel = "Policy", PrivacyPolicyUrl = "javascript:alert(1)", Accept = "a", Reject = "r" };
            var link = ConsentScreenView.Build(texts, null, _ => { }).Root.Q<Label>(ConsentScreenNames.Policy);
            Assert.AreEqual(DisplayStyle.None, link.style.display.value, "only http and https addresses open");
            texts.PrivacyPolicyUrl = "https://example.com/privacy";
            link = ConsentScreenView.Build(texts, null, _ => { }).Root.Q<Label>(ConsentScreenNames.Policy);
            Assert.AreEqual(DisplayStyle.Flex, link.style.display.value);
        }

        [Test]
        public void PartnersFor_NamesLevelPlayAndUnityAds_AndAddsTheEnabledNetworksOnce()
        {
            var settings = ScriptableObject.CreateInstance<AdsSettings>();
            try
            {
                settings.ConsentPartners.AddRange(new[] { "Meta Audience Network", " Unity Ads ", "", "Meta Audience Network" });
                CollectionAssert.AreEqual(new[] { "ironSource / LevelPlay", "Unity Ads", "Meta Audience Network" }, GameConsent.PartnersFor(settings));
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }
    }
}
