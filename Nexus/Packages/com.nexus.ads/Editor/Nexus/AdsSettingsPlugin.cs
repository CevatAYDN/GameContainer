using System.Collections.Generic;
using Nexus.Ads.Unity;
using Nexus.Editor;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Ads.Editor
{
    /// <summary>
    /// The Ads section's Settings tab (#193): every ad setting of the one settings asset. Test and Release are two tabs of
    /// that asset; which one a build reads is fixed in <see cref="AdsSettings.UsesReleaseTab"/> and has no switch here.
    /// Badges come from <see cref="ProviderStatusResolver"/>, findings from <see cref="AdsValidator"/>; this tab owns no rule.
    /// </summary>
    internal sealed class AdsSettingsPlugin : NexusEditorPlugin
    {
        public override string Id => "AdsSettings";
        public override string DisplayName => NexusLang.Get("ads_tab_settings");
        public override int Order => 1;
        public override string Category => "cat_ads";
        public override Color IconColor => NexusEditorStyles.AccentGreen;

        private bool _release;
        private AdPlatform _platform = AdPlatform.Android;
        private readonly List<(PropertyField Field, Label Unused, AdFormat Format)> _units = new List<(PropertyField, Label, AdFormat)>();

        [MenuItem("Nexus/Ads/Settings", priority = 41)]
        private static void OpenTab() => NexusWindow.Open("AdsSettings");

        public override VisualElement CreateView()
        {
            var probe = new UnityProjectProbe(); // a disk scan, so once per view
            var catalog = PlatformCatalog.Load(fresh: true);
            var settings = AssetDatabase.LoadAssetAtPath<AdsSettings>(AdsProjectActions.AssetPath);

            var view = new VisualElement { style = { flexGrow = 1 } };
            var toolbar = NexusEditorStyles.CreateToolbar(NexusLang.Get("ads_tab_settings").ToUpper());
            NexusEditorStyles.AddToolbarButton(toolbar, NexusLang.Get("ads_refresh"), Rebuild);
            view.Add(toolbar);
            var scroll = new ScrollView { style = { flexGrow = 1 } };
            scroll.AddToClassList("nexus-scroll-content");
            view.Add(scroll);
            if (settings == null)
            {
                var none = AdsViews.Card(scroll, NexusLang.Get("ads_tab_settings"));
                none.Add(NexusEditorStyles.CreateBody(string.Format(NexusLang.Get("ads_no_asset"), AdsProjectActions.AssetPath)));
                none.Add(NexusEditorStyles.CreateButton(NexusLang.Get("ads_create_asset"), () => { AdsProjectActions.LoadOrCreate(); Rebuild(); }, NexusEditorStyles.BtnBlue));
                return view;
            }
            AdsProjectActions.FillEmptyAdMobTestTab(settings, catalog);
            _units.Clear();
            var so = new SerializedObject(settings);
            var kind = settings.SelectedMediation;

            var mediation = AdsViews.Card(scroll, NexusLang.Get("ads_mediation"));
            mediation.Add(Field(so, nameof(AdsSettings.SelectedMediation)));
            mediation.Add(Field(so, nameof(AdsSettings.AdFreeRelease)));
            // the keys, the guide, Prepare Android and the privacy fields follow the mediation
            // only on a real change: a callback at bind time would rebuild the tab on every build
            mediation.TrackPropertyValue(so.FindProperty(nameof(AdsSettings.SelectedMediation)), p => { if (p.intValue != (int)kind) view.schedule.Execute(Rebuild); });
            foreach (AdPlatform platform in new[] { AdPlatform.Android, AdPlatform.Ios })
            {
                var status = AdsViews.SelectedStatus(settings, platform, probe);
                mediation.Add(AdsViews.Badge(platform + ": " + status, status.Status));
            }
            mediation.Add(AdsViews.Switch(new[] { "Android", "iOS" }, _platform == AdPlatform.Ios ? 1 : 0, i => { _platform = i == 1 ? AdPlatform.Ios : AdPlatform.Android; Rebuild(); }));
            mediation.Add(AdsViews.Outlook(settings, _platform, probe));

            var keys = AdsViews.Card(scroll, NexusLang.Get("ads_keys"));
            keys.Add(NexusEditorStyles.CreateHint(NexusLang.Get("ads_keys_hint")));
            keys.Add(AdsViews.Switch(new[] { "Test", "Release" }, _release ? 1 : 0, i => { _release = i == 1; Rebuild(); }));
            if (kind == AdProviderKind.None) keys.Add(new HelpBox(NexusLang.Get("ads_select_mediation"), HelpBoxMessageType.Info));
            else BuildKeys(keys, so, settings, catalog, kind);

            if (_platform == AdPlatform.Android && kind != AdProviderKind.None)
            {
                var android = AdsViews.Card(scroll, string.Format(NexusLang.Get("ads_prepare_android_tab"), _release ? "Release" : "Test"));
                var plan = AdsViews.AndroidPlan(settings, Tab, probe, catalog, Rebuild);
                android.Add(plan);
                // the plan follows the keys and the mediation; it reads files, as the page's plan did on each change
                android.TrackSerializedObjectValue(so, _ => AdsViews.Rebuild(plan, settings, Tab, probe, catalog, Rebuild));
            }

            var placements = AdsViews.Card(scroll, NexusLang.Get("ads_placements"));
            placements.Add(Field(so, nameof(AdsSettings.Placements)));
            placements.Add(Field(so, nameof(AdsSettings.GeneratedPlacementsPath)));
            placements.Add(Button(NexusLang.Get("ads_generate_placements"), AdsProjectActions.GeneratePlacements));
            placements.TrackPropertyValue(so.FindProperty(nameof(AdsSettings.Placements)), _ => UpdateUnits(settings));

            var policy = AdsViews.Card(scroll, NexusLang.Get("ads_policy_floors"));
            policy.Add(Field(so, nameof(AdsSettings.Policy)));

            var privacy = AdsViews.Card(scroll, NexusLang.Get("ads_privacy"));
            if (kind == AdProviderKind.LevelPlay) // LevelPlay ships no consent form (issue #154)
                privacy.Add(Field(so, nameof(AdsSettings.Consent), NexusLang.Get("ads_consent_levelplay")));
            privacy.Add(Field(so, nameof(AdsSettings.ChildDirected)));
            privacy.Add(Field(so, nameof(AdsSettings.UnderAgeOfConsent)));
            privacy.Add(Field(so, nameof(AdsSettings.MaxAdContentRating)));
            privacy.Add(Field(so, nameof(AdsSettings.PrivacyPolicyUrl)));

            var appAds = AdsViews.Card(scroll, "app-ads.txt");
            appAds.Add(Field(so, nameof(AdsSettings.AppAdsTxtLines)));
            appAds.Add(Button(NexusLang.Get("ads_generate_app_ads"), () => AdsProjectActions.WriteAppAdsTxt(settings)));

            var validation = AdsViews.Card(scroll, NexusLang.Get("ads_validation"));
            var report = new VisualElement();
            var row = AdsViews.Row(validation);
            row.Add(NexusEditorStyles.CreateButton(NexusLang.Get("ads_validate_test"), () => AdsViews.ShowReport(report, AdsValidator.Validate(settings, AdsValidator.Tab.Test, _platform, probe, catalog)), NexusEditorStyles.BtnBlue));
            row.Add(NexusEditorStyles.CreateButton(NexusLang.Get("ads_validate_release"), () => AdsViews.ShowReport(report, AdsValidator.Validate(settings, AdsValidator.Tab.Release, _platform, probe, catalog)), NexusEditorStyles.BtnBlue));
            validation.Add(report);

            scroll.Bind(so);
            return view;
        }

        private AdsValidator.Tab Tab => _release ? AdsValidator.Tab.Release : AdsValidator.Tab.Test;

        private void Rebuild() => Window?.RefreshActivePlugin();

        private void BuildKeys(VisualElement card, SerializedObject so, AdsSettings settings, PlatformCatalog catalog, AdProviderKind kind)
        {
            var keysProp = so.FindProperty(_release ? nameof(AdsSettings.Release) : nameof(AdsSettings.Test))
                .FindPropertyRelative(kind.ToString()).FindPropertyRelative(_platform.ToString());
            var fields = new VisualElement();
            // Google's sample ids are the AdMob Test tab: locked, every device gets test ads with them
            fields.SetEnabled(_release || kind != AdProviderKind.AdMob);
            fields.Add(new PropertyField(keysProp.FindPropertyRelative(nameof(PlatformKeys.AppKey)), kind == AdProviderKind.AdMob ? "App ID" : "App / SDK Key"));
            foreach (var (format, name) in new[] { (AdFormat.Rewarded, nameof(PlatformKeys.RewardedUnitId)), (AdFormat.Interstitial, nameof(PlatformKeys.InterstitialUnitId)), (AdFormat.Banner, nameof(PlatformKeys.BannerUnitId)) })
            {
                var unit = new PropertyField(keysProp.FindPropertyRelative(name), format + " unit id");
                var unused = NexusEditorStyles.CreateHint(NexusLang.Get("ads_unit_unused"));
                _units.Add((unit, unused, format));
                fields.Add(unit);
                fields.Add(unused);
            }
            UpdateUnits(settings);
            card.Add(fields);

            if (!_release)
            {
                if (kind == AdProviderKind.AdMob)
                {
                    card.Add(NexusEditorStyles.CreateHint(NexusLang.Get("ads_admob_test_locked")));
                    card.Add(Button(NexusLang.Get("ads_reset_test_data"), () => { AdsProjectActions.ResetAdMobTestData(settings, catalog, _platform); so.Update(); }));
                }
                else
                {
                    card.Add(NexusEditorStyles.CreateHint(string.Format(NexusLang.Get("ads_no_test_keys"), kind)));
                    card.Add(Button(NexusLang.Get("ads_copy_release"), () => { AdsProjectActions.CopyReleaseToTest(settings, kind, _platform); so.Update(); }));
                }
                card.Add(Field(so, nameof(AdsSettings.TestDeviceIds), NexusLang.Get("ads_test_devices")));
            }
            BuildGuide(card, catalog.Provider(kind));
        }

        /// <summary>A unit field is open only while a placement uses its format.</summary>
        private void UpdateUnits(AdsSettings settings)
        {
            foreach (var (field, unused, format) in _units)
            {
                bool used = settings.Uses(format);
                field.SetEnabled(used);
                unused.style.display = used ? DisplayStyle.None : DisplayStyle.Flex;
            }
        }

        /// <summary>The selected provider's dashboard steps and test-device location, from the catalog only.</summary>
        private static void BuildGuide(VisualElement parent, CatalogProvider provider)
        {
            if (provider == null) return;
            var guide = new Foldout { text = string.Format(NexusLang.Get("ads_guide"), provider.name), value = true };
            guide.style.marginTop = 6;
            var steps = AdsViews.Turkish ? provider.steps?.tr : provider.steps?.en;
            if (steps != null)
                for (int i = 0; i < steps.Length; i++) guide.Add(NexusEditorStyles.CreateHint((i + 1) + ". " + steps[i]));
            if (!string.IsNullOrEmpty(provider.sdk?.sdkKeyLocation)) guide.Add(NexusEditorStyles.CreateHint("SDK key: " + provider.sdk.sdkKeyLocation));
            var devices = provider.testData?.testDevices;
            string where = devices?.where ?? devices?.dashboard;
            if (!string.IsNullOrEmpty(where)) guide.Add(NexusEditorStyles.CreateHint(string.Format(NexusLang.Get("ads_test_devices_where"), where)));
            parent.Add(guide);
        }

        private static PropertyField Field(SerializedObject so, string name, string label = null)
            => label == null ? new PropertyField(so.FindProperty(name)) : new PropertyField(so.FindProperty(name), label);

        private static Button Button(string text, System.Action onClick)
        {
            var button = NexusEditorStyles.CreateButton(text, onClick, NexusEditorStyles.BtnGray);
            button.style.alignSelf = Align.FlexStart;
            button.style.marginTop = 4;
            return button;
        }
    }
}
