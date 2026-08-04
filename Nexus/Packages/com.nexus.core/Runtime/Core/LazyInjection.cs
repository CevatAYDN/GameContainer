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
                if (!_resolved)
                {
                    lock (_lock)
                    {
                        if (!_resolved)
                        {
                            _value = string.IsNullOrEmpty(_name)
                                ? _container.Resolve<T>()
                                : _container.Resolve<T>(_name);
                            _resolved = true;
                            _container.NotifyLazyServiceResolved(typeof(T), _value);
                        }
                    }
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
