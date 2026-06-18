using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Central registry for all active Nexus contexts.
    /// Provides thread-safe registration, unregistration, enumeration, and domain-reload-safe reset.
    /// </summary>
    [Preserve]
    public static class NexusRuntime
    {
        private static readonly List<IContext> s_activeContexts = new();
        private static readonly HashSet<IContext> s_contextSet = new();
        private static readonly object s_lock = new();

        /// <summary>Returns a thread-safe snapshot of all active contexts.</summary>
        /// <remarks>Locked access via <c>s_lock</c>. No allocation on each access (returns the live list).</remarks>
        public static IReadOnlyList<IContext> ActiveContexts
        {
            get
            {
                lock (s_lock)
                {
                    return s_activeContexts;
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InitializeOnLoad()
        {
            Reset();
        }

        /// <summary>Disposes all active contexts and clears the registry. Called automatically on domain reload.</summary>
        public static void Reset()
        {
            lock (s_lock)
            {
                for (int i = s_activeContexts.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        s_activeContexts[i].Dispose();
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }
                s_activeContexts.Clear();
                s_contextSet.Clear();
            }
        }

        /// <summary>Registers a context as active. Thread-safe.</summary>
        /// <param name="context">The context to register.</param>
        public static void RegisterContext(IContext context)
        {
            lock (s_lock)
            {
                if (s_contextSet.Add(context))
                {
                    s_activeContexts.Add(context);
                }
            }
        }

        /// <summary>Unregisters a context. Thread-safe.</summary>
        /// <param name="context">The context to unregister.</param>
        public static void UnregisterContext(IContext context)
        {
            lock (s_lock)
            {
                if (s_contextSet.Remove(context))
                {
                    s_activeContexts.Remove(context);
                }
            }
        }
    }
}
