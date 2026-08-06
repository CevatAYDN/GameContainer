using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core
{
    [UnityEngine.Scripting.Preserve]
    public interface IContextLifecycle
    {
        /// <summary>
        /// Called first. Use this to bind models, commands, and services.
        /// </summary>
        void OnConfigure(IContextBuilder builder);
        /// <summary>
        /// Called after configuration and reactive-model initialization.
        /// Use this for async setup work that depends on bindings being ready.
        /// </summary>
        ValueTask OnInitializeAsync(CancellationToken ct);
        /// <summary>
        /// Called after OnInitializeAsync for final startup work.
        /// Use this for signal subscriptions, view hookup, and runtime kickoff.
        /// </summary>
        ValueTask OnStartAsync(CancellationToken ct);
        void OnDispose();
    }

    /// <summary>
    /// Optional extension to <see cref="IContextLifecycle"/> for cross-context wiring.
    /// <see cref="OnPostContext"/> is called once ALL contexts in the application have completed
    /// their standard lifecycle (OnConfigure → OnInitializeAsync → OnStartAsync).
    /// Use this for wiring that spans a configured context hierarchy — e.g. exposing a
    /// parent-owned model to descendant contexts through an explicit cross-boundary binding.
    /// Sibling containers are intentionally isolated; communicate through a common ancestor
    /// or an explicitly shared service instead of relying on implicit sibling lookup.
    ///
    /// Implement this interface on the same class as <see cref="IContextLifecycle"/>.
    /// The framework detects the interface and calls <see cref="OnPostContext"/> after
    /// all contexts have finished their startup lifecycle.
    /// </summary>
    public interface IPostContextLifecycle
    {
        /// <summary>
        /// Called after ALL contexts have been configured and initialized.
        /// The <paramref name="builder"/> provides the same binding API as
        /// <see cref="IContextLifecycle.OnConfigure"/>, enabling late-binding
        /// of cross-context references.
        /// </summary>
        void OnPostContext(IContextBuilder builder);
    }
}
