using System.Collections.Generic;
using System.IO;
using Nexus.Ads.Unity;
using UnityEditor;
using UnityEngine;

namespace Nexus.Ads.Editor
{
    /// <summary>
    /// Project Settings > Nexus Ads: the one place every ad setting is edited. Test and Release are two
    /// tabs of the same asset; which tab a build reads is fixed in <see cref="AdsSettings.UsesReleaseTab"/>
    /// and has no switch here. Badges come from <see cref="ProviderStatusResolver"/>, findings from
    /// <see cref="AdsValidator"/>; this page owns no rule.
    /// </summary>
    public static class AdsSettingsProvider
    {
        public const string AssetPath = "Assets/Resources/" + AdsSettings.ResourceName + ".asset";
        private const string SettingsPath = "Project/Nexus Ads";

        private static int _tab, _platform;
        private static AdsValidationReport _lastReport;
        private static Vector2 _scroll;
        private static UnityProjectProbe _probe;
        private static PlatformCatalog _catalog;

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider(SettingsPath, SettingsScope.Project)
            {
                label = "Nexus Ads",
                keywords = new HashSet<string>(new[] { "ads", "mediation", "levelplay", "max", "admob", "placement", "rewarded", "interstitial" }),
                activateHandler = (_, __) => Refresh(),
                guiHandler = _ => Draw(),
            };
        }

        public static void Open() => SettingsService.OpenProjectSettings(SettingsPath);

        /// <summary>Re-reads the project (packages, assemblies, link.xml files); a disk scan, so never per repaint.</summary>
        public static void Refresh()
        {
            _probe = new UnityProjectProbe();
            _catalog = PlatformCatalog.Load(fresh: true);
        }

        /// <summary>The settings asset, created under Resources on first use.</summary>
        public static AdsSettings LoadOrCreate()
        {
            var settings = AssetDatabase.LoadAssetAtPath<AdsSettings>(AssetPath);
            if (settings != null) return settings;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(AssetPath)));
            settings = ScriptableObject.CreateInstance<AdsSettings>();
            AssetDatabase.CreateAsset(settings, AssetPath);
            AssetDatabase.SaveAssets();
            return settings;
        }

        private static void Draw()
        {
            var settings = AssetDatabase.LoadAssetAtPath<AdsSettings>(AssetPath);
            if (settings == null)
            {
                EditorGUILayout.HelpBox("No ads settings asset yet. It lives at " + AssetPath + " so the game can load it at runtime.", MessageType.Info);
                if (GUILayout.Button("Create settings asset")) LoadOrCreate();
                return;
            }
            if (_probe == null || _catalog == null) Refresh();
            var probe = _probe;
            var catalog = _catalog;
            var so = new SerializedObject(settings);
            so.Update();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (GUILayout.Button("Refresh project state", GUILayout.Width(160))) Refresh();

            EditorGUILayout.LabelField("Mediation", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AdsSettings.SelectedMediation)));
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AdsSettings.AdFreeRelease)));
            if (so.ApplyModifiedProperties()) EditorUtility.SetDirty(settings);
            AutoFillAdMobTestTab(settings, catalog, so);
            foreach (AdPlatform platform in new[] { AdPlatform.Android, AdPlatform.Ios })
            {
                bool sdk = probe.SdkInstalled(settings.SelectedMediation) && probe.ProviderPackageInstalled(settings.SelectedMediation);
                var status = ProviderStatusResolver.Resolve(settings, settings.SelectedMediation, platform, sdk, probe.SdkInstalled(AdProviderKind.LevelPlay) && probe.SdkInstalled(AdProviderKind.Max));
                Badge(platform + ": " + status, status.Status);
            }

            EditorGUILayout.Space();
            _platform = GUILayout.Toolbar(_platform, new[] { "Android", "iOS" });
            var platformSel = _platform == 1 ? AdPlatform.Ios : AdPlatform.Android;
            DrawOutlook(settings, platformSel, probe);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Keys", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("The editor and development builds read the Test tab; release builds read the Release tab. There is no switch.", MessageType.None);
            _tab = GUILayout.Toolbar(_tab, new[] { "Test", "Release" });
            bool release = _tab == 1;
            string tabProp = release ? nameof(AdsSettings.Release) : nameof(AdsSettings.Test);
            var kind = settings.SelectedMediation;
            if (kind == AdProviderKind.None) EditorGUILayout.HelpBox("Select a mediation to edit its keys.", MessageType.Info);
            else
            {
                var keysProp = so.FindProperty(tabProp).FindPropertyRelative(kind.ToString()).FindPropertyRelative(platformSel.ToString());
                bool locked = !release && kind == AdProviderKind.AdMob;
                using (new EditorGUI.DisabledScope(locked))
                {
                    EditorGUILayout.PropertyField(keysProp.FindPropertyRelative(nameof(PlatformKeys.AppKey)), new GUIContent(kind == AdProviderKind.AdMob ? "App ID" : "App / SDK Key"));
                    Unit(settings, keysProp, AdFormat.Rewarded, nameof(PlatformKeys.RewardedUnitId));
                    Unit(settings, keysProp, AdFormat.Interstitial, nameof(PlatformKeys.InterstitialUnitId));
                    Unit(settings, keysProp, AdFormat.Banner, nameof(PlatformKeys.BannerUnitId));
                }
                if (!release)
                {
                    if (kind == AdProviderKind.AdMob)
                    {
                        EditorGUILayout.HelpBox("Google's published sample ids; locked, every device gets test ads with them.", MessageType.None);
                        if (GUILayout.Button("Reset to official test data"))
                        {
                            so.ApplyModifiedProperties();
                            Undo.RecordObject(settings, "Reset AdMob test data");
                            var sample = catalog.SampleAdMobKeys(platformSel);
                            var target = settings.Test.AdMob.For(platformSel);
                            target.AppKey = sample.AppKey; target.RewardedUnitId = sample.RewardedUnitId; target.InterstitialUnitId = sample.InterstitialUnitId; target.BannerUnitId = sample.BannerUnitId;
                            EditorUtility.SetDirty(settings);
                            so.Update();
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox(kind + " publishes no test keys: the Test tab mirrors the Release tab; test mode and the device list make them test ads.", MessageType.None);
                        if (GUILayout.Button("Copy from Release"))
                        {
                            so.ApplyModifiedProperties();
                            Undo.RecordObject(settings, "Copy Release keys to Test");
                            var live = settings.Release.For(kind).For(platformSel);
                            var target = settings.Test.For(kind).For(platformSel);
                            target.AppKey = live.AppKey; target.RewardedUnitId = live.RewardedUnitId; target.InterstitialUnitId = live.InterstitialUnitId; target.BannerUnitId = live.BannerUnitId;
                            EditorUtility.SetDirty(settings);
                            so.Update();
                        }
                    }
                    EditorGUILayout.PropertyField(so.FindProperty(nameof(AdsSettings.TestDeviceIds)), new GUIContent("Test device ids (GAID / IDFA)"), true);
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Placements", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AdsSettings.Placements)), true);
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AdsSettings.GeneratedPlacementsPath)));
            if (GUILayout.Button("Generate AdPlacements constants"))
            {
                so.ApplyModifiedProperties();
                try { Debug.Log("[Nexus Ads] wrote " + AdPlacementsGenerator.Write(settings)); }
                catch (System.Exception ex) { Debug.LogError("[Nexus Ads] " + ex.Message); }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Policy floors (values below the package floor are raised to it)", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AdsSettings.Policy)), true);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Privacy", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AdsSettings.ChildDirected)));
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AdsSettings.UnderAgeOfConsent)));
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AdsSettings.MaxAdContentRating)));
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AdsSettings.PrivacyPolicyUrl)));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("app-ads.txt", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(so.FindProperty(nameof(AdsSettings.AppAdsTxtLines)), true);
            if (GUILayout.Button("Generate app-ads.txt"))
            {
                so.ApplyModifiedProperties();
                string path = EditorUtility.SaveFilePanel("Save app-ads.txt", "", "app-ads.txt", "txt");
                if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, AppAdsTxt.Render(settings.AppAdsTxtLines));
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Validation", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Validate Test tab")) { so.ApplyModifiedProperties(); _lastReport = AdsValidator.Validate(settings, AdsValidator.Tab.Test, platformSel, probe, catalog); }
                if (GUILayout.Button("Validate Release tab")) { so.ApplyModifiedProperties(); _lastReport = AdsValidator.Validate(settings, AdsValidator.Tab.Release, platformSel, probe, catalog); }
            }
            if (_lastReport != null)
            {
                EditorGUILayout.HelpBox(_lastReport.Summary(), _lastReport.fail > 0 ? MessageType.Error : _lastReport.warn > 0 ? MessageType.Warning : MessageType.Info);
            }
            EditorGUILayout.EndScrollView();
            if (so.ApplyModifiedProperties()) EditorUtility.SetDirty(settings);
        }

        /// <summary>When AdMob is selected and its Test tab is still empty, Google's sample ids are written in; the brief's "the Test tab fills itself".</summary>
        private static void AutoFillAdMobTestTab(AdsSettings settings, PlatformCatalog catalog, SerializedObject so)
        {
            if (settings.SelectedMediation != AdProviderKind.AdMob) return;
            bool changed = false;
            foreach (AdPlatform platform in new[] { AdPlatform.Android, AdPlatform.Ios })
            {
                var target = settings.Test.AdMob.For(platform);
                if (!string.IsNullOrEmpty(target.AppKey) || !string.IsNullOrEmpty(target.RewardedUnitId) || !string.IsNullOrEmpty(target.InterstitialUnitId) || !string.IsNullOrEmpty(target.BannerUnitId)) continue;
                var sample = catalog.SampleAdMobKeys(platform);
                target.AppKey = sample.AppKey; target.RewardedUnitId = sample.RewardedUnitId; target.InterstitialUnitId = sample.InterstitialUnitId; target.BannerUnitId = sample.BannerUnitId;
                changed = true;
            }
            if (changed) { EditorUtility.SetDirty(settings); so.Update(); }
        }

        private static void Unit(AdsSettings settings, SerializedProperty keys, AdFormat format, string field)
        {
            bool used = settings.Uses(format);
            using (new EditorGUI.DisabledScope(!used))
                EditorGUILayout.PropertyField(keys.FindPropertyRelative(field), new GUIContent(format + " unit id" + (used ? "" : " (no placement uses it)")));
        }

        /// <summary>What each kind of build does with ads, as <see cref="BuildOutlook"/> decides it; the settings page and the setup wizard show this and nothing else of the kind.</summary>
        public static void DrawOutlook(AdsSettings settings, AdPlatform platform, UnityProjectProbe probe)
        {
            bool sdk = probe.SdkInstalled(settings.SelectedMediation) && probe.ProviderPackageInstalled(settings.SelectedMediation);
            var status = ProviderStatusResolver.Resolve(settings, settings.SelectedMediation, platform, sdk, probe.SdkInstalled(AdProviderKind.LevelPlay) && probe.SdkInstalled(AdProviderKind.Max));
            EditorGUILayout.LabelField("What each build does (" + platform + ")", EditorStyles.boldLabel);
            foreach (var row in BuildOutlook.Rows(settings, platform, sdk, status))
                EditorGUILayout.HelpBox(row.Where + ": " + row.Text, row.Problem ? MessageType.Warning : MessageType.Info);
            EditorGUILayout.LabelField("Each line is what the build decides to run. Whether the mediation's server accepts the keys and fills an ad is known only from a run on a device.", EditorStyles.wordWrappedMiniLabel);
        }

        public static void Badge(string text, ProviderStatus status)
        {
            var color = status == ProviderStatus.Active ? new Color(0.2f, 0.7f, 0.3f) : status == ProviderStatus.Invalid ? new Color(0.85f, 0.25f, 0.25f) : status == ProviderStatus.TestOnly ? new Color(0.9f, 0.6f, 0.1f) : Color.gray;
            var style = new GUIStyle(EditorStyles.helpBox) { normal = { textColor = color }, fontStyle = FontStyle.Bold };
            EditorGUILayout.LabelField(text, style);
        }
    }
}
