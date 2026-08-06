using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Async lifecycle contract for UI screens/windows. <see cref="ScreenView"/> implements
    /// this so every screen integrates with <see cref="UIManager"/>'s async open/close
    /// pipeline (opening → opened → closing → closed).
    /// </summary>
    public interface IUIWindowLifecycle
    {
        ValueTask OnOpeningAsync(object args, CancellationToken ct);
        ValueTask OnOpenedAsync(CancellationToken ct);
        ValueTask OnClosingAsync(CancellationToken ct);
        ValueTask OnClosedAsync(CancellationToken ct);
    }
}
