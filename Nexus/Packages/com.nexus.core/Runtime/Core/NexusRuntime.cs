using System.Collections.Generic;
using UnityEngine;

namespace Nexus.Core
{
    public static class NexusRuntime
    {
        public static readonly List<IContext> ActiveContexts = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InitializeOnLoad()
        {
            Reset();
        }

        public static void Reset()
        {
            // Clear all active contexts
            for (int i = ActiveContexts.Count - 1; i >= 0; i--)
            {
                try
                {
                    ActiveContexts[i].Dispose();
                }
                catch (System.Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
            ActiveContexts.Clear();
        }

        public static void RegisterContext(IContext context)
        {
            if (!ActiveContexts.Contains(context))
            {
                ActiveContexts.Add(context);
            }
        }

        public static void UnregisterContext(IContext context)
        {
            ActiveContexts.Remove(context);
        }
    }
}
