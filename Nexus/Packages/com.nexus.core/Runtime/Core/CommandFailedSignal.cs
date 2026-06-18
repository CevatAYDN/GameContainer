using System;

namespace Nexus.Core
{
    public readonly struct CommandFailedSignal
    {
        public readonly Exception Exception;
        public readonly Type SourceCommand;
        public readonly object SourceSignal;

        public CommandFailedSignal(Exception exception, Type sourceCommand, object sourceSignal)
        {
            Exception = exception;
            SourceCommand = sourceCommand;
            SourceSignal = sourceSignal;
        }
    }
}
