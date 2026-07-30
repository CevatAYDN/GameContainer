using System;
using System.Collections.Generic;
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
    [StubService("Integrate with platform-specific haptic/audio SDKs or replace with native implementations")]
    public class FeedbackService : IFeedbackService, INexusService
    {
        [Inject] public IAudioService AudioService { get; set; }
        [Inject] public IHapticService HapticService { get; set; }

        /// <summary>
        /// Optional mapping from preset to AudioClip. When set, Play(FeedbackPreset)
        /// will also trigger audio playback alongside haptic feedback.
        /// </summary>
        public Dictionary<FeedbackPreset, AudioClip> PresetAudioClips { get; set; }

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
                    PlayPresetSound(preset, 1f, 1f);
                    break;

                case FeedbackPreset.MediumImpact:
                    HapticService?.Vibrate(HapticType.Medium);
                    PlayPresetSound(preset, 0.9f, 1.1f);
                    break;

                case FeedbackPreset.HeavyImpact:
                    HapticService?.Vibrate(HapticType.Heavy);
                    PlayPresetSound(preset, 0.8f, 1f);
                    break;

                case FeedbackPreset.CoinCollect:
                    HapticService?.Vibrate(HapticType.Selection);
                    PlayPresetSound(preset, 1.2f, 1.5f);
                    break;

                case FeedbackPreset.SuccessFanfare:
                    HapticService?.Vibrate(HapticType.Success);
                    PlayPresetSound(preset, 1f, 1f);
                    break;

                case FeedbackPreset.WarningAlert:
                    HapticService?.Vibrate(HapticType.Warning);
                    PlayPresetSound(preset, 0.8f, 1f);
                    break;

                case FeedbackPreset.ErrorFailure:
                    HapticService?.Vibrate(HapticType.Heavy);
                    PlayPresetSound(preset, 0.6f, 0.9f);
                    break;
            }
        }

        private void PlayPresetSound(FeedbackPreset preset, float pitchMin, float pitchMax)
        {
            if (PresetAudioClips == null || AudioService == null) return;
            if (PresetAudioClips.TryGetValue(preset, out var clip) && clip != null)
            {
                AudioService.PlaySfx(clip, 1f, pitchMin, pitchMax);
            }
        }

        public void PlayCustom(AudioClip clip = null, HapticType hapticType = HapticType.Light, float pitchMin = 1f, float pitchMax = 1f)
        {
            HapticService?.Vibrate(hapticType);
            if (clip != null && AudioService != null)
            {
                // Guard: ensure pitchMin <= pitchMax for UnityEngine.Random.Range
                if (pitchMin > pitchMax)
                {
                    (pitchMin, pitchMax) = (pitchMax, pitchMin);
                }
                AudioService.PlaySfx(clip, 1f, pitchMin, pitchMax);
            }
        }

        public void OnDispose() { }
    }
}
