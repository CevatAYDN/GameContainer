using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace Nexus.Core.Services
{
    public interface IAudioService
    {
        float MasterVolume { get; set; }
        float BgmVolume { get; set; }
        float SfxVolume { get; set; }
        bool IsMuted { get; set; }

        void PlayBgm(AudioClip clip, bool loop = true, float fadeDuration = 0.5f);
        void StopBgm(float fadeDuration = 0.5f);
        void PlaySfx(AudioClip clip, float volume = 1f, float pitchMin = 1f, float pitchMax = 1f);
        void PlaySfxAtPosition(AudioClip clip, Vector3 position, float volume = 1f);
    }

    [Preserve]
    public class AudioService : IAudioService, INexusService, IDisposable
    {
        [Inject] public IPlayerPrefsService PlayerPrefsService { get; set; }

        private const string KeyMasterVol = "NT_AudioMasterVol";
        private const string KeyBgmVol = "NT_AudioBgmVol";
        private const string KeySfxVol = "NT_AudioSfxVol";
        private const string KeyMuted = "NT_AudioMuted";

        private GameObject _audioRoot;
        private AudioSource _bgmSourceActive;
        private AudioSource _bgmSourceFade;
        private readonly List<AudioSource> _sfxPool = new();

        private float _masterVolume = 1f;
        private float _bgmVolume = 1f;
        private float _sfxVolume = 1f;
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

        public ValueTask InitializeAsync(CancellationToken ct)
        {
            _audioRoot = new GameObject("[Nexus_AudioService]");
            UnityEngine.Object.DontDestroyOnLoad(_audioRoot);

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
            float effectiveBgm = _isMuted ? 0f : _masterVolume * _bgmVolume;
            if (_bgmSourceActive != null) _bgmSourceActive.volume = effectiveBgm;
            if (_bgmSourceFade != null) _bgmSourceFade.volume = effectiveBgm;
        }

        public void PlayBgm(AudioClip clip, bool loop = true, float fadeDuration = 0.5f)
        {
            if (clip == null || _bgmSourceActive == null) return;
            if (_bgmSourceActive.clip == clip && _bgmSourceActive.isPlaying) return;

            _bgmSourceActive.clip = clip;
            _bgmSourceActive.loop = loop;
            _bgmSourceActive.volume = _isMuted ? 0f : _masterVolume * _bgmVolume;
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

        public void PlaySfx(AudioClip clip, float volume = 1f, float pitchMin = 1f, float pitchMax = 1f)
        {
            if (clip == null || _isMuted) return;

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

            var newSourceGo = new GameObject($"SFXSource_{_sfxPool.Count}");
            newSourceGo.transform.SetParent(_audioRoot.transform, false);
            var source = newSourceGo.AddComponent<AudioSource>();
            source.playOnAwake = false;
            _sfxPool.Add(source);
            return source;
        }

        public void OnDispose() => Dispose();

        public void Dispose()
        {
            if (_audioRoot != null)
            {
                UnityEngine.Object.Destroy(_audioRoot);
                _audioRoot = null;
            }
            _sfxPool.Clear();
        }
    }
}
