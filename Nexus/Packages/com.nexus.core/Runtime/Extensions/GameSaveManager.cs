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
    /// builder.BindService&lt;IGameSaveManager, GameSaveManager&gt;();
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
        /// <summary>Saves the registered model's state to persistent storage.</summary>
        Task SaveAsync(string slotName, CancellationToken ct = default);
        /// <summary>Loads and restores model state from persistent storage.</summary>
        Task<bool> LoadAsync(string slotName, CancellationToken ct = default);
        /// <summary>Returns true if a save exists for the given slot.
        /// Intentionally synchronous — File.Exists is a sub-millisecond metadata
        /// call, not payload I/O; converting it would break this interface's sync contract
        /// without a real main-thread blocking win.</summary>
        bool SaveExists(string slotName);
        /// <summary>Deletes a save slot from persistent storage.
        /// Intentionally synchronous metadata operation (same rationale as SaveExists).</summary>
        void DeleteSave(string slotName);
    }

    /// <summary>
    /// Default implementation of <see cref="IGameSaveManager"/>.
    /// Writes JSON-encoded <see cref="GameSaveData"/> to the Unity persistent data path.
    /// </summary>
    [Preserve]
    public sealed class GameSaveManager : IGameSaveManager, IDisposable
    {
        // For tests, allow overriding the persistent save directory. When null, the
        // implementation falls back to Application.persistentDataPath/saves.
        internal static string TestSaveDirectory { get; set; }
        private static string SaveDirectory => Path.Combine(TestSaveDirectory ?? Application.persistentDataPath, "saves");

        private volatile ISaveDataProvider _model;
        private SynchronizationContext _mainThreadContext;

        public GameSaveManager()
        {
            _mainThreadContext = SynchronizationContext.Current;
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
        // Retry jitter source, hoisted from the retry loop (a new Random per retry could
        // reseed identically under rapid retries). Only used under _saveLock.
        private readonly System.Random _retryJitter = new();

        /// <summary>Registers the model that provides save data.</summary>
        public void RegisterModel(ISaveDataProvider model)
        {
            var previous = _model;
            if (previous != null && !ReferenceEquals(previous, model))
            {
                NexusRuntime.Logger?.LogWarning(
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
                NexusRuntime.Logger?.LogWarning("[Nexus] No save model registered. Skipping save.");
                return;
            }

            ct.ThrowIfCancellationRequested();

            string dir = SaveDirectory;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            byte[] modelData = null;
            var ctx = MainThreadContext;
            if (ctx != null && SynchronizationContext.Current != ctx)
            {
                await RunOnCapturedContextAsync(() => modelData = model.CaptureSaveData(), ctx, ct);
            }
            else
            {
                modelData = model.CaptureSaveData();
            }

            var data = new GameSaveData
            {
                Version = Application.version,
                Timestamp = DateTime.UtcNow.ToString("O"),
                ModelData = modelData
            };

            string sanitized = SanitizeSlotName(slotName);
            string path = Path.Combine(dir, sanitized + ".sav");
            string tempPath = Path.Combine(dir, sanitized + ".sav.tmp");

            // Write atomically via temporary file to prevent save corruption on crash.
            // NOTE: the lambda is intentionally NOT async — the whole stage+rename+retry
            // sequence stays inside one _saveLock critical section. Releasing the lock
            // between attempts (to await the backoff) would let a concurrent SaveAsync on
            // the SAME slot interleave: a retrying thread could then overwrite the newer
            // concurrent save with its own stale data (silent lost update). Serializing the
            // full retry keeps retry-writes ordered; the cancellable backoff below blocks
            // only this worker thread during rare IO errors (disk busy, antivirus scan).
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                string json = JsonUtility.ToJson(data);
                lock (_saveLock)
                {
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
                                    NexusRuntime.Logger?.LogWarning($"[GameSaveManager] Failed to clean up temp file '{tempPath}': {cleanupEx.Message}");
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
            if (_model == null)
            {
                NexusRuntime.Logger?.LogWarning("[Nexus] No save model registered. Skipping load.");
                return Task.FromResult(false);
            }

            string path = Path.Combine(SaveDirectory, SanitizeSlotName(slotName) + ".sav");
            if (!File.Exists(path))
                return Task.FromResult(false);

            return LoadAndRestoreAsync(path, MainThreadContext, ct);
        }

        private async Task<bool> LoadAndRestoreAsync(string path, SynchronizationContext synchronizationContext, CancellationToken ct)
        {
            byte[] modelData = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string json = File.ReadAllText(path);
                    var data = JsonUtility.FromJson<GameSaveData>(json);
                    return data?.ModelData;
                }
                catch (Exception ex)
                {
                    NexusRuntime.Logger?.LogError($"[GameSaveManager] Failed to read save from '{path}': {ex.Message}");
                    return null;
                }
            }, ct);

            if (modelData == null)
                return false;

            ct.ThrowIfCancellationRequested();
            await RunOnCapturedContextAsync(
                () => _model.RestoreSaveData(modelData),
                synchronizationContext,
                ct);
            return true;
        }

        public bool SaveExists(string slotName)
        {
            ValidateSlotName(slotName);
            string path = Path.Combine(SaveDirectory, SanitizeSlotName(slotName) + ".sav");
            return File.Exists(path);
        }

        public void DeleteSave(string slotName)
        {
            ValidateSlotName(slotName);
            string path = Path.Combine(SaveDirectory, SanitizeSlotName(slotName) + ".sav");
            if (File.Exists(path))
                File.Delete(path);
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
