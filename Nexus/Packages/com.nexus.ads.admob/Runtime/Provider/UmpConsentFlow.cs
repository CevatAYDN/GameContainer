using System;
using System.Collections.Generic;
using GoogleMobileAds.Ump.Api;
using Nexus.Ads.Unity;
using UnityEngine;

namespace Nexus.Ads.AdMob
{
    /// <summary>
    /// Google's User Messaging Platform as <see cref="IConsentFlow"/>. <see cref="Request"/> asks UMP for an update on
    /// every launch, as Google's privacy page requires, and shows the form where the law needs it; consent kept from an
    /// earlier session lets ads load while this session's update runs. <see cref="AdMobProvider"/> runs it for AdMob; with
    /// another mediation selected the bootstrap runs it before that SDK starts, and LevelPlay reads the consent UMP stored
    /// (issue #63). The form shown is the message published for the Google Mobile Ads App ID under AdMob's Privacy &amp;
    /// messaging. Every UMP callback (the listeners arrive on the Android UI thread) continues on the main thread.
    /// </summary>
    public sealed class UmpConsentFlow : IConsentFlow
    {
        private static readonly List<string> DebugDeviceIds = new List<string>();
        private static bool _eeaGeography;

        /// <summary>
        /// UMP's hashed device ids (UMP prints the id in logcat or the Xcode console; on Android it is the id the Google
        /// Mobile Ads SDK prints for test devices) and whether those devices see the form as in the EEA. Applied only by a
        /// flow created with debug settings allowed, which a release build never is. Call before the bootstrap starts.
        /// </summary>
        public static void SetDebugDevices(IEnumerable<string> hashedDeviceIds, bool eeaGeography = false)
        {
            DebugDeviceIds.Clear();
            if (hashedDeviceIds != null)
                foreach (var id in hashedDeviceIds) if (!string.IsNullOrWhiteSpace(id)) DebugDeviceIds.Add(id.Trim());
            _eeaGeography = eeaGeography;
        }

        internal static IReadOnlyList<string> DebugDevices => DebugDeviceIds;
        internal static bool HasDebugSettings => DebugDeviceIds.Count > 0 || _eeaGeography;

        private readonly bool _underAgeOfConsent;
        private readonly bool _debugAllowed;
        private readonly UnityMainThread _main = new UnityMainThread();
        private bool _requested, _answered;
        private Action _onAnswered;

        /// <summary>Create on the Unity main thread. <paramref name="debugAllowed"/>: the debug devices and geography may apply (never in a release build).</summary>
        public UmpConsentFlow(bool underAgeOfConsent, bool debugAllowed)
        {
            _underAgeOfConsent = underAgeOfConsent;
            _debugAllowed = debugAllowed;
        }

        internal bool DebugApplies => _debugAllowed && HasDebugSettings;

        /// <summary>
        /// The request the flow for <paramref name="config"/> sends: its age tag and, while debug settings may apply (never in
        /// a release build), the debug devices and geography. A status update at launch must send the same request, or the
        /// flow's later answer replaces it under another region or age tag (issue #121).
        /// </summary>
        public static ConsentRequestParameters RequestParameters(ProviderConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            return Parameters(config.UnderAgeOfConsent, DebugAllowedFor(config));
        }

        /// <summary>Debug settings may apply under a mediation's test mode or on Google's sample units; a release build has neither.</summary>
        internal static bool DebugAllowedFor(ProviderConfig config) => config.TestMode || config.UsesPublishedTestData;

        private static ConsentRequestParameters Parameters(bool underAgeOfConsent, bool debugAllowed)
        {
            var request = new ConsentRequestParameters { TagForUnderAgeOfConsent = underAgeOfConsent };
            if (debugAllowed && HasDebugSettings)
                request.ConsentDebugSettings = new ConsentDebugSettings
                {
                    DebugGeography = _eeaGeography ? DebugGeography.EEA : DebugGeography.Disabled,
                    TestDeviceHashedIds = new List<string>(DebugDeviceIds),
                };
            return request;
        }

        public bool Completed => _answered;
        public bool CanRequestAds => ConsentInformation.CanRequestAds();
        public bool PrivacyOptionsRequired => _answered && ConsentInformation.PrivacyOptionsRequirementStatus == PrivacyOptionsRequirementStatus.Required;
        public event Action Changed;

        public void Request(Action onCompleted)
        {
            if (_answered) { onCompleted?.Invoke(); return; }
            _onAnswered += onCompleted;
            if (_requested) return;
            _requested = true;
            ConsentInformation.Update(Parameters(_underAgeOfConsent, _debugAllowed), updateError => OnMain(() =>
            {
                if (updateError != null) Debug.LogWarning("[Nexus Ads] consent (Google UMP) update failed: " + updateError.Message);
                ConsentForm.LoadAndShowConsentFormIfRequired(formError => OnMain(() =>
                {
                    if (formError != null) Debug.LogWarning("[Nexus Ads] consent (Google UMP) form: " + formError.Message);
                    Answer();
                    Changed?.Invoke();
                }));
            }));
            // consent obtained in an earlier session: ads may load while this session's update runs
            if (ConsentInformation.CanRequestAds()) Answer();
        }

        public void ShowPrivacyOptions(Action onClosed)
        {
            ConsentForm.ShowPrivacyOptionsForm(error => OnMain(() =>
            {
                if (error != null) Debug.LogWarning("[Nexus Ads] consent (Google UMP) privacy options: " + error.Message);
                onClosed?.Invoke();
                Changed?.Invoke();
            }));
        }

        private void Answer()
        {
            _answered = true;
            var callback = _onAnswered;
            _onAnswered = null;
            callback?.Invoke();
        }

        private void OnMain(Action action)
        {
            if (_main.IsCurrent) action();
            else _main.Post(action);
        }
    }
}
