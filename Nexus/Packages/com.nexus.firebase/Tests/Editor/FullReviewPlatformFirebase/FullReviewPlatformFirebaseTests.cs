#if UNITY_INCLUDE_TESTS && NEXUS_FULL_REVIEW_FIREBASE_141
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core;
using Nexus.Firebase.Analytics;
using Nexus.Firebase.Sdk;
using NUnit.Framework;
using UnityEditor.PackageManager;
using FirebaseDependencyStatus = global::Firebase.DependencyStatus;
using FirebaseParameter = global::Firebase.Analytics.Parameter;

namespace Nexus.Editor.Tests
{
    // EditMode, Firebase app/analytics/crashlytics/remote-config 14.1.0, and NEXUS_FULL_REVIEW_FIREBASE_141.
    // All SDK and PlayerPrefs delegates are replaced before binding or initialization.
    public sealed class FullReviewPlatformFirebaseTests
    {
        [SetUp]
        public void RequirePinnedFirebasePackages()
        {
            var registered = PackageInfo.GetAllRegisteredPackages();
            foreach (string name in new[]
            {
                "com.google.firebase.app", "com.google.firebase.analytics",
                "com.google.firebase.crashlytics", "com.google.firebase.remote-config"
            })
            {
                var package = Array.Find(registered, candidate => candidate.name == name);
                if (package == null || package.version != "14.1.0")
                    Assert.Inconclusive("This controlled SDK fixture requires registered " + name + " 14.1.0.");
            }

            // Metadata only: identify the loaded SDK assemblies without initializing Firebase.
            RequirePinnedAssembly(typeof(FirebaseDependencyStatus).Assembly, "com.google.firebase.app");
            RequirePinnedAssembly(typeof(FirebaseParameter).Assembly, "com.google.firebase.analytics");
        }

        [Test]
        public async Task P03_AnalyticsDisposedDuringDependencyCheck_DoesNotSubscribeOrSend()
        {
            var io = new AnalyticsIo();
            io.Consent.Set(ConsentState.NotRequired, AnalyticsChoice.Unknown);
            var dependency = new TaskCompletionSource<FirebaseDependencyStatus>();
            io.Service.CheckDependenciesAsync = () => dependency.Task;
            using var test = Bind(io.Service, io.Consent);
            Task startup = test.InitializeAsync().AsTask();
            try
            {
                Assert.That(startup.IsCompleted, Is.False, "The real service must await the controlled dependency check.");
                io.Service.LogEvent("review_queued");
                io.Service.SetUserProperty("review_cohort", "held");
                io.Service.Dispose();
                dependency.SetResult(FirebaseDependencyStatus.Available);
                await FinishDisposedStartup(startup);

                Assert.That(io.Consent.SubscriberCount, Is.Zero, "A disposed service must not attach a late consent handler.");
                Assert.That(io.CollectionChanges.Count + io.Properties.Count + io.Events.Count, Is.Zero,
                    "Finishing dependencies after disposal must not enable collection or flush held data.");
            }
            finally
            {
                dependency.TrySetResult(FirebaseDependencyStatus.Available);
                try { await FinishDisposedStartup(startup); }
                finally { io.Service.Dispose(); }
            }
        }

        [Test]
        public async Task P03_CrashReporterDisposedDuringDependencyCheck_DoesNotReopenOrSubscribe()
        {
            var io = new CrashIo();
            io.Consent.Set(ConsentState.NotRequired, AnalyticsChoice.Unknown);
            var dependency = new TaskCompletionSource<FirebaseDependencyStatus>();
            io.Service.CheckDependenciesAsync = () => dependency.Task;
            using var test = Bind(io.Service, io.Consent);
            Task startup = test.InitializeAsync().AsTask();
            try
            {
                Assert.That(startup.IsCompleted, Is.False);
                Assert.That(io.Service.SignalBus, Is.SameAs(test.Context.SignalBus), "Use the real injected service.");
                io.Service.SetCustomKey("screen", "boot");
                io.Service.Log("queued");
                io.Service.RecordNonFatal(new InvalidOperationException("queued"));
                io.Service.Dispose();
                dependency.SetResult(FirebaseDependencyStatus.Available);
                await FinishDisposedStartup(startup);

                Assert.That(io.Consent.SubscriberCount, Is.Zero, "A disposed reporter must not attach a late consent handler.");
                Assert.That(io.CollectionChanges.Count + io.Calls.Count, Is.Zero,
                    "Finishing dependencies after disposal must not reopen and flush CrashContext.");
            }
            finally
            {
                dependency.TrySetResult(FirebaseDependencyStatus.Available);
                try { await FinishDisposedStartup(startup); }
                finally { io.Service.Dispose(); }
            }
        }

        [Test]
        public async Task P03_RemoteConfigDisposedDuringDependencyCheck_DoesNotReadSdkSnapshot()
        {
            var io = new RemoteConfigIo();
            io.Values["review_key"] = "3";
            var dependency = new TaskCompletionSource<FirebaseDependencyStatus>();
            io.Service.CheckDependenciesAsync = () => dependency.Task;
            using var test = Bind(io.Service);
            Task startup = test.InitializeAsync().AsTask();
            try
            {
                Assert.That(startup.IsCompleted, Is.False);
                io.Service.Dispose();
                dependency.SetResult(FirebaseDependencyStatus.Available);
                await FinishDisposedStartup(startup);

                Assert.That(io.KeyReads + io.ValueReads, Is.Zero,
                    "A disposed service must not become ready and read the SDK snapshot after dependencies finish.");
                Assert.That(io.Service.GetInt("review_key", 17), Is.EqualTo(17));
            }
            finally
            {
                dependency.TrySetResult(FirebaseDependencyStatus.Available);
                try { await FinishDisposedStartup(startup); }
                finally { io.Service.Dispose(); }
            }
        }

        [Test]
        public async Task P07_PropertySetWhileDenied_IsNotSentAfterLaterGrant()
        {
            var io = new AnalyticsIo();
            io.Consent.Set(ConsentState.Obtained, AnalyticsChoice.Denied);
            using var test = Bind(io.Service, io.Consent);
            await test.InitializeAsync();

            io.Service.SetUserProperty("review_cohort", "denied_value");
            Assert.That(io.Properties.Count, Is.Zero);
            io.Consent.Set(ConsentState.Obtained, AnalyticsChoice.Granted);

            Assert.That(io.Properties.Count, Is.Zero, "Discarded properties must not survive for a later consent grant.");
        }

        [Test]
        public async Task AnalyticsUnknownConsent_HoldsPropertiesAndConvertsEventsBeforeSending()
        {
            var io = new AnalyticsIo();
            using var test = Bind(io.Service, io.Consent);
            io.Service.SetUserProperty("review_cohort", "held_value");
            io.Service.LogEvent("review_first", new Dictionary<string, object>
            {
                ["level"] = 3,
                ["won"] = true,
                ["ratio"] = 0.5f,
                ["mode"] = "test"
            });
            io.Service.LogEvent("review_second");
            await test.InitializeAsync();
            Assert.That(io.Properties.Count + io.Events.Count, Is.Zero, "Unknown consent still holds real service data.");

            io.Consent.Set(ConsentState.Obtained, AnalyticsChoice.Granted);

            CollectionAssert.AreEqual(new[] { "review_cohort=held_value" }, io.Properties);
            Assert.That(io.Events.Count, Is.EqualTo(2));
            Assert.That(io.Events[0].Name, Is.EqualTo("review_first"));
            Assert.That(io.Events[1].Name, Is.EqualTo("review_second"));
            var parameters = ReadParameters(io.Events[0].Parameters);
            Assert.That(parameters.Count, Is.EqualTo(4));
            Assert.That(parameters["level"], Is.TypeOf<long>());
            Assert.That(parameters["level"], Is.EqualTo(3L));
            Assert.That(parameters["won"], Is.EqualTo(1L));
            Assert.That(parameters["ratio"], Is.TypeOf<double>());
            Assert.That(parameters["ratio"], Is.EqualTo(0.5d));
            Assert.That(parameters["mode"], Is.EqualTo("test"));
            Assert.That(io.Events[1].Parameters.Length, Is.Zero);
            Assert.That(io.CollectionChanges[io.CollectionChanges.Count - 1], Is.True);
        }

        [Test]
        public async Task CrashReporterReady_FlushesBufferedContextAndKeepsRecursionGuard()
        {
            var io = new CrashIo();
            io.Consent.Set(ConsentState.NotRequired, AnalyticsChoice.Unknown);
            using var test = Bind(io.Service, io.Consent);
            io.Service.SetCustomKey("screen", "boot");
            io.Service.Log("queued");
            io.Service.RecordNonFatal(new InvalidOperationException("queued"));
            Assert.That(io.Calls.Count, Is.Zero);
            await test.InitializeAsync();

            CollectionAssert.AreEqual(new[] { "key:screen=boot", "log:queued", "exception:queued" }, io.Calls);
            io.Calls.Clear();
            io.Service.ExceptionSink = exception =>
            {
                io.Calls.Add("exception:" + exception.Message);
                io.Service.RecordNonFatal(new InvalidOperationException("recursive"));
            };
            io.Service.RecordNonFatal(new InvalidOperationException("after"));

            CollectionAssert.AreEqual(new[] { "exception:after" }, io.Calls);
            Assert.That(io.CollectionChanges[io.CollectionChanges.Count - 1], Is.True);
        }

        [Test]
        public async Task P08_RemovedAndReaddedKey_NotifiesAndUsesFallbackBetweenFetches()
        {
            var io = new RemoteConfigIo();
            io.Values["review_key"] = "3";
            using var test = Bind(io.Service);
            await test.InitializeAsync();
            string phase = "removed";
            var changed = new List<string>();
            io.Service.Changed += key => changed.Add(phase + ":" + key);

            io.NextValues = new Dictionary<string, string>();
            Assert.That(await io.Service.FetchAsync(CancellationToken.None), Is.True);
            int whileAbsent = io.Service.GetInt("review_key", 17);
            phase = "readded";
            io.NextValues = new Dictionary<string, string> { ["review_key"] = "3" };
            Assert.That(await io.Service.FetchAsync(CancellationToken.None), Is.True);

            Assert.That(io.FetchExpirations.Count, Is.EqualTo(2), "Memory-only last-fetch reads keep both fetches due.");
            Assert.That(io.Activations, Is.EqualTo(2));
            Assert.That(whileAbsent, Is.EqualTo(17));
            Assert.That(io.Service.GetInt("review_key", 17), Is.EqualTo(3));
            CollectionAssert.AreEqual(new[] { "removed:review_key", "readded:review_key" }, changed,
                "Removal and re-adding the same value are both observable changes.");
        }

        [Test]
        public async Task RemoteConfigRecentFetch_UsesCacheWithoutAnotherSdkFetch()
        {
            var io = new RemoteConfigIo { RememberLastFetch = true };
            io.Values["review_key"] = "1";
            using var test = Bind(io.Service);
            await test.InitializeAsync();
            var changed = new List<string>();
            io.Service.Changed += changed.Add;

            io.NextValues = new Dictionary<string, string> { ["review_key"] = "2" };
            Assert.That(await io.Service.FetchAsync(CancellationToken.None), Is.True);
            io.NextValues = new Dictionary<string, string> { ["review_key"] = "3" };
            Assert.That(await io.Service.FetchAsync(CancellationToken.None), Is.True);

            Assert.That(io.FetchExpirations.Count, Is.EqualTo(1));
            Assert.That(io.FetchExpirations[0], Is.EqualTo(TimeSpan.Zero));
            Assert.That(io.Activations, Is.EqualTo(1));
            Assert.That(io.LastFetchWrites, Is.EqualTo(1));
            Assert.That(io.Service.GetInt("review_key", 17), Is.EqualTo(2));
            CollectionAssert.AreEqual(new[] { "review_key" }, changed);
        }

        [Test]
        public async Task P03_CanceledToken_PreventsAllThreeDependencyChecks()
        {
            var analytics = new AnalyticsIo();
            var crash = new CrashIo();
            var config = new RemoteConfigIo();
            int dependencyChecks = 0;
            Func<Task<FirebaseDependencyStatus>> check = () =>
            {
                dependencyChecks++;
                return Task.FromResult(FirebaseDependencyStatus.Available);
            };
            analytics.Service.CheckDependenciesAsync = check;
            crash.Service.CheckDependenciesAsync = check;
            config.Service.CheckDependenciesAsync = check;
            using var analyticsTest = Bind(analytics.Service, analytics.Consent);
            using var crashTest = Bind(crash.Service, crash.Consent);
            using var configTest = Bind(config.Service);
            analyticsTest.Context.Container.Inject(analytics.Service);
            crashTest.Context.Container.Inject(crash.Service);
            configTest.Context.Container.Inject(config.Service);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            // Call the services directly: the context's own token check must not mask a missing service check.
            await AssertCallerCancellation(analytics.Service.InitializeAsync(cancellation.Token).AsTask(), cancellation.Token);
            await AssertCallerCancellation(crash.Service.InitializeAsync(cancellation.Token).AsTask(), cancellation.Token);
            await AssertCallerCancellation(config.Service.InitializeAsync(cancellation.Token).AsTask(), cancellation.Token);

            Assert.That(dependencyChecks, Is.Zero);
            Assert.That(analytics.Consent.SubscriberCount + crash.Consent.SubscriberCount, Is.Zero);
            Assert.That(config.KeyReads + config.ValueReads, Is.Zero);
        }

        [Test]
        public async Task P03_AnalyticsCanceledDuringDependencies_DoesNotSubscribeOrSend()
        {
            var io = new AnalyticsIo();
            io.Consent.Set(ConsentState.NotRequired, AnalyticsChoice.Unknown);
            var dependency = new TaskCompletionSource<FirebaseDependencyStatus>();
            io.Service.CheckDependenciesAsync = () => dependency.Task;
            using var test = Bind(io.Service, io.Consent);
            test.Context.Container.Inject(io.Service);
            using var cancellation = new CancellationTokenSource();
            Task startup = io.Service.InitializeAsync(cancellation.Token).AsTask();
            try
            {
                Assert.That(startup.IsCompleted, Is.False);
                cancellation.Cancel();
                dependency.SetResult(FirebaseDependencyStatus.Available);
                await AssertCallerCancellation(startup, cancellation.Token);
                Assert.That(io.Consent.SubscriberCount, Is.Zero);
                Assert.That(io.CollectionChanges.Count + io.Properties.Count + io.Events.Count, Is.Zero);
            }
            finally
            {
                dependency.TrySetResult(FirebaseDependencyStatus.Available);
                try { await FinishDisposedStartup(startup); }
                finally { io.Service.Dispose(); }
            }
        }

        [Test]
        public async Task P03_CrashReporterCanceledDuringDependencies_DoesNotSubscribeOrFlush()
        {
            var io = new CrashIo();
            io.Consent.Set(ConsentState.NotRequired, AnalyticsChoice.Unknown);
            var dependency = new TaskCompletionSource<FirebaseDependencyStatus>();
            io.Service.CheckDependenciesAsync = () => dependency.Task;
            using var test = Bind(io.Service, io.Consent);
            test.Context.Container.Inject(io.Service);
            using var cancellation = new CancellationTokenSource();
            Task startup = io.Service.InitializeAsync(cancellation.Token).AsTask();
            try
            {
                Assert.That(startup.IsCompleted, Is.False);
                io.Service.Log("queued");
                cancellation.Cancel();
                dependency.SetResult(FirebaseDependencyStatus.Available);
                await AssertCallerCancellation(startup, cancellation.Token);
                Assert.That(io.Consent.SubscriberCount, Is.Zero);
                Assert.That(io.CollectionChanges.Count + io.Calls.Count, Is.Zero);
            }
            finally
            {
                dependency.TrySetResult(FirebaseDependencyStatus.Available);
                try { await FinishDisposedStartup(startup); }
                finally { io.Service.Dispose(); }
            }
        }

        [Test]
        public async Task P03_RemoteConfigCanceledDuringDependencies_DoesNotReadSdkSnapshot()
        {
            var io = new RemoteConfigIo();
            io.Values["review_key"] = "3";
            var dependency = new TaskCompletionSource<FirebaseDependencyStatus>();
            io.Service.CheckDependenciesAsync = () => dependency.Task;
            using var test = Bind(io.Service);
            test.Context.Container.Inject(io.Service);
            using var cancellation = new CancellationTokenSource();
            Task startup = io.Service.InitializeAsync(cancellation.Token).AsTask();
            try
            {
                Assert.That(startup.IsCompleted, Is.False);
                cancellation.Cancel();
                dependency.SetResult(FirebaseDependencyStatus.Available);
                await AssertCallerCancellation(startup, cancellation.Token);
                Assert.That(io.KeyReads + io.ValueReads, Is.Zero);
                Assert.That(io.Service.GetInt("review_key", 17), Is.EqualTo(17));
            }
            finally
            {
                dependency.TrySetResult(FirebaseDependencyStatus.Available);
                try { await FinishDisposedStartup(startup); }
                finally { io.Service.Dispose(); }
            }
        }

        [Test]
        public async Task P03_DisposedServices_RejectNewInitializationAndIo()
        {
            var analytics = new AnalyticsIo();
            var crash = new CrashIo();
            var config = new RemoteConfigIo();
            config.Values["review_key"] = "3";
            int dependencyChecks = 0;
            Func<Task<FirebaseDependencyStatus>> check = () =>
            {
                dependencyChecks++;
                return Task.FromResult(FirebaseDependencyStatus.Available);
            };
            analytics.Service.CheckDependenciesAsync = check;
            crash.Service.CheckDependenciesAsync = check;
            config.Service.CheckDependenciesAsync = check;
            using var analyticsTest = Bind(analytics.Service, analytics.Consent);
            using var crashTest = Bind(crash.Service, crash.Consent);
            using var configTest = Bind(config.Service);
            await analyticsTest.InitializeAsync();
            await crashTest.InitializeAsync();
            await configTest.InitializeAsync();
            analytics.Service.Dispose();
            crash.Service.Dispose();
            config.Service.Dispose();
            analytics.CollectionChanges.Clear();
            crash.CollectionChanges.Clear();
            config.KeyReads = config.ValueReads = 0;

            analytics.Service.LogEvent("review_after_dispose");
            analytics.Service.SetUserProperty("review_cohort", "after_dispose");
            crash.Service.SetCustomKey("screen", "after_dispose");
            crash.Service.Log("after_dispose");
            crash.Service.RecordNonFatal(new InvalidOperationException("after_dispose"));
            Assert.That(config.Service.GetInt("review_key", 17), Is.EqualTo(17));
            Assert.That(await config.Service.FetchAsync(CancellationToken.None), Is.False);
            await analytics.Service.InitializeAsync(CancellationToken.None);
            await crash.Service.InitializeAsync(CancellationToken.None);
            await config.Service.InitializeAsync(CancellationToken.None);
            analytics.Consent.Set(ConsentState.NotRequired, AnalyticsChoice.Unknown);
            crash.Consent.Set(ConsentState.NotRequired, AnalyticsChoice.Unknown);

            Assert.That(dependencyChecks, Is.EqualTo(3), "Only the three original starts may reach the SDK.");
            Assert.That(analytics.Consent.SubscriberCount + crash.Consent.SubscriberCount, Is.Zero);
            Assert.That(analytics.CollectionChanges.Count + analytics.Properties.Count + analytics.Events.Count, Is.Zero);
            Assert.That(crash.CollectionChanges.Count + crash.Calls.Count, Is.Zero);
            Assert.That(config.KeyReads + config.ValueReads + config.FetchExpirations.Count + config.LastFetchWrites, Is.Zero);
        }

        [Test]
        public async Task P03_RemoteConfigCanceledDuringFetch_DoesNotActivateOrPublish()
        {
            var io = new RemoteConfigIo();
            using var test = Bind(io.Service);
            await test.InitializeAsync();
            io.KeyReads = io.ValueReads = 0;
            var fetched = new TaskCompletionSource<bool>();
            io.Service.FetchSdkAsync = _ => fetched.Task;
            using var cancellation = new CancellationTokenSource();
            Task<bool> fetch = io.Service.FetchAsync(cancellation.Token).AsTask();
            try
            {
                Assert.That(fetch.IsCompleted, Is.False);
                cancellation.Cancel();
                fetched.SetResult(true);
                Assert.That(await fetch, Is.False);
                Assert.That(io.Activations + io.KeyReads + io.ValueReads + io.LastFetchWrites, Is.Zero);
            }
            finally
            {
                fetched.TrySetResult(true);
                await fetch;
            }
        }

        [Test]
        public async Task P03_RemoteConfigDisposedDuringActivation_DoesNotPublish()
        {
            var io = new RemoteConfigIo();
            using var test = Bind(io.Service);
            await test.InitializeAsync();
            io.KeyReads = io.ValueReads = 0;
            var activated = new TaskCompletionSource<bool>();
            bool activationStarted = false;
            io.Service.ActivateSdkAsync = () => { activationStarted = true; return activated.Task; };
            Task<bool> fetch = io.Service.FetchAsync(CancellationToken.None).AsTask();
            try
            {
                Assert.That(activationStarted, Is.True);
                Assert.That(fetch.IsCompleted, Is.False);
                io.Service.Dispose();
                activated.SetResult(true);
                Assert.That(await fetch, Is.False);
                Assert.That(io.KeyReads + io.ValueReads + io.LastFetchWrites, Is.Zero);
            }
            finally
            {
                activated.TrySetResult(true);
                await fetch;
            }
        }

        private static void RequirePinnedAssembly(Assembly assembly, string name)
        {
            var package = PackageInfo.FindForAssembly(assembly);
            if (package == null || package.name != name || package.version != "14.1.0")
                Assert.Inconclusive("The loaded " + assembly.GetName().Name + " must belong to " + name + " 14.1.0.");
        }

        private static async Task AssertCallerCancellation(Task task, CancellationToken token)
        {
            try { await task; }
            catch (OperationCanceledException error)
            {
                Assert.That(error.CancellationToken, Is.EqualTo(token));
                return;
            }
            Assert.Fail("Initialization must observe the caller's canceled token.");
        }

        private static NexusTestContext Bind<T>(T service, ControlledConsent consent = null)
            where T : class, INexusService
        {
            // Explicit bindings only: no editor-mock FirebaseBindings path or assembly discovery.
            var test = NexusTestHarness.CreateContext();
            test.Builder.BindServiceInstance(service);
            if (consent != null) test.Builder.BindInstance<IAnalyticsConsent>(consent);
            return test;
        }

        private static async Task FinishDisposedStartup(Task startup)
        {
            try { await startup; }
            catch (OperationCanceledException) { }
        }

        private static Dictionary<string, object> ReadParameters(FirebaseParameter[] parameters)
        {
            // Firebase 14.1 Parameter is a managed payload with internal getters. Inspect output only;
            // never mutate a service field or invoke FirebaseAnalytics to read it.
            var name = typeof(FirebaseParameter).GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic);
            var value = typeof(FirebaseParameter).GetProperty("Value", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(name, Is.Not.Null, "The fixture expects the pinned Firebase 14.1 Parameter payload.");
            Assert.That(value, Is.Not.Null);
            var result = new Dictionary<string, object>();
            foreach (var parameter in parameters) result.Add((string)name.GetValue(parameter), value.GetValue(parameter));
            return result;
        }

        private sealed class ControlledConsent : IAnalyticsConsent
        {
            private Action _changed;
            public ConsentState State { get; private set; }
            public AnalyticsChoice Choice { get; private set; }
            public int SubscriberCount => _changed?.GetInvocationList().Length ?? 0;
            public event Action Changed { add => _changed += value; remove => _changed -= value; }

            public void Set(ConsentState state, AnalyticsChoice choice)
            {
                if (State == state && Choice == choice) return;
                State = state;
                Choice = choice;
                _changed?.Invoke();
            }
        }

        private sealed class AnalyticsIo
        {
            public readonly ControlledConsent Consent = new ControlledConsent();
            public readonly List<bool> CollectionChanges = new List<bool>();
            public readonly List<string> Properties = new List<string>();
            public readonly List<(string Name, FirebaseParameter[] Parameters)> Events =
                new List<(string Name, FirebaseParameter[] Parameters)>();
            public readonly FirebaseAnalyticsService Service;

            public AnalyticsIo()
            {
                Service = new FirebaseAnalyticsService
                {
                    CheckDependenciesAsync = () => Task.FromResult(FirebaseDependencyStatus.Available),
                    CollectionEnabledSink = CollectionChanges.Add,
                    PropertySink = (key, value) => Properties.Add(key + "=" + value),
                    EventSink = (name, parameters) => Events.Add((name, parameters))
                };
            }
        }

        private sealed class CrashIo
        {
            public readonly ControlledConsent Consent = new ControlledConsent();
            public readonly List<bool> CollectionChanges = new List<bool>();
            public readonly List<string> Calls = new List<string>();
            public readonly FirebaseCrashReporter Service;

            public CrashIo()
            {
                Service = new FirebaseCrashReporter
                {
                    CheckDependenciesAsync = () => Task.FromResult(FirebaseDependencyStatus.Available),
                    CollectionEnabledSink = CollectionChanges.Add,
                    CustomKeySink = (key, value) => Calls.Add("key:" + key + "=" + value),
                    LogSink = message => Calls.Add("log:" + message),
                    ExceptionSink = exception => Calls.Add("exception:" + exception.Message)
                };
            }
        }

        private sealed class RemoteConfigIo
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            public readonly List<TimeSpan> FetchExpirations = new List<TimeSpan>();
            public Dictionary<string, string> NextValues;
            public bool RememberLastFetch;
            public DateTime? LastFetch;
            public int Activations, KeyReads, ValueReads, LastFetchWrites;
            public readonly FirebaseRemoteConfigService Service;

            public RemoteConfigIo()
            {
                Service = new FirebaseRemoteConfigService
                {
                    CheckDependenciesAsync = () => Task.FromResult(FirebaseDependencyStatus.Available),
                    FetchSdkAsync = expiration =>
                    {
                        FetchExpirations.Add(expiration);
                        return Task.CompletedTask;
                    },
                    ActivateSdkAsync = () =>
                    {
                        Activations++;
                        if (NextValues != null)
                        {
                            Values.Clear();
                            foreach (var pair in NextValues) Values.Add(pair.Key, pair.Value);
                            NextValues = null;
                        }
                        return Task.FromResult(true);
                    },
                    ReadKeys = () => { KeyReads++; return Values.Keys; },
                    ReadRawValue = key => { ValueReads++; return Values.TryGetValue(key, out string value) ? value : null; },
                    ReadLastFetchUtc = () => LastFetch,
                    WriteLastFetchUtc = utcNow =>
                    {
                        LastFetchWrites++;
                        if (RememberLastFetch) LastFetch = utcNow;
                    }
                };
            }
        }
    }
}
#endif
