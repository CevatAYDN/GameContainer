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
    /// provider package, the dependency resolver, the native SDK declaration and the network adapter the mediation
    /// needs to fill (LevelPlay: Unity Ads), the custom main Gradle template
    /// with the catalog's Gradle lines, the resolved dependencies and the AdMob App ID. <see cref="Plan"/> is a pure
    /// function over settings, probe and catalog (the validator's <c>android.nativesdk</c> and <c>android.gradle</c>
    /// read it); <see cref="Apply"/> performs the Pending steps. Adding packages reloads the domain, so a press that
    /// adds packages stops there and the next press continues: every step is idempotent.
    /// </summary>
    public static class AndroidSetup
    {
        public const string MainTemplatePath = "Assets/Plugins/Android/mainTemplate.gradle";
        public const string PropertiesTemplatePath = "Assets/Plugins/Android/gradleTemplate.properties";
        public const string GoogleEdmPackage = "com.google.external-dependency-manager";
        public const string UnityEdmPackage = "com.unity.external-dependency-manager";
        private const string OpenUpm = "https://package.openupm.com";

        /// <summary>The resolver Prepare Android adds: Google's from OpenUPM, or Unity's from the Unity registry when a disabled stub holds Google's name.</summary>
        public static string ResolverPackage(IProjectProbe probe) => probe.DisabledEdmStub ? UnityEdmPackage : GoogleEdmPackage;

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
            string providerUrl = ProviderPackageUrl(probe.ConsumerManifestJson, sdk.providerPackage);
            steps.Add(new SetupStep("provider", providerInstalled ? SetupStatus.Done : !max && providerUrl != null ? SetupStatus.Pending : SetupStatus.Manual, "Nexus provider " + sdk.providerPackage,
                providerInstalled ? "installed" : max ? "the MAX provider does not exist yet (issue #86)" : providerUrl != null ? "adds " + providerUrl
                : "add " + sdk.providerPackage + " to Packages/manifest.json at the commit of com.nexus.ads"));

            if (sdk.resolver == "external")
                steps.Add(new SetupStep("resolver", probe.ExternalDependencyManagerInstalled ? SetupStatus.Done : SetupStatus.Pending, "dependency resolver",
                    probe.ExternalDependencyManagerInstalled ? "installed"
                    : probe.DisabledEdmStub ? "adds " + UnityEdmPackage + " from the Unity registry (this project's " + GoogleEdmPackage + " is a disabled stub, which would shadow the OpenUPM package)"
                    : "adds " + GoogleEdmPackage + " from OpenUPM"));

            // A declaration is done only at the version the installed package selects (issues #144, #145): one the package was
            // upgraded past, or one older than what LevelPlay's own installer takes, is replaced, never accepted for being there.
            string package = probe.PackageVersion(sdk.upm?.package);
            string versions = VersionsJsonFor(probe, sdk, out string source);
            bool nativeDone = true;
            if (!string.IsNullOrEmpty(sdk.nativeDependencyFile))
            {
                bool known = TryNativeSdk(versions, package, out var version, out var url);
                string declared = DeclaredVersion(probe.ProjectFileText(sdk.nativeDependencyFile));
                nativeDone = known && SameVersion(declared, version);
                steps.Add(new SetupStep("native.sdk", nativeDone ? SetupStatus.Done : known || package == null ? SetupStatus.Pending : SetupStatus.Manual, "native SDK declaration",
                    nativeDone ? sdk.nativeDependencyFile + " (" + version + ")"
                    : known ? (declared == null ? "downloads " : "replaces native SDK " + declared + " with ") + url + " (native SDK " + version + ", the newest " + source + " allows for " + sdk.upm?.package + " " + package + ") to " + sdk.nativeDependencyFile
                    : package == null ? "needs the SDK package first (its " + sdk.nativeVersionsJson + " names the native SDK)"
                    : source + " names no native SDK for " + sdk.upm?.package + " " + package + "; declare it in " + sdk.nativeDependencyFile + " by hand"));
            }

            var adapter = sdk.nativeAdapter;
            if (!string.IsNullOrEmpty(adapter?.dependencyFile))
            {
                bool known = TryAdapter(versions, package, adapter.key, out var version, out var url);
                string declared = DeclaredVersion(probe.ProjectFileText(adapter.dependencyFile));
                bool done = known && SameVersion(declared, version);
                nativeDone &= done;
                steps.Add(new SetupStep("native.adapter", done ? SetupStatus.Done : known || package == null ? SetupStatus.Pending : SetupStatus.Manual, adapter.key + " adapter declaration",
                    done ? adapter.dependencyFile + " (" + version + ")"
                    : known ? (declared == null ? "downloads " : "replaces " + adapter.key + " adapter " + declared + " with ") + url + " (" + adapter.key + " adapter " + version + ") to " + adapter.dependencyFile
                    : package == null ? "needs the SDK package first (its " + sdk.nativeVersionsJson + " names the adapter)"
                    : source + " names no " + adapter.key + " adapter for " + sdk.upm?.package + " " + package + "; declare it in " + adapter.dependencyFile + " by hand"));
            }

            bool lines = sdk.gradleLines != null && sdk.gradleLines.Length > 0;
            if (lines || !string.IsNullOrEmpty(sdk.resolvedMarker))
            {
                string template = probe.ProjectFileText(MainTemplatePath);
                steps.Add(new SetupStep("gradle.template", template != null ? SetupStatus.Done : SetupStatus.Pending, "custom main Gradle template",
                    template != null ? MainTemplatePath : "copies Unity's template to " + MainTemplatePath));
                if (!string.IsNullOrEmpty(sdk.gradlePropertiesNote))
                {
                    bool properties = probe.ProjectFileText(PropertiesTemplatePath) != null;
                    steps.Add(new SetupStep("gradle.properties", properties ? SetupStatus.Done : SetupStatus.Pending, "custom Gradle properties template",
                        properties ? PropertiesTemplatePath : "copies Unity's template to " + PropertiesTemplatePath + " (" + sdk.gradlePropertiesNote + ")"));
                }
                if (lines)
                {
                    var missing = sdk.gradleLines.Where(l => template == null || template.IndexOf(l, StringComparison.Ordinal) < 0).ToList();
                    steps.Add(new SetupStep("gradle.lines", missing.Count == 0 ? SetupStatus.Done : SetupStatus.Pending, "Gradle lines",
                        missing.Count == 0 ? string.Join("; ", sdk.gradleLines) : "adds " + string.Join("; ", missing) + " (" + sdk.gradleNote + ")"));
                }
                // shortcut: resolution is judged by the main template; a project whose resolver writes .aar files into
                // Assets/Plugins/Android instead (template patching off) builds fine but reads as Pending; accept those files when one shows up.
                if (!string.IsNullOrEmpty(sdk.resolvedMarker))
                {
                    // the version each declaration names, not only the artifact: a template resolved before an upgrade names the old one (#145)
                    var unresolved = UnresolvedSpecs(template, sdk, probe.ProjectFileText);
                    bool resolved = nativeDone && unresolved.Count == 0;
                    steps.Add(new SetupStep("resolve", resolved ? SetupStatus.Done : SetupStatus.Pending, "native dependencies resolved into the Gradle template",
                        resolved ? string.Join(", ", DeclaredSpecs(sdk, probe.ProjectFileText)) : "runs the resolver's Android resolution" + (unresolved.Count > 0 ? " (the template does not name " + string.Join(", ", unresolved) + ")" : "")));
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
        /// The native SDK LevelPlay's own installer takes for a package version: the newest <c>IronSourceSdk.versions</c> entry
        /// inside the package's <c>unityPackage.versions[v].ironSourceSdkVersion</c> (a range, or one exact version), and its
        /// dependency file URL. False when the file lists the package without a version in its range (issue #144).
        /// </summary>
        public static bool TryNativeSdk(string versionsJson, string packageVersion, out string sdkVersion, out string url)
        {
            sdkVersion = url = null;
            if (string.IsNullOrEmpty(versionsJson) || string.IsNullOrEmpty(packageVersion)) return false;
            string packages = Block(versionsJson, "unityPackage", 1);
            string sdk = Block(versionsJson, "IronSourceSdk", 1);
            if (packages == null || sdk == null) return false;
            var range = Regex.Match(packages, "\"" + Regex.Escape(packageVersion) + "\"\\s*:\\s*\\{\\s*\"ironSourceSdkVersion\"\\s*:\\s*\"([^\"]*)\"");
            var template = Regex.Match(sdk, "\"dependencyXmlURL\"\\s*:\\s*\"([^\"]+)\"");
            string list = Block(sdk, "versions", 1);
            if (!range.Success || !template.Success || list == null) return false;
            Version best = null;
            foreach (Match m in Regex.Matches(list, "\"([0-9]+(?:\\.[0-9]+)*)\"\\s*:\\s*\\{"))
                if (TryVersion(m.Groups[1].Value, out var candidate) && Allows(range.Groups[1].Value, candidate) && (best == null || candidate > best)) { best = candidate; sdkVersion = m.Groups[1].Value; }
            if (best == null) return false;
            url = template.Groups[1].Value.Replace("${VERSION}", sdkVersion);
            return true;
        }

        /// <summary>
        /// The versions file the plan reads: LevelPlay's own copy in the project when it lists the installed package (its
        /// installer works from that copy and refreshes it), otherwise the package's file (issue #144).
        /// </summary>
        public static string VersionsJsonFor(IProjectProbe probe, CatalogSdk sdk, out string source)
        {
            string package = probe.PackageVersion(sdk.upm?.package);
            string project = string.IsNullOrEmpty(sdk.nativeVersionsProjectFile) ? null : probe.ProjectFileText(sdk.nativeVersionsProjectFile);
            string listed = Block(project, "unityPackage", 1);
            if (package != null && listed != null && Regex.IsMatch(listed, "\"" + Regex.Escape(package) + "\"\\s*:\\s*\\{"))
            {
                source = sdk.nativeVersionsProjectFile;
                return project;
            }
            source = sdk.upm?.package + "/" + sdk.nativeVersionsJson;
            return package == null ? null : probe.PackageFileText(sdk.upm?.package, sdk.nativeVersionsJson);
        }

        /// <summary>The version a LevelPlay dependency file declares itself as (<c>&lt;unityversion&gt;</c>, what LevelPlay's installer reads back); null without one.</summary>
        public static string DeclaredVersion(string dependencyXml)
        {
            var m = Regex.Match(dependencyXml ?? "", "<unityversion>\\s*([^<\\s]+)\\s*</unityversion>");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>The Android spec (<c>group:artifact:version</c>) a dependency file declares for an artifact; null when it declares none.</summary>
        public static string DeclaredSpec(string dependencyXml, string artifact)
        {
            if (dependencyXml == null || string.IsNullOrEmpty(artifact)) return null;
            var m = Regex.Match(dependencyXml, "spec\\s*=\\s*\"(" + Regex.Escape(artifact) + ":[^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>The specs the declarations name for the artifacts the resolver must write into the template, in catalog order.</summary>
        public static List<string> DeclaredSpecs(CatalogSdk sdk, Func<string, string> readProjectFile)
        {
            var specs = new List<string>();
            foreach (var (artifact, file) in Declarations(sdk))
            {
                string spec = DeclaredSpec(readProjectFile(file), artifact);
                if (spec != null) specs.Add(spec);
            }
            return specs;
        }

        /// <summary>
        /// What the main template does not name yet: each declared spec with its version (a template resolved before an
        /// upgrade names the old one), or the artifact itself when no declaration names it (issue #145).
        /// </summary>
        public static List<string> UnresolvedSpecs(string template, CatalogSdk sdk, Func<string, string> readProjectFile)
        {
            var missing = new List<string>();
            foreach (var (artifact, file) in Declarations(sdk))
            {
                string spec = DeclaredSpec(readProjectFile(file), artifact);
                if (spec == null) missing.Add(artifact);
                else if (template == null || !Regex.IsMatch(template, "['\"]" + Regex.Escape(spec) + "['\"]")) missing.Add(spec);
            }
            return missing;
        }

        private static IEnumerable<(string artifact, string file)> Declarations(CatalogSdk sdk)
        {
            if (!string.IsNullOrEmpty(sdk.resolvedMarker)) yield return (sdk.resolvedMarker, sdk.nativeDependencyFile);
            if (!string.IsNullOrEmpty(sdk.nativeAdapter?.resolvedMarker)) yield return (sdk.nativeAdapter.resolvedMarker, sdk.nativeAdapter.dependencyFile);
        }

        private static bool SameVersion(string a, string b) => TryVersion(a, out var x) && TryVersion(b, out var y) && x == y;

        /// <summary>The key and object of <paramref name="key"/> where <paramref name="openBraces"/> objects are open (1: a member of the outermost object); null when absent.</summary>
        private static string Block(string json, string key, int openBraces)
        {
            if (string.IsNullOrEmpty(json)) return null;
            foreach (Match m in new Regex("\"" + Regex.Escape(key) + "\"\\s*:\\s*\\{").Matches(json))
                if (Depth(json, m.Index) == openBraces) return json.Substring(m.Index, ObjectEnd(json, m.Index + m.Length - 1) - m.Index);
            return null;
        }

        /// <summary>How many objects are open at <paramref name="index"/>; braces inside strings do not count.</summary>
        private static int Depth(string json, int index)
        {
            int depth = 0;
            bool inString = false;
            for (int i = 0; i < index; i++)
            {
                char c = json[i];
                if (inString) { if (c == '\\') i++; else if (c == '"') inString = false; continue; }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}') depth--;
            }
            return depth;
        }

        /// <summary>A LevelPlay compatibility value: a range (<see cref="InRange"/>) or one exact version, as LevelPlay's own regexes read it.</summary>
        private static bool Allows(string rangeOrVersion, Version v)
            => Regex.IsMatch(rangeOrVersion ?? "", @"^\s*[\[(\]]") ? InRange(rangeOrVersion, v) : TryVersion(rangeOrVersion?.Trim(), out var exact) && exact == v;

        /// <summary>
        /// The newest adapter version in LevelPlayVersions.json (<c>adapters[key].versions</c>) whose <c>ironSourceSdkVersion</c>
        /// range holds the native SDK the package version pairs with, and its dependency file URL (from the adapter's own block).
        /// </summary>
        public static bool TryAdapter(string versionsJson, string packageVersion, string key, out string adapterVersion, out string url)
        {
            adapterVersion = url = null;
            if (string.IsNullOrEmpty(key) || !TryNativeSdk(versionsJson, packageVersion, out var native, out _) || !TryVersion(native, out var sdk)) return false;
            var adapters = Regex.Match(versionsJson, "\"adapters\"\\s*:\\s*\\{");
            if (!adapters.Success) return false;
            var start = new Regex("\"" + Regex.Escape(key) + "\"\\s*:\\s*\\{").Match(versionsJson, adapters.Index + adapters.Length);
            if (!start.Success) return false;
            string block = versionsJson.Substring(start.Index, ObjectEnd(versionsJson, start.Index + start.Length - 1) - start.Index);
            var template = Regex.Match(block, "\"dependencyXmlURL\"\\s*:\\s*\"([^\"]+)\"");
            if (!template.Success) return false;
            Version best = null;
            foreach (Match m in Regex.Matches(block, "\"([0-9]+(?:\\.[0-9]+)*)\"\\s*:\\s*\\{\\s*\"ironSourceSdkVersion\"\\s*:\\s*\"([^\"]*)\""))
                if (TryVersion(m.Groups[1].Value, out var candidate) && Allows(m.Groups[2].Value, sdk) && (best == null || candidate > best)) { best = candidate; adapterVersion = m.Groups[1].Value; }
            if (best == null) return false;
            url = template.Groups[1].Value.Replace("${VERSION}", adapterVersion);
            return true;
        }

        /// <summary>The index after the object that opens at <paramref name="open"/>; braces inside strings (<c>${VERSION}</c>) do not count.</summary>
        private static int ObjectEnd(string json, int open)
        {
            int depth = 0;
            bool inString = false;
            for (int i = open; i < json.Length; i++)
            {
                char c = json[i];
                if (inString) { if (c == '\\') i++; else if (c == '"') inString = false; continue; }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return i + 1;
            }
            return json.Length;
        }

        /// <summary>A Maven-style range as LevelPlayVersions.json writes it: <c>[9.0.0, 10.0[</c> (a bracket facing away excludes the bound).</summary>
        private static bool InRange(string range, Version v)
        {
            var m = Regex.Match(range ?? "", @"^\s*([\[(\]])\s*([0-9.]*)\s*,\s*([0-9.]*)\s*([\])\[])\s*$");
            if (!m.Success) return false;
            if (TryVersion(m.Groups[2].Value, out var low) && (m.Groups[1].Value == "[" ? v < low : v <= low)) return false;
            if (TryVersion(m.Groups[3].Value, out var high) && (m.Groups[4].Value == "]" ? v > high : v >= high)) return false;
            return true;
        }

        /// <summary>Up to four numeric parts, padded with zeros so 9.0 and 9.0.0 compare equal.</summary>
        private static bool TryVersion(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrEmpty(text)) return false;
            var parts = text.Split('.');
            if (parts.Length > 4) return false;
            var n = new int[4];
            for (int i = 0; i < parts.Length; i++) if (!int.TryParse(parts[i], out n[i])) return false;
            version = new Version(n[0], n[1], n[2], n[3]);
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
            var sdk = catalog.Provider(settings.SelectedMediation)?.sdk;
            if (sdk == null) { result.Log.Add("no mediation selected; nothing to prepare"); return result; }
            // LevelPlay's installer refreshes its versions file before it chooses the native SDK; the plan chooses from the same file (#144)
            if (!string.IsNullOrEmpty(sdk.nativeVersionsUrl) && probe.PackageVersion(sdk.upm?.package) != null) result.Log.Add(RefreshVersionsFile(sdk));
            var plan = Plan(settings, tab, probe, catalog);
            bool Pending(string id) => plan.Any(s => s.id == id && s.status == SetupStatus.Pending);

            var add = new List<string>();
            var scopes = new List<string>();
            if (Pending("sdk")) { add.Add(sdk.upm.package); if (IsOpenUpm(sdk.upm.registry)) scopes.AddRange(sdk.upm.scopes ?? new[] { sdk.upm.package }); }
            if (Pending("provider")) add.Add(ProviderPackageUrl(probe.ConsumerManifestJson, sdk.providerPackage));
            if (Pending("resolver")) { add.Add(ResolverPackage(probe)); if (!probe.DisabledEdmStub) scopes.Add(GoogleEdmPackage); }
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
                if (TryNativeSdk(VersionsJsonFor(probe, sdk, out string source), probe.PackageVersion(sdk.upm.package), out var version, out var url))
                {
                    DownloadDependencyFile(url, sdk.nativeDependencyFile);
                    result.Log.Add("native SDK " + version + ": " + url + " → " + sdk.nativeDependencyFile);
                }
                else result.Log.Add("native SDK: " + source + " names no version for " + sdk.upm.package + " " + probe.PackageVersion(sdk.upm.package));
            }
            if (Pending("native.adapter"))
            {
                var adapter = sdk.nativeAdapter;
                if (TryAdapter(VersionsJsonFor(probe, sdk, out string source), probe.PackageVersion(sdk.upm.package), adapter.key, out var version, out var url))
                {
                    DownloadDependencyFile(url, adapter.dependencyFile);
                    result.Log.Add(adapter.key + " adapter " + version + ": " + url + " → " + adapter.dependencyFile);
                }
                else result.Log.Add(adapter.key + " adapter: " + source + " names no version for " + sdk.upm.package + " " + probe.PackageVersion(sdk.upm.package));
            }
            if (Pending("gradle.template"))
            {
                string source = Path.Combine(BuildPipeline.GetPlaybackEngineDirectory(BuildTarget.Android, BuildOptions.None), "Tools", "GradleTemplates", "mainTemplate.gradle");
                Directory.CreateDirectory(Path.GetDirectoryName(MainTemplatePath));
                File.Copy(source, MainTemplatePath);
                AssetDatabase.ImportAsset(MainTemplatePath);
                result.Log.Add("custom main Gradle template: " + source + " → " + MainTemplatePath);
            }
            if (Pending("gradle.properties"))
            {
                string source = Path.Combine(BuildPipeline.GetPlaybackEngineDirectory(BuildTarget.Android, BuildOptions.None), "Tools", "GradleTemplates", "gradleTemplate.properties");
                Directory.CreateDirectory(Path.GetDirectoryName(PropertiesTemplatePath));
                File.Copy(source, PropertiesTemplatePath);
                AssetDatabase.ImportAsset(PropertiesTemplatePath);
                result.Log.Add("custom Gradle properties template: " + source + " → " + PropertiesTemplatePath);
            }
            if (sdk.gradleLines != null && sdk.gradleLines.Length > 0 && File.Exists(MainTemplatePath))
            {
                string template = File.ReadAllText(MainTemplatePath);
                string updated = WithGradleLines(template, sdk.gradleLines, sdk.gradleNote);
                if (updated != template) { File.WriteAllText(MainTemplatePath, updated); result.Log.Add("Gradle lines: " + string.Join("; ", sdk.gradleLines)); }
            }
            // after the downloads: a declaration replaced above leaves its old version in the template (#145)
            if (!string.IsNullOrEmpty(sdk.resolvedMarker) && File.Exists(MainTemplatePath) && UnresolvedSpecs(File.ReadAllText(MainTemplatePath), sdk, probe.ProjectFileText).Count > 0)
            {
                var resolver = UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies().Select(a => a.GetType("GooglePlayServices.PlayServicesResolver")).FirstOrDefault(t => t != null);
                var resolveSync = resolver?.GetMethods().FirstOrDefault(m => m.Name == "ResolveSync" && m.GetParameters().Length == 1);
                if (resolveSync == null) result.Log.Add("resolve: no dependency resolver in the editor yet");
                else
                {
                    object answer = resolveSync.Invoke(null, new object[] { true });
                    // the resolver has answered false while writing the template (runs of 2026-10-24 and -25); the template is what Gradle reads, so report it
                    var missing = UnresolvedSpecs(File.ReadAllText(MainTemplatePath), sdk, probe.ProjectFileText);
                    result.Log.Add(missing.Count == 0
                        ? "resolve: the Gradle template now names " + string.Join(", ", DeclaredSpecs(sdk, probe.ProjectFileText))
                        : "resolve: the Gradle template still misses " + string.Join(", ", missing) + " (the resolver answered " + answer + "); run Assets > External Dependency Manager > Android Resolver > Force Resolve and read its log");
                }
            }
            if (Pending("admob.appid"))
            {
                string appId = (tab == AdsValidator.Tab.Release ? settings.Release : settings.Test).AdMob.Android.AppKey.Trim();
                var type = UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies().Select(a => a.GetType("GoogleMobileAds.Editor.GoogleMobileAdsSettings")).FirstOrDefault(t => t != null);
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

        /// <summary>Writes LevelPlay's remote versions file over the project's copy, as LevelPlay's installer does; when it cannot be read the plan keeps the files it has, and the log says so.</summary>
        private static string RefreshVersionsFile(CatalogSdk sdk)
        {
            string json;
            try
            {
                using (var client = new System.Net.WebClient()) json = client.DownloadString(sdk.nativeVersionsUrl);
            }
            catch (System.Net.WebException e)
            {
                return "versions file: " + sdk.nativeVersionsUrl + " could not be read (" + e.Message + "); the plan reads the versions files already in the project and the package";
            }
            if (Block(json, "unityPackage", 1) == null || Block(json, "IronSourceSdk", 1) == null)
                return "versions file: " + sdk.nativeVersionsUrl + " returned no LevelPlay versions; the plan reads the versions files already in the project and the package";
            string path = sdk.nativeVersionsProjectFile;
            if (File.Exists(path) && File.ReadAllText(path) == json) return "versions file: " + path + " is current with " + sdk.nativeVersionsUrl;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, json);
            AssetDatabase.ImportAsset(path);
            return "versions file: " + sdk.nativeVersionsUrl + " → " + path;
        }

        private static void DownloadDependencyFile(string url, string path)
        {
            string xml;
            using (var client = new System.Net.WebClient()) xml = client.DownloadString(url);
            if (xml.IndexOf("<dependencies>", StringComparison.Ordinal) < 0) throw new InvalidOperationException(url + " did not return a dependency file.");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, xml);
            AssetDatabase.ImportAsset(path);
        }

        /// <summary>
        /// Command line: <c>-executeMethod Nexus.Ads.Editor.AndroidSetup.PrepareCli [-adsTab Test|Release]</c>, without
        /// <c>-quit</c> (package requests complete on the editor loop; the method exits the editor itself). Exit 0: every step
        /// Done; 1: Pending or Manual steps remain; 2: could not run; 3: packages were added, run it again.
        /// </summary>
        // Unity's logger, not Console: one run's Console lines never reached its -logFile while the logger's did (issue #50)
        private static void Report(string line) => Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", line);

        public static void PrepareCli()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                int i = Array.IndexOf(args, "-adsTab");
                var tab = i >= 0 && i + 1 < args.Length && args[i + 1] == "Release" ? AdsValidator.Tab.Release : AdsValidator.Tab.Test;
                var settings = AdsSettings.Load();
                if (settings == null) { Report("[Nexus Ads] no settings asset (Resources/" + AdsSettings.ResourceName + ")"); EditorApplication.Exit(2); return; }
                var catalog = PlatformCatalog.Load();
                var applied = Apply(settings, tab, catalog);
                foreach (var line in applied.Log) Report("[Nexus Ads] Prepare Android: " + line);
                if (applied.PackageRequest != null)
                {
                    EditorApplication.update += WaitForPackages;
                    void WaitForPackages()
                    {
                        if (!applied.PackageRequest.IsCompleted) return;
                        EditorApplication.update -= WaitForPackages;
                        Report("[Nexus Ads] Prepare Android: packages " + applied.PackageRequest.Status + (applied.PackageRequest.Error != null ? " " + applied.PackageRequest.Error.message : "") + "; run it again");
                        EditorApplication.Exit(applied.PackageRequest.Status == StatusCode.Success ? 3 : 2);
                    }
                    return;
                }
                var plan = Plan(settings, tab, new UnityProjectProbe(), catalog);
                foreach (var step in plan) Report("[Nexus Ads] Prepare Android: " + step);
                EditorApplication.Exit(plan.All(s => s.status == SetupStatus.Done) ? 0 : 1);
            }
            catch (Exception e)
            {
                Report("[Nexus Ads] Prepare Android could not run: " + e);
                EditorApplication.Exit(2);
            }
        }
    }
}
