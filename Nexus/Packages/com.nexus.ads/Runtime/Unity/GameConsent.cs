using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nexus.Ads.Unity
{
    /// <summary>
    /// Registers the shared, game-drawn consent flow (issue #430) for a LevelPlay game without AdMob: one line, before
    /// <c>AdsBootstrap.StartAds()</c>, with <c>Consent: GameFlow</c> in the settings (or <c>GameFlow</c> in the platform's own <c>AndroidConsent</c> / <c>IosConsent</c>, issue #431).
    /// <code>
    /// GameConsent.Register(() => texts, version: 1, apply: LevelPlayProvider.ApplyConsent);
    /// </code>
    /// The answer lives in PlayerPrefs with the version; the analytics reader of <c>com.nexus.ads.bridge.nexus</c>
    /// (<c>GameConsentAnalyticsConsent</c>) reads the same <see cref="Answers"/>.
    /// </summary>
    public static class GameConsent
    {
        /// <summary>The partners every LevelPlay game names; <see cref="AdsSettings.ConsentPartners"/> adds the enabled networks.</summary>
        public static readonly IReadOnlyList<string> BuiltInPartners = new[] { "ironSource / LevelPlay", "Unity Ads" };

        /// <summary>The answer store of the registered flow; null before <see cref="Register"/>.</summary>
        public static ConsentAnswerStore Answers { get; private set; }

        /// <summary>Raised when the player gives a new answer (the first one or one from the privacy options).</summary>
        public static event Action AnswerChanged;

        /// <summary>
        /// Gives a runtime panel the game's UI input (the Nexus bridge sets it to <c>ToolkitPanelInput.Attach</c> of the one in the scene, which a
        /// game that routes Toolkit input through an EventSystem needs for a document created after its scene loaded). Null: no hook.
        /// </summary>
        public static Action<UnityEngine.UIElements.UIDocument> AttachInput { get; set; }

        /// <summary>
        /// Registers the flow. Empty <see cref="ConsentTexts.Partners"/> and <see cref="ConsentTexts.PrivacyPolicyUrl"/> come from the
        /// ad settings, and a child-directed or under-age build shows no screen and applies a refusal.
        /// </summary>
        /// <param name="texts">The screen's texts from the game's localization, read each time the screen opens.</param>
        /// <param name="version">The question's version, from 1: raise it when partners or texts change and every player is asked again.</param>
        /// <param name="apply">Forwards the answer to the mediation; <c>LevelPlayProvider.ApplyConsent</c> for LevelPlay.</param>
        /// <param name="style">The game's own look; null shows the built-in screen.</param>
        public static void Register(Func<ConsentTexts> texts, int version, Action<bool> apply, ConsentScreenStyle style = null)
        {
            if (texts == null) throw new ArgumentNullException(nameof(texts));
            if (apply == null) throw new ArgumentNullException(nameof(apply));
            var answers = new ConsentAnswerStore(new PlayerPrefsPacingStore(), version);
            answers.Changed += () => AnswerChanged?.Invoke();
            Answers = answers;
            AdsBootstrap.RegisterSharedConsentFlow(ConsentSource.GameFlow, cfg =>
            {
                var settings = AdsBootstrap.Instance != null ? AdsBootstrap.Instance.Settings : null;
                // the settings are the single source: an empty partner list or policy address comes from them
                Func<ConsentTexts> resolved = () => WithSettings(texts(), settings);
                // personalised ads are never allowed for a child-directed or under-age build, as the provider's COPPA flag says
                return CreateFlow(answers, new UitkConsentScreen(style), resolved, apply, cfg.ChildDirected || cfg.UnderAgeOfConsent);
            });
            AnswerChanged?.Invoke(); // a reader created before this call sees the question
        }

        /// <summary>
        /// True when the registered flow runs in a child-directed or under-age build: no screen, a refusal applied at every start,
        /// and nothing stored. The analytics reader then reports a refusal too, so nothing is sent.
        /// </summary>
        public static bool RefusedWithoutScreen { get; private set; }

        internal static GameConsentFlow CreateFlow(ConsentAnswerStore answers, IConsentScreen screen, Func<ConsentTexts> texts, Action<bool> apply, bool noPersonalisation)
        {
            RefusedWithoutScreen = noPersonalisation;
            if (noPersonalisation)
                Debug.LogWarning("[Nexus Ads] consent: child-directed or under-age build: the consent screen is not shown and a refusal is applied (non-personalised ads, no analytics)");
            AnswerChanged?.Invoke(); // the analytics reader re-reads the state the flag decides
            return new GameConsentFlow(answers, screen, texts, apply, noPersonalisation);
        }

        internal static ConsentTexts WithSettings(ConsentTexts texts, AdsSettings settings)
        {
            if (texts.Partners == null || texts.Partners.Count == 0) texts.Partners = PartnersFor(settings);
            if (string.IsNullOrEmpty(texts.PrivacyPolicyUrl) && settings != null) texts.PrivacyPolicyUrl = settings.PrivacyPolicyUrl;
            return texts;
        }

        /// <summary>The built-in partners and the settings' extra ones, without duplicates, for <see cref="ConsentTexts.Partners"/>.</summary>
        public static IReadOnlyList<string> PartnersFor(AdsSettings settings)
        {
            var partners = new List<string>(BuiltInPartners);
            if (settings != null)
                foreach (string name in settings.ConsentPartners)
                    if (!string.IsNullOrWhiteSpace(name) && !partners.Contains(name.Trim())) partners.Add(name.Trim());
            return partners;
        }
    }
}
