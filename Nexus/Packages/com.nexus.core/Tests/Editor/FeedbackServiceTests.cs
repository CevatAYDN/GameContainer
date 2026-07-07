using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Nexus.Core.Services;
using Vector3 = UnityEngine.Vector3;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class FeedbackServiceTests
    {
        private class MockHapticService : IHapticService
        {
            public HapticType LastVibrated { get; private set; } = (HapticType)(-1);
            public int VibrateCount { get; private set; }
            public bool IsEnabled { get; set; } = true;

            public void Vibrate(HapticType type = HapticType.Light)
            {
                LastVibrated = type;
                VibrateCount++;
            }
        }

        private class MockAudioService : IAudioService
        {
            public float MasterVolume { get; set; }
            public float BgmVolume { get; set; }
            public float SfxVolume { get; set; }
            public bool IsMuted { get; set; }

            public AudioClip LastSfxClip { get; private set; }
            public float LastPitchMin { get; private set; }
            public float LastPitchMax { get; private set; }
            public int PlaySfxCount { get; private set; }

            public ValueTask InitializeAsync(CancellationToken ct) => default;
            public void PlayBgm(AudioClip clip, bool loop = true, float fadeDuration = 0.5f) { }
            public void StopBgm(float fadeDuration = 0.5f) { }
            public void PlaySfx(AudioClip clip, float volumeScale = 1f, float pitchMin = 1f, float pitchMax = 1f)
            {
                LastSfxClip = clip;
                LastPitchMin = pitchMin;
                LastPitchMax = pitchMax;
                PlaySfxCount++;
            }
            public void PlaySfxAtPosition(AudioClip clip, Vector3 position, float volumeScale = 1f)
            {
                LastSfxClip = clip;
                PlaySfxCount++;
            }
            public void OnDispose() { }
        }

        [Test]
        public async Task FeedbackService_InitializeAndDispose_ExecutesCleanly()
        {
            var feedback = new FeedbackService();
            await feedback.InitializeAsync(default);
            Assert.DoesNotThrow(() => feedback.OnDispose());
        }

        [Test]
        public void FeedbackService_PlayPresets_WithoutDependencies_DoesNotThrow()
        {
            var feedback = new FeedbackService();
            foreach (FeedbackPreset preset in Enum.GetValues(typeof(FeedbackPreset)))
            {
                Assert.DoesNotThrow(() => feedback.Play(preset));
            }
        }

        [Test]
        public void FeedbackService_PlayPresets_TriggersHapticService()
        {
            var mockHaptics = new MockHapticService();
            var feedback = new FeedbackService
            {
                HapticService = mockHaptics
            };

            feedback.Play(FeedbackPreset.LightClick);
            Assert.AreEqual(HapticType.Light, mockHaptics.LastVibrated);

            feedback.Play(FeedbackPreset.MediumImpact);
            Assert.AreEqual(HapticType.Medium, mockHaptics.LastVibrated);

            feedback.Play(FeedbackPreset.HeavyImpact);
            Assert.AreEqual(HapticType.Heavy, mockHaptics.LastVibrated);

            feedback.Play(FeedbackPreset.CoinCollect);
            Assert.AreEqual(HapticType.Selection, mockHaptics.LastVibrated);

            feedback.Play(FeedbackPreset.SuccessFanfare);
            Assert.AreEqual(HapticType.Success, mockHaptics.LastVibrated);

            feedback.Play(FeedbackPreset.WarningAlert);
            Assert.AreEqual(HapticType.Warning, mockHaptics.LastVibrated);

            feedback.Play(FeedbackPreset.ErrorFailure);
            Assert.AreEqual(HapticType.Heavy, mockHaptics.LastVibrated);

            Assert.AreEqual(7, mockHaptics.VibrateCount);
        }

        [Test]
        public void FeedbackService_PlayCustom_TriggersAudioAndHapticServices()
        {
            var mockHaptics = new MockHapticService();
            var mockAudio = new MockAudioService();
            var feedback = new FeedbackService
            {
                HapticService = mockHaptics,
                AudioService = mockAudio
            };

            var testClip = AudioClip.Create("TestClip", 44100, 1, 44100, false);
            try
            {
                feedback.PlayCustom(testClip, HapticType.Selection, 0.9f, 1.1f);

                Assert.AreEqual(HapticType.Selection, mockHaptics.LastVibrated);
                Assert.AreEqual(testClip, mockAudio.LastSfxClip);
                Assert.AreEqual(0.9f, mockAudio.LastPitchMin);
                Assert.AreEqual(1.1f, mockAudio.LastPitchMax);
                Assert.AreEqual(1, mockAudio.PlaySfxCount);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(testClip);
            }
        }
    }
}
