using System;
using System.Collections.Generic;

namespace Nexus.Ads
{
    /// <summary>
    /// Everything a provider needs to start, for one tab (Test or Release) and one platform. The
    /// privacy flags are always present (<see cref="PolicyRules.ChildSettingsForwarded"/>); the test
    /// fields are what <see cref="ProviderGate"/> judges a build by.
    /// </summary>
    public sealed class ProviderConfig
    {
        public string AppKey { get; }
        /// <summary>Ad unit id per format; a format a placement uses must have one.</summary>
        public IReadOnlyDictionary<AdFormat, string> AdUnitIds { get; }
        /// <summary>The mediation's test mode flag (MAX, LevelPlay). Google has no flag; it has sample units.</summary>
        public bool TestMode { get; }
        /// <summary>Advertising ids of the devices allowed to request live SDK ads in a development build.</summary>
        public IReadOnlyList<string> TestDeviceIds { get; }
        /// <summary>The keys and units are the provider's published test data (Google's sample units), safe on any device.</summary>
        public bool UsesPublishedTestData { get; }
        public bool ChildDirected { get; }
        public bool UnderAgeOfConsent { get; }
        /// <summary>Google's scale: "G", "PG", "T" or "MA"; every provider maps it to its own.</summary>
        public string MaxAdContentRating { get; }

        public ProviderConfig(string appKey, IReadOnlyDictionary<AdFormat, string> adUnitIds, bool testMode,
            IReadOnlyList<string> testDeviceIds, bool usesPublishedTestData, bool childDirected, bool underAgeOfConsent,
            string maxAdContentRating)
        {
            if (string.IsNullOrWhiteSpace(maxAdContentRating)) throw new ArgumentException("The content rating is always set.", nameof(maxAdContentRating));
            AppKey = appKey ?? "";
            AdUnitIds = adUnitIds ?? new Dictionary<AdFormat, string>();
            TestMode = testMode;
            TestDeviceIds = testDeviceIds ?? Array.Empty<string>();
            UsesPublishedTestData = usesPublishedTestData;
            ChildDirected = childDirected;
            UnderAgeOfConsent = underAgeOfConsent;
            MaxAdContentRating = maxAdContentRating.Trim().ToUpperInvariant();
        }

        public string AdUnitId(AdFormat format) => AdUnitIds.TryGetValue(format, out var id) ? id : null;

        /// <summary>Case-insensitive, trimmed membership in <see cref="TestDeviceIds"/>.</summary>
        /// <summary>
        /// True for a missing id and for the all-zero id Android gives when the user turned off ad
        /// personalization or deleted the id: it names no device, so it never counts as a test device.
        /// </summary>
        public static bool IsUnavailableAdvertisingId(string advertisingId)
        {
            if (string.IsNullOrWhiteSpace(advertisingId)) return true;
            foreach (char c in advertisingId)
                if (c != '0' && c != '-' && !char.IsWhiteSpace(c)) return false;
            return true;
        }

        public bool IsTestDevice(string advertisingId)
        {
            if (IsUnavailableAdvertisingId(advertisingId)) return false;
            string wanted = advertisingId.Trim();
            for (int i = 0; i < TestDeviceIds.Count; i++)
                if (string.Equals(TestDeviceIds[i]?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>The whole configuration the controller runs on: placements, floors, provider and build.</summary>
    public sealed class AdsConfig
    {
        public IReadOnlyList<AdPlacement> Placements { get; }
        public AdPolicy Policy { get; }
        public ProviderConfig Provider { get; }
        public BuildKind Build { get; }

        public AdsConfig(IReadOnlyList<AdPlacement> placements, AdPolicy policy, ProviderConfig provider, BuildKind build)
        {
            Placements = placements ?? throw new ArgumentNullException(nameof(placements));
            Policy = policy ?? throw new ArgumentNullException(nameof(policy));
            Provider = provider ?? throw new ArgumentNullException(nameof(provider));
            Build = build;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < placements.Count; i++)
                if (!seen.Add(placements[i].Name)) throw new ArgumentException("Placement '" + placements[i].Name + "' is declared twice.", nameof(placements));
        }

        public AdPlacement Find(string name)
        {
            if (name == null) return null;
            for (int i = 0; i < Placements.Count; i++)
                if (string.Equals(Placements[i].Name, name, StringComparison.Ordinal)) return Placements[i];
            return null;
        }

        /// <summary>True when at least one placement uses the format; only those formats are preloaded.</summary>
        public bool Uses(AdFormat format)
        {
            for (int i = 0; i < Placements.Count; i++)
                if (Placements[i].Format == format) return true;
            return false;
        }
    }
}
