namespace Nexus.Ads
{
    /// <summary>An impression's revenue as the mediation reports it; forwarded unchanged to analytics.</summary>
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
