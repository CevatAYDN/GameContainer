using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Nexus.Ads.Unity
{
    /// <summary>The five words every surface shows for a provider; computed in one place only.</summary>
    public enum ProviderStatus
    {
        /// <summary>The SDK or the provider package is not in the project.</summary>
        NotInstalled,
        /// <summary>A required Release field is empty and there is no official test data.</summary>
        Inactive,
        /// <summary>Release is incomplete but the Test tab holds the provider's official test data.</summary>
        TestOnly,
        /// <summary>Release is complete and valid.</summary>
        Active,
        /// <summary>Release is complete but wrong: a malformed id, test data on the Release tab.</summary>
        Invalid,
    }

    public readonly struct ProviderStatusReport
    {
        public readonly ProviderStatus Status;
        /// <summary>The Release fields still empty (Inactive, TestOnly) or the fault (Invalid); empty otherwise.</summary>
        public readonly IReadOnlyList<string> Missing;
        public readonly string Reason;

        public ProviderStatusReport(ProviderStatus status, IReadOnlyList<string> missing, string reason)
        {
            Status = status;
            Missing = missing;
            Reason = reason;
        }

        /// <summary>"Active", or "Inactive — Release: interstitial unit id missing".</summary>
        public override string ToString() => Status + (string.IsNullOrEmpty(Reason) ? "" : " — " + Reason);

        public bool IncludedInRelease => Status == ProviderStatus.Active;
    }

    /// <summary>
    /// The only place that computes a provider's status from the settings and the environment. Project
    /// Settings, the wizard, the validator, the build and the debug panel show its result and never
    /// recompute it. Status is per platform.
    /// </summary>
    public static class ProviderStatusResolver
    {
        private static readonly Regex AdMobId = new Regex(@"^ca-app-pub-\d{16}[~/]\d{10}$", RegexOptions.CultureInvariant);

        /// <param name="sdkInstalled">The provider's SDK and its Nexus provider package are both in the project.</param>
        /// <param name="conflictingSdk">A second mediation SDK is in the project (the validator's two-mediation finding).</param>
        public static ProviderStatusReport Resolve(AdsSettings settings, AdProviderKind kind, AdPlatform platform, bool sdkInstalled, bool conflictingSdk = false)
        {
            if (kind == AdProviderKind.None || !sdkInstalled)
                return new ProviderStatusReport(ProviderStatus.NotInstalled, new string[0], kind == AdProviderKind.None ? "no mediation selected" : kind + " SDK is not installed");
            if (conflictingSdk)
                return new ProviderStatusReport(ProviderStatus.Invalid, new[] { "a second mediation SDK is in the project" }, "a second mediation SDK is in the project; one build carries one mediation");

            var missing = settings.MissingFields(kind, platform, settings.Release);
            if (missing.Count > 0)
            {
                bool testData = kind == AdProviderKind.AdMob && settings.MissingFields(kind, platform, settings.Test).Count == 0
                    && AdMobSampleIds.IsSample(settings.Test.AdMob.For(platform).AppKey);
                return testData
                    ? new ProviderStatusReport(ProviderStatus.TestOnly, missing, "Release: " + string.Join(", ", missing) + " missing; Test tab holds Google's sample units")
                    : new ProviderStatusReport(ProviderStatus.Inactive, missing, "Release: " + string.Join(", ", missing) + " missing");
            }

            var keys = settings.Release.For(kind).For(platform);
            string fault = Fault(kind, keys, settings);
            return fault == null
                ? new ProviderStatusReport(ProviderStatus.Active, new string[0], null)
                : new ProviderStatusReport(ProviderStatus.Invalid, new[] { fault }, "Release: " + fault);
        }

        private static string Fault(AdProviderKind kind, PlatformKeys keys, AdsSettings settings)
        {
            if (kind == AdProviderKind.AdMob)
            {
                if (AdMobSampleIds.IsSample(keys.AppKey)) return "App ID is Google's sample id";
                if (!AdMobId.IsMatch(keys.AppKey.Trim())) return "App ID is not of the form ca-app-pub-XXXXXXXXXXXXXXXX~XXXXXXXXXX";
                foreach (AdFormat format in System.Enum.GetValues(typeof(AdFormat)))
                {
                    string unit = keys.UnitId(format);
                    if (!settings.Uses(format) || string.IsNullOrWhiteSpace(unit)) continue;
                    if (AdMobSampleIds.IsSample(unit)) return format + " unit id is Google's sample id";
                    if (!AdMobId.IsMatch(unit.Trim())) return format + " unit id is not of the form ca-app-pub-XXXXXXXXXXXXXXXX/XXXXXXXXXX";
                }
            }
            return null;
        }
    }
}
