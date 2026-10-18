using System;

namespace Nexus.Firebase.Crash
{
    /// <summary>Records an error the game survived, so it shows up in crash reporting as a non-fatal.</summary>
    public interface ICrashReporter
    {
        void RecordNonFatal(Exception exception);
    }
}