namespace Nexus.Core
{
    public interface IRecoveryStrategy
    {
        RecoveryDecision OnCommandFailed(CommandFailureContext failure);
    }
}
