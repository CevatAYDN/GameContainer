using System.Threading;

namespace Nexus.Core
{
    /// <summary>
    /// Central thread-safe generator for sequential Type IDs.
    /// Used by <see cref="TypeIdCache{T}"/> for lock-free O(1) array indexing inside <see cref="NexusDI"/>.
    /// </summary>
    public static class TypeIdRegistry
    {
        private static int s_counter;

        /// <summary>Generates the next unique integer ID for a type.</summary>
        public static int GetNextId() => Interlocked.Increment(ref s_counter);
    }

    /// <summary>
    /// Compile-time/startup static cache that holds a unique integer ID per type <typeparamref name="T"/>.
    /// Enables <see cref="NexusDI"/> to resolve singletons in &lt; 2ns via array lookup (<c>_fastSlots[id]</c>).
    /// </summary>
    public static class TypeIdCache<T>
    {
        /// <summary>The unique integer token for type <typeparamref name="T"/>.</summary>
        public static readonly int Id = TypeIdRegistry.GetNextId();
    }
}
