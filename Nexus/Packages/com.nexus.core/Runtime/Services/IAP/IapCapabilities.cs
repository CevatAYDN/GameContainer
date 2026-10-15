using System;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>What a purchase attempt ended with.</summary>
    public enum PurchaseOutcome
    {
        Purchased,
        /// <summary>The player cancelled the store dialog.</summary>
        Cancelled,
        /// <summary>The store deferred the order (for example parental approval); the content is not unlocked yet.</summary>
        Pending,
        Failed,
        /// <summary>The store is not reachable or the product is not for sale in this build.</summary>
        Unavailable,
    }

    /// <summary>
    /// Optional adapter capability: purchase results as a <see cref="PurchaseOutcome"/>.
    /// <see cref="IapService"/> maps bool-only adapters to Purchased, Unavailable or Failed.
    /// </summary>
    [Preserve]
    public interface IIapOutcomeAdapter
    {
        void PurchaseWithOutcome(string productId, Action<PurchaseOutcome> onOutcome);
    }

    /// <summary>Optional adapter capability: store availability, the store's localized prices and a change notification.</summary>
    [Preserve]
    public interface IIapStoreInfoAdapter
    {
        bool IsAvailable { get; }
        /// <summary>The store's localized price string, or null until the store answered.</summary>
        string GetLocalizedPrice(string productId);
        /// <summary>Raised after connecting, after products or purchases were fetched, and after a restore.</summary>
        event Action Changed;
    }
}
