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
        /// <summary>Saves all registered model state to persistent storage.</summary>
        Task SaveAsync(string slotName, CancellationToken ct = default);
        /// <summary>Loads and restores model state from persistent storage.</summary>
        Task<bool> LoadAsync(string slotName, CancellationToken ct = default);
        /// <summary>Returns true if a save exists for the given slot.</summary>
        bool SaveExists(string slotName);
        /// <summary>Deletes a save slot from persistent storage.</summary>
        void DeleteSave(string slotName);
    }

    /// <summary>
    /// Default implementation of <see cref="IGameSaveManager"/>.
    /// Writes JSON-encoded <see cref="GameSaveData"/> to the Unity persistent data path.
    /// </summary>
    [Preserve]
    public sealed class GameSaveManager : IGameSaveManager, IDisposable
    {
        private static readonly string SaveDirectory = Application.persistentDataPath + "/saves/";

        // Only one active model can participate in save/load.
        // For composite saves, register an aggregate root model.
        private ISaveDataProvider _model;

        /// <summary>Registers the model that provides save data.</summary>
        public void RegisterModel(ISaveDataProvider model)
        {
            _model = model;
        }

        public Task SaveAsync(string slotName, CancellationToken ct = default)
        {
            if (_model == null)
            {
                NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.LogWarning("[Nexus] No save model registered. Skipping save.");
                return Task.CompletedTask;
            }

            if (!Directory.Exists(SaveDirectory))
                Directory.CreateDirectory(SaveDirectory);

            var data = new GameSaveData
            {
                Version = Application.version,
                Timestamp = DateTime.UtcNow.ToString("O"),
                ModelData = _model.CaptureSaveData()
            };

            string path = SaveDirectory + SanitizeSlotName(slotName) + ".sav";

            // Write asynchronously to avoid main-thread stutter.
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                string json = JsonUtility.ToJson(data);
                File.WriteAllText(path, json);
            }, ct);
        }

        public Task<bool> LoadAsync(string slotName, CancellationToken ct = default)
        {
            if (_model == null)
            {
                NexusRuntime.CurrentContext?.Resolve<ILoggerService>()?.LogWarning("[Nexus] No save model registered. Skipping load.");
                return Task.FromResult(false);
            }

            string path = SaveDirectory + SanitizeSlotName(slotName) + ".sav";
            if (!File.Exists(path))
                return Task.FromResult(false);

            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                string json = File.ReadAllText(path);
                var data = JsonUtility.FromJson<GameSaveData>(json);
                if (data?.ModelData == null)
                    return false;

                _model.RestoreSaveData(data.ModelData);
                return true;
            }, ct);
        }

        public bool SaveExists(string slotName)
        {
            string path = SaveDirectory + SanitizeSlotName(slotName) + ".sav";
            return File.Exists(path);
        }

        public void DeleteSave(string slotName)
        {
            string path = SaveDirectory + SanitizeSlotName(slotName) + ".sav";
            if (File.Exists(path))
                File.Delete(path);
        }

        private static string SanitizeSlotName(string slotName)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                slotName = slotName.Replace(c, '_');
            return slotName;
        }

        public void Dispose()
        {
            _model = null;
        }
    }
}
