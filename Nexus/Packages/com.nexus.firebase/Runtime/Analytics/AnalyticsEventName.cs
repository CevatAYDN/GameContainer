namespace Nexus.Firebase.Analytics
{
    /// <summary>
    /// The event and parameter names Firebase Analytics accepts: snake_case, starting with a letter, at most 40 characters.
    /// A name it refuses is dropped by the SDK without a word, so the name is checked before anything is sent.
    /// </summary>
    public static class AnalyticsEventName
    {
        public const int MaxLength = 40;

        public static bool IsValid(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxLength || !IsLowercaseLetter(name[0]))
            {
                return false;
            }

            for (int i = 1; i < name.Length; i++)
            {
                char character = name[i];
                if (!IsLowercaseLetter(character) && !IsDigit(character) && character != '_')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsLowercaseLetter(char character) => character >= 'a' && character <= 'z';

        private static bool IsDigit(char character) => character >= '0' && character <= '9';
    }
}
