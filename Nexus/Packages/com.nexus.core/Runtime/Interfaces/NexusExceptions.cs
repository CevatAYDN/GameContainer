using System;

namespace Nexus.Core
{
    // Common base class so callers can catch ALL Nexus framework errors
    // with a single catch(NexusException) instead of enumerating every concrete type.
    // Existing catch(Exception) and catch(SpecificNexusException) sites are unaffected.
    public abstract class NexusException : Exception
    {
        protected NexusException(string message) : base(message) { }
    }

    public class NexusReentrancyException : NexusException
    {
        public NexusReentrancyException(string message) : base(message) { }
    }

    public class NexusAsyncOverflowException : NexusException
    {
        public NexusAsyncOverflowException(string message) : base(message) { }
    }

    public class UnauthorizedPluginAccessException : NexusException
    {
        public UnauthorizedPluginAccessException(string message) : base(message) { }
    }

    public class NexusSyncAsyncMismatchException : NexusException
    {
        public NexusSyncAsyncMismatchException(string message) : base(message) { }
    }

    /// <summary>
    /// Thrown at startup when <see cref="ContextData.FailOnValidationErrors"/> is enabled and
    /// DI validation discovers issues (missing bindings, captive dependencies, constructor explosion).
    /// The <see cref="Exception.Message"/> lists every validation issue for triage.
    /// </summary>
    public class NexusDiValidationException : NexusException
    {
        public NexusDiValidationException(string message) : base(message) { }
    }
}
