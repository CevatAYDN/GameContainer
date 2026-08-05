using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core
{
    public interface ICommand
    {
        void Execute();
    }

    public interface ICommand<in TSignal> where TSignal : struct
    {
        void Execute(TSignal signal);
    }

    public interface IAsyncCommand
    {
        ValueTask ExecuteAsync(CancellationToken ct);
    }

    public interface IAsyncCommand<in TSignal> where TSignal : struct
    {
        ValueTask ExecuteAsync(TSignal signal, CancellationToken ct);
    }
}
