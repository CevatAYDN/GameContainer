using System;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>What a rewarded ad attempt ended with.</summary>
    public enum AdOutcome
    {
        /// <summary>The network granted the reward.</summary>
        Rewarded,
        /// <summary>The player closed the ad before the reward was granted.</summary>
        Closed,
        /// <summary>No ad was loaded for the placement; nothing was shown.</summary>
        NotReady,
        /// <summary>The network failed to show the ad, or no adapter can show ads in this build.</summary>
        Failed,
    }

    /// <summary>
    /// Optional adapter capability: rewarded results as an <see cref="AdOutcome"/> instead of a bool.
    /// <see cref="AdService"/> maps bool-only adapters to Rewarded/Closed.
    /// </summary>
    [Preserve]
    public interface IAdOutcomeAdapter
    {
        void RequestReward(string placement, Action<AdOutcome> onOutcome);
    }

    /// <summary>Optional adapter capability: raised whenever ad readiness may have changed (loaded, shown, consent changed).</summary>
    [Preserve]
    public interface IAdReadinessAdapter
    {
        event Action ReadyChanged;
    }

    /// <summary>
    /// Optional adapter capability: the consent form's privacy options (EEA/UK law requires that a
    /// player can reopen them from the menu). The adapter owns the consent flow itself.
    /// </summary>
    [Preserve]
    public interface IAdConsentAdapter
    {
        bool PrivacyOptionsRequired { get; }
        void ShowPrivacyOptions(Action onClosed);
    }
}
