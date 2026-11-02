using System;
using System.IO;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// The platform share sheet (#134). On Android an <c>ACTION_SEND</c> intent goes through the chooser; a file
    /// travels as a <c>content://</c> URI from the package's <c>NexusShare.androidlib</c> provider, because a
    /// <c>file://</c> URI throws <c>FileUriExposedException</c> from Android 7 on.
    /// </summary>
    [Preserve]
    public class ShareService : NexusService<IShareService>, IShareService
    {
        // The androidlib's manifest declares the provider as ${applicationId}.nexusfileprovider; the package name read
        // at run time is the value the manifest merger put there.
        internal const string AuthoritySuffix = ".nexusfileprovider";

        public bool ShareText(string text, string title)
        {
            if (string.IsNullOrEmpty(text)) throw new ArgumentException("There is no text to share.", nameof(text));
            if (title == null) throw new ArgumentNullException(nameof(title));
#if UNITY_ANDROID && !UNITY_EDITOR
            return StartChooser("text/plain", title, (activity, intent) =>
            {
                using (intent.Call<AndroidJavaObject>("putExtra", "android.intent.extra.TEXT", text)) { }
            });
#else
            return NotShared($"ShareText ({text.Length} characters, '{title}')");
#endif
        }

        public bool ShareFile(string path, string mimeType, string title)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("There is no file to share.", nameof(path));
            if (string.IsNullOrEmpty(mimeType)) throw new ArgumentException("A shared file needs its MIME type.", nameof(mimeType));
            if (title == null) throw new ArgumentNullException(nameof(title));
            if (!File.Exists(path))
            {
                NexusLog.WarnAlways($"[ShareService] ShareFile: no file at {path}; nothing was shared.");
                return false;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            return StartChooser(mimeType, title, (activity, intent) =>
            {
                string authority = activity.Call<string>("getPackageName") + AuthoritySuffix;
                using (var file = new AndroidJavaObject("java.io.File", path))
                using (var provider = new AndroidJavaClass("androidx.core.content.FileProvider"))
                using (var uri = provider.CallStatic<AndroidJavaObject>("getUriForFile", activity, authority, file))
                using (var clipData = new AndroidJavaClass("android.content.ClipData"))
                using (var clip = clipData.CallStatic<AndroidJavaObject>("newRawUri", "", uri))
                {
                    using (intent.Call<AndroidJavaObject>("putExtra", "android.intent.extra.STREAM", uri)) { }
                    using (intent.Call<AndroidJavaObject>("addFlags", 1)) { } // FLAG_GRANT_READ_URI_PERMISSION
                    // createChooser passes the grant on to the share sheet, which reads the file for its preview, only
                    // through ClipData; without it the sheet logs "Permission Denial: opening provider" (#274)
                    intent.Call("setClipData", clip);
                }
            });
#else
            return NotShared($"ShareFile ({path}, {mimeType}, '{title}')");
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static bool StartChooser(string mimeType, string title, Action<AndroidJavaObject, AndroidJavaObject> fill)
        {
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var intentClass = new AndroidJavaClass("android.content.Intent"))
                using (var intent = new AndroidJavaObject("android.content.Intent", "android.intent.action.SEND"))
                {
                    using (intent.Call<AndroidJavaObject>("setType", mimeType)) { }
                    fill(activity, intent);
                    using (var chooser = intentClass.CallStatic<AndroidJavaObject>("createChooser", intent, title))
                        activity.Call("startActivity", chooser);
                }
                return true;
            }
            catch (Exception ex)
            {
                // "Failed to find configured root" (a file outside the provider's folders) arrives here too
                NexusLog.WarnAlways($"[ShareService] The share sheet did not open: {ex.Message}");
                return false;
            }
        }
#else
        private static bool NotShared(string call)
        {
#if UNITY_EDITOR
            NexusRuntime.Logger?.Log($"[ShareService] {call}: editor preview, no share sheet.");
#else
            NexusLog.WarnAlways($"[ShareService] {call}: sharing is implemented for Android only (#134); nothing was shared.");
#endif
            return false;
        }
#endif
    }
}
