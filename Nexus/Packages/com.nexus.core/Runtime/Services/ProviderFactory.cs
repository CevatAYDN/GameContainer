using System;
using System.Collections.Generic;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Shared base for provider factories (ads, IAP, ...) that create platform adapters
    /// by name. Encapsulates the registry + creation with logging, so every provider
    /// factory implements exactly the same semantics without copying the logic.
    /// </summary>
    /// <typeparam name="TAdapter">The adapter contract the factory produces.</typeparam>
    [Preserve]
    public abstract class ProviderFactory<TAdapter>
    {
        private readonly Dictionary<string, Func<TAdapter>> _creators
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Display names the factory reports in warnings (e.g. "ad provider").</summary>
        private readonly string _providerKind;

        protected ProviderFactory(string providerKind)
        {
            _providerKind = providerKind;
        }

        /// <summary>Registers the built-in provider(s) shipped for this factory.</summary>
        protected void RegisterBuiltIn(string provider, Func<TAdapter> creator)
        {
            RegisterProvider(provider, creator);
        }

        /// <summary>Creates an adapter for the specified provider, or null if unknown.</summary>
        public virtual TAdapter CreateAdapter(string provider)
        {
            if (string.IsNullOrEmpty(provider)) return default;
            if (_creators.TryGetValue(provider, out var creator))
            {
                try { return creator(); }
                catch (Exception ex)
                {
                    NexusRuntime.Logger?.LogError($"[{GetType().Name}] Failed to create {_providerKind} for '{provider}': {ex.Message}");
                    return default;
                }
            }
            NexusRuntime.Logger?.LogWarning($"[{GetType().Name}] Unknown {_providerKind}: '{provider}'. Available: {string.Join(", ", _creators.Keys)}");
            return default;
        }

        /// <summary>Registers a custom adapter creator for a provider.</summary>
        public virtual void RegisterProvider(string provider, Func<TAdapter> creator)
        {
            if (string.IsNullOrEmpty(provider)) throw new ArgumentNullException(nameof(provider));
            if (creator == null) throw new ArgumentNullException(nameof(creator));
            _creators[provider] = creator;
        }
    }
}