using System;
using System.IO;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;
using Nexus.Firebase.Analytics;
using Nexus.Firebase.Crash;
using Nexus.Firebase.Editor;
using Nexus.Firebase.Mocks;
using Nexus.Firebase.RemoteConfig;

namespace Nexus.Firebase.Tests
{
    /// <summary>
    /// Issue #78: each game chose SDK or mock with its own define and wrote the manifest collection flags with its own post-processor
    /// (Wickling), or bound no Firebase service at all (ChipCrisis).
    /// </summary>
    public sealed class FirebaseBindingsTests
    {
        private sealed class GameConsent : NexusService<IAnalyticsConsent>, IAnalyticsConsent
        {
            public ConsentState State => ConsentState.Required;
            public AnalyticsChoice Choice => AnalyticsChoice.Unknown;
            public event Action Changed { add { } remove { } }
        }

        private const string Manifest =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\">\n" +
            "  <application>\n" +
            "    <meta-data android:name=\"firebase_crashlytics_collection_enabled\" android:value=\"true\" />\n" +
            "  </application>\n" +
            "</manifest>\n";

        [Test]
        public void TheEditorGetsTheMocksAndAConsentThatLetsThemSend()
        {
            using var test = NexusTestHarness.CreateContext(FirebaseBindings.Bind<GameConsent>, autoInitialize: true);

            Assert.IsInstanceOf<MockAnalyticsService>(test.Context.Resolve<IAnalyticsService>());
            Assert.IsInstanceOf<MockRemoteConfigService>(test.Context.Resolve<IRemoteConfigService>());
            Assert.IsInstanceOf<MockCrashReporter>(test.Context.Resolve<ICrashReporter>());
            var consent = test.Context.Resolve<IAnalyticsConsent>();
            Assert.IsInstanceOf<MockAnalyticsConsent>(consent, "no Firebase service reads the game's consent in the Editor");
            Assert.AreEqual(ConsentState.NotRequired, consent.State);
        }

        [Test]
        public void TheManifestGetsEachMissingCollectionFlagOffAndKeepsOneTheGameSet()
        {
            string path = Path.Combine(Path.GetTempPath(), "NexusFirebaseManifest-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(path, Manifest);
            try
            {
                var written = FirebaseCollectionFlags.Write(path, new[] { FirebaseCollectionFlags.AnalyticsFlag, FirebaseCollectionFlags.CrashlyticsFlag });

                Assert.That(written, Is.EqualTo(new[] { FirebaseCollectionFlags.AnalyticsFlag }));
                string text = File.ReadAllText(path);
                StringAssert.Contains("android:name=\"firebase_analytics_collection_enabled\" android:value=\"false\"", text);
                StringAssert.Contains("android:name=\"firebase_crashlytics_collection_enabled\" android:value=\"true\"", text);
                Assert.IsEmpty(FirebaseCollectionFlags.Write(path, new[] { FirebaseCollectionFlags.AnalyticsFlag }), "a second build adds no duplicate");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void AMissingManifestFailsTheBuild()
        {
            Assert.Throws<FileNotFoundException>(() => FirebaseCollectionFlags.Write(Path.Combine(Path.GetTempPath(), "no-such-manifest.xml"), new[] { FirebaseCollectionFlags.AnalyticsFlag }));
        }
    }
}
