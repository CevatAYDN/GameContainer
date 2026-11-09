using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Nexus.Tests.Runtime
{
    [TestFixture]
    [Category("RequiresPlayMode")]
    public sealed class FullReviewUnityServiceAudioTests
    {
        private sealed class RetainingRootProvider : IAudioRootProvider
        {
            public GameObject Root { get; } = new GameObject("FullReviewUnityServiceAudioRoot");
            public GameObject GetOrCreateRoot() => Root;
            public void ReleaseRoot(GameObject root) { }
        }

        private readonly List<AudioService> _services = new List<AudioService>();
        private readonly List<AudioClip> _clips = new List<AudioClip>();
        private RetainingRootProvider _provider;
        private GameObject _listener;

        [SetUp]
        public void CreateTestOwnedAudioRoot()
        {
            _provider = new RetainingRootProvider();
            foreach (var listener in Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Exclude))
                if (listener.isActiveAndEnabled) return;
            _listener = new GameObject("FullReviewUnityServiceAudioListener", typeof(AudioListener));
        }

        [UnityTearDown]
        public IEnumerator DisposeTestOwnedAudio()
        {
            try
            {
                for (int i = _services.Count - 1; i >= 0; i--) _services[i].Dispose();
            }
            finally
            {
                _services.Clear();
                if (_provider?.Root != null) Object.DestroyImmediate(_provider.Root);
                _provider = null;
                foreach (var clip in _clips)
                    if (clip != null) Object.DestroyImmediate(clip);
                _clips.Clear();
                if (_listener != null) Object.DestroyImmediate(_listener);
                _listener = null;
            }
            yield return null;
        }

        // U03 / #363: observe sources on the injected root, never the private coroutine runner.
        [UnityTest]
        public IEnumerator DisposingOneAudioService_DoesNotInterruptAnotherServicesCrossfadeOnTheSharedRoot()
        {
            var first = CreateAudioService();
            var survivor = CreateAudioService();
            var firstTrack = CreateTrack("FullReviewOwnerTrack");
            var outgoingTrack = CreateTrack("FullReviewOutgoingTrack");
            var incomingTrack = CreateTrack("FullReviewIncomingTrack");

            first.PlayBgm(firstTrack, fadeDuration: 0.1f);
            survivor.PlayBgm(outgoingTrack);
            AudioSource outgoing = null;
            yield return RequirePlayback(outgoingTrack, source => outgoing = source);

            survivor.PlayBgm(incomingTrack, fadeDuration: 0.2f);
            var incoming = SourceWithClip(incomingTrack);
            Assert.IsTrue(incoming != null, "U03 control: the incoming track must have a source.");
            yield return RequireCompletedCrossfade(outgoing, incoming,
                "U03 control without disposal");

            survivor.PlayBgm(outgoingTrack, fadeDuration: 0.5f);
            var replacement = SourceWithClip(outgoingTrack);
            Assert.IsTrue(replacement != null, "U03 setup: the next incoming track must have a source.");
            Assert.IsTrue(incoming.isPlaying, "U03 setup: the outgoing looping track must still play.");
            first.Dispose();

            yield return RequireCompletedCrossfade(incoming, replacement,
                "U03 after disposing the other service");
            Assert.IsTrue(_provider.Root != null, "U03: the retaining provider's root must remain alive.");
        }

        // U04 / #366: these assertions concern live source state, not audible output or a device.
        [UnityTest]
        public IEnumerator EffectsMute_SilencesAnAlreadyPlayingEffect()
            => ExistingEffectIsSilenced(audio => audio.IsSfxMuted = true, audio => audio.IsSfxMuted = false, "effects mute");

        [UnityTest]
        public IEnumerator MasterMute_SilencesAnAlreadyPlayingEffect()
            => ExistingEffectIsSilenced(audio => audio.IsMuted = true, audio => audio.IsMuted = false, "master mute");

        [UnityTest]
        public IEnumerator ZeroMasterVolume_SilencesAnAlreadyPlayingEffect()
            => ExistingEffectIsSilenced(audio => audio.MasterVolume = 0f, audio => audio.MasterVolume = 1f, "zero master volume");

        [UnityTest]
        public IEnumerator ZeroEffectsVolume_SilencesAnAlreadyPlayingEffect()
            => ExistingEffectIsSilenced(audio => audio.SfxVolume = 0f, audio => audio.SfxVolume = 1f, "zero effects volume");

        [UnityTest]
        public IEnumerator SpatialEffect_RestoresPerPlayGainAfterMuteAndChannelChanges()
            => ExistingEffectIsSilenced(audio => audio.IsSfxMuted = true, audio => audio.IsSfxMuted = false,
                "spatial effects mute", spatial: true);

        [TestCase(false)]
        [TestCase(true)]
        public void FirstPlayOnAnAllocatedVoice_DoesNotAllocateGainBookkeeping(bool spatial)
        {
            var clip = CreateTrack("FirstVoiceAllocation");
            var control = CreateAudioService();
            if (spatial) control.PlaySfxAtPosition(clip, Vector3.zero);
            else control.PlaySfx(clip);

            var audio = CreateAudioService();
            // Separate native voice creation (which allocates) from first playback, the #411 regression.
            typeof(AudioService).GetMethod("GetAvailableSfxSource",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(audio, null);
            var voice = Array.FindLast(_provider.Root.GetComponentsInChildren<AudioSource>(),
                source => source.gameObject.name == "SFXSource_0");
            Assert.That(voice, Is.Not.Null);
            long allocated;
            using (var probe = new Nexus.Core.GcAllocationProbe())
            {
                if (spatial) audio.PlaySfxAtPosition(clip, Vector3.zero, 0.75f);
                else audio.PlaySfx(clip, 0.75f);
                allocated = probe.Stop();
            }

            Assert.That(allocated, Is.Zero, "First playback on an allocated voice must not grow a gain dictionary (#411).");
            audio.IsSfxMuted = true;
            audio.IsSfxMuted = false;
            Assert.That(voice.volume, Is.EqualTo(0.75f).Within(0.0001f), "Keep the live-gain fix from #366.");
        }

        private IEnumerator ExistingEffectIsSilenced(Action<IAudioService> change, Action<IAudioService> restore,
            string setting, bool spatial = false)
        {
            var audio = CreateAudioService();
            var clip = CreateTrack("FullReviewExistingEffect");
            if (spatial) audio.PlaySfxAtPosition(clip, Vector3.zero, volume: 0.75f);
            else audio.PlaySfx(clip, volume: 0.75f);
            AudioSource source = null;
            yield return RequirePlayback(null, playing => source = playing);
            Assert.IsTrue(source.isPlaying && !source.mute && source.volume > 0f,
                "U04 setup: an unmuted effect must be playing before the setting changes.");

            change(audio);

            Assert.IsTrue(source == null || !source.isPlaying || source.mute || source.volume <= 0f,
                $"U04: {setting} left the existing effect's source live at its previous gain. {Describe(source)}");

            restore(audio);
            Assert.IsTrue(source != null && source.isPlaying && !source.mute,
                "U04: restoring the setting must preserve ongoing playback, not restart a stopped effect.");
            Assert.That(source.volume, Is.EqualTo(0.75f).Within(0.0001f),
                "U04: going through zero must not lose the effect's per-play gain.");
            audio.MasterVolume = 0.4f;
            audio.SfxVolume = 0.5f;
            Assert.That(source.volume, Is.EqualTo(0.15f).Within(0.0001f),
                "U04: live master/effects changes must retain this effect's 0.75 per-play gain.");
        }

        private AudioService CreateAudioService()
        {
            var audio = new AudioService { AudioRootProvider = _provider };
            _services.Add(audio);
            audio.InitializeAsync(default).GetAwaiter().GetResult();
            return audio;
        }

        private AudioClip CreateTrack(string name)
        {
            const int sampleRate = 8000;
            var samples = new float[sampleRate * 10];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = 0.1f * Mathf.Sin(2f * Mathf.PI * 440f * i / sampleRate);
            var clip = AudioClip.Create(name, samples.Length, 1, sampleRate, false);
            _clips.Add(clip);
            Assert.IsTrue(clip.SetData(samples, 0), "Audio setup: Unity must accept the test clip's PCM samples.");
            return clip;
        }

        private AudioSource SourceWithClip(AudioClip clip)
        {
            foreach (var source in _provider.Root.GetComponentsInChildren<AudioSource>())
                if (source.clip == clip) return source;
            return null;
        }

        private IEnumerator RequirePlayback(AudioClip clip, Action<AudioSource> observe)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 2d;
            do
            {
                foreach (var source in _provider.Root.GetComponentsInChildren<AudioSource>())
                {
                    if (!source.isPlaying || (clip != null && source.clip != clip)) continue;
                    observe(source);
                    yield break;
                }
                yield return null;
            } while (Time.realtimeSinceStartupAsDouble < deadline);
            Assert.Inconclusive(
                "Audio setup: no live playback was observed. Run PlayMode with Unity audio enabled; " +
                "this is not evidence for or against U03/U04. Global audio settings were not reset.");
        }

        private static IEnumerator RequireCompletedCrossfade(AudioSource outgoing, AudioSource incoming, string phase)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 5d;
            while (!CrossfadeCompleted(outgoing, incoming) && Time.realtimeSinceStartupAsDouble < deadline)
                yield return null;
            Assert.IsTrue(CrossfadeCompleted(outgoing, incoming),
                $"{phase}: the incoming track must reach full gain and the outgoing track must stop. " +
                $"Outgoing: {Describe(outgoing)}; incoming: {Describe(incoming)}");
        }

        private static bool CrossfadeCompleted(AudioSource outgoing, AudioSource incoming)
            => (outgoing == null || !outgoing.isPlaying) && incoming != null && incoming.isPlaying
                && Mathf.Abs(incoming.volume - 1f) <= 0.001f;

        private static string Describe(AudioSource source)
            => source == null ? "destroyed" : $"playing={source.isPlaying}, mute={source.mute}, volume={source.volume}";
    }
}
