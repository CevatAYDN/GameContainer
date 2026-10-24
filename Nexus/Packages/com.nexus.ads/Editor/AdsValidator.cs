using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Nexus.Ads.Unity;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Nexus.Ads.Editor
{
    public enum CheckResult { Pass, Warn, Fail }

    [Serializable]
    public sealed class AdsCheck
    {
        public string id;
        public string result;
        public string message;
        public string fix;

        public AdsCheck(string id, CheckResult result, string message, string fix = null)
        {
            this.id = id;
            this.result = result.ToString().ToLowerInvariant();
            this.message = message;
            this.fix = fix ?? "";
        }

        public CheckResult Result => result == "fail" ? CheckResult.Fail : result == "warn" ? CheckResult.Warn : CheckResult.Pass;
    }

    /// <summary>The machine-readable result of one validation; written by <see cref="AdsValidator.RunCli"/> as JSON.</summary>
    [Serializable]
    public sealed class AdsValidationReport
    {
        public string tab;
        public string platform;
        public string mediation;
        public string status;
        public bool adFreeRelease;
        public List<AdsCheck> checks = new List<AdsCheck>();
        public int pass;
        public int warn;
        public int fail;
        public string outcome;

        public bool Passed => fail == 0;

        public void Add(AdsCheck check)
        {
            checks.Add(check);
            switch (check.Result)
            {
                case CheckResult.Fail: fail++; break;
                case CheckResult.Warn: warn++; break;
                default: pass++; break;
            }
            outcome = fail > 0 ? "fail" : warn > 0 ? "warn" : "pass";
        }

        public string Summary()
        {
            var lines = new List<string> { "[Nexus Ads] " + tab + " tab, " + platform + ", " + mediation + " (" + status + "): " + outcome + " — " + pass + " pass, " + warn + " warn, " + fail + " fail" };
            foreach (var c in checks)
                if (c.Result != CheckResult.Pass) lines.Add("  " + c.result.ToUpperInvariant() + " " + c.id + ": " + c.message + (c.fix.Length > 0 ? " Fix: " + c.fix : ""));
            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// Every check the setup promises, as a pure function over the settings, a tab, a platform and a
    /// <see cref="IProjectProbe"/>. The editor page, the wizard, <see cref="RunCli"/> and the pre-build hook
    /// all call <see cref="Validate"/>; none of them has a rule of its own. Check ids are stable and
    /// listed in the package README; the register's "Validator" cells name them.
    /// </summary>
    public static class AdsValidator
    {
        public enum Tab { Test, Release }

        private static readonly Regex Identifier = new Regex("^[A-Za-z_][A-Za-z0-9_]*$");

        public static AdsValidationReport Validate(AdsSettings settings, Tab tab, AdPlatform platform, IProjectProbe probe, PlatformCatalog catalog)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (probe == null) throw new ArgumentNullException(nameof(probe));
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            var kind = settings.SelectedMediation;
            bool release = tab == Tab.Release;
            var report = new AdsValidationReport { tab = tab.ToString(), platform = platform.ToString(), mediation = kind.ToString(), adFreeRelease = settings.AdFreeRelease };

            // one mediation per build
            var installedMediations = new List<AdProviderKind>();
            foreach (var k in new[] { AdProviderKind.LevelPlay, AdProviderKind.Max }) if (probe.SdkInstalled(k)) installedMediations.Add(k);
            bool conflict = installedMediations.Count > 1;
            report.Add(conflict
                ? new AdsCheck("mediation.single", CheckResult.Fail, "two mediation SDKs are in the project (" + string.Join(", ", installedMediations) + "); one build carries one mediation.", "remove the SDK of the mediation that is not selected (LevelPlay and MAX bring the same network adapters in different versions).")
                : new AdsCheck("mediation.single", CheckResult.Pass, installedMediations.Count == 0 ? "no mediation SDK in the project" : installedMediations[0] + " is the only mediation SDK"));

            // the Google Mobile Ads SDK next to another mediation: only the selected provider starts, yet the SDK ships in the build
            if (kind == AdProviderKind.LevelPlay || kind == AdProviderKind.Max)
                report.Add(probe.SdkInstalled(AdProviderKind.AdMob)
                    ? new AdsCheck("mediation.admob", CheckResult.Warn, "the Google Mobile Ads SDK is in the project while " + kind + " is selected; the AdMob provider never starts (only the selected mediation does), but the SDK ships in the build.", "keep com.google.ads.mobile when a network adapter or Google's consent form (UMP) needs it; remove it otherwise.")
                    : new AdsCheck("mediation.admob", CheckResult.Pass, "no Google Mobile Ads SDK next to " + kind));

            // status from the single resolver
            bool sdk = probe.SdkInstalled(kind) && probe.ProviderPackageInstalled(kind);
            var status = ProviderStatusResolver.Resolve(settings, kind, platform, sdk, conflict);
            report.status = status.Status.ToString();

            if (kind == AdProviderKind.None)
            {
                report.Add(release && !settings.AdFreeRelease
                    ? new AdsCheck("mediation.selected", CheckResult.Fail, "no mediation is selected and this is not an ad-free release.", "select a mediation in Project Settings > Nexus Ads, or tick Ad-free release on purpose.")
                    : new AdsCheck("mediation.selected", CheckResult.Pass, release ? "ad-free release, on purpose" : "no mediation selected; the simulator runs"));
            }
            else if (release)
            {
                if (settings.AdFreeRelease)
                    report.Add(new AdsCheck("mediation.status", CheckResult.Pass, "ad-free release on purpose; " + kind + " is " + status + " and is not embedded"));
                else if (status.Status == ProviderStatus.Active)
                    report.Add(new AdsCheck("mediation.status", CheckResult.Pass, kind + " is Active on " + platform));
                else
                    report.Add(new AdsCheck("mediation.status", CheckResult.Fail, kind + " is " + status + "; a release build would ship without ads.", status.Status == ProviderStatus.NotInstalled ? "install the SDK and the provider package (Tools > Nexus Ads > Setup)." : "fill the Release tab: " + string.Join(", ", status.Missing) + "."));
            }
            else
            {
                report.Add(status.Status == ProviderStatus.NotInstalled
                    ? new AdsCheck("mediation.status", CheckResult.Warn, kind + " is NotInstalled; the editor and development builds run the simulator.", "install the SDK and the provider package (Tools > Nexus Ads > Setup).")
                    : new AdsCheck("mediation.status", CheckResult.Pass, kind + " is " + status.Status + " on " + platform));
            }

            if (kind != AdProviderKind.None)
            {
                CheckKeys(settings, kind, platform, release, catalog, report);
                CheckSdk(settings, kind, platform, release, probe, catalog, report);
            }
            CheckChildren(settings, kind, probe, catalog, report);
            CheckProject(settings, kind, release, probe, report);
            return report;
        }

        private static void CheckKeys(AdsSettings settings, AdProviderKind kind, AdPlatform platform, bool release, PlatformCatalog catalog, AdsValidationReport report)
        {
            var tab = release ? settings.Release : settings.Test;
            var missing = settings.MissingFields(kind, platform, tab);
            string tabName = release ? "Release" : "Test";
            if (missing.Count > 0)
            {
                bool fatal = release || kind == AdProviderKind.AdMob;
                report.Add(new AdsCheck("keys." + tabName.ToLowerInvariant() + ".missing", fatal ? CheckResult.Fail : CheckResult.Warn,
                    tabName + " tab, " + platform + ": " + string.Join(", ", missing) + " missing" + (fatal ? "." : "; the simulator runs on development builds."),
                    kind == AdProviderKind.AdMob && !release ? "press Reset to official test data on the Test tab." : release ? "enter the account's keys on the Release tab." : "press Copy from Release on the Test tab once the Release tab is filled."));
            }
            else report.Add(new AdsCheck("keys." + tabName.ToLowerInvariant() + ".missing", CheckResult.Pass, tabName + " tab, " + platform + ": every required field is filled"));

            var keys = tab.For(kind).For(platform);
            if (release)
            {
                bool testData = kind == AdProviderKind.AdMob && (AdMobSampleIds.IsSample(keys.AppKey) || AdMobSampleIds.IsSample(keys.RewardedUnitId) || AdMobSampleIds.IsSample(keys.InterstitialUnitId) || AdMobSampleIds.IsSample(keys.BannerUnitId));
                report.Add(testData
                    ? new AdsCheck("keys.release.testdata", CheckResult.Fail, "Release tab, " + platform + ": Google's sample ids are on the Release tab; a release with them earns nothing.", "replace them with the account's ids from admob.google.com.")
                    : new AdsCheck("keys.release.testdata", CheckResult.Pass, "Release tab carries no test data"));
                return;
            }

            if (kind == AdProviderKind.AdMob)
            {
                var sample = catalog.SampleAdMobKeys(platform);
                var differs = new List<string>();
                if (keys.AppKey.Trim() != sample.AppKey) differs.Add("App ID");
                if (settings.Uses(AdFormat.Rewarded) && keys.RewardedUnitId.Trim() != sample.RewardedUnitId) differs.Add("rewarded unit id");
                if (settings.Uses(AdFormat.Interstitial) && keys.InterstitialUnitId.Trim() != sample.InterstitialUnitId) differs.Add("interstitial unit id");
                if (settings.Uses(AdFormat.Banner) && keys.BannerUnitId.Trim() != sample.BannerUnitId) differs.Add("banner unit id");
                report.Add(differs.Count > 0
                    ? new AdsCheck("keys.test.official", CheckResult.Fail, "Test tab, " + platform + ": " + string.Join(", ", differs) + " differ from Google's published sample ids.", "press Reset to official test data; AdMob test ads come only from the sample ids.")
                    : new AdsCheck("keys.test.official", CheckResult.Pass, "Test tab holds Google's sample ids"));
            }
            else
            {
                var live = settings.Release.For(kind).For(platform);
                bool releaseComplete = settings.MissingFields(kind, platform, settings.Release).Count == 0;
                bool mirrors = keys.AppKey.Trim() == live.AppKey.Trim() && keys.RewardedUnitId.Trim() == live.RewardedUnitId.Trim() && keys.InterstitialUnitId.Trim() == live.InterstitialUnitId.Trim() && keys.BannerUnitId.Trim() == live.BannerUnitId.Trim();
                if (releaseComplete && !mirrors)
                    report.Add(new AdsCheck("keys.test.mirror", CheckResult.Fail, "Test tab, " + platform + ": " + kind + " publishes no test keys, so the Test tab must equal the Release tab (test mode and the device list make them test ads).", "press Copy from Release on the Test tab."));
                else
                    report.Add(new AdsCheck("keys.test.mirror", CheckResult.Pass, releaseComplete ? "Test tab mirrors the Release tab" : "Release tab not filled yet; nothing to mirror"));
                report.Add(settings.TestDeviceIds.Count == 0
                    ? new AdsCheck("devices.empty", CheckResult.Warn, "the test device list is empty: every development build runs the simulator.", "add the phone's advertising id (the debug panel shows it) to the Test tab's device list.")
                    : new AdsCheck("devices.empty", CheckResult.Pass, settings.TestDeviceIds.Count + " test device(s)"));
            }
        }

        private static void AddPlanCheck(AdsValidationReport report, List<SetupStep> plan, string id, string[] stepIds, string consequence)
        {
            var steps = plan.FindAll(s => Array.IndexOf(stepIds, s.id) >= 0);
            if (steps.Count == 0) return;
            var open = steps.FindAll(s => s.status != SetupStatus.Done);
            report.Add(open.Count == 0
                ? new AdsCheck(id, CheckResult.Pass, string.Join("; ", steps.ConvertAll(s => s.what + ": " + s.how)))
                : new AdsCheck(id, CheckResult.Fail, "Android: " + string.Join("; ", open.ConvertAll(s => s.what)) + " missing; " + consequence + ".", "press Prepare Android in Tools > Nexus Ads > Setup, or run AndroidSetup.PrepareCli."));
        }

        private static void CheckSdk(AdsSettings settings, AdProviderKind kind, AdPlatform platform, bool release, IProjectProbe probe, PlatformCatalog catalog, AdsValidationReport report)
        {
            if (probe.SdkInstalled(kind) && !probe.SdkViaUpm(kind))
                report.Add(new AdsCheck("sdk.unitypackage", CheckResult.Warn, kind + "'s SDK is a .unitypackage import (found by assembly name): versionDefines cannot see it, so the provider package compiles only with the scripting define " + (catalog.Provider(kind)?.sdk?.define ?? "") + " set by hand.",
                    "install the SDK as a UPM package (Tools > Nexus Ads > Setup > Install SDK) or add the define in Player Settings."));
            else if (probe.SdkInstalled(kind))
                report.Add(new AdsCheck("sdk.unitypackage", CheckResult.Pass, kind + "'s SDK is a UPM package"));

            if (kind == AdProviderKind.AdMob && platform == AdPlatform.Android)
            {
                string appId = probe.AdMobSettingsAppId(platform);
                var tab = release ? settings.Release : settings.Test;
                string expected = tab.AdMob.For(platform).AppKey?.Trim();
                if (appId == null)
                    report.Add(new AdsCheck("android.admob.appid", release ? CheckResult.Fail : CheckResult.Warn, "the Google Mobile Ads settings asset (Assets/GoogleMobileAds/Resources/GoogleMobileAdsSettings.asset) is missing; the plugin writes the App ID to the manifest from it.", "press Prepare Android in Tools > Nexus Ads > Setup (it writes the tab's App ID), or enter it in Assets > Google Mobile Ads > Settings."));
                else if (string.IsNullOrWhiteSpace(appId) || (!string.IsNullOrEmpty(expected) && appId.Trim() != expected))
                    report.Add(new AdsCheck("android.admob.appid", CheckResult.Fail, "the Google Mobile Ads settings asset carries '" + appId + "' but the " + (release ? "Release" : "Test") + " tab says '" + expected + "'; the manifest would start the SDK with the wrong app.", "press Prepare Android in Tools > Nexus Ads > Setup (it writes the tab's App ID), or enter it in Assets > Google Mobile Ads > Settings."));
                else
                    report.Add(new AdsCheck("android.admob.appid", CheckResult.Pass, "Google Mobile Ads settings asset carries the tab's App ID"));
            }

            if (release && !probe.ExternalDependencyManagerInstalled)
                report.Add(new AdsCheck("android.edm", CheckResult.Fail, "the External Dependency Manager is not in the project; the SDK's Android and iOS dependencies are not resolved without it.", "press Prepare Android in Tools > Nexus Ads > Setup (it adds the resolver the selected mediation needs and resolves)."));
            else
                report.Add(new AdsCheck("android.edm", CheckResult.Pass, probe.ExternalDependencyManagerInstalled ? "External Dependency Manager present" : "External Dependency Manager not checked for the Test tab"));

            // the Android setup the build needs, from the same plan Prepare Android performs
            if (platform == AdPlatform.Android && probe.SdkInstalled(kind) && !(release && settings.AdFreeRelease))
            {
                var plan = AndroidSetup.Plan(settings, release ? Tab.Release : Tab.Test, probe, catalog);
                AddPlanCheck(report, plan, "android.nativesdk", new[] { "native.sdk", "resolve" }, "the Gradle build fails without the native SDK (LevelPlay: cannot find symbol IronSourceError)");
                AddPlanCheck(report, plan, "android.gradle", new[] { "gradle.template", "gradle.lines" }, "the Gradle build fails without them (LevelPlay on Unity 6000.5: duplicate Kotlin classes)");
            }

            var provider = catalog.Provider(kind);
            if (provider?.sdk != null && provider.sdk.minAndroidApi > 0 && probe.AndroidMinSdk > 0 && probe.AndroidMinSdk < provider.sdk.minAndroidApi)
                report.Add(new AdsCheck("android.minsdk", CheckResult.Warn, "Android minimum SDK is API " + probe.AndroidMinSdk + "; " + provider.name + " needs API " + provider.sdk.minAndroidApi + ".", "raise Player Settings > Minimum API Level."));
        }

        private static void CheckChildren(AdsSettings settings, AdProviderKind kind, IProjectProbe probe, PlatformCatalog catalog, AdsValidationReport report)
        {
            if (!settings.ChildDirected) { report.Add(new AdsCheck("child.networks", CheckResult.Pass, "not child-directed")); return; }
            var provider = catalog.Provider(kind);
            if (provider != null && !provider.childDirectedAllowed)
                report.Add(new AdsCheck("child.mediation", CheckResult.Fail, provider.name + " cannot be used in a child-directed app (its publisher policies forbid initializing the SDK for children; not on Google's Families self-certified list).", "select Unity LevelPlay, or clear the child-directed flag if the app is not for children."));
            else
                report.Add(new AdsCheck("child.mediation", CheckResult.Pass, (provider?.name ?? "no mediation") + " allowed for a child-directed app"));
            var offenders = new List<string>();
            foreach (var id in probe.DetectedNetworks)
            {
                var network = catalog.Network(id);
                if (network != null && !network.childDirectedAllowed) offenders.Add(network.name);
            }
            report.Add(offenders.Count > 0
                ? new AdsCheck("child.networks", CheckResult.Warn, "child-directed app with network adapters that are not on Google's Families self-certified list or forbid children's apps: " + string.Join(", ", offenders) + " (detected by adapter package and folder names).", "remove those adapters and keep them out of the mediation groups in the dashboard.")
                : new AdsCheck("child.networks", CheckResult.Pass, "no adapter of a network that is off the Families list was detected"));
        }

        private static void CheckProject(AdsSettings settings, AdProviderKind kind, bool release, IProjectProbe probe, AdsValidationReport report)
        {
            // placements
            var bad = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in settings.Placements)
            {
                string name = p.Name ?? "";
                if (!Identifier.IsMatch(name)) bad.Add("'" + name + "' is not a C# identifier");
                else if (!seen.Add(name)) bad.Add("'" + name + "' is declared twice");
                if (p.Format == AdFormat.Interstitial && !p.NaturalBreak) bad.Add("'" + name + "' is an interstitial without NaturalBreak; it can never show");
            }
            if (bad.Count > 0) report.Add(new AdsCheck("placements.names", CheckResult.Fail, string.Join("; ", bad) + ".", "placement names are C# identifiers (shop_refill, not shop-refill) and unique; interstitial placements are marked NaturalBreak."));
            else report.Add(settings.Placements.Count == 0
                ? new AdsCheck("placements.names", CheckResult.Warn, "no placement is declared; nothing can show.", "add placements in Project Settings > Nexus Ads.")
                : new AdsCheck("placements.names", CheckResult.Pass, settings.Placements.Count + " placement(s)"));

            // floors
            var requested = settings.Policy;
            var effective = requested.ToPolicy();
            bool raised = requested.MinInterstitialGapSeconds < effective.MinInterstitialGapSeconds || requested.NoInterstitialForFirstSeconds < effective.NoInterstitialForFirstSeconds
                || requested.NoInterstitialAfterRewardedSeconds < effective.NoInterstitialAfterRewardedSeconds || requested.SessionResumeGapMinutes < effective.SessionResumeGapMinutes;
            report.Add(raised
                ? new AdsCheck("policy.floors", CheckResult.Warn, "a policy value is below the package floor and is raised to it at runtime.", "enter values at or above AdPolicy.Default (30 s gap, 60 s first-show delay, 30 s after rewarded, 30 min resume gap).")
                : new AdsCheck("policy.floors", CheckResult.Pass, "policy values at or above the floors"));

            // privacy policy
            report.Add(string.IsNullOrWhiteSpace(settings.PrivacyPolicyUrl)
                ? new AdsCheck("privacy.url", CheckResult.Warn, "no privacy policy URL; the networks and Google Play require one that names the ad SDKs (KVKK notice for Türkiye).", "enter the URL in Project Settings > Nexus Ads > Privacy.")
                : new AdsCheck("privacy.url", CheckResult.Pass, "privacy policy URL set"));

            // AD_ID: only a custom manifest can be read before a build; the merged manifest exists after it
            if (probe.AndroidTargetSdk >= 33 && probe.CustomAndroidManifest != null && probe.CustomAndroidManifest.IndexOf("com.google.android.gms.permission.AD_ID", StringComparison.Ordinal) < 0)
                report.Add(new AdsCheck("android.adid", CheckResult.Warn, "target SDK " + probe.AndroidTargetSdk + " with a custom AndroidManifest.xml that does not declare com.google.android.gms.permission.AD_ID; the SDKs' own manifests usually add it on merge, which this check cannot see before a build.", "add <uses-permission android:name=\"com.google.android.gms.permission.AD_ID\"/> to Assets/Plugins/Android/AndroidManifest.xml, and declare the advertising id in Play Console."));
            else
                report.Add(new AdsCheck("android.adid", CheckResult.Pass, probe.AndroidTargetSdk >= 33 ? "AD_ID: no custom manifest without the permission (the merged manifest is checked by the SDKs on build)" : "target SDK below 33"));

            // link.xml
            bool preserved = false;
            foreach (var text in probe.LinkXmlTexts) if (text.IndexOf("Nexus.Ads.Core", StringComparison.Ordinal) >= 0) { preserved = true; break; }
            report.Add(preserved
                ? new AdsCheck("android.linkxml", CheckResult.Pass, "a link.xml preserves Nexus.Ads.Core")
                : new AdsCheck("android.linkxml", release ? CheckResult.Warn : CheckResult.Pass, release ? "no link.xml preserves Nexus.Ads.Core; IL2CPP stripping may drop callback types." : "no link.xml yet (checked for release builds)", "press Write link.xml in Tools > Nexus Ads > Setup."));

            // app-ads.txt
            var withoutLine = new List<string>();
            foreach (var id in probe.DetectedNetworks)
            {
                bool covered = false;
                foreach (var line in settings.AppAdsTxtLines) if (line != null && line.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0) { covered = true; break; }
                if (!covered) withoutLine.Add(id);
            }
            report.Add(withoutLine.Count > 0
                ? new AdsCheck("appads.lines", CheckResult.Warn, "no app-ads.txt line mentions: " + string.Join(", ", withoutLine) + " (matched by network id in the line text).", "paste each network's app-ads.txt line from its dashboard into Project Settings > Nexus Ads > app-ads.txt lines, then generate the file.")
                : new AdsCheck("appads.lines", CheckResult.Pass, probe.DetectedNetworks.Count == 0 ? "no network adapters detected" : "every detected network has an app-ads.txt line"));

            // pins
            string pinFault = ManifestPinning.Fault(probe.ConsumerManifestJson);
            report.Add(pinFault == null
                ? new AdsCheck("manifest.pins", CheckResult.Pass, "the family packages point at one commit (or are embedded)")
                : new AdsCheck("manifest.pins", CheckResult.Fail, pinFault, "press Pin family packages in Tools > Nexus Ads > Setup, or edit Packages/manifest.json so every com.nexus.ads* entry carries the same commit."));
        }

        // ----- entry points

        public const string DefaultReportPath = "artifacts/ads-validation.json";

        /// <summary>
        /// Batch-mode entry: <c>-executeMethod Nexus.Ads.Editor.AdsValidator.RunCli -adsTab Test|Release [-adsPlatform Android|Ios] [-adsReport path]</c>.
        /// Exit 0 = no fail (warnings allowed), 1 = at least one fail, 2 = could not run (no settings asset, bad arguments).
        /// </summary>
        public static void RunCli()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                string tabArg = Arg(args, "-adsTab"), platformArg = Arg(args, "-adsPlatform") ?? "Android", reportPath = Arg(args, "-adsReport") ?? DefaultReportPath;
                if (!Enum.TryParse(tabArg, true, out Tab tab)) { Debug.LogError("[Nexus Ads] -adsTab Test|Release is required"); EditorApplication.Exit(2); return; }
                if (!Enum.TryParse(platformArg, true, out AdPlatform platform)) { Debug.LogError("[Nexus Ads] -adsPlatform Android|Ios"); EditorApplication.Exit(2); return; }
                var settings = AdsSettings.Load();
                if (settings == null) { Debug.LogError("[Nexus Ads] no Resources/" + AdsSettings.ResourceName + " asset"); EditorApplication.Exit(2); return; }
                var report = Validate(settings, tab, platform, new UnityProjectProbe(), PlatformCatalog.Load());
                WriteReport(report, reportPath);
                Debug.Log(report.Summary() + "\n[Nexus Ads] report: " + Path.GetFullPath(reportPath));
                EditorApplication.Exit(report.Passed ? 0 : 1);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Nexus Ads] validator could not run: " + ex);
                EditorApplication.Exit(2);
            }
        }

        public static void WriteReport(AdsValidationReport report, string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonUtility.ToJson(report, true));
        }

        private static string Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++) if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }

    /// <summary>
    /// Runs the validator on every player build: the Test tab for a development build, the Release tab
    /// otherwise. A fail stops the build; a project without the settings asset is left alone.
    /// </summary>
    public sealed class AdsBuildGuard : IPreprocessBuildWithReport
    {
        public int callbackOrder => -900;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android && report.summary.platform != BuildTarget.iOS) return; // ads ship on phones only
            var settings = AdsSettings.Load();
            if (settings == null) return;
            bool development = (report.summary.options & BuildOptions.Development) != 0;
            var platform = report.summary.platform == BuildTarget.iOS ? AdPlatform.Ios : AdPlatform.Android;
            var result = AdsValidator.Validate(settings, development ? AdsValidator.Tab.Test : AdsValidator.Tab.Release, platform, new UnityProjectProbe(), PlatformCatalog.Load());
            AdsValidator.WriteReport(result, AdsValidator.DefaultReportPath);
            if (result.Passed) { Debug.Log(result.Summary()); return; }
            throw new BuildFailedException(result.Summary());
        }
    }
}
