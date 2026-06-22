namespace Nexus.Samples.Counter
{
    public readonly struct CounterSignal
    {
        public readonly int Amount;
        public CounterSignal(int amount) => Amount = amount;
    }
}
