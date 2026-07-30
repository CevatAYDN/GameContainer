namespace Nexus.Starter
{
    /// <summary>
    /// A simple signal that carries an integer payload.
    /// Signals must be structs for 0-GC dispatch.
    /// </summary>
    public readonly struct NexusStartSignal
    {
        public readonly int Value;
        public NexusStartSignal(int value) => Value = value;
    }
}
