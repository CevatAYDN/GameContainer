namespace Nexus.Firebase.Analytics
{
    /// <summary>What happens to one analytics event: sent now, kept until the answer is known, or dropped for good.</summary>
    public enum AnalyticsDecision
    {
        Send,
        Hold,
        Discard,
    }
}