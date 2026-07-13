using Nexus.Core;
using UnityEngine;

namespace Nexus.Samples.Counter
{
    /// <summary>
    /// Custom recovery strategy. Bound via builder.Bind&lt;IRecoveryStrategy, ...&gt;()
    /// and auto-resolved by SignalBus when a command throws. Retries transient
    /// failures a couple of times, then skips to keep the chain alive.
    /// </summary>
    public class CounterRecoveryStrategy : IRecoveryStrategy
    {
        public RecoveryDecision OnCommandFailed(CommandFailureContext failure)
        {
            if (failure.RetryCount < 2)
            {
                Debug.LogWarning($"[Counter] Retrying {failure.CommandType.Name} (attempt {failure.RetryCount + 1})");
                return RecoveryDecision.Retry(2);
            }

            Debug.LogError($"[Counter] Giving up on {failure.CommandType.Name}: {failure.Exception.Message}");
            return RecoveryDecision.Skip();
        }
    }
}
