using System;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// Thread-safe lazy wrapper for deferred service resolution.
    /// When injected into an [Inject]-annotated field via NexusDI, the wrapped service
    /// is not constructed until the first access to <see cref="Value"/>.
    /// If the service implements <see cref="INexusService"/>, its <see cref="INexusService.InitializeAsync"/>
    /// is called during the next lazy-service initialization window in the Root lifecycle.
    /// </summary>
    [Preserve]
    public class LazyInjection<T> where T : class
    {
        private readonly NexusDI _container;
        private readonly string _name;
        private volatile T _value;
        private volatile bool _resolved;
        private readonly object _lock = new();

        internal LazyInjection(NexusDI container, string name = null)
        {
            _container = container ?? throw new ArgumentNullException(nameof(container));
            _name = name;
        }

        /// <summary>
        /// Gets the resolved service instance. On first access, resolves the service
        /// from the container and notifies the container for deferred initialization
        /// if the service implements <see cref="INexusService"/>.
        /// Thread-safe via double-check locking.
        /// </summary>
        public T Value
        {
            get
            {
                if (_resolved) return _value;

                // Resolve OUTSIDE _lock: Resolve() runs arbitrary user constructors and can
                // take container-internal locks — resolving while holding _lock risks a
                // lock-ordering deadlock and executes user code under our lock. Two racing
                // threads may both resolve; the first to publish wins and the losing
                // resolution is discarded (for transients this tolerates one extra,
                // never-published construction).
                T resolvedValue = string.IsNullOrEmpty(_name)
                    ? _container.Resolve<T>()
                    : _container.Resolve<T>(_name);

                bool won = false;
                lock (_lock)
                {
                    if (!_resolved)
                    {
                        _value = resolvedValue;
                        _resolved = true;
                        won = true;
                    }
                }
                if (won)
                {
                    // Also outside the lock — the container's deferred-init bookkeeping is
                    // user-visible code and must not run under _lock.
                    _container.NotifyLazyServiceResolved(typeof(T), _value);
                }
                return _value;
            }
        }

        /// <summary>
        /// Returns true if the service has been resolved at least once.
        /// </summary>
        public bool IsValueResolved => _resolved;
    }
}
