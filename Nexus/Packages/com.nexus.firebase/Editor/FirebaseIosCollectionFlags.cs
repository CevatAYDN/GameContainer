using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Editor.Ios;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

namespace Nexus.Firebase.Editor
{
    /// <summary>
    /// The iOS counterpart of <see cref="FirebaseCollectionFlags"/> (issue #434). Firebase collects from launch, before the consent
    /// answer, so every iOS build writes Firebase's own "collection off" keys into the exported Xcode project's <c>Info.plist</c> for each
    /// SDK package the project has: <c>FIREBASE_ANALYTICS_COLLECTION_ENABLED</c> and <c>FirebaseCrashlyticsCollectionEnabled</c>, both
    /// boolean false. <c>FirebaseAnalyticsService</c> and <c>FirebaseCrashReporter</c> turn collection on at run time once consent allows
    /// it, through the SDK on every platform. A key the plist already carries keeps the game's value. The same rule as Android applies to
    /// an SDK that is in the project but not as its UPM package: the build fails (issue #171). Not compiled or run when it was written,
    /// see the changelog.
    /// </summary>
    public static class FirebaseIosCollectionFlags
    {
        internal const string AnalyticsKey = "FIREBASE_ANALYTICS_COLLECTION_ENABLED";
        internal const string CrashlyticsKey = "FirebaseCrashlyticsCollectionEnabled";

#if NEXUS_FIREBASE_ANALYTICS
        private const bool AnalyticsPackage = true;
#else
        private const bool AnalyticsPackage = false;
#endif
#if NEXUS_FIREBASE_CRASHLYTICS
        private const bool CrashlyticsPackage = true;
#else
        private const bool CrashlyticsPackage = false;
#endif

        // Same order as the Android step, after the games' own post-processes (999), so a key the game set is seen and kept.
        [PostProcessBuild(10000)]
        public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
        {
            if (target != BuildTarget.iOS) return;
            var keys = KeysToWrite(AnalyticsPackage, CrashlyticsPackage, FirebaseCollectionFlags.IsLoaded);
            if (keys.Count == 0) return;
            Write(Path.Combine(pathToBuiltProject, "Info.plist"), keys);
        }

        /// <summary>The key of each SDK the project has as a UPM package; fails the build for an SDK outside UPM, as the Android step does.</summary>
        internal static List<string> KeysToWrite(bool analyticsPackage, bool crashlyticsPackage, Func<string, bool> assemblyLoaded)
        {
            var keys = new List<string>();
            foreach (string flag in FirebaseCollectionFlags.FlagsToWrite(analyticsPackage, crashlyticsPackage, assemblyLoaded))
            {
                keys.Add(flag == FirebaseCollectionFlags.AnalyticsFlag ? AnalyticsKey : CrashlyticsKey);
            }
            return keys;
        }

        /// <summary>Adds each missing key as boolean <c>false</c> and returns the ones it added; a missing Info.plist fails the build.</summary>
        internal static List<string> Write(string plistPath, IEnumerable<string> keys)
        {
            if (!File.Exists(plistPath))
            {
                throw new FileNotFoundException("[Nexus Firebase] The exported Info.plist is missing, so Firebase would collect before consent.", plistPath);
            }

            string xml = File.ReadAllText(plistPath);
            var written = new List<string>();
            foreach (string key in keys)
            {
                if (PlistXml.AddBoolIfAbsent(ref xml, key, false)) written.Add(key);
            }

            if (written.Count > 0) File.WriteAllText(plistPath, xml);
            Debug.Log("[Nexus Firebase] Collection off in Info.plist until consent: " + (written.Count > 0 ? string.Join(", ", written) : "already set by the game"));
            return written;
        }
    }
}
