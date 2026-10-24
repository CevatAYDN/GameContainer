using Nexus.Ads;
using Nexus.Ads.Unity;
using UnityEngine;

// Not under the Nexus.Ads namespace on purpose: there the name Ads would bind to the namespace, not to the facade class.
namespace NexusAdsSamples.PlainUnity
{
    /// <summary>
    /// Ads without Nexus, without a canvas and without a settings asset: drop this component into any
    /// scene. It makes sure an <see cref="AdsBootstrap"/> exists (with in-memory settings when the
    /// project has none yet), and draws two buttons through the static <see cref="Ads"/> facade. In the
    /// editor the simulator overlay plays the ad; on a device the gate decides between the SDK and the
    /// simulator. The proof that the core is independent: nothing here references com.nexus.core.
    /// </summary>
    public sealed class PlainUnityAdsDemo : MonoBehaviour
    {
        public const string RewardedPlacement = "shop_refill";
        public const string InterstitialPlacement = "level_end";

        private int _coins;
        private string _last = "";

        private void Awake()
        {
            if (AdsBootstrap.Instance != null || FindAnyObjectByType<AdsBootstrap>() != null) return;
            var settings = AdsSettings.Load();
            if (settings == null)
            {
                // no asset yet (Project Settings > Nexus Ads creates one): in-memory settings keep the demo running on the simulator
                settings = ScriptableObject.CreateInstance<AdsSettings>();
                settings.SelectedMediation = AdProviderKind.LevelPlay;
                settings.Placements.Add(new PlacementEntry { Name = RewardedPlacement, Format = AdFormat.Rewarded });
                settings.Placements.Add(new PlacementEntry { Name = InterstitialPlacement, Format = AdFormat.Interstitial, NaturalBreak = true });
            }
            var host = new GameObject("AdsBootstrap");
            host.SetActive(false);
            host.AddComponent<AdsBootstrap>().Configure(settings);
            host.SetActive(true);
        }

        private void Start()
        {
            var receipt = Ads.PendingReward;
            if (receipt.HasValue) { _coins += 50; Ads.AcknowledgeReward(receipt.Value.Id); _last = "paid a reward the last run was killed in the middle of"; }
        }

        private void OnGUI()
        {
            float scale = Mathf.Max(1f, Screen.dpi / 160f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            GUILayout.BeginArea(new Rect(16, 16, 320, 220));
            GUILayout.Label("Coins: " + _coins);
            GUI.enabled = Ads.IsReady(RewardedPlacement);
            if (GUILayout.Button(GUI.enabled ? "Watch an ad: +50 coins" : "Rewarded ad loading..."))
                Ads.Show(RewardedPlacement, result =>
                {
                    _last = "rewarded: " + result;
                    if (result != AdResult.Rewarded) return;
                    _coins += 50;
                    var receipt = Ads.PendingReward;
                    if (receipt.HasValue) Ads.AcknowledgeReward(receipt.Value.Id);
                });
            GUI.enabled = true;
            if (GUILayout.Button("Finish level (interstitial at the natural break)")) Ads.Show(InterstitialPlacement, result => _last = "interstitial: " + result);
            if (Ads.PrivacyOptionsRequired && GUILayout.Button("Privacy options")) Ads.ShowPrivacyOptions();
            GUILayout.Label(_last);
            GUILayout.EndArea();
            GUI.matrix = Matrix4x4.identity;
        }
    }
}
