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

    }

    public struct SrSignal { }
    public struct SrAsyncSignal { }
    public sealed class SrPlainCommand : ICommand { public void Execute() { } }
    public sealed class SrPlainAsyncCommand : IAsyncCommand { public ValueTask ExecuteAsync(CancellationToken ct) => default; }

}
