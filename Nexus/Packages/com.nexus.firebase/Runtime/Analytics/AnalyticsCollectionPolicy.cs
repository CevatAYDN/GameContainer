namespace Nexus.Firebase.Analytics
{
    /// <summary>
    /// Whether analytics and crash collection may run. Outside the EEA and the UK they are on once the consent update has answered
    /// NotRequired; in the EEA and the UK they stay off until the player answers the consent form. A cached answer from an earlier
    /// session is readable at launch, so a player who already answered gets analytics from launch (docs/ANALYTICS.md, Consent).
    /// </summary>
    public static class AnalyticsCollectionPolicy
    {
        public static bool IsCollectionAllowed(ConsentState state)
        {
            return state == ConsentState.NotRequired || state == ConsentState.Obtained;
        }
    }
}
