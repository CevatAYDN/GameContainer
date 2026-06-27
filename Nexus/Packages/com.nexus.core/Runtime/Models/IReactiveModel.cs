using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Marks a model as reactive — it participates in Nexus initialization lifecycle
    /// and may contain <see cref="ObservableProperty{T}"/> members that automatically
    /// notify the system (and bound views) when their values change.
    ///
    /// Unlike a plain DI singleton, an IReactiveModel:
    /// - Receives <see cref="OnBind"/> after all constructor injections are complete.
    /// - Is eligible for live-inspection in the Nexus GameManager editor tooling.
    /// - Can be automatically serialized/deserialized by the save system.
    /// </summary>
    [Preserve]
    public interface IReactiveModel
    {
        /// <summary>
        /// Called by the Nexus runtime once, after the model has been instantiated
        /// and all [Inject] fields/properties/constructors have been resolved.
        /// Use this for initialisation that depends on other injected dependencies.
        /// </summary>
        ValueTask OnBind(CancellationToken ct);
    }
}
