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
            return NameRefusal(name) == null;
        }

        /// <summary>A null value is refused: Firebase treats a null as "remove this property", which the game does not do here.</summary>
        public static bool IsValidValue(string value)
        {
            return ValueRefusal(value) == null;
        }

        /// <summary>Why Firebase refuses <paramref name="name"/> as a user property name, or null when it accepts it.</summary>
        public static string NameRefusal(string name)
        {
            return AnalyticsEventName.ShapeRefusal(name, MaxNameLength) ?? AnalyticsEventName.PrefixRefusal(name)
                ?? (Array.IndexOf(ReservedNames, name) >= 0 ? "the name is one Google reserves for user properties" : null);
        }

        /// <summary>Why a user property <paramref name="value"/> is refused, or null when it is accepted.</summary>
        public static string ValueRefusal(string value)
        {
            if (value == null)
            {
                return "the value is null, which Firebase reads as removing the property";
            }

            return value.Length > MaxValueLength ? "the value is longer than " + MaxValueLength + " characters" : null;
        }
    }
}