using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    public enum FeedbackPreset
    {
        LightClick,
        MediumImpact,
        HeavyImpact,
        CoinCollect,
        SuccessFanfare,
        WarningAlert,
        ErrorFailure
    }

    public interface IFeedbackService
    {
        void Play(FeedbackPreset preset);
        void PlayCustom(AudioClip clip = null, HapticType hapticType = HapticType.Light, float pitchMin = 1f, float pitchMax = 1f);
    }

    [Preserve]
    public class FeedbackService : IFeedbackService, INexusService
    {
        [Inject] public IAudioService AudioService { get; set; }
        [Inject] public IHapticService HapticService { get; set; }

        public ValueTask InitializeAsync(CancellationToken ct)
        {
            return default;
        }

        public void Play(FeedbackPreset preset)
        {
            switch (preset)
            {
                case FeedbackPreset.LightClick:
                    HapticService?.Vibrate(HapticType.Light);
                    break;

                case FeedbackPreset.MediumImpact:
                    HapticService?.Vibrate(HapticType.Medium);
                    break;

                case FeedbackPreset.HeavyImpact:
                    HapticService?.Vibrate(HapticType.Heavy);
                    break;

                case FeedbackPreset.CoinCollect:
                    HapticService?.Vibrate(HapticType.Selection);
                    break;

                case FeedbackPreset.SuccessFanfare:
                    HapticService?.Vibrate(HapticType.Success);
                    break;

                case FeedbackPreset.WarningAlert:
                    HapticService?.Vibrate(HapticType.Warning);
                    break;

                case FeedbackPreset.ErrorFailure:
                    HapticService?.Vibrate(HapticType.Heavy);
                    break;
            }
        }

        public void PlayCustom(AudioClip clip = null, HapticType hapticType = HapticType.Light, float pitchMin = 1f, float pitchMax = 1f)
        {
            HapticService?.Vibrate(hapticType);
            if (clip != null && AudioService != null)
            {
                AudioService.PlaySfx(clip, 1f, pitchMin, pitchMax);
            }
        }

        public void OnDispose() { }
    }
}
