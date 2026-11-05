using System;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;
using UnityEngine;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class PracticalServicesTests
    {
        [Test]
        public void EncryptedStorageService_CloudExportAndImport_RoundTripsData()
        {
            using var storage = new EncryptedStorageService("Test_Cloud_Salt");
            storage.SetString("User_Cloud_Data", "Level_50_Player_Save");
            storage.Save();

            string exportedBase64 = storage.ExportEncryptedSaveData("User_Cloud_Data");
            Assert.IsNotNull(exportedBase64);
            Assert.Greater(exportedBase64.Length, 0);

            // v3 authenticates the key name: restore the original key, rather than
            // expecting a payload copied to another key to bypass its HMAC binding.
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"Import of key 'User_Cloud_Imported' refused: the data fails its integrity check")); // issue #166
            Assert.IsFalse(storage.ImportEncryptedSaveData("User_Cloud_Imported", exportedBase64));
            Assert.IsFalse(storage.HasKey("User_Cloud_Imported"));
            storage.DeleteKey("User_Cloud_Data");
            bool importSuccess = storage.ImportEncryptedSaveData("User_Cloud_Data", exportedBase64);
            Assert.IsTrue(importSuccess);

            string importedValue = storage.GetString("User_Cloud_Data", null);
            Assert.AreEqual("Level_50_Player_Save", importedValue);

            storage.DeleteKey("User_Cloud_Data");
            storage.DeleteKey("User_Cloud_Imported");
        }

        [Test]
        public void EncryptedStorageService_InvalidCloudImport_PreservesExistingValue()
        {
            using var storage = new EncryptedStorageService("Test_Cloud_Validation_Salt");
            storage.SetString("User_Cloud_Data", "valid-local-save");
            storage.Save();

            string exportedBase64 = storage.ExportEncryptedSaveData("User_Cloud_Data");
            byte[] tampered = System.Convert.FromBase64String(exportedBase64);
            tampered[tampered.Length - 1] ^= 0xFF;

            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"Import of key 'User_Cloud_Data' refused: the data fails its integrity check")); // issue #166
            Assert.IsFalse(storage.ImportEncryptedSaveData("User_Cloud_Data", System.Convert.ToBase64String(tampered)));
            Assert.AreEqual("valid-local-save", storage.GetString("User_Cloud_Data", null));

            storage.DeleteKey("User_Cloud_Data");
        }

        [Test]
        public void OfflineTimeCalculator_GrantsNothingForAClockSetBack_AndClampsAClockPushedForwardToTheMonotonicTick()
        {
            using var storage = new EncryptedStorageService("Test_Offline_Salt");
            OfflineTimeCalculator.RecordQuitTimestamp(storage);
            long now = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // the clock was set back: the quit lies 1000 s in the future
            storage.SetLong("NT_LastQuitTimestamp", now + 1000);
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex(@"^\[OfflineTimeCalculator\] Time manipulation detected! Offline progress set to 0\.$"));
            Assert.AreEqual(0, OfflineTimeCalculator.CalculateOfflineSeconds(storage, 3600));

            // A8: the clock was pushed forward: the wall clock says 1000 s, the monotonic tick recorded at the quit says ~0
            storage.SetLong("NT_LastQuitTimestamp", now - 1000);
            Assert.LessOrEqual(OfflineTimeCalculator.CalculateOfflineSeconds(storage, 3600), 1,
                "a forward clock granted the wall clock's 1000 s");
            UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();

            storage.DeleteKey("NT_LastQuitTimestamp");
            storage.DeleteKey("NT_LastQuitMonotonicMs");
        }

        // Issue #205: every timestamp key shared one monotonic tick, so recording slot B clamped slot A's offline time
        // to the time since B's quit.
        [Test]
        public void OfflineTimeCalculator_RecordingOneKey_DoesNotClampTheOfflineTimeOfAnother()
        {
            var prefs = new InMemoryPlayerPrefsService();
            OfflineTimeCalculator.RecordQuitTimestamp(prefs, "slotA");
            // slot A quit an hour ago: move every value it stored back by an hour (seconds and milliseconds)
            foreach (var key in new System.Collections.Generic.List<string>(prefs.Keys))
                prefs.SetLong(key, prefs.GetLong(key) - (key == "slotA" ? 3600L : 3600L * 1000L));

            OfflineTimeCalculator.RecordQuitTimestamp(prefs, "slotB");

            Assert.GreaterOrEqual(OfflineTimeCalculator.CalculateOfflineSeconds(prefs, 28800L, "slotA"), 3590L);
        }

        // Issue #120: the cap went unchecked into Math.Min, so a negative cap from a game's configuration returned negative
        // offline seconds, which the game then paid as negative income
        [Test]
        public void OfflineTimeCalculator_ANegativeCap_GrantsNothing_AndIsLogged()
        {
            var prefs = new InMemoryPlayerPrefsService();
            OfflineTimeCalculator.RecordQuitTimestamp(prefs, "quit");
            foreach (var key in new System.Collections.Generic.List<string>(prefs.Keys))
                prefs.SetLong(key, prefs.GetLong(key) - (key == "quit" ? 100L : 100L * 1000L)); // quit 100 s ago

            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[OfflineTimeCalculator\] The cap of -5 s is negative"));
            Assert.AreEqual(0L, OfflineTimeCalculator.CalculateOfflineSeconds(prefs, -5L, "quit"));
        }
    }
}
