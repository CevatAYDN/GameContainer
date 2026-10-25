using System;
using System.Collections.Generic;
using NUnit.Framework;
using System.Threading.Tasks;
using UnityEngine;
using Nexus.Core.Services;

namespace Nexus.Editor.Tests
{
    [TestFixture]
    public class AudioAndHapticServiceTests
    {
        private class TestAudioRootProvider : IAudioRootProvider
        {
            private GameObject _root;

            public GameObject GetOrCreateRoot()
            {
                if (_root != null) return _root;
                _root = new GameObject("[Nexus_AudioService_Test]");
                // Skip DontDestroyOnLoad for EditMode tests
                return _root;
            }
        }

        [Test]
        public async Task AudioService_VolumeClampingAndMuteState()
        {
            using var audio = new AudioService
            {
                AudioRootProvider = new TestAudioRootProvider()
            };
            await audio.InitializeAsync(default);

            audio.MasterVolume = 0.8f;
            audio.BgmVolume = 0.5f;
            audio.SfxVolume = 0.7f;

            Assert.AreEqual(0.8f, audio.MasterVolume);
            Assert.AreEqual(0.5f, audio.BgmVolume);
            Assert.AreEqual(0.7f, audio.SfxVolume);

            audio.IsMuted = true;
            Assert.IsTrue(audio.IsMuted);

            audio.IsMuted = false;
            Assert.IsFalse(audio.IsMuted);
        }

        [Test]
        public async Task HapticService_VibrateDoesNotThrowExceptions()
        {
            var haptics = new HapticService();
            await haptics.InitializeAsync(default);

            Assert.DoesNotThrow(() => haptics.Vibrate(HapticType.Light));
            Assert.DoesNotThrow(() => haptics.Vibrate(HapticType.Medium));
            Assert.DoesNotThrow(() => haptics.Vibrate(HapticType.Heavy));
            Assert.DoesNotThrow(() => haptics.Vibrate(HapticType.Success));

            haptics.OnDispose();
        }

        // ── Issue #71: the game's store, separate mutes, haptic effects, the audio root contract ──

        private sealed class MemoryPrefs : IPlayerPrefsService
        {
            public readonly Dictionary<string, string> Data = new();
            public void SetString(string key, string value) => Data[key] = value;
            public string GetString(string key, string defaultValue = "") => Data.TryGetValue(key, out var v) ? v : defaultValue;
            public void SetInt(string key, int value) => Data[key] = value.ToString();
            public int GetInt(string key, int defaultValue = 0) => Data.TryGetValue(key, out var v) && int.TryParse(v, out var r) ? r : defaultValue;
            public void SetFloat(string key, float value) => Data[key] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            public float GetFloat(string key, float defaultValue = 0f) => Data.TryGetValue(key, out var v) && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : defaultValue;
            public void SetLong(string key, long value) => Data[key] = value.ToString();
            public long GetLong(string key, long defaultValue = 0L) => Data.TryGetValue(key, out var v) && long.TryParse(v, out var r) ? r : defaultValue;
            public bool GetBool(string key, bool defaultValue = false) => Data.TryGetValue(key, out var v) && bool.TryParse(v, out var r) ? r : defaultValue;
            public void SetBool(string key, bool value) => Data[key] = value.ToString();
            public bool HasKey(string key) => Data.ContainsKey(key);
            public void DeleteKey(string key) => Data.Remove(key);
            public void DeleteAll() => Data.Clear();
            public void Save() { }
        }

        private sealed class ReleasingRootProvider : IAudioRootProvider
        {
            public readonly GameObject Root = new GameObject("[Nexus_AudioService_Released]");
            public GameObject Released;
            public GameObject GetOrCreateRoot() => Root;
            public void ReleaseRoot(GameObject root) => Released = root;
        }

        [Test]
        public async Task AudioService_ReadsAndWritesTheGamesStore_NotThePlayerPrefsService()
        {
            var prefs = new MemoryPrefs();
            var game = new MemoryPrefs();
            game.SetFloat("NT_AudioMasterVol", 0.3f);
            using var audio = new AudioService
            {
                AudioRootProvider = new TestAudioRootProvider(),
                PlayerPrefsService = prefs,
                SettingsStores = new SettingsStores { Audio = game },
            };
            await audio.InitializeAsync(default);

            Assert.AreEqual(0.3f, audio.MasterVolume, 1e-5f);
            audio.BgmVolume = 0.5f;
            audio.IsMusicMuted = true;
            Assert.AreEqual("0.5", game.GetString("NT_AudioBgmVol"));
            Assert.IsTrue(game.GetBool("NT_AudioMusicMuted"));
            Assert.AreEqual(0, prefs.Data.Count, "Nothing may reach the default store once the game supplied one.");
        }

        [Test]
        public async Task HapticService_IsEnabled_IsWrittenToTheStoreAndReadBack()
        {
            var game = new MemoryPrefs();
            var stores = new SettingsStores { Haptics = game };
            var first = new HapticService { PlayerPrefsService = new MemoryPrefs(), SettingsStores = stores };
            await first.InitializeAsync(default);
            first.IsEnabled = false;

            var next = new HapticService { PlayerPrefsService = new MemoryPrefs(), SettingsStores = stores };
            await next.InitializeAsync(default);
            Assert.IsTrue(game.GetBool("NT_HapticsDisabled"));
            Assert.IsFalse(next.IsEnabled, "The setting did not survive a restart: IsEnabled was never persisted.");
        }

        [Test]
        public async Task EconomyAndProgression_PersistToTheGamesStore()
        {
            var prefs = new MemoryPrefs();
            var stores = new SettingsStores { Economy = new MemoryPrefs(), Progression = new MemoryPrefs() };
            var economy = new EconomyService { PlayerPrefsService = prefs, SettingsStores = stores };
            var progression = new ProgressionService { PlayerPrefsService = prefs, SettingsStores = stores };
            await economy.InitializeAsync(default);
            await progression.InitializeAsync(default);
            try
            {
                economy.Earn("coins", 25);
                progression.SetLevel(3);

                Assert.AreEqual(25L, stores.Economy.GetLong("NT_Eco_coins"));
                Assert.AreEqual(3, stores.Progression.GetInt("NT_Prog_CurrentLevel"));
                Assert.AreEqual(0, prefs.Data.Count);
            }
            finally { economy.Dispose(); progression.Dispose(); }
        }

        [Test]
        public async Task AudioService_MusicAndEffectsMuteSeparately()
        {
            var provider = new TestAudioRootProvider();
            using var audio = new AudioService { AudioRootProvider = provider, PlayerPrefsService = new MemoryPrefs() };
            await audio.InitializeAsync(default);
            var root = provider.GetOrCreateRoot();
            var music = root.GetComponents<AudioSource>();
            var clip = AudioClip.Create("click", 64, 1, 44100, false);
            try
            {
                audio.IsMusicMuted = true;
                audio.PlaySfx(clip);
                Assert.AreEqual(0f, music[0].volume, "Muted music plays at zero.");
                Assert.AreEqual(1, root.transform.childCount, "Effects still play while only the music is muted.");

                audio.IsMusicMuted = false;
                audio.IsSfxMuted = true;
                audio.PlaySfx(clip);
                Assert.AreEqual(1f, music[0].volume, 1e-5f, "Music plays while only the effects are muted.");
                Assert.AreEqual(1, root.transform.childCount, "A muted effect does not take a source.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public async Task AudioService_Dispose_HandsAProvidersRootBackWhateverItsType()
        {
            var provider = new ReleasingRootProvider();
            var withProvider = new AudioService { AudioRootProvider = provider, PlayerPrefsService = new MemoryPrefs() };
            await withProvider.InitializeAsync(default);
            withProvider.Dispose();
            Assert.AreSame(provider.Root, provider.Released, "A custom provider's root is returned to it, whatever its type.");
            Assert.IsTrue(provider.Root != null, "The provider decides whether its root is destroyed.");
            UnityEngine.Object.DestroyImmediate(provider.Root);
            // The root the service creates itself (no provider root) is checked in PlayMode: it calls DontDestroyOnLoad.
        }

        [Test]
        public void HapticService_CreatesDistinctEffects_AndRejectsInvalidOnes()
        {
            var haptics = new HapticService();
            var tap = haptics.CreateEffect(18, 70);
            var wave = haptics.CreateWaveform(new long[] { 0, 20, 40, 60 }, new[] { 0, 120, 0, 255 });
            Assert.IsTrue(tap.IsValid && wave.IsValid && tap.Id != wave.Id);
            Assert.DoesNotThrow(() => { haptics.Play(tap); haptics.Play(wave); haptics.Play(default); });

            Assert.Throws<ArgumentOutOfRangeException>(() => haptics.CreateEffect(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => haptics.CreateEffect(10, 256));
            Assert.Throws<ArgumentException>(() => haptics.CreateWaveform(new long[] { 0, 10 }, new[] { 0 }));
            Assert.Throws<ArgumentException>(() => haptics.CreateWaveform(new long[] { 0, 0 }, new[] { 0, 0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => haptics.CreateWaveform(new long[] { 0, 10 }, new[] { 0, 300 }));
        }
    }
}
