using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Nexus.Ads.Unity;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace Nexus.Ads.Editor
{
    public enum SetupStatus { Done, Pending, Manual }

    /// <summary>One step of the Android setup: Done, Pending (Prepare Android does it) or Manual (the developer does it).</summary>
    [Serializable]
    public sealed class SetupStep
    {
        public string id;
        public SetupStatus status;
        public string what;
        public string how;

        public SetupStep(string id, SetupStatus status, string what, string how) { this.id = id; this.status = status; this.what = what; this.how = how; }
        public override string ToString() => status + " " + id + ": " + what + " (" + how + ")";
    }

    /// <summary>
    /// The Android setup of the selected mediation, driven by <c>PlatformCatalog.json</c>: the SDK and the Nexus
    /// provider package, the dependency resolver, the native SDK declaration, the custom main Gradle template
    /// with the catalog's Gradle lines, the resolved dependencies and the AdMob App ID. <see cref="Plan"/> is a pure
    /// function over settings, probe and catalog (the validator's <c>android.nativesdk</c> and <c>android.gradle</c>
    /// read it); <see cref="Apply"/> performs the Pending steps. Adding packages reloads the domain, so a press that
    /// adds packages stops there and the next press continues: every step is idempotent.
    /// </summary>
    public static class AndroidSetup
    {
        public const string MainTemplatePath = "Assets/Plugins/Android/mainTemplate.gradle";
        public const string GoogleEdmPackage = "com.google.external-dependency-manager";
        private const string OpenUpm = "https://package.openupm.com";

        public static List<SetupStep> Plan(AdsSettings settings, AdsValidator.Tab tab, IProjectProbe probe, PlatformCatalog catalog)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (probe == null) throw new ArgumentNullException(nameof(probe));
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            var steps = new List<SetupStep>();
            var kind = settings.SelectedMediation;
            var provider = catalog.Provider(kind);
            if (kind == AdProviderKind.None || provider?.sdk == null) return steps;
            var sdk = provider.sdk;
            bool max = kind == AdProviderKind.Max;

            bool sdkInstalled = probe.SdkInstalled(kind);
            steps.Add(new SetupStep("sdk", sdkInstalled ? SetupStatus.Done : max ? SetupStatus.Manual : SetupStatus.Pending, provider.name + " SDK",
                sdkInstalled ? "installed" : max ? sdk.upm?.installedBy ?? sdk.unitypackage : "adds " + sdk.upm?.package + (IsOpenUpm(sdk.upm?.registry) ? " from OpenUPM" : " from the Unity registry")));

            bool providerInstalled = probe.PackageVersion(sdk.providerPackage) != null;
            var providerUrls = ProviderPackageUrls(probe, sdk);
            steps.Add(new SetupStep("provider", providerInstalled ? SetupStatus.Done : !max && providerUrls != null ? SetupStatus.Pending : SetupStatus.Manual, "Nexus provider " + sdk.providerPackage,
                providerInstalled ? "installed" : max ? "the MAX provider does not exist yet (issue #14)" : providerUrls != null ? "adds " + string.Join(", ", providerUrls)
                : "add " + string.Join(", ", new[] { sdk.providerPackage }.Concat(sdk.providerDependencies ?? new string[0])) + " to Packages/manifest.json at the commit of com.nexus.ads"));

            if (sdk.resolver == "external")
                steps.Add(new SetupStep("resolver", probe.ExternalDependencyManagerInstalled ? SetupStatus.Done : SetupStatus.Pending, "dependency resolver",
                    probe.ExternalDependencyManagerInstalled ? "installed" : "adds " + GoogleEdmPackage + " from OpenUPM"));

            if (!string.IsNullOrEmpty(sdk.nativeDependencyFile))
            {
                bool present = probe.ProjectFileText(sdk.nativeDependencyFile) != null;
                string how = sdk.nativeDependencyFile;
                if (!present)
                    how = TryNativeSdk(probe.PackageFileText(sdk.upm?.package, sdk.nativeVersionsJson), probe.PackageVersion(sdk.upm?.package), out var version, out var url)
                        ? "downloads " + url + " (native SDK " + version + ") to " + sdk.nativeDependencyFile
                        : "needs the SDK package first (its " + sdk.nativeVersionsJson + " names the native SDK)";
                steps.Add(new SetupStep("native.sdk", present ? SetupStatus.Done : SetupStatus.Pending, "native SDK declaration", how));
            }

            bool lines = sdk.gradleLines != null && sdk.gradleLines.Length > 0;
            if (lines || !string.IsNullOrEmpty(sdk.resolvedMarker))
            {
                string template = probe.ProjectFileText(MainTemplatePath);
                steps.Add(new SetupStep("gradle.template", template != null ? SetupStatus.Done : SetupStatus.Pending, "custom main Gradle template",
                    template != null ? MainTemplatePath : "copies Unity's template to " + MainTemplatePath));
                if (lines)
                {
                    var missing = sdk.gradleLines.Where(l => template == null || template.IndexOf(l, StringComparison.Ordinal) < 0).ToList();
                    steps.Add(new SetupStep("gradle.lines", missing.Count == 0 ? SetupStatus.Done : SetupStatus.Pending, "Gradle lines",
                        missing.Count == 0 ? string.Join("; ", sdk.gradleLines) : "adds " + string.Join("; ", missing) + " (" + sdk.gradleNote + ")"));
                }
                // shortcut: resolution is judged by the marker in the main template; a project whose resolver writes .aar files into
                // Assets/Plugins/Android instead (template patching off) builds fine but reads as Pending; accept those files when one shows up.
                if (!string.IsNullOrEmpty(sdk.resolvedMarker))
                {
                    bool resolved = template != null && template.IndexOf(sdk.resolvedMarker, StringComparison.Ordinal) >= 0;
                    steps.Add(new SetupStep("resolve", resolved ? SetupStatus.Done : SetupStatus.Pending, "native dependencies resolved into the Gradle template",
                        resolved ? sdk.resolvedMarker : "runs the resolver's Android resolution"));
                }
            }

            if (kind == AdProviderKind.AdMob)
            {
                string wanted = (tab == AdsValidator.Tab.Release ? settings.Release : settings.Test).AdMob.Android.AppKey?.Trim();
                string actual = probe.AdMobSettingsAppId(AdPlatform.Android);
                steps.Add(string.IsNullOrEmpty(wanted)
                    ? new SetupStep("admob.appid", SetupStatus.Manual, "App ID in the Google Mobile Ads settings asset", "enter the App ID on the " + tab + " tab first")
                    : new SetupStep("admob.appid", actual == wanted ? SetupStatus.Done : SetupStatus.Pending, "App ID in the Google Mobile Ads settings asset",
                        actual == wanted ? wanted : "writes " + wanted + " (" + tab + " tab) to " + sdk.settingsAsset));
            }

            if (max) steps.Add(new SetupStep("max.sdkkey", SetupStatus.Manual, "MAX SDK key", "enter it in " + sdk.sdkKeyLocation));
            return steps;
        }

        // ----- pure text functions (EditMode tests lock them)

        /// <summary>
        /// The native SDK a LevelPlay package version pairs with, from the package's own LevelPlayVersions.json
        /// (<c>unityPackage.versions[v].ironSourceSdkVersion</c>, the lower bound of its range) and the dependency file URL.
        /// </summary>
        public static bool TryNativeSdk(string versionsJson, string packageVersion, out string sdkVersion, out string url)
        {
            sdkVersion = url = null;
            if (string.IsNullOrEmpty(versionsJson) || string.IsNullOrEmpty(packageVersion)) return false;
            var range = Regex.Match(versionsJson, "\"" + Regex.Escape(packageVersion) + "\"\\s*:\\s*\\{\\s*\"ironSourceSdkVersion\"\\s*:\\s*\"\\[\\s*([0-9][0-9.]*)");
            var template = Regex.Match(versionsJson, "\"dependencyXmlURL\"\\s*:\\s*\"([^\"]+)\"");
            if (!range.Success || !template.Success) return false;
            sdkVersion = range.Groups[1].Value;
            url = template.Groups[1].Value.Replace("${VERSION}", sdkVersion);
            return true;
        }

        /// <summary>Adds the missing lines right after the template's <c>dependencies {</c>, outside the resolver's markers; unchanged when all are there.</summary>
        public static string WithGradleLines(string template, IEnumerable<string> lines, string note)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            var missing = (lines ?? Enumerable.Empty<string>()).Where(l => template.IndexOf(l, StringComparison.Ordinal) < 0).ToList();
            if (missing.Count == 0) return template;
            var open = Regex.Match(template, @"^[ \t]*dependencies[ \t]*\{[ \t]*\r?\n", RegexOptions.Multiline);
            if (!open.Success) throw new InvalidOperationException("The Gradle template has no dependencies block.");
            string nl = template.Contains("\r\n") ? "\r\n" : "\n";
            string block = "    // Nexus Ads: " + note + nl + string.Concat(missing.Select(l => "    " + l + nl));
            return template.Insert(open.Index + open.Length, block);
        }

        /// <summary>The provider package and its missing family dependencies as git URLs at the core's commit, or null when one cannot be derived.</summary>
        private static List<string> ProviderPackageUrls(IProjectProbe probe, CatalogSdk sdk)
        {
            var urls = new List<string>();
            foreach (var package in new[] { sdk.providerPackage }.Concat((sdk.providerDependencies ?? new string[0]).Where(d => probe.PackageVersion(d) == null)))
            {
                string url = ProviderPackageUrl(probe.ConsumerManifestJson, package);
                if (url == null) return null;
                urls.Add(url);
            }
            return urls;
        }

        /// <summary>The git URL of a family package at the commit com.nexus.ads is pinned to, or null when com.nexus.ads is not a git URL (embedded, file:, registry).</summary>
        public static string ProviderPackageUrl(string manifestJson, string providerPackage)
        {
            if (string.IsNullOrEmpty(manifestJson) || string.IsNullOrEmpty(providerPackage)) return null;
            var core = Regex.Match(manifestJson, "\"com\\.nexus\\.ads\"\\s*:\\s*\"([^\"]*?/)com\\.nexus\\.ads(#[^\"]+)\"");
            return core.Success && core.Groups[1].Value.Contains(".git") ? core.Groups[1].Value + providerPackage + core.Groups[2].Value : null;
        }

        /// <summary>Packages/manifest.json with an OpenUPM scoped registry that carries every scope; adds the registry or extends its scopes.</summary>
        public static string WithOpenUpmScopes(string manifestJson, IEnumerable<string> scopes)
        {
            if (manifestJson == null) throw new ArgumentNullException(nameof(manifestJson));
            var wanted = (scopes ?? Enumerable.Empty<string>()).Distinct().ToList();
            if (wanted.Count == 0) return manifestJson;
            string nl = manifestJson.Contains("\r\n") ? "\r\n" : "\n";
            var registry = Regex.Match(manifestJson, @"\{[^{}]*package\.openupm\.com[^{}]*\}");
            if (!registry.Success)
            {
                string quoted = string.Join(", ", wanted.Select(s => "\"" + s + "\""));
                string block = "  \"scopedRegistries\": [" + nl + "    { \"name\": \"package.openupm.com\", \"url\": \"" + OpenUpm + "\", \"scopes\": [ " + quoted + " ] }" + nl + "  ]," + nl;
                if (Regex.IsMatch(manifestJson, "\"scopedRegistries\"\\s*:\\s*\\["))
                    return Regex.Replace(manifestJson, "(\"scopedRegistries\"\\s*:\\s*\\[)", "$1" + nl + "    { \"name\": \"package.openupm.com\", \"url\": \"" + OpenUpm + "\", \"scopes\": [ " + quoted + " ] },", RegexOptions.None, TimeSpan.FromSeconds(1));
                int brace = manifestJson.IndexOf('{');
                return manifestJson.Insert(brace + 1, nl + block.TrimEnd('\r', '\n'));
            }
            var scopesArray = Regex.Match(registry.Value, "\"scopes\"\\s*:\\s*\\[([^\\]]*)\\]");
            var present = scopesArray.Success ? Regex.Matches(scopesArray.Groups[1].Value, "\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).ToList() : new List<string>();
            var add = wanted.Where(w => !present.Any(p => w == p || w.StartsWith(p + ".", StringComparison.Ordinal))).ToList();
            if (add.Count == 0) return manifestJson;
            string updated = scopesArray.Success
                ? registry.Value.Substring(0, scopesArray.Groups[1].Index) + string.Join(", ", present.Concat(add).Select(s => " \"" + s + "\"")) + " " + registry.Value.Substring(scopesArray.Groups[1].Index + scopesArray.Groups[1].Length)
                : registry.Value.TrimEnd('}').TrimEnd() + ", \"scopes\": [ " + string.Join(", ", add.Select(s => "\"" + s + "\"")) + " ] }";
            return manifestJson.Substring(0, registry.Index) + updated + manifestJson.Substring(registry.Index + registry.Length);
        }

        private static bool IsOpenUpm(string registry) => registry != null && registry.StartsWith(OpenUpm, StringComparison.OrdinalIgnoreCase);

        // ----- side effects

        public sealed class ApplyResult
        {
            public readonly List<string> Log = new List<string>();
            /// <summary>Set when packages were requested: the domain reloads when it completes, and the next press continues.</summary>
            public AddAndRemoveRequest PackageRequest;
        }

        /// <summary>Performs the Pending steps whose prerequisites exist. Packages first (one request, one reload); then the files, the resolution and the App ID.</summary>
        public static ApplyResult Apply(AdsSettings settings, AdsValidator.Tab tab, PlatformCatalog catalog)
        {
            var result = new ApplyResult();
            var probe = new UnityProjectProbe();
            var plan = Plan(settings, tab, probe, catalog);
            var sdk = catalog.Provider(settings.SelectedMediation)?.sdk;
            bool Pending(string id) => plan.Any(s => s.id == id && s.status == SetupStatus.Pending);
            if (sdk == null) { result.Log.Add("no mediation selected; nothing to prepare"); return result; }

            var add = new List<string>();
            var scopes = new List<string>();
            if (Pending("sdk")) { add.Add(sdk.upm.package); if (IsOpenUpm(sdk.upm.registry)) scopes.AddRange(sdk.upm.scopes ?? new[] { sdk.upm.package }); }
            if (Pending("provider")) add.AddRange(ProviderPackageUrls(probe, sdk));
            if (Pending("resolver")) { add.Add(GoogleEdmPackage); scopes.Add(GoogleEdmPackage); }
            if (add.Count > 0)
            {
                if (scopes.Count > 0)
                {
                    string manifest = File.ReadAllText("Packages/manifest.json");
                    string updated = WithOpenUpmScopes(manifest, scopes);
                    if (updated != manifest) { File.WriteAllText("Packages/manifest.json", updated); result.Log.Add("OpenUPM scopes: " + string.Join(", ", scopes.Distinct())); }
                }
                result.PackageRequest = Client.AddAndRemove(add.ToArray(), null);
                result.Log.Add("adding " + string.Join(", ", add) + "; press Prepare Android again after the packages are imported");
                return result;
            }

            if (Pending("native.sdk"))
            {
                if (TryNativeSdk(probe.PackageFileText(sdk.upm.package, sdk.nativeVersionsJson), probe.PackageVersion(sdk.upm.package), out var version, out var url))
                {
                    string xml;
                    using (var client = new System.Net.WebClient()) xml = client.DownloadString(url);
                    if (xml.IndexOf("<dependencies>", StringComparison.Ordinal) < 0) throw new InvalidOperationException(url + " did not return a dependency file.");
                    Directory.CreateDirectory(Path.GetDirectoryName(sdk.nativeDependencyFile));
                    File.WriteAllText(sdk.nativeDependencyFile, xml);
                    AssetDatabase.ImportAsset(sdk.nativeDependencyFile);
                    result.Log.Add("native SDK " + version + ": " + url + " → " + sdk.nativeDependencyFile);
                }
                else result.Log.Add("native SDK: the SDK package's " + sdk.nativeVersionsJson + " names no version for " + probe.PackageVersion(sdk.upm.package));
            }
            if (Pending("gradle.template"))
            {
                string source = Path.Combine(BuildPipeline.GetPlaybackEngineDirectory(BuildTarget.Android, BuildOptions.None), "Tools", "GradleTemplates", "mainTemplate.gradle");
                Directory.CreateDirectory(Path.GetDirectoryName(MainTemplatePath));
                File.Copy(source, MainTemplatePath);
                AssetDatabase.ImportAsset(MainTemplatePath);
                result.Log.Add("custom main Gradle template: " + source + " → " + MainTemplatePath);
            }
            if (sdk.gradleLines != null && sdk.gradleLines.Length > 0 && File.Exists(MainTemplatePath))
            {
                string template = File.ReadAllText(MainTemplatePath);
                string updated = WithGradleLines(template, sdk.gradleLines, sdk.gradleNote);
                if (updated != template) { File.WriteAllText(MainTemplatePath, updated); result.Log.Add("Gradle lines: " + string.Join("; ", sdk.gradleLines)); }
            }
            if (!string.IsNullOrEmpty(sdk.resolvedMarker) && File.Exists(MainTemplatePath) && File.ReadAllText(MainTemplatePath).IndexOf(sdk.resolvedMarker, StringComparison.Ordinal) < 0)
            {
                var resolver = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("GooglePlayServices.PlayServicesResolver")).FirstOrDefault(t => t != null);
                var resolveSync = resolver?.GetMethods().FirstOrDefault(m => m.Name == "ResolveSync" && m.GetParameters().Length == 1);
                if (resolveSync == null) result.Log.Add("resolve: no dependency resolver in the editor yet");
                else result.Log.Add("resolve: ResolveSync returned " + resolveSync.Invoke(null, new object[] { true }));
            }
            if (Pending("admob.appid"))
            {
                string appId = (tab == AdsValidator.Tab.Release ? settings.Release : settings.Test).AdMob.Android.AppKey.Trim();
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("GoogleMobileAds.Editor.GoogleMobileAdsSettings")).FirstOrDefault(t => t != null);
                var load = type?.GetMethod("LoadInstance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (load == null) result.Log.Add("App ID: the Google Mobile Ads editor settings type was not found; enter it in Assets > Google Mobile Ads > Settings");
                else
                {
                    var so = new SerializedObject((ScriptableObject)load.Invoke(null, null));
                    so.FindProperty("adMobAndroidAppId").stringValue = appId;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    AssetDatabase.SaveAssets();
                    result.Log.Add("App ID " + appId + " (" + tab + " tab) → " + sdk.settingsAsset);
                }
            }
            AssetDatabase.Refresh();
            return result;
        }

        /// <summary>
        /// Command line: <c>-executeMethod Nexus.Ads.Editor.AndroidSetup.PrepareCli [-adsTab Test|Release]</c>, without
        /// <c>-quit</c> (package requests complete on the editor loop; the method exits the editor itself). Exit 0: every step
        /// Done; 1: Pending or Manual steps remain; 2: could not run; 3: packages were added, run it again.
        /// </summary>
        public static void PrepareCli()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                int i = Array.IndexOf(args, "-adsTab");
                var tab = i >= 0 && i + 1 < args.Length && args[i + 1] == "Release" ? AdsValidator.Tab.Release : AdsValidator.Tab.Test;
                var settings = AdsSettings.Load();
                if (settings == null) { Console.WriteLine("[Nexus Ads] no settings asset (Resources/" + AdsSettings.ResourceName + ")"); EditorApplication.Exit(2); return; }
                var catalog = PlatformCatalog.Load();
                var applied = Apply(settings, tab, catalog);
                foreach (var line in applied.Log) Console.WriteLine("[Nexus Ads] Prepare Android: " + line);
                if (applied.PackageRequest != null)
                {
                    EditorApplication.update += WaitForPackages;
                    void WaitForPackages()
                    {
                        if (!applied.PackageRequest.IsCompleted) return;
                        EditorApplication.update -= WaitForPackages;
                        Console.WriteLine("[Nexus Ads] Prepare Android: packages " + applied.PackageRequest.Status + (applied.PackageRequest.Error != null ? " " + applied.PackageRequest.Error.message : "") + "; run it again");
                        EditorApplication.Exit(applied.PackageRequest.Status == StatusCode.Success ? 3 : 2);
                    }
                    return;
                }
                var plan = Plan(settings, tab, new UnityProjectProbe(), catalog);
                foreach (var step in plan) Console.WriteLine("[Nexus Ads] Prepare Android: " + step);
                EditorApplication.Exit(plan.All(s => s.status == SetupStatus.Done) ? 0 : 1);
            }
            catch (Exception e)
            {
                Console.WriteLine("[Nexus Ads] Prepare Android could not run: " + e);
                EditorApplication.Exit(2);
            }
        }
    }
}
