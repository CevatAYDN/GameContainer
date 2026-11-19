using System;
using System.Collections.Generic;
using Nexus.Ads;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    /// <summary>
    /// Issue #430: the engine-free core of the game-drawn consent flow. The host harness runs these (the pre-commit hook), and the
    /// editor runs them as NUnit; the UI Toolkit screen and the settings are tested in GameConsentFlowTests.
    /// </summary>
    public sealed class FullReviewAdsGameConsentTests
    {
        private sealed class MemoryStore : IPacingStore
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            public string Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
            public void Set(string key, string value) => Values[key] = value;
        }

        private sealed class FakeScreen : IConsentScreen
        {
            public int Shown, Hidden;
            public ConsentTexts LastTexts;
            public Action<bool> Choose;
            public void Show(ConsentTexts texts, Action<bool> onChoice) { Shown++; LastTexts = texts; Choose = onChoice; }
            public void Hide() { Hidden++; Choose = null; }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void RequireLog(List<string> log, params string[] expected)
        {
            Require(string.Join("|", log) == string.Join("|", expected), "expected [" + string.Join(", ", expected) + "] but got [" + string.Join(", ", log) + "]");
        }

        private static GameConsentFlow Flow(MemoryStore store, FakeScreen screen, List<string> log, int version = 1, bool noPersonalisation = false) =>
            new GameConsentFlow(new ConsentAnswerStore(store, version), screen, () => new ConsentTexts { Title = "t" }, a => log.Add("apply:" + a), noPersonalisation);

#if UNITY_EDITOR
        [Test]
#endif
        public void Request_CompletesOnlyAfterAChoice_AndTheAnswerReachesTheSdkBeforeTheCompletion()
        {
            var store = new MemoryStore(); var screen = new FakeScreen(); var log = new List<string>();
            var flow = Flow(store, screen, log);
            flow.Request(() => log.Add("completed"));
            Require(screen.Shown == 1, "the screen opens");
            Require(!flow.Completed && !flow.CanRequestAds, "nothing closes the screen without a choice");
            screen.Choose(true);
            Require(flow.Completed && flow.CanRequestAds, "answered");
            RequireLog(log, "apply:True", "completed");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ARepeatedRequest_WhileTheScreenIsOpen_ShowsOnceAndAnswersEveryCaller()
        {
            var screen = new FakeScreen(); var log = new List<string>();
            var flow = Flow(new MemoryStore(), screen, log);
            flow.Request(() => log.Add("a"));
            flow.Request(() => log.Add("b"));
            Require(screen.Shown == 1, "one screen");
            screen.Choose(false);
            RequireLog(log, "apply:False", "a", "b");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void AStoredAnswer_IsAppliedBeforeCompletionOnTheNextLaunch_WithoutTheScreen()
        {
            var store = new MemoryStore(); var first = new FakeScreen(); var log = new List<string>();
            Flow(store, first, log).Request(() => { });
            first.Choose(true);
            log.Clear();
            var second = new FakeScreen();
            var next = Flow(store, second, log);
            next.Request(() => log.Add("completed"));
            Require(second.Shown == 0 && next.Completed, "no screen, answered");
            RequireLog(log, "apply:True", "completed");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ABumpedVersion_AsksAgain_AndTheOldAnswerIsNotApplied()
        {
            var store = new MemoryStore(); var first = new FakeScreen(); var log = new List<string>();
            Flow(store, first, log, version: 1).Request(() => { });
            first.Choose(true);
            log.Clear();
            var second = new FakeScreen();
            var flow = Flow(store, second, log, version: 2);
            flow.Request(() => log.Add("completed"));
            Require(second.Shown == 1 && !flow.Completed, "version 2 asks again");
            Require(log.Count == 0, "the answer of version 1 says nothing about version 2");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void Reject_StillAllowsAds_AndIsAppliedAsNoConsent()
        {
            var screen = new FakeScreen(); var log = new List<string>();
            var flow = Flow(new MemoryStore(), screen, log);
            flow.Request(() => { });
            screen.Choose(false);
            Require(flow.Completed && flow.CanRequestAds, "a rejection still allows non-personalised ads");
            RequireLog(log, "apply:False");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ChangingTheAnswerInThePrivacyOptions_AppliesAtOnce_AndRaisesChanged()
        {
            var store = new MemoryStore(); var screen = new FakeScreen(); var log = new List<string>();
            var flow = Flow(store, screen, log);
            Require(!flow.PrivacyOptionsRequired, "no entry before the first answer");
            flow.Request(() => { });
            Require(!flow.PrivacyOptionsRequired, "no entry while the first screen is open");
            screen.Choose(true);
            Require(flow.PrivacyOptionsRequired, "the entry reopens the screen once answered");
            log.Clear();
            flow.Changed += () => log.Add("changed");
            flow.ShowPrivacyOptions(() => log.Add("closed"));
            Require(screen.Shown == 2, "the screen reopens");
            screen.Choose(false);
            RequireLog(log, "apply:False", "closed", "changed");
            Require(new ConsentAnswerStore(store, 1).TryRead(out bool accepted) && !accepted, "the next launch applies the new answer");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void AnUnreadableStoredAnswer_IsNoAnswer()
        {
            foreach (string raw in new[] { "", "1:", ":1", "1:2", "x:1", "11", "1:10", "-1:1" })
            {
                var store = new MemoryStore();
                store.Values[ConsentAnswerStore.Key] = raw;
                Require(!new ConsentAnswerStore(store, 1).TryRead(out _), "'" + raw + "' must read as no answer");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void DisposingTheFlow_RemovesAnOpenScreen_AndALateChoiceIsIgnored()
        {
            var store = new MemoryStore(); var screen = new FakeScreen(); var log = new List<string>();
            var flow = Flow(store, screen, log);
            flow.Request(() => log.Add("completed"));
            var choose = screen.Choose;
            ((IDisposable)flow).Dispose();
            Require(screen.Hidden == 1, "the open screen is removed");
            choose(true);
            Require(log.Count == 0, "a late choice completes nothing");
            Require(!new ConsentAnswerStore(store, 1).TryRead(out _), "an answer that arrives after disposal is not stored");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void TheScreenTexts_AreReadEachTimeItOpens_SoALanguageChangeApplies()
        {
            string title = "Privacy"; var screen = new FakeScreen();
            var flow = new GameConsentFlow(new ConsentAnswerStore(new MemoryStore(), 1), screen, () => new ConsentTexts { Title = title }, _ => { });
            flow.Request(() => { });
            Require(screen.LastTexts.Title == "Privacy", "first language");
            screen.Choose(true);
            title = "Gizlilik";
            flow.ShowPrivacyOptions(null);
            Require(screen.LastTexts.Title == "Gizlilik", "the next screen uses the new language");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void AChildDirectedOrUnderAgeBuild_ShowsNoScreen_AppliesARefusal_AndStoresNothing()
        {
            var store = new MemoryStore(); var screen = new FakeScreen(); var log = new List<string>();
            var flow = Flow(store, screen, log, noPersonalisation: true);
            flow.Request(() => log.Add("completed"));
            Require(screen.Shown == 0, "no screen");
            Require(flow.Completed && flow.CanRequestAds, "ads still load, without personalisation");
            Require(!flow.PrivacyOptionsRequired, "there is no choice to reopen");
            flow.ShowPrivacyOptions(() => log.Add("closed"));
            Require(screen.Shown == 0, "no screen from the options either");
            RequireLog(log, "apply:False", "completed", "closed");
            Require(!new ConsentAnswerStore(store, 1).TryRead(out _), "nothing is stored");
        }
    }
}
