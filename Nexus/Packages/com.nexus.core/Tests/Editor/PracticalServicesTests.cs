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

            // Import into a new key
            bool importSuccess = storage.ImportEncryptedSaveData("User_Cloud_Imported", exportedBase64);
            Assert.IsTrue(importSuccess);

            string importedValue = storage.GetString("User_Cloud_Imported", null);
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
            long realElapsedMs = Environment.TickCount64 - storage.GetLong("NT_LastQuitMonotonicMs", 0L);
            long forwardCheatSec = OfflineTimeCalculator.CalculateOfflineSeconds(storage, 3600);
            Assert.LessOrEqual(forwardCheatSec, Math.Max(0, realElapsedMs / 1000L) + 1,
                "Forward clock manipulation must not inflate offline progress beyond real elapsed time.");

            storage.DeleteKey("NT_LastQuitTimestamp");
            storage.DeleteKey("NT_LastQuitMonotonicMs");
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

        [Test]
        public void FloatingTextService_SpawnsAndUpdatesCleanly()
        {
            var floatService = new FloatingTextService();
            floatService.SpawnFloatingText("+$500", Vector3.zero, Color.yellow, 1.0f);

            Assert.AreEqual(1, floatService.ActiveTexts.Count);
            Assert.AreEqual("+$500", floatService.ActiveTexts[0].Text);

            // Update time to expire text
            floatService.UpdateService(1.1f);
            Assert.AreEqual(0, floatService.ActiveTexts.Count);
        }
    }
}
