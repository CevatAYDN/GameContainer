using UnityEngine;

namespace Nexus.Ads.Unity
{
    /// <summary>
    /// Reads the keys Google's UMP stores its IAB GPP answer under (issue #444): SharedPreferences on Android, NSUserDefaults on iOS.
    /// </summary>
    public static class UsPrivacyStore
    {
        /// <summary>The stored string for <paramref name="key"/>, or null (also in the editor and on any read failure).</summary>
#if UNITY_ANDROID && !UNITY_EDITOR
        private static bool _warned;
#endif

        public static string Read(string key)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                // UMP and the GPP spec use the app's default SharedPreferences, not Unity's PlayerPrefs file
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var manager = new AndroidJavaClass("android.preference.PreferenceManager"))
                using (var prefs = manager.CallStatic<AndroidJavaObject>("getDefaultSharedPreferences", activity))
                    return prefs.Call<string>("getString", key, (string)null);
            }
            catch (System.Exception e)
            {
                if (_warned) return null;
                _warned = true;
                Debug.LogWarning("[Nexus Ads] consent: could not read " + key + " from SharedPreferences, no do_not_sell is forwarded: " + e.Message);
                return null;
            }
#elif UNITY_IOS && !UNITY_EDITOR
            // shortcut: Unity's iOS PlayerPrefs stores the key unprefixed in NSUserDefaults.standardUserDefaults, which is where UMP writes; not
            // run on a device in #444. A native plugin would replace it if a device shows the key unread.
            return PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null;
#else
            return null;
#endif
        }
    }
}
