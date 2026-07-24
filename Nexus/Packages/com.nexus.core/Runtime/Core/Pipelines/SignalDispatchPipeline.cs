using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nexus.Core.Pipelines
{
    /// <summary>
    /// Encapsulates signal broadcasting, cross-context dispatching, and reentrancy tracking.
    /// Follows Single Responsibility Principle (SRP) to isolate signal routing logic from command pooling.
    /// </summary>
    public sealed class SignalDispatchPipeline
    {
        private readonly IContextResolver _contextResolver;

        public SignalDispatchPipeline(IContextResolver contextResolver)
        {
            _contextResolver = contextResolver ?? NexusRuntime.DefaultContextResolver;
        }

        /// <summary>
        /// Resolves target contexts and routes cross-context signals safely.
        /// </summary>
        public void BroadcastCrossContext<TSignal>(string targetScopeTag, TSignal signal) where TSignal : struct
        {
            if (string.IsNullOrEmpty(targetScopeTag)) return;

            var activeContexts = _contextResolver.GetActiveContexts();
            IContext targetCtx = null;

            if (activeContexts != null)
            {
                for (int i = 0; i < activeContexts.Count; i++)
                {
                    if (activeContexts[i] != null && activeContexts[i].ScopeTag == targetScopeTag)
                    {
                        targetCtx = activeContexts[i];
                        break;
                    }
                }
            }

            if (targetCtx?.SignalBus != null)
            {
                targetCtx.SignalBus.Fire(signal);
            }
            else
            {
                Debug.LogWarning($"[Nexus] Target context with ScopeTag '{targetScopeTag}' was not found or has a null SignalBus.");
            }
        }
    }
}
