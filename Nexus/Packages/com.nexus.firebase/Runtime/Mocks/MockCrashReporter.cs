using System;
using System.Collections.Generic;
using Nexus.Core;
using Nexus.Firebase.Crash;

namespace Nexus.Firebase.Mocks
{
    /// <summary>Records the non-fatals the game reports, for the editor, development builds and tests.</summary>
    public sealed class MockCrashReporter : NexusService<ICrashReporter>, ICrashReporter
    {
        private readonly List<Exception> _recorded = new List<Exception>();

        public IReadOnlyList<Exception> Recorded => _recorded;

        public void RecordNonFatal(Exception exception)
        {
            if (exception != null)
            {
                _recorded.Add(exception);
            }
        }
    }
}