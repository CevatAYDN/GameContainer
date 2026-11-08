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
    /// Issue #190: a Root waited its full 15 s timeout for a parent or a higher-priority sibling that had already failed
    /// to start, and it bound its lifecycles as container-owned IContextLifecycle instances: two lifecycles logged a
    /// rebinding warning, and the container disposed an IDisposable lifecycle besides its OnDispose.
    /// </summary>
    public sealed class RootStartupFailureTests
    {
        [SetUp] public void SetUp() => NexusRuntime.Reset();
        [TearDown] public void TearDown() => NexusRuntime.Reset();

        private sealed class FailsNextFrame : IContextLifecycle
        {
            public void OnConfigure(IContextBuilder builder) { }
            public async ValueTask OnInitializeAsync(CancellationToken ct)
            {
                await Task.Yield();
                throw new InvalidOperationException("startup failure 190");
            }
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

        private sealed class WaitsForCancellation : IContextLifecycle
        {
            public bool Started;
            public void OnConfigure(IContextBuilder builder) { }
            public ValueTask OnInitializeAsync(CancellationToken ct)
            {
                Started = true;
                return new ValueTask(Task.Delay(Timeout.Infinite, ct));
            }
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() { }
        }

        private sealed class DisposableLifecycle : IContextLifecycle, IDisposable
        {
            public int OnDisposeCalls, DisposeCalls;
            public void OnConfigure(IContextBuilder builder) { }
            public ValueTask OnInitializeAsync(CancellationToken ct) => default;
            public ValueTask OnStartAsync(CancellationToken ct) => default;
            public void OnDispose() => OnDisposeCalls++;
            public void Dispose() => DisposeCalls++;
        }

        private static ContextData NewData()
        {
            var data = ScriptableObject.CreateInstance<ContextData>();
            data.EnableAutoDiscovery = false;
            return data;
        }

        private static Root NewRoot(string name, ContextData data, Root parent = null, int priority = 0)
        {
            var go = new GameObject(name);
            go.SetActive(false);
            var root = go.AddComponent<Root>();
            root.SetUp(data, parent, priority);
            return root;
        }

        // 5 s against the 15 s default timeout: a root still waiting then waits for a root that failed
        private static IEnumerator WaitWhileStarting(Root root)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 5d;
            while (root.Context != null && !root.IsInitialized && Time.realtimeSinceStartupAsDouble < deadline)
                yield return null;
        }

        private static Regex Failed(string message) =>
            new Regex(@"^\[Nexus\] Root initialization failed: " + Regex.Escape(message));

        [UnityTest]
        public IEnumerator AChildOfAParentThatFailedToStart_StopsAtOnce_NotAfterItsTimeout()
        {
            var data = NewData();
            var parent = NewRoot("Parent190", data);
            var child = NewRoot("Child190", data, parent);
            try
            {
                parent.RegisterLifecycle(new FailsNextFrame());
                child.RegisterLifecycle(new PlainLifecycle());
                LogAssert.Expect(LogType.Error, Failed("startup failure 190"));
                LogAssert.Expect(LogType.Error, Failed("Root 'Child190' cannot start: its parent root 'Parent190' failed to start."));
                parent.gameObject.SetActive(true);
                child.gameObject.SetActive(true);
                yield return WaitWhileStarting(child);
                Assert.IsNull(child.Context, "the child still waits for a parent that failed to start");
                Assert.IsFalse(child.IsInitialized);
            }
            finally
            {
                Object.DestroyImmediate(child.gameObject);
                Object.DestroyImmediate(parent.gameObject);
                Object.DestroyImmediate(data);
            }
        }

        [UnityTest]
        public IEnumerator AChildEnabledAfterParentStartupFailure_ReportsTheParentFailure()
        {
            var data = NewData();
            var parent = NewRoot("LateParent337", data);
            var child = NewRoot("LateChild337", data, parent);
            try
            {
                parent.RegisterLifecycle(new FailsNextFrame());
                child.RegisterLifecycle(new PlainLifecycle());
                child.enabled = false;
                LogAssert.Expect(LogType.Error, Failed("startup failure 190"));
                parent.gameObject.SetActive(true);
                child.gameObject.SetActive(true);
                Assert.IsNotNull(child.Context, "Awake must create the child context before its delayed Start");
                var context = child.Context;
                yield return WaitWhileStarting(parent);
                Assert.IsTrue(context.IsDisposed);
                Assert.IsFalse(child.IsInitialized);

                LogAssert.Expect(LogType.Error, Failed("Root 'LateChild337' cannot start: its parent root 'LateParent337' failed to start."));
                child.enabled = true;
                yield return WaitWhileStarting(child);
                Assert.IsNull(child.Context);
                Assert.IsFalse(child.IsInitialized);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(child.gameObject);
                Object.DestroyImmediate(parent.gameObject);
                Object.DestroyImmediate(data);
            }
        }

        [UnityTest]
        public IEnumerator AChildEnabledAfterParentStartupCancellation_StopsWithoutAFalseFailure()
            => VerifyParentStartupCancellation(delayChildStart: true);

        [UnityTest]
        public IEnumerator AChildWaitingForParentStartupCancellation_StopsWithoutAFalseFailure()
            => VerifyParentStartupCancellation(delayChildStart: false);

        private static IEnumerator VerifyParentStartupCancellation(bool delayChildStart)
        {
            var data = NewData();
            var parent = NewRoot("CancelledParent337", data);
            var child = NewRoot("CancelledChild337", data, parent);
            var pending = new WaitsForCancellation();
            try
            {
                parent.RegisterLifecycle(pending);
                child.RegisterLifecycle(new PlainLifecycle());
                child.enabled = !delayChildStart;
                parent.gameObject.SetActive(true);
                child.gameObject.SetActive(true);
                var context = child.Context;
                yield return null;
                Assert.IsTrue(pending.Started);
                parent.Context.Dispose();
                yield return WaitWhileStarting(parent);
                Assert.IsNull(parent.Context);
                Assert.IsTrue(context.IsDisposed);

                if (delayChildStart) child.enabled = true;
                yield return WaitWhileStarting(child);
                Assert.IsNull(child.Context);
                Assert.IsFalse(child.IsInitialized);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(child.gameObject);
                Object.DestroyImmediate(parent.gameObject);
                Object.DestroyImmediate(data);
            }
        }

        [UnityTest]
        public IEnumerator ARootAfterASiblingThatFailedToStart_StopsAtOnce_NotAfterItsTimeout()
        {
            var data = NewData();
            var first = NewRoot("First190", data, null, 10);
            var second = NewRoot("Second190", data, null, 0);
            try
            {
                first.RegisterLifecycle(new FailsNextFrame());
                second.RegisterLifecycle(new PlainLifecycle());
                LogAssert.Expect(LogType.Error, Failed("startup failure 190"));
                LogAssert.Expect(LogType.Error, Failed("Root 'Second190' cannot start: sibling root 'First190', which starts before it, failed to start."));
                first.gameObject.SetActive(true);
                second.gameObject.SetActive(true);
                yield return WaitWhileStarting(second);
                Assert.IsNull(second.Context, "the root still waits for a sibling that failed to start");
                Assert.IsFalse(second.IsInitialized);
            }
            finally
            {
                Object.DestroyImmediate(second.gameObject);
                Object.DestroyImmediate(first.gameObject);
                Object.DestroyImmediate(data);
            }
        }

        [UnityTest]
        public IEnumerator ARootWithTwoLifecycles_StartsWithoutARebindingWarning_AndOnlyTheContextTearsThemDown()
        {
            var data = NewData();
            var root = NewRoot("Lifecycles190", data);
            var disposable = new DisposableLifecycle();
            try
            {
                root.RegisterLifecycle(disposable);
                root.RegisterLifecycle(new PlainLifecycle());
                root.gameObject.SetActive(true);
                yield return WaitWhileStarting(root);
                Assert.IsTrue(root.IsInitialized);
                Object.DestroyImmediate(root.gameObject);
                Assert.AreEqual(1, disposable.OnDisposeCalls);
                Assert.AreEqual(0, disposable.DisposeCalls, "the container disposed a lifecycle the context tears down");
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root.gameObject);
                Object.DestroyImmediate(data);
            }
        }
    }
}
