using System.Collections.Generic;

namespace Nexus.Ads.Unity
{
    /// <summary>What the host does in one build: the gate's verdict, what runs because of it and, when nothing runs that should, why.</summary>
    public readonly struct HostDecision
    {
        public readonly GateResult Gate;
        public readonly HostMode Mode;
        /// <summary>Why an idle host runs nothing; null when ads run and for the ad-free release, which runs nothing on purpose.</summary>
        public readonly string IdleReason;

        public HostDecision(GateResult gate, HostMode mode, string idleReason = null)
        {
            Gate = gate;
            Mode = mode;
            IdleReason = idleReason;
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

        /// <param name="googleConsentRegistered">Google's form is registered (com.nexus.ads.admob); the editor reads it from the project's packages.</param>
        /// <param name="gameConsentRegistered">The game registered its own flow; the editor cannot see that before a run and passes true.</param>
        public static HostDecision Decide(AdsSettings settings, BuildKind build, ProviderConfig provider, string advertisingId, bool providerRegistered, bool googleConsentRegistered, bool gameConsentRegistered)
        {
            bool adFree = settings.AdFreeRelease && build == BuildKind.Release;
            var gate = adFree
                ? new GateResult(GateDecision.Blocked, null, "ad-free release")
                : settings.SelectedMediation == AdProviderKind.None
                    ? new GateResult(GateDecision.Simulator, null, "no mediation is selected") // no SDK to gate, and no device rule to name
                    : ProviderGate.Evaluate(build, provider, advertisingId);
            var mode = ProviderGate.Host(build, gate, settings.SelectedMediation != AdProviderKind.None, providerRegistered);
            // the consent flow the settings name runs before the SDK, or nothing starts (issues #154, #181)
            string consentMissing = mode == HostMode.Provider ? MissingConsent(settings, googleConsentRegistered, gameConsentRegistered) : null;
            if (consentMissing != null) return new HostDecision(gate, HostMode.Idle, consentMissing);
            return new HostDecision(gate, mode, mode == HostMode.Idle && !adFree ? IdleReason(settings, gate) : null);
        }

        /// <summary>
        /// Why the provider must not start for want of the consent flow the settings name, or null (issues #154, #181). LevelPlay
        /// ships no form, so with <see cref="ConsentSource.GoogleUmp"/> Google's form (registered by com.nexus.ads.admob) and with
        /// <see cref="ConsentSource.GameFlow"/> the game's own flow must be registered with <c>AdsBootstrap.RegisterSharedConsentFlow</c>;
        /// a flow registered for the other source does not count.
        /// </summary>
        public static string MissingConsent(AdsSettings settings, bool googleRegistered, bool gameRegistered)
        {
            if (settings == null || !settings.ConsentNeedsRegisteredFlow) return null;
            var kind = settings.SelectedMediation;
            if (settings.Consent == ConsentSource.GoogleUmp)
                return googleRegistered ? null
                    : "the settings name Google's consent form (UMP) before " + kind + ", and none is registered: install com.google.ads.mobile and com.nexus.ads.admob, which register it (with both installed, Nexus.Ads.AdMob was left out of the player)"
                        + (gameRegistered ? "; the game's registered flow runs only with Consent: GameFlow" : "");
            return gameRegistered ? null
                : "the settings name the game's own consent flow before " + kind + ", and none is registered: call AdsBootstrap.RegisterSharedConsentFlow(ConsentSource.GameFlow, ...) before StartAds()"
                    + (googleRegistered ? "; Google's form from com.nexus.ads.admob runs only with Consent: GoogleUmp" : "");
        }

        /// <summary>
        /// Whether the bootstrap reads the device's advertising id before it starts: only where <see cref="Decide"/> compares
        /// it with the Test tab's list, a development build in test mode with a mediation selected and no sample units. With no
        /// mediation, in the editor and in a release build nothing reads it (issue #133).
        /// </summary>
        public static bool ReadsAdvertisingId(AdsSettings settings, BuildKind build, ProviderConfig provider)
            => settings.SelectedMediation != AdProviderKind.None && ProviderGate.NeedsAdvertisingId(build, provider);

        private static string IdleReason(AdsSettings settings, GateResult gate)
            => gate.Rule != null ? gate.Rule + ": " + gate.Reason
            : settings.SelectedMediation == AdProviderKind.None ? "no mediation is selected"
            : settings.SelectedMediation + " is selected but its provider is not registered (the provider package is not installed, or its assembly was stripped from the player)";

        /// <summary>The builds a developer cares about, for one platform, as the runtime would decide them; a development build splits in two when its device list changes the outcome.</summary>
        /// <param name="providerInstalled">The mediation's SDK and its Nexus provider package are both in the project.</param>
        /// <param name="releaseStatus">The selected mediation's status for this platform (the Release tab's words).</param>
        /// <param name="googleConsentInstalled">com.google.ads.mobile and com.nexus.ads.admob are both in the project, so Google's form is
        /// registered at run time; the game's own flow counts as registered, since it is registered at run time only.</param>
        public static IReadOnlyList<OutlookRow> Rows(AdsSettings settings, AdPlatform platform, bool providerInstalled, ProviderStatusReport releaseStatus, bool googleConsentInstalled)
        {
            var rows = new List<OutlookRow>
            {
                Row("Editor (Play mode)", settings, BuildKind.Editor, platform, "editor", providerInstalled, releaseStatus, googleConsentInstalled),
            };
            var unlisted = Row("Development build", settings, BuildKind.Development, platform, UnlistedId, providerInstalled, releaseStatus, googleConsentInstalled);
            string listedId = FirstListed(settings);
            if (listedId == null) rows.Add(unlisted);
            else
            {
                var listed = Row("Development build, listed device", settings, BuildKind.Development, platform, listedId, providerInstalled, releaseStatus, googleConsentInstalled);
                if (listed.Text == unlisted.Text) rows.Add(new OutlookRow("Development build, any device", listed.Mode, listed.Text, listed.Problem));
                else
                {
                    rows.Add(listed);
                    rows.Add(new OutlookRow("Development build, other devices", unlisted.Mode, unlisted.Text, unlisted.Problem));
                }
            }
            rows.Add(Row("Release build", settings, BuildKind.Release, platform, "release", providerInstalled, releaseStatus, googleConsentInstalled));
            return rows;
        }

        private static OutlookRow Row(string where, AdsSettings settings, BuildKind build, AdPlatform platform, string advertisingId, bool providerInstalled, ProviderStatusReport releaseStatus, bool googleConsentInstalled)
        {
            var provider = settings.ToProviderConfig(build, platform, settings.SelectedMediation);
            var decision = Decide(settings, build, provider, advertisingId, providerInstalled, googleConsentInstalled, gameConsentRegistered: true);
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
                    problem = decision.IdleReason != null;
                    if (!problem) return "no ads, on purpose: Ad-free release is on.";
                    return "no ads run: " + decision.IdleReason + (release ? "; the validator stops this build." : ".");
            }
        }
    }
}
