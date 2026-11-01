using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Xml;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEngine;

[assembly: InternalsVisibleTo("com.nexus.firebase.tests")]

namespace Nexus.Firebase.Editor
{
    /// <summary>
    /// Firebase collects from the moment the process starts, before game code can read the consent answer, so every Android build
    /// writes Firebase's own "collection off" flag into the launcher manifest for each SDK package the project has (issue #78):
    /// <c>firebase_analytics_collection_enabled</c> and <c>firebase_crashlytics_collection_enabled</c> (Crashlytics's opt-in
    /// reporting). <c>FirebaseAnalyticsService</c> and <c>FirebaseCrashReporter</c> turn collection on once the consent decision
    /// allows it. A flag the manifest already carries is left as the game set it.
    /// </summary>
    public sealed class FirebaseCollectionFlags : IPostGenerateGradleAndroidProject
    {
        internal const string AnalyticsFlag = "firebase_analytics_collection_enabled";
        internal const string CrashlyticsFlag = "firebase_crashlytics_collection_enabled";
        private const string AndroidNs = "http://schemas.android.com/apk/res/android";

        public int callbackOrder => 10000;

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

        public void OnPostGenerateGradleAndroidProject(string unityLibraryPath)
        {
            var flags = FlagsToWrite(AnalyticsPackage, CrashlyticsPackage, IsLoaded);
            if (flags.Count == 0)
            {
                return;
            }

            Write(Path.GetFullPath(Path.Combine(unityLibraryPath, "../launcher/src/main/AndroidManifest.xml")), flags);
        }

        /// <summary>
        /// The flag of each SDK the project has as a UPM package. An SDK it has otherwise (Google's .unitypackage in Assets/Firebase)
        /// fails the build: without the package's define this package binds no Firebase service for it and writes no flag, so
        /// Firebase would collect before consent (issue #171).
        /// </summary>
        internal static List<string> FlagsToWrite(bool analyticsPackage, bool crashlyticsPackage, Func<string, bool> assemblyLoaded)
        {
            var flags = new List<string>();
            Add(flags, analyticsPackage, assemblyLoaded("Firebase.Analytics"), AnalyticsFlag, "com.google.firebase.analytics");
            Add(flags, crashlyticsPackage, assemblyLoaded("Firebase.Crashlytics"), CrashlyticsFlag, "com.google.firebase.crashlytics");
            return flags;
        }

        private static void Add(List<string> flags, bool package, bool loaded, string flag, string packageName)
        {
            if (package) flags.Add(flag);
            else if (loaded) throw new BuildFailedException("[Nexus Firebase] Firebase is in the project but not as the UPM package " + packageName + ", so com.nexus.firebase binds no Firebase service for it and cannot turn its collection off before consent. Install Firebase as UPM packages (com.nexus.firebase README, Install the SDK).");
        }

        private static bool IsLoaded(string assemblyName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name == assemblyName) return true;
            }
            return false;
        }

        /// <summary>Adds each missing flag as <c>false</c> and returns the ones it added; a missing manifest fails the build.</summary>
        internal static List<string> Write(string manifestPath, IEnumerable<string> flags)
        {
            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException("[Nexus Firebase] The generated launcher manifest is missing, so Firebase would collect before consent.", manifestPath);
            }

            var manifest = new XmlDocument { PreserveWhitespace = true };
            manifest.Load(manifestPath);
            var application = (XmlElement)manifest.DocumentElement.GetElementsByTagName("application")[0];
            var written = new List<string>();
            foreach (string flag in flags)
            {
                if (HasMetaData(application, flag))
                {
                    continue;
                }

                XmlElement meta = manifest.CreateElement("meta-data");
                meta.SetAttribute("name", AndroidNs, flag);
                meta.SetAttribute("value", AndroidNs, "false");
                application.AppendChild(meta);
                written.Add(flag);
            }

            manifest.Save(manifestPath);
            Debug.Log("[Nexus Firebase] Collection off in the manifest until consent: " + (written.Count > 0 ? string.Join(", ", written) : "already set by the game"));
            return written;
        }

        private static bool HasMetaData(XmlElement application, string name)
        {
            foreach (XmlNode node in application.ChildNodes)
            {
                if (node is XmlElement element && element.Name == "meta-data" && element.GetAttribute("name", AndroidNs) == name)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
