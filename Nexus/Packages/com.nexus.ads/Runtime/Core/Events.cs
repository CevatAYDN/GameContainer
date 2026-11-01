namespace Nexus.Ads
{
    /// <summary>An impression's revenue as the mediation reports it, forwarded to analytics; a provider that leaves <see cref="Placement"/> empty gets the placement the controller last showed for that format.</summary>
    public readonly struct AdRevenue
    {
        public readonly string Network;
        public readonly string Placement;
        public readonly AdFormat Format;
        public readonly double Value;
        public readonly string Currency;
        /// <summary>The mediation's precision word ("exact", "estimated", "publisher_defined", "simulated", ...).</summary>
        public readonly string Precision;

        public AdRevenue(string network, string placement, AdFormat format, double value, string currency, string precision)
        {
            Network = network;
            Placement = placement;
            Format = format;
            Value = value;
            Currency = currency;
            Precision = precision;
        }
    }

    /// <summary>One paid reward the game has not acknowledged; see <see cref="AdController.PendingReward"/>.</summary>
    public readonly struct RewardReceipt
    {
        public readonly string Id;
        public readonly string Placement;

        public RewardReceipt(string id, string placement)
        {
            Id = id;
            Placement = placement;
        }
    }

    /// <summary>
    /// The pending reward receipt, an <see cref="IPacingStore"/> row, so a game reads it at start-up before any controller exists
    /// (issue #178). One receipt is kept: a reward written over an unacknowledged one is recorded as
    /// <see cref="AdEventKind.RewardReplaced"/>.
    /// </summary>
    public static class RewardReceipts
    {
        internal const string Key = "nexus.ads.reward.pending";

        /// <summary>The reward the game has not acknowledged yet, or null.</summary>
        public static RewardReceipt? Pending(IPacingStore store)
        {
            string raw = store?.Get(Key);
            if (string.IsNullOrEmpty(raw)) return null;
            int bar = raw.IndexOf('|');
            return bar <= 0 ? (RewardReceipt?)null : new RewardReceipt(raw.Substring(0, bar), raw.Substring(bar + 1));
        }

        /// <summary>Clears the pending receipt when its id matches; any other id leaves it untouched.</summary>
        public static void Acknowledge(IPacingStore store, string receiptId)
        {
            var pending = Pending(store);
            if (pending.HasValue && pending.Value.Id == receiptId) store.Set(Key, "");
        }

        internal static void Write(IPacingStore store, RewardReceipt receipt) => store.Set(Key, receipt.Id + "|" + receipt.Placement);
    }

    public enum AdEventKind
    {
        Loaded,
        LoadFailed,
        NoFill,
        Shown,
        ShowFailed,
        Rewarded,
        Closed,
        Revenue,
        Violation,
        NotReady,
        KillSwitch,
        /// <summary>The SDK did not start; the detail names the attempt and the retry's delay.</summary>
        InitFailed,
        /// <summary>The consent flow allows no ad request; nothing starts until the player changes it in the privacy options.</summary>
        ConsentRefused,
        /// <summary>The SDK started; after failed starts the detail counts them.</summary>
        Initialized,
        /// <summary>The consent flow or the SDK start has not answered after <see cref="AdController.AnswerWaitSeconds"/>; the
        /// detail names which. The controller keeps waiting (a consent form may still be open, an SDK may wait for a network).</summary>
        Unanswered,
        /// <summary>A loaded ad the provider no longer has (it expired, or the SDK dropped it when consent changed); the format
        /// reloads (issue #175).</summary>
        Expired,
        /// <summary>A subscriber or a telemetry sink threw while the controller answered a show or raised ReadyChanged; the detail
        /// names the step and the exception. The rest of the answer, the game's result included, still ran (issue #176).</summary>
        HandlerFailed,
        /// <summary>A paid reward was written over a receipt the game never acknowledged, whose reward the next start no longer
        /// sees; the detail names both receipts (issue #178).</summary>
        RewardReplaced,
    }

    /// <summary>One telemetry row. The Unity layer forwards it to the game's analytics; every row carries the placement name.</summary>
    public readonly struct AdEvent
    {
        public readonly AdEventKind Kind;
        public readonly AdFormat Format;
        public readonly string Placement;
        public readonly string Detail;

        public AdEvent(AdEventKind kind, AdFormat format, string placement, string detail = null)
        {
            Kind = kind;
            Format = format;
            Placement = placement;
            Detail = detail;
        }

        public override string ToString() => Kind + " " + Format + " '" + Placement + "'" + (Detail == null ? "" : ": " + Detail);
    }
}
