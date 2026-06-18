namespace Nexus
{
    // Simple struct signal with counter payload
    public readonly struct Test1CounterSignal
    {
        public readonly int Value;
        public Test1CounterSignal(int value) => Value = value;
    }
}
