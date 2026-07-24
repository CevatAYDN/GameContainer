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
    }
}
