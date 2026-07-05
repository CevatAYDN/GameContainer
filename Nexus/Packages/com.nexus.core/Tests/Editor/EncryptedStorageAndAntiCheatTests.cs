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

            storage.SetInt("PlayerCoins", 9999);
            storage.SetString("PlayerName", "Hero");

            Assert.AreEqual(9999, storage.GetInt("PlayerCoins"));
            Assert.AreEqual("Hero", storage.GetString("PlayerName"));
            Assert.IsTrue(storage.HasKey("PlayerCoins"));

            storage.DeleteKey("PlayerCoins");
            Assert.AreEqual(0, storage.GetInt("PlayerCoins"));
        }

        [Test]
        public void EncryptedStorageService_DataTypesSupported()
        {
            var storage = new EncryptedStorageService("Test_Salt_DataTypes");

            storage.SetFloat("Volume", 0.75f);
            storage.SetBool("IsVip", true);

            Assert.AreEqual(0.75f, storage.GetFloat("Volume"));
            Assert.IsTrue(storage.GetBool("IsVip"));

            storage.DeleteKey("Volume");
            storage.DeleteKey("IsVip");
        }

        [Test]
        public void EncryptedStorageService_TamperDetectionRejectsCorruptedFile()
        {
            var storage = new EncryptedStorageService("Test_Salt_Tamper");
            storage.SetInt("SecretScore", 5000);

            // Corrupt file bytes directly on disk to simulate hacker editing
            string folder = Path.Combine(UnityEngine.Application.persistentDataPath, "SecureData");
            string[] files = Directory.GetFiles(folder, "*.dat");
            Assert.IsTrue(files.Length > 0);

            string fileToTamper = files[0];
            byte[] corruptedBytes = File.ReadAllBytes(fileToTamper);
            corruptedBytes[corruptedBytes.Length - 1] ^= 0xFF; // Flip bits
            File.WriteAllBytes(fileToTamper, corruptedBytes);

            // Should detect HMAC mismatch and fall back to default value without crashing
            int result = storage.GetInt("SecretScore", 0);
            Assert.AreEqual(0, result);

            storage.DeleteKey("SecretScore");
        }

        [Test]
        public void EncryptedStorageService_DeviceBindingRejectsForeignSaveFile()
        {
            var deviceAStorage = new EncryptedStorageService("Device_A_Salt");
            var deviceBStorage = new EncryptedStorageService("Device_B_Salt");

            deviceAStorage.SetString("DeviceToken", "Token12345");

            // Device B attempts to read Device A's file
            string result = deviceBStorage.GetString("DeviceToken", "INVALID");
            Assert.AreEqual("INVALID", result);

            deviceAStorage.DeleteKey("DeviceToken");
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
    }
}
