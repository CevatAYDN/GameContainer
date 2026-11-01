using Nexus.Ads.Unity;
using Nexus.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Nexus.Ads.Editor
{
    /// <summary>
    /// The Ads section's Setup tab (#193): from nothing to a live ad. Rows come from the catalog, badges from
    /// <see cref="ProviderStatusResolver"/>, the findings from <see cref="AdsValidator"/>. Links open the official pages;
    /// Install SDK adds the UPM package where the catalog names a registry Unity can reach; Prepare Android performs the
    /// selected mediation's Android setup (<see cref="AndroidSetup"/>).
    /// </summary>
    internal sealed class AdsSetupPlugin : NexusEditorPlugin
    {
        private const string PrefRegistered = "NexusAds.Registered.";

        public override string Id => "AdsSetup";
        public override string DisplayName => NexusLang.Get("ads_tab_setup");
        public override int Order => 0;
        public override string Category => "cat_ads";
        public override Color IconColor => NexusEditorStyles.AccentGreen;

        private UnityProjectProbe _probe;
        private PlatformCatalog _catalog;
        private AdsValidator.Tab _androidTab = AdsValidator.Tab.Test;

        [MenuItem("Nexus/Ads/Setup", priority = 40)]
        private static void OpenTab() => NexusWindow.Open("AdsSetup");

        public override VisualElement CreateView()
        {
            // a disk scan, so once per view: Refresh and the tab's actions rebuild it
            _probe = new UnityProjectProbe();
            _catalog = PlatformCatalog.Load(fresh: true);
            var settings = AssetDatabase.LoadAssetAtPath<AdsSettings>(AdsProjectActions.AssetPath);

            var view = new VisualElement { style = { flexGrow = 1 } };
            var toolbar = NexusEditorStyles.CreateToolbar(NexusLang.Get("ads_tab_setup").ToUpper());
            NexusEditorStyles.AddToolbarButton(toolbar, NexusLang.Get("ads_refresh"), Rebuild);
            view.Add(toolbar);
            var scroll = new ScrollView { style = { flexGrow = 1 } };
            scroll.AddToClassList("nexus-scroll-content");
            view.Add(scroll);
            scroll.Add(NexusEditorStyles.CreateHint(string.Format(NexusLang.Get("ads_catalog_read"), _catalog.readDate)));

            var settingsCard = AdsViews.Card(scroll, NexusLang.Get("ads_setup_settings"));
            if (settings == null)
            {
                settingsCard.Add(NexusEditorStyles.CreateBody(string.Format(NexusLang.Get("ads_no_asset"), AdsProjectActions.AssetPath)));
                settingsCard.Add(NexusEditorStyles.CreateButton(NexusLang.Get("ads_create_asset"), () => { AdsProjectActions.LoadOrCreate(); Rebuild(); }, NexusEditorStyles.BtnBlue));
                return view;
            }
            settingsCard.Add(NexusEditorStyles.CreateButton(NexusLang.Get("ads_open_settings"), () => Window?.SwitchToPlugin("AdsSettings"), NexusEditorStyles.BtnGray));
            settingsCard.Add(AdsViews.Outlook(settings, AdPlatform.Android, _probe));

            BuildProviders(scroll, settings);
            BuildNetworks(scroll);

            var project = AdsViews.Card(scroll, NexusLang.Get("ads_setup_project"));
            var projectRow = AdsViews.Row(project);
            projectRow.Add(NexusEditorStyles.CreateButton(NexusLang.Get("ads_generate_placements"), AdsProjectActions.GeneratePlacements, NexusEditorStyles.BtnGray));
            projectRow.Add(NexusEditorStyles.CreateButton("link.xml", AdsProjectActions.WriteLinkXml, NexusEditorStyles.BtnGray));
            projectRow.Add(NexusEditorStyles.CreateButton(NexusLang.Get("ads_pin_packages"), AdsProjectActions.PinPackages, NexusEditorStyles.BtnGray));

            var android = AdsViews.Card(scroll, NexusLang.Get("ads_setup_android"));
            android.Add(AdsViews.Switch(new[] { NexusLang.Get("ads_tab_test_dev"), NexusLang.Get("ads_tab_release") }, (int)_androidTab,
                i => { _androidTab = (AdsValidator.Tab)i; Rebuild(); }));
            android.Add(AdsViews.AndroidPlan(settings, _androidTab, _probe, _catalog, Rebuild));

            BuildChecklist(scroll);

            var validate = AdsViews.Card(scroll, NexusLang.Get("ads_setup_validate"));
            var report = new VisualElement();
            var validateRow = AdsViews.Row(validate);
            validateRow.Add(NexusEditorStyles.CreateButton(NexusLang.Get("ads_validate_test"), () => AdsViews.ShowReport(report, AdsValidator.Validate(settings, AdsValidator.Tab.Test, AdPlatform.Android, _probe, _catalog)), NexusEditorStyles.BtnBlue));
            validateRow.Add(NexusEditorStyles.CreateButton(NexusLang.Get("ads_validate_release"), () => AdsViews.ShowReport(report, AdsValidator.Validate(settings, AdsValidator.Tab.Release, AdPlatform.Android, _probe, _catalog)), NexusEditorStyles.BtnBlue));
            validate.Add(report);
            return view;
        }

        private void Rebuild() => Window?.RefreshActivePlugin();

        private void BuildProviders(VisualElement parent, AdsSettings settings)
        {
            var card = AdsViews.Card(parent, NexusLang.Get("ads_setup_providers"));
            bool turkish = AdsViews.Turkish;
            bool conflict = _probe.SdkInstalled(AdProviderKind.LevelPlay) && _probe.SdkInstalled(AdProviderKind.Max);
            foreach (var provider in _catalog.providers)
            {
                var kind = provider.Kind;
                var box = NexusEditorStyles.CreateCard(NexusEditorStyles.CardBg);
                box.style.marginTop = 6;
                box.Add(NexusEditorStyles.CreateTitle(provider.name + " (" + provider.role + ", " + provider.when + ")" + (settings.SelectedMediation == kind ? "  ← " + NexusLang.Get("ads_selected") : ""), NexusEditorStyles.TextPrimary));
                foreach (AdPlatform platform in new[] { AdPlatform.Android, AdPlatform.Ios })
                {
                    bool sdk = _probe.SdkInstalled(kind) && _probe.ProviderPackageInstalled(kind);
                    var status = ProviderStatusResolver.Resolve(settings, kind, platform, sdk, conflict);
                    box.Add(AdsViews.Badge(platform + ": " + status, status.Status));
                    if (status.Status == ProviderStatus.NotInstalled) box.Add(NexusEditorStyles.CreateHint(NexusLang.Get("ads_next_install")));
                    else if (status.Missing.Count > 0) box.Add(NexusEditorStyles.CreateHint(string.Format(NexusLang.Get("ads_next_release_keys"), string.Join(", ", status.Missing))));
                }
                var links = AdsViews.Row(box);
                links.Add(NexusEditorStyles.CreateButton(NexusLang.Get("ads_registration"), () => Application.OpenURL(provider.registration), NexusEditorStyles.BtnGray));
                links.Add(NexusEditorStyles.CreateButton(NexusLang.Get("ads_docs"), () => Application.OpenURL(provider.docs), NexusEditorStyles.BtnGray));
                links.Add(NexusEditorStyles.CreateButton(NexusLang.Get("ads_policies"), () => { foreach (var url in provider.policies) Application.OpenURL(url); }, NexusEditorStyles.BtnGray));
                links.Add(NexusEditorStyles.CreateButton(NexusLang.Get("ads_install_sdk"), () => AdsProjectActions.InstallSdk(provider), NexusEditorStyles.BtnBlue));
                var steps = turkish ? provider.steps.tr : provider.steps.en;
                for (int i = 0; i < steps.Length; i++) box.Add(NexusEditorStyles.CreateHint((i + 1) + ". " + steps[i]));
                if (provider.testData != null) box.Add(NexusEditorStyles.CreateHint(string.Format(NexusLang.Get("ads_test_data"), provider.testData.note)));
                card.Add(box);
            }
        }

        private void BuildNetworks(VisualElement parent)
        {
            var card = AdsViews.Card(parent, NexusLang.Get("ads_setup_networks"));
            foreach (var network in _catalog.networks)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 2 } };
                string key = PrefRegistered + Application.productName + "." + network.id;
                var registered = new Toggle { value = EditorPrefs.GetBool(key, false), tooltip = NexusLang.Get("ads_registered_tip") };
                registered.RegisterValueChangedCallback(e => EditorPrefs.SetBool(key, e.newValue));
                row.Add(registered);
                var text = NexusEditorStyles.CreateBody(network.name + " — " + network.when + (network.childDirectedAllowed ? "" : " " + NexusLang.Get("ads_not_child_directed")));
                text.style.flexGrow = 1;
                text.style.flexShrink = 1;
                row.Add(text);
                row.Add(NexusEditorStyles.CreateButton(NexusLang.Get("ads_register"), () => Application.OpenURL(network.registration), NexusEditorStyles.BtnGray));
                row.Add(NexusEditorStyles.CreateButton(NexusLang.Get("ads_policy"), () => { foreach (var url in network.policies) Application.OpenURL(url); }, NexusEditorStyles.BtnGray));
                card.Add(row);
            }
            foreach (var rule in AdsViews.Turkish ? _catalog.commonRules.tr : _catalog.commonRules.en) card.Add(NexusEditorStyles.CreateHint("• " + rule));
        }

        private static void BuildChecklist(VisualElement parent)
        {
            var card = AdsViews.Card(parent, NexusLang.Get("ads_setup_checklist"));
            var checklist = AdsViews.Turkish ? ChecklistTr : ChecklistEn;
            for (int i = 0; i < checklist.Length; i++)
            {
                // keyed by the English text: the same in both languages and in every editor run, which string.GetHashCode is not (issue #195)
                string key = PrefRegistered + Application.productName + ".check." + ChecklistEn[i];
                var item = new Toggle { value = EditorPrefs.GetBool(key, false) };
                item.RegisterValueChangedCallback(e => EditorPrefs.SetBool(key, e.newValue));
                item.style.marginTop = 2;
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.FlexStart } };
                row.Add(item);
                var text = NexusEditorStyles.CreateBody(checklist[i]);
                text.style.flexShrink = 1;
                row.Add(text);
                card.Add(row);
            }
        }

        // The rules stay here, not in NexusLang: the English sentence is each item's EditorPrefs key (#195).
        private static readonly string[] ChecklistEn =
        {
            "I never tap a live ad and never ask anyone to; I test with the provider's test mode or sample units only.",
            "No release build, and no build with live ad units, goes to testers, friends or the team.",
            "One account per platform, registered with the same company details and the Play Console email.",
            "app-ads.txt is on the developer website named in the store listing and lists every enabled network.",
            "Play Console: ads declaration, target audience, Data safety and advertising-id declaration are filled; the Families age screen exists for mixed audiences.",
            "The privacy policy URL names the ad SDKs (KVKK notice for Türkiye).",
            "No bought installs from sources the networks treat as invalid traffic.",
        };

        private static readonly string[] ChecklistTr =
        {
            "Canlı reklama asla dokunmam, kimseden de istemem; yalnızca sağlayıcının test modu ya da örnek birimleriyle test ederim.",
            "Release build ya da canlı birimli hiçbir build test edenlere, arkadaşlara ya da ekibe gitmez.",
            "Platform başına tek hesap; aynı şirket bilgileri ve Play Console e-postası.",
            "app-ads.txt mağaza kaydındaki geliştirici web sitesinde ve etkin her ağı listeliyor.",
            "Play Console: reklam beyanı, hedef kitle, Data safety ve reklam kimliği beyanı dolu; karma kitle için Aileler yaş ekranı var.",
            "Gizlilik politikası URL'si reklam SDK'larını adlandırıyor (Türkiye için KVKK aydınlatma metni).",
            "Ağların geçersiz trafik saydığı kaynaklardan satın alınmış yükleme yok.",
        };
    }
}
