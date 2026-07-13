namespace Nexus.Samples.Counter
{
    /// <summary>Triggers the Concurrent-mode load command.</summary>
    public readonly struct CounterLoadSignal { }

    /// <summary>Triggers the Exclusive-mode persist command.</summary>
    public readonly struct CounterPersistSignal { }

    /// <summary>Triggers the async command (guarded by [CommandTimeout]).</summary>
    public readonly struct CounterAsyncSignal
    {
        public readonly int Payload;
        public CounterAsyncSignal(int payload) => Payload = payload;
    }

    /// <summary>Composite fan-in part 1 — the composite command fires only after BOTH are received.</summary>
    public readonly struct CounterAckSignal { }

    /// <summary>Composite fan-in part 2 — the composite command fires only after BOTH are received.</summary>
    public readonly struct CounterDataSignal { }
}
