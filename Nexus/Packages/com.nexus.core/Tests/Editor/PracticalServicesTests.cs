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
        public void AudioService_PlaySfxWithRandomPitch_ExecutesCleanly()
        {
            var audioService = new AudioService();
            // AudioService should accept PlaySfxWithRandomPitch interface call without throwing exceptions
            Assert.DoesNotThrow(() => audioService.PlaySfxWithRandomPitch(null, 0.95f, 1.05f));
        }

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
        public void OfflineTimeCalculator_ValidatesTimeAndDetectsTampering()
        {
            using var storage = new EncryptedStorageService("Test_Offline_Salt");

            // Record quit timestamp
            OfflineTimeCalculator.RecordQuitTimestamp(storage);

            // Calculate offline time (should be 0 or small positive number)
            long offlineSec = OfflineTimeCalculator.CalculateOfflineSeconds(storage, 3600);
            Assert.GreaterOrEqual(offlineSec, 0);

            // Test anti-cheat: set quit time 1000 seconds into the future
            long futureTimestamp = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1000;
            storage.SetLong("NT_LastQuitTimestamp", futureTimestamp);
            storage.Save();

            long tamperedOfflineSec = OfflineTimeCalculator.CalculateOfflineSeconds(storage, 3600);
            Assert.AreEqual(0, tamperedOfflineSec, "Tampered future timestamp must return 0 offline seconds.");

            // A8: a monotonic hardware tick is stored alongside the wall clock; a clock
            // pushed FORWARD (wall diff inflated but monotonic diff ~0) must be clamped
            // to the real elapsed time instead of granting inflated offline rewards.
            long monoMs = System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency;
            long realElapsedMs = monoMs - storage.GetLong("NT_LastQuitMonotonicMs", 0L);
            long forwardCheatSec = OfflineTimeCalculator.CalculateOfflineSeconds(storage, 3600);
            Assert.LessOrEqual(forwardCheatSec, Math.Max(0, realElapsedMs / 1000L) + 1,
                "Forward clock manipulation must not inflate offline progress beyond real elapsed time.");

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

        [Test]
        public void InputService_MoveInput_And_PlayerMoveSignal_Works()
        {
            var inputService = new InputService();
            inputService.SetVirtualJoystickInput(new Vector2(0.5f, 0.8f));

            inputService.UpdateInput(0.016f);

            Assert.IsTrue(inputService.IsInputActive);
            Assert.AreEqual(0.5f, inputService.MoveInput.x, 0.001f);
            Assert.AreEqual(0.8f, inputService.MoveInput.y, 0.001f);
        }
    }
}
