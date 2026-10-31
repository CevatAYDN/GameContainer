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

    /// <summary>What the store has said about a product (issue #183).</summary>
    public enum Ownership
    {
        /// <summary>The store has not answered yet (no store, not started, offline, or its purchase fetch failed).</summary>
        Unknown,
        NotOwned,
        Owned,
    }

    /// <summary>A paid consumable the game did not request in this session (issue #204). <paramref name="transactionId"/> is the
    /// store's id of the order: an order whose confirm failed in an earlier session is reported again, so a game that keeps the
    /// ids it granted grants each order once.</summary>
    public delegate void UnrequestedPurchaseHandler(string productId, string transactionId);

    /// <summary>
    /// Optional adapter capability (issue #204): paid consumables the game did not request in this session, for example one paid
    /// in a session whose process died before the game heard of it, a deferred payment approved, or a promo code. The adapter
    /// raises <see cref="UnrequestedPurchase"/> before it confirms (consumes) the order; with no handler it does not confirm it,
    /// warns in every build, and the store lists it again at the next purchase fetch.
    /// </summary>
    [Preserve]
    public interface IIapUnrequestedPurchaseAdapter
    {
        event UnrequestedPurchaseHandler UnrequestedPurchase;
    }

    /// <summary>Optional adapter capability: store availability, the store's localized prices and a change notification.</summary>
    [Preserve]
    public interface IIapStoreInfoAdapter
    {
        bool IsAvailable { get; }
        /// <summary>True once the store answered which products this account owns; until then a false
        /// <see cref="IIapStoreAdapter.IsOwned"/> means "not known yet", not "not owned".</summary>
        bool OwnershipKnown { get; }
        /// <summary>The store's localized price string, or null until the store answered.</summary>
        string GetLocalizedPrice(string productId);
        /// <summary>Raised after connecting, after products or purchases were fetched, and after a restore.</summary>
        event Action Changed;
    }
}
