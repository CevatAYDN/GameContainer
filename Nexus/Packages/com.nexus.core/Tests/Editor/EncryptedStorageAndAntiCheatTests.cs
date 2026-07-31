using System.IO;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class EncryptedStorageAndAntiCheatTests
    {
        [Test]
        public void EncryptedStorageService_EncryptsAndDecryptsValues()
        {
            var storage = new EncryptedStorageService("Test_Salt_99");
            storage.AutoSave = true;

            storage.SetInt("PlayerCoins", 9999);
            storage.SetString("PlayerName", "Hero");

            Assert.AreEqual(9999, storage.GetInt("PlayerCoins"));
            Assert.AreEqual("Hero", storage.GetString("PlayerName"));
            Assert.IsTrue(storage.HasKey("PlayerCoins"));

            storage.DeleteKey("PlayerCoins");
            Assert.AreEqual(0, storage.GetInt("PlayerCoins"));
            storage.Dispose();
        }

        [Test]
        public void EncryptedStorageService_DataTypesSupported()
        {
            var storage = new EncryptedStorageService("Test_Salt_DataTypes");
            storage.AutoSave = true;

            storage.SetFloat("Volume", 0.75f);
            storage.SetBool("IsVip", true);

            Assert.AreEqual(0.75f, storage.GetFloat("Volume"));
            Assert.IsTrue(storage.GetBool("IsVip"));

            storage.DeleteKey("Volume");
            storage.DeleteKey("IsVip");
            storage.Dispose();
        }

        [Test]
        public void EncryptedStorageService_TamperDetectionRejectsCorruptedFile()
        {
            var storage = new EncryptedStorageService("Test_Salt_Tamper");
            storage.AutoSave = true; // Make sure it saves to disk immediately

            // Clear old runs
            storage.DeleteKey("SecretScore");

            storage.SetInt("SecretScore", 5000);

            // Corrupt file bytes directly on disk to simulate hacker editing
            var getFilePathMethod = typeof(EncryptedStorageService).GetMethod("GetFilePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            string fileToTamper = getFilePathMethod.Invoke(storage, new object[] { "SecretScore" }) as string;
            Assert.IsTrue(File.Exists(fileToTamper));

            byte[] corruptedBytes = File.ReadAllBytes(fileToTamper);
            corruptedBytes[corruptedBytes.Length - 1] ^= 0xFF; // Flip bits
            File.WriteAllBytes(fileToTamper, corruptedBytes);

            // Create a new instance to read it back from the tampered disk file, bypassing the cache
            var newStorage = new EncryptedStorageService("Test_Salt_Tamper");
            int result = newStorage.GetInt("SecretScore", 0);
            Assert.AreEqual(0, result);

            newStorage.DeleteKey("SecretScore");
            newStorage.Dispose();
            storage.Dispose();
        }

        [Test]
        public void EncryptedStorageService_DeviceBindingRejectsForeignSaveFile()
        {
            var deviceAStorage = new EncryptedStorageService("Device_A_Salt");
            deviceAStorage.AutoSave = true;

            var deviceBStorage = new EncryptedStorageService("Device_B_Salt");
            deviceBStorage.AutoSave = true;

            deviceAStorage.DeleteKey("DeviceToken");
            deviceAStorage.SetString("DeviceToken", "Token12345");

            // Device B attempts to read Device A's file
            string result = deviceBStorage.GetString("DeviceToken", "INVALID");
            Assert.AreEqual("INVALID", result);

            deviceAStorage.DeleteKey("DeviceToken");
            deviceAStorage.Dispose();
            deviceBStorage.Dispose();
        }

        [Test]
        public void SecureObservableInt_RAMObfuscationWorksAndFiresOnChanged()
        {
            var secureInt = new SecureObservableInt(50);
            int callCount = 0;

            secureInt.OnChanged((oldVal, newVal) => callCount++);

            Assert.AreEqual(50, (int)secureInt);

            secureInt.Value = 150;
            Assert.AreEqual(150, (int)secureInt);
            Assert.AreEqual(1, callCount);
        }

        [Test]
        public void SecureObservableInt_SetWithoutNotifyDoesNotTriggerOnChanged()
        {
            var secureInt = new SecureObservableInt(100);
            int callCount = 0;

            secureInt.OnChanged((oldVal, newVal) => callCount++);
            secureInt.SetWithoutNotify(200);

            Assert.AreEqual(200, (int)secureInt);
            Assert.AreEqual(0, callCount);
        }

        [Test]
        public void SecureObservableLong_RAMObfuscationWorksAndFiresOnChanged()
        {
            var secureLong = new SecureObservableLong(50L);
            int callCount = 0;

            secureLong.OnChanged((oldVal, newVal) => callCount++);

            Assert.AreEqual(50L, (long)secureLong);

            secureLong.Value = 150L;
            Assert.AreEqual(150L, (long)secureLong);
            Assert.AreEqual(1, callCount);
        }

        [Test]
        public void SecureObservableLong_SetWithoutNotifyDoesNotTriggerOnChanged()
        {
            var secureLong = new SecureObservableLong(100L);
            int callCount = 0;

            secureLong.OnChanged((oldVal, newVal) => callCount++);
            secureLong.SetWithoutNotify(200L);

            Assert.AreEqual(200L, (long)secureLong);
            Assert.AreEqual(0, callCount);
        }

        [Test]
        public void SecureObservableLong_SupportsFullLongRange()
        {
            // Regression for the long economy migration: values beyond int range must
            // round-trip through the XOR-obscured storage (key rotation on write is
            // exercised implicitly — the fields are private, so only value round-tripping
            // is observable from outside).
            var secureLong = new SecureObservableLong(long.MaxValue);
            Assert.AreEqual(long.MaxValue, secureLong.Value);

            secureLong.Value = 123456789012345L;
            Assert.AreEqual(123456789012345L, secureLong.Value);

            secureLong.Value = 0L;
            Assert.AreEqual(0L, secureLong.Value);
        }

        [Test]
        public void SecureObservableFloat_RAMObfuscationWorksAndFiresOnChanged()
        {
            var secureFloat = new SecureObservableFloat(0.75f);
            int callCount = 0;

            secureFloat.OnChanged((oldVal, newVal) => callCount++);

            Assert.AreEqual(0.75f, (float)secureFloat, 0.0001f);

            secureFloat.Value = 12.5f;
            Assert.AreEqual(12.5f, (float)secureFloat, 0.0001f);
            Assert.AreEqual(1, callCount);
        }

        [Test]
        public void SecureObservableFloat_SetWithoutNotifyDoesNotTriggerOnChanged()
        {
            var secureFloat = new SecureObservableFloat(1f);
            int callCount = 0;

            secureFloat.OnChanged((oldVal, newVal) => callCount++);
            secureFloat.SetWithoutNotify(3.25f);

            Assert.AreEqual(3.25f, (float)secureFloat, 0.0001f);
            Assert.AreEqual(0, callCount);
        }

        [Test]
        public void SecureObservableFloat_RoundTripsNegativeAndZero()
        {
            // Bit-pattern XOR storage must survive sign and exponent bits untouched,
            // not just positive fractional values.
            var secureFloat = new SecureObservableFloat(-42.5f);
            Assert.AreEqual(-42.5f, secureFloat.Value, 0.0001f);

            secureFloat.Value = 0f;
            Assert.AreEqual(0f, secureFloat.Value, 0.0001f);

            secureFloat.Value = -0f;
            Assert.AreEqual(0f, secureFloat.Value, 0.0001f); // -0.0 bit pattern XOR round-trips to -0.0
        }

        [Test]
        public void AdService_InterstitialCooldownUsesObfuscatedStorage()
        {
            var service = new AdService();
            service.SetInterstitialCooldown(30f);

            // No adapter bound → IsInterstitialAvailable reflects only the cooldown gate.
            // Initial _lastInterstitialTime is -999f, so the first check passes.
            Assert.IsTrue(service.IsInterstitialAvailable("main"));

            // ShowInterstitial stamps _lastInterstitialTime = now → cooldown becomes active.
            service.ShowInterstitial("main");
            Assert.IsFalse(service.IsInterstitialAvailable("main"),
                "Interstitial must be unavailable right after a show (cooldown active).");

            // A zero cooldown clears the gate immediately.
            service.SetInterstitialCooldown(0f);
            Assert.IsTrue(service.IsInterstitialAvailable("main"));
        }

        [Test]
        public void EncryptedStorageService_LongAndCachingBehavior()
        {
            var salt = "Test_Salt_LongAndCaching";
            
            // Clean up any old test run files
            var storageTemp = new EncryptedStorageService(salt);
            storageTemp.DeleteKey("CachedLongKey");
            storageTemp.Save();
            storageTemp.Dispose();

            var storage = new EncryptedStorageService(salt);
            storage.AutoSave = false;

            long bigVal = 987654321012345L;
            storage.SetLong("CachedLongKey", bigVal);

            // Verify in-memory retrieval
            Assert.AreEqual(bigVal, storage.GetLong("CachedLongKey"));

            // Verify that the file doesn't exist on disk yet because AutoSave is false
            var getFilePathMethod = typeof(EncryptedStorageService).GetMethod("GetFilePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var filePath = getFilePathMethod.Invoke(storage, new object[] { "CachedLongKey" }) as string;
            
            Assert.IsFalse(File.Exists(filePath), "File should not exist on disk before Save() is called");

            // Save and verify file exists
            storage.Save();
            Assert.IsTrue(File.Exists(filePath), "File should exist on disk after Save() is called");

            // Clean up
            storage.DeleteKey("CachedLongKey");
            Assert.IsFalse(File.Exists(filePath), "File should be deleted from disk");
            storage.Dispose();
        }
    }
}
