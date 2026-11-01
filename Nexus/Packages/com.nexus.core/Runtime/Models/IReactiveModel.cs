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
    /// - Is listed in the Nexus window: the Architecture tab names the type, and in Play Mode the Context Inspector
    ///   counts the context's models and shows each live instance with its fields.
    /// Nexus does not save models: saving stays in the game (#75, #196).
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
