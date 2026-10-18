using System;

namespace Nexus.Firebase.Analytics
{
    /// <summary>
    /// The names Firebase Analytics accepts for events and parameters: letters, digits and underscores, starting with a letter, at most
    /// 40 characters, and no reserved prefix (firebase_, google_, ga_). Firebase compares names case-sensitively, so case is kept as given.
    /// A name Firebase refuses is dropped without a word, so the name is checked before anything is sent.
    /// </summary>
    public static class AnalyticsEventName
    {
        public const int MaxLength = 40;

        private static readonly string[] ReservedPrefixes = { "firebase_", "google_", "ga_" };

        public static bool IsValid(string name)
        {
            return HasValidShape(name, MaxLength) && !HasReservedPrefix(name);
        }

        /// <summary>A letter first, then letters, digits or underscores, within the length. Shared with user property names.</summary>
        internal static bool HasValidShape(string name, int maxLength)
        {
            if (string.IsNullOrEmpty(name) || name.Length > maxLength || !IsLetter(name[0]))
            {
                return false;
            }

            for (int i = 1; i < name.Length; i++)
            {
                char character = name[i];
                if (!IsLetter(character) && !IsDigit(character) && character != '_')
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool HasReservedPrefix(string name)
        {
            foreach (string prefix in ReservedPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsLetter(char character) => (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z');

        private static bool IsDigit(char character) => character >= '0' && character <= '9';
    }
}