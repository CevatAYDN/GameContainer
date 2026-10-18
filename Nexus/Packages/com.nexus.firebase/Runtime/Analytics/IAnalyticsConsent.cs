using System;

namespace Nexus.Firebase.Analytics
{
    /// <summary>
    /// Where the Firebase services read the consent answer and the player's choice from. The game implements it over its consent flow (for
    /// example the User Messaging Platform status and the stored purpose consent); <see cref="Changed"/> is raised when either value changes.
    /// </summary>
    public interface IAnalyticsConsent
    {
        ConsentState State { get; }

        AnalyticsChoice Choice { get; }

        event Action Changed;
    }
}