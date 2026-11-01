using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;
using Nexus.Firebase.Analytics;
using Nexus.Firebase.Crash;
using Nexus.Firebase.Editor;
using Nexus.Firebase.Mocks;
using Nexus.Firebase.RemoteConfig;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.TestTools;

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

        // Issue #171: a player without an SDK package bound the mock, which kept every event in memory and sent nothing, without a line
        [Test]
        public void APlayerWithoutAnSdkPackage_BindsAServiceThatDoesNothing_WithAnErrorNamingThePackage()
        {
            LogAssert.Expect(LogType.Error, new Regex(@"^\[Firebase\] com\.google\.firebase\.analytics is not installed as a package, so IAnalyticsService sends no event in this player"));
            LogAssert.Expect(LogType.Error, new Regex(@"^\[Firebase\] com\.google\.firebase\.crashlytics is not installed as a package, so ICrashReporter reports nothing in this player"));
            LogAssert.Expect(LogType.Error, new Regex(@"^\[Firebase\] com\.google\.firebase\.remote-config is not installed as a package, so IRemoteConfigService returns the game's fallbacks in this player"));
            using var test = NexusTestHarness.CreateContext(builder => FirebaseBindings.Bind<GameConsent>(builder, player: true, withSdks: false), autoInitialize: true);

            Assert.IsInstanceOf<UnavailableAnalyticsService>(test.Context.Resolve<IAnalyticsService>());
            Assert.IsInstanceOf<UnavailableCrashReporter>(test.Context.Resolve<ICrashReporter>());
            var config = test.Context.Resolve<IRemoteConfigService>();
            Assert.IsInstanceOf<UnavailableRemoteConfigService>(config);
            Assert.AreEqual(3, config.GetInt("lives", 3), "the game's fallback");
            Assert.IsFalse(config.FetchAsync(default).GetAwaiter().GetResult());
            Assert.IsInstanceOf<GameConsent>(test.Context.Resolve<IAnalyticsConsent>(), "a player reads the game's consent, never a mock");
        }

        // Issue #171: Firebase installed from Google's .unitypackage (Assets/Firebase) has no package define, so the build bound mocks and wrote no collection flag
        [Test]
        public void AFirebaseSdkOutsideUpm_FailsTheBuild_WhileAnInstalledPackageGetsItsFlag()
        {
            Assert.That(FirebaseCollectionFlags.FlagsToWrite(true, true, _ => true), Is.EqualTo(new[] { FirebaseCollectionFlags.AnalyticsFlag, FirebaseCollectionFlags.CrashlyticsFlag }));
            Assert.IsEmpty(FirebaseCollectionFlags.FlagsToWrite(false, false, _ => false), "a project without Firebase has nothing to turn off");
            var failure = Assert.Throws<BuildFailedException>(() => FirebaseCollectionFlags.FlagsToWrite(true, false, name => name == "Firebase.Crashlytics"));
            StringAssert.Contains("com.google.firebase.crashlytics", failure.Message);
        }

        [Test]
        public void AMissingManifestFailsTheBuild()
        {
            Assert.Throws<FileNotFoundException>(() => FirebaseCollectionFlags.Write(Path.Combine(Path.GetTempPath(), "no-such-manifest.xml"), new[] { FirebaseCollectionFlags.AnalyticsFlag }));
        }
    }
}
