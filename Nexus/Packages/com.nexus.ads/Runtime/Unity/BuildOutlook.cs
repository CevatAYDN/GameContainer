using System.Collections.Generic;

namespace Nexus.Ads.Unity
{
    /// <summary>What the host does in one build: the gate's verdict and what runs because of it.</summary>
    public readonly struct HostDecision
    {
        public readonly GateResult Gate;
        public readonly HostMode Mode;

        public HostDecision(GateResult gate, HostMode mode)
        {
            Gate = gate;
            Mode = mode;
        }
    }

    /// <summary>One kind of build and, in plain words, what the ad system does in it; <see cref="Problem"/> is true when no real ad will show.</summary>
    public readonly struct OutlookRow
    {
        public readonly string Where;
        public readonly HostMode Mode;
        public readonly string Text;
        public readonly bool Problem;

        public OutlookRow(string where, HostMode mode, string text, bool problem)
        {
            Where = where;
            Mode = mode;
            Text = text;
            Problem = problem;
        }
    }

    /// <summary>
    /// The only place that decides what a build runs and says it in words. <see cref="AdsBootstrap"/> runs
    /// <see cref="Decide"/>; Project Settings, the setup wizard and the on-device debug panel show
    /// <see cref="Say"/> of the same decision, so no surface can promise an ad the runtime will not run.
    /// </summary>
    public static class BuildOutlook
    {
        private const string UnlistedId = "unlisted-device";

        public static HostDecision Decide(AdsSettings settings, BuildKind build, ProviderConfig provider, string advertisingId, bool providerRegistered)
        {
            var gate = settings.AdFreeRelease && build == BuildKind.Release
                ? new GateResult(GateDecision.Blocked, null, "ad-free release")
                : ProviderGate.Evaluate(build, provider, advertisingId);
            return new HostDecision(gate, ProviderGate.Host(build, gate, settings.SelectedMediation != AdProviderKind.None, providerRegistered));
        }

        /// <summary>Why an idle host runs nothing (not the ad-free release, which is on purpose).</summary>
        public static string IdleReason(AdsSettings settings, GateResult gate)
            => gate.Rule != null ? gate.Rule + ": " + gate.Reason
            : settings.SelectedMediation == AdProviderKind.None ? "no mediation is selected"
            : settings.SelectedMediation + " is selected but its provider is not registered (the provider package is not installed, or its assembly was stripped from the player)";

        /// <summary>The builds a developer cares about, for one platform, as the runtime would decide them; a development build splits in two when its device list changes the outcome.</summary>
        /// <param name="providerInstalled">The mediation's SDK and its Nexus provider package are both in the project.</param>
        /// <param name="releaseStatus">The selected mediation's status for this platform (the Release tab's words).</param>
        public static IReadOnlyList<OutlookRow> Rows(AdsSettings settings, AdPlatform platform, bool providerInstalled, ProviderStatusReport releaseStatus)
        {
            var rows = new List<OutlookRow>
            {
                Row("Editor (Play mode)", settings, BuildKind.Editor, platform, "editor", providerInstalled, releaseStatus),
            };
            var unlisted = Row("Development build", settings, BuildKind.Development, platform, UnlistedId, providerInstalled, releaseStatus);
            string listedId = FirstListed(settings);
            if (listedId == null) rows.Add(unlisted);
            else
            {
                var listed = Row("Development build, listed device", settings, BuildKind.Development, platform, listedId, providerInstalled, releaseStatus);
                if (listed.Text == unlisted.Text) rows.Add(new OutlookRow("Development build, any device", listed.Mode, listed.Text, listed.Problem));
                else
                {
                    rows.Add(listed);
                    rows.Add(new OutlookRow("Development build, other devices", unlisted.Mode, unlisted.Text, unlisted.Problem));
                }
            }
            rows.Add(Row("Release build", settings, BuildKind.Release, platform, "release", providerInstalled, releaseStatus));
            return rows;
        }

        private static OutlookRow Row(string where, AdsSettings settings, BuildKind build, AdPlatform platform, string advertisingId, bool providerInstalled, ProviderStatusReport releaseStatus)
        {
            var provider = settings.ToProviderConfig(build, platform, settings.SelectedMediation);
            var decision = Decide(settings, build, provider, advertisingId, providerInstalled);
            bool problem;
            string text = Say(settings, build, platform, decision, releaseStatus, out problem);
            return new OutlookRow(where, decision.Mode, text, problem);
        }

        private static string FirstListed(AdsSettings settings)
        {
            foreach (var id in settings.TestDeviceIds) if (!string.IsNullOrWhiteSpace(id)) return id.Trim();
            return null;
        }

        /// <param name="releaseStatus">Only read for a release build; null elsewhere (the on-device panel never shows one).</param>
        public static string Say(AdsSettings settings, BuildKind build, AdPlatform platform, HostDecision decision, ProviderStatusReport? releaseStatus)
            => Say(settings, build, platform, decision, releaseStatus, out _);

        private static string Say(AdsSettings settings, BuildKind build, AdPlatform platform, HostDecision decision, ProviderStatusReport? releaseStatus, out bool problem)
        {
            var kind = settings.SelectedMediation;
            bool release = build == BuildKind.Release;
            problem = false;
            switch (decision.Mode)
            {
                case HostMode.Simulator:
                    if (build == BuildKind.Editor) return "labelled simulated ads; they prove nothing about a store build.";
                    return kind == AdProviderKind.None ? "labelled simulated ads: no mediation is selected."
                        : "labelled simulated ads, no real request: this device is not in the Test tab's device list.";

                case HostMode.Provider:
                    if (release)
                    {
                        if (releaseStatus.HasValue && releaseStatus.Value.Status == ProviderStatus.Active) return kind + " live ads: real and paid; never tap them.";
                        problem = true;
                        return kind + " would start, but it is " + (releaseStatus.HasValue ? releaseStatus.Value.ToString() : "not ready") + "; the validator stops this build.";
                    }
                    var missing = settings.MissingFields(kind, platform, settings.Test);
                    if (missing.Count > 0)
                    {
                        problem = true;
                        return kind + " starts, but the Test tab misses " + string.Join(", ", missing) + ": no ad loads.";
                    }
                    return kind == AdProviderKind.AdMob ? "AdMob test ads: Google's sample units, on every device."
                        : kind + " starts with the account keys on this listed device; test ads only if the device is also in the " + kind + " dashboard's test device list.";

                default:
                    problem = !(settings.AdFreeRelease && release);
                    if (!problem) return "no ads, on purpose: Ad-free release is on.";
                    return "no ads run: " + IdleReason(settings, decision.Gate) + (release ? "; the validator stops this build." : ".");
            }
        }
    }
}
