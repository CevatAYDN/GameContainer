namespace Nexus.Firebase.Analytics
{
    /// <summary>
    /// What the player chose for analytics. The consent status does not carry this: Obtained means the consent form was answered, not
    /// which purposes were accepted. The choice is read from the consent record the form stores, and Unknown until it is.
    /// </summary>
    public enum AnalyticsChoice
    {
        Unknown,
        Granted,
        Denied,
    }
}