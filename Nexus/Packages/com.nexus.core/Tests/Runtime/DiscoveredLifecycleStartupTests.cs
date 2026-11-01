using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nexus.Core.Tests
{
    /// <summary>Found by convention: ScopeTag "DiscoveredStartup" + "Lifecycle".</summary>
    public sealed class DiscoveredStartupLifecycle : IContextLifecycle
    {
        public static int Configured, Initialized, Started;
        public static void ResetCounts() => Configured = Initialized = Started = 0;

        public void OnConfigure(IContextBuilder builder) => Configured++;
        public ValueTask OnInitializeAsync(CancellationToken ct) { Initialized++; return default; }
        public ValueTask OnStartAsync(CancellationToken ct) { Started++; return default; }
        public void OnDispose() { }
    }

    /// <summary>Found by convention for ScopeTag "ThrowingStartup"; its constructor fails, as a game's can.</summary>
    public sealed class ThrowingStartupLifecycle : IContextLifecycle
    {
        public ThrowingStartupLifecycle() => throw new System.InvalidOperationException("the lifecycle's own reason");

        public void OnConfigure(IContextBuilder builder) { }
        public ValueTask OnInitializeAsync(CancellationToken ct) => default;
        public ValueTask OnStartAsync(CancellationToken ct) => default;
        public void OnDispose() { }
    }

    public sealed class ComponentStartupLifecycle : MonoBehaviour, IContextLifecycle
    {
        public int Configured, Initialized, Started;

        public void OnConfigure(IContextBuilder builder) => Configured++;
        public ValueTask OnInitializeAsync(CancellationToken ct) { Initialized++; return default; }
        public ValueTask OnStartAsync(CancellationToken ct) { Started++; return default; }
        public void OnDispose() { }
    }

    public sealed class DiscoveredLifecycleStartupTests
    {
        private const string ScopeTag = "DiscoveredStartup";
        private GameObject _go;
        private ContextData _data;

        [SetUp]
        public void SetUp()
        {
            NexusRuntime.Reset();
            DiscoveredStartupLifecycle.ResetCounts();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            if (_data != null) Object.DestroyImmediate(_data);
            NexusRuntime.Reset();
        }

        private Root CreateRoot(bool withComponentLifecycle, string scopeTag = ScopeTag, params string[] extraScopes)
        {
            _go = new GameObject("Discovery root");
            _go.SetActive(false);
            if (withComponentLifecycle) _go.AddComponent<ComponentStartupLifecycle>();
            _data = ScriptableObject.CreateInstance<ContextData>();
            _data.ScopeTag = scopeTag;
            _data.EnableAutoDiscovery = true;
            // The default scan skips assemblies named *.tests; scope the scan to this one so the convention type is found.
            var scopes = new System.Collections.Generic.List<string> { typeof(DiscoveredStartupLifecycle).Assembly.GetName().Name };
            scopes.AddRange(extraScopes);
            _data.AssemblyScopes = scopes.ToArray();
            var root = _go.AddComponent<Root>();
            root.SetUp(_data);
            return root;
        }

        private static IEnumerator WaitInitialized(Root root)
        {
            float deadline = Time.realtimeSinceStartup + 10f;
            while (!root.IsInitialized && Time.realtimeSinceStartup < deadline) yield return null;
        }

        [UnityTest]
        public IEnumerator RootRunsALifecycleFoundByConvention_ThroughInitializeAndStart()
        {
            var root = CreateRoot(withComponentLifecycle: false);
            _go.SetActive(true);
            yield return WaitInitialized(root);

            Assert.IsTrue(root.IsInitialized);
            Assert.AreEqual(1, DiscoveredStartupLifecycle.Configured, "OnConfigure");
            Assert.AreEqual(1, DiscoveredStartupLifecycle.Initialized, "OnInitializeAsync");
            Assert.AreEqual(1, DiscoveredStartupLifecycle.Started, "OnStartAsync");
        }

        [UnityTest]
        public IEnumerator ComponentLifecycle_RunsEachPhaseOnce_AndSuppressesDiscovery()
        {
            var root = CreateRoot(withComponentLifecycle: true);
            var component = _go.GetComponent<ComponentStartupLifecycle>();
            _go.SetActive(true);
            yield return WaitInitialized(root);

            Assert.IsTrue(root.IsInitialized);
            Assert.AreEqual((1, 1, 1), (component.Configured, component.Initialized, component.Started));
            Assert.AreEqual(0, DiscoveredStartupLifecycle.Configured, "a component lifecycle must keep discovery out");
        }

        // Issue #173: a scope assembly the player does not have (an asmdef excluded for the platform, a rename, stripping) was
        // skipped with a warning at most, so the context started without what it held and a release log could stay empty
        [UnityTest]
        public IEnumerator AScopeAssemblyThatCannotBeLoaded_IsAnErrorNamingItAndTheContext_Once()
        {
            var root = CreateRoot(withComponentLifecycle: false, ScopeTag, "Nexus.Missing.Scope173");
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
                @"^\[Nexus\] Context 'DiscoveredStartup': the scope assembly 'Nexus.Missing.Scope173' could not be loaded \(.+\); the lifecycle, bindings and signal handlers it holds are missing from this context"));
            _go.SetActive(true);
            yield return WaitInitialized(root);

            Assert.IsTrue(root.IsInitialized);
            Assert.AreEqual(1, DiscoveredStartupLifecycle.Started, "the scopes that load still run");
        }

        // Issue #173: the error named only "Exception has been thrown by the target of an invocation", so the cause and the stack were lost
        [UnityTest]
        public IEnumerator AConventionLifecycleWhoseConstructorThrows_IsLoggedWithItsCauseAndStack()
        {
            var root = CreateRoot(withComponentLifecycle: false, "ThrowingStartup");
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
                @"^\[Nexus\] Failed to instantiate lifecycle class 'ThrowingStartupLifecycle' by convention: System.InvalidOperationException: the lifecycle's own reason\s+at Nexus.Core.Tests.ThrowingStartupLifecycle..ctor"));
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"^\[Nexus\] No IContextLifecycle was discovered or registered"));
            _go.SetActive(true);
            yield return WaitInitialized(root);
            Assert.IsTrue(root.IsInitialized);
        }

        [UnityTest]
        public IEnumerator ALifecycleComponentAddedAfterAwake_IsReportedAsAnError()
        {
            var root = CreateRoot(withComponentLifecycle: false);
            _go.SetActive(true); // Awake reads the lifecycle components here
            var late = _go.AddComponent<ComponentStartupLifecycle>();
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
                @"^\[Nexus\] Root 'Discovery root': the lifecycle component ComponentStartupLifecycle was added after Awake and never runs"));
            yield return WaitInitialized(root);

            Assert.IsTrue(root.IsInitialized);
            Assert.AreEqual((0, 0, 0), (late.Configured, late.Initialized, late.Started), "the error reports the component; it does not run it");
        }
    }
}
