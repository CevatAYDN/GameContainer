namespace Nexus
{
    // Simple struct signal with counter payload
    public readonly struct TEST1CounterSignal
    {
        public readonly int Value;
        public TEST1CounterSignal(int value) => Value = value;
    }
}
