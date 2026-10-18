using System;

namespace Nexus.Firebase.Crash
{
    /// <summary>A logged error (Debug.LogError) turned into an exception, so crash reporting can group it. The log's own stack trace is kept in the message.</summary>
    public sealed class LoggedErrorException : Exception
    {
        public LoggedErrorException(string condition, string stackTrace)
            : base(string.IsNullOrEmpty(stackTrace) ? condition : condition + "\n" + stackTrace)
        {
        }
    }
}