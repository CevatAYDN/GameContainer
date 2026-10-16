using System;
using System.Runtime.CompilerServices;
using System.Threading;

[assembly: InternalsVisibleTo("com.nexus.ads.admob.tests")]

namespace Nexus.Ads.AdMob
{
    /// <summary>
    /// Brings SDK callbacks back to the Unity main thread. The Google Mobile Ads Unity plugin (11.6.0)
    /// invokes its User Messaging Platform listeners on the Android UI thread: the Java-to-C# callbacks in
    /// <c>Ump/Platforms/Android</c> call the stored delegate directly, without a dispatch to Unity, and
    /// Google's own sample wraps every one of them in <c>MobileAdsEventExecutor.ExecuteInUpdate</c>.
    /// Unity APIs throw off the main thread and the game's handlers must not run there, so the adapter
    /// captures this on the main thread and continues each callback through <see cref="TryPost"/>.
    /// </summary>
    internal sealed class AdMobMainThread
    {
        private readonly SynchronizationContext _context;
        private readonly int _threadId;

        internal AdMobMainThread(SynchronizationContext context, int threadId)
        {
            _context = context;
            _threadId = threadId;
        }

        /// <summary>Captures the calling thread and its <see cref="SynchronizationContext"/>; call it on the Unity main thread.</summary>
        public static AdMobMainThread Capture() => new AdMobMainThread(SynchronizationContext.Current, Thread.CurrentThread.ManagedThreadId);

        /// <summary>True on the captured thread, where a callback can continue inline.</summary>
        public bool IsCurrent => Thread.CurrentThread.ManagedThreadId == _threadId;

        /// <summary>
        /// Queues <paramref name="action"/> on the captured thread and returns true. Returns false without
        /// running anything when no context was captured (the capture did not happen on Unity's main
        /// thread), so the caller can decide instead of a queued action that nobody ever executes.
        /// </summary>
        public bool TryPost(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (_context == null) return false;
            _context.Post(state => ((Action)state)(), action);
            return true;
        }
    }
}
