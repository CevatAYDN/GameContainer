using System.IO;
using Nexus.Ads.Unity;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Nexus.Ads.Editor
{
    /// <summary>
    /// Tools > Nexus Ads > Setup: from nothing to a live ad, one window. Rows come from the catalog,
    /// badges from <see cref="ProviderStatusResolver"/>, the next step from the validator's first
    /// failing check. Links open the official pages; "Install SDK" adds the UPM package where the
    /// catalog names a registry Unity can reach; "Prepare Android" performs the selected mediation's
    /// Android setup (<see cref="AndroidSetup"/>).
    /// </summary>
    public sealed class AdsSetupWizard : EditorWindow
    {
        private const string PrefRegistered = "NexusAds.Registered.";
        private Vector2 _scroll;
        private bool _turkish;
        private AdsValidationReport _report;
        private UnityProjectProbe _probe;
        private PlatformCatalog _catalog;
        private AdsValidator.Tab _androidTab = AdsValidator.Tab.Test;

        private void OnEnable() => Refresh();
        private void OnFocus() => Refresh();

        private void Refresh()
        {
            _probe = new UnityProjectProbe();
            _catalog = PlatformCatalog.Load(fresh: true);
        }

        [MenuItem("Tools/Nexus Ads/Setup", priority = 10)]
        public static void Open() => GetWindow<AdsSetupWizard>("Nexus Ads Setup").Show();

        [MenuItem("Tools/Nexus Ads/Project Settings", priority = 11)]
        public static void OpenSettings() => AdsSettingsProvider.Open();

        [MenuItem("Tools/Nexus Ads/Generate Placements", priority = 20)]
        public static void GeneratePlacements()
        {
            var settings = AdsSettingsProvider.LoadOrCreate();
            try { Debug.Log("[Nexus Ads] wrote " + AdPlacementsGenerator.Write(settings)); }
            catch (System.Exception ex) { Debug.LogError("[Nexus Ads] " + ex.Message); }
        }

        [MenuItem("Tools/Nexus Ads/Write link.xml", priority = 21)]
        public static void WriteLinkXml() => Debug.Log("[Nexus Ads] wrote " + LinkXml.Write(AdsSettingsProvider.LoadOrCreate().SelectedMediation));

        [MenuItem("Tools/Nexus Ads/Pin family packages to one commit", priority = 22)]
        public static void PinPackages()
        {
            string manifest = File.ReadAllText("Packages/manifest.json");
            string pinned = ManifestPinning.Pin(manifest);
            if (pinned == manifest) { Debug.Log("[Nexus Ads] nothing to pin: the family packages are embedded or not git-pinned"); return; }
            File.WriteAllText("Packages/manifest.json", pinned);
            Client.Resolve();
            Debug.Log("[Nexus Ads] family packages pinned to the commit of com.nexus.ads");
        }

        private void OnGUI()
        {
            var settings = AssetDatabase.LoadAssetAtPath<AdsSettings>(AdsSettingsProvider.AssetPath);
            if (_probe == null || _catalog == null) Refresh();
            var catalog = _catalog;
            var probe = _probe;
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Catalog read " + catalog.readDate, EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Refresh", EditorStyles.miniButton)) Refresh();
                _turkish = GUILayout.Toggle(_turkish, "Türkçe", EditorStyles.miniButton);
            }

            EditorGUILayout.LabelField(_turkish ? "1. Ayarlar" : "1. Settings", EditorStyles.boldLabel);
            if (settings == null)
            {
                if (GUILayout.Button(_turkish ? "Ayar varlığını oluştur" : "Create the settings asset")) settings = AdsSettingsProvider.LoadOrCreate();
                EditorGUILayout.EndScrollView();
                return;
            }
            if (GUILayout.Button(_turkish ? "Project Settings > Nexus Ads'i aç" : "Open Project Settings > Nexus Ads")) AdsSettingsProvider.Open();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(_turkish ? "2. Mediation sağlayıcıları" : "2. Mediation providers", EditorStyles.boldLabel);
            bool conflict = probe.SdkInstalled(AdProviderKind.LevelPlay) && probe.SdkInstalled(AdProviderKind.Max);
            foreach (var provider in catalog.providers)
            {
                var kind = provider.Kind;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(provider.name + " (" + provider.role + ", " + provider.when + ")" + (settings.SelectedMediation == kind ? "  ← selected" : ""), EditorStyles.boldLabel);
                    foreach (AdPlatform platform in new[] { AdPlatform.Android, AdPlatform.Ios })
                    {
                        bool sdk = probe.SdkInstalled(kind) && probe.ProviderPackageInstalled(kind);
                        var status = ProviderStatusResolver.Resolve(settings, kind, platform, sdk, conflict);
                        AdsSettingsProvider.Badge(platform + ": " + status, status.Status);
                        if (status.Status == ProviderStatus.NotInstalled)
                            EditorGUILayout.LabelField(_turkish ? "  Sonraki adım: SDK'yı ve sağlayıcı paketini kur." : "  Next step: install the SDK and the provider package.", EditorStyles.miniLabel);
                        else if (status.Missing.Count > 0)
                            EditorGUILayout.LabelField((_turkish ? "  Sonraki adım: Release sekmesine gir: " : "  Next step: enter on the Release tab: ") + string.Join(", ", status.Missing), EditorStyles.miniLabel);
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(_turkish ? "Kayıt sayfası" : "Registration")) Application.OpenURL(provider.registration);
                        if (GUILayout.Button(_turkish ? "Doküman" : "Docs")) Application.OpenURL(provider.docs);
                        if (GUILayout.Button(_turkish ? "Politikalar" : "Policies")) foreach (var url in provider.policies) Application.OpenURL(url);
                        if (GUILayout.Button(_turkish ? "SDK'yı kur" : "Install SDK")) InstallSdk(provider);
                    }
                    var steps = _turkish ? provider.steps.tr : provider.steps.en;
                    for (int i = 0; i < steps.Length; i++) EditorGUILayout.LabelField((i + 1) + ". " + steps[i], EditorStyles.wordWrappedMiniLabel);
                    if (provider.testData != null) EditorGUILayout.LabelField((_turkish ? "Test verisi: " : "Test data: ") + provider.testData.note, EditorStyles.wordWrappedMiniLabel);
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(_turkish ? "3. Ağlar (mediation panelinden yönetilir)" : "3. Networks (managed in the mediation dashboard)", EditorStyles.boldLabel);
            foreach (var network in catalog.networks)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string key = PrefRegistered + Application.productName + "." + network.id;
                    bool registered = EditorPrefs.GetBool(key, false);
                    bool now = GUILayout.Toggle(registered, "", GUILayout.Width(18));
                    if (now != registered) EditorPrefs.SetBool(key, now);
                    EditorGUILayout.LabelField(network.name + " — " + network.when + (network.childDirectedAllowed ? "" : (_turkish ? " (çocuklara yönelik uygulamada kullanılmaz)" : " (not for child-directed apps)")));
                    if (GUILayout.Button(_turkish ? "Kayıt" : "Register", GUILayout.Width(80))) Application.OpenURL(network.registration);
                    if (GUILayout.Button(_turkish ? "Politika" : "Policy", GUILayout.Width(80))) foreach (var url in network.policies) Application.OpenURL(url);
                }
            }
            var rules = _turkish ? catalog.commonRules.tr : catalog.commonRules.en;
            foreach (var rule in rules) EditorGUILayout.LabelField("• " + rule, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(_turkish ? "4. Proje" : "4. Project", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(_turkish ? "AdPlacements üret" : "Generate AdPlacements")) GeneratePlacements();
                if (GUILayout.Button("link.xml")) WriteLinkXml();
                if (GUILayout.Button(_turkish ? "Paketleri tek commit'e sabitle" : "Pin family packages")) PinPackages();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(_turkish ? "5. Android (seçili mediation)" : "5. Android (selected mediation)", EditorStyles.boldLabel);
            _androidTab = (AdsValidator.Tab)GUILayout.Toolbar((int)_androidTab, _turkish ? new[] { "Test sekmesi (development build)", "Release sekmesi" } : new[] { "Test tab (development build)", "Release tab" });
            var plan = AndroidSetup.Plan(settings, _androidTab, probe, catalog);
            if (plan.Count == 0) EditorGUILayout.LabelField(_turkish ? "Mediation seçilmedi; hazırlanacak bir şey yok." : "No mediation selected; nothing to prepare.", EditorStyles.wordWrappedMiniLabel);
            foreach (var step in plan)
            {
                string status = step.status == SetupStatus.Done ? (_turkish ? "Tamam" : "Done") : step.status == SetupStatus.Pending ? (_turkish ? "Bekliyor" : "Pending") : (_turkish ? "Elle" : "Manual");
                EditorGUILayout.LabelField("[" + status + "] " + step.what, step.how, EditorStyles.wordWrappedMiniLabel);
            }
            using (new EditorGUI.DisabledScope(!plan.Exists(s => s.status == SetupStatus.Pending)))
                if (GUILayout.Button(_turkish ? "Android'i hazırla" : "Prepare Android"))
                {
                    try
                    {
                        var applied = AndroidSetup.Apply(settings, _androidTab, catalog);
                        foreach (var line in applied.Log) Debug.Log("[Nexus Ads] Prepare Android: " + line);
                    }
                    catch (System.Exception e) { Debug.LogError("[Nexus Ads] Prepare Android stopped: " + e.Message + " (offline? the native SDK file comes from the mediation's server)"); }
                    Refresh();
                }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(_turkish ? "6. Kodla zorlanamayan kurallar (onaylayın)" : "6. Rules the package cannot enforce (confirm each)", EditorStyles.boldLabel);
            foreach (var line in _turkish ? ChecklistTr : ChecklistEn)
            {
                string key = PrefRegistered + Application.productName + ".check." + line.GetHashCode();
                bool done = EditorPrefs.GetBool(key, false);
                bool now = EditorGUILayout.ToggleLeft(line, done, EditorStyles.wordWrappedLabel);
                if (now != done) EditorPrefs.SetBool(key, now);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(_turkish ? "7. Doğrula" : "7. Validate", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(_turkish ? "Test sekmesi" : "Test tab")) _report = AdsValidator.Validate(settings, AdsValidator.Tab.Test, AdPlatform.Android, probe, catalog);
                if (GUILayout.Button(_turkish ? "Release sekmesi" : "Release tab")) _report = AdsValidator.Validate(settings, AdsValidator.Tab.Release, AdPlatform.Android, probe, catalog);
            }
            if (_report != null) EditorGUILayout.HelpBox(_report.Summary(), _report.fail > 0 ? MessageType.Error : _report.warn > 0 ? MessageType.Warning : MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        private static void InstallSdk(CatalogProvider provider)
        {
            var upm = provider.sdk?.upm;
            if (provider.Kind == AdProviderKind.LevelPlay && upm != null) { Client.Add(upm.package); Debug.Log("[Nexus Ads] adding " + upm.package + " from the Unity registry"); return; }
            if (provider.Kind == AdProviderKind.AdMob && upm != null)
            {
                // OpenUPM is a scoped registry the project may not have yet: add it, or extend its scopes, then add the package
                string manifest = File.ReadAllText("Packages/manifest.json");
                string updated = AndroidSetup.WithOpenUpmScopes(manifest, upm.scopes ?? new[] { upm.package });
                if (updated != manifest) File.WriteAllText("Packages/manifest.json", updated);
                Client.Add(upm.package);
                Debug.Log("[Nexus Ads] adding " + upm.package + " from OpenUPM (11.6.0 brings Unity's External Dependency Manager as its dependency)");
                return;
            }
            // MAX: AppLovin's installer package writes its own scoped registry; the catalog points at the releases.
            Application.OpenURL(provider.sdk?.unitypackage ?? provider.docs);
        }

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
