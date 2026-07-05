using System;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Disk ve bulut kayıt işlemlerini throttle (kısıtlayarak) gerçekleştiren servis.
    /// Modellerden tamamen bağımsız çalışır ve Action delegate'lerini geciktirir.
    /// </summary>
    public interface ISaveThrottler
    {
        void TryRequestSave(Action saveAction);
        void ForceSave(Action saveAction);
        float SecondsSinceLastSave { get; }
        void Flush();
    }
}
