using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Core.Extensions;
using UnityEngine;

namespace NexusBench
{
    /// <summary>
    /// Regression cover for the 2026-11-06 review findings on signals, commands, saves and models
    /// (#294 #295 #296 #297 #298 #299). Each case fails on the code before its fix.
    /// </summary>
    public static class SignalsReviewSuite
    {
        private static int _failures;

        private static void Report(string name, bool ok, string detail)
        {
            Console.WriteLine($"[SigRev] {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
            ResultSink.Capture("SigRev", name, ok, detail);
            if (!ok) _failures++;
        }

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine();
            Console.WriteLine("===============================================================================");
            Console.WriteLine("[SigRev] SIGNALS / COMMANDS / SAVE / MODEL REVIEW FIXES");
            Console.WriteLine("===============================================================================");

            Run("SR1", Test_NonGenericCommand_RejectedAtBind);
            Run("SR2", Test_CommandOwnCancellation_IsAFailure);
            Run("SR3", Test_DispatchTimeout_StaysCancellation);
            Run("SR4", Test_DeleteSave_SkipsSnapshotCapturedBefore);
            Run("SR5", Test_NewVersionedObject_StartsAtCurrentVersion);
            Run("SR9", Test_VersionedObject_HidingTheHook_StillMigrates);

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "[SigRev] ALL REVIEW-FIX TESTS PASSED ✓"
                : $"[SigRev] {_failures} REVIEW-FIX TEST(S) FAILED ✗");
            return _failures;
        }

        private static void Run(string id, Action test)
        {
            try { test(); }
            catch (Exception ex) { Report(id, false, $"threw {ex.GetType().Name}: {ex.Message}"); }
        }

        private static SignalBus NewBus(NexusDI container) =>
            new SignalBus(container, new CommandPoolManager(container), new MockContext());

        // ── #294: a non-generic command registered for a signal ───────────────
        private static void Test_NonGenericCommand_RejectedAtBind()
        {
            var container = new NexusDI();
            var bus = NewBus(container);
            bool syncRejected = false, asyncRejected = false;
            try { bus.RegisterCommand(typeof(SrSignal), typeof(SrPlainCommand), ExecutionMode.Sequential, 0, isAsync: false); }
            catch (InvalidOperationException) { syncRejected = true; }
            try { bus.RegisterCommand(typeof(SrAsyncSignal), typeof(SrPlainAsyncCommand), ExecutionMode.Sequential, 0, isAsync: true); }
            catch (InvalidOperationException) { asyncRejected = true; }
            bus.Dispose();
            container.Dispose();
            Report("SR1. NonGenericCommand_ForASignal_IsRejectedAtRegistration", syncRejected && asyncRejected,
                $"ICommand rejected={syncRejected}, IAsyncCommand rejected={asyncRejected} (before: both accepted, then failed at every fire)");
        }

        // ── #295: a command's own OperationCanceledException ──────────────────
        private static void Test_CommandOwnCancellation_IsAFailure()
        {
            var container = new NexusDI();
            var bus = NewBus(container);
            bus.RegisterCommand(typeof(SrCancelSignal), typeof(SrSelfCancellingCommand), ExecutionMode.Sequential, 0, isAsync: true);
            int subscriberRan = 0;
            var failures = new List<Exception>();
            bus.Subscribe<SrCancelSignal>(_ => subscriberRan++);
            bus.Subscribe<CommandFailedSignal>(s => failures.Add(s.Exception));

            Exception thrown = null;
            try { bus.FireAsync(new SrCancelSignal()).AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) { thrown = ex; }
            Exception timeoutThrown = null;
            try { bus.FireAsyncWithTimeout(new SrCancelSignal(), 5000).AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) { timeoutThrown = ex; }
            bus.Dispose();
            container.Dispose();

            bool logged = failures.Count == 2 && RecoveryEngine.WasLogged(failures[0]) && RecoveryEngine.WasLogged(failures[1]);
            bool ok = thrown == null && timeoutThrown == null && logged && failures[0] is OperationCanceledException && subscriberRan == 2;
            Report("SR2. CommandOwnCancellation_IsLoggedAsFailure_AndTheDispatchGoesOn", ok,
                $"FireAsync threw={thrown?.GetType().Name ?? "nothing"}, FireAsyncWithTimeout threw={timeoutThrown?.GetType().Name ?? "nothing"}, " +
                $"CommandFailedSignal x{failures.Count}/2, logged={logged}, subscriber runs={subscriberRan}/2 " +
                "(before: the OCE ended the dispatch unlogged and FireAsyncWithTimeout reported a timeout)");
        }

        // The dispatch's own token (FireAsyncWithTimeout's timeout) still ends the dispatch as a cancellation.
        private static void Test_DispatchTimeout_StaysCancellation()
        {
            var container = new NexusDI();
            var bus = NewBus(container);
            bus.RegisterCommand(typeof(SrHangSignal), typeof(SrHangingCommand), ExecutionMode.Sequential, 0, isAsync: true);
            Exception failure = null;
            bus.Subscribe<CommandFailedSignal>(s => failure = s.Exception);
            Exception thrown = null;
            try { bus.FireAsyncWithTimeout(new SrHangSignal(), 50).AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) { thrown = ex; }
            bus.Dispose();
            container.Dispose();
            Report("SR3. DispatchTimeout_StaysACancellation", thrown is OperationCanceledException && failure == null,
                $"threw={thrown?.GetType().Name ?? "nothing"}, CommandFailedSignal={(failure == null ? "none" : failure.GetType().Name)}");
        }

        // ── #296: DeleteSave against a save captured before it ────────────────
        private static void Test_DeleteSave_SkipsSnapshotCapturedBefore()
        {
            const string slot = "sigrev-delete-slot";
            var gsm = new GameSaveManager();
            gsm.RegisterModel(new AuditSaveModel());
            object saveLock = typeof(GameSaveManager).GetField("_saveLock", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(gsm);
            bool exists;
            try
            {
                Task save;
                Monitor.Enter(saveLock); // the save's worker waits here after its capture
                try
                {
                    save = gsm.SaveAsync(slot);
                    gsm.DeleteSave(slot); // same thread: Monitor is reentrant
                }
                finally { Monitor.Exit(saveLock); }
                save.GetAwaiter().GetResult();
                exists = gsm.SaveExists(slot);
            }
            finally
            {
                try { gsm.DeleteSave(slot); } catch { }
                gsm.Dispose();
            }
            Report("SR4. DeleteSave_WinsOverASaveCapturedBeforeIt", !exists,
                $"slot exists after delete={exists} (before: the earlier snapshot was written after the delete)");
        }

        // ── #297: a new VersionedScriptableObject ─────────────────────────────
        private static void Test_NewVersionedObject_StartsAtCurrentVersion()
        {
            var fresh = ScriptableObject.CreateInstance<SrVersioned>();
            fresh.Enable();
            bool freshOk = fresh.Migrations == 0 && fresh.Version == 2 && fresh.Speed == 5f;

            // An asset loaded without the version field (saved before it existed) still migrates.
            var legacy = ScriptableObject.CreateInstance<SrVersioned>();
            ((object)legacy as ISerializationCallbackReceiver)?.OnAfterDeserialize();
            legacy.Enable();
            bool legacyOk = legacy.Migrations == 1 && legacy.Version == 2;

            Report("SR5. NewVersionedObject_StartsAtCurrentVersion_LoadedOneStillMigrates", freshOk && legacyOk,
                $"new: migrations={fresh.Migrations} version={fresh.Version} speed={fresh.Speed} (5 expected); " +
                $"loaded at version 0: migrations={legacy.Migrations} version={legacy.Version}");
        }

        // ── #297 D3: a subclass that implements ISerializationCallbackReceiver itself ──
        private static void Test_VersionedObject_HidingTheHook_StillMigrates()
        {
            int errors = 0;
            Action<string, string> listen = (level, text) =>
            {
                if (level == "Error" && text != null && text.Contains(nameof(SrHidingVersioned))) errors++;
            };
            Debug.Logged += listen;
            try
            {
                // Loaded at version 0: Unity calls the subclass's OnAfterDeserialize, never the base's hook.
                var loaded = ScriptableObject.CreateInstance<SrHidingVersioned>();
                ((ISerializationCallbackReceiver)loaded).OnAfterDeserialize();
                loaded.Enable();
                var second = ScriptableObject.CreateInstance<SrHidingVersioned>();
                ((ISerializationCallbackReceiver)second).OnAfterDeserialize();
                second.Enable();
                bool ok = loaded.Migrations == 1 && loaded.Version == 2 && second.Migrations == 1 && errors == 1;
                Report("SR9. VersionedObject_HidingTheSerializationHook_StillMigrates_AndLogsOnce", ok,
                    $"migrations={loaded.Migrations}/{second.Migrations} version={loaded.Version} errors={errors} (1 expected) " +
                    "(before: the loaded asset was stamped current and skipped Migrate)");
            }
            finally { Debug.Logged -= listen; }
        }

    }

    public struct SrSignal { }
    public struct SrAsyncSignal { }
    public sealed class SrPlainCommand : ICommand { public void Execute() { } }
    public sealed class SrPlainAsyncCommand : IAsyncCommand { public ValueTask ExecuteAsync(CancellationToken ct) => default; }

    public struct SrCancelSignal { }
    // Its own request timed out (an HttpClient timeout, a CancellationTokenSource it made): the token Nexus passed is live.
    public sealed class SrSelfCancellingCommand : IAsyncCommand<SrCancelSignal>
    {
        public ValueTask ExecuteAsync(SrCancelSignal signal, CancellationToken ct) => throw new OperationCanceledException("own request timed out");
    }

    public struct SrHangSignal { }
    public sealed class SrHangingCommand : IAsyncCommand<SrHangSignal>
    {
        public async ValueTask ExecuteAsync(SrHangSignal signal, CancellationToken ct) => await Task.Delay(Timeout.Infinite, ct);
    }

    public sealed class SrVersioned : VersionedScriptableObject
    {
        public float Speed = 5f; // v2 layout: units per second; v1 stored units per frame
        public int Migrations;
        public override int CurrentVersion => 2;
        public void Enable() => OnEnable();
        protected override void Migrate(int fromVersion)
        {
            Migrations++;
            if (fromVersion < 2) Speed *= 60f;
        }
    }

    public sealed class SrHidingVersioned : VersionedScriptableObject, ISerializationCallbackReceiver
    {
        public int Migrations;
        public override int CurrentVersion => 2;
        public void Enable() => OnEnable();
        protected override void Migrate(int fromVersion) => Migrations++;
        void ISerializationCallbackReceiver.OnBeforeSerialize() { }
        void ISerializationCallbackReceiver.OnAfterDeserialize() { }
    }

}
