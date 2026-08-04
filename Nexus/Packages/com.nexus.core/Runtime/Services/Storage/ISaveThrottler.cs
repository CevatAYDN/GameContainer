using System;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Disk ve bulut kayıt işlemlerini throttle (kısıtlayarak) gerçekleştiren servis.
    /// Modellerden tamamen bağımsız çalışır ve Action delegate'lerini geciktirir.
    ///
    /// Multi-owner: owner parametreli overload'lar, çağıran servisin kendi pending
    /// slot'una sahip olmasını sağlar — iki farklı servis aynı throttler'ı paylaştığında
    /// biri diğerinin bekleyen kaydını üzerine yazamaz (veri kaybı). Owner'sız overload'lar
    /// "default" slot'a gider ve eski davranışı korur.
    /// </summary>
    public interface ISaveThrottler
    {
        void TryRequestSave(Action saveAction);
        void TryRequestSave(string owner, Action saveAction);
        void ForceSave(Action saveAction);
        void ForceSave(string owner, Action saveAction);
        float SecondsSinceLastSave { get; }
        float GetSecondsSinceLastSave(string owner);
        void Flush();
        void Flush(string owner);
    }
}
