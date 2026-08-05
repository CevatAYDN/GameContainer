using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Scripting;

namespace Nexus.Core.Extensions
{
    /// <summary>
    /// Signals fired by the SceneLoader during scene transitions.
    /// Subscribe to these in mediators or commands to react to scene events.
    /// </summary>
    public readonly struct SceneLoadingSignal { public readonly string SceneName; public SceneLoadingSignal(string name) => SceneName = name; }
    public readonly struct SceneLoadedSignal { public readonly string SceneName; public SceneLoadedSignal(string name) => SceneName = name; }
    public readonly struct SceneUnloadedSignal { public readonly string SceneName; public SceneUnloadedSignal(string name) => SceneName = name; }
    /// <summary>Terminal signal for a load that did NOT complete (scene missing, exception,
    /// or cancellation). Loading UI listening to <see cref="SceneLoadingSignal"/> must
    /// subscribe to this as well as <see cref="SceneLoadedSignal"/> so it never waits forever.</summary>
    public readonly struct SceneLoadFailedSignal
    {
        public readonly string SceneName;
        public readonly string Error;
        public SceneLoadFailedSignal(string name, string error) { SceneName = name; Error = error; }
    }

    /// <summary>
    /// Service interface for Nexus-driven scene management.
    /// Integrates with the Nexus signal bus so mediators and commands
    /// can react to scene lifecycle events.
    /// </summary>
    [Preserve]
    public interface ISceneLoader
    {
        /// <summary>Loads a scene additively and fires SceneLoadedSignal on completion.</summary>
        Task LoadSceneAsync(string sceneName, LoadSceneMode mode = LoadSceneMode.Additive, CancellationToken ct = default);
        /// <summary>Unloads a scene and fires SceneUnloadedSignal.</summary>
        Task UnloadSceneAsync(string sceneName, CancellationToken ct = default);
        /// <summary>Activates a loaded scene as the active scene.</summary>
        void SetActiveScene(string sceneName);
    }

    /// <summary>
    /// Default Nexus scene loader. Fires Nexus signals on scene events.
    /// Bind in lifecycle:
    /// <code>builder.BindService&lt;ISceneLoader, SceneLoader&gt;();</code>
    /// </summary>
    [Preserve]
    public sealed class SceneLoader : ISceneLoader
    {
        private readonly ISignalBus _signalBus;
        private readonly HashSet<string> _loadingScenes = new();
        private readonly HashSet<string> _unloadingScenes = new();
        private readonly object _loadingLock = new();

        public SceneLoader(ISignalBus signalBus)
        {
            _signalBus = signalBus ?? throw new ArgumentNullException(nameof(signalBus));
        }

        public async Task LoadSceneAsync(string sceneName, LoadSceneMode mode = LoadSceneMode.Additive, CancellationToken ct = default)
        {
            lock (_loadingLock)
            {
                if (!_loadingScenes.Add(sceneName))
                {
                    NexusRuntime.Logger?.LogWarning($"[Nexus] Scene '{sceneName}' is already being loaded.");
                    return;
                }
            }

            try
            {
                _signalBus.Fire(new SceneLoadingSignal(sceneName));

                var op = SceneManager.LoadSceneAsync(sceneName, mode);
                if (op == null)
                {
                    NexusRuntime.Logger?.LogError($"[Nexus] Scene '{sceneName}' not found in build settings.");
                    // Terminal signal: loading UI must never wait forever on a failed load.
                    _signalBus.Fire(new SceneLoadFailedSignal(sceneName, "Scene not found in build settings."));
                    return;
                }

                op.allowSceneActivation = true;

                while (!op.isDone)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                }

                _signalBus.Fire(new SceneLoadedSignal(sceneName));
            }
            catch (Exception ex)
            {
                // Terminal signal on exception/cancellation paths too (see above).
                _signalBus.Fire(new SceneLoadFailedSignal(sceneName, ex.Message));
                throw;
            }
            finally
            {
                lock (_loadingLock)
                {
                    _loadingScenes.Remove(sceneName);
                }
            }
        }

        public async Task UnloadSceneAsync(string sceneName, CancellationToken ct = default)
        {
            // Duplicate-unload guard, matching LoadSceneAsync's duplicate-load guard.
            lock (_loadingLock)
            {
                if (!_unloadingScenes.Add(sceneName))
                {
                    NexusRuntime.Logger?.LogWarning($"[Nexus] Scene '{sceneName}' is already being unloaded.");
                    return;
                }
            }

            try
            {
                var op = SceneManager.UnloadSceneAsync(sceneName);
                if (op == null)
                {
                    NexusRuntime.Logger?.LogWarning($"[Nexus] Scene '{sceneName}' is not loaded or cannot be unloaded.");
                    return;
                }

                while (!op.isDone)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                }

                _signalBus.Fire(new SceneUnloadedSignal(sceneName));
            }
            finally
            {
                lock (_loadingLock)
                {
                    _unloadingScenes.Remove(sceneName);
                }
            }
        }

        public void SetActiveScene(string sceneName)
        {
            var scene = SceneManager.GetSceneByName(sceneName);
            if (scene.IsValid() && scene.isLoaded)
            {
                SceneManager.SetActiveScene(scene);
            }
            else
            {
                NexusRuntime.Logger?.LogWarning($"[Nexus] Cannot set active scene '{sceneName}': not loaded or invalid.");
            }
        }
    }
}
