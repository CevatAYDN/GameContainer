namespace Nexus.Firebase.Analytics
{
    /// <summary>
    /// The decision for an analytics event, given the consent answer and the player's choice. Where consent is not required the event is
    /// sent. Otherwise it waits until the answer is known, is sent when analytics were granted, and is dropped when they were refused.
    /// </summary>
    public static class AnalyticsCollectionPolicy
    {
        public static AnalyticsDecision Decide(ConsentState state, AnalyticsChoice choice)
        {
            if (state == ConsentState.NotRequired)
            {
                return AnalyticsDecision.Send;
            }

            if (state != ConsentState.Obtained)
            {
                return AnalyticsDecision.Hold;
            }

            switch (choice)
            {
                case AnalyticsChoice.Granted:
                    return AnalyticsDecision.Send;
                case AnalyticsChoice.Denied:
                    return AnalyticsDecision.Discard;
                default:
                    return AnalyticsDecision.Hold;
            }
        }
    }
}