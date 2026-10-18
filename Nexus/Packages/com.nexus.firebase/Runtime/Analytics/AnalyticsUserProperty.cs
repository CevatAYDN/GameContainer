using System;

namespace Nexus.Firebase.Analytics
{
    /// <summary>
    /// The limits of a user property: a name of at most 24 characters with Firebase's name shape and no reserved prefix or name, and a
    /// value of at most 36 characters. A property over a limit is dropped, not truncated.
    /// </summary>
    public static class AnalyticsUserProperty
    {
        public const int MaxNameLength = 24;

        public const int MaxValueLength = 36;

        private static readonly string[] ReservedNames = { "first_open_time", "last_deep_link_referrer", "user_id" };

        public static bool IsValidName(string name)
        {
            return AnalyticsEventName.HasValidShape(name, MaxNameLength)
                && !AnalyticsEventName.HasReservedPrefix(name)
                && Array.IndexOf(ReservedNames, name) < 0;
        }

        /// <summary>A null value is refused: Firebase treats a null as "remove this property", which the game does not do here.</summary>
        public static bool IsValidValue(string value)
        {
            return value != null && value.Length <= MaxValueLength;
        }
    }
}