using System;
using Nexus.Firebase.Analytics;

namespace Nexus.Firebase.Mocks
{
    /// <summary>A consent answer and choice set by hand, for the editor, development builds and tests.</summary>
    public sealed class MockAnalyticsConsent : IAnalyticsConsent
    {
        public ConsentState State { get; private set; }

        public AnalyticsChoice Choice { get; private set; }

        public event Action Changed;

        /// <summary>Sets the answer and the choice, and raises <see cref="Changed"/> when either differs from before.</summary>
        public void Set(ConsentState state, AnalyticsChoice choice)
        {
            if (State == state && Choice == choice)
            {
                return;
            }

            State = state;
            Choice = choice;
            Changed?.Invoke();
        }
    }
}