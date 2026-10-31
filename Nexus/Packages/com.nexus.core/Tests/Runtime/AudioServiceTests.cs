using System;
using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Tests
{
    /// <summary>The audio service with real sources and real fades: its root (issue #71) and its music (issue #186).</summary>
    public class AudioServiceTests
    {
        private sealed class NullRootProvider : IAudioRootProvider
        {
            public GameObject GetOrCreateRoot() => null;
        }

        private sealed class TestRootProvider : IAudioRootProvider
        {
            public GameObject Root;
            public GameObject GetOrCreateRoot() => Root != null ? Root : Root = new GameObject("[AudioServiceTests]");
            public void ReleaseRoot(GameObject root) => UnityEngine.Object.Destroy(root);
        }

        private static GameObject OwnRoot(AudioService audio)
            => (GameObject)typeof(AudioService).GetField("_audioRoot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(audio);

        private static IEnumerator WaitSeconds(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        private AudioService _audio;
        private TestRootProvider _provider;
        private AudioClip _trackA;
        private AudioClip _trackB;

        private AudioSource[] Music => _provider.Root.GetComponents<AudioSource>();

        [SetUp]
        public void SetUp()
        {
            _provider = new TestRootProvider();
            _audio = new AudioService { AudioRootProvider = _provider };
            _audio.InitializeAsync(default).GetAwaiter().GetResult();
            _trackA = AudioClip.Create("trackA", 44100, 1, 44100, false);
            _trackB = AudioClip.Create("trackB", 44100, 1, 44100, false);
        }

        [TearDown]
        public void TearDown()
        {
            _audio?.Dispose();
            UnityEngine.Object.Destroy(_trackA);
            UnityEngine.Object.Destroy(_trackB);
        }

        [UnityTest]
        public IEnumerator Dispose_DestroysTheRootTheServiceCreated()
        {
            var audio = new AudioService { AudioRootProvider = new NullRootProvider() };
            audio.InitializeAsync(default).GetAwaiter().GetResult();
            var own = OwnRoot(audio);
            Assert.IsNotNull(own, "With no provider root the service creates its own.");

            audio.Dispose();
            yield return null; // Destroy completes at the end of the frame in play mode.
            Assert.IsTrue(own == null, "The root the service created itself leaked after Dispose.");
        }

        // DeepSeek B6, folded into #186: an effect played before InitializeAsync created a root the service did not mark as its
        // own, so Dispose left it behind, and InitializeAsync then saw a root and created no music sources
        [UnityTest]
        public IEnumerator AnEffectBeforeInitialize_CreatesNoRoot_AndTheServiceStillStartsWithItsMusic()
        {
            var audio = new AudioService { AudioRootProvider = new NullRootProvider() };
            LogAssert.Expect(LogType.Warning, new Regex(@"\[AudioService\] An effect was played before InitializeAsync or after Dispose"));
            audio.PlaySfx(_trackA);
            Assert.IsTrue(OwnRoot(audio) == null, "an effect before InitializeAsync created a root");

            audio.InitializeAsync(default).GetAwaiter().GetResult();
            var own = OwnRoot(audio);
            audio.PlayBgm(_trackA);
            Assert.IsTrue(Array.Exists(own.GetComponents<AudioSource>(), s => s.clip == _trackA && s.isPlaying), "the music does not play");

            audio.Dispose();
            yield return null;
            Assert.IsTrue(own == null, "the root leaked after Dispose");
        }

        // Issue #186: StopBgm stopped only the incoming track of a crossfade; the outgoing one looped on until the next PlayBgm
        [UnityTest]
        public IEnumerator StopBgm_DuringACrossfade_StopsBothTracks()
        {
            _audio.PlayBgm(_trackA);
            _audio.PlayBgm(_trackB, fadeDuration: 0.5f);
            yield return null;
            yield return null;

            _audio.StopBgm();
            yield return WaitSeconds(0.7f);

            Assert.IsFalse(Array.Exists(Music, s => s.isPlaying), "a track still plays after StopBgm");
        }

        // Issue #186: the same with a fade-out: the crossfade stopped and its outgoing track looped at the volume it had reached
        [UnityTest]
        public IEnumerator StopBgmWithAFade_DuringACrossfade_FadesOutBothTracks()
        {
            _audio.PlayBgm(_trackA);
            _audio.PlayBgm(_trackB, fadeDuration: 0.5f);
            yield return null;
            yield return null;

            _audio.StopBgm(0.2f);
            yield return WaitSeconds(0.5f);

            Assert.IsFalse(Array.Exists(Music, s => s.isPlaying), "a track still plays after the fade-out");
        }

        // Issue #186: a level restart plays the track StopBgm is fading out; PlayBgm returned early and the fade then stopped
        // the track, leaving silence
        [UnityTest]
        public IEnumerator PlayBgm_OfTheTrackBeingFadedOut_KeepsItPlayingAtFullVolume()
        {
            _audio.PlayBgm(_trackA);
            _audio.StopBgm(0.3f);
            yield return null;

            _audio.PlayBgm(_trackA);
            yield return WaitSeconds(0.5f);

            var playing = Array.FindAll(Music, s => s.isPlaying);
            Assert.AreEqual(1, playing.Length, "one track plays");
            Assert.AreSame(_trackA, playing[0].clip);
            Assert.AreEqual(1f, playing[0].volume, 1e-4f);
        }
    }
}
