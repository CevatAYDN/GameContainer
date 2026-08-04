using System;
using UnityEngine;

namespace Nexus.Core
{
    /// <summary>
    /// Consolidates ambient Root-discovery logic that was previously in <see cref="View.OnEnable"/>.
    /// Deepens the registration pipeline: View calls one method; the complexity of finding
    /// the right Root, buffering pending views, and delegating to Context lives here.
    /// </summary>
    internal static class ViewRegistration
    {
        /// <summary>
        /// Registers a view with the nearest parent Root's context.
        /// Falls back to <c>FindObjectsByType</c> only when the scene has one unambiguous Root.
        /// </summary>
        /// <param name="view">The view to register.</param>
        /// <param name="pendingRoot">Reference to the view's cached pending-Root field.</param>
        public static void Register(IView view, ref Root pendingRoot)
        {
            if (view is not MonoBehaviour mb) return;

            var root = mb.GetComponentInParent<Root>();
            if (root != null)
            {
                if (root.Context != null)
                    root.Context.RegisterView(view);
                else
                {
                    pendingRoot = root;
                    root.RegisterPendingView(view);
                }
                return;
            }

            var roots = UnityEngine.Object.FindObjectsByType<Root>(FindObjectsInactive.Exclude);
            if (roots.Length == 1)
            {
                var singleRoot = roots[0];
                if (singleRoot.Context != null)
                    singleRoot.Context.RegisterView(view);
                else
                {
                    pendingRoot = singleRoot;
                    singleRoot.RegisterPendingView(view);
                }
            }
            else if (roots.Length > 1)
            {
                NexusRuntime.Logger?.LogError($"[Nexus] View '{mb.gameObject.name}' OnEnable: Multiple Root instances found. " +
                    "Registration aborted because the target context is ambiguous.");
            }
            else
            {
                NexusRuntime.Logger?.LogError($"[Nexus] View '{mb.gameObject.name}' OnEnable: No Root GameObject found in scene. " +
                    "Create a Root via GameObject → Nexus → Create Root.");
            }
        }
    }
}
