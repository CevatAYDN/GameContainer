using System;

namespace Nexus.Firebase.Crash
{
    /// <summary>Records an error the game survived as a non-fatal, and the context every later report carries.</summary>
    public interface ICrashReporter
    {
        void RecordNonFatal(Exception exception);
        /// <summary>A key and value every later crash and non-fatal report carries (the level, the build flavour, the last screen);
        /// a key keeps its last value. Set before the reporter is ready, it is held and applied once it is.</summary>
        void SetCustomKey(string key, string value);
        /// <summary>A line of the log the next crash or non-fatal report carries. Before the reporter is ready the newest
        /// <see cref="CrashContext.HeldLines"/> lines are held.</summary>
        void Log(string message);
    }
}