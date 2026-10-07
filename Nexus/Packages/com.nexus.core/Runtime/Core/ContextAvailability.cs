using UnityEngine;

namespace Nexus.Core
{
    /// <summary>Shared cold-path selection for scene injection. A present parent scope is authoritative.</summary>
    internal static class ContextAvailability
    {
        internal static bool IsAlive(IContext context)
        {
            if (context == null) return false;
            if (context is Context owned) return !owned.IsDisposed;
            try { return !context.LifetimeToken.IsCancellationRequested; }
            catch (System.ObjectDisposedException) { return false; }
        }

        internal static bool CanInject(IContext context) => IsAlive(context) &&
            (!(context is Context owned) || owned.IsInjectionReady);

        internal static IContext Find(Component component)
        {
            var root = component.GetComponentInParent<Root>(includeInactive: true);
            if (root != null) return CanInject(root.Context) ? root.Context : null;
            var global = NexusRuntime.GlobalContext;
            if (global != null) return CanInject(global) ? global : null;
            // Multiple independent code-first scopes require explicit ownership.
            var active = NexusRuntime.ActiveContexts;
            return active.Count == 1 && CanInject(active[0]) ? active[0] : null;
        }
    }
}
