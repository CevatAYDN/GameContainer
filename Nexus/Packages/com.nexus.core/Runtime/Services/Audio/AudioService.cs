using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    public interface IAudioRootProvider
    {
        GameObject GetOrCreateRoot();
    }

    public class DefaultAudioRootProvider : IAudioRootProvider
    {
        private GameObject _root;

        public GameObject GetOrCreateRoot()
        {
            if (_root != null) return _root;

            _root = new GameObject("[Nexus_AudioService]");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            return _root;
        }
    }

    public interface IAudioService
    {
        float MasterVolume { get; set; }
        /// <summary>User-controlled BGM preference (slider value). Persisted to PlayerPrefs.
        /// <para>FIX P0.3: do NOT set this directly from gameplay FSMs — it would overwrite
        /// the player's chosen preference whenever the player enters or leaves a level.
        /// Use <see cref="BgmStateMultiplier"/> instead for transient per-state ducking.</para></summary>
        float BgmVolume { get; set; }
        float SfxVolume { get; set; }
        bool IsMuted { get; set; }

        /// <summary>FIX P0.3 — Transient state-driven BGM volume multiplier (0..1).
        /// NOT persisted: the caller (e.g. a gameplay state) is responsible for restoring
        /// 1.0 — typically when returning to the main menu — so the saved user preference
        /// is preserved across level transitions. The effective BGM volume is
        /// <c>MasterVolume × BgmVolume × BgmStateMultiplier</c> when not muted.</summary>
        float BgmStateMultiplier { get; set; }

        void PlayBgm(AudioClip clip, bool loop = true, float fadeDuration = 0.5f);
        void StopBgm(float fadeDuration = 0.5f);
        void PlaySfx(AudioClip clip, float volume = 1f, float pitchMin = 1f, float pitchMax = 1f);
        void PlaySfxWithRandomPitch(AudioClip clip, float minPitch = 0.9f, float maxPitch = 1.1f, float volume = 1f) => PlaySfx(clip, volume, minPitch, maxPitch);
        void PlaySfxAtPosition(AudioClip clip, Vector3 position, float volume = 1f);
    }

    [Preserve]
    public class AudioService : NexusService<IAudioService>, IAudioService
    {
        [Inject] public IPlayerPrefsService PlayerPrefsService { get; set; }
        [Inject] public IAudioRootProvider AudioRootProvider { get; set; }

        private const string KeyMasterVol = "NT_AudioMasterVol";
        private const string KeyBgmVol = "NT_AudioBgmVol";
        private const string KeySfxVol = "NT_AudioSfxVol";
        private const string KeyMuted = "NT_AudioMuted";

        private GameObject _audioRoot;
        private AudioSource _bgmSourceActive;
        private AudioSource _bgmSourceFade;
        private readonly List<AudioSource> _sfxPool = new();

        // Hard cap on the SFX source pool. The old GetAvailableSfxSource grew the pool
        // unboundedly (a new GameObject + interpolated name string per allocation) on
        // SFX-heavy scenes — under a burst of simultaneous sounds the linear scan plus
        // create became effectively O(N²) with permanent memory growth. Once the cap is
        // reached the oldest channel is stolen instead of allocating another source.
        private const int MaxSfxPoolSize = 32;

        private float _masterVolume = 1f;
        private float _bgmVolume = 1f;
        private float _sfxVolume = 1f;
        // FIX P0.3: not persisted on purpose. This is the per-state ducking scalar, driven
        // by gameplay states and restored to 1.0 by the CALLER (e.g. on returning to the
        // main menu) — the service never auto-resets, so a level-load cannot silently
        // overwrite the player's saved BgmVolume slider value.
        private float _bgmStateMultiplier = 1f;
        private bool _isMuted;

        public float MasterVolume
        {
            get => _masterVolume;
            set
            {
                _masterVolume = Mathf.Clamp01(value);
                PlayerPrefsService?.SetFloat(KeyMasterVol, _masterVolume);
                UpdateVolumes();
            }
        }

        public float BgmVolume
        {
            get => _bgmVolume;
            set
            {
                _bgmVolume = Mathf.Clamp01(value);
                PlayerPrefsService?.SetFloat(KeyBgmVol, _bgmVolume);
                UpdateVolumes();
            }
        }

        /// <inheritdoc/>
        public float BgmStateMultiplier
        {
            get => _bgmStateMultiplier;
            set
            {
                // FIX P0.3: deliberately NOT persisted — gameplay states push a transient
                // scalar (Menu: 0.70, Playing: 0.40 / Boss 0.80, Pause: 0.20 per GDD §12)
                // without poisoning the user-saved slider value.
                _bgmStateMultiplier = Mathf.Clamp01(value);
                UpdateVolumes();
            }
        }

        public float SfxVolume
        {
            get => _sfxVolume;
            set
            {
                _sfxVolume = Mathf.Clamp01(value);
                PlayerPrefsService?.SetFloat(KeySfxVol, _sfxVolume);
                UpdateVolumes();
            }
        }

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                _isMuted = value;
                PlayerPrefsService?.SetBool(KeyMuted, _isMuted);
                UpdateVolumes();
            }
        }

        public override ValueTask InitializeAsync(CancellationToken ct)
        {
            _audioRoot = AudioRootProvider?.GetOrCreateRoot();
            if (_audioRoot == null)
            {
                _audioRoot = new GameObject("[Nexus_AudioService]");
                UnityEngine.Object.DontDestroyOnLoad(_audioRoot);
            }

            _bgmSourceActive = _audioRoot.AddComponent<AudioSource>();
            _bgmSourceActive.loop = true;
            _bgmSourceActive.playOnAwake = false;

            _bgmSourceFade = _audioRoot.AddComponent<AudioSource>();
            _bgmSourceFade.loop = true;
            _bgmSourceFade.playOnAwake = false;

            if (PlayerPrefsService != null)
            {
                _masterVolume = PlayerPrefsService.GetFloat(KeyMasterVol, 1f);
                _bgmVolume = PlayerPrefsService.GetFloat(KeyBgmVol, 1f);
                _sfxVolume = PlayerPrefsService.GetFloat(KeySfxVol, 1f);
                _isMuted = PlayerPrefsService.GetBool(KeyMuted, false);
            }

            UpdateVolumes();
            return default;
        }

        private void UpdateVolumes()
        {
            // FIX P0.3: Player preference × State multiplier. Old code wrote the state
            // multiplier (e.g. 0.40 from PlayingState) to PlayerPrefs every level entry,
            // overwriting the user's chosen slider value permanently.
            float effectiveBgm = _isMuted
                ? 0f
                : _masterVolume * _bgmVolume * _bgmStateMultiplier;
            if (_bgmSourceActive != null) _bgmSourceActive.volume = effectiveBgm;
            if (_bgmSourceFade != null) _bgmSourceFade.volume = effectiveBgm;
        }

        public void PlayBgm(AudioClip clip, bool loop = true, float fadeDuration = 0.5f)
        {
            if (clip == null || _bgmSourceActive == null) return;
            if (_bgmSourceActive.clip == clip && _bgmSourceActive.isPlaying) return;

            _bgmSourceActive.clip = clip;
            _bgmSourceActive.loop = loop;
            // FIX P0.3: same effective formula (master × user pref × state mult).
            _bgmSourceActive.volume = _isMuted
                ? 0f
                : _masterVolume * _bgmVolume * _bgmStateMultiplier;
            _bgmSourceActive.Play();
        }

        public void StopBgm(float fadeDuration = 0.5f)
        {
            if (_bgmSourceActive != null)
            {
                _bgmSourceActive.Stop();
                _bgmSourceActive.clip = null;
            }
        }

        public void PlaySfxWithRandomPitch(AudioClip clip, float minPitch = 0.9f, float maxPitch = 1.1f, float volume = 1f) => PlaySfx(clip, volume, minPitch, maxPitch);

        public void PlaySfx(AudioClip clip, float volume = 1f, float pitchMin = 1f, float pitchMax = 1f)
        {
            if (clip == null || _isMuted) return;

            // Guard: Random.Range(float, float) throws when min > max. Mirror the
            // FeedbackService.PlayCustom swap so inverted pitch ranges never crash.
            if (pitchMin > pitchMax) (pitchMin, pitchMax) = (pitchMax, pitchMin);

            var source = GetAvailableSfxSource();
            source.spatialBlend = 0f; // 2D sound
            source.volume = Mathf.Clamp01(volume) * _masterVolume * _sfxVolume;
            source.pitch = UnityEngine.Random.Range(pitchMin, pitchMax);
            source.PlayOneShot(clip);
        }

        public void PlaySfxAtPosition(AudioClip clip, Vector3 position, float volume = 1f)
        {
            if (clip == null || _isMuted) return;

            var source = GetAvailableSfxSource();
            source.transform.position = position;
            source.spatialBlend = 1f; // 3D spatial sound
            source.volume = Mathf.Clamp01(volume) * _masterVolume * _sfxVolume;
            source.pitch = 1f;
            source.PlayOneShot(clip);
        }

        private AudioSource GetAvailableSfxSource()
        {
            for (int i = 0; i < _sfxPool.Count; i++)
            {
                if (!_sfxPool[i].isPlaying)
                    return _sfxPool[i];
            }

            if (_sfxPool.Count >= MaxSfxPoolSize)
            {
                // Pool exhausted — steal the oldest channel instead of growing the pool
                // forever on SFX-heavy scenes. Stop the victim first so the volume / pitch
                // / spatialBlend / position set below do not distort the clip that was
                // still playing on it.
                _sfxPool[0].Stop();
                return _sfxPool[0];
            }

            var newSourceGo = new GameObject($"SFXSource_{_sfxPool.Count}");
            newSourceGo.transform.SetParent(_audioRoot.transform, false);
            var source = newSourceGo.AddComponent<AudioSource>();
            source.playOnAwake = false;
            _sfxPool.Add(source);
            return source;
        }

        public override void Dispose()
        {
            if (_audioRoot != null)
            {
                if (AudioRootProvider is DefaultAudioRootProvider)
                {
                    UnityEngine.Object.Destroy(_audioRoot);
                }
                _audioRoot = null;
            }
            _sfxPool.Clear();
        }
    }
}
