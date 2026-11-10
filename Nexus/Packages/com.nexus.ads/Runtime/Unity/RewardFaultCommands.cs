// #412's intent commands must not exist in release assemblies.
#pragma warning disable UAC0009
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Globalization;
using UnityEngine;

namespace Nexus.Ads.Unity
{
    /// <summary>Same explicit command surface for the panel and adb. No receipt is ever fabricated.</summary>
    public static class RewardFaultCommands
    {
        public static void Execute(IRewardFaultDebug provider, string command)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            if (command != null && command.StartsWith("delay:", StringComparison.Ordinal) &&
                double.TryParse(command.Substring(6), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) &&
                !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds >= 0 && seconds <= 120)
                provider.RewardFaults.DebugDelayReward(TimeSpan.FromSeconds(seconds));
            else if (command == "next") provider.RewardFaults.DebugDeliverRewardDuringNextShow();
            else if (command == "replay")
            {
                if (!provider.RewardFaults.DebugReplayLastReward())
                    throw new InvalidOperationException("No delivered SDK reward is available for replay.");
            }
            else if (command == "reset") provider.RewardFaults.DebugReset();
            else throw new ArgumentException("Use delay:<0..120 seconds>, next, replay or reset.", nameof(command));
            Debug.Log("[Nexus Ads Fault] command=" + command);
        }
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    // #416: read only on startup/focus gain. Idle development captures must not pay
    // for periodic JNI reflection. For a foreground adb command, HOME then am start
    // produces the focus transition without killing a held reward (see the guide).
    // The extra is consumed once and removed before execution; repeated user commands need a new am start.
    public sealed class RewardFaultIntent : MonoBehaviour
    {
        private const string Extra = "nexus.ads.rewardFault";

        private void Start() => ReadIntent();

        private void OnApplicationFocus(bool focused)
        {
            if (focused) ReadIntent();
        }

        private void ReadIntent()
        {
            var bootstrap = AdsBootstrap.Instance;
            if (bootstrap == null || !(bootstrap.Provider is IRewardFaultDebug provider)) return;
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                if (intent == null || !intent.Call<bool>("hasExtra", Extra)) return;
                string command = intent.Call<string>("getStringExtra", Extra);
                intent.Call("removeExtra", Extra);
                RewardFaultCommands.Execute(provider, command);
            }
            catch (Exception error) { Debug.LogWarning("[Nexus Ads Fault] intent: " + error.Message); }
        }
    }
#endif
}
#endif
