using System;

namespace Nexus.Ads
{
    // The seams between the engine-free core and its host. The Unity layer implements every one of
    // them; the host harness implements them by hand. The core never reaches past them.

    /// <summary>A monotonic clock plus the calendar day the per-day caps count against.</summary>
    public interface IAdClock
    {
        /// <summary>Seconds since an arbitrary origin; never goes backwards.</summary>
        double Seconds { get; }
        /// <summary>Whole UTC days since an arbitrary origin; changes at midnight UTC.</summary>
        long UtcDay { get; }
    }

    /// <summary>Whether the app is in the foreground. Shows are blocked and retries pause while it is not.</summary>
    public interface IAppVisibility
    {
        bool Visible { get; }
        event Action<bool> Changed;
    }

    /// <summary>Marshals SDK callbacks onto the thread the game runs on.</summary>
    public interface IMainThread
    {
        bool IsCurrent { get; }
        void Post(Action action);
    }

    /// <summary>Runs an action after a delay; disposing the handle cancels it. Drives retries and the simulator.</summary>
    public interface IAdScheduler
    {
        IDisposable Schedule(double seconds, Action action);
    }

    /// <summary>Durable string storage for the pacing counters that survive a restart (per-day caps).</summary>
    public interface IPacingStore
    {
        /// <summary>The stored value, or null.</summary>
        string Get(string key);
        void Set(string key, string value);
    }

    /// <summary>Receives every ad event; the Unity layer forwards them to analytics and the debug panel.</summary>
    public interface IAdTelemetry
    {
        void Record(in AdEvent e);
    }

    /// <summary>The player bought "remove ads": interstitials stop, rewarded ads stay.</summary>
    public interface IAdFreeEntitlement
    {
        bool AdFree { get; }
        event Action Changed;
    }

    /// <summary>
    /// Remote configuration: a kill switch and pacing values. Values pass through
    /// <see cref="AdPolicy.Tighten(string,double)"/>, so remote can only tighten; it never changes the provider.
    /// </summary>
    public interface IAdsRemoteConfig
    {
        bool KillSwitch { get; }
        bool TryGetDouble(string key, out double value);
        event Action Changed;
    }

    /// <summary>
    /// The consent flow of a region that requires one (Google UMP, the mediation's own form). The provider
    /// usually implements it itself. Until <see cref="Request"/> completes nothing is loaded or shown.
    /// </summary>
    public interface IConsentFlow
    {
        /// <summary>True once the flow answered for this session (consent given, not needed, or refused).</summary>
        bool Completed { get; }
        /// <summary>Ads may be requested now.</summary>
        bool CanRequestAds { get; }
        /// <summary>The law requires a privacy-options entry in the menu.</summary>
        bool PrivacyOptionsRequired { get; }
        /// <summary>Runs the flow; <paramref name="onCompleted"/> runs once, when <see cref="Completed"/> is true.</summary>
        void Request(Action onCompleted);
        void ShowPrivacyOptions(Action onClosed);
        event Action Changed;
    }

    /// <summary>
    /// One mediation SDK behind one contract. Exactly one provider runs per session. Callbacks may arrive on
    /// any thread; the controller marshals them. The provider never pays a reward on anything but the SDK's
    /// reward callback and never calls <see cref="Show"/>'s result twice.
    /// </summary>
    public interface IAdProvider
    {
        /// <summary>Stable lower-case id ("levelplay", "max", "admob", "simulator").</summary>
        string Name { get; }
        /// <summary><paramref name="onReady"/> runs once: true when ads can be requested, false when the SDK could not start.</summary>
        void Initialize(ProviderConfig config, Action<bool> onReady);
        /// <summary>Starts one load; the answer is <see cref="Loaded"/> or <see cref="LoadFailed"/>.</summary>
        void Load(AdFormat format);
        bool IsReady(AdFormat format);
        /// <summary>Shows a loaded ad; <paramref name="onResult"/> runs once when it closed, failed or paid.</summary>
        void Show(AdFormat format, string placement, Action<AdResult> onResult);
        void ShowBanner(string placement, BannerPosition position);
        void HideBanner();
        /// <summary>Opens the mediation's own test tool (test suite, mediation debugger); no-op where there is none.</summary>
        void OpenTestSuite();

        event Action<AdFormat> Loaded;
        /// <summary>Format and the SDK's reason; "no fill" is a reason, not an error.</summary>
        event Action<AdFormat, string> LoadFailed;
        /// <summary>Full-screen content took the screen; the host pauses audio and time until the show result.</summary>
        event Action<AdFormat> Opened;
        event Action<AdRevenue> Revenue;
    }
}
