using System.IO;
using System.Threading;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class EncryptedStorageAndAntiCheatTests
    {
        // ── Issue #79: a backup restored on another phone ──

        private static void ForgetStore(string salt)
        {
            UnityEngine.PlayerPrefs.DeleteKey($"NT_StorageSeed_{salt}");
            UnityEngine.PlayerPrefs.DeleteKey($"NT_StorageSeed_{salt}_App");
            var folder = Path.Combine(UnityEngine.Application.persistentDataPath, $"SecureData_{salt}");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        private static void Write(string salt, StorageKeyBinding binding, string device, int coins)
        {
            var store = new EncryptedStorageService(salt, binding, device) { AutoSave = true };
            store.SetInt("Coins", coins);
            store.Dispose();
        }

        private static int Read(string salt, StorageKeyBinding binding, string device)
        {
            var store = new EncryptedStorageService(salt, binding, device);
            try { return store.GetInt("Coins", -1); }
            finally { store.Dispose(); }
        }

        private static T Private<T>(EncryptedStorageService store, string name, params object[] args)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var field = typeof(EncryptedStorageService).GetField(name, flags);
            if (field != null) return (T)field.GetValue(store);
            return (T)typeof(EncryptedStorageService).GetMethod(name, flags).Invoke(store, args);
        }

        // What the builds of 2026-07-10 to 2026-07-31 wrote (58aea9e, fcc9fed): no version byte, IV, the first 16 bytes of
        // HMAC-SHA256(IV || ciphertext), AES-CBC ciphertext; under the MD5 file name.
        private static void WriteUnversioned(EncryptedStorageService store, string key, string value, byte[] aesKey, byte[] hmacKey, byte firstIvByte)
        {
            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = aesKey;
            aes.GenerateIV();
            byte[] iv = aes.IV;
            iv[0] = firstIvByte;
            aes.IV = iv;
            byte[] plain = System.Text.Encoding.UTF8.GetBytes(value);
            byte[] cipher;
            using (var encryptor = aes.CreateEncryptor()) cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
            byte[] hmac;
            using (var h = new System.Security.Cryptography.HMACSHA256(hmacKey))
            {
                h.TransformBlock(iv, 0, 16, null, 0);
                h.TransformFinalBlock(cipher, 0, cipher.Length);
                hmac = h.Hash;
            }
            var file = new byte[32 + cipher.Length];
            System.Buffer.BlockCopy(iv, 0, file, 0, 16);
            System.Buffer.BlockCopy(hmac, 0, file, 16, 16);
            System.Buffer.BlockCopy(cipher, 0, file, 32, cipher.Length);
            File.WriteAllBytes(Private<string>(store, "GetLegacyFilePath", key), file);
        }

        // Issue #166: since 54a9393 the reader compared the stored 16 HMAC bytes with all 32, so every save written before the
        // versioned format read as tampered; and an unversioned file whose first IV byte is 2 or 3 was taken for a versioned one
        [Test]
        public void ASaveWrittenBeforeTheVersionedFormat_IsReadAndMigrated_AlsoWhenItsFirstByteLooksLikeAVersion()
        {
            const string salt = "Test_Salt_Unversioned166";
            ForgetStore(salt);
            try
            {
                const string value = "level=12;coins=91011;gems=4"; // 27 bytes: a 64-byte file, past the 49-byte versioned header
                var writer = new EncryptedStorageService(salt, StorageKeyBinding.Device, "phone-A");
                WriteUnversioned(writer, "aes128", value, Private<byte[]>(writer, "_legacyEncryptionKey"), Private<byte[]>(writer, "_legacyHmacKey"), 0x41);
                WriteUnversioned(writer, "aes256", value, Private<byte[]>(writer, "_encryptionKey"), Private<byte[]>(writer, "_hmacKey"), 0x41);
                WriteUnversioned(writer, "aes128_first_byte_3", value, Private<byte[]>(writer, "_legacyEncryptionKey"), Private<byte[]>(writer, "_legacyHmacKey"), 3);
                WriteUnversioned(writer, "aes256_first_byte_2", value, Private<byte[]>(writer, "_encryptionKey"), Private<byte[]>(writer, "_hmacKey"), 2);
                writer.Dispose();

                var keys = new[] { "aes128", "aes256", "aes128_first_byte_3", "aes256_first_byte_2" };
                var reader = new EncryptedStorageService(salt, StorageKeyBinding.Device, "phone-A");
                foreach (var key in keys)
                {
                    Assert.AreEqual(value, reader.GetString(key, null), key);
                    Assert.IsFalse(File.Exists(Private<string>(reader, "GetLegacyFilePath", key)), key + ": the old file is removed once migrated");
                    Assert.AreEqual(3, File.ReadAllBytes(Private<string>(reader, "GetFilePath", key))[0], key + ": rewritten in the current format");
                }
                reader.Dispose();

                var again = new EncryptedStorageService(salt, StorageKeyBinding.Device, "phone-A");
                foreach (var key in keys) Assert.AreEqual(value, again.GetString(key, null), key + " after the migration");
                again.Dispose();
                UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
            }
            finally { ForgetStore(salt); }
        }

        // Issue #166: a save file emptied by a power loss read as missing with no log, though File.Replace had kept the
        // version before it in .bak; a save file that fails its check did not look at the backup either
        [Test]
        public void AnEmptyOrDamagedSaveFile_FallsBackToTheVersionBefore_AndSaysSo()
        {
            const string salt = "Test_Salt_Backup166";
            ForgetStore(salt);
            try
            {
                var writer = new EncryptedStorageService(salt, StorageKeyBinding.Device, "phone-A") { AutoSave = true };
                writer.SetString("coins", "100");
                writer.SetString("coins", "250"); // 250 in the save file, 100 in its .bak
                writer.SetString("gems", "7");
                writer.SetString("gems", "9");
                writer.SetString("lives", "3"); // written once: no .bak
                string coins = Private<string>(writer, "GetFilePath", "coins"), gems = Private<string>(writer, "GetFilePath", "gems"), lives = Private<string>(writer, "GetFilePath", "lives");
                writer.Dispose();

                File.WriteAllBytes(coins, new byte[0]); // what a power loss after the rename can leave
                byte[] damaged = File.ReadAllBytes(gems);
                damaged[damaged.Length - 1] ^= 0xFF;
                File.WriteAllBytes(gems, damaged);
                File.WriteAllBytes(lives, new byte[5]);

                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"key 'coins' is 0 bytes, shorter than any save: read the version before its last write from the backup, so that write is lost"));
                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"key 'gems' fails its integrity check: read the version before its last write from the backup"));
                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"key 'lives' is 5 bytes, shorter than any save: read as missing"));
                var reader = new EncryptedStorageService(salt, StorageKeyBinding.Device, "phone-A");
                Assert.AreEqual("100", reader.GetString("coins", null));
                Assert.AreEqual("7", reader.GetString("gems", null));
                Assert.IsNull(reader.GetString("lives", null), "no backup: nothing to fall back to");
                reader.Dispose();

                var again = new EncryptedStorageService(salt, StorageKeyBinding.Device, "phone-A");
                Assert.AreEqual("100", again.GetString("coins", null), "the recovered value was written back");
                Assert.AreEqual("7", again.GetString("gems", null));
                again.Dispose();
                UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
            }
            finally { ForgetStore(salt); }
        }

        // Issue #166: a seed missing from PlayerPrefs (PlayerPrefs.DeleteAll, or a switch from App to Device binding) was replaced
        // with no log in any build, though the save files written under it stayed on disk and could never be read again
        [Test]
        public void ANewSeedWithSavesOnDisk_IsAnErrorThatNamesTheCause_AndAFirstRunIsSilent()
        {
            const string salt = "Test_Salt_Seed166";
            ForgetStore(salt);
            try
            {
                Write(salt, StorageKeyBinding.Device, "phone-A", 1234); // a first run: no line

                UnityEngine.PlayerPrefs.DeleteKey($"NT_StorageSeed_{salt}");
                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(@"storage seed 'NT_StorageSeed_Test_Salt_Seed166' is missing from PlayerPrefs \(PlayerPrefs.DeleteAll.*\), and the 1 save file\(s\) in '.*SecureData_Test_Salt_Seed166' were written under it: they cannot be read"));
                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"key 'Coins' was written under the storage seed this run replaced"));
                Assert.AreEqual(-1, Read(salt, StorageKeyBinding.Device, "phone-A"));

                ForgetStore(salt);
                Write(salt, StorageKeyBinding.App, "phone-A", 1234);
                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(@"storage seed 'NT_StorageSeed_Test_Salt_Seed166' is missing from PlayerPrefs: the store ran with StorageKeyBinding.App, and Device binding does not take over the App seed"));
                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"key 'Coins' was written under the storage seed this run replaced"));
                Assert.AreEqual(-1, Read(salt, StorageKeyBinding.Device, "phone-A"));
                UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
            }
            finally { ForgetStore(salt); }
        }

        private static void RaiseAppEvent(string forwarder, params object[] args) =>
            typeof(EncryptedStorageService).GetMethod(forwarder, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, args);

        // Issue #166: the focus-loss and quit saves went to the store constructed last only, so of two live stores (an
        // App-bound and a Device-bound one, as the README shows) the first kept its unsaved keys in memory and lost them
        [Test]
        public void EveryLiveStore_SavesOnFocusLossAndQuit_NotOnlyTheLastOneConstructed()
        {
            const string saltA = "Test_Salt_LiveA166", saltB = "Test_Salt_LiveB166";
            ForgetStore(saltA);
            ForgetStore(saltB);
            var first = new EncryptedStorageService(saltA, StorageKeyBinding.App, "phone-A");
            var second = new EncryptedStorageService(saltB, StorageKeyBinding.Device, "phone-A");
            try
            {
                first.SetString("quit", "a");
                second.SetString("quit", "b");
                RaiseAppEvent("StaticOnQuitting");
                Assert.IsTrue(File.Exists(Private<string>(first, "GetFilePath", "quit")), "the first store saved on quit");
                Assert.IsTrue(File.Exists(Private<string>(second, "GetFilePath", "quit")), "the second store saved on quit");

                first.SetString("focus", "a");
                second.SetString("focus", "b");
                RaiseAppEvent("StaticOnFocusChanged", false); // each store saves on a worker thread
                string firstFocus = Private<string>(first, "GetFilePath", "focus"), secondFocus = Private<string>(second, "GetFilePath", "focus");
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (!(File.Exists(firstFocus) && File.Exists(secondFocus)) && waited.ElapsedMilliseconds < 5000) Thread.Sleep(10);
                Assert.IsTrue(File.Exists(firstFocus), "the first store saved on focus loss");
                Assert.IsTrue(File.Exists(secondFocus), "the second store saved on focus loss");

                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"Another live store already uses '.*SecureData_Test_Salt_LiveA166': two caches over the same files"));
                new EncryptedStorageService(saltA, StorageKeyBinding.App, "phone-A").Dispose();
                UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                first.Dispose();
                second.Dispose();
                ForgetStore(saltA);
                ForgetStore(saltB);
            }
        }

        // Issue #166: Dispose dropped the keys its final save could not write, and a call on a disposed store did nothing,
        // both without a line in any build
        [Test]
        public void DisposeNamesTheKeysItCouldNotSave_AndACallAfterDisposeIsWarnedOncePerKey()
        {
            const string salt = "Test_Salt_Dispose166";
            ForgetStore(salt);
            try
            {
                var store = new EncryptedStorageService(salt, StorageKeyBinding.Device, "phone-A");
                store.SetString("coins", "5");
                store.SetString("gems", "2");
                Directory.CreateDirectory(Private<string>(store, "GetFilePath", "coins") + ".tmp"); // the staged write of 'coins' cannot be created

                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"Save write failed for key 'coins'"));
                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(@"Disposed with 1 key\(s\) its final save could not write, so their last values are lost: 'coins' in '.*SecureData_Test_Salt_Dispose166'"));
                store.Dispose();
                Assert.IsTrue(File.Exists(Private<string>(store, "GetFilePath", "gems")), "the key that could be written was saved");

                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"SetString\('coins'\) on the store for '.*SecureData_Test_Salt_Dispose166' after Dispose: the value is not stored"));
                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"DeleteKey\('gems'\) on the store .* after Dispose: the key is not deleted"));
                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"GetString\('gems'\) on the store .* after Dispose: returns the default, not the saved value"));
                store.SetString("coins", "6");
                store.SetString("coins", "7"); // a repeat: no second line
                store.DeleteKey("gems");
                Assert.AreEqual("none", store.GetString("gems", "none"));
                UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
            }
            finally { ForgetStore(salt); }
        }

        private static string Decoded(EncryptedStorageService store, string key, string payload)
        {
            if (payload == null) return "(null)";
            var args = new object[] { System.Convert.FromBase64String(payload), key, null };
            return Private<bool>(store, "TryReadCurrentFormat", args) ? (string)args[2] : "(fails the import check)";
        }

        // Issue #166: the export read the save file, so a key kept only in its backup or under the older file name exported
        // as null, and after a failed save it exported the value before
        [Test]
        public void TheExport_IsTheValueTheStoreReads_AlsoFromTheBackupTheOlderFileNameOrAnUnsavedWrite()
        {
            const string salt = "Test_Salt_Export166";
            ForgetStore(salt);
            var store = new EncryptedStorageService(salt, StorageKeyBinding.Device, "phone-A") { AutoSave = true };
            string blocked = null;
            try
            {
                store.SetString("bak", "first");
                store.SetString("bak", "second");
                File.Delete(Private<string>(store, "GetFilePath", "bak")); // only the version before is left, in .bak
                WriteUnversioned(store, "legacy", "from-2026-07", Private<byte[]>(store, "_encryptionKey"), Private<byte[]>(store, "_hmacKey"), 0x41);
                store.SetString("unsaved", "old");
                blocked = Private<string>(store, "GetFilePath", "unsaved") + ".tmp";
                Directory.CreateDirectory(blocked); // the next write of 'unsaved' fails
                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"Save write failed for key 'unsaved'"));
                store.SetString("unsaved", "new");
                Private<System.Collections.IDictionary>(store, "_cache").Remove("bak"); // read from disk, as after a restart; 'unsaved' stays in memory only

                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"key 'bak' is missing and its backup is not"));
                string exported = "bak=" + Decoded(store, "bak", store.ExportEncryptedSaveData("bak"))
                    + "; legacy=" + Decoded(store, "legacy", store.ExportEncryptedSaveData("legacy"))
                    + "; unsaved=" + Decoded(store, "unsaved", store.ExportEncryptedSaveData("unsaved"));
                Assert.AreEqual("bak=first; legacy=from-2026-07; unsaved=new", exported);
                Directory.Delete(blocked);
                UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                if (blocked != null && Directory.Exists(blocked)) Directory.Delete(blocked);
                store.Dispose();
                ForgetStore(salt);
            }
        }

        // Issue #166: an import refused for any reason, and an export that failed, returned false or null with no reason
        // and no log in any build
        [Test]
        public void ImportAndExport_SayWhyTheyRefuse_InTheirResultAndInEveryBuild()
        {
            const string salt = "Test_Salt_Import166";
            ForgetStore(salt);
            var store = new EncryptedStorageService(salt, StorageKeyBinding.Device, "phone-A") { AutoSave = true };
            try
            {
                store.SetString("coins", "40");
                string payload = store.ExportEncryptedSaveData("coins", out string failure);
                Assert.IsNull(failure);
                Assert.IsNull(store.ExportEncryptedSaveData("never-written", out failure));
                Assert.AreEqual("the key has no value", failure, "not a failure: no line");

                var refusals = new (string key, string data, string reason)[]
                {
                    ("coins", "%%%", "the data is not Base64"),
                    ("coins", System.Convert.ToBase64String(new byte[10]), "the data is 10 bytes, shorter than any save"),
                    ("coins", System.Convert.ToBase64String(new byte[60]), "the data has format version 0, and this build imports 2 and 3"),
                    ("gems", payload, "the data fails its integrity check: it was exported for another key or under another storage seed"),
                };
                foreach (var (key, data, reason) in refusals)
                {
                    UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(
                        System.Text.RegularExpressions.Regex.Escape($"[EncryptedStorage] Import of key '{key}' refused: {reason}")));
                    Assert.IsFalse(store.ImportEncryptedSaveData(key, data, out failure), reason);
                    StringAssert.StartsWith(reason, failure);
                }
                Assert.AreEqual("40", store.GetString("coins", null), "a refused import leaves the save as it was");

                store.Dispose();
                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, "[EncryptedStorage] Import of key 'coins' refused: the store is disposed.");
                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, "[EncryptedStorage] Export of key 'coins' failed: the store is disposed.");
                Assert.IsFalse(store.ImportEncryptedSaveData("coins", payload, out failure));
                Assert.AreEqual("the store is disposed", failure);
                Assert.IsNull(store.ExportEncryptedSaveData("coins", out failure));
                Assert.AreEqual("the store is disposed", failure);
                UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                store.Dispose();
                ForgetStore(salt);
            }
        }

        [Test]
        public void AppBinding_ReadsDataRestoredOnAnotherPhone_DeviceBindingDoesNot()
        {
            const string device = "Test_Salt_Restore79_Device", app = "Test_Salt_Restore79_App";
            ForgetStore(device); ForgetStore(app);
            try
            {
                Write(device, StorageKeyBinding.Device, "phone-A", 1234);
                Assert.AreEqual(-1, Read(device, StorageKeyBinding.Device, "phone-B"), "The device-bound store reads a restored save as tampered.");

                Write(app, StorageKeyBinding.App, "phone-A", 1234);
                Assert.AreEqual(1234, Read(app, StorageKeyBinding.App, "phone-B"));
            }
            finally { ForgetStore(device); ForgetStore(app); }
        }

        [Test]
        public void AppBinding_TakesOverTheDeviceSeed_SoExistingDataSurvivesTheSwitchAndARestore()
        {
            const string salt = "Test_Salt_Restore79_Switch";
            ForgetStore(salt);
            try
            {
                Write(salt, StorageKeyBinding.Device, "phone-A", 77);
                Assert.AreEqual(77, Read(salt, StorageKeyBinding.App, "phone-A"), "Switching to App on the same phone must keep the data.");
                Assert.AreEqual(77, Read(salt, StorageKeyBinding.App, "phone-B"), "After the switch a restore on another phone must keep it too.");
            }
            finally { ForgetStore(salt); }
        }

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
        public void SecureObservableString_RAMObfuscationWorksAndFiresOnChanged()
        {
            var secureString = new SecureObservableString("Hero");
            int callCount = 0;

            secureString.OnChanged((oldVal, newVal) => callCount++);

            Assert.AreEqual("Hero", (string)secureString);

            secureString.Value = "Mage";
            Assert.AreEqual("Mage", (string)secureString);
            Assert.AreEqual(1, callCount);
        }

        [Test]
        public void SecureObservableString_SetWithoutNotifyDoesNotTriggerOnChanged()
        {
            var secureString = new SecureObservableString("Hero");
            int callCount = 0;

            secureString.OnChanged((oldVal, newVal) => callCount++);
            secureString.SetWithoutNotify("Mage");

            Assert.AreEqual("Mage", (string)secureString);
            Assert.AreEqual(0, callCount);
        }

        [Test]
        public void SecureObservableString_RoundTripsUnicodeNullAndEmpty()
        {
            // Per-char XOR masking must survive surrogate pairs (emoji), null, and empty.
            var secureString = new SecureObservableString("Café-😀-玩家");
            Assert.AreEqual("Café-😀-玩家", secureString.Value);

            secureString.Value = "";
            Assert.AreEqual("", secureString.Value);

            secureString.Value = null;
            Assert.IsNull(secureString.Value);

            var nullStart = new SecureObservableString(null);
            Assert.IsNull(nullStart.Value);
            nullStart.Value = "started-null";
            Assert.AreEqual("started-null", nullStart.Value);
        }

        [Test]
        public void SecureObservableString_SameValueAssignmentDoesNotFireOnChanged()
        {
            var secureString = new SecureObservableString("Hero");
            int callCount = 0;

            secureString.OnChanged((oldVal, newVal) => callCount++);
            secureString.Value = "Hero"; // identical string

            Assert.AreEqual(0, callCount, "Assigning the same value must not notify.");
        }

        [Test]
        public void AdService_InterstitialCooldownUsesObfuscatedStorage()
        {
            var service = new AdService();
            // An adapter whose placements are always ready, so IsInterstitialAvailable reflects only the cooldown gate
            // (without one no ad is available in any build, #162). Initial _lastInterstitialTime is -999f, so the first check passes.
            service.SetNetworkAdapter(new MockAdNetworkAdapter());
            service.SetInterstitialCooldown(30f);
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

        [Test]
        public void EncryptedStorageService_Save_DoesNotClearNewerDirtyValue()
        {
            const string salt = "Test_Salt_SaveVersionRace";
            const string key = "VersionedKey";
            var storage = new EncryptedStorageService(salt) { AutoSave = false };
            storage.DeleteKey(key);
            storage.SetString(key, "old-value");

            var writeLock = typeof(EncryptedStorageService).GetField("_writeLock",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(storage);
            Assert.IsNotNull(writeLock);

            var started = new ManualResetEventSlim(false);
            var saver = new Thread(() =>
            {
                started.Set();
                storage.Save();
            }) { IsBackground = true };

            // Hold the write gate while Save starts.  The fixed implementation snapshots only
            // after acquiring this gate; the old implementation snapshots old-value first,
            // then blocks inside its per-key atomic write.  Updating the key while the saver
            // is waiting deterministically exercises the stale-write/dirty-clear window.
            lock (writeLock)
            {
                saver.Start();
                Assert.IsTrue(started.Wait(1000));
                Assert.IsTrue(SpinWait.SpinUntil(
                    () => (saver.ThreadState & ThreadState.WaitSleepJoin) != 0, 1000),
                    "Save thread did not reach the serialized write gate.");
                storage.SetString(key, "new-value");
            }

            Assert.IsTrue(saver.Join(5000), "Save thread did not complete.");
            storage.Save();
            storage.Dispose();

            var reloaded = new EncryptedStorageService(salt);
            Assert.AreEqual("new-value", reloaded.GetString(key),
                "A newer SetString must remain dirty until its value reaches disk.");
            reloaded.DeleteKey(key);
            reloaded.Dispose();
        }

        // Issue #164: a release player drops LogWarning, so a save read back as its default or a replaced seed left no trace in its log
        [Test]
        public void ASaveReadAsTamperedOrAReplacedSeed_IsWarnedInAReleasePlayerToo()
        {
            const string salt = "Test_Salt_Release164";
            ForgetStore(salt);
            NexusRuntime.Reset(); // the fallback logger, which a release player disables; a logger another test left cached may be enabled
            NexusBuild.OverrideForTests(false);
            try
            {
                Assert.IsFalse(NexusRuntime.Logger.IsEnabled, "a release player's logger drops LogWarning");
                Write(salt, StorageKeyBinding.Device, "phone-A", 1234);

                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"tampering detected for key: Coins"));
                Assert.AreEqual(-1, Read(salt, StorageKeyBinding.Device, "phone-B"), "another phone's key reads the save as tampered");

                UnityEngine.PlayerPrefs.SetString($"NT_StorageSeed_{salt}", "%%%");
                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex(@"storage seed 'NT_StorageSeed_Test_Salt_Release164' could not be decoded .*, and the 1 save file\(s\) in '.*' were written under it"));
                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex(@"key 'Coins' was written under the storage seed this run replaced"));
                Assert.AreEqual(-1, Read(salt, StorageKeyBinding.Device, "phone-A"), "a replaced seed cannot read the old save");
                UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                NexusBuild.OverrideForTests(null);
                ForgetStore(salt);
            }
        }
    }
}
