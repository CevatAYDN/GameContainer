using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Nexus.Core;
using Nexus.Core.Services;
using Nexus.Netcode;
using UnityEngine;

namespace Nexus.Tests
{
    [TestFixture]
    public class HardeningRegressionTests
    {
        public sealed class Dependency : IDisposable
        {
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }
        public sealed class NamedConstructor
        {
            public readonly Dependency Value;
            public NamedConstructor([Inject(Name = "primary")] Dependency value) => Value = value;
        }
        public sealed class NamedMethod
        {
            public Dependency Value { get; private set; }
            [Inject] public void Initialize([Inject(Name = "primary")] Dependency value) => Value = value;
        }
        private sealed class Clock : ITimeProvider { public float Time; public float Now => Time; }
        private sealed class Validator : INetworkEconomyValidator
        {
            internal readonly TaskCompletionSource<bool> Response = new();
            public Task<bool> ValidateSpendAsync(string id, long amount, string reason) => Response.Task;
            public Task ValidateEarnAsync(string id, long amount, string reason) => Task.CompletedTask;
        }
        private sealed class Prefs : IPlayerPrefsService
        {
            internal readonly Dictionary<string, int> Values = new();
            internal readonly List<(int current, int max)> Saves = new();
            public int GetInt(string key, int defaultValue = 0) => Values.TryGetValue(key, out int value) ? value : defaultValue;
            public void SetInt(string key, int value) => Values[key] = value;
            public bool GetBool(string key, bool defaultValue = false) => defaultValue;
            public void SetBool(string key, bool value) { }
            public string GetString(string key, string defaultValue = "") => defaultValue;
            public void SetString(string key, string value) { }
            public float GetFloat(string key, float defaultValue = 0) => defaultValue;
            public void SetFloat(string key, float value) { }
            public long GetLong(string key, long defaultValue = 0) => defaultValue;
            public void SetLong(string key, long value) { }
            public bool HasKey(string key) => Values.ContainsKey(key);
            public void DeleteKey(string key) => Values.Remove(key);
            public void Save() => Saves.Add((Values["NT_Prog_CurrentLevel"], Values["NT_Prog_MaxLevel"]));
        }
        private struct Add : INetworkSignal { }
        private struct Multiply : INetworkSignal { public int Factor; }
        public struct First { public int Value; }
        public struct Second { public int Value; }
        public sealed class TypedCommand : ICompositeCommand<First, Second>
        {
            public static int Calls, Sum;
            public void Execute(First first, Second second) { Calls++; Sum = first.Value + second.Value; }
        }
        public sealed class FailingTypedCommand : ICompositeCommand<First, Second>
        {
            public static int Attempts;
            public void Execute(First first, Second second)
            {
                if (++Attempts == 1) throw new InvalidOperationException("first attempt");
            }
        }
        private sealed class AsyncStop : IAsyncStoppable
        {
            internal bool Stopped;
            public async ValueTask StopAsync(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                Stopped = true;
            }
        }
        private sealed class NetworkOnlyPlugin : INexusPlugin, ISignalInterceptor
        {
            public int Calls;
            public NexusPluginManifest Manifest { get; } = new("network-only", "1", PluginCapabilities.SignalInterceptor);
            public void OnPluginRegistered(IPluginContext context) => context.RegisterSignalInterceptor(this);
            public void OnPluginRemoved() { }
            public bool Intercept(ref object signal) { Calls++; return signal is INetworkSignal; }
        }
        public sealed class StartupModel : IReactiveModel
        {
            public bool Bound;
            public int Score;
            public ValueTask OnBind(CancellationToken ct) { Bound = true; return default; }
        }
        public sealed class StartupCommand : ICommand<First>
        {
            private readonly StartupModel _model;
            public StartupCommand(StartupModel model) => _model = model;
            public void Execute(First signal) { if (!_model.Bound) throw new InvalidOperationException("not initialized"); _model.Score += signal.Value; }
        }
        private sealed class Model : ISnapshotableModel<int>
        {
            internal int Value;
            public int CaptureSnapshot() => Value;
            public void RestoreSnapshot(int state) => Value = state;
        }
        private sealed class UnsupportedReplaceStorage : EncryptedStorageService
        {
            internal UnsupportedReplaceStorage() : base("hardening-" + Guid.NewGuid().ToString("N")) { AutoSave = true; }
            protected override void ReplaceCurrentFile(string staged, string current, string backup)
                => throw new PlatformNotSupportedException("Controlled platform seam");
        }
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        public sealed class FactoryCommand : ICommand<First>, ICompositeCommand
        {
            private readonly Action _execute;
            public FactoryCommand(Action execute) => _execute = execute;
            public void Execute(First signal) => _execute();
            public void Execute(CompositeContext signals) => _execute();
        }

        [Test]
        public void CommandRegistration_ConstructsInChildScope()
        {
            using var parent = ContextFactory.Create();
            using var child = ContextFactory.Create(parent: parent);
            var parentModel = new StartupModel { Bound = true };
            var childModel = new StartupModel { Bound = true };
            parent.Container.BindInstance(parentModel);
            child.Container.BindInstance(childModel);
            ((SignalBus)parent.SignalBus).RegisterCommand(typeof(First), typeof(StartupCommand), ExecutionMode.Sequential, 0, false);
            ((SignalBus)child.SignalBus).RegisterCommand(typeof(First), typeof(StartupCommand), ExecutionMode.Sequential, 0, false);
            child.SignalBus.Fire(new First { Value = 7 });
            Assert.AreEqual(7, childModel.Score);
            Assert.AreEqual(0, parentModel.Score);
        }

        [Test]
        public void CommandRegistration_NormalizesCachedBindingToTransient()
        {
            using var context = ContextFactory.Create();
            context.Container.BindInstance(new StartupModel { Bound = true });
            context.Container.Bind<StartupCommand>();
            var cached = context.Container.Resolve<StartupCommand>();
            ((SignalBus)context.SignalBus).RegisterCommand(typeof(First), typeof(StartupCommand), ExecutionMode.Concurrent, 0, false);
            var first = context.Container.Resolve<StartupCommand>();
            var second = context.Container.Resolve<StartupCommand>();
            Assert.AreNotSame(cached, first);
            Assert.AreNotSame(first, second);
        }

        [Test]
        public void CommandRegistration_PreservesConsumerFactory()
        {
            using var context = ContextFactory.Create();
            int calls = 0;
            context.Container.BindFactory<FactoryCommand>(() => new FactoryCommand(() => calls++));
            ((SignalBus)context.SignalBus).RegisterCommand(typeof(First), typeof(FactoryCommand), ExecutionMode.Sequential, 0, false);
            context.SignalBus.Fire(new First());
            Assert.AreEqual(1, calls);
        }

        [Test]
        public void CompositeRegistration_PreservesConsumerFactory()
        {
            using var context = ContextFactory.Create();
            int calls = 0;
            context.Container.BindFactory<FactoryCommand>(() => new FactoryCommand(() => calls++));
            ((SignalBus)context.SignalBus).RegisterCompositeCommand(new[] { typeof(First), typeof(Second) }, typeof(FactoryCommand), false, 0, false);
            context.SignalBus.Fire(new First());
            context.SignalBus.Fire(new Second());
            Assert.AreEqual(1, calls);
        }

        private static Dictionary<string, string> Cache(EncryptedStorageService storage)
            => (Dictionary<string, string>)typeof(EncryptedStorageService).GetField("_cache", Private).GetValue(storage);
        private static string PathFor(EncryptedStorageService storage, string key)
            => (string)typeof(EncryptedStorageService).GetMethod("GetFilePath", Private).Invoke(storage, new object[] { key });

        [Test]
        public void GenericTransient_IsFresh_AndNotDisposedByContainer()
        {
            var di = new NexusDI(); di.Bind<Dependency>(Lifetime.Transient);
            var first = di.Resolve<Dependency>(); var second = di.Resolve<Dependency>();
            Assert.AreNotSame(first, second); di.Dispose();
            Assert.IsFalse(first.Disposed); Assert.IsFalse(second.Disposed);
        }

        [Test]
        public void CachedResolve_RejectsDisposedContainer()
        {
            var di = new NexusDI(); di.Bind<Dependency>(); di.Resolve<Dependency>(); di.Dispose();
            Assert.Throws<ObjectDisposedException>(() => di.Resolve<Dependency>());
        }

        [Test]
        public void Rebind_InvalidatesGenericAndChildResolution()
        {
            using var parent = new NexusDI(); using var child = new NexusDI(parent);
            var first = new Dependency(); var second = new Dependency();
            parent.BindInstance(first); Assert.AreSame(first, parent.Resolve<Dependency>()); Assert.AreSame(first, child.Resolve<Dependency>());
            parent.BindInstance(second); Assert.AreSame(second, parent.Resolve<Dependency>()); Assert.AreSame(second, child.Resolve<Dependency>());
        }

        [Test]
        public void NamedParameters_CompileAndResolve_InConstructorsAndMethods()
        {
            using var di = new NexusDI(); var dependency = new Dependency();
            di.BindInstance("primary", dependency); di.Bind<NamedConstructor>();
            Assert.AreSame(dependency, di.Resolve<NamedConstructor>().Value);
            var method = new NamedMethod(); di.Inject(method); Assert.AreSame(dependency, method.Value);
        }

        [Test]
        public void ReentrantProperty_PreservesLastDeliveredOldValue()
        {
            var property = new ObservableProperty<int>(0); var pairs = new List<(int, int)>();
            property.OnChanged((oldValue, value) => { pairs.Add((oldValue, value)); if (value == 1) property.Value = 2; });
            property.Value = 1;
            CollectionAssert.AreEqual(new[] { (0, 1), (1, 2) }, pairs);
        }

        [Test]
        public void ReentrantProperty_CoalescedReturnToPublishedValueDoesNotNotifyAgain()
        {
            var property = new ObservableProperty<int>(); int calls = 0;
            property.OnChanged((old, value) =>
            {
                if (++calls > 2) Assert.Fail("Repeated no-op notification");
                property.Value = 2;
                property.Value = 1;
            });
            property.Value = 1;
            Assert.AreEqual(1, calls); Assert.AreEqual(1, property.Value);
        }

        [Test]
        public void NestedListDrain_DoesNotSharePayloadBuffer()
        {
            var outer = new ObservableList<int>(); var inner = new ObservableList<int>(); var received = new List<int>();
            inner.OnAdded((index, item) => { if (item == 100) { inner.Add(101); inner.Add(102); } });
            outer.OnAdded((index, item) => { received.Add(item); if (item == 1) { outer.Add(2); outer.Add(3); } if (item == 2) inner.Add(100); });
            outer.Add(1); CollectionAssert.AreEqual(new[] { 1, 2, 3 }, received);
        }

        [Test]
        public void InFlightSave_PreservesNewerRequest()
        {
            var clock = new Clock(); var throttler = new SaveThrottler { TimeProvider = clock }; int newest = 0;
            throttler.TryRequestSave("owner", () => throttler.TryRequestSave("owner", () => newest++));
            clock.Time = 10; throttler.Tick(10); Assert.AreEqual(1, newest);
        }

        [Test]
        public void FailingSave_RetryLimitCannotDiscardNewerAction()
        {
            var clock = new Clock(); var throttler = new SaveThrottler { TimeProvider = clock }; int failures = 0, saved = 0;
            throttler.TryRequestSave("owner", () =>
            {
                if (++failures == 5) throttler.TryRequestSave("owner", () => saved++);
                throw new IOException("controlled failure");
            });
            for (int i = 0; i < 5; i++) { clock.Time += 10; throttler.Tick(10); }
            Assert.AreEqual(5, failures); Assert.AreEqual(1, saved);
        }

        [Test]
        public void ThrowingCancellation_StillDisposesAndUnregistersContext()
        {
            var context = new Context(); context.Container.Bind<Dependency>(); var dependency = context.Resolve<Dependency>();
            context.LifetimeToken.Register(() => throw new InvalidOperationException("controlled cancellation failure"));
            Assert.Throws<AggregateException>(() => context.Dispose());
            Assert.IsTrue(dependency.Disposed); CollectionAssert.DoesNotContain(NexusRuntime.ActiveContexts, context);
            context.Dispose();
        }

        [Test]
        public async Task ThrowingCancellation_StillCompletesAsyncTeardown()
        {
            var context = new Context(); context.Container.Bind<Dependency>(); var dependency = context.Resolve<Dependency>();
            context.LifetimeToken.Register(() => throw new InvalidOperationException("controlled cancellation failure"));
            try { await context.DisposeAsync(); Assert.Fail("Expected callback error"); }
            catch (AggregateException) { }
            Assert.IsTrue(dependency.Disposed); CollectionAssert.DoesNotContain(NexusRuntime.ActiveContexts, context);
        }

        [Test]
        public async Task AsyncStop_HasIndependentTokenAfterLifetimeCancellation()
        {
            var context = new Context(); var stop = new AsyncStop();
            context.Container.BindInstance(stop); var lifetime = context.LifetimeToken;
            await context.DisposeAsync();
            Assert.IsTrue(lifetime.IsCancellationRequested); Assert.IsTrue(stop.Stopped);
        }

        [Test]
        public void Refund_SaturatesInsteadOfWrapping()
        {
            var validator = new Validator(); using var economy = new EconomyService { NetworkValidator = validator };
            economy.SetBalance("coin", 100); economy.Spend("coin", 100); economy.SetBalance("coin", long.MaxValue);
            validator.Response.SetResult(false); Assert.AreEqual(long.MaxValue, economy.GetBalance("coin"));
        }

        [Test]
        public void LateRefund_DoesNotResurrectDisposedService()
        {
            var validator = new Validator(); var economy = new EconomyService { NetworkValidator = validator };
            economy.SetBalance("coin", 100); economy.Spend("coin", 100); economy.Dispose(); validator.Response.SetResult(false);
            Assert.AreEqual(0, economy.GetBalance("coin")); Assert.IsNull(economy.GetObservableBalance("coin"));
        }

        [Test]
        public void LevelLimit_DoesNotWrapNegative()
        {
            using var progression = new ProgressionService(); progression.SetLevel(int.MaxValue); progression.CompleteCurrentLevel();
            Assert.AreEqual(int.MaxValue, progression.CurrentLevel.Value);
        }

        [Test]
        public void LevelUpdate_SavesOneCoherentTuple()
        {
            var prefs = new Prefs(); using var progression = new ProgressionService { PlayerPrefsService = prefs };
            progression.InitializeAsync(default).GetAwaiter().GetResult(); progression.CompleteCurrentLevel();
            CollectionAssert.AreEqual(new[] { (2, 2) }, prefs.Saves);
        }

        [Test]
        public void Rollback_PreservesCrossTypeOrder_AndRepeatedReplay()
        {
            using var context = new Context(); var model = new Model(); var network = new NetworkSignalBus(context.SignalBus);
            context.SignalBus.Subscribe<Add>(_ => model.Value++); context.SignalBus.Subscribe<Multiply>(_ => model.Value *= 10);
            network.RegisterModel<int>(model); network.SetTick(0);
            network.Fire(new Add()); network.Fire(new Multiply()); network.Fire(new Add());
            Assert.AreEqual(11, model.Value); network.RollbackAndResimulate(0, 0); Assert.AreEqual(11, model.Value);
            network.RollbackAndResimulate(0, 0); Assert.AreEqual(11, model.Value);
        }

        [Test]
        public void LateTickInputs_ReplayByTickThenRecordingOrder()
        {
            using var context = new Context(); var model = new Model(); var network = new NetworkSignalBus(context.SignalBus);
            context.SignalBus.Subscribe<Add>(_ => model.Value++); context.SignalBus.Subscribe<Multiply>(_ => model.Value *= 10);
            network.RegisterModel<int>(model); network.SetTick(0);
            network.FireAtTick(new Add(), 1); network.FireAtTick(new Multiply(), 0); network.FireAtTick(new Add(), 0);
            network.RollbackAndResimulate(0, 1); Assert.AreEqual(2, model.Value);
        }

        [Test]
        public void NestedNetworkEmissions_DoNotAccumulateAcrossRollbacks()
        {
            using var context = new Context(); var model = new Model(); var network = new NetworkSignalBus(context.SignalBus);
            context.SignalBus.Subscribe<Add>(_ => { model.Value++; network.Fire(new Multiply()); model.Value++; });
            context.SignalBus.Subscribe<Multiply>(_ => model.Value *= 10);
            network.RegisterModel<int>(model); network.SetTick(0); network.Fire(new Add());
            Assert.AreEqual(11, model.Value);
            for (int i = 0; i < 3; i++) { network.RollbackAndResimulate(0, 0); Assert.AreEqual(11, model.Value); }
        }

        [Test]
        public void NestedFutureTickEmission_ReplaysOriginalOnce()
        {
            using var context = new Context(); var model = new Model(); var network = new NetworkSignalBus(context.SignalBus);
            context.SignalBus.Subscribe<Add>(_ => { model.Value++; network.FireAtTick(new Multiply(), 1); });
            context.SignalBus.Subscribe<Multiply>(_ => model.Value *= 10);
            network.RegisterModel<int>(model); network.SetTick(0); network.Fire(new Add());
            for (int i = 0; i < 3; i++) { network.RollbackAndResimulate(0, 1); Assert.AreEqual(10, model.Value); }
        }

        [Test]
        public void WorkerInputs_WithSameTypeNestedEmission_PreserveParentIdentity()
        {
            using var context = new Context(); var model = new Model(); var network = new NetworkSignalBus(context.SignalBus);
            context.SignalBus.Subscribe<Add>(_ => { model.Value++; network.Fire(new Multiply { Factor = 10 }); model.Value++; });
            context.SignalBus.Subscribe<Multiply>(signal => model.Value *= signal.Factor);
            network.RegisterModel<int>(model); network.SetTick(0);
            Exception error = null;
            var producer = new Thread(() => { try { network.Fire(new Add()); network.Fire(new Multiply { Factor = 3 }); } catch (Exception ex) { error = ex; } });
            producer.Start(); Assert.IsTrue(producer.Join(3000)); Assert.IsNull(error);
            Assert.Throws<InvalidOperationException>(() => network.RollbackAndResimulate(0, 0));
            Assert.Throws<InvalidOperationException>(() => network.SetTick(1));
            context.HybridQueue.DrainThreadSafe(); Assert.AreEqual(33, model.Value);
            for (int i = 0; i < 3; i++) { network.RollbackAndResimulate(0, 0); Assert.AreEqual(33, model.Value); }
            network.SetTick(1); network.RollbackAndResimulate(1, 1); Assert.AreEqual(33, model.Value);
        }

        [Test]
        public void OwnerFire_CannotOvertakePendingWorkerInput()
        {
            using var context = new Context(); var model = new Model { Value = 1 }; var network = new NetworkSignalBus(context.SignalBus);
            context.SignalBus.Subscribe<Add>(_ => model.Value++); context.SignalBus.Subscribe<Multiply>(_ => model.Value *= 10);
            network.RegisterModel<int>(model); network.SetTick(0);
            var worker = new Thread(() => network.Fire(new Multiply())); worker.Start(); Assert.IsTrue(worker.Join(3000));
            Assert.Throws<InvalidOperationException>(() => network.Fire(new Add()));
            Assert.Throws<InvalidOperationException>(() => network.FireAtTick(new Add(), 0));
            context.HybridQueue.DrainThreadSafe(); network.Fire(new Add()); Assert.AreEqual(11, model.Value);
            network.RollbackAndResimulate(0, 0); Assert.AreEqual(11, model.Value);
        }

        [Test]
        public void WorkerTransport_BypassesMiddleware_AndRawSignalRunsOnce()
        {
            using var context = new Context(); var plugin = new NetworkOnlyPlugin(); context.RegisterPlugin(plugin);
            var network = new NetworkSignalBus(context.SignalBus); int received = 0;
            context.SignalBus.Subscribe<Add>(_ => received++); network.SetTick(0);
            var worker = new Thread(() => network.Fire(new Add())); worker.Start(); Assert.IsTrue(worker.Join(3000));
            context.HybridQueue.DrainThreadSafe(); Assert.AreEqual(1, received); Assert.AreEqual(1, plugin.Calls);
            network.SetTick(1); network.Fire(new Add()); Assert.AreEqual(2, received);
        }

        [Test]
        public void QueueClear_ReleasesNetworkItemsWithoutAcquiringTickLock()
        {
            using var context = new Context(); var network = new NetworkSignalBus(context.SignalBus);
            var producer = new Thread(() => network.Fire(new Add())); producer.Start(); Assert.IsTrue(producer.Join(3000));
            object tickLock = typeof(NetworkSignalBus).GetField("_tickLock", Private).GetValue(network);
            var cleaner = new Thread(context.HybridQueue.Clear) { IsBackground = true }; bool completed;
            lock (tickLock) { cleaner.Start(); completed = cleaner.Join(3000); }
            Assert.IsTrue(cleaner.Join(3000)); Assert.IsTrue(completed, "Queue cleanup must not take the owner's tick lock");
            network.SetTick(1);
        }

        [Test]
        public void FirstReplayOfFutureInput_GeneratesNestedAndFutureEventsOnce()
        {
            using var context = new Context(); var model = new Model(); var network = new NetworkSignalBus(context.SignalBus);
            context.SignalBus.Subscribe<Add>(_ => { model.Value++; network.Fire(new Multiply()); model.Value++; network.FireAtTick(new Multiply(), 2); });
            context.SignalBus.Subscribe<Multiply>(_ => model.Value *= 10);
            network.RegisterModel<int>(model); network.SetTick(0); network.FireAtTick(new Add(), 1);
            for (int i = 0; i < 3; i++) { network.RollbackAndResimulate(0, 2); Assert.AreEqual(110, model.Value); }
        }

        [Test]
        public void PartialRollback_PreservesRegeneratedFutureSchedule()
        {
            using var context = new Context(); var model = new Model(); var network = new NetworkSignalBus(context.SignalBus);
            context.SignalBus.Subscribe<Add>(_ => { model.Value++; network.FireAtTick(new Multiply(), 2); });
            context.SignalBus.Subscribe<Multiply>(_ => model.Value *= 10);
            network.RegisterModel<int>(model); network.SetTick(0); network.Fire(new Add());
            network.RollbackAndResimulate(0, 1); Assert.AreEqual(1, model.Value);
            network.RollbackAndResimulate(0, 2); Assert.AreEqual(10, model.Value);
        }

        [Test]
        public void TypedComposite_OneShotRetriesAfterFailedPair()
        {
            using var context = new Context(); FailingTypedCommand.Attempts = 0;
            using var registration = context.SignalBus.BindComposite<First, Second, FailingTypedCommand>(oneShot: true);
            context.SignalBus.Fire(new First());
#if UNITY_EDITOR
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, "[Nexus Error] first attempt");
#endif
            Assert.Throws<InvalidOperationException>(() => context.SignalBus.Fire(new Second()));
            context.SignalBus.Fire(new First()); context.SignalBus.Fire(new Second());
            context.SignalBus.Fire(new First()); context.SignalBus.Fire(new Second());
            Assert.AreEqual(2, FailingTypedCommand.Attempts);
        }

        [Test]
        public void TypedComposite_UsesLatestValues_AndDisposes()
        {
            using var context = new Context(); TypedCommand.Calls = TypedCommand.Sum = 0;
            var registration = context.SignalBus.BindComposite<First, Second, TypedCommand>();
            context.SignalBus.Fire(new First { Value = 1 }); context.SignalBus.Fire(new First { Value = 2 });
            context.SignalBus.Fire(new Second { Value = 3 }); Assert.AreEqual(5, TypedCommand.Sum); Assert.AreEqual(1, TypedCommand.Calls);
            registration.Dispose(); context.SignalBus.Fire(new First()); context.SignalBus.Fire(new Second()); Assert.AreEqual(1, TypedCommand.Calls);
        }

#if !NEXUS_DEBUG
        [Test]
        public void NestedRollback_SteadyStateHasNoManagedAllocation()
        {
            using var context = new Context(); var network = new NetworkSignalBus(context.SignalBus);
            context.SignalBus.Subscribe<Add>(_ => network.Fire(new Multiply()));
            context.SignalBus.Subscribe<Multiply>(_ => { });
            network.SetTick(0); network.Fire(new Add());
            for (int i = 0; i < 100; i++) network.RollbackAndResimulate(0, 9);
            using var allocationProbe = new GcAllocationProbe();
            for (int i = 0; i < 1000; i++) network.RollbackAndResimulate(0, 9);
            long allocation = allocationProbe.Stop();
            Assert.AreEqual(0, allocation);
        }

        [Test]
        public void TypedComposite_SteadyStateHasNoManagedAllocation()
        {
            using var context = new Context(); using var registration = context.SignalBus.BindComposite<First, Second, TypedCommand>();
            for (int i = 0; i < 100; i++) { context.SignalBus.Fire(new First()); context.SignalBus.Fire(new Second()); }
            using var allocationProbe = new GcAllocationProbe();
            for (int i = 0; i < 1000; i++) { context.SignalBus.Fire(new First()); context.SignalBus.Fire(new Second()); }
            long allocated = allocationProbe.Stop();
            Assert.AreEqual(0, allocated, "Production path without tracing/decorators");
        }
        #endif

        [Test]
        public void TypedComposite_OneShot_ExecutesOnce()
        {
            using var context = new Context(); TypedCommand.Calls = 0;
            using var registration = context.SignalBus.BindComposite<First, Second, TypedCommand>(oneShot: true);
            context.SignalBus.Fire(new First()); context.SignalBus.Fire(new Second());
            context.SignalBus.Fire(new First()); context.SignalBus.Fire(new Second()); Assert.AreEqual(1, TypedCommand.Calls);
        }

        [Test]
        public void Migration_DoesNotOverwriteNewerAutoSave()
        {
            using var storage = new EncryptedStorageService("hardening-" + Guid.NewGuid().ToString("N")) { AutoSave = true };
            const string key = "migration";
            try
            {
                storage.SetString(key, "old"); string path = PathFor(storage, key); byte[] raw = File.ReadAllBytes(path); raw[0] = 2;
                byte[] macKey = (byte[])typeof(EncryptedStorageService).GetField("_hmacKey", Private).GetValue(storage);
                using (var hmac = new HMACSHA256(macKey))
                {
                    byte[] message = new byte[raw.Length - 33]; Array.Copy(raw, 1, message, 0, 16); Array.Copy(raw, 49, message, 16, raw.Length - 49);
                    Array.Copy(hmac.ComputeHash(message), 0, raw, 17, 32);
                }
                File.WriteAllBytes(path, raw); Cache(storage).Clear();
                object writeLock = typeof(EncryptedStorageService).GetField("_writeLock", Private).GetValue(storage);
                string read = null; Exception error = null; bool blocked;
                var worker = new Thread(() => { try { read = storage.GetString(key); } catch (Exception ex) { error = ex; } });
                lock (writeLock)
                {
                    worker.Start(); blocked = SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, 3000);
                    if (blocked) storage.SetString(key, "new");
                }
                Assert.IsTrue(worker.Join(3000), "Migration must not deadlock"); Assert.IsTrue(blocked); Assert.IsNull(error);
                Assert.AreEqual("new", read); Cache(storage).Clear(); Assert.AreEqual("new", storage.GetString(key));
            }
            finally { storage.DeleteKey(key); }
        }

        [Test]
        public void UnsupportedReplace_PreservesAndRecoversPreviousCommit()
        {
            using var storage = new UnsupportedReplaceStorage(); const string key = "backup";
            try
            {
                storage.SetString(key, "old"); storage.SetString(key, "new"); string path = PathFor(storage, key);
                Assert.IsTrue(File.Exists(path + ".bak")); Cache(storage).Clear(); Assert.AreEqual("new", storage.GetString(key));
                // Controlled process-interruption state: backup committed, final path absent.
                File.Delete(path); Cache(storage).Clear(); Assert.IsTrue(storage.HasKey(key)); Assert.AreEqual("old", storage.GetString(key));
                storage.DeleteKey(key); Cache(storage).Clear(); Assert.IsFalse(storage.HasKey(key));
            }
            finally { storage.DeleteKey(key); }
        }

        [Test]
        public void QueuedImport_DoesNotOverwriteNewerMutation()
        {
            using var storage = new EncryptedStorageService("import-" + Guid.NewGuid().ToString("N")) { AutoSave = true };
            const string key = "save";
            try
            {
                storage.SetString(key, "old"); string backup = storage.ExportEncryptedSaveData(key);
                storage.AutoSave = false;
                object writeLock = typeof(EncryptedStorageService).GetField("_writeLock", Private).GetValue(storage);
                bool accepted = true, blocked; Exception error = null;
                var worker = new Thread(() => { try { accepted = storage.ImportEncryptedSaveData(key, backup); } catch (Exception ex) { error = ex; } });
                lock (writeLock)
                {
                    worker.Start(); blocked = SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, 3000);
                    if (blocked) storage.SetString(key, "new");
                }
                Assert.IsTrue(worker.Join(3000)); Assert.IsTrue(blocked); Assert.IsNull(error); Assert.IsFalse(accepted);
                Assert.AreEqual("new", storage.GetString(key)); storage.Save(); Cache(storage).Clear();
                Assert.AreEqual("new", storage.GetString(key));
            }
            finally { storage.DeleteKey(key); }
        }

        [Test]
        public async Task ExplicitStartup_ConfiguresOnceAndCleansUpOnFailure()
        {
            var context = await ContextFactory.StartAsync("explicit-test", builder => builder.Bind<Dependency>(Lifetime.Scoped));
            var dependency = context.Resolve<Dependency>(); await context.DisposeAsync(); Assert.IsTrue(dependency.Disposed);
            int before = NexusRuntime.ActiveContexts.Count;
            try { await ContextFactory.StartAsync("failed-test", _ => throw new InvalidOperationException("controlled startup failure")); Assert.Fail("Expected startup failure"); }
            catch (InvalidOperationException) { }
            Assert.AreEqual(before, NexusRuntime.ActiveContexts.Count);
        }

        [Test]
        public async Task ExplicitStartup_InitializesModelBeforeCommandDispatch()
        {
            var context = await ContextFactory.StartAsync("sample-pipeline", builder =>
            {
                builder.BindReactiveModel<StartupModel>();
                builder.BindSignal<First>().To<StartupCommand>();
            });
            try { context.SignalBus.Fire(new First { Value = 7 }); Assert.IsTrue(context.Resolve<StartupModel>().Bound); Assert.AreEqual(7, context.Resolve<StartupModel>().Score); }
            finally { await context.DisposeAsync(); }
        }

        [Test]
        public async Task AssetCompletion_ReleasesRegistrationOnLongLivedToken()
        {
            using var lifetime = new CancellationTokenSource();
            var weak = await CompleteAsset(lifetime.Token);
            for (int i = 0; i < 3 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Yield(); }
            Assert.IsFalse(weak.IsAlive, "The live token must not retain a completed load task");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task<WeakReference> CompleteAsset(CancellationToken token)
        {
            var completion = new TaskCompletionSource<UnityEngine.Object>();
            var method = typeof(ResourcesAssetLoadService).GetMethod("AwaitCompletionAsync", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(typeof(UnityEngine.Object));
            var task = (Task<UnityEngine.Object>)method.Invoke(null, new object[] { completion, token });
            completion.SetResult(null); await task;
            return new WeakReference(task);
        }
    }
}
