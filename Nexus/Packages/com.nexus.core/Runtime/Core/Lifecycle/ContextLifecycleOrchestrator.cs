using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Nexus.Core.Lifecycle
{
    /// <summary>
    /// Handles execution and iteration of context lifecycle phases (InitializeAsync, StartAsync).
    /// Follows Single Responsibility Principle (SRP) to decouple lifecycle orchestration from DI context containers.
    /// </summary>
    public sealed class ContextLifecycleOrchestrator
    {
        // Bounded, once-per-type diagnostic for instances implementing both the sync and the
        // async form of a lifecycle contract (both hooks fire — see the call sites).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, byte> s_bothImplementedWarned = new();

        private static void WarnBothImplemented(Type type, string syncName, string asyncName)
        {
            if (!s_bothImplementedWarned.TryAdd(type, 0)) return;
            NexusRuntime.Logger?.LogWarning(
                $"[Nexus] '{type.FullName}' implements both {syncName} and {asyncName}; BOTH hooks are invoked ({asyncName} first). " +
                $"Implement only one unless the two hooks genuinely do different work.");
        }

        public async ValueTask ExecuteLifecyclePhasesAsync(IReadOnlyList<IContextLifecycle> lifecycles, CancellationToken ct)
        {
            if (lifecycles == null || lifecycles.Count == 0) return;

            for (int i = 0; i < lifecycles.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                if (lifecycles[i] != null)
                {
                    try
                    {
                        await lifecycles[i].OnInitializeAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        // Route through NexusRuntime.Logger (the framework's
                        // logging abstraction) instead of raw Debug.LogError so log
                        // filtering/sinks stay consistent with the rest of the runtime.
                        NexusRuntime.Logger?.LogError($"[Nexus] Lifecycle OnInitializeAsync exception in {lifecycles[i].GetType().Name}: {ex.Message}");
                        NexusRuntime.Logger?.LogException(ex);
                    }
                }
            }

            for (int i = 0; i < lifecycles.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                if (lifecycles[i] != null)
                {
                    try
                    {
                        await lifecycles[i].OnStartAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogError($"[Nexus] Lifecycle OnStartAsync exception in {lifecycles[i].GetType().Name}: {ex.Message}");
                        NexusRuntime.Logger?.LogException(ex);
                    }
                }
            }
        }

        public async ValueTask ExecuteStartableLifecyclesAsync(IEnumerable<object> instances, CancellationToken ct)
        {
            if (instances == null) return;
            foreach (var inst in instances)
            {
                if (ct.IsCancellationRequested) break;
                if (inst == null) continue;

                // Both hooks run when a type implements both interfaces: they are separate
                // contracts (Start for immediate work, StartAsync for awaited work), and the
                // async hook is awaited before the sync one so ordering stays deterministic.
                // Implementing both is usually unintended, so it is reported once per type —
                // splitting the work across two hooks that both fire is easy to misread as
                // "only the async one runs".
                if (inst is IAsyncStartable asyncStartable)
                {
                    if (inst is IStartable) WarnBothImplemented(inst.GetType(), nameof(IStartable), nameof(IAsyncStartable));
                    try { await asyncStartable.StartAsync(ct); }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogError($"[Nexus] Exception in IAsyncStartable.StartAsync ({inst.GetType().FullName}): {ex.Message}");
                        NexusRuntime.Logger?.LogException(ex);
                    }
                }
                if (inst is IStartable startable)
                {
                    try { startable.Start(); }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogError($"[Nexus] Exception in IStartable.Start ({inst.GetType().FullName}): {ex.Message}");
                        NexusRuntime.Logger?.LogException(ex);
                    }
                }
            }
        }

        public async ValueTask ExecuteStoppableLifecyclesAsync(IEnumerable<object> instances, CancellationToken ct)
        {
            if (instances == null) return;
            var list = new List<object>(instances);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var inst = list[i];
                if (inst == null) continue;

                // Mirrors the startable path: both hooks run, async first, and implementing
                // both is reported once per type.
                if (inst is IAsyncStoppable asyncStoppable)
                {
                    if (inst is IStoppable) WarnBothImplemented(inst.GetType(), nameof(IStoppable), nameof(IAsyncStoppable));
                    try { await asyncStoppable.StopAsync(ct); }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogError($"[Nexus] Exception in IAsyncStoppable.StopAsync ({inst.GetType().FullName}): {ex.Message}");
                        NexusRuntime.Logger?.LogException(ex);
                    }
                }
                if (inst is IStoppable stoppable)
                {
                    try { stoppable.Stop(); }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogError($"[Nexus] Exception in IStoppable.Stop ({inst.GetType().FullName}): {ex.Message}");
                        NexusRuntime.Logger?.LogException(ex);
                    }
                }
            }
        }

        public void ExecuteStoppableLifecyclesSync(IEnumerable<object> instances)
        {
            if (instances == null) return;
            var list = new List<object>(instances);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var inst = list[i];
                if (inst == null) continue;

                // A type may implement BOTH interfaces. The sync path prefers the synchronous
                // Stop (deterministic, completes before this method returns) and only falls
                // back to the fire-and-forget async stop when no sync Stop exists — so the
                // shutdown sequence never runs twice for the same instance.
                if (inst is IStoppable stoppable)
                {
                    try { stoppable.Stop(); }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogError($"[Nexus] Exception in IStoppable.Stop ({inst.GetType().FullName}): {ex.Message}");
                        NexusRuntime.Logger?.LogException(ex);
                    }
                }
                // Also check IAsyncStoppable. The sync path cannot await, so we
                // fire-and-forget the async stop via SafeAsyncRunner pattern. This ensures
                // services implementing ONLY IAsyncStoppable (not IStoppable) still get
                // their cleanup called on synchronous Dispose (e.g. Root.OnDestroy).
                else if (inst is IAsyncStoppable asyncStoppable)
                {
                    try
                    {
                        // Fire-and-forget the async stop — best-effort cleanup.
                        // StopAsyncInternal supplies its own fresh 5-second timeout token
                        // (independent of the context's already-cancelled _cts), giving the
                        // stop a bounded grace window to finish real cleanup work instead of
                        // observing immediate cancellation.
                        _ = StopAsyncInternal(asyncStoppable);
                    }
                    catch (Exception ex)
                    {
                        NexusRuntime.Logger?.LogError($"[Nexus] Exception initiating IAsyncStoppable.StopAsync fire-and-forget ({inst.GetType().FullName}): {ex.Message}");
                        NexusRuntime.Logger?.LogException(ex);
                    }
                }
            }
        }

        private static async System.Threading.Tasks.Task StopAsyncInternal(IAsyncStoppable stoppable)
        {
            try
            {
                // Use a short timeout so fire-and-forget cleanup doesn't hang indefinitely.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                // ConfigureAwait(false) prevents the continuation from hopping back to
                // the (already torn-down) Unity SynchronizationContext during context dispose.
                await stoppable.StopAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* timeout or cancellation during teardown */ }
            catch (Exception ex)
            {
                NexusRuntime.Logger?.LogError($"[Nexus] Fire-and-forget IAsyncStoppable.StopAsync failed: {ex.Message}");
                NexusRuntime.Logger?.LogException(ex);
            }
        }
    }
}
