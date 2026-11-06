using System;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Throttles disk and cloud saves. It is independent of the models and delays Action delegates.
    ///
    /// Multi-owner: the overloads that take an owner give the calling service its own pending slot,
    /// so when two services share one throttler neither can overwrite the other's pending save (data
    /// loss). The overloads without an owner use the "default" slot and keep the old behaviour.
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
