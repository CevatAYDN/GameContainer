using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Nexus.Core;
using Nexus.Core.Services;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class FullReviewStorageProgressionTests
    {
        private const string CurrentKey = "NT_Prog_CurrentLevel";
        private const string MaximumKey = "NT_Prog_MaxLevel";

#if UNITY_EDITOR
        [Test]
#endif
        public void ST01_UnreadableProgression_DoesNotOverwriteSavedLevels()
        {
            var store = new InMemoryPlayerPrefsService();
            store.SetInt(CurrentKey, 50);
            store.SetInt(MaximumKey, 80);
            store.SaveOrThrow();
            store.MarkUnreadable(CurrentKey);
            store.MarkUnreadable(MaximumKey);
            var throttler = new SaveThrottler { TimeProvider = new FrozenClock() };
            var progression = new ProgressionService
            {
                PlayerPrefsService = store,
                SaveThrottler = throttler
            };
            bool initializationRefused = false;
            bool mutationRefused = false;
            try
            {
                try { progression.InitializeAsync(default).GetAwaiter().GetResult(); }
                catch (IOException) { initializationRefused = true; }
                try { progression.CompleteCurrentLevel(); }
                catch (IOException) { mutationRefused = true; }
            }
            finally
            {
                progression.Dispose();
                throttler.OnDispose();
            }

            var reopened = store.Reopen();
            bool faultsPreserved = reopened.TryGetString(CurrentKey, out _) == StorageReadStatus.Unreadable
                && reopened.TryGetString(MaximumKey, out _) == StorageReadStatus.Unreadable;
            reopened.Restore(CurrentKey);
            reopened.Restore(MaximumKey);
            int current = reopened.GetInt(CurrentKey, -1);
            int maximum = reopened.GetInt(MaximumKey, -1);
            Require(initializationRefused && mutationRefused && faultsPreserved && current == 50 && maximum == 80,
                $"Unreadable progression must remain recoverable: initializationRefused={initializationRefused}, "
                + $"mutationRefused={mutationRefused}, faultsPreserved={faultsPreserved}, durable={current}/{maximum}; expected 50/80.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ST01_MalformedMaximum_PreservesBothSavedKeys()
        {
            var store = new InMemoryPlayerPrefsService();
            store.SetInt(CurrentKey, 50);
            store.SetString(MaximumKey, "invalid-level");
            store.SaveOrThrow();
            var progression = new ProgressionService { PlayerPrefsService = store };
            int notifications = 0;
            progression.CurrentLevel.OnChanged((_, __) => notifications++);
            try
            {
                ExpectIOException(() => progression.InitializeAsync(default).GetAwaiter().GetResult());
                ExpectIOException(() => progression.SetLevel(1));
            }
            finally { progression.Dispose(); }

            var reopened = store.Reopen();
            Require(notifications == 0 && reopened.GetInt(CurrentKey) == 50
                && reopened.GetString(MaximumKey) == "invalid-level",
                "A failed second-key read must neither publish a half-loaded state nor overwrite either saved key.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ST01_Recovery_RetriesInitializationOnTheSameService()
        {
            var store = new InMemoryPlayerPrefsService();
            store.SetInt(CurrentKey, 50);
            store.SetInt(MaximumKey, 80);
            store.SaveOrThrow();
            store.MarkUnreadable(CurrentKey);
            using var progression = new ProgressionService { PlayerPrefsService = store };
            ExpectIOException(() => progression.InitializeAsync(default).GetAwaiter().GetResult());
            ExpectIOException(() => progression.SetLevel(1));
            store.Restore(CurrentKey);
            progression.InitializeAsync(default).GetAwaiter().GetResult();
            Require(progression.CurrentLevel.Value == 50 && progression.MaxUnlockedLevel.Value == 80,
                "Explicit retry after recovery must load the original native typed values.");
            progression.CompleteCurrentLevel();
            var reopened = store.Reopen();
            Require(reopened.GetInt(CurrentKey) == 51 && reopened.GetInt(MaximumKey) == 80,
                "Recovered progression must resume normal durable mutations.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ST01_AbsentAndLegacyNegativeLevels_KeepTheirDefaults()
        {
            foreach (bool storedNegative in new[] { false, true })
            {
                var store = new InMemoryPlayerPrefsService();
                if (storedNegative)
                {
                    store.SetInt(CurrentKey, int.MinValue);
                    store.SetInt(MaximumKey, -1);
                    store.SaveOrThrow();
                }
                using var progression = new ProgressionService { PlayerPrefsService = store };
                progression.InitializeAsync(default).GetAwaiter().GetResult();
                Require(progression.CurrentLevel.Value == 1 && progression.MaxUnlockedLevel.Value == 1,
                    "Absent data and valid legacy negatives must retain the existing level-one default.");
                progression.CompleteCurrentLevel();
                Require(store.Reopen().GetInt(CurrentKey) == 2 && store.Reopen().GetInt(MaximumKey) == 2,
                    "A healthy default must remain writable.");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ST03_DisposedOwnerFlush_CannotOverwriteReplacementLevels()
        {
            var store = new InMemoryPlayerPrefsService();
            var throttler = new SaveThrottler { TimeProvider = new FrozenClock() };
            var old = new ProgressionService { PlayerPrefsService = store, SaveThrottler = throttler };
            old.InitializeAsync(default).GetAwaiter().GetResult();
            using var replacement = new ProgressionService { PlayerPrefsService = store };
            int afterFlush, afterThrottlerDispose, afterRepeatedDispose;
            try
            {
                old.SetLevel(10);
                old.SetLevel(11);
                old.Dispose();
                Require(store.Reopen().GetInt(CurrentKey) == 11,
                    "Disposal must commit the old owner's pending final level before closing it.");
                replacement.InitializeAsync(default).GetAwaiter().GetResult();
                replacement.SetLevel(20);
                throttler.Flush();
                afterFlush = store.Reopen().GetInt(CurrentKey);
                replacement.SetLevel(21);
                throttler.OnDispose();
                afterThrottlerDispose = store.Reopen().GetInt(CurrentKey);
                replacement.SetLevel(22);
                old.Dispose();
                afterRepeatedDispose = store.Reopen().GetInt(CurrentKey);
            }
            finally
            {
                old.Dispose();
                throttler.OnDispose();
            }
            Require(afterFlush == 20 && afterThrottlerDispose == 21 && afterRepeatedDispose == 22,
                $"A retired progression owner must never overwrite its replacement: flush={afterFlush}, "
                + $"throttlerDispose={afterThrottlerDispose}, repeatedDispose={afterRepeatedDispose}; expected 20/21/22.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ST03_DisposedOwner_RejectsLateMutationAndInitialization()
        {
            var store = new InMemoryPlayerPrefsService();
            var progression = new ProgressionService { PlayerPrefsService = store };
            progression.InitializeAsync(default).GetAwaiter().GetResult();
            progression.SetLevel(10);
            progression.Dispose();
            ExpectDisposed(() => progression.SetLevel(20));
            ExpectDisposed(progression.CompleteCurrentLevel);
            ExpectDisposed(() => progression.InitializeAsync(default).GetAwaiter().GetResult());
            Require(progression.CurrentLevel.Value == 10 && store.Reopen().GetInt(CurrentKey) == 10,
                "A disposed progression owner must not change its view or its saved state.");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ST03_ObserverDispose_PersistsAcceptedLevelBeforeClosingOwner()
        {
            var failures = new List<string>();
            foreach (bool throttled in new[] { false, true })
                foreach (bool maximumObserver in new[] { false, true })
                {
                    var store = new InMemoryPlayerPrefsService();
                    store.SetInt(CurrentKey, 10);
                    store.SetInt(MaximumKey, 10);
                    store.SaveOrThrow();
                    var throttler = throttled ? new SaveThrottler { TimeProvider = new FrozenClock() } : null;
                    var progression = new ProgressionService { PlayerPrefsService = store, SaveThrottler = throttler };
                    progression.InitializeAsync(default).GetAwaiter().GetResult();
                    bool rejected = false;
                    var observed = maximumObserver ? progression.MaxUnlockedLevel : progression.CurrentLevel;
                    observed.OnChanged((_, level) =>
                    {
                        if (level != 11) return;
                        progression.Dispose();
                        try { progression.SetLevel(99); }
                        catch (ObjectDisposedException) { rejected = true; }
                    });
                    try
                    {
                        progression.CompleteCurrentLevel();
                        throttler?.Flush();
                        var reopened = store.Reopen();
                        int current = reopened.GetInt(CurrentKey), maximum = reopened.GetInt(MaximumKey);
                        if (!rejected || current != 11 || maximum != 11
                            || progression.CurrentLevel.Value != 11 || progression.MaxUnlockedLevel.Value != 11)
                            failures.Add($"throttled={throttled}, maximumObserver={maximumObserver}, "
                                + $"live={progression.CurrentLevel.Value}/{progression.MaxUnlockedLevel.Value}, "
                                + $"durable={current}/{maximum}, rejected={rejected}");
                    }
                    finally
                    {
                        progression.Dispose();
                        throttler?.OnDispose();
                    }
                }
            Require(failures.Count == 0,
                "Observer disposal must finish the accepted level tuple and reject later work: " + string.Join("; ", failures));
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void ST03_DisposeFinalSave_AllowsAStoreCallbackToObserveShutdown()
        {
            const string faultKey = "blocked-write";
            using var context = new Context();
            var logger = new CallbackLogger();
            context.Container.BindInstance<ILoggerService>(logger);
            Require(ReferenceEquals(NexusRuntime.Logger, logger), "The fixture must own its warning callback.");
            var files = new EncryptedFiles();
            var warningEntered = new ManualResetEventSlim();
            var finalSaveEntered = new ManualResetEventSlim();
            var reentryEntered = new ManualResetEventSlim();
            var reentryDone = new ManualResetEventSlim();
            var store = files.Open();
            store.SetString(faultKey, "old");
            store.SaveOrThrow();
            string[] initialFiles = Directory.GetFiles(files.Folder, "*.dat");
            Require(initialFiles.Length == 1, "The fixture needs one fault-injection target.");
            string obstruction = files.CheckedPath(initialFiles[0] + ".tmp");
            store.SetInt(CurrentKey, 10);
            store.SetInt(MaximumKey, 10);
            store.SaveOrThrow();
            var forwarding = new CallbackStore(store);
            var throttler = new SaveThrottler { TimeProvider = new FrozenClock() };
            var progression = new ProgressionService { PlayerPrefsService = forwarding, SaveThrottler = throttler };
            progression.InitializeAsync(default).GetAwaiter().GetResult();
            progression.SetLevel(11);
            progression.SetLevel(12);
            Directory.CreateDirectory(obstruction);
            store.SetString(faultKey, "new");

            Exception writerFailure = null, disposerFailure = null, callbackFailure = null;
            string setupFailure = null;
            bool callbackCompletedInGate = false, callbackRejected = false;
            int warningCount = 0;
            Thread callbackThread = null;
            forwarding.BeforeSave = () => finalSaveEntered.Set();
            logger.Warning = message =>
            {
                if (!message.Contains(faultKey) || Interlocked.Increment(ref warningCount) != 1) return;
                warningEntered.Set();
                if (!finalSaveEntered.Wait(TimeSpan.FromSeconds(5)))
                {
                    setupFailure = "Disposal never reached its external save.";
                    return;
                }
                callbackThread = new Thread(() =>
                {
                    try
                    {
                        reentryEntered.Set();
                        progression.SetLevel(99);
                    }
                    catch (ObjectDisposedException) { callbackRejected = true; }
                    catch (Exception ex) { callbackFailure = ex; }
                    finally { reentryDone.Set(); }
                }) { IsBackground = true, Name = "Storage404 callback" };
                callbackThread.Start();
                if (!reentryEntered.Wait(TimeSpan.FromSeconds(5)))
                    setupFailure = "The callback worker did not start.";
                else
                    callbackCompletedInGate = reentryDone.Wait(TimeSpan.FromSeconds(2));
                // Release the writer even on failure. The timeout records the lock cycle
                // without leaving the host or a later native test blocked indefinitely.
            };
            var writer = new Thread(() =>
            {
                try { store.Save(); }
                catch (Exception ex) { writerFailure = ex; }
            }) { IsBackground = true, Name = "Storage404 writer" };
            var disposer = new Thread(() =>
            {
                try { progression.Dispose(); }
                catch (Exception ex) { disposerFailure = ex; }
            }) { IsBackground = true, Name = "Storage404 dispose" };
            bool disposerStarted = false, workersStopped = false;
            try
            {
                writer.Start();
                bool warningReached = warningEntered.Wait(TimeSpan.FromSeconds(5));
                if (warningReached)
                {
                    disposer.Start();
                    disposerStarted = true;
                }
                else setupFailure = "The real encrypted write never reached its failure warning.";

                bool writerStopped = writer.Join(TimeSpan.FromSeconds(15));
                bool disposerStopped = !disposerStarted || disposer.Join(TimeSpan.FromSeconds(10));
                bool callbackStopped = writerStopped && (callbackThread == null || callbackThread.Join(TimeSpan.FromSeconds(5)));
                workersStopped = writerStopped && disposerStopped && callbackStopped;
                string evidence = $"warningReached={warningReached}, finalSaveReached={finalSaveEntered.IsSet}, "
                    + $"reentryStarted={reentryEntered.IsSet}, callbackCompletedWithin2s={callbackCompletedInGate}, "
                    + $"callbackRejected={callbackRejected}, workersStopped={workersStopped}, "
                    + $"setupFailure={setupFailure ?? "none"}, writerFailure={writerFailure}, "
                    + $"disposerFailure={disposerFailure}, callbackFailure={callbackFailure}";
                Console.WriteLine("[Storage404 bounded shutdown] " + evidence);
                Require(setupFailure == null && workersStopped && callbackCompletedInGate && callbackRejected
                    && writerFailure == null && disposerFailure == null && callbackFailure == null,
                    "Final disposal must release the level lock before external I/O. " + evidence);

                Directory.Delete(files.CheckedPath(obstruction));
                store.SaveOrThrow();
                store.Dispose();
                var reopened = files.Open();
                Require(reopened.GetInt(CurrentKey) == 12 && reopened.GetInt(MaximumKey) == 12,
                    "The final snapshot must persist 12/12 after rejecting the store callback's late mutation.");
            }
            finally
            {
                if (workersStopped)
                {
                    logger.Warning = null;
                    forwarding.BeforeSave = null;
                    if (Directory.Exists(obstruction)) Directory.Delete(files.CheckedPath(obstruction));
                    progression.Dispose();
                    throttler.OnDispose();
                    files.Dispose();
                    warningEntered.Dispose();
                    finalSaveEntered.Dispose();
                    reentryEntered.Dispose();
                    reentryDone.Dispose();
                }
                else
                {
                    Console.WriteLine("[Storage404 bounded shutdown] Background workers did not stop; skipping blocking cleanup. Fixture: " + files.Folder);
                }
            }
        }

        private static void ExpectDisposed(Action action)
        {
            try { action(); }
            catch (ObjectDisposedException) { return; }
            throw new InvalidOperationException("Expected a retired progression owner to reject the operation.");
        }

        private static void ExpectIOException(Action action)
        {
            try { action(); }
            catch (IOException) { return; }
            throw new InvalidOperationException("Expected unavailable progression to throw IOException.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class FrozenClock : ITimeProvider
        {
            public float Now => 0;
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

        private sealed class CallbackStore : IPlayerPrefsService
        {
            private readonly IPlayerPrefsService _inner;
            public Action BeforeSave;
            public CallbackStore(IPlayerPrefsService inner) => _inner = inner;
            public int GetInt(string key, int defaultValue = 0) => _inner.GetInt(key, defaultValue);
            public void SetInt(string key, int value) => _inner.SetInt(key, value);
            public bool GetBool(string key, bool defaultValue = false) => _inner.GetBool(key, defaultValue);
            public void SetBool(string key, bool value) => _inner.SetBool(key, value);
            public string GetString(string key, string defaultValue = "") => _inner.GetString(key, defaultValue);
            public void SetString(string key, string value) => _inner.SetString(key, value);
            public float GetFloat(string key, float defaultValue = 0) => _inner.GetFloat(key, defaultValue);
            public void SetFloat(string key, float value) => _inner.SetFloat(key, value);
            public long GetLong(string key, long defaultValue = 0) => _inner.GetLong(key, defaultValue);
            public void SetLong(string key, long value) => _inner.SetLong(key, value);
            public StorageReadStatus TryGetString(string key, out string value) => _inner.TryGetString(key, out value);
            public bool HasKey(string key) => _inner.HasKey(key);
            public void DeleteKey(string key) => _inner.DeleteKey(key);
            public void Save()
            {
                BeforeSave?.Invoke();
                _inner.Save();
            }
            public bool TrySave() => _inner.TrySave();
        }

        private sealed class EncryptedFiles : IDisposable
        {
            private readonly string _salt = "fullreview_progression_shutdown_" + Guid.NewGuid().ToString("N");
            private readonly List<EncryptedStorageService> _stores = new();
            public string Folder { get; }
            public EncryptedFiles()
            {
                Folder = Path.GetFullPath(Path.Combine(UnityEngine.Application.persistentDataPath, "SecureData_" + _salt));
                CheckFolder();
            }
            public EncryptedStorageService Open()
            {
                var store = new EncryptedStorageService(_salt);
                _stores.Add(store);
                return store;
            }
            public string CheckedPath(string path)
            {
                string full = Path.GetFullPath(path);
                Require(full.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    "File operations must stay inside the unique fixture.");
                return full;
            }
            private void CheckFolder()
            {
                string root = Path.GetFullPath(UnityEngine.Application.persistentDataPath);
                Require(string.Equals(Path.GetDirectoryName(Folder), root, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(Folder) == "SecureData_" + _salt, "Cleanup must stay inside the unique fixture.");
            }
            public void Dispose()
            {
                foreach (var store in _stores) store.Dispose();
                CheckFolder();
                if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
                UnityEngine.PlayerPrefs.DeleteKey("NT_StorageSeed_" + _salt);
                UnityEngine.PlayerPrefs.Save();
            }
        }
    }
}
