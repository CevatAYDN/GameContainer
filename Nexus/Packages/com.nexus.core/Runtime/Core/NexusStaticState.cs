using System;
using System.Collections.Generic;

namespace Nexus.Core
{
    /// <summary>
    /// Registry of process-lifetime caches that must be cleared when the runtime resets
    /// (domain reload, or a play-mode cycle with Disable Domain Reload enabled, where statics
    /// survive and would otherwise hold Type references from a previous compilation).
    ///
    /// Previously <see cref="NexusRuntime.Reset"/> named every cache site explicitly, so each
    /// new static cache required editing Reset as well — a step that is easy to forget and
    /// fails silently. Caches now register their own clear action, so the reset list cannot
    /// drift from the code that owns the state.
    ///
    /// Register from a static constructor or a <c>[RuntimeInitializeOnLoadMethod]</c>, and keep
    /// the clear action idempotent: it may run when nothing has been allocated yet.
    /// </summary>
    public static class NexusStaticState
    {
        private static readonly List<(string Name, Action Clear)> s_resettables = new();
        private static readonly HashSet<string> s_registeredNames = new();
        private static readonly object s_lock = new();

        /// <summary>
        /// Registers a clear action run by <see cref="NexusRuntime.Reset"/>. Registration is
        /// idempotent per <paramref name="name"/>, so a static constructor re-running after a
        /// domain reload cannot queue the same cache twice.
        /// </summary>
        /// <param name="name">Stable identifier, conventionally "Type.CacheName".</param>
        /// <param name="clear">Idempotent clear action.</param>
        public static void Register(string name, Action clear)
        {
            if (clear == null) throw new ArgumentNullException(nameof(clear));
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("A stable name is required.", nameof(name));
            lock (s_lock)
            {
                if (!s_registeredNames.Add(name)) return;
                s_resettables.Add((name, clear));
            }
        }

        /// <summary>
        /// Runs every registered clear action. A failing action is logged and does not prevent
        /// the remaining caches from being cleared — a half-cleared reset is worse than a
        /// reported failure.
        /// </summary>
        internal static void ClearAll()
        {
            (string Name, Action Clear)[] snapshot;
            lock (s_lock)
            {
                snapshot = s_resettables.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    snapshot[i].Clear();
                }
                catch (Exception ex)
                {
                    Services.NexusLog.Error(nameof(NexusStaticState), nameof(ClearAll),
                        $"Clearing static state '{snapshot[i].Name}' failed", ex);
                }
            }
        }

        /// <summary>Names of the currently registered caches (diagnostics/editor tooling).</summary>
        public static IReadOnlyList<string> RegisteredNames
        {
            get
            {
                lock (s_lock)
                    return new List<string>(s_registeredNames);
            }
        }
    }
}
