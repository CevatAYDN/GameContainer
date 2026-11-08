using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Services;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    // Shared with the host: real encrypted filesystem writes, public consumer confirmations.
    public sealed class EncryptedStorageCommitTests
    {
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void MixedSuccessfulAndFailedKeys_NeverConfirmTheBatch()
        {
            using var files = new Files();
            var store = files.Open();
            store.SetString("good", "saved");
            store.SetString("bad", "not saved");
            files.Block(store, "bad");
            Require(!store.TrySave(), "a partial batch must not confirm success");
            var reopened = files.Open();
            Require(reopened.GetString("good") == "saved" && reopened.GetString("bad", null) == null,
                "mixed batch must be checked from durable files, not the dirty cache");
            files.Close(store, failed: true);
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void AutoSaveFailure_RemainsUnconfirmed_AndReopensThePreviousValue()
        {
            using var files = new Files();
            var store = files.Open();
            store.SetString("value", "before");
            Require(store.TrySave(), "baseline must commit");
            files.Block(store, "value");
            store.AutoSave = true;
            store.SetString("value", "after");
            Require(!store.TrySave(), "AutoSave failure must remain visible to confirmation");
            Require(files.Open().GetString("value") == "before", "reopen must see the previous committed value");
            files.Close(store, failed: true);
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void FailedRecoveryRewrite_RemainsPendingUntilConfirmed()
        {
            using var files = new Files();
            var writer = files.Open();
            writer.SetString("value", "backup");
            Require(writer.TrySave(), "baseline must commit");
            writer.SetString("value", "current");
            Require(writer.TrySave(), "replacement must leave a recovery copy");
            files.Close(writer);
            // Simulate an interrupted final rename and a blocked recovery rewrite.
            File.Delete(Files.PathFor(writer, "value"));
            var reader = files.Open();
            files.Block(reader, "value");
            Require(reader.GetString("value") == "backup", "public read must recover the previous committed value");
            Require(!reader.TrySave(), "a failed recovery rewrite must remain pending, not report success");
            files.Close(reader, failed: true);
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void TombstoneAndDeleteKey_ConfirmOnlyAfterDurableDeletion()
        {
            using var files = new Files();
            var store = files.Open();
            store.SetString("tombstone", "old");
            store.SetString("delete", "old");
            Require(store.TrySave(), "baseline must commit");
            store.SetString("tombstone", "new");
            store.SetString("delete", "new");
            Require(store.TrySave(), "replacement and recovery files must commit");
            store.SetString("tombstone", null);
            store.DeleteKey("delete");
            Require(store.TrySave(), "both forms of successful delete must confirm");
            var reopened = files.Open();
            Require(!reopened.HasKey("tombstone") && !reopened.HasKey("delete"), "deleted data must not resurrect from disk or backup");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void FailedDelete_NeverConfirms_AndKeepsTheOldDurableValue()
        {
            foreach (bool tombstone in new[] { false, true })
            {
                using var files = new Files();
                var store = files.Open();
                store.SetString("value", "old");
                Require(store.TrySave(), "baseline must commit");
                using (File.Open(Files.PathFor(store, "value"), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    if (tombstone) store.SetString("value", null);
                    else store.DeleteKey("value");
                    Require(!store.TrySave(), "locked delete must not confirm");
                    files.Close(store, failed: true); // lock remains in place through Dispose
                }
                Require(files.Open().GetString("value") == "old", "failed delete must preserve the previous durable value");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void DeleteKey_UnqueryablePaths_RemainPendingUntilConfirmedRetry() => UnqueryableDeletePaths(false);

#if UNITY_EDITOR
        [Test]
#endif
        public void Tombstone_UnqueryablePaths_RemainPendingUntilConfirmedRetry() => UnqueryableDeletePaths(true);

        private static void UnqueryableDeletePaths(bool tombstone)
        {
            var failures = new List<string>();
            foreach (string kind in new[] { "current", ".bak", ".tmp", "legacy" })
            {
                using var files = new Files();
                var store = files.Open();
                store.SetString("value", "old");
                Require(store.TrySave(), "baseline must commit");
                store.SetString("value", "current");
                Require(store.TrySave(), "replacement must create a recovery copy");
                string current = Files.PathFor(store, "value");
                string path = kind == "legacy" ? Files.LegacyPathFor(store, "value")
                    : kind == "current" ? current : current + kind;
                // A directory deterministically makes File.Exists false but File.Delete fail.
                // Preserve encrypted bytes to restore the path, without changing any ACL.
                if (!File.Exists(path)) File.Copy(current, path);
                string parked = path + ".fixture-parked";
                File.Move(path, parked);
                Directory.CreateDirectory(path);
                Require(!File.Exists(path) && Directory.Exists(path), "fault fixture must hide the path from File.Exists");
                if (tombstone) store.SetString("value", null);
                else store.DeleteKey("value");
                bool confirmedBlocked = store.TrySave();

                Directory.Delete(path);
                File.Move(parked, path);
                bool confirmedRetry = store.TrySave();
                files.Close(store);
                var reopened = files.Open();
                bool resurrected = reopened.HasKey("value");
                bool stalePath = File.Exists(path); // .tmp is not readable through GetString, but must also be deleted
                if (confirmedBlocked || !confirmedRetry || resurrected || stalePath)
                    failures.Add($"{kind}: blocked-confirmed={confirmedBlocked}, retry-confirmed={confirmedRetry}, resurrected={resurrected}, stale-path={stalePath}");
            }
            Require(failures.Count == 0, "unconfirmed deletion must remain pending through path recovery: " + string.Join("; ", failures));
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ConcurrentMutation_DuringCommit_ReturnsFalse_ThenRetryPersistsTheNewVersion()
        {
            using var files = new Files();
            var store = files.OpenBlocking();
            store.SetString("value", "before");
            Require(store.TrySave(), "baseline must commit");
            store.SetString("value", "snapshot");
            using var entered = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            store.AfterReplace = () =>
            {
                entered.Set();
                Require(resume.Wait(TimeSpan.FromSeconds(10)), "test writer was not released");
            };
            var commit = Task.Run(() => store.TrySave());
            try
            {
                Require(entered.Wait(TimeSpan.FromSeconds(10)), "test writer never reached replace");
                store.SetString("value", "newer");
            }
            finally
            {
                resume.Set();
                commit.GetAwaiter().GetResult();
            }
            Require(!commit.GetAwaiter().GetResult(), "a superseded write must not confirm");
            Require(files.Open().GetString("value") == "snapshot", "first reopen must see only the committed snapshot");
            Require(store.TrySave() && files.Open().GetString("value") == "newer", "retry must commit the newer version");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void SupersededButAlreadyAutoSavedMutation_StillDoesNotConfirmTheOlderCall()
        {
            using var files = new Files();
            var store = files.OpenBlocking();
            store.SetString("value", "before");
            Require(store.TrySave(), "baseline must commit");
            store.SetString("value", "snapshot");
            store.AfterReplace = () =>
            {
                store.AutoSave = true;
                store.SetString("value", "newer");
            };
            Require(!store.TrySave(), "a newer AutoSave clearing its dirty marker must not make the superseded call succeed");
            Require(files.Open().GetString("value") == "newer" && store.TrySave(),
                "the independently committed newer mutation can be confirmed by a subsequent call");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void DisposeDuringCommit_AndDisposedStore_DoNotConfirm()
        {
            using var files = new Files();
            var store = files.OpenBlocking();
            store.SetString("value", "before");
            Require(store.TrySave(), "baseline must commit");
            store.SetString("value", "after");
            store.AfterReplace = store.Dispose;
            Require(!store.TrySave() && !store.TrySave(), "disposal during or before confirmation must fail closed");
            store.Save(); // legacy void contract still returns normally
            Require(files.Open().GetString("value") == "after", "Dispose must retain its existing final-flush behavior");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void DeleteWaitingOnAnotherWrite_CannotBeMistakenForACommittedDelete()
        {
            using var files = new Files();
            var store = files.OpenBlocking();
            store.SetString("write", "before");
            store.SetString("delete", "old");
            Require(store.TrySave(), "baseline must commit");
            store.SetString("write", "after");
            using var entered = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            store.AfterReplace = () =>
            {
                entered.Set();
                Require(resume.Wait(TimeSpan.FromSeconds(10)), "test writer was not released");
            };
            var commit = Task.Run(() => store.TrySave());
            Task deletion = null;
            try
            {
                Require(entered.Wait(TimeSpan.FromSeconds(10)), "test writer never reached replace");
                deletion = Task.Run(() => store.DeleteKey("delete"));
                // Public cache state shows the delete was accepted while filesystem I/O is blocked.
                Require(SpinWait.SpinUntil(() => store.TryGetString("delete", out _) == StorageReadStatus.Absent,
                    TimeSpan.FromSeconds(10)), "delete was never accepted");
            }
            finally
            {
                resume.Set();
                // Join even after an assertion failure; no worker can outlive fixture cleanup.
                commit.GetAwaiter().GetResult();
                deletion?.GetAwaiter().GetResult();
            }
            Require(!commit.Result, "a delete queued during the commit must invalidate that confirmation");
            Require(store.TrySave() && !files.Open().HasKey("delete"), "the next confirmation must observe the durable delete");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void LegacyCustomStore_DefaultIsFalse_AndSaveOrThrowDoesNotInferFromVoidSave()
        {
            var memory = new Nexus.Core.InMemoryPlayerPrefsService();
            IPlayerPrefsService store = memory;
            store.SetInt("value", 42);
            store.Save();
            Require(!store.TrySave(), "an unsupported store must not claim durable confirmation");
            try { store.SaveOrThrow(); }
            catch (IOException)
            {
                Require(memory.Saves == 1 && store.GetInt("value") == 42,
                    "confirmation must not infer success by calling legacy void Save, or change its contract");
                return;
            }
            throw new InvalidOperationException("unsupported confirmation must throw");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void EmptySupportedStore_ConfirmsWithoutPendingWork()
        {
            using var files = new Files();
            var store = files.Open();
            Require(store.TrySave(), "a live supported store with no pending mutation can confirm");
            store.SaveOrThrow();
        }

        private sealed class BlockingStore : EncryptedStorageService
        {
            public Action AfterReplace;
            public BlockingStore(string salt) : base(salt) { }
            protected override void ReplaceCurrentFile(string stagedPath, string currentPath, string backupPath)
            {
                base.ReplaceCurrentFile(stagedPath, currentPath, backupPath);
                var action = AfterReplace;
                AfterReplace = null;
                action?.Invoke();
            }
        }

        private sealed class Files : IDisposable
        {
            private readonly string _salt = "338_commit_" + Guid.NewGuid().ToString("N");
            private readonly List<EncryptedStorageService> _stores = new List<EncryptedStorageService>();
            private readonly HashSet<EncryptedStorageService> _closed = new HashSet<EncryptedStorageService>();
            public EncryptedStorageService Open() => Track(new EncryptedStorageService(_salt));
            public BlockingStore OpenBlocking() => Track(new BlockingStore(_salt));
            private T Track<T>(T store) where T : EncryptedStorageService { _stores.Add(store); return store; }
            public static string PathFor(EncryptedStorageService store, string key)
                => (string)typeof(EncryptedStorageService).GetMethod("GetFilePath", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(store, new object[] { key }); // fault injection only, not verification
            public static string LegacyPathFor(EncryptedStorageService store, string key)
                => (string)typeof(EncryptedStorageService).GetMethod("GetLegacyFilePath", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(store, new object[] { key }); // fault injection only
            public void Block(EncryptedStorageService store, string key) => Directory.CreateDirectory(PathFor(store, key) + ".tmp");
            public void Close(EncryptedStorageService store, bool failed = false)
            {
                if (!_closed.Add(store)) return;
#if UNITY_EDITOR
                if (failed)
                    UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error,
                        new System.Text.RegularExpressions.Regex(@"\[EncryptedStorageService\] Disposed with .* final save could not write or delete"));
#endif
                store.Dispose();
            }
            public void Dispose()
            {
                foreach (var store in _stores) Close(store);
                string root = Path.GetFullPath(UnityEngine.Application.persistentDataPath) + Path.DirectorySeparatorChar;
                string folder = Path.GetFullPath(Path.Combine(root, "SecureData_" + _salt));
                Require(folder.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(folder) == "SecureData_" + _salt,
                    "cleanup must stay inside the unique fixture");
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                UnityEngine.PlayerPrefs.DeleteKey("NT_StorageSeed_" + _salt);
                UnityEngine.PlayerPrefs.Save();
            }
        }
    }
}
