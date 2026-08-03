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
                        Debug.LogError($"[Nexus] Lifecycle OnInitializeAsync exception in {lifecycles[i].GetType().Name}: {ex.Message}");
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
                        Debug.LogError($"[Nexus] Lifecycle OnStartAsync exception in {lifecycles[i].GetType().Name}: {ex.Message}");
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

                // A type may implement BOTH IAsyncStartable and IStartable (unusual but valid).
                // Prefer the async path; only fall through to sync if async is absent so the
                // startup sequence is not executed twice for the same instance.
                if (inst is IAsyncStartable asyncStartable)
                {
                    try { await asyncStartable.StartAsync(ct); }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[Nexus] Exception in IAsyncStartable.StartAsync ({inst.GetType().FullName}): {ex.Message}");
                    }
                }
                if (inst is IStartable startable)
                {
                    try { startable.Start(); }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[Nexus] Exception in IStartable.Start ({inst.GetType().FullName}): {ex.Message}");
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

                if (inst is IAsyncStoppable asyncStoppable)
                {
                    try { await asyncStoppable.StopAsync(ct); }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[Nexus] Exception in IAsyncStoppable.StopAsync ({inst.GetType().FullName}): {ex.Message}");
                    }
                }
                if (inst is IStoppable stoppable)
                {
                    try { stoppable.Stop(); }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[Nexus] Exception in IStoppable.Stop ({inst.GetType().FullName}): {ex.Message}");
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
                if (inst is IStoppable stoppable)
                {
                    try { stoppable.Stop(); }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[Nexus] Exception in IStoppable.Stop ({inst.GetType().FullName}): {ex.Message}");
                    }
                }
            }
        }
    }
}
