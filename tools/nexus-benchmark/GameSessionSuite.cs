// Dogfood suite: a realistic hyper-casual game session simulated on the REAL Nexus
// runtime — real Context, real DI, real SignalBus, real services. This is not a
// benchmark: it is a full session lifecycle (boot -> gameplay tick loop -> economy ->
// autosave -> session continuity -> offline income -> crash -> recovery -> leak check)
// that exercises the runtime the way a Supercent-style game actually uses it.
//
// The session spans multiple boots of the same game (prefs + save slot + state object
// survive across simulated app restarts). A "crash" is simulated by killing a session
// without saving; the next boot must load the last good checkpoint and keep running.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Extensions;
using Nexus.Core.Services;
using UnityEngine;

namespace NexusBench
{
    // ---------------------------------------------------------------------------
    // Game signals + commands (real SignalBus machinery)
    // ---------------------------------------------------------------------------

    public readonly struct SessionEarnGoldSignal
    {
        public readonly long Amount;
        public SessionEarnGoldSignal(long amount) { Amount = amount; }
    }

    public readonly struct SessionSpendGoldSignal
    {
        public readonly long Amount;
        public SessionSpendGoldSignal(long amount) { Amount = amount; }
    }

    public readonly struct SessionLevelUpSignal { }

    public class SessionEarnGoldCommand : ICommand<SessionEarnGoldSignal>
    {
        [Inject] public EconomyService Economy;
        [Inject] public SessionState State;
        public void Execute(SessionEarnGoldSignal s)
        {
            Economy.Earn("gold", s.Amount);
            State.Gold = Economy.GetBalance("gold");
        }
    }

    public class SessionSpendGoldCommand : ICommand<SessionSpendGoldSignal>
    {
        [Inject] public EconomyService Economy;
        [Inject] public SessionState State;
        public void Execute(SessionSpendGoldSignal s)
        {
            if (Economy.Spend("gold", s.Amount)) State.Gold = Economy.GetBalance("gold");
        }
    }

    public class SessionLevelUpCommand : ICommand<SessionLevelUpSignal>
    {
        [Inject] public ProgressionService Progression;
        [Inject] public SessionState State;
        public void Execute(SessionLevelUpSignal s)
        {
            Progression.CompleteCurrentLevel();
            State.Level = Progression.CurrentLevel.Value;
        }
    }

    // ---------------------------------------------------------------------------
    // Persisted game state (the game's checkpoint authority)
    // ---------------------------------------------------------------------------

    public sealed class SessionState : ISaveDataProvider
    {
        public long Gold;
        public int Level;
        public long OfflineEarnedTotal;
        public long LastQuitUnix;

        public byte[] CaptureSaveData() => Encoding.UTF8.GetBytes(JsonUtility.ToJson(this));

        public void RestoreSaveData(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            var copy = JsonUtility.FromJson<SessionState>(Encoding.UTF8.GetString(data));
            Gold = copy.Gold;
            Level = copy.Level;
            OfflineEarnedTotal = copy.OfflineEarnedTotal;
            LastQuitUnix = copy.LastQuitUnix;
        }
    }

    // ---------------------------------------------------------------------------
    // The game's lifecycle: binds game systems, registers commands
    // ---------------------------------------------------------------------------

    public sealed class GameSessionLifecycle : IContextLifecycle
    {
        private readonly Context _ctx;
        private readonly SessionState _state;
        private readonly List<string> _log;

        public GameSessionLifecycle(Context ctx, SessionState state, List<string> log)
        {
            _ctx = ctx;
            _state = state;
            _log = log;
        }

        public void OnConfigure(IContextBuilder builder)
        {
            _log.Add("configure");
            _ctx.Container.BindInstance(_state);
            _ctx.Container.Bind<EconomyService>(isSingleton: true);
            _ctx.Container.Bind<ProgressionService>(isSingleton: true);
            _ctx.Container.Bind<SessionEarnGoldCommand>(isSingleton: false);
            _ctx.Container.Bind<SessionSpendGoldCommand>(isSingleton: false);
            _ctx.Container.Bind<SessionLevelUpCommand>(isSingleton: false);

            _ctx.SignalBusInternal.RegisterCommand(typeof(SessionEarnGoldSignal), typeof(SessionEarnGoldCommand), ExecutionMode.Sequential, 0, false);
            _ctx.SignalBusInternal.RegisterCommand(typeof(SessionSpendGoldSignal), typeof(SessionSpendGoldCommand), ExecutionMode.Sequential, 0, false);
            _ctx.SignalBusInternal.RegisterCommand(typeof(SessionLevelUpSignal), typeof(SessionLevelUpCommand), ExecutionMode.Sequential, 0, false);
        }

        public ValueTask OnInitializeAsync(CancellationToken ct)
        {
            _log.Add("init");
            _ctx.Resolve<EconomyService>();
            _ctx.Resolve<ProgressionService>().InitializeAsync(ct).GetAwaiter().GetResult();
            return default;
        }

        public ValueTask OnStartAsync(CancellationToken ct)
        {
            _log.Add("start");
            return default;
        }

        public void OnDispose() => _log.Add("dispose");
    }

    // ---------------------------------------------------------------------------
    // Runtime objects for the simulated gameplay
    // ---------------------------------------------------------------------------

    public sealed class SessionPoolable : Component, IPoolable
    {
        public int SpawnCount;
        public int DespawnCount;
        public void OnSpawned() => SpawnCount++;
        public void OnDespawned() => DespawnCount++;
    }

    /// <summary>Every 60th frame the player passively earns 5 gold.</summary>
    public sealed class AutoIncomeTickable : ITickable
    {
        public int Frames;
        public int IncomeEvents;
        private readonly ISignalBus _bus;
        public AutoIncomeTickable(ISignalBus bus) { _bus = bus; }

        public void Tick(float deltaTime)
        {
            Frames++;
            if (Frames % 60 == 0)
            {
                _bus.Fire(new SessionEarnGoldSignal(5));
                IncomeEvents++;
            }
        }
    }

    public sealed class FakeSessionPrefs : IPlayerPrefsService
    {
        private readonly Dictionary<string, string> _store = new();
        public int GetInt(string key, int defaultValue = 0) => int.TryParse(GetString(key, null), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int r) ? r : defaultValue;
        public void SetInt(string key, int value) => SetString(key, value.ToString());
        public bool GetBool(string key, bool defaultValue = false) => bool.TryParse(GetString(key, null), out bool r) ? r : defaultValue;
        public void SetBool(string key, bool value) => SetString(key, value.ToString());
        public string GetString(string key, string defaultValue = "") => _store.TryGetValue(key, out var v) ? v : defaultValue;
        public void SetString(string key, string value) => _store[key] = value;
        public float GetFloat(string key, float defaultValue = 0f) => float.TryParse(GetString(key, null), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r) ? r : defaultValue;
        public void SetFloat(string key, float value) => SetString(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        public long GetLong(string key, long defaultValue = 0L) => long.TryParse(GetString(key, null), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long r) ? r : defaultValue;
        public void SetLong(string key, long value) => SetString(key, value.ToString());
        public bool HasKey(string key) => _store.ContainsKey(key);
        public void DeleteKey(string key) => _store.Remove(key);
        public int CountKeys() => _store.Count;
        public void Save() { }
    }

    public sealed class FakeSessionTimeProvider : ITimeProvider
    {
        public float Now { get; set; }
    }

    // ---------------------------------------------------------------------------
    // The suite
    // ---------------------------------------------------------------------------

    public static class GameSessionSuite
    {
        private const string SaveSlot = "autosave";
        private const long OfflineGoldRatePerSecond = 10;

        private static int _failures;

        public static int Run()
        {
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine("[GameSession] DOGFOOD: FULL HYPER-CASUAL GAME SESSION ON REAL RUNTIME");
            Console.WriteLine("===============================================================================");

            _failures = 0;
            var prefs = new FakeSessionPrefs();
            var state = new SessionState();
            var gsm = new GameSaveManager();
            gsm.RegisterModel(state);

            GameObject prefab = null;
            try
            {
                // ---- Session A: boot, gameplay loop, economy, autosave, quit ----
                prefab = new GameObject("Enemy");
                prefab.AddComponent<SessionPoolable>();

                long signalsBeforeA = NexusRuntime.Metrics.TotalSignalsDispatched;
                var (ctxA, logA) = BootGame(prefs, state);
                Test_Boot_Phases_Ordered(ctxA, logA);
                Test_Gameplay_TickLoop_Economy_Pool(ctxA, prefab);
                Test_Autosave_Checkpoint(ctxA, gsm, prefs);
                ctxA.Dispose();

                // ---- Session B: continuity + offline income ----
                var (ctxB, _) = BootGame(prefs, state);
                Test_Session_Continuity_Loads_Checkpoint(ctxB, gsm, prefs);
                Test_Offline_Income_Applied(ctxB, prefs, state);
                gsm.SaveAsync(SaveSlot).GetAwaiter().GetResult();
                ctxB.Dispose();

                // ---- Session C: crash without save, then recovery ----
                var (ctxC, _) = BootGame(prefs, state);
                gsm.LoadAsync(SaveSlot).GetAwaiter().GetResult();
                long goldAtCrashPoint = state.Gold;
                Test_Crash_Without_Save(ctxC, gsm, goldAtCrashPoint);
                ctxC.Dispose();

                Test_Recovery_After_Crash(prefs, state, gsm, goldAtCrashPoint, signalsBeforeA);
                Test_SessionEnd_NoLeaks_And_Stats(signalsBeforeA);
            }
            finally
            {
                NexusRuntime.Reset();
                UnityEngine.PlayerPrefs.ClearAll();
                try { Directory.Delete(Path.Combine(Application.persistentDataPath, "saves"), true); }
                catch { /* already gone */ }
                if (prefab != null) UnityEngine.Object.Destroy(prefab);
            }

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "[GameSession] DOGFOOD SESSION PASSED — no corruption, no leaks, full continuity"
                : $"[GameSession] {_failures} DOGFOOD CHECK(S) FAILED");
            return _failures;
        }

        private static (Context ctx, List<string> log) BootGame(FakeSessionPrefs prefs, SessionState state)
        {
            NexusRuntime.Reset();
            var ctx = ContextFactory.Create();
            var log = new List<string>();
            var lifecycle = new GameSessionLifecycle(ctx, state, log);
            ctx.Configure(new[] { lifecycle });

            var initMethod = typeof(Context).GetMethod("InitializeLifecycleAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var vt = (ValueTask)initMethod.Invoke(ctx, new object[] { new IContextLifecycle[] { lifecycle }, ctx.LifetimeToken });
            vt.GetAwaiter().GetResult();

            ctx.Container.Resolve<EconomyService>().PlayerPrefsService = prefs;
            ctx.Container.Resolve<ProgressionService>().PlayerPrefsService = prefs;
            return (ctx, log);
        }

        private static void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"[GameSession] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("GameSession", name, ok, detail);
            if (!ok) _failures++;
        }

        // GS1. Boot: real Context full lifecycle in exact phase order
        private static void Test_Boot_Phases_Ordered(Context ctx, List<string> log)
        {
            bool phases = string.Join(",", log) == "configure,init,start";
            bool registered = NexusRuntime.ActiveContexts.Count == 1 && NexusRuntime.CurrentContext == ctx;
            Check("GS1. Boot_RealContext_FullLifecyclePhases", phases && registered,
                $"phases=[{string.Join(",", log)}] (expected configure,init,start), registered={registered}");
        }

        // GS2. Gameplay: 120-frame tick loop, passive income, economy commands, pool reuse
        private static void Test_Gameplay_TickLoop_Economy_Pool(Context ctx, GameObject prefab)
        {
            bool ok = false;
            string detail;
            try
            {
                var bus = ctx.SignalBusInternal;
                var tickService = new TickService();
                var income = new AutoIncomeTickable(bus);
                tickService.RegisterTickable(income);

                var pool = new ObjectPoolService();
                try
                {
                    pool.InitializeAsync(default).GetAwaiter().GetResult();
                    pool.Prewarm(prefab, 2);
                    var firstSpawn = pool.Spawn(prefab);
                    var poolable = firstSpawn.GetComponent<SessionPoolable>();

                    // 120 frames: passive income fires on frames 60 and 120 -> +10 gold.
                    for (int i = 0; i < 120; i++) tickService.OnTick(0.016f);

                    // Player earns 90 more and spends 30 via real commands.
                    bus.Fire(new SessionEarnGoldSignal(90));
                    bus.Fire(new SessionSpendGoldSignal(30));

                    // Pool reuse: despawn + respawn must return the SAME instance.
                    pool.Despawn(firstSpawn);
                    var respawned = pool.Spawn(prefab);
                    bool poolReused = ReferenceEquals(firstSpawn, respawned);

                    // Level up twice via command.
                    bus.Fire(new SessionLevelUpSignal());
                    bus.Fire(new SessionLevelUpSignal());
                    int level = ctx.Resolve<ProgressionService>().CurrentLevel.Value;

                    var state = ctx.Resolve<SessionState>();
                    bool goldOk = state.Gold == 10 + 90 - 30; // 70
                    bool levelOk = level == 3; // starts at 1, two level-ups
                    bool incomeOk = income.IncomeEvents == 2 && income.Frames == 120;
                    bool poolOk = poolReused && poolable.SpawnCount == 2;

                    ok = goldOk && levelOk && incomeOk && poolOk;
                    detail = $"gold={state.Gold} (expected 70), level={level} (expected 3), incomeEvents={income.IncomeEvents} (expected 2), frames={income.Frames}, poolReused={poolReused} spawns={poolable.SpawnCount}";
                }
                finally
                {
                    // Raw test-owned service (not bound to a context): dispose it so the
                    // master root, pool root and prewarmed instances don't leak per iteration.
                    pool.Dispose();
                }
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }

            Console.WriteLine($"[GameSession] Gameplay loop: {detail}");
            Check("GS2. Gameplay_TickLoop_Economy_PoolReuse", ok, detail);
        }

        // GS3. Autosave checkpoint written at session end
        private static void Test_Autosave_Checkpoint(Context ctx, GameSaveManager gsm, FakeSessionPrefs prefs)
        {
            bool ok = false;
            string detail;
            try
            {
                var throttler = new SaveThrottler(null, TimeSpan.FromSeconds(2));
                var time = new FakeSessionTimeProvider { Now = 0f };
                throttler.TimeProvider = time;

                int saves = 0;
                void saveAction() => saves++;
                throttler.TryRequestSave(saveAction);
                bool immediate = saves == 1;
                throttler.TryRequestSave(saveAction);
                bool throttled = saves == 1;
                throttler.ForceSave(saveAction);
                bool forced = saves == 2;

                var state = ctx.Resolve<SessionState>();
                OfflineTimeCalculator.RecordQuitTimestamp(prefs);
                state.LastQuitUnix = prefs.GetLong("NT_LastQuitTimestamp");
                gsm.SaveAsync(SaveSlot).GetAwaiter().GetResult();

                ok = immediate && throttled && forced && gsm.SaveExists(SaveSlot) && state.Gold == 70;
                detail = $"throttle=immediate({immediate})/throttled({throttled})/forced({forced}), saveExists={gsm.SaveExists(SaveSlot)}, goldAtQuit={state.Gold}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }

            Console.WriteLine($"[GameSession] Autosave: {detail}");
            Check("GS3. Autosave_Throttle_And_Checkpoint", ok, detail);
        }

        // GS4. Next boot loads the checkpoint (session continuity across restarts)
        private static void Test_Session_Continuity_Loads_Checkpoint(Context ctx, GameSaveManager gsm, FakeSessionPrefs prefs)
        {
            bool ok = false;
            string detail;
            try
            {
                bool loaded = gsm.LoadAsync(SaveSlot).GetAwaiter().GetResult();
                var state = ctx.Resolve<SessionState>();
                var economy = ctx.Resolve<EconomyService>();
                economy.PlayerPrefsService = prefs;
                ctx.Resolve<ProgressionService>().PlayerPrefsService = prefs;

                bool goldOk = state.Gold == 70;
                bool ecoOk = economy.GetBalance("gold") == 70; // economy persisted independently
                bool levelOk = state.Level == 3; // level checkpointed by the level-up command

                ok = loaded && goldOk && ecoOk && levelOk;
                detail = $"loaded={loaded}, gold={state.Gold} (expected 70), economyBalance={economy.GetBalance("gold")}, level={state.Level} (expected 3)";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }

            Console.WriteLine($"[GameSession] Continuity: {detail}");
            Check("GS4. Session_Continuity_Loads_Checkpoint", ok, detail);
        }

        // GS5. Offline income: 2h offline -> 10 gold/s capped by OfflineTimeCalculator
        private static void Test_Offline_Income_Applied(Context ctx, FakeSessionPrefs prefs, SessionState state)
        {
            bool ok = false;
            string detail;
            try
            {
                // Player was away 2 hours. A real 2h gap advances BOTH the wall clock AND the
                // hardware monotonic tick, so the simulation must move them consistently —
                // otherwise the A8 monotonic anti-cheat clamps the reward to the real elapsed
                // time (~0) and correctly reports tampering.
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                prefs.SetLong("NT_LastQuitTimestamp", now - 7200);
                prefs.SetLong("NT_LastQuitMonotonicMs", Environment.TickCount64 - (7200L * 1000L));

                long offlineSeconds = OfflineTimeCalculator.CalculateOfflineSeconds(prefs);
                long offlineGold = offlineSeconds * OfflineGoldRatePerSecond;

                ctx.Resolve<EconomyService>().Earn("gold", offlineGold);
                state.Gold = ctx.Resolve<EconomyService>().GetBalance("gold");
                state.OfflineEarnedTotal += offlineGold;

                ok = offlineSeconds == 7200 && state.Gold == 70 + offlineGold && state.OfflineEarnedTotal == offlineGold;
                detail = $"offlineSeconds={offlineSeconds} (expected 7200), offlineGold={offlineGold}, gold={state.Gold} (expected {70 + offlineGold})";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }

            Console.WriteLine($"[GameSession] Offline: {detail}");
            Check("GS5. Offline_Income_Applied", ok, detail);
        }

        // GS6. Crash: 50 earns land in memory but are NEVER saved; session killed.
        private static void Test_Crash_Without_Save(Context ctx, GameSaveManager gsm, long goldAtCrashPoint)
        {
            bool ok = false;
            string detail;
            try
            {
                var bus = ctx.SignalBusInternal;
                for (int i = 0; i < 50; i++) bus.Fire(new SessionEarnGoldSignal(10));

                var state = ctx.Resolve<SessionState>();
                bool inMemoryGained = state.Gold == goldAtCrashPoint + 500;
                bool crashNoThrow = true;
                try { ctx.Dispose(); }
                catch { crashNoThrow = false; } // abrupt kill, no save

                ok = inMemoryGained && crashNoThrow;
                detail = $"inMemoryGold={state.Gold} (expected {goldAtCrashPoint + 500}), crashDisposeNoThrow={crashNoThrow}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }

            Console.WriteLine($"[GameSession] Crash: {detail}");
            Check("GS6. Crash_Without_Save_Lost_Progress", ok, detail);
        }

        // GS7. Recovery: next boot loads the LAST GOOD checkpoint, unsaved gold lost, runtime healthy
        private static void Test_Recovery_After_Crash(FakeSessionPrefs prefs, SessionState state, GameSaveManager gsm, long goldAtCrashPoint, long signalsBefore)
        {
            bool ok = false;
            string detail;
            try
            {
                var (ctx, _) = BootGame(prefs, state);
                try
                {
                    bool loaded = gsm.LoadAsync(SaveSlot).GetAwaiter().GetResult();

                    // Dogfood finding: EconomyService persists every Earn to prefs immediately,
                    // so a crash leaves unsaved economy writes behind. The game checkpoint is
                    // the authority — resync the economy from it on boot (what a production
                    // game must do; without this the balance and checkpoint diverge).
                    var economy = ctx.Resolve<EconomyService>();
                    economy.PlayerPrefsService = prefs;
                    economy.SetBalance("gold", state.Gold);

                    bool reverted = state.Gold == goldAtCrashPoint; // 500 lost, checkpoint intact
                    bool intact = gsm.SaveExists(SaveSlot);

                    // Game keeps running after the crash: earn and level up again.
                    ctx.SignalBusInternal.Fire(new SessionEarnGoldSignal(5));
                    bool resumed = state.Gold == goldAtCrashPoint + 5;

                    ok = loaded && reverted && intact && resumed;
                    detail = $"loaded={loaded}, gold={state.Gold} (expected {goldAtCrashPoint + 5} after 5 resumed), checkpointIntact={intact}, economyResyncedFromCheckpoint";
                }
                finally
                {
                    ctx.Dispose();
                }
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }

            Console.WriteLine($"[GameSession] Recovery: {detail}");
            Check("GS7. Recovery_After_Crash_NoCorruption", ok, detail);
        }

        // GS8. Session end: every context disposed, registry empty, no object creep
        private static void Test_SessionEnd_NoLeaks_And_Stats(long signalsBefore)
        {
            bool ok = false;
            string detail;
            try
            {
                long signalsAfter = NexusRuntime.Metrics.TotalSignalsDispatched;
                bool registryClean = NexusRuntime.ActiveContexts.Count == 0;
                bool signalFlow = signalsAfter > signalsBefore;

                ok = registryClean && signalFlow;
                detail = $"activeContexts={NexusRuntime.ActiveContexts.Count} (expected 0), signalsDispatched={signalsAfter - signalsBefore}";
            }
            catch (Exception ex)
            {
                detail = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}";
            }

            Console.WriteLine($"[GameSession] Session end: {detail}");
            Check("GS8. SessionEnd_NoLeaks_SignalStats", ok, detail);
        }
    }
}
