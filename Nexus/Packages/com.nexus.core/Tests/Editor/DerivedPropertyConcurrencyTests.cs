using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
#if UNITY_EDITOR
using NUnit.Framework;
#endif

namespace Nexus.Editor.Tests
{
    public sealed class DerivedPropertyConcurrencyTests
    {
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void InitialCompute_CannotOverwriteANewerSourceResult_InEveryOperator()
        {
            for (int arity = 1; arity <= 3; arity++)
            {
                var first = new ObservableProperty<int>(1);
                var second = new ObservableProperty<int>(10);
                var third = new ObservableProperty<int>(100);
                using var captured = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                int calls = 0;
                int Compute(int value)
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        captured.Set();
                        Require(release.Wait(5000), "initial computation was not released");
                    }
                    return value * 10;
                }
                int inputs = arity;
                var creating = Task.Run(() => inputs == 1 ? first.Select(Compute)
                    : inputs == 2 ? first.CombineLatest(second, (a, b) => Compute(a + b))
                    : first.CombineLatest(second, third, (a, b, c) => Compute(a + b + c)));
                try
                {
                    Require(captured.Wait(5000), "initial computation did not start");
                    if (arity == 1) first.Value = 2;
                    else if (arity == 2) second.Value = 20;
                    else third.Value = 200;
                }
                finally { release.Set(); }
                using var derived = creating.GetAwaiter().GetResult();
                int expected = arity == 1 ? 20 : arity == 2 ? 210 : 2110;
                Require(derived.Value == expected, $"arity {arity}: expected {expected}, got {derived.Value}");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void CombineLatest_OlderComputeFinishingLast_DoesNotOverwriteNewerResult()
        {
            for (int arity = 2; arity <= 3; arity++)
            {
                var first = new ObservableProperty<int>(1);
                var second = new ObservableProperty<int>(10);
                var third = new ObservableProperty<int>(100);
                using var captured = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                int slow = arity == 2 ? 12 : 112;
                int Compute(int value)
                {
                    if (value == slow)
                    {
                        captured.Set();
                        Require(release.Wait(5000), "older computation was not released");
                    }
                    return value;
                }
                using var derived = arity == 2 ? first.CombineLatest(second, (a, b) => Compute(a + b))
                    : first.CombineLatest(second, third, (a, b, c) => Compute(a + b + c));
                var updating = Task.Run(() => first.Value = 2);
                try
                {
                    Require(captured.Wait(5000), "older computation did not start");
                    if (arity == 2) second.Value = 20; else third.Value = 200;
                }
                finally { release.Set(); }
                updating.GetAwaiter().GetResult();
                int expected = arity == 2 ? 22 : 212;
                Require(derived.Value == expected, $"arity {arity}: newer result {expected} was overwritten by {derived.Value}");
            }
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void Dispose_DuringSelector_DropsItsResultAndDetaches()
        {
            var source = new ObservableProperty<int>(1);
            using var captured = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int calls = 0, notified = 0;
            using var derived = source.Select(value =>
            {
                Interlocked.Increment(ref calls);
                if (value == 2)
                {
                    captured.Set();
                    Require(release.Wait(5000), "selector was not released");
                }
                return value * 10;
            });
            derived.OnChanged((_, _) => notified++);
            var updating = Task.Run(() => source.Value = 2);
            try
            {
                Require(captured.Wait(5000), "selector did not start");
                derived.Dispose();
            }
            finally { release.Set(); }
            updating.GetAwaiter().GetResult();
            source.Value = 3;
            Require(derived.IsDisposed && derived.Value == 10 && calls == 2 && notified == 0,
                "dispose must reject the in-flight result and stop later computations/notifications");
        }

#if UNITY_EDITOR
        [Test]
#endif
        public void Notification_CanWaitForAnotherSourceWriter_WithoutHoldingTheCommitLock()
        {
            var first = new ObservableProperty<int>(1);
            var second = new ObservableProperty<int>(10);
            using var derived = first.CombineLatest(second, (a, b) => a + b);
            Task writer = null;
            derived.OnChanged((_, value) =>
            {
                if (value != 12) return;
                writer = Task.Run(() => second.Value = 20);
                Require(writer.Wait(5000), "a notification blocked another source writer on the derived-property lock");
            });
            try { first.Value = 2; }
            finally { writer?.GetAwaiter().GetResult(); }
            Require(derived.Value == 22, "the newer result queued during notification must be published");
        }
    }
}
