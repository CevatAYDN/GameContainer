using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Extensions
{
    /// <summary>
    /// Serializable container for a snapshot of model state at a given point.
    /// Models implement <see cref="ISaveDataProvider"/> to populate/extract this.
    /// </summary>
    [Serializable]
    [Preserve]
    public sealed class GameSaveData
    {
        public string Version;
        public string Timestamp;
        public byte[] ModelData; // Raw snapshot blob
    }

    /// <summary>
    /// Optional interface for models that participate in save/load.
    /// </summary>
    [Preserve]
    public interface ISaveDataProvider
    {
        /// <summary>Serializes the model state into a byte array.</summary>
        byte[] CaptureSaveData();
        /// <summary>Restores model state from a previously captured byte array.</summary>
        void RestoreSaveData(byte[] data);
    }

    /// <summary>
    /// Lightweight game save manager that bridges Nexus models to Unity's file I/O.
    ///
    /// Usage (bind in lifecycle):
    /// <code>
    /// builder.Bind&lt;IGameSaveManager, GameSaveManager&gt;();
    /// </code>
    ///
    /// Or inject and call manually:
    /// <code>
    /// [Inject] private IGameSaveManager _saveManager;
    /// await _saveManager.SaveAsync("autosave");
    /// </code>
    /// </summary>
    [Preserve]
    public interface IGameSaveManager
    {
        /// <summary>Registers the model that provides save data. A single model is held at
        /// a time; registering a new model replaces the previous one.</summary>
        void RegisterModel(ISaveDataProvider model);
        /// <summary>Saves the registered model's state to persistent storage. Saves of one slot land in call order:
        /// a snapshot is not written over a newer one.</summary>
        Task SaveAsync(string slotName, CancellationToken ct = default);
        /// <summary>Loads and restores model state from persistent storage. A save that stopped between deleting the
        /// old file and moving the staged one in is loaded from the staged file, which becomes the save file.</summary>
        Task<bool> LoadAsync(string slotName, CancellationToken ct = default);
        /// <summary>Returns true if a save exists for the given slot, the staged file of an interrupted save included.
        /// Intentionally synchronous — File.Exists is a sub-millisecond metadata
        /// call, not payload I/O; converting it would break this interface's sync contract
        /// without a real main-thread blocking win.</summary>
        bool SaveExists(string slotName);
        /// <summary>Deletes a save slot from persistent storage, its staged file included.
        /// Intentionally synchronous metadata operation (same rationale as SaveExists).</summary>
        void DeleteSave(string slotName);
    }

    /// <summary>
    /// Default implementation of <see cref="IGameSaveManager"/>.
    /// Writes JSON-encoded <see cref="GameSaveData"/> to the Unity persistent data path.
    /// Every method may be called from a worker thread when the manager was constructed on the main
    /// thread (it reads Unity's main-thread-only paths there). Constructed elsewhere, the first
    /// <see cref="SaveAsync"/> reads them on its main-thread hop; until then <see cref="SaveExists"/>,
    /// <see cref="DeleteSave"/> and <see cref="LoadAsync"/> must be called on the main thread.
    /// </summary>
    [Preserve]
    public sealed class GameSaveManager : IGameSaveManager, IDisposable
    {
        // For tests, allow overriding the persistent save directory. When null, the
        // implementation falls back to Application.persistentDataPath/saves.
        internal static string TestSaveDirectory { get; set; }
        private string SaveDirectory => Path.Combine(TestSaveDirectory ?? _persistentDataPath ?? Application.persistentDataPath, "saves");

        private volatile ISaveDataProvider _model;
        private SynchronizationContext _mainThreadContext;
        // Unity's Application properties are main-thread only; read once there (constructor or SaveAsync's
        // main-thread hop) so worker-thread saves, loads and slot checks never touch them.
        private volatile string _persistentDataPath;
        private volatile string _version;

        public GameSaveManager()
        {
            _mainThreadContext = SynchronizationContext.Current;
            if (_mainThreadContext != null) CaptureMainThreadValues();
        }

        private void CaptureMainThreadValues()
        {
            _persistentDataPath ??= Application.persistentDataPath;
            _version ??= Application.version;
        }

        private SynchronizationContext MainThreadContext
        {
            get
            {
                if (_mainThreadContext == null && SynchronizationContext.Current != null)
                {
                    _mainThreadContext = SynchronizationContext.Current;
                }
                return _mainThreadContext;
            }
        }

        private readonly object _saveLock = new();
        // Issue #119: each capture takes the next number (captures run on the main thread, in call order); a write whose
        // slot already holds a later number is skipped. DeleteSave takes a number too (#296). Only used under _saveLock,
        // except the increment.
        private long _captureSequence;
        private readonly System.Collections.Generic.Dictionary<string, long> _writtenSequence = new();
        // Retry jitter source, hoisted from the retry loop (a new Random per retry could
        // reseed identically under rapid retries). Only used under _saveLock.
        private readonly System.Random _retryJitter = new();

        /// <summary>Registers the model that provides save data.</summary>
        public void RegisterModel(ISaveDataProvider model)
        {
            var previous = _model;
            if (previous != null && !ReferenceEquals(previous, model))
            {
                Services.NexusLog.WarnAlways(
                    "[GameSaveManager] RegisterModel replaced a previously registered model — only ONE model is held at a time; the earlier model no longer participates in save/load.");
            }
            _model = model;
        }

        public async Task SaveAsync(string slotName, CancellationToken ct = default)
        {
            ValidateSlotName(slotName);
            var model = _model;
            if (model == null)
            {
                Services.NexusLog.WarnAlways("[Nexus] No save model registered. Skipping save.");
                return;
            }

            ct.ThrowIfCancellationRequested();

            byte[] modelData = null;
            long sequence = 0;
            var ctx = MainThreadContext;
            if (ctx != null && SynchronizationContext.Current != ctx)
            {
                await RunOnCapturedContextAsync(() => { CaptureMainThreadValues(); modelData = model.CaptureSaveData(); sequence = Interlocked.Increment(ref _captureSequence); }, ctx, ct);
            }
            else
            {
                CaptureMainThreadValues();
                modelData = model.CaptureSaveData();
                sequence = Interlocked.Increment(ref _captureSequence);
            }

            string dir = SaveDirectory;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new GameSaveData
            {
                Version = _version,
                Timestamp = DateTime.UtcNow.ToString("O"),
                ModelData = modelData
            };

            string sanitized = SanitizeSlotName(slotName);
            string path = Path.Combine(dir, sanitized + ".sav");
            string tempPath = Path.Combine(dir, sanitized + ".sav.tmp");

            // Write atomically via temporary file to prevent save corruption on crash.
            // NOTE: the lambda is intentionally NOT async — the whole stage+rename+retry
            // sequence stays inside one _saveLock critical section, so one save's attempts never
            // interleave with another save's. The lock does not order separate saves: their
            // Task.Run bodies reach it in any order, so the capture sequence does (a write whose
            // slot already holds a later snapshot is skipped, issue #119). The cancellable backoff
            // below blocks only this worker thread during rare IO errors (disk busy, antivirus scan).
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                string json = JsonUtility.ToJson(data);
                lock (_saveLock)
                {
                    if (_writtenSequence.TryGetValue(path, out long written) && written > sequence)
                        return; // a later snapshot of this slot is already on disk
                    // Stage + rename must be one critical section (see _saveLock).
                    // Add robust retry logic with exponential backoff to improve resilience
                    // against transient I/O errors (disk busy, antivirus scan, etc.).
                    int attempt = 0;
                    const int maxAttempts = 3;
                    while (true)
                    {
                        try
                        {
                            File.WriteAllText(tempPath, json);
                            // Single overwrite-rename, never Delete-then-Move.
                            // The File.Exists check and File.Replace were a TOCTOU
                            // pair — the target could vanish between them (Replace then throws
                            // FileNotFoundException). Catch that specific case and retry as Move.
                            if (File.Exists(path))
                            {
                                try
                                {
                                    File.Replace(tempPath, path, null);
                                }
                                catch (FileNotFoundException)
                                {
                                    // Target disappeared between the check and Replace — the
                                    // staged file is still intact; fall back to a plain move.
                                    if (!File.Exists(tempPath))
                                        File.WriteAllText(tempPath, json); // restage if Replace consumed it
                                    File.Move(tempPath, path);
                                }
                                catch (Exception ex) when (ex is PlatformNotSupportedException or NotImplementedException)
                                {
                                    // File.Replace is not implemented on some IL2CPP/mobile
                                    // runtimes — degrade to delete+move instead of failing
                                    // every save on those platforms.
                                    NexusRuntime.Logger?.LogWarning(
                                        "[GameSaveManager] File.Replace not supported on this platform; falling back to delete+move (non-atomic).");
                                    if (File.Exists(path)) File.Delete(path);
                                    File.Move(tempPath, path);
                                }
                            }
                            else
                            {
                                File.Move(tempPath, path);
                            }
                            _writtenSequence[path] = sequence;
                            break; // success
                        }
                        catch (Exception ex)
                        {
                            attempt++;
                            NexusRuntime.Logger?.LogError($"[GameSaveManager] Save attempt {attempt} for '{slotName}' failed: {ex.Message}");
                            if (attempt >= maxAttempts)
                            {
                                // Never abandon the staged .tmp file — a stranded
                                // temp file would sit next to the save forever and the next save
                                // silently overwrites it, masking the original failure. Best-effort
                                // cleanup; the exception still propagates to the caller.
                                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                                catch (Exception cleanupEx)
                                {
                                    Services.NexusLog.WarnAlways($"[GameSaveManager] Failed to clean up temp file '{tempPath}': {cleanupEx.Message}");
                                }
                                // Give up and surface the error to the caller via exception
                                throw;
                            }
                            // Exponential backoff with jitter (cancellable via ct). Kept
                            // synchronous and under _saveLock — see the NOTE above.
                            var backoffMs = (int)(50 * Math.Pow(2, attempt - 1)) + _retryJitter.Next(0, 50);
                            Task.Delay(backoffMs, ct).GetAwaiter().GetResult(); // NEXUS003-exempt: serialized retry backoff — releasing _saveLock between attempts risks clobbering concurrent same-slot saves
                        }
                    }
                }
            }, ct);
        }

        public Task<bool> LoadAsync(string slotName, CancellationToken ct = default)
        {
            ValidateSlotName(slotName);
            var model = _model;
            if (model == null)
            {
                Services.NexusLog.WarnAlways("[Nexus] No save model registered. Skipping load.");
                return Task.FromResult(false);
            }

            string path = Path.Combine(SaveDirectory, SanitizeSlotName(slotName) + ".sav");
            if (!File.Exists(path) && !File.Exists(path + ".tmp"))
                return Task.FromResult(false);

            return LoadAndRestoreAsync(model, slotName, path, MainThreadContext, ct);
        }

        private async Task<bool> LoadAndRestoreAsync(ISaveDataProvider model, string slotName, string path, SynchronizationContext synchronizationContext, CancellationToken ct)
        {
            byte[] modelData = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                byte[] saved = File.Exists(path) ? ReadSave(path) : null;
                if (saved != null) return saved;
                // Issue #119: where File.Replace is missing, a save deletes the old file before it moves the staged one
                // in, and a crash between the two leaves only the staged file. Read and promote it under the save lock,
                // where no save is half-written.
                string tempPath = path + ".tmp";
                lock (_saveLock)
                {
                    if (!File.Exists(tempPath)) return null;
                    byte[] staged = ReadSave(tempPath);
                    if (staged == null) return null;
                    try
                    {
                        if (File.Exists(path)) File.Delete(path);
                        File.Move(tempPath, path);
                    }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogError($"[GameSaveManager] Slot '{slotName}' was read from its staged file, which could not become the save file: {ex.Message}");
                    }
                    Services.NexusLog.WarnAlways($"[GameSaveManager] Slot '{slotName}' was restored from its staged file: the save that wrote it stopped before it replaced the save file.");
                    return staged;
                }
            }, ct);

            if (modelData == null)
                return false;

            ct.ThrowIfCancellationRequested();
            await RunOnCapturedContextAsync(
                () => model.RestoreSaveData(modelData),
                synchronizationContext,
                ct);
            return true;
        }

        private static byte[] ReadSave(string file)
        {
            try
            {
                return JsonUtility.FromJson<GameSaveData>(File.ReadAllText(file))?.ModelData;
            }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogError($"[GameSaveManager] Failed to read save from '{file}': {ex.Message}");
                return null;
            }
        }

        public bool SaveExists(string slotName)
        {
            ValidateSlotName(slotName);
            string path = Path.Combine(SaveDirectory, SanitizeSlotName(slotName) + ".sav");
            return File.Exists(path) || File.Exists(path + ".tmp");
        }

        public void DeleteSave(string slotName)
        {
            ValidateSlotName(slotName);
            string path = Path.Combine(SaveDirectory, SanitizeSlotName(slotName) + ".sav");
            lock (_saveLock) // a save mid-write keeps its staged file until it is done
            {
                // The delete takes the next capture number like a save (#119): a save captured before it is skipped instead
                // of writing the slot back after the delete, and a save captured after it still writes (issue #296).
                _writtenSequence[path] = Interlocked.Increment(ref _captureSequence);
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            }
        }

        private static string SanitizeSlotName(string slotName)
        {
            slotName = Path.GetFileName(slotName);
            foreach (char c in Path.GetInvalidFileNameChars())
                slotName = slotName.Replace(c, '_');
            return slotName;
        }

        private static void ValidateSlotName(string slotName)
        {
            if (string.IsNullOrWhiteSpace(slotName) || slotName == "." || slotName == ".." || slotName.Contains("/") || slotName.Contains("\\"))
                throw new ArgumentException("Save slot name must be a non-empty filename without path characters.", nameof(slotName));
        }

        private static Task RunOnCapturedContextAsync(Action action, SynchronizationContext synchronizationContext, CancellationToken ct)
        {
            if (synchronizationContext == null)
            {
                action();
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Hang guard: if the posted callback is never executed — the Unity
            // SynchronizationContext stops processing during teardown/quit — the await
            // below would block forever. Propagate ct (the context LifetimeToken is
            // cancelled on dispose) directly to the TCS so a torn-down context unblocks
            // the caller as a cancellation instead of hanging. The registration is
            // disposed once the posted callback runs (or if Post throws); a dead
            // context leaves it registered until the token source is disposed, which is
            // exactly the window it exists to protect.
            CancellationTokenRegistration reg = default;
            reg = ct.Register(() => completion.TrySetCanceled(ct));
            try
            {
                synchronizationContext.Post(_ =>
                {
                    reg.Dispose();
                    if (ct.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(ct);
                        return;
                    }

                    try
                    {
                        action();
                        completion.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                }, null);
            }
            catch
            {
                reg.Dispose();
                throw;
            }
            return completion.Task;
        }

        public void Dispose()
        {
            _model = null;
        }
    }
}
