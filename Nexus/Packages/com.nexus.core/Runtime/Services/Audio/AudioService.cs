using System;
using System.Collections;
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

        /// <summary>
        /// Starts a BGM track. A <paramref name="fadeDuration"/> above zero crossfades from the
        /// current track over that many seconds; the default of zero switches instantly.
        /// Fading needs a live audio root running coroutines — where that is unavailable the
        /// switch falls back to instant rather than silently doing nothing.
        /// </summary>
        void PlayBgm(AudioClip clip, bool loop = true, float fadeDuration = 0f);
        /// <summary>
        /// Stops the BGM. A <paramref name="fadeDuration"/> above zero fades out over that many
        /// seconds (the source keeps playing until the fade completes); the default of zero
        /// stops immediately.
        /// </summary>
        void StopBgm(float fadeDuration = 0f);
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

        // Coroutine host for BGM fades: the audio root is a plain GameObject, so a tiny
        // runner component is added on demand (same pattern as ObjectPoolService's
        // PoolTimerRunner). Only one fade runs at a time — starting a new fade cancels
        // the previous one.
        private AudioFadeRunner _fadeRunner;
        private Coroutine _fadeCoroutine;

        // Hard cap on the SFX source pool. The old GetAvailableSfxSource grew the pool
        // unboundedly (a new GameObject + interpolated name string per allocation) on
        // SFX-heavy scenes — under a burst of simultaneous sounds the linear scan plus
        // create became effectively O(N²) with permanent memory growth. Once the cap is
        // reached the oldest channel is stolen instead of allocating another source.
        private const int MaxSfxPoolSize = 32;

        private float _masterVolume = 1f;
        private float _bgmVolume = 1f;
        private float _sfxVolume = 1f;
        // Not persisted on purpose. This is the per-state ducking scalar, driven
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
                // Deliberately NOT persisted — gameplay states push a transient
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
            // Idempotent. A second call (recovery re-init, accidental
            // transient binding, double context init) previously created a DUPLICATE
            // [Nexus_AudioService] root and a second pair of BGM sources on top of the
            // existing ones — sounds played from both roots and crossfades overlapped.
            if (_audioRoot != null) return default;

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
            // Player preference × State multiplier. Old code wrote the state
            // multiplier (e.g. 0.40 from PlayingState) to PlayerPrefs every level entry,
            // overwriting the user's chosen slider value permanently.
            float effectiveBgm = _isMuted
                ? 0f
                : _masterVolume * _bgmVolume * _bgmStateMultiplier;
            if (_bgmSourceActive != null) _bgmSourceActive.volume = effectiveBgm;
            if (_bgmSourceFade != null) _bgmSourceFade.volume = effectiveBgm;
        }

        public void PlayBgm(AudioClip clip, bool loop = true, float fadeDuration = 0f)
        {
            if (clip == null || _bgmSourceActive == null) return;
            if (_bgmSourceActive.clip == clip && _bgmSourceActive.isPlaying) return;

            // Same effective formula (master × user pref × state mult).
            float targetVolume = _isMuted
                ? 0f
                : _masterVolume * _bgmVolume * _bgmStateMultiplier;

            if (fadeDuration <= 0f || _bgmSourceFade == null || !TryGetFadeRunner(out var runner))
            {
                // Instant switch (previous behavior).
                StopFadeCoroutine();
                if (_bgmSourceFade != null && _bgmSourceFade.isPlaying)
                {
                    _bgmSourceFade.Stop();
                    _bgmSourceFade.clip = null;
                }
                _bgmSourceActive.clip = clip;
                _bgmSourceActive.loop = loop;
                _bgmSourceActive.volume = targetVolume;
                _bgmSourceActive.Play();
                return;
            }

            // Crossfade: the incoming clip starts on the idle fade source and ramps up
            // while the outgoing source ramps down; the two sources swap roles.
            StopFadeCoroutine();
            var incoming = _bgmSourceFade;
            var outgoing = _bgmSourceActive;
            incoming.clip = clip;
            incoming.loop = loop;
            incoming.volume = 0f;
            incoming.Play();
            _bgmSourceActive = incoming;
            _bgmSourceFade = outgoing;
            _fadeCoroutine = runner.StartCoroutine(CrossfadeCoroutine(incoming, outgoing, fadeDuration));
        }

        public void StopBgm(float fadeDuration = 0f)
        {
            if (_bgmSourceActive == null) return;

            if (fadeDuration <= 0f || !_bgmSourceActive.isPlaying || !TryGetFadeRunner(out var runner))
            {
                StopFadeCoroutine();
                _bgmSourceActive.Stop();
                _bgmSourceActive.clip = null;
                return;
            }

            StopFadeCoroutine();
            _fadeCoroutine = runner.StartCoroutine(FadeOutCoroutine(_bgmSourceActive, fadeDuration));
        }

        private bool TryGetFadeRunner(out AudioFadeRunner runner)
        {
            runner = null;
            if (_audioRoot == null || !_audioRoot.activeInHierarchy) return false;
            if (_fadeRunner == null)
                _fadeRunner = _audioRoot.GetComponent<AudioFadeRunner>() ?? _audioRoot.AddComponent<AudioFadeRunner>();
            runner = _fadeRunner;
            return runner != null;
        }

        private void StopFadeCoroutine()
        {
            if (_fadeCoroutine != null && _fadeRunner != null)
                _fadeRunner.StopCoroutine(_fadeCoroutine);
            _fadeCoroutine = null;
        }

        private IEnumerator CrossfadeCoroutine(AudioSource incoming, AudioSource outgoing, float duration)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                // Recompute the target each frame so volume/mute changes mid-fade apply.
                float target = _isMuted ? 0f : _masterVolume * _bgmVolume * _bgmStateMultiplier;
                if (incoming != null) incoming.volume = target * t;
                if (outgoing != null) outgoing.volume = target * (1f - t);
                yield return null;
            }
            if (outgoing != null)
            {
                outgoing.Stop();
                outgoing.clip = null;
            }
            if (incoming != null)
                incoming.volume = _isMuted ? 0f : _masterVolume * _bgmVolume * _bgmStateMultiplier;
            _fadeCoroutine = null;
        }

        private IEnumerator FadeOutCoroutine(AudioSource source, float duration)
        {
            float startVolume = source.volume;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                if (source == null) yield break;
                source.volume = startVolume * (1f - Mathf.Clamp01(elapsed / duration));
                yield return null;
            }
            if (source != null)
            {
                source.Stop();
                source.clip = null;
                // Restore the source volume for the next PlayBgm.
                source.volume = _isMuted ? 0f : _masterVolume * _bgmVolume * _bgmStateMultiplier;
            }
            _fadeCoroutine = null;
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

        // Round-robin scan cursor. The old always-from-index-0 scan was
        // O(N) per SFX call and — worse — always probed the oldest sources first, so
        // under sustained SFX load the same few sources were checked every call. The
        // cursor spreads the scan and finds an idle source in ~O(1) amortized.
        private int _sfxScanCursor;

        private AudioSource GetAvailableSfxSource()
        {
            int count = _sfxPool.Count;
            if (count > 0)
            {
                int start = _sfxScanCursor % count;
                for (int i = 0; i < count; i++)
                {
                    int idx = (start + i) % count;
                    if (!_sfxPool[idx].isPlaying)
                    {
                        _sfxScanCursor = idx + 1;
                        return _sfxPool[idx];
                    }
                }
            }

            if (_sfxPool.Count >= MaxSfxPoolSize)
            {
                // Pool exhausted — steal the channel at the cursor (round-robin), so
                // steals cycle fairly instead of starving index 0.
                int stealIdx = _sfxScanCursor % _sfxPool.Count;
                var stolen = _sfxPool[stealIdx];
                stolen.Stop();
                _sfxScanCursor = stealIdx + 1;
                return stolen;
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
            StopFadeCoroutine();
            _fadeRunner = null;

            if (_audioRoot != null)
            {
                if (AudioRootProvider is DefaultAudioRootProvider)
                {
                    SafeDestroyUtility.SafeDestroy(_audioRoot);
                }
                _audioRoot = null;
            }

            // Destroy pooled AudioSource GameObjects to avoid leaking editor/play-mode objects
            for (int i = 0; i < _sfxPool.Count; i++)
            {
                var src = _sfxPool[i];
                if (src != null)
                {
                    SafeDestroyUtility.SafeDestroy(src.gameObject);
                }
            }
            _sfxPool.Clear();
        }

        private class AudioFadeRunner : MonoBehaviour { }
    }
}
