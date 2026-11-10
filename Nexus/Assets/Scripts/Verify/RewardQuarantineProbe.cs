#if UNITY_ANDROID && NEXUS_DEVICE_PROBE && !UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using Nexus.Ads;
using Nexus.Ads.Unity;
using UnityEngine;
using Facade = Nexus.Ads.Unity.Ads;

namespace NexusVerify.Device
{
    // #415: production static facade + real PlayerPrefs over three process launches.
    internal sealed class RewardQuarantineProbe : MonoBehaviour
    {
        private const string Row = "nexus.ads.reward.pending";
        private const string Raw = "NXRQ3\n!!notbase64|x|-";
        private const string Fixture = "nexus.verify.quarantine415.";
        private string _phase;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var intent = activity.Call<AndroidJavaObject>("getIntent");
            if (intent.Call<string>("getStringExtra", "nexusProbe") != "quarantine415") return;
            var probe = new GameObject("[RewardQuarantineProbe]").AddComponent<RewardQuarantineProbe>();
            probe._phase = intent.Call<string>("getStringExtra", "nexusPhase");
        }

        private IEnumerator Start()
        {
            yield return null;
            try
            {
                if (Application.identifier != "com.UnityTechnologies.com.unity.template.urpblank")
                    throw new InvalidOperationException("Framework fixture app required.");
                if (_phase == "arm")
                {
                    if (PlayerPrefs.HasKey(Fixture + "quarantine") || Facade.PendingReward.HasValue)
                        throw new InvalidOperationException("Fixture or pending receipt already exists.");
                    PlayerPrefs.SetString(Row, Raw);
                    PlayerPrefs.Save();
                    bool unreadable = false;
                    try { _ = Facade.PendingReward; } catch (InvalidDataException) { unreadable = true; }
                    if (!unreadable) throw new InvalidOperationException("Corruption was not detected.");
                    string key = Facade.QuarantineUnreadableRewards();
                    if (key == null || PlayerPrefs.GetString(key) != Raw || Facade.PendingReward.HasValue)
                        throw new InvalidOperationException("Quarantine did not preserve the raw value.");
                    PlayerPrefs.SetString(Fixture + "quarantine", key);
                    var scheduler = gameObject.AddComponent<UnityAdScheduler>();
                    var provider = new SimulatorProvider(scheduler) { Interactive = true };
                    var config = new AdsConfig(new[] { new AdPlacement("reward", AdFormat.Rewarded) }, AdPolicy.Default,
                        new ProviderConfig("probe", null, true, null, false, false, false, "G"), BuildKind.Development);
                    using var controller = new AdController(config, provider, new UnityAdClock(),
                        gameObject.AddComponent<UnityAppVisibility>(), new UnityMainThread(), scheduler,
                        new PlayerPrefsPacingStore(), new UnityAdTelemetry(false), provider);
                    controller.Start();
                    string earned = null;
                    controller.Show("reward", "offer415", result =>
                    {
                        if (result != AdResult.Rewarded || !controller.DeliveringReward.HasValue)
                            throw new InvalidOperationException("No earned receipt after quarantine.");
                        earned = controller.DeliveringReward.Value.Id;
                    });
                    if (!provider.CompleteShow(AdResult.Rewarded) || earned == null || Facade.PendingReward?.Id != earned)
                        throw new InvalidOperationException("Facade cannot read the newly earned reward.");
                    PlayerPrefs.SetString(Fixture + "receipt", earned);
                    PlayerPrefs.Save();
                }
                else if (_phase == "recover" || _phase == "verify")
                {
                    string key = PlayerPrefs.GetString(Fixture + "quarantine", null);
                    string id = PlayerPrefs.GetString(Fixture + "receipt", null);
                    if (key == null || id == null || PlayerPrefs.GetString(key) != Raw)
                        throw new InvalidOperationException("Quarantine lost across process restart.");
                    if (_phase == "recover")
                    {
                        if (Facade.PendingReward?.Id != id || Facade.PendingReward?.Context != "offer415")
                            throw new InvalidOperationException("Earned receipt lost across process restart.");
                        Facade.AcknowledgeReward(id);
                    }
                    if (Facade.PendingReward.HasValue || Facade.QuarantineUnreadableRewards() != null)
                        throw new InvalidOperationException("Acknowledged queue reappeared.");
                    if (_phase == "verify")
                    {
                        PlayerPrefs.DeleteKey(key);
                        PlayerPrefs.DeleteKey(Fixture + "quarantine");
                        PlayerPrefs.DeleteKey(Fixture + "receipt");
                        PlayerPrefs.Save();
                    }
                }
                else throw new InvalidOperationException("Use arm/recover/verify.");
                Debug.Log("[RewardQuarantineProbe] PASS phase=" + _phase);
            }
            catch (Exception error) { Debug.LogError("[RewardQuarantineProbe] FAIL phase=" + _phase + " " + error); }
        }
    }
}
#endif
