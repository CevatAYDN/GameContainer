using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Central registry for all active Nexus contexts.
    /// Provides thread-safe registration, unregistration, enumeration, and domain-reload-safe reset.
    /// </summary>
    public static class NexusRuntime
    {
        public static event System.Action<IContext> OnContextRegistered;
        public static event System.Action<IContext> OnContextUnregistered;

        private static readonly List<IContext> s_activeContexts = new();
        private static readonly HashSet<IContext> s_contextSet = new();
        private static readonly object s_lock = new();

        /// <summary>Returns a thread-safe snapshot of all active contexts.</summary>
        /// <remarks>Locked access via <c>s_lock</c>. Returns a snapshot to prevent race conditions during iteration.</remarks>
        public static IReadOnlyList<IContext> ActiveContexts
        {
            get
            {
                lock (s_lock)
                {
                    return new List<IContext>(s_activeContexts);
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InitializeOnLoad()
        {
            Reset();
        }

        /// <summary>
        /// Creates and registers a pure code-based Context without requiring a Root GameObject in the scene.
        /// Ideal for tests, dedicated servers, or strictly data-oriented architectures.
        /// </summary>
        public static async System.Threading.Tasks.Task<IContext> CreatePureContextAsync(string scopeTag, string[] assemblyScopes = null)
        {
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.name = $"{scopeTag}ContextData_Pure";
            data.ScopeTag = scopeTag;
            if (assemblyScopes != null)
            {
                data.AssemblyScopes = assemblyScopes;
            }

            var context = new Context(null, data);
            context.Configure();

            if (context.Container.IsRegistered(typeof(IContextLifecycle)))
            {
                var lifecycle = context.Container.Resolve<IContextLifecycle>();
                await lifecycle.OnInitializeAsync(context.LifetimeToken);
                await lifecycle.OnStartAsync(context.LifetimeToken);
            }

            return context;
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
            bool added = false;
            lock (s_lock)
            {
                if (s_contextSet.Add(context))
                {
                    s_activeContexts.Add(context);
                    added = true;
                }
            }
            if (added)
            {
                try
                {
                    OnContextRegistered?.Invoke(context);
                }
                catch (System.Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }

        /// <summary>Unregisters a context. Thread-safe.</summary>
        /// <param name="context">The context to unregister.</param>
        public static void UnregisterContext(IContext context)
        {
            bool removed = false;
            lock (s_lock)
            {
                if (s_contextSet.Remove(context))
                {
                    s_activeContexts.Remove(context);
                    removed = true;
                }
            }
            if (removed)
            {
                try
                {
                    OnContextUnregistered?.Invoke(context);
                }
                catch (System.Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }
    }
}
