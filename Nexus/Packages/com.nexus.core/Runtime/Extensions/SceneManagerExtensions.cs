using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

        public SceneLoader(ISignalBus signalBus)
        {
            _signalBus = signalBus;
        }

        public async Task LoadSceneAsync(string sceneName, LoadSceneMode mode = LoadSceneMode.Additive, CancellationToken ct = default)
        {
            if (_loadingScenes.Contains(sceneName)) return;
            _loadingScenes.Add(sceneName);

            try
            {
                _signalBus.Fire(new SceneLoadingSignal(sceneName));

                var op = SceneManager.LoadSceneAsync(sceneName, mode);
                if (op == null)
                {
                    Debug.LogError($"[Nexus] Scene '{sceneName}' not found in build settings.");
                    return;
                }

                op.allowSceneActivation = true;

                while (!op.isDone)
                {
                    ct.ThrowIfCancellationRequested();
                    await Awaitable.NextFrameAsync();
                }

                _signalBus.Fire(new SceneLoadedSignal(sceneName));
            }
            finally
            {
                _loadingScenes.Remove(sceneName);
            }
        }

        public async Task UnloadSceneAsync(string sceneName, CancellationToken ct = default)
        {
            var op = SceneManager.UnloadSceneAsync(sceneName);
            if (op == null)
            {
                Debug.LogWarning($"[Nexus] Scene '{sceneName}' is not loaded or cannot be unloaded.");
                return;
            }

            while (!op.isDone)
            {
                ct.ThrowIfCancellationRequested();
                await Awaitable.NextFrameAsync();
            }

            _signalBus.Fire(new SceneUnloadedSignal(sceneName));
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
                Debug.LogWarning($"[Nexus] Cannot set active scene '{sceneName}': not loaded or invalid.");
            }
        }
    }
}
