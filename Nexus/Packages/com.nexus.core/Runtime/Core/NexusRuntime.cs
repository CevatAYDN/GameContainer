using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    [Preserve]
    public static class NexusRuntime
    {
        private static readonly List<IContext> s_activeContexts = new();
        private static readonly object s_lock = new();

        public static IReadOnlyList<IContext> ActiveContexts
        {
            get
            {
                lock (s_lock)
                {
                    return s_activeContexts.ToArray();
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InitializeOnLoad()
        {
            Reset();
        }

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
            }
        }

        public static void RegisterContext(IContext context)
        {
            lock (s_lock)
            {
                if (!s_activeContexts.Contains(context))
                {
                    s_activeContexts.Add(context);
                }
            }
        }

        public static void UnregisterContext(IContext context)
        {
            lock (s_lock)
            {
                s_activeContexts.Remove(context);
            }
        }
    }
}
