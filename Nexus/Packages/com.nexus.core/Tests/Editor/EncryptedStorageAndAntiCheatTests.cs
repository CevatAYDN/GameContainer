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
        public void IapService_MockOwnedIntegrity_TamperDetectedAndSetCleared()
        {
            var iap = new IapService();
            bool purchased = false;
            iap.PurchaseProduct("no_ads", (ok, id) => purchased = ok);
            Assert.IsTrue(purchased);
            Assert.IsTrue(iap.IsProductOwned("no_ads"));

            // Simulate a RAM scan injecting a fake product directly into the mock set,
            // bypassing the checksum recomputation (as a memory editor would).
            var field = typeof(IapService).GetField("_mockOwnedProducts",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var set = field?.GetValue(iap) as System.Collections.Generic.HashSet<string>;
            Assert.IsNotNull(set, "_mockOwnedProducts must exist as a HashSet<string>.");
            set.Add("hacked_owned_product");

            // The integrity check must reject the forged ownership and wipe the set so
            // no stale (including previously-legitimate) entries survive the tamper.
            Assert.IsFalse(iap.IsProductOwned("hacked_owned_product"),
                "Tampered product ownership must be rejected.");
            Assert.IsFalse(iap.IsProductOwned("no_ads"),
                "After a tamper the whole mock set is cleared (fail-closed).");

            // A new legitimate purchase still works after the wipe.
            bool repurchased = false;
            iap.PurchaseProduct("no_ads", (ok, id) => repurchased = ok);
            Assert.IsTrue(repurchased);
            Assert.IsTrue(iap.IsProductOwned("no_ads"));
        }

        [Test]
        public void IapService_MockOwned_NormalFlowUnaffected()
        {
            var iap = new IapService();
            iap.PurchaseProduct("gem_pack", (ok, id) => { });
            iap.PurchaseProduct("coin_pack", (ok, id) => { });

            Assert.IsTrue(iap.IsProductOwned("gem_pack"));
            Assert.IsTrue(iap.IsProductOwned("coin_pack"));
            Assert.IsFalse(iap.IsProductOwned("never_bought"));
        }

        [Test]
        public void IapService_MockOwned_PurchasePathRejectsTamperedSet()
        {
            // Regression: the purchase path must verify the checksum BEFORE mutating.
            // Otherwise a RAM-injected product would be silently legitimized: the next
            // legitimate purchase recomputes the checksum over the forged set, blessing
            // the injection permanently.
            var iap = new IapService();
            iap.PurchaseProduct("no_ads", (ok, id) => { });
            Assert.IsTrue(iap.IsProductOwned("no_ads"));

            var field = typeof(IapService).GetField("_mockOwnedProducts",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var set = field?.GetValue(iap) as System.Collections.Generic.HashSet<string>;
            Assert.IsNotNull(set);
            set.Add("hacked_owned_product");

            // Purchase directly (no IsProductOwned in between) — the purchase path itself
            // must detect the tamper, wipe the set, and only then grant the legit purchase.
            bool purchased = false;
            iap.PurchaseProduct("gem_pack", (ok, id) => purchased = ok);
            Assert.IsTrue(purchased);

            Assert.IsFalse(iap.IsProductOwned("hacked_owned_product"),
                "Forged ownership must not survive a legitimate purchase.");
            Assert.IsTrue(iap.IsProductOwned("gem_pack"),
                "The legitimate purchase must be granted on a clean set.");
        }

        [Test]
        public void IapService_MockOwned_ChecksumRotatesAcrossReads()
        {
            // The stored checksum is XOR-masked with a mask that rotates on every successful
            // verify. Two reads of an IDENTICAL set must yield different stored values — that
            // is what defeats a value-scan/static patch of the checksum field.
            var iap = new IapService();
            iap.PurchaseProduct("no_ads", (ok, id) => { });
            Assert.IsTrue(iap.IsProductOwned("no_ads"));

            var checksumField = typeof(IapService).GetField("_mockOwnedChecksum",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            int before = (int)checksumField.GetValue(iap);

            Assert.IsTrue(iap.IsProductOwned("no_ads"), "A second read of a clean set must still succeed.");
            int after = (int)checksumField.GetValue(iap);

            Assert.AreNotEqual(before, after,
                "The rotating mask must change the stored checksum on every successful read (otherwise a static value-scan could patch it).");
        }

        [Test]
        public void IapService_MockOwned_SnapshotReplayOfChecksumIsDetected()
        {
            // Stronger attack than a plain append: the attacker snapshots a consistent
            // (set, checksum, mask) triple, appends a fake product, then REPLAYS the snapshot
            // checksum+mask so the stored fields are internally consistent. The salted hash
            // over the changed set no longer matches, so the tamper is still detected.
            var iap = new IapService();
            iap.PurchaseProduct("no_ads", (ok, id) => { });
            Assert.IsTrue(iap.IsProductOwned("no_ads"));

            var checksumField = typeof(IapService).GetField("_mockOwnedChecksum",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var maskField = typeof(IapService).GetField("_mockOwnedMask",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var setField = typeof(IapService).GetField("_mockOwnedProducts",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            int snapshotChecksum = (int)checksumField.GetValue(iap);
            int snapshotMask = (int)maskField.GetValue(iap);

            var set = setField?.GetValue(iap) as System.Collections.Generic.HashSet<string>;
            Assert.IsNotNull(set);
            set.Add("hacked_owned_product");
            checksumField.SetValue(iap, snapshotChecksum); // replay the consistent snapshot
            maskField.SetValue(iap, snapshotMask);

            Assert.IsFalse(iap.IsProductOwned("hacked_owned_product"),
                "Replaying a consistent (checksum, mask) snapshot after appending must still be rejected.");
            Assert.IsFalse(iap.IsProductOwned("no_ads"),
                "After a tamper the whole mock set is cleared (fail-closed).");

            // A fresh legitimate purchase works after the wipe.
            bool repurchased = false;
            iap.PurchaseProduct("no_ads", (ok, id) => repurchased = ok);
            Assert.IsTrue(repurchased);
            Assert.IsTrue(iap.IsProductOwned("no_ads"));
        }

        [Test]
        public void IapService_MockOwned_ReadBeforeAnyPurchaseIsStable()
        {
            // Behavioral lock (not a regression detector): _mockOwnedChecksum is seeded
            // to the empty-set hash in the constructor, so the very first read on a fresh
            // service does not report a spurious "memory tampering detected" (0 vs.
            // empty-set hash mismatch). Wiping an empty set is unobservable, so this test
            // passes on both old and new code — it pins the constructor-seeding invariant.
            var iap = new IapService();
            Assert.IsFalse(iap.IsProductOwned("nothing_bought"));

            // A purchase after the read must still work and persist.
            iap.PurchaseProduct("coins_100", (ok, id) => { });
            Assert.IsTrue(iap.IsProductOwned("coins_100"));
            Assert.IsFalse(iap.IsProductOwned("nothing_bought"));
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
    }
}
