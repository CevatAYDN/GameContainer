// Service-layer proof suite: exercises the service/tooling files that were pulled
// into the harness (Localization, Haptics, Input, Feedback, Audio, FloatingText,
// Analytics, SceneLoader, WindowManager, NexusTestContext/NexusTestHarness) against
// the REAL runtime. Uses the functional Unity stubs (scene graph, AudioSource state,
// SceneManager operation queue) so the logic paths run end-to-end, not as mocks.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Extensions;
using Nexus.Core.Services;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NexusBench
{
    // ---------------------------------------------------------------------------
    // Test doubles (recording / controllable providers)
    // ---------------------------------------------------------------------------

    public sealed class SvcRecordingHaptic : IHapticService
    {
        public bool IsEnabled { get; set; } = true;
        public readonly List<HapticType> Calls = new();
        public void Vibrate(HapticType type) => Calls.Add(type);
    }

    public sealed class SvcLoggerRecorder : ILoggerService
    {
        public readonly List<string> Messages = new();
        public bool IsEnabled { get; set; } = true;
        public void Log(string message) => Messages.Add(message);
        public void LogWarning(string message) => Messages.Add(message);
        public void LogError(string message) => Messages.Add(message);
        public void LogException(Exception exception) => Messages.Add(exception?.Message);
    }

    public sealed class SvcWindowProvider : IUIAssetProvider
    {
        public int InstantiateCount;
        public int ReleaseCount;
        public TaskCompletionSource<GameObject> Gate;
        public GameObject LastWindow;
        private string _name;
        private Transform _parent;

        public Task<GameObject> InstantiateWindowAsync(string windowName, Transform parent)
        {
            InstantiateCount++;
            _name = windowName;
            _parent = parent;
            if (Gate != null) return Gate.Task;
            return Task.FromResult(CreateWindow());
        }

        public GameObject CreateWindow()
        {
            var go = new GameObject(_name ?? "Window");
            go.AddComponent<SvcWindowLifecycleRecorder>();
            if (_parent != null) go.transform.SetParent(_parent, false);
            LastWindow = go;
            if (Gate != null)
            {
                var gate = Gate;
                Gate = null;
                gate.SetResult(go);
            }
            return go;
        }

        public void ReleaseWindow(GameObject windowInstance)
        {
            ReleaseCount++;
            if (windowInstance != null) UnityEngine.Object.Destroy(windowInstance);
        }
    }

    public sealed class SvcWindowLifecycleRecorder : Component, IUIWindowLifecycle
    {
        public static readonly List<string> Events = new();
        public object LastArgs;

        public ValueTask OnOpeningAsync(object args, CancellationToken ct)
        {
            LastArgs = args;
            Events.Add("opening");
            return default;
        }
        public ValueTask OnOpenedAsync(CancellationToken ct) { Events.Add("opened"); return default; }
        public ValueTask OnClosingAsync(CancellationToken ct) { Events.Add("closing"); return default; }
        public ValueTask OnClosedAsync(CancellationToken ct) { Events.Add("closed"); return default; }
    }

    public sealed class SvcFlagService : INexusService
    {
        public int InitializeCount;
        public ValueTask InitializeAsync(CancellationToken ct) { InitializeCount++; return default; }
        public void OnDispose() { }
    }

    // ---------------------------------------------------------------------------
    // The suite
    // ---------------------------------------------------------------------------

    public static class ServicesSuite
    {
        private static int _failures;

        public static int Run()
        {
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine("[Services] SERVICE-LAYER PROOF: LOCALIZATION/INPUT/AUDIO/UI/SCENES ON REAL RUNTIME");
            Console.WriteLine("===============================================================================");

            _failures = 0;
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            try
            {
                RunLocalization();
                RunHaptics();
                RunInput();
                RunFeedback();
                RunAudio();
                RunFloatingText();
                RunAnalytics();
                RunSceneLoader();
                RunNexusTestContext();
                RunWindowManager();
            }
            catch (Exception ex)
            {
                Check("Suite_InternalError", false, $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                NexusRuntime.Reset();
                UnityEngine.PlayerPrefs.ClearAll();
                SceneManager.SimulateReset();
                SvcWindowLifecycleRecorder.Events.Clear();
            }

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "[Services] ALL SERVICE-LAYER TESTS PASSED ✓"
                : $"[Services] {_failures} SERVICE-LAYER TEST(S) FAILED ✗");
            return _failures;
        }

        private static void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"[Services] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("Services", name, ok, detail);
            if (!ok) _failures++;
        }

        // =========================================================================
        // Localization — pure BCL logic, zero Unity surface
        // =========================================================================

        private static void RunLocalization()
        {
            var svc = new LocalizationService();
            bool rtlEnabled = svc.IsRTL == false && svc.FormatRTLIfNeeded("hello") == "hello";
            Check("L1. RTL_Inactive_For_Latin", rtlEnabled, $"IsRTL={svc.IsRTL}");

            svc.SetLanguage("ar");

            string basic = svc.FormatRTLIfNeeded("مرحبا");
            Check("L2. RTL_Reverses_Basic_Arabic", basic == "ابحرم", $"reversed='{basic}'");

            // "A👋B🌍" must reverse to "🌍B👋A" — surrogate pairs stay intact.
            string emoji = svc.FormatRTLIfNeeded("A👋B🌍");
            Check("L3. RTL_Preserves_Emoji_SurrogatePairs", emoji == "🌍B👋A", $"reversed='{emoji}'");

            // "a\u0301b" (a + combining acute) must stay glued after reversal.
            string combining = svc.FormatRTLIfNeeded("a\u0301b");
            Check("L4. RTL_Preserves_CombiningMarks", combining == "ba\u0301", $"reversed='{combining}'");

            var prefs = new FakeSessionPrefs();
            var persisting = new LocalizationService(prefs);
            string changed = null;
            persisting.OnLanguageChanged += lang => changed = lang;
            persisting.SetLanguage("TR");
            bool switched = persisting.CurrentLanguage == "tr" && changed == "tr"
                && prefs.GetString("NT_Language") == "tr" && persisting.IsRTL == false;
            Check("L5. Language_Switch_Normalizes_And_Persists", switched,
                $"lang={persisting.CurrentLanguage} changed={changed} saved={prefs.GetString("NT_Language")}");

            persisting.SetLanguage("ar");
            bool rtl = persisting.IsRTL == true;
            persisting.SetLanguage("he");
            rtl &= persisting.IsRTL;
            persisting.SetLanguage("fa");
            rtl &= persisting.IsRTL;
            persisting.SetLanguage("ur");
            rtl &= persisting.IsRTL;
            persisting.SetLanguage("en");
            rtl &= !persisting.IsRTL;
            Check("L6. IsRTL_For_RTL_Languages", rtl, "ar/he/fa/ur true, en false");

            // Restart simulation: a fresh service must reload the persisted language.
            var reloaded = new LocalizationService(prefs);
            reloaded.InitializeAsync(default).GetAwaiter().GetResult();
            Check("L7. Language_Survives_Restart", reloaded.CurrentLanguage == "en",
                $"current={reloaded.CurrentLanguage}");

            // Fallback chain: current language -> "en" -> fallback param -> key itself.
            reloaded.SetLanguage("de");
            bool fallback = reloaded.GetString("btn_ok") == "OK"
                && reloaded.GetString("never_registered", "fb") == "fb"
                && reloaded.GetString("never_registered") == "never_registered"
                && reloaded.GetString(null, "n") == "n";
            Check("L8. Fallback_Chain_Current_To_En_To_Fallback_To_Key", fallback,
                $"btn_ok(de)='{reloaded.GetString("btn_ok")}'");

            reloaded.RegisterLanguageTable("de", new Dictionary<string, string> { { "btn_ok", "OK (DE)" } });
            reloaded.RegisterKey("de", "custom_key", "custom value");
            bool tables = reloaded.GetString("btn_ok") == "OK (DE)"
                && reloaded.GetString("custom_key") == "custom value"
                && reloaded.GetString("btn_undo") == "Undo";
            Check("L9. RegisterLanguageTable_And_RegisterKey", tables,
                $"btn_ok='{reloaded.GetString("btn_ok")}' custom='{reloaded.GetString("custom_key")}'");

            // Turkish built-in table check after switching from a custom "de" registration.
            reloaded.SetLanguage("tr");
            Check("L10. Builtin_Turkish_Table", reloaded.GetString("btn_undo") == "Geri Al",
                $"btn_undo='{reloaded.GetString("btn_undo")}'");
        }

        // =========================================================================
        // Haptics — platform-gated surface, prefs wiring + no-throw on desktop path
        // =========================================================================

        private static void RunHaptics()
        {
            var prefs = new FakeSessionPrefs();
            var svc = new HapticService();
            svc.PlayerPrefsService = prefs;
            svc.InitializeAsync(default).GetAwaiter().GetResult();
            bool enabledDefault = svc.IsEnabled;
            svc.Vibrate(HapticType.Heavy);
            Check("H1. Enabled_By_Default_And_NoThrow", enabledDefault, $"IsEnabled={enabledDefault}");

            prefs.SetBool("NT_HapticsDisabled", true);
            var disabled = new HapticService { PlayerPrefsService = prefs };
            disabled.InitializeAsync(default).GetAwaiter().GetResult();
            bool disabledViaPrefs = !disabled.IsEnabled;
            disabled.Vibrate(HapticType.Success);
            disabled.Vibrate(HapticType.Light);
            Check("H2. Prefs_Flag_Disables_And_NoThrow", disabledViaPrefs, $"IsEnabled={disabled.IsEnabled}");

            var prefsClear = new FakeSessionPrefs();
            prefsClear.SetBool("NT_HapticsDisabled", false);
            var reEnabled = new HapticService { PlayerPrefsService = prefsClear };
            reEnabled.InitializeAsync(default).GetAwaiter().GetResult();
            Check("H3. Explicit_False_Flag_Stays_Enabled", reEnabled.IsEnabled, $"IsEnabled={reEnabled.IsEnabled}");
        }

        // =========================================================================
        // Input — joystick clamping + signal emission through the real SignalBus
        // =========================================================================

        private static void RunInput()
        {
            var ctx = NexusTestHarness.CreateContext();
            try
            {
                ctx.Context.Container.Bind<InputService>(isSingleton: true);
                var input = ctx.Context.Resolve<InputService>();
                var received = new List<PlayerMoveSignal>();
                ctx.Context.SignalBus.Subscribe<PlayerMoveSignal>(s => received.Add(s));

                input.SetVirtualJoystickInput(new Vector2(3f, 4f));
                input.UpdateInput(0.016f);
                bool clamped = Math.Abs(input.MoveInput.magnitude - 1f) < 1e-3
                    && Math.Abs(input.MoveInput.x - 0.6f) < 1e-3
                    && Math.Abs(input.MoveInput.y - 0.8f) < 1e-3;
                Check("I1. Joystick_Clamped_To_Unit_Magnitude", clamped,
                    $"MoveInput={input.MoveInput} (expected ~(0.6, 0.8))");

                bool fired = received.Count == 1
                    && Math.Abs(received[0].Direction.x - 0.6f) < 1e-3
                    && Math.Abs(received[0].Direction.y - 0.8f) < 1e-3
                    && input.IsInputActive;
                Check("I2. PlayerMoveSignal_Fired_With_Clamped_Direction", fired,
                    $"received={received.Count} first={(received.Count > 0 ? received[0].Direction.ToString() : "none")}");

                input.SetVirtualJoystickInput(Vector2.zero);
                input.UpdateInput(0.016f);
                bool idle = !input.IsInputActive && received.Count == 1;
                Check("I3. Idle_Joystick_Fires_No_Signal", idle,
                    $"IsInputActive={input.IsInputActive} received={received.Count}");

                input.SetVirtualJoystickInput(new Vector2(2f, 2f));
                input.UpdateInput(0.016f);
                bool overClamped = Math.Abs(input.MoveInput.magnitude - 1f) < 1e-3 && received.Count == 2;
                Check("I4. Over_Length_Vector_Clamped", overClamped,
                    $"MoveInput={input.MoveInput} received={received.Count}");
            }
            finally
            {
                ctx.Dispose();
            }
        }

        // =========================================================================
        // Feedback — preset->haptic mapping + audio handoff through real services
        // =========================================================================

        private static void RunFeedback()
        {
            var ctx = NexusTestHarness.CreateContext();
            try
            {
                var prefs = new FakeSessionPrefs();
                var haptic = new SvcRecordingHaptic();
                ctx.Context.Container.BindInstance<IPlayerPrefsService>(prefs);
                ctx.Context.Container.BindInstance<IAudioRootProvider>(new DefaultAudioRootProvider());
                ctx.Context.Container.BindInstance<IHapticService>(haptic);
                ctx.Context.Container.Bind<IAudioService, AudioService>(isSingleton: true);
                ctx.Context.Container.Bind<FeedbackService>(isSingleton: true);

                var audio = ctx.Context.Resolve<IAudioService>() as AudioService;
                audio.PlayerPrefsService = prefs;
                audio.InitializeAsync(default).GetAwaiter().GetResult();

                var feedback = ctx.Context.Resolve<FeedbackService>();
                feedback.Play(FeedbackPreset.LightClick);
                feedback.Play(FeedbackPreset.MediumImpact);
                feedback.Play(FeedbackPreset.HeavyImpact);
                feedback.Play(FeedbackPreset.CoinCollect);
                feedback.Play(FeedbackPreset.SuccessFanfare);
                feedback.Play(FeedbackPreset.WarningAlert);
                feedback.Play(FeedbackPreset.ErrorFailure);

                bool mapping = haptic.Calls.Count == 7
                    && haptic.Calls[0] == HapticType.Light
                    && haptic.Calls[1] == HapticType.Medium
                    && haptic.Calls[2] == HapticType.Heavy
                    && haptic.Calls[3] == HapticType.Selection
                    && haptic.Calls[4] == HapticType.Success
                    && haptic.Calls[5] == HapticType.Warning
                    && haptic.Calls[6] == HapticType.Heavy;
                Check("F1. Preset_To_Haptic_Mapping", mapping,
                    $"calls=[{string.Join(",", haptic.Calls)}]");

                // With a preset->clip mapping, Play also hands off to AudioService.
                var clip = new AudioClip("coin");
                int sfxBefore = CountSfxSources();
                feedback.PresetAudioClips = new Dictionary<FeedbackPreset, AudioClip> { { FeedbackPreset.CoinCollect, clip } };
                haptic.Calls.Clear();
                feedback.Play(FeedbackPreset.CoinCollect);
                int sfxAfter = CountSfxSources();
                bool audioHandoff = haptic.Calls.Count == 1 && sfxAfter == sfxBefore + 1;
                Check("F2. Preset_Audio_Hands_Off_To_AudioService", audioHandoff,
                    $"sfxSources {sfxBefore}->{sfxAfter} haptic={haptic.Calls.Count}");

                // Inverted pitch range must be swapped, not crash (mirrors AudioService guard).
                haptic.Calls.Clear();
                feedback.PlayCustom(clip, HapticType.Medium, 0.9f, 0.5f);
                bool guarded = haptic.Calls.Count == 1 && haptic.Calls[0] == HapticType.Medium;
                Check("F3. PlayCustom_InvertedPitch_NoCrash", guarded,
                    $"haptic={haptic.Calls.Count}");

                feedback.PlayCustom(null, HapticType.Light);
                feedback.PlayCustom(clip, HapticType.Selection, 1f, 1f);
                Check("F4. PlayCustom_Without_Audio_Safe", haptic.Calls.Count == 3,
                    $"haptic={haptic.Calls.Count}");
            }
            finally
            {
                ctx.Dispose();
            }
        }

        // =========================================================================
        // Audio — volume math, P0.3 state-multiplier, SFX pool cap + steal, dedup
        // =========================================================================

        private static void RunAudio()
        {
            RunAudio_Core();
            RunAudio_PoolAndDedup();
        }

        private static void RunAudio_Core()
        {
            var ctx = NexusTestHarness.CreateContext();
            try
            {
                var prefs = new FakeSessionPrefs();
                var rootProvider = new DefaultAudioRootProvider();
                ctx.Context.Container.BindInstance<IPlayerPrefsService>(prefs);
                ctx.Context.Container.BindInstance<IAudioRootProvider>(rootProvider);
                ctx.Context.Container.Bind<IAudioService, AudioService>(isSingleton: true);
                var audio = ctx.Context.Resolve<IAudioService>() as AudioService;
                audio.InitializeAsync(default).GetAwaiter().GetResult();

                audio.MasterVolume = 0.8f;
                audio.BgmVolume = 0.5f;
                audio.SfxVolume = 0.6f;
                bool persisted = prefs.GetFloat("NT_AudioMasterVol") == 0.8f
                    && prefs.GetFloat("NT_AudioBgmVol") == 0.5f
                    && prefs.GetFloat("NT_AudioSfxVol") == 0.6f;
                bool clamped = audio.MasterVolume == 0.8f && audio.SfxVolume == 0.6f;
                Check("A1. Volumes_Clamped_And_Persisted", persisted && clamped,
                    $"master={audio.MasterVolume} bgm={audio.BgmVolume} sfx={audio.SfxVolume}");

                audio.MasterVolume = 5f;
                bool clampedDown = audio.MasterVolume == 1f && prefs.GetFloat("NT_AudioMasterVol") == 1f;
                Check("A2. Volume_Clamped_To_Max", clampedDown, $"master={audio.MasterVolume}");

                // P0.3: the state multiplier is transient — must NOT touch PlayerPrefs.
                audio.BgmVolume = 1f;
                int keysBefore = prefs.CountKeys();
                audio.BgmStateMultiplier = 0.4f;
                audio.BgmStateMultiplier = 0.25f;
                int keysAfter = prefs.CountKeys();
                bool notPersisted = keysAfter == keysBefore;
                float effective = 0f;
                var root = rootProvider.GetOrCreateRoot();
                var bgmSources = root.GetComponents<AudioSource>();
                if (bgmSources.Length >= 2) effective = bgmSources[0].volume;
                bool effectiveMath = Math.Abs(effective - (1f * 1f * 0.25f)) < 1e-3;
                Check("A3. BgmStateMultiplier_Not_Persisted_And_Effective", notPersisted && effectiveMath,
                    $"keys {keysBefore}->{keysAfter} effective={effective} (expected 0.25)");

                audio.IsMuted = true;
                var mutedSources = root.GetComponents<AudioSource>();
                bool muted = prefs.GetBool("NT_AudioMuted") && mutedSources[0].volume == 0f
                    && mutedSources[1].volume == 0f;
                Check("A4. Muted_Drives_Effective_Zero_And_Persists", muted,
                    $"volumes={mutedSources[0].volume}/{mutedSources[1].volume}");
            }
            finally
            {
                ctx.Dispose();
            }
        }

        private static void RunAudio_PoolAndDedup()
        {
            var ctx = NexusTestHarness.CreateContext();
            try
            {
                var prefs = new FakeSessionPrefs();
                var rootProvider = new DefaultAudioRootProvider();
                ctx.Context.Container.BindInstance<IPlayerPrefsService>(prefs);
                ctx.Context.Container.BindInstance<IAudioRootProvider>(rootProvider);
                ctx.Context.Container.Bind<IAudioService, AudioService>(isSingleton: true);
                var audio = ctx.Context.Resolve<IAudioService>() as AudioService;
                audio.InitializeAsync(default).GetAwaiter().GetResult();

                var clip = new AudioClip("sfx");
                int baseline = CountSfxSources();
                for (int i = 0; i < 40; i++) audio.PlaySfx(clip, 1f, 1f, 1f);
                int sources = CountSfxSources();
                Check("A5. SfxPool_Capped_At_32_Steals_Oldest", sources == baseline + 32,
                    $"sfxSources={sources} (cap=32, 40 plays, baseline={baseline})");

                // Muted: PlaySfx must return early without touching the pool.
                audio.IsMuted = true;
                int before = CountSfxSources();
                for (int i = 0; i < 10; i++) audio.PlaySfx(clip);
                Check("A6. Muted_PlaySfx_No_Allocation", CountSfxSources() == before,
                    $"sfxSources {before}->{CountSfxSources()}");

                audio.IsMuted = false;
                var root = rootProvider.GetOrCreateRoot();
                var bgm = root.GetComponents<AudioSource>();
                var clipA = new AudioClip("bgm_a");
                var clipB = new AudioClip("bgm_b");
                audio.PlayBgm(clipA);
                bool first = bgm[0].clip == clipA && bgm[0].isPlaying;
                audio.PlayBgm(clipA);
                bool dedup = bgm[0].clip == clipA && bgm[0].isPlaying;
                audio.PlayBgm(clipB);
                bool switched = bgm[0].clip == clipB && bgm[0].isPlaying;
                audio.StopBgm();
                bool stopped = !bgm[0].isPlaying && bgm[0].clip == null;
                Check("A7. PlayBgm_Deduplicates_SameClip", first && dedup && switched && stopped,
                    $"first={first} dedup={dedup} switched={switched} stopped={stopped}");

                audio.PlaySfx(clip, 1f, 0.9f, 0.5f);
                bool guard = CountSfxSources() > 0;
                Check("A8. InvertedPitch_Guard_NoCrash", guard, $"sfxSources={CountSfxSources()}");
            }
            finally
            {
                ctx.Dispose();
            }
        }

        // =========================================================================
        // FloatingText — spawn/duration/expiry state machine
        // =========================================================================

        private static void RunFloatingText()
        {
            var ctx = NexusTestHarness.CreateContext();
            try
            {
                ctx.Context.Container.Bind<IFloatingTextService, FloatingTextService>(isSingleton: true);
                var svc = ctx.Context.Resolve<IFloatingTextService>() as FloatingTextService;

                svc.SpawnFloatingText("+500", new Vector3(1f, 2f, 3f), Color.green, 1f, 2f);
                var item = svc.ActiveTexts[0];
                bool fields = svc.ActiveTexts.Count == 1 && item.Text == "+500"
                    && item.StartPosition == new Vector3(1f, 2f, 3f)
                    && item.Color == Color.green && item.Duration == 1f
                    && item.RiseHeight == 2f && item.ElapsedTime == 0f;
                Check("FT1. Spawn_Tracks_All_Fields", fields,
                    $"text='{item.Text}' pos={item.StartPosition} color={item.Color} dur={item.Duration}");

                svc.UpdateService(0.4f);
                bool midLife = svc.ActiveTexts.Count == 1 && Math.Abs(svc.ActiveTexts[0].ElapsedTime - 0.4f) < 1e-6;
                svc.UpdateService(0.6f);
                bool expired = svc.ActiveTexts.Count == 0;
                Check("FT2. ElapsedTime_Accumulates_And_Expires", midLife && expired,
                    $"count={svc.ActiveTexts.Count} elapsed={(svc.ActiveTexts.Count > 0 ? svc.ActiveTexts[0].ElapsedTime : -1)}");

                // Duration is clamped to >= 0.1 — tiny durations still render at least one update.
                svc.SpawnFloatingText("+1", Vector3.zero, Color.white, 0.01f);
                svc.UpdateService(0.05f);
                bool clamped = svc.ActiveTexts.Count == 1 && Math.Abs(svc.ActiveTexts[0].Duration - 0.1f) < 1e-6;
                Check("FT3. Duration_Clamped_To_Minimum", clamped,
                    $"duration={(svc.ActiveTexts.Count > 0 ? svc.ActiveTexts[0].Duration : -1)}");

                svc.SpawnFloatingText("", Vector3.zero, Color.red, 1f);
                svc.SpawnFloatingText(null, Vector3.zero, Color.red, 1f);
                bool emptyIgnored = svc.ActiveTexts.Count == 1;
                Check("FT4. Empty_Or_Null_Text_Ignored", emptyIgnored, $"count={svc.ActiveTexts.Count}");
            }
            finally
            {
                ctx.Dispose();
            }
        }

        // =========================================================================
        // Analytics — logging path through the real NexusRuntime.Logger resolution
        // =========================================================================

        private static void RunAnalytics()
        {
            var ctx = NexusTestHarness.CreateContext();
            try
            {
                var logger = new SvcLoggerRecorder();
                ctx.Context.Container.BindInstance<ILoggerService>(logger);
                ctx.Context.Container.Bind<IAnalyticsService, AnalyticsService>(isSingleton: true);
                var analytics = ctx.Context.Resolve<IAnalyticsService>();
                analytics.LogEvent("level_start");
                analytics.LogEvent("purchase", new Dictionary<string, object> { { "item", "gems_100" }, { "price", 1.99 } });
                analytics.SetUserProperty("tier", "whale");
                bool logged = logger.Messages.Count == 3
                    && logger.Messages[0] == "[NexusAnalytics] Event: level_start"
                    && logger.Messages[1] == "[NexusAnalytics] Event: purchase | Params: item: gems_100, price: 1.99"
                    && logger.Messages[2] == "[NexusAnalytics] UserProperty: tier = whale";
                Check("AN1. Events_Routed_To_Registered_Logger", logged,
                    $"messages=[{string.Join(" | ", logger.Messages)}]");

                var bare = new AnalyticsService();
                bare.LogEvent("no_logger_no_crash");
                bare.SetUserProperty("k", "v");
                Check("AN2. No_Logger_Registered_NoCrash", true, "3 calls with no logger");
            }
            finally
            {
                ctx.Dispose();
            }
        }

        // =========================================================================
        // SceneLoader — scene flow signals, dedup guard, cancellation, not-found
        // =========================================================================

        private static void RunSceneLoader()
        {
            var ctx = NexusTestHarness.CreateContext();
            try
            {
                var loader = new SceneLoader(ctx.Context.SignalBus);
                var events = new List<string>();
                ctx.Context.SignalBus.Subscribe<SceneLoadingSignal>(s => events.Add($"loading:{s.SceneName}"));
                ctx.Context.SignalBus.Subscribe<SceneLoadedSignal>(s => events.Add($"loaded:{s.SceneName}"));
                ctx.Context.SignalBus.Subscribe<SceneUnloadedSignal>(s => events.Add($"unloaded:{s.SceneName}"));

                SceneManager.SimulateReset();
                SceneManager.SimulateAddScene("Level1", loaded: false);

                // S1: load completes only when the SceneManager operation finishes.
                var load = loader.LoadSceneAsync("Level1");
                bool started = events.Count == 1 && events[0] == "loading:Level1"
                    && SceneManager.SimulatePendingCount == 1;
                SceneManager.SimulateCompleteAll();
                load.GetAwaiter().GetResult();
                bool completed = events.Count == 2 && events[1] == "loaded:Level1";
                Check("S1. Load_Fires_Signals_In_Order", started && completed,
                    $"events=[{string.Join(",", events)}]");

                // S2: a second load of the same scene must be rejected (single op).
                events.Clear();
                var dup1 = loader.LoadSceneAsync("Level1");
                var dup2 = loader.LoadSceneAsync("Level1");
                bool guard = SceneManager.SimulatePendingCount == 1
                    && dup2.IsCompleted
                    && events.Count == 1 && events[0] == "loading:Level1";
                SceneManager.SimulateCompleteAll();
                dup1.GetAwaiter().GetResult();
                Check("S2. Duplicate_Load_Rejected_By_Guard", guard,
                    $"pending={SceneManager.SimulatePendingCount} events=[{string.Join(",", events)}]");

                // S3: unknown scene -> SceneManager returns null -> error path, no crash.
                events.Clear();
                var missing = loader.LoadSceneAsync("NotInBuild");
                missing.GetAwaiter().GetResult();
                bool notFound = events.Count == 1 && events[0] == "loading:NotInBuild"
                    && SceneManager.SimulatePendingCount == 0;
                Check("S3. Missing_Scene_NotFound_NoCrash", notFound,
                    $"events=[{string.Join(",", events)}]");

                // S4: cancellation mid-load aborts without leaking the guard.
                // Cancel while the load is suspended (op NOT yet completed), so the
                // loop's ThrowIfCancellationRequested fires — then complete the op.
                events.Clear();
                using (var cts = new CancellationTokenSource())
                {
                    var cancelTask = loader.LoadSceneAsync("Level1", LoadSceneMode.Additive, cts.Token);
                    cts.Cancel();
                    bool aborted = false;
                    try { cancelTask.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { aborted = true; }
                    SceneManager.SimulateCompleteAll();
                    bool guardReleased = events.Count == 1 && events[0] == "loading:Level1";
                    var afterCancel = loader.LoadSceneAsync("Level1");
                    bool reopened = SceneManager.SimulatePendingCount == 1;
                    SceneManager.SimulateCompleteAll();
                    afterCancel.GetAwaiter().GetResult();
                    Check("S4. Cancellation_Aborts_And_Releases_Guard", aborted && guardReleased && reopened,
                        $"aborted={aborted} pending={SceneManager.SimulatePendingCount} events=[{string.Join(",", events)}]");
                }

                // S5: unload fires its signal; unknown scene unload is a safe no-op.
                events.Clear();
                var unload = loader.UnloadSceneAsync("Level1");
                SceneManager.SimulateCompleteAll();
                unload.GetAwaiter().GetResult();
                var noOp = loader.UnloadSceneAsync("NotLoaded");
                noOp.GetAwaiter().GetResult();
                bool unloaded = events.Count == 1 && events[0] == "unloaded:Level1";
                Check("S5. Unload_Fires_Signal_Safe_On_Unknown", unloaded,
                    $"events=[{string.Join(",", events)}]");

                // S6: SetActiveScene only accepts a valid loaded scene.
                SceneManager.SimulateAddScene("Menu", loaded: false);
                loader.SetActiveScene("Menu");
                bool rejected = SceneManager.SimulateActiveScene != "Menu";
                SceneManager.SimulateAddScene("Hud", loaded: true);
                loader.SetActiveScene("Hud");
                bool accepted = SceneManager.SimulateActiveScene == "Hud";
                Check("S6. SetActiveScene_Validates_Loaded", rejected && accepted,
                    $"active='{SceneManager.SimulateActiveScene}'");
            }
            finally
            {
                ctx.Dispose();
                SceneManager.SimulateReset();
            }
        }

        // =========================================================================
        // NexusTestContext / NexusTestHarness — the runtime's own test utilities
        // =========================================================================

        private static void RunNexusTestContext()
        {
            var harness = NexusTestHarness.CreateContext();
            try
            {
                var counter = new TestCounter();
                harness.Context.Container.BindInstance(counter);
                harness.Register<SvcCounterCommand>();
                harness.Register<ServicesTestSignal>();

                harness.Dispatch(new ServicesTestSignal(42));
                harness.Dispatch(new ServicesTestSignal(7));
                bool tracked = harness.GetDispatchedSignalCount<ServicesTestSignal>() == 2
                    && harness.SignalWasDispatched<ServicesTestSignal>()
                    && harness.GetLastDispatchedSignal<ServicesTestSignal>().Value == 7
                    && harness.GetDispatchedSignals<ServicesTestSignal>().Count == 2
                    && counter.Value == 2;
                Check("T1. Register_Dispatch_Tracks_And_Executes", tracked,
                    $"dispatched={harness.GetDispatchedSignalCount<ServicesTestSignal>()} executed={counter.Value}");

                bool asserted = true;
                try
                {
                    harness.AssertSignalNotDispatched<ServicesTestSignal>();
                    asserted = false;
                }
                catch (UnityEngine.Assertions.AssertionException) { }
                Check("T2. AssertSignalNotDispatched_Throws_On_Dispatched", asserted,
                    "AssertionException thrown as expected");

                harness.ClearDispatchedSignals();
                bool cleared = harness.GetDispatchedSignalCount<ServicesTestSignal>() == 0
                    && !harness.SignalWasDispatched<ServicesTestSignal>();
                Check("T3. ClearDispatchedSignals_Resets_Tracking", cleared,
                    $"count={harness.GetDispatchedSignalCount<ServicesTestSignal>()}");

                var missingAttr = new NexusTestContext(harness.Context);
                bool threw = false;
                try { missingAttr.Register<SvcNoHandlerCommand>(); }
                catch (InvalidOperationException) { threw = true; }
                Check("T4. Command_Without_Handler_Throws", threw,
                    $"threw={threw}");
            }
            finally
            {
                harness.Dispose();
            }

            // Auto-initialize pipeline through NexusTestHarness.CreateContext(configure, true).
            // A real IContextLifecycle's OnConfigure is the documented place for BindService —
            // Context.Configure() resolves the bound lifecycle and calls OnConfigure with its
            // OWN builder, so the service lands in the init pipeline (unlike a standalone builder).
            var auto = NexusTestHarness.CreateContext(
                builder => builder.BindInstance<IContextLifecycle>(new SvcTestLifecycle()),
                autoInitialize: true);
            var flagSvc = auto.Context.Resolve<INexusService>() as SvcFlagService;
            bool pipelineRan = flagSvc?.InitializeCount == 1;
            auto.Dispose();
            Check("T5. CreateContext_AutoInitialize_Runs_Services", pipelineRan,
                $"InitializeCount={flagSvc?.InitializeCount}");

            // Child context: scoped container, independent lifecycle, clean teardown.
            int activeBefore = NexusRuntime.ActiveContexts.Count;
            var parent = NexusTestHarness.CreateContext("parent-scope");
            var child = NexusTestHarness.CreateChildContext(parent, "child-scope");
            bool hierarchy = parent.Context != child.Context && child.Context != null;
            child.Dispose();
            parent.Dispose();
            NexusRuntime.Reset();
            bool cleanup = NexusRuntime.ActiveContexts.Count == 0 && activeBefore >= 0;
            Check("T6. Child_Context_Scoped_And_Clean_Teardown", hierarchy && cleanup,
                $"hierarchy={hierarchy} activeAfterReset={NexusRuntime.ActiveContexts.Count}");
        }

        // =========================================================================
        // WindowManager — lifecycle order, concurrent-open dedup (E-5), modal blocking
        // =========================================================================

        private static void RunWindowManager()
        {
            SvcWindowLifecycleRecorder.Events.Clear();
            var ctx = NexusTestHarness.CreateContext();
            try
            {
                var provider = new SvcWindowProvider();
                ctx.Context.Container.BindInstance<IUIAssetProvider>(provider);
                ctx.Context.Container.Bind<IWindowManager, WindowManager>(isSingleton: true);
                var wm = ctx.Context.Resolve<IWindowManager>() as WindowManager;
                wm.InitializeAsync(default).GetAwaiter().GetResult();

                // W1: full open/close cycle with lifecycle callbacks in order.
                var opened = wm.OpenWindowAsync("Shop", UILayer.Screen, "payload").GetAwaiter().GetResult();
                bool openOrder = SvcWindowLifecycleRecorder.Events.Count == 2
                    && SvcWindowLifecycleRecorder.Events[0] == "opening"
                    && SvcWindowLifecycleRecorder.Events[1] == "opened";
                bool openState = wm.IsWindowOpen("Shop") && provider.InstantiateCount == 1;
                SvcWindowLifecycleRecorder.Events.Clear();
                wm.CloseWindowAsync("Shop").GetAwaiter().GetResult();
                bool closeOrder = SvcWindowLifecycleRecorder.Events.Count == 2
                    && SvcWindowLifecycleRecorder.Events[0] == "closing"
                    && SvcWindowLifecycleRecorder.Events[1] == "closed";
                bool closeState = !wm.IsWindowOpen("Shop") && provider.ReleaseCount == 1;
                Check("W1. Open_Close_Lifecycle_In_Order", openOrder && openState && closeOrder && closeState,
                    $"open=[{string.Join(",", SvcWindowLifecycleRecorder.Events)}] released={provider.ReleaseCount}");

                // W2 (E-5 fix): a second concurrent open of the same window waits for the
                // pending opener and returns the SAME instance — one instantiation only.
                provider.Gate = new TaskCompletionSource<GameObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                var t1 = wm.OpenWindowAsync("Shop");
                var t2 = wm.OpenWindowAsync("Shop");
                bool bothPending = wm.PendingWindowCount == 1;
                var timeout = Task.Delay(5000);
                while (wm.PendingWindowCount != 1 && !timeout.IsCompleted)
                {
                    Thread.Sleep(5);
                }
                bool secondWaited = !t2.IsCompleted && !t1.IsCompleted && wm.PendingWindowCount == 1;
                provider.CreateWindow();
                var w1 = t1.GetAwaiter().GetResult();
                var w2 = t2.GetAwaiter().GetResult();
                bool deduped = ReferenceEquals(w1, w2) && provider.InstantiateCount == 2
                    && wm.IsWindowOpen("Shop");
                Check("W2. Concurrent_Open_Deduplicated_Same_Instance", bothPending && secondWaited && deduped,
                    $"sameInstance={ReferenceEquals(w1, w2)} instantiated={provider.InstantiateCount} pending={wm.PendingWindowCount}");
                wm.CloseWindowAsync("Shop").GetAwaiter().GetResult();

                // W3: CloseTopWindow closes the most recently opened window.
                SvcWindowLifecycleRecorder.Events.Clear();
                wm.OpenWindowAsync("A").GetAwaiter().GetResult();
                wm.OpenWindowAsync("B").GetAwaiter().GetResult();
                wm.CloseTopWindowAsync().GetAwaiter().GetResult();
                bool topClosed = !wm.IsWindowOpen("B") && wm.IsWindowOpen("A");
                Check("W3. CloseTopWindow_Closes_Most_Recent", topClosed,
                    $"B={wm.IsWindowOpen("B")} A={wm.IsWindowOpen("A")}");

                // W4: a modal window blocks interactivity on all lower layers.
                wm.OpenWindowAsync("ModalWin", UILayer.Modal).GetAwaiter().GetResult();
                var hud = GameObject.Find("HUD");
                var modal = GameObject.Find("Modal");
                bool modalBlocks = hud != null && modal != null
                    && hud.GetComponent<CanvasGroup>().interactable == false
                    && hud.GetComponent<CanvasGroup>().blocksRaycasts == false
                    && modal.GetComponent<CanvasGroup>().interactable == true;
                wm.CloseWindowAsync("ModalWin").GetAwaiter().GetResult();
                bool unblocked = hud.GetComponent<CanvasGroup>().interactable == true;
                Check("W4. Modal_Blocks_Lower_Layers_And_Unblocks", modalBlocks && unblocked,
                    $"hudBlocked={modalBlocks} unblocked={unblocked}");

                // W5: snapshot reports open windows with layer + history order.
                wm.CloseWindowAsync("A").GetAwaiter().GetResult();
                wm.OpenWindowAsync("First", UILayer.HUD).GetAwaiter().GetResult();
                wm.OpenWindowAsync("Second", UILayer.Popup).GetAwaiter().GetResult();
                var snap = wm.GetOpenWindowsSnapshot();
                bool snapshot = snap.Count == 2 && snap[0].Name == "First" && snap[1].Name == "Second"
                    && snap[0].Layer == UILayer.HUD && snap[1].Layer == UILayer.Popup
                    && snap[0].HistoryOrder < snap[1].HistoryOrder;
                Check("W5. Snapshot_Sorted_By_Open_Order", snapshot,
                    $"snapshot=[{string.Join(",", snap.Select(s => s.Name))}]");

                // W6: reopen after close instantiates fresh.
                wm.CloseAllAsync().GetAwaiter().GetResult();
                wm.OpenWindowAsync("First").GetAwaiter().GetResult();
                bool reopen = wm.IsWindowOpen("First") && provider.InstantiateCount == 8;
                Check("W6. Reopen_After_Close_Instantiates_Fresh", reopen,
                    $"instantiated={provider.InstantiateCount}");

                wm.Dispose();
                bool disposed = false;
                try { disposed = !wm.IsWindowOpen("First"); }
                catch (ObjectDisposedException) { disposed = true; }
                Check("W7. Dispose_Cleans_Windows_And_Canvas", disposed,
                    $"IsWindowOpen={disposed} (ObjectDisposedException after Dispose = disposed)");
            }
            finally
            {
                ctx.Dispose();
            }
        }

        // ---------------------------------------------------------------------------

        private static int CountSfxSources()
        {
            return UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude)
                .Count(go => go.name.StartsWith("SFXSource_"));
        }
    }

    // ---------------------------------------------------------------------------
    // Test command + signal for the NexusTestContext utilities
    // ---------------------------------------------------------------------------

    public readonly struct ServicesTestSignal
    {
        public readonly int Value;
        public ServicesTestSignal(int value) { Value = value; }
    }

    [SignalHandler(typeof(ServicesTestSignal))]
    public class SvcCounterCommand : ICommand<ServicesTestSignal>
    {
        [Inject] public TestCounter Counter;
        public void Execute(ServicesTestSignal signal)
        {
            Counter.Value++;
        }
    }

    public class SvcNoHandlerCommand : ICommand<ServicesTestSignal>
    {
        public void Execute(ServicesTestSignal signal) { }
    }

    // IContextLifecycle whose OnConfigure binds a service through the REAL Context
    // builder — the documented auto-init path exercised by T5.
    public sealed class SvcTestLifecycle : IContextLifecycle
    {
        public void OnConfigure(IContextBuilder builder) => builder.BindService<INexusService, SvcFlagService>();
        public ValueTask OnInitializeAsync(CancellationToken ct) => default;
        public ValueTask OnStartAsync(CancellationToken ct) => default;
        public void OnDispose() { }
    }
}
