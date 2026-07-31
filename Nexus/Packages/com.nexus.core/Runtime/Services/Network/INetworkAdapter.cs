using System;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    /// <summary>
    /// Official abstraction bridge connecting 3rd-party multiplayer network frameworks
    /// (Photon Fusion, Netcode for GameObjects, Mirror, FishNet) to Nexus SignalBus.
    /// </summary>
    [Preserve]
    public interface INetworkAdapter
    {
        /// <summary>Returns true if the current peer is running as Authoritative Host or Server.</summary>
        bool IsServer { get; }

        /// <summary>Returns true if the current peer is running as Client.</summary>
        bool IsClient { get; }

        /// <summary>Returns true if network session is actively connected.</summary>
        bool IsConnected { get; }

        /// <summary>Current Round-Trip Time (RTT) latency in milliseconds.</summary>
        float LatencyMs { get; }

        /// <summary>Fired when network connection state changes.</summary>
        event Action<bool> OnConnectionChanged;

        /// <summary>Sends a struct signal across the network to specified target peers.</summary>
        void SendSignal<T>(T signal, string targetRecipient = null) where T : struct;
    }
}
