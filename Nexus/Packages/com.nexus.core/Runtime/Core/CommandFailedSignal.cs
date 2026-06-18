using System;

namespace Nexus.Core
{
    /// <summary>
    /// Signal fired when a command execution fails with an unhandled exception.
    /// Contains the exception details and references to the source command and signal.
    /// </summary>
    public readonly struct CommandFailedSignal
    {
        /// <summary>The exception that caused the command to fail.</summary>
        public readonly Exception Exception;

        /// <summary>The <see cref="Type"/> of the command that failed.</summary>
        public readonly Type SourceCommand;

        /// <summary>The original signal instance that triggered the failed command.</summary>
        public readonly object SourceSignal;

        /// <summary>Creates a new <see cref="CommandFailedSignal"/> instance.</summary>
        /// <param name="exception">The exception that caused the failure.</param>
        /// <param name="sourceCommand">The <see cref="Type"/> of the command that failed.</param>
        /// <param name="sourceSignal">The signal that triggered the failed command.</param>
        public CommandFailedSignal(Exception exception, Type sourceCommand, object sourceSignal)
        {
            Exception = exception;
            SourceCommand = sourceCommand;
            SourceSignal = sourceSignal;
        }
    }
}
