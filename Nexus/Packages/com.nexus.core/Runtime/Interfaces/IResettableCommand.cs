namespace Nexus.Core
{
    /// <summary>
    /// Optional interface for command classes that hold transient execution state.
    /// Implemented by pooled commands to reset state upon being returned to the pool,
    /// preventing state contamination across pooled executions.
    /// </summary>
    public interface IResettableCommand
    {
        /// <summary>
        /// Resets custom state variables before the command instance is returned to the pool.
        /// </summary>
        void ResetState();
    }
}
