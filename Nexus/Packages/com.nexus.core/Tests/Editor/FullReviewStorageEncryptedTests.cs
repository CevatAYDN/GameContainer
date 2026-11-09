using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Services;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewStorageEncryptedTests
    {
        private const string BalanceKey = "NT_Eco_coin";

#if UNITY_EDITOR
        [Test]
#endif
        public void ST02_UnqueryableCanonical_DoesNotCacheAnAbsentBalance()
            => VerifyObstructedCanonical(withBackup: false);

#if UNITY_EDITOR
        [Test]
#endif
        public void ST02_UnqueryableCanonical_DoesNotLoadAStaleBackup()
            => VerifyObstructedCanonical(withBackup: true);

#if UNITY_EDITOR
        [Test]
#endif
        public void ST02_AbsentKey_RemainsAbsentAndWritable()
        {
            using var files = new Files();
            var store = files.Open();
            Require(store.TryGetString(BalanceKey, out _) == StorageReadStatus.Absent && !store.HasKey(BalanceKey),
                "A genuinely missing file in a readable store must still be absent.");
            store.SetLong(BalanceKey, 25);
            store.SaveOrThrow();
            store.Dispose();
            Require(files.Open().GetLong(BalanceKey, -1) == 25, "An absent key must remain writable.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ST02_MissingOrCorruptCanonical_StillRecoversValidBackup()
        {
            foreach (bool corrupt in new[] { false, true })
            {
                using var files = new Files();
                var seed = files.Open();
                seed.SetLong(BalanceKey, 900);
                seed.SaveOrThrow();
                seed.SetLong(BalanceKey, 1000);
                seed.SaveOrThrow();
                seed.Dispose();
                string path = files.OnlySavePath();
                if (corrupt) File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                else File.Delete(files.CheckedPath(path));
                var store = files.Open();
                Require(store.GetLong(BalanceKey, -1) == 900,
                    "Confirmed absence or corrupt bytes must retain the established valid-backup recovery.");
                store.Dispose();
                Require(files.Open().GetLong(BalanceKey, -1) == 900, "Recovered data must remain durable.");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ST05_FailedReadAfterConfirmedDelete_CannotRestoreUnreadableStatus()
            => VerifyDeletionDuringFailedRead(useNullWrite: false);

#if UNITY_EDITOR
        [Test]
#endif
        public void ST05_FailedReadAfterConfirmedNullWrite_CannotRestoreUnreadableStatus()
            => VerifyDeletionDuringFailedRead(useNullWrite: true);

#if UNITY_EDITOR
        [Test]
#endif
        public async Task ST05_PausedFailedRead_CannotPoisonAConcurrentConfirmedDelete()
        {
            using var context = new Context();
            var logger = new CallbackLogger();
            context.Container.BindInstance<ILoggerService>(logger);
            Require(ReferenceEquals(NexusRuntime.Logger, logger), "The isolated fixture must own its warning callback.");
            using var files = new Files();
            var seed = files.Open();
            seed.SetLong(BalanceKey, 1000);
            seed.SaveOrThrow();
            seed.Dispose();
            File.WriteAllBytes(files.OnlySavePath(), new byte[] { 1, 2, 3 });
            var store = files.Open();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            logger.Warning = message =>
            {
                if (!message.Contains(BalanceKey)) return;
                entered.Set();
                Require(release.Wait(TimeSpan.FromSeconds(10)), "The failed reader was not released.");
            };
            Task reader = Task.Run(() => store.TryGetString(BalanceKey, out _));
            try
            {
                Require(entered.Wait(TimeSpan.FromSeconds(10)), "The read must reach its failure before deletion.");
                store.DeleteKey(BalanceKey);
                store.SaveOrThrow();
            }
            finally
            {
                release.Set();
                await reader;
                logger.Warning = null;
            }
            StorageReadStatus later = store.TryGetString(BalanceKey, out _);
            bool hasKey = store.HasKey(BalanceKey);
            store.Dispose();
            var durable = files.Open().TryGetString(BalanceKey, out _);
            Require(later == StorageReadStatus.Absent && !hasKey && durable == StorageReadStatus.Absent,
                $"A stale worker read must not undo the confirmed deletion: later={later}, hasKey={hasKey}, durable={durable}.");
        }

        private static void VerifyDeletionDuringFailedRead(bool useNullWrite)
        {
            using var context = new Context();
            var logger = new CallbackLogger();
            context.Container.BindInstance<ILoggerService>(logger);
            Require(ReferenceEquals(NexusRuntime.Logger, logger), "The isolated fixture must own its warning callback.");
            using var files = new Files();
            var seed = files.Open();
            seed.SetLong(BalanceKey, 1000);
            seed.SaveOrThrow();
            seed.Dispose();
            File.WriteAllBytes(files.OnlySavePath(), new byte[] { 1, 2, 3 });
            var store = files.Open();
            bool deleted = false;
            logger.Warning = message =>
            {
                if (deleted || !message.Contains(BalanceKey)) return;
                deleted = true;
                if (useNullWrite) store.SetString(BalanceKey, null);
                else store.DeleteKey(BalanceKey);
                store.SaveOrThrow();
            };
            StorageReadStatus interrupted = store.TryGetString(BalanceKey, out _);
            logger.Warning = null;
            StorageReadStatus later = store.TryGetString(BalanceKey, out _);
            bool hasKey = store.HasKey(BalanceKey);
            store.Dispose();
            StorageReadStatus durable = files.Open().TryGetString(BalanceKey, out _);
            Require(deleted && later == StorageReadStatus.Absent && !hasKey && durable == StorageReadStatus.Absent,
                $"A completed deletion must supersede the older failed read: nullWrite={useNullWrite}, deleted={deleted}, "
                + $"interrupted={interrupted}, later={later}, hasKey={hasKey}, durable={durable}; expected Absent/false/Absent.");
        }

        private static void VerifyObstructedCanonical(bool withBackup)
        {
            using var files = new Files();
            var seed = files.Open();
            if (withBackup)
            {
                seed.SetLong(BalanceKey, 900);
                seed.SaveOrThrow();
            }
            seed.SetLong(BalanceKey, 1000);
            seed.SaveOrThrow();
            seed.Dispose();
            string path = files.OnlySavePath();
            string parked = files.CheckedPath(path + ".parked");
            var store = files.Open();
            using var economy = new EconomyService { PlayerPrefsService = store };
            bool available;
            bool present;
            long observed;
            StorageReadStatus status;
            File.Move(files.CheckedPath(path), parked);
            Directory.CreateDirectory(path);
            try
            {
                available = economy.TryGetBalance("coin", out observed);
                status = store.TryGetString(BalanceKey, out _);
                present = store.HasKey(BalanceKey);
                Require(store.ExportEncryptedSaveData(BalanceKey, out var failure) == null
                    && failure != null && failure.Contains("could not be read"),
                    "An unqueryable export must report a read failure without throwing or choosing a stale backup.");
            }
            finally
            {
                Directory.Delete(files.CheckedPath(path));
                File.Move(parked, files.CheckedPath(path));
            }

            economy.Earn("coin", 25);
            economy.Dispose();
            store.SaveOrThrow();
            store.Dispose();
            long durable = files.Open().GetLong(BalanceKey, -1);
            Require(!available && present && status == StorageReadStatus.Unreadable && durable == 1025,
                $"An unqueryable canonical file must not load a fallback: backup={withBackup}, "
                + $"available={available}, present={present}, observed={observed}, status={status}, durable={durable}; expected 1025.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class CallbackLogger : ILoggerService
        {
            public Action<string> Warning;
            public bool IsEnabled { get; set; } = true;
            public void Log(string message) { }
            public void LogWarning(string message) => Warning?.Invoke(message);
            public void LogError(string message) { }
            public void LogException(Exception exception) { }
        }

        private sealed class Files : IDisposable
        {
            private readonly string _salt = "fullreview_storage_" + Guid.NewGuid().ToString("N");
            private readonly List<EncryptedStorageService> _stores = new();
            private readonly string _folder;

            public Files()
            {
                string root = Path.GetFullPath(UnityEngine.Application.persistentDataPath);
                _folder = Path.GetFullPath(Path.Combine(root, "SecureData_" + _salt));
                Require(string.Equals(Path.GetDirectoryName(_folder), root, StringComparison.OrdinalIgnoreCase),
                    "The fixture must be a unique direct child of persistentDataPath.");
            }

            public EncryptedStorageService Open()
            {
                var store = new EncryptedStorageService(_salt);
                _stores.Add(store);
                return store;
            }

            public string OnlySavePath()
            {
                string[] paths = Directory.GetFiles(_folder, "*.dat");
                Require(paths.Length == 1, "The fixture must have exactly one canonical save file.");
                return CheckedPath(paths[0]);
            }

            public string CheckedPath(string path)
            {
                string full = Path.GetFullPath(path);
                Require(full.StartsWith(_folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    "File operations must stay inside the unique fixture.");
                return full;
            }

            public void Dispose()
            {
                foreach (var store in _stores) store.Dispose();
                string root = Path.GetFullPath(UnityEngine.Application.persistentDataPath);
                Require(string.Equals(Path.GetDirectoryName(_folder), root, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(_folder) == "SecureData_" + _salt,
                    "Cleanup must stay inside the unique fixture.");
                if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
                UnityEngine.PlayerPrefs.DeleteKey("NT_StorageSeed_" + _salt);
                UnityEngine.PlayerPrefs.Save();
            }
        }
    }
}
