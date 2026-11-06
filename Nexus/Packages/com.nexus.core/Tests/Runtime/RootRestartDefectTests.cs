using System;
using System.Collections;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Nexus.Core.Tests
{
    /// <summary>
    /// Issue #197: a Root whose Configure failed was configured again by each child's Awake and Start, logging the error
    /// each time; a Root destroyed while it waited for its parent logged a NullReferenceException as a failed start; a
    /// lazy service first resolved on a worker thread ran its InitializeAsync on that thread.
    /// </summary>
    public sealed class RootRestartDefectTests
    {
        [SetUp] public void SetUp() => NexusRuntime.Reset();
        [TearDown] public void TearDown() => NexusRuntime.Reset();

        private sealed class FailingConfigure : IContextLifecycle
        {
            public static int Configured;
            public void OnConfigure(IContextBuilder builder)
            {
                Configured++;
                throw new InvalidOperationException("configure failure 197");
            }
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        private sealed class PlainLifecycle : IContextLifecycle
        {
            public void OnConfigure(IContextBuilder builder) { }
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        private sealed class NeverInitializes : IContextLifecycle
        {
            public void OnConfigure(IContextBuilder builder) { }
            public async ValueTask OnInitializeAsync(CancellationToken ct) => await Task.Delay(Timeout.Infinite, ct);
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        private sealed class ThreadProbeService : INexusService
        {
            public static int InitializedOnThread;
            public ValueTask InitializeAsync(CancellationToken ct)
            {
                InitializedOnThread = Thread.CurrentThread.ManagedThreadId;
                return default;
            }
            public void OnDispose() { }
        }

        private static ContextData NewData()
        {
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.EnableAutoDiscovery = false;
            data.AssemblyScopes = Array.Empty<string>();
            return data;
        }

        private static Root NewRoot(string name, ContextData data, Root parent = null)
        {
            var go = new GameObject(name);
            go.SetActive(false);
            var root = go.AddComponent<Root>();
            root.SetUp(data, parent, 0);
            return root;
        }

        private static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        [UnityTest]
        public IEnumerator AParentWhoseConfigureFails_LogsItOnce_AndItsChildrenStopWithoutConfiguringItAgain()
        {
            FailingConfigure.Configured = 0;
            var data = NewData();
            var parent = NewRoot("Parent197", data);
            var first = NewRoot("FirstChild197", data, parent);
            var second = NewRoot("SecondChild197", data, parent);
            try
            {
                parent.RegisterLifecycle(new FailingConfigure());
                first.RegisterLifecycle(new PlainLifecycle());
                second.RegisterLifecycle(new PlainLifecycle());
                LogAssert.Expect(LogType.Error, new Regex(@"^\[Nexus\] Root 'Parent197' failed to configure its context: System\.InvalidOperationException: configure failure 197"));
                LogAssert.Expect(LogType.Error, new Regex(@"^\[Nexus\] Root 'FirstChild197' cannot start: its parent root 'Parent197' failed to start\.$"));
                LogAssert.Expect(LogType.Error, new Regex(@"^\[Nexus\] Root 'SecondChild197' cannot start: its parent root 'Parent197' failed to start\.$"));
                parent.gameObject.SetActive(true);
                first.gameObject.SetActive(true);
                second.gameObject.SetActive(true);
                yield return Frames(5);

                // Cannot fail reliably: repeated configuration can occur after the five-frame wait (#317).
                Assert.AreEqual(1, FailingConfigure.Configured, "the failed root was configured again");
                Assert.IsNull(first.Context);
                Assert.IsNull(second.Context);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(second.gameObject);
                Object.DestroyImmediate(first.gameObject);
                Object.DestroyImmediate(parent.gameObject);
                Object.DestroyImmediate(data);
            }
        }

        [UnityTest]
        public IEnumerator ARootDestroyedWhileItWaitsForItsParent_StopsWithoutAnError()
        {
            var data = NewData();
            var parent = NewRoot("SlowParent197", data);
            var child = NewRoot("WaitingChild197", data, parent);
            try
            {
                parent.RegisterLifecycle(new NeverInitializes());
                child.RegisterLifecycle(new PlainLifecycle());
                parent.gameObject.SetActive(true);
                child.gameObject.SetActive(true);
                yield return Frames(3);
                // Cannot fail reliably: three frames do not prove the child reached its parent wait (#317).
                Assert.IsNotNull(child.Context, "the child is not waiting for its parent");

                Object.Destroy(child.gameObject);
                yield return Frames(3);

                // Cannot fail reliably: a destruction error can arrive after the three-frame wait (#317).
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                if (child != null) Object.DestroyImmediate(child.gameObject);
                Object.DestroyImmediate(parent.gameObject);
                Object.DestroyImmediate(data);
            }
        }

        [UnityTest]
        public IEnumerator ALazyServiceFirstResolvedOnAWorkerThread_InitializesOnTheMainThread()
        {
            ThreadProbeService.InitializedOnThread = 0;
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            var context = ContextFactory.Create(null, NewData()).OwnsContextData();
            try
            {
                var builder = new ContextBuilder(context.Container, context.SignalBusInternal);
                builder.BindLazyService<ThreadProbeService>();
                context.ConfigureWithBuilder(builder, new IContextLifecycle[] { new PlainLifecycle() }, scanAssemblies: false);
                var startup = context.InitializeLifecycleAsync(context.ConfiguredLifecycles, CancellationToken.None).AsTask();
                while (!startup.IsCompleted) yield return null;
                Assert.IsTrue(startup.IsCompletedSuccessfully, startup.Exception?.ToString());

                var resolve = Task.Run(() => context.Container.Resolve<ThreadProbeService>());
                while (!resolve.IsCompleted) yield return null;
                Assert.IsTrue(resolve.IsCompletedSuccessfully, resolve.Exception?.ToString());
                for (int i = 0; i < 30 && ThreadProbeService.InitializedOnThread == 0; i++) yield return null;

                Assert.AreEqual(mainThread, ThreadProbeService.InitializedOnThread);
            }
            finally { context.Dispose(); }
        }
    }
}
