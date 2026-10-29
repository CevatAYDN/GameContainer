using System;

namespace Nexus.Firebase.Analytics
{
    /// <summary>
    /// The names Firebase Analytics accepts for events and parameters: letters, digits and underscores, starting with a letter, at most
    /// 40 characters, and no reserved prefix (firebase_, google_, ga_); an event name is also none of the names Google reserves for apps.
    /// Firebase compares names case-sensitively, so case is kept as given.
    /// A name Firebase refuses is dropped without a word, so the name is checked before anything is sent.
    /// </summary>
    public static class AnalyticsEventName
    {
        public const int MaxLength = 40;

        private static readonly string[] ReservedPrefixes = { "firebase_", "google_", "ga_" };

        // Both of Google's lists, which differ: https://support.google.com/analytics/answer/13316687 (GA4, Mobile; "isn't exhaustive") and
        // https://firebase.google.com/docs/reference/android/com/google/firebase/analytics/FirebaseAnalytics.Event (the SDK drops them).
        // GA4's firebase_* names are left to the prefix rule. Matched exactly, as Firebase compares event names case-sensitively.
        private static readonly string[] ReservedNames =
        {
            "ad_activeview", "ad_click", "ad_exposure", "ad_query", "ad_reward", "adunit_exposure", "app_background", "app_clear_data",
            "app_exception", "app_install", "app_remove", "app_store_refund", "app_store_subscription_cancel", "app_store_subscription_convert",
            "app_store_subscription_renew", "app_update", "app_upgrade", "dynamic_link_app_open", "dynamic_link_app_update",
            "dynamic_link_first_open", "error", "first_open", "first_visit", "notification_dismiss", "notification_foreground",
            "notification_open", "notification_receive", "notification_send", "os_update", "session_start", "session_start_with_rollout",
            "user_engagement",
        };

        public static bool IsValid(string name)
        {
            return HasValidShape(name, MaxLength) && !HasReservedPrefix(name) && Array.IndexOf(ReservedNames, name) < 0;
        }

        /// <summary>The shape and prefix rules without the reserved event names: Google reserves those for events, so a parameter may be called <c>error</c>.</summary>
        public static bool IsValidParameterName(string name)
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