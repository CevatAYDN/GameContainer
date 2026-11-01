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
        /// <summary>Loads a scene additively and fires SceneLoadedSignal on completion. A second call for a scene that is loading
        /// waits for that load. Cancelling stops the wait, not the load: Unity cannot stop a scene load, so the scene finishes
        /// loading and SceneLoadedSignal fires.</summary>
        Task LoadSceneAsync(string sceneName, LoadSceneMode mode = LoadSceneMode.Additive, CancellationToken ct = default);
        /// <summary>Unloads a scene and fires SceneUnloadedSignal. A second call for a scene that is unloading waits for that
        /// unload; cancelling stops the wait, not the unload.</summary>
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
        // The load and the unload in flight per scene: a second call for the same scene waits for it (issue #188).
        private readonly Dictionary<string, (Task task, LoadSceneMode mode)> _loads = new();
        private readonly Dictionary<string, Task> _unloads = new();
        private readonly object _loadingLock = new();

        public SceneLoader(ISignalBus signalBus)
        {
            _signalBus = signalBus ?? throw new ArgumentNullException(nameof(signalBus));
        }

        public Task LoadSceneAsync(string sceneName, LoadSceneMode mode = LoadSceneMode.Additive, CancellationToken ct = default)
        {
            TaskCompletionSource<bool> done = null;
            Task load;
            lock (_loadingLock)
            {
                if (_loads.TryGetValue(sceneName, out var running))
                {
                    load = running.task;
                    if (running.mode != mode)
                        NexusLog.WarnAlways($"[Nexus] Scene '{sceneName}' is already loading with {running.mode}; this {mode} load waits for that one.");
                }
                else
                {
                    done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    load = done.Task;
                    _loads[sceneName] = (load, mode);
                }
            }

            if (done != null) _ = RunLoadAsync(sceneName, mode, done);
            return ct.CanBeCanceled ? WaitAsync(load, sceneName, "load", nameof(SceneLoadedSignal), ct) : load;
        }

        private async Task RunLoadAsync(string sceneName, LoadSceneMode mode, TaskCompletionSource<bool> done)
        {
            Exception failure = null;
            bool loaded = false;
            try
            {
                FireSafely(new SceneLoadingSignal(sceneName), sceneName);

                var op = SceneManager.LoadSceneAsync(sceneName, mode);
                if (op == null)
                {
                    NexusRuntime.Logger?.LogError($"[Nexus] Scene '{sceneName}' not found in build settings.");
                    // Terminal signal: loading UI must never wait forever on a failed load.
                    FireSafely(new SceneLoadFailedSignal(sceneName, "Scene not found in build settings."), sceneName);
                    return;
                }

                op.allowSceneActivation = true;
                // Not cancellable: Unity cannot stop a scene load (issue #188); the caller's token only ends its own wait.
                while (!op.isDone) await Task.Yield();
                loaded = true;
            }
            catch (Exception ex)
            {
                failure = ex;
                NexusRuntime.Logger?.LogError($"[Nexus] Loading scene '{sceneName}' failed: {ex}");
                // Terminal signal on the exception path too (see above).
                FireSafely(new SceneLoadFailedSignal(sceneName, ex.Message), sceneName);
            }
            finally
            {
                lock (_loadingLock) _loads.Remove(sceneName);
                if (loaded) FireSafely(new SceneLoadedSignal(sceneName), sceneName);
                if (failure != null) done.TrySetException(failure);
                else done.TrySetResult(loaded);
            }
        }

        public Task UnloadSceneAsync(string sceneName, CancellationToken ct = default)
        {
            TaskCompletionSource<bool> done = null;
            Task unload;
            lock (_loadingLock)
            {
                if (!_unloads.TryGetValue(sceneName, out unload))
                {
                    done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    unload = done.Task;
                    _unloads[sceneName] = unload;
                }
            }

            if (done != null) _ = RunUnloadAsync(sceneName, done);
            return ct.CanBeCanceled ? WaitAsync(unload, sceneName, "unload", nameof(SceneUnloadedSignal), ct) : unload;
        }

        private async Task RunUnloadAsync(string sceneName, TaskCompletionSource<bool> done)
        {
            Exception failure = null;
            bool unloaded = false;
            try
            {
                var op = SceneManager.UnloadSceneAsync(sceneName);
                if (op == null)
                {
                    NexusLog.WarnAlways($"[Nexus] Scene '{sceneName}' is not loaded or cannot be unloaded.");
                    return;
                }

                while (!op.isDone) await Task.Yield();
                unloaded = true;
            }
            catch (Exception ex)
            {
                failure = ex;
                NexusRuntime.Logger?.LogError($"[Nexus] Unloading scene '{sceneName}' failed: {ex}");
            }
            finally
            {
                lock (_loadingLock) _unloads.Remove(sceneName);
                if (unloaded) FireSafely(new SceneUnloadedSignal(sceneName), sceneName);
                if (failure != null) done.TrySetException(failure);
                else done.TrySetResult(unloaded);
            }
        }

        // The caller's token ends its own wait; the operation, which Unity cannot stop, finishes and fires its signal (issue #188).
        private static async Task WaitAsync(Task operation, string sceneName, string what, string signal, CancellationToken ct)
        {
            if (!operation.IsCompleted)
            {
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (ct.Register(() => cancelled.TrySetResult(true)))
                {
                    if (await Task.WhenAny(operation, cancelled.Task) != operation)
                    {
                        NexusLog.WarnAlways($"[Nexus] The {what} of scene '{sceneName}' was cancelled while it ran; Unity cannot stop it, so it finishes and {signal} fires.");
                        ct.ThrowIfCancellationRequested();
                    }
                }
            }
            await operation;
        }

        // A subscriber that throws must not change what the load or unload did (issue #188). The bus has written a
        // subscriber's failure (issue #189); a failure it did not write is written here.
        private void FireSafely<T>(T signal, string sceneName) where T : struct
        {
            try { _signalBus.Fire(signal); }
            catch (Exception ex)
            {
                if (!RecoveryEngine.WasLogged(ex))
                    NexusRuntime.Logger?.LogError($"[Nexus] Firing {typeof(T).Name} for scene '{sceneName}' failed: {ex}");
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
                NexusLog.WarnAlways($"[Nexus] Cannot set active scene '{sceneName}': not loaded or invalid.");
            }
        }
    }
}
